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
    public ulong? QueueDroppedSamples => GetValue("QueueDroppedSamples");

    /// <summary>
    /// Gets the number of streaming timer ISR entries this session, or <see langword="null"/> if absent.
    /// In a healthy session this equals <see cref="TotalSamplesStreamed"/> + <see cref="QueueDroppedSamples"/>.
    /// </summary>
    public ulong? TimerISRCalls => GetValue("TimerISRCalls");

    private ulong? GetValue(string key) =>
        Values.TryGetValue(key, out var value) ? value : null;
}
