using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Resolves a <see cref="System.IO.Stream"/> or file path into the (line-source, total-bytes)
/// pair that a text-based SD card log parser's session builder needs.
/// </summary>
/// <remarks>
/// The CSV and JSON log parsers each read the same kind of text log and previously carried an
/// identical copy of this stream/file resolution — including the fallback that buffers a
/// forward-only stream up front because it can't be re-read; it now lives here once so the two
/// formats can't drift out of sync with each other.
/// </remarks>
internal static class SdCardTextFileSource
{
    /// <summary>
    /// Resolves <paramref name="fileStream"/> into an <c>openLines</c>/<c>totalBytes</c> pair
    /// and hands it to <paramref name="buildSessionAsync"/>.
    /// </summary>
    /// <remarks>
    /// A seekable stream is re-read from its starting position on each enumeration via
    /// <see cref="SdCardParseSource"/>. A forward-only stream can only be read once, so its
    /// lines are decoded up front instead.
    /// </remarks>
    public static async Task<T> ParseAsync<T>(
        System.IO.Stream fileStream,
        CancellationToken ct,
        Func<Func<CancellationToken, IAsyncEnumerable<SdCardLogLine>>, Func<long>, CancellationToken, Task<T>> buildSessionAsync)
    {
        var source = SdCardParseSource.TryCreate(fileStream);
        if (source != null)
        {
            return await buildSessionAsync(
                token => SdCardTextLineReader.ReadLinesAsync(source, token),
                () => source.TotalBytes,
                ct).ConfigureAwait(false);
        }

        var lines = new List<SdCardLogLine>();
        await foreach (var line in SdCardTextLineReader.ReadLinesAsync(fileStream, ct).ConfigureAwait(false))
        {
            lines.Add(line);
        }

        return await buildSessionAsync(
            _ => SdCardTextLineReader.ToAsyncEnumerable(lines),
            () => lines.Count > 0 ? lines[^1].BytesRead : 0,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens <paramref name="filePath"/> as a rewindable source and hands the resulting
    /// <c>openLines</c>/<c>totalBytes</c> pair to <paramref name="buildSessionAsync"/>.
    /// </summary>
    public static Task<T> ParseFileAsync<T>(
        string filePath,
        int bufferSize,
        CancellationToken ct,
        Func<Func<CancellationToken, IAsyncEnumerable<SdCardLogLine>>, Func<long>, CancellationToken, Task<T>> buildSessionAsync)
    {
        var source = SdCardParseSource.ForFile(filePath, bufferSize);

        return buildSessionAsync(
            token => SdCardTextLineReader.ReadLinesAsync(source, token),
            () => source.TotalBytes,
            ct);
    }

    /// <summary>
    /// An async iterator that yields no samples, for a log file with no data to report.
    /// </summary>
#pragma warning disable CS1998 // Async iterator: yield break requires async; no real awaits.
    public static async IAsyncEnumerable<SdCardLogEntry> EmptySamples()
    {
        yield break;
    }
#pragma warning restore CS1998
}
