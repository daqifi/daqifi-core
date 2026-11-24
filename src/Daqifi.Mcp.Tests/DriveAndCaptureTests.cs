using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// Tests for the schedule behind <c>drive_and_capture</c>: when the edge goes out, and what state
/// the pin is left in on each way a capture can end.
/// </summary>
/// <remarks>
/// No assertion here bounds how <i>late</i> a write may be. The schedule is host-timed and a
/// starved CI runner can hold it for seconds; what is pinned is the order, the floor (never before
/// its delay) and the final level, which are the parts that hold however slow the machine is.
/// </remarks>
public class DigitalStimulusTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    // The delay is the baseline the rows hold before the edge, so it cannot start counting until
    // there are rows. A stream that never produces a sample never gets an edge either.
    [Fact]
    public async Task TheEdge_WaitsForTheFirstSample()
    {
        var writes = new Writes();
        var stimulus = new DigitalStimulus(writes.Drive, level: true, TimeSpan.Zero, pulseWidth: null);

        stimulus.Start();
        await Task.Delay(100);
        Assert.Empty(writes.Levels);

        stimulus.OnFirstSample();
        await writes.WaitForCountAsync(1, Bound);
        await stimulus.FinishAsync(restoreStep: false);

        Assert.Equal(new[] { true }, writes.Levels);
        Assert.NotNull(stimulus.EdgeIssuedAtMs);
    }

    [Fact]
    public async Task TheEdge_IsNotIssuedBeforeItsDelay()
    {
        var writes = new Writes();
        var stimulus = new DigitalStimulus(writes.Drive, level: true, TimeSpan.FromMilliseconds(150), pulseWidth: null);

        stimulus.Start();
        stimulus.OnFirstSample();
        await writes.WaitForCountAsync(1, Bound);
        await stimulus.FinishAsync(restoreStep: false);

        // A few milliseconds of slack: a timer may fire a tick early, and the report is rounded.
        Assert.InRange(stimulus.EdgeIssuedAtMs!.Value, 140, int.MaxValue);
    }

    [Fact]
    public async Task APulse_ReturnsThePinAfterItsWidth()
    {
        var writes = new Writes();
        var stimulus = new DigitalStimulus(
            writes.Drive, level: false, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));

        stimulus.Start();
        stimulus.OnFirstSample();
        await writes.WaitForCountAsync(2, Bound);
        await stimulus.FinishAsync(restoreStep: false);

        // An active-low pulse: driven low, then back high. Released once, not again on the way out.
        Assert.Equal(new[] { false, true }, writes.Levels);
        Assert.False(stimulus.IsAsserted);
        Assert.InRange(stimulus.ReleaseIssuedAtMs!.Value - stimulus.EdgeIssuedAtMs!.Value, 90, int.MaxValue);
    }

    // The capture ended (its window, an error, a cancel) while the pulse was still high. Whatever
    // is wired to the pin must not be left driven because the read around it stopped.
    [Fact]
    public async Task Finish_ReleasesAPulseThatIsStillAsserted()
    {
        var writes = new Writes();
        var stimulus = new DigitalStimulus(writes.Drive, level: true, TimeSpan.Zero, TimeSpan.FromHours(1));

        stimulus.Start();
        stimulus.OnFirstSample();
        await writes.WaitForCountAsync(1, Bound);
        await stimulus.FinishAsync(restoreStep: false).WaitAsync(Bound);

        Assert.Equal(new[] { true, false }, writes.Levels);
        Assert.NotNull(stimulus.ReleaseIssuedAtMs);
        Assert.False(stimulus.IsAsserted);
    }

    // A step is meant to stay — that is what makes it a step — unless the call it belonged to
    // failed, in which case nobody is going to be told the output is asserted.
    [Theory]
    [InlineData(false, new[] { true })]
    [InlineData(true, new[] { true, false })]
    public async Task Finish_LeavesAStepInPlace_UnlessTheCaptureDidNotComplete(bool restoreStep, bool[] expected)
    {
        var writes = new Writes();
        var stimulus = new DigitalStimulus(writes.Drive, level: true, TimeSpan.Zero, pulseWidth: null);

        stimulus.Start();
        stimulus.OnFirstSample();
        await writes.WaitForCountAsync(1, Bound);
        await stimulus.FinishAsync(restoreStep).WaitAsync(Bound);

        Assert.Equal(expected, writes.Levels);
        Assert.Equal(!restoreStep, stimulus.IsAsserted);
    }

    [Fact]
    public async Task Finish_BeforeTheEdgeWasDue_NeverTouchesThePin()
    {
        var writes = new Writes();
        var stimulus = new DigitalStimulus(writes.Drive, level: true, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        stimulus.Start();
        stimulus.OnFirstSample();
        await stimulus.FinishAsync(restoreStep: true).WaitAsync(Bound);

        Assert.Empty(writes.Levels);
        Assert.Null(stimulus.EdgeIssuedAtMs);
        Assert.Null(stimulus.ReleaseIssuedAtMs);
        Assert.False(stimulus.IsAsserted);
    }

    // A write that throws may still have reached the device, so the baseline is attempted anyway —
    // for a step as much as for a pulse, because the call is now going to fail and nobody will be
    // told the output is asserted. The failure is not swallowed, or a pin that never moved would
    // be reported as driven.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Finish_WhenTheEdgeWriteFailed_StillPutsThePinBackAndSurfacesTheFailure(bool pulse)
    {
        var writes = new Writes { Fails = (level, _) => level };
        var stimulus = new DigitalStimulus(
            writes.Drive, level: true, TimeSpan.Zero, pulse ? TimeSpan.FromHours(1) : null);

        stimulus.Start();
        stimulus.OnFirstSample();
        await writes.WaitForCountAsync(1, Bound);

        // restoreStep: false, as it is when the capture itself completed — the schedule failing is
        // what makes this a failed call.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => stimulus.FinishAsync(restoreStep: false).WaitAsync(Bound));
        Assert.Equal("write 0 failed", ex.Message);
        Assert.Equal(new[] { true, false }, writes.Levels);
        Assert.False(stimulus.IsAsserted);
    }

    // The pulse's own release threw. It has not been released, so it must not read as released:
    // the way out gets a second go at the pin, and a transient failure does not leave it driven.
    [Fact]
    public async Task AScheduledReleaseThatFails_IsRetriedOnTheWayOut()
    {
        var writes = new Writes { Fails = (level, attempt) => !level && attempt == 1 };
        var stimulus = new DigitalStimulus(writes.Drive, level: true, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));

        stimulus.Start();
        stimulus.OnFirstSample();
        await writes.WaitForCountAsync(2, Bound);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => stimulus.FinishAsync(restoreStep: false).WaitAsync(Bound));

        // The original failure is the one reported, and the retry went out.
        Assert.Equal("write 1 failed", ex.Message);
        Assert.Equal(new[] { true, false, false }, writes.Levels);
        Assert.False(stimulus.IsAsserted);
        Assert.NotNull(stimulus.ReleaseIssuedAtMs);
    }

    [Fact]
    public async Task WhenThePinCannotBePutBackEither_TheFirstFailureIsTheOneReported()
    {
        var writes = new Writes { Fails = (_, _) => true };
        var stimulus = new DigitalStimulus(writes.Drive, level: true, TimeSpan.Zero, pulseWidth: null);

        stimulus.Start();
        stimulus.OnFirstSample();
        await writes.WaitForCountAsync(1, Bound);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => stimulus.FinishAsync(restoreStep: false).WaitAsync(Bound));

        Assert.Equal("write 0 failed", ex.Message);
        Assert.Equal(new[] { true, false }, writes.Levels);
        // Still the edge's level as far as anything here knows, and it says so.
        Assert.True(stimulus.IsAsserted);
    }

    // The delay's timer fired just as the capture ended, so the schedule resumes normally rather
    // than cancelled. Without a decision shared with the capture's end it would go on to drive the
    // pin — after the rows it was meant to be in had been returned, and for a step, for good.
    [Fact]
    public async Task AnEdgeWhoseWaitCompletedAsTheCaptureEnded_IsNeverIssued()
    {
        var writes = new Writes();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A wait that ignores cancellation, completing only when the test says: the timer that
        // had already fired.
        Task Wait(TimeSpan _, CancellationToken __)
        {
            waiting.TrySetResult();
            return timer.Task;
        }

        var stimulus = new DigitalStimulus(writes.Drive, level: true, TimeSpan.FromHours(1), pulseWidth: null, Wait);

        stimulus.Start();
        stimulus.OnFirstSample();
        await waiting.Task.WaitAsync(Bound);

        // The capture ends first...
        var finish = stimulus.FinishAsync(restoreStep: false);
        // ...and only then does the schedule get to run past its wait.
        timer.SetResult();
        await finish.WaitAsync(Bound);

        Assert.Empty(writes.Levels);
        Assert.Null(stimulus.EdgeIssuedAtMs);
        Assert.False(stimulus.IsAsserted);
    }

    [Fact]
    public void FirstSampleTap_ReportsOnlyTheFirstSample_AndPassesEveryOneThrough()
    {
        var inner = new LatestValueSink(new[] { new ChannelKey(ChannelType.Analog, 0), new ChannelKey(ChannelType.Analog, 1) });
        var firsts = 0;
        var tap = new FirstSampleTap(inner, () => firsts++);

        tap.Add(LiveSampleCaptureTests.Sample(ChannelType.Analog, 0, 1.0));
        Assert.False(tap.IsComplete);
        tap.Add(LiveSampleCaptureTests.Sample(ChannelType.Analog, 1, 2.0));
        tap.Add(LiveSampleCaptureTests.Sample(ChannelType.Analog, 7, 3.0));

        Assert.Equal(1, firsts);
        Assert.True(tap.IsComplete);
        Assert.Equal(1, tap.UnexpectedSampleCount);
        Assert.Equal(2, inner.ReportedChannelCount);
    }

    [Theory]
    // delay, pulse, window -> delay, pulse (null = a step)
    [InlineData(200, 0, 1000, 200, null)]
    [InlineData(200, -1, 1000, 200, null)]
    [InlineData(-5, 100, 1000, 0, 100)]
    [InlineData(5000, 0, 1000, 1000, null)]
    [InlineData(800, 300, 1000, 800, 200)]
    // Nothing is left after an edge at the very end, but a pulse was asked for, so it stays one.
    [InlineData(1000, 50, 1000, 1000, 1)]
    public void Stimulus_IsClampedIntoTheCaptureWindow(
        int delayMs, int pulseWidthMs, int windowMs, int expectedDelayMs, int? expectedPulseMs)
    {
        var (delay, pulseWidth) = DaqifiAgent.ClampStimulus(delayMs, pulseWidthMs, TimeSpan.FromMilliseconds(windowMs));

        Assert.Equal(expectedDelayMs, delay.TotalMilliseconds);
        Assert.Equal(expectedPulseMs, (int?)pulseWidth?.TotalMilliseconds);
    }

    /// <summary>A pin that records every level it was driven to, in order.</summary>
    private sealed class Writes
    {
        private readonly List<bool> _levels = new();

        /// <summary>
        /// Decides, from the level and the write's position (0 for the first), whether that write
        /// throws — after it has been recorded, since a write that throws may still have landed.
        /// </summary>
        internal Func<bool, int, bool>? Fails { get; init; }

        internal bool[] Levels
        {
            get
            {
                lock (_levels)
                {
                    return _levels.ToArray();
                }
            }
        }

        internal void Drive(bool level)
        {
            int attempt;
            lock (_levels)
            {
                attempt = _levels.Count;
                _levels.Add(level);
            }

            if (Fails?.Invoke(level, attempt) == true)
            {
                throw new InvalidOperationException($"write {attempt} failed");
            }
        }

        internal async Task WaitForCountAsync(int count, TimeSpan bound)
        {
            using var timeout = new CancellationTokenSource(bound);
            while (Levels.Length < count)
            {
                await Task.Delay(5, timeout.Token);
            }
        }
    }
}

