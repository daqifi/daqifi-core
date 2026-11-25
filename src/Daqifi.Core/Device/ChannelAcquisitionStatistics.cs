using System;
using Daqifi.Core.Channel;

namespace Daqifi.Core.Device;

/// <summary>
/// Host-observed acquisition statistics for a single channel over one measurement window — the
/// per-channel half of <see cref="AcquisitionStatisticsSnapshot"/>. Immutable; taking a new
/// snapshot produces a new instance.
/// </summary>
/// <remarks>
/// <para>
/// Two clocks are reported side by side, and the difference between them is the point.
/// <see cref="DeviceClockSampleRateHz"/> is derived from the timestamps the device itself put on
/// the samples (<see cref="IDataSample.Timestamp"/>, reconstructed from the device tick counter);
/// <see cref="MeasuredSampleRateHz"/> is derived from when the host actually received them. Data
/// the host never received depresses both, so either one answers "am I losing samples?" — but
/// only their disagreement can tell you that the device's own clock is not keeping real time,
/// which no amount of device-reported bookkeeping can reveal on its own.
/// </para>
/// <para>
/// A window with a single sample has no interval and no rate to report: every rate is 0 and every
/// interval is <see cref="TimeSpan.Zero"/>, since <c>n - 1</c> intervals exist for <c>n</c>
/// samples.
/// </para>
/// <para>
/// Device timestamps are not guaranteed to advance. <c>TimestampProcessor</c> deliberately
/// reconstructs a time that moves <em>backwards</em> when the device sends a frame out of order,
/// so the device-clock figures here are built to survive that: the span they are measured over
/// runs between the earliest and latest timestamps seen rather than the first and last recorded,
/// which is the same thing for a well-behaved stream but cannot collapse to zero or go negative
/// for a misbehaving one. <see cref="OutOfOrderSampleCount"/> says whether it happened at all.
/// </para>
/// </remarks>
public sealed record ChannelAcquisitionStatistics
{
    /// <summary>
    /// Gets the type of the channel these statistics describe.
    /// </summary>
    public required ChannelType ChannelType { get; init; }

    /// <summary>
    /// Gets the channel number these statistics describe.
    /// </summary>
    public required int ChannelNumber { get; init; }

    /// <summary>
    /// Gets the channel's name as it read when the most recent sample was recorded. Names are mutable, so
    /// this is the last observed value rather than an identity — <see cref="ChannelType"/> plus
    /// <see cref="ChannelNumber"/> is what a channel is tracked by, which is also what keeps a
    /// channel's statistics intact when a status message replaces the channel objects mid-session.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the number of samples recorded for this channel in the window.
    /// </summary>
    public required long SampleCount { get; init; }

    /// <summary>
    /// Gets the earliest <see cref="IDataSample.Timestamp"/> seen — device time, not arrival time. The
    /// timestamp of the first sample recorded unless the device sent one out of order.
    /// </summary>
    public required DateTime EarliestSampleTimestamp { get; init; }

    /// <summary>
    /// Gets the latest <see cref="IDataSample.Timestamp"/> seen, likewise the most recent one recorded
    /// unless the device sent one out of order.
    /// </summary>
    public required DateTime LatestSampleTimestamp { get; init; }

    /// <summary>
    /// Gets the host clock reading when the first sample was recorded.
    /// </summary>
    public required DateTime FirstReceivedAt { get; init; }

    /// <summary>
    /// Gets the host clock reading when the most recent sample was recorded.
    /// </summary>
    public required DateTime LastReceivedAt { get; init; }

    /// <summary>
    /// Gets the smallest gap between a sample's timestamp and the latest timestamp seen before it (device
    /// clock). Firmware that stamps several samples with the same tick value reports
    /// <see cref="TimeSpan.Zero"/> here, which is a true statement about the timestamps rather than a
    /// defect in the measurement. Samples counted by <see cref="OutOfOrderSampleCount"/> contribute
    /// no interval at all, since a negative number is not a gap.
    /// </summary>
    public required TimeSpan MinSampleInterval { get; init; }

    /// <summary>
    /// Gets the largest such gap — the jitter figure that matters, since a single stalled interval is what
    /// a dropped block of samples looks like.
    /// </summary>
    public required TimeSpan MaxSampleInterval { get; init; }

    /// <summary>
    /// Gets how many samples carried a timestamp earlier than the latest one already seen on this channel.
    /// Normally zero; a non-zero value means the device's timestamps did not advance monotonically,
    /// so every device-clock figure here describes a stream that moved backwards at some point and
    /// should be read as approximate. The host-clock figures are unaffected.
    /// </summary>
    public required long OutOfOrderSampleCount { get; init; }

