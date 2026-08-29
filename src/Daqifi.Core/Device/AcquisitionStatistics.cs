using System;
using System.Collections.Generic;
using Daqifi.Core.Channel;

#nullable enable

namespace Daqifi.Core.Device;

/// <summary>
/// Measures the health of a live acquisition as the host actually experiences it: how many
/// samples each channel produced, the rate they really arrived at, how far apart they were, and
/// the range of values they carried. Attach one to a streaming device, stream, then read a
/// <see cref="Snapshot"/>.
/// </summary>
/// <remarks>
/// <para>
/// "Am I actually getting 1 kHz, or am I losing frames?" is the first question about any
/// acquisition, and nothing in Core could answer it. <see cref="TimestampGapDetector"/> flags
/// individual gaps as they happen and <see cref="DaqifiStreamingDevice.DroppedLiveSampleCount"/>
/// counts what a slow consumer discarded, but neither adds up to a measured rate; the device's
/// own <c>SYSTem:STReam:STATS?</c> counters describe what the firmware believes it sent, not what
/// arrived. This is the host-side account, ported from daqifi-desktop's <c>SummaryLogger</c>
/// (issue #502) so every Core consumer has it rather than just the desktop app.
/// </para>
/// <para>
/// <b>Opt-in, and free when nobody opts in.</b> This is an observer over the per-channel
/// <see cref="IChannel.SampleReceived"/> events the decode pipeline already raises — the same
/// seam <see cref="DaqifiStreamingDevice.StreamSamplesAsync"/> uses — not a second decode path
/// and not a hook inside the existing one. With no instance attached there is no subscriber, so
/// the decode path runs exactly the code it ran before this type existed. With one attached,
/// recording a sample allocates nothing.
/// </para>
/// <para>
/// Two ways to feed it, and they can be mixed:
/// </para>
/// <code>
/// // Attached: every decoded sample on every channel, for as long as it is not disposed.
/// using var stats = new AcquisitionStatistics(device);
/// device.StartStreaming();
/// await Task.Delay(TimeSpan.FromSeconds(5));
/// var snapshot = stats.Snapshot();
///
/// // Fed by hand: whatever a consumer chooses to pass it.
/// var stats = new AcquisitionStatistics();
/// await foreach (var sample in device.StreamSamplesAsync(ct)) stats.Record(sample);
/// </code>
/// <para>
/// An attached instance follows the device across a status message that replaces the channel
/// objects: it resubscribes to the new channels, and because a channel is tracked by its type and
/// number rather than by object identity, its statistics carry across the swap instead of
/// splitting in two.
/// </para>
/// <para>
/// Thread-safe. Samples arrive on the device's message-consumer thread while
/// <see cref="Snapshot"/> and <see cref="Reset"/> are called from wherever the consumer lives.
/// </para>
/// <para>
/// A device that sends a frame out of order gets a reconstructed timestamp that moves backwards
/// (see <c>TimestampProcessor</c>), which the device-clock figures are built to survive rather
/// than to hide: the backwards step is excluded from the jitter bounds, the span the device-clock
/// rate is measured over runs between the earliest and latest timestamps seen, and
/// <see cref="ChannelAcquisitionStatistics.OutOfOrderSampleCount"/> reports that it happened.
/// </para>
/// <para>
/// <b>Deliberate departures from the desktop original</b>, each a defect there rather than a
/// design choice: minimum and maximum value are seeded from the first sample (desktop left them
/// at zero, so a channel sitting at 4.5 V reported a minimum of 0); means divide by the number of
/// samples actually seen (desktop divided by a configured window size, so a partly-filled window
/// under-reported and an over-filled one over-reported); and a window runs until it is
/// <see cref="Reset"/> rather than being swapped out automatically every N samples.
/// </para>
/// </remarks>
public sealed class AcquisitionStatistics : IDisposable
{
    /// <summary>
    /// The host clock. Local time, matching <see cref="TimestampProcessor"/> — the timestamps on
    /// the samples being measured against it are local, and subtracting a UTC reading from one of
    /// those would report the machine's UTC offset as acquisition latency.
    /// </summary>
    private static readonly Func<DateTime> SystemClock = () => DateTime.Now;

    /// <summary>
    /// Guards every mutable field below. Held for the duration of a record, which is a handful of
    /// comparisons on already-extracted values — no channel property is read and no event is
    /// raised while it is held.
    /// </summary>
    private readonly object _lock = new object();