/// <summary>
/// Contract tests for <c>drive_and_capture</c> against a connected device double: what reaches the
/// wire, in what order, and where the pin is left.
/// </summary>
/// <remarks>
/// A capture that is expected to contain an edge is never ended by its window here. The window
/// is this machine's clock and so is the stimulus, and on a starved CI runner the schedule can
/// be held for seconds — a 500 ms window closed before a 50 ms delay had fired (Windows leg of
/// #1026). Those captures run under a window far longer than the test could wait and are ended
/// by their row budget instead, which <see cref="SamplePump"/> only fills once the pin has seen
/// every write the test is about. However slow the machine, the edge is in the capture.
/// </remarks>
public class DriveAndCaptureContractTests
{
    private const string Id = AgentHarness.DeviceId;
    private const int Pin = 3;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    /// <summary>A capture window no test waits out: a hang guard, where the row budget is the real end.</summary>
    private const int NeverMs = 60_000;

    /// <summary>The row budget <see cref="SamplePump"/> fills to end a capture; well above what it trickles first.</summary>
    private const int RowBudget = 500;

    private static readonly string Output = ScpiMessageProducer.SetDioPortDirection(Pin, 1).Data;
    private static readonly string High = ScpiMessageProducer.SetDioPortState(Pin, 1).Data;
    private static readonly string Low = ScpiMessageProducer.SetDioPortState(Pin, 0).Data;
    private static readonly string StopStream = ScpiMessageProducer.StopStreaming.Data;

