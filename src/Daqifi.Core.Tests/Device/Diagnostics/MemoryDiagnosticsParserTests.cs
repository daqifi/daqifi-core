using Daqifi.Core.Device.Diagnostics;

namespace Daqifi.Core.Tests.Device.Diagnostics;

public class MemoryDiagnosticsParserTests
{
    // Mirrors the firmware SCPI_GetMemFree output (HeapTotal/HeapFree/... = value).
    private static readonly string[] SampleResponse =
    {
        "HeapTotal=75000",
        "HeapFree=45000",
        "HeapUsed=30000",
        "HeapMinEverFree=13000",
        "CoherentPoolTotal=32768",
        "CoherentPoolFree=16384",
        "SdCircularSize=8192",
        "SamplePoolCount=1100",
        "SampleElementBytes=32",
        "SamplePoolInUse=4",
        "SamplePoolMaxUsed=12",
    };

    [Fact]
    public void TryParse_ParsesHeadlineFieldsAndRawValues()
    {
        var ok = MemoryDiagnosticsParser.TryParse(SampleResponse, out var mem);

        Assert.True(ok);
        Assert.NotNull(mem);
        Assert.Equal(75000UL, mem!.HeapTotal);
        Assert.Equal(45000UL, mem.HeapFree);
        Assert.Equal(30000UL, mem.HeapUsed);
        Assert.Equal(13000UL, mem.HeapMinEverFree);
        Assert.Equal(32768UL, mem.CoherentPoolTotal);
        Assert.Equal(16384UL, mem.CoherentPoolFree);
        Assert.Equal(1100UL, mem.SamplePoolCount);
        Assert.Equal(4UL, mem.SamplePoolInUse);
        Assert.Equal(12UL, mem.SamplePoolMaxUsed);
        Assert.Equal(8192UL, mem.Values["SdCircularSize"]);
        Assert.Equal(11, mem.Values.Count);
    }

    [Fact]
    public void TryParse_MissingFieldsReturnNull()
    {
        var ok = MemoryDiagnosticsParser.TryParse(new[] { "HeapFree=100" }, out var mem);

        Assert.True(ok);
        Assert.Equal(100UL, mem!.HeapFree);
        Assert.Null(mem.HeapTotal);
        Assert.Null(mem.SamplePoolCount);
    }

    [Fact]
    public void TryParse_WhenNoParseablePairs_ReturnsFalse()
    {
        var ok = MemoryDiagnosticsParser.TryParse(new[] { "garbage", "" }, out var mem);

        Assert.False(ok);
        Assert.Null(mem);
    }
}
