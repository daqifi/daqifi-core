using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using Daqifi.Core.Device.SdCard;
using Google.Protobuf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>
/// Unit tests for <see cref="StreamFrameDecoder"/>, the streaming hot path extracted from
/// <see cref="DaqifiStreamingDevice"/> (#344).
/// </summary>
/// <remarks>
/// <para>
/// The end-to-end behavior of this pipeline is already pinned through the device by
/// <c>DaqifiStreamingDeviceDecodeTests</c>, and those tests are deliberately untouched — they are
/// the evidence that the extraction changed nothing. What these add is the part only a direct test
/// can see: the <b>order and multiplicity of the calls back into the host</b>. Through the device
/// those callbacks are invisible — a raw frame re-raised twice, or a discard counted after the
/// event rather than before, looks identical from the outside until a consumer trips over it.
/// </para>
/// </remarks>
public class StreamFrameDecoderTests
{
    private const uint TicksPerSecond = 1000;

    #region Raw-frame re-raise

    [Fact]
    public void FrameArrivingWhileNotStreaming_IsReRaisedOnce_AndNotDecoded()
    {
        var host = new FakeHost { IsStreaming = false };
        var ai0 = host.AddAnalog(0, enabled: true);
        var decoder = new StreamFrameDecoder(host);

        decoder.ProcessFrame(AnalogFrame(1000, 1.5f));

        Assert.Equal(new[] { "raw" }, host.Calls);
        Assert.Null(ai0.ActiveSample);
    }

    [Fact]
    public void DeliveredFrame_IsReRaisedExactlyOnce()
    {
        // The raw re-raise and the decode are two separate consumer paths off one frame. Re-raising
        // twice would double-count for every consumer that hand-demuxes the protobuf frame.
        var host = new FakeHost { IsStreaming = true };
        var ai0 = host.AddAnalog(0, enabled: true);
        var decoder = new StreamFrameDecoder(host);
        decoder.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        decoder.ProcessFrame(AnalogFrame(1000, 2.5f));

        Assert.Equal(new[] { "raw" }, host.Calls);
        Assert.Equal(2.5, ai0.ActiveSample!.Value);
    }

    [Fact]
    public void SuppressedWarmupFrame_IsNotReRaised_ButItsDigitalPayloadStillDecodes()
    {
        // A short-analog leading frame is unusable to raw consumers (they read AnalogInData straight
        // off it), so it is withheld — but withholding the whole frame would lose digital edges.
        var host = new FakeHost { IsStreaming = true };
        var ai0 = host.AddAnalog(0, enabled: true);
        var ai1 = host.AddAnalog(1, enabled: true);
        var di0 = host.AddDigital(0, enabled: true);
        var decoder = new StreamFrameDecoder(host);
        decoder.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        var frame = new DaqifiOutMessage { MsgTimeStamp = 1000, DigitalData = ByteString.CopyFrom(0x01) };
        frame.AnalogInDataFloat.Add(9f); // one value for two enabled analog channels
        decoder.ProcessFrame(frame);

        Assert.Equal(new[] { "discard" }, host.Calls); // no "raw"
        Assert.Null(ai0.ActiveSample);
        Assert.Null(ai1.ActiveSample);
        Assert.Equal(1.0, di0.ActiveSample!.Value);
    }

    [Fact]
    public void StaleLeftoverFrame_IsNeitherReRaisedNorDecoded()
    {
        var host = new FakeHost { IsStreaming = false };
        var ai0 = host.AddAnalog(0, enabled: true);
        var decoder = new StreamFrameDecoder(host);

        // Seed the gate's counter reference with the trailing frame the device emits after the stop
        // command lands, then open a session whose first frame falls inside the leftover window.
        decoder.ProcessFrame(AnalogFrame(10_000, 1f));
        host.Calls.Clear();
        host.IsStreaming = true;
        decoder.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        decoder.ProcessFrame(AnalogFrame(10_500, 99f));

        Assert.Equal(new[] { "discard" }, host.Calls);
        Assert.Null(ai0.ActiveSample);
        Assert.Equal(1, decoder.DiscardedStreamFrameCount);
    }

    #endregion

    #region Discard counting