    /// <summary>
    /// Per-channel accumulators, keyed by channel type and number rather than by channel
    /// instance: a status message replaces the device's channel objects wholesale, and keying by
    /// instance would restart every channel's statistics each time that happened.
    /// </summary>
    private readonly Dictionary<(ChannelType Type, int Number), ChannelState> _channels =
        new Dictionary<(ChannelType Type, int Number), ChannelState>();

    private readonly Func<DateTime> _clock;

    /// <summary>
    /// The device this instance attached to, or <c>null</c> when it is fed by hand. Held only to
    /// unsubscribe from <see cref="IStreamingDevice.ChannelsPopulated"/> on disposal.
    /// </summary>
    private readonly IStreamingDevice? _device;

    /// <summary>
    /// The channels currently subscribed to. Replaced wholesale when the device repopulates.
    /// </summary>
    private IReadOnlyList<IChannel> _subscribed = Array.Empty<IChannel>();

    private bool _disposed;

    private DateTime _startedAt;
    private long _totalSampleCount;
    private DateTime _firstReceivedAt;
    private DateTime _lastReceivedAt;
    private long _minLatencyTicks;
    private long _maxLatencyTicks;

    /// <summary>
    /// Running total of per-sample latency, in ticks, as a <see cref="double"/> so a long
    /// acquisition cannot overflow the accumulator. Ticks are integral and far below 2^53 even
    /// for hours of streaming, so the sum stays exact.
    /// </summary>
    private double _latencyTicksSum;

    /// <summary>
    /// Creates an aggregator that records only what is handed to <see cref="Record(LiveSample)"/>
    /// or <see cref="Record(IChannel, IDataSample)"/>, subscribing to nothing.
    /// </summary>
    public AcquisitionStatistics()
        : this(null, null)
    {
    }

    /// <summary>
    /// Creates an aggregator attached to a device: every sample decoded on any of its channels is
    /// recorded until this instance is disposed.
    /// </summary>
    /// <remarks>
    /// Attaching does not start or stop streaming, and samples that arrived before it was created
    /// are not recovered — the measurement window begins here.
    /// </remarks>
    /// <param name="device">The device whose channels to observe.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <c>null</c>.</exception>
    public AcquisitionStatistics(IStreamingDevice device)
        : this(device ?? throw new ArgumentNullException(nameof(device)), null)
    {
    }

    /// <summary>
    /// Shared constructor; the <paramref name="clock"/> seam lets tests drive host-time readings
    /// (arrival rate, latency) deterministically instead of racing the wall clock.
    /// </summary>
    /// <param name="device">The device to attach to, or <c>null</c> to record only what is handed over.</param>
    /// <param name="clock">The host clock to read, or <c>null</c> for <see cref="DateTime.Now"/>.</param>
    internal AcquisitionStatistics(IStreamingDevice? device, Func<DateTime>? clock)
    {
        _clock = clock ?? SystemClock;
        _startedAt = _clock();
        _device = device;

        if (device == null)
        {
            return;
        }

        device.ChannelsPopulated += OnChannelsPopulated;
        Subscribe(device.GetChannelsSnapshot());
    }

    /// <summary>
    /// Takes a point-in-time reading of the current measurement window.
    /// </summary>
    /// <remarks>
    /// Allocates — it builds an immutable result — so poll it at a display cadence rather than per
    /// sample. Remains readable after disposal, so a consumer can detach and then report.
    /// </remarks>
    /// <returns>The statistics accumulated since construction or the last <see cref="Reset"/>.</returns>
    public AcquisitionStatisticsSnapshot Snapshot()
    {
        lock (_lock)
        {
            var channels = new List<ChannelAcquisitionStatistics>(_channels.Count);
            foreach (var pair in _channels)
            {
                var state = pair.Value;
                channels.Add(new ChannelAcquisitionStatistics(
                    pair.Key.Type,
                    pair.Key.Number,
                    state.Name,
                    state.SampleCount,
                    state.EarliestTimestamp,
                    state.LatestTimestamp,
                    state.FirstReceivedAt,
                    state.LastReceivedAt,
                    new TimeSpan(state.MinIntervalTicks),
                    new TimeSpan(state.MaxIntervalTicks),
                    state.OutOfOrderCount,
                    state.MinValue,
                    state.MaxValue,

                    // An entry exists only because a sample created it, and every sample
                    // increments ValueSampleCount, so it is never zero here.
                    state.ValueSum / state.ValueSampleCount,
                    state.Scaling?.Unit,
                    state.ValueSampleCount));
            }

            // Device order — analog first, then digital, ascending within each — so a caller
            // rendering these next to the device's own Channels collection sees the same order
            // rather than whatever the ChannelType enum happens to declare.
            channels.Sort(static (left, right) =>
            {
                var byType = TypeRank(left.ChannelType).CompareTo(TypeRank(right.ChannelType));
                return byType != 0
                    ? byType
                    : left.ChannelNumber.CompareTo(right.ChannelNumber);
            });

            var hasSamples = _totalSampleCount > 0;
            return new AcquisitionStatisticsSnapshot(
                _startedAt,
                _totalSampleCount,
                hasSamples ? _firstReceivedAt : DateTime.MinValue,
                hasSamples ? _lastReceivedAt : DateTime.MinValue,
                hasSamples ? new TimeSpan(_minLatencyTicks) : TimeSpan.Zero,
                hasSamples ? new TimeSpan(_maxLatencyTicks) : TimeSpan.Zero,
                hasSamples ? new TimeSpan((long)(_latencyTicksSum / _totalSampleCount)) : TimeSpan.Zero,
                channels);
        }
    }

