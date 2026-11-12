using Daqifi.Core.Device;
using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Firmware;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// The firmware gate on CRC-protected SD writes (issue #956), driven through
/// <see cref="DaqifiStreamingDevice"/> so the real <see cref="DaqifiDevice.EnsureSupported"/> and
/// requirement table decide, rather than a test double's answer.
/// </summary>
public partial class SdCardOperationsTests
{
    private static TestableSdCardStreamingDevice NyquistOn(string firmwareVersion)
    {
        var device = new TestableSdCardStreamingDevice("TestDevice");
        device.Metadata.DeviceType = DeviceType.Nyquist1;
        device.Metadata.FirmwareVersion = firmwareVersion;
        device.Metadata.Capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
        return device;
    }

    [Theory]
    [InlineData("3.8.0")]
    [InlineData("3.5.0")]
    public async Task SetSdCardCrcProtectionAsync_OnFirmwareBeforeV3_9_0_ThrowsFeatureNotSupportedAndSendsNothing(string firmware)
    {
        var device = NyquistOn(firmware);
        device.Connect();

        var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(
            () => device.SetSdCardCrcProtectionAsync(true));

        Assert.Equal(DeviceFeature.SdCardCrcProtection, ex.Feature);
        Assert.Equal(new FirmwareVersion(3, 9, 0, null, 0), ex.RequiredVersion);
        Assert.Empty(device.SentMessages);
    }

    [Fact]
    public async Task GetSdCardCrcProtectionAsync_OnFirmwareBeforeV3_9_0_ThrowsFeatureNotSupportedAndSendsNothing()
    {
        var device = NyquistOn("3.8.0");
        device.Connect();

        var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(
            () => device.GetSdCardCrcProtectionAsync());

        Assert.Equal(DeviceFeature.SdCardCrcProtection, ex.Feature);
        Assert.Empty(device.SentMessages);
    }

    [Fact]
    public async Task SetSdCardCrcProtectionAsync_OnV3_9_0_SendsTheCommandAndReportsItApplied()
    {
        var device = NyquistOn("3.9.0");
        device.CannedTextResponse = new() { "7797608448,7798738944" };
        device.Connect();

        var result = await device.SetSdCardCrcProtectionAsync(true);

        Assert.Equal(SdCardCrcProtectionResult.Applied, result);
        Assert.Contains(device.SentMessages, m => m.Data == "SYSTem:STORage:SD:PROTect 1");
        Assert.Contains(device.SentMessages, m => m.Data == "SYSTem:STORage:SD:SPACe?");
    }

    [Fact]
    public async Task GetSdCardCrcProtectionAsync_OnV3_9_0_ReadsTheDevice()
    {
        var device = NyquistOn("3.9.0");
        device.CannedTextResponse = new() { "1" };
        device.Connect();

        Assert.True(await device.GetSdCardCrcProtectionAsync());
        Assert.Equal("SYSTem:STORage:SD:PROTect?", device.SentMessages.Last().Data);
    }

    [Fact]
    public async Task CrcProtection_OnABoardWithNoSdSlot_ThrowsFeatureNotSupported()
    {
        var device = NyquistOn("3.9.0");
        device.Metadata.Capabilities = new DeviceCapabilities();
        device.Connect();

        await Assert.ThrowsAsync<FeatureNotSupportedException>(
            () => device.SetSdCardCrcProtectionAsync(true));
        await Assert.ThrowsAsync<FeatureNotSupportedException>(
            () => device.GetSdCardCrcProtectionAsync());
        Assert.Empty(device.SentMessages);
    }
}
