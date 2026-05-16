using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Parses the response from the <c>SYSTem:STORage:SD:SPACe?</c> SCPI command.
/// The firmware returns a single line of the form <c>"free,total"</c> where
/// both values are unsigned byte counts.
/// </summary>
public static class SdCardSpaceParser
{
    /// <summary>
    /// Attempts to parse a single response line into a <see cref="SdCardStorageInfo"/>.
    /// </summary>
    /// <param name="line">A response line, e.g. <c>"1048576000,2097152000"</c>.</param>
    /// <param name="result">The parsed storage info, or <see langword="null"/> if parsing failed.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(string? line, [NotNullWhen(true)] out SdCardStorageInfo? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var commaIndex = line.IndexOf(',');
        if (commaIndex <= 0 || commaIndex == line.Length - 1)
        {
            return false;
        }

        var freeSpan = line.AsSpan(0, commaIndex).Trim();
        var totalSpan = line.AsSpan(commaIndex + 1).Trim();

        if (!long.TryParse(freeSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var free) ||
            !long.TryParse(totalSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
        {
            return false;
        }

        // Reject negatives and any free > total — both indicate corrupt firmware
        // metadata. Surfacing them as a parse failure forces the typed exception
        // path rather than producing a negative UsedBytes downstream.
        if (free < 0 || total < 0 || free > total)
        {
            return false;
        }

        result = new SdCardStorageInfo(free, total);
        return true;
    }

    /// <summary>
    /// Attempts to parse <see cref="SdCardStorageInfo"/> from a sequence of response lines,
    /// trying each non-empty line in order until one succeeds.
    /// </summary>
    /// <param name="lines">Response lines from the device.</param>
    /// <param name="result">The parsed storage info, or <see langword="null"/> if no line could be parsed.</param>
    /// <returns><see langword="true"/> if any line was successfully parsed; otherwise <see langword="false"/>.</returns>
    public static bool TryParseLines(IEnumerable<string> lines, [NotNullWhen(true)] out SdCardStorageInfo? result)
    {
        foreach (var line in lines)
        {
            if (TryParse(line, out result))
            {
                return true;
            }
        }

        result = null;
        return false;
    }
}
