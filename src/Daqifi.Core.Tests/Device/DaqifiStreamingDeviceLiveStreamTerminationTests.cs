using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Issue #496: <see cref="DaqifiStreamingDevice.StreamSamplesAsync"/> never ended when the device
/// went away. The buffer behind it was completed only by the enumerator's own exit path, so a
/// consumer sitting in the documented <c>await foreach</c> idiom when the cable was pulled got no
/// samples, no exception and no loop exit — it waited forever on a device the rest of the library
/// had already reported as <see cref="ConnectionStatus.Lost"/>, still subscribed to every channel.
/// </summary>
/// <remarks>
/// These drive the whole path a real drop takes — transport watchdog, device status transition,
/// enumeration — through a transport that can report a drop on command. The collaborator's own
/// bookkeeping (which channels stay subscribed, what happens with several enumerations at once,
/// the dispose that transitions nowhere) is pinned directly in <c>LiveSampleStreamTests</c>.
/// </remarks>
public class DaqifiStreamingDeviceLiveStreamTerminationTests
{
    /// <summary>
    /// Upper bound on every await here. A regression in this fix is precisely "the await never
    /// returns", so an unbounded wait would park the whole test run rather than fail it.
    /// </summary>
    private static readonly TimeSpan MoveNextTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AnUnplug_FaultsTheEnumeration_WithDeviceNotConnected()
    {
        using var transport = new DroppableTransport();
        using var device = CreateStreaming(transport);

        await using var e = device.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync(); // runs the body: registers and subscribes synchronously

        transport.SimulateDrop();

        // The whole point of the issue: this used to be an unbounded wait.
        var thrown = await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => moveNext.AsTask().WaitAsync(MoveNextTimeout));