    // The whole point of the tool: the edge is inside the stream, not before or after it. A
    // capture holds the device exclusively, so nothing but the capture itself can put it there.
    [Fact]
    public async Task APulse_IsDrivenAndReleasedInsideTheCapture()
    {
        var (agent, device) = await ReadyDeviceAsync();
        using var pump = new SamplePump(device, pourWhen: AfterPinWrites(device, 4));

        var result = await agent.DriveAndCaptureAsync(
            Id, Pin, high: true, delayMs: 50, pulseWidthMs: 50, NeverMs, RowBudget, CancellationToken.None)
            .WaitAsync(Bound);

        var sent = device.Sent.ToList();
        var start = sent.FindIndex(IsStreamStart);
        var stop = sent.IndexOf(StopStream);
        Assert.True(start >= 0 && stop > start, $"the capture never ran its own stream: {string.Join(" | ", sent)}");

        // Baseline before the stream starts: output, then low — the same order set_digital_output uses.
        Assert.Equal(new[] { Output, Low }, sent.Take(start).Where(IsPinCommand));
        // The pulse, and nothing else on the pin, between the start and the stop.
        Assert.Equal(new[] { High, Low }, sent.Skip(start).Take(stop - start).Where(IsPinCommand));
        Assert.DoesNotContain(sent.Skip(stop), IsPinCommand);

        Assert.Equal(Pin, result.Channel);
        Assert.True(result.High);
        Assert.Equal(50, result.DelayMs);
        Assert.Equal(50, result.PulseWidthMs);
        Assert.InRange(result.EdgeIssuedAtMs!.Value, 40, int.MaxValue);
        Assert.InRange(result.ReleaseIssuedAtMs!.Value - result.EdgeIssuedAtMs.Value, 40, int.MaxValue);
        Assert.False(result.LastCommandedHigh);

        Assert.True(result.Capture.StartedStream);
        Assert.Equal(new[] { "AI0" }, result.Capture.Columns);
        Assert.True(result.Capture.RowCount > 0);
    }

