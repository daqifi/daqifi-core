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
        Assert.False(capabilities.HasWiFi);
        Assert.False(capabilities.HasUsb);
        Assert.Equal(0, capabilities.AnalogInputChannels);
        Assert.Equal(0, capabilities.AnalogOutputChannels);
        Assert.Equal(0, capabilities.DigitalChannels);
        Assert.Equal(1000, capabilities.MaxSamplingRate);
    }

    [Theory]
    [InlineData(DeviceType.Nyquist1)]
    [InlineData(DeviceType.Nyquist2)]
    [InlineData(DeviceType.Nyquist3)]
    public void FromDeviceType_NyquistDevices_SetsCorrectCapabilities(DeviceType deviceType)
    {
        // Act
        var capabilities = DeviceCapabilities.FromDeviceType(deviceType);

        // Assert
        Assert.True(capabilities.SupportsStreaming);
        Assert.True(capabilities.HasSdCard);
        Assert.True(capabilities.HasWiFi);
        Assert.True(capabilities.HasUsb);
        Assert.Equal(1000, capabilities.MaxSamplingRate);
    }

    [Fact]
    public void FromDeviceType_UnknownDevice_ReturnsDefaultCapabilities()
    {
        // Act
        var capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Unknown);

        // Assert
        Assert.True(capabilities.SupportsStreaming);
        Assert.False(capabilities.HasSdCard);
        Assert.False(capabilities.HasWiFi);
        Assert.False(capabilities.HasUsb);
        Assert.Equal(0, capabilities.AnalogInputChannels);
        Assert.Equal(0, capabilities.AnalogOutputChannels);
        Assert.Equal(0, capabilities.DigitalChannels);
        Assert.Equal(1000, capabilities.MaxSamplingRate);
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        // Arrange
        var capabilities = new DeviceCapabilities();

        // Act
        capabilities.AnalogInputChannels = 8;
        capabilities.AnalogOutputChannels = 2;
        capabilities.DigitalChannels = 16;
        capabilities.MaxSamplingRate = 2000;

        // Assert
        Assert.Equal(8, capabilities.AnalogInputChannels);
        Assert.Equal(2, capabilities.AnalogOutputChannels);
        Assert.Equal(16, capabilities.DigitalChannels);
        Assert.Equal(2000, capabilities.MaxSamplingRate);
    }
}
