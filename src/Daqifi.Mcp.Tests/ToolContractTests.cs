using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// Contract tests for the tools an agent can call once a device is connected (#465).
/// </summary>
/// <remarks>
/// The tool layer's job is argument validation, ordering, and state mapping, and its failures are
/// characteristically silent — an empty list, a rate reported as valid that the device will
/// refuse. These drive the real <see cref="DaqifiAgent"/> against a
/// <see cref="FakeStreamingDevice"/>, so what is asserted is what an agent would actually be
/// told and what the device would actually be sent.
/// </remarks>
public class IntrospectionToolContractTests
{
    [Fact]
    public void ListConnected_ReportsTheRegisteredDeviceAndItsChannelCounts()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice(analogChannels: 4, digitalChannels: 8);

        var listed = Assert.Single(agent.ListConnected());

        Assert.Equal(AgentHarness.DeviceId, listed.DeviceId);
        Assert.True(listed.Connected);
        Assert.Equal(4, listed.AnalogChannelCount);
        Assert.Equal(8, listed.DigitalChannelCount);
    }

    [Fact]
    public async Task GetStatus_ReflectsTheEnabledChannelsAndRate()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice();
        await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 2, 0 });
        await agent.ConfigureDigitalChannelsAsync(AgentHarness.DeviceId, new[] { 5 });
        await agent.SetSampleRateAsync(AgentHarness.DeviceId, 300);

        var status = agent.GetStatus(AgentHarness.DeviceId);

        Assert.Equal(AgentHarness.DeviceId, status.DeviceId);
        Assert.False(status.Streaming);
        Assert.False(status.LoggingToSdCard);
        Assert.Equal(300, status.SampleRateHz);
        // Sorted, not in the order they were asked for: an agent diffs these between calls.
        Assert.Equal(new[] { 0, 2 }, status.EnabledAnalogChannels);
        Assert.Equal(new[] { 5 }, status.EnabledDigitalChannels);
    }

    [Fact]
    public void ListChannels_ReportsEveryChannelWithItsTypeAndPwmCapability()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice(analogChannels: 2, digitalChannels: 8);

        var channels = agent.ListChannels(AgentHarness.DeviceId);

        Assert.Equal(10, channels.Count);
        Assert.Equal(2, channels.Count(c => c.Type == nameof(ChannelType.Analog)));

        // PWM capability is a hardware fact the agent has no other way to learn, and getting it
        // wrong sends an agent at a channel the firmware will half-arm and then refuse (#450).
        var pwmCapable = channels
            .Where(c => c.Type == nameof(ChannelType.Digital) && c.PwmCapable == true)
            .Select(c => c.ChannelNumber)
            .OrderBy(n => n);
        Assert.Equal(new[] { 0, 3, 4, 5, 6, 7 }, pwmCapable);

        // Analog channels carry no digital-only fields at all, rather than a misleading false.
        Assert.All(
            channels.Where(c => c.Type == nameof(ChannelType.Analog)),
            c =>
            {
                Assert.Null(c.PwmCapable);
                Assert.Null(c.PwmEnabled);
                Assert.Null(c.OutputValue);
            });
    }

    [Fact]
    public async Task DisconnectDevice_ReleasesTheDeviceAndForgetsIt()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice();

        var message = await agent.DisconnectAsync(AgentHarness.DeviceId);

        Assert.Contains("Disconnected", message);
        Assert.False(device.IsConnected);
        Assert.Empty(agent.ListConnected());

        // And the id is genuinely gone, not just hidden from the listing.
        var ex = Assert.Throws<InvalidOperationException>(() => agent.GetStatus(AgentHarness.DeviceId));
        Assert.Contains("connect_device", ex.Message);
    }

    [Fact]
    public void Tools_OnADeviceThatHasSinceDropped_SaySoAndReleaseIt()
    {
        // The silent version of this failure is the worst one: a handle that still answers
        // introspection from stale in-memory state while the device is gone.
        var (agent, device) = AgentHarness.WithConnectedDevice();
        device.Disconnect();

        var ex = Assert.Throws<InvalidOperationException>(() => agent.GetStatus(AgentHarness.DeviceId));
        Assert.Contains("no longer connected", ex.Message);

        // Evicted, so it does not linger in list_connected_devices as if it were usable.
        Assert.Empty(agent.ListConnected());
    }
}

