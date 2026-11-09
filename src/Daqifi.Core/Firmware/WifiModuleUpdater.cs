using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
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

    /// <summary>
    /// How far the prepare step took the device into WiFi firmware-update mode. The firmware has
    /// two states here and <c>LAN:APPLY</c> means the opposite thing in each, so recovery has to
    /// know which one it is talking to (#853).
    /// </summary>
    private enum LanUpdateModeProgress
    {
        /// <summary>
        /// Nothing was sent. The device is in its normal mode and there is nothing to recover.
        /// </summary>
        NotRequested,

        /// <summary>
        /// <c>LAN:FWUpdate</c> is on the wire but the <c>LAN:APPLY</c> that acts on it is not.
        /// The firmware holds a request flag, keeps answering its console, and has no bridge. An
        /// apply sent from here ENTERS the bridge — and the firmware clears the request flag only
        /// on the way back OUT of the bridge, so recovery from here cannot clear it with a command
        /// and reboots the device instead rather than leave the flag armed for whatever applies
        /// next.
        /// </summary>
        UpdateRequested,

        /// <summary>
        /// The <c>LAN:APPLY</c> that enters the bridge is on the wire, but the time budgeted for
        /// the device to act on it has not run out yet. The apply only QUEUES the transition: the
        /// firmware's WiFi manager consumes the request flag on its next REINIT, so from here the
        /// device may still be holding that flag. The firmware also holds an apply-in-progress
        /// gate from that apply until the bridge is up, so a second apply sent from here is most
        /// likely rejected outright (-200) and does nothing at all — and if the gate's safety
        /// release lets one through, the pending request makes it ENTER rather than leave.
        /// Recovery therefore has to let the entry window run out first — see
        /// <c>TryLeaveLanUpdateModeAfterFailureAsync</c>.
        /// </summary>
        BridgeEntryPending,

        /// <summary>
        /// The <c>LAN:APPLY</c> that enters the bridge is on the wire AND the entry window has
        /// run out, so the device may be bridged and its console silent. An apply sent from here
        /// LEAVES the bridge.
        /// </summary>
        BridgeEntered
    }

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

        // Read only by the failure paths at the bottom of this method. How far the prepare step got
        // decides what recovery is allowed to send, because the firmware's two update-mode states
        // react to LAN:APPLY in opposite directions: from "requested" an APPLY ENTERS the bridge,
        // and from "bridged" the same APPLY leaves it. Recovery that cannot tell the two apart
        // either strands a bridged device or bridges a device that was still answering (#853).
        // Advanced inside the prepare step, at each point where the device's state actually moves —
        // and "bridged" is only claimed once the device has had the whole window budgeted for
        // acting on the apply, because the apply queues that transition rather than performing it.
        var lanUpdateProgress = LanUpdateModeProgress.NotRequested;

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

                    // Observe cancellation before the first state-changing Send below: a
                    // prep step that is already canceled must not still power the module on
                    // or flip the device into LAN firmware-update mode.
                    stateToken.ThrowIfCancellationRequested();

                    if (device.IsStreaming)
                    {
                        device.StopStreaming();
                    }

                    // The WINC is powered off in standby and right after a PIC32 reflash, and LAN
                    // commands are rejected (-200) in that state — so LAN:FWUpdate would silently
                    // fail to take effect and the flash tool would find no bridge. Power it on and
                    // let it settle first. This is the prep order daqifi-desktop hand-rolls today
                    // (EnableLanUpdateMode: POWer:STATe 1 → settle → LAN:FWUpdate); owning it here
                    // is what lets a consumer drop that workaround (part of #269).
                    if (Options.PowerOnWifiModuleBeforeLanUpdateMode)
                    {
                        device.Send(ScpiMessageProducer.TurnDeviceOn);
                        if (Options.PowerOnWifiModuleSettleDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(Options.PowerOnWifiModuleSettleDelay, stateToken).ConfigureAwait(false);
                        }
                    }

                    // LAN:FWUpdate only REQUESTS WiFi firmware-update mode: the firmware's SCPI
                    // handler raises a request flag and nothing else. The device keeps answering
                    // its console and the WINC bridge does not exist yet.
                    device.Send(ScpiMessageProducer.SetLanFirmwareUpdateMode);

                    // Advanced here rather than before the whole prepare step. Everything above
                    // this line fails with the update-mode command definitively un-sent, and
                    // arming for those turns an immediate "device must be connected" failure into
                    // a full ReconnectingAfterFlash wait for a transport that was never gone.
                    // Immediately *after* the Send is as early as it can honestly be: Send is
                    // synchronous and no await separates it from this line, so nothing can
                    // interleave between them. From here on Core must assume the request took — a
                    // cancel or state timeout can land while the device is still acting on a
                    // command it already received.
                    lanUpdateProgress = LanUpdateModeProgress.UpdateRequested;

                    await Task.Delay(Options.PostLanFirmwareModeDelay, stateToken).ConfigureAwait(false);

                    // The command that actually enters the bridge. The firmware's WiFi manager
                    // reads the request flag on its next REINIT, and LAN:APPLY is what queues that
                    // REINIT: it moves the module into WiFi-firmware-update mode, brings the
                    // USB-to-WINC serial bridge up and turns USB-transparent mode on. Without it
                    // the device stays in "requested" forever and the flash tool finds no bridge
                    // to talk to — the first half of #853. It is also the order the firmware's own
                    // utility script uses (POWer:STATe 1, LAN:FWUpdate, LAN:APPLY) and the order
                    // WifiBridgeActivator.Activate already drives over a raw serial port.
                    device.Send(ScpiMessageProducer.ApplyNetworkLan);

                    // Same reasoning as above, one state further on — but only as far as the apply
                    // honestly takes the device. The apply QUEUES the transition: the firmware's
                    // WiFi manager consumes the request flag on its next REINIT, so right after
                    // the Send the flag can still be set and the firmware's apply gate is still
                    // held, which makes a second apply from here a no-op at best. "Bridged" is
                    // claimed below, once the entry window has actually run out.
                    lanUpdateProgress = LanUpdateModeProgress.BridgeEntryPending;

                    // Let the bridge come up before the port is handed to the flash tool. See
                    // PostLanBridgeEntryDelay: on a board that cannot reach its access point the
                    // firmware defers this transition by up to its station retry backoff.
                    if (Options.PostLanBridgeEntryDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(Options.PostLanBridgeEntryDelay, stateToken).ConfigureAwait(false);
                    }

                    // The entry window has run out, so the device has had all the time this host
                    // budgets for the queued REINIT. From here a recovery LAN:APPLY leaves the
                    // bridge instead of entering it. A cancel or state timeout that lands before
                    // this line leaves the progress at BridgeEntryPending, and recovery waits the
                    // window out itself rather than applying into a possibly-pending request.
                    lanUpdateProgress = LanUpdateModeProgress.BridgeEntered;

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

                    // Leave the USB-to-WINC transparent bridge FIRST. While transparent mode is on
                    // the SCPI console layer is bypassed, so the LAN commands below would be
                    // forwarded to the WINC as raw bytes instead of being interpreted by the
                    // device — the LAN configuration would silently not be restored. Sent
                    // unconditionally: it is a no-op when the device is not in transparent mode,
                    // and restoring the pre-flash state is the entire job of this step. Completes
                    // the recovery half of daqifi-desktop's ResetLanAfterUpdate (part of #269).
                    device.Send(ScpiMessageProducer.SetUsbTransparencyMode(0));

                    // Leaving the bridge is a device-side mode transition, not an instantaneous
                    // one. Until the SCPI console path is back, bytes on the port are still
                    // forwarded to the WINC as raw data, so a LAN command sent immediately after
                    // can be swallowed by the bridge and the restore silently does nothing —
                    // the same intermittent failure the exit itself exists to prevent.
                    // WifiBridgeActivator.Deactivate already paces this exact transition by the
                    // same amount over a raw serial port; this is the managed-connection twin.
                    if (Options.PostUsbTransparentModeExitDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(Options.PostUsbTransparentModeExitDelay, stateToken).ConfigureAwait(false);
                    }

                    device.Send(ScpiMessageProducer.EnableNetworkLan);
                    device.Send(ScpiMessageProducer.ApplyNetworkLan);
                    device.Send(ScpiMessageProducer.SaveNetworkLan);
                },
                cancellationToken).ConfigureAwait(false);

            _context.CompleteOperation(progress, "WiFi module update completed.", totalBytes, totalBytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Before reporting the outcome: a canceled WiFi flash is the single most likely way to
            // leave the module bridged, so the exit runs here too — on its own token, since the
            // caller's is already canceled by definition on this path.
            await TryLeaveLanUpdateModeAfterFailureAsync(device, lanUpdateProgress).ConfigureAwait(false);

            // A WiFi module update never writes the PIC32 application flash, so every failed end
            // state here reports it as unchanged.
            _context.TransitionToState(
                FirmwareUpdateState.Failed, "WiFi module update canceled.", Pic32FlashCondition.NotChanged);
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
            await TryLeaveLanUpdateModeAfterFailureAsync(device, lanUpdateProgress).ConfigureAwait(false);

            _context.TransitionToState(FirmwareUpdateState.Failed, failedOperation, Pic32FlashCondition.NotChanged);
            _context.ReportProgress(progress, FirmwareUpdateState.Failed, _context.LastReportedPercent, failedOperation, 0, totalBytes);
            Logger.LogError(ex, "WiFi module update failed in state {State}.", failedState);

            throw _context.CreateFirmwareUpdateException(
                failedState, failedOperation, ex, flashCondition: Pic32FlashCondition.NotChanged);
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

        // Bounded retry for the LAN chip-info probe (closes #144, #203): without it the WiFi
        // version decision short-circuits to ChipInfoUnavailable on a module that is merely
        // still starting up, and flows on to a multi-minute reflash of already-current
        // firmware. The loop itself is public API (issue #269) so consumers share one
        // implementation of it rather than each hand-rolling their own; see
        // GetLanChipInfoWithRetryAsync for what it does and why.
        var (chipInfo, wasLanNotInitialized) = await lanChipInfoProvider
            .GetLanChipInfoWithRetryAsync(BuildLanChipInfoRetryOptions(), Logger, cancellationToken)
            .ConfigureAwait(false);
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
                _context.CompleteOperation(progress, message, 100, 100);
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
                    _context.CompleteOperation(progress, minimumMessage, 100, 100);
                    return true;
                }

                return false;
        }
    }

    /// <summary>
    /// Projects this service's chip-info retry settings onto the shared probe's options.
    /// </summary>
    /// <remarks>
    /// Read fresh on every probe rather than cached: <see cref="FirmwareUpdateServiceOptions"/>
    /// stays mutable after the service is constructed, so a caller that adjusts the budget between
    /// calls gets the budget it asked for.
    /// </remarks>
    private LanChipInfoRetryOptions BuildLanChipInfoRetryOptions() => new()
    {
        MaxAttempts = Options.LanChipInfoMaxAttempts,
        RetryDelay = Options.LanChipInfoRetryDelay,
        TotalTimeout = Options.LanChipInfoTotalTimeout,
        KickLanApplyOnNotInitialized = Options.KickLanApplyOnNotInitialized,
    };

    private ExternalProcessRequest BuildWifiProcessRequest(
        IStreamingDevice device,
        string firmwarePath,
        IProgress<FirmwareUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var toolPath = ResolveWifiToolPath(firmwarePath);
        var port = ResolveWifiPort(device);

        // A .cmd or .bat tool is not directly executable, so on Windows it runs through cmd.exe.
        // That shell parses the line itself, with rules of its own, so the whole command line is
        // built in one place and tested as a pure string. See WincFlashCommandLine (issue #844).
        var extension = Path.GetExtension(toolPath);
        var throughWindowsShell =
            (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)) &&
            OperatingSystem.IsWindows();

        var command = WincFlashCommandLine.Build(
            toolPath,
            Options.WifiFlashToolArgumentsTemplate,
            port,
            firmwarePath,
            throughWindowsShell);

        // Tracks the live device-flash phase (write/read/verify) from the tool's block-address
        // output so the bar advances across the multi-minute flash, instead of latching to the
        // image-build phase's "100%" lines and freezing. See WifiFlashProgressParser.
        var progressParser = new WifiFlashProgressParser();
        var progressLock = new object();

        return new ExternalProcessRequest
        {
            FileName = command.FileName,
            Arguments = command.Arguments,
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

        // The port goes to the Microchip flash tool as /p <port>, so it has to be a name the
        // operating system can open — COM7, /dev/cu.usbmodem1101 — and the only place that name
        // exists is the device's serial transport.
        //
        // This used to read device.Name. A device's name is a display label, not a port: the
        // factory fills it from DeviceConnectionOptions.DeviceName, whose default is
        // "DAQiFi Device". So unless the caller set WifiPortOverride, the tool was launched with
        // /p "DAQiFi Device" and reported a confusing failure to open the port (issue #842).
        if (device is ISerialPortNamed serialDevice)
        {
            var portName = serialDevice.SerialPortName;
            if (!string.IsNullOrWhiteSpace(portName))
            {
                return portName;
            }
        }

        // Fail here rather than pass something the tool cannot open. A guess costs the caller a
        // tool error that names the wrong problem; this names the real one.
        throw new InvalidOperationException(
            "Unable to resolve a serial port for the WiFi module update. The WiFi flash tool is " +
            "launched with the serial port the device is on, and this device does not report one " +
            "— it is not connected over serial. Connect the device over USB/serial, or set " +
            $"{nameof(FirmwareUpdateServiceOptions)}.{nameof(FirmwareUpdateServiceOptions.WifiPortOverride)} " +
            "to the port name (for example \"COM7\"). The device's name is a display label and " +
            "is not a serial port.");
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
    /// Whatever the state, recovery has to leave the device answerable AND without the
    /// firmware's WiFi-firmware-update request still set. The request matters because the firmware
    /// clears it in exactly one place — the WiFi manager's REINIT branch, and only when the bridge
    /// is already up — while its ENTRY handler deliberately carries the flag across a state reset.
    /// A recovery that leaves it set hands the next <c>LAN:APPLY</c> from anywhere a silent bridge
    /// entry, and Core itself sends that command from the network-configuration apply and from the
    /// not-initialized chip-info kick (#853).
    /// <para>
    /// <paramref name="progress"/> decides how:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="LanUpdateModeProgress.BridgeEntered"/> — the device may already be bridged, so
    /// the exit goes out as-is. This is the managed-connection twin of
    /// <see cref="WifiBridgeActivator.Deactivate"/> and sends the same two commands in the same
    /// order.
    /// </description></item>
    /// <item><description>
    /// <see cref="LanUpdateModeProgress.BridgeEntryPending"/> — the apply that enters the bridge
    /// is on the wire but the device has not had the whole
    /// <see cref="FirmwareUpdateServiceOptions.PostLanBridgeEntryDelay"/> window to act on it. The
    /// apply only queues the transition, and the firmware guards that transition with an
    /// apply-in-progress gate it holds until the bridge is up, so an exit sent now is most likely
    /// rejected outright (-200) and silently does nothing. The queued transition cannot be called
    /// back either, so this state waits the window out first — ahead of the transparent-mode exit
    /// too, because a REINIT that fires afterwards turns transparent mode back on and undoes it.
    /// </description></item>
    /// <item><description>
    /// <see cref="LanUpdateModeProgress.UpdateRequested"/> — the request is on the wire but the
    /// apply is not, so the device is still answering its console with the request flag set, and
    /// no bridge exists to leave. This state sends the transparent-mode exit and then
    /// <c>SYSTem:REboot</c>, which is the only way to clear the request that cannot itself go
    /// wrong: the flag is WiFi-manager RAM state, so the reboot drops it outright. Driving the
    /// device through the bridge to consume the request works only while the queued entry lands
    /// inside <see cref="FirmwareUpdateServiceOptions.PostLanBridgeEntryDelay"/> — the firmware's
    /// bridge init turns USB-transparent mode back on, so a late entry re-silences the console and
    /// swallows the trailing <c>LAN:APPLY</c>, trading a latent bridge entry for an immediate dead
    /// one. Nothing was flashed on this path, so the reboot costs only the re-enumeration.
    /// </description></item>
    /// </list>
    /// The two bridge states' waits are not cancelled and neither reads the device state back
    /// (#879 covers confirming it): the caller's token is already cancelled on the path that needs
    /// this most, and aborting either wait would skip or waste the exit. The cost is bounded by the
    /// options — at worst one
    /// <see cref="FirmwareUpdateServiceOptions.PostLanBridgeEntryDelay"/> plus one
    /// <see cref="FirmwareUpdateServiceOptions.PostUsbTransparentModeExitDelay"/> on top of the
    /// reconnect budget — so a host that lengthens the entry window to suit a slow board is
    /// lengthening this path by the same amount. The pause before <c>LAN:APPLY</c> is
    /// <see cref="FirmwareUpdateServiceOptions.PostUsbTransparentModeExitDelay"/>, the same settle
    /// the success path waits before its LAN restore; its default matches
    /// <see cref="WifiBridgeActivator.InterCommandDelay"/>.
    /// <para>
    /// Deliberately NOT the success path's full <c>LAN:ENAbled</c>/<c>APPLY</c>/<c>SAVE</c>
    /// restore: that persists a configuration, which has no business running off the back of a
    /// flash that did not complete. The job here is only to make the device answerable again.
    /// </para>
    /// </remarks>
    private async Task TryLeaveLanUpdateModeAfterFailureAsync(
        IStreamingDevice device,
        LanUpdateModeProgress progress)
    {
        if (progress == LanUpdateModeProgress.NotRequested)
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
            // come back before anything can be sent. Returns immediately when still connected,
            // which is every failure that lands before prep's Disconnect — both states below.
            await _context.WaitForSerialReconnectAsync(device, restoreCts.Token).ConfigureAwait(false);

            // The request is on the wire and the apply that acts on it is not, so the device is
            // still answering its console — but it is holding the request flag, and the firmware
            // clears that flag in exactly one place: a REINIT taken while the bridge is already
            // up. Leaving it set hands the next LAN:APPLY from anywhere a silent bridge entry,
            // Core's own network-configuration apply and not-initialized chip-info kick included,
            // with no recovery armed to catch it (#853).
            //
            // So clear it the one way that cannot go wrong. Driving the device through the bridge
            // to consume the request also works, but only if the queued entry lands inside
            // PostLanBridgeEntryDelay: the firmware's bridge init turns USB-transparent mode back
            // ON, so an entry that lands after the exit went out re-silences the console and the
            // trailing LAN:APPLY is swallowed by the bridge it was meant to leave. That trades a
            // latent bridge entry for an immediate dead console. The request flag lives in the
            // WiFi manager's RAM state, so a reboot clears it outright — no queued transition, no
            // apply gate, no window to lose. Nothing was flashed on this path (prep had not even
            // reached its disconnect), so a reboot costs only the re-enumeration, and SYSTem:REboot
            // is already how this project clears a wedged WiFi manager.
            if (progress == LanUpdateModeProgress.UpdateRequested)
            {
                // Harmless here (transparent mode is not on in this state) and sent first for the
                // same reason the bridged path sends it first: if Core's view is somehow behind
                // the device's, this is what hands the console back so the reboot is even read.
                device.Send(ScpiMessageProducer.SetUsbTransparencyMode(0));
                device.Send(ScpiMessageProducer.RebootDevice);

                Logger.LogInformation(
                    "A WiFi update failed with firmware-update mode requested but not applied, so the device is " +
                    "not bridged but is still holding the firmware's update request. Rebooted it to clear that " +
                    "request: the firmware only clears it on the way out of the bridge, so leaving it set would " +
                    "let the next LAN:APPLY from anywhere enter WiFi firmware-update mode.");
                return;
            }

            // The apply that enters the bridge is on the wire but the device has not been given
            // the whole window this host budgets for acting on it. The apply only queues the
            // transition, and the firmware holds an apply-in-progress gate from that apply until
            // the bridge is up, so an exit sent inside this window is most likely rejected (-200)
            // and silently does nothing — and once the gate's own safety release lets one through,
            // the request is still pending, so it re-enters rather than leaves. The queued
            // transition cannot be called back, so the only correct move is to let it land. Wait
            // the window out before anything goes on the wire, including the transparent-mode
            // exit: a REINIT that fires after that exit turns transparent mode back on and undoes
            // it.
            //
            // Uncancelled and bounded by the option, for the same reason as the settle below: the
            // caller's token is already cancelled on the path that needs this most. It does not
            // make the device state certain — reading it back is #879, and this wait is the same
            // bounded stand-in the forward path above already accepts.
            if (progress == LanUpdateModeProgress.BridgeEntryPending
                && Options.PostLanBridgeEntryDelay > TimeSpan.Zero)
            {
                Logger.LogInformation(
                    "Waiting {Delay} for the queued WiFi bridge entry to land before sending the bridge exit, " +
                    "so the exit is not rejected while the firmware's apply gate is still held.",
                    Options.PostLanBridgeEntryDelay);

                await Task.Delay(Options.PostLanBridgeEntryDelay).ConfigureAwait(false);
            }

            // Past the reconnect the two-command exit runs to completion instead of re-observing
            // the budget. Half of it is the one outcome worse than not starting: the console is
            // handed back but the WiFi manager is left in its bridge-mode state machine, so the
            // device looks answerable while its module still is not. The un-cancelled tail is the
            // transparent-mode exit settle plus two synchronous writes, so the helper stays
            // bounded either way. The caller token is already cancelled on this path and must not
            // abort the exit — the settle observes neither it nor the recovery budget.
            device.Send(ScpiMessageProducer.SetUsbTransparencyMode(0));

            // Same settle as the success path, including the zero escape hatch. Leaving the bridge
            // is a device-side mode transition: until the SCPI console path is back, bytes on the
            // port are still forwarded raw to the WINC, so a command sent immediately after can
            // be swallowed. Uncancelled on purpose — see the remarks above.
            if (Options.PostUsbTransparentModeExitDelay > TimeSpan.Zero)
            {
                await Task.Delay(Options.PostUsbTransparentModeExitDelay).ConfigureAwait(false);
            }

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
