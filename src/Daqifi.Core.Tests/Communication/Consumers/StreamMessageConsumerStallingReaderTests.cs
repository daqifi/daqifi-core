using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Daqifi.Core.Tests.Communication.Consumers;

/// <summary>
/// Regression coverage for issue #383 — a consumer whose reader thread is parked in a slow
/// blocking read must still refuse a second reader on the same stream, and a read that merely
/// hits the stream's configured timeout must not be reported as an error.
/// </summary>
public class StreamMessageConsumerStallingReaderTests
{
    [Fact]
    public void Start_WhenStoppedReaderExitsWithinGrace_WaitsAndRestartsSameInstance()
    {
        // The #383 recovery: a reader that outlives the stop budget but does return is absorbed by
        // Start()'s grace period, so the restart succeeds on the SAME instance. That is what makes
        // a bounded read timeout sufficient, and it means no second reader is ever put on the
        // stream. Release() lands while Start() is already waiting.
        using var stream = new StallingStream(Timeout.InfiniteTimeSpan);
        using var consumer = new StreamMessageConsumer<string>(stream, new LineBasedMessageParser());

        consumer.Start();
        Assert.True(stream.WaitForReadEntered(TimeSpan.FromSeconds(2)));
        Assert.False(consumer.StopSafely(timeoutMs: 200));

        using (var releaser = new Timer(_ => stream.Release(), null, 200, Timeout.Infinite))
        {
            consumer.Start();
        }

        Assert.True(consumer.IsRunning);
        Assert.True(consumer.StopSafely(timeoutMs: 2000));
    }

    [Fact]
    public void Start_WhenStoppedReaderNeverExits_StillRefusesASecondReader()
    {
        // A read that never returns means the stream is stuck. The guard must outlast the grace
        // period — a second reader on that stream is exactly the framing corruption it prevents.
        using var stream = new StallingStream(Timeout.InfiniteTimeSpan);
        using var consumer = new StreamMessageConsumer<string>(stream, new LineBasedMessageParser());

        consumer.Start();
        Assert.True(stream.WaitForReadEntered(TimeSpan.FromSeconds(2)));
        Assert.False(consumer.StopSafely(timeoutMs: 200));

        Assert.Throws<ConsumerThreadNotExitedException>(() => consumer.Start());
        Assert.False(consumer.IsRunning);
        Assert.Equal(1, stream.ReadCount);

        stream.Release();
        Assert.True(WaitUntil(() => !IsConsumerThreadAlive(consumer), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ProcessMessages_WhenReadTimesOutWithSocketTimeout_DoesNotReportError()
    {
        // A synchronous NetworkStream read that expires on SO_RCVTIMEO surfaces as
        // IOException(SocketException TimedOut), not TimeoutException. With the short
        // operational receive timeout now applied to TCP connections, treating that as an
        // error would raise ErrorOccurred on every idle interval.
        using var stream = new TimingOutStream(
            () => new IOException("read timed out", new SocketException((int)SocketError.TimedOut)));
        using var consumer = new StreamMessageConsumer<string>(stream, new LineBasedMessageParser());

        var errors = 0;
        consumer.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);

        consumer.Start();
        Assert.True(WaitUntil(() => stream.ReadCount >= 3, TimeSpan.FromSeconds(2)));
        Assert.True(consumer.StopSafely(timeoutMs: 2000));

        Assert.Equal(0, Volatile.Read(ref errors));
    }

    [Fact]
    public void ProcessMessages_WhenReadFailsWithNonTimeoutIoError_StillReportsError()
    {
        // The benign-timeout carve-out must not swallow real I/O faults such as a reset peer.
        using var stream = new TimingOutStream(
            () => new IOException("connection reset", new SocketException((int)SocketError.ConnectionReset)));
        using var consumer = new StreamMessageConsumer<string>(stream, new LineBasedMessageParser());

        Exception? captured = null;
        consumer.ErrorOccurred += (_, e) => captured ??= e.Error;

        consumer.Start();
        Assert.True(WaitUntil(() => captured != null, TimeSpan.FromSeconds(2)));
        Assert.True(consumer.StopSafely(timeoutMs: 2000));

        Assert.IsType<IOException>(captured);
    }

    [Fact]
    public void Start_RacedByTwoCallersDuringGrace_SpawnsOnlyOneReader()
    {
        // The grace period widened an existing window: two callers restarting concurrently could
        // both observe a cleared running flag, both wait out the grace, and then each spawn a
        // reader — two Stream.Read loops on one stream. Start() serializes the whole transition,
        // so exactly one replacement reader may exist.
        //
        // Driven through the grace window deliberately: both callers are parked in the stale-thread
        // join when that thread is released, which is the widest the race ever gets.
        using var stream = new ThreadCountingStallingStream();
        using var consumer = new StreamMessageConsumer<string>(stream, new LineBasedMessageParser());

        consumer.Start();
        Assert.True(stream.WaitForReadEntered(TimeSpan.FromSeconds(2)));
        Assert.False(consumer.StopSafely(timeoutMs: 100));

        using var bothInStart = new CountdownEvent(2);
        var failures = new List<Exception>();
        var starters = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
        {
            bothInStart.Signal();
            try
            {
                consumer.Start();
            }
            catch (Exception ex)
            {
                lock (failures) { failures.Add(ex); }
            }
        })).ToArray();

        foreach (var t in starters) t.Start();
        Assert.True(bothInStart.Wait(TimeSpan.FromSeconds(5)));

        // Release the stale reader so both racers' grace joins complete at essentially the same
        // moment — the worst case for the race.
        Thread.Sleep(50);
        stream.Release();

        foreach (var t in starters) Assert.True(t.Join(TimeSpan.FromSeconds(10)));
        Assert.True(consumer.StopSafely(timeoutMs: 2000));

        // One original reader plus at most one replacement. Three distinct reader threads means
        // two were spawned concurrently against the same stream.
        Assert.True(
            stream.DistinctReaderCount <= 2,
            $"{stream.DistinctReaderCount} distinct reader threads touched the stream; at most 2 (original + one replacement) is correct. Failures: {failures.Count}");
    }

