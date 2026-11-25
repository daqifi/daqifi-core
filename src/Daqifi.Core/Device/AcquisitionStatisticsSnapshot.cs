using System;
using System.Collections.Generic;

namespace Daqifi.Core.Device;

/// <summary>
/// A point-in-time reading of an <see cref="AcquisitionStatistics"/> measurement window: the
/// device-wide totals plus one <see cref="ChannelAcquisitionStatistics"/> per channel that has
/// produced a sample. Immutable, and detached from the aggregator that produced it — samples
/// recorded after the snapshot was taken do not change it.
/// </summary>
public sealed record AcquisitionStatisticsSnapshot
{
    /// <summary>
    /// Gets the host clock reading when this measurement window began — construction, or the most recent
    /// <see cref="AcquisitionStatistics.Reset"/>. Present even when no samples arrived, so "nothing
    /// came in for the last 30 seconds" is expressible.
    /// </summary>
    public required DateTime StartedAt { get; init; }

    /// <summary>
    /// Gets the number of samples recorded across every channel.
    /// </summary>
    public required long TotalSampleCount { get; init; }

    /// <summary>
    /// Gets the host clock reading when the first sample of the window was recorded, or
    /// <see cref="DateTime.MinValue"/> when none were.
    /// </summary>
    public required DateTime FirstReceivedAt { get; init; }

    /// <summary>
    /// Gets the host clock reading when the most recent sample was recorded, or
    /// <see cref="DateTime.MinValue"/> when none were.
    /// </summary>
    public required DateTime LastReceivedAt { get; init; }

    /// <summary>
    /// Gets the smallest observed latency (see <see cref="MeanLatency"/>).
    /// </summary>
    public required TimeSpan MinimumLatency { get; init; }

    /// <summary>
    /// Gets the largest observed latency (see <see cref="MeanLatency"/>).
    /// </summary>
    public required TimeSpan MaximumLatency { get; init; }

    /// <summary>
    /// Gets the mean of <c>received-at minus sample timestamp</c> across every recorded sample: how far
    /// behind the device's own account of when a sample was taken the host was when it saw it.
    /// Because the device timestamp is reconstructed from an anchor taken at the first frame of the
    /// session, this measures transport delay <em>plus</em> accumulated drift between the two clocks,
    /// and it can legitimately be negative if the device clock outruns the host's.
    /// </summary>
    public required TimeSpan MeanLatency { get; init; }

    /// <summary>
    /// Per-channel statistics in the device's own channel order — analog channels first, then
    /// digital, ascending by channel number within each. A channel that produced no samples in this
    /// window is absent rather than present-and-empty.
    /// </summary>
    public required IReadOnlyList<ChannelAcquisitionStatistics> Channels { get; init; }

    /// <summary>
    /// Gets the host-clock span from the first recorded sample to the most recent one, or
    /// <see cref="TimeSpan.Zero"/> when no samples were recorded.
    /// </summary>
    /// <remarks>
    /// Measured between samples rather than from <see cref="StartedAt"/>, so it describes how long
    /// data has been flowing rather than how long the aggregator has existed.
    /// </remarks>
    public TimeSpan Duration => TotalSampleCount > 0
        ? LastReceivedAt - FirstReceivedAt
        : TimeSpan.Zero;
}
