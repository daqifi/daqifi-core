using System;
using System.Collections.Generic;
using System.Globalization;

namespace Daqifi.Core.Device.Diagnostics;

/// <summary>
/// Parses the response from the <c>SYSTem:LOG:CMDHistory?</c> SCPI query into a list of commands.
/// </summary>
/// <remarks>
/// The firmware returns a <c>Last N commands:</c> header followed by one <c>&lt;n&gt;: &lt;command&gt;</c>
/// line per remembered command (newest first), or the single line <c>No command history</c> when the
/// buffer is empty. This parser strips the header and the numeric prefix, returning just the command
/// text in the order the device reported it.
/// </remarks>
public static class CommandHistoryParser
{
    private const string NoHistoryMarker = "No command history";

    /// <summary>
    /// Parses command-history response lines into command strings.
    /// </summary>
    /// <param name="lines">The raw response lines from the device.</param>
    /// <returns>The remembered commands (newest first); empty when there is no history.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="lines"/> is null.</exception>
    public static IReadOnlyList<string> Parse(IEnumerable<string> lines)
    {
        if (lines == null)
        {
            throw new ArgumentNullException(nameof(lines));
        }

        var commands = new List<string>();

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var line = rawLine.Trim();

            if (line.Equals(NoHistoryMarker, StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<string>();
            }

            // Each command line is "<index>: <command>". Lines that don't match
            // (e.g. the "Last N commands:" header) are skipped: the header's
            // numeric portion is absent, so the index parse fails.
            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0)
            {
                continue;
            }

            var indexSpan = line.AsSpan(0, colonIndex).Trim();
            if (!int.TryParse(indexSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            var command = line.Substring(colonIndex + 1).Trim();
            if (command.Length > 0)
            {
                commands.Add(command);
            }
        }

        return commands;
    }
}
