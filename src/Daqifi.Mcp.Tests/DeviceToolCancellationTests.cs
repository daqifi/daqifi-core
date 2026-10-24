using Daqifi.Mcp.Tools;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// Client cancel must abort a device-tool call that is waiting on the registry gate or the
/// device operation lock, rather than sitting out the wait. It must abort only the wait: once a
/// tool has the device, the change it makes lands whole.
/// </summary>
public class DeviceToolCancellationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    /// <summary>Every tool that changes device state under the device operation lock.</summary>
    public static TheoryData<string> DeviceLockTools() => new()
    {
        "configure_analog_channels",
        "configure_digital_channels",
        "set_digital_direction",
        "set_digital_output",
        "set_pwm_output",
        "disable_pwm",
        "set_analog_output",
        "latch_analog_outputs",
        "set_sample_rate",
    };

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
            Assert.False(disconnect.IsCompleted);

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

        // The cancelled wait never took the gate, so it is free for the next caller.
        await agent.DisconnectAsync(AgentHarness.DeviceId).WaitAsync(Bound);
        Assert.Empty(agent.ListConnected());
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

    /// <summary>
    /// Driven through the MCP tool rather than the agent, so it pins the whole path: the tool hands
    /// the token on, the agent gives it to the lock wait, and the tool's guard lets the cancel out
    /// as a cancel instead of an error message.
    /// </summary>
    [Theory]
    [MemberData(nameof(DeviceLockTools))]
    public async Task DeviceTool_WhenCancelledWhileWaitingOnTheDeviceLock_ThrowsAndSendsNothing(string tool)
    {
        var (agent, device) = AgentHarness.WithConnectedDevice(analogOutputs: 2);
        device.ClearSent();
        var capabilityReads = device.CapabilityReads;

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
            var ct = cts.Token;
            var id = AgentHarness.DeviceId;
            Task call = tool switch
            {
                "configure_analog_channels" => DaqifiTools.ConfigureAnalogChannels(agent, id, new[] { 0 }, ct),
                "configure_digital_channels" => DaqifiTools.ConfigureDigitalChannels(agent, id, new[] { 0 }, ct),
                "set_digital_direction" => DaqifiTools.SetDigitalDirection(agent, id, 0, "output", ct),
                "set_digital_output" => DaqifiTools.SetDigitalOutput(agent, id, 0, true, ct),
                "set_pwm_output" => DaqifiTools.SetPwmOutput(agent, id, 4, 50, 1000, ct),
                "disable_pwm" => DaqifiTools.DisablePwm(agent, id, 4, ct),
                "set_analog_output" => DaqifiTools.SetAnalogOutput(agent, id, 0, 2.5, true, ct),
                "latch_analog_outputs" => DaqifiTools.LatchAnalogOutputs(agent, id, ct),
                _ => DaqifiTools.SetSampleRate(agent, id, 100, ct),
            };

            // Still parked on the lock, so the cancel below lands on the wait, not before the call.
            Assert.False(call.IsCompleted);

            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Bound));
            Assert.Empty(device.Sent);
            Assert.Equal(capabilityReads, device.CapabilityReads);
        }
        finally
        {
            release.TrySetResult();
            await holding.WaitAsync(Bound);
        }

        // The cancelled wait never took the lock, so it is free for the next caller.
        await device.RunExclusiveAsync(_ => Task.CompletedTask).WaitAsync(Bound);
    }

    [Fact]
    public async Task ConfigureAnalogChannelsAsync_WhenCancelledDuringCapabilityRefresh_StillBringsTheRateUnderTheNewCap()
    {
        // 12000 Hz fits one channel (cap 20000) but not four (cap 5000). The mask has gone out by
        // the time the re-read starts, so a cancel that stopped here would leave four channels live
        // at a rate the firmware will not stream at.
        var (agent, device) = AgentHarness.WithConnectedDevice(analogChannels: 4);
        await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0 });
        await agent.SetSampleRateAsync(AgentHarness.DeviceId, 12_000);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.BeforeCapabilityRead = async _ =>
        {
            entered.SetResult();
            await release.Task;
        };

        using var cts = new CancellationTokenSource();
        var configure = agent.ConfigureAnalogChannelsAsync(
            AgentHarness.DeviceId, new[] { 0, 1, 2, 3 }, cts.Token);

        try
        {
            await entered.Task.WaitAsync(Bound);
            await cts.CancelAsync();
        }
        finally
        {
            release.TrySetResult();
        }

        var result = await configure.WaitAsync(Bound);

        Assert.Equal(new[] { 0, 1, 2, 3 }, result.EnabledAnalogChannels);
        Assert.Equal(12_000, result.SampleRateAdjustedFromHz);
        Assert.Equal(5_000, result.SampleRateHz);
        Assert.Equal(5_000, agent.GetStatus(AgentHarness.DeviceId).SampleRateHz);
    }
}
