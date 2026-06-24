using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Daqifi.Core.Device.Diagnostics;

/// <summary>
/// Parses the <c>MODULE: &lt;level&gt; (ceiling &lt;ceiling&gt;)</c> echo returned by the
/// <c>SYSTem:LOG:LEVel module,level</c> command into a <see cref="LogLevelSetting"/>.
/// </summary>
public static partial class LogLevelParser
{
    // e.g. "STREAM: 2 (ceiling 3)". Tolerant of surrounding/internal whitespace.
    private static readonly Regex LevelRegex = BuildLevelRegex();

    /// <summary>
    /// Attempts to parse a single echo line into a <see cref="LogLevelSetting"/>.
    /// </summary>
    /// <param name="line">A response line, e.g. <c>"STREAM: 2 (ceiling 3)"</c>.</param>
    /// <param name="result">The parsed setting, or <see langword="null"/> if parsing failed.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(string? line, [NotNullWhen(true)] out LogLevelSetting? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = LevelRegex.Match(line.Trim());
        if (!match.Success)
        {
            return false;
        }

        // The numeric groups are bounded by \d+ in the pattern; int.Parse is safe
        // for the realistic single-digit levels the firmware emits.
        result = new LogLevelSetting
        {
            Module = match.Groups["module"].Value,
            Level = int.Parse(match.Groups["level"].Value),
            Ceiling = int.Parse(match.Groups["ceiling"].Value),
        };
        return true;
    }

    /// <summary>
    /// Attempts to parse a <see cref="LogLevelSetting"/> from a sequence of response lines,
    /// trying each non-empty line in order until one succeeds.
    /// </summary>
    /// <param name="lines">Response lines from the device.</param>
    /// <param name="result">The parsed setting, or <see langword="null"/> if no line could be parsed.</param>
    /// <returns><see langword="true"/> if any line was successfully parsed; otherwise <see langword="false"/>.</returns>
    public static bool TryParseLines(IEnumerable<string> lines, [NotNullWhen(true)] out LogLevelSetting? result)
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

    [GeneratedRegex(@"^(?<module>\S+):\s*(?<level>\d+)\s*\(ceiling\s*(?<ceiling>\d+)\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BuildLevelRegex();
}
