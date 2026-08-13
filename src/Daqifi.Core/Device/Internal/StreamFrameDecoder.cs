using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using System;
using System.Collections.Generic;
using System.Threading;

#nullable enable

namespace Daqifi.Core.Device.Internal
{
    /// <summary>
    /// The streaming hot path extracted from <see cref="DaqifiStreamingDevice"/> (#344): everything
    /// that happens to a frame between the transport handing it over and the per-channel
    /// <see cref="IChannel.SampleReceived"/> samples it becomes — the two frame guards, timestamp
    /// reconstruction, gap detection, and the analog/digital unpacking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session-scoped state this owns — the timestamp anchor, the gap detector, the
    /// cross-session leftover gate, the warmup-frame guard, and the two per-session counters — is
    /// exactly the set that <see cref="BeginSession"/> resets together. Keeping it in one object is
    /// the point: the failure mode this guards against is a partial reset, where frames are decoded
    /// against the previous session's anchor and stamped with times that never happened.
    /// </para>
    /// <para>
    /// The <see cref="DaqifiStreamingDevice.StreamFrameDiscarded"/> and
    /// <see cref="DaqifiStreamingDevice.GapDetected"/> events stay on the device, raised through
    /// <see cref="IDeviceOperationHost"/> rather than owned here, because their <c>sender</c> has to
    /// remain the device a subscriber attached to. So does re-raising the raw frame, which must go
    /// through the device's <c>base.OnStreamMessageReceived</c> so a subclass that overrides it
    /// still intercepts it.
    /// </para>
    /// <para>
    /// Not thread-safe, matching the code it was moved from. The decode path —
    /// <see cref="ProcessFrame"/> and everything under it — runs solely on the single
    /// message-consumer thread, which is also the thread that repopulates channels.
    /// <see cref="BeginSession"/> is the exception, and the reason this is stated rather than
    /// assumed: it runs on whichever thread called <see cref="DaqifiStreamingDevice.StartStreaming"/>
    /// or sent a raw start-streaming command through <c>Send</c>, and it writes the session fields
    /// with no synchronization against the consumer thread reading them.
    /// </para>
    /// <para>
    /// What makes that sound is the session boundary, not synchronization: a session's frames only
    /// exist once the device has been told to start, and the two starts bracket the reset so that
    /// no frame is decoded as part of a session whose reset has not run. <c>StartStreaming</c>
    /// resets before it sends the command. The raw-<c>Send</c> path can only reset after the
    /// command has gone out, but it leaves <see cref="IDeviceOperationHost.IsStreaming"/> false
    /// until the reset completes, so a frame landing in that window is re-raised as a stray frame
    /// and never decoded. Starting a stream concurrently with another thread's in-flight session is
    /// outside the device's contract, exactly as it was before this was extracted.
    /// </para>
    /// <para>
    /// The two counters are interlocked because they are read from arbitrary threads through the
    /// device's public properties.
    /// </para>
    /// </remarks>
    internal sealed class StreamFrameDecoder
    {
        /// <summary>
        /// The maximum number of leading short-analog frames suppressed at stream start
        /// (see <see cref="_awaitingFirstFullAnalogFrame"/>). Bounds the warmup-frame guard so a
        /// genuinely short stream can never be withheld indefinitely.
        /// </summary>
        private const int MaxSuppressedWarmupFrames = 5;

        /// <summary>
        /// The per-device key used with <see cref="_timestampProcessor"/>. The processor is not
        /// shared across devices, so the key only needs to be stable within this instance.
        /// </summary>
        private const string StreamTimestampKey = "stream";

        private readonly IDeviceOperationHost _host;

        /// <summary>
        /// Reconstructs host timestamps from the device's rolling 32-bit tick counter during a
        /// streaming session. Scoped to this device instance, so a single fixed key suffices.
        /// </summary>
        private readonly ITimestampProcessor _timestampProcessor = new TimestampProcessor();

        /// <summary>
        /// Detects dropped samples from the device-clock delta between frames. Reset at the start of
        /// every streaming session alongside <see cref="_timestampProcessor"/>. Drives
        /// <see cref="DaqifiStreamingDevice.GapDetected"/>.
        /// </summary>
        private readonly TimestampGapDetector _gapDetector = new();

        /// <summary>
        /// Keeps frames the device latched from the previous streaming session out of this one
        /// (daqifi-nyquist-firmware #533). Re-armed by <see cref="BeginSession"/>.
        /// </summary>
        private readonly StreamFrameGate _frameGate = new();

