using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Daqifi.Core.Device;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Direct tests for <see cref="LineParsing"/>, the shared "first parseable line wins" helper behind
/// six response parsers (<c>SdCardSpaceParser</c>, <c>LogLevelParser</c>, <c>LanChipInfoParser</c>,
/// the capability API-version query, the system error-count query, and the analog-output readback).
/// Those callers only ever exercise the happy path with well-formed inputs, so the helper's own
/// contract — argument guards, ordering, the failed-parse output value, the non-null result check,
/// and lazy enumeration — had nothing pinning it. These cover exactly that contract.
/// </summary>
public class LineParsingTests
{
    /// <summary>A reference-typed parse result, standing in for the parser DTOs the real callers use.</summary>
    private sealed class Parsed(string value)
    {
        public string Value { get; } = value;
    }

    /// <summary>Parses any line that is a valid integer into a <see cref="Parsed"/>.</summary>
    private static bool TryParseNumericLine(string? line, [NotNullWhen(true)] out Parsed? result)
    {
        if (line is not null && int.TryParse(line, out _))
        {
            result = new Parsed(line);
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Yields <paramref name="lines"/>, then throws if enumeration continues past the last one.
    /// Used to prove the helper stops pulling as soon as a line parses.
    /// </summary>
    private static IEnumerable<string> ThrowingAfter(params string[] lines)
    {
        foreach (var line in lines)
        {
            yield return line;
        }

        throw new InvalidOperationException("Enumerated past the first successful line.");
    }

    // ---- reference-type overload -------------------------------------------------------------

    [Fact]
    public void TryParseFirst_Reference_NullLines_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => LineParsing.TryParseFirst<Parsed>(null!, TryParseNumericLine, out _));

        Assert.Equal("lines", ex.ParamName);
    }

