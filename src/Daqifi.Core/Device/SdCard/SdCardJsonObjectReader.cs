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
/// <para>
/// Joining lines by brace depth means a damaged line that opens a brace it never closes would
/// otherwise swallow every object after it. The reader therefore resynchronizes as soon as the
/// text in hand can no longer be the beginning of one object, and picks the log up again at the
/// next object boundary, so one bad line costs one object rather than the rest of the file.
/// </para>
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
    /// a sample is not a sample. An object left unclosed in the middle of the file is discarded
    /// as soon as the reader can tell, and reading carries on with the objects after it.
    /// </remarks>
    public static async IAsyncEnumerable<SdCardJsonObject> ReadObjectsAsync(
        Func<CancellationToken, IAsyncEnumerable<SdCardLogLine>> openLines,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new StringBuilder();
        var depth = 0;
        var bracketDepth = 0;
        var inString = false;
        var escaped = false;

        await foreach (var line in openLines(ct).WithCancellation(ct).ConfigureAwait(false))
        {
            // A line that opens a top-level object while one is already open, outside any array,
            // says the open one was damaged: the firmware writes a nested object either inside
            // an array ("ai", "di") or inline on one line ("meta"), never alone at the start of
            // a line. Abandoning the damaged text here resynchronizes on this line's object.
            if (depth > 0 && bracketDepth == 0 && !inString && StartsAnObject(line.Text))
            {
                buffer.Clear();
                depth = 0;
                escaped = false;
            }

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
                    bracketDepth = 0;
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
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        if (bracketDepth > 0)
                        {
                            bracketDepth--;
                        }

                        break;
                    case '}':
                        depth--;
                        if (depth == 0)
                        {
                            yield return new SdCardJsonObject(buffer.ToString(), line.BytesRead);
                            buffer.Clear();
                            bracketDepth = 0;
                        }

                        break;
                }
            }

            if (depth == 0)
            {
                continue;
            }

            // A line boundary inside an object has to fall between two JSON tokens: the firmware
            // breaks its objects after a brace, a bracket or a comma, and JSON has no multi-line
            // string. Text that ends anywhere else — a word, a half-written keyword, a string
            // left open — is damage, and waiting for a closing brace it will never see would
            // cost every object after it.
            if (inString || !EndsBetweenTokens(buffer))
            {
                buffer.Clear();
                depth = 0;
                bracketDepth = 0;
                inString = false;
                escaped = false;
                continue;
            }

            if (buffer.Length > MaxObjectLength)
            {
                // Give up on this object and resynchronize at the next opening brace.
                buffer.Clear();
                depth = 0;
                bracketDepth = 0;
                inString = false;
                escaped = false;
                continue;
            }

            // The line terminator the reader stripped still separates two JSON tokens, so put a
            // newline back in its place.
            buffer.Append('\n');
        }
    }

    /// <summary>
    /// Whether a line's first character of substance opens a JSON object.
    /// </summary>
    private static bool StartsAnObject(string lineText)
    {
        foreach (var character in lineText)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            return character == '{';
        }

        return false;
    }

    /// <summary>
    /// Whether the text accumulated so far ends where one JSON token ends and the next may
    /// begin, which is the only place a line of a well-formed object can break.
    /// </summary>
    /// <remarks>
    /// Structure (<c>{ } [ ] , :</c>) ends a token. So does a closing quote, a digit or any of
    /// the characters a number may end on, and the last letter of <c>true</c>, <c>false</c> or
    /// <c>null</c>. Anything else — the <c>d</c> of a line reading <c>{bad</c>, say — cannot be
    /// the end of a JSON token, so the object is damaged.
    /// </remarks>
    private static bool EndsBetweenTokens(StringBuilder buffer)
    {
        for (var i = buffer.Length - 1; i >= 0; i--)
        {
            var character = buffer[i];
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            return character switch
            {
                '{' or '}' or '[' or ']' or ',' or ':' or '"' => true,
                '-' or '+' or '.' or 'e' or 'E' => true,
                'l' => true,
                _ => character >= '0' && character <= '9'
            };
        }

        return true;
    }
}
