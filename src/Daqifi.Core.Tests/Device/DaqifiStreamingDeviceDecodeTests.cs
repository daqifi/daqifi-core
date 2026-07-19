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
    public void Decode_WarmupFrame_StillReRaisesRawMessage()
    {
        // Suppression skips only the per-channel decode; raw-frame consumers still see the frame.
        var device = CreateStreamingDevice(analogCount: 2);
        AnalogChannel(device, 0).IsEnabled = true;
        AnalogChannel(device, 1).IsEnabled = true;
        device.StartStreaming();

        var rawFrames = 0;
        device.MessageReceived += (_, _) => rawFrames++;

        var warmup = new DaqifiOutMessage { MsgTimeStamp = 1 };
        warmup.AnalogInDataFloat.Add(0.1f);
        device.InvokeStreamMessage(warmup);

        Assert.Equal(1, rawFrames);
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

        // Session 1: warmup + a full frame.
        device.StartStreaming();
        var w1 = new DaqifiOutMessage { MsgTimeStamp = 1 };
        w1.AnalogInDataFloat.Add(0.1f);
        device.InvokeStreamMessage(w1);
        var f1 = new DaqifiOutMessage { MsgTimeStamp = 2 };
        f1.AnalogInDataFloat.Add(1f);
        f1.AnalogInDataFloat.Add(2f);
        device.InvokeStreamMessage(f1);
        device.StopStreaming();

        // Session 2: a fresh warmup frame must again be suppressed.
        device.StartStreaming();
        var w2 = new DaqifiOutMessage { MsgTimeStamp = 3 };
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
