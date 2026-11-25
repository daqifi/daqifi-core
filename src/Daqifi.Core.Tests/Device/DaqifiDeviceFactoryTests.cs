using System.Net;
using System.Net.Sockets;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device;

public class DaqifiDeviceFactoryTests
{
    #region DeviceConnectionOptions Tests

    [Fact]
    public void DeviceConnectionOptions_Default_ReturnsDefaultOptions()
    {
        // Act
        var options = DeviceConnectionOptions.Default;

        // Assert
        Assert.Equal("DAQiFi Device", options.DeviceName);
        Assert.Null(options.ConnectionRetry);
        Assert.True(options.InitializeDevice);
        // Connecting takes control of the device (and stops any running stream) unless
        // explicitly opted out of — the historical behavior (#385).
        Assert.False(options.PreserveActiveStream);
    }

    [Fact]
    public void DeviceConnectionOptions_Observing_PreservesActiveStream()
    {
        // Act
        var options = DeviceConnectionOptions.Observing;

        // Assert — the opt-in preset still initializes, it just does so non-disruptively
        Assert.True(options.PreserveActiveStream);
        Assert.True(options.InitializeDevice);
    }

    [Fact]
    public void DeviceConnectionOptions_FastAndResilient_DoNotPreserveActiveStream()
    {
        // Assert — the existing presets keep the historical take-control behavior
        Assert.False(DeviceConnectionOptions.Fast.PreserveActiveStream);
        Assert.False(DeviceConnectionOptions.Resilient.PreserveActiveStream);
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
    public async Task ConnectTcpAsync_ToClosedLoopbackPort_ThrowsSocketException()
    {
        // Arrange - localhost port 1 (reserved, never listening), with the same retry options the
        // transport twin TcpStreamTransport_ConnectAsync_WithClosedPort gets by default (NoRetry,
        // 5s per-attempt timeout). The timeout must stay well above the refusal latency: Linux and
        // macOS refuse a closed loopback port at once, but Windows retries the SYN before
        // reporting the refusal, which takes longer than 1s. A shorter timeout lets the connect
        // timeout win on Windows and surfaces TimeoutException instead of the refusal.
        var options = new DeviceConnectionOptions
        {
            ConnectionRetry = ConnectionRetryOptions.NoRetry,
            InitializeDevice = false
        };

        // Act & Assert - the factory lets the transport's refusal through unwrapped.
        await Assert.ThrowsAsync<SocketException>(
            () => DaqifiDeviceFactory.ConnectTcpAsync(IPAddress.Loopback, 1, options));
    }

    #endregion

    #region ConnectSerialAsync Tests

    [Fact]
    public async Task ConnectSerialAsync_NullPortName_ThrowsArgumentNullException()
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => DaqifiDeviceFactory.ConnectSerialAsync(null!));

