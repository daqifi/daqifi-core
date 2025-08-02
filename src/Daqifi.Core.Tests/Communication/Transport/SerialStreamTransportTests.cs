using Daqifi.Core.Communication.Transport;
using System.IO.Ports;

namespace Daqifi.Core.Tests.Communication.Transport;

public class SerialStreamTransportTests
{
    [Fact]
    public void SerialStreamTransport_Constructor_ShouldInitializeCorrectly()
    {
        // Arrange & Act
        using var transport = new SerialStreamTransport("COM1");
        
        // Assert
        Assert.False(transport.IsConnected);
        Assert.Contains("COM1", transport.ConnectionInfo);
        Assert.Contains("Disconnected", transport.ConnectionInfo);
    }

    [Fact]
    public void SerialStreamTransport_Constructor_WithCustomSettings_ShouldInitializeCorrectly()
    {
        // Arrange & Act
        using var transport = new SerialStreamTransport("COM2", 9600, Parity.Even, 7, StopBits.Two);
        
        // Assert
        Assert.False(transport.IsConnected);
        Assert.Contains("COM2", transport.ConnectionInfo);
        Assert.Contains("Disconnected", transport.ConnectionInfo);
    }

    [Fact]
    public void SerialStreamTransport_Stream_WhenNotConnected_ShouldThrowException()
    {
        // Arrange
        using var transport = new SerialStreamTransport("COM1");
        
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => transport.Stream);
    }

    [Fact]
    public void SerialStreamTransport_Connect_WithInvalidPort_ShouldThrowException()
    {
        // Arrange - Use a port name that shouldn't exist
        using var transport = new SerialStreamTransport("COM999");
        
        // Act & Assert - Should throw some form of exception
        Assert.ThrowsAny<Exception>(() => transport.Connect());
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task SerialStreamTransport_ConnectAsync_WithInvalidPort_ShouldThrowException()
    {
        // Arrange - Use a port name that shouldn't exist
        using var transport = new SerialStreamTransport("COM999");
        
        // Act & Assert - Should throw some form of exception
        await Assert.ThrowsAnyAsync<Exception>(() => transport.ConnectAsync());
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public void SerialStreamTransport_Disconnect_WhenNotConnected_ShouldNotThrow()
    {
        // Arrange
        using var transport = new SerialStreamTransport("COM1");
        
        // Act & Assert - Should not throw
        transport.Disconnect();
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task SerialStreamTransport_DisconnectAsync_WhenNotConnected_ShouldNotThrow()
    {
        // Arrange
        using var transport = new SerialStreamTransport("COM1");
        
        // Act & Assert - Should not throw
        await transport.DisconnectAsync();
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public void SerialStreamTransport_StatusChanged_ShouldFireOnConnectionFailure()
    {
        // Arrange
        using var transport = new SerialStreamTransport("COM999");
        TransportStatusEventArgs? capturedArgs = null;
        
        transport.StatusChanged += (sender, args) => capturedArgs = args;
        
        // Act
        try
        {
            transport.Connect();
        }
        catch
        {
            // Expected
        }
        
        // Assert
        Assert.NotNull(capturedArgs);
        Assert.False(capturedArgs.IsConnected);
        Assert.NotNull(capturedArgs.Error);
    }

    [Fact]
    public void SerialStreamTransport_Dispose_ShouldCleanupResources()
    {
        // Arrange
        var transport = new SerialStreamTransport("COM1");
        
        // Act
        transport.Dispose();
        
        // Assert - Should throw ObjectDisposedException for operations after disposal
        Assert.Throws<ObjectDisposedException>(() => transport.Connect());
        Assert.Throws<ObjectDisposedException>(() => transport.Stream);
    }

    [Fact]
    public void SerialStreamTransport_ConnectionInfo_ShouldReflectCurrentState()
    {
        // Arrange
        using var transport = new SerialStreamTransport("COM3", 9600);
        
        // Act & Assert - Disconnected state
        var disconnectedInfo = transport.ConnectionInfo;
        Assert.Contains("Disconnected", disconnectedInfo);
        Assert.Contains("COM3", disconnectedInfo);
    }

    [Fact]
    public void SerialStreamTransport_GetAvailablePortNames_ShouldReturnArray()
    {
        // Act
        var portNames = SerialStreamTransport.GetAvailablePortNames();
        
        // Assert
        Assert.NotNull(portNames);
        // Note: We can't assert specific ports as they vary by system
        // but we can verify it returns an array without throwing
    }

    // Integration test that would require a real serial port - marked as integration test
    [Fact(Skip = "Integration test - requires physical serial port")]
    public async Task SerialStreamTransport_RealConnection_ShouldWorkEndToEnd()
    {
        // This test would connect to a real serial port if available
        // Could be enabled for integration testing scenarios with actual hardware
        
        var availablePorts = SerialStreamTransport.GetAvailablePortNames();
        if (availablePorts.Length == 0)
            return; // No ports available
            
        using var transport = new SerialStreamTransport(availablePorts[0]);
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