        // A drop is not a shutdown the caller asked for, and the flag is how a consumer decides
        // whether to offer a reconnect.
        Assert.False(thrown.IsShuttingDown);
        Assert.Equal(ConnectionStatus.Lost, device.Status);
    }

    [Fact]
    public async Task AnUnplug_YieldsWhatWasAlreadyBuffered_BeforeItFaults()
    {
        using var transport = new DroppableTransport();
        using var device = CreateStreaming(transport);

        await using var e = device.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync();
        device.InvokeStreamMessage(AnalogFrame(1000, 1.5f));
        device.InvokeStreamMessage(AnalogFrame(1010, 2.5f));

        Assert.True(await moveNext.AsTask().WaitAsync(MoveNextTimeout));
        Assert.Equal(1.5, e.Current.Sample.Value);

        transport.SimulateDrop();

        // Samples decoded before the drop are real measurements; reporting the drop must not
        // throw them away.
        Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(MoveNextTimeout));
        Assert.Equal(2.5, e.Current.Sample.Value);

        await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => e.MoveNextAsync().AsTask().WaitAsync(MoveNextTimeout));
    }

    [Fact]
    public async Task ADisconnect_EndsTheEnumeration_WithoutThrowing()
    {
        using var transport = new DroppableTransport();
        using var device = CreateStreaming(transport);

        await using var e = device.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync();

        device.Disconnect();

        // A teardown the caller asked for is the ordinary end of a session, not a failure —
        // making it throw would mean every clean shutdown ended in an exception.
        Assert.False(await moveNext.AsTask().WaitAsync(MoveNextTimeout));
    }

    [Fact]
    public async Task AnAsyncDisconnect_EndsTheEnumeration_WithoutThrowing()
    {
        using var transport = new DroppableTransport();
        using var device = CreateStreaming(transport);

        await using var e = device.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync();

        await device.DisconnectAsync();

        Assert.False(await moveNext.AsTask().WaitAsync(MoveNextTimeout));
    }

    [Fact]
    public async Task ADispose_EndsTheEnumeration_WithoutThrowing()
    {
        using var transport = new DroppableTransport();
        var device = CreateStreaming(transport);

        var e = device.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync();

        // `await using var device` cannot rescue a parked loop — DisposeAsync only runs once the
        // loop exits — so the loop has to end when something else disposes the device.
        await device.DisposeAsync();

        Assert.False(await moveNext.AsTask().WaitAsync(MoveNextTimeout));
        await e.DisposeAsync();
    }

    [Fact]
    public async Task StartingOnADeviceThatIsNotConnected_ThrowsRatherThanWaiting()
    {
        using var transport = new DroppableTransport();
        using var device = new LiveStreamDevice("Never Connected", transport);

        var thrown = await Assert.ThrowsAsync<DeviceNotConnectedException>(
            async () =>
            {
                await foreach (var _ in device.StreamSamplesAsync(CancellationToken.None))
                {
                }
            });

        Assert.False(thrown.IsShuttingDown);
    }

    [Fact]
    public async Task StartingOnADisposedDevice_ThrowsWithIsShuttingDown()
    {
        using var transport = new DroppableTransport();
        var device = CreateStreaming(transport);
        var enumerable = device.StreamSamplesAsync(CancellationToken.None);

        device.Dispose();

        var thrown = await Assert.ThrowsAsync<DeviceNotConnectedException>(
            async () =>
            {
                await foreach (var _ in enumerable)
                {
                }
            });

        Assert.True(thrown.IsShuttingDown);
    }

    [Fact]
    public async Task AfterADrop_ANewEnumerationOnTheRestoredSessionWorks()
    {
        using var transport = new DroppableTransport();
        using var device = CreateStreaming(transport);

        await using (var dropped = device.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator())
        {
            var moveNext = dropped.MoveNextAsync();
            transport.SimulateDrop();
            await Assert.ThrowsAsync<DeviceNotConnectedException>(
                () => moveNext.AsTask().WaitAsync(MoveNextTimeout));
        }

        // The documented way back after a drop — including one an automatic reconnect repaired:
        // the old enumeration is over, and the restored session gets a new one. Nothing about
        // the drop leaves the device unable to serve it.
        device.Connect();
        device.PopulateChannelsFromStatus(StatusFrame(analogCount: 1));
        AnalogChannel(device, 0).IsEnabled = true;
        device.StartStreaming();

        await using var resumed = device.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
        var next = resumed.MoveNextAsync();
        device.InvokeStreamMessage(AnalogFrame(2000, 3.5f));

        Assert.True(await next.AsTask().WaitAsync(MoveNextTimeout));
        Assert.Equal(3.5, resumed.Current.Sample.Value);
    }

    [Fact]
    public async Task ADropDoesNotDisturbACancellingConsumer()
    {
        // Cancellation still wins its own race: it is the consumer's own exit, and it must keep
        // surfacing as OperationCanceledException rather than as the drop's typed exception.
        using var transport = new DroppableTransport();
        using var device = CreateStreaming(transport);

        using var cts = new CancellationTokenSource();
        var e = device.StreamSamplesAsync(cts.Token).GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => moveNext.AsTask().WaitAsync(MoveNextTimeout));
        await e.DisposeAsync();
    }

    [Fact]
    public void AThrowingStatusHook_DoesNotAbortTheTransition()
    {
        // The hooks this fix added sit on the drop path, where issue #494 established that an
        // escaping exception skips the reconnect start and unwinds into the transport before it
        // has released the port. That rule cannot be weaker for the library's own code than it
        // is for a consumer's handler.
        using var transport = new DroppableTransport();
        using var device = new ThrowingHookDevice("Badly Wired Device", transport);

        var seen = new List<ConnectionStatus>();
        var errors = new List<DeviceErrorEventArgs>();
        device.StatusChanged += (_, e) => seen.Add(e.Status);
        device.ErrorOccurred += (_, e) => errors.Add(e);

        var escaped = Record.Exception(() => device.Connect());

        Assert.Null(escaped);
        Assert.Equal(ConnectionStatus.Connected, device.Status);
        Assert.Contains(ConnectionStatus.Connected, seen);
        Assert.Contains(errors, e => e.Source == DeviceErrorSource.StatusNotification);
    }

    [Fact]
    public void AThrowingReleaseHook_DoesNotFailDispose()
    {
        using var transport = new DroppableTransport();
        var device = new ThrowingHookDevice("Badly Wired Device", transport);
        device.Connect();

        var escaped = Record.Exception(() => device.Dispose());

        // A Dispose that throws hides the handles it did release behind an exception nobody can
        // act on, so the library's own cleanup failure is reported instead.
        Assert.Null(escaped);
    }

    #region Helpers

    private static LiveStreamDevice CreateStreaming(IStreamTransport transport)
    {
        var device = new LiveStreamDevice("TestDevice", transport);
        device.Connect();
        device.PopulateChannelsFromStatus(StatusFrame(analogCount: 1));
        AnalogChannel(device, 0).IsEnabled = true;
        device.StartStreaming();
        return device;
    }

    private static DaqifiOutMessage StatusFrame(int analogCount)
    {
        var status = new DaqifiOutMessage
        {
            AnalogInPortNum = (uint)analogCount,
            DigitalPortNum = 0,
            AnalogInRes = 65535,
        };
        for (var i = 0; i < analogCount; i++)
        {
            status.AnalogInPortRange.Add(1.0f);
        }
        return status;
    }

    private static IAnalogChannel AnalogChannel(DaqifiStreamingDevice device, int number) =>
        (IAnalogChannel)device.Channels.First(c => c.Type == ChannelType.Analog && c.ChannelNumber == number);

    private static DaqifiOutMessage AnalogFrame(uint timestamp, float value)
    {
        var frame = new DaqifiOutMessage { MsgTimeStamp = timestamp };
        frame.AnalogInDataFloat.Add(value);
        return frame;
    }

    private sealed class LiveStreamDevice : DaqifiStreamingDevice
    {
        public LiveStreamDevice(string name, IStreamTransport transport) : base(name, transport) { }

        public void InvokeStreamMessage(DaqifiOutMessage message) => OnStreamMessageReceived(message);

        // The transport here exists to report a drop, not to carry traffic.
        public override void Send<T>(IOutboundMessage<T> message) { }
    }

    /// <summary>
    /// A device whose internal lifecycle hooks throw — the shape of a defect inside Core, which
    /// still must not be able to break a status transition or a dispose.
    /// </summary>
    private sealed class ThrowingHookDevice : DaqifiStreamingDevice
    {
        public ThrowingHookDevice(string name, IStreamTransport transport) : base(name, transport) { }

        public override void Send<T>(IOutboundMessage<T> message) { }

        internal override void OnConnectionStatusChanged(ConnectionStatus status) =>
            throw new InvalidOperationException("a badly wired internal status hook");

        internal override void ReleaseDerivedResources() =>
            throw new InvalidOperationException("a badly wired internal release hook");
    }

    /// <summary>
    /// A transport over an in-memory stream that can report an unexpected drop on command, which
    /// is all these tests need from it. Mirrors the one in
    /// <c>DeviceStatusChangedIsolationTests</c>; kept local so neither test file's fake can be
    /// bent to suit the other.
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

    #endregion
}
