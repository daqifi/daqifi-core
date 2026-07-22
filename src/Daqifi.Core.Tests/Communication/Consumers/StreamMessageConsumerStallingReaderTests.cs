using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

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
