using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using System.Net;
using System.Net.Sockets;

namespace Daqifi.Core.Tests.Communication.Transport;

/// <summary>
/// Issue #382 for the TCP transport: an unexpected drop must raise <c>StatusChanged(false)</c> —
/// which is what makes <see cref="ConnectionStatus.Lost"/> reachable — while an intentional
/// disconnect must stay an ordinary disconnect. Driven end to end over a real loopback socket
/// with a real reader loop, so the whole feedback path is exercised.
/// </summary>
public class TcpStreamTransportDropDetectionTests
{
    [Fact]
    public void WhenThePeerClosesTheConnection_TransportReportsTheDrop()
    {
        using var listener = new LoopbackListener();
        using var transport = new TcpStreamTransport(IPAddress.Loopback, listener.Port);

        var drops = new List<TransportStatusEventArgs>();
        var dropped = new ManualResetEventSlim(false);
        transport.StatusChanged += (_, e) =>
        {
            if (e.IsConnected)
            {
                return;
            }

            lock (drops)
            {
                drops.Add(e);
            }

            dropped.Set();
        };

        transport.Connect();
        using var server = listener.AcceptOne();

        using var consumer = new StreamMessageConsumer<string>(
            transport.Stream, new LineBasedMessageParser(), healthSink: transport);
        consumer.Start();

        // The peer goes away. The reader loop is the only thing that can notice.
        server.Close();

        Assert.True(dropped.Wait(TimeSpan.FromSeconds(10)), "the transport never reported the drop");
        Assert.False(transport.IsConnected);

        TransportStatusEventArgs drop;
        lock (drops)
        {
            drop = Assert.Single(drops);
        }

        Assert.IsType<TransportNotConnectedException>(drop.Error);

        consumer.StopSafely(timeoutMs: 2000);
    }

    [Fact]
    public void WhenDisconnectedIntentionally_TransportReportsAnOrdinaryDisconnect()
    {
        using var listener = new LoopbackListener();
        using var transport = new TcpStreamTransport(IPAddress.Loopback, listener.Port);

        var drops = new List<TransportStatusEventArgs>();
        transport.StatusChanged += (_, e) =>
        {
            if (e.IsConnected)
            {
                return;
            }

            lock (drops)
            {
                drops.Add(e);
            }
        };

        transport.Connect();
        using var server = listener.AcceptOne();

        using var consumer = new StreamMessageConsumer<string>(
            transport.Stream, new LineBasedMessageParser(), healthSink: transport);
        consumer.Start();

        transport.Disconnect();

        // Tearing down the socket makes the reader loop fail repeatedly; none of that may be
        // reported as a drop on top of the disconnect the caller asked for.
        Thread.Sleep(500);
        consumer.StopSafely(timeoutMs: 2000);

        lock (drops)
        {
            var status = Assert.Single(drops);
            Assert.Null(status.Error);
        }
    }

    [Fact]
    public void DeviceOverTcp_WhenThePeerDisappears_ReportsConnectionLost()
    {
        // The acceptance criterion end to end: a real transport, a real device, an unexpected
        // drop, and ConnectionStatus.Lost coming out the other side.
        using var listener = new LoopbackListener();
        var transport = new TcpStreamTransport(IPAddress.Loopback, listener.Port);
        using var device = new DaqifiDevice("Loopback Device", transport);

        var lost = new ManualResetEventSlim(false);
        var statuses = new List<ConnectionStatus>();
        device.StatusChanged += (_, e) =>
        {
            lock (statuses)
            {
                statuses.Add(e.Status);
            }

            if (e.Status == ConnectionStatus.Lost)
            {
                lost.Set();
            }
        };

        device.Connect();
        using var server = listener.AcceptOne();
        Assert.Equal(ConnectionStatus.Connected, device.Status);

        server.Close();

        Assert.True(lost.Wait(TimeSpan.FromSeconds(10)), "the device never reported ConnectionStatus.Lost");
        Assert.Equal(ConnectionStatus.Lost, device.Status);
        Assert.False(device.IsConnected);
    }

    [Fact]
    public void DeviceOverTcp_WhenDisconnectedIntentionally_ReportsDisconnectedNotLost()
    {
        using var listener = new LoopbackListener();
        var transport = new TcpStreamTransport(IPAddress.Loopback, listener.Port);
        using var device = new DaqifiDevice("Loopback Device", transport);

        device.Connect();
        using var server = listener.AcceptOne();
        Assert.Equal(ConnectionStatus.Connected, device.Status);

        var statuses = new List<ConnectionStatus>();
        device.StatusChanged += (_, e) =>
        {
            lock (statuses)
            {
                statuses.Add(e.Status);
            }
        };

        device.Disconnect();

        // Give any lingering reader-loop failure a chance to be (wrongly) escalated.
        Thread.Sleep(500);

        lock (statuses)
        {
            Assert.DoesNotContain(ConnectionStatus.Lost, statuses);
            Assert.Contains(ConnectionStatus.Disconnected, statuses);
        }

        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
    }

    /// <summary>
    /// A loopback TCP listener on an OS-assigned port, so these tests never collide with each
    /// other or with anything else on the machine.
    /// </summary>
    private sealed class LoopbackListener : IDisposable
    {
        private readonly TcpListener _listener;

        public LoopbackListener()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public TcpClient AcceptOne() => _listener.AcceptTcpClient();

        public void Dispose() => _listener.Stop();
    }
}
