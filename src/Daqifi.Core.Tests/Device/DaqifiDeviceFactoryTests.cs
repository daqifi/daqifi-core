using System.Net;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device;

public class DaqifiDeviceFactoryTests
{
    #region DeviceConnectionOptions Tests

    [Fact]
    public void DeviceConnectionOptions_DefaultValues_AreCorrect()
    {
        // Act
        var options = new DeviceConnectionOptions();

        // Assert
        Assert.Equal("DAQiFi Device", options.DeviceName);
        Assert.Null(options.ConnectionRetry);
        Assert.True(options.InitializeDevice);
    }

    [Fact]
    public void DeviceConnectionOptions_Default_ReturnsDefaultOptions()
    {
        // Act
        var options = DeviceConnectionOptions.Default;

        // Assert
        Assert.Equal("DAQiFi Device", options.DeviceName);
        Assert.Null(options.ConnectionRetry);
        Assert.True(options.InitializeDevice);
    }

    [Fact]
    public void DeviceConnectionOptions_Fast_UsesFastConnectionRetry()
    {
        // Act
        var options = DeviceConnectionOptions.Fast;

        // Assert
        Assert.NotNull(options.ConnectionRetry);
        Assert.Equal(ConnectionRetryOptions.Fast.MaxAttempts, options.ConnectionRetry.MaxAttempts);
        Assert.Equal(ConnectionRetryOptions.Fast.InitialDelay, options.ConnectionRetry.InitialDelay);
        Assert.Equal(ConnectionRetryOptions.Fast.ConnectionTimeout, options.ConnectionRetry.ConnectionTimeout);
        Assert.True(options.InitializeDevice);
    }

    [Fact]
    public void DeviceConnectionOptions_Resilient_UsesResilientConnectionRetry()
    {
        // Act
        var options = DeviceConnectionOptions.Resilient;

        // Assert
        Assert.NotNull(options.ConnectionRetry);
        Assert.Equal(ConnectionRetryOptions.Resilient.MaxAttempts, options.ConnectionRetry.MaxAttempts);
        Assert.Equal(ConnectionRetryOptions.Resilient.InitialDelay, options.ConnectionRetry.InitialDelay);
        Assert.Equal(ConnectionRetryOptions.Resilient.ConnectionTimeout, options.ConnectionRetry.ConnectionTimeout);
        Assert.True(options.InitializeDevice);
    }

    [Fact]
    public void DeviceConnectionOptions_CanSetCustomValues()
    {
        // Arrange & Act
        var options = new DeviceConnectionOptions
        {
            DeviceName = "Custom Device",
            ConnectionRetry = new ConnectionRetryOptions { MaxAttempts = 5 },
            InitializeDevice = false
        };

        // Assert
        Assert.Equal("Custom Device", options.DeviceName);
        Assert.NotNull(options.ConnectionRetry);
        Assert.Equal(5, options.ConnectionRetry.MaxAttempts);
        Assert.False(options.InitializeDevice);
    }

    #endregion

    #region ConnectTcpAsync Tests

