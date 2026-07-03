using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Google.Protobuf;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Unit tests for <see cref="DaqifiStreamingDevice"/>'s decoded per-frame sample pipeline:
/// stream frames are decoded into per-channel samples that drive <see cref="IChannel.SampleReceived"/>.
/// </summary>
public class DaqifiStreamingDeviceDecodeTests
{
    #region Analog decoding

    [Fact]
    public void Decode_UsbFloatPath_UsesFloatsDirectlyWithNoRawValue()
    {
        // Arrange: 3 analog channels, enable AI0 and AI2 (leaving a gap at AI1).
        var device = CreateStreamingDevice(analogCount: 3);
        var ai0 = AnalogChannel(device, 0);
        var ai2 = AnalogChannel(device, 2);
        ai0.IsEnabled = true;
        ai2.IsEnabled = true;
        device.StartStreaming();

        var frame = new DaqifiOutMessage { MsgTimeStamp = 4242 };
        frame.AnalogInDataFloat.Add(1.5f);
        frame.AnalogInDataFloat.Add(2.5f);

        // Act
        device.InvokeStreamMessage(frame);

        // Assert: values map to enabled channels in ascending channel-number order.
        Assert.NotNull(ai0.ActiveSample);
        Assert.Equal(1.5, ai0.ActiveSample!.Value);
        Assert.Null(ai0.ActiveSample.RawValue); // pre-scaled float => no raw ADC count
        Assert.Equal(4242u, ai0.ActiveSample.DeviceTimestamp);

        Assert.NotNull(ai2.ActiveSample);
        Assert.Equal(2.5, ai2.ActiveSample!.Value);
        Assert.Null(ai2.ActiveSample.RawValue);

        // The disabled channel between them received nothing.
        Assert.Null(AnalogChannel(device, 1).ActiveSample);
    }

    [Fact]
    public void Decode_WifiRawPath_AppliesChannelCalibrationAndPreservesRawCount()
    {
        // Arrange: give the channels a non-identity port range so scaling is observable.
        var device = CreateStreamingDevice(analogCount: 2, portRange: 10.0f, resolution: 65535);
        var ai0 = AnalogChannel(device, 0);
        var ai1 = AnalogChannel(device, 1);
        ai0.IsEnabled = true;
        ai1.IsEnabled = true;
        device.StartStreaming();

        var frame = new DaqifiOutMessage { MsgTimeStamp = 7 };
        frame.AnalogInData.Add(1000);
        frame.AnalogInData.Add(2000);

        // Act
        device.InvokeStreamMessage(frame);

        // Assert: decode applied the channel's own calibration and preserved the raw count.
        Assert.NotNull(ai0.ActiveSample);
        Assert.Equal(ai0.GetScaledValue(1000), ai0.ActiveSample!.Value);
        Assert.Equal(1000, ai0.ActiveSample.RawValue);
        Assert.NotEqual(1000.0, ai0.ActiveSample.Value); // scaling actually happened

        Assert.NotNull(ai1.ActiveSample);
        Assert.Equal(ai1.GetScaledValue(2000), ai1.ActiveSample!.Value);
        Assert.Equal(2000, ai1.ActiveSample.RawValue);
    }

    [Fact]
    public void Decode_MapsValuesByChannelNumberNotEnableOrder()
    {
        // Enable the higher-numbered channel "first" to prove ordering is by channel number.
        var device = CreateStreamingDevice(analogCount: 3);
        var ai2 = AnalogChannel(device, 2);
        var ai0 = AnalogChannel(device, 0);
        ai2.IsEnabled = true;
        ai0.IsEnabled = true;
        device.StartStreaming();

        var frame = new DaqifiOutMessage { MsgTimeStamp = 1 };
        frame.AnalogInDataFloat.Add(10f); // first value -> lowest channel number (AI0)
        frame.AnalogInDataFloat.Add(20f); // second value -> AI2

        device.InvokeStreamMessage(frame);

        Assert.Equal(10.0, ai0.ActiveSample!.Value);
        Assert.Equal(20.0, ai2.ActiveSample!.Value);
    }

    [Fact]
    public void Decode_RaisesSampleReceivedWithChannelReference()
    {
        var device = CreateStreamingDevice(analogCount: 1);
        var ai0 = AnalogChannel(device, 0);
        ai0.IsEnabled = true;
        device.StartStreaming();

        SampleReceivedEventArgs? captured = null;
        ai0.SampleReceived += (_, e) => captured = e;

        var frame = new DaqifiOutMessage { MsgTimeStamp = 99 };
        frame.AnalogInDataFloat.Add(3.14f);

        device.InvokeStreamMessage(frame);

        Assert.NotNull(captured);
        Assert.Same(ai0, captured!.Channel);
        Assert.Equal(3.14, captured.Sample.Value, 5);
        Assert.Equal(99u, captured.Sample.DeviceTimestamp);
    }

    #endregion

