using System;
using System.Collections.Generic;
using Daqifi.Core.Device.Diagnostics;

namespace Daqifi.Core.Tests.Device.Diagnostics;

/// <summary>
/// Direct tests for the shared <c>Key=Value</c> diagnostics response parser. It was previously
/// exercised only through its two wrappers (<c>StreamStatsParser</c>, <c>MemoryDiagnosticsParser</c>),
/// which pin the happy path but never reach its malformed-line guards, its numeric bounds, or the
/// <c>TryParse</c> contract guards. These cover exactly those.
/// </summary>
public class KeyValueResponseParserTests
{
    [Fact]
    public void Parse_NullLines_ReturnsEmptyInsteadOfThrowing()
    {
        // A device that answered with nothing at all must not take the caller down.
        var values = KeyValueResponseParser.Parse(null!);

        Assert.Empty(values);
    }

    [Theory]
    [InlineData("=5")]              // separator first: no key at all
    [InlineData("   =5")]           // same, once the line is trimmed
    [InlineData("HeapFree=")]       // separator last: no value at all
    [InlineData("HeapFree")]        // no separator at all
    public void Parse_MalformedPairShapes_AreSkipped(string line)
    {
        var values = KeyValueResponseParser.Parse(new[] { line });

        Assert.Empty(values);
    }

    [Theory]
    [InlineData("-1")]                       // counters are unsigned; a negative is not a counter
    [InlineData("18446744073709551616")]     // one past ulong.MaxValue
    [InlineData("0x10")]                     // hex is not accepted
    [InlineData("1.5")]                      // no decimal point
    [InlineData("1,000")]                    // no group separators
    public void Parse_NonUnsignedIntegerValues_AreSkipped(string value)
    {
        var values = KeyValueResponseParser.Parse(new[] { $"HeapFree={value}" });

        Assert.Empty(values);
    }

    [Fact]
    public void Parse_LeadingPlusSign_IsAccepted()
    {
        var values = KeyValueResponseParser.Parse(new[] { "HeapFree=+7" });

        Assert.Equal(7UL, values["HeapFree"]);
    }

    [Fact]
    public void Parse_WhitespaceAroundKeyAndValue_IsTrimmed()
    {
        var values = KeyValueResponseParser.Parse(new[] { "  HeapFree  =  42  " });

        Assert.Equal(42UL, values["HeapFree"]);
    }

    [Fact]
    public void Parse_DuplicateKey_KeepsTheLastValue()
    {
        // The device can repeat a field across a re-emitted block; the freshest reading wins.
        var values = KeyValueResponseParser.Parse(new[] { "HeapFree=10", "HeapFree=20" });

        Assert.Equal(20UL, values["HeapFree"]);
        Assert.Single(values);
    }

    [Fact]
    public void Parse_KeysThatDifferOnlyByCase_AreDistinctFields()
    {
        // Case-insensitive keys would silently collapse two firmware counters into one.
        var values = KeyValueResponseParser.Parse(new[] { "HeapFree=1", "HEAPFREE=2" });

        Assert.Equal(2, values.Count);
        Assert.Equal(1UL, values["HeapFree"]);
        Assert.Equal(2UL, values["HEAPFREE"]);
    }

    [Fact]
    public void Parse_ErrorLineCarryingAPair_IsDroppedWholesale()
    {
        // "ERROR<tab>..." is a SCPI error line. It must be rejected before the '=' split runs,
        // or its text lands in the map as a bogus field.
        var values = KeyValueResponseParser.Parse(new[] { "ERROR\tHeapFree=0" });

        Assert.Empty(values);
    }

    [Fact]
    public void TryParse_NullLines_ReturnsFalseWithoutInvokingTheFactory()
    {
        var invoked = false;

        var ok = KeyValueResponseParser.TryParse<object>(
            null!,
            _ =>
            {
                invoked = true;
                return new object();
            },
            out var result);

        Assert.False(ok);
        Assert.Null(result);
        Assert.False(invoked);
    }

    [Fact]
    public void TryParse_NullFactory_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => KeyValueResponseParser.TryParse<object>(new[] { "HeapFree=1" }, null!, out _));
    }

    [Fact]
    public void TryParse_FactoryReturningNull_Throws()
    {
        // TryParse advertises a non-null result when it returns true; a factory that breaks
        // that must fail loudly rather than hand callers a null they will not null-check.
        Assert.Throws<InvalidOperationException>(
            () => KeyValueResponseParser.TryParse<object>(new[] { "HeapFree=1" }, _ => null!, out _));
    }

    [Fact]
    public void TryParse_PassesTheParsedPairsToTheFactory()
    {
        IReadOnlyDictionary<string, ulong>? seen = null;

        var ok = KeyValueResponseParser.TryParse(
            new[] { "", "HeapFree=1", "no separator here", "HeapUsed=2" },
            pairs =>
            {
                seen = pairs;
                return new object();
            },
            out var result);

        Assert.True(ok);
        Assert.NotNull(result);
        Assert.NotNull(seen);
        Assert.Equal(2, seen!.Count);
        Assert.Equal(1UL, seen["HeapFree"]);
        Assert.Equal(2UL, seen["HeapUsed"]);
    }
}
