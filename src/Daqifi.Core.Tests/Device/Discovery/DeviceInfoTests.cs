using System.Net;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

public class DeviceInfoTests
{
    [Fact]
    public void DeviceInfo_WiFiDevice_FormatsToStringCorrectly()
    {
        // Arrange
        var deviceInfo = new DeviceInfo
        {
            Name = "NYQUIST",
            SerialNumber = "12345",
            IPAddress = IPAddress.Parse("192.168.1.100"),
            Port = 9760,
            ConnectionType = ConnectionType.WiFi
        };

        // Act
        var result = deviceInfo.ToString();

        // Assert
        Assert.Contains("NYQUIST", result);
        Assert.Contains("192.168.1.100", result);
        Assert.Contains("9760", result);
        Assert.Contains("12345", result);
    }

    [Fact]
    public void DeviceInfo_SerialDevice_FormatsToStringCorrectly()
    {
        // Arrange
        var deviceInfo = new DeviceInfo
        {
            Name = "DAQiFi",
            SerialNumber = "67890",
            PortName = "COM3",
            ConnectionType = ConnectionType.Serial
        };

        // Act
        var result = deviceInfo.ToString();

        // Assert
        Assert.Contains("DAQiFi", result);
        Assert.Contains("COM3", result);
        Assert.Contains("67890", result);
    }

    [Fact]
    public void DeviceInfo_HidDevice_FormatsToStringCorrectly()
    {
        // Arrange
        var deviceInfo = new DeviceInfo
        {
            Name = "DAQiFi Bootloader",
            SerialNumber = "11111",
            ConnectionType = ConnectionType.Hid
        };

        // Act
        var result = deviceInfo.ToString();

        // Assert
        Assert.Contains("DAQiFi Bootloader", result);
        Assert.Contains("HID", result);
        Assert.Contains("11111", result);
    }

    [Fact]
    public void DeviceInfo_DefaultValues_AreSetCorrectly()
    {
        // Arrange & Act
        var deviceInfo = new DeviceInfo();

        // Assert
        Assert.Equal(string.Empty, deviceInfo.Name);
        Assert.Equal(string.Empty, deviceInfo.SerialNumber);
        Assert.Equal(string.Empty, deviceInfo.FirmwareVersion);
        Assert.Equal(DeviceType.Unknown, deviceInfo.Type);
        Assert.Equal(ConnectionType.Unknown, deviceInfo.ConnectionType);
        Assert.True(deviceInfo.IsPowerOn); // Default should be true
        Assert.Null(deviceInfo.LocalInterfaceAddress); // Default should be null
    }

    [Fact]
    public void DeviceInfo_LocalInterfaceAddress_CanBeSetAndRetrieved()
    {
        // Arrange
        var localAddress = IPAddress.Parse("192.168.1.50");
        var deviceInfo = new DeviceInfo
        {
            Name = "NYQUIST",
            SerialNumber = "12345",
            IPAddress = IPAddress.Parse("192.168.1.100"),
            Port = 9760,
            LocalInterfaceAddress = localAddress,
            ConnectionType = ConnectionType.WiFi
        };

        // Act & Assert
        Assert.Equal(localAddress, deviceInfo.LocalInterfaceAddress);
    }

    [Fact]
    public void DeviceInfo_WiFiDevice_WithLocalInterfaceAddress_StoresCorrectly()
    {
        // Arrange - Simulates a device discovered via WiFi on a multi-NIC system
        var localInterface = IPAddress.Parse("10.0.0.50");
        var deviceIp = IPAddress.Parse("10.0.0.100");

        // Act
        var deviceInfo = new DeviceInfo
        {
            Name = "DAQiFi Device",
            SerialNumber = "SN123456",
            FirmwareVersion = "1.0.0",
            IPAddress = deviceIp,
            MacAddress = "AA-BB-CC-DD-EE-FF",
            Port = 9760,
            LocalInterfaceAddress = localInterface,
            Type = DeviceType.Nyquist1,
            IsPowerOn = true,
            ConnectionType = ConnectionType.WiFi
        };

        // Assert
        Assert.Equal(deviceIp, deviceInfo.IPAddress);
        Assert.Equal(localInterface, deviceInfo.LocalInterfaceAddress);
        Assert.Equal(ConnectionType.WiFi, deviceInfo.ConnectionType);
    }

    [Fact]
    public void DeviceInfo_SerialDevice_LocalInterfaceAddress_IsNull()
    {
        // Arrange - Serial devices don't have LocalInterfaceAddress
        var deviceInfo = new DeviceInfo
        {
            Name = "DAQiFi Serial",
            SerialNumber = "67890",
            PortName = "COM3",
            ConnectionType = ConnectionType.Serial
        };

        // Act & Assert
        Assert.Null(deviceInfo.LocalInterfaceAddress);
    }
}
