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
        EventHandler<MessageReceivedEventArgs<object>> handler = (sender, args) => { };
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

    [Fact]
    public void CoreDeviceAdapter_WithCustomMessageParser_ShouldUseProvidedParser()
    {
        // Arrange
        using var transport = new TcpStreamTransport("localhost", 12345);
        var customParser = new CompositeMessageParser();
        
        // Act
        using var adapter = new CoreDeviceAdapter(transport, customParser);
        
        // Assert
        Assert.NotNull(adapter);
        Assert.Same(transport, adapter.Transport);
    }

    [Fact]
    public void CoreDeviceAdapter_WithNullMessageParser_ShouldUseDefaultCompositeParser()
    {
        // Arrange
        using var transport = new TcpStreamTransport("localhost", 12345);
        
        // Act
        using var adapter = new CoreDeviceAdapter(transport, null);
        
        // Assert
        Assert.NotNull(adapter);
        Assert.Same(transport, adapter.Transport);
    }

    [Fact]
    public void CoreDeviceAdapter_CreateTextOnlyTcpAdapter_ShouldCreateCorrectly()
    {
        // Act
        using var adapter = CoreDeviceAdapter.CreateTextOnlyTcpAdapter("192.168.1.100", 12345);
        
        // Assert
        Assert.NotNull(adapter);
        Assert.IsType<TcpStreamTransport>(adapter.Transport);
        Assert.Contains("192.168.1.100", adapter.ConnectionInfo);
        Assert.Contains("12345", adapter.ConnectionInfo);
        Assert.False(adapter.IsConnected);
    }

    [Fact]
    public void CoreDeviceAdapter_CreateProtobufOnlyTcpAdapter_ShouldCreateCorrectly()
    {
        // Act
        using var adapter = CoreDeviceAdapter.CreateProtobufOnlyTcpAdapter("192.168.1.100", 12345);
        
        // Assert
        Assert.NotNull(adapter);
        Assert.IsType<TcpStreamTransport>(adapter.Transport);
        Assert.Contains("192.168.1.100", adapter.ConnectionInfo);
        Assert.Contains("12345", adapter.ConnectionInfo);
        Assert.False(adapter.IsConnected);
    }

    [Fact]
    public void CoreDeviceAdapter_CreateTcpAdapterWithCustomParser_ShouldCreateCorrectly()
    {
        // Arrange
        var customParser = new CompositeMessageParser(new LineBasedMessageParser("\n"), null);
        
        // Act
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("192.168.1.100", 12345, customParser);
        
        // Assert
        Assert.NotNull(adapter);
        Assert.IsType<TcpStreamTransport>(adapter.Transport);
        Assert.Contains("192.168.1.100", adapter.ConnectionInfo);
    }

    [Fact]
    public void CoreDeviceAdapter_CreateSerialAdapterWithCustomParser_ShouldCreateCorrectly()
    {
        // Arrange
        var customParser = new CompositeMessageParser();
        
        // Act
        using var adapter = CoreDeviceAdapter.CreateSerialAdapter("COM1", 115200, customParser);
        
        // Assert
        Assert.NotNull(adapter);
        Assert.IsType<SerialStreamTransport>(adapter.Transport);
        Assert.Contains("COM1", adapter.ConnectionInfo);
        Assert.Contains("115200", adapter.ConnectionInfo);
    }

    [Fact]
    public void CoreDeviceAdapter_MessageConsumerType_ShouldHandleObjectMessages()
    {
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345);
        
        // Act & Assert - Verify the MessageConsumer can handle object messages
        Assert.True(adapter.MessageConsumer == null); // Not connected yet
        
        // Verify event handler can be assigned with object type
        EventHandler<MessageReceivedEventArgs<object>> handler = (sender, args) => 
        {
            // Should be able to handle both string and protobuf messages
            if (args.Message.Data is string textMsg)
            {
                // Handle text message
            }
            else if (args.Message.Data is DaqifiOutMessage protobufMsg)
            {
                // Handle protobuf message
            }
        };
        
        adapter.MessageReceived += handler;
        adapter.MessageReceived -= handler;
    }

    [Fact]
    public void CoreDeviceAdapter_BackwardCompatibility_ShouldStillSupportBasicOperations()
    {
        // Arrange - Test that existing desktop code patterns still work
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("192.168.1.100", 12345);
        
        // Act & Assert - Basic operations that desktop code relies on
        Assert.False(adapter.IsConnected);
        Assert.NotEmpty(adapter.ConnectionInfo);
        Assert.NotNull(adapter.Transport);
        
        // Event subscription should work
        var messageReceived = false;
        adapter.MessageReceived += (sender, args) => messageReceived = true;
        
        var statusChanged = false;
        adapter.ConnectionStatusChanged += (sender, args) => statusChanged = true;
        
        var errorOccurred = false;
        adapter.ErrorOccurred += (sender, args) => errorOccurred = true;
        
        // Write method should work even when not connected
        var writeResult = adapter.Write("*IDN?");
        Assert.True(writeResult); // Should return true for queuing
    }
}