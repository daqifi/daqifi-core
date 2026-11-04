using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Daqifi.Core.Device.SdCard;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// Pins which unit the CSV parser reads an analog value as, and what it converts it to
/// (issue #848).
/// <para>
/// Firmware writes one of three value forms and the file does not name the one it used
/// (daqifi/daqifi-nyquist-firmware#1179): raw ADC codes as whole numbers in raw mode, millivolts
/// as whole numbers at <c>CONF:VOLTage:PRECision 0</c>, and volts with a decimal point at any
/// higher precision. So the rule is: a decimal point means volts, and
/// <see cref="SdCardParseOptions.IntegerValueUnits"/> says what a whole number means.
/// </para>
/// <para>
/// Every fixture here uses numbers where the three readings differ by a large factor, so a
/// wrong branch cannot pass. With the override below — resolution 4095, port range 10 V — the
/// token <c>245</c> reads as 0.598 V as a raw code, as 0.245 V as millivolts, and as 245 V as
/// volts.
/// </para>
/// </summary>
public sealed class SdCardCsvValueUnitTests
{
    private const uint TickRate = 50_000_000u;

    /// <summary>ADC resolution in the issue's own repro, and on the bench Nyquist 1.</summary>
    private const uint AdcResolution = 4095u;

    /// <summary>Port range in volts, as the issue's repro states it.</summary>
    private const double PortRangeVolts = 10.0;

    // -------------------------------------------------------------------------
    // The rule, one case per file shape
    // -------------------------------------------------------------------------

    [Theory]
    // Raw ADC codes. The caller states raw, or states nothing — unset means raw, which is what
    // every release before this one assumed for every value.
    [InlineData("1000,2048", true, null, SdCardAnalogValueUnit.RawAdcCodes, 2048.0 / 4095.0 * 10.0)]
    [InlineData("1000,2048", true, SdCardIntegerValueUnits.RawAdcCodes, SdCardAnalogValueUnit.RawAdcCodes, 2048.0 / 4095.0 * 10.0)]
    // Raw ADC codes with no device configuration: there is nothing to convert them with, so the
    // codes come back as they were — and the sample says so, rather than calling them volts.
    [InlineData("1000,2048", false, null, SdCardAnalogValueUnit.RawAdcCodes, 2048.0)]
    // Volts, written with a decimal point. The device already applied its calibration, so the
    // value is reported unchanged whether or not an override is supplied. Before the fix the
    // override case returned 2.93e-06.
    [InlineData("1000,0.001200", true, null, SdCardAnalogValueUnit.Volts, 0.0012)]
    [InlineData("1000,0.001200", false, null, SdCardAnalogValueUnit.Volts, 0.0012)]
    // Volts stay volts even when the caller has stated what a whole number means, because this
    // row holds no whole number.
    [InlineData("1000,0.001200", true, SdCardIntegerValueUnits.Millivolts, SdCardAnalogValueUnit.Volts, 0.0012)]
    // Whole-number millivolts. The device already applied its calibration here too, so the only
    // conversion is a division by 1000 — again with or without an override. Before the fix the
    // override case returned 0.598 and the no-override case returned 245.
    [InlineData("1000,245", true, SdCardIntegerValueUnits.Millivolts, SdCardAnalogValueUnit.Millivolts, 0.245)]
    [InlineData("1000,245", false, SdCardIntegerValueUnits.Millivolts, SdCardAnalogValueUnit.Millivolts, 0.245)]
    public async Task ParseAsync_ReadsTheValueUnitFromTheFileAndConvertsForThatUnit(
        string dataRow,
        bool withOverride,
        SdCardIntegerValueUnits? integerValueUnits,
        SdCardAnalogValueUnit expectedUnit,
        double expectedValue)
    {
        // Arrange
        await using var stream = Csv("ain0_ts,ain0_val", dataRow);

        // Act
        var samples = await ParseAsync(stream, Options(withOverride, 1, integerValueUnits));

        // Assert
        var sample = Assert.Single(samples);
        Assert.Equal(expectedUnit, sample.SourceAnalogValueUnit);
        Assert.Equal(expectedValue, sample.AnalogValues[0], 9);
    }