        /// <summary>
        /// True from the start of a streaming session that begins with analog channels enabled,
        /// until the first analog-bearing frame carrying the full enabled-channel complement has
        /// been decoded (disarmed for a digital-only start). Guards the malformed warmup frame
        /// the firmware emits at stream start (issue #351): its fast streaming encoder can emit a
        /// leading frame with fewer analog values than the enabled channel mask, which would
        /// otherwise reach every consumer as a partial <see cref="DataSample"/> (silently corrupting
        /// first-value baselining, gap detection, and export). For such leading short frames only
        /// the malformed analog decode is skipped — a combined frame's digital payload is still
        /// decoded and the raw frame is still re-raised — until the first full frame arrives,
        /// bounded by <see cref="MaxSuppressedWarmupFrames"/>.
        /// </summary>
        private bool _awaitingFirstFullAnalogFrame;

        /// <summary>
        /// Count of leading short-analog frames suppressed in the current session; capped by
        /// <see cref="MaxSuppressedWarmupFrames"/>.
        /// </summary>
        private int _suppressedWarmupFrameCount;

        /// <summary>
        /// Backing counter for <see cref="DiscardedStreamFrameCount"/>.
        /// </summary>
        private long _discardedStreamFrameCount;

        /// <summary>
        /// Backing counter for <see cref="DecodeFailureCount"/>.
        /// </summary>
        private long _decodeFailureCount;

        /// <summary>
        /// The <see cref="IDeviceOperationHost.ChannelStateVersion"/> the two cached views below
        /// were built from, or -1 before the first build. Any other value means they are stale.
        /// </summary>
        private long _cachedChannelStateVersion = -1;

        /// <summary>
        /// The channel snapshot the current frame decodes against. Refreshed only when the device
        /// reports a different channel-state version.
        /// </summary>
        private IReadOnlyList<IChannel> _cachedChannels = Array.Empty<IChannel>();

        /// <summary>
        /// The enabled analog channels, ordered by channel number — the order the device streams
        /// analog values in.
        /// </summary>
        /// <remarks>
        /// Rebuilding this per frame was the single largest steady-state cost on the decode path
        /// (issue #490): a snapshot copy taken under the device's channels lock, a fresh list, and
        /// a comparison sort, a thousand times a second, for a set that only changes when channels
        /// are configured. Caching it also takes the decode thread out of contention with
        /// <c>EnableChannel</c> for that lock.
        /// </remarks>
        private IAnalogChannel[] _cachedActiveAnalogChannels = Array.Empty<IAnalogChannel>();

