using System.Text;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device.Discovery;
using Google.Protobuf;

namespace Daqifi.Core.Tests.Device.Discovery;

/// <summary>
/// Covers the serial probe's request/reply exchange and — the point of #486 — its teardown.
/// </summary>
/// <remarks>
/// <para>
/// The probe used to complete its own continuation on the message consumer's reader thread, because
/// the "status arrived" signal was a <see cref="TaskCompletionSource{TResult}"/> with inline
/// continuations that is completed from inside the consumer's dispatch. Everything after the wait —
/// including the teardown, whose first act is to join that very thread — then ran on the reader
/// itself. The join could not possibly succeed, so it burned its whole 500ms budget and returned
/// with the reader still running over a stream the caller was about to close.
/// </para>
/// <para>
/// These tests assert that property directly rather than by wall clock: after the exchange returns,
/// the thread that was reading the stream has exited, and the continuation did not run on it. Both
/// fail against the inline-continuation version, and neither depends on how fast the machine is.
/// </para>
/// </remarks>
public class SerialProbeTeardownTests
{
    /// <summary>
    /// Stand-in for the read timeout the probe opens its port with (1s on real hardware), kept short
    /// enough that the silent-device cases do not dominate the suite's runtime.
    /// </summary>
    private const int ProbeStartReadTimeoutMs = 400;

    /// <summary>Mirror of the production constant the probe shortens to once a device answers.</summary>
    private const int PostIdentifyReadTimeoutMs = 50;

    private const ulong TestSerialNumber = 9090539562006014104;

