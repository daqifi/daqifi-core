using System.Collections.Generic;

namespace Daqifi.Core.Device.Diagnostics;

/// <summary>
/// Streaming performance counters returned by the <c>SYSTem:STReam:STATS?</c> SCPI query.
/// </summary>
/// <remarks>
/// The firmware emits a set of <c>Key=Value</c> lines whose exact membership varies by firmware
/// version (40+ fields covering queue/pool drops, per-transport dropped bytes, SD write metrics,
/// encoder failures, and timer ISR accounting). All parsed pairs are available via
/// <see cref="Values"/>; the typed properties are convenience accessors for the most commonly used
/// headline counters and return <see langword="null"/> when the field is absent from the response.
/// </remarks>
public sealed record StreamStats
{
    /// <summary>
    /// Gets all parsed <c>Key=Value</c> counters from the response, keyed by field name.
    /// </summary>
    public required IReadOnlyDictionary<string, ulong> Values { get; init; }

    /// <summary>
    /// Gets the total number of samples successfully queued from the acquisition ISR this session,
    /// or <see langword="null"/> if absent.
    /// </summary>
    public ulong? TotalSamplesStreamed => GetValue("TotalSamplesStreamed");

    /// <summary>
    /// Gets the total number of bytes encoded/offered to the output transport this session,
    /// or <see langword="null"/> if absent.
    /// </summary>
    public ulong? TotalBytesStreamed => GetValue("TotalBytesStreamed");

    /// <summary>
    /// Gets the number of samples dropped because the streaming queue or sample pool could not keep
    /// up, or <see langword="null"/> if absent.
    /// </summary>
    /// <remarks>
    /// This is the sum of <see cref="PoolExhaustedSamples"/> and <see cref="QueueOverflowSamples"/>
    /// on firmware that reports the split; never add those two to it.
    /// </remarks>
    public ulong? QueueDroppedSamples => GetValue("QueueDroppedSamples");

    /// <summary>
    /// Gets the part of <see cref="QueueDroppedSamples"/> lost because the sample pool had no free
    /// slot (the pool is too shallow for the configured rate), or <see langword="null"/> if absent.
    /// </summary>
    public ulong? PoolExhaustedSamples => GetValue("PoolExhaustedSamples");

    /// <summary>
    /// Gets the part of <see cref="QueueDroppedSamples"/> lost because the streaming queue was full
    /// while the pool still had slots (the firmware's streaming task drained too slowly), or
    /// <see langword="null"/> if absent.
    /// </summary>
    public ulong? QueueOverflowSamples => GetValue("QueueOverflowSamples");

    /// <summary>
    /// Gets the number of streaming timer ISR entries this session, or <see langword="null"/> if absent.
    /// In a healthy session this equals <see cref="TotalSamplesStreamed"/> + <see cref="QueueDroppedSamples"/>.
    /// </summary>
    public ulong? TimerISRCalls => GetValue("TimerISRCalls");

    /// <summary>
    /// Gets the number of ticks after the startup grace period where the shared ADC scan had not
    /// finished by the next trigger, so the sample went out with stale data, or
    /// <see langword="null"/> if absent (firmware before 3.9.0).
    /// </summary>
    /// <remarks>
    /// The sample was still streamed, so the firmware does not count it in
    /// <see cref="QueueDroppedSamples"/> or <see cref="PreciseSampleLossPercent"/>. Expected to be about 0;
    /// a rising value means the configured rate is above what the scan can sustain.
    /// </remarks>
    public ulong? ScanStaleDroppedSteady => GetValue("ScanStaleDroppedSteady");

    /// <summary>
    /// Gets the longest single pass, in nanoseconds, of the firmware's per-tick channel read loop
    /// this session, or <see langword="null"/> if absent.
    /// </summary>
    /// <remarks>
    /// Reported from firmware 3.9.0 by builds with the read-loop profiler compiled in (the default).
    /// This is the ADC-side cost behind the device's maximum streaming rate.
    /// </remarks>
    public ulong? ReadLoopMaxNs => GetValue("ReadLoopMaxNs");

    /// <summary>
    /// Gets the mean time, in nanoseconds, of the firmware's per-tick channel read loop this session,
    /// or <see langword="null"/> if absent.
    /// </summary>
    /// <remarks>
    /// Covers every enabled channel in one tick; divide by the enabled channel count for a
    /// per-channel figure. Present under the same conditions as <see cref="ReadLoopMaxNs"/>.
    /// </remarks>
    public ulong? ReadLoopMeanNs => GetValue("ReadLoopMeanNs");

    /// <summary>
    /// Gets the bytes by which WiFi send completions confirmed more than was sent, or
    /// <see langword="null"/> if absent (firmware before 3.9.0).
    /// </summary>
    /// <remarks>
    /// A firmware bookkeeping diagnostic, not stream damage: non-zero means the transmit ring
    /// mis-paired a completion. It is the opposite direction of <c>WifiPartialBytesMissing</c>.
    /// </remarks>
    public ulong? WifiTcpOverBytesExtra => GetValue("WifiTcpOverBytesExtra");

    /// <summary>
    /// Gets the number of WiFi flush attempts the firmware refused because the in-flight cap was
    /// reached, or <see langword="null"/> if absent (firmware before 3.9.0).
    /// </summary>
    /// <remarks>
    /// Not loss: the refused bytes are retried. Non-zero is expected under WiFi streaming load and
    /// 0 on an idle connection.
    /// </remarks>
    public ulong? WifiTcpInflightOverflow => GetValue("WifiTcpInflightOverflow");

    /// <summary>
    /// Gets the percentage of samples the firmware dropped this session, unrounded, or
    /// <see langword="null"/> if <see cref="TotalSamplesStreamed"/> or
    /// <see cref="QueueDroppedSamples"/> is absent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use this, not the firmware's own <c>SampleLossPercent</c> field in <see cref="Values"/>.
    /// That field is a whole number that rounds down, so any loss under 1% reads 0: a WiFi CSV
    /// stream that dropped 0.44% of its samples reports <c>SampleLossPercent=0</c>.
    /// </para>
    /// <para>
    /// Computed exactly as the firmware computes <c>SampleLossPercent</c>, without the rounding:
    /// <see cref="QueueDroppedSamples"/> / (<see cref="TotalSamplesStreamed"/> +
    /// <see cref="QueueDroppedSamples"/>) × 100. <see cref="PoolExhaustedSamples"/> and
    /// <see cref="QueueOverflowSamples"/> are already inside <see cref="QueueDroppedSamples"/>, and
    /// stale-scan samples (<see cref="ScanStaleDroppedSteady"/>) were still streamed, so none of
    /// them is added again. Returns 0 when no sample was attempted.
    /// </para>
    /// </remarks>
    public double? PreciseSampleLossPercent
    {
        get
        {
            if (TotalSamplesStreamed is not { } streamed || QueueDroppedSamples is not { } dropped)
            {
                return null;
            }

            // In double, not ulong: the sum of two 64-bit counters can wrap.
            var attempted = (double)streamed + dropped;
            return attempted == 0 ? 0.0 : dropped * 100.0 / attempted;
        }
    }

    private ulong? GetValue(string key) =>
        Values.TryGetValue(key, out var value) ? value : null;
}
