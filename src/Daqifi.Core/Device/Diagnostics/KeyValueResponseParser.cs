using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Daqifi.Core.Device.Diagnostics;

/// <summary>
/// Parses the <c>Key=Value</c> line-oriented responses shared by the streaming-stats and
/// memory-diagnostics SCPI queries into a dictionary of unsigned integer counters.
/// </summary>
/// <remarks>
/// The parser is deliberately tolerant: it skips blank lines, SCPI error/status lines, lines
/// without a <c>=</c> separator, and pairs whose value is not a non-negative integer. This keeps a
/// firmware revision that adds new (or non-numeric) fields from breaking the parse — known numeric
/// fields are still extracted. Duplicate keys keep the last value seen.
/// </remarks>
internal static class KeyValueResponseParser
{
    /// <summary>
    /// Parses response lines into a case-sensitive map of field name to unsigned value.
    /// </summary>
    /// <param name="lines">The raw response lines from the device.</param>
    /// <returns>The parsed key/value pairs; empty when no parseable pair was found.</returns>
    public static IReadOnlyDictionary<string, ulong> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, ulong>();

        if (lines == null)
        {
            return result;
        }

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var line = rawLine.Trim();

            // Drop SCPI error responses and firmware status text so a stray
            // error interleaved with the counters doesn't pollute the result.
            if (ScpiResponseClassifier.IsErrorResponseLine(line))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == line.Length - 1)
            {
                continue;
            }

            var key = line.Substring(0, separatorIndex).Trim();
            var valueSpan = line.AsSpan(separatorIndex + 1).Trim();

            if (key.Length == 0)
            {
                continue;
            }

            if (!ulong.TryParse(valueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Parses response lines and, if at least one field was recognized, builds the typed result
    /// via <paramref name="factory"/>.
    /// </summary>
    /// <remarks>
    /// Shared by the <c>Key=Value</c> diagnostics wrappers (<see cref="MemoryDiagnosticsParser"/>,
    /// <see cref="StreamStatsParser"/>) so each only needs to supply its own result type.
    /// </remarks>
    /// <param name="lines">The raw response lines from the device.</param>
    /// <param name="factory">
    /// Builds the typed result from the parsed key/value pairs. Must not return
    /// <see langword="null"/>; callers rely on <c>TryParse</c> returning a non-null
    /// <paramref name="result"/> whenever it returns <see langword="true"/>.
    /// </param>
    /// <param name="result">The built result, or <see langword="null"/> if no field could be parsed.</param>
    /// <returns><see langword="true"/> if at least one field was parsed; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="factory"/> returned <see langword="null"/>.</exception>
    public static bool TryParse<T>(
        IEnumerable<string> lines,
        Func<IReadOnlyDictionary<string, ulong>, T> factory,
        [NotNullWhen(true)] out T? result)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);

        var values = Parse(lines);
        if (values.Count == 0)
        {
            result = null;
            return false;
        }

        result = factory(values) ?? throw new InvalidOperationException(
            $"{nameof(factory)} returned null; TryParse callers rely on a non-null result when returning true.");
        return true;
    }
}