    /// <summary>
    /// Discards everything accumulated so far and starts a new measurement window. Attachment is
    /// unaffected — an attached instance keeps recording into the new window.
    /// </summary>
    public void Reset()
    {
        var now = _clock();
        lock (_lock)
        {
            _channels.Clear();
            _startedAt = now;
            _totalSampleCount = 0;
            _firstReceivedAt = default;
            _lastReceivedAt = default;
            _minLatencyTicks = 0;
            _maxLatencyTicks = 0;
            _latencyTicksSum = 0.0;
        }
    }

    /// <summary>
    /// Records one sample from <see cref="DaqifiStreamingDevice.StreamSamplesAsync"/>.
    /// </summary>
    /// <param name="sample">The channel-attributed sample to record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sample"/> is <c>null</c>.</exception>
    public void Record(LiveSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        Record(sample.Channel, sample.Sample);
    }

    /// <summary>
    /// Records one sample against a channel.
    /// </summary>
    /// <remarks>
    /// Ignored once this instance has been disposed. Disposal exists to detach from a live device,
    /// and a sample already in flight on the decode thread when that happens must not turn into an
    /// exception thrown from an event handler on the decode path.
    /// </remarks>
    /// <param name="channel">The channel the sample belongs to.</param>
    /// <param name="sample">The sample to record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="channel"/> or <paramref name="sample"/> is <c>null</c>.</exception>
    public void Record(IChannel channel, IDataSample sample)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(sample);