    #region Digital decoding

    [Fact]
    public void Decode_Digital_UnpacksBitsPerChannel()
    {
        var device = CreateStreamingDevice(analogCount: 0, digitalCount: 4);
        var dio = Enumerable.Range(0, 4).Select(n => DigitalChannel(device, n)).ToList();
        foreach (var d in dio) d.IsEnabled = true;
        device.StartStreaming();

        // 0b1010 => DIO0=low, DIO1=high, DIO2=low, DIO3=high
        var frame = new DaqifiOutMessage { MsgTimeStamp = 5 };
        frame.DigitalData = ByteString.CopyFrom(new byte[] { 0b1010 });

        device.InvokeStreamMessage(frame);

        Assert.Equal(0.0, dio[0].ActiveSample!.Value);
        Assert.Equal(1.0, dio[1].ActiveSample!.Value);
        Assert.Equal(0.0, dio[2].ActiveSample!.Value);
        Assert.Equal(1.0, dio[3].ActiveSample!.Value);
        Assert.Equal(1, dio[1].ActiveSample!.RawValue);
        Assert.Equal(5u, dio[1].ActiveSample!.DeviceTimestamp);
    }

    [Fact]
    public void Decode_Digital_SkipsOutputDirectionChannels()
    {
        var device = CreateStreamingDevice(analogCount: 0, digitalCount: 2);
        var dio0 = DigitalChannel(device, 0);
        var dio1 = DigitalChannel(device, 1);
        dio0.IsEnabled = true;
        dio1.IsEnabled = true;
        dio1.Direction = ChannelDirection.Output; // output channels are not sampled
        device.StartStreaming();

        var frame = new DaqifiOutMessage { MsgTimeStamp = 5 };
        frame.DigitalData = ByteString.CopyFrom(new byte[] { 0b11 });

        device.InvokeStreamMessage(frame);

        Assert.NotNull(dio0.ActiveSample);
        Assert.Equal(1.0, dio0.ActiveSample!.Value);
        Assert.Null(dio1.ActiveSample); // output channel skipped
    }

