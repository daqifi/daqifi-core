using System.Text;
using Daqifi.Core.Channel;
using Daqifi.Core.Device;
using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Export;
using Daqifi.Core.Tests.Device.SdCard;

namespace Daqifi.Core.Tests.Export;

/// <summary>
/// A live recording and an SD-card recording of one channel on one device have to come out under
/// one column key, or nothing can join the two files (issue #915).
/// </summary>
public class DeviceIdentityExportTests
{
    private const string FirmwareHexSerial = "7E2815916200E898";
    private const string DecimalSerial = "9090539562006014104";

    public static TheoryData<SdCardLogFormat, string> LogsStatingTheSerialInHex => new()
    {
        {
            SdCardLogFormat.Csv,
            $"# Device: Nyquist 1\n# Serial Number: {FirmwareHexSerial}\n# Timestamp Tick Rate: 42000000 Hz\n" +
            "ain0_ts,ain0_val\n1000,13\n"
        },
        {
            SdCardLogFormat.Json,
            $"{{\"meta\":{{\"dev\":\"Nyquist 1\",\"sn\":\"{FirmwareHexSerial}\",\"tick_hz\":42000000}}}}\n" +
            "{\"ts\":1000,\"ai\":[{\"ch\":0,\"val\":13}]}\n"
        },
    };

    [Theory]
    [MemberData(nameof(LogsStatingTheSerialInHex))]
    public async Task WithNoNameOnEitherSide_AnSdExportHasTheLiveExportsKey(SdCardLogFormat format, string log)
    {
        var sd = (await ParseAsync(format, log)).AsSampleSource();

        Assert.Equal(LiveKey(DeviceConnectionOptions.DefaultDeviceName), sd.Channels[0].Key);
        Assert.Equal(LiveKey(null), sd.Channels[0].Key);
    }

    [Theory]
    [MemberData(nameof(LogsStatingTheSerialInHex))]
    public async Task GivenTheDevicesName_AnSdExportHasTheLiveExportsKey(SdCardLogFormat format, string log)
    {
        var sd = (await ParseAsync(format, log)).AsSampleSource(deviceName: "Bench Nq1");

        Assert.Equal(LiveKey("Bench Nq1"), sd.Channels[0].Key);
    }

    [Fact]
    public async Task AProtobufLogsSerial_IsTheSameDecimalText()
    {
        using var log = new SdCardTestFileBuilder()
            .AddMessage(SdCardTestFileBuilder.CreateStatusMessage(serialNumber: 9090539562006014104UL))
            .Build();

        var session = await new SdCardFileParser().ParseAsync(log, "log.bin");

        Assert.Equal(DecimalSerial, session.DeviceConfig?.DeviceSerialNumber);
    }

    [Theory]
    // The header's format comes from the firmware's format specifier, not from its digits: this is
    // 0x400, not the decimal number 400 it resembles.
    [InlineData("0000000000000400", "1024")]
    // Anything that is not the firmware's 16 hex digits is kept as written.
    [InlineData("SN1", "SN1")]
    [InlineData("9090539562006014104", "9090539562006014104")]
    public async Task OnlyTheFirmwaresOwnFormatIsConverted(string stated, string expected)
    {
        var log = $"# Device: Nyquist 1\n# Serial Number: {stated}\nain0_ts,ain0_val\n1000,13\n";

        var session = await ParseAsync(SdCardLogFormat.Csv, log);

        Assert.Equal(expected, session.DeviceConfig?.DeviceSerialNumber);
    }

    private static Task<SdCardLogSession> ParseAsync(SdCardLogFormat format, string log)
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes(log));
        return format == SdCardLogFormat.Csv
            ? new SdCardCsvFileParser().ParseAsync(stream, "log.csv")
            : new SdCardJsonFileParser().ParseAsync(stream, "log.json");
    }

    private static string LiveKey(string? deviceName)
    {
        var channel = new AnalogChannel(0) { Name = "AI0" };
        return new LiveStreamSampleSource(NoSamples(), [channel], deviceName, DecimalSerial).Channels[0].Key;
    }

    private static async IAsyncEnumerable<LiveSample> NoSamples()
    {
        await Task.CompletedTask;
        yield break;
    }
}
