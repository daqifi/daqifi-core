using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Firmware.Winc;
using Microsoft.Extensions.Logging;

namespace Daqifi.Core.Firmware;

/// <summary>
/// The WiFi (WINC) module half of <see cref="FirmwareUpdateService"/>: probes the module's current
/// firmware version, puts the device into LAN firmware-update mode, drives the external WINC flash
/// tool (interactive prompt handshake, transient-failure retries, output-based success
/// verification), then reconnects and restores the LAN configuration. Owns the external process
/// runner and the firmware download service; shared state-machine, progress and retry plumbing
/// lives in <see cref="FirmwareUpdateContext"/>. Callers must serialize invocations — the service
/// facade holds the operation lock.
/// </summary>
internal sealed class WifiModuleUpdater
{
    // WINC flash tool prompt markers (stdin handshake).
    private const string WincBootPromptMarker = "Power cycle WINC and set to bootloader mode";
    private const string WincContinuePromptMarker = "Press any key to continue";

    // WINC flash tool failure markers. The "transient" set is recoverable by re-running the
    // tool once the device has settled into bridge mode; the full set forces a failure verdict.
    private const string WifiBridgeIdQueryFailureMarker = "failed to read serial bridge ID query response";
    private const string WifiProgrammerInitFailureMarker = "failed to initialise programming firmware";
    private const string WifiProgrammingFailedMarker = "Programming device failed";
    private const string WifiReadXoFailedMarker = "Reading XO (offset) failed";
    private const string WifiBuildImageFailedMarker = "Building programming image failed";

    private readonly FirmwareUpdateContext _context;
    private readonly IExternalProcessRunner _externalProcessRunner;
    private readonly IFirmwareDownloadService _firmwareDownloadService;

    internal WifiModuleUpdater(
        FirmwareUpdateContext context,
        IExternalProcessRunner externalProcessRunner,
        IFirmwareDownloadService firmwareDownloadService)
    {
        _context = context;
        _externalProcessRunner = externalProcessRunner;
        _firmwareDownloadService = firmwareDownloadService;
    }

    private ILogger Logger => _context.Logger;

    private FirmwareUpdateServiceOptions Options => _context.Options;

