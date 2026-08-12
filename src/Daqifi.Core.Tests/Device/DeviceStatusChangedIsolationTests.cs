using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Issue #494: <see cref="DaqifiDevice.StatusChanged"/> is raised on whichever thread observed the
/// change, so a UI consumer touching a bound property from the handler throws a cross-thread
/// <see cref="InvalidOperationException"/> out of a library event. That exception used to escape the
/// status transition and take everything that runs after it with it — most damagingly the reconnect
/// start — and then vanish into a background loop's catch, leaving a dead device and no error.
/// </summary>
public class DeviceStatusChangedIsolationTests
{
    [Fact]
    public void AThrowingSubscriber_DoesNotEscapeConnect()
    {
        using var transport = new DroppableTransport();
        using var device = new DaqifiDevice("Badly Observed Device", transport);

        device.StatusChanged += (_, _) => throw new InvalidOperationException("the calling thread cannot access this object");

        var escaped = Record.Exception(() => device.Connect());

        Assert.Null(escaped);
        Assert.Equal(ConnectionStatus.Connected, device.Status);
        Assert.True(device.IsConnected);
    }

    [Fact]
    public void AThrowingSubscriber_DoesNotEscapeDisconnect()
    {
        using var transport = new DroppableTransport();
        using var device = new DaqifiDevice("Badly Observed Device", transport);

        device.Connect();
        device.StatusChanged += (_, _) => throw new InvalidOperationException("the calling thread cannot access this object");

        var escaped = Record.Exception(() => device.Disconnect());

        Assert.Null(escaped);
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
    }

    [Fact]
    public void AThrowingSubscriber_OnADrop_LeavesTheDeviceReportingLost()
    {
        using var transport = new DroppableTransport();
        using var device = new DaqifiDevice("Badly Observed Device", transport);

        device.Connect();
        device.StatusChanged += (_, _) => throw new InvalidOperationException("the calling thread cannot access this object");

        transport.SimulateDrop();

        Assert.Equal(ConnectionStatus.Lost, device.Status);
        Assert.False(device.IsConnected);
    }

    [Fact]
    public void AThrowingSubscriber_IsReportedOnErrorOccurred()
    {
        // The failure has to be visible somewhere. ErrorOccurred is the library's existing "a
        // background callback failed" surface (issue #378), and it also writes to the device logger.
        using var transport = new DroppableTransport();
        using var device = new DaqifiDevice("Badly Observed Device", transport);

        var thrown = new InvalidOperationException("the calling thread cannot access this object");
        var errors = new List<DeviceErrorEventArgs>();
        device.ErrorOccurred += (_, e) =>
        {
            lock (errors)
            {
                errors.Add(e);
            }
        };

        // Subscribed after the connect so the drop is the only transition the subscriber sees.
        device.Connect();
        device.StatusChanged += (_, _) => throw thrown;

        transport.SimulateDrop();

        lock (errors)
        {
            var error = Assert.Single(errors);
            Assert.Equal(DeviceErrorSource.StatusNotification, error.Source);
            Assert.Same(thrown, error.Error);
        }
    }

    [Fact]
    public void AThrowingErrorSubscriber_OnTopOfAThrowingStatusSubscriber_StillDoesNotEscape()
    {
        // The report of a failed notification must not itself become an escaping failure.
        using var transport = new DroppableTransport();
        using var device = new DaqifiDevice("Doubly Badly Observed Device", transport);

        device.StatusChanged += (_, _) => throw new InvalidOperationException("a badly behaved status subscriber");
        device.ErrorOccurred += (_, _) => throw new InvalidOperationException("a badly behaved error subscriber");

        var escaped = Record.Exception(() => device.Connect());

        Assert.Null(escaped);
        Assert.Equal(ConnectionStatus.Connected, device.Status);
    }

    /// <summary>
    /// A transport over an in-memory stream that can report an unexpected drop on command, which is
    /// all these tests need from it.
    /// </summary>
    private sealed class DroppableTransport : IStreamTransport
    {
        private readonly MemoryStream _stream = new();
        private bool _isConnected;
        private bool _disposed;

        public Stream Stream => _disposed
            ? throw new ObjectDisposedException(nameof(DroppableTransport))
            : _stream;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => IsConnected ? "Droppable: Connected" : "Droppable: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
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

        public void Connect() => ConnectAsync().GetAwaiter().GetResult();

        public void Disconnect() => DisconnectAsync().GetAwaiter().GetResult();

        /// <summary>Reports an unexpected drop, the way a watchdog would.</summary>
        public void SimulateDrop()
        {
            _isConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(
                false, ConnectionInfo, new TransportNotConnectedException("the device went away")));
        }

        public void Dispose()
        {
            _isConnected = false;
            _disposed = true;
            _stream.Dispose();
        }
    }
}