    [Fact]
    public void DiscardIsCountedBeforeTheEventIsRaised()
    {
        // Documented guarantee on DiscardedStreamFrameCount: read inside a StreamFrameDiscarded
        // handler, the count already includes the frame being reported. The device raises the event,
        // so only a direct test can pin that the decoder increments first.
        var host = new FakeHost { IsStreaming = true };
        host.AddAnalog(0, enabled: true);
        host.AddAnalog(1, enabled: true);
        StreamFrameDecoder? decoder = null;
        long countSeenByHandler = -1;
        host.OnDiscard = _ => countSeenByHandler = decoder!.DiscardedStreamFrameCount;

        decoder = new StreamFrameDecoder(host);
        decoder.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        var frame = new DaqifiOutMessage { MsgTimeStamp = 1000 };
        frame.AnalogInDataFloat.Add(9f);
        decoder.ProcessFrame(frame);

        Assert.Equal(1, countSeenByHandler);
    }

    [Fact]
    public void DiscardIsCountedEvenWhenTheHostRaisesNothing()
    {
        var host = new FakeHost { IsStreaming = true };
        host.AddAnalog(0, enabled: true);
        host.AddAnalog(1, enabled: true);
        var decoder = new StreamFrameDecoder(host);
        decoder.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        var frame = new DaqifiOutMessage { MsgTimeStamp = 1000 };
        frame.AnalogInDataFloat.Add(9f);
        decoder.ProcessFrame(frame);

        Assert.Equal(1, decoder.DiscardedStreamFrameCount);
    }

    [Fact]
    public void DiscardEventCarriesTheCountsTheDecisionWasMadeOn()
    {
        var host = new FakeHost { IsStreaming = true };
        host.AddAnalog(0, enabled: true);
        host.AddAnalog(1, enabled: true);
        host.AddAnalog(2, enabled: true);
        StreamFrameDiscardedEventArgs? reported = null;
        host.OnDiscard = e => reported = e;

        var decoder = new StreamFrameDecoder(host);
        decoder.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        var frame = new DaqifiOutMessage { MsgTimeStamp = 4242 };
        frame.AnalogInDataFloat.Add(1f);
        decoder.ProcessFrame(frame);

        Assert.NotNull(reported);
        Assert.Equal(StreamFrameDiscardReason.PartialAnalogFrame, reported!.Reason);
        Assert.Equal(4242u, reported.DeviceTimestamp);
        Assert.Equal(1, reported.AnalogValueCount);
        Assert.Equal(3, reported.EnabledAnalogChannelCount);
    }

    #endregion

    #region Decode-failure isolation

    [Fact]
    public void DecodeFailure_IsCountedAndReported_AndTheFrameStillReachedRawConsumers()
    {
        // Best-effort per frame (#378): a throwing decode must not tear down the stream, and the
        // raw re-raise has already happened by then — the ordering is what keeps a bad decode from
        // starving the other consumer path.
        var host = new FakeHost { IsStreaming = true };
        var channel = host.AddAnalog(0, enabled: true);
        var thrower = AttachThrowingSubscriber(channel);
        var decoder = new StreamFrameDecoder(host);
        decoder.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        decoder.ProcessFrame(AnalogFrame(1000, 1f));

        Assert.Equal(new[] { "raw", "decode-failure" }, host.Calls);
        Assert.Equal(1, decoder.DecodeFailureCount);
        Assert.Same(thrower.Error, host.LastDecodeFailure);
    }

    [Fact]
    public void DecodeFailure_DoesNotStopTheNextFrame()
    {
        var host = new FakeHost { IsStreaming = true };
        var channel = host.AddAnalog(0, enabled: true);
        var thrower = AttachThrowingSubscriber(channel);
        var decoder = new StreamFrameDecoder(host);
        decoder.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        decoder.ProcessFrame(AnalogFrame(1000, 1f));
        thrower.Error = null;
        decoder.ProcessFrame(AnalogFrame(2000, 7f));

        Assert.Equal(1, decoder.DecodeFailureCount);
        Assert.Equal(7.0, channel.ActiveSample!.Value);
    }

    #endregion

    #region Session reset

