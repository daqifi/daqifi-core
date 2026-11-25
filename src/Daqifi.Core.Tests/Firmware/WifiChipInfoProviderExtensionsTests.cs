using System.Diagnostics;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

/// <summary>
/// The bounded chip-info probe that <see cref="WifiChipInfoProviderExtensions.GetWifiChipInfoWithRetryAsync"/>
/// exposes for consumers (issue #269). Core's own WiFi status check runs the same code, so the
/// end-to-end expectations live in <c>FirmwareUpdateServiceTests</c>; these pin the probe itself.
/// </summary>
public class WifiChipInfoProviderExtensionsTests
{
    private static WifiChipInfo SampleChipInfo => new()
    {
        ChipId = 1377184,
        FwVersion = "19.7.7",
        BuildDate = "Mar 30 2022"
    };

    private static readonly TimeSpan FastRetryDelay = TimeSpan.FromMilliseconds(5);

    private static WifiChipInfoRetryOptions FastOptions(
        int maxAttempts = 3,
        bool kickWifiApply = true,
        IProgress<WifiChipInfoProbeProgress>? progress = null) => new()
        {
            MaximumAttempts = maxAttempts,
            RetryDelay = FastRetryDelay,
            TotalTimeout = TimeSpan.FromSeconds(30),
            KickWifiApplyOnNotInitialized = kickWifiApply,
            Progress = progress,
        };

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenFirstAttemptSucceeds_QueriesOnce()
    {
        var device = new ScriptedWifiChipInfoDevice(SampleChipInfo);

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions());

