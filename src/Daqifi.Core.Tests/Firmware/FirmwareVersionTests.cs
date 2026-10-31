using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

public class FirmwareVersionTests
{
    [Theory]
    [InlineData("3.2.0", 3, 2, 0, null, 0)]
    [InlineData("v3.2.0", 3, 2, 0, null, 0)]
    [InlineData("1.0.0", 1, 0, 0, null, 0)]
    [InlineData("v1.0.0", 1, 0, 0, null, 0)]
    [InlineData("3.2", 3, 2, 0, null, 0)]
    [InlineData("3", 3, 0, 0, null, 0)]
    public void TryParse_ValidRelease_ParsesCorrectly(
        string input, int major, int minor, int patch, string? label, int preNum)
    {
        Assert.True(FirmwareVersion.TryParse(input, out var v));
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.Equal(label, v.PreLabel);
        Assert.Equal(preNum, v.PreNumber);
        Assert.False(v.IsPreRelease);
    }

    [Theory]
    [InlineData("3.2.0b2", 3, 2, 0, "b", 2)]
    [InlineData("v3.2.0rc1", 3, 2, 0, "rc", 1)]
    [InlineData("1.0.0alpha", 1, 0, 0, "alpha", 0)]
    [InlineData("2.1.0beta3", 2, 1, 0, "beta", 3)]
    [InlineData("v1.0.0pre", 1, 0, 0, "pre", 0)]
    // PreLabel keeps the raw spelling and the raw case of the suffix. Equality normalizes
    // label aliases (see Equals_AliasLabels_*), but the parsed label itself is never rewritten:
    // "b" stays "b", "beta" stays "beta", and "B" stays "B".
    [InlineData("3.2.0b1", 3, 2, 0, "b", 1)]
    [InlineData("3.2.0beta1", 3, 2, 0, "beta", 1)]
    [InlineData("3.2.0B1", 3, 2, 0, "B", 1)]
    [InlineData("1.0.0dev2", 1, 0, 0, "dev", 2)]
    [InlineData("v2.0.0RC1", 2, 0, 0, "RC", 1)]
    public void TryParse_ValidPreRelease_ParsesCorrectly(
        string input, int major, int minor, int patch, string? label, int preNum)
    {
        Assert.True(FirmwareVersion.TryParse(input, out var v));
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.Equal(label, v.PreLabel);
        Assert.Equal(preNum, v.PreNumber);
        Assert.True(v.IsPreRelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("abc")]
    // A digit run that overflows Int32 matches the regex but must NOT throw from a TryParse —
    // it is reported as unparseable so version-gated callers fail closed (regression for the
    // int.Parse/int.TryParse asymmetry on the major component).
    [InlineData("999999999999999999.0.0")]
    [InlineData("2147483648.0.0")]
    // A PRESENT minor/patch that overflows rejects the whole version too (symmetric with major) —
    // it must not silently truncate to 0. (An ABSENT minor/patch still defaults to 0, e.g. "3".)
    [InlineData("3.999999999999999999.0")]
    [InlineData("3.0.999999999999999999")]
    public void TryParse_Invalid_ReturnsFalse(string? input)
    {
        Assert.False(FirmwareVersion.TryParse(input, out _));
    }

    [Theory]
    [InlineData(" v3.2.0 ", 3, 2, 0)]
    [InlineData("  3.2.0  ", 3, 2, 0)]
    public void TryParse_WithWhitespace_TrimsAndParses(string input, int major, int minor, int patch)
    {
        Assert.True(FirmwareVersion.TryParse(input, out var v));
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
    }

    [Fact]
    public void CompareTo_SameVersion_ReturnsZero()
    {
        FirmwareVersion.TryParse("3.2.0", out var a);
        FirmwareVersion.TryParse("3.2.0", out var b);
        Assert.Equal(0, a.CompareTo(b));
    }

    [Fact]
    public void CompareTo_MajorTakesPrecedence()
    {
        FirmwareVersion.TryParse("4.0.0", out var v4);
        FirmwareVersion.TryParse("3.9.9", out var v3);
        Assert.True(v4 > v3);
    }

    [Fact]
    public void CompareTo_MinorTakesPrecedence()
    {
        FirmwareVersion.TryParse("3.2.0", out var higher);
        FirmwareVersion.TryParse("3.1.9", out var lower);
        Assert.True(higher > lower);
    }

    [Fact]
    public void CompareTo_PatchCompared()
    {
        FirmwareVersion.TryParse("3.2.1", out var higher);
        FirmwareVersion.TryParse("3.2.0", out var lower);
        Assert.True(higher > lower);
    }

    [Fact]
    public void CompareTo_ReleaseGreaterThanPreRelease()
    {
        FirmwareVersion.TryParse("3.2.0", out var release);
        FirmwareVersion.TryParse("3.2.0rc1", out var rc);
        Assert.True(release > rc);
    }

    [Fact]
    public void CompareTo_RcGreaterThanBeta()
    {
        FirmwareVersion.TryParse("3.2.0rc1", out var rc);
        FirmwareVersion.TryParse("3.2.0b1", out var beta);
        Assert.True(rc > beta);
    }

    [Fact]
    public void CompareTo_BetaGreaterThanAlpha()
    {
        FirmwareVersion.TryParse("3.2.0beta1", out var beta);
        FirmwareVersion.TryParse("3.2.0alpha1", out var alpha);
        Assert.True(beta > alpha);
    }

    [Fact]
    public void CompareTo_SamePreLabel_HigherNumberWins()
    {
        FirmwareVersion.TryParse("3.2.0b2", out var b2);
        FirmwareVersion.TryParse("3.2.0b1", out var b1);
        Assert.True(b2 > b1);
    }

    [Fact]
    public void Compare_Strings_BothValid()
    {
        Assert.True(FirmwareVersion.Compare("3.2.0", "3.1.0") > 0);
        Assert.True(FirmwareVersion.Compare("3.1.0", "3.2.0") < 0);
        Assert.Equal(0, FirmwareVersion.Compare("3.2.0", "3.2.0"));
    }

    [Fact]
    public void Compare_Strings_InvalidSortsBefore()
    {
        Assert.True(FirmwareVersion.Compare(null, "3.2.0") < 0);
        Assert.True(FirmwareVersion.Compare("3.2.0", null) > 0);
        Assert.Equal(0, FirmwareVersion.Compare(null, null));
        Assert.Equal(0, FirmwareVersion.Compare("invalid", "alsobad"));
    }

    [Theory]
    [InlineData("3.2.0", "3.2.0")]
    [InlineData("v3.2.0", "3.2.0")]
    [InlineData("3.2.0b2", "3.2.0b2")]
    [InlineData("v1.0.0rc1", "1.0.0rc1")]
    [InlineData("2.0.0alpha", "2.0.0alpha")]
    // ToString round-trips the raw label verbatim. Two versions that are now equal
    // ("3.2.0b2" and "3.2.0beta2") still print differently, because the string is what the
    // device reported and a user has to be able to recognize it.
    [InlineData("3.2.0beta2", "3.2.0beta2")]
    [InlineData("1.0.0B2", "1.0.0B2")]
    [InlineData("v2.0.0RC1", "2.0.0RC1")]
    public void ToString_FormatsCorrectly(string input, string expected)
    {
        FirmwareVersion.TryParse(input, out var v);
        Assert.Equal(expected, v.ToString());
    }

    /// <summary>
    /// Pins the ordering that <see cref="FirmwareVersion.CompareTo"/> produces across every
    /// spelling of a pre-release label. Normalizing equality must not move any of these.
    /// </summary>
    [Theory]
    // release beats every pre-release, whichever way the label is spelled
    [InlineData("3.2.0", "3.2.0rc1", 1)]
    [InlineData("3.2.0", "3.2.0releasecandidate1", 1)]
    [InlineData("3.2.0", "3.2.0b1", 1)]
    [InlineData("3.2.0", "3.2.0beta1", 1)]
    [InlineData("3.2.0", "3.2.0dev1", 1)]
    // rc beats beta beats alpha, across spellings and across cases
    [InlineData("3.2.0rc1", "3.2.0beta1", 1)]
    [InlineData("3.2.0RC1", "3.2.0b1", 1)]
    [InlineData("3.2.0releasecandidate1", "3.2.0beta1", 1)]
    [InlineData("3.2.0beta1", "3.2.0alpha1", 1)]
    [InlineData("3.2.0b1", "3.2.0a1", 1)]
    [InlineData("3.2.0B1", "3.2.0ALPHA1", 1)]
    [InlineData("3.2.0beta1", "3.2.0dev1", 1)]
    [InlineData("3.2.0beta1", "3.2.0preview1", 1)]
    // the pre-release number breaks a tie between two spellings of the same rank
    [InlineData("3.2.0beta2", "3.2.0b1", 1)]
    [InlineData("3.2.0b2", "3.2.0beta1", 1)]
    [InlineData("3.2.0b1", "3.2.0beta2", -1)]
    // major, minor and patch still outrank the label
    [InlineData("4.0.0alpha1", "3.9.9", 1)]
    [InlineData("3.3.0a1", "3.2.0", 1)]
    [InlineData("3.2.1a1", "3.2.0", 1)]
    public void CompareTo_AcrossLabelSpellings_OrdersByRank(string left, string right, int expectedSign)
    {
        Assert.True(FirmwareVersion.TryParse(left, out var l));
        Assert.True(FirmwareVersion.TryParse(right, out var r));

        Assert.Equal(expectedSign, Math.Sign(l.CompareTo(r)));
        Assert.Equal(-expectedSign, Math.Sign(r.CompareTo(l)));
    }

    /// <summary>
    /// Every pair here is one version written two ways. <see cref="FirmwareVersion.CompareTo"/>
    /// has always ranked them the same, so equality and the hash code have to agree (issue #843).
    /// </summary>
    public static TheoryData<string, string> AliasPairs() => new()
    {
        { "3.2.0b1", "3.2.0beta1" },
        { "3.2.0a1", "3.2.0alpha1" },
        { "1.0.0rc1", "1.0.0releasecandidate1" },
        { "1.0.0pre", "1.0.0preview" },
        { "1.0.0pre", "1.0.0dev" },
        { "1.0.0alpha", "1.0.0dev" },
        { "2.1.0b3", "2.1.0beta3" },
        // the label rank is case-insensitive, so the case of the spelling does not matter either
        { "3.2.0B1", "3.2.0beta1" },
        { "1.0.0RC2", "1.0.0rc2" },
        { "3.2.0BETA1", "3.2.0b1" },
    };

    [Theory]
    [MemberData(nameof(AliasPairs))]
    public void Equals_AliasLabels_AreEqual(string left, string right)
    {
        Assert.True(FirmwareVersion.TryParse(left, out var l));
        Assert.True(FirmwareVersion.TryParse(right, out var r));

        Assert.Equal(0, l.CompareTo(r));
        Assert.True(l.Equals(r));
        Assert.True(r.Equals(l));
        Assert.True(l == r);
        Assert.False(l != r);
        Assert.Equal(l, r);
    }

    [Theory]
    [MemberData(nameof(AliasPairs))]
    public void GetHashCode_AliasLabels_AreEqual(string left, string right)
    {
        Assert.True(FirmwareVersion.TryParse(left, out var l));
        Assert.True(FirmwareVersion.TryParse(right, out var r));

        Assert.Equal(l.GetHashCode(), r.GetHashCode());
    }

    [Theory]
    // a different label rank, a different pre-release number, a pre-release against its
    // release, and a different patch all stay unequal
    [InlineData("3.2.0b1", "3.2.0rc1")]
    [InlineData("3.2.0beta1", "3.2.0alpha1")]
    [InlineData("3.2.0b1", "3.2.0b2")]
    [InlineData("3.2.0b1", "3.2.0beta2")]
    [InlineData("3.2.0", "3.2.0b1")]
    [InlineData("3.2.0", "3.2.1")]
    [InlineData("3.2.0", "4.2.0")]
    [InlineData("3.2.0", "3.3.0")]
    public void Equals_DifferentVersions_AreNotEqual(string left, string right)
    {
        Assert.True(FirmwareVersion.TryParse(left, out var l));
        Assert.True(FirmwareVersion.TryParse(right, out var r));

        Assert.NotEqual(0, l.CompareTo(r));
        Assert.False(l.Equals(r));
        Assert.False(l == r);
        Assert.True(l != r);
    }

    [Fact]
    public void Distinct_AliasLabels_CollapseToOneVersion()
    {
        FirmwareVersion.TryParse("3.2.0b1", out var b1);
        FirmwareVersion.TryParse("3.2.0beta1", out var beta1);
        FirmwareVersion.TryParse("3.2.0B1", out var upperB1);

        var distinct = new[] { b1, beta1, upperB1 }.Distinct().ToList();

        Assert.Single(distinct);
    }

    [Fact]
    public void HashSet_AliasLabels_HoldOneVersion()
    {
        FirmwareVersion.TryParse("1.0.0rc2", out var rc2);
        FirmwareVersion.TryParse("1.0.0releasecandidate2", out var releaseCandidate2);

        var set = new HashSet<FirmwareVersion> { rc2 };

        Assert.Contains(releaseCandidate2, set);
        Assert.False(set.Add(releaseCandidate2));
        Assert.Single(set);
    }

    [Fact]
    public void DictionaryKey_AliasLabels_ResolveToOneEntry()
    {
        FirmwareVersion.TryParse("2.1.0beta3", out var beta3);
        FirmwareVersion.TryParse("2.1.0b3", out var b3);

        var map = new Dictionary<FirmwareVersion, string> { [beta3] = "release notes" };

        Assert.True(map.TryGetValue(b3, out var notes));
        Assert.Equal("release notes", notes);
    }

    /// <summary>
    /// The general contract: across a matrix of every spelling in play, a zero
    /// <see cref="FirmwareVersion.CompareTo"/> result and <c>Equals</c> always agree.
    /// </summary>
    [Fact]
    public void Equals_AgreesWithCompareTo_AcrossEveryPair()
    {
        string[] inputs =
        [
            "3.2.0", "3.2.1", "3.3.0", "4.2.0",
            "3.2.0a1", "3.2.0alpha1", "3.2.0b1", "3.2.0beta1", "3.2.0B1", "3.2.0b2",
            "3.2.0rc1", "3.2.0releasecandidate1", "3.2.0RC1",
            "3.2.0pre", "3.2.0preview", "3.2.0dev",
        ];

        var versions = inputs.Select(s =>
        {
            Assert.True(FirmwareVersion.TryParse(s, out var v));
            return v;
        }).ToList();

        foreach (var left in versions)
        {
            foreach (var right in versions)
            {
                var ranksEqual = left.CompareTo(right) == 0;

                Assert.Equal(ranksEqual, left.Equals(right));
                if (ranksEqual)
                {
                    Assert.Equal(left.GetHashCode(), right.GetHashCode());
                }
            }
        }
    }

    /// <summary>
    /// An unknown label carries no rank of its own, so <c>CompareTo</c> puts every unknown
    /// label at the lowest rank. Equality follows it, which is the point of the fix: two
    /// unrecognized labels with the same number are one version.
    /// </summary>
    [Fact]
    public void Equals_UnknownLabels_FollowCompareTo()
    {
        FirmwareVersion.TryParse("1.0.0foo1", out var foo1);
        FirmwareVersion.TryParse("1.0.0bar1", out var bar1);

        Assert.Equal(0, foo1.CompareTo(bar1));
        Assert.Equal(foo1, bar1);
        Assert.Equal(foo1.GetHashCode(), bar1.GetHashCode());

        // an unknown label still sorts below a known pre-release label and below the release
        FirmwareVersion.TryParse("1.0.0b1", out var b1);
        Assert.True(b1 > foo1);
    }

    /// <summary>
    /// An empty label is not a pre-release, exactly as a missing label is not. The two were
    /// unequal before the fix because the raw strings differed.
    /// </summary>
    [Fact]
    public void Equals_EmptyLabel_MatchesMissingLabel()
    {
        var missing = new FirmwareVersion(3, 2, 0, null, 0);
        var empty = new FirmwareVersion(3, 2, 0, string.Empty, 0);

        Assert.False(missing.IsPreRelease);
        Assert.False(empty.IsPreRelease);
        Assert.Equal(0, missing.CompareTo(empty));
        Assert.Equal(missing, empty);
        Assert.Equal(missing.GetHashCode(), empty.GetHashCode());
    }

    [Fact]
    public void Operators_LessThanOrEqual_GreaterThanOrEqual()
    {
        FirmwareVersion.TryParse("3.2.0", out var a);
        FirmwareVersion.TryParse("3.2.0", out var b);
        FirmwareVersion.TryParse("3.1.0", out var c);

        Assert.True(a <= b);
        Assert.True(a >= b);
        Assert.True(c <= a);
        Assert.True(c < a);
        Assert.True(a >= c);
    }
}