        Assert.Equal("portName", exception.ParamName);
    }

    [Fact]
    public async Task ConnectSerialAsync_EmptyPortName_ThrowsArgumentException()
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => DaqifiDeviceFactory.ConnectSerialAsync(""));

        Assert.Equal("portName", exception.ParamName);
    }

    [Fact]
    public async Task ConnectSerialAsync_WhitespacePortName_ThrowsArgumentException()
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => DaqifiDeviceFactory.ConnectSerialAsync("   "));

        Assert.Equal("portName", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task ConnectSerialAsync_InvalidBaudRate_ThrowsArgumentOutOfRangeException(int invalidBaudRate)
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => DaqifiDeviceFactory.ConnectSerialAsync("COM3", invalidBaudRate));

        Assert.Equal("baudRate", exception.ParamName);
    }

    [Theory]
    [InlineData(9600)]
    [InlineData(115200)]
    [InlineData(1000000)]
    public async Task ConnectSerialAsync_ValidBaudRate_DoesNotThrowArgumentOutOfRangeException(int validBaudRate)
    {
        // Arrange - Use a cancelled token so we don't actually try to connect
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert - Should throw OperationCanceledException, not ArgumentOutOfRangeException
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => DaqifiDeviceFactory.ConnectSerialAsync("COM3", validBaudRate, null, cts.Token));
    }

    [Fact]
    public async Task ConnectSerialAsync_WithCancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => DaqifiDeviceFactory.ConnectSerialAsync("COM3", null, cts.Token));
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
    public async Task ConnectFromDeviceInfoAsync_SerialDeviceMissingPortName_ThrowsArgumentException()
    {
        // Arrange
        var deviceInfo = new TestDeviceInfo
        {
            ConnectionType = ConnectionType.Serial,
            PortName = null
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => DaqifiDeviceFactory.ConnectFromDeviceInfoAsync(deviceInfo));

        Assert.Contains("PortName", exception.Message);
    }

    [Fact]
    public async Task ConnectFromDeviceInfoAsync_SerialDevice_WithCancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var deviceInfo = new TestDeviceInfo
        {
            ConnectionType = ConnectionType.Serial,
            PortName = "COM3"
        };

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => DaqifiDeviceFactory.ConnectFromDeviceInfoAsync(deviceInfo, null, cts.Token));
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
            ConnectionType = ConnectionType.Wifi,
            Port = 9760,
            IpAddress = null
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => DaqifiDeviceFactory.ConnectFromDeviceInfoAsync(deviceInfo));

        Assert.Contains("IpAddress", exception.Message);
    }

    [Fact]
    public async Task ConnectFromDeviceInfoAsync_WiFiDeviceMissingPort_ThrowsArgumentException()
    {
        // Arrange
        var deviceInfo = new TestDeviceInfo
        {
            ConnectionType = ConnectionType.Wifi,
            IpAddress = IPAddress.Parse("192.168.1.100"),
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
            ConnectionType = ConnectionType.Wifi,
            IpAddress = IPAddress.Parse("192.168.1.100"),
            Port = 9760
        };

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => DaqifiDeviceFactory.ConnectFromDeviceInfoAsync(deviceInfo, null, cts.Token));
    }

    [Fact]
    public async Task ConnectFromDeviceInfoAsync_WiFiWithLocalInterface_BindsOutboundSocketToIt()
    {
        // Arrange - 192.0.2.1 is in TEST-NET-1 (RFC 5737) and is not assigned to any local
        // interface. If the factory threads LocalInterfaceAddress into the TCP transport,
        // the bind will fail with EADDRNOTAVAIL (SocketException) before connect is attempted.
        // If the factory ignores LocalInterfaceAddress, the connection to 127.0.0.1 would
        // proceed and fail with a different error (or succeed against the listener), so a
        // SocketException at bind time is the signal that the wiring is correct.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var deviceInfo = new TestDeviceInfo
            {
                ConnectionType = ConnectionType.Wifi,
                IpAddress = IPAddress.Loopback,
                Port = port,
                LocalInterfaceAddress = IPAddress.Parse("192.0.2.1")
            };
            var options = new DeviceConnectionOptions
            {
                ConnectionRetry = new ConnectionRetryOptions
                {
                    Enabled = false,
                    ConnectionTimeout = TimeSpan.FromSeconds(1)
                },
                InitializeDevice = false
            };

            // Act & Assert
            await Assert.ThrowsAsync<SocketException>(
                () => DaqifiDeviceFactory.ConnectFromDeviceInfoAsync(deviceInfo, options));
        }
        finally
        {
            listener.Stop();
        }
    }

    #endregion

    #region Network capability (issue #616)

    private static readonly DeviceConnectionOptions BareConnect = new()
    {
        ConnectionRetry = new ConnectionRetryOptions
        {
            Enabled = false,
            ConnectionTimeout = TimeSpan.FromSeconds(5)
        },
        InitializeDevice = false
    };

    /// <summary>
    /// Runs <paramref name="connect"/> against a loopback listener that accepts the handshake and
    /// nothing else — all a connect with <c>InitializeDevice = false</c> needs.
    /// </summary>
    /// <remarks>
    /// Listens on both loopback addresses: "localhost" resolves to ::1 first, and Windows retries a
    /// refused connect for ~2 s before moving on to 127.0.0.1.
    /// </remarks>
    private static async Task WithLoopbackListenerAsync(Func<int, Task> connect)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var listenerV6 = new TcpListener(IPAddress.IPv6Loopback, port);
        try
        {
            try
            {
                listenerV6.Start();
            }
            catch (SocketException)
            {
                // No IPv6 here, or the port is taken on ::1: the IPv4 listener is enough.
            }

            await connect(port);
        }
        finally
        {
            listenerV6.Stop();
            listener.Stop();
        }
    }

    [Fact]
    public async Task ConnectTcpAsync_ByIpAddress_ReturnsANetworkDeviceAtTheConnectAddress()
    {
        await WithLoopbackListenerAsync(async port =>
        {
            await using var device = await DaqifiDeviceFactory.ConnectTcpAsync(IPAddress.Loopback, port, BareConnect);

            var networked = Assert.IsAssignableFrom<INetworkAddressedDevice>(device);
            Assert.Equal(IPAddress.Loopback, networked.IpAddress);
        });
    }

    [Fact]
    public async Task ConnectTcpAsync_ByHostName_ReportsTheResolvedAddress()
    {
        await WithLoopbackListenerAsync(async port =>
        {
            await using var device = await DaqifiDeviceFactory.ConnectTcpAsync("localhost", port, BareConnect);

            var networked = Assert.IsAssignableFrom<INetworkAddressedDevice>(device);
            Assert.NotNull(networked.IpAddress);
            Assert.True(IPAddress.IsLoopback(networked.IpAddress!), $"Expected a loopback address, got {networked.IpAddress}");
        });
    }

    [Fact]
    public async Task ConnectFromDeviceInfoAsync_WiFi_ReturnsANetworkDeviceAtTheDiscoveredAddress()
    {
        await WithLoopbackListenerAsync(async port =>
        {
            var deviceInfo = new TestDeviceInfo
            {
                ConnectionType = ConnectionType.Wifi,
                IpAddress = IPAddress.Loopback,
                Port = port
            };

            await using var device = await DaqifiDeviceFactory.ConnectFromDeviceInfoAsync(deviceInfo, BareConnect);

            var networked = Assert.IsAssignableFrom<INetworkAddressedDevice>(device);
            Assert.Equal(IPAddress.Loopback, networked.IpAddress);
        });
    }

    [Fact]
    public async Task NetworkDevice_FollowsAReconnectThatLandsOnADifferentPeer()
    {
        // A host-name transport re-resolves on every connect. Simulated with the transport's
        // connect seam: the same name lands on IPv4 loopback first, then on IPv6 loopback, so
        // the one device instance must report the second peer after the reconnect, not the
        // address it was built with.
        if (!Socket.OSSupportsIPv6)
        {
            return;
        }

        var first = new TcpListener(IPAddress.Loopback, 0);
        var second = new TcpListener(IPAddress.IPv6Loopback, 0);
        first.Start();
        second.Start();
        try
        {
            var target = (IPEndPoint)first.LocalEndpoint;
            var transport = new TcpStreamTransport("daqifi-peer.test", 9760)
            {
                ConnectTaskFactory = (client, token) =>
                    client.ConnectAsync(target.Address, target.Port, token).AsTask()
            };
            await transport.ConnectAsync(BareConnect.ConnectionRetry);
            using var device = DaqifiDeviceFactory.CreateDevice(transport, BareConnect);
            var networked = Assert.IsAssignableFrom<INetworkAddressedDevice>(device);

            Assert.Equal(IPAddress.Loopback, networked.IpAddress);

            await transport.DisconnectAsync();
            target = (IPEndPoint)second.LocalEndpoint;
            await transport.ConnectAsync(BareConnect.ConnectionRetry);

            Assert.Equal(IPAddress.IPv6Loopback, networked.IpAddress);
        }
        finally
        {
            first.Stop();
            second.Stop();
        }
    }

    [Fact]
    public void CreateDevice_SerialTransport_IsNotANetworkDevice()
    {
        // The serial factory path (ConnectSerialAsync) builds its device here once the port is
        // open; a test cannot open a real port, so it checks the construction step directly.
        using var device = DaqifiDeviceFactory.CreateDevice(
            new SerialStreamTransport("COM_TEST_616"), DeviceConnectionOptions.Default);

        Assert.False(device is INetworkAddressedDevice);
        Assert.True(device.IsUsbConnection);
    }

    [Fact]
    public void CreateDevice_TcpTransport_IsANetworkDeviceAtTheTransportAddress()
    {
        var address = IPAddress.Parse("192.0.2.10");
        using var device = DaqifiDeviceFactory.CreateDevice(
            new TcpStreamTransport(address, 9760), DeviceConnectionOptions.Default);

        var networked = Assert.IsAssignableFrom<INetworkAddressedDevice>(device);
        Assert.Equal(address, networked.IpAddress);
        Assert.False(device.IsUsbConnection);
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
        public IPAddress? IpAddress { get; set; }
        public string? MacAddress { get; set; }
        public int? Port { get; set; }
        public IPAddress? LocalInterfaceAddress { get; set; }
        public Daqifi.Core.Device.DeviceType Type { get; set; } = Daqifi.Core.Device.DeviceType.Nyquist1;
        public bool IsPowerOn { get; set; } = true;
        public ConnectionType ConnectionType { get; set; } = ConnectionType.Unknown;
        public string? PortName { get; set; }
        public string? DevicePath { get; set; }
        public string? LocationKey { get; set; }
    }

    #endregion
}
