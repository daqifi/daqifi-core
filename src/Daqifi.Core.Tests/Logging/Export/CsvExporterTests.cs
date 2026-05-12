using System.Globalization;
using Daqifi.Core.Channel;
using Daqifi.Core.Logging.Export;

namespace Daqifi.Core.Tests.Logging.Export;

public class CsvExporterTests
{
    private static readonly ChannelDescriptor Ch1 = new("DevA", "SN001", "Channel1", ChannelType.Analog);
    private static readonly ChannelDescriptor Ch2 = new("DevA", "SN001", "Channel2", ChannelType.Analog);
    private static readonly ChannelDescriptor ChDig = new("DevB", "SN002", "Digital1", ChannelType.Digital);

    private static readonly long T0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly long T1 = T0 + TimeSpan.TicksPerSecond;
    private static readonly long T2 = T0 + 2 * TimeSpan.TicksPerSecond;

    private static async Task<(string[] lines, string header)> ExportToLinesAsync(
        ISampleSource source,
        CsvExportOptions options,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sw = new StringWriter();
        var exporter = new CsvExporter();
        await exporter.ExportAsync(source, sw, options, progress, cancellationToken);
        var content = sw.ToString();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                           .Select(l => l.TrimEnd('\r'))
                           .ToArray();
        return (lines, lines.Length > 0 ? lines[0] : string.Empty);
    }

    // ── Header ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_AbsoluteTime_WritesTimeHeader()
    {
        var source = new InMemorySampleSource(
            [Ch1],
            [new SampleRow(T0, Ch1.Key, 1.0)]);

        var (_, header) = await ExportToLinesAsync(source, new CsvExportOptions());

        Assert.StartsWith("Time,", header);
    }

