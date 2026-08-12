using System.Text;
using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Logging.Export;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// Contract tests for the SD-card retrieval tools (#500) — the half of the SD surface an agent
/// needs to get data back off the card, as opposed to starting a log it can never read.
/// </summary>
/// <remarks>
/// No device is attached here, so what these pin is everything the tools decide before (and
/// after) the wire: which calls a <c>--read-only</c> server refuses, what a caller is told when
/// nothing is connected, and the parse/export chain that turns a downloaded log into a CSV.
/// </remarks>
public class SdCardAgentGuardTests
{
    private static DaqifiAgent NewAgent(bool readOnly = false) =>
        new(new ServerOptions { ReadOnly = readOnly });

    [Fact]
    public async Task ListSdFiles_UnknownDevice_PointsAtConnectDevice()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent().ListSdFilesAsync("serial:NOPE", CancellationToken.None));
        Assert.Contains("connect_device", ex.Message);
    }

    [Fact]
    public async Task GetSdStorage_UnknownDevice_PointsAtConnectDevice()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent().GetSdStorageAsync("serial:NOPE", CancellationToken.None));
        Assert.Contains("connect_device", ex.Message);
    }

    [Fact]
    public async Task DownloadSdFile_UnknownDevice_PointsAtConnectDevice()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent().DownloadSdFileAsync("serial:NOPE", "log.bin", exportCsv: true, CancellationToken.None));
        Assert.Contains("connect_device", ex.Message);
    }

    // The whole point of --read-only is that a caller can still LOOK. Retrieval reads device data
    // and changes nothing on the card, so it must not be swept up with the mutating tools; these
    // three fail for want of a device, never for want of permission.
    [Theory]
    [InlineData("list")]
    [InlineData("storage")]
    [InlineData("download")]
    public async Task Retrieval_IsStillAvailableInReadOnlyMode(string operation)
    {
        var agent = NewAgent(readOnly: true);

        Task Call() => operation switch
        {
            "list" => agent.ListSdFilesAsync("serial:NOPE", CancellationToken.None),
            "storage" => agent.GetSdStorageAsync("serial:NOPE", CancellationToken.None),
            _ => agent.DownloadSdFileAsync("serial:NOPE", "log.bin", exportCsv: true, CancellationToken.None),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(Call);
        Assert.DoesNotContain("read-only", ex.Message);
        Assert.Contains("not connected", ex.Message);
    }

    [Fact]
    public async Task DeleteSdFile_InReadOnlyMode_IsRefusedBeforeAnythingElse()
    {
        // Deliberately a device id that does not exist: the read-only refusal has to win, or a
        // caller could discover that permission was never the obstacle only after connecting.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent(readOnly: true).DeleteSdFileAsync("serial:NOPE", "log.bin", CancellationToken.None));
        Assert.Contains("read-only", ex.Message);
    }

    [Fact]
    public async Task DeleteSdFile_WithControl_UnknownDevice_PointsAtConnectDevice()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent().DeleteSdFileAsync("serial:NOPE", "log.bin", CancellationToken.None));
        Assert.Contains("connect_device", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DownloadSdFile_BlankFileName_PointsAtListSdFiles(string fileName)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent().DownloadSdFileAsync("serial:NOPE", fileName, exportCsv: true, CancellationToken.None));
        Assert.Contains("list_sd_files", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DeleteSdFile_BlankFileName_PointsAtListSdFiles(string fileName)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent().DeleteSdFileAsync("serial:NOPE", fileName, CancellationToken.None));
        Assert.Contains("list_sd_files", ex.Message);
    }

    // Core rejects these before putting a name into an SCPI command, but the listing pre-flight
    // runs first — so without the same check here a name full of newlines comes back as a
    // multi-line "there is no file called ..." instead of something a caller can act on.
    [Theory]
    [InlineData("log\n.bin")]
    [InlineData("log\r.bin")]
    [InlineData("log\t.bin")]
    [InlineData("log\".bin")]
    [InlineData("log;rm.bin")]
    [InlineData("log\u0000.bin")]
    public async Task FileNamesWithControlOrCommandCharacters_AreRejectedCleanly(string fileName)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent().DownloadSdFileAsync("serial:NOPE", fileName, exportCsv: true, CancellationToken.None));

        Assert.Contains("not a valid SD file name", ex.Message);
        Assert.DoesNotContain("\n", ex.Message);
        Assert.DoesNotContain("\r", ex.Message);

        var deleteEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAgent().DeleteSdFileAsync("serial:NOPE", fileName, CancellationToken.None));
        Assert.Contains("not a valid SD file name", deleteEx.Message);
    }

    [Fact]
    public async Task OrdinaryFileNames_AreNotCaughtByTheCharacterCheck()
    {
        // Spaces, dots, dashes and underscores all appear in real on-card names; the check must
        // only be looking for the characters that break an SCPI command or an error message.
        foreach (var name in new[] { "log_20260812_120000.bin", "iso A 1.bin", "bench-pr2.json" })
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => NewAgent().DownloadSdFileAsync("serial:NOPE", name, exportCsv: true, CancellationToken.None));
            Assert.Contains("not connected", ex.Message);
        }
    }
}

