using System.Collections.Generic;
using Daqifi.Core.Device.Diagnostics;

namespace Daqifi.Core.Tests.Device.Diagnostics;

public class StreamStatsParserTests
{
    // Representative subset of a real SYSTem:STReam:STATS? response (the device
    // emits 40+ Key=Value lines; the parser keeps every numeric pair).
    private static readonly string[] SampleResponse =
    {
        "TotalSamplesStreamed=15000",
        "TotalBytesStreamed=480000",
        "QueueDroppedSamples=0",
        "PoolExhaustedSamples=0",
        "UsbDroppedBytes=0",
        "WifiTcpBytesSent=480000",
        "TimerISRCalls=15000",
        "SamplePoolMaxUsed=12",
    };

    [Fact]
    public void TryParse_ParsesHeadlineCountersAndRawValues()
    {
        var ok = StreamStatsParser.TryParse(SampleResponse, out var stats);

        Assert.True(ok);
        Assert.NotNull(stats);
        Assert.Equal(15000UL, stats!.TotalSamplesStreamed);
        Assert.Equal(480000UL, stats.TotalBytesStreamed);
        Assert.Equal(0UL, stats.QueueDroppedSamples);
        Assert.Equal(15000UL, stats.TimerISRCalls);
        // Fields without a typed accessor are still available via Values.
        Assert.Equal(480000UL, stats.Values["WifiTcpBytesSent"]);
        Assert.Equal(8, stats.Values.Count);
    }

    [Fact]
    public void TryParse_HandlesLargeUInt64Values()
    {
        // 64-bit counters (%llu in firmware) must not overflow.
        var lines = new[] { "TotalBytesStreamed=18446744073709551615" };

        var ok = StreamStatsParser.TryParse(lines, out var stats);

        Assert.True(ok);
        Assert.Equal(ulong.MaxValue, stats!.TotalBytesStreamed);
    }

    [Fact]
    public void TryParse_TrimsTrailingCarriageReturnsAndSkipsBlankLines()
    {
        var lines = new[] { "HeapNoise=1\r", "", "   ", "TotalSamplesStreamed=42\r" };

        var ok = StreamStatsParser.TryParse(lines, out var stats);

        Assert.True(ok);
        Assert.Equal(42UL, stats!.TotalSamplesStreamed);
        Assert.Equal(1UL, stats.Values["HeapNoise"]);
    }

    [Fact]
    public void TryParse_SkipsNonNumericAndErrorLines()
    {
        var lines = new[]
        {
            "**ERROR: -200,\"Execution error\"",
            "BuildInfo=notanumber",
            "TotalSamplesStreamed=7",
        };

        var ok = StreamStatsParser.TryParse(lines, out var stats);

        Assert.True(ok);
        Assert.Single(stats!.Values);
        Assert.Equal(7UL, stats.TotalSamplesStreamed);
    }

    [Fact]
    public void TryParse_MissingHeadlineFieldsReturnNull()
    {
        var ok = StreamStatsParser.TryParse(new[] { "SomeOtherField=3" }, out var stats);

        Assert.True(ok);
        Assert.Null(stats!.TotalSamplesStreamed);
        Assert.Null(stats.QueueDroppedSamples);
    }

    [Fact]
    public void TryParse_WhenNoParseablePairs_ReturnsFalse()
    {
        var ok = StreamStatsParser.TryParse(new[] { "**ERROR: -200,\"Execution error\"", "" }, out var stats);

        Assert.False(ok);
        Assert.Null(stats);
    }

    [Fact]
    public void TryParse_WhenEmpty_ReturnsFalse()
    {
        var ok = StreamStatsParser.TryParse(new List<string>(), out var stats);

        Assert.False(ok);
        Assert.Null(stats);
    }
}
