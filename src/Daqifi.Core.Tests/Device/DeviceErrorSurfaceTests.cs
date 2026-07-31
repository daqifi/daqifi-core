using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using System.Net;
using System.Text;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Issue #378: background failures used to be invisible — the message consumer raised
/// <c>ErrorOccurred</c> into an event with no subscribers, and a per-frame decode failure was
/// swallowed by an empty catch. Both now reach <see cref="DaqifiDevice.ErrorOccurred"/> without
/// changing anything about how the device behaves.
/// </summary>
public class DeviceErrorSurfaceTests
{
    #region Message consumer errors

    [Fact]
    public void AFailingReadLoop_ReachesAnErrorOccurredSubscriber()
    {
        using var transport = new ScriptedStreamTransport();
        using var device = new DaqifiDevice("Erroring Device", transport);

        var raised = new ManualResetEventSlim(false);
        DeviceErrorEventArgs? captured = null;
        device.ErrorOccurred += (_, e) =>
        {
            captured = e;
            raised.Set();
        };

        device.Connect();
        transport.ScriptedStream.FailReads = true;

        Assert.True(raised.Wait(TimeSpan.FromSeconds(10)), "the read failure never reached a subscriber");
        Assert.Equal(DeviceErrorSource.MessageConsumer, captured!.Source);
        Assert.IsType<IOException>(captured.Error);
    }

    [Fact]
    public void AHealthyIdleDevice_RaisesNoErrors()
    {
        // The bench case: a connected device that simply has nothing to say must stay quiet.
        // A read timeout is what an idle device looks like, not a failure.
        using var transport = new ScriptedStreamTransport();
        using var device = new DaqifiDevice("Quiet Device", transport);

        var errors = 0;
        device.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);

        device.Connect();
        transport.ScriptedStream.TimeoutReads = true;

        Thread.Sleep(500);