public class SdCardReportDtoTests
{
    [Fact]
    public void SdStorageReport_ComputesPercentFreeAndUsed()
    {
        var report = SdStorageReport.From("serial:X", new SdCardStorageInfo(FreeBytes: 250, TotalBytes: 1000));

        Assert.Equal(250, report.FreeBytes);
        Assert.Equal(750, report.UsedBytes);
        Assert.Equal(25.0, report.PercentFree);
    }

    [Fact]
    public void SdStorageReport_ZeroTotal_DoesNotDivideByZero()
    {
        var report = SdStorageReport.From("serial:X", new SdCardStorageInfo(FreeBytes: 0, TotalBytes: 0));
        Assert.Equal(0, report.PercentFree);
    }

    // A listing entry with no size token means "unknown", and 0 means "empty file" — the two are
    // different enough that Core keeps the distinction (an unexpectedly-0-byte transfer is how a
    // wedged SD subsystem announces itself), so the tool must not flatten it.
    [Fact]
    public void SdFileEntry_UnknownSize_StaysNullRatherThanZero()
    {
        var entry = SdFileEntry.From(new SdCardFileInfo("log_20260812_120000.bin"));
        Assert.Null(entry.SizeBytes);

        var empty = SdFileEntry.From(new SdCardFileInfo("empty.bin", createdDate: null, sizeInBytes: 0));
        Assert.Equal(0, empty.SizeBytes);
    }
}

/// <summary>
/// Tests for the adapter that feeds a parsed SD log to Core's CSV exporter.
/// </summary>
public class SdCardSampleSourceTests
{
    private static SdCardLogEntry Entry(long ticks, uint digital, params double[] analog) =>
        new(new DateTime(ticks, DateTimeKind.Utc), analog, digital, null);

