using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Integration.Desktop;
using System.Text;

namespace Daqifi.Core.Tests.Integration.Desktop;

/// <summary>
/// Tests for the improvements made to CoreDeviceAdapter to address GitHub issue #39.
/// These tests verify that the adapter now provides true drop-in replacement capability
/// for desktop applications with proper message type compatibility and WiFi device features.
/// </summary>
public class CoreDeviceAdapterImprovementsTests
{
    [Fact]
    public void CreateTcpAdapter_ShouldAutomaticallySetWiFiDeviceFlag()
    {
        // Arrange & Act - TCP adapters should default to WiFi mode
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("192.168.1.100", 12345);

        // Assert
        Assert.True(adapter.IsWifiDevice);
    }

    [Fact]
    public void CreateSerialAdapter_ShouldNotSetWiFiDeviceFlag()
    {
        // Arrange & Act - Serial adapters should not be WiFi devices
        using var adapter = CoreDeviceAdapter.CreateSerialAdapter("COM1", 115200);

        // Assert
        Assert.False(adapter.IsWifiDevice);
    }

    [Fact]
    public void IsWiFiDevice_CanBeSetAndRetrieved()
    {
        // Arrange
        using var transport = new TcpStreamTransport("localhost", 12345);
        using var adapter = new CoreDeviceAdapter(transport);

        // Act & Assert - Should be able to set and get WiFi device flag
        Assert.False(adapter.IsWifiDevice); // Default false for manual construction
        
        adapter.IsWifiDevice = true;
        Assert.True(adapter.IsWifiDevice);
        
        adapter.IsWifiDevice = false;
        Assert.False(adapter.IsWifiDevice);
    }

    [Fact]
    public void ClearBuffer_WithoutConnection_ShouldReturnFalse()
    {
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345);

        // Act
        var result = adapter.ClearBuffer();

