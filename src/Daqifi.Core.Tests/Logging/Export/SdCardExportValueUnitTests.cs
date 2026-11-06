using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Logging.Export;
using Daqifi.Core.Tests.Device.SdCard;

namespace Daqifi.Core.Tests.Logging.Export;

/// <summary>
/// The unit an SD-card export writes, and how it gets one at all.
/// </summary>
/// <remarks>
/// The same samples left Core in two different units depending on the path — volts from a live
/// recording, raw ADC counts from an SD-card log — and neither file said which (issue #903).
/// Firmware 3.8.0 writes no calibration into a log, so the only way the SD path can report volts
/// is for the connected device to supply the figures.
/// </remarks>
public class SdCardExportValueUnitTests
{
    private const uint Resolution = 1024u;
    private const uint TickRate = 42_000_000u;

    private static MemoryStream NoCalibrationCsvLog() =>
        SdCardTestCsvFileBuilder.BuildCsvFileSharedTimestamp(
            "Nyquist 1",
            "7E2815916200E898",
            TickRate,
            (1_000u, [0, 13]),
            (2_000u, [0, 15]));

    /// <summary>
    /// What a connected Nq1 supplies: a resolution and a unity calibration, so one ADC count is
    /// 1/1024 V exactly, which is the scale the live path reports.
    /// </summary>
    private static SdCardDeviceConfiguration LiveConfiguration() => new(
        AnalogPortCount: 2,
        DigitalPortCount: 1,
        TimestampFrequency: TickRate,
        DeviceSerialNumber: "7E2815916200E898",
        DevicePartNumber: "Nyquist 1",
        FirmwareRevision: "3.8.0",
        CalibrationValues: [(1.0, 0.0), (1.0, 0.0)],
        Resolution: Resolution,
        PortRange: [1.0, 1.0],
        InternalScaleM: [1.0, 1.0]);

    private static async Task<string[]> ExportAsync(SdCardLogSession session)
    {
        await using var writer = new StringWriter();
        await new CsvExporter().ExportAsync(session.AsSampleSource(), writer, new CsvExportOptions());
        return writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task WithNoCalibration_TheExportSaysTheValuesAreUnscaled()
    {
        using var log = NoCalibrationCsvLog();
        var session = await new SdCardCsvFileParser().ParseAsync(log, "log_20261106_093627.csv");

        var lines = await ExportAsync(session);

        // A number with no unit is the defect. An unscaled number that says so is readable.
        Assert.Equal(
            "Time,Daqifi:7E2815916200E898:AI0 (unscaled),Daqifi:7E2815916200E898:AI1 (unscaled),"
            + "Daqifi:7E2815916200E898:DIO",
            lines[0]);
        Assert.EndsWith(",0,13,0", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithTheDevicesCalibration_TheExportIsInVoltsAndSaysSo()
    {
        using var log = NoCalibrationCsvLog();
        var session = await new SdCardCsvFileParser().ParseAsync(
            log,
            "log_20261106_093627.csv",
            new SdCardParseOptions { ConfigurationOverride = LiveConfiguration() });

        var lines = await ExportAsync(session);

        Assert.Equal(
            "Time,Daqifi:7E2815916200E898:AI0 (V),Daqifi:7E2815916200E898:AI1 (V),"
            + "Daqifi:7E2815916200E898:DIO",
            lines[0]);

        // 13 counts at 1/1024 V per count, the same scale a live recording reports.
        Assert.EndsWith(",0,0.0126953125,0", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDigitalColumnIsNeverGivenAUnit()
    {
        // A digital column holds a port value, which has no unit.
        var builder = new SdCardTestFileBuilder();
        builder.AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
            timestamp: 1_000, analogIntValues: [7], digitalData: [0x03]));

        using var log = builder.Build();
        var session = await new SdCardFileParser().ParseAsync(log, "log.bin");

        var lines = await ExportAsync(session);

        Assert.EndsWith("Daqifi:unknown:DIO", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AValueUnitDoesNotChangeAChannelKey()
    {
        // The header is labelled, the key is not: a label could otherwise move a value into the
        // wrong column.
        var source = new SdCardLogSampleSource(
            EmptyEntries(), "SN", analogChannelCount: 1) { ValueUnit = SampleValueUnits.Volts };

        Assert.Equal("Daqifi:SN:AI0", source.GetChannels()[0].Key);
        Assert.Equal(SampleValueUnits.Volts, source.ValueUnit);
    }

    [Fact]
    public async Task ASourceThatStatesNoUnit_WritesTheHeaderUnchanged()
    {
        var source = new SdCardLogSampleSource(EmptyEntries(), "SN", analogChannelCount: 1);

        Assert.Null(source.ValueUnit);

        await using var writer = new StringWriter();
        await new CsvExporter().ExportAsync(source, writer, new CsvExportOptions());

        Assert.Equal(
            "Time,Daqifi:SN:AI0,Daqifi:SN:DIO",
            writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0]);
    }

    // ── SdCardParseOptions.ForDevice ────────────────────────────────────────

    [Fact]
    public void ForDevice_CarriesTheDevicesCalibration()
    {
        using var device = new DaqifiDevice("TestDevice");
        device.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = 2,
            DigitalPortNum = 1,
            AnalogInRes = Resolution,
            TimestampFreq = TickRate
        });

        var options = SdCardParseOptions.ForDevice(device);

        Assert.NotNull(options.ConfigurationOverride);
        Assert.Equal(Resolution, options.ConfigurationOverride.Resolution);
        Assert.Equal(TickRate, options.ConfigurationOverride.TimestampFrequency);
        Assert.Equal(2, options.ConfigurationOverride.AnalogPortCount);
    }

    [Fact]
    public async Task ForDevice_IsEnoughToMakeTheExportVolts()
    {
        using var device = new DaqifiDevice("TestDevice");
        device.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = 2,
            DigitalPortNum = 1,
            AnalogInRes = Resolution,
            TimestampFreq = TickRate
        });

        using var log = NoCalibrationCsvLog();
        var session = await new SdCardCsvFileParser().ParseAsync(
            log, "log_20261106_093627.csv", SdCardParseOptions.ForDevice(device));

        Assert.Equal(SampleValueUnits.Volts, session.AsSampleSource().ValueUnit);
    }

    [Fact]
    public void ForDevice_NullDevice_Throws()
    {
        Assert.Throws<ArgumentNullException>("device", () => SdCardParseOptions.ForDevice(null!));
    }

    private static async IAsyncEnumerable<SdCardLogEntry> EmptyEntries()
    {
        await Task.CompletedTask;
        yield break;
    }
}