    internal async Task RunUpdateAsync(
        IStreamingDevice device,
        string firmwarePath,
        IProgress<FirmwareUpdateProgress>? progress,
        bool skipVersionCheck,
        CancellationToken cancellationToken)
    {
        const long totalBytes = 100;

        // Read only by the failure paths at the bottom of this method. Once the update-mode command
        // is on the wire the device may be sitting in LAN firmware-update / USB-transparent bridge
        // mode, where the SCPI console is bypassed and the module stays unusable until something
        // takes it back out — a power cycle, or the bridge-exit below. Today only the *successful*
        // path restores it, so a failed or canceled flash strands the device; that is exactly why
        // daqifi-desktop still wraps this call in its own recovery finally (part of #269).
        // Armed inside the prepare step, at the one point where "may be bridged" becomes true.
        var mayBeInLanUpdateMode = false;

        try
        {
            if (!skipVersionCheck
                && await IsWifiFirmwareUpToDateAsync(device, progress, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            _context.TransitionToState(FirmwareUpdateState.PreparingDevice, "Preparing device for WiFi module update.");
            _context.ReportProgress(progress, FirmwareUpdateState.PreparingDevice, 0, _context.CurrentOperation, 0, totalBytes);

            await _context.ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.PreparingDevice,
                "prepare device for WiFi update mode",
                async stateToken =>
                {
                    FirmwareUpdateContext.EnsureDeviceConnected(device);

                    if (device.IsStreaming)
                    {
                        device.StopStreaming();
                    }

                    device.Send(ScpiMessageProducer.SetLanFirmwareUpdateMode);

                    // Armed here rather than before the whole prepare step. Everything above this
                    // line fails with the update-mode command definitively un-sent, and arming for
                    // those turns an immediate "device must be connected" failure into a full
                    // ReconnectingAfterFlash wait for a transport that was never gone. Immediately
                    // *after* the Send is as early as it can honestly be: Send is synchronous and
                    // no await separates it from this line, so nothing can interleave between them.
                    // From here on Core must assume the mode took — a cancel or state timeout can
                    // land while the device is still acting on a command it already received. A
                    // redundant bridge-exit on a device that never entered update mode is a no-op;
                    // a skipped one leaves a bridged device needing a power cycle.
                    mayBeInLanUpdateMode = true;

                    await Task.Delay(Options.PostLanFirmwareModeDelay, stateToken).ConfigureAwait(false);
                    device.Disconnect();

                    // The OS does not free the USB-CDC COM handle the instant Disconnect returns.
                    // Wait so the external WINC flash tool can open the port; without this the tool
                    // fails to open it and exits in ~1s producing no programming output (caught by
                    // the output-based success verification below as a failure).
                    if (Options.PostLanDisconnectPortReleaseDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(Options.PostLanDisconnectPortReleaseDelay, stateToken).ConfigureAwait(false);
                    }
                },
                cancellationToken).ConfigureAwait(false);

            _context.TransitionToState(FirmwareUpdateState.Programming, "Running WiFi module flash tool.");
            _context.ReportProgress(progress, FirmwareUpdateState.Programming, 20, _context.CurrentOperation, 0, totalBytes);

            // Build a fresh request per attempt: the stdin prompt responder carries one-shot
            // state (it answers the WINC prompt exactly once), so reusing a request across a
            // retry would leave the responder already "spent". The factory takes the per-attempt
            // linked token so the prompt-delay wait stays cancellable.
            var processResult = await RunWifiFlashToolWithRetryAsync(
                ct => BuildWifiProcessRequest(device, firmwarePath, progress, ct),
                cancellationToken).ConfigureAwait(false);

            if (processResult.TimedOut)
            {
                throw new TimeoutException(
                    $"WiFi flashing process timed out after {Options.WifiProcessTimeout.TotalSeconds:F0} seconds " +
                    $"(exit code {processResult.ExitCode}). " +
                    BuildProcessLogExcerpt(processResult));
            }

            // Verify success from the tool's OWN output, not from its exit code or run duration.
            // A genuine flash ends with "verify passed" then the success marker; when the tool
            // cannot reach the WINC — most often because the device never released the serial port,
            // so the tool couldn't open it and bailed in ~1s — it produces none of these. The exit
            // code is unreliable in both directions (some WINC script/tool combinations emit failure
            // markers yet still exit 0), so the success marker is the authority.
            if (!ContainsAny(processResult.StandardOutputLines, Options.WifiFlashSuccessMarker))
            {
                throw new IOException(
                    $"WiFi flashing did not complete successfully — the flash tool never reported " +
                    $"'{Options.WifiFlashSuccessMarker}'. {DescribeWifiFlashFailure(processResult)} " +
                    BuildProcessLogExcerpt(processResult));
            }

            // Everything past this point runs on an already-flashed, already-verified WINC image,
            // so it gets its own state rather than sharing Verifying with the PIC32 CRC check —
            // a reconnect timeout here is environmental, not a bad flash (#398 gap 4).
            _context.TransitionToState(
                FirmwareUpdateState.ReconnectingAfterFlash,
                "Reconnecting device and restoring LAN configuration.");
            _context.ReportProgress(progress, FirmwareUpdateState.ReconnectingAfterFlash, 92, _context.CurrentOperation, 92, totalBytes);

            await _context.ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.ReconnectingAfterFlash,
                "reconnect serial transport after WiFi flash",
                async stateToken =>
                {
                    await Task.Delay(Options.PostWifiReconnectDelay, stateToken).ConfigureAwait(false);
                    await _context.WaitForSerialReconnectAsync(device, stateToken).ConfigureAwait(false);
                    device.Send(ScpiMessageProducer.EnableNetworkLan);
                    device.Send(ScpiMessageProducer.ApplyNetworkLan);
                    device.Send(ScpiMessageProducer.SaveNetworkLan);
                },
                cancellationToken).ConfigureAwait(false);

            _context.TransitionToState(FirmwareUpdateState.Complete, "WiFi module update completed.");
            _context.ReportProgress(progress, FirmwareUpdateState.Complete, 100, _context.CurrentOperation, totalBytes, totalBytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Before reporting the outcome: a canceled WiFi flash is the single most likely way to
            // leave the module bridged, so the exit runs here too — on its own token, since the
            // caller's is already canceled by definition on this path.
            await TryLeaveLanUpdateModeAfterFailureAsync(device, mayBeInLanUpdateMode).ConfigureAwait(false);

            _context.TransitionToState(FirmwareUpdateState.Failed, "WiFi module update canceled.");
            _context.ReportProgress(progress, FirmwareUpdateState.Failed, _context.LastReportedPercent, _context.CurrentOperation, 0, totalBytes);
            Logger.LogWarning("WiFi module update canceled.");
            throw;
        }
        catch (Exception ex)
        {
            var failedState = _context.CurrentState;
            var failedOperation = _context.CurrentOperation;

            // Captured above first, so the reported failure names the step that actually failed
            // rather than the recovery attempt that follows it.
            await TryLeaveLanUpdateModeAfterFailureAsync(device, mayBeInLanUpdateMode).ConfigureAwait(false);

            _context.TransitionToState(FirmwareUpdateState.Failed, failedOperation);
            _context.ReportProgress(progress, FirmwareUpdateState.Failed, _context.LastReportedPercent, failedOperation, 0, totalBytes);
            Logger.LogError(ex, "WiFi module update failed in state {State}.", failedState);

            throw _context.CreateFirmwareUpdateException(failedState, failedOperation, ex);
        }
    }

    internal async Task<WifiFirmwareStatus> CheckStatusAsync(
        IStreamingDevice device,
        CancellationToken cancellationToken)
    {
        // Resolved up front so every return path below can state the bar it judged
        // against, including the ones that never get to read the device. Parsed
        // defensively rather than trusting Validate(): the options object stays mutable
        // after the service is constructed, and an unparseable minimum must degrade to
        // "no minimum opinion" (null) instead of throwing out of a read-only probe.
        FirmwareVersion? minimumSupported =
            FirmwareVersion.TryParse(Options.MinimumSupportedWifiFirmwareVersion, out var parsedMinimum)
                ? parsedMinimum
                : null;

        if (device is not ILanChipInfoProvider lanChipInfoProvider)
        {
            return new WifiFirmwareStatus
            {
                IsUpToDate = false,
                Reason = WifiFirmwareStatusReason.DeviceDoesNotSupportLanQuery,
                MinimumSupportedVersion = minimumSupported,
            };
        }

        // Closes #301: right after a PIC32 reflash the WINC module comes back
        // powered off, so the first GETChipInfo? probe below would always fail,
        // report ChipInfoUnavailable, and send the caller into a needless
        // multi-minute WiFi reflash. Powering it on first (mirroring what
        // daqifi-desktop's FirmwareUpdateCoordinator does today) closes that gap.
        // Skipped when the device isn't connected — Send would throw, and a
        // disconnected device will fail the chip-info probe regardless.
        if (Options.PowerOnWifiModuleBeforeProbe && device.IsConnected)
        {
            // Observe cancellation before this state-changing Send: a
            // pre-cancelled call must not power on the device before the
            // cancellation is surfaced to the caller.
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                device.Send(ScpiMessageProducer.TurnDeviceOn);
                if (Options.PowerOnWifiModuleSettleDelay > TimeSpan.Zero)
                {
                    await Task.Delay(Options.PowerOnWifiModuleSettleDelay, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort: the chip-info probe below has its own bounded
                // retry and gracefully degrades to ChipInfoUnavailable, so a
                // failure to send the power-on command must not abort the
                // whole status check. Skip the settle delay too — there is
                // nothing to settle if the send itself failed.
                Logger.LogDebug(ex, "Failed to send WINC power-on command before chip-info probe; continuing without it.");
            }
        }

        // Bounded retry for the LAN chip-info probe (closes #144). Right
        // after a PIC32 firmware update the application is up while WiFi
        // is still finishing startup, so the first chip-info query can
        // transiently fail; without retry, the WiFi version decision
        // would short-circuit to ChipInfoUnavailable and flow on to a
        // multi-minute reflash of already-current WiFi firmware. The
        // retry budget is bounded (LanChipInfoMaxAttempts × RetryDelay)
        // and observes cancellation between attempts.
        var (chipInfo, wasLanNotInitialized) = await TryGetLanChipInfoWithRetryAsync(
            device, lanChipInfoProvider, cancellationToken).ConfigureAwait(false);
        if (chipInfo == null)
        {
            return new WifiFirmwareStatus
            {
                IsUpToDate = false,
                Reason = wasLanNotInitialized
                    ? WifiFirmwareStatusReason.LanNotInitialized
                    : WifiFirmwareStatusReason.ChipInfoUnavailable,
                MinimumSupportedVersion = minimumSupported,
            };
        }

        // Parsed once here, before the release lookup, because the minimum-supported
        // answer must survive that lookup failing — that is the whole point of it.
        var deviceVersionParsed = FirmwareVersion.TryParse(chipInfo.FwVersion, out var deviceVersion);
        bool? meetsMinimum = deviceVersionParsed && minimumSupported is { } minimum
            ? deviceVersion >= minimum
            : null;

        FirmwareReleaseInfo? latestWifi;
        try
        {
            latestWifi = await _firmwareDownloadService
                .GetLatestWifiReleaseAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogDebug(ex, "Failed to query latest WiFi firmware release; reporting status as LatestReleaseUnavailable.");
            return new WifiFirmwareStatus
            {
                CurrentChipInfo = chipInfo,
                IsUpToDate = false,
                Reason = WifiFirmwareStatusReason.LatestReleaseUnavailable,
                MinimumSupportedVersion = minimumSupported,
                MeetsMinimumSupportedVersion = meetsMinimum,
            };
        }

        if (latestWifi == null)
        {
            return new WifiFirmwareStatus
            {
                CurrentChipInfo = chipInfo,
                IsUpToDate = false,
                Reason = WifiFirmwareStatusReason.LatestReleaseUnavailable,
                MinimumSupportedVersion = minimumSupported,
                MeetsMinimumSupportedVersion = meetsMinimum,
            };
        }

        // Only the device-reported version needs parsing; latestWifi.Version
        // is already a strongly-typed FirmwareVersion from FirmwareDownloadService.
        // Re-parsing TagName would risk divergence from the canonical Version
        // (different tag prefix conventions, etc.) and cost an extra parse.
        if (!deviceVersionParsed)
        {
            return new WifiFirmwareStatus
            {
                CurrentChipInfo = chipInfo,
                LatestRelease = latestWifi,
                IsUpToDate = false,
                Reason = WifiFirmwareStatusReason.VersionUnparseable,
                MinimumSupportedVersion = minimumSupported,
                MeetsMinimumSupportedVersion = meetsMinimum,
            };
        }

        var isCurrent = deviceVersion >= latestWifi.Version;
        return new WifiFirmwareStatus
        {
            CurrentChipInfo = chipInfo,
            LatestRelease = latestWifi,
            IsUpToDate = isCurrent,
            Reason = isCurrent ? WifiFirmwareStatusReason.UpToDate : WifiFirmwareStatusReason.UpdateAvailable,
            MinimumSupportedVersion = minimumSupported,
            MeetsMinimumSupportedVersion = meetsMinimum,
        };
    }

    private async Task<bool> IsWifiFirmwareUpToDateAsync(
        IStreamingDevice device,
        IProgress<FirmwareUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Internal callsite: in addition to deciding the boolean, we must
        // transition to Complete + report 100% progress so the caller's
        // single UpdateWifiModuleAsync(...) call observes the same end-state
        // as a successful flash. CheckWifiFirmwareStatusAsync (the public
        // planning API) does not have that side effect — its callers own
        // their own logging / UI transitions.
        var status = await CheckStatusAsync(device, cancellationToken).ConfigureAwait(false);

        switch (status.Reason)
        {
            case WifiFirmwareStatusReason.UpdateAvailable:
                Logger.LogInformation(
                    "WiFi firmware update available: device has {DeviceVersion}, latest is {LatestVersion}.",
                    status.CurrentChipInfo!.FwVersion,
                    status.LatestRelease!.TagName);
                return false;

            case WifiFirmwareStatusReason.UpToDate:
                var message = $"WiFi firmware is already up to date (device: {status.CurrentChipInfo!.FwVersion}, latest: {status.LatestRelease!.TagName}).";
                Logger.LogInformation(message);
                _context.TransitionToState(FirmwareUpdateState.Complete, message);
                _context.ReportProgress(progress, FirmwareUpdateState.Complete, 100, message, 100, 100);
                return true;

            default:
                // DeviceDoesNotSupportLanQuery, ChipInfoUnavailable,
                // LanNotInitialized, LatestReleaseUnavailable,
                // VersionUnparseable — no latest-release verdict is available.
                //
                // "Conservative" here used to mean "flash", but a WINC reflash is a
                // multi-minute, destructive operation, so flashing on a *network*
                // failure is the expensive guess, not the safe one. When the device
                // itself answered and its reported version meets the minimum Core
                // supports, that is a real verdict reached without the network, so
                // honor it and skip the flash. Only LatestReleaseUnavailable can
                // reach here with a non-null answer: every other default reason
                // means the device version is unknown or unparseable, which leaves
                // MeetsMinimumSupportedVersion null and still falls through to the
                // flash. A caller that wants to reflash regardless already has
                // skipVersionCheck: true.
                if (status.MeetsMinimumSupportedVersion == true)
                {
                    var minimumMessage =
                        $"WiFi firmware meets the minimum supported version (device: {status.CurrentChipInfo!.FwVersion}, "
                        + $"minimum: {status.MinimumSupportedVersion}); latest-release lookup was unavailable ({status.Reason}), "
                        + "so skipping the flash rather than reflashing a supported module.";
                    Logger.LogInformation(minimumMessage);
                    _context.TransitionToState(FirmwareUpdateState.Complete, minimumMessage);
                    _context.ReportProgress(progress, FirmwareUpdateState.Complete, 100, minimumMessage, 100, 100);
                    return true;
                }

                return false;
        }
    }

    private async Task<(LanChipInfo? ChipInfo, bool WasLanNotInitialized)> TryGetLanChipInfoWithRetryAsync(
        IStreamingDevice device,
        ILanChipInfoProvider lanChipInfoProvider,
        CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, Options.LanChipInfoMaxAttempts);
        var retryDelay = Options.LanChipInfoRetryDelay;
        var totalTimeout = Options.LanChipInfoTotalTimeout;

        // Tracks the most recent failure's classification (reset on any
        // non-LanNotInitialized outcome) so the caller can report the
        // specific WifiFirmwareStatusReason.LanNotInitialized only when
        // that was genuinely the terminal condition, not stale from an
        // earlier attempt. Sent at most once per probe (closes #203) —
        // repeatedly kicking APPLY would tear down and re-init the WINC
        // on every failed attempt, risking disruption of an already-
        // associated WiFi link for no additional benefit.
        var lastFailureWasLanNotInitialized = false;
        var hasSentLanApply = false;

        // Wall-clock budget guards against the pathological case where
        // attempt-count × per-attempt-timeout + retry-delay sum vastly
        // exceeds the configured retry budget (e.g., 3 × 2s device timeout
        // + 2 × 2s delay = ~10s while the operation lock is held). Linking
        // the caller's CT preserves cancellation semantics; the timeout
        // CTS just adds a deadline.
        using var timeoutCts = new CancellationTokenSource(totalTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                linkedToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Logger.LogDebug(
                    "LAN chip-info probe hit total timeout ({Timeout}) before attempt {Attempt}/{Max}.",
                    totalTimeout,
                    attempt,
                    maxAttempts);
                return (null, lastFailureWasLanNotInitialized);
            }

            try
            {
                var chipInfo = await lanChipInfoProvider.GetLanChipInfoAsync(linkedToken).ConfigureAwait(false);
                if (chipInfo != null)
                {
                    if (attempt > 1)
                    {
                        Logger.LogDebug(
                            "LAN chip-info query succeeded on attempt {Attempt}/{Max}.",
                            attempt,
                            maxAttempts);
                    }
                    return (chipInfo, false);
                }
                lastFailureWasLanNotInitialized = false;
                Logger.LogDebug(
                    "LAN chip-info query returned null on attempt {Attempt}/{Max}.",
                    attempt,
                    maxAttempts);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Logger.LogDebug(
                    "LAN chip-info probe hit total timeout ({Timeout}) during attempt {Attempt}/{Max}.",
                    totalTimeout,
                    attempt,
                    maxAttempts);
                return (null, lastFailureWasLanNotInitialized);
            }
            catch (LanNotInitializedException ex)
            {
                lastFailureWasLanNotInitialized = true;
                Logger.LogDebug(
                    ex,
                    "LAN chip-info query on attempt {Attempt}/{Max} reported the WINC state machine is not initialized.",
                    attempt,
                    maxAttempts);

                if (Options.KickLanApplyOnNotInitialized && !hasSentLanApply && device.IsConnected)
                {
                    // Observe cancellation before this state-changing Send, mirroring
                    // the WINC power-on guard above: a cancelled probe must not still
                    // kick APPLY on the device. Uses the caller's token (not the
                    // linked timeout token) so a total-timeout expiry alone doesn't
                    // suppress a kick the caller never actually asked to cancel.
                    cancellationToken.ThrowIfCancellationRequested();

                    hasSentLanApply = true;
                    try
                    {
                        device.Send(ScpiMessageProducer.ApplyNetworkLan);
                        Logger.LogDebug("Sent LAN:APPLY to initialize the WINC state machine after a not-initialized chip-info response.");
                    }
                    catch (Exception sendEx) when (sendEx is not OperationCanceledException)
                    {
                        // Best-effort: falling through to the normal retry delay/loop
                        // below still gives the device a chance to recover on its own.
                        Logger.LogDebug(sendEx, "Failed to send LAN:APPLY after a not-initialized chip-info response; continuing retry loop without it.");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastFailureWasLanNotInitialized = false;
                Logger.LogDebug(
                    ex,
                    "LAN chip-info query failed on attempt {Attempt}/{Max}.",
                    attempt,
                    maxAttempts);
            }

            if (attempt < maxAttempts)
            {
                try
                {
                    await Task.Delay(retryDelay, linkedToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    Logger.LogDebug(
                        "LAN chip-info probe hit total timeout ({Timeout}) during retry delay after attempt {Attempt}/{Max}.",
                        totalTimeout,
                        attempt,
                        maxAttempts);
                    return (null, lastFailureWasLanNotInitialized);
                }
            }
        }

        Logger.LogDebug(
            "LAN chip-info query exhausted {Max} attempts; reporting status as {Reason}.",
            maxAttempts,
            lastFailureWasLanNotInitialized ? WifiFirmwareStatusReason.LanNotInitialized : WifiFirmwareStatusReason.ChipInfoUnavailable);
        return (null, lastFailureWasLanNotInitialized);
    }

    private ExternalProcessRequest BuildWifiProcessRequest(
        IStreamingDevice device,
        string firmwarePath,
        IProgress<FirmwareUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var toolPath = ResolveWifiToolPath(firmwarePath);
        var port = ResolveWifiPort(device);

        var toolArguments = Options.WifiFlashToolArgumentsTemplate
            .Replace("{port}", QuoteArgument(port), StringComparison.Ordinal)
            .Replace("{firmwarePath}", QuoteArgument(firmwarePath), StringComparison.Ordinal);

        var executablePath = toolPath;
        var executableArguments = toolArguments;

        var extension = Path.GetExtension(toolPath);
        if ((extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)) &&
            OperatingSystem.IsWindows())
        {
            executablePath = "cmd.exe";
            executableArguments = $"/c \"{toolPath}\" {toolArguments}";
        }

        // Tracks the live device-flash phase (write/read/verify) from the tool's block-address
        // output so the bar advances across the multi-minute flash, instead of latching to the
        // image-build phase's "100%" lines and freezing. See WifiFlashProgressParser.
        var progressParser = new WifiFlashProgressParser();
        var progressLock = new object();

        return new ExternalProcessRequest
        {
            FileName = executablePath,
            Arguments = executableArguments,
            WorkingDirectory = Path.GetDirectoryName(toolPath),
            Timeout = Options.WifiProcessTimeout,
            OnStandardOutputLine = line =>
            {
                Logger.LogInformation("WiFi flash output: {Line}", line);

                double processPercent;
                lock (progressLock)
                {
                    var updated = progressParser.Observe(line);
                    if (!updated.HasValue)
                    {
                        return;
                    }

                    processPercent = updated.Value;
                }

                // Map the 0-100 device-flash percent into the Programming state's 20-90 overall band.
                var overallPercent = 20 + (processPercent * 0.70);
                _context.ReportProgress(
                    progress,
                    FirmwareUpdateState.Programming,
                    overallPercent,
                    line,
                    (long)Math.Round(processPercent),
                    100);
            },
            OnStandardErrorLine = line => Logger.LogWarning("WiFi flash stderr: {Line}", line),
            StandardInputResponseFactory = BuildWifiPromptResponder(cancellationToken)
        };
    }

    /// <summary>
    /// Builds the stdin responder for the WINC flash tool's interactive prompts. At the
    /// "Power cycle WINC" prompt it fires the optional bridge-activation callback, waits
    /// <see cref="FirmwareUpdateServiceOptions.WincBootPromptResponseDelay"/> so the firmware can
    /// finish bridge-mode init, then sends the empty continue line. The returned delegate carries
    /// one-shot state, so a fresh responder must be built for each flash attempt.
    /// </summary>
    /// <param name="cancellationToken">
    /// The flash run's linked token (state timeout + caller cancellation). The prompt-response wait
    /// observes it so a timeout or cancel unblocks the output-pump thread promptly instead of
    /// sleeping out the full delay after the process has been killed.
    /// </param>
    private Func<string, string?> BuildWifiPromptResponder(CancellationToken cancellationToken)
    {
        var continueSignalSent = false;

        return line =>
        {
            if (line.Contains(WincBootPromptMarker, StringComparison.OrdinalIgnoreCase))
            {
                if (continueSignalSent)
                {
                    return null;
                }

                if (Options.WifiBridgeActivationCallback is { } activate)
                {
                    Logger.LogInformation("Activating WiFi bridge mode before WINC programming.");
                    try
                    {
                        activate();
                        Logger.LogInformation("Bridge activation callback completed; waiting for firmware bridge init.");
                    }
                    catch (Exception ex)
                    {
                        // The bridge activation is best-effort — a failure here must not abort the
                        // flash; the tool may still reach the WINC and the success verification is
                        // the source of truth for the outcome.
                        Logger.LogWarning(ex, "WiFi bridge activation callback threw; continuing with the flash.");
                    }
                }
                else
                {
                    Logger.LogInformation("WiFi flash tool requested WINC power-cycle; waiting before sending continue signal.");
                }

                if (Options.WincBootPromptResponseDelay > TimeSpan.Zero)
                {
                    // The responder runs inline on the process output-pump thread and the tool
                    // blocks on stdin until we return, so the wait must be synchronous (a fire-and-
                    // forget Task.Delay would not pause it). Block on a cancellable delay so a run
                    // timeout / cancel unblocks the pump immediately; if canceled, skip the continue
                    // signal — the process is being torn down anyway.
                    try
                    {
                        Task.Delay(Options.WincBootPromptResponseDelay, cancellationToken)
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.LogDebug("WINC prompt-response wait canceled; skipping the continue signal.");
                        return null;
                    }
                }

                continueSignalSent = true;
                Logger.LogInformation("Sending continue signal to WiFi flash tool.");
                return string.Empty;
            }

            if (!continueSignalSent &&
                line.Contains(WincContinuePromptMarker, StringComparison.OrdinalIgnoreCase))
            {
                continueSignalSent = true;
                Logger.LogInformation("Sending continue signal to WiFi flash tool.");
                return string.Empty;
            }

            return null;
        };
    }

    private async Task<ExternalProcessResult> RunWifiFlashToolWithRetryAsync(
        Func<CancellationToken, ExternalProcessRequest> requestFactory,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, Options.WifiFlashAttempts);
        ExternalProcessResult result = null!;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            // Build the request inside the state-timeout lambda so the responder closes over the
            // linked token (state timeout + caller cancellation) and its prompt-delay wait unblocks
            // when the run is canceled or times out.
            result = await _context.ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.Programming,
                "execute WiFi flash process",
                ct => _externalProcessRunner.RunAsync(requestFactory(ct), ct),
                cancellationToken).ConfigureAwait(false);

            // A timeout or a verified success ends the loop; so does a non-transient failure,
            // since re-running the tool only helps when the device hadn't yet settled into bridge
            // mode. Only a transient bridge-init failure with attempts remaining triggers a retry.
            if (result.TimedOut ||
                ContainsAny(result.StandardOutputLines, Options.WifiFlashSuccessMarker) ||
                attempt >= attempts ||
                !IsTransientWifiFlashFailure(result))
            {
                return result;
            }

            Logger.LogWarning(
                "WiFi flash tool reported a transient bridge-init failure on attempt {Attempt}/{Attempts}; retrying in {DelayMs} ms.",
                attempt,
                attempts,
                Options.WifiFlashRetryDelay.TotalMilliseconds);
            await Task.Delay(Options.WifiFlashRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// True when the result shows a transient bridge-init failure — the device hadn't finished
    /// entering bridge mode when the tool issued its first query. Re-running the tool once the
    /// device has settled typically succeeds, so these (and only these) are retried.
    /// </summary>
    private static bool IsTransientWifiFlashFailure(ExternalProcessResult result)
    {
        // Retry ONLY on the bridge-init markers — the device hadn't finished entering bridge mode
        // when the tool issued its first query, which a re-run fixes. These markers co-occur with
        // the generic "Programming device failed" / "Reading XO failed" lines in the real failure
        // output, so keying on them alone still catches the transient case without retrying a
        // genuine (non-recoverable) programming failure — which would only delay the real error and
        // needlessly re-fire the bridge-activation callback. Scan both streams since tool/script
        // versions route these lines inconsistently.
        return ContainsAny(result.StandardErrorLines, WifiBridgeIdQueryFailureMarker, WifiProgrammerInitFailureMarker)
            || ContainsAny(result.StandardOutputLines, WifiBridgeIdQueryFailureMarker, WifiProgrammerInitFailureMarker);
    }

    /// <summary>
    /// Produces a short human-readable reason for a flash that did not report the success marker,
    /// distinguishing "the tool never opened the port" from a device-reported programming failure.
    /// </summary>
    private static string DescribeWifiFlashFailure(ExternalProcessResult result)
    {
        // A "Building programming image failed" is a LOCAL image-build failure that happens before
        // any on-device flashing, so it must not be reported as a device-reachability failure.
        if (ContainsAny(result.StandardErrorLines, WifiBuildImageFailedMarker) ||
            ContainsAny(result.StandardOutputLines, WifiBuildImageFailedMarker))
        {
            return "The flash tool failed to build the programming image (before contacting the device).";
        }

        // Markers that imply the tool actually reached the device. Scan both streams — tool/script
        // versions route these to stdout vs stderr inconsistently.
        if (ContainsAny(
                result.StandardErrorLines,
                WifiBridgeIdQueryFailureMarker,
                WifiProgrammerInitFailureMarker,
                WifiProgrammingFailedMarker,
                WifiReadXoFailedMarker) ||
            ContainsAny(
                result.StandardOutputLines,
                WifiBridgeIdQueryFailureMarker,
                WifiProgrammerInitFailureMarker,
                WifiProgrammingFailedMarker,
                WifiReadXoFailedMarker))
        {
            return "The flash tool reached the device but reported a programming failure.";
        }

        // "No output" must consider BOTH streams — some failure modes (tool/port errors) print
        // only to stderr, so checking stdout alone would mislabel them as "no output".
        if (result.StandardOutputLines.Count == 0 && result.StandardErrorLines.Count == 0)
        {
            return "The flash tool produced no output — it likely could not open the serial port " +
                   "(the device may not have released it).";
        }

        if (result.StandardOutputLines.Count == 0 && result.StandardErrorLines.Count > 0)
        {
            return $"The flash tool wrote only to stderr and never programmed the device (exit code {result.ExitCode}).";
        }

        return $"The flash tool exited with code {result.ExitCode} without completing the program.";
    }

    private static bool ContainsAny(IReadOnlyList<string> lines, params string[] markers)
    {
        foreach (var line in lines)
        {
            foreach (var marker in markers)
            {
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private string ResolveWifiToolPath(string firmwarePath)
    {
        if (!File.Exists(firmwarePath) && !Directory.Exists(firmwarePath))
        {
            throw new FileNotFoundException("WiFi firmware path was not found.", firmwarePath);
        }

        // Resolution goes through the shared locator so there is a single answer to
        // "can this environment flash the WiFi module?" (part of #271).
        var locator = new WincFlashToolLocator(Options.WifiFlashToolFileName);
        if (locator.TryResolveToolPath(firmwarePath, out var toolPath))
        {
            return toolPath;
        }

        // Say why plainly. Microchip's flash tool is a Windows .cmd/.exe, so on Linux and macOS
        // this is not a misconfigured path — the tool genuinely cannot be there, and the caller
        // needs to know that rather than reading it as a missing download.
        var platformNote = OperatingSystem.IsWindows()
            ? string.Empty
            : $" On {(OperatingSystem.IsMacOS() ? "macOS" : "this platform")} the WiFi flash tool is " +
              "unavailable — Microchip ships it as a Windows program. WiFi module flashing is " +
              "currently Windows-only; see issue #271.";

        throw new FileNotFoundException(
            $"Could not locate '{Options.WifiFlashToolFileName}' under '{firmwarePath}'.{platformNote}");
    }

    private string ResolveWifiPort(IStreamingDevice device)
    {
        if (!string.IsNullOrWhiteSpace(Options.WifiPortOverride))
        {
            return Options.WifiPortOverride;
        }

        if (!string.IsNullOrWhiteSpace(device.Name))
        {
            return device.Name;
        }

        throw new InvalidOperationException("Unable to resolve a serial port name for WiFi update.");
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var escaped = value.Replace("\"", "\\\"", StringComparison.Ordinal);
        return escaped.IndexOfAny([' ', '\t']) >= 0
            ? $"\"{escaped}\""
            : escaped;
    }

    private static string BuildProcessLogExcerpt(ExternalProcessResult result)
    {
        var excerpt = result.StandardErrorLines
            .Concat(result.StandardOutputLines)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(5)
            .ToArray();

        if (excerpt.Length == 0)
        {
            return "No process output captured.";
        }

        return $"Process output excerpt: {string.Join(" | ", excerpt)}";
    }

    /// <summary>
    /// Best-effort attempt to take the device back out of LAN firmware-update / USB-transparent
    /// bridge mode after a WiFi update failed or was canceled. Never throws: the original failure
    /// is what the caller needs to see, and a recovery that cannot reach the device must not
    /// replace it.
    /// </summary>
    /// <remarks>
    /// This is the managed-connection twin of <see cref="WifiBridgeActivator.Deactivate"/>, and
    /// sends the same two commands in the same order with the same pause between them:
    /// <c>SYSTem:USB:SetTransparentMode 0</c> hands the port back to the SCPI console, then
    /// <c>LAN:APPLY</c> kicks the WiFi manager out of its bridge-mode state machine.
    /// <para>
    /// Deliberately NOT the success path's full <c>LAN:ENAbled</c>/<c>APPLY</c>/<c>SAVE</c>
    /// restore: that persists a configuration, which has no business running off the back of a
    /// flash that did not complete. The job here is only to make the device answerable again.
    /// </para>
    /// </remarks>
    private async Task TryLeaveLanUpdateModeAfterFailureAsync(IStreamingDevice device, bool mayBeInLanUpdateMode)
    {
        if (!mayBeInLanUpdateMode)
        {
            return;
        }

        try
        {
            // A fresh token, never the caller's: on the cancellation path the caller's token is
            // already canceled, and that is precisely the case where the device most needs the
            // exit. It bounds the reconnect wait and nothing else, because waiting for a serial
            // transport to come back is the only step here that can take unbounded time. The
            // post-flash reconnect budget is the natural size for it — the same physical operation,
            // already tunable by a host that knows its re-enumeration is slow. The worst case (the
            // device never returns) costs that budget once, which is the right price for not
            // stranding a bridged module.
            using var restoreCts = new CancellationTokenSource(
                Options.GetStateTimeout(FirmwareUpdateState.ReconnectingAfterFlash));

            // Prep disconnects the device, so on almost every failure path the transport has to
            // come back before anything can be sent. Returns immediately when still connected.
            await _context.WaitForSerialReconnectAsync(device, restoreCts.Token).ConfigureAwait(false);

            // Past the reconnect the two-command exit runs to completion instead of re-observing
            // the budget. Half of it is the one outcome worse than not starting: the console is
            // handed back but the WiFi manager is left in its bridge-mode state machine, so the
            // device looks answerable while its module still is not. The un-cancelled tail is a
            // fixed pause plus two synchronous writes, so the helper stays bounded either way.
            device.Send(ScpiMessageProducer.SetUsbTransparencyMode(0));

            // Leaving the bridge is a device-side mode transition, not an instantaneous one:
            // until the SCPI console path is back, bytes on the port are still forwarded raw to
            // the WINC, so a command sent immediately after can be swallowed by the bridge.
            await Task.Delay(WifiBridgeActivator.InterCommandDelay).ConfigureAwait(false);

            device.Send(ScpiMessageProducer.ApplyNetworkLan);

            Logger.LogInformation(
                "Sent the WiFi bridge-exit sequence after a failed WiFi module update.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Could not take the device out of WiFi update mode after a failed update; it may still be in " +
                "USB-transparent bridge mode and need a power cycle.");
        }
    }
}