    [Fact]
    public void Dispose_RacingAStartInItsGraceWindow_LeavesNoReaderRunning()
    {
        // Start() checks disposal, then can sit in the grace join for up to a second before
        // spawning. If Dispose() slips through in between, a reader outlives the Dispose() call —
        // callbacks firing after teardown, against a stream the owner considers released.
        // Dispose() marks disposal under the same lock Start() holds, so that cannot happen
        // whichever side wins the race.
        var stream = new ThreadCountingStallingStream();
        var consumer = new StreamMessageConsumer<string>(stream, new LineBasedMessageParser());
        try
        {
            consumer.Start();
            Assert.True(stream.WaitForReadEntered(TimeSpan.FromSeconds(2)));
            Assert.False(consumer.StopSafely(timeoutMs: 100));

            // Parks inside Start()'s grace join, holding the lock.
            Exception? starterFailure = null;
            var starter = new Thread(() =>
            {
                try { consumer.Start(); }
                catch (Exception ex) { starterFailure = ex; }
            });
            starter.Start();

            Thread.Sleep(100);           // let the starter reach the grace join
            stream.Release();            // its join can now complete
            consumer.Dispose();          // races the starter's publish

            Assert.True(starter.Join(TimeSpan.FromSeconds(10)));

            // The invariant, regardless of who won: nothing is still reading once Dispose returned.
            Assert.False(
                consumer.IsRunning,
                $"A reader was running after Dispose() returned (starter outcome: {starterFailure?.GetType().Name ?? "started"}).");
        }
        finally
        {
            stream.Release();
            consumer.Dispose();
            stream.Dispose();
        }
    }

    [Fact]
    public void Start_CalledFromMessageReceivedCallbackAfterStop_RefusesWithoutSelfJoin()
    {
        // MessageReceived callbacks run on the consumer thread, so a handler can call Start() after
        // another thread has already cleared the running flag. The stale thread is then *us*:
        // joining it would block for the whole grace period and refuse anyway. Refuse immediately
        // instead, matching the self-join guard ClearBuffer already has.
        // CRLF: LineBasedMessageParser's default line ending.
        using var stream = new SingleLineThenIdleStream("hello\r\n");
        using var consumer = new StreamMessageConsumer<string>(stream, new LineBasedMessageParser());

        using var handlerEntered = new ManualResetEventSlim(false);
        using var proceed = new ManualResetEventSlim(false);
        using var finished = new ManualResetEventSlim(false);
        Exception? captured = null;
        var elapsedMs = -1L;

        consumer.MessageReceived += (_, _) =>
        {
            if (!handlerEntered.IsSet)
            {
                handlerEntered.Set();
                proceed.Wait(TimeSpan.FromSeconds(5));

                var stopwatch = Stopwatch.StartNew();
                try
                {
                    consumer.Start();
                }
                catch (Exception ex)
                {
                    captured = ex;
                }

                stopwatch.Stop();
                elapsedMs = stopwatch.ElapsedMilliseconds;
                finished.Set();
            }
        };

        consumer.Start();
        Assert.True(handlerEntered.Wait(TimeSpan.FromSeconds(5)));

        // Another thread stops the consumer while the callback is parked, so the callback's
        // Start() sees a cleared running flag and a still-alive consumer thread — itself.
        Assert.False(consumer.StopSafely(timeoutMs: 100));
        proceed.Set();

        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsType<ConsumerThreadNotExitedException>(captured);
        Assert.True(
            elapsedMs < 500,
            $"Start() self-joined for {elapsedMs}ms; it must refuse immediately rather than wait out the grace period.");
    }