    [Fact]
    public async Task AStep_IsLeftAtTheNewLevel_AndSaysSo()
    {
        var (agent, device) = await ReadyDeviceAsync();
        using var pump = new SamplePump(device, pourWhen: AfterPinWrites(device, 3));

        // Active-low this time, so the baseline is high and the edge is the fall.
        var result = await agent.DriveAndCaptureAsync(
            Id, Pin, high: false, delayMs: 50, pulseWidthMs: 0, NeverMs, RowBudget, CancellationToken.None)
            .WaitAsync(Bound);

        Assert.Equal(new[] { Output, High, Low }, device.Sent.Where(IsPinCommand));
        Assert.Null(result.PulseWidthMs);
        Assert.NotNull(result.EdgeIssuedAtMs);
        Assert.Null(result.ReleaseIssuedAtMs);
        Assert.False(result.LastCommandedHigh);
    }

    // Core parks a Send made by any flow that does not own the device's operation lock until the
    // exclusive block ends — for this tool, after the capture the edge was meant to be in. The
    // double records commands without going through that path, so the ownership is asked for
    // directly: an exclusive block entered by the owner runs at once, anyone else's waits.
    [Fact]
    public async Task EveryPinWrite_IsMadeByTheFlowHoldingTheDevice_SoCoreSendsItRatherThanParkingIt()
    {
        var (agent, device) = await ReadyDeviceAsync();
        using var pump = new SamplePump(device, pourWhen: AfterPinWrites(device, 4));

        var heldTheDevice = new List<(string Command, bool Held)>();
        device.OnSend = command =>
        {
            if (IsPinCommand(command))
            {
                var nested = device.RunExclusiveAsync(_ => Task.CompletedTask);
                lock (heldTheDevice)
                {
                    heldTheDevice.Add((command, nested.IsCompletedSuccessfully));
                }
            }
        };

        await agent.DriveAndCaptureAsync(
            Id, Pin, high: true, delayMs: 20, pulseWidthMs: 20, NeverMs, RowBudget, CancellationToken.None)
            .WaitAsync(Bound);

        Assert.Equal(new[] { Output, Low, High, Low }, heldTheDevice.Select(w => w.Command));
        Assert.All(heldTheDevice, w => Assert.True(w.Held, $"{w.Command} was sent from a flow that does not hold the device"));
    }

