using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using System.Collections.Concurrent;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Issue #943: the <c>Initializing</c> state had no owner, so a second <c>InitializeAsync</c>
/// call, or one left over from an earlier connection, could report over the top of the call the
/// state belonged to. Each test parks an initialization inside its setup block on a gate the test
/// holds, so the overlap is arranged rather than raced for.
/// </summary>
public class DaqifiDeviceConcurrentInitializeTests
{
    /// <summary>Long enough that only a hang reaches it; nothing here waits on time passing.</summary>
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task InitializeAsync_WhenTheFirstOfTwoCallsIsCancelled_TheSecondCompletesAndTheDeviceIsReady()
    {
        // Arrange
        using var device = new GatedInitDevice();
        var gate = device.GateSetupBlock(1);
        device.Connect();

        using var cts = new CancellationTokenSource();
        var first = device.InitializeAsync(cancellationToken: cts.Token);
        await gate.Entered.Task.WaitAsync(Hang);
        var second = device.InitializeAsync();
        Assert.False(second.IsCompleted);

        // Act
        cts.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Hang));
        await second.WaitAsync(Hang);
        Assert.Equal(DeviceLifecycle.Ready, device.Lifecycle);
        Assert.Equal(DeviceState.Ready, device.State);
    }

    [Fact]
    public async Task InitializeAsync_WhenTheFirstOfTwoCallsFails_TheSecondGetsThatFailureNotAConnectionChange()
    {
        // Arrange
        using var device = new GatedInitDevice();
        var gate = device.GateSetupBlock(1);
        device.Connect();

        var first = device.InitializeAsync();
        await gate.Entered.Task.WaitAsync(Hang);
        var second = device.InitializeAsync();
        var failure = new IOException("the port went quiet");

        // Act
        gate.Release.SetException(failure);

        // Assert
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => first.WaitAsync(Hang)));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => second.WaitAsync(Hang)));
        Assert.Equal(DeviceLifecycle.Error, device.Lifecycle);

        // And a failed initialization can still be retried.
        await device.InitializeAsync().WaitAsync(Hang);
        Assert.Equal(DeviceLifecycle.Ready, device.Lifecycle);
    }

    [Fact]
    public async Task InitializeAsync_WhenTwoCallsOverlap_SendsTheInitializationCommandsOnce()
    {
        // Arrange
        using var device = new GatedInitDevice();
        var gate = device.GateSetupBlock(1);
        device.Connect();

        var first = device.InitializeAsync();
        await gate.Entered.Task.WaitAsync(Hang);
        var second = device.InitializeAsync();

        // Act
        gate.Release.SetResult();
        await first.WaitAsync(Hang);
        await second.WaitAsync(Hang);

        // Assert
        Assert.Equal(DeviceLifecycle.Ready, device.Lifecycle);
        Assert.Equal(1, device.SetupBlockCount);
        Assert.Single(device.Sent, data => data == ScpiMessageProducer.StopStreaming.Data);
        Assert.Single(device.Sent, data => data == ScpiMessageProducer.SetProtobufStreamFormat.Data);
        Assert.Single(device.Sent, data => data == ScpiMessageProducer.GetDeviceInfo.Data);
    }

    [Fact]
    public async Task InitializeAsync_WhenTheWaitingCallIsCancelled_OnlyThatCallEnds()
    {
        // Arrange
        using var device = new GatedInitDevice();
        var gate = device.GateSetupBlock(1);
        device.Connect();

        var first = device.InitializeAsync();
        await gate.Entered.Task.WaitAsync(Hang);
        using var cts = new CancellationTokenSource();
        var second = device.InitializeAsync(cancellationToken: cts.Token);

        // Act
        cts.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(Hang));
        Assert.Equal(DeviceLifecycle.Initializing, device.Lifecycle);

        gate.Release.SetResult();
        await first.WaitAsync(Hang);
        Assert.Equal(DeviceLifecycle.Ready, device.Lifecycle);
        Assert.Equal(1, device.SetupBlockCount);
    }

    [Fact]
    public async Task InitializeAsync_WhenTheConnectionIsLostUnderAWaitingCall_ItIsNotToldTheDeviceIsShuttingDown()
    {
        // Arrange — one call initializing, a second waiting on it.
        using var device = new GatedInitDevice();
        var gate = device.GateSetupBlock(1);
        device.Connect();

        using var cts = new CancellationTokenSource();
        var first = device.InitializeAsync(cancellationToken: cts.Token);
        await gate.Entered.Task.WaitAsync(Hang);
        var second = device.InitializeAsync();

        // Act — the link drops, and the first call unwinds with a cancellation that is not the
        // waiting call's own. Nobody asked for a disconnect.
        Assert.True(device.TryTransition(DeviceLifecycle.Lost));
        cts.Cancel();

        // Assert — the waiting call finds no connection to initialize, which is all it says.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Hang));
        var thrown = await Assert.ThrowsAsync<DeviceNotConnectedException>(() => second.WaitAsync(Hang));
        Assert.False(thrown.IsShuttingDown);
        Assert.Equal(DeviceLifecycle.Lost, device.Lifecycle);
        Assert.Equal(1, device.SetupBlockCount);
    }

    [Fact]
    public async Task InitializeAsync_WhenTheConnectionIsBeingTornDownUnderAWaitingCall_ItIsToldSo()
    {
        // Arrange
        using var device = new GatedInitDevice();
        var gate = device.GateSetupBlock(1);
        device.Connect();

        using var cts = new CancellationTokenSource();
        var first = device.InitializeAsync(cancellationToken: cts.Token);
        await gate.Entered.Task.WaitAsync(Hang);
        var second = device.InitializeAsync();

        // Act — a teardown somebody asked for, caught part-way.
        Assert.True(device.TryTransition(DeviceLifecycle.Disconnecting));
        cts.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Hang));
        var thrown = await Assert.ThrowsAsync<DeviceNotConnectedException>(() => second.WaitAsync(Hang));
        Assert.True(thrown.IsShuttingDown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeAsync_WhenACallFromAnOldConnectionReportsLate_TheNewConnectionIsUntouched(bool oldCallIsCancelled)
    {
        // Arrange — one initialization parked on the first connection, then a reconnect, then a
        // second initialization parked on the new connection.
        using var device = new GatedInitDevice();
        var oldGate = device.GateSetupBlock(1);
        var newGate = device.GateSetupBlock(2);
        device.Connect();

        using var cts = new CancellationTokenSource();
        var onOldConnection = device.InitializeAsync(cancellationToken: cts.Token);
        await oldGate.Entered.Task.WaitAsync(Hang);

        device.Disconnect();
        device.Connect();

        var onNewConnection = device.InitializeAsync();
        await newGate.Entered.Task.WaitAsync(Hang);
        Assert.Equal(DeviceLifecycle.Initializing, device.Lifecycle);

        // Act — the old call ends late, and reports Connected (cancelled) or Error (failed).
        if (oldCallIsCancelled)
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => onOldConnection.WaitAsync(Hang));
        }
        else
        {
            oldGate.Release.SetException(new IOException("the old port went away"));
            await Assert.ThrowsAsync<IOException>(() => onOldConnection.WaitAsync(Hang));
        }

        // Assert — the report was refused: the state still belongs to the new connection's call,
        // which goes on to finish.
        Assert.Equal(DeviceLifecycle.Initializing, device.Lifecycle);
        Assert.False(onNewConnection.IsCompleted);

        newGate.Release.SetResult();
        await onNewConnection.WaitAsync(Hang);
        Assert.Equal(DeviceLifecycle.Ready, device.Lifecycle);
    }

    /// <summary>A place a test holds an initialization: inside its setup block, before any command.</summary>
    private sealed class Gate
    {
        /// <summary>Completes when an initialization has reached the gate.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Complete it to let the initialization through; fault it to make the setup block fail.</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// A device with no transport that answers <c>SYSInfoPB?</c> with a healthy status, records
    /// what it is sent, and can hold the n-th setup block at a <see cref="Gate"/>.
    /// </summary>
    private sealed class GatedInitDevice : DaqifiDevice
    {
        private readonly ConcurrentDictionary<int, Gate> _gates = new();
        private readonly ConcurrentQueue<string> _sent = new();
        private int _setupBlockCount;

        public GatedInitDevice() : base("TestDevice")
        {
        }

        /// <summary>How many times the init setup block has been entered.</summary>
        public int SetupBlockCount => Volatile.Read(ref _setupBlockCount);

        public IReadOnlyCollection<string> Sent => _sent;

        /// <summary>Holds the <paramref name="callNumber"/>-th setup block (counting from 1) until released.</summary>
        public Gate GateSetupBlock(int callNumber) => _gates.GetOrAdd(callNumber, _ => new Gate());

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is not IOutboundMessage<string> text)
            {
                return;
            }

            _sent.Enqueue(text.Data);
            if (text.Data.Contains("SYSInfoPB"))
            {
                OnStatusMessageReceived(new DaqifiOutMessage
                {
                    DevicePn = "Nq1",
                    DeviceSn = 9090539562006014104,
                    DeviceFwRev = "3.8.0",
                    AnalogInPortNum = 2,
                    DigitalPortNum = 2
                });
            }
        }

        // Skipped, which leaves the setup block as the only exchange on the async overload below.
        public override Task<Daqifi.Core.Device.Capabilities.CapabilityDocument?> ReadCapabilityDocumentAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<Daqifi.Core.Device.Capabilities.CapabilityDocument?>(null);
        }

        // The error-queue drain.
        protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null,
            bool keepBlankLines = false)
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        // The setup block is the one exchange on the async overload (#667).
        protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Func<CancellationToken, Task> setupActionAsync,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default)
        {
            if (_gates.TryGetValue(Interlocked.Increment(ref _setupBlockCount), out var gate))
            {
                gate.Entered.TrySetResult();
                await gate.Release.Task.WaitAsync(cancellationToken);
            }

            await setupActionAsync(cancellationToken);
            return Array.Empty<string>();
        }
    }
}