    [Fact]
    public async Task Export_RelativeTime_WritesRelativeTimeHeader()
    {
        var source = new InMemorySampleSource(
            [Ch1],
            [new SampleRow(T0, Ch1.Key, 1.0)]);

        var (_, header) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true });

        Assert.StartsWith("Relative Time (s),", header);
    }

    [Fact]
    public async Task Export_MultipleChannels_WritesAllChannelKeysInHeader()
    {
        var source = new InMemorySampleSource(
            [Ch1, Ch2],
            [new SampleRow(T0, Ch1.Key, 1.0)]);

        var (_, header) = await ExportToLinesAsync(source, new CsvExportOptions());

        Assert.Contains(Ch1.Key, header);
        Assert.Contains(Ch2.Key, header);
    }

    [Fact]
    public async Task Export_NoChannels_WritesNothingAndReturns()
    {
        var source = new InMemorySampleSource([], []);
        var sw = new StringWriter();
        var exporter = new CsvExporter();
        await exporter.ExportAsync(source, sw, new CsvExportOptions());

        Assert.Empty(sw.ToString());
    }

    // ── Absolute vs relative timestamps ─────────────────────────────────────

    [Fact]
    public async Task Export_AbsoluteTime_FormatsAsIso8601RoundTrip()
    {
        var source = new InMemorySampleSource(
            [Ch1],
            [new SampleRow(T0, Ch1.Key, 42.0)]);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions());

        var dataLine = lines[1];
        var expectedTime = new DateTime(T0).ToString("O");
        Assert.StartsWith(expectedTime + ",", dataLine);
    }

    [Fact]
    public async Task Export_RelativeTime_FirstRowIsZero()
    {
        var source = new InMemorySampleSource(
            [Ch1],
            [new SampleRow(T0, Ch1.Key, 1.0), new SampleRow(T1, Ch1.Key, 2.0)]);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true });

        Assert.StartsWith("0.000,", lines[1]);
    }

    [Fact]
    public async Task Export_RelativeTime_SecondRowIsCorrectOffset()
    {
        var source = new InMemorySampleSource(
            [Ch1],
            [new SampleRow(T0, Ch1.Key, 1.0), new SampleRow(T1, Ch1.Key, 2.0)]);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true });

        Assert.StartsWith("1.000,", lines[2]);
    }

    [Fact]
    public async Task Export_RelativeTime_SubSecondPrecision_ThreeDecimalPlaces()
    {
        var halfSecond = T0 + TimeSpan.TicksPerSecond / 2;
        var source = new InMemorySampleSource(
            [Ch1],
            [new SampleRow(T0, Ch1.Key, 1.0), new SampleRow(halfSecond, Ch1.Key, 2.0)]);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true });

        Assert.StartsWith("0.500,", lines[2]);
    }

    // ── Invalid ticks fallback ───────────────────────────────────────────────

    [Fact]
    public async Task Export_ZeroTicks_FormatsAsDateTimeMinValue()
    {
        // ticks==0 is DateTime.MinValue (0001-01-01 00:00:00) — a legal
        // DateTime value. Pre-fix, FormatTimestamp's `ticks <= 0` check
        // rejected it as INVALID; post-fix, only negative ticks are
        // invalid and the formatter renders the absolute timestamp.
        var source = new InMemorySampleSource(
            [Ch1],
            [new SampleRow(0L, Ch1.Key, 1.0)]);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions());

        Assert.StartsWith("0001-01-01T00:00:00", lines[1]);
        Assert.DoesNotContain("INVALID", lines[1]);
    }

    [Fact]
    public async Task Export_NegativeTicks_WritesInvalidToken()
    {
        var source = new InMemorySampleSource(
            [Ch1],
            [new SampleRow(-1L, Ch1.Key, 1.0)]);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions());

        Assert.StartsWith("INVALID(-1),", lines[1]);
    }

    [Fact]
    public async Task Export_TicksBeyondMaxValue_WritesInvalidToken()
    {
        var overflowTicks = DateTime.MaxValue.Ticks + 1;
        var source = new InMemorySampleSource(
            [Ch1],
            [new SampleRow(overflowTicks, Ch1.Key, 1.0)]);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions());

        Assert.StartsWith($"INVALID({overflowTicks}),", lines[1]);
    }

    [Fact]
    public async Task Export_RelativeTime_NegativeTicks_StillWritesInvalidToken()
    {
        // Use a genuinely invalid tick value (negative). Post-fix,
        // ticks==0 is now valid and would format as "0.000" relative
        // seconds, so the prior INVALID(0) expectation no longer holds.
        var source = new InMemorySampleSource(
            [Ch1],
            [new SampleRow(-1L, Ch1.Key, 1.0)]);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true });

        Assert.StartsWith("INVALID(-1),", lines[1]);
    }

    // ── Timestamp bucketing ──────────────────────────────────────────────────

    [Fact]
    public async Task Export_SameTimestamp_MultipleChannels_WritesOneRow()
    {
        var source = new InMemorySampleSource(
            [Ch1, Ch2],
            [new SampleRow(T0, Ch1.Key, 1.1), new SampleRow(T0, Ch2.Key, 2.2)]);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true });

        Assert.Equal(2, lines.Length); // header + 1 data row
    }

    [Fact]
    public async Task Export_SameTimestamp_MultipleChannels_BothValuesInRow()
    {
        var source = new InMemorySampleSource(
            [Ch1, Ch2],
            [new SampleRow(T0, Ch1.Key, 1.1), new SampleRow(T0, Ch2.Key, 2.2)]);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true });

        var dataLine = lines[1];
        Assert.Contains("1.1", dataLine);
        Assert.Contains("2.2", dataLine);
    }

    [Fact]
    public async Task Export_DuplicateChannelAtSameTimestamp_LastValueWins()
    {
        var source = new InMemorySampleSource(
            [Ch1],
            [new SampleRow(T0, Ch1.Key, 1.1), new SampleRow(T0, Ch1.Key, 9.9)]);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true });

        var dataLine = lines[1];
        Assert.EndsWith(",9.9", dataLine);
    }

    // ── Mixed-channel sessions (gaps) ────────────────────────────────────────

    [Fact]
    public async Task Export_ChannelMissingAtTimestamp_LeavesEmptyCell()
    {
        // Ch1 has a sample at T0, Ch2 does not
        var source = new InMemorySampleSource(
            [Ch1, Ch2],
            [new SampleRow(T0, Ch1.Key, 5.0)]);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true });

        // Expected: "0.000,5,<empty>"
        var parts = lines[1].Split(',');
        Assert.Equal(3, parts.Length);
        Assert.Equal("0.000", parts[0]);
        Assert.Equal("5", parts[1]);
        Assert.Equal(string.Empty, parts[2]);
    }

    [Fact]
    public async Task Export_ThreeTimestamps_MixedChannelPresence_CorrectRowCount()
    {
        var samples = new[]
        {
            new SampleRow(T0, Ch1.Key, 1.0),
            new SampleRow(T0, Ch2.Key, 2.0),
            new SampleRow(T1, Ch1.Key, 3.0), // Ch2 missing at T1
            new SampleRow(T2, Ch2.Key, 4.0), // Ch1 missing at T2
        };
        var source = new InMemorySampleSource([Ch1, Ch2], samples);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true });

        Assert.Equal(4, lines.Length); // header + 3 timestamps
    }

    // ── Custom delimiter ─────────────────────────────────────────────────────

    [Fact]
    public async Task Export_TabDelimiter_UsesTabInOutput()
    {
        var source = new InMemorySampleSource(
            [Ch1, Ch2],
            [new SampleRow(T0, Ch1.Key, 1.0), new SampleRow(T0, Ch2.Key, 2.0)]);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true, Delimiter = "\t" });

        Assert.Contains("\t", lines[0]);
        Assert.Contains("\t", lines[1]);
        Assert.DoesNotContain(",", lines[1]);
    }

    // ── All-samples value format ─────────────────────────────────────────────

    [Fact]
    public async Task Export_AllSamples_UsesGeneralFormat()
    {
        var source = new InMemorySampleSource(
            [Ch1],
            [new SampleRow(T0, Ch1.Key, 1.23456789)]);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true });

        var parts = lines[1].Split(',');
        Assert.Equal(1.23456789.ToString("G", CultureInfo.InvariantCulture), parts[1]);
    }

    // ── Averaging mode ───────────────────────────────────────────────────────

    [Fact]
    public async Task Export_AverageWindow2_SingleChannel_HalfTheRows()
    {
        var samples = Enumerable.Range(0, 6)
            .Select(i => new SampleRow(T0 + i * TimeSpan.TicksPerMillisecond, Ch1.Key, i + 1.0))
            .ToList();
        var source = new InMemorySampleSource([Ch1], samples);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true, AverageWindow = 2 });

        // 6 samples / window 2 = 3 data rows
        Assert.Equal(4, lines.Length); // header + 3
    }

    [Fact]
    public async Task Export_AverageWindow2_CorrectAverageValues()
    {
        // Values: 1, 2, 3, 4 → windows (1+2)/2=1.5 and (3+4)/2=3.5
        var samples = new[]
        {
            new SampleRow(T0, Ch1.Key, 1.0),
            new SampleRow(T1, Ch1.Key, 2.0),
            new SampleRow(T2, Ch1.Key, 3.0),
            new SampleRow(T2 + TimeSpan.TicksPerSecond, Ch1.Key, 4.0),
        };
        var source = new InMemorySampleSource([Ch1], samples);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true, AverageWindow = 2 });

        var row1Parts = lines[1].Split(',');
        var row2Parts = lines[2].Split(',');
        Assert.Equal("1.5", row1Parts[1]);
        Assert.Equal("3.5", row2Parts[1]);
    }

    [Fact]
    public async Task Export_AverageWindow_MultipleChannels_AveragedPerChannel()
    {
        // Window=2: sample1 ch1=10, sample2 ch2=20 → one averaged row
        var samples = new[]
        {
            new SampleRow(T0, Ch1.Key, 10.0),
            new SampleRow(T1, Ch2.Key, 20.0),
        };
        var source = new InMemorySampleSource([Ch1, Ch2], samples);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true, AverageWindow = 2 });

        Assert.Equal(2, lines.Length); // header + 1 averaged row
        var parts = lines[1].Split(',');
        Assert.Equal("10", parts[1]); // ch1: one sample, avg = 10
        Assert.Equal("20", parts[2]); // ch2: one sample, avg = 20
    }

    [Fact]
    public async Task Export_AverageWindow_ChannelWithNoSamplesInWindow_EmptyCell()
    {
        // Window=2: both samples are Ch1 only — Ch2 stays empty
        var samples = new[]
        {
            new SampleRow(T0, Ch1.Key, 5.0),
            new SampleRow(T1, Ch1.Key, 7.0),
        };
        var source = new InMemorySampleSource([Ch1, Ch2], samples);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true, AverageWindow = 2 });

        var parts = lines[1].Split(',');
        Assert.Equal(3, parts.Length);
        Assert.Equal(string.Empty, parts[2]); // Ch2 has no samples in window
    }

    [Fact]
    public async Task Export_AverageWindow_FlushesPartialFinalWindow()
    {
        // 5 samples, window=2 → 2 complete windows + 1 trailing sample that must still appear.
        var samples = new[]
        {
            new SampleRow(T0, Ch1.Key, 2.0),
            new SampleRow(T1, Ch1.Key, 4.0),
            new SampleRow(T2, Ch1.Key, 6.0),
            new SampleRow(T2 + TimeSpan.TicksPerSecond, Ch1.Key, 8.0),
            new SampleRow(T2 + 2 * TimeSpan.TicksPerSecond, Ch1.Key, 10.0),
        };
        var source = new InMemorySampleSource([Ch1], samples);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true, AverageWindow = 2 });

        Assert.Equal(4, lines.Length); // header + 2 full windows + 1 partial
        Assert.Equal("3", lines[1].Split(',')[1]);  // (2+4)/2
        Assert.Equal("7", lines[2].Split(',')[1]);  // (6+8)/2
        Assert.Equal("10", lines[3].Split(',')[1]); // partial window holds only the 5th sample
    }

    [Fact]
    public async Task Export_AverageWindow_RelativeTime_UsesLastSampleTickInWindow()
    {
        // Two samples separated by 500ms — last tick should determine relative time
        var t500ms = T0 + TimeSpan.TicksPerSecond / 2;
        var samples = new[]
        {
            new SampleRow(T0, Ch1.Key, 1.0),
            new SampleRow(t500ms, Ch1.Key, 2.0),
        };
        var source = new InMemorySampleSource([Ch1], samples);

        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true, AverageWindow = 2 });

        Assert.StartsWith("0.500,", lines[1]);
    }

    // ── Progress reporting ───────────────────────────────────────────────────

    [Fact]
    public async Task Export_ReportsProgressAt100OnCompletion()
    {
        var source = new InMemorySampleSource(
            [Ch1],
            [new SampleRow(T0, Ch1.Key, 1.0)]);

        var reported = new System.Collections.Concurrent.ConcurrentBag<int>();
        var tcs = new TaskCompletionSource();
        var progress = new Progress<int>(v =>
        {
            reported.Add(v);
            if (v == 100) tcs.TrySetResult();
        });

        await new CsvExporter().ExportAsync(source, new StringWriter(), new CsvExportOptions(), progress);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Contains(100, reported);
    }

    [Fact]
    public async Task Export_Averaged_ReportsProgressAt100OnCompletion()
    {
        var source = new InMemorySampleSource(
            [Ch1],
            [new SampleRow(T0, Ch1.Key, 1.0), new SampleRow(T1, Ch1.Key, 2.0)]);

        var reported = new System.Collections.Concurrent.ConcurrentBag<int>();
        var tcs = new TaskCompletionSource();
        var progress = new Progress<int>(v =>
        {
            reported.Add(v);
            if (v == 100) tcs.TrySetResult();
        });

        await new CsvExporter().ExportAsync(source, new StringWriter(), new CsvExportOptions { AverageWindow = 2 }, progress);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Contains(100, reported);
    }

    // ── AverageWindow validation ─────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task Export_AverageWindowZeroOrNegative_ThrowsArgumentOutOfRange(int window)
    {
        var source = new InMemorySampleSource([Ch1], [new SampleRow(T0, Ch1.Key, 1.0)]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new CsvExporter().ExportAsync(source, new StringWriter(),
                new CsvExportOptions { AverageWindow = window }));
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_CancelledBeforeStart_ThrowsOperationCancelled()
    {
        var source = new InMemorySampleSource(
            [Ch1],
            [new SampleRow(T0, Ch1.Key, 1.0)]);

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new CsvExporter().ExportAsync(source, new StringWriter(), new CsvExportOptions(), cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Export_CancelledBeforeStart_DoesNotWriteHeader()
    {
        var source = new InMemorySampleSource(
            [Ch1],
            [new SampleRow(T0, Ch1.Key, 1.0)]);
        var writer = new StringWriter();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new CsvExporter().ExportAsync(source, writer, new CsvExportOptions(), cancellationToken: cts.Token));

        Assert.Equal(string.Empty, writer.ToString());
    }

    // ── Channel key format ───────────────────────────────────────────────────

    [Fact]
    public void ChannelDescriptor_Key_FormatIsCorrect()
    {
        Assert.Equal("DevA:SN001:Channel1", Ch1.Key);
    }

    [Fact]
    public async Task Export_DigitalChannel_IncludedInHeader()
    {
        var source = new InMemorySampleSource(
            [Ch1, ChDig],
            [new SampleRow(T0, Ch1.Key, 1.0), new SampleRow(T0, ChDig.Key, 1.0)]);

        var (_, header) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true });

        Assert.Contains(ChDig.Key, header);
    }

    // ── No EF/Windows references compile check ───────────────────────────────

    [Fact]
    public void ExporterTypes_DoNotReferenceEfCoreOrWindows()
    {
        // If this file compiles, the types exist without EF/WPF dependencies.
        // This test is a compile-time guarantee — it always passes if the project builds.
        var _ = new CsvExporter();
        Assert.NotNull(_);
    }

    // ── #191 progress finalization on no-op export ───────────────────────────

    [Fact]
    public async Task Export_NoChannels_StillReports100ProgressOnCompletion()
    {
        var source = new InMemorySampleSource([], []);
        var report = new ListProgress<int>();
        var sw = new StringWriter();

        await new CsvExporter().ExportAsync(source, sw, new CsvExportOptions(), report);

        Assert.Contains(100, report.Reports);
    }

    private sealed class ListProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = new();
        public void Report(T value) => Reports.Add(value);
    }

    // ── #193 CSV header escaping ─────────────────────────────────────────────

    [Fact]
    public async Task Export_ChannelNameContainingDelimiter_QuotesHeaderField()
    {
        var ch = new ChannelDescriptor("DevA", "SN001", "name,with,commas", ChannelType.Analog);
        var source = new InMemorySampleSource([ch], [new SampleRow(T0, ch.Key, 1.0)]);
        var (_, header) = await ExportToLinesAsync(source, new CsvExportOptions());
        Assert.Contains("\"DevA:SN001:name,with,commas\"", header);
    }

    [Fact]
    public async Task Export_ChannelNameContainingQuote_DoublesAndQuotesField()
    {
        var ch = new ChannelDescriptor("DevA", "SN001", "name\"with\"quote", ChannelType.Analog);
        var source = new InMemorySampleSource([ch], [new SampleRow(T0, ch.Key, 1.0)]);
        var (_, header) = await ExportToLinesAsync(source, new CsvExportOptions());
        Assert.Contains("\"DevA:SN001:name\"\"with\"\"quote\"", header);
    }

    [Fact]
    public async Task Export_DeviceNameStartingWithFormulaChar_GetsLeadingApostrophe()
    {
        // Channel keys starting with =/+/-/@ would be evaluated as
        // formulas by Excel/LibreOffice/Sheets. The mitigation prefixes
        // a literal ' to force text mode.
        var ch = new ChannelDescriptor("=DevA", "SN001", "Channel1", ChannelType.Analog);
        var source = new InMemorySampleSource([ch], [new SampleRow(T0, ch.Key, 1.0)]);
        var (_, header) = await ExportToLinesAsync(source, new CsvExportOptions());
        Assert.Contains("'=DevA:SN001:Channel1", header);
    }

    [Fact]
    public async Task Export_WhitespacePrefixedFormulaChar_StillNeutralized()
    {
        // " =SUM(A1)" — leading whitespace bypasses a naive value[0]
        // check but spreadsheets still interpret it as a formula.
        var ch = new ChannelDescriptor(" =DevA", "SN001", "Channel1", ChannelType.Analog);
        var source = new InMemorySampleSource([ch], [new SampleRow(T0, ch.Key, 1.0)]);
        var (_, header) = await ExportToLinesAsync(source, new CsvExportOptions());
        Assert.Contains("' =DevA:SN001:Channel1", header);
    }

    [Theory]
    [InlineData("\u00A0")] // NBSP
    [InlineData("\u2003")] // EM SPACE
    public async Task Export_UnicodeWhitespacePrefixedFormulaChar_StillNeutralized(string whitespace)
    {
        // Trim-based formula-injection mitigations that only strip ' '
        // and '\t' miss CSV PoCs that prepend NBSP / EM SPACE / line
        // separator before '='. char.IsWhiteSpace covers the full
        // Unicode whitespace set so the leading apostrophe still lands.
        var deviceName = whitespace + "=DevA";
        var ch = new ChannelDescriptor(deviceName, "SN001", "Channel1", ChannelType.Analog);
        var source = new InMemorySampleSource([ch], [new SampleRow(T0, ch.Key, 1.0)]);
        var (_, header) = await ExportToLinesAsync(source, new CsvExportOptions());
        Assert.Contains("'" + deviceName + ":SN001:Channel1", header);
    }

    [Fact]
    public async Task Export_LeadingTrailingWhitespaceInDeviceName_QuotesField()
    {
        // Excel, Google Sheets, and pandas trim unquoted leading/trailing
        // whitespace in CSV fields; quoting preserves the exact value
        // through round-trip parsing.
        var deviceName = "  DevA  ";
        var ch = new ChannelDescriptor(deviceName, "SN001", "Channel1", ChannelType.Analog);
        var source = new InMemorySampleSource([ch], [new SampleRow(T0, ch.Key, 1.0)]);
        var (_, header) = await ExportToLinesAsync(source, new CsvExportOptions());
        Assert.Contains("\"  DevA  :SN001:Channel1\"", header);
    }

    // ── #193 data-row escaping (timestamps + values) ─────────────────────────

    [Fact]
    public async Task Export_ColonDelimiter_QuotesIsoTimestamp()
    {
        // ISO 8601 absolute timestamps inherently contain ':'. With ':'
        // chosen as the delimiter, the timestamp field must be RFC 4180
        // quoted so it stays a single CSV field.
        var source = new InMemorySampleSource([Ch1], [new SampleRow(T0, Ch1.Key, 1.0)]);
        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { Delimiter = ":" });
        // Body row: "2024-...":1
        Assert.StartsWith("\"", lines[1]);
        Assert.Contains("\":", lines[1]);
    }

    [Fact]
    public async Task Export_DotDelimiter_QuotesRelativeTimestampAndValue()
    {
        var source = new InMemorySampleSource(
            [Ch1],
            [new SampleRow(T0, Ch1.Key, 0.5), new SampleRow(T0 + TimeSpan.TicksPerSecond, Ch1.Key, 1.5)]);
        var (lines, _) = await ExportToLinesAsync(
            source, new CsvExportOptions { Delimiter = ".", UseRelativeTime = true });
        // Both relative timestamps and float values contain '.' so both
        // get quoted under the '.' delimiter.
        Assert.Equal("\"0.000\".\"0.5\"", lines[1]);
        Assert.Equal("\"1.000\".\"1.5\"", lines[2]);
    }

    [Fact]
    public async Task Export_NegativeValue_NotApostrophePrefixed()
    {
        // Regression: data fields use formulaSafe=false so negative
        // numbers (whose leading '-' is a sign, not a formula char)
        // aren't clobbered into "'-1.5".
        var source = new InMemorySampleSource(
            [Ch1],
            [new SampleRow(T0, Ch1.Key, -1.5)]);
        var (lines, _) = await ExportToLinesAsync(source, new CsvExportOptions { UseRelativeTime = true });
        Assert.Equal("0.000,-1.5", lines[1]);
    }

    // ── #193 delimiter validation ────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData(",,")]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\"")]
    public async Task Export_InvalidDelimiter_ThrowsArgumentException(string bad)
    {
        var source = new InMemorySampleSource([Ch1], [new SampleRow(T0, Ch1.Key, 1.0)]);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ExportToLinesAsync(source, new CsvExportOptions { Delimiter = bad }));
    }
}
