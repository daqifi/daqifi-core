using System;
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
/// Tries to parse a single response line into <typeparamref name="T"/>, following the standard
/// <c>Try*</c> pattern, for a value-typed <typeparamref name="T"/> (e.g. <see langword="int"/> or
/// <see langword="double"/>). See <see cref="TryParseLine{T}"/> for the reference-type variant.
/// </summary>
/// <typeparam name="T">The parsed result type.</typeparam>
/// <param name="line">A single response line. May be <see langword="null"/> or empty.</param>
/// <param name="result">The parsed value, or <c>default</c> if parsing failed.</param>
internal delegate bool TryParseLineValue<T>(string? line, out T result) where T : struct;

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
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> or <paramref name="tryParse"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="tryParse"/> returned <see langword="true"/> with a <see langword="null"/> result.</exception>
    public static bool TryParseFirst<T>(IEnumerable<string> lines, TryParseLine<T> tryParse, [NotNullWhen(true)] out T? result)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(tryParse);

        foreach (var line in lines)
        {
            if (tryParse(line, out result))
            {
                if (result is null)
                {
                    throw new InvalidOperationException(
                        $"{nameof(tryParse)} returned true with a null result; TryParseFirst callers rely on a non-null result when returning true.");
                }

                return true;
            }
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Tries each line in <paramref name="lines"/> in order against <paramref name="tryParse"/>,
    /// returning the first successful parse. Value-typed counterpart of
    /// <see cref="TryParseFirst{T}(IEnumerable{string}, TryParseLine{T}, out T)"/> for results such
    /// as <see langword="int"/> or <see langword="double"/>.
    /// </summary>
    /// <remarks>
    /// Shared by the "first parseable line wins" numeric response parsers (e.g. the capability
    /// API-version query, the system error-count query, and the analog-output readback) so each
    /// only needs to supply its own single-line <c>TryParse</c>, including any line-skipping
    /// (blank lines, error lines, etc.) it wants to apply before attempting to parse.
    /// </remarks>
    /// <typeparam name="T">The parsed result type.</typeparam>
    /// <param name="lines">The response lines to try, in order.</param>
    /// <param name="tryParse">The single-line parse function to apply to each line.</param>
    /// <param name="result">The first successfully parsed value, or <c>default</c> if none parsed.</param>
    /// <returns><see langword="true"/> if any line was successfully parsed; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> or <paramref name="tryParse"/> is <see langword="null"/>.</exception>
    public static bool TryParseFirst<T>(IEnumerable<string> lines, TryParseLineValue<T> tryParse, out T result)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(tryParse);

        foreach (var line in lines)
        {
            if (tryParse(line, out result))
            {
                return true;
            }
        }

        result = default;
        return false;
    }
}
