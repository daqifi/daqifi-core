using Daqifi.Core.Communication.Transport;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Daqifi.Core.Tests.Communication.Transport;

public class TcpStreamTransportTests
{
    [Fact]
    public void TcpStreamTransport_Constructor_WithIPAddress_ShouldInitializeCorrectly()
    {
        // Arrange & Act
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 5000);
        
        // Assert
        Assert.False(transport.IsConnected);
        Assert.Contains("5000", transport.ConnectionInfo);
        Assert.Contains("Disconnected", transport.ConnectionInfo);
    }

    [Fact]
    public void TcpStreamTransport_Constructor_WithHostname_ShouldInitializeCorrectly()
    {
        // Arrange & Act
        using var transport = new TcpStreamTransport("localhost", 5000);
        
        // Assert
        Assert.False(transport.IsConnected);
        Assert.Equal("localhost", transport.Hostname);
        Assert.Contains("5000", transport.ConnectionInfo);
    }

    [Fact]
    public void TcpStreamTransport_Stream_WhenNotConnected_ThrowsTransportNotConnectedException()
    {
        // Arrange
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 5000);

        // Act & Assert - the typed exception (uniform with the serial transport), which is
        // still an InvalidOperationException so existing broad catches keep working.
        var ex = Assert.Throws<TransportNotConnectedException>(() => transport.Stream);
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    [Fact]
    public void TcpStreamTransport_Connect_WithClosedPort_ShouldThrowException()
    {
        // Arrange - Use localhost port 1 (reserved, never listening)
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 1);
        
        // Act & Assert
        Assert.Throws<SocketException>(() => transport.Connect());
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task TcpStreamTransport_ConnectAsync_WithClosedPort_ShouldThrowException()
    {
        // Arrange - Use localhost port 1 (reserved, never listening)
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 1);
        
        // Act & Assert
        await Assert.ThrowsAsync<SocketException>(() => transport.ConnectAsync());
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task TcpStreamTransport_DisconnectAsync_WhenNotConnected_ShouldNotThrow()
    {
        // Arrange
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 5000);
        
        // Act & Assert - Should not throw
        await transport.DisconnectAsync();
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public void TcpStreamTransport_StatusChanged_ShouldFireOnConnectionFailure()
    {
        // Arrange - Use localhost port 1 (reserved, never listening)
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 1);
        TransportStatusEventArgs? capturedArgs = null;
        
        transport.StatusChanged += (sender, args) => capturedArgs = args;
        
        // Act
        try
        {
            transport.Connect();
        }
        catch (SocketException)
        {
            // Expected
        }
        
        // Assert
        Assert.NotNull(capturedArgs);
        Assert.False(capturedArgs.IsConnected);
        Assert.NotNull(capturedArgs.Error);
        Assert.IsType<SocketException>(capturedArgs.Error);
    }

    [Fact]
    public void TcpStreamTransport_Dispose_ShouldCleanupResources()
    {
        // Arrange
        var transport = new TcpStreamTransport(IPAddress.Loopback, 5000);
        
        // Act
        transport.Dispose();
        
        // Assert - Should throw ObjectDisposedException for operations after disposal
        Assert.Throws<ObjectDisposedException>(() => transport.Connect());
        Assert.Throws<ObjectDisposedException>(() => transport.Stream);
    }

    [Fact]
    public void TcpStreamTransport_ConnectionInfo_ShouldReflectCurrentState()
    {
        // Arrange
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 5000);
        
        // Act & Assert - Disconnected state
        var disconnectedInfo = transport.ConnectionInfo;
        Assert.Contains("Disconnected", disconnectedInfo);
        Assert.Contains("127.0.0.1:5000", disconnectedInfo);
    }

    [Fact]
    public void TcpStreamTransport_Constructor_WithLocalInterface_ExposesIt()
    {
        // Arrange & Act
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 5000, IPAddress.Loopback);

        // Assert
        Assert.Equal(IPAddress.Loopback, transport.LocalInterface);
    }

    [Fact]
    public void TcpStreamTransport_Constructor_WithoutLocalInterface_LocalInterfaceIsNull()
    {
        // Arrange & Act
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 5000);

        // Assert
        Assert.Null(transport.LocalInterface);
    }

    [Fact]
    public async Task TcpStreamTransport_ConnectAsync_WithUnassignedLocalInterface_ThrowsSocketException()
    {
        // Arrange - 192.0.2.1 is in TEST-NET-1 (RFC 5737) and is not assigned to any local
        // interface, so binding the outbound socket to it must fail with EADDRNOTAVAIL.
        // This proves the local-interface argument actually drives the socket bind.
        var bogusLocal = IPAddress.Parse("192.0.2.1");
        using var transport = new TcpStreamTransport(IPAddress.Loopback, 1, bogusLocal);

        // Act & Assert
        await Assert.ThrowsAsync<SocketException>(() => transport.ConnectAsync());
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task TcpStreamTransport_ConnectAsync_WithLoopbackLocalInterface_ConnectsAndReportsBoundLocal()
    {
        // Arrange - real listener on loopback so the connection actually completes
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var transport = new TcpStreamTransport(IPAddress.Loopback, port, IPAddress.Loopback);

            // Act
            await transport.ConnectAsync();

            // Assert
            Assert.True(transport.IsConnected);
            Assert.Contains("127.0.0.1", transport.ConnectionInfo);
            await transport.DisconnectAsync();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TcpStreamTransport_Disconnect_AfterThePeerResetTheSocket_ReleasesIt()
    {
        // An I/O failure on a reset connection leaves the socket open but reporting
        // Connected == false, and the watchdog needs several faults before it tears it down
        // itself. Disconnect used to return early on !IsConnected in that window: no
        // StatusChanged, the watchdog disarmed, and the socket held until the next connect
        // overwrote it.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var transport = new TcpStreamTransport(IPAddress.Loopback, port);
            await transport.ConnectAsync();
            await BreakTheConnectionFromThePeerAsync(listener, transport);
            var reported = new List<bool>();
            transport.StatusChanged += (_, e) => reported.Add(e.IsConnected);

            await transport.DisconnectAsync();

            Assert.Equal(new[] { false }, reported);
            Assert.Throws<TransportNotConnectedException>(() => transport.Stream);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TcpStreamTransport_Reconnect_OverADeadSession_IsLeftAloneByALateLossForTheOldOne()
    {
        // Reconnecting straight over a dead session (no Disconnect in between): the old socket is
        // released rather than overwritten, and a loss the old session's detector delivers after
        // the replacement does not touch the new connection.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var transport = new TcpStreamTransport(IPAddress.Loopback, port);
            await transport.ConnectAsync();
            var oldSession = transport.Stream;
            await BreakTheConnectionFromThePeerAsync(listener, transport);

            await transport.ConnectAsync();
            var lost = new List<Exception?>();
            transport.StatusChanged += (_, e) =>
            {
                if (!e.IsConnected) lost.Add(e.Error);
            };

            transport.HandleConnectionLost(new IOException("old session, late"), oldSession);

            Assert.True(transport.IsConnected);
            Assert.NotSame(oldSession, transport.Stream);
            Assert.Empty(lost);
            Assert.Throws<ObjectDisposedException>(() => oldSession.Write(new byte[1], 0, 1));
            await transport.DisconnectAsync();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TcpStreamTransport_ConnectedSubscriberThatDisconnectsAtOnce_LeavesConnectAsyncSucceeding()
    {
        // A StatusChanged(true) subscriber is free to disconnect synchronously. Arming drop
        // detection after that notification found the stream already gone and threw out of the
        // ConnectAsync that had succeeded.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var transport = new TcpStreamTransport(IPAddress.Loopback, port);
            transport.StatusChanged += (_, e) =>
            {
                if (e.IsConnected) transport.Disconnect();
            };

            await transport.ConnectAsync();

            Assert.False(transport.IsConnected);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TcpStreamTransport_AConnectFailedByAThrowingConnectedSubscriber_LeavesNoDetectionArmed()
    {
        // A StatusChanged(true) subscriber that throws fails the attempt after it armed drop
        // detection for its stream; the failed attempt's detection must go with its socket.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var transport = new TcpStreamTransport(IPAddress.Loopback, port);
            transport.StatusChanged += (_, e) =>
            {
                if (e.IsConnected) throw new InvalidOperationException("subscriber failed");
            };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => transport.ConnectAsync(ConnectionRetryOptions.NoRetry));

            Assert.False(transport.IsConnected);
            Assert.False(transport.IsDropDetectionArmed);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Has the peer close the connection, then writes until the stack reports the reset, which
    /// leaves the client socket held but reporting <c>Connected == false</c>. Writing rather than
    /// reading makes the failure arrive the same way on every platform.
    /// </summary>
    private static async Task BreakTheConnectionFromThePeerAsync(TcpListener listener, TcpStreamTransport transport)
    {
        using (await listener.AcceptTcpClientAsync())
        {
        }

        var stream = transport.Stream;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            try
            {
                stream.Write(new byte[] { 0x0A }, 0, 1);
            }
            catch (IOException)
            {
                Assert.False(transport.IsConnected);
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("The peer's close never surfaced as a failed write.");
    }

    [Fact]
    public async Task TcpStreamTransport_ConnectAsync_LowersReceiveTimeoutToOperationalValue()
    {
        // Regression for issue #383: leaving the socket at the (multi-second) connection timeout
        // meant a consumer thread parked in a blocking read could not be joined inside the
        // consumer-swap window, so the restart hit the double-reader guard and a connect failed
        // with "a previous consumer thread has not yet exited". The serial transport already
        // lowers its ReadTimeout after opening; TCP must do the same.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var transport = new TcpStreamTransport(IPAddress.Loopback, port);
            var options = new ConnectionRetryOptions
            {
                Enabled = false,
                MaxAttempts = 1,
                ConnectionTimeout = TimeSpan.FromSeconds(10)
            };

            await transport.ConnectAsync(options);

            Assert.True(transport.IsConnected);
            Assert.Equal(TcpStreamTransport.OperationalReceiveTimeoutMs, transport.Stream.ReadTimeout);

            // The short timeout must actually bound a blocking read of an idle connection, which
            // is what lets StopSafely's Join succeed.
            var stopwatch = Stopwatch.StartNew();
            Assert.Throws<IOException>(() => transport.Stream.Read(new byte[16], 0, 16));
            stopwatch.Stop();
            Assert.True(
                stopwatch.ElapsedMilliseconds < 5000,
                $"Read should have expired near the operational timeout, took {stopwatch.ElapsedMilliseconds}ms.");

            await transport.DisconnectAsync();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TcpStreamTransport_ConnectAsync_WhenConnectTimesOut_ThrowsTimeoutException()
    {
        // Arrange - substitute a never-completing connect task (internal test seam), so the
        // configured timeout is deterministically what fails the attempt — no dependency on how
        // the host's network stack treats an unroutable address. The timeout used to surface as
        // TaskCanceledException (the WaitAsync cancellation token), which read like an app bug
        // to consumers (daqifi-desktop#517) — it must be a TimeoutException.
        using var transport = new TcpStreamTransport(IPAddress.Parse("192.0.2.1"), 9760);
        transport.ConnectTaskFactory = _ => Task.Delay(Timeout.Infinite);
        TransportStatusEventArgs? capturedArgs = null;
        transport.StatusChanged += (sender, args) => capturedArgs = args;
        var options = new ConnectionRetryOptions
        {
            Enabled = false,
            MaxAttempts = 1,
            ConnectionTimeout = TimeSpan.FromMilliseconds(250)
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => transport.ConnectAsync(options));
        Assert.Contains("192.0.2.1:9760", ex.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
        Assert.False(transport.IsConnected);

        // The status event must carry the translated exception too.
        Assert.NotNull(capturedArgs);
        Assert.False(capturedArgs.IsConnected);
        Assert.IsType<TimeoutException>(capturedArgs.Error);
    }
}