public class AnalogConfigurationToolContractTests
{
    [Fact]
    public async Task ConfigureAnalogChannels_EnablesExactlyTheRequestedSet()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice(analogChannels: 4);

        var result = await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0, 2 });

        Assert.Equal(new[] { 0, 2 }, result.EnabledAnalogChannels);
        // Bit 0 + bit 2 = 5. Asserted on the wire, because "enabled" that never reached the
        // device is the shape of an MCP escape.
        Assert.Contains(ScpiMessageProducer.EnableAdcChannels("5").Data, device.Sent);
    }

    [Fact]
    public async Task ConfigureAnalogChannels_DisablesTheChannelsLeftOut()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice(analogChannels: 4);
        await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0, 1, 2 });
        device.ClearSent();

        var result = await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 1 });

        Assert.Equal(new[] { 1 }, result.EnabledAnalogChannels);
        Assert.Equal(ScpiMessageProducer.EnableAdcChannels("2").Data, device.Sent.Last());
    }

    [Fact]
    public async Task ConfigureAnalogChannels_WithAnEmptyList_DisablesEverything()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice(analogChannels: 4);
        await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0, 1 });

        var result = await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, Array.Empty<int>());

        Assert.Empty(result.EnabledAnalogChannels);
    }

    [Fact]
    public async Task ConfigureAnalogChannels_WithAnUnknownChannel_ChangesNothing()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice(analogChannels: 4);
        await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 1 });
        device.ClearSent();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0, 9 }));

        // The message has to name what does exist; an agent cannot see the board.
        Assert.Contains("9", ex.Message);
        Assert.Contains("0, 1, 2, 3", ex.Message);

        // All-or-nothing: a partly-applied selection is a configuration neither side asked for.
        Assert.Empty(device.Sent);
        Assert.Equal(new[] { 1 }, agent.GetStatus(AgentHarness.DeviceId).EnabledAnalogChannels);
    }

    [Fact]
    public async Task ConfigureAnalogChannels_RefreshesTheDeviceCapBeforeReportingARate()
    {
        // #447: the cap is scoped to the channel set that was live when the document was read, so
        // a configuration call that did not re-read it would validate against the previous set.
        var (agent, device) = AgentHarness.WithConnectedDevice();
        var before = device.CapabilityReads;

        await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0 });

        Assert.True(device.CapabilityReads > before);
    }

    [Fact]
    public async Task WideningTheChannelSet_LowersALiveRateThatNoLongerFits()
    {
        // The #447 escape in full: 2000 Hz is legal with one channel enabled (cap 20000) and
        // illegal with four (cap 5000). Left alone it stays live, is echoed back as valid, and
        // cannot even be re-set, because re-requesting the same value now fails.
        var (agent, _) = AgentHarness.WithConnectedDevice(analogChannels: 4);
        await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0 });
        await agent.SetSampleRateAsync(AgentHarness.DeviceId, 12_000);

        var result = await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0, 1, 2, 3 });

        Assert.Equal(12_000, result.SampleRateAdjustedFromHz);
        Assert.Equal(5_000, result.SampleRateHz);
        Assert.Equal(5_000, agent.GetStatus(AgentHarness.DeviceId).SampleRateHz);
    }

    [Fact]
    public async Task NarrowingTheChannelSet_LeavesAFittingRateAlone()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice(analogChannels: 4);
        await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0, 1, 2, 3 });
        await agent.SetSampleRateAsync(AgentHarness.DeviceId, 1_000);

        var result = await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0 });

        // Null means "nothing was adjusted" — an agent keys off it, so it must not be set to the
        // unchanged rate just because a re-validation ran.
        Assert.Null(result.SampleRateAdjustedFromHz);
        Assert.Equal(1_000, result.SampleRateHz);
    }

    [Fact]
    public async Task DisablingEveryChannel_LeavesTheLiveRateAloneRatherThanZeroingIt()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice();
        await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0 });
        await agent.SetSampleRateAsync(AgentHarness.DeviceId, 2_000);

        var result = await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, Array.Empty<int>());

        Assert.Null(result.SampleRateAdjustedFromHz);
        Assert.Equal(2_000, result.SampleRateHz);
    }
}

public class DigitalConfigurationToolContractTests
{
    [Fact]
    public async Task ConfigureDigitalChannels_EnablesExactlyTheRequestedSet()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice(digitalChannels: 8);