    [Fact]
    public async Task ParseAsync_RowMixesDecimalAndWholeNumberValues_FailsInsteadOfGuessing()
    {
        // Arrange — one value has a decimal point and one does not, so no single unit covers the
        // row. Firmware writes one form for every channel in a recording, so a file like this is
        // not one it produced. Reading it either way reports a voltage wrong by 1000 or more.
        await using var stream = Csv("ain0_ts,ain0_val,ain1_ts,ain1_val", "1000,0.001200,1000,245");

        // Act + assert — before the fix both values were scaled as ADC codes and nothing said so.
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => ParseAsync(stream, Options(withOverride: true, channelCount: 2, integerValueUnits: null)));
        Assert.Contains("no single unit", error.Message);
    }

    // -------------------------------------------------------------------------
    // The recorded bench repro on issue #848
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_Issue848BenchRepro_ReadsTwelveAndSixteenMillivoltsAsVolts()
    {
        // Arrange — the capture recorded on issue #848: a two second, two channel, 100 Hz
        // recording on a Nyquist 1 at firmware 3.8.0, with the device in whole-number millivolt
        // mode. The first row held 12 and 16. They are 12 mV and 16 mV. The parser reported
        // 12.000 and 16.000 and called them volts, a factor of 1000 out.
        await using var stream = Csv("ain0_ts,ain0_val,ain1_ts,ain1_val", "1000,12,1000,16");

        // Act
        var samples = await ParseAsync(stream, Options(
            withOverride: false,
            channelCount: 2,
            integerValueUnits: SdCardIntegerValueUnits.Millivolts));

        // Assert
        var sample = Assert.Single(samples);
        Assert.Equal(SdCardAnalogValueUnit.Millivolts, sample.SourceAnalogValueUnit);
        Assert.Equal(0.012, sample.AnalogValues[0], 9);
        Assert.Equal(0.016, sample.AnalogValues[1], 9);
    }

    [Fact]
    public async Task ParseAsync_Issue848BenchRepro_WithTheDeviceOverride_StillReadsMillivolts()
    {
        // Arrange — the same capture, parsed the documented way, with a configuration override
        // taken from the connected device. This is the case the issue calls high severity: the
        // override made the reading worse, not better (12 mV came out as 0.0293 V).
        await using var stream = Csv("ain0_ts,ain0_val,ain1_ts,ain1_val", "1000,12,1000,16");

        // Act
        var samples = await ParseAsync(stream, Options(
            withOverride: true,
            channelCount: 2,
            integerValueUnits: SdCardIntegerValueUnits.Millivolts));

        // Assert
        var sample = Assert.Single(samples);
        Assert.Equal(SdCardAnalogValueUnit.Millivolts, sample.SourceAnalogValueUnit);
        Assert.Equal(0.012, sample.AnalogValues[0], 9);
        Assert.Equal(0.016, sample.AnalogValues[1], 9);
    }

    // -------------------------------------------------------------------------
    // The unit is per row, and the digital pair is not an analog value
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_DigitalPair_DoesNotDecideTheAnalogUnit()
    {
        // Arrange — a dio pair is always a whole number. It must not turn a volts row into a
        // mixed row, and it must not be divided by 1000 either.
        await using var stream = Csv(
            "ain0_ts,ain0_val,dio_ts,dio_val",
            "1000,0.001200,1000,7");

        // Act
        var samples = await ParseAsync(stream, Options(
            withOverride: true,
            channelCount: 1,
            integerValueUnits: SdCardIntegerValueUnits.Millivolts));

        // Assert
        var sample = Assert.Single(samples);
        Assert.Equal(SdCardAnalogValueUnit.Volts, sample.SourceAnalogValueUnit);
        Assert.Equal(0.0012, sample.AnalogValues[0], 9);
        Assert.Equal(7u, sample.DigitalData);
    }

    [Fact]
    public async Task ParseAsync_ValueWrittenWithAnExponent_IsVolts()
    {
        // Arrange — an exponent is a decimal form too, so this value is volts and keeps its
        // size. Read as an ADC code it would come out at 2.9e-09.
        await using var stream = Csv("ain0_ts,ain0_val", "1000,1.2E-03");

        // Act
        var samples = await ParseAsync(stream, Options(withOverride: true, channelCount: 1, integerValueUnits: null));

        // Assert
        var sample = Assert.Single(samples);
        Assert.Equal(SdCardAnalogValueUnit.Volts, sample.SourceAnalogValueUnit);
        Assert.Equal(0.0012, sample.AnalogValues[0], 9);
    }

    [Fact]
    public async Task ParseAsync_PrecisionFourCapture_ReportsVoltsForEveryRow()
    {
        // Arrange — the issue's precision-4 repro: row 0 is 0.0012 V and 0.0024 V with two dio
        // channels at zero. With the override it used to come out as 2.93e-06 and 5.86e-06.
        await using var stream = Csv(
            "ain0_ts,ain0_val,ain1_ts,ain1_val",
            "1000,0.001200,1000,0.002400",
            "2000,0.003600,2000,0.004800");

        // Act
        var samples = await ParseAsync(stream, Options(withOverride: true, channelCount: 2, integerValueUnits: null));

        // Assert
        Assert.Equal(2, samples.Count);
        Assert.All(samples, s => Assert.Equal(SdCardAnalogValueUnit.Volts, s.SourceAnalogValueUnit));
        Assert.Equal(0.0012, samples[0].AnalogValues[0], 9);
        Assert.Equal(0.0024, samples[0].AnalogValues[1], 9);
        Assert.Equal(0.0036, samples[1].AnalogValues[0], 9);
        Assert.Equal(0.0048, samples[1].AnalogValues[1], 9);
    }

    // -------------------------------------------------------------------------
    // A non-finite reading states no unit (issue #883)
    // -------------------------------------------------------------------------

    [Theory]
    // The volts control from the issue's table: nothing non-finite, two decimal-form values.
    [InlineData("2000,0.123,2000,0.456", null, SdCardAnalogValueUnit.Volts, 0.123, false)]
    // Whole numbers plus a NaN. This case parsed before the fix as well, because none of the
    // row's finite values is in the decimal form either.
    [InlineData("2000,100,2000,nan", null, SdCardAnalogValueUnit.RawAdcCodes, 100.0 / 4095.0 * 10.0, true)]
    // The regression: a volts row with one NaN. Before the fix the NaN counted as a whole
    // number, the row read as mixed, and the exception refused the whole file.
    [InlineData("2000,0.123,2000,nan", null, SdCardAnalogValueUnit.Volts, 0.123, true)]
    // The same row with the caller's whole-number default set. The NaN is still unit-less, so
    // the row is still volts and the finite value is not divided by 1000.
    [InlineData("2000,0.123,2000,nan", SdCardIntegerValueUnits.Millivolts, SdCardAnalogValueUnit.Volts, 0.123, true)]
    // The .NET spellings of an infinity, which double.TryParse accepts today. Both used to be
    // read as whole numbers and refuse a volts file.
    [InlineData("2000,0.123,2000,Infinity", null, SdCardAnalogValueUnit.Volts, 0.123, true)]
    [InlineData("2000,0.123,2000,-Infinity", null, SdCardAnalogValueUnit.Volts, 0.123, true)]
    [InlineData("2000,0.123,2000,infinity", null, SdCardAnalogValueUnit.Volts, 0.123, true)]
    [InlineData("2000,0.123,2000,NAN", null, SdCardAnalogValueUnit.Volts, 0.123, true)]
    // Whole-number millivolts with a NaN alongside: the finite value still divides by 1000.
    [InlineData("2000,245,2000,nan", SdCardIntegerValueUnits.Millivolts, SdCardAnalogValueUnit.Millivolts, 0.245, true)]
    public async Task ParseAsync_NonFiniteValue_DoesNotDecideOrVetoTheRowUnit(
        string dataRow,
        SdCardIntegerValueUnits? integerValueUnits,
        SdCardAnalogValueUnit expectedUnit,
        double expectedFirstValue,
        bool secondValueIsNonFinite)
    {
        // Arrange — a clean volts row ahead of the row under test, exactly as the issue measured
        // it, so a file-level refusal is visible as the loss of both rows rather than one.
        await using var stream = Csv(
            "ain0_ts,ain0_val,ain1_ts,ain1_val",
            "1000,0.100,1000,0.200",
            dataRow);

        // Act
        var samples = await ParseAsync(stream, Options(withOverride: true, channelCount: 2, integerValueUnits));

        // Assert — both rows survive, and the row under test reports the unit its finite values
        // state.
        Assert.Equal(2, samples.Count);
        Assert.Equal(SdCardAnalogValueUnit.Volts, samples[0].SourceAnalogValueUnit);
        Assert.Equal(expectedUnit, samples[1].SourceAnalogValueUnit);
        Assert.Equal(expectedFirstValue, samples[1].AnalogValues[0], 9);

        if (secondValueIsNonFinite)
        {
            // The reading is reported as the non-finite value it is, not dropped and not
            // laundered into a number: that is what tells a caller the channel read nothing.
            Assert.False(double.IsFinite(samples[1].AnalogValues[1]));
        }
        else
        {
            Assert.Equal(0.456, samples[1].AnalogValues[1], 9);
        }
    }

    [Fact]
    public async Task ParseAsync_RowHoldsOnlyNonFiniteValues_TakesTheCallersWholeNumberDefault()
    {
        // Arrange — no value in the row states a unit, so there is nothing to read the unit
        // from. The caller's default is the only answer available, and it costs nothing:
        // no conversion changes a NaN.
        await using var stream = Csv(
            "ain0_ts,ain0_val,ain1_ts,ain1_val",
            "1000,0.100,1000,0.200",
            "2000,nan,2000,nan");

        // Act
        var samples = await ParseAsync(stream, Options(
            withOverride: true,
            channelCount: 2,
            integerValueUnits: SdCardIntegerValueUnits.Millivolts));

        // Assert
        Assert.Equal(2, samples.Count);
        Assert.Equal(SdCardAnalogValueUnit.Millivolts, samples[1].SourceAnalogValueUnit);
        Assert.All(samples[1].AnalogValues, v => Assert.True(double.IsNaN(v)));
    }

    [Fact]
    public async Task ParseAsync_RowStillMixesTwoFiniteForms_StillFails()
    {
        // Arrange — the mixed-form rule is not relaxed by #883: a NaN alongside a genuine
        // decimal/whole-number mix does not excuse the mix, because the two finite values still
        // disagree about the row's unit.
        await using var stream = Csv(
            "ain0_ts,ain0_val,ain1_ts,ain1_val,ain2_ts,ain2_val",
            "1000,0.001200,1000,245,1000,nan");

        // Act + assert
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => ParseAsync(stream, Options(withOverride: true, channelCount: 3, integerValueUnits: null)));
        Assert.Contains("no single unit", error.Message);
    }

    [Fact]
    public async Task ParseAsync_NonFiniteValueInAVoltsRow_DoesNotRefuseTheRestOfTheFile()
    {
        // Arrange — the issue's headline cost: before the fix the exception came out of the
        // parser and the caller imported nothing at all, so rows after the offending one were
        // lost as well.
        await using var stream = Csv(
            "ain0_ts,ain0_val,ain1_ts,ain1_val",
            "1000,0.100,1000,0.200",
            "2000,0.123,2000,nan",
            "3000,0.300,3000,0.400");

        // Act
        var samples = await ParseAsync(stream, Options(withOverride: true, channelCount: 2, integerValueUnits: null));

        // Assert
        Assert.Equal(3, samples.Count);
        Assert.All(samples, s => Assert.Equal(SdCardAnalogValueUnit.Volts, s.SourceAnalogValueUnit));
        Assert.Equal(0.300, samples[2].AnalogValues[0], 9);
        Assert.Equal(0.400, samples[2].AnalogValues[1], 9);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a firmware-shaped CSV file whose value tokens are written exactly as given, so
    /// each fixture controls the lexical form the rule reads.
    /// </summary>
    private static MemoryStream Csv(string columnHeader, params string[] dataRows)
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

        return new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static SdCardParseOptions Options(
        bool withOverride,
        int channelCount,
        SdCardIntegerValueUnits? integerValueUnits) =>
        new()
        {
            ConfigurationOverride = withOverride ? FromDeviceStyleOverride(channelCount) : null,
            IntegerValueUnits = integerValueUnits
        };

    /// <summary>
    /// The configuration a caller gets from <c>SdCardDeviceConfiguration.FromDevice</c>, which
    /// is the documented way to parse a log straight after download. Resolution and port range
    /// are the figures in the issue's repro and on the bench Nyquist 1.
    /// </summary>
    private static SdCardDeviceConfiguration FromDeviceStyleOverride(int channelCount) =>
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

    private static async Task<List<SdCardLogEntry>> ParseAsync(Stream stream, SdCardParseOptions options)
    {
        var parser = new SdCardCsvFileParser();
        var session = await parser.ParseAsync(stream, "log_20261031_080550.csv", options);
        var samples = new List<SdCardLogEntry>();
        await foreach (var sample in session.Samples)
        {
            samples.Add(sample);
        }

        return samples;
    }
}
