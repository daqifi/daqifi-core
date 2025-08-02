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

    [Fact(Skip = "Network test - can be slow and unreliable in CI")]
    public void TcpStreamTransport_Connect_WithInvalidAddress_ShouldThrowException()
    {
        // Arrange
        using var transport = new TcpStreamTransport(IPAddress.Parse("192.0.2.1"), 12345); // TEST-NET-1
        
        // Act & Assert
        Assert.Throws<SocketException>(() => transport.Connect());
        Assert.False(transport.IsConnected);
    }

    [Fact(Skip = "Network test - can be slow and unreliable in CI")]
    public async Task TcpStreamTransport_ConnectAsync_WithInvalidAddress_ShouldThrowException()
    {
        // Arrange
        using var transport = new TcpStreamTransport(IPAddress.Parse("192.0.2.1"), 12345); // TEST-NET-1
        
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

    [Fact(Skip = "Network test - can be slow and unreliable in CI")]
    public void TcpStreamTransport_StatusChanged_ShouldFireOnConnectionFailure()
    {
        // Arrange
        using var transport = new TcpStreamTransport(IPAddress.Parse("192.0.2.1"), 12345);
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