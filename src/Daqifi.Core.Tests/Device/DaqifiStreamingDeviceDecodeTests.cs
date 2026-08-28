using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Google.Protobuf;
using System;
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
    #region Gap detection

    [Fact]
    public void GapDetected_FiresOnceOnDeviceClockGap_AfterSteadyCadence()
    {
        var device = CreateStreamingDevice(analogCount: 1);
        AnalogChannel(device, 0).IsEnabled = true;
        device.StartStreaming();

        var events = new List<TimestampGapEventArgs>();
        device.GapDetected += (_, e) => events.Add(e);

        // First frame (no prior reference) + steady 1000-tick cadence to seed the EMA.
        for (uint ts = 1000; ts <= 11000; ts += 1000)
        {
            device.InvokeStreamMessage(AnalogFrame(ts, 1.0f));
        }
        Assert.Empty(events); // steady cadence -> no gap

        // A 5x jump in the device clock = dropped samples.
        device.InvokeStreamMessage(AnalogFrame(16000, 1.0f));

        Assert.Single(events);
        Assert.Equal(16000u, events[0].DeviceTimestamp);
        Assert.True(events[0].SecondsSincePreviousMessage > 0);
    }

    [Fact]
    public void GapDetected_DoesNotFireOnSteadyCadence()
    {
        var device = CreateStreamingDevice(analogCount: 1);
        AnalogChannel(device, 0).IsEnabled = true;
        device.StartStreaming();

        var fired = false;
        device.GapDetected += (_, _) => fired = true;

        for (uint ts = 1000; ts <= 50000; ts += 1000)
        {
            device.InvokeStreamMessage(AnalogFrame(ts, 1.0f));
        }

        Assert.False(fired);
    }

    [Fact]
    public void GapDetected_ResetsBetweenSessions_NoFalseGapOnDifferentCadence()
    {
        var device = CreateStreamingDevice(analogCount: 1);
        AnalogChannel(device, 0).IsEnabled = true;

        // Session 1: fast cadence (1000-tick deltas) trains the EMA.
        device.StartStreaming();
        for (uint ts = 1000; ts <= 11000; ts += 1000)
        {
            device.InvokeStreamMessage(AnalogFrame(ts, 1.0f));
        }
        device.StopStreaming();

        var events = new List<TimestampGapEventArgs>();
        device.GapDetected += (_, e) => events.Add(e);

        // Session 2: slower cadence (3000-tick deltas). Were the EMA not reset at StartStreaming,
        // the first real delta (3000) would exceed 2x the stale 1000 average and false-trip.
        device.StartStreaming();
        device.InvokeStreamMessage(AnalogFrame(100000, 1.0f)); // first frame — no reference
        device.InvokeStreamMessage(AnalogFrame(103000, 1.0f)); // +3000 re-seeds the EMA
        device.InvokeStreamMessage(AnalogFrame(106000, 1.0f)); // +3000 steady

        Assert.Empty(events);
    }

    [Fact]
    public void GapDetected_ThrowingSubscriber_DoesNotSkipFrameDecode()
    {
        var device = CreateStreamingDevice(analogCount: 1);
        var ai0 = AnalogChannel(device, 0);
        ai0.IsEnabled = true;
        device.StartStreaming();

        device.GapDetected += (_, _) => throw new InvalidOperationException("boom");

        // Steady cadence, then a gap frame that also carries a decodable sample. The gap fires the
        // throwing subscriber — decode of this frame must still happen.
        for (uint ts = 1000; ts <= 11000; ts += 1000)
        {
            device.InvokeStreamMessage(AnalogFrame(ts, 1.0f));
        }
        device.InvokeStreamMessage(AnalogFrame(20000, 7.5f)); // 9x jump -> gap -> throwing handler

        Assert.NotNull(ai0.ActiveSample);
        Assert.Equal(7.5, ai0.ActiveSample!.Value);
        Assert.Equal(20000u, ai0.ActiveSample.DeviceTimestamp);
    }

    #endregion

    #region Analog decoding

    [Fact]
    public void Decode_PreScaledFloatFrame_UsesFloatsDirectlyWithNoRawValue()
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
    public void Decode_RawCountFrame_AppliesChannelCalibrationAndPreservesRawCount()
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
    public void Decode_Digital_MapsBitsByChannelNumberNotEnablePosition()
    {
        // The firmware streams the whole DIO port (the wire-level enable is global), so an
        // enabled channel reads the bit at its channel number. Enable only DIO 5: a decoder
        // that densely packed enabled channels would wrongly read bit 0.
        var device = CreateStreamingDevice(analogCount: 0, digitalCount: 8);
        var dio5 = DigitalChannel(device, 5);
        dio5.IsEnabled = true;
        device.StartStreaming();

        var frame = new DaqifiOutMessage { MsgTimeStamp = 5 };
        frame.DigitalData = ByteString.CopyFrom(new byte[] { 0b0010_0000 }); // only bit 5 high

        device.InvokeStreamMessage(frame);

        Assert.Equal(1.0, dio5.ActiveSample!.Value);
    }

    [Fact]
    public void Decode_Digital_SubsetOfChannelsEnabled_EachReadsItsOwnBit()
    {
        // Loopback-style scenario: only DIO 3 and DIO 5 enabled, with other port bits set as
        // noise. Positional decoding would read bits 0 and 1 (both high) for these channels.
        var device = CreateStreamingDevice(analogCount: 0, digitalCount: 16);
        var dio3 = DigitalChannel(device, 3);
        var dio5 = DigitalChannel(device, 5);
        dio3.IsEnabled = true;
        dio5.IsEnabled = true;
        device.StartStreaming();

        var frame = new DaqifiOutMessage { MsgTimeStamp = 5 };
        frame.DigitalData = ByteString.CopyFrom(new byte[] { 0b0010_0011, 0xFF }); // bit 3 low, bit 5 high

        device.InvokeStreamMessage(frame);

        Assert.Equal(0.0, dio3.ActiveSample!.Value);
        Assert.Equal(1.0, dio5.ActiveSample!.Value);
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
    public void Decode_Digital_SkipsOutputDirectionChannels_TheDeviceItselfReported()
    {
        // Regression for #685. A pin left as an output by a previous session stays an output on
        // the board across a reconnect, and nothing in Core's init sequence resets it. Before the
        // fix Core hardcoded every freshly populated channel to Input, so the guard above never
        // fired on a reconnect and the pin's own driven level was delivered as an input reading.
        // Nobody calls SetDioDirection here — the direction comes only from the status frame.
        var device = new DecodableStreamingDevice("TestDevice");
        device.Connect();

        var status = new DaqifiOutMessage { DigitalPortNum = 2 };
        status.DigitalPortDir = ByteString.CopyFrom(new byte[] { 0b1111_1101 }); // TRIS: DIO1 is an output
        device.PopulateChannelsFromStatus(status);

        var dio0 = DigitalChannel(device, 0);
        var dio1 = DigitalChannel(device, 1);
        dio0.IsEnabled = true;
        dio1.IsEnabled = true;
        device.StartStreaming();

        var frame = new DaqifiOutMessage { MsgTimeStamp = 5 };
        frame.DigitalData = ByteString.CopyFrom(new byte[] { 0b11 });

        device.InvokeStreamMessage(frame);

        Assert.Equal(ChannelDirection.Output, dio1.Direction);
        Assert.NotNull(dio0.ActiveSample);
        Assert.Equal(1.0, dio0.ActiveSample!.Value);
        Assert.Null(dio1.ActiveSample);
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
    public void Decode_RaisesClassifiedStreamMessageReceived()
    {
        // Classified event should fire in addition to the undifferentiated MessageReceived.
        var device = CreateStreamingDevice(analogCount: 1);
        AnalogChannel(device, 0).IsEnabled = true;
        device.StartStreaming();

        DaqifiOutMessage? classified = null;
        device.StreamMessageReceived += m => classified = m;

        var frame = new DaqifiOutMessage { MsgTimeStamp = 1 };
        frame.AnalogInDataFloat.Add(1f);

        device.InvokeStreamMessage(frame);

        Assert.Same(frame, classified);
    }

    [Fact]
    public void Decode_SubscriberExceptionInStreamMessageReceived_StillDecodesSample()
    {
        // A misbehaving StreamMessageReceived subscriber runs inside the base
        // OnStreamMessageReceived call, before DecodeStreamFrame — it must not prevent
        // the sample decode below from running.
        var device = CreateStreamingDevice(analogCount: 1);
        var ai0 = AnalogChannel(device, 0);
        ai0.IsEnabled = true;
        device.StartStreaming();

        device.StreamMessageReceived += _ => throw new InvalidOperationException("boom");

        var frame = new DaqifiOutMessage { MsgTimeStamp = 1 };
        frame.AnalogInDataFloat.Add(1f);

        var ex = Record.Exception(() => device.InvokeStreamMessage(frame));

        Assert.Null(ex);
        Assert.Equal(1.0, ai0.ActiveSample!.Value);
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
    public void Decode_MidStreamFewerValuesThanChannels_MapsAvailableWithoutThrowing()
    {
        // The warmup guard (issue #351) only suppresses *leading* short frames. Once a full frame
        // has been seen, a later short frame is still best-effort mapped rather than dropped.
        var device = CreateStreamingDevice(analogCount: 2);
        var ai0 = AnalogChannel(device, 0);
        var ai1 = AnalogChannel(device, 1);
        ai0.IsEnabled = true;
        ai1.IsEnabled = true;
        device.StartStreaming();

        // First a full frame to clear the warmup guard.
        var full = new DaqifiOutMessage { MsgTimeStamp = 1 };
        full.AnalogInDataFloat.Add(9f);
        full.AnalogInDataFloat.Add(9f);
        device.InvokeStreamMessage(full);

        // Then a mid-stream short frame: one value for two enabled channels.
        var frame = new DaqifiOutMessage { MsgTimeStamp = 2 };
        frame.AnalogInDataFloat.Add(1f);

        var ex = Record.Exception(() => device.InvokeStreamMessage(frame));

        Assert.Null(ex);
        Assert.Equal(1.0, ai0.ActiveSample!.Value);
        Assert.Equal(9.0, ai1.ActiveSample!.Value); // retains its last (full-frame) value
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

    #region Warmup-frame suppression (issue #351)

    [Fact]
    public void Decode_SuppressesMalformedFirstFrame_ThenEmitsFullFrame()
    {
        // Reproduces the bench evidence: 2 enabled analog channels, first frame carries a single
        // analog value (a firmware warmup frame). That partial first sample must not reach the
        // channels; the next full frame must decode normally.
        var device = CreateStreamingDevice(analogCount: 2);
        var ai0 = AnalogChannel(device, 0);
        var ai1 = AnalogChannel(device, 1);
        ai0.IsEnabled = true;
        ai1.IsEnabled = true;
        device.StartStreaming();

        var samples = new List<double>();
        ai0.SampleReceived += (_, e) => samples.Add(e.Sample.Value);

        // Malformed first frame: one value for two enabled channels.
        var warmup = new DaqifiOutMessage { MsgTimeStamp = 1000 };
        warmup.AnalogInDataFloat.Add(0.1f);
        device.InvokeStreamMessage(warmup);

        Assert.Null(ai0.ActiveSample); // warmup frame suppressed
        Assert.Null(ai1.ActiveSample);
        Assert.Empty(samples);

        // Next full frame decodes for both channels.
        var full = new DaqifiOutMessage { MsgTimeStamp = 1840 };
        full.AnalogInDataFloat.Add(4f);
        full.AnalogInDataFloat.Add(8f);
        device.InvokeStreamMessage(full);

        Assert.Equal(4.0, ai0.ActiveSample!.Value);
        Assert.Equal(8.0, ai1.ActiveSample!.Value);
        Assert.Equal(new[] { 4.0 }, samples); // AI0 saw exactly one (correct) sample
    }

    [Fact]
    public void Decode_WarmupFrameThenSteadyCadence_NoFalseGap()
    {
        // The warmup frame's timestamp is normal (one sample period before the next frame), so it
        // anchors the session clock correctly — a steady cadence after it reports no false gap.
        var device = CreateStreamingDevice(analogCount: 2);
        AnalogChannel(device, 0).IsEnabled = true;
        AnalogChannel(device, 1).IsEnabled = true;
        device.StartStreaming();

        var gaps = new List<TimestampGapEventArgs>();
        device.GapDetected += (_, e) => gaps.Add(e);

        // Warmup frame (partial analog), then a steady one-period cadence.
        var warmup = new DaqifiOutMessage { MsgTimeStamp = 1000 };
        warmup.AnalogInDataFloat.Add(0.1f);
        device.InvokeStreamMessage(warmup);

        for (uint ts = 2000; ts <= 12000; ts += 1000)
        {
            var frame = new DaqifiOutMessage { MsgTimeStamp = ts };
            frame.AnalogInDataFloat.Add(1f);
            frame.AnalogInDataFloat.Add(2f);
            device.InvokeStreamMessage(frame);
        }

        Assert.Empty(gaps);
    }

    [Fact]
    public void Decode_CombinedWarmupFrame_SuppressesAnalogButKeepsDigital()
    {
        // The firmware's fast encoder packs analog+digital into one frame, so the warmup frame
        // carries a valid digital payload alongside its partial analog values (issue #351 evidence:
        // "analog=[1] digital=00-04"). Only the malformed analog is dropped; digital is preserved.
        var device = CreateStreamingDevice(analogCount: 2, digitalCount: 2);
        var ai0 = AnalogChannel(device, 0);
        var ai1 = AnalogChannel(device, 1);
        var dio0 = DigitalChannel(device, 0);
        var dio1 = DigitalChannel(device, 1);
        ai0.IsEnabled = true;
        ai1.IsEnabled = true;
        dio0.IsEnabled = true;
        dio1.IsEnabled = true;
        device.StartStreaming();

        var warmup = new DaqifiOutMessage { MsgTimeStamp = 1000 };
        warmup.AnalogInDataFloat.Add(0.1f); // partial analog: 1 value for 2 enabled channels
        warmup.DigitalData = ByteString.CopyFrom(new byte[] { 0b10 }); // DIO0 low, DIO1 high

        device.InvokeStreamMessage(warmup);

        // Analog values suppressed...
        Assert.Null(ai0.ActiveSample);
        Assert.Null(ai1.ActiveSample);
        // ...but the digital payload in the same frame is still decoded.
        Assert.Equal(0.0, dio0.ActiveSample!.Value);
        Assert.Equal(1.0, dio1.ActiveSample!.Value);
    }

    [Fact]
    public void Decode_WarmupFrame_NotHandedToRawFrameConsumers()
    {
        // Issue #425: the malformed frame must not reach raw-frame consumers either. They read
        // AnalogInDataFloat straight off the frame, so a partial payload is exactly as harmful
        // there as in the decoded path — the example CLI's offline export inferred a channel count
        // of one from it and truncated every sample that followed.
        var device = CreateStreamingDevice(analogCount: 2);
        AnalogChannel(device, 0).IsEnabled = true;
        AnalogChannel(device, 1).IsEnabled = true;
        device.StartStreaming();

        var rawFrames = new List<DaqifiOutMessage>();
        device.MessageReceived += (_, e) =>
        {
            if (e.Message.Data is DaqifiOutMessage frame) rawFrames.Add(frame);
        };
        var classified = 0;
        device.StreamMessageReceived += _ => classified++;

        var warmup = new DaqifiOutMessage { MsgTimeStamp = 1 };
        warmup.AnalogInDataFloat.Add(0.1f);
        device.InvokeStreamMessage(warmup);

        Assert.Empty(rawFrames);
        Assert.Equal(0, classified);

        // The next full frame reaches raw consumers untouched.
        var full = new DaqifiOutMessage { MsgTimeStamp = 2 };
        full.AnalogInDataFloat.Add(1f);
        full.AnalogInDataFloat.Add(2f);
        device.InvokeStreamMessage(full);

        Assert.Equal(new[] { 1f, 2f }, Assert.Single(rawFrames).AnalogInDataFloat);
        Assert.Equal(1, classified);
    }

    [Fact]
    public void Decode_WarmupFrame_ReportsDiscardWithCounts()
    {
        // A suppressed frame must be observable: a consumer counting samples has to be able to
        // tell "Core dropped a malformed frame" from "the device sent nothing".
        var device = CreateStreamingDevice(analogCount: 4);
        for (var n = 0; n < 4; n++) AnalogChannel(device, n).IsEnabled = true;
        device.StartStreaming();

        var discards = new List<StreamFrameDiscardedEventArgs>();
        device.StreamFrameDiscarded += (_, e) => discards.Add(e);

        var warmup = new DaqifiOutMessage { MsgTimeStamp = 1836224389 };
        warmup.AnalogInDataFloat.Add(2f); // the bench evidence: 1 value for 4 enabled channels
        device.InvokeStreamMessage(warmup);

        var discarded = Assert.Single(discards);
        Assert.Equal(StreamFrameDiscardReason.PartialAnalogFrame, discarded.Reason);
        Assert.Equal(1836224389u, discarded.DeviceTimestamp);
        Assert.Equal(1, discarded.AnalogValueCount);
        Assert.Equal(4, discarded.EnabledAnalogChannelCount);
        Assert.Equal(1, device.DiscardedStreamFrameCount);
    }

    [Fact]
    public void Decode_ThrowingDiscardSubscriber_DoesNotBreakTheStream()
    {
        // The catch around the discard event exists to contain a bad subscriber: the frame it was
        // reporting is still dropped, and — the part that matters — the frames after it are still
        // decoded. Deliberately no throwing TraceListener here: Trace.Listeners is process-global,
        // and a test that installs a throwing listener can be reached by anything else running in
        // the same process. SafeTrace covers the listener case in production.
        var device = CreateStreamingDevice(analogCount: 2);
        AnalogChannel(device, 0).IsEnabled = true;
        AnalogChannel(device, 1).IsEnabled = true;
        device.StartStreaming();

        var calls = 0;
        device.StreamFrameDiscarded += (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("bad subscriber");
        };

        var warmup = new DaqifiOutMessage { MsgTimeStamp = 1000 };
        warmup.AnalogInDataFloat.Add(0.1f);

        Assert.Null(Record.Exception(() => device.InvokeStreamMessage(warmup)));
        Assert.Equal(1, calls); // the subscriber really did run and really did throw

        // The stream carries on: the next full frame decodes normally.
        var full = new DaqifiOutMessage { MsgTimeStamp = 2000 };
        full.AnalogInDataFloat.Add(4f);
        full.AnalogInDataFloat.Add(8f);
        Assert.Null(Record.Exception(() => device.InvokeStreamMessage(full)));
        Assert.Equal(4.0, AnalogChannel(device, 0).ActiveSample!.Value);
        Assert.Equal(8.0, AnalogChannel(device, 1).ActiveSample!.Value);
    }

    [Fact]
    public void Decode_WellFormedFirstFrame_PassesThroughCompletelyUnchanged()
    {
        // The guard is meant to be safe to leave in permanently, including on firmware that no
        // longer emits the malformed frame: a well-formed first frame must reach both consumer
        // paths, with the same object and the same values, and report no discard at all.
        var device = CreateStreamingDevice(analogCount: 2);
        var ai0 = AnalogChannel(device, 0);
        var ai1 = AnalogChannel(device, 1);
        ai0.IsEnabled = true;
        ai1.IsEnabled = true;
        device.StartStreaming();

        var rawFrames = new List<object?>();
        device.MessageReceived += (_, e) => rawFrames.Add(e.Message.Data);
        var discards = 0;
        device.StreamFrameDiscarded += (_, _) => discards++;

        var first = new DaqifiOutMessage { MsgTimeStamp = 1000 };
        first.AnalogInDataFloat.Add(4f);
        first.AnalogInDataFloat.Add(8f);
        device.InvokeStreamMessage(first);

        Assert.Same(first, Assert.Single(rawFrames));
        Assert.Equal(4.0, ai0.ActiveSample!.Value);
        Assert.Equal(8.0, ai1.ActiveSample!.Value);
        Assert.Equal(0, discards);
        Assert.Equal(0, device.DiscardedStreamFrameCount);
    }

    [Fact]
    public void Decode_SingleEnabledAnalogChannel_FirstFrameNotSuppressed()
    {
        // One enabled channel and one analog value is a *complete* frame, not a partial one. This
        // is the case the "analog count < enabled count" rule must never get wrong, because it is
        // indistinguishable from the malformed frame by value count alone.
        var device = CreateStreamingDevice(analogCount: 4);
        var ai0 = AnalogChannel(device, 0);
        ai0.IsEnabled = true; // exactly one enabled
        device.StartStreaming();

        var rawFrames = 0;
        device.MessageReceived += (_, _) => rawFrames++;

        var first = new DaqifiOutMessage { MsgTimeStamp = 1000 };
        first.AnalogInDataFloat.Add(3.5f);
        device.InvokeStreamMessage(first);

        Assert.Equal(1, rawFrames);
        Assert.Equal(3.5, ai0.ActiveSample!.Value);
        Assert.Equal(0, device.DiscardedStreamFrameCount);
    }

    [Fact]
    public void Decode_FullFirstFrame_NotSuppressed()
    {
        // A first frame that already carries the full complement decodes immediately.
        var device = CreateStreamingDevice(analogCount: 2);
        var ai0 = AnalogChannel(device, 0);
        var ai1 = AnalogChannel(device, 1);
        ai0.IsEnabled = true;
        ai1.IsEnabled = true;
        device.StartStreaming();

        var frame = new DaqifiOutMessage { MsgTimeStamp = 1 };
        frame.AnalogInDataFloat.Add(1f);
        frame.AnalogInDataFloat.Add(2f);
        device.InvokeStreamMessage(frame);

        Assert.Equal(1.0, ai0.ActiveSample!.Value);
        Assert.Equal(2.0, ai1.ActiveSample!.Value);
    }

    [Fact]
    public void Decode_DigitalOnlyStream_FirstFrameNotSuppressed()
    {
        // With no analog channels enabled the warmup guard never engages: a digital-only first
        // frame is decoded normally.
        var device = CreateStreamingDevice(analogCount: 0, digitalCount: 4);
        var dio = Enumerable.Range(0, 4).Select(n => DigitalChannel(device, n)).ToList();
        foreach (var d in dio) d.IsEnabled = true;
        device.StartStreaming();

        var frame = new DaqifiOutMessage { MsgTimeStamp = 1 };
        frame.DigitalData = ByteString.CopyFrom(new byte[] { 0b1010 });
        device.InvokeStreamMessage(frame);

        Assert.Equal(0.0, dio[0].ActiveSample!.Value);
        Assert.Equal(1.0, dio[1].ActiveSample!.Value);
    }

    [Fact]
    public void Decode_WarmupGuardReArmsForEachSession()
    {
        // The guard is re-armed at every StartStreaming, so a warmup frame is suppressed at the
        // start of a *subsequent* session too.
        var device = CreateStreamingDevice(analogCount: 2);
        var ai0 = AnalogChannel(device, 0);
        var ai1 = AnalogChannel(device, 1);
        ai0.IsEnabled = true;
        ai1.IsEnabled = true;

        device.StreamingFrequency = 100; // 500_000 ticks per sample at the 50 MHz default

        // Session 1: warmup + a full frame.
        device.StartStreaming();
        var w1 = new DaqifiOutMessage { MsgTimeStamp = 500_000 };
        w1.AnalogInDataFloat.Add(0.1f);
        device.InvokeStreamMessage(w1);
        var f1 = new DaqifiOutMessage { MsgTimeStamp = 1_000_000 };
        f1.AnalogInDataFloat.Add(1f);
        f1.AnalogInDataFloat.Add(2f);
        device.InvokeStreamMessage(f1);
        device.StopStreaming();

        // Session 2 starts well past the leftover window (a real stop/start gap), so its first
        // frame is genuine — and, being partial, must again be suppressed as a warmup frame.
        device.StartStreaming();
        var w2 = new DaqifiOutMessage { MsgTimeStamp = 60_000_000 };
        w2.AnalogInDataFloat.Add(5f); // single value -> partial again
        device.InvokeStreamMessage(w2);

        // AI1 still holds session-1's value; the session-2 warmup frame did not overwrite AI0.
        Assert.Equal(1.0, ai0.ActiveSample!.Value);
        Assert.Equal(2.0, ai1.ActiveSample!.Value);
    }

    [Fact]
    public void Decode_DigitalOnlyStart_ThenAnalogEnabledMidStream_ShortFrameNotSuppressed()
    {
        // The warmup guard is armed only when analog channels are enabled at StartStreaming. A
        // session that starts digital-only leaves it disarmed, so a short analog frame arriving
        // after analog is enabled mid-stream is best-effort mapped, not treated as a leading
        // warmup frame far from session start.
        var device = CreateStreamingDevice(analogCount: 2, digitalCount: 2);
        var ai0 = AnalogChannel(device, 0);
        var ai1 = AnalogChannel(device, 1);
        var dio0 = DigitalChannel(device, 0);
        dio0.IsEnabled = true; // digital-only at start
        device.StartStreaming();

        // A digital frame streams normally.
        var digital = new DaqifiOutMessage { MsgTimeStamp = 1 };
        digital.DigitalData = ByteString.CopyFrom(new byte[] { 0b1 });
        device.InvokeStreamMessage(digital);
        Assert.Equal(1.0, dio0.ActiveSample!.Value);

        // Enable analog mid-stream, then a short analog frame arrives (guard was never armed).
        device.EnableChannels(new[] { ai0, ai1 });
        var shortAnalog = new DaqifiOutMessage { MsgTimeStamp = 2 };
        shortAnalog.AnalogInDataFloat.Add(7f); // one value for two enabled channels

        var ex = Record.Exception(() => device.InvokeStreamMessage(shortAnalog));

        Assert.Null(ex);
        Assert.Equal(7.0, ai0.ActiveSample!.Value); // not suppressed — best-effort mapped
        Assert.Null(ai1.ActiveSample);
    }

    [Fact]
    public void Decode_PersistentShortFrames_ReleasedAfterCap()
    {
        // Safety bound: a stream that only ever sends short frames must not be withheld forever.
        // After MaxSuppressedWarmupFrames (5) suppressed frames, the guard releases.
        var device = CreateStreamingDevice(analogCount: 2);
        var ai0 = AnalogChannel(device, 0);
        AnalogChannel(device, 1).IsEnabled = true;
        ai0.IsEnabled = true;
        device.StartStreaming();

        // 5 suppressed, the 6th is released (best-effort mapped).
        for (var i = 0; i < 6; i++)
        {
            var frame = new DaqifiOutMessage { MsgTimeStamp = (uint)(1000 + i) };
            frame.AnalogInDataFloat.Add(i);
            device.InvokeStreamMessage(frame);
        }

        Assert.NotNull(ai0.ActiveSample);
        Assert.Equal(5.0, ai0.ActiveSample!.Value); // the 6th frame's value
    }

    #endregion

    #region Cross-session leftover frames (firmware #533)

    [Fact]
    public void Decode_LeftoverFrameFromPreviousSession_DiscardedFromBothConsumerPaths()
    {
        // The device latches the last frame of a stopped session and emits it as the first frame of
        // the next one, one sample period after the session it actually belongs to. It must not
        // reach consumers or anchor the new session's clock.
        var device = CreateStreamingDevice(analogCount: 2);
        var ai0 = AnalogChannel(device, 0);
        var ai1 = AnalogChannel(device, 1);
        ai0.IsEnabled = true;
        ai1.IsEnabled = true;
        device.StreamingFrequency = 100; // 500_000 ticks per sample

        // Session 1 ends with a frame at tick 10_000_000.
        device.StartStreaming();
        var last = new DaqifiOutMessage { MsgTimeStamp = 10_000_000 };
        last.AnalogInDataFloat.Add(1f);
        last.AnalogInDataFloat.Add(2f);
        device.InvokeStreamMessage(last);
        device.StopStreaming();

        // Session 2 starts a long while later, but the first frame that arrives carries a counter
        // one sample period past session 1's last frame — the latched leftover.
        device.StartStreaming();
        var rawFrames = 0;
        device.MessageReceived += (_, _) => rawFrames++;
        var discards = new List<StreamFrameDiscardedEventArgs>();
        device.StreamFrameDiscarded += (_, e) => discards.Add(e);

        var leftover = new DaqifiOutMessage { MsgTimeStamp = 10_500_000 };
        leftover.AnalogInDataFloat.Add(98f);
        leftover.AnalogInDataFloat.Add(99f);
        device.InvokeStreamMessage(leftover);

        Assert.Equal(0, rawFrames);
        var discarded = Assert.Single(discards);
        Assert.Equal(StreamFrameDiscardReason.StaleLeftoverFrame, discarded.Reason);
        Assert.Equal(10_500_000u, discarded.DeviceTimestamp);
        Assert.Equal(2, discarded.AnalogValueCount);          // a leftover is a *full* frame
        Assert.Equal(2, discarded.EnabledAnalogChannelCount);
        Assert.Equal(1.0, ai0.ActiveSample!.Value); // still session 1's values
        Assert.Equal(2.0, ai1.ActiveSample!.Value);

        // The genuine first frame of session 2 follows and is delivered normally.
        var genuine = new DaqifiOutMessage { MsgTimeStamp = 400_000_000 };
        genuine.AnalogInDataFloat.Add(5f);
        genuine.AnalogInDataFloat.Add(6f);
        device.InvokeStreamMessage(genuine);

        Assert.Equal(1, rawFrames);
        Assert.Equal(5.0, ai0.ActiveSample!.Value);
        Assert.Equal(6.0, ai1.ActiveSample!.Value);
        Assert.Equal(1, device.DiscardedStreamFrameCount);
    }

    [Fact]
    public void Decode_QuickRestart_DoesNotDiscardGenuineFrames()
    {
        // The leftover window scales with the sample period, so a restart that takes longer than a
        // couple of sample periods is never mistaken for a leftover. At 100 Hz the window is 25 ms;
        // this restart gap is 200 ms.
        var device = CreateStreamingDevice(analogCount: 2);
        var ai0 = AnalogChannel(device, 0);
        ai0.IsEnabled = true;
        AnalogChannel(device, 1).IsEnabled = true;
        device.StreamingFrequency = 100;

        device.StartStreaming();
        var last = new DaqifiOutMessage { MsgTimeStamp = 10_000_000 };
        last.AnalogInDataFloat.Add(1f);
        last.AnalogInDataFloat.Add(2f);
        device.InvokeStreamMessage(last);
        device.StopStreaming();

        device.StartStreaming();
        var discards = 0;
        device.StreamFrameDiscarded += (_, _) => discards++;

        // 200 ms later at 50 MHz = 10_000_000 ticks on.
        var genuine = new DaqifiOutMessage { MsgTimeStamp = 20_000_000 };
        genuine.AnalogInDataFloat.Add(7f);
        genuine.AnalogInDataFloat.Add(8f);
        device.InvokeStreamMessage(genuine);

        Assert.Equal(0, discards);
        Assert.Equal(7.0, ai0.ActiveSample!.Value);
    }

    [Fact]
    public void Decode_LeftoverGuardInactiveOnFirstSession()
    {
        // With no counter value from before the session there is nothing to compare against, so the
        // first session after connect delivers its first frame untouched rather than guessing.
        var device = CreateStreamingDevice(analogCount: 1);
        var ai0 = AnalogChannel(device, 0);
        ai0.IsEnabled = true;
        device.StartStreaming();

        var discards = 0;
        device.StreamFrameDiscarded += (_, _) => discards++;

        var first = new DaqifiOutMessage { MsgTimeStamp = 7 };
        first.AnalogInDataFloat.Add(3f);
        device.InvokeStreamMessage(first);

        Assert.Equal(0, discards);
        Assert.Equal(3.0, ai0.ActiveSample!.Value);
    }

    [Fact]
    public void Decode_FrameArrivingWhileStopped_SeedsTheLeftoverReference()
    {
        // The device can emit a final frame after the stop command lands, and the frame latched for
        // the next session follows *that* one. A frame received while stopped is still re-raised to
        // raw consumers, but it must also update the reference the next session is checked against.
        var device = CreateStreamingDevice(analogCount: 1);
        var ai0 = AnalogChannel(device, 0);
        ai0.IsEnabled = true;
        device.StreamingFrequency = 100;

        var trailing = new DaqifiOutMessage { MsgTimeStamp = 10_000_000 };
        trailing.AnalogInDataFloat.Add(1f);

        var rawFrames = 0;
        device.MessageReceived += (_, _) => rawFrames++;
        device.InvokeStreamMessage(trailing); // arrives while not streaming
        Assert.Equal(1, rawFrames);
        Assert.Null(ai0.ActiveSample); // re-raised but not decoded

        device.StartStreaming();
        var discards = 0;
        device.StreamFrameDiscarded += (_, _) => discards++;

        var leftover = new DaqifiOutMessage { MsgTimeStamp = 10_500_000 };
        leftover.AnalogInDataFloat.Add(99f);
        device.InvokeStreamMessage(leftover);

        Assert.Equal(1, discards);
        Assert.Null(ai0.ActiveSample);
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

    private static DaqifiOutMessage AnalogFrame(uint timestamp, float value)
    {
        var frame = new DaqifiOutMessage { MsgTimeStamp = timestamp };
        frame.AnalogInDataFloat.Add(value);
        return frame;
    }

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