        Assert.Equal(0, Volatile.Read(ref errors));
        Assert.Equal(ConnectionStatus.Connected, device.Status);
    }

    [Fact]
    public void AThrowingErrorSubscriber_DoesNotDisturbTheReaderLoop()
    {
        using var transport = new ScriptedStreamTransport();
        using var device = new DaqifiDevice("Erroring Device", transport);

        var raised = new ManualResetEventSlim(false);
        device.ErrorOccurred += (_, _) =>
        {
            raised.Set();
            throw new InvalidOperationException("a badly behaved subscriber");
        };

        device.Connect();
        transport.ScriptedStream.FailReads = true;
        Assert.True(raised.Wait(TimeSpan.FromSeconds(10)));

        // The reader survived the throwing handler: it is still issuing reads, and the device was
        // not knocked out of Connected by it.
        var readsSoFar = transport.ScriptedStream.ReadCount;
        Assert.True(WaitUntil(() => transport.ScriptedStream.ReadCount > readsSoFar + 2, TimeSpan.FromSeconds(10)),
            "the reader loop stopped issuing reads after a subscriber threw");
        Assert.Equal(ConnectionStatus.Connected, device.Status);
    }

    [Fact]
    public void AfterDisconnect_TheConsumerErrorSurfaceIsDetached()
    {
        using var transport = new ScriptedStreamTransport();
        using var device = new DaqifiDevice("Erroring Device", transport);

        var errors = 0;
        device.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);

        device.Connect();
        device.Disconnect();

        var afterDisconnect = Volatile.Read(ref errors);
        transport.ScriptedStream.FailReads = true;
        Thread.Sleep(400);

        Assert.Equal(afterDisconnect, Volatile.Read(ref errors));
    }

    #endregion

    #region Stream decode errors

    [Fact]
    public void ASystematicallyThrowingDecode_StaysObservableWhileTheStreamKeepsRunning()
    {
        var device = CreateStreamingDevice();
        var channel = (IAnalogChannel)device.Channels.First(c => c.Type == ChannelType.Analog);
        channel.IsEnabled = true;

        // A subscriber that throws on every sample is the realistic shape of a decode that fails
        // on every frame: it propagates out of the per-channel push and into the frame's catch.
        channel.SampleReceived += (_, _) => throw new InvalidOperationException("decode consumer is broken");

        var errors = new List<DeviceErrorEventArgs>();
        device.ErrorOccurred += (_, e) =>
        {
            lock (errors)
            {
                errors.Add(e);
            }
        };

        var rawFrames = 0;
        device.StreamMessageReceived += _ => Interlocked.Increment(ref rawFrames);

        device.StartStreaming();

        const int frameCount = 200;
        for (var i = 1; i <= frameCount; i++)
        {
            device.InvokeStreamMessage(AnalogFrame((uint)(i * 1000), 1.0f));
        }

        // Isolation is unchanged: every frame was still delivered, and the stream is still running.
        Assert.Equal(frameCount, Volatile.Read(ref rawFrames));
        Assert.True(device.IsStreaming);

        // But the failure is no longer silent.
        Assert.Equal(frameCount, device.DecodeFailureCount);

        lock (errors)
        {
            var error = Assert.Single(errors);
            Assert.Equal(DeviceErrorSource.StreamDecode, error.Source);
            Assert.IsType<InvalidOperationException>(error.Error);
        }
    }

    [Fact]
    public void ADecodeStorm_IsBoundedByTheThrottle()
    {
        // The volume guarantee from the acceptance criteria: thousands of identical failures must
        // not become thousands of events.
        var device = CreateStreamingDevice();
        var channel = (IAnalogChannel)device.Channels.First(c => c.Type == ChannelType.Analog);
        channel.IsEnabled = true;
        channel.SampleReceived += (_, _) => throw new InvalidOperationException("decode consumer is broken");

        var raises = 0;
        device.ErrorOccurred += (_, _) => Interlocked.Increment(ref raises);

        device.StartStreaming();

        const int frameCount = 5000;
        for (var i = 1; i <= frameCount; i++)
        {
            device.InvokeStreamMessage(AnalogFrame((uint)(i * 1000), 1.0f));
        }

        Assert.Equal(frameCount, device.DecodeFailureCount);

        // Bounded, not proportional. The policy allows one raise per five seconds per bucket, so a
        // fast machine sees exactly one; the upper bound leaves room for a slow one without turning
        // this into a timing test.
        Assert.InRange(Volatile.Read(ref raises), 1, 3);
    }

    [Fact]
    public void AHealthyDecode_LeavesTheFailureCounterAtZeroAndRaisesNothing()
    {
        var device = CreateStreamingDevice();
        var channel = (IAnalogChannel)device.Channels.First(c => c.Type == ChannelType.Analog);
        channel.IsEnabled = true;

        var samples = 0;
        channel.SampleReceived += (_, _) => Interlocked.Increment(ref samples);

        var raises = 0;
        device.ErrorOccurred += (_, _) => Interlocked.Increment(ref raises);

        device.StartStreaming();
        for (var i = 1; i <= 50; i++)
        {
            device.InvokeStreamMessage(AnalogFrame((uint)(i * 1000), 1.0f));
        }

        Assert.Equal(50, Volatile.Read(ref samples));
        Assert.Equal(0, device.DecodeFailureCount);
        Assert.Equal(0, Volatile.Read(ref raises));
    }

    [Fact]
    public void TheDecodeFailureCounter_DescribesTheCurrentSession()
    {
        var device = CreateStreamingDevice();
        var channel = (IAnalogChannel)device.Channels.First(c => c.Type == ChannelType.Analog);
        channel.IsEnabled = true;
        channel.SampleReceived += (_, _) => throw new InvalidOperationException("decode consumer is broken");

        device.StartStreaming();
        device.InvokeStreamMessage(AnalogFrame(1000, 1.0f));
        device.InvokeStreamMessage(AnalogFrame(2000, 1.0f));
        Assert.Equal(2, device.DecodeFailureCount);

        device.StopStreaming();
        device.StartStreaming();

        Assert.Equal(0, device.DecodeFailureCount);
    }

    [Fact]
    public void AFrameThatArrivesOutsideAStreamingSession_IsNotCountedAsADecodeFailure()
    {
        // Frames outside a session are re-raised but never decoded, so nothing can fail.
        var device = CreateStreamingDevice();
        var channel = (IAnalogChannel)device.Channels.First(c => c.Type == ChannelType.Analog);
        channel.IsEnabled = true;
        channel.SampleReceived += (_, _) => throw new InvalidOperationException("decode consumer is broken");

        device.InvokeStreamMessage(AnalogFrame(1000, 1.0f));

        Assert.Equal(0, device.DecodeFailureCount);
    }

    #endregion

    #region Helpers

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

    private static DecodableStreamingDevice CreateStreamingDevice()
    {
        var device = new DecodableStreamingDevice("Decode Device");
        device.Connect();

        var status = new DaqifiOutMessage
        {
            AnalogInPortNum = 1,
            AnalogInRes = 65535,
        };
        status.AnalogInPortRange.Add(1.0f);

        device.PopulateChannelsFromStatus(status);
        return device;
    }

    private static DaqifiOutMessage AnalogFrame(uint timestamp, float value)
    {
        var frame = new DaqifiOutMessage { MsgTimeStamp = timestamp };
        frame.AnalogInDataFloat.Add(value);
        return frame;
    }

    /// <summary>
    /// A streaming device with no transport, exposing the protected stream handler so frames can be
    /// injected directly and swallowing outbound SCPI.
    /// </summary>
    private sealed class DecodableStreamingDevice : DaqifiStreamingDevice
    {
        public DecodableStreamingDevice(string name, IPAddress? ipAddress = null) : base(name, ipAddress)
        {
        }

        public void InvokeStreamMessage(DaqifiOutMessage message) => OnStreamMessageReceived(message);

        public override void Send<T>(IOutboundMessage<T> message)
        {
        }
    }

    /// <summary>
    /// A transport over a stream whose reads can be made to fail or time out on demand, so a device
    /// can be driven through a failing read loop without hardware.
    /// </summary>
    private sealed class ScriptedStreamTransport : IStreamTransport
    {
        private bool _isConnected;
        private bool _disposed;

        public ScriptedStream ScriptedStream { get; } = new();

        public Stream Stream => _disposed
            ? throw new ObjectDisposedException(nameof(ScriptedStreamTransport))
            : ScriptedStream;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Scripted: Connected" : "Scripted: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            _isConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            _isConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
            return Task.CompletedTask;
        }

        public void Connect() => ConnectAsync().GetAwaiter().GetResult();

        public void Disconnect() => DisconnectAsync().GetAwaiter().GetResult();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _isConnected = false;
            _disposed = true;
            ScriptedStream.Dispose();
        }
    }

    /// <summary>
    /// A stream whose reads can be switched between delivering scripted bytes, timing out (an idle
    /// device), and failing (a device that has gone away).
    /// </summary>
    internal sealed class ScriptedStream : Stream
    {
        private readonly Queue<byte[]> _pending = new();
        private readonly object _gate = new();
        private int _readCount;

        public volatile bool FailReads;
        public volatile bool TimeoutReads;

        public int ReadCount => Volatile.Read(ref _readCount);

        public override int Read(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref _readCount);

            if (TimeoutReads)
            {
                Thread.Sleep(5);
                throw new TimeoutException("no data within the read timeout");
            }

            if (FailReads)
            {
                Thread.Sleep(5);
                throw new IOException("the device is gone");
            }

            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    Thread.Sleep(5);
                    return 0;
                }

                var chunk = _pending.Dequeue();
                var length = Math.Min(chunk.Length, count);
                Array.Copy(chunk, 0, buffer, offset, length);
                return length;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            // Outbound SCPI goes nowhere; these tests only exercise the read side.
            _ = Encoding.UTF8.GetString(buffer, offset, count);
        }

        public override bool CanRead => true;
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

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    #endregion
}
