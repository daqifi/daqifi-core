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
        Assert.False(capabilities.HasWiFi);
        Assert.False(capabilities.HasWincWifiModule);
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
    public void FromDeviceType_NyquistDevices_SetsTheHardwareFlags(DeviceType deviceType)
    {
        var capabilities = DeviceCapabilities.FromDeviceType(deviceType);

        Assert.True(capabilities.SupportsStreaming);
        Assert.True(capabilities.HasSdCard);
        Assert.True(capabilities.HasWiFi);
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

        Assert.Equal(16, capabilities.AnalogInputChannels);
        Assert.Equal(0, capabilities.AnalogOutputChannels);
        Assert.Equal(16, capabilities.DigitalChannels);
        Assert.Equal(22000, capabilities.MaxSamplingRate);
    }

    [Fact]
    public void FromDeviceType_Nyquist3_MatchesTheFirmwareBoardConfiguration()
    {
        // From the firmware's NQ3BoardConfig.c (8 public AD7609 inputs, 8 DAC7718 outputs, 16 DIO)
        // and its advertised 16 kHz ISR ceiling for NQ2/NQ3. Read from source, not from a board.
        var capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist3);

        Assert.Equal(8, capabilities.AnalogInputChannels);
        Assert.Equal(8, capabilities.AnalogOutputChannels);
        Assert.Equal(16, capabilities.DigitalChannels);
        Assert.Equal(16000, capabilities.MaxSamplingRate);
    }

    [Fact]
    public void FromDeviceType_Nyquist2_StatesNoChannelCountsAndTheFirmwareRateCeiling()
    {
        // The firmware has no NQ2 build configuration, so no channel count can be stated here;
        // the device reports them. The rate is the firmware's advertised NQ2/NQ3 ceiling.
        var capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist2);

        Assert.Equal(0, capabilities.AnalogInputChannels);
        Assert.Equal(0, capabilities.AnalogOutputChannels);
        Assert.Equal(0, capabilities.DigitalChannels);
        Assert.Equal(16000, capabilities.MaxSamplingRate);
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
        Assert.False(capabilities.HasWincWifiModule);
        Assert.False(capabilities.HasUsb);
        Assert.Equal(0, capabilities.AnalogInputChannels);
        Assert.Equal(0, capabilities.AnalogOutputChannels);
        Assert.Equal(0, capabilities.DigitalChannels);
        Assert.Equal(1000, capabilities.MaxSamplingRate);
    }

    [Fact]
    public void Clone_RoundTripsEveryPublicProperty()
    {
        // Arrange: set every public property to a non-default value so a dropped copy is detectable.
        // Reflection-driven so a newly added property can't be silently omitted from Clone() (#331).
        var capabilities = new DeviceCapabilities
        {
            SupportsStreaming = false,
            HasSdCard = true,
            HasWiFi = true,
            HasWincWifiModule = true,
            HasUsb = true,
            AnalogInputChannels = 8,
            AnalogOutputChannels = 2,
            DigitalChannels = 16,
            MaxSamplingRate = 5000
        };

        // Act
        var clone = capabilities.Clone();

        // Assert: every readable public instance property must round-trip.
        var properties = typeof(DeviceCapabilities)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead);

        foreach (var property in properties)
        {
            var original = property.GetValue(capabilities);
            var copied = property.GetValue(clone);

            // Guard against the test itself leaving a property at its default (which would mask a drop).
            Assert.NotEqual(property.GetValue(new DeviceCapabilities()), original);
            Assert.Equal(original, copied);
        }
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
