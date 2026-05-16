using Daqifi.Core.Device.SdCard;

namespace Daqifi.Core.Tests.Device.SdCard;

public class SdCardSpaceParserTests
{
    [Fact]
    public void TryParse_ValidResponse_ReturnsStorageInfo()
    {
        Assert.True(SdCardSpaceParser.TryParse("1048576000,2097152000", out var result));
        Assert.NotNull(result);
        Assert.Equal(1_048_576_000L, result.FreeBytes);
        Assert.Equal(2_097_152_000L, result.TotalBytes);
        Assert.Equal(1_048_576_000L, result.UsedBytes);
    }

    [Fact]
    public void TryParse_WithSurroundingWhitespace_ReturnsStorageInfo()
    {
        Assert.True(SdCardSpaceParser.TryParse("  100 , 500  ", out var result));
        Assert.NotNull(result);
        Assert.Equal(100L, result.FreeBytes);
        Assert.Equal(500L, result.TotalBytes);
    }

    [Fact]
    public void TryParse_FullCard_ReturnsZeroFree()
    {
        Assert.True(SdCardSpaceParser.TryParse("0,1000000", out var result));
        Assert.NotNull(result);
        Assert.Equal(0L, result.FreeBytes);
        Assert.Equal(1_000_000L, result.TotalBytes);
        Assert.Equal(1_000_000L, result.UsedBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_NullOrWhitespace_ReturnsFalse(string? input)
    {
        Assert.False(SdCardSpaceParser.TryParse(input, out var result));
        Assert.Null(result);
    }

    [Theory]
    [InlineData("1048576000")]            // no comma
    [InlineData(",2097152000")]           // missing free
    [InlineData("1048576000,")]           // missing total
    [InlineData("abc,2097152000")]        // non-numeric free
    [InlineData("1048576000,xyz")]        // non-numeric total
    [InlineData("-1,2097152000")]         // negative free
    [InlineData("1048576000,-1")]         // negative total
    [InlineData("**ERROR: -200, \"Execution error\"")] // SCPI error
    public void TryParse_Malformed_ReturnsFalse(string input)
    {
        Assert.False(SdCardSpaceParser.TryParse(input, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void TryParseLines_FindsFirstValidLine()
    {
        var lines = new[]
        {
            "",
            "some preamble",
            "1024,4096",
            "trailing text"
        };

        Assert.True(SdCardSpaceParser.TryParseLines(lines, out var result));
        Assert.NotNull(result);
        Assert.Equal(1024L, result.FreeBytes);
        Assert.Equal(4096L, result.TotalBytes);
    }

    [Fact]
    public void TryParseLines_NoValidLines_ReturnsFalse()
    {
        var lines = new[] { "", "garbage", "**ERROR: -200" };

        Assert.False(SdCardSpaceParser.TryParseLines(lines, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void TryParseLines_EmptySequence_ReturnsFalse()
    {
        Assert.False(SdCardSpaceParser.TryParseLines([], out var result));
        Assert.Null(result);
    }

    [Fact]
    public void UsedBytes_ComputesDifference()
    {
        var info = new SdCardStorageInfo(FreeBytes: 300, TotalBytes: 1000);
        Assert.Equal(700L, info.UsedBytes);
    }
}
