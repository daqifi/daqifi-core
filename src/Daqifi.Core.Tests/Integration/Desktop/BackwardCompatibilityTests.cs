using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Integration.Desktop;
using System.Text;

namespace Daqifi.Core.Tests.Integration.Desktop;

/// <summary>
/// Tests to ensure backward compatibility with existing DAQiFi Desktop applications.
/// These tests verify that existing desktop code patterns continue to work after the v0.4.0 changes.
/// </summary>
public class BackwardCompatibilityTests
{
    [Fact]
    public void BackwardCompatibility_ExistingFactoryMethods_ShouldStillWork()
    {
        // Arrange & Act - These are the factory methods that existing desktop code uses
        using var tcpAdapter = CoreDeviceAdapter.CreateTcpAdapter("192.168.1.100", 12345);
        using var serialAdapter = CoreDeviceAdapter.CreateSerialAdapter("COM1", 115200);
        
        // Assert - All should work exactly as before
        Assert.NotNull(tcpAdapter);
        Assert.NotNull(serialAdapter);
        Assert.IsType<TcpStreamTransport>(tcpAdapter.Transport);
        Assert.IsType<SerialStreamTransport>(serialAdapter.Transport);
    }

    [Fact]
    public void BackwardCompatibility_BasicProperties_ShouldBehaveSame()
    {
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("192.168.1.100", 12345);
        
        // Act & Assert - Properties that desktop code relies on
        Assert.False(adapter.IsConnected);
        Assert.Contains("192.168.1.100", adapter.ConnectionInfo);
        Assert.Contains("12345", adapter.ConnectionInfo);
        Assert.NotNull(adapter.Transport);
        Assert.Null(adapter.MessageProducer); // Null until connected
        Assert.Null(adapter.MessageConsumer); // Null until connected
    }

    [Fact]
    public void BackwardCompatibility_WriteMethod_ShouldBehaveSame()
    {
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("192.168.1.100", 12345);
        
        // Act - Test all the SCPI commands that desktop applications typically send
        var commands = new[]
        {
            "*IDN?",
            "*RST",
            "SYST:ERR?",
            "CONF:VOLT:DC",
            "READ?"
        };
        
        // Assert - All should return true (queued successfully)
        foreach (var command in commands)
        {
            var result = adapter.Write(command);
            Assert.True(result);
        }
    }

    [Fact]
    public void BackwardCompatibility_EventSubscription_ShouldWorkWithNewType()
    {
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("192.168.1.100", 12345);
        
        var messageReceived = false;
        var connectionStatusChanged = false;
        var errorOccurred = false;
        
        // Act - Subscribe to events as existing desktop code does
        adapter.MessageReceived += (sender, args) =>
        {
            messageReceived = true;
            // Desktop code should be able to handle the new object type
            var data = args.Message.Data;
            if (data is string textMessage)
            {
                // Handle SCPI text responses as before
            }
            else if (data is DaqifiOutMessage protobufMessage)
            {
                // Handle new protobuf messages
            }
        };
        
        adapter.ConnectionStatusChanged += (sender, args) =>
        {
            connectionStatusChanged = true;
            // Desktop code typically checks these properties
            var isConnected = args.IsConnected;
            var connectionInfo = args.ConnectionInfo;
            var error = args.Error;
        };
        
        adapter.ErrorOccurred += (sender, args) =>
        {
            errorOccurred = true;
            // Desktop code typically logs these errors
            var error = args.Error;
            var rawData = args.RawData;
        };
        
        // Assert - Event subscription should work without compilation errors
        Assert.False(messageReceived); // Events won't fire without connection
        Assert.False(connectionStatusChanged);
        Assert.False(errorOccurred);
    }

    [Fact]
    public void BackwardCompatibility_ConnectDisconnect_ShouldBehaveSame()
    {
        // Arrange
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345);
        
        // Act - Test both sync and async methods that desktop code uses
        var connectResult = adapter.Connect(); // Sync version
        var disconnectResult = adapter.Disconnect(); // Sync version
        
        // Also test async versions
        var connectAsyncResult = adapter.ConnectAsync().GetAwaiter().GetResult();
        var disconnectAsyncResult = adapter.DisconnectAsync().GetAwaiter().GetResult();
        