        internal StreamFrameDecoder(IDeviceOperationHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <inheritdoc cref="DaqifiStreamingDevice.DiscardedStreamFrameCount"/>
        internal long DiscardedStreamFrameCount => Interlocked.Read(ref _discardedStreamFrameCount);

        /// <inheritdoc cref="DaqifiStreamingDevice.DecodeFailureCount"/>
        internal long DecodeFailureCount => Interlocked.Read(ref _decodeFailureCount);

        /// <summary>
        /// Resets everything that is scoped to one streaming session, so the frames that follow are
        /// decoded against this session rather than the last one.
        /// </summary>
        /// <remarks>
        /// Called for <see cref="DaqifiStreamingDevice.StartStreaming"/> and for a start-streaming
        /// command sent directly through <c>Send</c>. It is one method precisely so the two cannot
        /// drift: a raw-started stream that skipped this would reconstruct timestamps from the
        /// previous session's anchor and re-use its gap detector, producing samples stamped with
        /// times that never happened — silently.
        /// </remarks>
        /// <param name="timestampFrequency">
        /// The device-reported tick frequency for this session (the device's 50 MHz fallback when
        /// unreported).
        /// </param>
        /// <param name="streamingFrequencyHz">The rate this session is starting at.</param>
        internal void BeginSession(uint timestampFrequency, int streamingFrequencyHz)
        {
            // Re-anchor per-session timestamp reconstruction: the first frame of this session
            // anchors to the current host time, and subsequent frames advance by the device-tick
            // delta. Apply the device-reported tick frequency (falls back to the 50 MHz default
            // when unreported, e.g. older firmware).
            _timestampProcessor.Reset(StreamTimestampKey);
            _timestampProcessor.SetTimestampFrequency(StreamTimestampKey, timestampFrequency);
            _gapDetector.Reset();

            // Arm the warmup-frame guard only when analog channels are enabled at stream start —
            // the reproduced failure mode (issue #351) is the firmware's leading partial-analog
            // frame at the start of an *analog* stream. A digital-only start needs no guard; leaving
            // it disarmed there also avoids suppressing short analog frames that could arrive far
            // from session start if analog channels are enabled mid-stream (a scenario with no
            // observed warmup frame).
            // Reads the device directly rather than through the decode path's cache: this runs on
            // the caller's thread (see the class remarks), and the cache fields belong to the
            // consumer thread. Once per session, so there is nothing to save here anyway.
            _awaitingFirstFullAnalogFrame = CountEnabledAnalogChannels(_host.SnapshotChannels()) > 0;
            _suppressedWarmupFrameCount = 0;
            Interlocked.Exchange(ref _decodeFailureCount, 0);

            // Re-arm the cross-session leftover guard against the counter value this session
            // inherits, and at this session's rate — the window is measured in sample periods.
            _frameGate.BeginSession(timestampFrequency, streamingFrequencyHz);
            Interlocked.Exchange(ref _discardedStreamFrameCount, 0);
        }

        /// <summary>
        /// Handles a streaming data frame: screens out the frames the device should not have sent,
        /// then re-raises the frame for raw-frame consumers and, while streaming, decodes it into
        /// per-channel samples that drive <see cref="IChannel.SampleReceived"/>.
        /// </summary>
        /// <remarks>
        /// Screening covers both consumer paths, which is the whole point of issue #425. The
        /// per-channel decode has guarded against the firmware's malformed leading frame since
        /// issue #351, but the raw <see cref="DaqifiDevice.MessageReceived"/> event was still handed
        /// the frame verbatim — and that is the path most callers actually use, including the
        /// example CLI, whose offline export inferred a channel count of one from it and truncated
        /// every sample that followed. Whatever is unfit for the decoded path is unfit for the raw
        /// one; both are now gated together, and every drop is reported through
        /// <see cref="DaqifiStreamingDevice.StreamFrameDiscarded"/>.
        /// </remarks>
        /// <param name="message">The streaming message from the device.</param>
        internal void ProcessFrame(DaqifiOutMessage message)
        {
            if (_host.IsStreaming && _frameGate.IsValidating && _frameGate.IsLeftoverFromPreviousSession(message))
            {
                RaiseStreamFrameDiscarded(
                    StreamFrameDiscardReason.StaleLeftoverFrame,
                    message,
                    CountAnalogValues(message),
                    GetActiveAnalogChannels().Length);
                return;
            }

            _frameGate.TrackFrame(message.MsgTimeStamp);

            // Only decode into channel samples while an app-driven stream is active. A stray frame
            // that arrives outside a streaming session is still re-raised but not decoded.
            if (!_host.IsStreaming)
            {
                _host.RaiseRawStreamFrame(message);
                return;
            }

            EmitStreamFrame(message);
        }

        /// <summary>
        /// Delivers a frame that has cleared <see cref="_frameGate"/>: re-raises it for raw-frame
        /// consumers and decodes it into per-channel samples.
        /// </summary>
        /// <remarks>
        /// The firmware's malformed leading frame (issue #351) is caught here rather than by the
        /// gate, because it needs the enabled-channel count and because what it costs is narrower:
        /// only the analog payload is unusable. Such a frame is withheld from raw consumers — they
        /// read <c>AnalogInData</c> straight off it, so there is no way to hand it over safely — but
        /// its digital payload is still decoded and its timestamp still anchors the session clock,
        /// exactly as before.
        /// </remarks>
        /// <param name="message">The frame to deliver.</param>
        private void EmitStreamFrame(DaqifiOutMessage message)
        {
            var suppressAnalog = false;
            var analogValueCount = 0;
            var enabledAnalogChannelCount = 0;

            if (_awaitingFirstFullAnalogFrame)
            {
                suppressAnalog = ShouldSuppressPartialAnalog(
                    message, out analogValueCount, out enabledAnalogChannelCount);
            }

            if (suppressAnalog)
            {
                // The counts reported here are the very ones the suppression decision was made on,
                // not a fresh reading: channel enablement can change from another thread, and a
                // discard whose reported numbers disagree with the reason it was discarded would
                // make the telemetry harder to trust than no telemetry at all.
                RaiseStreamFrameDiscarded(
                    StreamFrameDiscardReason.PartialAnalogFrame,
                    message,
                    analogValueCount,
                    enabledAnalogChannelCount);
            }
            else
            {
                // Preserve the raw-frame MessageReceived event so existing consumers that hand-demux
                // the protobuf frame keep working unchanged.
                _host.RaiseRawStreamFrame(message);
            }

            try
            {
                DecodeStreamFrame(message, suppressAnalog);
            }
            catch (Exception ex)
            {
                // A single malformed frame must never tear down the stream or starve other
                // consumers; decoding is best-effort per frame. That isolation stays exactly as it
                // was — the frame is dropped and the loop continues — but it is no longer silent
                // (issue #378): a decode that throws on every frame yields no samples, which used
                // to be indistinguishable from a device sending nothing at all. Both the counter
                // and the (throttled) event are observation only; neither changes what happens to
                // this frame or the next one.
                Interlocked.Increment(ref _decodeFailureCount);
                _host.RaiseStreamDecodeFailure(ex);
            }
        }

        /// <summary>
        /// Decides whether a leading frame's analog payload is the firmware's malformed warmup frame
        /// (issue #351): fewer analog values than there are enabled analog channels. Disarms the
        /// guard on the first analog-bearing frame that is not short, or once
        /// <see cref="MaxSuppressedWarmupFrames"/> have been suppressed.
        /// </summary>
        /// <param name="message">The frame about to be delivered.</param>
        /// <param name="analogValueCount">The number of analog values the frame carried.</param>
        /// <param name="enabledAnalogChannelCount">
        /// The number of enabled analog channels the decision was made against. Handed back so the
        /// discard event reports the same numbers the decision used rather than re-reading channel
        /// state that another thread may have changed in between.
        /// </param>
        /// <returns><c>true</c> when the frame's analog values must be withheld.</returns>
        private bool ShouldSuppressPartialAnalog(
            DaqifiOutMessage message,
            out int analogValueCount,
            out int enabledAnalogChannelCount)
        {
            analogValueCount = CountAnalogValues(message);
            enabledAnalogChannelCount = 0;

            // A frame with no analog payload says nothing about the warmup frame either way, so the
            // guard stays armed for the first analog-bearing frame.
            if (analogValueCount == 0)
            {
                return false;
            }

            enabledAnalogChannelCount = GetActiveAnalogChannels().Length;
            if (enabledAnalogChannelCount > 0
                && analogValueCount < enabledAnalogChannelCount
                && _suppressedWarmupFrameCount < MaxSuppressedWarmupFrames)
            {
                _suppressedWarmupFrameCount++;
                return true;
            }

            _awaitingFirstFullAnalogFrame = false;
            return false;
        }

        /// <summary>
        /// The number of analog values a frame carries, from whichever payload the transport used —
        /// USB streams pre-scaled floats, WiFi streams raw ADC counts.
        /// </summary>
        private static int CountAnalogValues(DaqifiOutMessage message) =>
            message.AnalogInDataFloat.Count > 0
                ? message.AnalogInDataFloat.Count
                : message.AnalogInData.Count;

        /// <summary>
        /// Counts a withheld frame and asks the device to raise
        /// <see cref="DaqifiStreamingDevice.StreamFrameDiscarded"/> for it.
        /// </summary>
        /// <remarks>
        /// The increment happens before the event is raised, so a handler that reads
        /// <see cref="DaqifiStreamingDevice.DiscardedStreamFrameCount"/> sees a total that already
        /// includes the frame it is being told about — a documented guarantee.
        /// </remarks>
        /// <param name="reason">Why the frame was withheld.</param>
        /// <param name="frame">The frame that was withheld.</param>
        /// <param name="analogValueCount">The number of analog values the frame carried.</param>
        /// <param name="enabledAnalogChannelCount">
        /// The number of enabled analog channels to report. Passed in rather than re-derived so it
        /// is the same reading the discard decision was made against.
        /// </param>
        private void RaiseStreamFrameDiscarded(
            StreamFrameDiscardReason reason,
            DaqifiOutMessage frame,
            int analogValueCount,
            int enabledAnalogChannelCount)
        {
            Interlocked.Increment(ref _discardedStreamFrameCount);

            _host.RaiseStreamFrameDiscarded(new StreamFrameDiscardedEventArgs(
                reason, frame.MsgTimeStamp, analogValueCount, enabledAnalogChannelCount));
        }

        /// <summary>
        /// Decodes a streaming frame into per-channel samples: selects the active channels in
        /// device order, chooses the correct value source (USB pre-scaled float vs. WiFi raw ADC
        /// count scaled via calibration), unpacks digital bits, and pushes a sample to each channel.
        /// </summary>
        /// <param name="message">The streaming message to decode.</param>
        /// <param name="suppressAnalog">
        /// When <c>true</c>, the frame's analog payload is the firmware's malformed warmup frame
        /// (issue #351) and is skipped. Only the analog values are withheld — a combined frame's
        /// digital payload is still decoded, and the frame's (normal one-period) timestamp still
        /// anchors the session clock, so digital state and edges are not lost.
        /// </param>
        private void DecodeStreamFrame(DaqifiOutMessage message, bool suppressAnalog)
        {
            var hasFloat = message.AnalogInDataFloat.Count > 0;
            var hasRawAnalog = message.AnalogInData.Count > 0;
            var hasDigital = message.DigitalData.Length > 0;

            if (!hasFloat && !hasRawAnalog && !hasDigital)
            {
                return;
            }

            // Snapshot channels once: the consumer thread that repopulates channels is the same
            // thread that runs this decode, so the structure is stable for the duration of the call.
            // Both views are read after a single refresh so this frame's analog and digital decodes
            // are guaranteed to be describing the same set of channels.
            RefreshChannelCacheIfStale();
            var channels = _cachedChannels;
            var activeAnalog = _cachedActiveAnalogChannels;

            // Reconstruct a host timestamp from the device tick counter (rollover-aware) and carry
            // the raw device tick value through to each decoded sample.
            var deviceTimestamp = message.MsgTimeStamp;
            var timestampResult = _timestampProcessor.ProcessTimestamp(StreamTimestampKey, deviceTimestamp);
            var hostTimestamp = timestampResult.Timestamp;

            // Flag dropped samples from the device-clock delta (immune to host arrival jitter).
            // Isolate subscriber exceptions (the device does that) so a throwing GapDetected handler
            // cannot skip the per-channel decode below — which the caller's broad catch would then
            // silently drop.
            if (_gapDetector.IsGap(timestampResult.SecondsBetweenMessages))
            {
                _host.RaiseGapDetected(new TimestampGapEventArgs(
                    hostTimestamp, timestampResult.SecondsBetweenMessages, deviceTimestamp));
            }

            if ((hasFloat || hasRawAnalog) && !suppressAnalog)
            {
                DecodeAnalog(message, activeAnalog, hostTimestamp, deviceTimestamp, hasFloat);
            }

            if (hasDigital)
            {
                DecodeDigital(message, channels, hostTimestamp, deviceTimestamp);
            }
        }

        /// <summary>
        /// Brings <see cref="_cachedChannels"/> and <see cref="_cachedActiveAnalogChannels"/> up to
        /// date if the device's channel state has moved since they were built.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The version is read <em>before</em> the snapshot, deliberately. A channel change landing
        /// between the two reads then leaves the cache carrying newer contents under an older
        /// version, so the next frame simply rebuilds — the cost of a redundant rebuild. Reading it
        /// afterwards would do the opposite and stamp a pre-change snapshot with the post-change
        /// version, which no later frame could ever detect: analog values would keep being mapped
        /// onto the channels that were enabled before the change, silently.
        /// </para>
        /// <para>
        /// Consumer-thread only, like the rest of the decode path. <see cref="BeginSession"/> runs
        /// on the caller's thread and deliberately does not touch these fields.
        /// </para>
        /// </remarks>
        private void RefreshChannelCacheIfStale()
        {
            var version = _host.ChannelStateVersion;
            if (version == _cachedChannelStateVersion)
            {
                return;
            }

            var channels = _host.SnapshotChannels();

            var activeAnalogCount = 0;
            foreach (var channel in channels)
            {
                if (channel.IsEnabled && channel is IAnalogChannel)
                {
                    activeAnalogCount++;
                }
            }

            var activeAnalog = activeAnalogCount == 0
                ? Array.Empty<IAnalogChannel>()
                : new IAnalogChannel[activeAnalogCount];

            var index = 0;
            foreach (var channel in channels)
            {
                if (channel.IsEnabled && channel is IAnalogChannel analog)
                {
                    activeAnalog[index++] = analog;
                }
            }

            // The device streams one value per enabled analog channel, ordered by channel number,
            // not by activation order — so re-derive that ordering here.
            Array.Sort(activeAnalog, static (a, b) => a.ChannelNumber.CompareTo(b.ChannelNumber));

            _cachedChannels = channels;
            _cachedActiveAnalogChannels = activeAnalog;
            _cachedChannelStateVersion = version;
        }

        /// <summary>
        /// The enabled analog channels in device order, refreshing the cache if it is stale.
        /// </summary>
        private IAnalogChannel[] GetActiveAnalogChannels()
        {
            RefreshChannelCacheIfStale();
            return _cachedActiveAnalogChannels;
        }

        /// <summary>
        /// The number of enabled analog channels in a channel snapshot.
        /// </summary>
        private static int CountEnabledAnalogChannels(IReadOnlyList<IChannel> channels)
        {
            var count = 0;
            foreach (var channel in channels)
            {
                if (channel.IsEnabled && channel is IAnalogChannel)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Maps a frame's analog values to the enabled analog channels, in ascending channel order.
        /// USB firmware streams pre-scaled floats (used directly); WiFi firmware streams raw ADC
        /// counts (scaled per channel via <see cref="IAnalogChannel.GetScaledValue"/>).
        /// </summary>
        /// <param name="message">The frame being decoded.</param>
        /// <param name="activeAnalog">
        /// The enabled analog channels already ordered by channel number — the order the device
        /// streams values in. Supplied by <see cref="GetActiveAnalogChannels"/> rather than derived
        /// here, so the filter and the sort happen when channels change instead of per frame.
        /// </param>
        /// <param name="hostTimestamp">The reconstructed host timestamp for this frame.</param>
        /// <param name="deviceTimestamp">The frame's raw device tick value.</param>
        /// <param name="hasFloat">Whether the frame carries pre-scaled floats rather than raw counts.</param>
        private static void DecodeAnalog(
            DaqifiOutMessage message,
            IAnalogChannel[] activeAnalog,
            DateTime hostTimestamp,
            uint deviceTimestamp,
            bool hasFloat)
        {
            var dataCount = hasFloat ? message.AnalogInDataFloat.Count : message.AnalogInData.Count;
            var count = Math.Min(dataCount, activeAnalog.Length);

            for (var i = 0; i < count; i++)
            {
                var channel = activeAnalog[i];
                double scaled;
                int? raw;

                if (hasFloat)
                {
                    // USB firmware already scaled to volts; no raw ADC count is available.
                    scaled = message.AnalogInDataFloat[i];
                    raw = null;
                }
                else
                {
                    // WiFi firmware sent a raw ADC count; apply this channel's calibration.
                    var rawValue = message.AnalogInData[i];
                    scaled = channel.GetScaledValue(rawValue);
                    raw = rawValue;
                }

                // The engineering-unit conversion, if the caller configured one. Stamped onto the
                // sample rather than applied to its Value: the volts stay readable next to the
                // converted reading, and nothing about the sample is mutated after the fact. Read
                // per sample because scaling can change at any time without a channel-state version
                // bump, which is what the cached channel array keys off (#501).
                var scaling = channel is IScaledChannel scaledChannel ? scaledChannel.Scaling : null;

                channel.SetActiveSample(
                    new DataSample(hostTimestamp, scaled, raw, deviceTimestamp) { Scaling = scaling });
            }
        }

        /// <summary>
        /// Unpacks a frame's digital byte(s) into per-channel high/low samples for the enabled
        /// digital input channels. The firmware streams the whole DIO port as a raw pin-state
        /// snapshot (the wire-level DIO enable is global, not per pin), so a channel's bit
        /// position is its channel number — bit <c>n</c> lives at byte <c>n / 8</c>, bit
        /// <c>n % 8</c> (LSB first) — independent of which channels the client has enabled.
        /// Output-direction channels are not sampled (their state is client-driven via
        /// <see cref="IStreamingDevice.SetDioValue"/>). Channels whose number lies beyond the
        /// payload get no sample rather than a bogus "low" reading.
        /// </summary>
        private static void DecodeDigital(
            DaqifiOutMessage message,
            IReadOnlyList<IChannel> channels,
            DateTime hostTimestamp,
            uint deviceTimestamp)
        {
            var digitalData = message.DigitalData;
            var bitCount = digitalData.Length * 8;

            foreach (var channel in channels)
            {
                if (!channel.IsEnabled || channel.Type != ChannelType.Digital)
                {
                    continue;
                }

                // Only input-direction channels carry a meaningful streamed reading.
                if (channel.Direction != ChannelDirection.Input)
                {
                    continue;
                }

                var bitIndex = channel.ChannelNumber;
                if (bitIndex >= bitCount)
                {
                    continue;
                }

                var bit = (digitalData[bitIndex / 8] & (1 << (bitIndex % 8))) != 0;

                channel.SetActiveSample(
                    new DataSample(hostTimestamp, bit ? 1.0 : 0.0, bit ? 1 : 0, deviceTimestamp));
            }
        }
    }
}
