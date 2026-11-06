using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Logging.Export;
using Daqifi.Core.Tests.Device.SdCard;

namespace Daqifi.Core.Tests.Logging.Export;

/// <summary>
/// Exports a protobuf SD-card log that carries no configuration block, which is what firmware
/// 3.8.0 writes. The log states no analog port count and no timestamp clock, so both have to come
/// from somewhere else — and taking the port count as zero discarded every analog reading in the
/// file while reporting a successful export (issue #902).
/// </summary>
/// <remarks>
/// The fixtures here mirror the shape of a real recording: the first message carries the digital
/// port alone, so "take the first entry's width" still yields zero, and the analog values follow.
/// </remarks>
public class SdCardProtobufLogExportTests
{
    /// <summary>
    /// A log with no configuration block at all: no port counts, no timestamp clock, no serial.
    /// The first message carries the digital port only.
    /// </summary>
    private static MemoryStream NoConfigurationBlockLog(int analogWidth = 16, int entries = 4)
    {
        var builder = new SdCardTestFileBuilder();

        builder.AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
            timestamp: 1_000,
            digitalData: [0x00]));

        for (var i = 0; i < entries; i++)
        {
            var values = new int[analogWidth];
            for (var ch = 0; ch < analogWidth; ch++)
            {
                values[ch] = ch + i;
            }

            builder.AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
                timestamp: (uint)(2_000 + (i * 1_000)),
                analogIntValues: values,
                digitalData: [0x00]));
        }

        return builder.Build();
    }

    private static async Task<SdCardLogSession> ParseAsync(MemoryStream log) =>
        await new SdCardFileParser().ParseAsync(log, "log_20261106_093815.bin");

    [Fact]
    public async Task ALogThatStatesNoPortCount_StillExportsEveryAnalogColumn()
    {
        using var log = NoConfigurationBlockLog();
        var session = await ParseAsync(log);

        // The log itself states nothing, exactly as the bench recording does.
        Assert.Null(session.DeviceConfig);

        var source = session.AsSampleSource();

        Assert.Equal(16, source.AnalogChannelCount);
        Assert.Equal(17, source.GetChannels().Count);
        Assert.Equal("Daqifi:unknown:AI0", source.GetChannels()[0].Key);
        Assert.Equal("Daqifi:unknown:AI15", source.GetChannels()[15].Key);
        Assert.Equal("Daqifi:unknown:DIO", source.GetChannels()[16].Key);
    }

    [Fact]
    public async Task ALogThatStatesNoPortCount_ExportsTheValuesTheEntriesCarry()
    {
        using var log = NoConfigurationBlockLog();
        var session = await ParseAsync(log);
        var source = session.AsSampleSource();

        await using var writer = new StringWriter();
        await new CsvExporter().ExportAsync(source, writer, new CsvExportOptions());

        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("Time,Daqifi:unknown:AI0,", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(",Daqifi:unknown:AI15,Daqifi:unknown:DIO", lines[0], StringComparison.Ordinal);

        // 1 header + 1 digital-only entry + 4 entries carrying values.
        Assert.Equal(6, lines.Length);

        // The second entry carries 0..15; every one of those values reaches the file.
        var fields = lines[2].Split(',');
        Assert.Equal(18, fields.Length);
        Assert.Equal(Enumerable.Range(0, 16).Select(v => v.ToString()).ToArray(), fields[1..17]);

        // Nothing had to be dropped for want of a column.
        Assert.Equal(0, source.DroppedAnalogColumns);
    }

    [Fact]
    public async Task ALogWithNoAnalogValuesAtAll_ExportsTheDigitalColumnAlone()
    {
        // "This device has no analog channels" and "the file did not say" are different facts.
        // A log with no analog values in it is the first, and it must not grow invented columns.
        using var log = NoConfigurationBlockLog(analogWidth: 0);
        var session = await ParseAsync(log);

        var source = session.AsSampleSource();

        Assert.Equal(0, source.AnalogChannelCount);
        Assert.Equal(new[] { "Daqifi:unknown:DIO" }, source.GetChannels().Select(c => c.Key).ToArray());
        Assert.Equal(0, session.ObservedAnalogValueCount);
    }

    [Fact]
    public async Task ALogThatStatesNoPortCount_ReportsTheWidthItSawInTheData()
    {
        using var log = NoConfigurationBlockLog(analogWidth: 3);
        var session = await ParseAsync(log);

        Assert.Equal(3, session.ObservedAnalogValueCount);
    }

    [Fact]
    public async Task AStatusMessageThatStatesTheClockButNotThePortCount_StillFindsTheWidth()
    {
        // The configuration scan used to stop as soon as the clock was settled. A status message
        // that states the clock and no port count left the width unknown, and the scan stopped
        // before the first entry that could have answered it.
        var builder = new SdCardTestFileBuilder();
        builder.AddMessage(SdCardTestFileBuilder.CreateStatusMessage(
            analogPortNum: 0,
            digitalPortNum: 1,
            timestampFreq: 42_000_000,
            firmwareRevision: null,
            partNumber: null,
            serialNumber: 0));
        builder.AddMessage(SdCardTestFileBuilder.CreateStreamMessage(
            timestamp: 2_000,
            analogIntValues: [1, 2, 3, 4],
            digitalData: [0x00]));

        using var log = builder.Build();
        var session = await ParseAsync(log);

        Assert.Equal(4, session.ObservedAnalogValueCount);
        Assert.Equal(4, session.AsSampleSource().AnalogChannelCount);
    }

    [Fact]
    public async Task ALogThatStatesNoClock_UsesTheRateShippedFirmwareReports()
    {
        // 50 MHz matched no shipped device, so every timestamp in such a file was about 19% fast.
        using var log = NoConfigurationBlockLog();
        var session = await ParseAsync(log);

        Assert.Equal(42_000_000u, session.TimestampFrequency);
        Assert.Equal(SdCardTimestampSource.Fallback, session.TimestampFrequencySource);
    }

    [Fact]
    public async Task AConnectedDevicesClock_StillBeatsTheDefault()
    {
        using var log = NoConfigurationBlockLog();
        var session = await new SdCardFileParser().ParseAsync(
            log,
            "log_20261106_093815.bin",
            new SdCardParseOptions
            {
                ConfigurationOverride = new SdCardDeviceConfiguration(
                    AnalogPortCount: 16,
                    DigitalPortCount: 1,
                    TimestampFrequency: 80_000_000,
                    DeviceSerialNumber: "LIVE-SN",
                    DevicePartNumber: null,
                    FirmwareRevision: null,
                    CalibrationValues: null)
            });

        Assert.Equal(80_000_000u, session.TimestampFrequency);
        Assert.Equal(SdCardTimestampSource.Device, session.TimestampFrequencySource);
    }
}