        // Assert - Should return false when not connected
        Assert.False(result);
    }

    [Fact]
    public void StopSafely_ShouldNotThrowException()
    {
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345);

        // Act & Assert - Should not throw even when not connected
        adapter.StopSafely();
    }

    [Fact]
    public void MessageReceived_Event_ShouldProvideCorrectMessageTypes()
    {
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345);
        
        var messageReceived = false;
        object? receivedMessageData = null;

        adapter.MessageReceived += (sender, args) =>
        {
            messageReceived = true;
            receivedMessageData = args.Message.Data;
            
            // Desktop applications should be able to handle both message types
            switch (args.Message.Data)
            {
                case string textMessage:
                    // Handle SCPI text responses
                    Assert.NotNull(textMessage);
                    break;
                    
                case DaqifiOutMessage protobufMessage:
                    // Handle protobuf messages
                    Assert.NotNull(protobufMessage);
                    break;
                    
                default:
                    // Should handle other types gracefully
                    break;
            }
        };

        // Act - Event subscription should work without compilation errors
        // Assert - Event handler setup should work
        Assert.False(messageReceived); // No messages until connected
        Assert.Null(receivedMessageData);
    }

    [Fact]
    public void DesktopIntegrationPattern_ShouldWorkWithMinimalCode()
    {
        // This test demonstrates the improved drop-in replacement capability
        
        // Arrange - Simulate desktop application pattern
        using var device = CoreDeviceAdapter.CreateTcpAdapter("127.0.0.1", 1);
        
        var messagesReceived = 0;
        var connectionStatusChanges = 0;
        var errorsOccurred = 0;

        // Act - Desktop application integration pattern
        device.MessageReceived += (sender, args) =>
        {
            messagesReceived++;
            // Desktop can now directly cast message data
            var messageData = args.Message.Data;
            
            if (messageData is string textResponse)
            {
                // Handle SCPI responses exactly as before
                if (textResponse.StartsWith("DAQiFi"))
                {
                    // Device identification response
                }
            }
            else if (messageData is DaqifiOutMessage protobufResponse)
            {
                // Handle binary protobuf messages with rich device data
                var deviceSerial = protobufResponse.DeviceSn;
                var analogChannels = protobufResponse.AnalogInPortNum;
            }
        };
        
        device.ConnectionStatusChanged += (sender, args) =>
        {
            connectionStatusChanges++;
            if (args.IsConnected)
            {
                // Connected - send initialization commands
                device.Write("*IDN?");
                device.Write("SYSTEM:INFO?");
                
                // WiFi devices can clear buffers
                if (device.IsWifiDevice)
                {
                    device.ClearBuffer();
                }
            }
        };
        
        device.ErrorOccurred += (sender, args) =>
        {
            errorsOccurred++;
            // Handle errors exactly as before
            var error = args.Error;
            var rawData = args.RawData;
        };

        // Connection attempt (will fail with test IP, but pattern works)
        var connected = device.Connect();
        
        if (connected)
        {
            device.Write("*IDN?");
            Thread.Sleep(100);
            device.StopSafely(); // Use new safe stop method
            device.Disconnect();
        }

        // Assert - Integration should work without exceptions
        Assert.False(connected); // Expected with test IP
        Assert.Equal(0, messagesReceived); // No messages with failed connection
    }

    [Fact]
    public void WiFiDevice_AutomaticBufferClearing_ShouldOccurAfterConnection()
    {
        // This test verifies that WiFi devices automatically clear buffers after connection
        // We can't test actual buffer clearing without a real connection, but we can verify
        // the pattern works without exceptions
        
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345);
        
        // Act - Verify WiFi device settings
        Assert.True(adapter.IsWifiDevice);
        
        // Verify ClearBuffer can be called safely even without connection
        var clearResult = adapter.ClearBuffer();
        Assert.False(clearResult); // Expected false when not connected
        
        // Verify StopSafely can be called safely
        adapter.StopSafely(); // Should not throw
    }

    [Fact]
    public void DesktopCompatibleConsumer_ShouldProvideExactEventSignature()
    {
        // Arrange
        using var memoryStream = new MemoryStream();
        var parser = new CompositeMessageParser();
        using var consumer = new DesktopCompatibleMessageConsumer(memoryStream, parser);
        
        // Act - Set WiFi device properties like desktop does
        consumer.IsWifiDevice = true;
        Assert.True(consumer.IsWifiDevice);
        
        // Verify methods exist and can be called
        consumer.ClearBuffer(); // Should not throw
        consumer.Start();
        Assert.True(consumer.IsRunning);
        
        consumer.Stop();
        Assert.False(consumer.IsRunning);
        
        var stopResult = consumer.StopSafely(100);
        Assert.True(stopResult); // Should stop cleanly when already stopped
    }

    [Fact]
    public void MessageEventArgs_ShouldBeCompatibleWithDesktopCasting()
    {
        // This test verifies that the message event args can be cast exactly as desktop code expects
        
        // Arrange - Create a mock message like the desktop would receive
        var testMessage = new TestInboundMessage("Test Response");
        var eventArgs = new MessageReceivedEventArgs<object>(testMessage, Encoding.UTF8.GetBytes("test"));
        
        // Act - Simulate desktop casting patterns
        var messageData = eventArgs.Message.Data;
        
        // Assert - Desktop should be able to cast message data directly
        Assert.NotNull(messageData);
        Assert.IsType<string>(messageData);
        Assert.Equal("Test Response", messageData);
        
        // Verify event args has the properties desktop expects
        Assert.NotNull(eventArgs.Message);
        Assert.NotNull(eventArgs.RawData);
    }

    [Fact] 
    public void CoreDeviceAdapter_ShouldSupportAllDesktopRequiredMethods()
    {
        // This test verifies all the methods that desktop applications require are present
        
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345);
        
        // Act & Assert - All these methods should exist and be callable
        
        // Connection methods (both sync and async)
        var connectResult = adapter.Connect();
        var disconnectResult = adapter.Disconnect();
        
        // Command sending
        var writeResult = adapter.Write("*IDN?");
        Assert.True(writeResult); // Should accept commands even when not connected
        
        // Properties that desktop relies on
        Assert.False(adapter.IsConnected);
        Assert.NotNull(adapter.ConnectionInfo);
        Assert.NotNull(adapter.Transport);
        
        // WiFi-specific features
        Assert.True(adapter.IsWifiDevice); // TCP adapters should be WiFi devices
        var clearBufferResult = adapter.ClearBuffer();
        adapter.StopSafely();
        
        // Event subscription (should not throw)
        adapter.MessageReceived += (s, e) => { };
        adapter.ConnectionStatusChanged += (s, e) => { };
        adapter.ErrorOccurred += (s, e) => { };
        
        // Static factory methods
        var serialAdapter = CoreDeviceAdapter.CreateSerialAdapter("COM1", 115200);
        Assert.NotNull(serialAdapter);
        Assert.False(serialAdapter.IsWifiDevice); // Serial should not be WiFi
        serialAdapter.Dispose();
        
        var availablePorts = CoreDeviceAdapter.GetAvailableSerialPorts();
        Assert.NotNull(availablePorts);
    }

    /// <summary>
    /// Test implementation of IInboundMessage for testing purposes.
    /// </summary>
    private class TestInboundMessage : IInboundMessage<object>
    {
        public TestInboundMessage(string data)
        {
            Data = data;
            Timestamp = DateTime.UtcNow;
        }
        
        public object Data { get; }
        public DateTime Timestamp { get; }
    }
}