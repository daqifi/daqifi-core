using System.Collections.Generic;

namespace Daqifi.Core.Device.Diagnostics;

/// <summary>
/// Device memory diagnostics returned by the <c>SYSTem:MEMory:FREE?</c> SCPI query.
/// </summary>
/// <remarks>
/// The firmware emits a set of <c>Key=Value</c> lines describing FreeRTOS heap usage and the
/// coherent/sample memory pools. The exact field set varies by firmware version; all parsed pairs
/// are available via <see cref="Values"/>, and the typed properties are convenience accessors that
/// return <see langword="null"/> when the field is absent from the response.
/// </remarks>
public sealed record MemoryDiagnostics
{
    /// <summary>
    /// Gets all parsed <c>Key=Value</c> fields from the response, keyed by field name.
    /// </summary>
    public required IReadOnlyDictionary<string, ulong> Values { get; init; }

    /// <summary>
    /// Gets the total FreeRTOS heap size in bytes, or <see langword="null"/> if absent.
    /// </summary>
    public ulong? HeapTotal => GetValue("HeapTotal");

    /// <summary>
    /// Gets the currently free heap in bytes, or <see langword="null"/> if absent.
    /// </summary>
    public ulong? HeapFree => GetValue("HeapFree");

    /// <summary>
    /// Gets the currently used heap in bytes, or <see langword="null"/> if absent.
    /// </summary>
    public ulong? HeapUsed => GetValue("HeapUsed");

    /// <summary>
    /// Gets the lowest free-heap watermark observed since boot, in bytes, or <see langword="null"/> if absent.
    /// </summary>
    public ulong? HeapMinEverFree => GetValue("HeapMinEverFree");

    /// <summary>
    /// Gets the total size of the coherent DMA pool in bytes, or <see langword="null"/> if absent.
    /// </summary>
    public ulong? CoherentPoolTotal => GetValue("CoherentPoolTotal");

    /// <summary>
    /// Gets the free space in the coherent DMA pool in bytes, or <see langword="null"/> if absent.
    /// </summary>
    public ulong? CoherentPoolFree => GetValue("CoherentPoolFree");

    /// <summary>
    /// Gets the capacity of the analog-input sample pool (number of elements), or <see langword="null"/> if absent.
    /// </summary>
    public ulong? SamplePoolCount => GetValue("SamplePoolCount");

    /// <summary>
    /// Gets the number of sample-pool elements currently in use, or <see langword="null"/> if absent.
    /// </summary>
    public ulong? SamplePoolInUse => GetValue("SamplePoolInUse");

    /// <summary>
    /// Gets the peak number of sample-pool elements used since boot, or <see langword="null"/> if absent.
    /// </summary>
    public ulong? SamplePoolMaxUsed => GetValue("SamplePoolMaxUsed");

    private ulong? GetValue(string key) =>
        Values.TryGetValue(key, out var value) ? value : null;
}