    private static async IAsyncEnumerable<SdCardLogEntry> Entries(params SdCardLogEntry[] entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
        }
        await Task.CompletedTask;
    }

    [Fact]
    public void Channels_AreOnePerAnalogPortPlusTheDigitalPort()
    {
        var source = new SdCardSampleSource(Entries(), "SN123", analogPortCount: 3);
        var channels = source.GetChannels();

        Assert.Equal(4, channels.Count);
        Assert.Equal(new[] { "AI0", "AI1", "AI2", "DIO" }, channels.Select(c => c.ChannelName));
        Assert.All(channels, c => Assert.Contains("SN123", c.Key));
    }

    [Fact]
    public void NoAnalogPorts_StillExportsTheDigitalColumn()
    {
        // CsvExporter returns without writing anything when a source has no channels, so an
        // analog-less device has to keep at least one column or the export silently produces
        // an empty file.
        Assert.Single(new SdCardSampleSource(Entries(), "SN123", analogPortCount: 0).GetChannels());
    }

    [Fact]
    public async Task RowCount_CountsTimestamps_WhileSampleCountCountsEntries()
    {
        var source = new SdCardSampleSource(
            Entries(
                Entry(1000, 0b01, 1.0, 2.0),
                Entry(1000, 0b10, 3.0, 4.0),   // same timestamp — merges into the first row
                Entry(2000, 0b11, 5.0, 6.0)),
            "SN123",
            analogPortCount: 2);

        var rows = new List<SampleRow>();
        await foreach (var row in source.StreamSamples())
        {
            rows.Add(row);
        }

        Assert.Equal(9, rows.Count);        // 3 entries x (2 analog + 1 digital)
        Assert.Equal(3, source.SampleCount);
        Assert.Equal(2, source.RowCount);
        Assert.Equal(0, source.DroppedAnalogColumns);
    }

    [Fact]
    public async Task MoreAnalogValuesThanChannels_IsTruncatedAndReported()
    {
        var source = new SdCardSampleSource(
            Entries(Entry(1000, 0, 1.0, 2.0, 3.0)),
            "SN123",
            analogPortCount: 1);

        var rows = new List<SampleRow>();
        await foreach (var row in source.StreamSamples())
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);        // AI0 + DIO; the two extra values have nowhere to go
        Assert.Equal(2, source.DroppedAnalogColumns);
    }

    [Fact]
    public async Task FewerAnalogValuesThanChannels_LeavesTheRemainingColumnsEmpty()
    {
        var source = new SdCardSampleSource(
            Entries(Entry(1000, 0, 1.0)),
            "SN123",
            analogPortCount: 3);

        var rows = new List<SampleRow>();
        await foreach (var row in source.StreamSamples())
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal(0, source.DroppedAnalogColumns);
    }

    // The row count is reported to the agent as "how many lines are in your CSV", so it is only
    // worth anything if it matches what CsvExporter actually writes. Run the real exporter and
    // count the lines rather than trusting the rule the counter was written against.
    [Fact]
    public async Task RowCount_MatchesTheLinesTheRealExporterWrites()
    {
        var source = new SdCardSampleSource(
            Entries(
                Entry(1000, 0b01, 1.0, 2.0),
                Entry(1000, 0b10, 3.0, 4.0),
                Entry(2000, 0b11, 5.0, 6.0),
                Entry(3000, 0b00, 7.0, 8.0)),
            "SN123",
            analogPortCount: 2);

        var writer = new StringWriter();
        await new CsvExporter().ExportAsync(source, writer, new CsvExportOptions { UseRelativeTime = false });

        var lines = writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToList();

        Assert.Equal(source.RowCount, lines.Count - 1);   // minus the header
        Assert.StartsWith("Time,", lines[0]);
        Assert.Contains("AI0", lines[0]);
        Assert.Contains("DIO", lines[0]);
    }
}

