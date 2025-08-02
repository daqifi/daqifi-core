using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Integration.Desktop;

namespace Daqifi.Core.Tests.Integration.Desktop;

public class CoreDeviceAdapterTests
{
    [Fact]
    public void CoreDeviceAdapter_Constructor_ShouldInitializeCorrectly()
    {
        // Arrange
        using var transport = new TcpStreamTransport("localhost", 12345);
        
        // Act
        using var adapter = new CoreDeviceAdapter(transport);
        
        // Assert
        Assert.NotNull(adapter.Transport);
        Assert.Same(transport, adapter.Transport); 
        Assert.Null(adapter.MessageProducer); // Not created until connected
        Assert.Null(adapter.MessageConsumer); // Not created until connected
        Assert.False(adapter.IsConnected);
    }

    [Fact]
    public void CoreDeviceAdapter_Constructor_WithNullTransport_ShouldThrowException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new CoreDeviceAdapter(null!));
    }

    [Fact]
    public void CoreDeviceAdapter_CreateTcpAdapter_ShouldCreateCorrectly()
    {
        // Act
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("192.168.1.100", 12345);
        
        // Assert
        Assert.NotNull(adapter);
        Assert.IsType<TcpStreamTransport>(adapter.Transport);
        Assert.Contains("192.168.1.100", adapter.ConnectionInfo);
        Assert.Contains("12345", adapter.ConnectionInfo);
        Assert.False(adapter.IsConnected);
    }

    [Fact]
    public void CoreDeviceAdapter_CreateSerialAdapter_ShouldCreateCorrectly()
    {
        // Act
        using var adapter = CoreDeviceAdapter.CreateSerialAdapter("COM1", 9600);
        
        // Assert
        Assert.NotNull(adapter);
        Assert.IsType<SerialStreamTransport>(adapter.Transport);
        Assert.Contains("COM1", adapter.ConnectionInfo);
        Assert.False(adapter.IsConnected);
    }

    [Fact]
    public void CoreDeviceAdapter_CreateSerialAdapter_WithDefaultBaudRate_ShouldCreateCorrectly()
    {
        // Act
        using var adapter = CoreDeviceAdapter.CreateSerialAdapter("COM2");
        
        // Assert
        Assert.NotNull(adapter);
        Assert.IsType<SerialStreamTransport>(adapter.Transport);
        Assert.Contains("COM2", adapter.ConnectionInfo);
        Assert.False(adapter.IsConnected);
    }

    [Fact]
    public void CoreDeviceAdapter_GetAvailableSerialPorts_ShouldReturnArray()
    {
        // Act
        var ports = CoreDeviceAdapter.GetAvailableSerialPorts();
        
        // Assert
        Assert.NotNull(ports);
        // Note: We can't assert specific ports as they vary by system
        // but we can verify it returns an array without throwing
    }

    [Fact]
    public void CoreDeviceAdapter_Write_WhenNotConnected_ShouldReturnTrue()
    {
        // Arrange
        using var transport = new TcpStreamTransport("localhost", 12345);
        using var adapter = new CoreDeviceAdapter(transport);
        
        // Act
        var result = adapter.Write("*IDN?");
        
        // Assert
        Assert.True(result); // Should return true even if not connected (queues message)
    }

    [Fact] 
    public void CoreDeviceAdapter_Connect_WithInvalidAddress_ShouldReturnFalse()
    {
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("192.0.2.1", 1); // TEST-NET-1 address that should fail fast
        
        // Act
        var result = adapter.Connect();
        
        // Assert
        Assert.False(result);
        Assert.False(adapter.IsConnected);
    }

    [Fact]
    public async Task CoreDeviceAdapter_ConnectAsync_WithInvalidAddress_ShouldReturnFalse()
    {
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("192.0.2.1", 1); // TEST-NET-1 address that should fail fast
        
        // Act
        var result = await adapter.ConnectAsync();
        
        // Assert
        Assert.False(result);
        Assert.False(adapter.IsConnected);
    }

    [Fact]
    public void CoreDeviceAdapter_Disconnect_WhenNotConnected_ShouldReturnTrue()
    {
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345);
        
        // Act
        var result = adapter.Disconnect();
        
        // Assert
        Assert.True(result);
        Assert.False(adapter.IsConnected);
    }

    [Fact]
    public async Task CoreDeviceAdapter_DisconnectAsync_WhenNotConnected_ShouldReturnTrue()
    {
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345);
        
        // Act
        var result = await adapter.DisconnectAsync();
        
        // Assert
        Assert.True(result);
        Assert.False(adapter.IsConnected);
    }

    [Fact]
    public void CoreDeviceAdapter_DataStream_WhenNotConnected_ShouldReturnNull()
    {
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345);
        
        // Act
        var stream = adapter.DataStream;
        
        // Assert
        Assert.Null(stream);
    }

    [Fact]
    public void CoreDeviceAdapter_ConnectionStatusChanged_ShouldFireOnConnectionAttempt()
    {
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("192.0.2.1", 1); // TEST-NET-1 address that should fail fast
        TransportStatusEventArgs? capturedArgs = null;
        
        adapter.ConnectionStatusChanged += (sender, args) => capturedArgs = args;
        
        // Act
        adapter.Connect(); // This should fail and fire the event
        
        // Assert
        Assert.NotNull(capturedArgs);
        Assert.False(capturedArgs.IsConnected);
        Assert.NotNull(capturedArgs.Error);
    }

    [Fact]
    public void CoreDeviceAdapter_MessageReceived_ShouldProvideEventAccess()
    {
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345);
        var eventHandlerAdded = false;
        
        // Act - Just verify we can add/remove event handlers without exceptions
        EventHandler<MessageReceivedEventArgs<string>> handler = (sender, args) => { };
        adapter.MessageReceived += handler;
        eventHandlerAdded = true;
        adapter.MessageReceived -= handler;
        
        // Assert
        Assert.True(eventHandlerAdded);
    }

    [Fact]
    public void CoreDeviceAdapter_ErrorOccurred_ShouldProvideEventAccess()
    {
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345);
        var eventHandlerAdded = false;
        
        // Act - Just verify we can add/remove event handlers without exceptions
        EventHandler<MessageConsumerErrorEventArgs> handler = (sender, args) => { };
        adapter.ErrorOccurred += handler;
        eventHandlerAdded = true;
        adapter.ErrorOccurred -= handler;
        
        // Assert
        Assert.True(eventHandlerAdded);
    }

    [Fact]
    public void CoreDeviceAdapter_Dispose_ShouldCleanupResources()
    {
        // Arrange
        var adapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345);
        
        // Act
        adapter.Dispose();
        
        // Assert - Should not throw when disposed
        // Additional disposal should also be safe
        adapter.Dispose();
    }

    [Fact]
    public void CoreDeviceAdapter_Properties_ShouldReflectTransportState()
    {
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("127.0.0.1", 54321);
        
        // Act & Assert
        Assert.Contains("127.0.0.1", adapter.ConnectionInfo);
        Assert.Contains("54321", adapter.ConnectionInfo);
        Assert.False(adapter.IsConnected);
        
        // Verify properties update with transport state
        var connectionInfo = adapter.ConnectionInfo;
        Assert.Contains("Disconnected", connectionInfo);
    }

    [Fact]
    public void CoreDeviceAdapter_MessageProducer_ShouldBeNullWhenNotConnected()
    {
        // Arrange
        using var transport = new TcpStreamTransport("localhost", 12345);
        using var adapter = new CoreDeviceAdapter(transport);
        
        // Act & Assert
        Assert.Null(adapter.MessageProducer);
        Assert.Null(adapter.MessageConsumer);
    }

    [Fact]
    public void CoreDeviceAdapter_IntegrationUsagePattern_ShouldWorkAsExpected()
    {
        // This test demonstrates how desktop applications would use the adapter
        
        // Arrange - Create adapter for a WiFi device
        using var wifiAdapter = CoreDeviceAdapter.CreateTcpAdapter("192.168.1.100", 12345);
        
        // Act - Typical usage pattern
        var connectResult = wifiAdapter.Connect(); // Would normally succeed with real device
        var writeResult = wifiAdapter.Write("*IDN?"); // Queue command
        var connectionInfo = wifiAdapter.ConnectionInfo;
        var isConnected = wifiAdapter.IsConnected;
        
        // Subscribe to events as desktop applications would
        wifiAdapter.MessageReceived += (sender, args) => {
            // Handle received messages
            var messageData = args.Message.Data;
        };
        
        wifiAdapter.ConnectionStatusChanged += (sender, args) => {
            // Handle connection status changes
            var connected = args.IsConnected;
        };
        
        // Clean up
        wifiAdapter.Disconnect();
        
        // Assert - Basic functionality works
        Assert.False(connectResult); // Expected to fail with test address
        Assert.True(writeResult); // Commands can be queued
        Assert.NotEmpty(connectionInfo);
        Assert.False(isConnected);
    }
}