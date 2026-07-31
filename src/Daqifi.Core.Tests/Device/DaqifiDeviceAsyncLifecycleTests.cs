using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using System.Diagnostics;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Covers the cancellable async connect/disconnect surface and <see cref="IAsyncDisposable"/>
/// added in issue #341, together with the guarantee that the pre-existing synchronous entry
/// points still behave exactly as they did.
/// </summary>
public class DaqifiDeviceAsyncLifecycleTests
{
    [Fact]
    public async Task ConnectAsync_Succeeds_ReportsConnectedAndStartsSending()
    {
        using var transport = new GatedMockTransport();
        transport.OpenConnectGate();
        await using var device = new DaqifiDevice("Mock Device", transport);

        var statusChanges = new List<ConnectionStatus>();
        device.StatusChanged += (_, args) => statusChanges.Add(args.Status);

        await device.ConnectAsync();

        Assert.True(device.IsConnected);
        Assert.Equal(ConnectionStatus.Connected, device.Status);
        Assert.Equal(DeviceState.Connected, device.State);
        Assert.Equal(new[] { ConnectionStatus.Connecting, ConnectionStatus.Connected }, statusChanges);

        // The producer really is running over the transport's stream, not just flagged as such.
        device.Send(ScpiMessageProducer.GetDeviceInfo);
        Assert.True(await transport.WaitForWrittenTextAsync("SYSTem:SYSInfoPB?"));
    }

    [Fact]
    public async Task ConnectAsync_TokenAlreadyCanceled_NeverTouchesTheTransport()
    {
        using var transport = new GatedMockTransport();
        using var device = new DaqifiDevice("Mock Device", transport);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.ConnectAsync(cts.Token));