    [Fact]
    public void TryParseFirst_Reference_NullTryParse_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => LineParsing.TryParseFirst<Parsed>(new[] { "1" }, null!, out _));

        Assert.Equal("tryParse", ex.ParamName);
    }

    [Fact]
    public void TryParseFirst_Reference_FirstParseableLineWins()
    {
        // Two lines parse; the helper must return the earlier one, not the last one seen.
        var parsed = LineParsing.TryParseFirst<Parsed>(
            new[] { "not a number", "11", "22" }, TryParseNumericLine, out var result);

        Assert.True(parsed);
        Assert.Equal("11", result!.Value);
    }

    [Fact]
    public void TryParseFirst_Reference_TriesLinesInOrderAndStopsAtTheFirstSuccess()
    {
        var attempted = new List<string?>();

        LineParsing.TryParseFirst<Parsed>(
            new[] { "header", "", "7", "8" },
            (string? line, [NotNullWhen(true)] out Parsed? value) =>
            {
                attempted.Add(line);
                return TryParseNumericLine(line, out value);
            },
            out _);

        Assert.Equal(new string?[] { "header", "", "7" }, attempted);
    }

    [Fact]
    public void TryParseFirst_Reference_DoesNotEnumeratePastTheFirstSuccess()
    {
        // A materializing implementation (lines.ToList()) would trip the sentinel throw.
        var parsed = LineParsing.TryParseFirst<Parsed>(
            ThrowingAfter("skip", "5"), TryParseNumericLine, out var result);

        Assert.True(parsed);
        Assert.Equal("5", result!.Value);
    }

    [Fact]
    public void TryParseFirst_Reference_NoLineParses_ReturnsFalseAndClearsAStaleResult()
    {
        // The last attempt writes a non-null result while still returning false; the helper must
        // not leak it, because callers rely on null meaning "nothing parsed".
        var parsed = LineParsing.TryParseFirst<Parsed>(
            new[] { "a", "b" },
            (string? line, [NotNullWhen(true)] out Parsed? value) =>
            {
                value = new Parsed(line!);
                return false;
            },
            out var result);

        Assert.False(parsed);
        Assert.Null(result);
    }

    [Fact]
    public void TryParseFirst_Reference_EmptyLines_ReturnsFalseWithoutInvokingTryParse()
    {
        var invocations = 0;

        var parsed = LineParsing.TryParseFirst<Parsed>(
            Array.Empty<string>(),
            (string? line, [NotNullWhen(true)] out Parsed? value) =>
            {
                invocations++;
                value = null;
                return false;
            },
            out var result);

        Assert.False(parsed);
        Assert.Null(result);
        Assert.Equal(0, invocations);
    }

    [Fact]
    public void TryParseFirst_Reference_NullLineElement_IsHandedToTryParseRatherThanSkipped()
    {
        // The delegate signature accepts string?, and the real single-line parsers do their own
        // null handling, so the helper must not filter nulls out on their behalf.
        var attempted = new List<string?>();

        LineParsing.TryParseFirst<Parsed>(
            new string[] { null!, "3" },
            (string? line, [NotNullWhen(true)] out Parsed? value) =>
            {
                attempted.Add(line);
                return TryParseNumericLine(line, out value);
            },
            out _);

        Assert.Equal(new string?[] { null, "3" }, attempted);
    }

    [Fact]
    public void TryParseFirst_Reference_TrueWithNullResult_ThrowsInvalidOperationException()
    {
        // A misbehaving single-line parser must be caught here rather than handing a null back
        // through a [NotNullWhen(true)] out parameter.
        var ex = Assert.Throws<InvalidOperationException>(() => LineParsing.TryParseFirst<Parsed>(
            new[] { "1" },
            (string? line, [NotNullWhen(true)] out Parsed? value) =>
            {
                // Deliberately violates the Try* contract: null result alongside true.
                value = null!;
                return true;
            },
            out _));

        Assert.Contains("null result", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseFirst_Reference_TryParseException_Propagates()
    {
        // Parse failures are signalled by returning false; a thrown exception is a real fault and
        // must not be swallowed into a "nothing parsed" result.
        Assert.Throws<FormatException>(() => LineParsing.TryParseFirst<Parsed>(
            new[] { "1" },
            (string? line, [NotNullWhen(true)] out Parsed? value) => throw new FormatException("boom"),
            out _));
    }

    // ---- value-type overload -----------------------------------------------------------------

    [Fact]
    public void TryParseFirst_Value_NullLines_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => LineParsing.TryParseFirst<int>(
            null!, static (string? line, out int value) => { value = 0; return false; }, out _));

        Assert.Equal("lines", ex.ParamName);
    }

    [Fact]
    public void TryParseFirst_Value_NullTryParse_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => LineParsing.TryParseFirst<int>(new[] { "1" }, null!, out _));

        Assert.Equal("tryParse", ex.ParamName);
    }

    [Fact]
    public void TryParseFirst_Value_FirstParseableLineWins()
    {
        var parsed = LineParsing.TryParseFirst<int>(
            new[] { "junk", "11", "22" },
            static (string? line, out int value) => int.TryParse(line, out value),
            out var result);

        Assert.True(parsed);
        Assert.Equal(11, result);
    }

    [Fact]
    public void TryParseFirst_Value_NoLineParses_ReturnsFalseAndResetsAStaleResult()
    {
        // Same leak risk as the reference overload: a failing attempt that still wrote to the out
        // parameter must not survive into the caller's variable.
        var parsed = LineParsing.TryParseFirst<int>(
            new[] { "a", "b" },
            static (string? line, out int value) => { value = 99; return false; },
            out var result);

        Assert.False(parsed);
        Assert.Equal(0, result);
    }

    [Fact]
    public void TryParseFirst_Value_DefaultValuedSuccess_IsReportedAsSuccess()
    {
        // Unlike the reference overload, default(T) is a legitimate parsed value here: "0 errors"
        // and "0 bytes free" are real device answers and must not be mistaken for a parse failure.
        var parsed = LineParsing.TryParseFirst<int>(
            new[] { "0", "5" },
            static (string? line, out int value) => int.TryParse(line, out value),
            out var result);

        Assert.True(parsed);
        Assert.Equal(0, result);
    }

    [Fact]
    public void TryParseFirst_Value_DoesNotEnumeratePastTheFirstSuccess()
    {
        var parsed = LineParsing.TryParseFirst<int>(
            ThrowingAfter("skip", "5"),
            static (string? line, out int value) => int.TryParse(line, out value),
            out var result);

        Assert.True(parsed);
        Assert.Equal(5, result);
    }
}