    [Fact]
    public async Task RequestDeviceStatusAsync_DeviceAnswers_ReturnsParsedStatus()
    {
        using var stream = new ScriptedProbeStream(ProbeStartReadTimeoutMs, autoRespond: true);

        var status = await SerialDeviceFinder.RequestDeviceStatusAsync(stream, CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal(TestSerialNumber, status.DeviceSn);
        Assert.Equal("Nq1", status.DevicePn);
        Assert.Contains(stream.WrittenCommands, c => c.Contains("SYSInfoPB"));
    }

    [Fact]
    public async Task RequestDeviceStatusAsync_WhenItReturns_ReaderThreadHasExited()
    {
        using var stream = new ScriptedProbeStream(ProbeStartReadTimeoutMs, autoRespond: false);

        var exchange = SerialDeviceFinder.RequestDeviceStatusAsync(stream, CancellationToken.None);

        // Sampled at the moment the exchange completes, not after awaiting it: a reader that is on
        // its way out exits within a read timeout of being asked to, so an assertion made after the
        // await would pass either way and prove nothing.
        bool? readerAliveAtCompletion = null;
        var observed = exchange.ContinueWith(
            _ => readerAliveAtCompletion = stream.FirstReaderThread!.IsAlive,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        stream.WaitForFirstRead();
        stream.RespondWithStatus();
        await observed;

        Assert.NotNull(await exchange);
        // The whole point: the consumer was really joined, so nothing is left reading the stream the
        // caller is about to close under it. With the teardown running on the reader itself, that
        // thread is necessarily still alive at completion — it is the thread completing the task.
        Assert.False(readerAliveAtCompletion);
    }

    [Fact]
    public async Task RequestDeviceStatusAsync_DoesNotCompleteOnTheReaderThread()
    {
        using var stream = new ScriptedProbeStream(ProbeStartReadTimeoutMs, autoRespond: false);

        var exchange = SerialDeviceFinder.RequestDeviceStatusAsync(stream, CancellationToken.None);

        // Attach before answering, so the continuation is guaranteed to run on whichever thread
        // completes the exchange rather than inline on this one.
        var completionThreadId = 0;
        var observed = exchange.ContinueWith(
            _ => completionThreadId = Environment.CurrentManagedThreadId,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        stream.WaitForFirstRead();
        stream.RespondWithStatus();

        await observed;
        Assert.NotNull(await exchange);
        Assert.NotEqual(0, completionThreadId);
        Assert.DoesNotContain(completionThreadId, stream.ReaderThreadIds);
    }

    [Fact]
    public async Task RequestDeviceStatusAsync_SilentDevice_ReturnsNullAfterRetrying()
    {
        using var stream = new ScriptedProbeStream(ProbeStartReadTimeoutMs, autoRespond: false);

        var status = await SerialDeviceFinder.RequestDeviceStatusAsync(stream, CancellationToken.None);

        Assert.Null(status);
        // Deliberately a bound, not an exact count, mirroring
        // RequestDeviceStatusAsync_Cancelled_GivesUpEarlyAndStillTearsDown below. Requests go out
        // on a RetryIntervalMs cadence measured against wall-clock time inside a fixed
        // ResponseTimeoutMs window; a scheduling gap on a loaded CI runner (e.g. a GC pause or
        // thread-pool starvation between polls) can push the loop's next wake-up past the window
        // before the second send's retry mark is reached, so only the first attempt goes out.
        // Observed on CI: expected 2, actual 1. What is always true is that the probe sends at
        // least once and never exceeds MaxRetries — a port that answers nothing must not be probed
        // forever.
        Assert.InRange(stream.WrittenCommands.Count, 1, 2);
        Assert.False(stream.FirstReaderThread!.IsAlive);
    }

    [Fact]
    public async Task RequestDeviceStatusAsync_Cancelled_GivesUpEarlyAndStillTearsDown()
    {
        using var stream = new ScriptedProbeStream(ProbeStartReadTimeoutMs, autoRespond: false);
        using var cts = new CancellationTokenSource();

        var exchange = SerialDeviceFinder.RequestDeviceStatusAsync(stream, cts.Token);
        stream.WaitForFirstRead();
        await cts.CancelAsync();

        // Cancellation ends the wait but is reported as "no device on this port" rather than as an
        // exception: the wait is a Task.WhenAny, which hands back the cancelled delay instead of
        // throwing it. Long-standing behaviour, and callers are unaffected — ProbeSafelyAsync
        // watches the same token itself and is what turns a cancelled sweep into an
        // OperationCanceledException. Pinned here so a future refactor has to choose deliberately.
        Assert.Null(await exchange);
        // Teardown is in a finally, so giving up early must leave the stream just as quiet as the
        // success path does.
        Assert.False(stream.FirstReaderThread!.IsAlive);
        // Deliberately a bound, not an exact count. Requests go out on a RetryIntervalMs cadence and
        // the cancellation lands wherever the scheduler puts it, so asserting that the retry did not
        // happen is a race against a 300ms timer — it failed on CI doing exactly that. What is
        // always true is that the probe never exceeds MaxRetries.
        Assert.InRange(stream.WrittenCommands.Count, 1, 2);
    }

    [Fact]
    public async Task RequestDeviceStatusAsync_LeavesTheStreamOpenForItsOwner()
    {
        using var stream = new ScriptedProbeStream(ProbeStartReadTimeoutMs, autoRespond: true);

        await SerialDeviceFinder.RequestDeviceStatusAsync(stream, CancellationToken.None);

        // The port owner closes the port; the exchange only owns the producer and consumer it
        // started. Disposing the stream here would close the port out from under the DTR-drop
        // settle that follows.
        Assert.Equal(0, stream.DisposeCount);
    }

    [Fact]
    public async Task RequestDeviceStatusAsync_CanBeRunTwiceOverTheSameStream()
    {
        using var stream = new ScriptedProbeStream(ProbeStartReadTimeoutMs, autoRespond: true);

        var first = await SerialDeviceFinder.RequestDeviceStatusAsync(stream, CancellationToken.None);
        var readsDuringFirst = stream.ReadsIssuedWithTimeout.Count;
        var second = await SerialDeviceFinder.RequestDeviceStatusAsync(stream, CancellationToken.None);

        // Only true if the first exchange left no reader behind: a second consumer over a stream
        // that is still being read by the first would interleave reads and shred the framing.
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(TestSerialNumber, second.DeviceSn);
        Assert.Equal(2, stream.ReaderThreadIds.Distinct().Count());
        // Each exchange started from the timeout it found, because the previous one put it back. The
        // second exchange's opening read is the one that matters — it is the read that would have
        // inherited the first exchange's shortened timeout — so it is indexed explicitly rather than
        // covered by an assertion about the start of the list.
        var reads = stream.ReadsIssuedWithTimeout;
        Assert.True(reads.Count > readsDuringFirst, "the second exchange should have read the stream");
        Assert.Equal(ProbeStartReadTimeoutMs, reads[0]);
        Assert.Equal(ProbeStartReadTimeoutMs, reads[readsDuringFirst]);
    }

    [Fact]
    public async Task RequestDeviceStatusAsync_AfterIdentifying_ShortensTheReadTimeoutForTeardown()
    {
        using var stream = new ScriptedProbeStream(ProbeStartReadTimeoutMs, autoRespond: true);

        Assert.NotNull(await SerialDeviceFinder.RequestDeviceStatusAsync(stream, CancellationToken.None));

        // The reader can only notice the imminent stop when its current read returns, so the read it
        // issues after handing over the status is the one teardown has to wait out.
        Assert.Equal(PostIdentifyReadTimeoutMs, stream.ReadTimeoutHistory.First());
        Assert.All(
            stream.ReadsIssuedWithTimeout.Skip(1),
            issued => Assert.Equal(PostIdentifyReadTimeoutMs, issued));
    }

    [Fact]
    public async Task RequestDeviceStatusAsync_AfterIdentifying_RestoresTheReadTimeoutItFound()
    {
        using var stream = new ScriptedProbeStream(ProbeStartReadTimeoutMs, autoRespond: true);

        Assert.NotNull(await SerialDeviceFinder.RequestDeviceStatusAsync(stream, CancellationToken.None));

        // The stream belongs to the caller and stays open, so the faster teardown must not be left
        // behind as a permanently twitchier read timeout on someone else's stream.
        Assert.Equal(ProbeStartReadTimeoutMs, stream.ReadTimeout);
        Assert.Equal([PostIdentifyReadTimeoutMs, ProbeStartReadTimeoutMs], stream.ReadTimeoutHistory);
    }

    [Fact]
    public async Task RequestDeviceStatusAsync_StreamWithoutTimeoutSupport_StillCompletes()
    {
        using var stream = new ScriptedProbeStream(ProbeStartReadTimeoutMs, autoRespond: true)
        {
            RejectTimeoutAccess = true
        };

        // Shortening and restoring are both best-effort: a stream that refuses to talk about its
        // timeouts costs a slower teardown, not a failed probe.
        Assert.NotNull(await SerialDeviceFinder.RequestDeviceStatusAsync(stream, CancellationToken.None));
        Assert.Empty(stream.ReadTimeoutHistory);
    }

    [Fact]
    public async Task RequestDeviceStatusAsync_SilentDevice_LeavesTheReadTimeoutAlone()
    {
        using var stream = new ScriptedProbeStream(ProbeStartReadTimeoutMs, autoRespond: false);

        Assert.Null(await SerialDeviceFinder.RequestDeviceStatusAsync(stream, CancellationToken.None));

        // A port with nothing to say keeps the timeout it started with. Shortening it for the whole
        // probe would make an unresponsive port wake — and throw a TimeoutException — many times a
        // second for the entire response window, which buys nothing: there is no teardown latency to
        // save on a probe that found no device.
        Assert.Empty(stream.ReadTimeoutHistory);
        Assert.All(
            stream.ReadsIssuedWithTimeout,
            issued => Assert.Equal(ProbeStartReadTimeoutMs, issued));
    }

    /// <summary>
    /// A stand-in for an open serial port: reads block for a timeout and then throw
    /// <see cref="TimeoutException"/> exactly as <c>SerialPort.BaseStream</c> does, and writes are
    /// captured so the SCPI request can be answered with a real length-delimited protobuf status
    /// frame. Records every thread that reads it, which is what the teardown assertions key off.
    /// </summary>
    private sealed class ScriptedProbeStream : Stream
    {
        private readonly bool _autoRespond;
        private volatile int _readTimeoutMs;
        private readonly object _gate = new();
        private readonly Queue<byte> _inbound = new();
        private readonly List<string> _written = [];
        private readonly List<int> _readerThreadIds = [];
        private readonly List<int> _readTimeoutHistory = [];
        private readonly List<int> _readsIssuedWithTimeout = [];
        private readonly ManualResetEventSlim _dataAvailable = new(false);
        private readonly ManualResetEventSlim _firstRead = new(false);
        private Thread? _firstReaderThread;

        public ScriptedProbeStream(int readTimeoutMs, bool autoRespond)
        {
            _readTimeoutMs = readTimeoutMs;
            _autoRespond = autoRespond;
        }

        /// <summary>The timeout each read was issued with, in order.</summary>
        public IReadOnlyList<int> ReadsIssuedWithTimeout
        {
            get { lock (_gate) { return _readsIssuedWithTimeout.ToList(); } }
        }

        public int DisposeCount { get; private set; }

        /// <summary>Every value the probe assigned to <see cref="ReadTimeout"/>, in order.</summary>
        public IReadOnlyList<int> ReadTimeoutHistory
        {
            get { lock (_gate) { return _readTimeoutHistory.ToList(); } }
        }

        /// <summary>When set, the stream behaves like one whose timeouts cannot be inspected or set.</summary>
        public bool RejectTimeoutAccess { get; init; }

        public override bool CanTimeout => !RejectTimeoutAccess;

        public override int ReadTimeout
        {
            get => RejectTimeoutAccess ? throw new InvalidOperationException("Timeouts not supported.") : _readTimeoutMs;
            set
            {
                if (RejectTimeoutAccess)
                {
                    throw new InvalidOperationException("Timeouts not supported.");
                }

                _readTimeoutMs = value;
                lock (_gate)
                {
                    _readTimeoutHistory.Add(value);
                }
            }
        }

        public IReadOnlyList<string> WrittenCommands
        {
            get { lock (_gate) { return _written.ToList(); } }
        }

        public IReadOnlyList<int> ReaderThreadIds
        {
            get { lock (_gate) { return _readerThreadIds.ToList(); } }
        }

        public Thread? FirstReaderThread
        {
            get { lock (_gate) { return _firstReaderThread; } }
        }

        /// <summary>
        /// Blocks the calling thread until the consumer has entered its first read, so a test can
        /// answer the probe while the reader is genuinely parked in <see cref="Read(byte[], int, int)"/>.
        /// </summary>
        /// <remarks>
        /// Deliberately synchronous. The exchange this gates is bounded by a fixed 1s wall-clock
        /// response window that is already running by the time a test gets here, and the previous
        /// async form -- <c>await Task.Run(() =&gt; _firstRead.Wait(token))</c> -- spent two
        /// thread-pool queue hops inside that window: one to dispatch the <c>Task.Run</c> body and
        /// one to resume the awaiting test method. On a CI runner with both target frameworks'
        /// test processes running xunit collections in parallel, a saturated pool injects new
        /// worker threads only about twice a second, so those two hops were observed to push the
        /// "device answers" call past the response window: the probe then returned <c>null</c> and
        /// the test failed on an assertion about teardown that never got to run. The consumer's
        /// reader is a dedicated <see cref="Thread"/> rather than a pool work item, so it reaches
        /// its first read regardless of pool pressure and this wait returns promptly.
        /// </remarks>
        public void WaitForFirstRead()
        {
            Assert.True(
                _firstRead.Wait(TimeSpan.FromSeconds(5)),
                "the consumer never entered its first read");
        }

        public void RespondWithStatus()
        {
            var message = new DaqifiOutMessage
            {
                DeviceSn = TestSerialNumber,
                DevicePn = "Nq1",
                DeviceFwRev = "3.7.2",
                // DetectMessageType calls a frame with a non-zero port count a status message.
                AnalogInPortNum = 16,
                DigitalPortNum = 16
            };

            using var buffer = new MemoryStream();
            message.WriteDelimitedTo(buffer);

            lock (_gate)
            {
                foreach (var b in buffer.ToArray())
                {
                    _inbound.Enqueue(b);
                }

                _dataAvailable.Set();
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                _readerThreadIds.Add(Environment.CurrentManagedThreadId);
                _readsIssuedWithTimeout.Add(_readTimeoutMs);
                _firstReaderThread ??= Thread.CurrentThread;
            }

            _firstRead.Set();

            if (!_dataAvailable.Wait(_readTimeoutMs))
            {
                throw new TimeoutException("No data within the read timeout.");
            }

            lock (_gate)
            {
                var read = 0;
                while (read < count && _inbound.Count > 0)
                {
                    buffer[offset + read++] = _inbound.Dequeue();
                }

                if (_inbound.Count == 0)
                {
                    _dataAvailable.Reset();
                }

                return read;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var text = Encoding.ASCII.GetString(buffer, offset, count);
            lock (_gate)
            {
                _written.Add(text);
            }

            if (_autoRespond && text.Contains("SYSInfoPB"))
            {
                RespondWithStatus();
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override void Flush() { }
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
                _dataAvailable.Dispose();
                _firstRead.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
