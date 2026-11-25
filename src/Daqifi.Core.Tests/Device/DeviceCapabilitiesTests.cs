using System.Reflection;
using Daqifi.Core.Device;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Unit tests for the <see cref="DeviceCapabilities"/> class.
/// </summary>
public class DeviceCapabilitiesTests
{
    [Fact]
    public void Constructor_InitializesWithDefaultValues()
    {
        // Act
        var capabilities = new DeviceCapabilities();

        // Assert
        Assert.True(capabilities.SupportsStreaming);
        Assert.False(capabilities.HasSdCard);
        Assert.False(capabilities.HasWifi);
        Assert.False(capabilities.HasWincWifiModule);
        Assert.False(capabilities.HasUsb);
        Assert.Equal(0, capabilities.AnalogInputChannelCount);
        Assert.Equal(0, capabilities.AnalogOutputChannelCount);
        Assert.Equal(0, capabilities.DigitalChannelCount);
        Assert.Equal(1000, capabilities.MaximumSampleRateHz);
    }

    [Theory]
    [InlineData(DeviceType.Nyquist1)]
    [InlineData(DeviceType.Nyquist2)]
    [InlineData(DeviceType.Nyquist3)]
    public void FromDeviceType_NyquistDevices_SetsTheHardwareFlags(DeviceType deviceType)
    {
        var capabilities = DeviceCapabilities.FromDeviceType(deviceType);

        Assert.True(capabilities.SupportsStreaming);
        Assert.True(capabilities.HasSdCard);
        Assert.True(capabilities.HasWifi);
        Assert.True(capabilities.HasWincWifiModule);
        Assert.True(capabilities.HasUsb);
    }

    [Fact]
    public void FromDeviceType_Nyquist1_MatchesTheRealBoard()
    {
        // The reading from a connected Nq1 (firmware 3.8.0): ai=16 ao=0 dio=16 maxRate=22000.
        // The table used to say 0, 0, 0 and 1000 (#904). That 1000 Hz looked plausible and capped
        // any caller that trusted it at twenty-two times below the board's real ceiling.
        // CapabilityDocumentMergeTests pins the same row against a capability document captured
        // from the board.
        var capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);

        Assert.Equal(16, capabilities.AnalogInputChannelCount);
        Assert.Equal(0, capabilities.AnalogOutputChannelCount);
        Assert.Equal(16, capabilities.DigitalChannelCount);
        Assert.Equal(22000, capabilities.MaximumSampleRateHz);
    }

    [Fact]
    public void FromDeviceType_Nyquist3_MatchesTheFirmwareBoardConfiguration()
    {
        // From the firmware's NQ3BoardConfig.c (8 public AD7609 inputs, 8 DAC7718 outputs, 16 DIO)
        // and its advertised 16 kHz ISR ceiling for NQ2/NQ3. Read from source, not from a board.
        var capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist3);

        Assert.Equal(8, capabilities.AnalogInputChannelCount);
        Assert.Equal(8, capabilities.AnalogOutputChannelCount);
        Assert.Equal(16, capabilities.DigitalChannelCount);
        Assert.Equal(16000, capabilities.MaximumSampleRateHz);
    }

    [Fact]
    public void FromDeviceType_Nyquist2_StatesNoChannelCountsAndTheFirmwareRateCeiling()
    {
        // The firmware has no NQ2 build configuration, so no channel count can be stated here;
        // the device reports them. The rate is the firmware's advertised NQ2/NQ3 ceiling.
        var capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist2);

        Assert.Equal(0, capabilities.AnalogInputChannelCount);
        Assert.Equal(0, capabilities.AnalogOutputChannelCount);
        Assert.Equal(0, capabilities.DigitalChannelCount);
        Assert.Equal(16000, capabilities.MaximumSampleRateHz);
    }

    [Fact]
    public void FromDeviceType_UnknownDevice_ReturnsDefaultCapabilities()
    {
        // Act
        var capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Unknown);

        // Assert
        Assert.True(capabilities.SupportsStreaming);
        Assert.False(capabilities.HasSdCard);
        Assert.False(capabilities.HasWifi);
        Assert.False(capabilities.HasWincWifiModule);
        Assert.False(capabilities.HasUsb);
        Assert.Equal(0, capabilities.AnalogInputChannelCount);
        Assert.Equal(0, capabilities.AnalogOutputChannelCount);
        Assert.Equal(0, capabilities.DigitalChannelCount);
        Assert.Equal(1000, capabilities.MaximumSampleRateHz);
    }
}
