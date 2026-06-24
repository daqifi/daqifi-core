using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Daqifi.Core.Device.Diagnostics;

/// <summary>
/// Parses the response from the <c>SYSTem:STReam:STATS?</c> SCPI query into a <see cref="StreamStats"/>.
/// </summary>
/// <remarks>
/// The firmware returns a set of <c>Key=Value</c> lines (see <see cref="KeyValueResponseParser"/>).
/// Parsing succeeds when at least one counter is recognized.
/// </remarks>
public static class StreamStatsParser
{
    /// <summary>
    /// Attempts to parse streaming stats from a sequence of response lines.
    /// </summary>
    /// <param name="lines">Response lines from the device.</param>
    /// <param name="result">The parsed stats, or <see langword="null"/> if no counter could be parsed.</param>
    /// <returns><see langword="true"/> if at least one counter was parsed; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(IEnumerable<string> lines, [NotNullWhen(true)] out StreamStats? result)
    {
        var values = KeyValueResponseParser.Parse(lines);
        if (values.Count == 0)
        {
            result = null;
            return false;
        }

        result = new StreamStats { Values = values };
        return true;
    }
}
