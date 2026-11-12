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

    // The full SYSTem:STReam:STATS? answer of firmware 3.9.0 (default build: READ_LOOP_PROFILE on,
    // PB_PROFILE_COUNTERS off), every key in SCPI_GetStreamStats' emit order at tag v3.9.0. The
    // numbers are the release notes' known CSV-over-WiFi case: one T2 channel near 4.7 kHz for
    // 60 s, losing 0.44% of its samples at the sample pool -- while SampleLossPercent says 0.
    private static readonly string[] V390Response =
    {
        "TotalSamplesStreamed=280759",
        "TotalBytesStreamed=3930626",
        "QueueDroppedSamples=1241",
        "QueueDroppedSamplesSteady=1241",
        "PoolExhaustedSamples=1241",
        "QueueOverflowSamples=0",
        "UsbDroppedBytes=0",
        "UsbDroppedBytesSteady=0",
        "WifiDroppedBytes=0",
        "WifiDroppedBytesSteady=0",
        "WifiTcpBytesSent=3930626",
        "WifiTcpBytesConfirmed=3929602",
        "WifiTcpSendErrors=0",
        "WifiTcpPartialSends=0",
        "WifiPartialBytesMissing=0",
        "WifiTcpOverBytesExtra=0",
        "WifiTcpInflightOverflow=5312",
        "WifiWriteBufferRejectedCalls=0",
        "WifiWriteBufferRejectedBytes=0",
        "WifiCirbufProduced=3930626",
        "WifiCirbufConsumed=3930626",
        "WifiCirbufBufSize=16384",
        "WifiListenUptimeSec=74",
        "WifiListenState=1",
        "WifiSocketOpenFails=0",
        "WifiListenFails=0",
        "WifiAcceptFails=0",
        "WifiAcceptRefused=0",
        "WifiClientForceClosed=0",
        "WifiListenReopens=0",
        "WifiListenHardResets=0",
        "WifiIdleClosed=0",
        "UsbBytesSent=2048",
        "SdDroppedBytes=0",
        "SdDroppedBytesSteady=0",
        "SdWriteCalls=0",
        "SdWriteSectors=0",
        "SdBytesWritten=0",
        "SdWriteErrors=0",
        "SdWriteMaxLatencyMs=0",
        "SdWriteAlignedCopies=0",
        "EncoderFailures=0",
        "EncoderFailuresSteady=0",
        "EncoderDroppedSamples=0",
        "EncoderDroppedSamplesSteady=0",
        "DioDroppedSamples=0",
        "DioDroppedSamplesSteady=0",
        "EosOverruns=0",
        "CatchUpSamples=0",
        "ScanStaleDropped=2",
        "ScanStaleDroppedSteady=0",
        "ClippedSamples=0",
        "ClippedChannelMask=0",
        "T1ArdyMisses=0",
        "ReadLoopMaxNs=3818",
        "ReadLoopMeanNs=1214",
        "TimerISRCalls=282000",
        "CircularBufferEndBytes=0",
        "SamplePoolMaxUsed=1100",
        "SampleLossPercent=0",
        "ByteLossPercent=0",
        "WindowLossPercent=0",
    };

    [Fact]
    public void TryParse_V390Response_ExposesTheNewCountersAsProperties()
    {
        var ok = StreamStatsParser.TryParse(V390Response, out var stats);

        Assert.True(ok);
        Assert.NotNull(stats);
        Assert.Equal(62, stats!.Values.Count);
        Assert.Equal(1241UL, stats.PoolExhaustedSamples);
        Assert.Equal(0UL, stats.QueueOverflowSamples);
        Assert.Equal(0UL, stats.ScanStaleDroppedSteady);
        Assert.Equal(3818UL, stats.ReadLoopMaxNs);
        Assert.Equal(1214UL, stats.ReadLoopMeanNs);
        Assert.Equal(0UL, stats.WifiTcpOverBytesExtra);
        Assert.Equal(5312UL, stats.WifiTcpInflightOverflow);

        // The firmware's own invariant, which the loss figure depends on: the split adds up to the
        // aggregate, and every timer tick is either streamed or dropped.
        Assert.Equal(stats.QueueDroppedSamples, stats.PoolExhaustedSamples + stats.QueueOverflowSamples);
        Assert.Equal(stats.TimerISRCalls, stats.TotalSamplesStreamed + stats.QueueDroppedSamples);
    }

    [Fact]
    public void PreciseSampleLossPercent_V390Response_ReportsTheSubOnePercentLossTheFirmwareRoundsToZero()
    {
        var ok = StreamStatsParser.TryParse(V390Response, out var stats);

        Assert.True(ok);
        // The trap the release notes call out: the firmware's whole-number field reads 0...
        Assert.Equal(0UL, stats!.Values["SampleLossPercent"]);
        // ...while 1241 of 282000 attempted samples were dropped.
        Assert.Equal(1241.0 * 100.0 / 282000.0, stats.PreciseSampleLossPercent!.Value, 12);
        Assert.Equal(0.44, Math.Round(stats.PreciseSampleLossPercent.Value, 2));
    }

    [Theory]
    [InlineData(1000UL, 0UL)]
    [InlineData(999UL, 1UL)]
    [InlineData(990UL, 10UL)]
    [InlineData(97UL, 3UL)]
    [InlineData(2UL, 1UL)]
    [InlineData(0UL, 5UL)]
    public void PreciseSampleLossPercent_FlooredMatchesTheFirmwaresWholeNumberFigure(ulong streamed, ulong dropped)
    {
        // Same arithmetic as SCPI_GetStreamStats -- dropped * 100 / (streamed + dropped), integer
        // division -- so the precise figure only ever adds the fraction the firmware truncated.
        var firmwareWholePercent = dropped * 100UL / (streamed + dropped);
        var stats = new StreamStats
        {
            Values = new Dictionary<string, ulong>
            {
                ["TotalSamplesStreamed"] = streamed,
                ["QueueDroppedSamples"] = dropped,
            },
        };

        Assert.Equal((double)firmwareWholePercent, Math.Floor(stats.PreciseSampleLossPercent!.Value));
    }

    [Fact]
    public void PreciseSampleLossPercent_DoesNotAddTheSubCountersOrStaleScansToTheDroppedCount()
    {
        // PoolExhausted + QueueOverflow are already inside QueueDroppedSamples, and stale-scan
        // samples were still streamed. Counting any of them again would double-count.
        var stats = new StreamStats
        {
            Values = new Dictionary<string, ulong>
            {
                ["TotalSamplesStreamed"] = 900,
                ["QueueDroppedSamples"] = 100,
                ["PoolExhaustedSamples"] = 60,
                ["QueueOverflowSamples"] = 40,
                ["ScanStaleDroppedSteady"] = 500,
            },
        };

        Assert.Equal(10.0, stats.PreciseSampleLossPercent);
    }

    [Fact]
    public void PreciseSampleLossPercent_WhenNothingWasAttempted_IsZero()
    {
        // Matches the firmware, which reports SampleLossPercent=0 for an empty session.
        var stats = new StreamStats
        {
            Values = new Dictionary<string, ulong>
            {
                ["TotalSamplesStreamed"] = 0,
                ["QueueDroppedSamples"] = 0,
            },
        };

        Assert.Equal(0.0, stats.PreciseSampleLossPercent);
    }

    [Fact]
    public void PreciseSampleLossPercent_DoesNotWrapOnMaximalCounters()
    {
        var stats = new StreamStats
        {
            Values = new Dictionary<string, ulong>
            {
                ["TotalSamplesStreamed"] = ulong.MaxValue,
                ["QueueDroppedSamples"] = ulong.MaxValue,
            },
        };

        Assert.Equal(50.0, stats.PreciseSampleLossPercent);
    }

    [Theory]
    [InlineData("TotalSamplesStreamed")]
    [InlineData("QueueDroppedSamples")]
    public void PreciseSampleLossPercent_WhenEitherCountIsAbsent_IsNull(string onlyKey)
    {
        var stats = new StreamStats { Values = new Dictionary<string, ulong> { [onlyKey] = 10 } };

        Assert.Null(stats.PreciseSampleLossPercent);
    }

    [Fact]
    public void TryParse_WhenFirmwareOmitsTheV390Counters_TheyReadNull()
    {
        // A 3.5.0-3.8.x device does not send these. Null, not zero: zero would claim "measured,
        // and none" for a counter the device never had. The loss figure still works there,
        // because its two inputs predate 3.9.0.
        var ok = StreamStatsParser.TryParse(SampleResponse, out var stats);

        Assert.True(ok);
        Assert.Null(stats!.QueueOverflowSamples);
        Assert.Null(stats.ScanStaleDroppedSteady);
        Assert.Null(stats.ReadLoopMaxNs);
        Assert.Null(stats.ReadLoopMeanNs);
        Assert.Null(stats.WifiTcpOverBytesExtra);
        Assert.Null(stats.WifiTcpInflightOverflow);
        Assert.Equal(0UL, stats.PoolExhaustedSamples);
        Assert.Equal(0.0, stats.PreciseSampleLossPercent);
    }
}
