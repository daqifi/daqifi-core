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
}
