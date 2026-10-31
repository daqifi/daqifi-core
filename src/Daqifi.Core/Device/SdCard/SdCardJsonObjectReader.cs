using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// One complete top-level JSON object from a log file, together with how far into the file
/// reading it has got.
/// </summary>
/// <param name="Text">The object, from its opening brace to its closing brace.</param>
/// <param name="BytesRead">
/// Bytes consumed from the source up to and including the line the object ends on. Counted the
/// same way <see cref="SdCardLogLine.BytesRead"/> counts it.
/// </param>
internal readonly record struct SdCardJsonObject(string Text, long BytesRead);

/// <summary>
/// Streams the top-level JSON objects of a log file, one at a time, without holding the file
/// in memory.
/// </summary>
/// <remarks>
/// The DAQiFi firmware writes a JSON log as a stream of objects rather than one object per
/// line: a sample object opens with a bare <c>{</c> on its own line and spends one line on each
/// of its fields (<c>JSON_Encoder.c</c>). A line-at-a-time reader therefore sees no complete
/// object at all. This reader joins the lines back into objects by brace depth, so the same
/// file parses whether the firmware puts an object on one line or on twenty.
/// </remarks>
internal static class SdCardJsonObjectReader
{
    /// <summary>
    /// The largest object text this reader will accumulate, in characters.
    /// </summary>
    /// <remarks>
    /// A log cut short mid-object, or corrupt text carrying a stray <c>{</c>, would otherwise
    /// make the accumulator grow until the end of the file. The firmware's own objects are
    /// bounded by its transmit buffer and are a few kilobytes at most, so this limit is far
    /// above anything a real log contains.
    /// </remarks>
    private const int MaxObjectLength = 1024 * 1024;

    /// <summary>
    /// Reads the complete top-level JSON objects of a log file, in order.
    /// </summary>
    /// <param name="openLines">Opens the file's non-blank lines from the start.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Each complete object, in the order the file states them.</returns>
    /// <remarks>
    /// Text outside an object — whitespace, separators, or a line of something that is not JSON
    /// at all — is discarded. An object left unclosed at the end of the file is discarded too: a
    /// log cut short by a power loss or by a failed transfer ends with a partial object, and half
    /// a sample is not a sample.
    /// </remarks>
    public static async IAsyncEnumerable<SdCardJsonObject> ReadObjectsAsync(
        Func<CancellationToken, IAsyncEnumerable<SdCardLogLine>> openLines,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new StringBuilder();
        var depth = 0;
        var inString = false;
        var escaped = false;

        await foreach (var line in openLines(ct).WithCancellation(ct).ConfigureAwait(false))
        {
            foreach (var character in line.Text)
            {
                if (depth == 0)
                {
                    // Between objects. Everything up to the next opening brace is not ours.
                    if (character != '{')
                    {
                        continue;
                    }

                    buffer.Clear();
                    buffer.Append(character);
                    depth = 1;
                    inString = false;
                    escaped = false;
                    continue;
                }

                buffer.Append(character);

                if (inString)
                {
                    // Braces inside a string value are text, not structure. The firmware's
                    // device name and serial number are strings, so this has to hold.
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                switch (character)
                {
                    case '"':
                        inString = true;
                        break;
                    case '{':
                        depth++;
                        break;
                    case '}':
                        depth--;
                        if (depth == 0)
                        {
                            yield return new SdCardJsonObject(buffer.ToString(), line.BytesRead);
                            buffer.Clear();
                        }

                        break;
                }
            }

            if (depth == 0)
            {
                continue;
            }

            if (buffer.Length > MaxObjectLength)
            {
                // Give up on this object and resynchronize at the next opening brace.
                buffer.Clear();
                depth = 0;
                inString = false;
                escaped = false;
                continue;
            }

            // The line terminator the reader stripped still separates two JSON tokens, so put a
            // newline back in its place.
            buffer.Append('\n');
        }
    }
}
