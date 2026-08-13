using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Streams the non-blank lines of a text log, one at a time, without holding the file in memory.
/// </summary>
internal static class SdCardTextLineReader
{
    /// <summary>
    /// Reads the non-blank lines of the source, re-opening it from the start on each enumeration.
    /// </summary>
    /// <param name="source">The rewindable source to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The file's non-blank lines, in order.</returns>
    public static async IAsyncEnumerable<string> ReadLinesAsync(
        SdCardParseSource source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var lease = source.Open();
        await using (lease.ConfigureAwait(false))
        {
            await foreach (var line in ReadLinesAsync(lease.Stream, ct).ConfigureAwait(false))
            {
                yield return line;
            }
        }
    }

    /// <summary>
    /// Reads the non-blank lines of a stream from its current position.
    /// </summary>
    /// <param name="stream">The stream to read. It is left open.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stream's non-blank lines, in order.</returns>
    public static async IAsyncEnumerable<string> ReadLinesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
    }

    /// <summary>
    /// Presents an already-materialized sequence as an async sequence, so the streaming and
    /// forward-only-stream code paths can share one sample producer.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="items">The materialized items.</param>
    /// <returns>The same items, as an async sequence.</returns>
#pragma warning disable CS1998 // Async iterator: yield return requires async; nothing here awaits.
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
    }
#pragma warning restore CS1998
}