        var result = await agent.ConfigureDigitalChannelsAsync(AgentHarness.DeviceId, new[] { 3, 1 });

        Assert.Equal(new[] { 1, 3 }, result.EnabledDigitalChannels);
    }

    [Fact]
    public async Task ConfigureDigitalChannels_DisablesTheChannelsLeftOut()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice(digitalChannels: 8);
        await agent.ConfigureDigitalChannelsAsync(AgentHarness.DeviceId, new[] { 0, 1, 2 });

        var result = await agent.ConfigureDigitalChannelsAsync(AgentHarness.DeviceId, new[] { 2 });

        Assert.Equal(new[] { 2 }, result.EnabledDigitalChannels);
    }

    [Fact]
    public async Task ConfigureDigitalChannels_WithAnUnknownChannel_ChangesNothing()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice(digitalChannels: 8);
        device.ClearSent();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.ConfigureDigitalChannelsAsync(AgentHarness.DeviceId, new[] { 1, 42 }));

        Assert.Contains("42", ex.Message);
        Assert.Empty(device.Sent);
        Assert.Empty(agent.GetStatus(AgentHarness.DeviceId).EnabledDigitalChannels);
    }

    [Fact]
    public async Task ConfigureDigitalChannels_DoesNotDisturbTheAnalogSelection()
    {
        // The two tools share a channel collection; a digital call that swept analog channels up
        // with it would silently end an acquisition.
        var (agent, _) = AgentHarness.WithConnectedDevice();
        await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0, 1 });

        await agent.ConfigureDigitalChannelsAsync(AgentHarness.DeviceId, new[] { 4 });

        var status = agent.GetStatus(AgentHarness.DeviceId);
        Assert.Equal(new[] { 0, 1 }, status.EnabledAnalogChannels);
        Assert.Equal(new[] { 4 }, status.EnabledDigitalChannels);
    }
}

public class DigitalPinToolContractTests
{
    [Fact]
    public async Task SetDigitalDirection_DrivesTheChannelItWasAskedAbout()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice();

        var result = await agent.SetDigitalDirectionAsync(AgentHarness.DeviceId, 2, "output");

        Assert.Equal(2, result.Channel);
        Assert.Equal(nameof(ChannelDirection.Output), result.Direction);
        Assert.Contains(ScpiMessageProducer.SetDioPortDirection(2, 1).Data, device.Sent);
    }

    [Fact]
    public async Task SetDigitalDirection_OnAnUnknownChannel_NamesTheChannelsThatExist()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice(digitalChannels: 4);
        device.ClearSent();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.SetDigitalDirectionAsync(AgentHarness.DeviceId, 11, "output"));

        Assert.Contains("11", ex.Message);
        Assert.Contains("0, 1, 2, 3", ex.Message);
        Assert.Empty(device.Sent);
    }

    [Fact]
    public async Task SetDigitalOutput_OnAnInputChannel_SwitchesDirectionBeforeDrivingIt()
    {
        // One tool call is documented as enough to drive a pin. The order is the contract: a
        // value written while the pin is still an input goes nowhere.
        var (agent, device) = AgentHarness.WithConnectedDevice();
        device.ClearSent();

        var result = await agent.SetDigitalOutputAsync(AgentHarness.DeviceId, 1, high: true);

        Assert.True(result.OutputValue);
        Assert.Equal(nameof(ChannelDirection.Output), result.Direction);

        var direction = device.Sent.ToList().IndexOf(ScpiMessageProducer.SetDioPortDirection(1, 1).Data);
        var value = device.Sent.ToList().IndexOf(ScpiMessageProducer.SetDioPortState(1, 1).Data);
        Assert.True(direction >= 0, "the direction command was never sent");
        Assert.True(value >= 0, "the value command was never sent");
        Assert.True(direction < value, "the value was driven before the pin was an output");
    }

    [Fact]
    public async Task SetDigitalOutput_OnAChannelAlreadyAnOutput_DoesNotResendTheDirection()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice();
        await agent.SetDigitalDirectionAsync(AgentHarness.DeviceId, 1, "output");
        device.ClearSent();

        await agent.SetDigitalOutputAsync(AgentHarness.DeviceId, 1, high: false);

        Assert.DoesNotContain(ScpiMessageProducer.SetDioPortDirection(1, 1).Data, device.Sent);
        Assert.Contains(ScpiMessageProducer.SetDioPortState(1, 0).Data, device.Sent);
    }

    [Theory]
    [InlineData("direction")]
    [InlineData("output")]
    public async Task DigitalWrites_AreRefusedWhilePwmRunsOnTheChannel(string operation)
    {
        // #449: the firmware ignores direction/state writes while PWM is running, so a tool that
        // forwarded them would report success for a pin that never moved.
        var (agent, device) = AgentHarness.WithConnectedDevice();
        await agent.SetPwmOutputAsync(AgentHarness.DeviceId, 4, dutyCyclePercent: 50, frequencyHz: 1000);
        device.ClearSent();

        Task Call() => operation == "direction"
            ? agent.SetDigitalDirectionAsync(AgentHarness.DeviceId, 4, "output")
            : agent.SetDigitalOutputAsync(AgentHarness.DeviceId, 4, high: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(Call);

        // Names the tool an agent actually has, and only that: Core's own guard would have said
        // "SetPwmEnabled(channel, false)", an SDK method an MCP caller has no way to reach.
        Assert.Contains("disable_pwm", ex.Message);
        Assert.DoesNotContain("SetPwmEnabled", ex.Message);
        Assert.Empty(device.Sent);
    }

    [Fact]
    public async Task DigitalWrites_ResumeOncePwmIsDisabled()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice();
        await agent.SetPwmOutputAsync(AgentHarness.DeviceId, 4, dutyCyclePercent: 50, frequencyHz: 1000);
        await agent.DisablePwmAsync(AgentHarness.DeviceId, 4);
        device.ClearSent();

        var result = await agent.SetDigitalOutputAsync(AgentHarness.DeviceId, 4, high: true);

        Assert.True(result.OutputValue);
        Assert.Contains(ScpiMessageProducer.SetDioPortState(4, 1).Data, device.Sent);
    }
}