    [Fact]
    public async Task ConnectTcpAsync_NullHost_ThrowsArgumentNullException()
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => DaqifiDeviceFactory.ConnectTcpAsync((string)null!, 9760));

        Assert.Equal("host", exception.ParamName);
    }

    [Fact]
    public async Task ConnectTcpAsync_EmptyHost_ThrowsArgumentNullException()
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => DaqifiDeviceFactory.ConnectTcpAsync("", 9760));

        Assert.Equal("host", exception.ParamName);
    }

    [Fact]
    public async Task ConnectTcpAsync_WhitespaceHost_ThrowsArgumentNullException()
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => DaqifiDeviceFactory.ConnectTcpAsync("   ", 9760));

        Assert.Equal("host", exception.ParamName);
    }

    [Fact]
    public async Task ConnectTcpAsync_NullIpAddress_ThrowsArgumentNullException()
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => DaqifiDeviceFactory.ConnectTcpAsync((IPAddress)null!, 9760));

        Assert.Equal("ipAddress", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(100000)]
    public async Task ConnectTcpAsync_InvalidPort_ThrowsArgumentOutOfRangeException(int invalidPort)
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", invalidPort));

        Assert.Equal("port", exception.ParamName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(80)]
    [InlineData(9760)]
    [InlineData(65535)]
    public async Task ConnectTcpAsync_ValidPort_DoesNotThrowArgumentOutOfRangeException(int validPort)
    {
        // Arrange - Use a cancelled token so we don't actually try to connect
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert - Should throw OperationCanceledException, not ArgumentOutOfRangeException
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", validPort, null, cts.Token));
    }

    [Fact]
    public async Task ConnectTcpAsync_WithCancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760, null, cts.Token));
    }

    [Fact]
    public async Task ConnectTcpAsync_ByIpAddress_WithCancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => DaqifiDeviceFactory.ConnectTcpAsync(IPAddress.Parse("192.168.1.100"), 9760, null, cts.Token));
    }

    [Fact]
    public async Task ConnectTcpAsync_InvalidHost_ThrowsException()
    {
        // Arrange - Use localhost port 1 (reserved, never listening)
        var options = new DeviceConnectionOptions
        {
            ConnectionRetry = new ConnectionRetryOptions
            {
                Enabled = false,
                ConnectionTimeout = TimeSpan.FromSeconds(1)
            },
            InitializeDevice = false
        };

        // Act & Assert - Should throw due to connection refused
        await Assert.ThrowsAnyAsync<Exception>(
            () => DaqifiDeviceFactory.ConnectTcpAsync(IPAddress.Loopback, 1, options));
    }

    #endregion

    #region ConnectTcp Sync Tests

    [Fact]
    public void ConnectTcp_NullHost_ThrowsArgumentNullException()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(
            () => DaqifiDeviceFactory.ConnectTcp((string)null!, 9760));

        Assert.Equal("host", exception.ParamName);
    }

    [Fact]
    public void ConnectTcp_NullIpAddress_ThrowsArgumentNullException()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(
            () => DaqifiDeviceFactory.ConnectTcp((IPAddress)null!, 9760));

        Assert.Equal("ipAddress", exception.ParamName);
    }

    #endregion

    #region ConnectFromDeviceInfoAsync Tests

    [Fact]
    public async Task ConnectFromDeviceInfoAsync_NullDeviceInfo_ThrowsArgumentNullException()
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => DaqifiDeviceFactory.ConnectFromDeviceInfoAsync(null!));

        Assert.Equal("deviceInfo", exception.ParamName);
    }

    [Fact]
    public async Task ConnectFromDeviceInfoAsync_SerialDevice_ThrowsNotSupportedException()
    {
        // Arrange
        var deviceInfo = new TestDeviceInfo
        {
            ConnectionType = ConnectionType.Serial,
            PortName = "COM3"
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => DaqifiDeviceFactory.ConnectFromDeviceInfoAsync(deviceInfo));

        Assert.Contains("Serial", exception.Message);
    }

    [Fact]
    public async Task ConnectFromDeviceInfoAsync_HidDevice_ThrowsNotSupportedException()
    {
        // Arrange
        var deviceInfo = new TestDeviceInfo
        {
            ConnectionType = ConnectionType.Hid,
            DevicePath = "/dev/hidraw0"
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => DaqifiDeviceFactory.ConnectFromDeviceInfoAsync(deviceInfo));

        Assert.Contains("HID", exception.Message);
    }

    [Fact]
    public async Task ConnectFromDeviceInfoAsync_UnknownConnectionType_ThrowsNotSupportedException()
    {
        // Arrange
        var deviceInfo = new TestDeviceInfo
        {
            ConnectionType = ConnectionType.Unknown
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => DaqifiDeviceFactory.ConnectFromDeviceInfoAsync(deviceInfo));

        Assert.Contains("Unknown", exception.Message);
    }

    [Fact]
    public async Task ConnectFromDeviceInfoAsync_WiFiDeviceMissingIpAddress_ThrowsArgumentException()
    {
        // Arrange
        var deviceInfo = new TestDeviceInfo
        {
            ConnectionType = ConnectionType.WiFi,
            Port = 9760,
            IPAddress = null
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => DaqifiDeviceFactory.ConnectFromDeviceInfoAsync(deviceInfo));

        Assert.Contains("IPAddress", exception.Message);
    }

    [Fact]
    public async Task ConnectFromDeviceInfoAsync_WiFiDeviceMissingPort_ThrowsArgumentException()
    {
        // Arrange
        var deviceInfo = new TestDeviceInfo
        {
            ConnectionType = ConnectionType.WiFi,
            IPAddress = IPAddress.Parse("192.168.1.100"),
            Port = null
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => DaqifiDeviceFactory.ConnectFromDeviceInfoAsync(deviceInfo));

        Assert.Contains("Port", exception.Message);
    }

    [Fact]
    public async Task ConnectFromDeviceInfoAsync_WithCancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var deviceInfo = new TestDeviceInfo
        {
            ConnectionType = ConnectionType.WiFi,
            IPAddress = IPAddress.Parse("192.168.1.100"),
            Port = 9760
        };

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => DaqifiDeviceFactory.ConnectFromDeviceInfoAsync(deviceInfo, null, cts.Token));
    }

    #endregion

    #region ConnectFromDeviceInfo Sync Tests

    [Fact]
    public void ConnectFromDeviceInfo_NullDeviceInfo_ThrowsArgumentNullException()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(
            () => DaqifiDeviceFactory.ConnectFromDeviceInfo(null!));

        Assert.Equal("deviceInfo", exception.ParamName);
    }

    [Fact]
    public void ConnectFromDeviceInfo_SerialDevice_ThrowsNotSupportedException()
    {
        // Arrange
        var deviceInfo = new TestDeviceInfo
        {
            ConnectionType = ConnectionType.Serial,
            PortName = "COM3"
        };

        // Act & Assert
        var exception = Assert.Throws<NotSupportedException>(
            () => DaqifiDeviceFactory.ConnectFromDeviceInfo(deviceInfo));

        Assert.Contains("Serial", exception.Message);
    }

    #endregion

    #region Test Helpers

    /// <summary>
    /// Test implementation of IDeviceInfo for unit testing.
    /// </summary>
    private class TestDeviceInfo : IDeviceInfo
    {
        public string Name { get; set; } = "Test Device";
        public string SerialNumber { get; set; } = "SN123456";
        public string FirmwareVersion { get; set; } = "1.0.0";
        public IPAddress? IPAddress { get; set; }
        public string? MacAddress { get; set; }
        public int? Port { get; set; }
        public IPAddress? LocalInterfaceAddress { get; set; }
        public Daqifi.Core.Device.Discovery.DeviceType Type { get; set; } = Daqifi.Core.Device.Discovery.DeviceType.Nyquist1;
        public bool IsPowerOn { get; set; } = true;
        public ConnectionType ConnectionType { get; set; } = ConnectionType.Unknown;
        public string? PortName { get; set; }
        public string? DevicePath { get; set; }
    }

    #endregion
}
