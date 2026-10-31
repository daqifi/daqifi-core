using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Daqifi.Core.Device.SdCard;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// Pins which channel's calibration an SD card CSV column is scaled with (#849).
/// </summary>
/// <remarks>
/// <para>
/// The firmware names only the <em>enabled</em> channels in the CSV header, by number
/// (<c>ain7_ts,ain7_val</c>), so a column's position in the row says nothing about which
/// channel it carries. The parser used to look calibration, port range, and internal scale up
/// by position anyway, which silently gave a lone <c>ain7_val</c> column channel 0's
/// coefficients.
/// </para>
/// <para>
/// Every configuration here gives each channel a <em>different</em> slope, intercept, range,
/// and internal scale, and every column a different raw count. A lookup that reads the wrong
/// channel therefore produces a visibly different number instead of the same one; the shuffled
/// cases below assert both the value the named channel gives and that it is not the value the
/// old positional lookup gave.
/// </para>
/// </remarks>
public sealed class SdCardCsvCalibrationByChannelTests
{
    private const uint Resolution = 1000u;
    private const uint TickRate = 1000u;

    // Eight channels, no two alike in any list.
    private static readonly (double Slope, double Intercept)[] Calibration =
    [
        (1.5, 0.01), (2.0, 0.02), (2.5, 0.03), (3.0, 0.04),
        (3.5, 0.05), (4.0, 0.06), (4.5, 0.07), (5.0, 0.08)
    ];

    private static readonly double[] PortRange = [10.0, 11.0, 12.0, 13.0, 14.0, 15.0, 16.0, 17.0];

    private static readonly double[] InternalScale = [1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 2.75];

    private static SdCardDeviceConfiguration EightChannelConfig() => new(
        AnalogPortCount: 8,
        DigitalPortCount: 1,
        TimestampFrequency: TickRate,
        DeviceSerialNumber: "SN849",
        DevicePartNumber: "Nyquist 1",
        FirmwareRevision: "3.8.0",
        CalibrationValues: Calibration,
        Resolution: Resolution,
        PortRange: PortRange,
        InternalScaleM: InternalScale);

    /// <summary>
    /// The volts channel <paramref name="channel"/> gives for <paramref name="raw"/>:
    /// <c>raw / resolution * portRange * calM * internalScaleM + calB</c>.
    /// </summary>
    private static double VoltsFor(int channel, double raw) =>
        raw / Resolution * PortRange[channel] * Calibration[channel].Slope * InternalScale[channel]
        + Calibration[channel].Intercept;

    /// <summary>
    /// The volts an unconfigured channel gives: unity gain, no offset, a one-volt range, and a
    /// unity internal scale — the documented fallback for a channel the configuration does not
    /// describe, shared with the protobuf and JSON paths.
    /// </summary>
    private static double UnconfiguredVoltsFor(double raw) => raw / Resolution;

    // ---------------------------------------------------------------------
    // Which channel's coefficients a column gets
    // ---------------------------------------------------------------------