public class PwmToolContractTests
{
    [Fact]
    public async Task SetPwmOutput_SendsDutyThenFrequencyThenEnable()
    {
        // The firmware applies the stored duty when the frequency is programmed, so this order is
        // what keeps a stale compare value from being latched.
        var (agent, device) = AgentHarness.WithConnectedDevice();
        device.ClearSent();

        var result = await agent.SetPwmOutputAsync(AgentHarness.DeviceId, 4, dutyCyclePercent: 25, frequencyHz: 2000);

        var sent = device.Sent.ToList();
        var duty = sent.IndexOf(ScpiMessageProducer.SetPwmChannelDutyCycle(4, 25).Data);
        var frequency = sent.IndexOf(ScpiMessageProducer.SetPwmChannelFrequency(0, 2000).Data);
        var enable = sent.IndexOf(ScpiMessageProducer.SetPwmChannelEnabled(4, true).Data);

        Assert.True(duty >= 0 && frequency >= 0 && enable >= 0, $"missing command in: {string.Join(" | ", sent)}");
        Assert.True(duty < frequency, "the frequency was programmed before the duty was stored");
        Assert.True(frequency < enable, "the channel was enabled before the timer was programmed");

        Assert.True(result.Enabled);
        Assert.Equal(25, result.DutyCyclePercent);
        Assert.Equal(2000, result.FrequencyHz);
    }

    [Fact]
    public async Task SetPwmOutput_WithFrequencyZero_KeepsTheSessionFrequency()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice();
        await agent.SetPwmOutputAsync(AgentHarness.DeviceId, 4, dutyCyclePercent: 40, frequencyHz: 7000);
        device.ClearSent();

        var result = await agent.SetPwmOutputAsync(AgentHarness.DeviceId, 5, dutyCyclePercent: 60, frequencyHz: 0);