/// <summary>
/// End-to-end tests for the download's parse-and-export step, driven with a synthetic on-disk log
/// so the whole chain below the wire — format detection, parse, CSV write, counts — is covered
/// without a device.
/// </summary>
public class SdCardCsvExportTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"daqifi_mcp_test_{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Writes a firmware-shaped SD CSV log with one analog channel and the digital port.</summary>
    private string WriteLog(string localFileName, int sampleCount)
    {
        var content = new StringBuilder()
            .Append("# Device: Nyquist 1\n")
            .Append("# Serial Number: SN500\n")
            .Append("# Timestamp Tick Rate: 100 Hz\n")
            .Append("ain0_ts,ain0_val,dio_ts,dio_val\n");

        for (var i = 0; i < sampleCount; i++)
        {
            var tick = 1000 + (i * 100);
            content.Append($"{tick},{i + 1}.0,{tick},{i % 2}\n");
        }

        var path = Path.Combine(_directory, localFileName);
        File.WriteAllText(path, content.ToString());
        return path;
    }

    [Fact]
    public async Task WritesTheCsvNextToTheDownloadAndCountsIt()
    {
        var path = WriteLog("daqifi_abc123.bin", sampleCount: 4);

        var (csvPath, rows, samples, warning) = await DaqifiAgent.ExportCsvAsync(
            path, "log_20260812_120000.csv", liveConfig: null, CancellationToken.None);

        Assert.True(File.Exists(csvPath));
        Assert.Equal(4, samples);
        Assert.Equal(4, rows);
        Assert.Null(warning);

        var lines = File.ReadAllLines(csvPath);
        Assert.Equal(5, lines.Length);           // header + 4 rows
        Assert.Contains("AI0", lines[0]);
    }

    // Regression: the firmware logs in CSV too, and Core's temp file keeps the device-side
    // extension — so deriving the export path by swapping the extension names the very file being
    // read, and the export truncates its own input before it can parse it.
    [Fact]
    public async Task CsvSourceFile_IsNotOverwrittenByItsOwnExport()
    {
        var path = WriteLog("daqifi_abc123.csv", sampleCount: 4);
        var sourceBytes = File.ReadAllBytes(path);

        var (csvPath, _, samples, _) = await DaqifiAgent.ExportCsvAsync(
            path, "log_20260812_120000.csv", liveConfig: null, CancellationToken.None);

        Assert.NotEqual(path, csvPath);
        Assert.Equal(4, samples);
        Assert.Equal(sourceBytes, File.ReadAllBytes(path));
    }

    // The local file is a temp name Core minted; only the device-side name says what the format
    // is. Detecting from the local path would make the parse depend on a name nobody chose.
    [Fact]
    public async Task FormatComesFromTheDeviceSideName_NotTheLocalTempName()
    {
        var path = Path.Combine(_directory, "daqifi_deadbeef.tmp");
        File.Copy(WriteLog("source.csv", sampleCount: 2), path);

        var (csvPath, _, samples, _) = await DaqifiAgent.ExportCsvAsync(
            path, "log_20260812_120000.csv", liveConfig: null, CancellationToken.None);

        Assert.Equal(2, samples);
        Assert.True(File.Exists(csvPath));
    }

    [Fact]
    public async Task UnsupportedExtension_FailsWithoutWritingAnything()
    {
        var path = WriteLog("daqifi_abc123.bin", sampleCount: 2);
        var before = Directory.GetFiles(_directory);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => DaqifiAgent.ExportCsvAsync(path, "log.dat", liveConfig: null, CancellationToken.None));

        Assert.Contains("Unsupported file extension", ex.Message);
        Assert.Equal(before, Directory.GetFiles(_directory));
    }

    // A header-only CSV looks exactly like a successful export of a file with no data in it, so
    // the empty case has to announce itself — and must not leave that misleading file behind.
    [Fact]
    public async Task EmptyLog_ReportsZeroSamplesAndLeavesNoCsvBehind()
    {
        var path = WriteLog("daqifi_empty.bin", sampleCount: 0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DaqifiAgent.ExportCsvAsync(path, "log_20260812_120000.csv", liveConfig: null, CancellationToken.None));

        Assert.Contains("zero samples", ex.Message);
        Assert.Contains("raw file is still available", ex.Message);
        Assert.Equal(new[] { path }, Directory.GetFiles(_directory));
    }
}

/// <summary>
/// Tests for the pre-flight that turns "that file is not on the card" from a 20-second stall the
/// firmware never answers into an immediate, actionable refusal.
/// </summary>
public class SdCardFileNameResolutionTests
{
    private static SdCardFileInfo File(string name) => new(name, null, 100);

    [Fact]
    public async Task NameInTheCachedListing_IsUsedWithoutRelisting()
    {
        var sd = new FakeSdCard { Cached = [File("log_a.bin"), File("log_b.bin")] };

        var resolved = await DaqifiAgent.ResolveFileNameAsync(sd, "log_b.bin", CancellationToken.None);

        Assert.Equal("log_b.bin", resolved);
        Assert.Equal(0, sd.ListCalls);
    }

    // A file recorded by start_sd_logging since the last listing is genuinely on the card. The
    // refresh is what stops the check rejecting it for being absent from a stale snapshot.
    [Fact]
    public async Task NameMissingFromTheCache_IsFoundByRelisting()
    {
        var sd = new FakeSdCard { Cached = [File("old.bin")], Fresh = [File("old.bin"), File("just_recorded.bin")] };

        var resolved = await DaqifiAgent.ResolveFileNameAsync(sd, "just_recorded.bin", CancellationToken.None);

        Assert.Equal("just_recorded.bin", resolved);
        Assert.Equal(1, sd.ListCalls);
    }

