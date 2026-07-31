using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Tests.Communication.Consumers;

/// <summary>
/// No failure mode in the reader loop may spin without progress. A failure that repeats every
/// iteration must be backed off, and a failure that is genuinely I/O against the stream must reach
/// the transport so a dead link can still be escalated (issues #377, #378).
/// </summary>
public class StreamMessageConsumerBackoffTests
{
    /// <summary>
    /// Window over which raise counts are sampled. With the loop's 100 ms error back-off this
    /// bounds a repeating failure to a handful of raises; without one it is limited only by how
    /// fast the thread can spin.
    /// </summary>
    private static readonly TimeSpan SampleWindow = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// Generous ceiling for "backed off" over <see cref="SampleWindow"/>. The unbacked-off loop
    /// produces tens of thousands in the same window, so the separation is not marginal.
    /// </summary>
    private const int BackedOffCeiling = 25;

    [Fact]
    public void WhenTheCanReadProbeThrows_ItIsTreatedAsAStreamFaultAndReportedToTheTransport()
    {
        // Probing readability touches the same handle a read does, and on a half-torn-down stream
        // the getter itself can throw. That is the link failing, so it has to reach the transport —
        // otherwise this one failure mode silently bypasses connection-loss escalation.
        using var stream = new ThrowingCanReadStream();
        var sink = new RecordingHealthSink();
        var errors = 0;

        using var consumer = new StreamMessageConsumer<string>(
            stream, new LineBasedMessageParser(), healthSink: sink);
        consumer.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);

        consumer.Start();

        Assert.True(
            WaitUntil(() => sink.FaultCount >= TransportConnectionWatchdog.ConsecutiveFaultThreshold,
                TimeSpan.FromSeconds(10)),
            $"a throwing CanRead must be reported to the transport, saw {sink.FaultCount} fault(s)");

        Assert.True(Volatile.Read(ref errors) >= 1, "it must also be visible as a consumer error");