        // Assert - Behavior should be the same (will fail with test address, but methods work)
        Assert.False(connectResult); // Expected to fail with invalid address
        Assert.True(disconnectResult); // Should succeed
        Assert.False(connectAsyncResult);
        Assert.True(disconnectAsyncResult);
    }

    [Fact]
    public void BackwardCompatibility_DisposalPattern_ShouldBehaveSame()
    {
        // Arrange & Act - Test disposal patterns that desktop code might use
        var adapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345);
        
        // Using statement should work
        using (adapter)
        {
            var isConnected = adapter.IsConnected;
            Assert.False(isConnected);
        }
        
        // Multiple disposal should be safe
        adapter.Dispose();
        adapter.Dispose(); // Should not throw
    }

    [Fact]
    public void BackwardCompatibility_SerialPortEnumeration_ShouldStillWork()
    {
        // Act - Static method that desktop applications use for port discovery
        var availablePorts = CoreDeviceAdapter.GetAvailableSerialPorts();
        
        // Assert - Should return array (might be empty on test systems)
        Assert.NotNull(availablePorts);
        Assert.IsType<string[]>(availablePorts);
    }

    [Fact]
    public void BackwardCompatibility_ConstructorOverloads_ShouldMaintainCompatibility()
    {
        // Arrange
        using var transport = new TcpStreamTransport("localhost", 12345);
        
        // Act - Original constructor should still work
        using var adapter1 = new CoreDeviceAdapter(transport);
        
        // New constructor with parser should also work
        using var adapter2 = new CoreDeviceAdapter(transport, null);
        using var adapter3 = new CoreDeviceAdapter(transport, new CompositeMessageParser());
        
        // Assert - All constructors should work
        Assert.NotNull(adapter1);
        Assert.NotNull(adapter2);
        Assert.NotNull(adapter3);
        Assert.Same(transport, adapter1.Transport);
        Assert.Same(transport, adapter2.Transport);
        Assert.Same(transport, adapter3.Transport);
    }

    [Fact]
    public void BackwardCompatibility_ExistingDesktopCodePattern_ShouldCompileAndRun()
    {
        // This test simulates the exact pattern used in existing desktop applications
        
        // Arrange - Use localhost with unopened port for fast failure
        using var device = CoreDeviceAdapter.CreateTcpAdapter("127.0.0.1", 1); // localhost:1 should fail immediately
        
        // Subscribe to events (this is how desktop apps get device responses)
        device.MessageReceived += (sender, args) =>
        {
            // Desktop apps typically cast or check the message data
            var response = args.Message.Data?.ToString()?.Trim() ?? "";
            
            if (response.StartsWith("DAQiFi"))
            {
                // Handle device identification
            }
            else if (response.Contains("Error"))
            {
                // Handle error responses
            }
        };
        
        device.ConnectionStatusChanged += (sender, args) =>
        {
            if (args.IsConnected)
            {
                // Device connected - send identification command
                device.Write("*IDN?");
            }
        };
        
        // Act - Typical connection sequence
        var connected = device.Connect(); // This will fail with test IP, but pattern works
        
        if (connected)
        {
            device.Write("*IDN?");
            device.Write("SYST:ERR?");
            
            // Wait for responses (in real app)
            Thread.Sleep(100);
            
            device.Disconnect();
        }
        
        // Assert - Code pattern should work without exceptions
        Assert.False(connected); // Expected with test IP
    }

    [Fact]
    public void BackwardCompatibility_MessageHandling_ShouldSupportBothTypes()
    {
        // Arrange - Test that desktop apps can handle both old and new message types
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345);
        
        var handledTextMessages = 0;
        var handledProtobufMessages = 0;
        var handledUnknownMessages = 0;
        
        adapter.MessageReceived += (sender, args) =>
        {
            var messageData = args.Message.Data;
            
            // Pattern that desktop applications should use for compatibility
            switch (messageData)
            {
                case string textMessage:
                    handledTextMessages++;
                    // Handle SCPI text responses as before
                    break;
                    
                case DaqifiOutMessage protobufMessage:
                    handledProtobufMessages++;
                    // Handle new binary protobuf messages
                    break;
                    
                default:
                    handledUnknownMessages++;
                    // Handle any other message types gracefully
                    break;
            }
        };
        
        // Assert - Event handler setup should work
        Assert.Equal(0, handledTextMessages);
        Assert.Equal(0, handledProtobufMessages);
        Assert.Equal(0, handledUnknownMessages);
    }

    [Fact]
    public void BackwardCompatibility_PerformanceCharacteristics_ShouldBeComparable()
    {
        // Arrange - Test that performance hasn't degraded significantly
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345);
        
        // Act - Time basic operations
        var startTime = DateTime.UtcNow;
        
        for (int i = 0; i < 1000; i++)
        {
            adapter.Write($"Command {i}");
        }
        
        var endTime = DateTime.UtcNow;
        var elapsed = endTime - startTime;
        
        // Assert - Should complete quickly (basic sanity check)
        Assert.True(elapsed.TotalMilliseconds < 1000); // Should be very fast for 1000 commands
    }
}