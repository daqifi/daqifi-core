using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Tests for issue #899: a rate above what the enabled channels can sustain used to reach the
/// device, which answered it with SCPI <c>-222</c> and streamed nothing — a session that
/// reported itself as running and delivered no samples, with no exception and no error event.
/// </summary>
/// <remarks>
/// The bench figures quoted here are from an NQ1 on firmware 3.8.0 with two analog channels
/// enabled: the board ceiling is 22000 Hz, the cap for that channel set is 6924 Hz, 6924 Hz gave
/// 69,198 samples in 5 s and 10000 Hz gave none.
/// </remarks>
public class DaqifiStreamingDeviceRateCapTests
{
    private const int BoardCeilingHz = 22000;
    private const int TwoChannelCapHz = 6924;

    #region The cap is applied at the start, not left to the caller

    [Fact]
    public void StartStreaming_RateUnderTheCap_StreamsAtTheRateAsked()
    {
        var device = CreateDevice(currentMaximumRateHz: TwoChannelCapHz);
        device.StreamingFrequency = 1000;

        device.StartStreaming();

        Assert.True(device.IsStreaming);
        Assert.Equal(1000, device.StreamingFrequency);
        Assert.Equal(
            ScpiMessageProducer.StartStreaming(1000).Data,
            Assert.Single(device.SentMessages).Data);
    }

    [Fact]
    public void StartStreaming_RateAboveTheCap_LowersItBeforeSendingTheStart()
    {
        // The rate the device would refuse. The setter accepts it — 10000 is under the board
        // ceiling — so nothing before this point had any reason to complain.
        var device = CreateDevice(currentMaximumRateHz: TwoChannelCapHz);
        device.StreamingFrequency = 10000;
        Assert.Equal(10000, device.StreamingFrequency);

        device.StartStreaming();

        Assert.Equal(TwoChannelCapHz, device.StreamingFrequency);
        Assert.Equal(
            ScpiMessageProducer.StartStreaming(TwoChannelCapHz).Data,
            Assert.Single(device.SentMessages).Data);
    }

    [Fact]
    public void StartStreaming_RateAtTheBoardCeilingWithChannelsEnabled_IsStillLowered()
    {
        // The exact case the issue measured: 22000 Hz is legal to set and produces no samples.
        var device = CreateDevice(currentMaximumRateHz: TwoChannelCapHz);
        device.StreamingFrequency = BoardCeilingHz;

        device.StartStreaming();

        Assert.Equal(TwoChannelCapHz, device.StreamingFrequency);
    }

    [Fact]
    public void StartStreaming_WithNothingEnabled_LeavesTheRateAlone()
    {
        // A cap of 0 is "no analog input is enabled", not "stream at 0 Hz". Driving the rate to
        // the cap here would reject every rate; the rate is stale, not invalid.
        var device = CreateDevice(currentMaximumRateHz: 0);
        device.StreamingFrequency = 10000;

        device.StartStreaming();

        Assert.Equal(10000, device.StreamingFrequency);
    }

    [Fact]
    public void StartStreaming_DeviceStatedNoCap_LeavesTheRateAlone()
    {
        // A device that has said nothing about how its channel set affects the rate reports the
        // board ceiling, so a rate under that ceiling must still go out unchanged.
        var device = CreateDevice(currentMaximumRateHz: null);
        device.StreamingFrequency = 10000;

        device.StartStreaming();

        Assert.Equal(10000, device.StreamingFrequency);
    }

    [Fact]
    public void StartStreaming_AlreadyStreaming_DoesNotTouchTheRate()
    {
        var device = CreateDevice(currentMaximumRateHz: TwoChannelCapHz);
        device.StreamingFrequency = 1000;
        device.StartStreaming();
        device.SentMessages.Clear();

        // The cap dropped under the live rate, but a restart is not a session boundary and the
        // early return must stay an early return.
        device.Metadata.ApplyCapabilityDocument(BenchDocument(500));
        device.StartStreaming();

        Assert.Equal(1000, device.StreamingFrequency);
        Assert.Empty(device.SentMessages);
    }

    #endregion

    #region The rejection message quotes the limit that applies

    [Fact]
    public void StreamingFrequency_AboveTheCeiling_NamesTheCeilingAndTheChannelSetCap()
    {
        var device = CreateDevice(currentMaximumRateHz: 5713);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => device.StreamingFrequency = BoardCeilingHz + 1);

