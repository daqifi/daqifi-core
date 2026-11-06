using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Logging.Export;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// A row that does not match the header's column count is not a sample (issue #905).
/// </summary>
/// <remarks>
/// <para>
/// The device writes an SD-card log row by row, so a recording that is stopped rather than closed
/// can end part-way through one. That row used to pass as a sample with values for a few channels
/// and none for the rest, and the export wrote it as a row of empty cells — which a reader that
/// reads an empty cell as zero sees as every remaining channel dropping to zero for one sample.
/// </para>
/// <para>
/// A truncated tail is normal, so it is dropped and reported rather than thrown over. The last
/// row of a file and a short row in the middle of one are counted apart, because the first says
/// the recording was cut and the second says the parser could not read a row.
/// </para>
/// </remarks>
public class SdCardCsvTruncatedRowTests
{
    private const uint TickRate = 42_000_000u;
    private const string ColumnHeader = "ain0_ts,ain0_val,ain1_ts,ain1_val,dio_ts,dio_val";

    private static MemoryStream Csv(params string[] dataRows)
    {
        var text = new StringBuilder();
        text.Append("# Device: Nyquist 1\n");
        text.Append("# Serial Number: 7E2815916200E898\n");
        text.Append($"# Timestamp Tick Rate: {TickRate} Hz\n");
        text.Append(ColumnHeader).Append('\n');
        foreach (var row in dataRows)
        {
            text.Append(row).Append('\n');
        }

        return new MemoryStream(new UTF8Encoding(false).GetBytes(text.ToString()));
    }

    private static async Task<(List<SdCardLogEntry> Samples, List<SdCardParseProgress> Progress)>
        ParseAsync(Stream stream)
    {
        var progress = new List<SdCardParseProgress>();
        var session = await new SdCardCsvFileParser().ParseAsync(
            stream,
            "log_20261106_093627.csv",
            new SdCardParseOptions
            {
                Progress = new SynchronousProgress<SdCardParseProgress>(progress.Add)
            });

        var samples = new List<SdCardLogEntry>();
        await foreach (var entry in session.Samples)
        {
            samples.Add(entry);
        }

        return (samples, progress);
    }

    [Fact]
    public async Task ARecordingCutMidRow_DropsTheShortFinalRow()
    {
        // The real file's shape: full rows, then a stop part-way through the last one.
        await using var stream = Csv(
            "1000,0,1000,12,1000,0",
            "2000,0,2000,13,2000,0",
            "3000,0,3000,13");

        var (samples, progress) = await ParseAsync(stream);

        Assert.Equal(2, samples.Count);
        Assert.Equal(2, samples[^1].AnalogValues.Count);
        Assert.True(progress[^1].TruncatedFinalRow);

        // The tail is not a row the parser failed on; it is a recording that was cut.
        Assert.Equal(0, progress[^1].RowsSkipped);
    }

    [Fact]
    public async Task ACleanRecording_ReportsNoTruncatedRow()
    {
        // The clean end and the cut end must not look the same in the data.
        await using var stream = Csv(
            "1000,0,1000,12,1000,0",
            "2000,0,2000,13,2000,0");

        var (samples, progress) = await ParseAsync(stream);

        Assert.Equal(2, samples.Count);
        Assert.False(progress[^1].TruncatedFinalRow);
        Assert.Equal(0, progress[^1].RowsSkipped);
    }

    [Fact]
    public async Task AShortRowInTheMiddle_IsAParseProblemRatherThanATruncatedTail()
    {
        await using var stream = Csv(
            "1000,0,1000,12,1000,0",
            "2000,0,2000",
            "3000,0,3000,13,3000,0");

        var (samples, progress) = await ParseAsync(stream);

        Assert.Equal(2, samples.Count);
        Assert.Equal(1, progress[^1].RowsSkipped);
        Assert.False(progress[^1].TruncatedFinalRow);
    }

    [Fact]
    public async Task ARowWithMoreFieldsThanTheHeader_IsDroppedToo()
    {
        // The header is the file's statement of its own layout. A row that does not match it
        // cannot be read against it, whichever way it differs.
        await using var stream = Csv(
            "1000,0,1000,12,1000,0",
            "2000,0,2000,13,2000,0,2000,99");

        var (samples, progress) = await ParseAsync(stream);

        Assert.Single(samples);
        Assert.True(progress[^1].TruncatedFinalRow);
    }

    [Fact]
    public async Task TheExportNoLongerEndsInARowOfEmptyCells()
    {
        await using var stream = Csv(
            "1000,0,1000,12,1000,0",
            "2000,0,2000,13,2000,0",
            "3000,0,3000,13");

        var session = await new SdCardCsvFileParser().ParseAsync(stream, "log_20261106_093627.csv");

        await using var writer = new StringWriter();
        await new CsvExporter().ExportAsync(
            session.AsSampleSource(), writer, new CsvExportOptions());

        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);                 // header + two complete rows
        Assert.DoesNotContain(",,", lines[^1]);        // no empty cells in the last row
        Assert.EndsWith(",0,13,0", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileWhoseOnlyRowIsTruncated_YieldsNoSamplesAndSaysWhy()
    {
        await using var stream = Csv("1000,0,1000");

        var (samples, progress) = await ParseAsync(stream);

        Assert.Empty(samples);
        Assert.True(progress[^1].TruncatedFinalRow);
    }

    [Fact]
    public async Task ARowWithATrailingComma_DoesNotPassAsAFullRow()
    {
        // One more field than the header has columns, and the extra one is empty — which is
        // exactly the shape that would export as a cell nobody wrote.
        await using var stream = Csv(
            "1000,0,1000,12,1000,0",
            "2000,0,2000,13,2000,0,");

        var (samples, progress) = await ParseAsync(stream);

        Assert.Single(samples);
        Assert.True(progress[^1].TruncatedFinalRow);
    }

    [Fact]
    public async Task AFileWithNoColumnHeader_StillParsesItsRows()
    {
        // With no header there is no column count to match a row against, so nothing is dropped.
        var text = new StringBuilder();
        text.Append("1000,0,1000,12\n");
        text.Append("2000,0,2000,13\n");
        await using var stream = new MemoryStream(new UTF8Encoding(false).GetBytes(text.ToString()));

        var (samples, progress) = await ParseAsync(stream);

        Assert.Equal(2, samples.Count);
        Assert.False(progress[^1].TruncatedFinalRow);
    }
}