    [Fact]
    public void Decode_Digital_BeyondTwoBytes_ReadsCorrectByteWithoutWrapping()
    {
        // Regression for Qodo #279: with >16 enabled digital channels / >2 payload bytes, bit
        // position i must map to byte i/8, bit i%8 — not wrap byte 1 for i>=16.
        var device = CreateStreamingDevice(analogCount: 0, digitalCount: 17);
        var dio = Enumerable.Range(0, 17).Select(n => DigitalChannel(device, n)).ToList();
        foreach (var d in dio) d.IsEnabled = true;
        device.StartStreaming();

        // Only channel index 16 high: byte 2 bit 0. A wrapping decoder would read byte 1 bit 0 (low).
        var frame = new DaqifiOutMessage { MsgTimeStamp = 5 };
        frame.DigitalData = ByteString.CopyFrom(new byte[] { 0x00, 0x00, 0b0000_0001 });

        device.InvokeStreamMessage(frame);

        Assert.Equal(1.0, dio[16].ActiveSample!.Value);
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(0.0, dio[i].ActiveSample!.Value);
        }
    }

    [Fact]
    public void Decode_Digital_MoreChannelsThanPayloadBits_StopsInsteadOfForcingLow()
    {
        // With a single payload byte (8 bits) but more enabled channels, channels past the
        // payload get no sample rather than a bogus "low" reading.
        var device = CreateStreamingDevice(analogCount: 0, digitalCount: 10);
        var dio = Enumerable.Range(0, 10).Select(n => DigitalChannel(device, n)).ToList();
        foreach (var d in dio) d.IsEnabled = true;
        device.StartStreaming();

        var frame = new DaqifiOutMessage { MsgTimeStamp = 5 };
        frame.DigitalData = ByteString.CopyFrom(new byte[] { 0xFF }); // 8 bits, channels 0-7

        device.InvokeStreamMessage(frame);

        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(1.0, dio[i].ActiveSample!.Value);
        }
        Assert.Null(dio[8].ActiveSample);
        Assert.Null(dio[9].ActiveSample);
    }

    #endregion

    #region Gating and resilience

    [Fact]
    public void Decode_WhenNotStreaming_DoesNotProduceSamples()
    {
        var device = CreateStreamingDevice(analogCount: 1);
        var ai0 = AnalogChannel(device, 0);
        ai0.IsEnabled = true;
        // Note: StartStreaming intentionally NOT called.

        var raised = false;
        ai0.SampleReceived += (_, _) => raised = true;

        var frame = new DaqifiOutMessage { MsgTimeStamp = 1 };
        frame.AnalogInDataFloat.Add(1f);

        device.InvokeStreamMessage(frame);

        Assert.Null(ai0.ActiveSample);
        Assert.False(raised);
    }

    [Fact]
    public void Decode_StillReRaisesRawMessageReceived()
    {
        // Existing consumers that hand-demux the raw frame must keep working.
        var device = CreateStreamingDevice(analogCount: 1);
        AnalogChannel(device, 0).IsEnabled = true;
        device.StartStreaming();

        MessageReceivedEventArgs? raw = null;
        device.MessageReceived += (_, e) => raw = e;

        var frame = new DaqifiOutMessage { MsgTimeStamp = 1 };
        frame.AnalogInDataFloat.Add(1f);

        device.InvokeStreamMessage(frame);

        Assert.NotNull(raw);
    }

    [Fact]
    public void Decode_MoreValuesThanChannels_MapsAvailableWithoutThrowing()
    {
        var device = CreateStreamingDevice(analogCount: 1);
        var ai0 = AnalogChannel(device, 0);
        ai0.IsEnabled = true;
        device.StartStreaming();

        var frame = new DaqifiOutMessage { MsgTimeStamp = 1 };
        frame.AnalogInDataFloat.Add(1f);
        frame.AnalogInDataFloat.Add(2f); // extra value with no channel to receive it

        var ex = Record.Exception(() => device.InvokeStreamMessage(frame));

        Assert.Null(ex);
        Assert.Equal(1.0, ai0.ActiveSample!.Value);
    }

    [Fact]
    public void Decode_FewerValuesThanChannels_MapsAvailableWithoutThrowing()
    {
        var device = CreateStreamingDevice(analogCount: 2);
        var ai0 = AnalogChannel(device, 0);
        var ai1 = AnalogChannel(device, 1);
        ai0.IsEnabled = true;
        ai1.IsEnabled = true;
        device.StartStreaming();

        var frame = new DaqifiOutMessage { MsgTimeStamp = 1 };
        frame.AnalogInDataFloat.Add(1f); // only one value for two enabled channels

        var ex = Record.Exception(() => device.InvokeStreamMessage(frame));

        Assert.Null(ex);
        Assert.Equal(1.0, ai0.ActiveSample!.Value);
        Assert.Null(ai1.ActiveSample);
    }

    [Fact]
    public void Decode_CarriesDeviceTimestampVerbatimAcrossFrames()
    {
        var device = CreateStreamingDevice(analogCount: 1);
        var ai0 = AnalogChannel(device, 0);
        ai0.IsEnabled = true;
        device.StartStreaming();

        var first = new DaqifiOutMessage { MsgTimeStamp = 1000 };
        first.AnalogInDataFloat.Add(1f);
        device.InvokeStreamMessage(first);
        var firstHost = ai0.ActiveSample!.Timestamp;
        Assert.Equal(1000u, ai0.ActiveSample!.DeviceTimestamp);

        var second = new DaqifiOutMessage { MsgTimeStamp = 2000 };
        second.AnalogInDataFloat.Add(2f);
        device.InvokeStreamMessage(second);
        Assert.Equal(2000u, ai0.ActiveSample!.DeviceTimestamp);

        // Host timestamp advances monotonically as device ticks increase.
        Assert.True(ai0.ActiveSample!.Timestamp >= firstHost);
    }

    #endregion

    #region Helpers

    private static DecodableStreamingDevice CreateStreamingDevice(
        int analogCount,
        int digitalCount = 0,
        float? portRange = null,
        uint resolution = 65535)
    {
        var device = new DecodableStreamingDevice("TestDevice");
        device.Connect();

        var status = new DaqifiOutMessage
        {
            AnalogInPortNum = (uint)analogCount,
            DigitalPortNum = (uint)digitalCount,
            AnalogInRes = resolution,
        };

        for (var i = 0; i < analogCount; i++)
        {
            status.AnalogInPortRange.Add(portRange ?? 1.0f);
        }

        device.PopulateChannelsFromStatus(status);
        return device;
    }

    private static IAnalogChannel AnalogChannel(DaqifiStreamingDevice device, int number) =>
        (IAnalogChannel)device.Channels.First(c => c.Type == ChannelType.Analog && c.ChannelNumber == number);

    private static IChannel DigitalChannel(DaqifiStreamingDevice device, int number) =>
        device.Channels.First(c => c.Type == ChannelType.Digital && c.ChannelNumber == number);

    /// <summary>
    /// A <see cref="DaqifiStreamingDevice"/> that captures sent SCPI commands (so streaming
    /// setup does not require a real transport) and exposes the protected stream handler so a
    /// frame can be injected directly.
    /// </summary>
    private sealed class DecodableStreamingDevice : DaqifiStreamingDevice
    {
        public DecodableStreamingDevice(string name, IPAddress? ipAddress = null) : base(name, ipAddress)
        {
        }

        public List<IOutboundMessage<string>> SentMessages { get; } = new();

        public void InvokeStreamMessage(DaqifiOutMessage message) => OnStreamMessageReceived(message);

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> stringMessage)
            {
                SentMessages.Add(stringMessage);
            }
        }
    }

    #endregion
}
