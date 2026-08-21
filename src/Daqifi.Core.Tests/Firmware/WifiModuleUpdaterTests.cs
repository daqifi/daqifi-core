using System.Diagnostics;
using Daqifi.Core.Device;
using Daqifi.Core.Firmware;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Core.Tests.Firmware;

/// <summary>
/// Direct tests for <see cref="WifiModuleUpdater"/>, the collaborator behind
/// <c>FirmwareUpdateService.UpdateWifiModuleAsync</c> / <c>CheckWifiFirmwareStatusAsync</c>
/// (part of #464).
/// </summary>
/// <remarks>
/// Deliberately NOT a re-run of <c>FirmwareUpdateServiceTests</c> at one level down: the facade
/// tests already cover the WiFi flow's happy path, cancellation points and bridge-exit recovery,
/// and duplicating them here would only mean two places to edit. What lives here is what the
/// facade cannot reach or cannot state cleanly — the tool/port resolution and argument quoting,
/// each branch of the "why did the flash not report success" verdict, the retry policy's
/// boundaries (including an attempt count mutated below its validated minimum), the one-shot stdin
/// prompt handshake and its per-attempt freshness, and the status probe's behaviour when the
/// mutable options object is changed after construction.
/// </remarks>
public class WifiModuleUpdaterTests : IDisposable
{
    private const string SuccessMarker = "Operation completed successfully";
    private const string WincBootPrompt = "Power cycle WINC and set to bootloader mode";
    private const string ContinuePrompt = "Press any key to continue";
    private const string BridgeIdQueryFailure = "failed to read serial bridge ID query response";
    private const string ProgrammerInitFailure = "failed to initialise programming firmware";

    private readonly List<string> _tempDirectories = [];

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------------------------------
    // Flash tool + port resolution
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RunUpdate_WhenFirmwarePathDoesNotExist_FailsWithTheMissingPath()
    {
        var (updater, _) = CreateUpdater(CreateFastOptions(), new FakeExternalProcessRunner());
        var device = new FakeStreamingDevice("COM7");
        var missingPath = Path.Combine(Path.GetTempPath(), "daqifi-core-tests-missing-" + Guid.NewGuid().ToString("N"));

        var error = await Assert.ThrowsAsync<FirmwareUpdateException>(
            () => RunUpdateAsync(updater, device, missingPath));

        var notFound = Assert.IsType<FileNotFoundException>(error.InnerException);
        Assert.Equal(missingPath, notFound.FileName);
    }

