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

    [Fact]
    public void Clone_CopiesAllFieldValues()
    {
        // Arrange
        var capabilities = new DeviceCapabilities
        {
            SupportsStreaming = false,
            HasSdCard = true,
            HasWiFi = true,
            HasUsb = true,
            AnalogInputChannels = 8,
            AnalogOutputChannels = 2,
            DigitalChannels = 16,
            MaxSamplingRate = 5000
        };

        // Act
        var clone = capabilities.Clone();

        // Assert
        Assert.Equal(capabilities.SupportsStreaming, clone.SupportsStreaming);
        Assert.Equal(capabilities.HasSdCard, clone.HasSdCard);
        Assert.Equal(capabilities.HasWiFi, clone.HasWiFi);
        Assert.Equal(capabilities.HasUsb, clone.HasUsb);
        Assert.Equal(capabilities.AnalogInputChannels, clone.AnalogInputChannels);
        Assert.Equal(capabilities.AnalogOutputChannels, clone.AnalogOutputChannels);
        Assert.Equal(capabilities.DigitalChannels, clone.DigitalChannels);
        Assert.Equal(capabilities.MaxSamplingRate, clone.MaxSamplingRate);
    }

    [Fact]
    public void Clone_ReturnsDistinctInstance()
    {
        // Arrange
        var capabilities = new DeviceCapabilities();

        // Act
        var clone = capabilities.Clone();

        // Assert
        Assert.NotSame(capabilities, clone);
    }

    [Fact]
    public void Clone_MutatingCloneDoesNotAffectOriginal()
    {
        // Arrange
        var capabilities = new DeviceCapabilities { AnalogInputChannels = 8 };
        var clone = capabilities.Clone();

        // Act
        clone.AnalogInputChannels = 16;

        // Assert
        Assert.Equal(8, capabilities.AnalogInputChannels);
        Assert.Equal(16, clone.AnalogInputChannels);
    }
}