    [Fact]
    public async Task NameOnNeitherListing_IsRefusedAndNamesWhatIsThere()
    {
        var sd = new FakeSdCard { Fresh = [File("log_a.bin"), File("log_b.bin")] };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DaqifiAgent.ResolveFileNameAsync(sd, "typo.bin", CancellationToken.None));

        Assert.Contains("no file named 'typo.bin'", ex.Message);
        Assert.Contains("log_a.bin", ex.Message);
        Assert.Contains("log_b.bin", ex.Message);
    }

    [Fact]
    public async Task EmptyCard_SaysSo()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DaqifiAgent.ResolveFileNameAsync(new FakeSdCard(), "anything.bin", CancellationToken.None));

        Assert.Contains("card is empty", ex.Message);
    }

    // FAT is case-insensitive, so a caller's spelling should match — and what goes to the firmware
    // is the card's own spelling, not the caller's.
    [Fact]
    public async Task MatchIgnoresCase_AndReturnsTheCardsSpelling()
    {
        var sd = new FakeSdCard { Cached = [File("LOG_A.BIN")] };

        Assert.Equal("LOG_A.BIN", await DaqifiAgent.ResolveFileNameAsync(sd, "log_a.bin", CancellationToken.None));
    }

    // The listing is a courtesy. If it cannot be taken, the download itself has to be the thing
    // that fails — this check must never be the reason a perfectly downloadable file is refused.
    [Fact]
    public async Task ListingFailure_LetsTheDownloadProceed()
    {
        var sd = new FakeSdCard { ListThrows = new SdCardOperationException("busy", []) };

        Assert.Equal("log_a.bin", await DaqifiAgent.ResolveFileNameAsync(sd, "log_a.bin", CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_IsNotSwallowedByTheListingGuard()
    {
        var sd = new FakeSdCard { ListThrows = new OperationCanceledException() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DaqifiAgent.ResolveFileNameAsync(sd, "log_a.bin", CancellationToken.None));
    }

    private sealed class FakeSdCard : ISdCardOperations
    {
        public IReadOnlyList<SdCardFileInfo> Cached { get; init; } = [];
        public IReadOnlyList<SdCardFileInfo> Fresh { get; init; } = [];
        public Exception? ListThrows { get; init; }
        public int ListCalls { get; private set; }

        public IReadOnlyList<SdCardFileInfo> SdCardFiles => Cached;

        public Task<IReadOnlyList<SdCardFileInfo>> GetSdCardFilesAsync(CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return ListThrows is not null ? Task.FromException<IReadOnlyList<SdCardFileInfo>>(ListThrows) : Task.FromResult(Fresh);
        }

#pragma warning disable CS0067 // the interface requires it; nothing here raises it
        public event EventHandler<LowSdSpaceWarningEventArgs>? LowSdSpaceWarning;
#pragma warning restore CS0067

        public bool IsLoggingToSdCard => false;
        public Task<SdCardStorageInfo> GetSdCardStorageAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SdCardSpaceCheckResult> CheckSdCardSpaceAsync(SdCardCaptureEstimate? plannedCapture = null, long minimumFreeBytes = SdCardSpaceCheck.DefaultMinimumFreeBytes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void SetSdCardMinimumFreeSpace(long bytes) => throw new NotSupportedException();
        public Task StartSdCardLoggingAsync(string? fileName = null, string? channelMask = null, SdCardLogFormat format = SdCardLogFormat.Protobuf, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SdCardLoggingSession> StartSdCardLoggingSessionAsync(string? fileName = null, string? channelMask = null, SdCardLogFormat format = SdCardLogFormat.Protobuf, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopSdCardLoggingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSdCardFileAsync(string fileName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task FormatSdCardAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SdCardDownloadResult> DownloadSdCardFileAsync(string fileName, Stream destinationStream, IProgress<SdCardTransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SdCardDownloadResult> DownloadSdCardFileAsync(string fileName, IProgress<SdCardTransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
