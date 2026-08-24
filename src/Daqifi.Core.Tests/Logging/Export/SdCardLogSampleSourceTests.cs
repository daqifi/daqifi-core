using System.Runtime.CompilerServices;
using Daqifi.Core.Channel;
using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Logging.Export;

namespace Daqifi.Core.Tests.Logging.Export;

/// <summary>
/// Covers the SD-card-log-to-export adapter on its own — no device and no real log file, so every
/// case here is deterministic. Weighted to the edges the hand-rolled copies of this adapter got
/// wrong or left unspecified: a log wider than the channel count it was parsed with, a log
/// narrower than it, an empty log, cancellation mid-read, and a caller with nothing to identify
/// the device by.
/// </summary>
public class SdCardLogSampleSourceTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── Channel set and keys ────────────────────────────────────────────────

    [Fact]
    public void GetChannels_IsOneColumnPerAnalogPortThenTheDigitalPort()
    {
        var source = new SdCardLogSampleSource(Entries(), "SN123", analogChannelCount: 3);

        var channels = source.GetChannels();

        Assert.Equal(new[] { "AI0", "AI1", "AI2", "DIO" }, channels.Select(c => c.ChannelName).ToArray());
        Assert.Equal(
            new[]
            {
                "Daqifi:SN123:AI0", "Daqifi:SN123:AI1", "Daqifi:SN123:AI2", "Daqifi:SN123:DIO",
            },
            channels.Select(c => c.Key).ToArray());
        Assert.Equal(ChannelType.Analog, channels[0].ChannelType);
        Assert.Equal(ChannelType.Digital, channels[^1].ChannelType);
        Assert.Equal(3, source.AnalogChannelCount);
    }

    [Fact]
    public void NoAnalogChannels_StillExportsTheDigitalColumn()
    {
        // CsvExporter writes nothing at all for a source with no channels, so an analog-less
        // device has to keep at least one column or the export silently produces an empty file.
        var source = new SdCardLogSampleSource(Entries(), "SN123", analogChannelCount: 0);

        Assert.Equal(new[] { "DIO" }, source.GetChannels().Select(c => c.ChannelName).ToArray());
        Assert.Equal(0, source.AnalogChannelCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankSerialNumber_BecomesUnknown_RatherThanAnEmptyKeySegment(string? serial)
    {
        var source = new SdCardLogSampleSource(Entries(), serial, analogChannelCount: 1);

        Assert.Equal("Daqifi:unknown:AI0", source.GetChannels()[0].Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankDeviceName_BecomesTheDefault_BecauseALogCarriesNone(string? deviceName)
    {
        var source = new SdCardLogSampleSource(Entries(), "SN", analogChannelCount: 1, deviceName);

        Assert.Equal($"{SdCardLogSampleSource.DefaultDeviceName}:SN:AI0", source.GetChannels()[0].Key);
    }

    [Fact]
    public void ExplicitDeviceName_IsUsedInTheKeys()
    {
        var source = new SdCardLogSampleSource(Entries(), "SN", analogChannelCount: 1, "Nyquist");

        Assert.Equal("Nyquist:SN:AI0", source.GetChannels()[0].Key);
    }

    // ── Argument guards ─────────────────────────────────────────────────────

    [Fact]
    public void NullSamples_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            "samples",
            () => new SdCardLogSampleSource(null!, "SN", analogChannelCount: 1));
    }

    [Fact]
    public void NegativeAnalogChannelCount_Throws_RatherThanClampingSilently()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            "analogChannelCount",
            () => new SdCardLogSampleSource(Entries(), "SN", analogChannelCount: -1));

        Assert.Equal(-1, ex.ActualValue);
    }

    // ── Streaming and counters ──────────────────────────────────────────────

    [Fact]
    public async Task StreamSamples_EmitsEveryAnalogValueThenTheDigitalPort()
    {
        var source = new SdCardLogSampleSource(
            Entries(Entry(0, digital: 0b101, 1.5, -2.5)), "SN", analogChannelCount: 2);

        var rows = await Collect(source);

        Assert.Equal(
            new[] { ("Daqifi:SN:AI0", 1.5), ("Daqifi:SN:AI1", -2.5), ("Daqifi:SN:DIO", 5.0) },
            rows.Select(r => (r.ChannelKey, r.Value)).ToArray());
        Assert.All(rows, r => Assert.Equal(T0.Ticks, r.TimestampTicks));
    }

    [Fact]
    public async Task RowCount_CountsTimestamps_WhileSampleCountCountsEntries()
    {
        var source = new SdCardLogSampleSource(
            Entries(
                Entry(0, 0b01, 1.0, 2.0),
                Entry(0, 0b10, 3.0, 4.0),   // repeats the timestamp — the exporter merges the two
                Entry(1, 0b11, 5.0, 6.0)),
            "SN",
            analogChannelCount: 2);

        var rows = await Collect(source);

        Assert.Equal(9, rows.Count);            // 3 entries x (2 analog + 1 digital)
        Assert.Equal(3, source.SampleCount);
        Assert.Equal(2, source.RowCount);
        Assert.Equal(0, source.DroppedAnalogColumns);
        Assert.Equal(0, source.NonMonotonicEntryCount);
    }

    [Fact]
    public async Task RowCount_MatchesTheLinesTheRealExporterWrites()
    {
        // The row count is reported to a user as "how many lines are in your CSV", so it is only
        // worth anything if it matches what CsvExporter actually writes. Run the real exporter and
        // count the lines rather than trusting the rule the counter was written against.
        var source = new SdCardLogSampleSource(
            Entries(
                Entry(0, 0b01, 1.0, 2.0),
                Entry(0, 0b10, 3.0, 4.0),
                Entry(1, 0b11, 5.0, 6.0),
                Entry(2, 0b00, 7.0, 8.0)),
            "SN",
            analogChannelCount: 2);

        var writer = new StringWriter();
        await new CsvExporter().ExportAsync(source, writer, new CsvExportOptions { UseRelativeTime = false });

        var lines = writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToList();

        Assert.Equal(source.RowCount, lines.Count - 1);   // minus the header
        Assert.Contains("Daqifi:SN:AI0", lines[0]);
        Assert.Contains("Daqifi:SN:DIO", lines[0]);
    }

    [Fact]
    public async Task MoreAnalogValuesThanColumns_IsTruncatedAndReportedAsTheWidestOverflow()
    {
        // The widest overflow, not the sum: it reads as "the CSV is this many columns short",
        // which a total that grew with the file's length would not.
        var source = new SdCardLogSampleSource(
            Entries(
                Entry(0, 0, 1.0, 2.0),          // one value over
                Entry(1, 0, 1.0, 2.0, 3.0),     // two over — the widest
                Entry(2, 0, 1.0, 2.0)),         // one over again
            "SN",
            analogChannelCount: 1);

        var rows = await Collect(source);

        Assert.Equal(6, rows.Count);            // 3 entries x (1 analog + 1 digital)
        Assert.Equal(2, source.DroppedAnalogColumns);
        Assert.Equal(1, source.AnalogChannelCount);
    }

    [Fact]
    public async Task FewerAnalogValuesThanColumns_LeavesTheRestOfTheRowEmpty()
    {
        var source = new SdCardLogSampleSource(
            Entries(Entry(0, 0, 1.0)), "SN", analogChannelCount: 3);

        var rows = await Collect(source);

        Assert.Equal(new[] { "Daqifi:SN:AI0", "Daqifi:SN:DIO" }, rows.Select(r => r.ChannelKey).ToArray());
        Assert.Equal(0, source.DroppedAnalogColumns);
    }

    [Fact]
    public async Task EmptyLog_ProducesNoRows_AndTheExporterWritesTheHeaderAlone()
    {
        var source = new SdCardLogSampleSource(Entries(), "SN", analogChannelCount: 2);

        var writer = new StringWriter();
        await new CsvExporter().ExportAsync(source, writer, new CsvExportOptions());

        Assert.Equal(0, source.SampleCount);
        Assert.Equal(0, source.RowCount);
        Assert.Single(writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task BackwardsTimestamp_IsCountedButStillExported()
    {
        // Losing data is worse than exporting it out of order, so the entry is kept and the
        // caller is told the time column is not ascending.
        var source = new SdCardLogSampleSource(
            Entries(Entry(2, 0, 1.0), Entry(1, 0, 2.0), Entry(1, 0, 3.0), Entry(3, 0, 4.0)),
            "SN",
            analogChannelCount: 1);

        var rows = await Collect(source);

        Assert.Equal(8, rows.Count);
        Assert.Equal(1, source.NonMonotonicEntryCount);   // the repeat is not backwards
        Assert.Equal(3, source.RowCount);
    }

    [Fact]
    public async Task Counters_AccumulateAcrossEnumerations_TheyAreNotResetPerExport()
    {
        var source = new SdCardLogSampleSource(
            Entries(Entry(0, 0, 1.0), Entry(1, 0, 2.0)), "SN", analogChannelCount: 1);

        await Collect(source);
        await Collect(source);

        Assert.Equal(4, source.SampleCount);
        Assert.Equal(4, source.RowCount);
    }

    // ── Cancellation ────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancellation_StopsTheReadAndLeavesTheCountersAtWhatWasRead()
    {
        using var cts = new CancellationTokenSource();

        var source = new SdCardLogSampleSource(
            Entries(Entry(0, 0, 1.0), Entry(1, 0, 2.0), Entry(2, 0, 3.0)),
            "SN",
            analogChannelCount: 1);

        var rows = new List<SampleRow>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var row in source.StreamSamples(cts.Token))
            {
                rows.Add(row);
                if (rows.Count == 2)
                {
                    await cts.CancelAsync();
                }
            }
        });

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, source.SampleCount);
    }

    [Fact]
    public async Task Cancellation_IsForwardedToTheUnderlyingLogRead()
    {
        // The read of the file itself has to stop, not just the loop over it: an SD log can be
        // hundreds of megabytes, and a cancelled export that keeps decoding is not cancelled.
        using var cts = new CancellationTokenSource();
        var observed = false;

        async IAsyncEnumerable<SdCardLogEntry> Watching([EnumeratorCancellation] CancellationToken token = default)
        {
            await Task.Yield();
            observed = token.CanBeCanceled;
            token.ThrowIfCancellationRequested();
            yield return Entry(0, 0, 1.0);
        }

        await cts.CancelAsync();

        var source = new SdCardLogSampleSource(Watching(), "SN", analogChannelCount: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in source.StreamSamples(cts.Token))
            {
            }
        });

        Assert.True(observed);
        Assert.Equal(0, source.SampleCount);
    }

    // ── GetSampleCountAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetSampleCountAsync_ReturnsZeroWithoutReadingTheLog()
    {
        // Counting would mean a second pass, and a stream-backed session has one read cursor:
        // the pass taken to count is a pass the export cannot take.
        var source = new SdCardLogSampleSource(Exploding(), "SN", analogChannelCount: 1);

        Assert.Equal(0, await source.GetSampleCountAsync(CancellationToken.None));
    }

    // ── AsSampleSource over a parsed session ────────────────────────────────

    [Fact]
    public void AsSampleSource_TakesSerialAndWidthFromTheLogsOwnConfiguration()
    {
        var session = Session(Config(analogPorts: 2, serial: "LOG-SN"));

        var source = session.AsSampleSource(Config(analogPorts: 8, serial: "LIVE-SN"));

        Assert.Equal(2, source.AnalogChannelCount);
        Assert.Equal("Daqifi:LOG-SN:AI0", source.GetChannels()[0].Key);
    }

    [Fact]
    public void AsSampleSource_FallsBackToTheLiveConfiguration_WhenTheLogCarriesNone()
    {
        var source = Session(config: null).AsSampleSource(Config(analogPorts: 2, serial: "LIVE-SN"));

        Assert.Equal(2, source.AnalogChannelCount);
        Assert.Equal("Daqifi:LIVE-SN:AI0", source.GetChannels()[0].Key);
    }

    [Fact]
    public void AsSampleSource_FallsBackFieldByField_WhenTheLogNamesNoSerial()
    {
        var session = Session(Config(analogPorts: 2, serial: null));

        var source = session.AsSampleSource(Config(analogPorts: 8, serial: "LIVE-SN"));

        Assert.Equal(2, source.AnalogChannelCount);              // the log's width still wins
        Assert.Equal("Daqifi:LIVE-SN:AI0", source.GetChannels()[0].Key);
    }

    [Fact]
    public void AsSampleSource_ALogStatingZeroAnalogPorts_IsTakenAtItsWord()
    {
        var session = Session(Config(analogPorts: 0, serial: "LOG-SN"));

        var source = session.AsSampleSource(Config(analogPorts: 8, serial: "LIVE-SN"));

        Assert.Equal(0, source.AnalogChannelCount);
        Assert.Single(source.GetChannels());
    }

    [Fact]
    public void AsSampleSource_WithNoConfigurationAtAll_ExportsTheDigitalColumnForAnUnknownDevice()
    {
        var source = Session(config: null).AsSampleSource();

        Assert.Equal(new[] { "Daqifi:unknown:DIO" }, source.GetChannels().Select(c => c.Key).ToArray());
    }

    [Fact]
    public void AsSampleSource_ACorruptNegativePortCount_ReadsAsNoAnalogPorts_NotAThrow()
    {
        // A corrupt status message is parsed data being normalized, not a caller's argument being
        // validated — the digital column of a damaged log is still worth exporting.
        var session = Session(Config(analogPorts: -3, serial: "LOG-SN"));

        var source = session.AsSampleSource();

        Assert.Equal(0, source.AnalogChannelCount);
        Assert.Single(source.GetChannels());
    }

    [Fact]
    public void AsSampleSource_PassesTheDeviceNameThrough()
    {
        var source = Session(Config(analogPorts: 1, serial: "SN")).AsSampleSource(deviceName: "Nyquist");

        Assert.Equal("Nyquist:SN:AI0", source.GetChannels()[0].Key);
    }

    [Fact]
    public void AsSampleSource_NullSession_Throws()
    {
        Assert.Throws<ArgumentNullException>("session", () => ((SdCardLogSession)null!).AsSampleSource());
    }

    [Fact]
    public void AsSampleSource_DoesNotReadTheLog()
    {
        // Wrapping has to stay free: the exporter decides when the file is read, and on a
        // stream-backed session an early read would consume the one cursor there is.
        var session = new SdCardLogSession("log.bin", null, Config(analogPorts: 1, serial: "SN"), Exploding());

        var source = session.AsSampleSource();

        Assert.Equal(2, source.GetChannels().Count);
        Assert.Equal(0, source.SampleCount);
    }

    [Fact]
    public async Task AsSampleSource_StreamsTheSessionsOwnEntries()
    {
        var session = new SdCardLogSession(
            "log.bin", null, Config(analogPorts: 1, serial: "SN"), Entries(Entry(0, 3, 4.5)));

        var rows = await Collect(session.AsSampleSource());

        Assert.Equal(
            new[] { ("Daqifi:SN:AI0", 4.5), ("Daqifi:SN:DIO", 3.0) },
            rows.Select(r => (r.ChannelKey, r.Value)).ToArray());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static SdCardLogEntry Entry(int secondsFromT0, uint digital, params double[] analog) =>
        new(T0.AddSeconds(secondsFromT0), analog, digital, null);

    private static SdCardDeviceConfiguration Config(int analogPorts, string? serial) =>
        new(analogPorts, DigitalPortCount: 1, TimestampFrequency: 50000, serial, null, null, null);

    private static SdCardLogSession Session(SdCardDeviceConfiguration? config) =>
        new("log.bin", null, config, Entries());

    private static IAsyncEnumerable<SdCardLogEntry> Entries(params SdCardLogEntry[] entries)
        => Stream(entries);

    /// <summary>
    /// Honours the token the way a real parse does, so a cancellation test measures something.
    /// A compiler-generated async iterator without <see cref="EnumeratorCancellationAttribute"/>
    /// ignores the token entirely.
    /// </summary>
    private static async IAsyncEnumerable<SdCardLogEntry> Stream(
        SdCardLogEntry[] entries,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return entry;
        }
    }

    /// <summary>A stream that fails the test if anything enumerates it.</summary>
    private static async IAsyncEnumerable<SdCardLogEntry> Exploding()
    {
        await Task.Yield();
        Assert.Fail("The log was read when it should not have been.");
        yield break;
    }

    private static async Task<List<SampleRow>> Collect(SdCardLogSampleSource source)
    {
        var rows = new List<SampleRow>();
        await foreach (var row in source.StreamSamples())
        {
            rows.Add(row);
        }

        return rows;
    }
}