    [Fact]
    public void BeginSession_ResetsBothCounters()
    {
        var host = new FakeHost { IsStreaming = true };
        var channel = host.AddAnalog(0, enabled: true);
        host.AddAnalog(1, enabled: true);
        var decoder = new StreamFrameDecoder(host);
        decoder.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        var shortFrame = new DaqifiOutMessage { MsgTimeStamp = 1000 };
        shortFrame.AnalogInDataFloat.Add(1f);
        decoder.ProcessFrame(shortFrame);

        var thrower = AttachThrowingSubscriber(channel);
        decoder.ProcessFrame(FullAnalogFrame(2000, 1f, 2f));
        thrower.Error = null;

        Assert.Equal(1, decoder.DiscardedStreamFrameCount);
        Assert.Equal(1, decoder.DecodeFailureCount);

        decoder.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        Assert.Equal(0, decoder.DiscardedStreamFrameCount);
        Assert.Equal(0, decoder.DecodeFailureCount);
    }

    [Fact]
    public void BeginSession_LeavesTheWarmupGuardDisarmedForADigitalOnlyStart()
    {
        // No analog channel is enabled at session start, so a short analog frame arriving later
        // (analog enabled mid-stream) must not be mistaken for the firmware's warmup frame.
        var host = new FakeHost { IsStreaming = true };
        var decoder = new StreamFrameDecoder(host);
        host.AddDigital(0, enabled: true);
        decoder.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        var ai0 = host.AddAnalog(0, enabled: true);
        host.AddAnalog(1, enabled: true);

        var frame = new DaqifiOutMessage { MsgTimeStamp = 1000 };
        frame.AnalogInDataFloat.Add(3f);
        decoder.ProcessFrame(frame);

        Assert.Equal(new[] { "raw" }, host.Calls);
        Assert.Equal(0, decoder.DiscardedStreamFrameCount);
        Assert.Equal(3.0, ai0.ActiveSample!.Value);
    }

    [Fact]
    public void WarmupSuppression_IsBounded()
    {
        // A genuinely short stream must never be withheld forever: after the cap the frames flow.
        var host = new FakeHost { IsStreaming = true };
        host.AddAnalog(0, enabled: true);
        var ai1 = host.AddAnalog(1, enabled: true);
        var decoder = new StreamFrameDecoder(host);
        decoder.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        for (uint ts = 1000; ts <= 10_000; ts += 1000)
        {
            var frame = new DaqifiOutMessage { MsgTimeStamp = ts };
            frame.AnalogInDataFloat.Add(1f);
            decoder.ProcessFrame(frame);
        }

        Assert.Equal(5, decoder.DiscardedStreamFrameCount);
        Assert.Null(ai1.ActiveSample); // still only one value per frame
    }

    #endregion

    #region Gap detection

    [Fact]
    public void GapDetected_IsRaisedThroughTheHost_OnADeviceClockJump()
    {
        var host = new FakeHost { IsStreaming = true };
        host.AddAnalog(0, enabled: true);
        var decoder = new StreamFrameDecoder(host);
        decoder.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);

        for (uint ts = 1000; ts <= 11_000; ts += 1000)
        {
            decoder.ProcessFrame(AnalogFrame(ts, 1f));
        }
        Assert.Null(host.LastGap);

        decoder.ProcessFrame(AnalogFrame(16_000, 1f));

