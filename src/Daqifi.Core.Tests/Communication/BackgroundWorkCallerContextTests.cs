using System.Text;
using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Tests.Communication;

/// <summary>
/// Long-lived background work must not inherit the starting flow's <see cref="AsyncLocal{T}"/>
/// state. The device keeps "inside a text exchange", "holds the operation lock" and "inside a
/// lifecycle operation" that way, and a reader restarted at the end of a raw capture, or a pump
/// or timer created inside a connect, used to carry the flag for its whole life: a text query
/// from it was refused as re-entrant, and a reconnect started from it walked past gates it did
/// not hold.
/// </summary>
public class BackgroundWorkCallerContextTests
{
    private static readonly AsyncLocal<bool> InsideAnOperation = new();

    [Fact]
    public void TheConsumersReaderThread_DoesNotInheritTheStartersFlowState()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("line\r\n"));
        using var consumer = new StreamMessageConsumer<string>(stream, new LineBasedMessageParser());
        bool? seenOnReader = null;
        using var received = new ManualResetEventSlim();
        consumer.MessageReceived += (_, _) =>
        {
            seenOnReader = InsideAnOperation.Value;
            received.Set();
        };

        StartInsideAnOperation(consumer.Start);

        Assert.True(received.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(seenOnReader);
        consumer.Stop();
    }

    [Fact]
    public void TheProducersWriterThread_DoesNotInheritTheStartersFlowState()
    {
        using var stream = new ThrowOnWriteStream();
        using var producer = new MessageProducer<string>(stream);
        bool? seenOnWriter = null;
        using var failed = new ManualResetEventSlim();
        producer.SendFailed += (_, _) =>
        {
            seenOnWriter = InsideAnOperation.Value;
            failed.Set();
        };

        StartInsideAnOperation(producer.Start);
        producer.Send(new ScpiMessage("*IDN?"));

        Assert.True(failed.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(seenOnWriter);
        producer.Stop();
    }

    [Fact]
    public void TheWatchdogsPresenceTimer_DoesNotInheritTheStartersFlowState()
    {
        using var watchdog = new TransportConnectionWatchdog("Test transport (X)", (_, _) => { });
        watchdog.Arm(new object());
        bool? seenOnTimer = null;
        using var polled = new ManualResetEventSlim();

        StartInsideAnOperation(() => watchdog.StartPresencePolling(
            () =>
            {
                seenOnTimer = InsideAnOperation.Value;
                polled.Set();
                return true;
            },
            "gone",
            TimeSpan.FromMilliseconds(10)));

        Assert.True(polled.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(seenOnTimer);
    }

    /// <summary>Runs <paramref name="start"/> on a flow that has the operation flag set.</summary>
    private static void StartInsideAnOperation(Action start)
    {
        var thread = new Thread(() =>
        {
            InsideAnOperation.Value = true;
            start();
        });
        thread.Start();
        thread.Join();
    }

    private sealed class ThrowOnWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("write failed");
    }
}
