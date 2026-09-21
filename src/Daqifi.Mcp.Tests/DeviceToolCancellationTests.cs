using Daqifi.Mcp.Tools;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// Client cancel must abort a device-tool call that is waiting on the registry gate or the
/// device operation lock — including analog configure's capability re-read — rather than
/// sitting out the wait.
/// </summary>
public class DeviceToolCancellationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DisconnectAsync_WhenCancelledWhileWaitingOnTheGate_ThrowsWithoutWaitingForTheLock()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice();
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = agent.HoldRegistryGateAsync(acquired, release.Task);

        try
        {
            await acquired.Task.WaitAsync(Bound);

            using var cts = new CancellationTokenSource();
            var disconnect = agent.DisconnectAsync(AgentHarness.DeviceId, cts.Token);
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => disconnect.WaitAsync(Bound));

            Assert.Single(agent.ListConnected());
        }
        finally
        {
            release.TrySetResult();
            await holding.WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task ConfigureAnalogChannelsAsync_WhenCancelledWhileWaitingOnTheDeviceLock_ThrowsWithoutWaitingForTheLock()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice();
        device.ClearSent();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = device.RunExclusiveAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        });

        try
        {
            await entered.Task.WaitAsync(Bound);

            using var cts = new CancellationTokenSource();
            var configure = agent.ConfigureAnalogChannelsAsync(
                AgentHarness.DeviceId, new[] { 0 }, cts.Token);
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => configure.WaitAsync(Bound));

            Assert.Empty(device.Sent);
            Assert.Equal(0, device.CapabilityReads);
        }
        finally
        {
            release.TrySetResult();
            await holding.WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task ConfigureAnalogChannelsAsync_WhenCancelledDuringCapabilityRefresh_ThrowsWithoutFinishingTheRead()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        device.BeforeCapabilityRead = async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };

        using var cts = new CancellationTokenSource();
        var configure = agent.ConfigureAnalogChannelsAsync(
            AgentHarness.DeviceId, new[] { 0 }, cts.Token);

        await entered.Task.WaitAsync(Bound);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => configure.WaitAsync(Bound));

        Assert.Equal(1, device.CapabilityReads);
    }

    [Fact]
    public async Task DisconnectDevice_CancelledCall_IsNotDisguisedAsAToolError()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DaqifiTools.DisconnectDevice(agent, AgentHarness.DeviceId, cts.Token));
    }

    [Fact]
    public async Task ConfigureAnalogChannels_CancelledCall_IsNotDisguisedAsAToolError()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DaqifiTools.ConfigureAnalogChannels(
                agent, AgentHarness.DeviceId, new[] { 0 }, cts.Token));
    }
}