        Assert.NotNull(host.LastGap);
        Assert.Equal(16_000u, host.LastGap!.DeviceTimestamp);
    }

    [Fact]
    public void BeginSession_ResetsTheGapDetector()
    {
        var host = new FakeHost { IsStreaming = true };
        host.AddAnalog(0, enabled: true);
        var decoder = new StreamFrameDecoder(host);

        decoder.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);
        for (uint ts = 1000; ts <= 11_000; ts += 1000)
        {
            decoder.ProcessFrame(AnalogFrame(ts, 1f));
        }

        // A slower session: were the EMA carried over, the first 3000-tick delta would false-trip.
        decoder.BeginSession(TicksPerSecond, streamingFrequencyHz: 1);
        host.LastGap = null;
        decoder.ProcessFrame(AnalogFrame(100_000, 1f));
        decoder.ProcessFrame(AnalogFrame(103_000, 1f));
        decoder.ProcessFrame(AnalogFrame(106_000, 1f));

        Assert.Null(host.LastGap);
    }

    #endregion

    #region Guards

    [Fact]
    public void Constructor_RejectsANullHost()
    {
        Assert.Throws<ArgumentNullException>(() => new StreamFrameDecoder(null!));
    }

    #endregion

    #region Helpers

    private static ThrowSwitch AttachThrowingSubscriber(IChannel channel)
    {
        var thrower = new ThrowSwitch();
        channel.SampleReceived += (_, _) =>
        {
            if (thrower.Error != null)
            {
                throw thrower.Error;
            }
        };
        return thrower;
    }

    private static DaqifiOutMessage AnalogFrame(uint timestamp, float value)
    {
        var frame = new DaqifiOutMessage { MsgTimeStamp = timestamp };
        frame.AnalogInDataFloat.Add(value);
        return frame;
    }

    private static DaqifiOutMessage FullAnalogFrame(uint timestamp, params float[] values)
    {
        var frame = new DaqifiOutMessage { MsgTimeStamp = timestamp };
        foreach (var value in values)
        {
            frame.AnalogInDataFloat.Add(value);
        }
        return frame;
    }

    /// <summary>
    /// An <see cref="IDeviceOperationHost"/> that records the decoder's calls back into the device
    /// in order. Only the members the decoder uses are implemented; the rest throw, so a future
    /// change that makes the decoder reach for device I/O fails loudly instead of quietly.
    /// </summary>
    private sealed class FakeHost : IDeviceOperationHost
    {
        private readonly List<IChannel> _channels = new();

        public List<string> Calls { get; } = new();

        public Action<StreamFrameDiscardedEventArgs>? OnDiscard { get; set; }

        public TimestampGapEventArgs? LastGap { get; set; }

        public Exception? LastDecodeFailure { get; private set; }

        public bool IsStreaming { get; set; }

        public AnalogChannel AddAnalog(int number, bool enabled)
        {
            var channel = new AnalogChannel(number) { IsEnabled = enabled };
            _channels.Add(channel);
            return channel;
        }

        public IChannel AddDigital(int number, bool enabled)
        {
            var channel = new DigitalChannel(number) { IsEnabled = enabled, Direction = ChannelDirection.Input };
            _channels.Add(channel);
            return channel;
        }

        public IReadOnlyList<IChannel> SnapshotChannels() => _channels.ToArray();

        public void RaiseStreamFrameDiscarded(StreamFrameDiscardedEventArgs e)
        {
            Calls.Add("discard");
            OnDiscard?.Invoke(e);
        }

        public void RaiseGapDetected(TimestampGapEventArgs e)
        {
            Calls.Add("gap");
            LastGap = e;
        }

        public void RaiseRawStreamFrame(DaqifiOutMessage message) => Calls.Add("raw");

        public void RaiseStreamDecodeFailure(Exception error)
        {
            Calls.Add("decode-failure");
            LastDecodeFailure = error;
        }

        // Not part of the decode path.
        public bool IsConnected => throw new NotSupportedException();
        public bool IsUsbConnection => throw new NotSupportedException();
        public int StreamingFrequency => throw new NotSupportedException();
        public TimeSpan SdCardDownloadTimeout => throw new NotSupportedException();
        public TimeSpan SdCardTransferIdleTimeout => throw new NotSupportedException();
        public void StopStreaming() => throw new NotSupportedException();
        public void Send<T>(IOutboundMessage<T> message) => throw new NotSupportedException();
        public void WithChannelsLock(Action action) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null) => throw new NotSupportedException();
        public Task ExecuteRawCaptureAsync(
            Func<Stream, CancellationToken, Task> rawAction,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void EnsureSupported(DeviceFeature feature) => throw new NotSupportedException();
        public FeatureNotSupportedException CreateFeatureNotSupportedException(DeviceFeature feature)
            => throw new NotSupportedException();
        public void RaiseLowSdSpaceWarning(LowSdSpaceWarningEventArgs e) => throw new NotSupportedException();
    }

    /// <summary>
    /// A switch for a <see cref="IChannel.SampleReceived"/> subscriber that throws while
    /// <see cref="Error"/> is set. A throwing subscriber is the realistic shape of a decode that
    /// fails — it propagates out of the per-channel push and into the frame's catch — and is the
    /// same lever <c>DeviceErrorSurfaceTests</c> uses.
    /// </summary>
    private sealed class ThrowSwitch
    {
        public Exception? Error { get; set; } = new InvalidOperationException("decode consumer is broken");
    }

    #endregion
}