    [Fact]
    public async Task ProcessMessages_OverIdleTcpConnection_IsSilentAndStopsPromptly()
    {
        // End-to-end over a real socket rather than a hand-rolled exception: this pins down how
        // *this* platform surfaces an expired SO_RCVTIMEO read, so the benign-timeout carve-out
        // can't silently stop matching. Both halves of the #383 fix are asserted here — the idle
        // connection must stay quiet, and the reader must be joinable well inside the 1s budget
        // the consumer-swap path allows.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var transport = new TcpStreamTransport(IPAddress.Loopback, port);
            await transport.ConnectAsync(new ConnectionRetryOptions
            {
                Enabled = false,
                MaxAttempts = 1,
                ConnectionTimeout = TimeSpan.FromSeconds(10)
            });
            // Bounded so a fixture failure fails fast instead of hanging the suite.
            using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));

            using var consumer = new StreamMessageConsumer<string>(transport.Stream, new LineBasedMessageParser());
            var errors = new List<Exception>();
            consumer.ErrorOccurred += (_, e) =>
            {
                lock (errors) { errors.Add(e.Error); }
            };

            consumer.Start();
            // Long enough for several receive timeouts to expire with the peer sending nothing.
            await Task.Delay(TcpStreamTransport.OperationalReceiveTimeoutMs * 3);

            var stopwatch = Stopwatch.StartNew();
            var stopped = consumer.StopSafely(timeoutMs: 1000);
            stopwatch.Stop();

            lock (errors)
            {
                Assert.True(
                    errors.Count == 0,
                    $"Idle TCP reads must not raise ErrorOccurred; got: {string.Join("; ", errors.Select(e => $"{e.GetType().Name}/{(e as IOException)?.InnerException?.GetType().Name}: {e.Message}"))}");
            }

            Assert.True(stopped, "A reader on an idle TCP connection must be joinable within the swap's 1s budget.");
            Assert.True(
                stopwatch.ElapsedMilliseconds < 1000,
                $"Stop took {stopwatch.ElapsedMilliseconds}ms; the operational receive timeout should bound it.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static bool IsConsumerThreadAlive<T>(StreamMessageConsumer<T> consumer)
    {
        var thread = (Thread?)typeof(StreamMessageConsumer<T>)
            .GetField("_consumerThread", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(consumer);
        return thread is { IsAlive: true };
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }

    /// <summary>
    /// A stream whose <see cref="Read(byte[], int, int)"/> blocks until released or until the
    /// supplied stall elapses — stands in for a reader parked in a blocking socket read.
    /// </summary>
    private sealed class StallingStream : Stream
    {
        private readonly TimeSpan _stall;
        private readonly ManualResetEventSlim _release = new(false);
        private readonly ManualResetEventSlim _readEntered = new(false);
        private int _readCount;

        public StallingStream(TimeSpan stall) => _stall = stall;

        public int ReadCount => Volatile.Read(ref _readCount);

        public bool WaitForReadEntered(TimeSpan timeout) => _readEntered.Wait(timeout);

        public void Release() => _release.Set();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref _readCount);
            _readEntered.Set();
            _release.Wait(_stall);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _release.Set();
                _release.Dispose();
                _readEntered.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Blocks the first reader until released, and records how many distinct threads have entered
    /// <see cref="Read(byte[], int, int)"/> — the observable signature of a double-start.
    /// </summary>
    private sealed class ThreadCountingStallingStream : Stream
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly ManualResetEventSlim _readEntered = new(false);
        private readonly HashSet<int> _readerThreadIds = [];

        public int DistinctReaderCount
        {
            get { lock (_readerThreadIds) { return _readerThreadIds.Count; } }
        }

        public bool WaitForReadEntered(TimeSpan timeout) => _readEntered.Wait(timeout);

        public void Release() => _release.Set();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_readerThreadIds)
            {
                _readerThreadIds.Add(Environment.CurrentManagedThreadId);
            }

            _readEntered.Set();
            if (!_release.IsSet)
            {
                _release.Wait(TimeSpan.FromSeconds(10));
            }
            else
            {
                Thread.Sleep(10);
            }

            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _release.Set();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Emits one payload on the first read so exactly one MessageReceived callback fires, then
    /// idles. Used to drive a handler that runs on the consumer thread.
    /// </summary>
    private sealed class SingleLineThenIdleStream : Stream
    {
        private readonly byte[] _payload;
        private int _offset;

        public SingleLineThenIdleStream(string text) => _payload = Encoding.UTF8.GetBytes(text);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _payload.Length - _offset;
            if (remaining <= 0)
            {
                Thread.Sleep(10);
                return 0;
            }

            // Honor count: Stream.Read may never return more than the caller asked for, so serve
            // the payload across as many partial reads as it takes.
            var toCopy = Math.Min(count, remaining);
            Array.Copy(_payload, _offset, buffer, offset, toCopy);
            _offset += toCopy;
            return toCopy;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A stream whose <see cref="Read(byte[], int, int)"/> always throws the exception produced by
    /// the supplied factory, used to model socket read outcomes.
    /// </summary>
    private sealed class TimingOutStream : Stream
    {
        private readonly Func<Exception> _exceptionFactory;
        private int _readCount;

        public TimingOutStream(Func<Exception> exceptionFactory) => _exceptionFactory = exceptionFactory;

        public int ReadCount => Volatile.Read(ref _readCount);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref _readCount);
            // A real socket read would block for the timeout interval before failing; pace the
            // loop so the test doesn't spin a core while it waits.
            Thread.Sleep(5);
            throw _exceptionFactory();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