        Assert.Contains("at most 22000 Hz", exception.Message);
        Assert.Contains("5713 Hz", exception.Message);
    }

    [Fact]
    public void StreamingFrequency_BelowOne_NamesOnlyTheLowerBound()
    {
        // The issue's exact complaint: "StreamingFrequency = 0" reported
        // "must be between 1 and 22000 Hz (the device's maximum sampling rate)" while the live
        // cap was 5713 — a ceiling the value never approached, described as the limit a stream
        // is held to.
        var device = CreateDevice(currentMaximumRateHz: 5713);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => device.StreamingFrequency = 0);

        Assert.Contains("at least 1 Hz", exception.Message);
        Assert.DoesNotContain("22000", exception.Message);
        Assert.DoesNotContain("5713", exception.Message);
    }

    #endregion

    #region StartStreamingAsync reads the device's verdict on the start

    [Fact]
    public async Task StartStreamingAsync_RefusedStart_ReadsTheErrorQueueAndThrows()
    {
        var device = CreateTransportDevice(currentMaximumRateHz: null);
        device.QueuedErrors.Add("-222,\"Data out of range\"");
        device.StreamingFrequency = 1000;

        var exception = await Assert.ThrowsAsync<DeviceCommandFailedException>(
            () => device.StartStreamingAsync());

        Assert.Equal(-222, exception.ErrorCode);
        Assert.Equal("-222,\"Data out of range\"", exception.DeviceResponse);
        Assert.Contains("StartStreamData", exception.Command);

        // The queue was read, so the entry is no longer on the device to break the next connect
        // (#898) — and the session is not left claiming to stream.
        Assert.Equal(1, device.ErrorQueueReads);
        Assert.False(device.IsStreaming);
    }

    [Fact]
    public async Task StartStreamingAsync_DeviceSendsAFrame_NeverReadsTheErrorQueue()
    {
        // The queue read is a text exchange, which takes the protobuf consumer off the wire. A
        // stream that is delivering must never pay for it.
        var device = CreateTransportDevice(currentMaximumRateHz: null);
        device.QueuedErrors.Add("-222,\"Data out of range\"");
        device.StreamingFrequency = 1000;
        device.SendFrameOnStart = true;

        await device.StartStreamingAsync();

        Assert.True(device.IsStreaming);
        Assert.Equal(0, device.ErrorQueueReads);
    }

    [Fact]
    public async Task StartStreamingAsync_SilentStartWithACleanQueue_IsNotTreatedAsAFailure()
    {
        // A device with no analog channels enabled does not always start a stream and does not
        // always say why. Nothing readable is not evidence of a refusal.
        var device = CreateTransportDevice(currentMaximumRateHz: null);
        device.StreamingFrequency = 1000;

        await device.StartStreamingAsync();

        Assert.True(device.IsStreaming);
        Assert.Equal(1, device.ErrorQueueReads);
    }

    [Fact]
    public async Task StartStreamingAsync_RereadsTheCapabilityDocumentBeforeApplyingTheCap()
    {
        // The device computes its cap for the set enabled when the document was read, so the
        // figure from connect time describes a channel set that is no longer there.
        var device = CreateTransportDevice(currentMaximumRateHz: null);
        device.StreamingFrequency = 10000;
        device.CapabilityDocumentOnRead = BenchDocument(TwoChannelCapHz);
        device.SendFrameOnStart = true;

        await device.StartStreamingAsync();

        Assert.Equal(1, device.CapabilityDocumentReads);
        Assert.Equal(TwoChannelCapHz, device.StreamingFrequency);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// A connected NQ1 with sixteen analog and sixteen digital channels populated, the board
    /// ceiling at 22000 Hz, and the capability document stating
    /// <paramref name="currentMaximumRateHz"/> as the cap for the enabled set.
    /// </summary>
    private static CapturingStreamingDevice CreateDevice(int? currentMaximumRateHz)
    {
        var device = new CapturingStreamingDevice("BenchNq1");
        PrepareDevice(device, currentMaximumRateHz);
        device.Connect();
        device.SentMessages.Clear();
        return device;
    }

    /// <summary>
    /// The same device over a transport, which is what lets the start confirmation run: without
    /// one there is no error queue to read.
    /// </summary>
    private static ConfirmingStreamingDevice CreateTransportDevice(int? currentMaximumRateHz)
    {
        var device = new ConfirmingStreamingDevice("BenchNq1", new SinkTransport());
        PrepareDevice(device, currentMaximumRateHz);
        device.Connect();
        device.SentMessages.Clear();
        return device;
    }

    private static void PrepareDevice(DaqifiStreamingDevice device, int? currentMaximumRateHz)
    {
        device.Metadata.DeviceType = DeviceType.Nyquist1;
        device.Metadata.Capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
        device.Metadata.Capabilities.MaxSamplingRate = BoardCeilingHz;
        device.Metadata.FirmwareVersion = "3.8.0";
        device.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = 16,
            AnalogInRes = 65535,
            DigitalPortNum = 16
        });

        if (currentMaximumRateHz.HasValue)
        {
            device.Metadata.ApplyCapabilityDocument(BenchDocument(currentMaximumRateHz.Value));
        }
    }

    /// <summary>
    /// The bench NQ1's capability document, reduced to what the cap is computed from: the
    /// device's own answer for the enabled set.
    /// </summary>
    private static CapabilityDocument BenchDocument(int currentMaximumRateHz)
    {
        var channels = new List<CapabilityChannel>();
        for (var id = 0; id < 16; id++)
        {
            channels.Add(new CapabilityChannel { Id = id, Kind = CapabilityChannelKind.AnalogInput });
        }

        return new CapabilityDocument
        {
            SchemaVersion = 2,
            Channels = channels,
            Streaming = new CapabilityStreaming
            {
                MinimumSampleRateHz = 1,
                MaximumSampleRateHz = BoardCeilingHz,
                CurrentMaximumRateHz = currentMaximumRateHz,
                RateValidation = "error"
            }
        };
    }

    /// <summary>A device that records what was sent instead of writing to hardware.</summary>
    private class CapturingStreamingDevice : DaqifiStreamingDevice
    {
        public CapturingStreamingDevice(string name, IPAddress? ipAddress = null)
            : base(name, ipAddress)
        {
        }

        public CapturingStreamingDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        public List<IOutboundMessage<string>> SentMessages { get; } = new();

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> stringMessage)
            {
                SentMessages.Add(stringMessage);
            }
        }
    }

    /// <summary>
    /// Adds the two device exchanges the start confirmation depends on, scripted: the capability
    /// re-read and the error-queue read. Both are <c>virtual</c> on the device precisely so a
    /// stand-in can answer them.
    /// </summary>
    private sealed class ConfirmingStreamingDevice : CapturingStreamingDevice
    {
        public ConfirmingStreamingDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        /// <summary>What the scripted error queue holds. Handed over once and then empty.</summary>
        public List<string> QueuedErrors { get; } = new();

        public int ErrorQueueReads { get; private set; }

        public int CapabilityDocumentReads { get; private set; }

        /// <summary>The document the scripted capability read applies, if any.</summary>
        public CapabilityDocument? CapabilityDocumentOnRead { get; set; }

        /// <summary>
        /// Whether the device answers the start command with a stream frame, as a device that
        /// accepted the start does.
        /// </summary>
        public bool SendFrameOnStart { get; set; }

        public override void Send<T>(IOutboundMessage<T> message)
        {
            base.Send(message);

            if (SendFrameOnStart
                && message is IOutboundMessage<string> stringMessage
                && stringMessage.Data.Contains("StartStreamData", StringComparison.Ordinal))
            {
                OnStreamMessageReceived(new DaqifiOutMessage { MsgTimeStamp = 1000 });
            }
        }

        public override Task<IReadOnlyList<string>> DrainErrorQueueAsync(
            int maxIterations = 256,
            CancellationToken cancellationToken = default)
        {
            ErrorQueueReads++;
            var popped = QueuedErrors.ToList();
            QueuedErrors.Clear();
            return Task.FromResult<IReadOnlyList<string>>(popped);
        }

        public override Task<CapabilityDocument?> ReadCapabilityDocumentAsync(
            CancellationToken cancellationToken = default)
        {
            CapabilityDocumentReads++;

            if (CapabilityDocumentOnRead != null)
            {
                Metadata.ApplyCapabilityDocument(CapabilityDocumentOnRead);
            }

            return Task.FromResult(CapabilityDocumentOnRead);
        }
    }

    /// <summary>A transport over a stream nothing ever arrives on.</summary>
    private sealed class SinkTransport : IStreamTransport
    {
        private readonly NullSinkStream _stream = new();
        private bool _isConnected;
        private bool _disposed;

        public Stream Stream => _disposed
            ? throw new ObjectDisposedException(nameof(SinkTransport))
            : _stream;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Sink: Connected" : "Sink: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SinkTransport));
            }

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

        public void Connect() => ConnectAsync().Wait();

        public void Disconnect() => DisconnectAsync().Wait();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _isConnected = false;
            _disposed = true;
            _stream.Dispose();
        }
    }

    private sealed class NullSinkStream : Stream
    {
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            // Nothing ever arrives; back off so the consumer's reader loop does not spin a core.
            Thread.Sleep(5);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }

    #endregion
}
