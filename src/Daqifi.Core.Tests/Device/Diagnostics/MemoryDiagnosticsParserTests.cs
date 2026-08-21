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

    // The full 17-field answer a real Nq1 on firmware 3.7.2 gives, values as measured on the
    // bench (issue #536). The device was nearly out of heap, which is what makes it a useful
    // fixture: HeapFree looks merely tight until you see that the whole of it is one block.
    private static readonly string[] BenchResponse =
    {
        "HeapTotal=75000",
        "HeapFree=7544",
        "HeapUsed=67456",
        "HeapMinEverFree=7544",
        "LargestFreeBlock=7544",
        "SmallestFreeBlock=7544",
        "HeapFreeBlocks=1",
        "CoherentPoolTotal=32768",
        "CoherentPoolFree=16384",
        "SdCircularSize=512",
        "SamplePoolCount=1100",
        "SamplePoolInUse=4",
        "SamplePoolMaxUsed=12",
        "SamplePoolBytes=22000",
        "SampleNextFreeBytes=2200",
        "SampleQueueBytes=4480",
        "SampleElementBytes=20",
    };

    [Fact]
    public void TryParse_ExposesTheFragmentationAndPoolFieldsAsProperties()
    {
        // These eight parsed correctly all along -- they landed in Values with the right numbers.
        // What was missing was any way to reach them except by magic string, on the one question
        // a consumer actually asks this API: how close to the edge is the device?
        var ok = MemoryDiagnosticsParser.TryParse(BenchResponse, out var mem);

        Assert.True(ok);
        Assert.NotNull(mem);

        Assert.Equal(7544UL, mem!.LargestFreeBlock);
        Assert.Equal(7544UL, mem.SmallestFreeBlock);
        Assert.Equal(1UL, mem.HeapFreeBlocks);
        Assert.Equal(512UL, mem.SdCircularSize);
        Assert.Equal(22000UL, mem.SamplePoolBytes);
        Assert.Equal(2200UL, mem.SampleNextFreeBytes);
        Assert.Equal(4480UL, mem.SampleQueueBytes);
        Assert.Equal(20UL, mem.SampleElementBytes);

        // The reading the fields exist to make possible: the largest allocation still serviceable
        // is the entire free heap, because it is one unbroken run. Unfragmented -- and nearly out.
        Assert.Equal(mem.HeapFree, mem.LargestFreeBlock);
        Assert.Equal(17, mem.Values.Count);
    }

    [Fact]
    public void TryParse_WhenFirmwareOmitsTheNewFields_TheyReadNull()
    {
        // The field set varies by firmware version, which the type already documents. An older
        // device that does not send these must give null rather than zero -- zero would read as
        // "no free block at all", the alarming opposite of "did not say".
        var ok = MemoryDiagnosticsParser.TryParse(SampleResponse, out var mem);

        Assert.True(ok);
        Assert.NotNull(mem);
        Assert.Null(mem!.LargestFreeBlock);
        Assert.Null(mem.SmallestFreeBlock);
        Assert.Null(mem.HeapFreeBlocks);
        Assert.Null(mem.SamplePoolBytes);
        Assert.Null(mem.SampleNextFreeBytes);
        Assert.Null(mem.SampleQueueBytes);

        // Present in the older fixture, so these two must still resolve.
        Assert.Equal(8192UL, mem.SdCircularSize);
        Assert.Equal(32UL, mem.SampleElementBytes);
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
