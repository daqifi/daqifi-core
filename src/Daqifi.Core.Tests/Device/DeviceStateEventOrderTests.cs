using System.Collections.Concurrent;
using Daqifi.Core.Device;
using Daqifi.Core.Tests.TestSupport;
using Xunit;
using ReopeningTransport = Daqifi.Core.Tests.Device.DaqifiDeviceLifecycleStateTests.ReopeningTransport;
using TestableDaqifiDevice = Daqifi.Core.Tests.Device.DaqifiDeviceInitializeTests.TestableDaqifiDevice;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Issue #1035: <see cref="IDevice.StateChanged"/> is raised for every change of
/// <see cref="IDevice.State"/>, with both values, in the order the changes were made, and with no
/// lock held.
/// </summary>
public class DeviceStateEventOrderTests
{
    #region One test per path through the lifecycle

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connect(bool useAsync)
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        var log = new StateLog(device);

        if (useAsync)
        {
            await device.ConnectAsync();
        }
        else
        {
            device.Connect();
        }

        log.AssertWalked(DeviceState.Disconnected, DeviceState.Connecting, DeviceState.Connected);
    }

    [Fact]
    public void ConnectFailure()
    {
        using var transport = new ReopeningTransport { FailNextConnects = 1 };
        using var device = new TestableDaqifiDevice("Device", transport);
        var log = new StateLog(device);

        Assert.Throws<IOException>(() => device.Connect());

        log.AssertWalked(DeviceState.Disconnected, DeviceState.Connecting, DeviceState.Disconnected);
    }

    [Fact]
    public async Task InitializeSuccess()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();
        var log = new StateLog(device);

        await device.InitializeAsync();

        log.AssertWalked(DeviceState.Connected, DeviceState.Initializing, DeviceState.Ready);
    }

    [Fact]
    public async Task InitializeFailure_ThenARetryThatSucceeds()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();
        var log = new StateLog(device);

        device.OnDeviceInfoRequested = () => throw new IOException("no answer");
        await Assert.ThrowsAsync<IOException>(() => device.InitializeAsync());
        Assert.True(device.IsConnected);

        device.OnDeviceInfoRequested = null;
        await device.InitializeAsync();

        log.AssertWalked(
            DeviceState.Connected,
            DeviceState.Initializing,
            DeviceState.InitializationFailed,
            DeviceState.Initializing,
            DeviceState.Ready);
    }

    [Fact]
    public async Task InitializeCancelled()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        using var cts = new CancellationTokenSource();
        device.Connect();
        var log = new StateLog(device);

        device.OnDeviceInfoRequested = cts.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.InitializeAsync(cancellationToken: cts.Token));

        log.AssertWalked(DeviceState.Connected, DeviceState.Initializing, DeviceState.Connected);
    }

    [Fact]
    public async Task Loss()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();
        await device.InitializeAsync();
        var log = new StateLog(device);

        transport.SimulateDrop();

        log.AssertWalked(DeviceState.Ready, DeviceState.Lost);
    }

    [Fact]
    public async Task LossDuringInitialization()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();
        var log = new StateLog(device);

        device.OnDeviceInfoRequested = transport.SimulateDrop;
        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => device.InitializeAsync());

        log.AssertWalked(DeviceState.Connected, DeviceState.Initializing, DeviceState.Lost);
    }

    [Fact]
    public async Task ReconnectSuccess()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport) { ReconnectOptions = FastReconnect(3) };
        device.Connect();
        await device.InitializeAsync();
        var log = new StateLog(device);
        using var reconnected = new ManualResetEventSlim(false);
        device.Reconnected += (_, _) => reconnected.Set();

        transport.SimulateDrop();

        Assert.True(reconnected.Wait(WaitUntil.DefaultTimeout), "the reconnect never finished");
        log.AssertWalked(
            DeviceState.Ready,
            DeviceState.Lost,
            DeviceState.Retrying,
            DeviceState.Connecting,
            DeviceState.Connected,
            DeviceState.Initializing,
            DeviceState.Ready);
    }

    [Fact]
    public async Task ReconnectFailure()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport) { ReconnectOptions = FastReconnect(2) };
        device.Connect();
        await device.InitializeAsync();
        var log = new StateLog(device);
        using var gaveUp = new ManualResetEventSlim(false);
        device.ReconnectFailed += (_, _) => gaveUp.Set();

        // The first attempt opens no transport; the second opens one and cannot initialize over it.
        transport.FailNextConnects = 1;
        device.OnDeviceInfoRequested = () => throw new IOException("no answer");
        transport.SimulateDrop();

        Assert.True(gaveUp.Wait(WaitUntil.DefaultTimeout), "the reconnect never gave up");
        log.AssertWalked(
            DeviceState.Ready,
            DeviceState.Lost,
            DeviceState.Retrying,
            DeviceState.Connecting,
            DeviceState.Disconnected,
            DeviceState.Retrying,
            DeviceState.Connecting,
            DeviceState.Connected,
            DeviceState.Initializing,
            DeviceState.InitializationFailed,
            DeviceState.Disconnecting,
            DeviceState.Failed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disconnect(bool useAsync)
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();
        await device.InitializeAsync();
        var log = new StateLog(device);

        if (useAsync)
        {
            await device.DisconnectAsync();
        }
        else
        {
            device.Disconnect();
        }

        log.AssertWalked(DeviceState.Ready, DeviceState.Disconnecting, DeviceState.Disconnected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose(bool useAsync)
    {
        using var transport = new ReopeningTransport();
        var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();
        await device.InitializeAsync();
        var log = new StateLog(device);

        if (useAsync)
        {
            await device.DisposeAsync();
        }
        else
        {
            device.Dispose();
        }

        log.AssertWalked(
            DeviceState.Ready,
            DeviceState.Disconnecting,
            DeviceState.Disconnected,
            DeviceState.Disposed);
    }

    [Fact]
    public void DisposeOfADeviceThatWasNeverConnected()
    {
        var device = new DaqifiDevice("Device");
        var log = new StateLog(device);

        device.Dispose();
        device.Dispose();

        log.AssertWalked(DeviceState.Disconnected, DeviceState.Disposed);
    }

    #endregion

    #region Order across threads and handlers

    [Fact]
    public async Task AChangeOnAnotherThread_IsRaisedAfterTheEventBeingRaised_AndDoesNotWaitForIt()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();

        using var readyEntered = new ManualResetEventSlim(false);
        using var releaseReady = new ManualResetEventSlim(false);
        device.StateChanged += (_, e) =>
        {
            if (e.CurrentState == DeviceState.Ready)
            {
                readyEntered.Set();
                releaseReady.Wait(WaitUntil.DefaultTimeout);
            }
        };

        // Subscribed after the slow handler, so it has not heard about Ready when the drop lands.
        var log = new StateLog(device);
        var initializing = Task.Run(() => device.InitializeAsync());
        Assert.True(readyEntered.Wait(WaitUntil.DefaultTimeout), "the device never became ready");

        await Task.Run(transport.SimulateDrop).WaitAsync(WaitUntil.DefaultTimeout);

        Assert.Equal(DeviceState.Lost, device.State);
        log.AssertWalked(DeviceState.Connected, DeviceState.Initializing);

        releaseReady.Set();
        await initializing.WaitAsync(WaitUntil.DefaultTimeout);

        log.AssertWalked(
            DeviceState.Connected,
            DeviceState.Initializing,
            DeviceState.Ready,
            DeviceState.Lost);
    }

    [Fact]
    public void AChangeMadeInsideAHandler_ReachesEverySubscriberInOrder()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.StateChanged += (_, e) =>
        {
            if (e.CurrentState == DeviceState.Connected)
            {
                device.Disconnect();
            }
        };
        var log = new StateLog(device);

        device.Connect();

        log.AssertWalked(
            DeviceState.Disconnected,
            DeviceState.Connecting,
            DeviceState.Connected,
            DeviceState.Disconnecting,
            DeviceState.Disconnected);
    }

    [Fact]
    public void AHandlerRunsWithNoLockHeld()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        var movedFromAnotherThread = new List<bool>();
        device.StateChanged += (_, e) =>
        {
            if (e.CurrentState == DeviceState.Connected)
            {
                // A lock held across the event would leave this waiting for the handler, and the
                // handler waiting for it.
                movedFromAnotherThread.Add(
                    Task.Run(transport.SimulateDrop).Wait(WaitUntil.DefaultTimeout));
            }
        };
        var log = new StateLog(device);

        device.Connect();

        Assert.Equal([true], movedFromAnotherThread);
        log.AssertWalked(
            DeviceState.Disconnected,
            DeviceState.Connecting,
            DeviceState.Connected,
            DeviceState.Lost);
    }

    [Fact]
    public async Task ChangesRacingOnManyThreads_AreRaisedAsOneUnbrokenWalk()
    {
        var device = new DaqifiDevice("Device");
        var log = new StateLog(device);
        var steps = new[]
        {
            DeviceState.Connecting, DeviceState.Connected, DeviceState.Initializing, DeviceState.Ready,
            DeviceState.Disconnecting, DeviceState.Disconnected, DeviceState.Lost, DeviceState.Retrying
        };

        await Task.WhenAll(Enumerable.Range(0, 4).Select(seed => Task.Run(() =>
        {
            var random = new Random(seed);
            for (var i = 0; i < 2000; i++)
            {
                device.TryTransition(steps[random.Next(steps.Length)]);
            }
        }))).WaitAsync(WaitUntil.DefaultTimeout);

        // Whoever published last may have left its event to a thread that is still delivering.
        WaitUntil.That(
            () => log.Current == device.State,
            "the last change was never raised");
        log.AssertUnbroken();
    }

    [Fact]
    public async Task AHandlerThatDisconnectsADeviceAsItStartsInitializing_StopsTheInitialization()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();
        var attemptsBefore = device.TextCommandAttemptCount;
        device.StateChanged += (_, e) =>
        {
            if (e.CurrentState == DeviceState.Initializing)
            {
                device.Disconnect();
            }
        };

        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => device.InitializeAsync());

        Assert.Equal(DeviceState.Disconnected, device.State);
        Assert.Equal(attemptsBefore, device.TextCommandAttemptCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASessionBuiltInsideADisconnectingHandler_IsNotTornDownByTheDisconnectThatRaisedIt(bool useAsync)
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();
        await device.InitializeAsync();

        var rebuilt = false;
        device.StateChanged += (_, e) =>
        {
            if (rebuilt)
            {
                return;
            }

            if (e.CurrentState == DeviceState.Disconnecting)
            {
                device.Disconnect();
            }
            else if (e.CurrentState == DeviceState.Disconnected)
            {
                rebuilt = true;
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

        Assert.Equal(DeviceState.Connected, device.State);
        Assert.True(transport.IsConnected);
        await device.InitializeAsync();
        Assert.Equal(DeviceState.Ready, device.State);
    }

    [Fact]
    public async Task AFailedInitialization_ReleasesASecondCallerBeforeItsHandlersRun()
    {
        using var transport = new ReopeningTransport();
        using var device = new TestableDaqifiDevice("Device", transport);
        device.Connect();

        Task? second = null;
        device.OnDeviceInfoRequested = () =>
        {
            second ??= device.InitializeAsync();
            throw new IOException("no answer");
        };

        var secondFinished = false;
        device.StateChanged += (_, e) =>
        {
            if (e.CurrentState == DeviceState.InitializationFailed)
            {
                secondFinished = ((IAsyncResult)second!).AsyncWaitHandle.WaitOne(WaitUntil.DefaultTimeout);
            }
        };

        await Assert.ThrowsAsync<IOException>(() => device.InitializeAsync());

        Assert.True(secondFinished, "the second caller was still waiting while the handler ran");
        await Assert.ThrowsAsync<IOException>(() => second!);
    }

    #endregion

    private static ReconnectOptions FastReconnect(int maxAttempts) => new()
    {
        Enabled = true,
        MaxAttempts = maxAttempts,
        InitialDelay = TimeSpan.FromMilliseconds(5),
        MaxDelay = TimeSpan.FromMilliseconds(20)
    };

    /// <summary>Records every change a device raises, from the state it was in when this was built.</summary>
    private sealed class StateLog
    {
        private readonly DeviceState _start;
        private readonly ConcurrentQueue<DeviceStateChangedEventArgs> _changes = new();

        internal StateLog(IDevice device)
        {
            _start = device.State;
            device.StateChanged += (sender, e) =>
            {
                if (ReferenceEquals(device, sender))
                {
                    _changes.Enqueue(e);
                }
            };
        }

        internal DeviceState Current => _changes.LastOrDefault()?.CurrentState ?? _start;

        /// <summary>Asserts the whole walk, and that each event's previous value is the last one's current.</summary>
        internal void AssertWalked(params DeviceState[] expected)
        {
            Assert.Equal(expected, _changes.Select(e => e.CurrentState).Prepend(_start));
            AssertUnbroken();
        }

        internal void AssertUnbroken()
        {
            var at = _start;
            foreach (var change in _changes)
            {
                Assert.Equal(at, change.PreviousState);
                at = change.CurrentState;
            }
        }
    }
}
