using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Google.Protobuf;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Issue #379: after a mid-stream drop, a device with reconnect enabled has to rebuild the whole
/// session by itself — transport, initialization, channel configuration, and the stream — with no
/// consumer code involved. With reconnect left at its default it must do exactly what it has always
/// done: report <see cref="ConnectionStatus.Lost"/> and stop.
/// </summary>
/// <remarks>
/// Driven by a scripted transport that can be dropped on command and told to refuse the next N
/// reconnects, so the loop's success, retry, give-up and cancellation paths are all reachable
/// without hardware. The one test that goes the long way round — real read failures escalated by
/// the production <see cref="TransportConnectionWatchdog"/> — is
/// <see cref="AMidStreamReadFailure_DrivesTheWholeLoopEndToEnd"/>.
/// </remarks>
public class DeviceReconnectTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Safety valve on the gates that park a scripted connect or initialization mid-flight. A test
    /// that is going to pass releases its gate within milliseconds, so this only ever fires on a
    /// test that has already failed — which is exactly why it has to stay well under
    /// <see cref="EventTimeout"/>. A gate outliving the assertion that gave up on it leaves a
    /// background thread parked inside the transport long after the test finished, where it can
    /// interleave with whatever runs next.
    /// </summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A policy that reconnects promptly, so tests do not spend their time waiting.</summary>
    private static ReconnectOptions FastPolicy(int maxAttempts = 4) => new()
    {
        Enabled = true,
        MaxAttempts = maxAttempts,
        InitialDelay = TimeSpan.FromMilliseconds(10),
        MaxDelay = TimeSpan.FromMilliseconds(60),
        BackoffMultiplier = 2.0
    };

    #region Default behaviour is unchanged

    [Fact]
    public void WithReconnectAtItsDefault_ADropStopsAtLost()
    {
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Default Device", transport);

        ConnectAndInitialize(device);

        // Subscribed after the connect, so the recorded transitions are only what the drop caused.
        var statuses = new List<ConnectionStatus>();
        device.StatusChanged += (_, e) =>
        {
            lock (statuses)
            {
                statuses.Add(e.Status);
            }
        };

        var reconnectEvents = 0;
        device.ReconnectAttempt += (_, _) => Interlocked.Increment(ref reconnectEvents);
        device.Reconnected += (_, _) => Interlocked.Increment(ref reconnectEvents);
        device.ReconnectFailed += (_, _) => Interlocked.Increment(ref reconnectEvents);

        var connectsBeforeDrop = transport.ConnectCount;

        transport.SimulateDrop();

        // Long enough that any reconnect worth having would have started by now.
        Thread.Sleep(500);

        Assert.Equal(ConnectionStatus.Lost, device.Status);
        Assert.False(device.IsReconnecting);
        Assert.Equal(0, Volatile.Read(ref reconnectEvents));
        Assert.Equal(connectsBeforeDrop, transport.ConnectCount);
        Assert.Equal(1, device.InitializeCount);

        lock (statuses)
        {
            // Exactly one transition, to Lost. No Retrying, no Failed, nothing else.
            Assert.Equal(new[] { ConnectionStatus.Lost }, statuses);
        }
    }

    [Fact]
    public void ReconnectIsOffOnAFreshDevice()
    {
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Fresh Device", transport);

        Assert.False(device.ReconnectOptions.Enabled);
        Assert.False(device.IsReconnecting);
    }

    #endregion

    #region The session comes back

    [Fact]
    public async Task AfterADrop_TheSessionIsRebuiltWithNoConsumerInvolvement()
    {
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Resuming Device", transport);
        device.ReconnectOptions = FastPolicy();

        ConnectAndInitialize(device);

        var analog1 = device.Channels.Single(c => c.Type == ChannelType.Analog && c.ChannelNumber == 1);
        var analog3 = device.Channels.Single(c => c.Type == ChannelType.Analog && c.ChannelNumber == 3);
        var digital0 = device.Channels.Single(c => c.Type == ChannelType.Digital && c.ChannelNumber == 0);
        device.EnableChannels(new[] { analog1, analog3, digital0 });

        device.StreamingFrequency = 250;
        device.StartStreaming();

        var reconnected = WaitFor<ReconnectedEventArgs>(h => device.Reconnected += h);
        device.ClearSentCommands();

        transport.SimulateDrop();

        var result = await reconnected;

        // The device is connected, re-initialized and streaming again — nobody was asked to help.
        Assert.Equal(ConnectionStatus.Connected, device.Status);
        Assert.True(device.IsConnected);
        Assert.Equal(2, device.InitializeCount);
        Assert.True(device.IsStreaming);
        Assert.True(result.StreamingResumed);
        Assert.Equal(1, result.AttemptNumber);
        Assert.True(result.Outage > TimeSpan.Zero);
        Assert.False(device.IsReconnecting);

        // The channel configuration was replayed onto the reconnected device. It has to have been
        // sent, not merely remembered: the scripted device reports every analog channel disabled
        // when it comes back, exactly as a rebooted one does.
        var sent = device.SentCommands;
        var expectedMask = (1u << 1) | (1u << 3);
        Assert.Contains($"ENAble:VOLTage:DC {expectedMask}", sent);
        Assert.Contains("DIO:PORt:ENAble 1", sent);

        // ...and the stream restarted at the frequency it was running at before the drop.
        Assert.Contains("SYSTem:StartStreamData 250", sent);

        Assert.True(device.Channels.Single(c => c.Type == ChannelType.Analog && c.ChannelNumber == 1).IsEnabled);
        Assert.True(device.Channels.Single(c => c.Type == ChannelType.Analog && c.ChannelNumber == 3).IsEnabled);
        Assert.False(device.Channels.Single(c => c.Type == ChannelType.Analog && c.ChannelNumber == 0).IsEnabled);
        Assert.True(device.Channels.Single(c => c.Type == ChannelType.Digital && c.ChannelNumber == 0).IsEnabled);
    }

    [Fact]
    public async Task ADeviceThatWasNotStreaming_ComesBackIdle()
    {
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Idle Device", transport);
        device.ReconnectOptions = FastPolicy();

        ConnectAndInitialize(device);
        device.EnableChannel(device.Channels.First(c => c.Type == ChannelType.Analog));

        var reconnected = WaitFor<ReconnectedEventArgs>(h => device.Reconnected += h);
        device.ClearSentCommands();

        transport.SimulateDrop();
        var result = await reconnected;

        Assert.False(result.StreamingResumed);
        Assert.False(device.IsStreaming);
        Assert.DoesNotContain(device.SentCommands, c => c.StartsWith("SYSTem:StartStreamData", StringComparison.Ordinal));

        // The channel configuration is restored regardless of whether a stream was running.
        Assert.Contains("ENAble:VOLTage:DC 1", device.SentCommands);
    }

    [Fact]
    public async Task WithResumeStreamingOff_TheChannelsComeBackButTheStreamDoesNot()
    {
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Manual Resume Device", transport);
        var policy = FastPolicy();
        policy.ResumeStreaming = false;
        device.ReconnectOptions = policy;

        ConnectAndInitialize(device);
        device.EnableChannel(device.Channels.First(c => c.Type == ChannelType.Analog));
        device.StartStreaming();

        var reconnected = WaitFor<ReconnectedEventArgs>(h => device.Reconnected += h);
        device.ClearSentCommands();

        transport.SimulateDrop();
        var result = await reconnected;

        Assert.False(result.StreamingResumed);
        Assert.Contains("ENAble:VOLTage:DC 1", device.SentCommands);
        Assert.DoesNotContain(device.SentCommands, c => c.StartsWith("SYSTem:StartStreamData", StringComparison.Ordinal));

        // The device is genuinely idle, and says so. Reporting a stale IsStreaming here would both
        // lie and make the caller's own StartStreaming() a silent no-op.
        Assert.False(device.IsStreaming);
        device.StartStreaming();
        Assert.True(device.IsStreaming);
        Assert.Contains(device.SentCommands, c => c.StartsWith("SYSTem:StartStreamData", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeviceThatRefusesTheFirstAttempts_IsRetriedUntilItAnswers()
    {
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Slow-To-Return Device", transport);
        device.ReconnectOptions = FastPolicy(maxAttempts: 5);

        ConnectAndInitialize(device);
        device.EnableChannel(device.Channels.First(c => c.Type == ChannelType.Analog));

        var attempts = new List<ReconnectAttemptEventArgs>();
        device.ReconnectAttempt += (_, e) =>
        {
            lock (attempts)
            {
                attempts.Add(e);
            }
        };

        var reconnected = WaitFor<ReconnectedEventArgs>(h => device.Reconnected += h);

        // The device is still coming back up: the next two connects are refused.
        transport.FailNextConnects(2);
        transport.SimulateDrop();

        var result = await reconnected;

        Assert.Equal(3, result.AttemptNumber);

        lock (attempts)
        {
            Assert.Equal(3, attempts.Count);
            Assert.Equal(new[] { 1, 2, 3 }, attempts.Select(a => a.AttemptNumber));
            Assert.All(attempts, a => Assert.Equal(5, a.MaxAttempts));

            // Backing off, not hammering: each wait is longer than the last.
            Assert.True(attempts[1].Delay > attempts[0].Delay);
            Assert.True(attempts[2].Delay > attempts[1].Delay);

            // The first attempt has nothing to report; the later ones say why the previous failed.
            Assert.Null(attempts[0].PreviousError);
            Assert.NotNull(attempts[1].PreviousError);
            Assert.NotNull(attempts[2].PreviousError);
        }
    }

    [Fact]
    public async Task AMidStreamReadFailure_DrivesTheWholeLoopEndToEnd()
    {
        // The long way round: no simulated drop, just reads that start failing. The production
        // watchdog escalates them to Lost, which is what the reconnect loop hangs off.
        using var transport = new ScriptedReconnectTransport(useWatchdog: true);
        using var device = new ScriptedStreamingDevice("Unplugged Device", transport);
        device.ReconnectOptions = FastPolicy();

        ConnectAndInitialize(device);
        device.EnableChannel(device.Channels.First(c => c.Type == ChannelType.Analog));
        device.StartStreaming();

        var reconnected = WaitFor<ReconnectedEventArgs>(h => device.Reconnected += h);
        device.ClearSentCommands();

        transport.FailReadsUntilNextConnect();

        var result = await reconnected;

        Assert.True(result.StreamingResumed);
        Assert.Equal(ConnectionStatus.Connected, device.Status);
        Assert.Contains("ENAble:VOLTage:DC 1", device.SentCommands);
        Assert.Contains(device.SentCommands, c => c.StartsWith("SYSTem:StartStreamData", StringComparison.Ordinal));
    }

    #endregion

    #region Giving up

    [Fact]
    public async Task WhenEveryAttemptFails_ItGivesUpLoudly()
    {
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Gone Device", transport);
        device.ReconnectOptions = FastPolicy(maxAttempts: 3);

        ConnectAndInitialize(device);

        DeviceErrorEventArgs? deviceError = null;
        device.ErrorOccurred += (_, e) => deviceError = e;

        var failed = WaitFor<ReconnectFailedEventArgs>(h => device.ReconnectFailed += h);

        transport.FailNextConnects(int.MaxValue);
        transport.SimulateDrop();

        var result = await failed;

        Assert.Equal(3, result.AttemptsMade);
        Assert.False(result.WasCanceled);
        Assert.NotNull(result.LastError);

        // Terminal, and impossible to miss: a distinct status plus the device error surface.
        Assert.Equal(ConnectionStatus.Failed, device.Status);
        Assert.False(device.IsConnected);

        Assert.NotNull(deviceError);
        Assert.Equal(DeviceErrorSource.Reconnect, deviceError!.Source);
        var reconnectFailure = Assert.IsType<DeviceReconnectFailedException>(deviceError.Error);
        Assert.Equal(3, reconnectFailure.AttemptsMade);
        Assert.Equal("Gone Device", reconnectFailure.DeviceName);
        Assert.NotNull(reconnectFailure.InnerException);

        // It really did stop: no further attempts after the report.
        var connectsAtGiveUp = transport.ConnectCount;
        Thread.Sleep(300);
        Assert.Equal(connectsAtGiveUp, transport.ConnectCount);
        Assert.False(device.IsReconnecting);
    }

    [Fact]
    public async Task AnInitializationThatKeepsFailing_AlsoExhaustsTheAttempts()
    {
        // The transport comes back every time; it is the device that will not initialize. The loop
        // must not treat a reachable-but-unusable device as a success.
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Unresponsive Device", transport);
        device.ReconnectOptions = FastPolicy(maxAttempts: 2);

        ConnectAndInitialize(device);

        // Set after the first (successful) initialization, so only the reconnect's attempts fail.
        device.InitializeFailure = _ =>
            new TimeoutException("the device never reported its channel configuration");

        var failed = WaitFor<ReconnectFailedEventArgs>(h => device.ReconnectFailed += h);
        transport.SimulateDrop();

        var result = await failed;

        Assert.Equal(2, result.AttemptsMade);
        Assert.IsType<TimeoutException>(result.LastError);
        Assert.Equal(ConnectionStatus.Failed, device.Status);

        // Nothing half-open is left behind: the transport it managed to open was closed again.
        Assert.False(transport.IsConnected);
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task CancelReconnect_StopsTheLoopAndLeavesTheDeviceLost()
    {
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Abandoned Device", transport);
        var policy = FastPolicy(maxAttempts: 20);
        policy.InitialDelay = TimeSpan.FromSeconds(30);
        policy.MaxDelay = TimeSpan.FromSeconds(30);
        device.ReconnectOptions = policy;

        ConnectAndInitialize(device);

        var failed = WaitFor<ReconnectFailedEventArgs>(h => device.ReconnectFailed += h);

        transport.FailNextConnects(int.MaxValue);
        transport.SimulateDrop();

        // Wait until the loop is parked in its 30 s backoff, so cancellation lands at a known point
        // rather than racing the loop's own teardown.
        WaitUntilRetrying(device);
        Assert.True(device.IsReconnecting);

        device.CancelReconnect();

        var result = await failed;

        // The 30 s backoff means cancellation, not exhaustion, is what ended this.
        Assert.True(result.WasCanceled);
        Assert.Equal(1, result.AttemptsMade);
        Assert.Equal(ConnectionStatus.Lost, device.Status);
        Assert.False(device.IsReconnecting);
        Assert.False(device.IsConnected);
    }

    [Fact]
    public async Task Disconnect_DuringAReconnect_WinsAndTheLoopStopsQuietly()
    {
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Departing Device", transport);
        var policy = FastPolicy(maxAttempts: 20);
        policy.InitialDelay = TimeSpan.FromSeconds(30);
        policy.MaxDelay = TimeSpan.FromSeconds(30);
        device.ReconnectOptions = policy;

        ConnectAndInitialize(device);

        var failed = WaitFor<ReconnectFailedEventArgs>(h => device.ReconnectFailed += h);

        transport.FailNextConnects(int.MaxValue);
        transport.SimulateDrop();
        WaitUntilRetrying(device);

        device.Disconnect();

        var result = await failed;
        Assert.True(result.WasCanceled);

        // The caller's teardown owns the outcome: the loop must not have overwritten it, nor
        // re-opened the transport behind it.
        Thread.Sleep(300);
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
        Assert.False(transport.IsConnected);
        Assert.False(device.IsReconnecting);
    }

    [Fact]
    public void Disconnect_WhileAConnectAttemptIsInFlight_DoesNotLeaveTheDeviceQuietlyAlive()
    {
        // Opening a port blocks for as long as it takes, so a caller pulling the plug on the device
        // lands *inside* an attempt rather than tidily between two. The attempt still finishes and
        // brings the transport back up; if the loop merely bails out at that point, the caller is
        // left holding a device they closed that is silently open again, reporting Connected, with
        // a reader thread running on it.
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Resurrected Device", transport);
        device.ReconnectOptions = FastPolicy();

        ConnectAndInitialize(device);

        var connectGate = transport.BlockConnects();
        transport.ConnectEntered.Reset();

        transport.SimulateDrop();

        Assert.True(
            transport.ConnectEntered.Wait(EventTimeout),
            "the reconnect never reached the transport's connect");

        // Release the parked attempt from another thread. The caller's Disconnect now waits for an
        // in-flight connect rather than running alongside it, so releasing after it returns would
        // wait on a call that is itself waiting on the gate.
        ReleaseAfter(connectGate, TimeSpan.FromMilliseconds(100));

        // The caller shuts the device down while that connect is in flight. The attempt still
        // finishes and re-opens the transport.
        device.Disconnect();
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);

        WaitUntil(() => !device.IsReconnecting, "the reconnect loop never finished");

        // The caller's decision has to survive it.
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
        Assert.False(device.IsConnected);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public void ACallerDisconnect_NeverDrivesTheTransportAlongsideAnInFlightReconnect()
    {
        // Cancelling is not synchronizing. SupersedeReconnect asks the loop to stop and returns
        // immediately, but a loop already inside a blocking transport connect cannot be
        // interrupted — so without a lifecycle lock the caller's Disconnect closes the port while
        // the reconnect is still opening it, and both threads race to build a message consumer.
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Contended Device", transport);
        device.ReconnectOptions = FastPolicy();

        ConnectAndInitialize(device);

        var connectGate = transport.BlockConnects();
        transport.ConnectEntered.Reset();

        transport.SimulateDrop();

        Assert.True(
            transport.ConnectEntered.Wait(EventTimeout),
            "the reconnect never reached the transport's connect");

        ReleaseAfter(connectGate, TimeSpan.FromMilliseconds(100));

        device.Disconnect();

        WaitUntil(() => !device.IsReconnecting, "the reconnect loop never finished");

        Assert.False(
            transport.SawConcurrentLifecycleCalls,
            "a caller's Disconnect was inside the transport at the same time as the reconnect's connect");

        // The round-one guarantee still holds on top of the new one.
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
        Assert.False(device.IsConnected);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public void AConnectThatCannotTakeTheLifecycleLock_FailsRatherThanRunningAlongside()
    {
        // The escape hatch: an earlier revision logged a warning on timeout and ran the operation
        // anyway, which is precisely the double-open it was added to prevent — two threads both
        // finding no message consumer, both starting one, two readers on one stream. Contention
        // now has to fail, not proceed.
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Contended Connect Device", transport)
        {
            LifecycleLockTimeoutOverride = TimeSpan.FromMilliseconds(200)
        };
        device.ReconnectOptions = FastPolicy();

        ConnectAndInitialize(device);

        var connectGate = transport.BlockConnects();
        transport.ConnectEntered.Reset();

        transport.SimulateDrop();
        Assert.True(
            transport.ConnectEntered.Wait(EventTimeout),
            "the reconnect never reached the transport's connect");

        // The reconnect is parked inside the transport holding the lifecycle lock, so a caller
        // connect cannot have it. It must give up rather than open a second connection alongside.
        var thrown = Assert.Throws<TimeoutException>(() => device.Connect());
        Assert.Contains("Nothing was opened", thrown.Message, StringComparison.OrdinalIgnoreCase);

        Assert.False(
            transport.SawConcurrentLifecycleCalls,
            "a caller's Connect ran inside the transport alongside the reconnect's connect");

        connectGate.Set();
        WaitUntil(() => !device.IsReconnecting, "the reconnect loop never finished");
    }

    [Fact]
    public void ADisconnectThatCannotTakeTheLifecycleLock_WaitsRatherThanFailing()
    {
        // The other half of the policy. Teardown is the resource-release path and Dispose depends
        // on it, so contention must not turn it into an exception — it waits instead, and the
        // short connect-side timeout must not leak into it.
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Patient Teardown Device", transport)
        {
            LifecycleLockTimeoutOverride = TimeSpan.FromMilliseconds(200)
        };
        device.ReconnectOptions = FastPolicy();

        ConnectAndInitialize(device);

        var connectGate = transport.BlockConnects();
        transport.ConnectEntered.Reset();

        transport.SimulateDrop();
        Assert.True(
            transport.ConnectEntered.Wait(EventTimeout),
            "the reconnect never reached the transport's connect");

        // Held well past the connect-side timeout, so a shared budget would have expired.
        ReleaseAfter(connectGate, TimeSpan.FromMilliseconds(600));

        device.Disconnect();

        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
        Assert.False(transport.SawConcurrentLifecycleCalls);

        WaitUntil(() => !device.IsReconnecting, "the reconnect loop never finished");
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public void ANormalConnectDisconnectCycle_ReportsNoLifecycleContention()
    {
        // The uncontended path is unchanged by the lock: nothing waits, nothing overlaps.
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Ordinary Device", transport);

        ConnectAndInitialize(device);
        device.Disconnect();

        Assert.False(transport.SawConcurrentLifecycleCalls);
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
    }

    [Fact]
    public async Task Disconnect_WhileInitializationIsInFlight_IsNotReportedAsASuccessfulReconnect()
    {
        // The same race one step later: the transport came back and initialization was most of the
        // way through when the caller disconnected. Announcing a recovered session at that point
        // would hand them a device they had just closed.
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedBaseDevice("Late Abandoned Device", transport);
        device.ReconnectOptions = FastPolicy();

        device.Connect();
        await device.InitializeAsync();

        var initGate = new ManualResetEventSlim(false);
        device.InitializeEntered.Reset();
        device.InitializeGate = initGate;

        var reconnectedCount = 0;
        device.Reconnected += (_, _) => Interlocked.Increment(ref reconnectedCount);

        transport.SimulateDrop();

        Assert.True(
            device.InitializeEntered.Wait(EventTimeout),
            "the reconnect never reached initialization");

        device.Disconnect();
        initGate.Set();

        WaitUntil(() => !device.IsReconnecting, "the reconnect loop never finished");

        Assert.Equal(0, Volatile.Read(ref reconnectedCount));
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
        Assert.False(device.IsConnected);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public void DisposingDuringAReconnect_DoesNotThrow()
    {
        var transport = new ScriptedReconnectTransport();
        var device = new ScriptedStreamingDevice("Disposed Device", transport);
        device.ReconnectOptions = FastPolicy(maxAttempts: 20);

        ConnectAndInitialize(device);

        transport.FailNextConnects(int.MaxValue);
        transport.SimulateDrop();
        WaitUntilRetrying(device);

        device.Dispose();
        transport.Dispose();

        // Whatever the loop was in the middle of, it unwinds without surfacing anything.
        Thread.Sleep(400);
    }

    #endregion

    #region Robustness

    [Fact]
    public async Task AThrowingReconnectSubscriber_DoesNotStopTheLoop()
    {
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Badly Observed Device", transport);
        device.ReconnectOptions = FastPolicy();

        ConnectAndInitialize(device);
        device.ReconnectAttempt += (_, _) => throw new InvalidOperationException("a badly behaved subscriber");

        var reconnected = WaitFor<ReconnectedEventArgs>(h => device.Reconnected += h);
        transport.SimulateDrop();

        var result = await reconnected;
        Assert.Equal(1, result.AttemptNumber);
        Assert.Equal(ConnectionStatus.Connected, device.Status);
    }

    [Fact]
    public async Task ASecondDropAfterARecovery_StartsAFreshLoop()
    {
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Flaky Device", transport);
        device.ReconnectOptions = FastPolicy();

        ConnectAndInitialize(device);
        device.EnableChannel(device.Channels.First(c => c.Type == ChannelType.Analog));
        device.StartStreaming();

        var first = WaitFor<ReconnectedEventArgs>(h => device.Reconnected += h);
        transport.SimulateDrop();
        await first;

        // Reconnected is raised from inside the loop; let it finish unwinding so the second drop is
        // unambiguously a fresh one rather than one folded into the loop still in flight.
        WaitUntil(() => !device.IsReconnecting, "the first reconnect loop never finished");

        var second = WaitFor<ReconnectedEventArgs>(h => device.Reconnected += h);
        device.ClearSentCommands();
        transport.SimulateDrop();
        var result = await second;

        Assert.True(result.StreamingResumed);
        Assert.Equal(3, device.InitializeCount);
        Assert.Contains("ENAble:VOLTage:DC 1", device.SentCommands);
    }

    [Fact]
    public void AHandlerThatDisconnectsOnLost_IsNotOverruledByAReconnect()
    {
        // The teardown-on-Lost pattern the docs show for devices without reconnect. If a consumer
        // still does it, their decision has to stand — a reconnect starting behind it would reopen
        // a device they just closed.
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Torn Down Device", transport);
        device.ReconnectOptions = FastPolicy();

        ConnectAndInitialize(device);

        device.StatusChanged += (_, e) =>
        {
            if (e.Status == ConnectionStatus.Lost)
            {
                device.Disconnect();
            }
        };

        var reconnectEvents = 0;
        device.ReconnectAttempt += (_, _) => Interlocked.Increment(ref reconnectEvents);

        var connectsBeforeDrop = transport.ConnectCount;
        transport.SimulateDrop();

        Thread.Sleep(500);

        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
        Assert.Equal(0, Volatile.Read(ref reconnectEvents));
        Assert.Equal(connectsBeforeDrop, transport.ConnectCount);
        Assert.False(transport.IsConnected);
        Assert.False(device.IsReconnecting);
    }

    [Fact]
    public void SettingReconnectOptionsToNull_IsRejected()
    {
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Picky Device", transport);

        Assert.Throws<ArgumentNullException>(() => device.ReconnectOptions = null!);
    }

    [Fact]
    public void CancelReconnect_WithNothingRunning_IsHarmless()
    {
        using var transport = new ScriptedReconnectTransport();
        using var device = new ScriptedStreamingDevice("Quiet Device", transport);

        device.CancelReconnect();
        device.CancelReconnect();

        Assert.False(device.IsReconnecting);
    }

    #endregion

    #region Helpers

    private static void ConnectAndInitialize(ScriptedStreamingDevice device)
    {
        device.Connect();
        device.InitializeAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Blocks until the reconnect loop has torn the dead session down and is waiting out its
    /// backoff, which is the only point at which a test can cancel without racing that teardown.
    /// </summary>
    private static void WaitUntilRetrying(DaqifiStreamingDevice device) =>
        WaitUntil(
            () => device.Status == ConnectionStatus.Retrying,
            "the reconnect loop never reached its backoff wait");

    /// <summary>
    /// Releases a gate from another thread after a short delay, for tests where the call that would
    /// otherwise release it is itself waiting on that gate.
    /// </summary>
    private static void ReleaseAfter(ManualResetEventSlim gate, TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay).ConfigureAwait(false);
            gate.Set();
        });
    }

    private static void WaitUntil(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + EventTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(5);
        }

        Assert.True(condition(), because);
    }

    /// <summary>
    /// Subscribes to an event and returns a task that completes with its first payload, so a test
    /// can arm the wait before provoking the drop that satisfies it.
    /// </summary>
    private static Task<TArgs> WaitFor<TArgs>(Action<EventHandler<TArgs>> subscribe)
        where TArgs : EventArgs
    {
        var tcs = new TaskCompletionSource<TArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscribe((_, e) => tcs.TrySetResult(e));

        return WaitWithTimeoutAsync(tcs.Task);
    }

    private static async Task<TArgs> WaitWithTimeoutAsync<TArgs>(Task<TArgs> task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(EventTimeout)).ConfigureAwait(false);
        Assert.True(completed == task, $"timed out waiting for {typeof(TArgs).Name}");
        return await task.ConfigureAwait(false);
    }

    /// <summary>
    /// A streaming device with no real SCPI: commands are captured rather than written, and
    /// initialization synthesizes the status message a device would have sent.
    /// </summary>
    private sealed class ScriptedStreamingDevice : DaqifiStreamingDevice
    {
        private readonly List<string> _sent = new();
        private int _initializeCount;

        public ScriptedStreamingDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        /// <summary>
        /// Shortens the connect-side lifecycle wait so the contention path is reachable in a test
        /// without a ten-second pause. Mirrors how <c>SdCardDownloadTimeout</c> is overridden.
        /// </summary>
        public TimeSpan? LifecycleLockTimeoutOverride { get; init; }

        internal override TimeSpan LifecycleLockTimeout =>
            LifecycleLockTimeoutOverride ?? base.LifecycleLockTimeout;

        /// <summary>Number of analog channels the scripted device reports.</summary>
        public int AnalogChannelCount { get; init; } = 4;

        /// <summary>Number of digital channels the scripted device reports.</summary>
        public int DigitalChannelCount { get; init; } = 2;

        /// <summary>
        /// Optional failure injector, keyed by 1-based initialization count, so a test can let the
        /// first (pre-drop) initialization succeed and fail every later one.
        /// </summary>
        public Func<int, Exception?>? InitializeFailure { get; set; }

        public int InitializeCount => Volatile.Read(ref _initializeCount);

        public IReadOnlyList<string> SentCommands
        {
            get
            {
                lock (_sent)
                {
                    return _sent.ToArray();
                }
            }
        }

        public void ClearSentCommands()
        {
            lock (_sent)
            {
                _sent.Clear();
            }
        }

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> stringMessage)
            {
                lock (_sent)
                {
                    _sent.Add(stringMessage.Data);
                }
            }
        }

        public override Task InitializeAsync(
            TimeSpan? channelPopulationTimeout = null,
            CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _initializeCount);

            cancellationToken.ThrowIfCancellationRequested();

            if (!IsConnected)
            {
                return Task.FromException(new DeviceNotConnectedException());
            }

            var failure = InitializeFailure?.Invoke(attempt);
            if (failure != null)
            {
                return Task.FromException(failure);
            }

            PopulateChannelsFromStatus(BuildStatusMessage());
            return Task.CompletedTask;
        }

        /// <summary>
        /// The status a device sends after a fresh boot: channels present, and — crucially —
        /// <c>analog_in_port_enabled</c> reported as all-zero. A restored enable set therefore
        /// cannot be an in-memory leftover; it has to have been re-applied.
        /// </summary>
        private DaqifiOutMessage BuildStatusMessage()
        {
            var status = new DaqifiOutMessage
            {
                AnalogInPortNum = (uint)AnalogChannelCount,
                AnalogInRes = 65535,
                DigitalPortNum = (uint)DigitalChannelCount,
                AnalogInPortEnabled = ByteString.CopyFrom(new byte[] { 0x00, 0x00 })
            };

            for (var i = 0; i < AnalogChannelCount; i++)
            {
                status.AnalogInPortRange.Add(1.0f);
                status.AnalogInCalM.Add(1.0f);
                status.AnalogInCalB.Add(0.0f);
                status.AnalogInIntScaleM.Add(1.0f);
            }

            return status;
        }
    }

    /// <summary>
    /// A plain (non-streaming) device on a scripted transport, whose initialization can be parked
    /// mid-flight. Deriving from <see cref="DaqifiDevice"/> rather than the streaming subclass is
    /// the point: its session restore is the base no-op, so nothing in it re-checks the connection
    /// on the loop's behalf, and the loop's own final guard is what has to catch a caller who
    /// disconnected while initialization was running.
    /// </summary>
    private sealed class ScriptedBaseDevice : DaqifiDevice
    {
        private int _initializeCount;

        public ScriptedBaseDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        /// <summary>Signalled once initialization is past its entry checks.</summary>
        public ManualResetEventSlim InitializeEntered { get; } = new(false);

        /// <summary>Parks initialization after its entry checks until set.</summary>
        public ManualResetEventSlim? InitializeGate { get; set; }

        public int InitializeCount => Volatile.Read(ref _initializeCount);

        public override void Send<T>(IOutboundMessage<T> message)
        {
        }

        public override Task InitializeAsync(
            TimeSpan? channelPopulationTimeout = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _initializeCount);
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsConnected)
            {
                return Task.FromException(new DeviceNotConnectedException());
            }

            // Entry checks are done; everything past here is the seconds of SCPI round-trips a real
            // initialization spends, which is the window a caller's Disconnect lands in.
            InitializeEntered.Set();
            InitializeGate?.Wait(GateTimeout);

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A transport that can be dropped on command and told to refuse the next N reconnects, so the
    /// whole reconnect loop is reachable without hardware. Each connect hands out a <em>fresh</em>
    /// stream, matching the real serial transport, whose <c>BaseStream</c> is a new instance after a
    /// reopen.
    /// </summary>
    private sealed class ScriptedReconnectTransport : IStreamTransport, ITransportHealthSink
    {
        private readonly object _gate = new();
        private readonly TransportConnectionWatchdog? _watchdog;
        private IdleStream _stream = new();
        private bool _isConnected;
        private bool _disposed;
        private int _connectFailuresRemaining;
        private int _connectCount;

        public ScriptedReconnectTransport(bool useWatchdog = false)
        {
            if (useWatchdog)
            {
                _watchdog = new TransportConnectionWatchdog("Scripted transport", HandleConnectionLost);
            }
        }

        public int ConnectCount => Volatile.Read(ref _connectCount);

        public Stream Stream
        {
            get
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    return _stream;
                }
            }
        }

        public bool IsConnected
        {
            get
            {
                lock (_gate)
                {
                    return _isConnected && !_disposed;
                }
            }
        }

        public string ConnectionInfo => IsConnected ? "Scripted: Connected" : "Scripted: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        /// <summary>Refuses the next <paramref name="count"/> connect attempts.</summary>
        public void FailNextConnects(int count)
        {
            lock (_gate)
            {
                _connectFailuresRemaining = count;
            }
        }

        /// <summary>
        /// Makes reads start failing, as a pulled cable does, until the next successful connect
        /// hands out a fresh stream.
        /// </summary>
        public void FailReadsUntilNextConnect()
        {
            lock (_gate)
            {
                _stream.FailReads = true;
            }
        }

        /// <summary>Reports a drop the way a transport's own detection would.</summary>
        public void SimulateDrop()
        {
            lock (_gate)
            {
                _isConnected = false;
            }

            _watchdog?.Disarm();
            StatusChanged?.Invoke(
                this,
                new TransportStatusEventArgs(false, ConnectionInfo, new IOException("the device went away")));
        }

        /// <summary>
        /// Signalled as soon as a connect attempt begins, before it does any work.
        /// </summary>
        public ManualResetEventSlim ConnectEntered { get; } = new(false);

        private volatile ManualResetEventSlim? _connectGate;

        /// <summary>
        /// Parks every subsequent connect attempt until the returned gate is set, so a test can
        /// act — pull the device out from under the loop, say — while one is genuinely in flight.
        /// Real connects block for as long as opening a port takes; this makes that window
        /// controllable instead of a race to lose.
        /// </summary>
        public ManualResetEventSlim BlockConnects()
        {
            var gate = new ManualResetEventSlim(false);
            _connectGate = gate;
            return gate;
        }

        private int _lifecycleDepth;
        private volatile bool _sawConcurrentLifecycleCalls;

        /// <summary>
        /// True if a connect and a disconnect were ever inside this transport at the same time.
        /// A real transport is a serial port or a socket: opening one while closing it is
        /// undefined, so the device must never do it.
        /// </summary>
        public bool SawConcurrentLifecycleCalls => _sawConcurrentLifecycleCalls;

        private void EnterLifecycle()
        {
            if (Interlocked.Increment(ref _lifecycleDepth) > 1)
            {
                _sawConcurrentLifecycleCalls = true;
            }
        }

        private void ExitLifecycle() => Interlocked.Decrement(ref _lifecycleDepth);

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            Interlocked.Increment(ref _connectCount);
            EnterLifecycle();
            try
            {
                ConnectCore(retryOptions);
            }
            finally
            {
                ExitLifecycle();
            }

            return Task.CompletedTask;
        }

        private void ConnectCore(ConnectionRetryOptions? retryOptions)
        {
            // Outside the lock: a test holding the gate must still be able to inspect the
            // transport and drive the device while the connect is parked here.
            ConnectEntered.Set();
            _connectGate?.Wait(GateTimeout);

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_connectFailuresRemaining > 0)
                {
                    _connectFailuresRemaining--;
                    throw new IOException("the device is not back yet");
                }

                // A reopened transport is a new stream; binding a consumer to the old one is
                // exactly the bug the device's Disconnect/Connect cycle exists to avoid.
                _stream.Dispose();
                _stream = new IdleStream();
                _isConnected = true;
            }

            _watchdog?.Arm();
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
        }

        public Task DisconnectAsync()
        {
            EnterLifecycle();
            try
            {
                // Disarm first: closing the handle is what makes in-flight reads fail, and none of
                // that is a lost connection.
                _watchdog?.Disarm();

                lock (_gate)
                {
                    _isConnected = false;
                }

                StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
            }
            finally
            {
                ExitLifecycle();
            }

            return Task.CompletedTask;
        }

        public void Connect() => ConnectAsync().GetAwaiter().GetResult();

        public void Disconnect() => DisconnectAsync().GetAwaiter().GetResult();

        public void ReportIoFault(Exception error) => _watchdog?.RecordFault(error);

        public void ReportIoSuccess() => _watchdog?.RecordSuccess();

        private void HandleConnectionLost(Exception error)
        {
            lock (_gate)
            {
                _isConnected = false;
            }

            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo, error));
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _isConnected = false;
                _stream.Dispose();
            }

            _watchdog?.Dispose();
        }
    }

    /// <summary>
    /// A stream that idles quietly — the shape a connected device with nothing to say presents to
    /// the reader loop — until told to fail its reads.
    /// </summary>
    private sealed class IdleStream : Stream
    {
        public volatile bool FailReads;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (FailReads)
            {
                Thread.Sleep(5);
                throw new IOException("the device is gone");
            }

            // Not a socket: a zero-byte read means "nothing yet", never a fault.
            Thread.Sleep(20);
            return 0;
        }

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

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }

    #endregion
}