    /// <summary>
    /// One row per header order. <c>expectedChannels</c> lists, per analog column, the channel
    /// whose coefficients that column must be scaled with.
    /// </summary>
    [Theory]
    // Natural order — the only case where position and name agree.
    [InlineData("ain0_ts,ain0_val,ain1_ts,ain1_val,ain2_ts,ain2_val", "0,1,2")]
    // Shuffled: the firmware writes the enabled channels in its own order.
    [InlineData("ain3_ts,ain3_val,ain1_ts,ain1_val,ain0_ts,ain0_val", "3,1,0")]
    // The issue's own repro: one high channel, alone in the file.
    [InlineData("ain7_ts,ain7_val", "7")]
    // A gap in the enabled set, with the digital pair still at the end.
    [InlineData("ain2_ts,ain2_val,ain5_ts,ain5_val,dio_ts,dio_val", "2,5")]
    // The "ch" prefix variant, also out of order.
    [InlineData("ch5_ts,ch5_val,ch2_ts,ch2_val", "5,2")]
    // A leading zero is still the same channel number.
    [InlineData("ain04_ts,ain04_val", "4")]
    // A duplicated channel: both columns name channel 2, so both get channel 2's coefficients.
    [InlineData("ain2_ts,ain2_val,ain2_ts,ain2_val", "2,2")]
    public async Task ParseAsync_ScalesEachColumnWithTheChannelItsHeaderNames(
        string header,
        string expectedChannels)
    {
        // Arrange — a different raw count per column, so a swapped value shows up too.
        var channels = expectedChannels
            .Split(',')
            .Select(c => int.Parse(c, CultureInfo.InvariantCulture))
            .ToArray();
        var raws = Enumerable.Range(0, channels.Length)
            .Select(i => 100.0 * (i + 1))
            .ToArray();

        await using var stream = BuildCsv(header, raws, digitalValue: 7u);
        var options = new SdCardParseOptions { ConfigurationOverride = EightChannelConfig() };

        // Act
        var session = await new SdCardCsvFileParser().ParseAsync(stream, "log.csv", options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        var sample = Assert.Single(samples);
        Assert.Equal(channels.Length, sample.AnalogValues.Count);

        for (var column = 0; column < channels.Length; column++)
        {
            var channel = channels[column];
            Assert.Equal(VoltsFor(channel, raws[column]), sample.AnalogValues[column], 10);

            if (channel != column)
            {
                // The number the positional lookup produced, which no column may land on now.
                Assert.NotEqual(VoltsFor(column, raws[column]), sample.AnalogValues[column], 10);
            }
        }
    }

    [Fact]
    public async Task ParseAsync_HeaderNamesAChannelTheConfigurationDoesNotDescribe_UsesTheDocumentedFallbackNotAnotherChannel()
    {
        // Arrange — the log names channel 9; the configuration describes eight channels, so it
        // holds no coefficients for it. The old positional lookup read slot 0 and reported
        // channel 0's volts for it.
        await using var stream = BuildCsv("ain9_ts,ain9_val", [500.0]);
        var options = new SdCardParseOptions { ConfigurationOverride = EightChannelConfig() };

        // Act
        var session = await new SdCardCsvFileParser().ParseAsync(stream, "log.csv", options);
        var samples = await ToListAsync(session.Samples);

        // Assert — unity gain and a one-volt range, never channel 0's gain.
        var sample = Assert.Single(samples);
        Assert.Equal(UnconfiguredVoltsFor(500.0), sample.AnalogValues[0], 10);
        Assert.NotEqual(VoltsFor(0, 500.0), sample.AnalogValues[0], 10);
    }

    [Fact]
    public async Task ParseAsync_TheReproFromIssue849_ReportsTwentyVoltsNotTen()
    {
        // Arrange — the issue's own numbers: one channel-7 column holding a full-scale count,
        // a gain of 2 on channel 7, and a gain of 1 on channel 0. Channel 7's coefficients
        // give 20 V; channel 0's give 10 V, which is what the file used to report.
        var content =
            "# Device: Nyquist 1\n" +
            "# Timestamp Tick Rate: 1000 Hz\n" +
            "ain7_ts,ain7_val\n" +
            "1000,4095\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var config = new SdCardDeviceConfiguration(
            AnalogPortCount: 8,
            DigitalPortCount: 0,
            TimestampFrequency: 1000u,
            DeviceSerialNumber: null,
            DevicePartNumber: "Nyquist 1",
            FirmwareRevision: null,
            CalibrationValues: [(1.0, 0.0), (1.0, 0.0), (1.0, 0.0), (1.0, 0.0),
                                (1.0, 0.0), (1.0, 0.0), (1.0, 0.0), (2.0, 0.0)],
            Resolution: 4095u,
            PortRange: [10.0, 10.0, 10.0, 10.0, 10.0, 10.0, 10.0, 10.0],
            InternalScaleM: [1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0]);

        // Act
        var session = await new SdCardCsvFileParser().ParseAsync(
            stream, "log.csv", new SdCardParseOptions { ConfigurationOverride = config });
        var samples = await ToListAsync(session.Samples);

        // Assert
        var sample = Assert.Single(samples);
        Assert.Equal(20.0, sample.AnalogValues[0], 10);
    }