        // The frequency is one device-wide timer; asking for 0 means "leave it", not "0 Hz".
        Assert.Equal(7000, result.FrequencyHz);
        Assert.DoesNotContain(device.Sent, c => c.StartsWith("PWM:CHannel:FREQuency", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetPwmOutput_OnANonCapableChannel_IsRefusedBeforeTheChannelIsArmed()
    {
        // The firmware flags a channel PWM-active before its own capability check fails, and never
        // rolls that back — so an enable that reaches a non-capable channel leaves it dead to
        // digital writes. Nothing at all may go out.
        var (agent, device) = AgentHarness.WithConnectedDevice();
        device.ClearSent();

        // Channel 1 is not one of the output-compare channels (0, 3, 4, 5, 6, 7).
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => agent.SetPwmOutputAsync(AgentHarness.DeviceId, 1, dutyCyclePercent: 50, frequencyHz: 1000));

        Assert.Contains("0, 3, 4, 5, 6, 7", ex.Message);
        Assert.DoesNotContain(device.Sent, c => c.StartsWith("PWM:CHannel:ENable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisablePwm_IsAcceptedOnANonCapableChannel()
    {
        // The firmware can flag a channel PWM-active before failing its own capability check, and
        // this is the only command that clears it — so it must not be gated on capability.
        var (agent, device) = AgentHarness.WithConnectedDevice();
        device.ClearSent();

        var result = await agent.DisablePwmAsync(AgentHarness.DeviceId, 1);

        Assert.False(result.Enabled);
        Assert.Contains(ScpiMessageProducer.SetPwmChannelEnabled(1, false).Data, device.Sent);
    }

    [Fact]
    public async Task DisablePwm_BeforeAnythingWasCommanded_ReportsNoDutyOrFrequency()
    {
        // #450: Core seeds duty and frequency with session defaults that look exactly like real
        // device state. Reporting them would be the tool inventing a reading.
        var (agent, _) = AgentHarness.WithConnectedDevice();

        var result = await agent.DisablePwmAsync(AgentHarness.DeviceId, 4);

        Assert.Null(result.DutyCyclePercent);
        Assert.Null(result.FrequencyHz);
    }

    [Fact]
    public async Task DisablePwm_AfterSetPwmOutput_ReportsWhatWasCommanded()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice();
        await agent.SetPwmOutputAsync(AgentHarness.DeviceId, 4, dutyCyclePercent: 35, frequencyHz: 900);

        var result = await agent.DisablePwmAsync(AgentHarness.DeviceId, 4);

        Assert.False(result.Enabled);
        Assert.Equal(35, result.DutyCyclePercent);
        Assert.Equal(900, result.FrequencyHz);
    }

    [Fact]
    public async Task DisablePwm_OnOneChannel_StillReportsTheDeviceWideFrequency()
    {
        // The frequency was commanded for the device, not for the channel, so it stays reportable
        // on a channel that never had a duty set of its own.
        var (agent, _) = AgentHarness.WithConnectedDevice();
        await agent.SetPwmOutputAsync(AgentHarness.DeviceId, 4, dutyCyclePercent: 35, frequencyHz: 900);

        var result = await agent.DisablePwmAsync(AgentHarness.DeviceId, 6);

        Assert.Equal(900, result.FrequencyHz);
        Assert.Null(result.DutyCyclePercent);
    }

    [Fact]
    public async Task PwmTools_OnAnUnknownChannel_NameTheChannelsThatExist()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice(digitalChannels: 8);

        var setEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.SetPwmOutputAsync(AgentHarness.DeviceId, 30, dutyCyclePercent: 50, frequencyHz: 1000));
        Assert.Contains("Unknown digital channel 30", setEx.Message);

        var disableEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.DisablePwmAsync(AgentHarness.DeviceId, 30));
        Assert.Contains("Unknown digital channel 30", disableEx.Message);
    }
}

