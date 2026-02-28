using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

public class LanChipInfoParserTests
{
    [Fact]
    public void TryParse_ValidJson_ReturnsChipInfo()
    {
        const string json = """{"ChipId":5596,"FwVersion":"19.5.4","BuildDate":"Jan  8 2019 Time 12:38:49"}""";

        Assert.True(LanChipInfoParser.TryParse(json, out var result));
        Assert.NotNull(result);
        Assert.Equal(5596, result.ChipId);
        Assert.Equal("19.5.4", result.FwVersion);
        Assert.Equal("Jan  8 2019 Time 12:38:49", result.BuildDate);
    }

    [Fact]
    public void TryParse_JsonMissingBuildDate_UsesEmptyString()
    {
        const string json = """{"ChipId":1234,"FwVersion":"19.5.4"}""";

        Assert.True(LanChipInfoParser.TryParse(json, out var result));
        Assert.NotNull(result);
        Assert.Equal("19.5.4", result.FwVersion);
        Assert.Equal(string.Empty, result.BuildDate);
    }

    [Fact]
    public void TryParse_JsonMissingChipId_UsesZero()
    {
        const string json = """{"FwVersion":"19.5.4","BuildDate":"Jan  8 2019"}""";

        Assert.True(LanChipInfoParser.TryParse(json, out var result));
        Assert.NotNull(result);
        Assert.Equal(0, result.ChipId);
        Assert.Equal("19.5.4", result.FwVersion);
    }

    [Fact]
    public void TryParse_JsonMissingFwVersion_ReturnsFalse()
    {
        const string json = """{"ChipId":1234,"BuildDate":"Jan  8 2019"}""";

        Assert.False(LanChipInfoParser.TryParse(json, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_JsonEmptyFwVersion_ReturnsFalse()
    {
        const string json = """{"ChipId":1234,"FwVersion":"","BuildDate":"Jan  8 2019"}""";

        Assert.False(LanChipInfoParser.TryParse(json, out var result));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_NullOrWhitespace_ReturnsFalse(string? input)
    {
        Assert.False(LanChipInfoParser.TryParse(input, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_MalformedJson_ReturnsFalse()
    {
        Assert.False(LanChipInfoParser.TryParse("{not valid json", out var result));
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_NonJsonString_ReturnsFalse()
    {
        Assert.False(LanChipInfoParser.TryParse("plain text response", out var result));
        Assert.Null(result);
    }

    [Fact]
    public void TryParseLines_FindsFirstValidLine()
    {
        var lines = new[]
        {
            "",
            "some preamble",
            """{"ChipId":5596,"FwVersion":"19.5.4","BuildDate":"Jan  8 2019"}""",
            "trailing text"
        };

        Assert.True(LanChipInfoParser.TryParseLines(lines, out var result));
        Assert.NotNull(result);
        Assert.Equal("19.5.4", result.FwVersion);
    }

    [Fact]
    public void TryParseLines_NoValidLines_ReturnsFalse()
    {
        var lines = new[] { "", "not json", "also not json" };

        Assert.False(LanChipInfoParser.TryParseLines(lines, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void TryParseLines_EmptySequence_ReturnsFalse()
    {
        Assert.False(LanChipInfoParser.TryParseLines([], out var result));
        Assert.Null(result);
    }
}