    // ---------------------------------------------------------------------
    // A header that cannot be matched fails instead of guessing
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("foo_ts,foo_val")]                      // not a channel name at all
    [InlineData("ain_ts,ain_val")]                      // the prefix, with no number
    [InlineData("ainX_ts,ainX_val")]                    // a number that is not a number
    [InlineData("ain0_ts,ain0_val,bogus_ts,bogus_val")] // one good column, one not
    public async Task ParseAsync_AnalogHeaderThatNamesNoChannel_ThrowsInsteadOfScalingByPosition(
        string header)
    {
        // Arrange — a column whose channel number cannot be read. Scaling it by position would
        // report a wrong voltage with nothing to say so, which is worse than a failed parse.
        var raws = Enumerable.Range(0, header.Split(',').Length / 2)
            .Select(i => 100.0 * (i + 1))
            .ToArray();
        await using var stream = BuildCsv(header, raws);
        var options = new SdCardParseOptions { ConfigurationOverride = EightChannelConfig() };

        // Act + Assert — the message names the column, so the caller can see which one it is.
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => new SdCardCsvFileParser().ParseAsync(stream, "log.csv", options));
        Assert.Contains("_ts", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_AnalogHeaderThatNamesNoChannel_FailsEvenWithNoConfiguration()
    {
        // Arrange — the header is wrong whether or not anything would have been scaled, and a
        // caller that reads the file now and supplies a configuration later must not have been
        // told the file was fine.
        await using var stream = BuildCsv("foo_ts,foo_val", [100.0]);

        // Act + Assert
        await Assert.ThrowsAsync<InvalidDataException>(
            () => new SdCardCsvFileParser().ParseAsync(stream, "log.csv"));
    }

    [Fact]
    public async Task ParseAsync_DigitalOnlyHeader_IsNotTreatedAsAnUnmatchedChannel()
    {
        // Arrange — "dio" names no channel number and never needs one.
        var content =
            $"# Timestamp Tick Rate: {TickRate} Hz\n" +
            "dio_ts,dio_val\n" +
            "1000,5\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        // Act
        var session = await new SdCardCsvFileParser().ParseAsync(stream, "log.csv");
        var samples = await ToListAsync(session.Samples);

        // Assert
        var sample = Assert.Single(samples);
        Assert.Empty(sample.AnalogValues);
        Assert.Equal(5u, sample.DigitalData);
    }

    [Fact]
    public async Task ParseAsync_HeaderWithSpacesAfterEachComma_StillReadsEveryChannelNumber()
    {
        // Arrange — a hand-edited header. The spaces must not hide a channel number, and must
        // not turn the dio pair into an unmatched analog column either. The data rows keep the
        // firmware's own spacing: a space in a data row is a separate question, which the
        // parser's skip-the-bad-row contract already answers.
        var content =
            $"# Timestamp Tick Rate: {TickRate} Hz\n" +
            "ain3_ts, ain3_val, ain1_ts, ain1_val, dio_ts, dio_val\n" +
            "1000,100,1000,200,1000,7\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var options = new SdCardParseOptions { ConfigurationOverride = EightChannelConfig() };

        // Act
        var session = await new SdCardCsvFileParser().ParseAsync(stream, "log.csv", options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        var sample = Assert.Single(samples);
        Assert.Equal(2, sample.AnalogValues.Count);
        Assert.Equal(VoltsFor(3, 100.0), sample.AnalogValues[0], 10);
        Assert.Equal(VoltsFor(1, 200.0), sample.AnalogValues[1], 10);
        Assert.Equal(7u, sample.DigitalData);
    }

    [Fact]
    public async Task ParseAsync_FileWithNoColumnHeader_StillScalesByPosition()
    {
        // Arrange — data rows only. Nothing names a channel, so position is all there is; this
        // is the one case the name-based lookup cannot improve on.
        var content =
            $"# Timestamp Tick Rate: {TickRate} Hz\n" +
            "1000,100,1000,200\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var options = new SdCardParseOptions { ConfigurationOverride = EightChannelConfig() };

        // Act
        var session = await new SdCardCsvFileParser().ParseAsync(stream, "log.csv", options);
        var samples = await ToListAsync(session.Samples);

        // Assert
        var sample = Assert.Single(samples);
        Assert.Equal(VoltsFor(0, 100.0), sample.AnalogValues[0], 10);
        Assert.Equal(VoltsFor(1, 200.0), sample.AnalogValues[1], 10);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Writes a one-row CSV log with the given column header. A header ending in a
    /// <c>dio</c> pair also gets <paramref name="digitalValue"/> as its last column.
    /// </summary>
    private static MemoryStream BuildCsv(string header, double[] raws, uint digitalValue = 0u)
    {
        var columns = new List<string>();
        foreach (var raw in raws)
        {
            columns.Add("1000");
            columns.Add(raw.ToString("F6", CultureInfo.InvariantCulture));
        }

        if (header.Contains("dio", StringComparison.OrdinalIgnoreCase))
        {
            columns.Add("1000");
            columns.Add(digitalValue.ToString(CultureInfo.InvariantCulture));
        }

        var content =
            "# Device: Nyquist 1\n" +
            "# Serial Number: SN849\n" +
            $"# Timestamp Tick Rate: {TickRate} Hz\n" +
            header + "\n" +
            string.Join(",", columns) + "\n";

        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }

    private static async Task<List<SdCardLogEntry>> ToListAsync(IAsyncEnumerable<SdCardLogEntry> source)
    {
        var list = new List<SdCardLogEntry>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
