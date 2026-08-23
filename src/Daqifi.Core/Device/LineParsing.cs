using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Daqifi.Core.Device;

/// <summary>
/// Tries to parse a single response line into <typeparamref name="T"/>, following the standard
/// <c>Try*</c> pattern: returns <see langword="true"/> and a non-null <paramref name="result"/>
/// on success, or <see langword="false"/> and a <see langword="null"/> <paramref name="result"/>
/// otherwise.
/// </summary>
/// <typeparam name="T">The parsed result type.</typeparam>
/// <param name="line">A single response line. May be <see langword="null"/> or empty.</param>
/// <param name="result">The parsed value, or <see langword="null"/> if parsing failed.</param>
internal delegate bool TryParseLine<T>(string? line, [NotNullWhen(true)] out T? result) where T : class;

/// <summary>
/// Shared helper for the several device/firmware response parsers whose multi-line variant tries
/// each line in order and returns the first one that parses successfully.
/// </summary>
internal static class LineParsing
{
    /// <summary>
    /// Tries each line in <paramref name="lines"/> in order against <paramref name="tryParse"/>,
    /// returning the first successful parse.
    /// </summary>
    /// <remarks>
    /// Shared by the "first parseable line wins" response parsers (e.g. <c>SdCardSpaceParser</c>,
    /// <c>LogLevelParser</c>, <c>LanChipInfoParser</c>) so each only needs to supply its own
    /// single-line <c>TryParse</c>.
    /// </remarks>
    /// <typeparam name="T">The parsed result type.</typeparam>
    /// <param name="lines">The response lines to try, in order.</param>
    /// <param name="tryParse">The single-line parse function to apply to each line.</param>
    /// <param name="result">The first successfully parsed value, or <see langword="null"/> if none parsed.</param>
    /// <returns><see langword="true"/> if any line was successfully parsed; otherwise <see langword="false"/>.</returns>
    public static bool TryParseFirst<T>(IEnumerable<string> lines, TryParseLine<T> tryParse, [NotNullWhen(true)] out T? result)
        where T : class
    {
        foreach (var line in lines)
        {
            if (tryParse(line, out result))
            {
                return true;
            }
        }

        result = null;
        return false;
    }
}