    /// <summary>
    /// Gets the smallest value seen, seeded from the first sample, in <see cref="Unit"/>.
    /// </summary>
    public required double MinValue { get; init; }

    /// <summary>
    /// Gets the largest value seen, seeded from the first sample, in <see cref="Unit"/>.
    /// </summary>
    public required double MaxValue { get; init; }

    /// <summary>
    /// Gets the arithmetic mean of every value seen in the window, in <see cref="Unit"/>.
    /// </summary>
    public required double MeanValue { get; init; }

    /// <summary>
    /// Gets the unit the three value figures are expressed in — the channel's configured engineering
    /// unit (e.g. <c>"PSI"</c>) when it has one, otherwise whatever its scaling states, or
    /// <c>null</c> when unstated.
    /// </summary>
    /// <remarks>
    /// It is here because without it the three numbers cannot be interpreted and cannot be
    /// converted after the fact. This type is the only place in Core that consumes samples on the
    /// consumer's behalf and returns just derived figures — every other path hands over the sample,
    /// so a caller can read <c>ScaledValue</c> and <c>Unit</c> itself. A negative gain also
    /// transposes the extremes, so re-applying a scaling by hand is not a safe workaround
    /// (issue #534).
    /// </remarks>
    public string? Unit { get; init; }

    /// <summary>
    /// Gets how many samples the three value figures above were computed from. Normally equal to
    /// <see cref="SampleCount"/>.
    /// </summary>
    /// <remarks>
    /// It differs when the channel's scaling was reassigned mid-window. Aggregating across that is
    /// meaningless — a minimum in PSI and a maximum in Bar are not the extremes of anything — so
    /// the value figures restart at the change and describe only the samples taken under the
    /// scaling <see cref="Unit"/> names. This field is what makes that restart visible
    /// instead of an unexplained gap. The timing, ordering and count statistics are unaffected by
    /// scaling and continue across it.
    /// </remarks>
    public long ValueSampleCount { get; init; }

    /// <summary>
    /// Gets the sample rate the host actually received this channel at, in Hz, measured against
    /// the host clock: <c>(SampleCount - 1) / (LastReceivedAt - FirstReceivedAt)</c>.
    /// </summary>
    /// <remarks>
    /// This is the answer to "am I really getting 1 kHz?". It is measured over the whole window,
    /// so transport batching — a USB or WiFi stack handing over several frames at once — averages
    /// out rather than showing up as a rate error, but genuinely missing samples do not.
    /// </remarks>
    public double MeasuredSampleRateHz => Rate(SampleCount, LastReceivedAt - FirstReceivedAt);

    /// <summary>
    /// Gets the sample rate the device's own timestamps claim, in Hz:
    /// <c>(SampleCount - 1) / (LatestSampleTimestamp - EarliestSampleTimestamp)</c>.
    /// </summary>
    /// <remarks>
    /// Compare against <see cref="MeasuredSampleRateHz"/>. The two agreeing but sitting below the
    /// commanded rate means samples went missing; the two disagreeing means the device's clock and
    /// real time have parted company, and it is the measured rate that describes what the host got.
    /// </remarks>
    public double DeviceClockSampleRateHz => Rate(SampleCount, LatestSampleTimestamp - EarliestSampleTimestamp);

    /// <summary>
    /// Gets the mean gap between consecutive sample timestamps (device clock) — the exact mean of
    /// the intervals <see cref="MinSampleInterval"/> and <see cref="MaxSampleInterval"/> bound,
    /// since consecutive intervals telescope to
    /// <c>(Latest - Earliest) / (SampleCount - 1)</c>. Approximate rather than exact once
    /// <see cref="OutOfOrderSampleCount"/> is non-zero, because a timestamp that moved backwards
    /// is still counted in the denominator.
    /// </summary>
    public TimeSpan MeanSampleInterval => SampleCount > 1
        ? (LatestSampleTimestamp - EarliestSampleTimestamp) / (SampleCount - 1)
        : TimeSpan.Zero;

    /// <summary>
    /// Samples per second over a span, or 0 when there is no interval to measure over.
    /// </summary>
    /// <remarks>
    /// <c>SampleCount - 1</c> rather than <c>SampleCount</c>: the span runs from the first sample
    /// to the last, which encloses one fewer interval than there are samples. Counting samples
    /// instead would over-report the rate, most visibly on short windows.
    /// </remarks>
    internal static double Rate(long sampleCount, TimeSpan span) =>
        sampleCount > 1 && span.Ticks > 0
            ? (sampleCount - 1) / span.TotalSeconds
            : 0.0;
}
