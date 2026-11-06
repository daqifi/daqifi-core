using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Daqifi.Core.Device.SdCard;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// Pins what the CSV parser does with a non-finite reading and with a row it cannot read at all
/// (issue #884).
/// <para>
/// Firmware writes a non-finite reading with <c>printf("%f")</c>, which produces C's <c>nan</c>,
/// <c>inf</c> and <c>-inf</c>. <c>double.TryParse</c> takes <c>nan</c> and .NET's
/// <c>Infinity</c>, but not C's <c>inf</c> — so a row holding one used to fail the parse and be
/// dropped whole, with no exception, no progress warning and no count. Both halves are pinned
/// here: the spelling parses, and a row that still cannot be read is counted where a caller can
/// see it.
/// </para>
/// </summary>
public sealed class SdCardCsvNonFiniteValueTests
{
    private const uint TickRate = 50_000_000u;
    private const uint AdcResolution = 4095u;
    private const double PortRangeVolts = 10.0;

    // -------------------------------------------------------------------------
    // C's inf spelling parses, the way nan already did
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("inf", true)]
    [InlineData("INF", true)]
    [InlineData("Inf", true)]
    [InlineData("+inf", true)]
    [InlineData("infinity", true)]
    [InlineData("INFINITY", true)]
    [InlineData("Infinity", true)]
    [InlineData("+Infinity", true)]
    [InlineData("-inf", false)]
    [InlineData("-INF", false)]
    [InlineData("-infinity", false)]
    [InlineData("-Infinity", false)]
    public async Task ParseAsync_InfinityToken_IsReportedAsAnInfinityInsteadOfDroppingTheRow(
        string valueToken,
        bool expectPositive)
    {
        // Arrange — a volts row whose second channel read non-finite, which is the shape the
        // issue measured. Before the fix the C spellings lost the whole row: both readings and
        // the timestamp, silently.
        await using var stream = Csv(
            "ain0_ts,ain0_val,ain1_ts,ain1_val",
            "1000,0.100,1000,0.200",
            $"2000,0.123,2000,{valueToken}");

        // Act
        var (samples, progress) = await ParseAsync(stream, Options(integerValueUnits: null));

        // Assert — the row survives, the finite reading keeps its value and its unit, and the
        // non-finite one is reported as the infinity it is rather than as a zero or a gap.
        Assert.Equal(2, samples.Count);
        Assert.Equal(SdCardAnalogValueUnit.Volts, samples[1].SourceAnalogValueUnit);
        Assert.Equal(0.123, samples[1].AnalogValues[0], 9);
        Assert.Equal(expectPositive, double.IsPositiveInfinity(samples[1].AnalogValues[1]));
        Assert.Equal(!expectPositive, double.IsNegativeInfinity(samples[1].AnalogValues[1]));

        // And nothing was dropped, so the count a caller watches stays at zero.
        Assert.Equal(0, progress[^1].RowsSkipped);
    }

    [Fact]
    public async Task ParseAsync_InfinityInAWholeNumberRow_KeepsTheRowsOwnUnit()
    {
        // Arrange — the same reading in a raw-code recording. An infinity states no unit, so it
        // neither decides the row's unit nor vetoes it (#883), and the finite code is still
        // scaled as a code.
        await using var stream = Csv(
            "ain0_ts,ain0_val,ain1_ts,ain1_val",
            "1000,100,1000,200",
            "2000,100,2000,inf");

        // Act
        var (samples, progress) = await ParseAsync(stream, Options(integerValueUnits: null));

        // Assert
        Assert.Equal(2, samples.Count);
        Assert.Equal(SdCardAnalogValueUnit.RawAdcCodes, samples[1].SourceAnalogValueUnit);
        Assert.Equal(100.0 / 4095.0 * 10.0, samples[1].AnalogValues[0], 9);
        Assert.True(double.IsPositiveInfinity(samples[1].AnalogValues[1]));
        Assert.Equal(0, progress[^1].RowsSkipped);
    }

    [Theory]
    [InlineData(null, SdCardAnalogValueUnit.RawAdcCodes)]
    [InlineData(SdCardIntegerValueUnits.RawAdcCodes, SdCardAnalogValueUnit.RawAdcCodes)]
    [InlineData(SdCardIntegerValueUnits.Millivolts, SdCardAnalogValueUnit.Millivolts)]
    public async Task ParseAsync_RowOfNothingButCSpelledInfinities_ParsesAndKeepsItsUnit(
        SdCardIntegerValueUnits? integerValueUnits,
        SdCardAnalogValueUnit expectedUnit)
    {
        // Arrange — where the two fixes meet. #883 excluded a non-finite reading from the
        // decimal-form tally by testing the parsed number rather than the token, and #884 then
        // taught the parser C's lowercase spellings. Had #884 instead matched token text, this
        // row would read as two whole numbers in a volts file and ResolveRowUnit would throw
        // the mixed-form InvalidDataException — exactly the bug #883 fixed. Both signs are here
        // because the sign is stripped before the word is matched.
        await using var stream = Csv(
            "ain0_ts,ain0_val,ain1_ts,ain1_val",
            "1000,0.100,1000,0.200",
            "2000,inf,2000,-inf");

        // Act — no throw is half the assertion.
        var (samples, progress) = await ParseAsync(stream, Options(integerValueUnits));

        // Assert — the row survives whole, and because it holds no finite value at all it takes
        // the caller's whole-number default rather than refusing. That default is harmless: no
        // conversion in the parser alters an infinity.
        Assert.Equal(2, samples.Count);
        Assert.Equal(SdCardAnalogValueUnit.Volts, samples[0].SourceAnalogValueUnit);
        Assert.Equal(expectedUnit, samples[1].SourceAnalogValueUnit);
        Assert.True(double.IsPositiveInfinity(samples[1].AnalogValues[0]));
        Assert.True(double.IsNegativeInfinity(samples[1].AnalogValues[1]));

        // And nothing was dropped, so the count stays at zero.
        Assert.Equal(0, progress[^1].RowsSkipped);
    }

    [Fact]
    public async Task ParseAsync_CSpelledInfinityMixedWithNan_StillStatesNoUnit()
    {
        // Arrange — the other half of the meeting point: C's inf alongside the nan that
        // double.TryParse already took, in a volts row. Neither states a unit, so the row
        // resolves on its one finite reading instead of being refused as mixed-form.
        await using var stream = Csv(
            "ain0_ts,ain0_val,ain1_ts,ain1_val,ain2_ts,ain2_val",
            "1000,0.100,1000,0.200,1000,0.300",
            "2000,0.123,2000,-inf,2000,nan");

        // Act
        var options = Options(integerValueUnits: null);
        options.ConfigurationOverride = Override(3);
        var (samples, progress) = await ParseAsync(stream, options);

        // Assert
        Assert.Equal(2, samples.Count);
        Assert.Equal(SdCardAnalogValueUnit.Volts, samples[1].SourceAnalogValueUnit);
        Assert.Equal(0.123, samples[1].AnalogValues[0], 9);
        Assert.True(double.IsNegativeInfinity(samples[1].AnalogValues[1]));
        Assert.True(double.IsNaN(samples[1].AnalogValues[2]));
        Assert.Equal(0, progress[^1].RowsSkipped);
    }

    [Theory]
    // A word that merely starts like the spelling is not a reading, and neither is a sign on its
    // own: the parser must keep refusing these rather than inventing an infinity for them.
    [InlineData("info")]
    [InlineData("in")]
    [InlineData("i")]
    [InlineData("-")]
    [InlineData("infinit")]
    [InlineData("infinityy")]
    [InlineData("1inf")]
    [InlineData("--inf")]
    public async Task ParseAsync_TokenThatIsNotAReading_IsStillRefused(string valueToken)
    {
        // Arrange
        await using var stream = Csv(
            "ain0_ts,ain0_val,ain1_ts,ain1_val",
            "1000,0.100,1000,0.200",
            $"2000,0.123,2000,{valueToken}");

        // Act
        var (samples, progress) = await ParseAsync(stream, Options(integerValueUnits: null));

        // Assert — the clean row still imports and the unreadable one is skipped, but now it is
        // counted instead of vanishing.
        Assert.Single(samples);
        Assert.Equal(1, progress[^1].RowsSkipped);
    }

    // -------------------------------------------------------------------------
    // A dropped row is counted where a caller can see it
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_EveryRowReadable_ReportsNoSkippedRows()
    {
        // Arrange
        await using var stream = Csv(
            "ain0_ts,ain0_val,ain1_ts,ain1_val",
            "1000,0.100,1000,0.200",
            "2000,0.300,2000,0.400");

        // Act
        var (samples, progress) = await ParseAsync(stream, Options(integerValueUnits: null));

        // Assert — a caller reporting "every reading imported" is entitled to say so here.
        Assert.Equal(2, samples.Count);
        Assert.NotEmpty(progress);
        Assert.All(progress, p => Assert.Equal(0, p.RowsSkipped));
    }

    [Fact]
    public async Task ParseAsync_UnreadableRows_AreCountedInTheFinalProgressReport()
    {
        // Arrange — three rows the parser cannot read, among readable ones. The final progress
        // report always goes out, so it carries the whole file's count even though this file is
        // far shorter than the hundred-line reporting interval.
        await using var stream = Csv(
            "ain0_ts,ain0_val,ain1_ts,ain1_val",
            "1000,0.100,1000,0.200",
            "2000,0.300,junk",              // wrong column count
            "3000,0.500,3000,oops",         // value that is not a number
            "notatimestamp,0.700,4000,0.800", // timestamp that is not a number
            "5000,0.900,5000,1.000");

        // Act
        var (samples, progress) = await ParseAsync(stream, Options(integerValueUnits: null));

        // Assert
        Assert.Equal(2, samples.Count);
        Assert.Equal(3, progress[^1].RowsSkipped);
        Assert.Equal(5, progress[^1].MessagesRead);
    }

    [Fact]
    public async Task ParseAsync_SkippedRowCount_RisesWhileTheFileIsRead()
    {
        // Arrange — the count is cumulative across an enumeration, so a caller watching progress
        // on a long file sees the loss as it happens rather than only at the end. 250 rows puts
        // two reports ahead of the final one, at the parser's hundred-line interval — and every
        // hundredth row is readable, because a report is only taken after a row is produced.
        var rows = new List<string>();
        for (var i = 1; i <= 250; i++)
        {
            rows.Add(i % 10 == 5
                ? $"{i * 1000},0.100,{i * 1000},unreadable"
                : $"{i * 1000},0.100,{i * 1000},0.200");
        }

        await using var stream = Csv("ain0_ts,ain0_val,ain1_ts,ain1_val", rows.ToArray());

        // Act
        var (samples, progress) = await ParseAsync(stream, Options(integerValueUnits: null));

        // Assert
        Assert.Equal(225, samples.Count);
        Assert.Equal(25, progress[^1].RowsSkipped);
        Assert.True(progress.Count >= 3, $"expected interim reports, got {progress.Count}");
        Assert.Equal(10, progress[0].RowsSkipped);
        Assert.Equal(20, progress[1].RowsSkipped);

        // Monotonic: a count that went backwards would mean a caller could see loss undone.
        for (var i = 1; i < progress.Count; i++)
        {
            Assert.True(
                progress[i].RowsSkipped >= progress[i - 1].RowsSkipped,
                $"report {i} reported fewer skipped rows than report {i - 1}");
        }
    }

    [Fact]
    public async Task ParseAsync_SecondEnumeration_CountsFromZeroAgain()
    {
        // Arrange — the session re-reads the file on each enumeration, so the count belongs to
        // the enumeration and must not accumulate across two of them.
        var bytes = CsvBytes(
            "ain0_ts,ain0_val,ain1_ts,ain1_val",
            "1000,0.100,1000,0.200",
            "2000,0.300,2000,oops");

        var parser = new SdCardCsvFileParser();

        // Act
        var first = new List<SdCardParseProgress>();
        await using (var stream = new MemoryStream(bytes))
        {
            await DrainAsync(parser, stream, first);
        }

        var second = new List<SdCardParseProgress>();
        await using (var stream = new MemoryStream(bytes))
        {
            await DrainAsync(parser, stream, second);
        }

        // Assert
        Assert.Equal(1, first[^1].RowsSkipped);
        Assert.Equal(1, second[^1].RowsSkipped);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static byte[] CsvBytes(string columnHeader, params string[] dataRows)
    {
        var sb = new StringBuilder();
        sb.Append("# Device: Nyquist 1\n");
        sb.Append("# Serial Number: 7E2815916200E898\n");
        sb.Append($"# Timestamp Tick Rate: {TickRate} Hz\n");
        sb.Append(columnHeader).Append('\n');
        foreach (var row in dataRows)
        {
            sb.Append(row).Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static MemoryStream Csv(string columnHeader, params string[] dataRows) =>
        new(CsvBytes(columnHeader, dataRows));

    private static SdCardParseOptions Options(SdCardIntegerValueUnits? integerValueUnits) =>
        new()
        {
            ConfigurationOverride = Override(2),
            IntegerValueUnits = integerValueUnits
        };

    private static SdCardDeviceConfiguration Override(int channelCount) =>
        new(
            AnalogPortCount: channelCount,
            DigitalPortCount: 0,
            TimestampFrequency: TickRate,
            DeviceSerialNumber: "7E2815916200E898",
            DevicePartNumber: "Nyquist 1",
            FirmwareRevision: "3.8.0",
            CalibrationValues: Repeat((1.0, 0.0), channelCount),
            Resolution: AdcResolution,
            PortRange: Repeat(PortRangeVolts, channelCount),
            InternalScaleM: Repeat(1.0, channelCount));

    private static T[] Repeat<T>(T value, int count)
    {
        var values = new T[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = value;
        }

        return values;
    }

    private static async Task<(List<SdCardLogEntry> Samples, List<SdCardParseProgress> Progress)> ParseAsync(
        Stream stream,
        SdCardParseOptions options)
    {
        // A Progress<T> posts to the captured synchronization context, which in a test is the
        // thread pool, so reports could arrive after the enumeration finishes. Collect them
        // synchronously instead, so the assertions see every report in order.
        var reports = new List<SdCardParseProgress>();
        options.Progress = new SynchronousProgress(reports);

        var parser = new SdCardCsvFileParser();
        var session = await parser.ParseAsync(stream, "log_20261031_080550.csv", options);
        var samples = new List<SdCardLogEntry>();
        await foreach (var sample in session.Samples)
        {
            samples.Add(sample);
        }

        return (samples, reports);
    }

    private static async Task DrainAsync(
        SdCardCsvFileParser parser,
        Stream stream,
        List<SdCardParseProgress> reports)
    {
        var options = Options(integerValueUnits: null);
        options.Progress = new SynchronousProgress(reports);
        var session = await parser.ParseAsync(stream, "log_20261031_080550.csv", options);
        await foreach (var _ in session.Samples)
        {
        }
    }

    private sealed class SynchronousProgress(List<SdCardParseProgress> reports)
        : IProgress<SdCardParseProgress>
    {
        public void Report(SdCardParseProgress value) => reports.Add(value);
    }
}
