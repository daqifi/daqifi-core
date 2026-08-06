using System.Diagnostics;
using System.Net;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

/// <summary>
/// The bounded chip-info probe that <see cref="LanChipInfoProviderExtensions.GetLanChipInfoWithRetryAsync"/>
/// exposes for consumers (issue #269). Core's own WiFi status check runs the same code, so the
/// end-to-end expectations live in <c>FirmwareUpdateServiceTests</c>; these pin the probe itself.
/// </summary>
public class LanChipInfoProviderExtensionsTests
{
    private static LanChipInfo SampleChipInfo => new()
    {
        ChipId = 1377184,
        FwVersion = "19.7.7",
        BuildDate = "Mar 30 2022"
    };

    private static LanChipInfoRetryOptions FastOptions(
        int maxAttempts = 3,
        bool kickLanApply = true) => new()
        {
            MaxAttempts = maxAttempts,
            RetryDelay = TimeSpan.FromMilliseconds(5),
            TotalTimeout = TimeSpan.FromSeconds(30),
            KickLanApplyOnNotInitialized = kickLanApply,
        };

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenFirstAttemptSucceeds_QueriesOnce()
    {
        var device = new ScriptedLanChipInfoDevice(SampleChipInfo);

        var result = await device.GetLanChipInfoWithRetryAsync(FastOptions());

        Assert.True(result.Succeeded);
        Assert.Equal("19.7.7", result.ChipInfo!.FwVersion);
        Assert.False(result.WasLanNotInitialized);
        Assert.Equal(1, device.GetLanChipInfoCallCount);
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenTransientFailuresPrecedeSuccess_RetriesUntilItReads()
    {
        // #144: right after a PIC32 update the application is up while WiFi is still starting, so
        // the first queries fail. Taking the first failure as the answer is what sends a caller
        // into a needless multi-minute reflash of already-current firmware.
        var device = new ScriptedLanChipInfoDevice(
            new InvalidOperationException("transient"),
            new InvalidOperationException("transient"),
            SampleChipInfo);

        var result = await device.GetLanChipInfoWithRetryAsync(FastOptions());

        Assert.True(result.Succeeded);
        Assert.Equal(3, device.GetLanChipInfoCallCount);
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenEveryAttemptFails_ReturnsUnavailableWithoutThrowing()
    {
        var device = new ScriptedLanChipInfoDevice(new InvalidOperationException("still broken"));

        var result = await device.GetLanChipInfoWithRetryAsync(FastOptions());

        Assert.False(result.Succeeded);
        Assert.Null(result.ChipInfo);
        Assert.False(result.WasLanNotInitialized);
        Assert.Equal(3, device.GetLanChipInfoCallCount);
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenProviderReturnsNull_RetriesAndReportsUnavailable()
    {
        // An unrecognizable response is a null, not an exception — it must be retried too.
        var device = new ScriptedLanChipInfoDevice((object?)null);

        var result = await device.GetLanChipInfoWithRetryAsync(FastOptions());

        Assert.False(result.Succeeded);
        Assert.False(result.WasLanNotInitialized);
        Assert.Equal(3, device.GetLanChipInfoCallCount);
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenLanNotInitialized_SendsLanApplyExactlyOnceAndRecovers()
    {
        // #203: LAN:ENAbled=1 but the WINC state machine hasn't reached INITIALIZED, so the query
        // answers SCPI -200. One LAN:APPLY resolves it; kicking on every attempt would tear the
        // module down and re-init it repeatedly, risking an already-associated link.
        var device = new ScriptedLanChipInfoDevice(
            new LanNotInitializedException("**ERROR: -200, \"Execution error\""),
            new LanNotInitializedException("**ERROR: -200, \"Execution error\""),
            SampleChipInfo);

        var result = await device.GetLanChipInfoWithRetryAsync(FastOptions());

        Assert.True(result.Succeeded);
        Assert.Equal(3, device.GetLanChipInfoCallCount);
        Assert.Equal(1, device.LanApplySentCount);
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenLanNotInitializedExhaustsBudget_ReportsThatSpecifically()
    {
        var device = new ScriptedLanChipInfoDevice(
            new LanNotInitializedException("**ERROR: -200, \"Execution error\""));

        var result = await device.GetLanChipInfoWithRetryAsync(FastOptions());

        Assert.False(result.Succeeded);
        Assert.True(result.WasLanNotInitialized);
        Assert.Equal(1, device.LanApplySentCount);
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenALaterFailureIsNotLanNotInitialized_ReportsTheTerminalCondition()
    {
        // The flag describes how the probe actually ended, not whether -200 was ever seen. A stale
        // "not initialized" would send a caller off kicking APPLY at a module that failed for some
        // entirely different reason on the attempt that mattered.
        var device = new ScriptedLanChipInfoDevice(
            new LanNotInitializedException("**ERROR: -200, \"Execution error\""),
            new InvalidOperationException("transport went away"),
            new InvalidOperationException("transport went away"));

        var result = await device.GetLanChipInfoWithRetryAsync(FastOptions());

        Assert.False(result.Succeeded);
        Assert.False(result.WasLanNotInitialized);
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenKickDisabled_DoesNotSendLanApply()
    {
        var device = new ScriptedLanChipInfoDevice(
            new LanNotInitializedException("**ERROR: -200, \"Execution error\""));

        var result = await device.GetLanChipInfoWithRetryAsync(FastOptions(kickLanApply: false));

        Assert.True(result.WasLanNotInitialized);
        Assert.Equal(0, device.LanApplySentCount);
        Assert.DoesNotContain("SYSTem:COMMunicate:LAN:APPLY", device.SentCommands);
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenDeviceDisconnected_DoesNotSendLanApply()
    {
        // There is nothing to send through: the kick would throw or, worse, silently queue.
        var device = new ScriptedLanChipInfoDevice(
            new LanNotInitializedException("**ERROR: -200, \"Execution error\""))
        {
            IsConnected = false
        };

        var result = await device.GetLanChipInfoWithRetryAsync(FastOptions());

        Assert.True(result.WasLanNotInitialized);
        Assert.Equal(0, device.LanApplySentCount);
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenLanApplySendThrows_KeepsRetrying()
    {
        // Best effort: a failed kick must not abort the probe — the module may still settle on its
        // own within the remaining attempts.
        var device = new ScriptedLanChipInfoDevice(
            new LanNotInitializedException("**ERROR: -200, \"Execution error\""),
            SampleChipInfo)
        {
            ThrowOnSend = true
        };

        var result = await device.GetLanChipInfoWithRetryAsync(FastOptions());

        Assert.True(result.Succeeded);
        Assert.Equal(2, device.GetLanChipInfoCallCount);
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenProviderIsNotAStreamingDevice_StillProbesWithoutKicking()
    {
        // The kick needs a device to send through. A bare provider simply doesn't get one, and that
        // must not be an error.
        var provider = new ScriptedLanChipInfoProvider(
            new LanNotInitializedException("**ERROR: -200, \"Execution error\""),
            SampleChipInfo);

        var result = await provider.GetLanChipInfoWithRetryAsync(FastOptions());

        Assert.True(result.Succeeded);
        Assert.Equal(2, provider.GetLanChipInfoCallCount);
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenTotalTimeoutExpires_StopsEarlyWithoutThrowing()
    {
        // The wall-clock ceiling is the point: attempts × the device's own response timeout plus
        // the delays can far outrun what the caller meant to spend, and this runs under a lock.
        var device = new ScriptedLanChipInfoDevice(new InvalidOperationException("slow and broken"));
        var options = new LanChipInfoRetryOptions
        {
            MaxAttempts = 50,
            RetryDelay = TimeSpan.FromMilliseconds(20),
            TotalTimeout = TimeSpan.FromMilliseconds(120),
        };

        var stopwatch = Stopwatch.StartNew();
        var result = await device.GetLanChipInfoWithRetryAsync(options);
        stopwatch.Stop();

        Assert.False(result.Succeeded);
        Assert.True(
            device.GetLanChipInfoCallCount < 50,
            $"Expected the total-timeout budget to cut the probe short, but all 50 attempts ran ({device.GetLanChipInfoCallCount}).");
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenCallerCancels_Throws()
    {
        // The caller's own cancellation is the one thing that is not absorbed into a result.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var device = new ScriptedLanChipInfoDevice(SampleChipInfo);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.GetLanChipInfoWithRetryAsync(FastOptions(), cancellationToken: cts.Token));

        Assert.Equal(0, device.GetLanChipInfoCallCount);
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenCancelledRacingNotInitialized_DoesNotSendLanApply()
    {
        // A cancellation that lands between the -200 detection and the kick must not still leave a
        // state-changing command on the device.
        using var cts = new CancellationTokenSource();
        var device = new ScriptedLanChipInfoDevice(
            new LanNotInitializedException("**ERROR: -200, \"Execution error\""));
        device.OnGetLanChipInfo = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.GetLanChipInfoWithRetryAsync(FastOptions(), cancellationToken: cts.Token));

        Assert.Equal(0, device.LanApplySentCount);
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenMaxAttemptsBelowOne_StillMakesOneAttempt()
    {
        // The options object is a plain settable record a consumer can get wrong; a probe that
        // queries nothing at all and reports "unavailable" would be indistinguishable from a dead
        // module.
        var device = new ScriptedLanChipInfoDevice(SampleChipInfo);

        var result = await device.GetLanChipInfoWithRetryAsync(FastOptions(maxAttempts: 0));

        Assert.True(result.Succeeded);
        Assert.Equal(1, device.GetLanChipInfoCallCount);
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenSingleAttemptFails_DoesNotWaitTheRetryDelay()
    {
        // No delay after the final attempt — otherwise every exhausted probe pays for a retry it
        // never makes.
        var device = new ScriptedLanChipInfoDevice(new InvalidOperationException("broken"));
        var options = new LanChipInfoRetryOptions
        {
            MaxAttempts = 1,
            RetryDelay = TimeSpan.FromSeconds(10),
            TotalTimeout = TimeSpan.FromSeconds(30),
        };

        var stopwatch = Stopwatch.StartNew();
        var result = await device.GetLanChipInfoWithRetryAsync(options);
        stopwatch.Stop();

        Assert.False(result.Succeeded);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"A single-attempt probe waited {stopwatch.Elapsed} — the trailing retry delay was applied.");
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenTotalTimeoutIsNegative_MakesNoAttemptWithoutThrowing()
    {
        // A budget of "less than nothing" is the same as no budget, which the option documents as
        // "no attempt is made". Handing it straight to CancellationTokenSource would instead throw
        // ArgumentOutOfRangeException out of a probe whose whole contract is that failures come back
        // as an unavailable result.
        var device = new ScriptedLanChipInfoDevice(SampleChipInfo);
        var options = new LanChipInfoRetryOptions
        {
            MaxAttempts = 3,
            RetryDelay = TimeSpan.FromMilliseconds(5),
            TotalTimeout = TimeSpan.FromSeconds(-5),
        };

        var result = await device.GetLanChipInfoWithRetryAsync(options);

        Assert.False(result.Succeeded);
        Assert.False(result.WasLanNotInitialized);
        Assert.Equal(0, device.GetLanChipInfoCallCount);
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenTotalTimeoutIsInfinite_SpendsEveryAttempt()
    {
        // Timeout.InfiniteTimeSpan is negative, but it is how .NET spells "no ceiling" and must keep
        // meaning that — collapsing it into the no-budget case would silently turn an unbounded
        // probe into one that never queries at all.
        var device = new ScriptedLanChipInfoDevice(new InvalidOperationException("broken"));
        var options = new LanChipInfoRetryOptions
        {
            MaxAttempts = 3,
            RetryDelay = TimeSpan.FromMilliseconds(5),
            TotalTimeout = Timeout.InfiniteTimeSpan,
        };

        var result = await device.GetLanChipInfoWithRetryAsync(options);

        Assert.False(result.Succeeded);
        Assert.Equal(3, device.GetLanChipInfoCallCount);
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenRetryDelayIsNegative_KeepsRetryingWithoutThrowing()
    {
        // Same reasoning as the budget: a negative pause is a misconfiguration, not a reason to
        // throw ArgumentOutOfRangeException out of Task.Delay midway through the loop.
        var device = new ScriptedLanChipInfoDevice(
            new InvalidOperationException("transient"),
            new InvalidOperationException("transient"),
            SampleChipInfo);
        var options = new LanChipInfoRetryOptions
        {
            MaxAttempts = 3,
            RetryDelay = TimeSpan.FromSeconds(-1),
            TotalTimeout = TimeSpan.FromSeconds(30),
        };

        var result = await device.GetLanChipInfoWithRetryAsync(options);

        Assert.True(result.Succeeded);
        Assert.Equal(3, device.GetLanChipInfoCallCount);
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenRetryDelayIsInfinite_DoesNotHangBetweenAttempts()
    {
        // An infinite *pause* inside a bounded retry has no meaning, and with an equally unbounded
        // budget nothing would ever release it — the probe would hang on the caller's thread
        // forever. The guard token bounds a regression here to 10s instead of wedging the suite.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var device = new ScriptedLanChipInfoDevice(new InvalidOperationException("broken"));
        var options = new LanChipInfoRetryOptions
        {
            MaxAttempts = 2,
            RetryDelay = Timeout.InfiniteTimeSpan,
            TotalTimeout = Timeout.InfiniteTimeSpan,
        };

        var stopwatch = Stopwatch.StartNew();
        var result = await device.GetLanChipInfoWithRetryAsync(options, cancellationToken: guard.Token);
        stopwatch.Stop();

        Assert.False(result.Succeeded);
        Assert.Equal(2, device.GetLanChipInfoCallCount);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"An infinite retry delay stalled the probe for {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WithoutOptions_UsesCoreDefaults()
    {
        var device = new ScriptedLanChipInfoDevice(SampleChipInfo);

        var result = await device.GetLanChipInfoWithRetryAsync();

        Assert.True(result.Succeeded);

        var defaults = new LanChipInfoRetryOptions();
        Assert.Equal(3, defaults.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), defaults.RetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(8), defaults.TotalTimeout);
        Assert.True(defaults.KickLanApplyOnNotInitialized);
    }

    [Fact]
    public async Task GetLanChipInfoWithRetryAsync_WhenProviderIsNull_Throws()
    {
        ILanChipInfoProvider? provider = null;

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => provider!.GetLanChipInfoWithRetryAsync());
    }

    /// <summary>
    /// A provider whose successive answers are scripted: each outcome is a <see cref="LanChipInfo"/>
    /// to return, an <see cref="Exception"/> to fault with, or null for an unparseable response. The
    /// last outcome repeats, so "always fails" is a one-element script.
    /// </summary>
    private class ScriptedLanChipInfoProvider : ILanChipInfoProvider
    {
        private readonly IReadOnlyList<object?> _outcomes;
        private int _next;

        public ScriptedLanChipInfoProvider(params object?[] outcomes)
        {
            _outcomes = outcomes.Length > 0 ? outcomes : [null];
        }

        public int GetLanChipInfoCallCount { get; private set; }

        /// <summary>Runs as the query lands, for tests that need to race it.</summary>
        public Action? OnGetLanChipInfo { get; set; }

        public Task<LanChipInfo?> GetLanChipInfoAsync(CancellationToken cancellationToken = default)
        {
            GetLanChipInfoCallCount++;
            OnGetLanChipInfo?.Invoke();

            var outcome = _outcomes[Math.Min(_next, _outcomes.Count - 1)];
            _next++;

            // A faulted task rather than a synchronous throw: that is how a real async device
            // surfaces a failure, and it is the path the probe's catch clauses actually see.
            return outcome switch
            {
                Exception ex => Task.FromException<LanChipInfo?>(ex),
                LanChipInfo info => Task.FromResult<LanChipInfo?>(info),
                _ => Task.FromResult<LanChipInfo?>(null),
            };
        }
    }

    /// <summary>
    /// The scripted provider as a full device, so the probe can find something to send the
    /// <c>LAN:APPLY</c> kick through.
    /// </summary>
    private sealed class ScriptedLanChipInfoDevice : ScriptedLanChipInfoProvider, IStreamingDevice
    {
        public ScriptedLanChipInfoDevice(params object?[] outcomes)
            : base(outcomes)
        {
        }

        /// <summary>Number of <c>SYSTem:COMMunicate:LAN:APPLY</c> commands sent.</summary>
        public int LanApplySentCount { get; private set; }

        public List<string> SentCommands { get; } = [];

        /// <summary>Makes <see cref="Send{T}"/> fail, standing in for a transport that has gone away.</summary>
        public bool ThrowOnSend { get; set; }

        public string Name => "SCRIPTED";
        public IPAddress? IpAddress => null;
        public bool IsConnected { get; set; } = true;
        public ConnectionStatus Status => IsConnected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected;
        public int StreamingFrequency { get; set; }
        public bool IsStreaming { get; private set; }

        public event EventHandler<DeviceStatusEventArgs>? StatusChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<MessageReceivedEventArgs>? MessageReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<DeviceErrorEventArgs>? ErrorOccurred
        {
            add { }
            remove { }
        }

        public void Connect() => IsConnected = true;

        public void Disconnect() => IsConnected = false;

        public void Send<T>(IOutboundMessage<T> message)
        {
            if (ThrowOnSend)
            {
                throw new InvalidOperationException("Simulated transport failure.");
            }

            if (message is IOutboundMessage<string> textMessage)
            {
                SentCommands.Add(textMessage.Data);
                if (textMessage.Data == "SYSTem:COMMunicate:LAN:APPLY")
                {
                    LanApplySentCount++;
                }
            }
        }

        public void StartStreaming() => IsStreaming = true;
        public void StopStreaming() => IsStreaming = false;
        public void EnableChannel(IChannel channel) { }
        public void EnableChannels(IEnumerable<IChannel> channels) { }
        public void DisableChannel(IChannel channel) { }
        public void DisableAllChannels() { }
        public void SetDioDirection(IChannel channel, ChannelDirection direction) { }
        public void SetDioValue(IChannel channel, bool value) { }
        public void SetPwmEnabled(IChannel channel, bool enabled) { }
        public void SetPwmDutyCycle(IChannel channel, int dutyCyclePercent) { }
        public void SetPwmFrequency(int frequencyHz) { }
        public int PwmFrequencyHz => 0;
        public void SetAnalogOutput(int channelNumber, double voltage) { }
        public void Reboot() => Disconnect();
        public void SaveAdcCalibration() { }
        public void LoadAdcCalibration() { }
        public void SaveVoltagePrecision() { }
        public void LoadVoltagePrecision() { }
        public void SetAdcCalibrationSlope(int channelNumber, double calM) { }
        public void SetAdcCalibrationOffset(int channelNumber, double calB) { }
        public void SaveFactoryAdcCalibration() { }
        public void LoadFactoryAdcCalibration() { }
        public void UseAdcCalibration(int bank) { }
    }
}