    [Fact]
    public async Task RunUpdate_WhenToolIsMissingUnderTheFirmwarePath_SaysWhyForThisPlatform()
    {
        var options = CreateFastOptions();
        var (updater, _) = CreateUpdater(options, new FakeExternalProcessRunner());
        var device = new FakeStreamingDevice("COM7");

        // The directory exists and holds firmware, just not the Microchip flash tool.
        var firmwareDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDirectory, "firmware.bin"), "payload");

        var error = await Assert.ThrowsAsync<FirmwareUpdateException>(
            () => RunUpdateAsync(updater, device, firmwareDirectory));

        var notFound = Assert.IsType<FileNotFoundException>(error.InnerException);
        Assert.Contains(options.WifiFlashToolFileName, notFound.Message, StringComparison.Ordinal);
        Assert.Contains(firmwareDirectory, notFound.Message, StringComparison.Ordinal);

        // Off Windows this is not a misconfigured path — Microchip ships the tool as a Windows
        // program — and the message has to say so, or the caller reads it as a bad download.
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("Windows-only", notFound.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("Windows-only", notFound.Message, StringComparison.Ordinal);
            Assert.Contains("#271", notFound.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RunUpdate_PrefersThePortOverrideOverTheDeviceName()
    {
        var options = CreateFastOptions();
        options.WifiPortOverride = "COM42";

        var runner = SucceedingRunner();
        var (updater, _) = CreateUpdater(options, runner);

        await RunUpdateAsync(updater, new FakeStreamingDevice("COM7"), CreateFirmwareDirectory());

        Assert.NotNull(runner.LastRequest);
        Assert.Contains("/p COM42", runner.LastRequest!.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("COM7", runner.LastRequest.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunUpdate_WithoutAPortOverride_FlashesThroughTheDevicesOwnPort()
    {
        var runner = SucceedingRunner();
        var (updater, _) = CreateUpdater(CreateFastOptions(), runner);

        await RunUpdateAsync(updater, new FakeStreamingDevice("COM7"), CreateFirmwareDirectory());

        Assert.NotNull(runner.LastRequest);
        Assert.Contains("/p COM7", runner.LastRequest!.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunUpdate_WhenNoPortCanBeResolved_FailsRatherThanFlashingAnEmptyPort()
    {
        var runner = SucceedingRunner();
        var (updater, _) = CreateUpdater(CreateFastOptions(), runner);

        // No override, and a device that cannot name its own port.
        var error = await Assert.ThrowsAsync<FirmwareUpdateException>(
            () => RunUpdateAsync(updater, new FakeStreamingDevice(string.Empty), CreateFirmwareDirectory()));

        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(0, runner.RunCount);
    }

    [Fact]
    public async Task RunUpdate_QuotesArgumentsContainingSpaces()
    {
        var options = CreateFastOptions();
        options.WifiPortOverride = "COM 42";

        // The default template substitutes only {port}; a host that also passes the image path
        // is what exercises the second substitution, and a temp path with a space in it is
        // exactly where an unquoted argument silently splits into two.
        options.WifiFlashToolArgumentsTemplate = "/p {port} /f {firmwarePath} /w";

        var runner = SucceedingRunner();
        var (updater, _) = CreateUpdater(options, runner);

        var firmwareDirectory = CreateFirmwareDirectory("daqifi core tests with spaces");

        await RunUpdateAsync(updater, new FakeStreamingDevice("COM7"), firmwareDirectory);

        Assert.NotNull(runner.LastRequest);
        Assert.Contains("/p \"COM 42\"", runner.LastRequest!.Arguments, StringComparison.Ordinal);
        Assert.Contains($"/f \"{firmwareDirectory}\"", runner.LastRequest.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunUpdate_EscapesEmbeddedQuotesWithoutWrappingASpacelessArgument()
    {
        var options = CreateFastOptions();
        options.WifiPortOverride = "CO\"M7";

        var runner = SucceedingRunner();
        var (updater, _) = CreateUpdater(options, runner);

        await RunUpdateAsync(updater, new FakeStreamingDevice("COM7"), CreateFirmwareDirectory());

        Assert.NotNull(runner.LastRequest);
        Assert.Contains("/p CO\\\"M7", runner.LastRequest!.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunUpdate_RunsACmdToolThroughTheWindowsShellAndAnythingElseDirectly()
    {
        var runner = SucceedingRunner();
        var (updater, _) = CreateUpdater(CreateFastOptions(), runner);

        var firmwareDirectory = CreateFirmwareDirectory();
        var toolPath = Path.Combine(firmwareDirectory, "winc_flash_tool.cmd");

        await RunUpdateAsync(updater, new FakeStreamingDevice("COM7"), firmwareDirectory);

        Assert.NotNull(runner.LastRequest);
        if (OperatingSystem.IsWindows())
        {
            // A .cmd is not directly executable by Process.Start's usual path, so it goes
            // through cmd.exe with the tool path itself quoted as the first argument.
            Assert.Equal("cmd.exe", runner.LastRequest!.FileName);
            Assert.StartsWith($"/c \"{toolPath}\"", runner.LastRequest.Arguments, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(toolPath, runner.LastRequest!.FileName);
            Assert.StartsWith("/p ", runner.LastRequest.Arguments, StringComparison.Ordinal);
        }

        Assert.Equal(firmwareDirectory, runner.LastRequest.WorkingDirectory);
    }

    // ---------------------------------------------------------------------------------------
    // "The tool did not report success" — one verdict per failure shape
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RunUpdate_WhenTheToolTimesOut_ReportsTheBudgetItBlewAndWhatItHadPrinted()
    {
        var options = CreateFastOptions();
        options.WifiProcessTimeout = TimeSpan.FromSeconds(90);

        var runner = new FakeExternalProcessRunner
        {
            NextResult = Result(exitCode: -1, timedOut: true, stdout: ["still working"])
        };
        var (updater, _) = CreateUpdater(options, runner);

        var error = await Assert.ThrowsAsync<FirmwareUpdateException>(
            () => RunUpdateAsync(updater, new FakeStreamingDevice("COM7"), CreateFirmwareDirectory()));

        var timeout = Assert.IsType<TimeoutException>(error.InnerException);
        Assert.Contains("90 seconds", timeout.Message, StringComparison.Ordinal);
        Assert.Contains("exit code -1", timeout.Message, StringComparison.Ordinal);
        Assert.Contains("still working", timeout.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunUpdate_WhenTheToolPrintedNothing_BlamesThePortItCouldNotOpen()
    {
        var runner = new FakeExternalProcessRunner { NextResult = Result(exitCode: 1) };
        var (updater, _) = CreateUpdater(CreateFastOptions(), runner);

        var message = await FlashFailureMessageAsync(updater, runner);

        Assert.Contains("produced no output", message, StringComparison.Ordinal);
        Assert.Contains("could not open the serial port", message, StringComparison.Ordinal);
        Assert.Contains("No process output captured.", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunUpdate_WhenTheToolWroteOnlyToStderr_SaysSoWithTheExitCode()
    {
        var runner = new FakeExternalProcessRunner
        {
            NextResult = Result(exitCode: 3, stderr: ["cannot open port"])
        };
        var (updater, _) = CreateUpdater(CreateFastOptions(), runner);

        var message = await FlashFailureMessageAsync(updater, runner);

        Assert.Contains("only to stderr", message, StringComparison.Ordinal);
        Assert.Contains("exit code 3", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunUpdate_WhenTheImageBuildFailed_DoesNotBlameTheDevice()
    {
        var runner = new FakeExternalProcessRunner
        {
            // Co-occurs with the generic device-side marker in the real tool output; the local
            // build failure has to win, because it happens before the device is ever contacted.
            NextResult = Result(
                exitCode: 1,
                stdout: ["Building programming image failed", "Programming device failed"])
        };
        var (updater, _) = CreateUpdater(CreateFastOptions(), runner);

        var message = await FlashFailureMessageAsync(updater, runner);

        Assert.Contains("failed to build the programming image", message, StringComparison.Ordinal);
        Assert.Contains("before contacting the device", message, StringComparison.Ordinal);
        Assert.DoesNotContain("reached the device", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunUpdate_WhenTheDeviceReportedAProgrammingFailure_SaysTheToolReachedIt()
    {
        var runner = new FakeExternalProcessRunner
        {
            NextResult = Result(exitCode: 1, stderr: ["Reading XO (offset) failed"])
        };
        var (updater, _) = CreateUpdater(CreateFastOptions(), runner);

        var message = await FlashFailureMessageAsync(updater, runner);

        Assert.Contains("reached the device but reported a programming failure", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunUpdate_WhenTheToolJustExited_ReportsTheExitCodeAndAnExcerptOfItsOutput()
    {
        var runner = new FakeExternalProcessRunner
        {
            NextResult = Result(
                exitCode: 7,
                stdout: ["out-1", "   ", "out-2", "out-3", "out-4", "out-5"],
                stderr: ["err-1", string.Empty, "err-2"])
        };
        var (updater, _) = CreateUpdater(CreateFastOptions(), runner);

        var message = await FlashFailureMessageAsync(updater, runner);

        Assert.Contains("exited with code 7 without completing the program", message, StringComparison.Ordinal);

        // stderr first (it is where the reason usually is), blank lines dropped, five lines max —
        // so out-4 and out-5 must not make it in.
        Assert.Contains("Process output excerpt: err-1 | err-2 | out-1 | out-2 | out-3", message, StringComparison.Ordinal);
        Assert.DoesNotContain("out-4", message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Retry policy
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RunUpdate_RetriesATransientBridgeFailureRoutedToStdout()
    {
        // Tool and script versions disagree about which stream these lines go to, so the
        // classifier scans both; keying on stderr alone would silently stop retrying.
        var runner = new FakeExternalProcessRunner();
        runner.ResultSequence.Enqueue(Result(exitCode: 1, stdout: [BridgeIdQueryFailure]));
        runner.ResultSequence.Enqueue(Result(exitCode: 0, stdout: [SuccessMarker]));

        var (updater, context) = CreateUpdater(CreateFastOptions(), runner);

        await RunUpdateAsync(updater, new FakeStreamingDevice("COM7"), CreateFirmwareDirectory());

        Assert.Equal(2, runner.RunCount);
        Assert.Equal(FirmwareUpdateState.Complete, context.CurrentState);
    }

    [Fact]
    public async Task RunUpdate_WhenEveryAttemptIsTransient_StopsAtTheConfiguredAttemptCount()
    {
        var options = CreateFastOptions();
        options.WifiFlashAttempts = 3;

        var runner = new FakeExternalProcessRunner
        {
            NextResult = Result(exitCode: 1, stderr: [ProgrammerInitFailure])
        };
        var (updater, _) = CreateUpdater(options, runner);

        await Assert.ThrowsAsync<FirmwareUpdateException>(
            () => RunUpdateAsync(updater, new FakeStreamingDevice("COM7"), CreateFirmwareDirectory()));

        Assert.Equal(3, runner.RunCount);
    }

    [Fact]
    public async Task RunUpdate_WhenOnlyOneAttemptIsAllowed_DoesNotRetryATransientFailure()
    {
        var options = CreateFastOptions();
        options.WifiFlashAttempts = 1;

        var runner = new FakeExternalProcessRunner
        {
            NextResult = Result(exitCode: 1, stderr: [BridgeIdQueryFailure])
        };
        var (updater, _) = CreateUpdater(options, runner);

        await Assert.ThrowsAsync<FirmwareUpdateException>(
            () => RunUpdateAsync(updater, new FakeStreamingDevice("COM7"), CreateFirmwareDirectory()));

        Assert.Equal(1, runner.RunCount);
    }

    [Fact]
    public async Task RunUpdate_WhenTheAttemptCountIsMisconfiguredToZero_StillRunsTheToolOnce()
    {
        // FirmwareUpdateServiceOptions.Validate rejects this, but the options object stays
        // mutable after the service is constructed, so the flow has to defend itself: zero
        // attempts would otherwise skip the loop entirely and return a null result.
        var options = CreateFastOptions();
        options.WifiFlashAttempts = 0;

        var runner = SucceedingRunner();
        var (updater, context) = CreateUpdater(options, runner);

        await RunUpdateAsync(updater, new FakeStreamingDevice("COM7"), CreateFirmwareDirectory());

        Assert.Equal(1, runner.RunCount);
        Assert.Equal(FirmwareUpdateState.Complete, context.CurrentState);
    }

    [Fact]
    public async Task RunUpdate_GivesEachAttemptItsOwnRequestSoThePromptResponderIsNeverSpent()
    {
        var runner = new FakeExternalProcessRunner();
        runner.ResultSequence.Enqueue(Result(exitCode: 1, stdout: [WincBootPrompt, BridgeIdQueryFailure]));
        runner.ResultSequence.Enqueue(Result(exitCode: 0, stdout: [WincBootPrompt, SuccessMarker]));

        var (updater, _) = CreateUpdater(CreateFastOptions(), runner);

        await RunUpdateAsync(updater, new FakeStreamingDevice("COM7"), CreateFirmwareDirectory());

        Assert.Equal(2, runner.Requests.Count);
        Assert.NotSame(runner.Requests[0], runner.Requests[1]);

        // The responder answers once per run. A shared (already-spent) responder would leave the
        // retry's WINC prompt unanswered and the tool blocked on stdin.
        Assert.Equal(2, runner.PromptResponses.Count);
        Assert.All(runner.PromptResponses, entry => Assert.Equal(string.Empty, entry.Response));
    }

    // ---------------------------------------------------------------------------------------
    // The interactive stdin prompt handshake
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RunUpdate_AtTheWincBootPrompt_ActivatesTheBridgeThenSendsContinue()
    {
        var activations = 0;
        var options = CreateFastOptions();
        options.WifiBridgeActivationCallback = () => activations++;

        var runner = new FakeExternalProcessRunner
        {
            NextResult = Result(exitCode: 0, stdout: [WincBootPrompt, SuccessMarker])
        };
        var (updater, _) = CreateUpdater(options, runner);

        await RunUpdateAsync(updater, new FakeStreamingDevice("COM7"), CreateFirmwareDirectory());

        Assert.Equal(1, activations);
        var prompt = Assert.Single(runner.PromptResponses);
        Assert.Equal(WincBootPrompt, prompt.Line);
        Assert.Equal(string.Empty, prompt.Response);
    }

    [Fact]
    public async Task RunUpdate_WhenTheWincBootPromptRepeats_AnswersItOnlyOnce()
    {
        var activations = 0;
        var options = CreateFastOptions();
        options.WifiBridgeActivationCallback = () => activations++;

        var runner = new FakeExternalProcessRunner
        {
            NextResult = Result(exitCode: 0, stdout: [WincBootPrompt, WincBootPrompt, SuccessMarker])
        };
        var (updater, _) = CreateUpdater(options, runner);

        await RunUpdateAsync(updater, new FakeStreamingDevice("COM7"), CreateFirmwareDirectory());

        // A second continue line would be read by the tool as input to whatever it asks next.
        Assert.Single(runner.PromptResponses);
        Assert.Equal(1, activations);
    }

    [Fact]
    public async Task RunUpdate_WhenOnlyTheGenericContinuePromptAppears_StillAnswersIt()
    {
        var runner = new FakeExternalProcessRunner
        {
            NextResult = Result(exitCode: 0, stdout: [ContinuePrompt, SuccessMarker])
        };
        var (updater, _) = CreateUpdater(CreateFastOptions(), runner);

        await RunUpdateAsync(updater, new FakeStreamingDevice("COM7"), CreateFirmwareDirectory());

        var prompt = Assert.Single(runner.PromptResponses);
        Assert.Equal(ContinuePrompt, prompt.Line);
    }

    [Fact]
    public async Task RunUpdate_WhenTheContinuePromptFollowsTheBootPrompt_DoesNotAnswerTwice()
    {
        var runner = new FakeExternalProcessRunner
        {
            NextResult = Result(exitCode: 0, stdout: [WincBootPrompt, ContinuePrompt, SuccessMarker])
        };
        var (updater, _) = CreateUpdater(CreateFastOptions(), runner);

        await RunUpdateAsync(updater, new FakeStreamingDevice("COM7"), CreateFirmwareDirectory());

        var prompt = Assert.Single(runner.PromptResponses);
        Assert.Equal(WincBootPrompt, prompt.Line);
    }

    [Fact]
    public async Task RunUpdate_WhenTheBridgeActivationCallbackThrows_StillFinishesTheFlash()
    {
        var options = CreateFastOptions();
        options.WifiBridgeActivationCallback = () => throw new InvalidOperationException("no bridge here");

        var runner = new FakeExternalProcessRunner
        {
            NextResult = Result(exitCode: 0, stdout: [WincBootPrompt, SuccessMarker])
        };
        var (updater, context) = CreateUpdater(options, runner);

        await RunUpdateAsync(updater, new FakeStreamingDevice("COM7"), CreateFirmwareDirectory());

        // Best-effort by design: the tool may still reach the WINC, and the success marker —
        // not the callback — is the authority on the outcome.
        Assert.Equal(FirmwareUpdateState.Complete, context.CurrentState);
        Assert.Single(runner.PromptResponses);
    }

    // ---------------------------------------------------------------------------------------
    // Progress
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RunUpdate_MapsTheToolsFlashProgressIntoTheProgrammingBand()
    {
        var runner = new FakeExternalProcessRunner
        {
            NextResult = Result(
                exitCode: 0,
                stdout: ["begin write operation", "begin read operation", SuccessMarker])
        };
        var (updater, _) = CreateUpdater(CreateFastOptions(), runner);

        var reports = new List<FirmwareUpdateProgress>();
        await RunUpdateAsync(
            updater,
            new FakeStreamingDevice("COM7"),
            CreateFirmwareDirectory(),
            new ListProgress(reports));

        var programming = reports
            .Where(r => r.State == FirmwareUpdateState.Programming)
            .Select(r => r.PercentComplete)
            .ToList();

        // The tool's own 0-100 is squeezed into 20-90 so the bar keeps room for the prep that
        // came before and the reconnect that follows: write start (5) -> 23.5, read start
        // (60) -> 62.
        Assert.Contains(programming, p => Math.Abs(p - 23.5) < 0.001);
        Assert.Contains(programming, p => Math.Abs(p - 62) < 0.001);
        Assert.All(programming, p => Assert.InRange(p, 20, 90));
    }

    // ---------------------------------------------------------------------------------------
    // CheckStatusAsync against a mutable options object
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CheckStatus_WhenTheMinimumSupportedVersionIsUnparseable_HasNoMinimumOpinion()
    {
        // FirmwareUpdateService's constructor rejects this value, but the options object is
        // settable afterwards — and a read-only probe must degrade to "no opinion" rather than
        // throw out of it.
        var options = CreateFastOptions();
        options.MinimumSupportedWifiFirmwareVersion = "not-a-version";

        var download = new FakeFirmwareDownloadService
        {
            LatestWifiRelease = Release("19.7.7")
        };
        var (updater, _) = CreateUpdater(options, new FakeExternalProcessRunner(), download);

        var device = new FakeLanChipInfoDevice("COM7")
        {
            DefaultChipInfoResponse = () => ChipInfo("19.7.7")
        };

        var status = await updater.CheckStatusAsync(device, CancellationToken.None);

        Assert.Null(status.MinimumSupportedVersion);
        Assert.Null(status.MeetsMinimumSupportedVersion);
        Assert.Equal(WifiFirmwareStatusReason.UpToDate, status.Reason);
    }

    [Fact]
    public async Task CheckStatus_RereadsTheRetryBudgetOnEveryProbe()
    {
        var options = CreateFastOptions();
        options.LanChipInfoMaxAttempts = 1;

        var (updater, _) = CreateUpdater(options, new FakeExternalProcessRunner());
        var device = new FakeLanChipInfoDevice("COM7");

        var first = await updater.CheckStatusAsync(device, CancellationToken.None);
        Assert.Equal(WifiFirmwareStatusReason.ChipInfoUnavailable, first.Reason);
        Assert.Equal(1, device.ChipInfoQueryCount);

        // A host that widens the budget between calls gets the budget it asked for; a cached
        // projection would keep spending the old one.
        options.LanChipInfoMaxAttempts = 3;

        var second = await updater.CheckStatusAsync(device, CancellationToken.None);
        Assert.Equal(WifiFirmwareStatusReason.ChipInfoUnavailable, second.Reason);
        Assert.Equal(4, device.ChipInfoQueryCount);
    }

    [Fact]
    public async Task CheckStatus_WhenThePowerOnCommandThrows_StillProbesAndSkipsTheSettleWait()
    {
        var options = CreateFastOptions();
        options.PowerOnWifiModuleBeforeProbe = true;

        // Long enough that waiting it out is unmistakable if the skip regresses.
        options.PowerOnWifiModuleSettleDelay = TimeSpan.FromSeconds(30);

        var download = new FakeFirmwareDownloadService { LatestWifiRelease = Release("19.7.7") };
        var (updater, _) = CreateUpdater(options, new FakeExternalProcessRunner(), download);

        var device = new FakeLanChipInfoDevice("COM7")
        {
            DefaultChipInfoResponse = () => ChipInfo("19.7.7"),
            OnCommandSent = command =>
            {
                if (command == "SYSTem:POWer:STATe 1")
                {
                    throw new IOException("port went away");
                }
            }
        };

        var stopwatch = Stopwatch.StartNew();
        var status = await updater.CheckStatusAsync(device, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(WifiFirmwareStatusReason.UpToDate, status.Reason);
        Assert.Equal(1, device.ChipInfoQueryCount);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"There is nothing to settle when the power-on send failed, but the probe waited {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task CheckStatus_WhenAlreadyCanceled_NeverPowersOnTheModule()
    {
        var options = CreateFastOptions();
        options.PowerOnWifiModuleBeforeProbe = true;

        var (updater, _) = CreateUpdater(options, new FakeExternalProcessRunner());
        var device = new FakeLanChipInfoDevice("COM7");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => updater.CheckStatusAsync(device, cts.Token));

        Assert.Empty(device.SentCommands);
        Assert.Equal(0, device.ChipInfoQueryCount);
    }

    [Fact]
    public async Task CheckStatus_WhenTheDeviceCannotAnswerLanQueries_StillStatesTheBarItJudgedAgainst()
    {
        var options = CreateFastOptions();
        options.MinimumSupportedWifiFirmwareVersion = "19.7.7";

        var (updater, _) = CreateUpdater(options, new FakeExternalProcessRunner());

        // A plain streaming device is not an ILanChipInfoProvider, so the probe never runs.
        var status = await updater.CheckStatusAsync(new FakeStreamingDevice("COM7"), CancellationToken.None);

        Assert.Equal(WifiFirmwareStatusReason.DeviceDoesNotSupportLanQuery, status.Reason);
        Assert.False(status.IsUpToDate);

        // Reported even here: a caller must always be able to say what bar it was judging
        // against, including on the paths that never got to read the device.
        Assert.Equal(new FirmwareVersion(19, 7, 7, null, 0), status.MinimumSupportedVersion);

        // Three-state on purpose — "could not tell", not "below the minimum".
        Assert.Null(status.MeetsMinimumSupportedVersion);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static (WifiModuleUpdater Updater, FirmwareUpdateContext Context) CreateUpdater(
        FirmwareUpdateServiceOptions options,
        IExternalProcessRunner runner,
        IFirmwareDownloadService? downloadService = null)
    {
        var context = new FirmwareUpdateContext(
            eventSender: new object(),
            NullLogger.Instance,
            options);

        return (
            new WifiModuleUpdater(context, runner, downloadService ?? new FakeFirmwareDownloadService()),
            context);
    }

    /// <summary>
    /// Runs the update with the version check skipped, so the flash mechanics under test are not
    /// also exercising the chip-info probe. The probe has its own tests below.
    /// </summary>
    private static Task RunUpdateAsync(
        WifiModuleUpdater updater,
        IStreamingDevice device,
        string firmwarePath,
        IProgress<FirmwareUpdateProgress>? progress = null)
    {
        return updater.RunUpdateAsync(
            device,
            firmwarePath,
            progress,
            skipVersionCheck: true,
            CancellationToken.None);
    }

    /// <summary>
    /// Runs a flash that is scripted to fail and returns the message chain, so a test can assert
    /// on the verdict the updater produced for that particular failure shape.
    /// </summary>
    private async Task<string> FlashFailureMessageAsync(
        WifiModuleUpdater updater,
        FakeExternalProcessRunner runner)
    {
        var error = await Assert.ThrowsAsync<FirmwareUpdateException>(
            () => RunUpdateAsync(updater, new FakeStreamingDevice("COM7"), CreateFirmwareDirectory()));

        var inner = Assert.IsType<IOException>(error.InnerException);

        // The tool ran; only the success marker was missing. A retry here would mean the failure
        // was misclassified as transient.
        Assert.Equal(1, runner.RunCount);
        Assert.Contains("never reported", inner.Message, StringComparison.Ordinal);

        return inner.Message;
    }

    private static FakeExternalProcessRunner SucceedingRunner() => new()
    {
        NextResult = Result(exitCode: 0, stdout: [SuccessMarker])
    };

    private static ExternalProcessResult Result(
        int exitCode,
        bool timedOut = false,
        string[]? stdout = null,
        string[]? stderr = null)
    {
        return new ExternalProcessResult(
            exitCode,
            timedOut,
            TimeSpan.FromMilliseconds(10),
            stdout ?? [],
            stderr ?? []);
    }

    private static LanChipInfo ChipInfo(string version) => new()
    {
        ChipId = 0x1503,
        FwVersion = version,
        BuildDate = "2026-01-01"
    };

    private static FirmwareReleaseInfo Release(string version)
    {
        Assert.True(FirmwareVersion.TryParse(version, out var parsed));
        return new FirmwareReleaseInfo
        {
            Version = parsed,
            TagName = "v" + version,
            IsPreRelease = false
        };
    }

    private static FirmwareUpdateServiceOptions CreateFastOptions() => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(5),
        PreparingDeviceTimeout = TimeSpan.FromSeconds(10),
        ProgrammingTimeout = TimeSpan.FromSeconds(10),
        VerifyingTimeout = TimeSpan.FromSeconds(10),
        WifiProcessTimeout = TimeSpan.FromSeconds(10),

        // Every lifecycle wait below models something the fakes do not have: a device settling,
        // an OS releasing a COM handle, a WINC finishing bridge init. Collapsed to nothing so
        // these tests measure behavior rather than sleep.
        PostLanFirmwareModeDelay = TimeSpan.Zero,
        PostLanDisconnectPortReleaseDelay = TimeSpan.Zero,
        PostWifiReconnectDelay = TimeSpan.Zero,
        PostUsbTransparentModeExitDelay = TimeSpan.Zero,
        PostReconnectStaleHandleDelay = TimeSpan.Zero,
        WincBootPromptResponseDelay = TimeSpan.Zero,
        PowerOnWifiModuleSettleDelay = TimeSpan.Zero,
        WifiFlashRetryDelay = TimeSpan.FromMilliseconds(1),
        LanChipInfoRetryDelay = TimeSpan.Zero,
        LanChipInfoTotalTimeout = TimeSpan.FromSeconds(10)
    };

    private string CreateFirmwareDirectory(string? namePrefix = null)
    {
        var directory = CreateTempDirectory(namePrefix);
        File.WriteAllText(Path.Combine(directory, "winc_flash_tool.cmd"), "@echo off");
        return directory;
    }

    private string CreateTempDirectory(string? namePrefix = null)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"{namePrefix ?? "daqifi-core-tests"}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);
        return directory;
    }

    private sealed class ListProgress(ICollection<FirmwareUpdateProgress> reports)
        : IProgress<FirmwareUpdateProgress>
    {
        public void Report(FirmwareUpdateProgress value) => reports.Add(value);
    }
}
