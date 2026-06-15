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

        // Act & Assert - ThrowsAny (assignability), mirroring how a consumer's
        // catch (InvalidOperationException) still catches the now-derived typed exception.
        Assert.ThrowsAny<InvalidOperationException>(() => transport.Stream);
    }

    [Fact]
    public void SerialStreamTransport_Stream_WhenNotConnected_ThrowsTransportNotConnectedException()
    {
        // Arrange - never connected: _serialPort is null
        using var transport = new SerialStreamTransport("COM1");

        // Act & Assert - the typed exception, which is still an InvalidOperationException
        // so existing broad catches keep working.
        var ex = Assert.Throws<TransportNotConnectedException>(() => transport.Stream);
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Contains("COM1", ex.Message);
    }

    [Fact]
    public void SerialStreamTransport_SetSerialPortForTesting_TakesOwnershipAndDisposesPreviousPort()
    {
        // The seam documents that the transport takes ownership: a previously held port is
        // disposed when replaced or cleared (qodo #240 review). Exercise every branch.
        using var transport = new SerialStreamTransport("COM1");
        var first = new DisposalTrackingSerialPort();
        var second = new DisposalTrackingSerialPort();

        transport.SetSerialPortForTesting(first);

        // Re-assigning the same instance is a no-op and must NOT dispose it.
        transport.SetSerialPortForTesting(first);
        Assert.False(first.IsDisposed);

        // Swapping in a different instance disposes the previous one.
        transport.SetSerialPortForTesting(second);
        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);

        // Clearing disposes the current one.
        transport.SetSerialPortForTesting(null);
        Assert.True(second.IsDisposed);
    }

    [Fact]
    public void SerialStreamTransport_Stream_WhenPortClosedMidOperation_ThrowsTypedException_NotRawBaseStreamMessage()
    {
        // Arrange - simulate the issue #238 scenario: the port is non-null but closed
        // (device unplugged, or a DTR-triggered MCU reset re-enumerated the COM port
        // mid-connect). A constructed-but-unopened SerialPort reports IsOpen == false and
        // its BaseStream getter throws the raw framework message we must NOT leak.
        using var transport = new SerialStreamTransport("COM1");
        transport.SetSerialPortForTesting(new SerialPort("COM1"));

        // IsConnected must reflect the closed-port state so callers can pre-check.
        Assert.False(transport.IsConnected);

        // Act & Assert - the guard surfaces the typed exception before BaseStream is touched.
        var ex = Assert.Throws<TransportNotConnectedException>(() => transport.Stream);

        // The message must name the transport state, not the raw framework message.
        Assert.DoesNotContain("BaseStream is only available", ex.Message);
        Assert.Contains("not connected", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// A <see cref="SerialPort"/> that records whether it has been disposed, so tests can assert
    /// the transport's ownership/disposal contract. Never opened.
    /// </summary>
    private sealed class DisposalTrackingSerialPort : SerialPort
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}