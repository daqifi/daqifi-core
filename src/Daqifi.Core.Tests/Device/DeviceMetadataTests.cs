using Daqifi.Core.Device;
using Google.Protobuf;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Unit tests for the <see cref="DeviceMetadata"/> class.
/// </summary>
public class DeviceMetadataTests
{
    [Fact]
    public void Constructor_InitializesWithDefaultValues()
    {
        // Act
        var metadata = new DeviceMetadata();

        // Assert
        Assert.Equal(string.Empty, metadata.PartNumber);
        Assert.Equal(string.Empty, metadata.SerialNumber);
        Assert.Equal(string.Empty, metadata.FirmwareVersion);
        Assert.Equal(string.Empty, metadata.HardwareRevision);
        Assert.Equal(DeviceType.Unknown, metadata.DeviceType);
        Assert.NotNull(metadata.Capabilities);
        Assert.Equal(string.Empty, metadata.IpAddress);
        Assert.Equal(string.Empty, metadata.MacAddress);
        Assert.Equal(string.Empty, metadata.Ssid);
        Assert.Equal(string.Empty, metadata.HostName);
        Assert.Equal(string.Empty, metadata.FriendlyName);
        Assert.Equal(0, metadata.DevicePort);
    }

    [Fact]
    public void UpdateFromProtobuf_UpdatesPartNumberAndDeviceType()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            DevicePn = "Nq3"
        };

        // Act
        metadata.UpdateFromProtobuf(message);

        // Assert
        Assert.Equal("Nq3", metadata.PartNumber);
        Assert.Equal(DeviceType.Nyquist3, metadata.DeviceType);
        Assert.True(metadata.Capabilities.HasSdCard);
        Assert.True(metadata.Capabilities.HasWiFi);
    }

    [Fact]
    public void UpdateFromProtobuf_UpdatesSerialNumber()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            DeviceSn = 12345
        };

        // Act
        metadata.UpdateFromProtobuf(message);

        // Assert
        Assert.Equal("12345", metadata.SerialNumber);
    }

    [Fact]
    public void UpdateFromProtobuf_UpdatesFirmwareAndHardwareVersions()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            DeviceFwRev = "1.2.3",
            DeviceHwRev = "2.0"
        };

        // Act
        metadata.UpdateFromProtobuf(message);

        // Assert
        Assert.Equal("1.2.3", metadata.FirmwareVersion);
        Assert.Equal("2.0", metadata.HardwareRevision);
    }

    [Fact]
    public void UpdateFromProtobuf_UpdatesNetworkInformation()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            Ssid = "TestNetwork",
            HostName = "daqifi-001",
            DevicePort = 9760,
            WifiSecurityMode = 3,
            WifiInfMode = 1
        };

        // Act
        metadata.UpdateFromProtobuf(message);

        // Assert
        Assert.Equal("TestNetwork", metadata.Ssid);
        Assert.Equal("daqifi-001", metadata.HostName);
        Assert.Equal(9760, metadata.DevicePort);
        Assert.Equal(3u, metadata.WifiSecurityMode);
        Assert.Equal(1u, metadata.WifiInfrastructureMode);
    }

    [Fact]
    public void UpdateFromProtobuf_UpdatesIpAddress()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            IpAddr = ByteString.CopyFrom(new byte[] { 192, 168, 1, 100 })
        };

        // Act
        metadata.UpdateFromProtobuf(message);

        // Assert
        Assert.Equal("192.168.1.100", metadata.IpAddress);
    }

    [Fact]
    public void UpdateFromProtobuf_UpdatesMacAddress()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            MacAddr = ByteString.CopyFrom(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF })
        };

        // Act
        metadata.UpdateFromProtobuf(message);

        // Assert
        Assert.Equal("AA-BB-CC-DD-EE-FF", metadata.MacAddress);
    }

    [Fact]
    public void UpdateFromProtobuf_UpdatesChannelCounts()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 8,
            AnalogOutPortNum = 2,
            DigitalPortNum = 16
        };

        // Act
        metadata.UpdateFromProtobuf(message);

        // Assert
        Assert.Equal(8, metadata.Capabilities.AnalogInputChannels);
        Assert.Equal(2, metadata.Capabilities.AnalogOutputChannels);
        Assert.Equal(16, metadata.Capabilities.DigitalChannels);
    }

    [Fact]
    public void UpdateFromProtobuf_UpdatesHealthTelemetry()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            BattStatus = 87,
            TempStatus = 42,
            PwrStatus = 2,
            DeviceStatus = 5
        };

        // Act
        metadata.UpdateFromProtobuf(message);

        // Assert
        Assert.Equal(87, metadata.Health.BatteryPercent);
        Assert.Equal(42, metadata.Health.BoardTemperatureCelsius);
        Assert.Equal(2u, metadata.Health.PowerStatus);
        Assert.Equal(5u, metadata.Health.DeviceStatus);
    }

    [Fact]
    public void UpdateFromProtobuf_FromSerializedStatusPayload_DecodesHealthTelemetry()
    {
        // Arrange: build a status message, serialize it to the wire bytes a device would send,
        // then decode it back through the protobuf parser — exercising the real frame decode path
        // (issue #335 asks for a captured/serialized payload, not just an in-memory message).
        var original = new DaqifiOutMessage
        {
            DevicePn = "Nq1",
            BattStatus = 87,
            TempStatus = -5,
            PwrStatus = 2,
            DeviceStatus = 5
        };
        byte[] payload = original.ToByteArray();
        var decoded = DaqifiOutMessage.Parser.ParseFrom(payload);

        var metadata = new DeviceMetadata();

        // Act
        metadata.UpdateFromProtobuf(decoded);

        // Assert
        Assert.Equal(87, metadata.Health.BatteryPercent);
        Assert.Equal(-5, metadata.Health.BoardTemperatureCelsius);
        Assert.Equal(2u, metadata.Health.PowerStatus);
        Assert.Equal(5u, metadata.Health.DeviceStatus);
    }

    [Theory]
    [InlineData(101u)]      // above the documented 0-100 range
    [InlineData(500u)]      // clearly nonsensical
    [InlineData(uint.MaxValue)] // would wrap to -1 if cast straight to int
    public void UpdateFromProtobuf_OutOfRangeBattery_IsIgnored(uint battStatus)
    {
        // Arrange: a prior valid reading, then a bad one.
        var metadata = new DeviceMetadata();
        metadata.UpdateFromProtobuf(new DaqifiOutMessage { BattStatus = 60 });

        // Act
        metadata.UpdateFromProtobuf(new DaqifiOutMessage { BattStatus = battStatus });

        // Assert: out-of-range battery never surfaces (and never wraps negative); last-known kept.
        Assert.Equal(60, metadata.Health.BatteryPercent);
    }

    [Fact]
    public void Health_SetToNull_IsCoercedToInstance_AndStatusUpdateDoesNotThrow()
    {
        // Health has a public setter; a consumer assigning null must not break the status path.
        var metadata = new DeviceMetadata { Health = null! };

        Assert.NotNull(metadata.Health);

        var exception = Record.Exception(() =>
            metadata.UpdateFromProtobuf(new DaqifiOutMessage { BattStatus = 42 }));

        Assert.Null(exception);
        Assert.Equal(42, metadata.Health.BatteryPercent);
    }

    [Fact]
    public void UpdateFromProtobuf_NegativeBoardTemperature_IsPreserved()
    {
        // TempStatus is a signed field; sub-zero board temperatures must round-trip.
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage { TempStatus = -10 };

        metadata.UpdateFromProtobuf(message);

        Assert.Equal(-10, metadata.Health.BoardTemperatureCelsius);
    }

    [Fact]
    public void UpdateFromProtobuf_ZeroHealthFields_LeaveLastKnownValues()
    {
        // A partial status message (all health fields 0) must not clobber a prior reading.
        var metadata = new DeviceMetadata();
        metadata.UpdateFromProtobuf(new DaqifiOutMessage { BattStatus = 90, TempStatus = 30 });

        metadata.UpdateFromProtobuf(new DaqifiOutMessage { AnalogInPortNum = 8 });

        Assert.Equal(90, metadata.Health.BatteryPercent);
        Assert.Equal(30, metadata.Health.BoardTemperatureCelsius);
    }

    [Fact]
    public void UpdateFromProtobuf_IgnoresEmptyOrZeroValues()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            DevicePn = "",
            DeviceSn = 0,
            DeviceFwRev = "",
            Ssid = "",
            HostName = "",
            DevicePort = 0
        };

        // Act
        metadata.UpdateFromProtobuf(message);

        // Assert
        Assert.Equal(string.Empty, metadata.PartNumber);
        Assert.Equal(string.Empty, metadata.SerialNumber);
        Assert.Equal(string.Empty, metadata.FirmwareVersion);
        Assert.Equal(string.Empty, metadata.Ssid);
        Assert.Equal(string.Empty, metadata.HostName);
        Assert.Equal(0, metadata.DevicePort);
    }

    [Fact]
    public void UpdateFromProtobuf_UpdatesFriendlyName()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            FriendlyDeviceName = "My Device"
        };

        // Act
        metadata.UpdateFromProtobuf(message);

        // Assert
        Assert.Equal("My Device", metadata.FriendlyName);
    }

    [Fact]
    public void UpdateFromProtobuf_WithEmptyFriendlyName_LeavesFriendlyNameUnchanged()
    {
        // Arrange
        var metadata = new DeviceMetadata { FriendlyName = "Previous Name" };
        var message = new DaqifiOutMessage
        {
            FriendlyDeviceName = ""
        };

        // Act
        metadata.UpdateFromProtobuf(message);

        // Assert
        Assert.Equal("Previous Name", metadata.FriendlyName);
    }

    [Fact]
    public void CopyFrom_CopiesAllFieldValues()
    {
        // Arrange
        var source = new DeviceMetadata
        {
            PartNumber = "Nq3",
            SerialNumber = "12345",
            FirmwareVersion = "1.2.3",
            HardwareRevision = "2.0",
            DeviceType = DeviceType.Nyquist3,
            Capabilities = new DeviceCapabilities
            {
                SupportsStreaming = true,
                HasSdCard = true,
                HasWiFi = true,
                HasUsb = true,
                AnalogInputChannels = 8,
                AnalogOutputChannels = 2,
                DigitalChannels = 16,
                MaxSamplingRate = 5000
            },
            Health = new DeviceHealth
            {
                BatteryPercent = 75,
                BoardTemperatureCelsius = 33,
                PowerStatus = 1,
                DeviceStatus = 4
            },
            IpAddress = "192.168.1.100",
            MacAddress = "AA-BB-CC-DD-EE-FF",
            Ssid = "TestNetwork",
            HostName = "daqifi-001",
            FriendlyName = "My Device",
            DevicePort = 9760,
            WifiSecurityMode = 3,
            WifiInfrastructureMode = 1
        };
        var target = new DeviceMetadata();

        // Act
        target.CopyFrom(source);

        // Assert
        Assert.Equal(source.PartNumber, target.PartNumber);
        Assert.Equal(source.SerialNumber, target.SerialNumber);
        Assert.Equal(source.FirmwareVersion, target.FirmwareVersion);
        Assert.Equal(source.HardwareRevision, target.HardwareRevision);
        Assert.Equal(source.DeviceType, target.DeviceType);
        Assert.Equal(source.IpAddress, target.IpAddress);
        Assert.Equal(source.MacAddress, target.MacAddress);
        Assert.Equal(source.Ssid, target.Ssid);
        Assert.Equal(source.HostName, target.HostName);
        Assert.Equal(source.FriendlyName, target.FriendlyName);
        Assert.Equal(source.DevicePort, target.DevicePort);
        Assert.Equal(source.WifiSecurityMode, target.WifiSecurityMode);
        Assert.Equal(source.WifiInfrastructureMode, target.WifiInfrastructureMode);
        Assert.Equal(source.Capabilities.AnalogInputChannels, target.Capabilities.AnalogInputChannels);
        Assert.Equal(source.Capabilities.AnalogOutputChannels, target.Capabilities.AnalogOutputChannels);
        Assert.Equal(source.Capabilities.DigitalChannels, target.Capabilities.DigitalChannels);
        Assert.Equal(source.Capabilities.MaxSamplingRate, target.Capabilities.MaxSamplingRate);
        Assert.Equal(source.Capabilities.HasSdCard, target.Capabilities.HasSdCard);
        Assert.Equal(source.Capabilities.HasWiFi, target.Capabilities.HasWiFi);
        Assert.Equal(source.Capabilities.HasUsb, target.Capabilities.HasUsb);
        Assert.Equal(source.Capabilities.SupportsStreaming, target.Capabilities.SupportsStreaming);
        Assert.Equal(source.Health.BatteryPercent, target.Health.BatteryPercent);
        Assert.Equal(source.Health.BoardTemperatureCelsius, target.Health.BoardTemperatureCelsius);
        Assert.Equal(source.Health.PowerStatus, target.Health.PowerStatus);
        Assert.Equal(source.Health.DeviceStatus, target.Health.DeviceStatus);
    }

    [Fact]
    public void CopyFrom_HealthIsDeepCopiedNotShared()
    {
        // Arrange
        var source = new DeviceMetadata { Health = new DeviceHealth { BatteryPercent = 50 } };
        var target = new DeviceMetadata();

        // Act
        target.CopyFrom(source);
        source.Health.BatteryPercent = 10;

        // Assert
        Assert.NotSame(source.Health, target.Health);
        Assert.Equal(50, target.Health.BatteryPercent);
    }

    [Fact]
    public void CopyFrom_CapabilitiesAreDeepCopiedNotShared()
    {
        // Arrange
        var source = new DeviceMetadata();
        var target = new DeviceMetadata();

        // Act
        target.CopyFrom(source);

        // Assert
        Assert.NotSame(source.Capabilities, target.Capabilities);
    }

    [Fact]
    public void CopyFrom_MutatingSourceAfterCopyDoesNotAffectTarget()
    {
        // Arrange
        var source = new DeviceMetadata { PartNumber = "Nq3" };
        var target = new DeviceMetadata();
        target.CopyFrom(source);

        // Act
        source.PartNumber = "Nq1";
        source.Capabilities.AnalogInputChannels = 8;

        // Assert
        Assert.Equal("Nq3", target.PartNumber);
        Assert.Equal(0, target.Capabilities.AnalogInputChannels);
    }

    [Fact]
    public void CopyFrom_NullSource_ThrowsArgumentNullException()
    {
        // Arrange
        var target = new DeviceMetadata();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => target.CopyFrom(null!));
    }

    [Fact]
    public void CopyFrom_SourceWithNullCapabilities_DefaultsToNewCapabilities()
    {
        // Arrange
        var source = new DeviceMetadata { Capabilities = null! };
        var target = new DeviceMetadata();

        // Act
        target.CopyFrom(source);

        // Assert
        Assert.NotNull(target.Capabilities);
    }
}
