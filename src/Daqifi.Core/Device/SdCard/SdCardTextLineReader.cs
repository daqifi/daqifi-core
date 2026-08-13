using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// A non-blank line of a text log, together with how far into the file reading it has got.
/// </summary>
/// <param name="Text">The line, without its terminator.</param>
/// <param name="BytesRead">
/// Bytes consumed from the source so far. For a seekable source this is the stream's real
/// position, which advances a read buffer at a time and lands exactly on the file length at the
/// end; for a source that has already been read into memory it is the running total of the line
/// lengths, which is the same figure the parsers reported before they became lazy.
/// </param>
internal readonly record struct SdCardLogLine(string Text, long BytesRead);

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
    public static async IAsyncEnumerable<SdCardLogLine> ReadLinesAsync(
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
    public static async IAsyncEnumerable<SdCardLogLine> ReadLinesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);

        var seekable = stream.CanSeek;
        var estimatedBytes = 0L;

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            // +1 for the terminator, matching what the parsers counted before they became lazy.
            estimatedBytes += line.Length + 1;

            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return new SdCardLogLine(line, seekable ? stream.Position : estimatedBytes);
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
