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
    /// <param name="FirstSampleTimestamp">
    /// The <see cref="IDataSample.Timestamp"/> of the first sample recorded — device time, not
    /// arrival time.
    /// </param>
    /// <param name="LastSampleTimestamp">The <see cref="IDataSample.Timestamp"/> of the most recent sample recorded.</param>
    /// <param name="FirstReceivedAt">The host clock reading when the first sample was recorded.</param>
    /// <param name="LastReceivedAt">The host clock reading when the most recent sample was recorded.</param>
    /// <param name="MinSampleInterval">
    /// The smallest gap between consecutive sample timestamps (device clock). Firmware that stamps
    /// several samples with the same tick value reports <see cref="TimeSpan.Zero"/> here, which is a
    /// true statement about the timestamps rather than a defect in the measurement.
    /// </param>
    /// <param name="MaxSampleInterval">
    /// The largest gap between consecutive sample timestamps (device clock) — the jitter figure that
    /// matters, since a single stalled interval is what a dropped block of samples looks like.
    /// </param>
    /// <param name="MinValue">The smallest scaled value seen, seeded from the first sample.</param>
    /// <param name="MaxValue">The largest scaled value seen, seeded from the first sample.</param>
    /// <param name="MeanValue">The arithmetic mean of every scaled value seen in the window.</param>
    public sealed record ChannelAcquisitionStatistics(
        ChannelType ChannelType,
        int ChannelNumber,
        string Name,
        long SampleCount,
        DateTime FirstSampleTimestamp,
        DateTime LastSampleTimestamp,
        DateTime FirstReceivedAt,
        DateTime LastReceivedAt,
        TimeSpan MinSampleInterval,
        TimeSpan MaxSampleInterval,
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
        /// <c>(SampleCount - 1) / (LastSampleTimestamp - FirstSampleTimestamp)</c>.
        /// </summary>
        /// <remarks>
        /// Compare against <see cref="MeasuredSampleRateHz"/>. The two agreeing but sitting below the
        /// commanded rate means samples went missing; the two disagreeing means the device's clock and
        /// real time have parted company, and it is the measured rate that describes what the host got.
        /// </remarks>
        public double DeviceClockSampleRateHz => Rate(SampleCount, LastSampleTimestamp - FirstSampleTimestamp);

        /// <summary>
        /// Gets the mean gap between consecutive sample timestamps (device clock) — the exact mean of
        /// the intervals <see cref="MinSampleInterval"/> and <see cref="MaxSampleInterval"/> bound,
        /// since consecutive intervals telescope to <c>(Last - First) / (SampleCount - 1)</c>.
        /// </summary>
        public TimeSpan MeanSampleInterval => SampleCount > 1
            ? (LastSampleTimestamp - FirstSampleTimestamp) / (SampleCount - 1)
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
