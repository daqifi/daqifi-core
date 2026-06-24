using System;
using System.Collections.Generic;

namespace Daqifi.Core.Device.Diagnostics;

/// <summary>
/// Parses the response from the <c>SYSTem:LOG?</c> SCPI query into <see cref="SystemLogEntry"/> objects.
/// </summary>
/// <remarks>
/// The firmware dumps the log buffer as free-form text, one entry per line. Blank lines and SCPI
/// error/status lines (e.g. a <c>**ERROR</c> response if the query itself failed) are dropped; every
/// other line becomes one <see cref="SystemLogEntry"/> with its trailing line ending trimmed.
/// </remarks>
public static class SystemLogParser
{
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

            var message = rawLine.Trim();

            if (ScpiResponseClassifier.IsErrorResponseLine(message))
            {
                continue;
            }

            entries.Add(new SystemLogEntry { Message = message });
        }

        return entries;
    }
}
