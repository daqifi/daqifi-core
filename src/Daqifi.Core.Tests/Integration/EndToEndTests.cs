using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using System.Text;

namespace Daqifi.Core.Tests.Integration;

/// <summary>
/// Integration tests that verify the complete end-to-end flow:
/// Transport -> MessageProducer -> Device -> SCPI Commands
/// </summary>
public class EndToEndTests
{
    [Fact]
    public void EndToEnd_MockTransport_ShouldDeliverSCPICommands()
    {
        // Arrange - Create a complete stack with mock transport
        using var mockTransport = new MockMemoryStreamTransport();
        using var device = new DaqifiDevice("Integration Test Device", mockTransport);
        
        var statusChanges = new List<ConnectionStatus>();
        device.StatusChanged += (sender, args) => statusChanges.Add(args.Status);
        
        // Act - Complete connection and command flow
        device.Connect();
        Thread.Sleep(100); // Allow message producer to start
        
        // Send multiple SCPI commands
        device.Send(ScpiMessageProducer.GetDeviceInfo);
        device.Send(ScpiMessageProducer.RebootDevice);
        device.Send(ScpiMessageProducer.StartStreaming(1000));
        device.Send(ScpiMessageProducer.StopStreaming);
        
        // Allow background processing
        Thread.Sleep(300);
        
        device.Disconnect();
        
        // Assert - Verify complete flow worked correctly
        Assert.Contains(ConnectionStatus.Connecting, statusChanges);
        Assert.Contains(ConnectionStatus.Connected, statusChanges);
        Assert.Contains(ConnectionStatus.Disconnected, statusChanges);
        
        // Verify all SCPI commands were written to transport
        var transportContent = mockTransport.GetWrittenContent();
        Assert.Contains("SYSTem:SYSInfoPB?", transportContent);
        Assert.Contains("SYSTem:REboot", transportContent);
        Assert.Contains("SYSTem:StartStreamData 1000", transportContent);
        Assert.Contains("SYSTem:StopStreamData", transportContent);
    }

    [Fact]  
    public void EndToEnd_DeviceWithStreamConstructor_ShouldMaintainBackwardCompatibility()
    {
        // Arrange - Test the older Stream-based constructor
        using var stream = new MemoryStream();
        using var device = new DaqifiDevice("Backward Compatible Device", stream);
        
        // Act
        device.Connect();
        Thread.Sleep(100);
        
        device.Send(ScpiMessageProducer.GetDeviceInfo);
        Thread.Sleep(200);
        
        device.Disconnect();
        
        // Assert
        var streamContent = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("SYSTem:SYSInfoPB?", streamContent);
    }

    [Fact]
    public void EndToEnd_MultipleDevices_ShouldWorkIndependently()
    {
        // Arrange - Multiple devices with separate transports
        using var transport1 = new MockMemoryStreamTransport();
        using var transport2 = new MockMemoryStreamTransport();
        using var device1 = new DaqifiDevice("Device 1", transport1);
        using var device2 = new DaqifiDevice("Device 2", transport2);
        
        // Act - Connect and use both devices
        device1.Connect();
        device2.Connect();
        Thread.Sleep(100);
        
        device1.Send(ScpiMessageProducer.GetDeviceInfo);
        device2.Send(ScpiMessageProducer.RebootDevice);
        Thread.Sleep(300);
        
        device1.Disconnect();
        device2.Disconnect();
        
        // Assert - Each device should have its own messages
        var content1 = transport1.GetWrittenContent();
        var content2 = transport2.GetWrittenContent();
        
        Assert.Contains("SYSTem:SYSInfoPB?", content1);
        Assert.DoesNotContain("SYSTem:REboot", content1);
        
        Assert.Contains("SYSTem:REboot", content2);
        Assert.DoesNotContain("SYSTem:SYSInfoPB?", content2);
    }

    [Fact]
    public void EndToEnd_MessageProducerLifecycle_ShouldHandleStartStopCorrectly()
    {
        // Arrange
        using var transport = new MockMemoryStreamTransport();
        using var device = new DaqifiDevice("Lifecycle Test Device", transport);
        
        // Act - Multiple connect/disconnect cycles
        device.Connect();
        device.Send(ScpiMessageProducer.GetDeviceInfo);
        Thread.Sleep(100);
        device.Disconnect();
        
        // Reconnect
        device.Connect();
        device.Send(ScpiMessageProducer.RebootDevice);
        Thread.Sleep(100);
        device.Disconnect();
        
        // Assert - Both messages should be present
        var content = transport.GetWrittenContent();
        Assert.Contains("SYSTem:SYSInfoPB?", content);
        Assert.Contains("SYSTem:REboot", content);
    }

    // Mock transport for integration testing
    private class MockMemoryStreamTransport : IStreamTransport
    {
        private readonly MemoryStream _stream = new();
        private bool _isConnected;
        private bool _disposed;

        public Stream Stream => _disposed ? throw new ObjectDisposedException(nameof(MockMemoryStreamTransport)) : _stream;
        public bool IsConnected => _isConnected && !_disposed;
        public string ConnectionInfo => _disposed ? "Disposed" : (_isConnected ? "Mock: Connected" : "Mock: Disconnected");

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public string GetWrittenContent() => Encoding.UTF8.GetString(_stream.ToArray());

        public Task ConnectAsync()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MockMemoryStreamTransport));
            _isConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            _isConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
            return Task.CompletedTask;
        }

        public void Connect() => ConnectAsync().Wait();
        public void Disconnect() => DisconnectAsync().Wait();

        public void Dispose()
        {
            if (!_disposed)
            {
                _isConnected = false;
                _stream.Dispose();
                _disposed = true;
            }
        }
    }
}