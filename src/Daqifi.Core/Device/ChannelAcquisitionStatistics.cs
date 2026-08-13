using System;
using Daqifi.Core.Channel;

namespace Daqifi.Core.Device
{
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
    /// <param name="ChannelType">The type of the channel these statistics describe.</param>
    /// <param name="ChannelNumber">The channel number these statistics describe.</param>
    /// <param name="Name">
    /// The channel's name as it read when the most recent sample was recorded. Names are mutable, so
    /// this is the last observed value rather than an identity — <paramref name="ChannelType"/> plus
    /// <paramref name="ChannelNumber"/> is what a channel is tracked by, which is also what keeps a
    /// channel's statistics intact when a status message replaces the channel objects mid-session.
    /// </param>
    /// <param name="SampleCount">The number of samples recorded for this channel in the window.</param>
    /// <param name="EarliestSampleTimestamp">
    /// The earliest <see cref="IDataSample.Timestamp"/> seen — device time, not arrival time. The
    /// timestamp of the first sample recorded unless the device sent one out of order.
    /// </param>
    /// <param name="LatestSampleTimestamp">
    /// The latest <see cref="IDataSample.Timestamp"/> seen, likewise the most recent one recorded
    /// unless the device sent one out of order.
    /// </param>
    /// <param name="FirstReceivedAt">The host clock reading when the first sample was recorded.</param>
    /// <param name="LastReceivedAt">The host clock reading when the most recent sample was recorded.</param>
    /// <param name="MinSampleInterval">
    /// The smallest gap between consecutive sample timestamps (device clock). Firmware that stamps
    /// several samples with the same tick value reports <see cref="TimeSpan.Zero"/> here, which is a
    /// true statement about the timestamps rather than a defect in the measurement. Backwards steps
    /// are excluded — they are counted by <see cref="OutOfOrderSampleCount"/> instead, since a
    /// negative number is not a gap.
    /// </param>
    /// <param name="MaxSampleInterval">
    /// The largest gap between consecutive sample timestamps (device clock) — the jitter figure that
    /// matters, since a single stalled interval is what a dropped block of samples looks like.
    /// </param>
    /// <param name="OutOfOrderSampleCount">
    /// How many samples carried a timestamp earlier than the sample before them. Normally zero; a
    /// non-zero value means the device's timestamps did not advance monotonically, so every
    /// device-clock figure here describes a stream that moved backwards at some point and should be
    /// read as approximate. The host-clock figures are unaffected.
    /// </param>
    /// <param name="MinValue">The smallest scaled value seen, seeded from the first sample.</param>
    /// <param name="MaxValue">The largest scaled value seen, seeded from the first sample.</param>
    /// <param name="MeanValue">The arithmetic mean of every scaled value seen in the window.</param>
    public sealed record ChannelAcquisitionStatistics(
        ChannelType ChannelType,
        int ChannelNumber,
        string Name,
        long SampleCount,
        DateTime EarliestSampleTimestamp,
        DateTime LatestSampleTimestamp,
        DateTime FirstReceivedAt,
        DateTime LastReceivedAt,
        TimeSpan MinSampleInterval,
        TimeSpan MaxSampleInterval,
        long OutOfOrderSampleCount,
        double MinValue,
        double MaxValue,
        double MeanValue)
    {
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
}
