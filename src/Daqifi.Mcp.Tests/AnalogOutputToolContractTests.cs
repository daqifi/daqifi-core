using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// Contract tests for the analog-output (DAC) tools — the MCP half of #499, whose Core half landed
/// in #526.
/// </summary>
/// <remarks>
/// The failure this suite exists to catch is a silent one. Core sends an analog-output command by
/// channel number and never reads the device's error queue back, so a write that the firmware
/// discarded is indistinguishable, from the agent's side, from one it applied. What is asserted
/// here is therefore mostly about refusals: which calls never reach the wire, and whether the agent
/// is told why.
/// </remarks>
public class AnalogOutputToolContractTests
{
    [Fact]
    public void ListAnalogOutputs_ReportsEachChannelWithItsRangeAndResolution()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice(analogOutputs: 2);

        var outputs = agent.ListAnalogOutputs(AgentHarness.DeviceId);

        Assert.Equal(new[] { 0, 1 }, outputs.Select(o => o.Channel));
        Assert.All(outputs, o =>
        {
            Assert.Equal(0, o.MinVolts);
            Assert.Equal(10, o.MaxVolts);
            Assert.Equal(12, o.ResolutionBits);
            // The device stated this range, so a refusal quotes the hardware rather than an
            // assumption of ours — which is the difference between "your device cannot do that"
            // and "we guessed it cannot".
            Assert.False(o.RangeIsAssumed);
            Assert.Null(o.Volts);
            Assert.Null(o.PendingVolts);
        });
    }

    [Fact]
    public void ListAnalogOutputs_OnADeviceWithNone_IsEmptyRatherThanAnError()
    {
        // An agent asking what a device can drive is entitled to the answer "nothing".
        var (agent, _) = AgentHarness.WithConnectedDevice();

        Assert.Empty(agent.ListAnalogOutputs(AgentHarness.DeviceId));
    }

    [Fact]
    public void ListConnectedDevices_CountsAnalogOutputsSeparatelyFromInputs()
    {
        // Outputs sit in the same channel collection as the inputs; folding them into
        // AnalogChannelCount would report an acquisition wider than the device can actually run.
        var (agent, _) = AgentHarness.WithConnectedDevice(analogChannels: 4, digitalChannels: 8, analogOutputs: 2);

        var listed = Assert.Single(agent.ListConnected());

        Assert.Equal(4, listed.AnalogChannelCount);
        Assert.Equal(8, listed.DigitalChannelCount);
        Assert.Equal(2, listed.AnalogOutputChannelCount);
    }

    [Fact]
    public async Task SetAnalogOutput_StagesAndLatchesInThatOrder()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice(analogOutputs: 2);

        var result = await agent.SetAnalogOutputAsync(AgentHarness.DeviceId, 1, 2.5, latch: true);

        Assert.True(result.Applied);
        Assert.True(result.RangeChecked);
        Assert.Equal(2.5, result.State!.Volts);
        Assert.Null(result.State.PendingVolts);
        Assert.Equal(
            new[]
            {
                ScpiMessageProducer.SetAnalogOutputVoltage(1, 2.5).Data,
                ScpiMessageProducer.UpdateDacOutputs.Data,
            },
            device.Sent);
    }

    [Fact]
    public async Task SetAnalogOutput_WithLatchFalse_DoesNotDriveThePin()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice(analogOutputs: 2);

        var result = await agent.SetAnalogOutputAsync(AgentHarness.DeviceId, 0, 4.0, latch: false);

        Assert.False(result.Applied);
        // Pending, not driven: the distinction is the whole point of the staged form, and an agent
        // that read this as "done" would report a voltage the hardware is not carrying.
        Assert.Equal(4.0, result.State!.PendingVolts);
        Assert.Null(result.State.Volts);
        Assert.Equal(new[] { ScpiMessageProducer.SetAnalogOutputVoltage(0, 4.0).Data }, device.Sent);
    }

    [Fact]
    public async Task LatchAnalogOutputs_AppliesEveryStagedChannelTogether()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice(analogOutputs: 2);
        await agent.SetAnalogOutputAsync(AgentHarness.DeviceId, 0, 1.5, latch: false);
        await agent.SetAnalogOutputAsync(AgentHarness.DeviceId, 1, 7.5, latch: false);
        device.ClearSent();

        var result = await agent.LatchAnalogOutputsAsync(AgentHarness.DeviceId);

        Assert.Equal(new[] { ScpiMessageProducer.UpdateDacOutputs.Data }, device.Sent);
        Assert.Equal(new double?[] { 1.5, 7.5 }, result.Outputs.Select(o => o.Volts));
        Assert.All(result.Outputs, o => Assert.Null(o.PendingVolts));
    }

    [Fact]
    public async Task SetAnalogOutput_WithLatch_AlsoAppliesAnotherChannelStagedEarlier()
    {
        // The latch is device-wide, not per-channel. A caller that staged channel 0 and then wrote
        // channel 1 outright has moved both pins, and the result it gets back names only channel 1
        // — which is why the tool description sends it to list_analog_outputs to see them all.
        var (agent, _) = AgentHarness.WithConnectedDevice(analogOutputs: 2);
        await agent.SetAnalogOutputAsync(AgentHarness.DeviceId, 0, 3.0, latch: false);

        var result = await agent.SetAnalogOutputAsync(AgentHarness.DeviceId, 1, 6.0, latch: true);

        Assert.Equal(6.0, result.State!.Volts);
        var outputs = agent.ListAnalogOutputs(AgentHarness.DeviceId);
        Assert.Equal(3.0, outputs[0].Volts);
        Assert.Null(outputs[0].PendingVolts);
    }

    [Fact]
    public async Task LatchAnalogOutputs_WithNothingStaged_StillCommandsTheDevice()
    {
        // A caller that has lost track of what it staged can always land the device in a known
        // state; the device re-applies what it already holds.
        var (agent, device) = AgentHarness.WithConnectedDevice(analogOutputs: 2);
        device.ClearSent();

        var result = await agent.LatchAnalogOutputsAsync(AgentHarness.DeviceId);

        Assert.Equal(new[] { ScpiMessageProducer.UpdateDacOutputs.Data }, device.Sent);
        Assert.All(result.Outputs, o => Assert.Null(o.Volts));
    }

    [Fact]
    public async Task SetAnalogOutput_OutOfRange_IsRefusedBeforeAnythingIsSent()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice(analogOutputs: 2);
        device.ClearSent();

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => agent.SetAnalogOutputAsync(AgentHarness.DeviceId, 0, 42, latch: true));

        // The message has to carry the range: the firmware would have clamped 42 V silently, so
        // this is the only place an agent learns what the channel actually accepts.
        Assert.Contains("10", ex.Message);
        Assert.Empty(device.Sent);
        Assert.Null(agent.ListAnalogOutputs(AgentHarness.DeviceId)[0].PendingVolts);
    }

    [Fact]
    public async Task SetAnalogOutput_OnAnUnknownChannel_NamesTheOutputsThatExist()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice(analogOutputs: 2);
        device.ClearSent();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.SetAnalogOutputAsync(AgentHarness.DeviceId, 7, 1.0, latch: true));

        Assert.Contains("7", ex.Message);
        Assert.Contains("0, 1", ex.Message);
        Assert.Empty(device.Sent);
    }

    [Fact]
    public async Task SetAnalogOutput_OnABoardWithoutDacs_IsRefusedRatherThanSilentlyDiscarded()
    {
        // The failure this prevents: Nyquist 1 firmware rejects the DAC command internally, Core
        // never reads the error queue back, and the agent is told the write succeeded.
        var (agent, device) = AgentHarness.WithConnectedDevice();
        device.Metadata.DeviceType = DeviceType.Nyquist1;
        device.ClearSent();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.SetAnalogOutputAsync(AgentHarness.DeviceId, 0, 2.5, latch: true));

        Assert.Contains("Nyquist 3", ex.Message);
        Assert.Contains(nameof(DeviceType.Nyquist1), ex.Message);
        Assert.Empty(device.Sent);
    }

    [Fact]
    public async Task SetAnalogOutput_OnADeviceThatDescribedNoOutputs_WritesByNumberAndSaysItWasNotChecked()
    {
        // Nyquist 3 firmware below v3.5.0 has no capability document, and the document is the only
        // place a device describes its DACs. Refusing here would leave those boards with no
        // analog-output path at all — so the write goes out, flagged as unvouched-for.
        var (agent, device) = AgentHarness.WithConnectedDevice();
        Assert.Equal(DeviceType.Unknown, device.Metadata.DeviceType);
        device.ClearSent();

        var result = await agent.SetAnalogOutputAsync(AgentHarness.DeviceId, 3, 42, latch: true);

        Assert.False(result.RangeChecked);
        Assert.Null(result.State);
        Assert.True(result.Applied);
        Assert.Equal(
            new[]
            {
                ScpiMessageProducer.SetAnalogOutputVoltage(3, 42).Data,
                ScpiMessageProducer.UpdateDacOutputs.Data,
            },
            device.Sent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task SetAnalogOutput_OnANegativeChannel_SaysWhatAChannelNumberMayBe(int describedOutputs)
    {
        // The same answer whatever the device described. Folded into the modelled-channel checks
        // this reads as "-1 is not one of 0, 1" on one device and as something else on the other —
        // the same mistake explained two ways, neither of them saying what is actually allowed.
        var (agent, device) = AgentHarness.WithConnectedDevice(analogOutputs: describedOutputs);
        device.ClearSent();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.SetAnalogOutputAsync(AgentHarness.DeviceId, -1, 1.0, latch: true));

        Assert.Contains("0 or greater", ex.Message);
        Assert.Empty(device.Sent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ReadAnalogOutput_OnANegativeChannel_SaysWhatAChannelNumberMayBe(int describedOutputs)
    {
        var (agent, device) = AgentHarness.WithConnectedDevice(analogOutputs: describedOutputs);
        device.ClearSent();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.ReadAnalogOutputAsync(AgentHarness.DeviceId, -1, CancellationToken.None));

        Assert.Contains("0 or greater", ex.Message);
        Assert.Empty(device.Sent);
    }

    [Fact]
    public async Task ReadAnalogOutput_ReturnsWhatTheDeviceAnswersAndRecordsIt()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice(analogOutputs: 2);
        device.AnalogOutputReadbacks[1] = "3.750";
        device.ClearSent();

        var reading = await agent.ReadAnalogOutputAsync(AgentHarness.DeviceId, 1, CancellationToken.None);

        Assert.Equal(3.75, reading.Volts);
        Assert.Equal(new[] { ScpiMessageProducer.GetAnalogOutputVoltage(1).Data }, device.Sent);
        // Recorded on the channel, so list_analog_outputs agrees with the round-trip afterwards
        // instead of still reporting "nothing has driven this".
        Assert.Equal(3.75, agent.ListAnalogOutputs(AgentHarness.DeviceId)[1].Volts);
    }

    [Fact]
    public async Task ReadAnalogOutput_WhenTheDeviceAnswersNothing_Throws()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice(analogOutputs: 2);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.ReadAnalogOutputAsync(AgentHarness.DeviceId, 0, CancellationToken.None));

        Assert.Contains("not a voltage", ex.Message);
    }

    [Fact]
    public async Task ReadAnalogOutput_WhileStreaming_IsRefusedWithTheReason()
    {
        // The reply would arrive with binary stream frames welded onto the front of it and fail as
        // a parse error, which describes the collision without naming it.
        var (agent, device) = AgentHarness.WithConnectedDevice(analogOutputs: 2);
        device.AnalogOutputReadbacks[0] = "1.000";
        device.StartStreaming();
        device.ClearSent();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.ReadAnalogOutputAsync(AgentHarness.DeviceId, 0, CancellationToken.None));

        Assert.Contains("streaming", ex.Message);
        Assert.Empty(device.Sent);
    }

    [Fact]
    public async Task ReadAnalogOutput_IsAvailableInReadOnlyMode()
    {
        // It changes nothing on the device, so it sits with list_sd_files rather than with the
        // writes.
        var (agent, device) = AgentHarness.WithConnectedDevice(readOnly: true, analogOutputs: 2);
        device.AnalogOutputReadbacks[0] = "0.500";

        var reading = await agent.ReadAnalogOutputAsync(AgentHarness.DeviceId, 0, CancellationToken.None);

        Assert.Equal(0.5, reading.Volts);
    }

    [Fact]
    public async Task AnalogOutputTools_OnADeviceThatIsNotConnected_SaySo()
    {
        var agent = new DaqifiAgent(new ServerOptions());

        Assert.Throws<InvalidOperationException>(() => agent.ListAnalogOutputs("serial:NOPE"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.SetAnalogOutputAsync("serial:NOPE", 0, 1.0, latch: true));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.LatchAnalogOutputsAsync("serial:NOPE"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.ReadAnalogOutputAsync("serial:NOPE", 0, CancellationToken.None));
    }

    [Fact]
    public void AnalogOutputs_AreNotAcquisitionChannels()
    {
        // They share the channel collection with the inputs, so the guard that matters is that
        // nothing treats them as something to sample.
        var (agent, _) = AgentHarness.WithConnectedDevice(analogChannels: 4, analogOutputs: 2);

        var channels = agent.ListChannels(AgentHarness.DeviceId);

        Assert.Equal(2, channels.Count(c => c.Type == nameof(ChannelType.AnalogOutput)));
        Assert.All(
            channels.Where(c => c.Type == nameof(ChannelType.AnalogOutput)),
            c => Assert.False(c.Enabled));
        Assert.Empty(agent.GetStatus(AgentHarness.DeviceId).EnabledAnalogChannels);
    }
}