        consumer.StopSafely(timeoutMs: 2000);
    }

    [Fact]
    public void WhenTheCanReadProbeThrows_TheLoopBacksOffInsteadOfSpinning()
    {
        using var stream = new ThrowingCanReadStream();
        var errors = 0;

        using var consumer = new StreamMessageConsumer<string>(stream, new LineBasedMessageParser());
        consumer.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);

        consumer.Start();
        Thread.Sleep(SampleWindow);
        consumer.StopSafely(timeoutMs: 2000);

        var raised = Volatile.Read(ref errors);
        Assert.True(raised >= 1, "the failure must be reported at all");
        Assert.True(raised <= BackedOffCeiling, $"expected a backed-off cadence, saw {raised} raises");
    }

    [Fact]
    public void WhenParsingThrowsOnEveryIteration_TheLoopBacksOffInsteadOfSpinning()
    {
        // A parser that throws on the bytes it holds will throw on exactly the same bytes next time
        // round, so retrying at full speed makes no progress and burns a core while raising errors
        // as fast as the thread can go.
        using var stream = new AlwaysReadableStream();
        var sink = new RecordingHealthSink();
        var errors = 0;

        using var consumer = new StreamMessageConsumer<string>(
            stream, new AlwaysThrowingParser(), healthSink: sink);
        consumer.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);

        consumer.Start();
        Thread.Sleep(SampleWindow);
        consumer.StopSafely(timeoutMs: 2000);

        var raised = Volatile.Read(ref errors);
        Assert.True(raised >= 1, "the parse failure must be reported at all");
        Assert.True(raised <= BackedOffCeiling, $"expected a backed-off cadence, saw {raised} raises");
    }

    [Fact]
    public void AParseFailure_IsNeverReportedToTheTransportAsAnIoFault()
    {
        // The counterpart to the back-off: a parse failure means the bytes were bad, not that the
        // link is gone. Escalating it would disconnect a perfectly healthy device over malformed
        // data — the reads themselves are succeeding.
        using var stream = new AlwaysReadableStream();
        var sink = new RecordingHealthSink();

        using var consumer = new StreamMessageConsumer<string>(
            stream, new AlwaysThrowingParser(), healthSink: sink);

        consumer.Start();
        Thread.Sleep(SampleWindow);
        consumer.StopSafely(timeoutMs: 2000);

        Assert.Equal(0, sink.FaultCount);
        Assert.True(sink.SuccessCount >= 1, "the reads themselves were fine and must be reported as such");
    }

    [Fact]
    public void AFailureThatLandsWhileStopping_IsSwallowedRatherThanKillingTheProcess()
    {
        // The reader loop runs on a background thread, so an exception that escapes its catch does
        // not just end the loop — it terminates the host process. A stop that lands while the try
        // body is mid-flight used to do exactly that, because the catch filter stopped matching the
        // moment the running flag cleared.
        //
        // Reaching the end of this test at all is the assertion: if the exception escapes, the test
        // host dies and the whole run aborts.
        using var stream = new AlwaysReadableStream();
        var parser = new GatedThrowingParser();
        var errors = 0;

        using var consumer = new StreamMessageConsumer<string>(stream, parser);
        consumer.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);

        consumer.Start();

        Assert.True(parser.Entered.Wait(TimeSpan.FromSeconds(10)), "the parser was never reached");

        // Clear the running flag while the parser is parked inside the try body, then let it throw.
        var stopper = new Thread(() => consumer.StopSafely(timeoutMs: 5000)) { IsBackground = true };
        stopper.Start();
        Assert.True(WaitUntil(() => !consumer.IsRunning, TimeSpan.FromSeconds(10)));
        parser.Release.Set();

        Assert.True(stopper.Join(TimeSpan.FromSeconds(10)), "the consumer never stopped");
        Assert.False(consumer.IsRunning);

        // The failure arrived during teardown, so it says nothing about the device and is not
        // reported as a device-visible error.
        Assert.Equal(0, Volatile.Read(ref errors));
    }

    [Fact]
    public void AStopThatLandsDuringTheReadabilityProbe_ReportsNothing()
    {
        // Closing a handle is *supposed* to make the reader fail, and the stream going unreadable is
        // exactly what a deliberate Disconnect looks like from inside the loop. Reporting that would
        // put a phantom "the connection died" into the transport's health sink and into consumer
        // diagnostics on every intentional teardown.
        using var stream = new GatedUnreadableStream();
        var sink = new RecordingHealthSink();
        var errors = 0;

        using var consumer = new StreamMessageConsumer<string>(
            stream, new LineBasedMessageParser(), healthSink: sink);
        consumer.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);

        consumer.Start();

        Assert.True(stream.ProbeEntered.Wait(TimeSpan.FromSeconds(10)), "the probe was never reached");

        // Clear the running flag while the reader is parked inside the probe, then let the stream
        // report itself unreadable — the shape of a handle closed underneath an in-flight read.
        var stopper = new Thread(() => consumer.StopSafely(timeoutMs: 5000)) { IsBackground = true };
        stopper.Start();
        Assert.True(WaitUntil(() => !consumer.IsRunning, TimeSpan.FromSeconds(10)));
        stream.ReleaseProbe.Set();

        Assert.True(stopper.Join(TimeSpan.FromSeconds(10)), "the consumer never stopped");

        Assert.Equal(0, sink.FaultCount);
        Assert.Equal(0, Volatile.Read(ref errors));
    }

    [Fact]
    public void AStopThatLandsDuringAReadFailure_ReportsNothing()
    {
        // Same rule for the read itself, which is the path a real teardown almost always takes:
        // the handle is closed, and the in-flight Read throws because of it.
        using var stream = new GatedThrowingReadStream();
        var sink = new RecordingHealthSink();
        var errors = 0;

        using var consumer = new StreamMessageConsumer<string>(
            stream, new LineBasedMessageParser(), healthSink: sink);
        consumer.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);

        consumer.Start();

        Assert.True(stream.ReadEntered.Wait(TimeSpan.FromSeconds(10)), "the read was never reached");

        var stopper = new Thread(() => consumer.StopSafely(timeoutMs: 5000)) { IsBackground = true };
        stopper.Start();
        Assert.True(WaitUntil(() => !consumer.IsRunning, TimeSpan.FromSeconds(10)));
        stream.ReleaseRead.Set();

        Assert.True(stopper.Join(TimeSpan.FromSeconds(10)), "the consumer never stopped");

        Assert.Equal(0, sink.FaultCount);
        Assert.Equal(0, Volatile.Read(ref errors));
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

    private sealed class RecordingHealthSink : ITransportHealthSink
    {
        private int _faultCount;
        private int _successCount;

        public int FaultCount => Volatile.Read(ref _faultCount);

        public int SuccessCount => Volatile.Read(ref _successCount);

        public void ReportIoFault(Exception error) => Interlocked.Increment(ref _faultCount);

        public void ReportIoSuccess() => Interlocked.Increment(ref _successCount);
    }

    /// <summary>
    /// A parser that fails on everything it is handed, standing in for a systematic parse failure.
    /// </summary>
    private sealed class AlwaysThrowingParser : IMessageParser<string>
    {
        public IEnumerable<IInboundMessage<string>> ParseMessages(byte[] data, out int consumedBytes)
        {
            consumedBytes = 0;
            throw new FormatException("this parser cannot handle anything");
        }
    }

    /// <summary>
    /// A parser that parks inside <see cref="ParseMessages"/> until released, then throws — so a
    /// stop can be made to land precisely while the reader is inside its try body.
    /// </summary>
    private sealed class GatedThrowingParser : IMessageParser<string>
    {
        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public IEnumerable<IInboundMessage<string>> ParseMessages(byte[] data, out int consumedBytes)
        {
            consumedBytes = 0;
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(30));
            throw new FormatException("failing after the stop landed");
        }
    }

    /// <summary>
    /// A readable stream that always yields a byte, so the loop reaches the parse stage every
    /// iteration.
    /// </summary>
    private sealed class AlwaysReadableStream : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            buffer[offset] = (byte)'x';
            return 1;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A stream whose readability probe parks until released and then reports the stream
    /// unreadable, so a stop can be made to land precisely while the reader is inside the probe.
    /// </summary>
    private sealed class GatedUnreadableStream : Stream
    {
        private int _probeCount;

        public ManualResetEventSlim ProbeEntered { get; } = new(false);

        public ManualResetEventSlim ReleaseProbe { get; } = new(false);

        public override bool CanRead
        {
            get
            {
                // Only gate the first probe; later ones (e.g. from PerformClear) answer at once.
                if (Interlocked.Increment(ref _probeCount) != 1)
                {
                    return false;
                }

                ProbeEntered.Set();
                ReleaseProbe.Wait(TimeSpan.FromSeconds(30));
                return false;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A stream whose read parks until released and then fails, reproducing the in-flight read that
    /// a closing handle breaks.
    /// </summary>
    private sealed class GatedThrowingReadStream : Stream
    {
        public ManualResetEventSlim ReadEntered { get; } = new(false);

        public ManualResetEventSlim ReleaseRead { get; } = new(false);

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadEntered.Set();
            ReleaseRead.Wait(TimeSpan.FromSeconds(30));
            throw new IOException("the handle was closed underneath this read");
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A stream whose readability probe itself fails, as a handle torn down underneath the reader
    /// can.
    /// </summary>
    private sealed class ThrowingCanReadStream : Stream
    {
        public override bool CanRead => throw new ObjectDisposedException(nameof(ThrowingCanReadStream));

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new ObjectDisposedException(nameof(ThrowingCanReadStream));

        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