        Assert.Equal(0, transport.ConnectAttempts);
        Assert.False(device.IsConnected);
        // The device never even claimed to be connecting, so no consumer sees a spurious transition.
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
    }

    [Fact]
    public async Task ConnectAsync_CanceledMidAttempt_AbandonsItAndReportsDisconnected()
    {
        // The transport blocks inside ConnectAsync until its gate opens — the stand-in for a real
        // dial that has not answered yet. Cancelling must break that wait rather than wait it out.
        using var transport = new GatedMockTransport();
        using var device = new DaqifiDevice("Mock Device", transport);
        using var cts = new CancellationTokenSource();

        var statusChanges = new List<ConnectionStatus>();
        device.StatusChanged += (_, args) => statusChanges.Add(args.Status);

        var connect = device.ConnectAsync(cts.Token);
        Assert.True(await transport.WaitForConnectEnteredAsync());
        Assert.False(connect.IsCompleted);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        Assert.False(device.IsConnected);
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
        Assert.Equal(DeviceState.Disconnected, device.State);
        Assert.Equal(new[] { ConnectionStatus.Connecting, ConnectionStatus.Disconnected }, statusChanges);
    }

    [Fact]
    public async Task ConnectAsync_CanceledAfterTheTransportOpened_ClosesItAgain()
    {
        // A cancel that lands in the window between "transport up" and "pumps started" must not
        // leak a live connection owned by a device that reports itself disconnected.
        using var transport = new GatedMockTransport();
        transport.OpenConnectGate();
        using var cts = new CancellationTokenSource();
        transport.OnConnected = () => cts.Cancel();

        using var device = new DaqifiDevice("Mock Device", transport);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.ConnectAsync(cts.Token));

        Assert.Equal(1, transport.ConnectAttempts);
        Assert.Equal(1, transport.DisconnectCount);
        Assert.False(transport.IsConnected);
        Assert.False(device.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_TransportThrows_ReportsDisconnectedAndRethrows()
    {
        using var transport = new GatedMockTransport { ConnectFailure = new InvalidOperationException("no route") };
        using var device = new DaqifiDevice("Mock Device", transport);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => device.ConnectAsync());

        Assert.Equal("no route", thrown.Message);
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
        Assert.Equal(DeviceState.Disconnected, device.State);
    }

    [Fact]
    public async Task DisconnectAsync_TearsDownTheTransportAndAllowsReconnect()
    {
        using var transport = new GatedMockTransport();
        transport.OpenConnectGate();
        using var device = new DaqifiDevice("Mock Device", transport);

        await device.ConnectAsync();
        await device.DisconnectAsync();

        Assert.Equal(1, transport.DisconnectCount);
        Assert.False(transport.IsConnected);
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
        Assert.Equal(DeviceState.Disconnected, device.State);

        // Still reusable afterwards — teardown must not have poisoned anything.
        await device.ConnectAsync();
        Assert.True(device.IsConnected);
    }

    [Fact]
    public async Task DisconnectAsync_NeverReportsLost()
    {
        using var transport = new GatedMockTransport();
        transport.OpenConnectGate();
        using var device = new DaqifiDevice("Mock Device", transport);
        await device.ConnectAsync();

        var statusChanges = new List<ConnectionStatus>();
        device.StatusChanged += (_, args) => statusChanges.Add(args.Status);

        await device.DisconnectAsync();

        Assert.DoesNotContain(ConnectionStatus.Lost, statusChanges);
        Assert.Contains(ConnectionStatus.Disconnected, statusChanges);
    }

    [Fact]
    public async Task DisconnectAsync_WhileATextExchangeHoldsTheLock_CancelingSkipsTheWait()
    {
        // The acceptance criterion behind #341: the sync Disconnect() can sit on the text-exchange
        // lock for its full budget, which on a UI thread is a multi-second freeze. Cancelling the
        // async one gives up that courtesy wait immediately and tears down anyway.
        using var transport = new GatedMockTransport();
        transport.OpenConnectGate();
        using var device = new TextExchangeProbeDevice("Mock Device", transport);
        await device.ConnectAsync();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldExchange = device.HoldTextExchangeAsync(entered, responseTimeoutMs: 3000);
        Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(10))));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var sw = Stopwatch.StartNew();
        var disconnect = device.DisconnectAsync(cts.Token);
        var finishedFirst = await Task.WhenAny(disconnect, heldExchange);
        sw.Stop();

        Assert.Same(disconnect, finishedFirst);
        await disconnect;
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"DisconnectAsync waited {sw.ElapsedMilliseconds}ms despite a cancelled token.");
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);

        // Let the abandoned exchange unwind; it is expected to fail now that the device is gone.
        try
        {
            await heldExchange;
        }
        catch (Exception)
        {
            // Losing the race with teardown is the documented outcome, not a test failure.
        }
    }

    [Fact]
    public async Task AwaitUsing_DisconnectsAndDisposesTheTransport()
    {
        var transport = new GatedMockTransport();
        transport.OpenConnectGate();

        await using (var device = new DaqifiDevice("Mock Device", transport))
        {
            await device.ConnectAsync();
            Assert.True(device.IsConnected);
        }

        Assert.True(transport.IsDisposed);
        Assert.Equal(1, transport.DisconnectCount);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentAndInterchangeableWithDispose()
    {
        var transport = new GatedMockTransport();
        transport.OpenConnectGate();
        var device = new DaqifiDevice("Mock Device", transport);
        await device.ConnectAsync();

        await device.DisposeAsync();
        await device.DisposeAsync();
        device.Dispose();

        // Exactly one teardown, no matter how many times (or which way) it is disposed.
        Assert.Equal(1, transport.DisconnectCount);
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public async Task DisposeAsync_OnANeverConnectedDevice_StillDisposesTheTransport()
    {
        var transport = new GatedMockTransport();
        var device = new DaqifiDevice("Mock Device", transport);

        await device.DisposeAsync();

        Assert.True(transport.IsDisposed);
        Assert.Equal(0, transport.DisconnectCount);
    }

    [Fact]
    public async Task Dispose_CalledWhileDisposeAsyncIsStillTearingDown_DoesNotStartASecondTeardown()
    {
        // Regression for the disposal overlap race. DisposeAsync spends real awaited time inside
        // DisconnectAsync, and a flag published only at the end of teardown leaves that whole
        // window open — a concurrent Dispose() would sail through and dispose the transport and the
        // text-exchange semaphore a second time.
        var transport = new GatedMockTransport();
        transport.OpenConnectGate();
        var device = new DaqifiDevice("Mock Device", transport);
        await device.ConnectAsync();

        transport.HoldFirstDisconnect();
        var disposeAsync = device.DisposeAsync();
        Assert.True(await transport.WaitForDisconnectEnteredAsync(),
            "DisposeAsync never reached the transport disconnect.");

        // The async teardown is parked mid-flight. A Dispose() arriving now must bounce off the
        // gate immediately rather than run its own teardown.
        var sw = Stopwatch.StartNew();
        device.Dispose();
        sw.Stop();

        transport.ReleaseDisconnect();
        await disposeAsync;

        Assert.Equal(1, transport.DisconnectCount);
        Assert.Equal(1, transport.DisposeCount);
        Assert.Equal(0, transport.SyncDisconnectCalls);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1),
            $"Dispose() spent {sw.ElapsedMilliseconds}ms; it should have returned at once as the loser.");
    }

    [Fact]
    public async Task Dispose_WhenTheDisconnectThrows_StillReleasesTheResources()
    {
        // Claiming the gate up front means there is no second chance at disposal, so teardown has
        // to release the handles even when the disconnect fails on the way out.
        var transport = new ThrowOnDisconnectTransport();
        var device = new DaqifiDevice("Mock Device", transport);
        device.Connect();

        Assert.Throws<IOException>(() => device.Dispose());

        Assert.True(transport.IsDisposed);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DisposeAsync_AfterSyncDispose_IsANoOp()
    {
        var transport = new GatedMockTransport();
        transport.OpenConnectGate();
        var device = new DaqifiDevice("Mock Device", transport);
        await device.ConnectAsync();

        device.Dispose();
        await device.DisposeAsync();

        Assert.Equal(1, transport.DisconnectCount);
    }

    // ---- The async connect path must carry everything the sync one does ----
    //
    // #415 added the background-error surface and its per-session throttle reset to Connect().
    // Every test it shipped drives Connect(), because ConnectAsync() did not exist yet — so
    // nothing else in the suite would notice if the async path lost either one when the two
    // were folded into a shared step set. These two tests are that guard.

    [Fact]
    public async Task ConnectAsync_WiresUpTheBackgroundErrorSurface()
    {
        using var transport = new ScriptedErrorTransport();
        using var device = new DaqifiDevice("Erroring Device", transport);

        var raised = new ManualResetEventSlim(false);
        DeviceErrorEventArgs? captured = null;
        device.ErrorOccurred += (_, e) =>
        {
            captured = e;
            raised.Set();
        };

        await device.ConnectAsync();
        transport.ScriptedStream.FailReads = true;

        Assert.True(raised.Wait(TimeSpan.FromSeconds(10)),
            "a read failure after ConnectAsync never reached an ErrorOccurred subscriber.");
        Assert.Equal(DeviceErrorSource.MessageConsumer, captured!.Source);
    }

    [Fact]
    public async Task ConnectAsync_OpensAFreshErrorThrottleSessionOnReconnect()
    {
        // The throttle collapses repeats of the same (source, exception type), so a reconnect must
        // clear it or the new session's first failure is swallowed.
        //
        // Nothing here depends on how fast the machine is. Against the default five-second window
        // it would: the test has to observe two errors inside one window, so a slow run makes the
        // second error due anyway (passing while proving nothing), and a tight bound fails a
        // correct implementation on a loaded runner. Widening the window to ten minutes takes the
        // clock out of it — a fresh session raises immediately, and a surviving bucket stays shut
        // for far longer than any run of this test.
        using var transport = new ScriptedErrorTransport();
        using var device = new DaqifiDevice("Erroring Device", transport);

        var throttle = new DeviceErrorThrottle(TimeSpan.FromMinutes(10));
        device.SetErrorThrottleForTesting(throttle);

        var gate = new object();
        var firstSessionRaised = new ManualResetEventSlim(false);
        var secondSessionRaised = new ManualResetEventSlim(false);
        var inSecondSession = false;
        long firstRaisedAt = 0;
        long secondRaisedAt = 0;
        DeviceErrorEventArgs? secondSessionFirstError = null;

        device.ErrorOccurred += (_, e) =>
        {
            lock (gate)
            {
                if (inSecondSession)
                {
                    // Only the session's *first* raise carries the "nothing suppressed behind me"
                    // claim; later ones legitimately report collapsed occurrences.
                    if (secondSessionFirstError != null)
                    {
                        return;
                    }

                    secondSessionFirstError = e;
                    secondRaisedAt = Stopwatch.GetTimestamp();
                    secondSessionRaised.Set();
                    return;
                }

                if (firstRaisedAt == 0)
                {
                    firstRaisedAt = Stopwatch.GetTimestamp();
                    firstSessionRaised.Set();
                }
            }
        };

        // Session one: fail reads until the throttle has opened a window and collapsed at least one
        // repeat behind it, so a surviving bucket would have a non-zero count to carry forward.
        await device.ConnectAsync();
        transport.ScriptedStream.FailReads = true;
        Assert.True(firstSessionRaised.Wait(TimeSpan.FromSeconds(10)),
            "the first session never reported an error.");

        // Quiesce and tear down. Disconnect stops the consumer, so no session-one raise can still
        // be in flight when the flag flips below.
        transport.ScriptedStream.FailReads = false;
        await device.DisconnectAsync();

        // Session two: same failure, same bucket key.
        await device.ConnectAsync();
        lock (gate)
        {
            inSecondSession = true;
        }

        transport.ScriptedStream.FailReads = true;

        // Primary guard. Without the reset the bucket is still shut — for another ten minutes — so
        // this failure never arrives and the wait is what trips.
        Assert.True(secondSessionRaised.Wait(TimeSpan.FromSeconds(10)),
            "the reconnected session never reported an error at all: its first failure was "
            + "collapsed into the throttle window the previous session opened, so ConnectAsync "
            + "did not reset the error throttle.");

        // Corroborating guard, independent of the clock in a different way: a reset clears the
        // bucket, so a fresh session's first raise has nothing collapsed behind it. A bucket that
        // survived would report the previous session's count here whenever it eventually fired.
        Assert.True(
            secondSessionFirstError!.SuppressedCount == 0,
            $"The reconnected session's first error reported {secondSessionFirstError.SuppressedCount} "
            + "suppressed occurrence(s), so the throttle bucket survived the reconnect.");

        // Backstop for the test's own premise rather than for the code: if the two errors somehow
        // landed a whole throttle window apart, the second raise was due regardless and the run
        // proved nothing. Ten minutes makes that unreachable in practice — a run that slow has
        // failed on the waits above long before — but assert it rather than assume it, so the test
        // can never report a pass it did not earn.
        var gap = Stopwatch.GetElapsedTime(firstRaisedAt, secondRaisedAt);
        Assert.True(
            gap < throttle.Interval,
            $"Inconclusive run: {gap.TotalSeconds:0.##}s separated the two errors, which is beyond "
            + $"the {throttle.Interval.TotalMinutes:0.##}-minute throttle window this test installs. "
            + "The second raise would have been due even without the reset, so this run cannot "
            + "distinguish the two cases. Failing rather than reporting a pass that guards nothing.");
    }

    /// <summary>
    /// Wraps #415's <see cref="DeviceErrorSurfaceTests.ScriptedStream"/> in a transport, so the
    /// async connect path can be pointed at a stream whose reads fail on demand.
    /// </summary>
    private sealed class ScriptedErrorTransport : IStreamTransport
    {
        private bool _isConnected;
        private bool _disposed;

        public DeviceErrorSurfaceTests.ScriptedStream ScriptedStream { get; } = new();

        public Stream Stream => _disposed
            ? throw new ObjectDisposedException(nameof(ScriptedErrorTransport))
            : ScriptedStream;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Scripted: Connected" : "Scripted: Disconnected";

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

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _isConnected = false;
            _disposed = true;
            ScriptedStream.Dispose();
        }
    }

    // ---- Synchronous parity: the pre-#341 entry points must behave identically ----

    [Fact]
    public void Connect_Disconnect_StillDriveTheTransportSynchronously()
    {
        using var transport = new GatedMockTransport();
        transport.OpenConnectGate();
        using var device = new DaqifiDevice("Mock Device", transport);

        var statusChanges = new List<ConnectionStatus>();
        device.StatusChanged += (_, args) => statusChanges.Add(args.Status);

        device.Connect();

        Assert.True(device.IsConnected);
        Assert.Equal(DeviceState.Connected, device.State);
        Assert.Equal(1, transport.SyncConnectCalls);

        device.Disconnect();

        Assert.False(device.IsConnected);
        Assert.Equal(DeviceState.Disconnected, device.State);
        Assert.Equal(1, transport.SyncDisconnectCalls);
        Assert.Equal(
            new[] { ConnectionStatus.Connecting, ConnectionStatus.Connected, ConnectionStatus.Disconnected },
            statusChanges);
    }

    [Fact]
    public void Connect_WhenTheTransportThrows_PropagatesTheOriginalExceptionUnwrapped()
    {
        using var transport = new GatedMockTransport { ConnectFailure = new InvalidOperationException("no route") };
        using var device = new DaqifiDevice("Mock Device", transport);

        var thrown = Assert.Throws<InvalidOperationException>(() => device.Connect());

        Assert.Equal("no route", thrown.Message);
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
        Assert.Equal(DeviceState.Disconnected, device.State);
    }

    [Fact]
    public void Dispose_StillDisconnectsAndDisposesTheTransport()
    {
        var transport = new GatedMockTransport();
        transport.OpenConnectGate();
        var device = new DaqifiDevice("Mock Device", transport);
        device.Connect();

        device.Dispose();

        Assert.Equal(1, transport.SyncDisconnectCalls);
        Assert.True(transport.IsDisposed);
    }

    /// <summary>
    /// Transport whose close fails, standing in for a serial port that throws on the way out.
    /// </summary>
    private sealed class ThrowOnDisconnectTransport : IStreamTransport
    {
        private readonly MemoryStream _stream = new();
        private bool _isConnected;

        public bool IsDisposed { get; private set; }

        public Stream Stream => _stream;
        public bool IsConnected => _isConnected;
        public string ConnectionInfo => "Throwing";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            _isConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync() => throw new IOException("the port went away");

        public void Connect() => ConnectAsync().GetAwaiter().GetResult();

        public void Disconnect() => DisconnectAsync().GetAwaiter().GetResult();

        public void Dispose()
        {
            IsDisposed = true;
            _isConnected = false;
            _stream.Dispose();
        }
    }

    /// <summary>
    /// Exposes the protected text-exchange seam so a test can hold the device-wide text exchange
    /// lock while a disconnect is attempted.
    /// </summary>
    private sealed class TextExchangeProbeDevice(string name, IStreamTransport transport)
        : DaqifiDevice(name, transport)
    {
        /// <summary>
        /// Runs a text exchange that holds the lock for roughly <paramref name="responseTimeoutMs"/>
        /// (nothing ever replies on the mock stream), signalling <paramref name="entered"/> from
        /// inside the lock.
        /// </summary>
        public Task HoldTextExchangeAsync(TaskCompletionSource entered, int responseTimeoutMs) =>
            ExecuteTextCommandAsync(
                () => entered.TrySetResult(),
                responseTimeoutMs: responseTimeoutMs,
                completionTimeoutMs: 50);
    }

    /// <summary>
    /// Mock transport whose connect can be held open, made to fail, or observed — everything the
    /// cancellation paths need without a real socket or serial port.
    /// </summary>
    private sealed class GatedMockTransport : IStreamTransport
    {
        private readonly MemoryStream _stream = new();
        private readonly TaskCompletionSource _connectGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _connectEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disconnectGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disconnectEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _isConnected;
        private volatile bool _disposed;
        private int _connectAttempts;
        private int _disconnectCount;
        private int _syncConnectCalls;
        private int _syncDisconnectCalls;
        private int _disposeCount;
        private int _disconnectGateArmed;

        /// <summary>When set, every connect attempt throws this instead of succeeding.</summary>
        public Exception? ConnectFailure { get; init; }

        /// <summary>Invoked after the transport reports itself connected, before ConnectAsync returns.</summary>
        public Action? OnConnected { get; set; }

        public int ConnectAttempts => Volatile.Read(ref _connectAttempts);
        public int DisconnectCount => Volatile.Read(ref _disconnectCount);
        public int SyncConnectCalls => Volatile.Read(ref _syncConnectCalls);
        public int SyncDisconnectCalls => Volatile.Read(ref _syncDisconnectCalls);
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public bool IsDisposed => _disposed;

        /// <summary>
        /// Parks the FIRST disconnect until <see cref="ReleaseDisconnect"/> is called. Only the
        /// first, so a regression that starts a second teardown fails an assertion instead of
        /// deadlocking the test run.
        /// </summary>
        public void HoldFirstDisconnect() => Interlocked.Exchange(ref _disconnectGateArmed, 1);

        public void ReleaseDisconnect() => _disconnectGate.TrySetResult();

        public Task<bool> WaitForDisconnectEnteredAsync() => WaitAsync(_disconnectEntered.Task);

        public Stream Stream => _disposed
            ? throw new ObjectDisposedException(nameof(GatedMockTransport))
            : _stream;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Gated: Connected" : "Gated: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public void OpenConnectGate() => _connectGate.TrySetResult();

        public Task<bool> WaitForConnectEnteredAsync() => WaitAsync(_connectEntered.Task);

        public async Task<bool> WaitForWrittenTextAsync(string expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (System.Text.Encoding.UTF8.GetString(_stream.ToArray()).Contains(expected))
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    // Snapshotting a MemoryStream the producer thread is writing to can tear;
                    // just look again on the next poll.
                }

                await Task.Delay(25);
            }

            return false;
        }

        public Task ConnectAsync() => ConnectAsync(null, CancellationToken.None);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions) =>
            ConnectAsync(retryOptions, CancellationToken.None);

        public Task ConnectAsync(CancellationToken cancellationToken) =>
            ConnectAsync(null, cancellationToken);

        public async Task ConnectAsync(ConnectionRetryOptions? retryOptions, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Interlocked.Increment(ref _connectAttempts);
            _connectEntered.TrySetResult();

            if (ConnectFailure != null)
            {
                throw ConnectFailure;
            }

            await _connectGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            _isConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
            OnConnected?.Invoke();
        }

        public async Task DisconnectAsync()
        {
            if (!_isConnected)
            {
                return;
            }

            _disconnectEntered.TrySetResult();

            if (Interlocked.Exchange(ref _disconnectGateArmed, 0) == 1)
            {
                await _disconnectGate.Task.ConfigureAwait(false);
            }

            Interlocked.Increment(ref _disconnectCount);
            _isConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
        }

        public void Connect()
        {
            Interlocked.Increment(ref _syncConnectCalls);
            ConnectAsync().GetAwaiter().GetResult();
        }

        public void Disconnect()
        {
            Interlocked.Increment(ref _syncDisconnectCalls);
            DisconnectAsync().GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);

            if (_disposed)
            {
                return;
            }

            _isConnected = false;
            _disposed = true;
            _stream.Dispose();
        }

        private static async Task<bool> WaitAsync(Task task)
        {
            return ReferenceEquals(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))));
        }
    }
}