        Assert.True(result.Succeeded);
        Assert.Equal("19.7.7", result.ChipInfo!.FwVersion);
        Assert.False(result.WasWifiNotInitialized);
        Assert.Equal(1, device.GetWifiChipInfoCallCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenTransientFailuresPrecedeSuccess_RetriesUntilItReads()
    {
        // #144: right after a PIC32 update the application is up while WiFi is still starting, so
        // the first queries fail. Taking the first failure as the answer is what sends a caller
        // into a needless multi-minute reflash of already-current firmware.
        var device = new ScriptedWifiChipInfoDevice(
            new InvalidOperationException("transient"),
            new InvalidOperationException("transient"),
            SampleChipInfo);

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions());

        Assert.True(result.Succeeded);
        Assert.Equal(3, device.GetWifiChipInfoCallCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenEveryAttemptFails_ReturnsUnavailableWithoutThrowing()
    {
        var device = new ScriptedWifiChipInfoDevice(new InvalidOperationException("still broken"));

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions());

        Assert.False(result.Succeeded);
        Assert.Null(result.ChipInfo);
        Assert.False(result.WasWifiNotInitialized);
        Assert.Equal(3, device.GetWifiChipInfoCallCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenProviderReturnsNull_RetriesAndReportsUnavailable()
    {
        // An unrecognizable response is a null, not an exception — it must be retried too.
        var device = new ScriptedWifiChipInfoDevice((object?)null);

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions());

        Assert.False(result.Succeeded);
        Assert.False(result.WasWifiNotInitialized);
        Assert.Equal(3, device.GetWifiChipInfoCallCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenWifiNotInitialized_SendsLanApplyExactlyOnceAndRecovers()
    {
        // #203: LAN:ENAbled=1 but the WINC state machine hasn't reached INITIALIZED, so the query
        // answers SCPI -200. One LAN:APPLY resolves it; kicking on every attempt would tear the
        // module down and re-init it repeatedly, risking an already-associated link.
        var device = new ScriptedWifiChipInfoDevice(
            new WifiNotInitializedException("**ERROR: -200, \"Execution error\""),
            new WifiNotInitializedException("**ERROR: -200, \"Execution error\""),
            SampleChipInfo);

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions());

        Assert.True(result.Succeeded);
        Assert.Equal(3, device.GetWifiChipInfoCallCount);
        Assert.Equal(1, device.LanApplySentCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenWifiNotInitializedExhaustsBudget_ReportsThatSpecifically()
    {
        var device = new ScriptedWifiChipInfoDevice(
            new WifiNotInitializedException("**ERROR: -200, \"Execution error\""));

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions());

        Assert.False(result.Succeeded);
        Assert.True(result.WasWifiNotInitialized);
        Assert.Equal(1, device.LanApplySentCount);
        Assert.Equal(3, device.GetWifiChipInfoCallCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenALaterFailureIsNotWifiNotInitialized_ReportsTheTerminalCondition()
    {
        // The flag describes how the probe actually ended, not whether -200 was ever seen. A stale
        // "not initialized" would send a caller off kicking APPLY at a module that failed for some
        // entirely different reason on the attempt that mattered.
        var device = new ScriptedWifiChipInfoDevice(
            new WifiNotInitializedException("**ERROR: -200, \"Execution error\""),
            new InvalidOperationException("transport went away"),
            new InvalidOperationException("transport went away"));

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions());

        Assert.False(result.Succeeded);
        Assert.False(result.WasWifiNotInitialized);
        Assert.Equal(3, device.GetWifiChipInfoCallCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenKickDisabled_DoesNotSendLanApply()
    {
        var device = new ScriptedWifiChipInfoDevice(
            new WifiNotInitializedException("**ERROR: -200, \"Execution error\""));

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions(kickWifiApply: false));

        Assert.True(result.WasWifiNotInitialized);
        Assert.Equal(0, device.LanApplySentCount);
        Assert.DoesNotContain("SYSTem:COMMunicate:LAN:APPLY", device.SentCommands);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenDeviceDisconnected_DoesNotSendLanApply()
    {
        // There is nothing to send through: the kick would throw or, worse, silently queue.
        var device = new ScriptedWifiChipInfoDevice(
            new WifiNotInitializedException("**ERROR: -200, \"Execution error\""))
        {
            IsConnected = false
        };

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions());

        Assert.True(result.WasWifiNotInitialized);
        Assert.Equal(0, device.LanApplySentCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenLanApplySendThrows_KeepsRetrying()
    {
        // Best effort: a failed kick must not abort the probe — the module may still settle on its
        // own within the remaining attempts.
        var device = new ScriptedWifiChipInfoDevice(
            new WifiNotInitializedException("**ERROR: -200, \"Execution error\""),
            SampleChipInfo)
        {
            ThrowOnSend = true
        };

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions());

        Assert.True(result.Succeeded);
        Assert.Equal(2, device.GetWifiChipInfoCallCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenProviderIsNotAStreamingDevice_StillProbesWithoutKicking()
    {
        // The kick needs a device to send through. A bare provider simply doesn't get one, and that
        // must not be an error.
        var provider = new ScriptedWifiChipInfoProvider(
            new WifiNotInitializedException("**ERROR: -200, \"Execution error\""),
            SampleChipInfo);

        var result = await provider.GetWifiChipInfoWithRetryAsync(FastOptions());

        Assert.True(result.Succeeded);
        Assert.Equal(2, provider.GetWifiChipInfoCallCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenTotalTimeoutExpires_StopsEarlyWithoutThrowing()
    {
        // The wall-clock ceiling is the point: attempts × the device's own response timeout plus
        // the delays can far outrun what the caller meant to spend, and this runs under a lock.
        var device = new ScriptedWifiChipInfoDevice(new InvalidOperationException("slow and broken"));
        var options = new WifiChipInfoRetryOptions
        {
            MaximumAttempts = 50,
            RetryDelay = TimeSpan.FromMilliseconds(20),
            TotalTimeout = TimeSpan.FromMilliseconds(120),
        };

        var stopwatch = Stopwatch.StartNew();
        var result = await device.GetWifiChipInfoWithRetryAsync(options);
        stopwatch.Stop();

        Assert.False(result.Succeeded);
        Assert.True(
            device.GetWifiChipInfoCallCount < 50,
            $"Expected the total-timeout budget to cut the probe short, but all 50 attempts ran ({device.GetWifiChipInfoCallCount}).");
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenCallerCancels_Throws()
    {
        // The caller's own cancellation is the one thing that is not absorbed into a result.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var device = new ScriptedWifiChipInfoDevice(SampleChipInfo);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.GetWifiChipInfoWithRetryAsync(FastOptions(), cancellationToken: cts.Token));

        // The token the probe surfaces is the linked one, so identity is not the assertion —
        // what matters is that it reports cancellation, and that nothing was queried.
        Assert.True(ex.CancellationToken.IsCancellationRequested);
        Assert.Equal(0, device.GetWifiChipInfoCallCount);
        Assert.Equal(0, device.LanApplySentCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenCancelledRacingNotInitialized_DoesNotSendLanApply()
    {
        // A cancellation that lands between the -200 detection and the kick must not still leave a
        // state-changing command on the device.
        using var cts = new CancellationTokenSource();
        var device = new ScriptedWifiChipInfoDevice(
            new WifiNotInitializedException("**ERROR: -200, \"Execution error\""));
        device.OnGetWifiChipInfo = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.GetWifiChipInfoWithRetryAsync(FastOptions(), cancellationToken: cts.Token));

        Assert.Equal(0, device.LanApplySentCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenMaxAttemptsBelowOne_StillMakesOneAttempt()
    {
        // The options object is a plain settable record a consumer can get wrong; a probe that
        // queries nothing at all and reports "unavailable" would be indistinguishable from a dead
        // module.
        var device = new ScriptedWifiChipInfoDevice(SampleChipInfo);

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions(maxAttempts: 0));

        Assert.True(result.Succeeded);
        Assert.Equal(1, device.GetWifiChipInfoCallCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenSingleAttemptFails_DoesNotWaitTheRetryDelay()
    {
        // No delay after the final attempt — otherwise every exhausted probe pays for a retry it
        // never makes.
        var device = new ScriptedWifiChipInfoDevice(new InvalidOperationException("broken"));
        var options = new WifiChipInfoRetryOptions
        {
            MaximumAttempts = 1,
            RetryDelay = TimeSpan.FromSeconds(10),
            TotalTimeout = TimeSpan.FromSeconds(30),
        };

        var stopwatch = Stopwatch.StartNew();
        var result = await device.GetWifiChipInfoWithRetryAsync(options);
        stopwatch.Stop();

        Assert.False(result.Succeeded);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"A single-attempt probe waited {stopwatch.Elapsed} — the trailing retry delay was applied.");
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenTotalTimeoutIsNegative_MakesNoAttemptWithoutThrowing()
    {
        // A budget of "less than nothing" is the same as no budget, which the option documents as
        // "no attempt is made". Handing it straight to CancellationTokenSource would instead throw
        // ArgumentOutOfRangeException out of a probe whose whole contract is that failures come back
        // as an unavailable result.
        var device = new ScriptedWifiChipInfoDevice(SampleChipInfo);
        var options = new WifiChipInfoRetryOptions
        {
            MaximumAttempts = 3,
            RetryDelay = TimeSpan.FromMilliseconds(5),
            TotalTimeout = TimeSpan.FromSeconds(-5),
        };

        var result = await device.GetWifiChipInfoWithRetryAsync(options);

        Assert.False(result.Succeeded);
        Assert.False(result.WasWifiNotInitialized);
        Assert.Equal(0, device.GetWifiChipInfoCallCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenTotalTimeoutIsInfinite_SpendsEveryAttempt()
    {
        // Timeout.InfiniteTimeSpan is negative, but it is how .NET spells "no ceiling" and must keep
        // meaning that — collapsing it into the no-budget case would silently turn an unbounded
        // probe into one that never queries at all.
        var device = new ScriptedWifiChipInfoDevice(new InvalidOperationException("broken"));
        var options = new WifiChipInfoRetryOptions
        {
            MaximumAttempts = 3,
            RetryDelay = TimeSpan.FromMilliseconds(5),
            TotalTimeout = Timeout.InfiniteTimeSpan,
        };

        var result = await device.GetWifiChipInfoWithRetryAsync(options);

        Assert.False(result.Succeeded);
        Assert.Equal(3, device.GetWifiChipInfoCallCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenRetryDelayIsNegative_KeepsRetryingWithoutThrowing()
    {
        // Same reasoning as the budget: a negative pause is a misconfiguration, not a reason to
        // throw ArgumentOutOfRangeException out of Task.Delay midway through the loop.
        var device = new ScriptedWifiChipInfoDevice(
            new InvalidOperationException("transient"),
            new InvalidOperationException("transient"),
            SampleChipInfo);
        var options = new WifiChipInfoRetryOptions
        {
            MaximumAttempts = 3,
            RetryDelay = TimeSpan.FromSeconds(-1),
            TotalTimeout = TimeSpan.FromSeconds(30),
        };

        var result = await device.GetWifiChipInfoWithRetryAsync(options);

        Assert.True(result.Succeeded);
        Assert.Equal(3, device.GetWifiChipInfoCallCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenRetryDelayIsInfinite_DoesNotHangBetweenAttempts()
    {
        // An infinite *pause* inside a bounded retry has no meaning, and with an equally unbounded
        // budget nothing would ever release it — the probe would hang on the caller's thread
        // forever. The guard token bounds a regression here to 10s instead of wedging the suite.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var device = new ScriptedWifiChipInfoDevice(new InvalidOperationException("broken"));
        var options = new WifiChipInfoRetryOptions
        {
            MaximumAttempts = 2,
            RetryDelay = Timeout.InfiniteTimeSpan,
            TotalTimeout = Timeout.InfiniteTimeSpan,
        };

        var stopwatch = Stopwatch.StartNew();
        var result = await device.GetWifiChipInfoWithRetryAsync(options, cancellationToken: guard.Token);
        stopwatch.Stop();

        Assert.False(result.Succeeded);
        Assert.Equal(2, device.GetWifiChipInfoCallCount);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"An infinite retry delay stalled the probe for {stopwatch.Elapsed}.");
    }

    [Fact]
    public void WifiChipInfoRetryOptions_RecordDefaults_MatchTheCoreWifiProbe()
    {
        // The record's own defaults, not a probe that happens to construct one. A failing first
        // read would sleep RetryDelay (2s) before the next attempt; this fact never calls the probe.
        var defaults = new WifiChipInfoRetryOptions();

        Assert.Equal(3, defaults.MaximumAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), defaults.RetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(8), defaults.TotalTimeout);
        Assert.True(defaults.KickWifiApplyOnNotInitialized);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WithoutOptions_ReturnsScriptedChipInfoOnTheFirstAttempt()
    {
        // A successful first read returns immediately. The default 2s RetryDelay is only the pause
        // between attempts, so this must not sleep it.
        var chipInfo = SampleChipInfo;
        var device = new ScriptedWifiChipInfoDevice(chipInfo);

        var result = await device.GetWifiChipInfoWithRetryAsync();

        Assert.Same(chipInfo, result.ChipInfo);
        Assert.True(result.Succeeded);
        Assert.Equal(1, device.GetWifiChipInfoCallCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenProviderIsNull_Throws()
    {
        IWifiChipInfoProvider? provider = null;

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => provider!.GetWifiChipInfoWithRetryAsync());

        Assert.Equal("provider", ex.ParamName);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WithTheRecordDefaultAttemptCount_QueriesExactlyThatManyTimes()
    {
        // Pins the retry budget's attempt count to the record's own default rather than to a
        // literal, so a change to the shipped default cannot slip past this file. The delay and
        // the ceiling are overridden to keep the test fast; the attempt count is not.
        var defaultAttempts = new WifiChipInfoRetryOptions().MaximumAttempts;
        var device = new ScriptedWifiChipInfoDevice(new InvalidOperationException("broken"));
        var options = new WifiChipInfoRetryOptions
        {
            RetryDelay = TimeSpan.FromMilliseconds(5),
            TotalTimeout = TimeSpan.FromSeconds(30),
        };

        var result = await device.GetWifiChipInfoWithRetryAsync(options);

        Assert.False(result.Succeeded);
        Assert.Equal(3, defaultAttempts);
        Assert.Equal(defaultAttempts, device.GetWifiChipInfoCallCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenTheFirstAttemptSucceeds_ReportsNothing()
    {
        // A probe that reads the module straight away never showed the user a wait, so there is
        // nothing to say about it. A single "attempt 1 of 3" here would make every healthy device
        // flash a "retrying" status line.
        var progress = new RecordingProgress();
        var device = new ScriptedWifiChipInfoDevice(SampleChipInfo);

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions(progress: progress));

        Assert.True(result.Succeeded);
        Assert.Empty(progress.Reports);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenItSucceedsOnALaterAttempt_ReportsEachFailedAttemptInOrder()
    {
        var progress = new RecordingProgress();
        var device = new ScriptedWifiChipInfoDevice(
            new InvalidOperationException("transient"),
            new InvalidOperationException("transient"),
            SampleChipInfo);

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions(progress: progress));

        Assert.True(result.Succeeded);
        Assert.Equal(
            [
                AttemptFailed(1, 3, false, FastRetryDelay),
                AttemptFailed(2, 3, false, FastRetryDelay),
            ],
            progress.Reports);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenEveryAttemptFails_ReportsEveryOneAndStillReturnsUnavailable()
    {
        // The last report carries TimeSpan.Zero: no retry follows it. That is how a caller tells
        // "retrying" from "that was the last try" without counting attempts itself.
        var progress = new RecordingProgress();
        var device = new ScriptedWifiChipInfoDevice(new InvalidOperationException("still broken"));

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions(progress: progress));

        Assert.False(result.Succeeded);
        Assert.Null(result.ChipInfo);
        Assert.False(result.WasWifiNotInitialized);
        Assert.Equal(3, device.GetWifiChipInfoCallCount);
        Assert.Equal(
            [
                AttemptFailed(1, 3, false, FastRetryDelay),
                AttemptFailed(2, 3, false, FastRetryDelay),
                AttemptFailed(3, 3, false, TimeSpan.Zero),
            ],
            progress.Reports);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenOnlyOneAttemptSawTheNotInitializedState_ReportsItOnThatAttemptAlone()
    {
        // The flag on a report describes the attempt being reported, not the worst thing seen so
        // far — a status line that said "WiFi still starting up" would otherwise keep saying it
        // after the module started failing for an unrelated reason.
        var progress = new RecordingProgress();
        var device = new ScriptedWifiChipInfoDevice(
            new WifiNotInitializedException("**ERROR: -200, \"Execution error\""),
            new InvalidOperationException("transport went away"),
            SampleChipInfo);

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions(progress: progress));

        Assert.True(result.Succeeded);
        Assert.Equal(
            [
                AttemptFailed(1, 3, true, FastRetryDelay),
                AttemptFailed(2, 3, false, FastRetryDelay),
            ],
            progress.Reports);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenAnAttemptAnswersNull_ReportsItLikeAnyOtherFailure()
    {
        var progress = new RecordingProgress();
        var device = new ScriptedWifiChipInfoDevice((object?)null, SampleChipInfo);

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions(progress: progress));

        Assert.True(result.Succeeded);
        Assert.Equal(
            [AttemptFailed(1, 3, false, FastRetryDelay)],
            progress.Reports);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WithNoProgressSink_StillSpendsTheSameBudget()
    {
        // The seam is additive: the sink defaults to null, and a caller that sets nothing gets the
        // behaviour that shipped. Every other fact in this file calls the probe without one.
        Assert.Null(new WifiChipInfoRetryOptions().Progress);

        var device = new ScriptedWifiChipInfoDevice(new InvalidOperationException("still broken"));
        var options = new WifiChipInfoRetryOptions
        {
            RetryDelay = FastRetryDelay,
            TotalTimeout = TimeSpan.FromSeconds(30),
        };

        var result = await device.GetWifiChipInfoWithRetryAsync(options);

        Assert.False(result.Succeeded);
        Assert.Equal(3, device.GetWifiChipInfoCallCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenTheProgressHandlerThrows_StillReadsTheModule()
    {
        // A consumer's status handler must not be able to discard a chip-info read the probe was
        // one attempt away from making. Reporting is best effort.
        var progress = new RecordingProgress(_ => throw new InvalidOperationException("handler blew up"));
        var device = new ScriptedWifiChipInfoDevice(
            new InvalidOperationException("transient"),
            new InvalidOperationException("transient"),
            SampleChipInfo);

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions(progress: progress));

        Assert.True(result.Succeeded);
        Assert.Equal("19.7.7", result.ChipInfo!.FwVersion);
        Assert.Equal(3, device.GetWifiChipInfoCallCount);
        Assert.Equal(2, progress.Reports.Count);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenTheProgressHandlerThrowsAndEveryAttemptFails_ReportsUnavailableAsBefore()
    {
        // The other half: a throwing handler must not turn an absorbed failure into an exception
        // out of a method documented to return "unavailable" instead.
        var progress = new RecordingProgress(_ => throw new InvalidOperationException("handler blew up"));
        var device = new ScriptedWifiChipInfoDevice(
            new WifiNotInitializedException("**ERROR: -200, \"Execution error\""));

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions(progress: progress));

        Assert.False(result.Succeeded);
        Assert.True(result.WasWifiNotInitialized);
        Assert.Equal(3, device.GetWifiChipInfoCallCount);
        Assert.Equal(3, progress.Reports.Count);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenTheProgressHandlerThrowsOperationCanceled_DoesNotEndTheProbe()
    {
        // Cancellation belongs to the caller's token. A handler that throws this particular
        // exception — e.g. by checking a token of its own — must not look like one.
        var progress = new RecordingProgress(_ => throw new OperationCanceledException("not the caller"));
        var device = new ScriptedWifiChipInfoDevice(
            new InvalidOperationException("transient"),
            SampleChipInfo);

        var result = await device.GetWifiChipInfoWithRetryAsync(FastOptions(progress: progress));

        Assert.True(result.Succeeded);
        Assert.Equal(2, device.GetWifiChipInfoCallCount);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenCallerCancelsBeforeTheFirstAttempt_ReportsNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var progress = new RecordingProgress();
        var device = new ScriptedWifiChipInfoDevice(SampleChipInfo);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.GetWifiChipInfoWithRetryAsync(
                FastOptions(progress: progress),
                cancellationToken: cts.Token));

        Assert.Empty(progress.Reports);
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenTheTotalTimeoutCutsTheProbeShort_ReportsStayOrderedAndBounded()
    {
        // The wall-clock branches end the probe rather than falling through to a report, so how
        // many arrive depends on where the budget expired. What must hold regardless: the attempt
        // numbers are 1, 2, 3, ... with no gaps and no duplicates, and never more of them than
        // attempts were made.
        var progress = new RecordingProgress();
        var device = new ScriptedWifiChipInfoDevice(new InvalidOperationException("slow and broken"));
        var options = new WifiChipInfoRetryOptions
        {
            MaximumAttempts = 50,
            RetryDelay = TimeSpan.FromMilliseconds(20),
            TotalTimeout = TimeSpan.FromMilliseconds(120),
            Progress = progress,
        };

        var result = await device.GetWifiChipInfoWithRetryAsync(options);

        Assert.False(result.Succeeded);
        Assert.True(
            progress.Reports.Count <= device.GetWifiChipInfoCallCount,
            $"Got {progress.Reports.Count} reports for {device.GetWifiChipInfoCallCount} attempts.");
        Assert.Equal(
            Enumerable.Range(1, progress.Reports.Count),
            progress.Reports.Select(report => report.Attempt));
        Assert.All(progress.Reports, report => Assert.Equal(50, report.MaximumAttempts));
    }

    [Fact]
    public async Task GetWifiChipInfoWithRetryAsync_WhenTheBudgetIsASingleAttempt_ReportsItWithNoRetryDelay()
    {
        var progress = new RecordingProgress();
        var device = new ScriptedWifiChipInfoDevice(new InvalidOperationException("broken"));

        var result = await device.GetWifiChipInfoWithRetryAsync(
            FastOptions(maxAttempts: 1, progress: progress));

        Assert.False(result.Succeeded);
        Assert.Equal(
            [AttemptFailed(1, 1, false, TimeSpan.Zero)],
            progress.Reports);
    }

    /// <summary>
    /// Records every report in arrival order, and can throw from the handler afterwards to stand
    /// in for a consumer whose status code is broken.
    /// </summary>
    /// <remarks>
    /// A direct <see cref="IProgress{T}"/> rather than <see cref="Progress{T}"/>: the BCL class
    /// posts to a synchronization context, which would both reorder the assertions and hide a
    /// throwing handler behind its own marshalling. This one reports on the probe's own thread,
    /// which is the strict case.
    /// </remarks>
    private static WifiChipInfoProbeProgress AttemptFailed(
        int attempt, int maxAttempts, bool wasWifiNotInitialized, TimeSpan retryDelay) => new()
        {
            Attempt = attempt,
            MaximumAttempts = maxAttempts,
            WasWifiNotInitialized = wasWifiNotInitialized,
            RetryDelay = retryDelay
        };

    private sealed class RecordingProgress(Action<WifiChipInfoProbeProgress>? onReport = null)
        : IProgress<WifiChipInfoProbeProgress>
    {
        private readonly List<WifiChipInfoProbeProgress> _reports = [];

        public IReadOnlyList<WifiChipInfoProbeProgress> Reports => _reports;

        public void Report(WifiChipInfoProbeProgress value)
        {
            // Recorded before the handler runs, so a throwing handler still shows what it saw.
            _reports.Add(value);
            onReport?.Invoke(value);
        }
    }

    /// <summary>
    /// A provider whose successive answers are scripted: each outcome is a <see cref="WifiChipInfo"/>
    /// to return, an <see cref="Exception"/> to fault with, or null for an unparseable response. The
    /// last outcome repeats, so "always fails" is a one-element script.
    /// </summary>
    private class ScriptedWifiChipInfoProvider : IWifiChipInfoProvider
    {
        private readonly IReadOnlyList<object?> _outcomes;
        private int _next;

        public ScriptedWifiChipInfoProvider(params object?[] outcomes)
        {
            _outcomes = outcomes.Length > 0 ? outcomes : [null];
        }

        public int GetWifiChipInfoCallCount { get; private set; }

        /// <summary>Runs as the query lands, for tests that need to race it.</summary>
        public Action? OnGetWifiChipInfo { get; set; }

        public Task<WifiChipInfo?> GetWifiChipInfoAsync(CancellationToken cancellationToken = default)
        {
            GetWifiChipInfoCallCount++;
            OnGetWifiChipInfo?.Invoke();

            var outcome = _outcomes[Math.Min(_next, _outcomes.Count - 1)];
            _next++;

            // A faulted task rather than a synchronous throw: that is how a real async device
            // surfaces a failure, and it is the path the probe's catch clauses actually see.
            return outcome switch
            {
                Exception ex => Task.FromException<WifiChipInfo?>(ex),
                WifiChipInfo info => Task.FromResult<WifiChipInfo?>(info),
                _ => Task.FromResult<WifiChipInfo?>(null),
            };
        }
    }

    /// <summary>
    /// The scripted provider as a full device, so the probe can find something to send the
    /// <c>LAN:APPLY</c> kick through.
    /// </summary>
    private sealed class ScriptedWifiChipInfoDevice : ScriptedWifiChipInfoProvider, IStreamingDevice
    {
        public ScriptedWifiChipInfoDevice(params object?[] outcomes)
            : base(outcomes)
        {
        }

        /// <summary>Number of <c>SYSTem:COMMunicate:LAN:APPLY</c> commands sent.</summary>
        public int LanApplySentCount { get; private set; }

        public List<string> SentCommands { get; } = [];

        /// <summary>Makes <see cref="Send{T}"/> fail, standing in for a transport that has gone away.</summary>
        public bool ThrowOnSend { get; set; }

        public string Name => "SCRIPTED";
        public bool IsConnected { get; set; } = true;
        public DeviceState State => IsConnected ? DeviceState.Connected : DeviceState.Disconnected;
        public DeviceMetadata Metadata { get; } = new();
        public IReadOnlyList<IChannel> Channels => Array.Empty<IChannel>();
        public IReadOnlyList<IChannel> GetChannelsSnapshot() => Array.Empty<IChannel>();
        public int SampleRateHz { get; set; }
        public bool IsStreaming { get; private set; }

        public event EventHandler<DeviceStateChangedEventArgs>? StateChanged
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

        public event EventHandler<ChannelsPopulatedEventArgs>? ChannelsPopulated
        {
            add { }
            remove { }
        }

        public void Connect() => IsConnected = true;

        public void Disconnect() => IsConnected = false;

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connect();
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            Disconnect();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disconnect();
            return ValueTask.CompletedTask;
        }

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
        public void SetDioDirection(IDigitalChannel channel, ChannelDirection direction) { }
        public void SetDioValue(IDigitalChannel channel, bool value) { }
        public void SetPwmEnabled(IDigitalChannel channel, bool enabled) { }
        public void SetPwmDutyCycle(IDigitalChannel channel, int dutyCyclePercent) { }
        public void SetPwmFrequency(int frequencyHz) { }
        public int PwmFrequencyHz => 0;
        public void SetAnalogOutput(IAnalogOutputChannel channel, double voltage) { }
        public int PwmFrequencyFloorHz => 0;
        public void StageAnalogOutput(IAnalogOutputChannel channel, double voltage) { }
        public void LatchAnalogOutputs() { }
        public Task<double> GetAnalogOutputAsync(IAnalogOutputChannel channel, CancellationToken cancellationToken = default) => Task.FromResult(0.0);
        public bool Supports(DeviceFeature feature) => false;
        public Task InitializeAsync(TimeSpan? channelPopulationTimeout = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshDeviceStatusAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CapabilityDocument?> ReadCapabilityDocumentAsync(CancellationToken cancellationToken = default) => Task.FromResult<CapabilityDocument?>(null);
        public void Reboot() => Disconnect();
        public Task StartStreamingAsync(CancellationToken cancellationToken = default) { StartStreaming(); return Task.CompletedTask; }
        public Task StopStreamingAsync(CancellationToken cancellationToken = default) { StopStreaming(); return Task.CompletedTask; }
        public Task RebootAsync(CancellationToken cancellationToken = default) { Reboot(); return Task.CompletedTask; }

        public Task SaveAdcCalibrationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAdcCalibrationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetAdcCalibrationSlopeAsync(int channelNumber, double calM, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetAdcCalibrationOffsetAsync(int channelNumber, double calB, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UseAdcCalibrationAsync(AdcCalibrationBank bank, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveVoltagePrecisionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadVoltagePrecisionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
