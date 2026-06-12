using Daqifi.Core.Communication.Transport;
using System.Net;
using System.Net.Sockets;

namespace Daqifi.Core.Tests.Communication.Transport;

public class TcpStreamTransportTests
{
    [Fact]
    public void TcpStreamTransport_Constructor_WithIPAddress_ShouldInitializeCorrectly()
    {
        // Arrange & Act
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 5000);
        
        // Assert
        Assert.False(transport.IsConnected);
        Assert.Contains("5000", transport.ConnectionInfo);
        Assert.Contains("Disconnected", transport.ConnectionInfo);
    }

    [Fact]
    public void TcpStreamTransport_Constructor_WithHostname_ShouldInitializeCorrectly()
    {
        // Arrange & Act
        using var transport = new TcpStreamTransport("localhost", 5000);
        
        // Assert
        Assert.False(transport.IsConnected);
        Assert.Equal("localhost", transport.Hostname);
        Assert.Contains("5000", transport.ConnectionInfo);
    }

    [Fact]
    public void TcpStreamTransport_Stream_WhenNotConnected_ShouldThrowException()
    {
        // Arrange
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 5000);
        
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => transport.Stream);
    }

    [Fact]
    public void TcpStreamTransport_Connect_WithClosedPort_ShouldThrowException()
    {
        // Arrange - Use localhost port 1 (reserved, never listening)
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 1);
        
        // Act & Assert
        Assert.Throws<SocketException>(() => transport.Connect());
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task TcpStreamTransport_ConnectAsync_WithClosedPort_ShouldThrowException()
    {
        // Arrange - Use localhost port 1 (reserved, never listening)
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 1);
        
        // Act & Assert
        await Assert.ThrowsAsync<SocketException>(() => transport.ConnectAsync());
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public void TcpStreamTransport_Disconnect_WhenNotConnected_ShouldNotThrow()
    {
        // Arrange
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 5000);
        
        // Act & Assert - Should not throw
        transport.Disconnect();
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task TcpStreamTransport_DisconnectAsync_WhenNotConnected_ShouldNotThrow()
    {
        // Arrange
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 5000);
        
        // Act & Assert - Should not throw
        await transport.DisconnectAsync();
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public void TcpStreamTransport_StatusChanged_ShouldFireOnConnectionFailure()
    {
        // Arrange - Use localhost port 1 (reserved, never listening)
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 1);
        TransportStatusEventArgs? capturedArgs = null;
        
        transport.StatusChanged += (sender, args) => capturedArgs = args;
        
        // Act
        try
        {
            transport.Connect();
        }
        catch (SocketException)
        {
            // Expected
        }
        
        // Assert
        Assert.NotNull(capturedArgs);
        Assert.False(capturedArgs.IsConnected);
        Assert.NotNull(capturedArgs.Error);
        Assert.IsType<SocketException>(capturedArgs.Error);
    }

    [Fact]
    public void TcpStreamTransport_Dispose_ShouldCleanupResources()
    {
        // Arrange
        var transport = new TcpStreamTransport(IPAddress.Loopback, 5000);
        
        // Act
        transport.Dispose();
        
        // Assert - Should throw ObjectDisposedException for operations after disposal
        Assert.Throws<ObjectDisposedException>(() => transport.Connect());
        Assert.Throws<ObjectDisposedException>(() => transport.Stream);
    }

    [Fact]
    public void TcpStreamTransport_ConnectionInfo_ShouldReflectCurrentState()
    {
        // Arrange
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 5000);
        
        // Act & Assert - Disconnected state
        var disconnectedInfo = transport.ConnectionInfo;
        Assert.Contains("Disconnected", disconnectedInfo);
        Assert.Contains("127.0.0.1:5000", disconnectedInfo);
    }

    [Fact]
    public void TcpStreamTransport_Constructor_WithLocalInterface_ExposesIt()
    {
        // Arrange & Act
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 5000, IPAddress.Loopback);

        // Assert
        Assert.Equal(IPAddress.Loopback, transport.LocalInterface);
    }

    [Fact]
    public void TcpStreamTransport_Constructor_WithoutLocalInterface_LocalInterfaceIsNull()
    {
        // Arrange & Act
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 5000);

        // Assert
        Assert.Null(transport.LocalInterface);
    }

    [Fact]
    public async Task TcpStreamTransport_ConnectAsync_WithUnassignedLocalInterface_ThrowsSocketException()
    {
        // Arrange - 192.0.2.1 is in TEST-NET-1 (RFC 5737) and is not assigned to any local
        // interface, so binding the outbound socket to it must fail with EADDRNOTAVAIL.
        // This proves the local-interface argument actually drives the socket bind.
        var bogusLocal = IPAddress.Parse("192.0.2.1");
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 1, bogusLocal);

        // Act & Assert
        await Assert.ThrowsAsync<SocketException>(() => transport.ConnectAsync());
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task TcpStreamTransport_ConnectAsync_WithLoopbackLocalInterface_ConnectsAndReportsBoundLocal()
    {
        // Arrange - real listener on loopback so the connection actually completes
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var transport = new TcpStreamTransport(IPAddress.Loopback, port, IPAddress.Loopback);

            // Act
            await transport.ConnectAsync();

            // Assert
            Assert.True(transport.IsConnected);
            Assert.Contains("127.0.0.1", transport.ConnectionInfo);
            await transport.DisconnectAsync();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TcpStreamTransport_ConnectAsync_WhenConnectTimesOut_ThrowsTimeoutException()
    {
        // Arrange - substitute a never-completing connect task (internal test seam), so the
        // configured timeout is deterministically what fails the attempt — no dependency on how
        // the host's network stack treats an unroutable address. The timeout used to surface as
        // TaskCanceledException (the WaitAsync cancellation token), which read like an app bug
        // to consumers (daqifi-desktop#517) — it must be a TimeoutException.
        using var transport = new TcpStreamTransport(IPAddress.Parse("192.0.2.1"), 9760);
        transport.ConnectTaskFactory = _ => Task.Delay(Timeout.Infinite);
        TransportStatusEventArgs? capturedArgs = null;
        transport.StatusChanged += (sender, args) => capturedArgs = args;
        var options = new ConnectionRetryOptions
        {
            Enabled = false,
            MaxAttempts = 1,
            ConnectionTimeout = TimeSpan.FromMilliseconds(250)
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => transport.ConnectAsync(options));
        Assert.Contains("192.0.2.1:9760", ex.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
        Assert.False(transport.IsConnected);

        // The status event must carry the translated exception too.
        Assert.NotNull(capturedArgs);
        Assert.False(capturedArgs.IsConnected);
        Assert.IsType<TimeoutException>(capturedArgs.Error);
    }

    // Integration test that requires a real server - marked as integration test
    [Fact(Skip = "Integration test - requires external server")]
    public async Task TcpStreamTransport_RealConnection_ShouldWorkEndToEnd()
    {
        // This test would connect to a real TCP server if available
        // Could be enabled for integration testing scenarios
        
        using var transport = new TcpStreamTransport("httpbin.org", 80);
        TransportStatusEventArgs? connectedArgs = null;
        TransportStatusEventArgs? disconnectedArgs = null;
        
        transport.StatusChanged += (sender, args) =>
        {
            if (args.IsConnected)
                connectedArgs = args;
            else
                disconnectedArgs = args;
        };
        
        await transport.ConnectAsync();
        
        Assert.True(transport.IsConnected);
        Assert.NotNull(transport.Stream);
        Assert.NotNull(connectedArgs);
        Assert.True(connectedArgs.IsConnected);
        
        await transport.DisconnectAsync();
        
        Assert.False(transport.IsConnected);
        Assert.NotNull(disconnectedArgs);
        Assert.False(disconnectedArgs.IsConnected);
    }
}