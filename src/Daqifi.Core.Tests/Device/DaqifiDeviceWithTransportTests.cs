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
            return ConnectAsync(null);
        }

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
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

        // Test helper: simulate a *silent* drop — the underlying transport closes without
        // raising a status event, mirroring a serial port closed by an unplug or a
        // DTR-triggered MCU reset mid-connect. The owning device keeps reporting Connected
        // because it never received a StatusChanged(false) (issue #238).
        public void SimulateSilentConnectionLoss()
        {
            _isConnected = false;
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
    public void DaqifiDevice_Disconnect_ShouldNotReportLostStatus()
    {
        // Arrange
        using var transport = new MockStreamTransport();
        using var device = new DaqifiDevice("Mock Device", transport);

        device.Connect();
        Assert.Equal(ConnectionStatus.Connected, device.Status);

        var statusChanges = new List<ConnectionStatus>();
        device.StatusChanged += (sender, args) => statusChanges.Add(args.Status);

        // Act - Intentional disconnect
        device.Disconnect();

        // Assert - Should go straight to Disconnected, never Lost
        Assert.DoesNotContain(ConnectionStatus.Lost, statusChanges);
        Assert.Contains(ConnectionStatus.Disconnected, statusChanges);
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
    }

    [Fact]
    public async Task DaqifiDevice_ExecuteTextCommand_WhenTransportDroppedSilently_ThrowsTransportNotConnectedException()
    {
        // Arrange — the device believes it is still Connected (no StatusChanged was fired),
        // but the underlying transport has silently dropped: the serial analog is a port closed
        // by an unplug or a DTR-triggered MCU reset mid-connect, which raises no status event.
        using var transport = new MockStreamTransport();
        using var device = new DaqifiDevice("Mock Device", transport);
        device.Connect();
        Assert.Equal(ConnectionStatus.Connected, device.Status);

        transport.SimulateSilentConnectionLoss();

        // Device still reports Connected (status-based), but the transport does not.
        Assert.Equal(ConnectionStatus.Connected, device.Status);
        Assert.False(transport.IsConnected);

        // Act & Assert — InitializeAsync drives ExecuteTextCommandCoreAsync, which must surface
        // the typed transport-disconnected exception (still an InvalidOperationException for
        // backward compatibility) rather than dereferencing Stream and leaking a raw framework
        // message (issue #238).
        var ex = await Assert.ThrowsAsync<TransportNotConnectedException>(
            () => device.InitializeAsync());
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Equal(DeviceState.Error, device.State);
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

    [Fact]
    public void DaqifiDevice_DisconnectThenConnect_SendsToCurrentTransportStream()
    {
        // Regression for the latent stale-stream bug surfaced by PR #200's
        // post-reconnect readiness probe: SerialStreamTransport.Stream returns
        // _serialPort.BaseStream, which is a fresh instance after a transport
        // Disconnect → Connect. Pre-fix, DaqifiDevice.Disconnect left
        // _messageProducer / _messageConsumer alive with references to the
        // PREVIOUS BaseStream, and Connect's "if (_messageProducer == null)"
        // guard skipped recreation — so any subsequent Send() wrote to the
        // disposed stream and silently no-op'd, leaving the text consumer
        // with zero bytes on the new stream. Fix nulls them in Disconnect so
        // Connect rebuilds them against the transport's current Stream.
        using var transport = new SwappingMockStreamTransport();
        using var device = new DaqifiDevice("Mock Device", transport);

        device.Connect();
        Thread.Sleep(50); // MessageProducer background thread spin-up
        device.Send(ScpiMessageProducer.GetDeviceInfo);
        Thread.Sleep(200); // Allow the producer to flush
        var firstStreamBytes = transport.CurrentStreamSnapshot();
        Assert.NotEmpty(firstStreamBytes);

        device.Disconnect();
        transport.RotateStream();
        device.Connect();
        Thread.Sleep(50);
        device.Send(ScpiMessageProducer.GetDeviceInfo);
        Thread.Sleep(200);

        // The send AFTER reconnect must land on the new stream — not on the
        // first (now-disposed) stream we captured above.
        var secondStreamBytes = transport.CurrentStreamSnapshot();
        Assert.NotEmpty(secondStreamBytes);
        Assert.NotSame(firstStreamBytes, secondStreamBytes);
    }

    // Mock transport that swaps to a fresh MemoryStream on RotateStream(),
    // mirroring the real SerialStreamTransport whose Stream property returns
    // _serialPort.BaseStream — a new instance after each Disconnect → Connect.
    private class SwappingMockStreamTransport : IStreamTransport
    {
        private MemoryStream _stream = new();
        private bool _isConnected;
        private bool _disposed;

        public Stream Stream => _disposed ? throw new ObjectDisposedException(nameof(SwappingMockStreamTransport)) : _stream;
        public bool IsConnected => _isConnected && !_disposed;
        public string ConnectionInfo => _disposed ? "Disposed" : (_isConnected ? "Swap: Connected" : "Swap: Disconnected");

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public void RotateStream()
        {
            var old = _stream;
            _stream = new MemoryStream();
            old.Dispose();
        }

        public byte[] CurrentStreamSnapshot() => _stream.ToArray();

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SwappingMockStreamTransport));
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