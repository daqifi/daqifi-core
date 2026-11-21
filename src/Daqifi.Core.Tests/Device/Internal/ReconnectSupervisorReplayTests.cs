using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>
/// The hand-off at the end of a reconnect loop: a drop the single-flight guard turned away while
/// the loop ran is replayed, on that drop's own epoch, and nothing else is.
/// </summary>
/// <remarks>
/// The host here reads <see cref="ConnectionStatus.Lost"/> throughout, as a real device still does
/// while a caller's <c>Disconnect</c> has superseded the session but not yet torn it down. That is
/// the state in which a replay on the current epoch would start a loop nobody asked for.
/// </remarks>
public class ReconnectSupervisorReplayTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task ALoopThatEndsWithNoDropTurnedAway_DoesNotStartAnother()
    {
        var host = new LostHost();
        var supervisor = NewSupervisor(host);

        supervisor.BeginIfEnabled(supervisor.PrepareForDrop());

        Assert.True(await host.FirstConnectAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await WaitUntilIdleAsync(supervisor);
        await Task.Delay(Settle);

        Assert.Equal(1, host.ConnectAttempts);
        Assert.False(supervisor.IsRunning);
    }

    [Fact]
    public async Task ADropTurnedAwayWhileALoopRan_IsReplayedWhenItEnds()
    {
        var host = new LostHost();
        var supervisor = NewSupervisor(host);
        host.OnFirstConnect = () => supervisor.BeginIfEnabled(supervisor.PrepareForDrop());

        supervisor.BeginIfEnabled(supervisor.PrepareForDrop());

        Assert.True(await host.SecondConnectAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await WaitUntilIdleAsync(supervisor);
    }

    private static ReconnectSupervisor NewSupervisor(LostHost host) => new(host)
    {
        Options = new ReconnectOptions
        {
            Enabled = true,
            MaxAttempts = 1,
            InitialDelay = TimeSpan.Zero,
        },
    };

    private static async Task WaitUntilIdleAsync(ReconnectSupervisor supervisor)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (supervisor.IsRunning && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.False(supervisor.IsRunning);
    }

    /// <summary>A host that stays on Lost and fails every connect attempt.</summary>
    private sealed class LostHost : IReconnectHost
    {
        private int _connectAttempts;

        public int ConnectAttempts => Volatile.Read(ref _connectAttempts);

        public Action? OnFirstConnect { get; set; }

        public TaskCompletionSource<bool> FirstConnectAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> SecondConnectAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger Logger => NullLogger.Instance;

        public string Name => "FakeNq1";

        public TimeProvider TimeProvider => TimeProvider.System;

        public ConnectionStatus Status => ConnectionStatus.Lost;

        public bool IsDisposed => false;

        public bool IsDisconnecting => false;

        public bool HasTransport => true;

        public bool CallerWantsDisconnected => false;

        public void ConnectCore()
        {
            var attempt = Interlocked.Increment(ref _connectAttempts);
            if (attempt == 1)
            {
                OnFirstConnect?.Invoke();
                FirstConnectAttempted.TrySetResult(true);
            }
            else if (attempt == 2)
            {
                SecondConnectAttempted.TrySetResult(true);
            }

            throw new IOException("The device is not answering.");
        }

        public void DisconnectCore(ConnectionStatus finalStatus)
        {
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void CaptureSessionSnapshot()
        {
        }

        public Task<bool> RestoreSessionSnapshotAsync(ReconnectOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public void RaiseReconnectAttempt(ReconnectAttemptEventArgs args)
        {
        }

        public void RaiseReconnected(ReconnectedEventArgs args)
        {
        }

        public void RaiseReconnectFailed(ReconnectFailedEventArgs args)
        {
        }

        public void RaiseDeviceError(DeviceErrorSource source, Exception error)
        {
        }
    }
}
