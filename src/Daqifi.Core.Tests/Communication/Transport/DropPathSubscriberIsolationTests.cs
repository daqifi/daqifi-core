using Daqifi.Core.Communication.Transport;
using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

namespace Daqifi.Core.Tests.Communication.Transport;

/// <summary>
/// Issue #494: the drop path hands the connection handle to <c>Dispose</c> only after
/// <see cref="IStreamTransport.StatusChanged"/> has been raised. A subscriber that throws used to
/// unwind past that dispose — and because the field had already been nulled, a later
/// <c>Disconnect()</c>/<c>Dispose()</c> skipped it too, so the OS handle stayed claimed for the
/// life of the process (on serial, re-plugging the device then fails with "Access is denied").
/// </summary>
/// <remarks>
/// A WPF/WinForms consumer touching a bound property from the handler is the realistic source: the
/// event is documented as firing on a background thread, so a cross-thread
/// <see cref="InvalidOperationException"/> is the exception these tests use.
/// </remarks>
public class DropPathSubscriberIsolationTests
{
    [Fact]
    public void SerialTransport_WhenAStatusSubscriberThrowsOnAnIoFaultDrop_ThePortIsStillDisposed()
    {
        using var transport = new SerialStreamTransport("/dev/ttyTest494", livenessCheckInterval: TimeSpan.Zero);
        var port = new DisposalTrackingSerialPort();
        transport.SetSerialPortForTesting(port);

        transport.StatusChanged += (_, e) =>
        {
            if (!e.IsConnected)
            {
                throw new InvalidOperationException("the calling thread cannot access this object");
            }
        };

        transport.StartDropDetection();

        // The reader loop escalates an unbroken run of failures into a drop.
        for (var i = 0; i < TransportConnectionWatchdog.ConsecutiveFaultThreshold; i++)
        {
            transport.ReportIoFault(new IOException("device gone"));
        }

        Assert.True(port.IsDisposed, "the throwing subscriber leaked the serial port handle");
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public void SerialTransport_WhenAStatusSubscriberThrowsOnAPresenceDrop_ThePortIsStillDisposed()
    {
        // The unplug path proper: the presence poll notices the port is gone. It is also the path
        // where the subscriber exception is least visible — the watchdog's poll loop absorbs it —
        // so the handle release cannot be left depending on the subscriber behaving.
        var present = true;
        using var transport = new SerialStreamTransport("/dev/ttyTest494", livenessCheckInterval: TimeSpan.FromHours(1))
        {
            PortPresenceProbe = _ => Volatile.Read(ref present)
        };

        var port = new DisposalTrackingSerialPort();
        transport.SetSerialPortForTesting(port);

        transport.StatusChanged += (_, e) =>
        {
            if (!e.IsConnected)
            {
                throw new InvalidOperationException("the calling thread cannot access this object");
            }
        };

        transport.StartDropDetection();
        Assert.True(transport.IsLivenessMonitorActive);

        Volatile.Write(ref present, false);
        for (var i = 0; i < TransportConnectionWatchdog.PresenceMissThreshold; i++)
        {
            transport.PollLivenessForTesting();
        }

        Assert.True(port.IsDisposed, "the throwing subscriber leaked the serial port handle");
    }

    [Fact]
    public void SerialTransport_AThrowingStatusSubscriber_DoesNotEscapeIntoTheReportingLoop()
    {
        // ReportIoFault is called from the reader and writer loops. Rethrowing here would only put
        // the subscriber's exception somewhere those loops have to absorb it again, so the drop
        // path swallows it — the device-level StatusChanged, which is where real consumers
        // subscribe, is the surface that reports it (DaqifiDevice raises ErrorOccurred).
        using var transport = new SerialStreamTransport("/dev/ttyTest494", livenessCheckInterval: TimeSpan.Zero);
        transport.SetSerialPortForTesting(new DisposalTrackingSerialPort());
        transport.StatusChanged += (_, _) => throw new InvalidOperationException("a badly behaved subscriber");

        transport.StartDropDetection();

        var escaped = Record.Exception(() =>
        {
            for (var i = 0; i < TransportConnectionWatchdog.ConsecutiveFaultThreshold; i++)
            {
                transport.ReportIoFault(new IOException("device gone"));
            }
        });

        Assert.Null(escaped);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public void SerialTransport_AThrowingStatusSubscriber_IsTracedRatherThanSwallowedSilently()
    {
        // These transports carry no ILogger, so the best-effort trace DeviceFinderBase uses for the
        // same situation is the only diagnosability a bare-transport consumer gets. The listener is
        // synchronized and only asserted with Contains, so traffic from tests running in parallel
        // can neither corrupt it nor fail this.
        using var transport = new SerialStreamTransport("/dev/ttyTest494", livenessCheckInterval: TimeSpan.Zero);
        transport.SetSerialPortForTesting(new DisposalTrackingSerialPort());
        transport.StatusChanged += (_, _) => throw new InvalidOperationException("a badly behaved subscriber");

        var captured = new StringWriter();
        var listener = new TextWriterTraceListener(TextWriter.Synchronized(captured));
        Trace.Listeners.Add(listener);
        try
        {
            transport.StartDropDetection();

            for (var i = 0; i < TransportConnectionWatchdog.ConsecutiveFaultThreshold; i++)
            {
                transport.ReportIoFault(new IOException("device gone"));
            }

            Trace.Flush();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            listener.Dispose();
        }

        var traced = captured.ToString();
        Assert.Contains(nameof(SerialStreamTransport), traced);
        Assert.Contains("a badly behaved subscriber", traced);
    }

    [Fact]
    public void SerialTransport_WhenTheSubscribersExceptionCannotRenderItself_TheDropPathIsStillContained()
    {
        // The exception being traced came out of consumer code as surely as the subscriber did, so
        // rendering it is not a safe operation either. Composing the trace message outside the guard
        // let a throwing ToString() escape the catch that exists to contain the subscriber — the
        // handle still went out in the finally, but the watchdog/reader thread was disrupted anyway,
        // which is the very thing the isolation is for.
        using var transport = new SerialStreamTransport("/dev/ttyTest494", livenessCheckInterval: TimeSpan.Zero);
        var port = new DisposalTrackingSerialPort();
        transport.SetSerialPortForTesting(port);
        transport.StatusChanged += (_, _) => throw new UnrenderableException();

        transport.StartDropDetection();

        var escaped = Record.Exception(() =>
        {
            for (var i = 0; i < TransportConnectionWatchdog.ConsecutiveFaultThreshold; i++)
            {
                transport.ReportIoFault(new IOException("device gone"));
            }
        });

        Assert.Null(escaped);
        Assert.True(port.IsDisposed);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public void TcpTransport_WhenTheSubscribersExceptionCannotRenderItself_TheDropPathIsStillContained()
    {
        using var listener = new LoopbackListener();
        using var transport = new TcpStreamTransport(IPAddress.Loopback, listener.Port);
        transport.StatusChanged += (_, e) =>
        {
            if (!e.IsConnected)
            {
                throw new UnrenderableException();
            }
        };

        transport.Connect();
        using var server = listener.AcceptOne();

        var escaped = Record.Exception(() =>
        {
            for (var i = 0; i < TransportConnectionWatchdog.ConsecutiveFaultThreshold; i++)
            {
                transport.ReportIoFault(new IOException("peer gone"));
            }
        });

        Assert.Null(escaped);
        Assert.False(transport.IsConnected);
        AssertPeerObservedTheSocketClose(server);
    }

    [Fact]
    public void TcpTransport_WhenAStatusSubscriberThrows_TheSocketIsStillReleased()
    {
        using var listener = new LoopbackListener();
        using var transport = new TcpStreamTransport(IPAddress.Loopback, listener.Port);

        transport.StatusChanged += (_, e) =>
        {
            if (!e.IsConnected)
            {
                throw new InvalidOperationException("the calling thread cannot access this object");
            }
        };

        transport.Connect();
        using var server = listener.AcceptOne();

        for (var i = 0; i < TransportConnectionWatchdog.ConsecutiveFaultThreshold; i++)
        {
            transport.ReportIoFault(new IOException("peer gone"));
        }

        Assert.False(transport.IsConnected);
        AssertPeerObservedTheSocketClose(server);
    }

    /// <summary>
    /// Asserts the peer saw the client socket actually close, which is the proof the handle was
    /// released rather than merely dropped from its field.
    /// </summary>
    /// <remarks>
    /// A graceful close (the expected case here — nothing is ever sent to this client, so its
    /// receive buffer is empty at close) surfaces as a zero-byte read. A close that the stack turns
    /// into a reset instead is equally good proof, so it is accepted rather than left as a source of
    /// platform flakiness. What must not be accepted is a <see cref="SocketError.TimedOut"/>: that
    /// is precisely the symptom of the leak these tests exist to catch, since a socket still held
    /// open sends the peer neither a FIN nor an RST.
    /// </remarks>
    private static void AssertPeerObservedTheSocketClose(TcpClient server)
    {
        server.Client.ReceiveTimeout = 5000;

        int read;
        try
        {
            read = server.Client.Receive(new byte[1]);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            return;
        }

        Assert.Equal(0, read);
    }

    /// <summary>
    /// A loopback TCP listener on an OS-assigned port, so these tests never collide with each other
    /// or with anything else on the machine.
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

    /// <summary>
    /// An exception that throws while being rendered — the shape a consumer's own exception type can
    /// take when its <see cref="object.ToString"/> (or a property it reads) is itself buggy.
    /// </summary>
    private sealed class UnrenderableException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("cannot render");
    }

    /// <summary>
    /// A <see cref="SerialPort"/> that records whether it was disposed, so "the handle was
    /// released" is asserted directly rather than inferred.
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
