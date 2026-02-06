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
    public void CompareTo_NewerVersion_ReturnsPositive()
    {
        FirmwareVersion.TryParse("3.2.0", out var newer);
        FirmwareVersion.TryParse("3.1.0", out var older);
        Assert.True(newer.CompareTo(older) > 0);
        Assert.True(newer > older);
    }

    [Fact]
    public void CompareTo_OlderVersion_ReturnsNegative()
    {
        FirmwareVersion.TryParse("2.0.0", out var older);
        FirmwareVersion.TryParse("3.0.0", out var newer);
        Assert.True(older.CompareTo(newer) < 0);
        Assert.True(older < newer);
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
    public void ToString_FormatsCorrectly(string input, string expected)
    {
        FirmwareVersion.TryParse(input, out var v);
        Assert.Equal(expected, v.ToString());
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
        Assert.True(a >= c);
    }
}
