using Daqifi.Core.Device.Diagnostics;

namespace Daqifi.Core.Tests.Device.Diagnostics;

public class LogLevelParserTests
{
    [Fact]
    public void TryParse_ParsesModuleLevelAndCeiling()
    {
        var ok = LogLevelParser.TryParse("STREAM: 2 (ceiling 3)", out var setting);

        Assert.True(ok);
        Assert.Equal("STREAM", setting!.Module);
        Assert.Equal(2, setting.Level);
        Assert.Equal(3, setting.Ceiling);
    }

    [Fact]
    public void TryParse_TrimsAndIsCaseInsensitiveOnCeilingKeyword()
    {
        var ok = LogLevelParser.TryParse("  WIFI: 1 (CEILING 3)\r\n", out var setting);

        Assert.True(ok);
        Assert.Equal("WIFI", setting!.Module);
        Assert.Equal(1, setting.Level);
        Assert.Equal(3, setting.Ceiling);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a level line")]
    [InlineData("STREAM: 2")]
    [InlineData("**ERROR: -224,\"Illegal parameter value\"")]
    public void TryParse_WhenUnparseable_ReturnsFalse(string line)
    {
        var ok = LogLevelParser.TryParse(line, out var setting);

        Assert.False(ok);
        Assert.Null(setting);
    }

    [Theory]
    // level overflows Int32 (> 2,147,483,647): must fail gracefully, not throw OverflowException.
    [InlineData("STREAM: 99999999999 (ceiling 3)")]
    // ceiling overflows Int32.
    [InlineData("STREAM: 2 (ceiling 99999999999)")]
    // both overflow.
    [InlineData("STREAM: 99999999999 (ceiling 99999999999)")]
    public void TryParse_WhenNumericGroupOverflowsInt32_ReturnsFalseWithoutThrowing(string line)
    {
        var ok = LogLevelParser.TryParse(line, out var setting);

        Assert.False(ok);
        Assert.Null(setting);
    }

    [Fact]
    public void TryParseLines_SkipsAnOverflowingLineAndParsesTheNext()
    {
        // A line that matches the regex shape but carries an out-of-range level must
        // not throw — it should be treated as unparseable so a later valid line wins.
        var lines = new[] { "STREAM: 99999999999 (ceiling 3)", "ADC: 1 (ceiling 3)" };

        var ok = LogLevelParser.TryParseLines(lines, out var setting);

        Assert.True(ok);
        Assert.Equal("ADC", setting!.Module);
        Assert.Equal(1, setting.Level);
        Assert.Equal(3, setting.Ceiling);
    }

    [Fact]
    public void TryParse_ParsesInt32MaxValueBoundary()
    {
        var ok = LogLevelParser.TryParse("STREAM: 2147483647 (ceiling 2147483647)", out var setting);

        Assert.True(ok);
        Assert.Equal(int.MaxValue, setting!.Level);
        Assert.Equal(int.MaxValue, setting.Ceiling);
    }

    [Fact]
    public void TryParseLines_ReturnsFirstParseableLine()
    {
        var lines = new[] { "garbage", "ADC: 0 (ceiling 3)", "DAC: 3 (ceiling 3)" };

        var ok = LogLevelParser.TryParseLines(lines, out var setting);

        Assert.True(ok);
        Assert.Equal("ADC", setting!.Module);
        Assert.Equal(0, setting.Level);
    }

    [Fact]
    public void TryParseLines_WhenNoneParse_ReturnsFalse()
    {
        var ok = LogLevelParser.TryParseLines(new[] { "a", "b" }, out var setting);

        Assert.False(ok);
        Assert.Null(setting);
    }
}
