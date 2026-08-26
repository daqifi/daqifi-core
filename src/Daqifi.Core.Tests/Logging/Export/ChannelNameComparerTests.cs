using Daqifi.Core.Logging.Export;

namespace Daqifi.Core.Tests.Logging.Export;

public class ChannelNameComparerTests
{
    private static readonly ChannelNameComparer Comparer = ChannelNameComparer.Instance;

    private static int Sign(int value) => Math.Sign(value);

    // ── The defect this type exists for ─────────────────────────────────────

    [Theory]
    [InlineData("AI2", "AI10")]
    [InlineData("AI9", "AI10")]
    [InlineData("AI11", "AI100")]
    [InlineData("DIO2", "DIO10")]
    public void Compare_TwoDigitIndex_SortsAfterEverySingleDigitOne(string smaller, string larger)
    {
        Assert.Equal(-1, Sign(Comparer.Compare(smaller, larger)));
        Assert.Equal(1, Sign(Comparer.Compare(larger, smaller)));
    }

    [Fact]
    public void Sort_TwelveAnalogChannels_OrdersThemNumericallyNotLexicographically()
    {
        // The bug needs a two-digit index to appear at all: with nine channels or fewer,
        // lexicographic and natural order are identical.
        string[] names = [.. Enumerable.Range(0, 12).Select(i => $"AI{i}")];
        var shuffled = names.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        // Guard the premise — plain ordinal really does misorder these.
        Assert.Equal(
            ["AI0", "AI1", "AI10", "AI11", "AI2", "AI3", "AI4", "AI5", "AI6", "AI7", "AI8", "AI9"],
            shuffled);

        Assert.Equal(names, shuffled.OrderBy(n => n, Comparer).ToArray());
    }

    // ── Names without a trailing number behave exactly as before ────────────

    [Theory]
    [InlineData("Temperature", "Voltage")]
    [InlineData("AI", "DIO")]
    [InlineData("Alpha", "alpha")]
    public void Compare_NoTrailingDigits_IsOrdinal(string first, string second)
    {
        Assert.Equal(Sign(string.CompareOrdinal(first, second)), Sign(Comparer.Compare(first, second)));
    }

    [Fact]
    public void Compare_DifferentPrefixes_ComparesPrefixBeforeNumber()
    {
        // The prefix decides, so a large index on one prefix never overtakes another prefix.
        Assert.Equal(-1, Sign(Comparer.Compare("AI99", "DIO0")));
    }

    [Fact]
    public void Compare_PrefixComparisonIsOrdinal_NotCultureSensitive()
    {
        // Ordinal puts every uppercase letter before every lowercase one; a culture-sensitive
        // comparison interleaves them. Pinning this keeps SQLite's BINARY collation in agreement.
        Assert.Equal(1, Sign(Comparer.Compare("aI0", "AI0")));
    }

    // ── Edge cases that have to stay a total order ──────────────────────────

    [Fact]
    public void Compare_UnnumberedName_SortsBeforeTheNumberedOneSharingItsPrefix()
    {
        Assert.Equal(-1, Sign(Comparer.Compare("AI", "AI0")));
        Assert.Equal(1, Sign(Comparer.Compare("AI0", "AI")));
    }

    [Fact]
    public void Compare_LeadingZeros_SameValueStillOrdersDeterministically()
    {
        // "AI01" and "AI1" are the same number but different columns, so neither may be
        // reported as equal to the other.
        Assert.Equal(-1, Sign(Comparer.Compare("AI01", "AI1")));
        Assert.Equal(1, Sign(Comparer.Compare("AI1", "AI01")));
        Assert.Equal(-1, Sign(Comparer.Compare("AI0", "AI00")));
    }

    [Fact]
    public void Compare_LeadingZerosDoNotChangeTheValue()
    {
        Assert.Equal(-1, Sign(Comparer.Compare("AI002", "AI10")));
    }

    [Fact]
    public void Compare_DigitRunTooLongForALong_DoesNotOverflowOrThrow()
    {
        // Parsing the trailing digits would throw or wrap here; counting them does not.
        var huge = "AI" + new string('9', 40);
        var huger = "AI1" + new string('0', 41);

        Assert.Equal(-1, Sign(Comparer.Compare("AI7", huge)));
        Assert.Equal(-1, Sign(Comparer.Compare(huge, "AI" + new string('9', 41))));
        Assert.Equal(1, Sign(Comparer.Compare(huger, "AI0")));
    }

    [Fact]
    public void Compare_AllDigitNames_ComparesNumerically()
    {
        Assert.Equal(-1, Sign(Comparer.Compare("9", "12")));
    }

    [Fact]
    public void Compare_NonAsciiDigits_AreLeftInThePrefixRatherThanParsed()
    {
        // Arabic-Indic digits are Unicode decimal digits but cannot be compared as ASCII values.
        // They stay in the prefix, where they at least sort consistently, and nothing throws.
        const string arabicTwo = "AI٢";
        const string arabicThree = "AI٣";

        Assert.Equal(-1, Sign(Comparer.Compare(arabicTwo, arabicThree)));
        Assert.Equal(0, Comparer.Compare(arabicTwo, "AI٢"));
    }

    [Fact]
    public void Compare_IdenticalNames_AreEqual()
    {
        Assert.Equal(0, Comparer.Compare("AI10", "AI10"));
        Assert.Equal(0, Comparer.Compare(string.Empty, string.Empty));
    }

    [Fact]
    public void Compare_Nulls_SortBeforeEveryName()
    {
        Assert.Equal(0, Comparer.Compare(null, null));
        Assert.Equal(-1, Sign(Comparer.Compare(null, "AI0")));
        Assert.Equal(1, Sign(Comparer.Compare("AI0", null)));
    }

    [Fact]
    public void Compare_EmptyName_SortsBeforeANumberedOne()
    {
        Assert.Equal(-1, Sign(Comparer.Compare(string.Empty, "0")));
        Assert.Equal(-1, Sign(Comparer.Compare(string.Empty, "AI0")));
    }

    [Fact]
    public void Compare_IsAntisymmetricAndTransitiveAcrossAMixedSet()
    {
        // A comparer that contradicts itself makes List.Sort's output depend on input order,
        // so check the three ordering laws directly over an awkward set.
        string[] names =
        [
            "AI0", "AI00", "AI1", "AI01", "AI2", "AI10", "AI11", "AI",
            "DIO0", "DIO10", "Temperature", "aI0", "", "9", "12"
        ];

        foreach (var a in names)
        {
            Assert.Equal(0, Comparer.Compare(a, a));

            foreach (var b in names)
            {
                Assert.Equal(-Sign(Comparer.Compare(b, a)), Sign(Comparer.Compare(a, b)));

                foreach (var c in names)
                {
                    if (Comparer.Compare(a, b) < 0 && Comparer.Compare(b, c) < 0)
                    {
                        Assert.True(
                            Comparer.Compare(a, c) < 0,
                            $"'{a}' < '{b}' < '{c}' but '{a}' does not sort before '{c}'.");
                    }
                }
            }
        }
    }

    [Fact]
    public void Compare_DistinctNames_AreNeverReportedEqual()
    {
        string[] names = ["AI0", "AI00", "AI1", "AI01", "AI", "AI10", "", "0"];

        foreach (var a in names)
        {
            foreach (var b in names)
            {
                if (!string.Equals(a, b, StringComparison.Ordinal))
                {
                    Assert.NotEqual(0, Comparer.Compare(a, b));
                }
            }
        }
    }
}
