using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Tests.TestSupport;
using Google.Protobuf;
using System.Reflection;
using Xunit;
using TestableDaqifiDevice = Daqifi.Core.Tests.Device.DaqifiDeviceInitializeTests.TestableDaqifiDevice;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Issue #931: a device has one state. After a lost connection, a connect has to give a device
/// that operates, and a connect from an event handler has to leave Status, State and IsConnected
/// telling the same story. Automatic reconnection is off in every test here.
/// </summary>
public class DaqifiDeviceLifecycleStateTests
{
    private static readonly IOutboundMessage<string> ProbeCommand = ScpiMessageProducer.GetDeviceInfo;

    #region A connect after a lost connection

    [Fact]
    public async Task ALostConnection_ReportsTheSameStateFromStatusAndState()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();
        await device.InitializeAsync();
        Assert.Equal(DeviceState.Ready, device.State);

        transport.SimulateDrop();

        Assert.Equal(ConnectionStatus.Lost, device.Status);
        Assert.Equal(DeviceState.Disconnected, device.State);
        Assert.False(device.IsConnected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterALostConnection_Connect_TheProducerWritesToTheCurrentStream(bool useAsync)
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();
        await device.InitializeAsync();
        var lostStream = transport.CurrentStream;

        transport.SimulateDrop();
        await ConnectAsync(device, useAsync);

        Assert.NotSame(lostStream, transport.CurrentStream);
        var writtenToLostStream = lostStream.WrittenByteCount;

        device.SendThroughProducer = true;
        device.Send(ProbeCommand);

        WaitUntil.That(
            () => transport.CurrentStream.WrittenByteCount > 0,
            "the command never reached the stream the transport is connected on");
        Assert.Equal(writtenToLostStream, lostStream.WrittenByteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterALostConnection_Connect_TheConsumerReadsTheCurrentStream(bool useAsync)
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();
        await device.InitializeAsync();
        var lostStream = transport.CurrentStream;

        transport.SimulateDrop();
        await ConnectAsync(device, useAsync);
        await device.InitializeAsync();

        using var received = new ManualResetEventSlim(false);
        const string marker = "from-the-current-stream";
        device.StatusMessageReceived += message =>
        {
            if (message.DevicePn == marker)
            {
                received.Set();
            }
        };

        transport.CurrentStream.Deliver(new DaqifiOutMessage
        {
            DevicePn = marker,
            AnalogInPortNum = 2,
            DigitalPortNum = 2
        });

        Assert.True(
            received.Wait(WaitUntil.DefaultTimeout),
            "a frame on the stream the transport is connected on never reached the device");

        // The lost stream's reader is gone rather than idling alongside the new one.
        var readsBefore = lostStream.ReadCount;
        WaitUntil.HoldsFor(
            () => lostStream.ReadCount == readsBefore,
            "the reader of the lost stream is still running",
            TimeSpan.FromMilliseconds(200));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterALostConnection_Connect_BuildsNewPumpsRatherThanRestartingTheOldOnes(bool useAsync)
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();
        var lostProducer = PrivateField(device, "_messageProducer");
        var lostConsumer = PrivateField(device, "_messageConsumer");
        Assert.NotNull(lostProducer);
        Assert.NotNull(lostConsumer);

        transport.SimulateDrop();
        await ConnectAsync(device, useAsync);

        Assert.NotSame(lostProducer, PrivateField(device, "_messageProducer"));
        Assert.NotSame(lostConsumer, PrivateField(device, "_messageConsumer"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterALostConnection_ConnectAndInitialize_ReachesReady(bool useAsync)
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();
        await device.InitializeAsync();
        var attemptsBeforeDrop = device.TextCommandAttemptCount;

        transport.SimulateDrop();
        await ConnectAsync(device, useAsync);
        Assert.Equal(ConnectionStatus.Connected, device.Status);
        Assert.Equal(DeviceState.Connected, device.State);

        await device.InitializeAsync();

        Assert.Equal(ConnectionStatus.Connected, device.Status);
        Assert.Equal(DeviceState.Ready, device.State);
        Assert.Equal(attemptsBeforeDrop + 1, device.TextCommandAttemptCount);
    }

    [Fact]
    public async Task AfterALostConnection_AConnectThatFails_LeavesTheDeviceDisconnectedAndAbleToRetry()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();
        await device.InitializeAsync();

        transport.SimulateDrop();
        transport.FailNextConnects = 1;

        Assert.Throws<IOException>(() => device.Connect());
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
        Assert.Equal(DeviceState.Disconnected, device.State);

        device.Connect();
        await device.InitializeAsync();

        Assert.Equal(DeviceState.Ready, device.State);
    }

    #endregion

    #region A connect or disconnect from an event handler

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConnectFromADisconnectedHandler_LeavesOneStateAndInitializes(bool useAsync)
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();
        await device.InitializeAsync();
        var attemptsBefore = device.TextCommandAttemptCount;

        var reconnected = false;
        device.StatusChanged += (_, e) =>
        {
            if (e.Status == ConnectionStatus.Disconnected && !reconnected)
            {
                reconnected = true;
                device.Connect();
            }
        };

        if (useAsync)
        {
            await device.DisconnectAsync();
        }
        else
        {
            device.Disconnect();
        }

        Assert.True(reconnected);
        Assert.Equal(ConnectionStatus.Connected, device.Status);
        Assert.Equal(DeviceState.Connected, device.State);
        Assert.True(device.IsConnected);
        Assert.True(transport.IsConnected);

        await device.InitializeAsync();

        Assert.Equal(ConnectionStatus.Connected, device.Status);
        Assert.Equal(DeviceState.Ready, device.State);
        Assert.Equal(attemptsBefore + 1, device.TextCommandAttemptCount);
    }

    [Fact]
    public async Task AConnectAndInitializeInsideADisconnectedHandler_ReachesReady()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();

        var reconnected = false;
        device.StatusChanged += (_, e) =>
        {
            if (e.Status == ConnectionStatus.Disconnected && !reconnected)
            {
                reconnected = true;
                device.Connect();
                device.InitializeAsync().GetAwaiter().GetResult();
            }
        };

        device.Disconnect();

        Assert.Equal(ConnectionStatus.Connected, device.Status);
        Assert.Equal(DeviceState.Ready, device.State);

        // The teardown that raised Disconnected has to leave nothing behind that the session the
        // handler built would trip over: the operation lock in particular.
        await device.RunExclusiveAsync(_ => Task.CompletedTask).WaitAsync(WaitUntil.DefaultTimeout);
    }

