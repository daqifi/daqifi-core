using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Tests.TestSupport;
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
    public void SerialStreamTransport_ApplyOperationalTimeouts_BoundsBothDirections()
    {
        // #399: only ReadTimeout used to be lowered after open, so WriteTimeout kept the connect
        // timeout for the life of the port. SerialPort.Write takes no CancellationToken, so a
        // device that stops draining its receive buffer parks the write for that whole duration
        // with nothing able to interrupt it. Both directions must end up bounded and short.
        using var port = new SerialPort("COM1")
        {
            ReadTimeout = 30000,
            WriteTimeout = 30000
        };

        SerialStreamTransport.ApplyOperationalTimeouts(port);

        Assert.Equal(500, port.ReadTimeout);
        Assert.Equal(2000, port.WriteTimeout);
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
    public async Task SerialStreamTransport_ConnectAsync_WithMissingPort_ReportsPortNotFound()
    {
        // #424: SerialPort.Open reports a port that does not exist as "Access to the port '...' is
        // denied.", sending users after a permissions problem for what is almost always a typo or
        // a stale name (USB device nodes are renumbered across replugs).
        var portName = AbsentSerialPorts.Create();
        using var transport = new SerialStreamTransport(portName);

        var ex = await Assert.ThrowsAsync<SerialPortConnectException>(() => transport.ConnectAsync());

        Assert.Equal(SerialPortConnectFailure.NotFound, ex.Reason);
        Assert.Equal(portName, ex.PortName);
        Assert.Contains(portName, ex.Message);
        Assert.Contains("was not found", ex.Message);
        Assert.DoesNotContain("denied", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task SerialStreamTransport_ConnectAsync_WithMissingPort_KeepsThePlatformException()
    {
        // The translation adds a name for the failure; it must not throw the diagnosis away.
        using var transport = new SerialStreamTransport(AbsentSerialPorts.Create());

        var ex = await Assert.ThrowsAsync<SerialPortConnectException>(() => transport.ConnectAsync());

        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task SerialStreamTransport_ConnectAsync_WithMissingPort_StillCatchableAsIoException()
    {
        // The chosen base type: a caller bracketing a connect with catch (IOException) keeps working.
        using var transport = new SerialStreamTransport(AbsentSerialPorts.Create());

        var ex = await Assert.ThrowsAnyAsync<IOException>(() => transport.ConnectAsync());

        Assert.IsType<SerialPortConnectException>(ex);
    }

    [Fact]
    public async Task SerialStreamTransport_ConnectAsync_WithMissingPort_ReportsTheTypedErrorOnStatusChanged()
    {
        // The status event carries the same translated exception, so a subscriber classifying a
        // failed connect sees the reason rather than the platform's guess.
        using var transport = new SerialStreamTransport(AbsentSerialPorts.Create());
        Exception? reported = null;
        transport.StatusChanged += (_, e) => reported ??= e.Error;

        await Assert.ThrowsAsync<SerialPortConnectException>(() => transport.ConnectAsync());

        var typed = Assert.IsType<SerialPortConnectException>(reported);
        Assert.Equal(SerialPortConnectFailure.NotFound, typed.Reason);
    }

    [Fact]
    public async Task SerialStreamTransport_ConnectAsync_WhenThePresenceProbeThrows_DoesNotLeakIt()
    {
        // The production classification path calls SerialPort.GetPortNames(), which can throw on
        // some hosts. That must degrade the reason, never replace the connect failure with the
        // probe's own exception or escape as an unhandled error.
        using var transport = new SerialStreamTransport(AbsentSerialPorts.Create())
        {
            PortPresenceProbe = _ => throw new UnauthorizedAccessException("the probe could not run")
        };

        var ex = await Assert.ThrowsAsync<SerialPortConnectException>(() => transport.ConnectAsync());

        // The caller still gets the real open failure, with the platform exception preserved, and
        // no trace of the probe's failure anywhere in the chain.
        Assert.NotNull(ex.InnerException);
        Assert.DoesNotContain("the probe could not run", ex.ToString());
    }

    [Fact]
    public async Task SerialStreamTransport_ConnectAsync_WithMissingPort_StillHonorsRetryPolicy()
    {
        // Translation happens inside a connect attempt, so it is still just a failed attempt: the
        // retry loop runs the configured number of times and surfaces the typed exception at the end.
        using var transport = new SerialStreamTransport(AbsentSerialPorts.Create());
        var options = new ConnectionRetryOptions
        {
            Enabled = true,
            MaxAttempts = 3,
            InitialDelay = TimeSpan.Zero,
            MaxDelay = TimeSpan.Zero
        };
        var failures = 0;
        transport.StatusChanged += (_, e) => { if (!e.IsConnected) failures++; };

        var ex = await Assert.ThrowsAsync<SerialPortConnectException>(
            () => transport.ConnectAsync(options));

        Assert.Equal(SerialPortConnectFailure.NotFound, ex.Reason);
        Assert.Equal(3, failures);
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