    // Cancelled with the output asserted. A pulse is released because it always is; a step is put
    // back because the call it belonged to never returned, so nobody was told it is asserted.
    [Theory]
    [InlineData(60_000)]
    [InlineData(0)]
    public async Task ACancelWhileThePinIsAsserted_PutsItBackBeforeTheStreamIsStopped(int pulseWidthMs)
    {
        var (agent, device) = await ReadyDeviceAsync();
        using var pump = new SamplePump(device);
        using var cts = new CancellationTokenSource();

        var call = agent.DriveAndCaptureAsync(
            Id, Pin, high: true, delayMs: 0, pulseWidthMs, NeverMs, RowBudget, cts.Token);

        await WaitUntilAsync(() => device.Sent.Contains(High));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Bound));

        var sent = device.Sent.ToList();
        Assert.Equal(new[] { Output, Low, High, Low }, sent.Where(IsPinCommand));
        Assert.True(sent.LastIndexOf(Low) < sent.IndexOf(StopStream), "the stream was stopped before the pin was released");
        Assert.False(device.IsStreaming);
    }

    // The step's own write failed. The capture around it completed, so nothing else would undo
    // it — and a write that threw may have reached the pin. The call fails, and the pin goes back.
    [Fact]
    public async Task AStepWhoseWriteFails_FailsTheCall_AndPutsThePinBack()
    {
        var (agent, device) = await ReadyDeviceAsync();
        var attempted = false;
        device.OnSend = command =>
        {
            if (command == High)
            {
                attempted = true;
                throw new InvalidOperationException("the edge write failed");
            }
        };
        using var pump = new SamplePump(device, pourWhen: () => Volatile.Read(ref attempted));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.DriveAndCaptureAsync(
                Id, Pin, high: true, delayMs: 20, pulseWidthMs: 0, NeverMs, RowBudget, CancellationToken.None)
                .WaitAsync(Bound));

        Assert.Equal("the edge write failed", ex.Message);
        // The double records a command only once it was accepted, so the failed High is absent
        // and the second Low is the pin being put back.
        Assert.Equal(new[] { Output, Low, Low }, device.Sent.Where(IsPinCommand));
    }

    // The very last thing the call does — stopping the stream it started — failed, after a step
    // that had rightly been left in place. The call now fails, so the step is undone after all.
    [Fact]
    public async Task AStepFollowedByAFailedStreamStop_FailsTheCall_AndPutsThePinBack()
    {
        var (agent, device) = await ReadyDeviceAsync();
        device.OnSend = command =>
        {
            if (command == StopStream)
            {
                throw new InvalidOperationException("the stream would not stop");
            }
        };
        using var pump = new SamplePump(device, pourWhen: AfterPinWrites(device, 3));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.DriveAndCaptureAsync(
                Id, Pin, high: true, delayMs: 20, pulseWidthMs: 0, NeverMs, RowBudget, CancellationToken.None)
                .WaitAsync(Bound));

        Assert.Equal("the stream would not stop", ex.Message);
        Assert.Equal(new[] { Output, Low, High, Low }, device.Sent.Where(IsPinCommand));
    }

    // The row budget ran out during the baseline. The edge never went out, the pin never left its
    // baseline, and the result says both rather than reporting an edge that is not in the rows.
    [Fact]
    public async Task ACaptureThatEndsBeforeTheEdge_NeverDrivesIt()
    {
        var (agent, device) = await ReadyDeviceAsync();
        using var pump = new SamplePump(device);

        var result = await agent.DriveAndCaptureAsync(
            Id, Pin, high: true, delayMs: 30_000, pulseWidthMs: 100, NeverMs, maxRows: 5, CancellationToken.None)
            .WaitAsync(Bound);

        Assert.Equal(new[] { Output, Low }, device.Sent.Where(IsPinCommand));
        Assert.Null(result.EdgeIssuedAtMs);
        Assert.Null(result.ReleaseIssuedAtMs);
        Assert.False(result.LastCommandedHigh);
        Assert.True(result.Capture.RowLimitReached);
        Assert.Equal(5, result.Capture.RowCount);
    }

    [Fact]
    public async Task ADeviceThatSendsNothing_GetsNoEdge()
    {
        var (agent, device) = await ReadyDeviceAsync();

        var result = await agent.DriveAndCaptureAsync(
            Id, Pin, high: true, delayMs: 0, pulseWidthMs: 0, durationMs: 250, maxRows: 500, CancellationToken.None);

        Assert.DoesNotContain(High, device.Sent);
        Assert.Null(result.EdgeIssuedAtMs);
        Assert.Equal(0, result.Capture.RowCount);
    }

    [Fact]
    public async Task PwmRunningOnTheChannel_IsRefused_BeforeAnythingIsSent()
    {
        var (agent, device) = await ReadyDeviceAsync();
        await agent.SetPwmOutputAsync(Id, 4, dutyCyclePercent: 50, frequencyHz: 1000);
        device.ClearSent();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.DriveAndCaptureAsync(Id, 4, high: true, 50, 0, 250, 500, CancellationToken.None));

        Assert.Contains("disable_pwm", ex.Message);
        Assert.DoesNotContain("SetPwmEnabled", ex.Message);
        Assert.Empty(device.Sent);
        Assert.False(device.IsStreaming);
    }

    // A Nyquist with only digital channels enabled often does not start streaming, and the edge
    // waits for a first sample — so this would otherwise be a call that moves the pin to its
    // baseline, waits out its window, and returns an empty capture.
    [Fact]
    public async Task WithNoAnalogChannelEnabled_IsRefused_BeforeThePinIsTouched()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice();
        await agent.ConfigureDigitalChannelsAsync(Id, new[] { Pin });
        device.ClearSent();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.DriveAndCaptureAsync(Id, Pin, high: true, 50, 0, 250, 500, CancellationToken.None));

        Assert.Contains("configure_analog_channels", ex.Message);
        Assert.Empty(device.Sent);
    }

    [Fact]
    public async Task AnUnknownDigitalChannel_IsRefused_NamingTheOnesThatExist()
    {
        var (agent, device) = await ReadyDeviceAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.DriveAndCaptureAsync(Id, 99, high: true, 50, 0, 250, 500, CancellationToken.None));

        Assert.Contains("Unknown digital channel 99", ex.Message);
        Assert.Empty(device.Sent);
    }

    [Fact]
    public async Task AnUnknownDevice_PointsAtConnectDevice()
    {
        var agent = new DaqifiAgent(new ServerOptions());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.DriveAndCaptureAsync("serial:NOPE", Pin, high: true, 50, 0, 250, 500, CancellationToken.None));

        Assert.Contains("connect_device", ex.Message);
    }

    // The prompt used to ask for a capture "across the transition if you can", which no tool could
    // do. It now names the one that can, and where in the result the edge is actually visible.
    [Fact]
    public void TheDriveAndMeasurePrompt_SendsTheAgentToThisTool()
    {
        var prompt = Prompts.DaqifiPrompts.DriveAndMeasure(digitalChannel: 5, analogChannel: 2, durationMs: 750);

        Assert.Contains("drive_and_capture", prompt);
        Assert.Contains("DIO5", prompt);
        Assert.Contains("analog channel 2", prompt);
        Assert.Contains("750 ms", prompt);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A connected double with AI0 enabled and nothing yet on the wire.</summary>
    private static async Task<(DaqifiAgent Agent, FakeStreamingDevice Device)> ReadyDeviceAsync()
    {
        var (agent, device) = AgentHarness.WithConnectedDevice();
        await agent.ConfigureAnalogChannelsAsync(Id, new[] { 0 });
        device.ClearSent();
        return (agent, device);
    }

    private static bool IsPinCommand(string command) => command == Output || command == High || command == Low;

    private static bool IsStreamStart(string command) =>
        command.StartsWith(ScpiMessageProducer.StartStreaming(1).Data[..^1], StringComparison.Ordinal);

    /// <summary>For <see cref="SamplePump"/>: true once the pin has been written this many times.</summary>
    private static Func<bool> AfterPinWrites(FakeStreamingDevice device, int count) =>
        () => device.Sent.Count(IsPinCommand) >= count;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Bound);
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    /// <summary>
    /// Stands in for the device's sample stream, raising samples on AI0, each on its own timestamp
    /// so every one is a new row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing is raised until the capture has sent its stream-start command, which it does only
    /// after subscribing. A sample raised before that reaches nobody, and the pump only has so
    /// many to give before it waits: on a runner slow to get from building the pump to starting
    /// the capture, it would spend them all on an empty room, and then the edge would wait for a
    /// first sample while the pump waited for the edge.
    /// </para>
    /// <para>
    /// It then trickles a few rows — enough for a first sample and some baseline, far short of
    /// <see cref="RowBudget"/> — and waits. Only once <c>pourWhen</c> says the test has seen what
    /// it is about does it pour, which fills the row budget and ends the capture. Left null it
    /// never pours: for the captures that end some other way.
    /// </para>
    /// </remarks>
    private sealed class SamplePump : IDisposable
    {
        private const int Trickle = 50;

        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        internal SamplePump(FakeStreamingDevice device, Func<bool>? pourWhen = null)
        {
            var channel = device.GetChannelsSnapshot().Single(c => c is { Type: ChannelType.Analog, ChannelNumber: 0 });
            _loop = Task.Run(async () =>
            {
                var timestamp = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Local);
                void Raise()
                {
                    channel.SetActiveSample(new DataSample(timestamp, 1.25));
                    timestamp = timestamp.AddMilliseconds(1);
                }

                var streaming = false;
                var trickled = 0;
                while (!_stop.IsCancellationRequested)
                {
                    streaming = streaming || device.Sent.Any(IsStreamStart);
                    if (streaming && pourWhen?.Invoke() == true)
                    {
                        // A batch at a time, so the capture's reader gets the core between them.
                        for (var i = 0; i < 100; i++)
                        {
                            Raise();
                        }
                    }
                    else if (streaming && trickled < Trickle)
                    {
                        Raise();
                        trickled++;
                    }

                    await Task.Delay(2);
                }
            });
        }

        public void Dispose()
        {
            _stop.Cancel();
            _loop.Wait(Bound);
            _stop.Dispose();
        }
    }
}
