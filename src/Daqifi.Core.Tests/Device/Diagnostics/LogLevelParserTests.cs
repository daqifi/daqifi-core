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
