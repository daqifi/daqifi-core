using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using System.Net;
using System.Net.Sockets;

namespace Daqifi.Core.Tests.Device;

public class DaqifiDeviceWithTransportTests
{
    [Fact]
    public void DaqifiDevice_WithTransport_ShouldInitializeCorrectly()
    {
        // Arrange
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 5000);
        
        // Act
        using var device = new DaqifiDevice("Test Device", transport);
        
        // Assert
        Assert.Equal("Test Device", device.Name);
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
        Assert.False(device.IsConnected);
    }

    [Fact]
    public void DaqifiDevice_Connect_WithTransport_ShouldAttemptConnection()
    {
        // Arrange - Use localhost port 1 (reserved, never listening)
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 1);
        using var device = new DaqifiDevice("Test Device", transport);
        
        // Act & Assert - Should throw due to connection refused
        Assert.Throws<SocketException>(() => device.Connect());
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
    }

    [Fact]
    public void DaqifiDevice_StatusChanged_ShouldFireOnTransportEvents()
    {
        // Arrange - Use localhost port 1 (reserved, never listening)
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 1);
        using var device = new DaqifiDevice("Test Device", transport);
        
        var statusChanges = new List<ConnectionStatus>();
        device.StatusChanged += (sender, args) => statusChanges.Add(args.Status);
        
        // Act - Try to connect (will fail)
        try
        {
            device.Connect();
        }
        catch (SocketException)
        {
            // Expected
        }
        
        // Assert
        Assert.Contains(ConnectionStatus.Connecting, statusChanges);
        Assert.Contains(ConnectionStatus.Disconnected, statusChanges);
    }

    [Fact]
    public void DaqifiDevice_Disconnect_WithTransport_ShouldDisconnectSafely()
    {
        // Arrange
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 5000);
        using var device = new DaqifiDevice("Test Device", transport);
        
        // Act - Disconnect without connecting should not throw
        device.Disconnect();
        
        // Assert
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
        Assert.False(device.IsConnected);
    }

    [Fact]
    public void DaqifiDevice_SendMessage_WithoutConnection_ShouldThrowException()
    {
        // Arrange
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 5000);
        using var device = new DaqifiDevice("Test Device", transport);
        
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => device.Send(ScpiMessageProducer.GetDeviceInfo));
    }

    [Fact]
    public void DaqifiDevice_Dispose_WithTransport_ShouldCleanupResources()
    {
        // Arrange
        var transport = new TcpStreamTransport(IPAddress.Loopback, 5000);
        var device = new DaqifiDevice("Test Device", transport);
        
        // Act
        device.Dispose();
        
        // Assert - Transport should be disposed, operations should throw
        Assert.Throws<ObjectDisposedException>(() => transport.Connect());
        Assert.Throws<ObjectDisposedException>(() => transport.Stream);
    }

    // Mock transport for testing device behavior without network dependencies
    private class MockStreamTransport : IStreamTransport
    {
        private readonly MemoryStream _stream = new();
        private bool _isConnected;
        private bool _disposed;

        public Stream Stream => _disposed ? throw new ObjectDisposedException(nameof(MockStreamTransport)) : _stream;
        public bool IsConnected => _isConnected && !_disposed;
        public string ConnectionInfo => _disposed ? "Disposed" : (_isConnected ? "Mock: Connected" : "Mock: Disconnected");

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public Task ConnectAsync()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MockStreamTransport));
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

        // Test helper method to simulate connection loss
        public void SimulateConnectionLoss()
        {
            _isConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, "Connection lost"));
        }

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

    [Fact]
    public void DaqifiDevice_WithMockTransport_ShouldConnectAndSendMessages()
    {
        // Arrange
        using var transport = new MockStreamTransport();
        using var device = new DaqifiDevice("Mock Device", transport);
        
        var statusChanges = new List<ConnectionStatus>();
        device.StatusChanged += (sender, args) => statusChanges.Add(args.Status);
        
        // Act
        device.Connect();
        
        // Allow time for message producer to start
        Thread.Sleep(100);
        
        device.Send(ScpiMessageProducer.GetDeviceInfo);
        
        // Allow time for message to be processed
        Thread.Sleep(200);
        
        device.Disconnect();
        
        // Assert
        Assert.Contains(ConnectionStatus.Connecting, statusChanges);
        Assert.Contains(ConnectionStatus.Connected, statusChanges);
        Assert.Contains(ConnectionStatus.Disconnected, statusChanges);
        
        // Verify message was written to stream
        var memoryStream = (MemoryStream)transport.Stream;
        var streamContent = System.Text.Encoding.UTF8.GetString(memoryStream.ToArray());
        Assert.Contains("SYSTem:SYSInfoPB?", streamContent);
    }

    [Fact] 
    public void DaqifiDevice_TransportConnectionLost_ShouldUpdateStatus()
    {
        // Arrange
        using var transport = new MockStreamTransport();
        using var device = new DaqifiDevice("Mock Device", transport);
        
        device.Connect();
        Assert.Equal(ConnectionStatus.Connected, device.Status);
        
        // Act - Simulate transport connection loss 
        transport.SimulateConnectionLoss();
        
        // Assert
        Assert.Equal(ConnectionStatus.Lost, device.Status);
    }
}