    [Fact]
    public void ADisconnectFromAConnectedHandler_LeavesOneState()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.StatusChanged += (_, e) =>
        {
            if (e.Status == ConnectionStatus.Connected)
            {
                device.Disconnect();
            }
        };

        device.Connect();

        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
        Assert.Equal(DeviceState.Disconnected, device.State);
        Assert.False(device.IsConnected);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task AConnectFromALostHandler_GivesADeviceThatOperates()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();
        await device.InitializeAsync();

        device.StatusChanged += (_, e) =>
        {
            if (e.Status == ConnectionStatus.Lost)
            {
                device.Connect();
            }
        };

        transport.SimulateDrop();

        Assert.Equal(ConnectionStatus.Connected, device.Status);
        Assert.Equal(DeviceState.Connected, device.State);

        await device.InitializeAsync();
        Assert.Equal(DeviceState.Ready, device.State);

        device.SendThroughProducer = true;
        device.Send(ProbeCommand);
        WaitUntil.That(
            () => transport.CurrentStream.WrittenByteCount > 0,
            "the command never reached the stream the transport is connected on");
    }

    [Fact]
    public async Task EveryHandlerSeesOneState()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);

        var seen = new List<(ConnectionStatus Status, DeviceState State, bool IsConnected)>();
        device.StatusChanged += (_, _) => seen.Add((device.Status, device.State, device.IsConnected));

        device.Connect();
        await device.InitializeAsync();
        transport.SimulateDrop();
        device.Connect();
        device.Disconnect();

        Assert.Equal(
            new[]
            {
                (ConnectionStatus.Connecting, DeviceState.Connecting, false),
                (ConnectionStatus.Connected, DeviceState.Connected, true),
                (ConnectionStatus.Lost, DeviceState.Disconnected, false),
                (ConnectionStatus.Connecting, DeviceState.Connecting, false),
                (ConnectionStatus.Connected, DeviceState.Connected, true),
                (ConnectionStatus.Disconnected, DeviceState.Disconnected, false)
            },
            seen);
    }

    #endregion

    #region Automatic reconnection

    [Fact]
    public async Task AnAutomaticReconnect_ReachesReadyOverTheCurrentStream()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport)
        {
            ReconnectOptions = FastReconnect(maxAttempts: 4)
        };
        device.Connect();
        await device.InitializeAsync();

        var seen = new List<(ConnectionStatus Status, DeviceState State)>();
        device.StatusChanged += (_, _) =>
        {
            lock (seen)
            {
                seen.Add((device.Status, device.State));
            }
        };
        using var reconnected = new ManualResetEventSlim(false);
        device.Reconnected += (_, _) => reconnected.Set();

        transport.FailNextConnects = 1;
        transport.SimulateDrop();

        Assert.True(reconnected.Wait(WaitUntil.DefaultTimeout), "the reconnect never finished");
        Assert.Equal(ConnectionStatus.Connected, device.Status);
        Assert.Equal(DeviceState.Ready, device.State);

        lock (seen)
        {
            Assert.Contains((ConnectionStatus.Lost, DeviceState.Disconnected), seen);
            Assert.Contains((ConnectionStatus.Retrying, DeviceState.Disconnected), seen);
        }

        device.SendThroughProducer = true;
        device.Send(ProbeCommand);
        WaitUntil.That(
            () => transport.CurrentStream.WrittenByteCount > 0,
            "the command never reached the stream the transport is connected on");
    }

    [Fact]
    public async Task AnAutomaticReconnectThatGivesUp_ReadsFailedAndDisconnected_AndACallerCanStillConnect()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport)
        {
            ReconnectOptions = FastReconnect(maxAttempts: 2)
        };
        device.Connect();
        await device.InitializeAsync();

        using var gaveUp = new ManualResetEventSlim(false);
        device.ReconnectFailed += (_, _) => gaveUp.Set();

        transport.FailNextConnects = 2;
        transport.SimulateDrop();

        Assert.True(gaveUp.Wait(WaitUntil.DefaultTimeout), "the reconnect never gave up");
        Assert.Equal(ConnectionStatus.Failed, device.Status);
        Assert.Equal(DeviceState.Disconnected, device.State);

        device.Connect();
        await device.InitializeAsync();

        Assert.Equal(DeviceState.Ready, device.State);
    }

    private static ReconnectOptions FastReconnect(int maxAttempts) => new()
    {
        Enabled = true,
        MaxAttempts = maxAttempts,
        InitialDelay = TimeSpan.FromMilliseconds(10),
        MaxDelay = TimeSpan.FromMilliseconds(60),
        BackoffMultiplier = 2.0
    };

    #endregion

    #region A connect the device moved on from

    [Fact]
    public void ADisconnectFromAConnectingHandler_LeavesNothingRunning()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        var statuses = new List<ConnectionStatus>();
        device.StatusChanged += (_, e) =>
        {
            statuses.Add(e.Status);
            if (e.Status == ConnectionStatus.Connecting)
            {
                device.Disconnect();
            }
        };

        device.Connect();

        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
        Assert.Equal(DeviceState.Disconnected, device.State);
        Assert.False(transport.IsConnected);
        Assert.Null(PrivateField(device, "_messageProducer"));
        Assert.Null(PrivateField(device, "_messageConsumer"));
        Assert.DoesNotContain(ConnectionStatus.Connected, statuses);
    }

    #endregion

    #region Initialization that loses its session

    [Fact]
    public async Task AnInitializationOutlivedByADisconnect_DoesNotReportReady()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();

        // The status request is the last thing the wait for channels does before the device
        // answers, so the session goes away with the initialization still in flight.
        var disconnected = false;
        device.OnDeviceInfoRequested = () =>
        {
            if (!disconnected)
            {
                disconnected = true;
                device.Disconnect();
            }
        };

        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => device.InitializeAsync());

        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
        Assert.Equal(DeviceState.Disconnected, device.State);
    }

    [Fact]
    public async Task AnInitializationThatFailsAfterADrop_ReportsTheDrop()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();

        device.OnDeviceInfoRequested = () =>
        {
            transport.SimulateDrop();
            throw new IOException("the device went away");
        };

        await Assert.ThrowsAsync<IOException>(() => device.InitializeAsync());

        Assert.Equal(ConnectionStatus.Lost, device.Status);
        Assert.Equal(DeviceState.Disconnected, device.State);
    }

    #endregion

    #region A state value no code sets

    [Fact]
    public void DeviceStateStreaming_IsMarkedObsolete()
    {
        var member = typeof(DeviceState).GetMember("Streaming").Single();

        Assert.NotNull(member.GetCustomAttribute<ObsoleteAttribute>());
    }

    #endregion

    private static Task ConnectAsync(DaqifiDevice device, bool useAsync)
    {
        if (useAsync)
        {
            return device.ConnectAsync();
        }

        device.Connect();
        return Task.CompletedTask;
    }

    private static object? PrivateField(DaqifiDevice device, string name) =>
        typeof(DaqifiDevice)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(device);

    /// <summary>
    /// A transport that behaves as the serial and TCP transports do across a drop: it stops being
    /// connected, it has no stream until the next connect, and the next connect hands out a new
    /// stream. The old stream stays writable and readable, so a pump left on it is seen doing
    /// work rather than failing.
    /// </summary>
    private sealed class ReopeningTransport : IStreamTransport
    {
        private volatile LiveStream? _stream;

        /// <summary>The stream of the most recent connect, including after a drop.</summary>
        public LiveStream CurrentStream { get; private set; } = new();

        public int FailNextConnects { get; set; }

        public Stream Stream => _stream ?? throw new TransportNotConnectedException("Not connected.");

        public bool IsConnected => _stream != null;

        public string ConnectionInfo => IsConnected ? "Connected" : "Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public void SimulateDrop()
        {
            _stream = null;
            StatusChanged?.Invoke(
                this,
                new TransportStatusEventArgs(false, ConnectionInfo, new IOException("the device went away")));
        }

        public void Connect()
        {
            if (IsConnected)
            {
                return;
            }

            if (FailNextConnects > 0)
            {
                FailNextConnects--;
                throw new IOException("the device is not back yet");
            }

            CurrentStream = new LiveStream();
            _stream = CurrentStream;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
        }

        public void Disconnect()
        {
            if (!IsConnected)
            {
                return;
            }

            _stream = null;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
        }

        public Task ConnectAsync()
        {
            Connect();
            return Task.CompletedTask;
        }

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions) => ConnectAsync();

        public Task DisconnectAsync()
        {
            Disconnect();
            return Task.CompletedTask;
        }

        public void Dispose() => _stream = null;
    }

    /// <summary>Counts what the pumps do to it, and hands the consumer whatever it is given.</summary>
    private sealed class LiveStream : Stream
    {
        private readonly object _gate = new();
        private byte[] _inbound = Array.Empty<byte>();
        private int _readCount;
        private long _writtenByteCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public long WrittenByteCount => Interlocked.Read(ref _writtenByteCount);

        public void Deliver(IMessage message)
        {
            using var frame = new MemoryStream();
            message.WriteDelimitedTo(frame);

            lock (_gate)
            {
                _inbound = _inbound.Concat(frame.ToArray()).ToArray();
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref _readCount);

            lock (_gate)
            {
                if (_inbound.Length > 0)
                {
                    var taken = Math.Min(count, _inbound.Length);
                    Array.Copy(_inbound, 0, buffer, offset, taken);
                    _inbound = _inbound.Skip(taken).ToArray();
                    return taken;
                }
            }

            Thread.Sleep(5);
            return 0;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Interlocked.Add(ref _writtenByteCount, count);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
