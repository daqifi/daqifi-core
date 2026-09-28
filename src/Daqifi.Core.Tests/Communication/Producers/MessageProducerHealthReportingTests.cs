using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Tests.TestSupport;

namespace Daqifi.Core.Tests.Communication.Producers;

/// <summary>
/// Issue #382: a write that keeps failing is, like a read that keeps failing, evidence the device
/// is gone. The producer must report both outcomes to the transport instead of silently draining
/// the queue into a dead stream.
/// </summary>
public class MessageProducerHealthReportingTests
{
    [Fact]
    public void WhenWritesFail_TheProducerReportsFaultsAndKeepsDraining()
    {
        using var stream = new FailingWriteStream { FailWrites = true };
        var sink = new CountingHealthSink();
        using var producer = new MessageProducer<string>(stream, healthSink: sink);

        producer.Start();
        for (var i = 0; i < 5; i++)
        {
            producer.Send(new ScpiMessage($"CMD{i}"));
        }

        WaitUntil.That(
            () => sink.FaultCount >= 5,
            () => $"expected a fault report per failed write, saw {sink.FaultCount}",
            TimeSpan.FromSeconds(5));
        Assert.Equal(0, sink.SuccessCount);

        // Failures must not stall the loop: the queue still drains. IsIdle rather than the queue
        // count, which reaches zero as the last message is dequeued, before its write has run.
        WaitUntil.That(
            () => producer.IsIdle,
            "the producer never drained the failed writes",
            TimeSpan.FromSeconds(5));
        Assert.True(producer.IsRunning);
    }

    [Fact]
    public void WhenWritesSucceed_TheProducerReportsSuccess()
    {
        using var stream = new FailingWriteStream();
        var sink = new CountingHealthSink();
        using var producer = new MessageProducer<string>(stream, healthSink: sink);

        producer.Start();
        producer.Send(new ScpiMessage("CMD"));

        WaitUntil.That(
            () => sink.SuccessCount >= 1,
            "the producer never reported a successful write",
            TimeSpan.FromSeconds(5));
        Assert.Equal(0, sink.FaultCount);
    }

    [Fact]
    public void WhenWritesTimeOut_NoFaultIsReported()
    {
        // A write timeout means the device is not draining its buffer right now, not that the
        // link is gone. Escalating it would disconnect a healthy but momentarily busy device.
        using var stream = new FailingWriteStream { TimeoutWrites = true };
        var sink = new CountingHealthSink();
        using var producer = new MessageProducer<string>(stream, healthSink: sink);

        producer.Start();
        for (var i = 0; i < 5; i++)
        {
            producer.Send(new ScpiMessage($"CMD{i}"));
        }

        // IsIdle, not the queue count: the queue empties as the last message is dequeued, before
        // its write times out and before anything is (or is wrongly) reported for it. IsIdle
        // also covers the write in flight, so every outcome has reached the sink once it holds.
        WaitUntil.That(
            () => producer.IsIdle,
            "the producer never drained the timed-out writes",
            TimeSpan.FromSeconds(5));
        Assert.Equal(5L, producer.StartedWriteCount);
        Assert.Equal(0, sink.FaultCount);
        Assert.Equal(0, sink.SuccessCount);
    }

    [Fact]
    public void WithNoHealthSink_TheProducerBehavesExactlyAsBefore()
    {
        using var stream = new FailingWriteStream { FailWrites = true };
        using var producer = new MessageProducer<string>(stream);

        producer.Start();
        producer.Send(new ScpiMessage("CMD"));

        WaitUntil.That(
            () => producer.IsIdle,
            "the producer never drained without a health sink",
            TimeSpan.FromSeconds(5));
        Assert.True(producer.IsRunning);
    }

    private sealed class CountingHealthSink : ITransportHealthSink
    {
        private int _faultCount;
        private int _successCount;

        public int FaultCount => Volatile.Read(ref _faultCount);
        public int SuccessCount => Volatile.Read(ref _successCount);

        public void ReportIoFault(Exception error) => Interlocked.Increment(ref _faultCount);
        public void ReportIoSuccess() => Interlocked.Increment(ref _successCount);
    }

    private sealed class FailingWriteStream : MemoryStream
    {
        public volatile bool FailWrites;
        public volatile bool TimeoutWrites;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (TimeoutWrites)
            {
                throw new TimeoutException("the device is not draining its buffer");
            }

            if (FailWrites)
            {
                throw new IOException("the device is gone");
            }

            base.Write(buffer, offset, count);
        }
    }
}
