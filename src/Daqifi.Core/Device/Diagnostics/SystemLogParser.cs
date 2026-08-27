using System;
using System.Collections.Generic;

namespace Daqifi.Core.Device.Diagnostics;

/// <summary>
/// Parses the response from the <c>SYSTem:LOG?</c> SCPI query into <see cref="SystemLogEntry"/> objects.
/// </summary>
/// <remarks>
/// <para>
/// The firmware dumps the log buffer as free-form text, one entry per line. Blank lines and SCPI
/// error/status lines (e.g. a <c>**ERROR</c> response if the query itself failed) are dropped; every
/// other line becomes one <see cref="SystemLogEntry"/> with its trailing line ending trimmed.
/// </para>
/// <para>
/// Lines carrying non-text bytes are dropped too. Reading the log does not stop the stream, so when
/// the query runs mid-capture the firmware's protobuf frames land on the reply and split it into
/// hundreds of mangled lines (issue #537). Without this filter every one of them became an entry: a
/// bench read of an 8-entry log while streaming returned ~723 entries, 715 of them fabricated out of
/// frame bytes (issue #682). Survivors are still kept -- the log read is destructive on the device,
/// so discarding the whole response would lose the real entries for good -- at the cost of the one
/// boundary entry that arrives with noise welded onto its front.
/// </para>
/// </remarks>
public static class SystemLogParser
{
    /// <summary>
    /// The characters stripped from each end of a line before it is classified. Deliberately not
    /// <see cref="string.Trim()"/>: that removes every Unicode whitespace character, which includes
    /// control characters such as vertical tab (<c>\u000B</c>), form feed (<c>\u000C</c>) and NEL
    /// (<c>\u0085</c>) — exactly the evidence the corruption check looks for, erased just before it
    /// runs. Only the padding and line endings a real firmware line can carry are removed here, so
    /// <c>"\u000Bstream-junk"</c> stays corrupt while <c>"entry\r"</c> still becomes <c>"entry"</c>.
    /// </summary>
    private static readonly char[] PaddingAndLineEndings = { ' ', '\t', '\r', '\n' };

    /// <summary>
    /// Parses log response lines into entries.
    /// </summary>
    /// <param name="lines">The raw response lines from the device.</param>
    /// <returns>The parsed log entries, in the order returned by the device (oldest first).</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="lines"/> is null.</exception>
    public static IReadOnlyList<SystemLogEntry> Parse(IEnumerable<string> lines)
    {
        if (lines == null)
        {
            throw new ArgumentNullException(nameof(lines));
        }

        var entries = new List<SystemLogEntry>();

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var message = rawLine.Trim(PaddingAndLineEndings);

            if (ScpiResponseClassifier.IsErrorResponseLine(message))
            {
                continue;
            }

            if (ScpiResponseClassifier.IsBinaryCorruptedLine(message))
            {
                continue;
            }

            entries.Add(new SystemLogEntry { Message = message });
        }

        return entries;
    }
}