        // Channel and sample members are read here rather than under the lock: the channel's
        // properties take the channel's own lock, and reaching for one while holding this one
        // would nest the two locks on the decode thread for no benefit.
        RecordCore(channel.Type, channel.ChannelNumber, channel.Name, sample.Timestamp,
            sample.ScaledValue, sample.Scaling);
    }

    /// <summary>
    /// Detaches from the device, if one was attached. The accumulated statistics remain readable.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_device != null)
            {
                _device.ChannelsPopulated -= OnChannelsPopulated;
            }

            foreach (var channel in _subscribed)
            {
                channel.SampleReceived -= OnSampleReceived;
            }

            _subscribed = Array.Empty<IChannel>();
        }
    }

    /// <summary>
    /// Folds the gap between one sample's timestamp and the previous one's into a channel's
    /// jitter bounds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A negative gap is not a gap. <c>TimestampProcessor</c> reconstructs a time that moves
    /// backwards when the device sends a frame out of order, and folding that in would report a
    /// negative minimum interval — a number that reads as jitter but is really a statement about
    /// frame ordering. It is counted instead, which is the same policy
    /// <see cref="TimestampGapDetector"/> already applies to non-positive deltas. Zero, on the
    /// other hand, is kept: firmware that stamps consecutive samples with the same tick value at
    /// high rates is telling the truth about its own clock.
    /// </para>
    /// <para>
    /// "Out of order" here means behind the channel's high-water mark, not merely behind the
    /// sample recorded immediately before — so a run of samples that all sit behind it is counted
    /// once each. That is the definition
    /// <see cref="ChannelAcquisitionStatistics.OutOfOrderSampleCount"/> documents, and the one
    /// that keeps a rewind from being followed by a compensating forward jump that would be
    /// reported as a gap.
    /// </para>
    /// </remarks>
    /// <param name="state">The channel's accumulators.</param>
    /// <param name="intervalTicks">
    /// The gap since the furthest-advanced timestamp seen on this channel, which is negative
    /// exactly when this sample's timestamp precedes it.
    /// </param>
    private static void RecordInterval(ChannelState state, long intervalTicks)
    {
        if (intervalTicks < 0)
        {
            state.OutOfOrderCount++;
            return;
        }

        if (state.ForwardIntervalCount == 0)
        {
            state.MinIntervalTicks = intervalTicks;
            state.MaxIntervalTicks = intervalTicks;
        }
        else if (intervalTicks < state.MinIntervalTicks)
        {
            state.MinIntervalTicks = intervalTicks;
        }
        else if (intervalTicks > state.MaxIntervalTicks)
        {
            state.MaxIntervalTicks = intervalTicks;
        }

        state.ForwardIntervalCount++;
    }

    /// <summary>
    /// Orders channel types the way a device lists its channels: analog first, then digital.
    /// </summary>
    private static int TypeRank(ChannelType type) => type == ChannelType.Analog ? 0 : 1;

    /// <summary>
    /// Folds one sample into the accumulators.
    /// </summary>
    /// <param name="type">The sample's channel type.</param>
    /// <param name="number">The sample's channel number.</param>
    /// <param name="name">The channel's name as it reads now.</param>
    /// <param name="timestamp">The sample's device-derived timestamp.</param>
    /// <param name="value">
    /// The sample's value in the unit the channel is configured to report -- <c>ScaledValue</c>,
    /// not <c>Value</c>. The word "scaled" is overloaded here: <c>IDataSample.Value</c> is
    /// already calibration-scaled (volts), while <c>ScaledValue</c> additionally applies the
    /// channel's <c>ChannelScaling</c> (e.g. PSI). These statistics report the latter, so they
    /// mean what the channel was configured to mean (issue #534).
    /// </param>
    /// <param name="scaling">
    /// The transform <paramref name="value"/> was produced by, or <c>null</c> when the channel
    /// has none. Kept so a consumer can tell volts from engineering units -- without it the
    /// snapshot reports three bare numbers whose meaning cannot be recovered, because it has
    /// already consumed the samples they came from -- and so a change of scaling mid-window can
    /// be detected.
    /// </param>
    private void RecordCore(ChannelType type, int number, string name, DateTime timestamp,
        double value, ChannelScaling? scaling)
    {
        var now = _clock();

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var key = (type, number);
            if (!_channels.TryGetValue(key, out var state))
            {
                state = new ChannelState();
                _channels[key] = state;
            }

            state.Name = name;

            // A channel's scaling can be reassigned mid-session -- IScaledChannel documents it
            // ("every sample decoded from then on carries it"), and samples keep the scaling
            // that was in force when they were decoded. So one window can genuinely contain
            // samples in two different units.
            //
            // Aggregating across that is meaningless: a min in PSI and a max in Bar are not
            // extremes of anything, and reporting only the newest unit would put a label on
            // them that is actively false. When the scaling changes, the VALUE accumulators
            // restart from this sample, so the three value figures always describe exactly one
            // scaling. ValueSampleCount says how many samples that is, so the restart is
            // visible rather than an unexplained gap against SampleCount.
            //
            // Compared on the whole scaling, not just the unit: a gain change that keeps the
            // unit still makes earlier readings incomparable. ChannelScaling is a record, so
            // this is structural equality.
            var scalingChanged = state.ValueSampleCount > 0 && !Equals(scaling, state.Scaling);
            state.Scaling = scaling;

            // Timing, ordering and count statistics are unaffected by scaling and continue
            // across the change untouched.

            if (state.ValueSampleCount == 0 || scalingChanged)
            {
                // Seeded from the first sample under this scaling, not left at zero: a channel
                // that never reads anywhere near zero must not report zero as its extreme.
                state.MinValue = value;
                state.MaxValue = value;
                state.ValueSum = 0.0;
                state.ValueSampleCount = 0;
            }

            if (state.SampleCount == 0)
            {
                state.EarliestTimestamp = timestamp;
                state.LatestTimestamp = timestamp;
                state.FirstReceivedAt = now;
            }
            else
            {
                if (value < state.MinValue)
                {
                    state.MinValue = value;
                }

                if (value > state.MaxValue)
                {
                    state.MaxValue = value;
                }

                // Measured against the furthest-advanced timestamp seen, not against whichever
                // sample happened to be recorded last. A stale frame rewinds the clock and the
                // frame after it jumps forward again to roughly where it was; measuring that
                // jump from the rewound value would report the whole rewind as a gap, which is
                // an artifact of the reordering rather than a stall in the data.
                RecordInterval(state, timestamp.Ticks - state.LatestTimestamp.Ticks);

                // Extremes rather than first-and-last, so the span the device-clock rate is
                // measured over survives a timestamp that moves backwards. Identical to
                // first-and-last whenever the device's timestamps advance, which is the norm.
                if (timestamp < state.EarliestTimestamp)
                {
                    state.EarliestTimestamp = timestamp;
                }

                if (timestamp > state.LatestTimestamp)
                {
                    state.LatestTimestamp = timestamp;
                }
            }

            state.ValueSum += value;
            state.ValueSampleCount++;
            state.LastReceivedAt = now;
            state.SampleCount++;

            var latencyTicks = now.Ticks - timestamp.Ticks;
            if (_totalSampleCount == 0)
            {
                _firstReceivedAt = now;
                _minLatencyTicks = latencyTicks;
                _maxLatencyTicks = latencyTicks;
            }
            else
            {
                if (latencyTicks < _minLatencyTicks)
                {
                    _minLatencyTicks = latencyTicks;
                }

                if (latencyTicks > _maxLatencyTicks)
                {
                    _maxLatencyTicks = latencyTicks;
                }
            }

            _latencyTicksSum += latencyTicks;
            _lastReceivedAt = now;
            _totalSampleCount++;
        }
    }

    /// <summary>
    /// Moves the subscription to the channel objects a status message has just installed.
    /// </summary>
    private void OnChannelsPopulated(object? sender, ChannelsPopulatedEventArgs e) =>
        Subscribe(e.Channels);

    /// <summary>
    /// Subscribes to a set of channels, dropping whatever was subscribed before.
    /// </summary>
    /// <param name="channels">
    /// A snapshot of the device's channels. Both callers hand over a copy the device has already
    /// taken under its own lock, so it is safe to hold on to.
    /// </param>
    private void Subscribe(IReadOnlyList<IChannel> channels)
    {
        // Swap and resubscribe as one step. Two populations racing each other could otherwise
        // interleave into a channel that is subscribed twice and never fully unsubscribed —
        // every sample on it counted twice, for the rest of the session. Safe to hold across the
        // event accessors: they are field-like events, so they take no lock of their own, and a
        // sample arriving concurrently has already read everything it needs off its channel
        // before it reaches for this lock.
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var channel in _subscribed)
            {
                channel.SampleReceived -= OnSampleReceived;
            }

            _subscribed = channels;

            foreach (var channel in channels)
            {
                channel.SampleReceived += OnSampleReceived;
            }
        }
    }

    /// <summary>
    /// The decode path's entry point into this aggregator. Records unconditionally: what a sample
    /// is worth measuring is the caller's business, not this handler's.
    /// </summary>
    private void OnSampleReceived(object? sender, SampleReceivedEventArgs e) =>
        RecordCore(e.Channel.Type, e.Channel.ChannelNumber, e.Channel.Name, e.Sample.Timestamp,
            e.Sample.ScaledValue, e.Sample.Scaling);

    /// <summary>
    /// One channel's mutable accumulators. A class rather than a struct so it can be updated in
    /// place through a dictionary lookup, which is what keeps recording allocation-free.
    /// </summary>
    private sealed class ChannelState
    {
        internal string Name = string.Empty;
        internal ChannelScaling? Scaling;
        internal long ValueSampleCount;
        internal long SampleCount;

        /// <summary>The extremes of the timestamps seen, which the device-clock span is measured over.</summary>
        internal DateTime EarliestTimestamp;
        internal DateTime LatestTimestamp;

        internal DateTime FirstReceivedAt;
        internal DateTime LastReceivedAt;
        internal long MinIntervalTicks;
        internal long MaxIntervalTicks;

        /// <summary>
        /// How many non-negative intervals have been folded in; zero means the bounds above are
        /// unseeded. Not the same as <see cref="SampleCount"/> minus one once
        /// <see cref="OutOfOrderCount"/> is non-zero.
        /// </summary>
        internal long ForwardIntervalCount;

        internal long OutOfOrderCount;
        internal double MinValue;
        internal double MaxValue;
        internal double ValueSum;
    }
}