public class SampleRateToolContractTests
{
    [Fact]
    public async Task SetSampleRate_AtExactlyTheCap_IsAccepted()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice();
        await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0, 1 });

        var result = await agent.SetSampleRateAsync(AgentHarness.DeviceId, 10_000);

        Assert.Equal(10_000, result.RequestedRateHz);
        Assert.Equal(10_000, agent.GetStatus(AgentHarness.DeviceId).SampleRateHz);
    }

    [Fact]
    public async Task SetSampleRate_AboveTheCap_IsRejectedAndLeavesTheLiveRateAlone()
    {
        // Rejected rather than silently clamped: the device answers an over-ask with SCPI -222
        // and streams nothing, so an agent told "ok, 10000" would wait forever for samples.
        var (agent, _) = AgentHarness.WithConnectedDevice();
        await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0, 1 });
        await agent.SetSampleRateAsync(AgentHarness.DeviceId, 400);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.SetSampleRateAsync(AgentHarness.DeviceId, 10_001));

        Assert.Contains("10000", ex.Message);
        Assert.Equal(400, agent.GetStatus(AgentHarness.DeviceId).SampleRateHz);
    }

    [Fact]
    public async Task SetSampleRate_WithNothingEnabled_SaysToEnableAChannel()
    {
        // A cap of 0 is a real answer, and "exceeds the maximum 0 Hz" would point an agent at
        // lowering the rate when the remedy is enabling a channel.
        var (agent, _) = AgentHarness.WithConnectedDevice();
        await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, Array.Empty<int>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.SetSampleRateAsync(AgentHarness.DeviceId, 100));

        Assert.Contains("No channels are enabled", ex.Message);
        Assert.Contains("configure_analog_channels", ex.Message);
        Assert.DoesNotContain("exceeds the maximum", ex.Message);
    }

    [Fact]
    public async Task SetSampleRate_HonoursTheServerWideClamp()
    {
        var (agent, _) = AgentHarness.WithConnectedDevice(maxSampleRateHz: 250);
        await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0 });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.SetSampleRateAsync(AgentHarness.DeviceId, 251));
        Assert.Contains("250", ex.Message);

        var ok = await agent.SetSampleRateAsync(AgentHarness.DeviceId, 250);
        Assert.Equal(250, ok.RequestedRateHz);
    }

    [Fact]
    public async Task ConfiguringChannels_AlsoEnforcesTheServerWideClamp()
    {
        // The operator's clamp has to bind on the re-validation path too, or a channel change
        // could leave a rate above it live and reported as adjusted-and-fine.
        var (agent, _) = AgentHarness.WithConnectedDevice(maxSampleRateHz: 300);
        await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0 });
        await agent.SetSampleRateAsync(AgentHarness.DeviceId, 300);

        var result = await agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0, 1 });

        Assert.Null(result.SampleRateAdjustedFromHz);
        Assert.Equal(300, result.SampleRateHz);
    }

    [Fact]
    public async Task SetSampleRate_BelowOne_IsRejectedBeforeTheDeviceIsTouched()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice();
        device.ClearSent();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.SetSampleRateAsync(AgentHarness.DeviceId, 0));

        Assert.Empty(device.Sent);
    }
}

public class ReadOnlyModeContractTests
{
    /// <summary>
    /// Every mutating tool, refused before it reaches a device that is genuinely connected. The
    /// existing no-device tests cannot tell a real refusal from "there was nothing to do anyway".
    /// </summary>
    public static TheoryData<string> MutatingTools() => new()
    {
        "configure_analog_channels",
        "configure_digital_channels",
        "set_digital_direction",
        "set_digital_output",
        "set_pwm_output",
        "disable_pwm",
        "set_sample_rate",
    };

    [Theory]
    [MemberData(nameof(MutatingTools))]
    public async Task MutatingTools_AreRefusedAndSendNothing(string tool)
    {
        var (agent, device) = AgentHarness.WithConnectedDevice(readOnly: true);
        device.ClearSent();

        Task Call() => tool switch
        {
            "configure_analog_channels" => agent.ConfigureAnalogChannelsAsync(AgentHarness.DeviceId, new[] { 0 }),
            "configure_digital_channels" => agent.ConfigureDigitalChannelsAsync(AgentHarness.DeviceId, new[] { 0 }),
            "set_digital_direction" => agent.SetDigitalDirectionAsync(AgentHarness.DeviceId, 0, "output"),
            "set_digital_output" => agent.SetDigitalOutputAsync(AgentHarness.DeviceId, 0, high: true),
            "set_pwm_output" => agent.SetPwmOutputAsync(AgentHarness.DeviceId, 4, 50, 1000),
            "disable_pwm" => agent.DisablePwmAsync(AgentHarness.DeviceId, 4),
            _ => agent.SetSampleRateAsync(AgentHarness.DeviceId, 100),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(Call);
        Assert.Contains("read-only", ex.Message);
        Assert.Empty(device.Sent);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("channels")]
    [InlineData("list")]
    public void Introspection_StillWorksInReadOnlyMode(string tool)
    {
        var (agent, _) = AgentHarness.WithConnectedDevice(readOnly: true);

        switch (tool)
        {
            case "status":
                Assert.Equal(AgentHarness.DeviceId, agent.GetStatus(AgentHarness.DeviceId).DeviceId);
                break;
            case "channels":
                Assert.NotEmpty(agent.ListChannels(AgentHarness.DeviceId));
                break;
            default:
                Assert.Single(agent.ListConnected());
                break;
        }
    }
}
