using System.Runtime.ExceptionServices;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Microsoft.Extensions.Logging;

namespace Daqifi.Core.Firmware;

/// <summary>
/// The PIC32 bootloader half of <see cref="FirmwareUpdateService"/>: forces the device into
/// bootloader mode, waits for the HID bootloader to enumerate, connects, then erases, programs,
/// CRC-verifies and jumps back to the application. Also serves the standalone bootloader
/// diagnostics (health check / soft reset) and the post-failure re-erase cleanup. Drives the
/// individual bootloader exchanges through <see cref="Pic32BootloaderSession"/> and the shared
/// state-machine, progress and retry plumbing through <see cref="FirmwareUpdateContext"/>.
/// Callers must serialize invocations — the service facade holds the operation lock.
/// </summary>
internal sealed class Pic32FirmwareUpdater
{
    // States where a failure may have left the application flash partially
    // written AND the HID bootloader is still connected, so re-erasing to a
    // clean bootloader state is both necessary and possible. Failures in
    // PreparingDevice/WaitingForBootloader/Connecting happen before any flash
    // write; a JumpingToApp failure happens after HID has already been
    // disconnected — neither is eligible for cleanup.
    private static readonly IReadOnlySet<FirmwareUpdateState> CleanupEligibleStates
        = new HashSet<FirmwareUpdateState>
        {
            FirmwareUpdateState.ErasingFlash,
            FirmwareUpdateState.Programming,
            FirmwareUpdateState.Verifying
        };

    private readonly FirmwareUpdateContext _context;
    private readonly Pic32BootloaderSession _session;

    internal Pic32FirmwareUpdater(FirmwareUpdateContext context, Pic32BootloaderSession session)
    {
        _context = context;
        _session = session;
    }

    private ILogger Logger => _context.Logger;

    private FirmwareUpdateServiceOptions Options => _context.Options;

    internal async Task RunUpdateAsync(
        IStreamingDevice device,
        IReadOnlyList<byte[]> hexRecords,
        IReadOnlyList<FlashCrcRegion> crcRegions,
        long totalBytes,
        IProgress<FirmwareUpdateProgress>? progress,
        string? targetDevicePath,
        string? targetLocationKey,
        CancellationToken cancellationToken)
    {
        // Recorded so a WaitingForBootloader timeout can name the requested path/location in its message.
        _session.SetRequestedTarget(targetDevicePath, targetLocationKey);

        try
        {
            _context.TransitionToState(FirmwareUpdateState.PreparingDevice, "Preparing device for PIC32 firmware update.");
            _context.ReportProgress(progress, FirmwareUpdateState.PreparingDevice, 0, _context.CurrentOperation, 0, totalBytes);

            await _context.ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.PreparingDevice,
                "prepare the device for bootloader mode",
                async stateToken =>
                {
                    FirmwareUpdateContext.EnsureDeviceConnected(device);

                    if (device.IsStreaming)
                    {
                        device.StopStreaming();
                    }

                    device.Send(ScpiMessageProducer.ForceBootloader);
                    await Task.Delay(Options.PostForceBootDelay, stateToken).ConfigureAwait(false);
                    device.Disconnect();
                },
                cancellationToken).ConfigureAwait(false);

            _context.TransitionToState(FirmwareUpdateState.WaitingForBootloader, "Waiting for HID bootloader device.");
            _context.ReportProgress(progress, FirmwareUpdateState.WaitingForBootloader, 5, _context.CurrentOperation, 0, totalBytes);

            var hidDevice = await _context.ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.WaitingForBootloader,
                "wait for HID bootloader enumeration",
                ct => _session.WaitForBootloaderDeviceAsync(targetDevicePath, targetLocationKey, ct),
                cancellationToken).ConfigureAwait(false);

            _context.TransitionToState(FirmwareUpdateState.Connecting, "Connecting to HID bootloader.");
            _context.ReportProgress(progress, FirmwareUpdateState.Connecting, 10, _context.CurrentOperation, 0, totalBytes);

            string version;
            try
            {
                await _context.ExecuteWithStateTimeoutAsync(
                    FirmwareUpdateState.Connecting,
                    "connect HID transport",
                    ct => _session.ConnectWithRetryAsync(hidDevice, targetDevicePath, targetLocationKey, ct),
                    cancellationToken).ConfigureAwait(false);

                version = await _context.ExecuteWithStateTimeoutAsync(
                    FirmwareUpdateState.Connecting,
                    "request bootloader version",
                    _session.RequestVersionAsync,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // #298: a dirty HID bootloader handle left behind by another
                // program (or a previous run) can make the connect or the
                // version health check fail even though the device is
                // physically present. Nothing has been erased yet, so it's
                // safe to attempt one JMP_TO_APP soft reset to force a clean
                // re-enumeration before giving up.
                version = await RecoverBootloaderHealthWithSoftResetAsync(
                    ex,
                    targetDevicePath,
                    targetLocationKey,
                    cancellationToken).ConfigureAwait(false);
            }

            Logger.LogInformation("Bootloader version response: {BootloaderVersion}", version);

            _context.TransitionToState(FirmwareUpdateState.ErasingFlash, "Erasing PIC32 flash.");
            _context.ReportProgress(progress, FirmwareUpdateState.ErasingFlash, 15, _context.CurrentOperation, 0, totalBytes);

            await _context.ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.ErasingFlash,
                "erase flash",
                _session.EraseFlashWithRetryAsync,
                cancellationToken).ConfigureAwait(false);

            _context.TransitionToState(FirmwareUpdateState.Programming, "Programming flash records.");
            _context.ReportProgress(progress, FirmwareUpdateState.Programming, 20, _context.CurrentOperation, 0, totalBytes);

            await _context.ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.Programming,
                "program flash records",
                ct => _session.ProgramFlashAsync(hexRecords, totalBytes, progress, ct),
                cancellationToken).ConfigureAwait(false);

            _context.TransitionToState(FirmwareUpdateState.Verifying, "Verifying flash contents via CRC.");
            _context.ReportProgress(progress, FirmwareUpdateState.Verifying, 92, _context.CurrentOperation, totalBytes, totalBytes);

            await _context.ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.Verifying,
                "verify flash contents via CRC",
                ct => _session.VerifyFlashContentsAsync(crcRegions, progress, totalBytes, ct),
                cancellationToken).ConfigureAwait(false);

            _context.TransitionToState(FirmwareUpdateState.JumpingToApp, "Jumping to application firmware.");
            _context.ReportProgress(progress, FirmwareUpdateState.JumpingToApp, 95, _context.CurrentOperation, totalBytes, totalBytes);

            await _context.ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.JumpingToApp,
                "jump to application and reconnect serial transport",
                ct => JumpToApplicationAndReconnectAsync(device, ct),
                cancellationToken).ConfigureAwait(false);

            _context.TransitionToState(FirmwareUpdateState.Complete, "PIC32 firmware update completed.");
            _context.ReportProgress(progress, FirmwareUpdateState.Complete, 100, _context.CurrentOperation, totalBytes, totalBytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var canceledState = _context.CurrentState;
            Logger.LogWarning("PIC32 firmware update canceled in state {State}.", canceledState);

            // A cancel mid-flash still leaves the device half-flashed, so it must
            // be cleaned up just like any other failure in a flash-touching state
            // (acceptance criterion #208: never leave a half-flashed device). The
            // cleanup runs on its own token, so the already-canceled operation
            // token does not abort it. We still rethrow the cancellation.
            await CleanUpAfterFailureAsync(canceledState, progress, totalBytes, canceled: true)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // Capture the state/operation at the moment of failure BEFORE any
            // cleanup transitions move us off it — these stay the diagnostic
            // "where it broke" context on the thrown exception.
            var failedState = _context.CurrentState;
            var failedOperation = _context.CurrentOperation;
            Logger.LogError(ex, "PIC32 firmware update failed in state {State}.", failedState);

            var cleanupOutcome = await CleanUpAfterFailureAsync(
                failedState, progress, totalBytes).ConfigureAwait(false);

            throw _context.CreateFirmwareUpdateException(
                failedState, failedOperation, ex, BuildRecoveryGuidance(failedState, cleanupOutcome));
        }
        finally
        {
            await _session.SafeDisconnectAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Standalone bootloader health check: waits for the bootloader to enumerate, connects the HID
    /// transport and reads back the bootloader version. Touches no flash.
    /// </summary>
    internal Task<string> RunHealthCheckAsync(
        string? targetDevicePath,
        CancellationToken cancellationToken)
    {
        return RunDiagnosticAsync(
            targetDevicePath,
            failureSubject: "Bootloader health check",
            async (phase, innerCt) =>
            {
                phase.Advance(FirmwareUpdateState.Connecting, "request bootloader version");
                var version = await _context.ExecuteWithStateTimeoutAsync(
                    phase.State,
                    phase.Operation,
                    _session.RequestVersionAsync,
                    innerCt).ConfigureAwait(false);

                Logger.LogInformation(
                    "Standalone bootloader health check succeeded; version {BootloaderVersion}.", version);
                return version;
            },
            cancellationToken);
    }

    /// <summary>
    /// Standalone bootloader soft reset: connects the HID transport and issues a single
    /// <c>JMP_TO_APP</c> so the device leaves bootloader mode. Touches no flash.
    /// </summary>
    internal Task RunSoftResetAsync(
        string? targetDevicePath,
        CancellationToken cancellationToken)
    {
        return RunDiagnosticAsync<object?>(
            targetDevicePath,
            failureSubject: "Bootloader soft reset",
            async (phase, innerCt) =>
            {
                phase.Advance(FirmwareUpdateState.JumpingToApp, "issue JMP_TO_APP soft reset");
                await _context.ExecuteWithStateTimeoutAsync(
                    phase.State,
                    phase.Operation,
                    _session.SendJumpToApplicationAsync,
                    innerCt).ConfigureAwait(false);

                Logger.LogInformation(
                    "Standalone JMP_TO_APP soft reset issued to bootloader without touching flash.");
                return null;
            },
            cancellationToken);
    }

    /// <summary>
    /// The phase a standalone diagnostic is currently in, so a failure is reported against the
    /// step it actually occurred in — mirroring <see cref="RunUpdateAsync"/>'s
    /// failedState/failedOperation capture. Mutable and passed by reference because the shared
    /// preamble and the caller's final step both advance it, and the catch block reads whatever
    /// it last held.
    /// </summary>
    private sealed class DiagnosticPhase
    {
        internal FirmwareUpdateState State { get; private set; } = FirmwareUpdateState.WaitingForBootloader;

        internal string Operation { get; private set; } = "wait for HID bootloader enumeration";

        /// <summary>
        /// Moves to the next phase. State and operation move together so a step can never
        /// report one phase's state with another phase's label.
        /// </summary>
        internal void Advance(FirmwareUpdateState state, string operation)
        {
            State = state;
            Operation = operation;
        }
    }

    /// <summary>
    /// Runs a standalone bootloader diagnostic: the shared "wait for the bootloader to enumerate,
    /// then open the HID transport" preamble, then <paramref name="finalStep"/>, with the common
    /// failure contract — a cancel requested by the caller propagates untouched, and anything else
    /// is wrapped as a <see cref="FirmwareUpdateException"/> naming the phase that broke.
    /// Touches no flash.
    /// </summary>
    private async Task<T> RunDiagnosticAsync<T>(
        string? targetDevicePath,
        string failureSubject,
        Func<DiagnosticPhase, CancellationToken, Task<T>> finalStep,
        CancellationToken cancellationToken)
    {
        var phase = new DiagnosticPhase();
        try
        {
            var hidDevice = await _context.ExecuteWithStateTimeoutAsync(
                phase.State,
                phase.Operation,
                innerCt => _session.WaitForBootloaderDeviceAsync(targetDevicePath, null, innerCt),
                cancellationToken).ConfigureAwait(false);

            phase.Advance(FirmwareUpdateState.Connecting, "connect HID transport");
            await _context.ExecuteWithStateTimeoutAsync(
                phase.State,
                phase.Operation,
                innerCt => _session.ConnectWithRetryAsync(hidDevice, targetDevicePath, null, innerCt),
                cancellationToken).ConfigureAwait(false);

            return await finalStep(phase, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw _context.CreateFirmwareUpdateException(
                phase.State, phase.Operation, ex, failureSubject: failureSubject);
        }
    }

    /// <summary>
    /// Describes the terminal disposition of a failed PIC32 update after the
    /// optional re-erase cleanup pass, used to tailor the recovery guidance and
    /// reflected by the service's terminal state.
    /// </summary>
    private enum Pic32CleanupOutcome
    {
        /// <summary>
        /// Cleanup did not apply: the failure occurred before flash was written
        /// (PreparingDevice/WaitingForBootloader/Connecting) or after the HID
        /// bootloader was disconnected (JumpingToApp). Terminal state: Failed.
        /// </summary>
        NotEligible,

        /// <summary>
        /// The application flash was re-erased successfully; the device is in a
        /// clean bootloader state and can be re-flashed. Terminal state: Recovered.
        /// </summary>
        Recovered,

        /// <summary>
        /// Cleanup was eligible but could not complete (the HID transport had
        /// dropped, or the re-erase itself failed), so the device may be in a
        /// half-flashed state. Terminal state: Failed.
        /// </summary>
        CleanupFailed
    }

    /// <summary>
    /// After a PIC32 update failure, re-erases the application flash when the
    /// failure left the device half-flashed but still reachable over HID, so it
    /// is never abandoned in a partially-programmed state. Drives the
    /// CleaningUp → Recovered (success) or → Failed (cleanup failed) terminal
    /// transitions and reports them via state/progress events. The update has
    /// already failed; this only determines how safely it ends.
    /// </summary>
    private async Task<Pic32CleanupOutcome> CleanUpAfterFailureAsync(
        FirmwareUpdateState failedState,
        IProgress<FirmwareUpdateProgress>? progress,
        long totalBytes,
        bool canceled = false)
    {
        var frozenPercent = _context.LastReportedPercent;
        var eligible = CleanupEligibleStates.Contains(failedState);

        if (!eligible || !_session.IsConnected)
        {
            // No re-erase will run. Either the failure never touched flash / the
            // device is past HID (NotEligible — keep the per-state guidance), or
            // a flash-touching failure left the HID transport unusable so we
            // cannot re-erase (CleanupFailed — warn that it may be half-flashed).
            // Both terminate in Failed. On the cancel path the rethrown
            // OperationCanceledException carries no recovery guidance, so this
            // terminal event text is the only channel observers get.
            var outcome = eligible ? Pic32CleanupOutcome.CleanupFailed : Pic32CleanupOutcome.NotEligible;

            string failedOperation;
            if (outcome == Pic32CleanupOutcome.CleanupFailed)
            {
                failedOperation = canceled
                    ? "PIC32 firmware update canceled; cleanup re-erase skipped because the HID transport " +
                      "disconnected — device may be in a half-flashed state."
                    : "Cleanup re-erase skipped: HID transport disconnected; device may be in a half-flashed state.";
                Logger.LogWarning(
                    "Cannot run firmware re-erase cleanup after failure in {State}: HID transport is no longer " +
                    "connected; device may be in a half-flashed state.",
                    failedState);
            }
            else
            {
                failedOperation = canceled ? "PIC32 firmware update canceled." : _context.CurrentOperation;
            }

            _context.TransitionToState(FirmwareUpdateState.Failed, failedOperation);
            _context.ReportProgress(progress, FirmwareUpdateState.Failed, frozenPercent, failedOperation, 0, totalBytes);
            return outcome;
        }

        var cleaningOperation = canceled
            ? "Update canceled; re-erasing flash to leave the device in a clean bootloader state."
            : "Re-erasing flash to leave the device in a clean bootloader state.";

        try
        {
            // The CleaningUp notification runs inside the try: a throwing
            // StateChanged subscriber or progress sink must land in the catch
            // below (CleaningUp → Failed) rather than stranding the machine in
            // the non-terminal CleaningUp state, which has no reset path.
            _context.TransitionToState(FirmwareUpdateState.CleaningUp, cleaningOperation);
            _context.ReportProgress(progress, FirmwareUpdateState.CleaningUp, frozenPercent, cleaningOperation, 0, totalBytes);
            Logger.LogInformation(
                "Attempting firmware re-erase cleanup after failure in {State}.", failedState);

            // Reuse the same retry-wrapped erase path as the main flow, but on a
            // fresh timeout token: the cleanup must run on a best-effort basis
            // even if the original operation token was already canceled, and it
            // is bounded by the same budget as a normal erase.
            using var cleanupCts = new CancellationTokenSource(
                Options.GetStateTimeout(FirmwareUpdateState.CleaningUp));
            await _session.EraseFlashWithRetryAsync(cleanupCts.Token).ConfigureAwait(false);

            var recoveredOperation = canceled
                ? "Update canceled; flash re-erased — device is in a clean bootloader state and can be re-flashed."
                : "Flash re-erased; device is in a clean bootloader state and can be re-flashed.";
            _context.TransitionToState(FirmwareUpdateState.Recovered, recoveredOperation);
            _context.ReportProgress(progress, FirmwareUpdateState.Recovered, frozenPercent, recoveredOperation, 0, totalBytes);
            Logger.LogInformation(
                "Firmware re-erase cleanup succeeded; device is in a clean bootloader state.");
            return Pic32CleanupOutcome.Recovered;
        }
        catch (Exception cleanupEx)
        {
            if (_context.CurrentState == FirmwareUpdateState.Recovered)
            {
                // The re-erase itself succeeded — a StateChanged subscriber or
                // progress sink threw after the Recovered transition committed.
                // The device is clean; a consumer callback must not turn that
                // into a half-flashed verdict (and Recovered → Failed is not a
                // legal transition).
                Logger.LogWarning(
                    cleanupEx,
                    "A state/progress observer threw after the Recovered transition; cleanup itself succeeded.");
                return Pic32CleanupOutcome.Recovered;
            }

            const string cleanupFailedOperation =
                "Cleanup re-erase failed; device may be in a half-flashed state.";
            _context.TransitionToState(FirmwareUpdateState.Failed, cleanupFailedOperation);
            _context.ReportProgress(progress, FirmwareUpdateState.Failed, frozenPercent, cleanupFailedOperation, 0, totalBytes);
            Logger.LogError(
                cleanupEx,
                "Firmware re-erase cleanup failed after failure in {State}; device may be half-flashed.",
                failedState);
            return Pic32CleanupOutcome.CleanupFailed;
        }
    }

    private static string BuildRecoveryGuidance(
        FirmwareUpdateState failedState,
        Pic32CleanupOutcome cleanupOutcome)
    {
        // When a re-erase cleanup ran, its outcome — not the original failure
        // state — drives the guidance: the operator needs to know whether the
        // device is safe to simply re-flash or may be half-flashed.
        switch (cleanupOutcome)
        {
            case Pic32CleanupOutcome.Recovered:
                return "The update did not complete, but the device's application flash was automatically " +
                       "re-erased and it is now in a clean bootloader state — safe to re-flash. " +
                       "Simply re-run the firmware update.";
            case Pic32CleanupOutcome.CleanupFailed:
                return "The update failed and the automatic re-erase cleanup could not complete, so the device " +
                       "may be in a half-flashed state. Power-cycle the device into bootloader mode and re-run " +
                       "the firmware update; the next erase will restore a clean state.";
            case Pic32CleanupOutcome.NotEligible:
            default:
                return FirmwareUpdateContext.BuildRecoveryGuidance(failedState);
        }
    }

    /// <summary>
    /// Recovers from a failed <see cref="FirmwareUpdateState.Connecting"/> health
    /// check (bad connect or a garbage version response) by issuing one
    /// <c>JMP_TO_APP</c> soft reset, waiting for the bootloader to re-enumerate,
    /// and retrying the connect + version check exactly once. See #298: the
    /// observed failure is a dirty HID bootloader handle left behind by another
    /// program, which a clean reset clears without touching flash.
    /// </summary>
    private async Task<string> RecoverBootloaderHealthWithSoftResetAsync(
        Exception originalFailure,
        string? targetDevicePath,
        string? targetLocationKey,
        CancellationToken cancellationToken)
    {
        Logger.LogWarning(
            originalFailure,
            "Bootloader connect/health-check failed in {State}; attempting a JMP_TO_APP soft-reset recovery before giving up.",
            FirmwareUpdateState.Connecting);

        try
        {
            // Best-effort: the handle may already be unusable (that's often why
            // the health check failed in the first place), so a write failure
            // here just falls through to the original failure below rather than
            // surfacing a new unhandled exception.
            if (_session.IsConnected)
            {
                await _session.SendJumpToApplicationAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(
                ex,
                "JMP_TO_APP soft-reset write failed; the bootloader handle is likely already unusable.");
            // Rethrow via ExceptionDispatchInfo (not `throw originalFailure;`) so the
            // original exception's stack trace still points at the actual
            // connect/health-check failure site, not this recovery method.
            ExceptionDispatchInfo.Capture(originalFailure).Throw();
            throw; // unreachable; satisfies flow analysis
        }
        finally
        {
            await _session.SafeDisconnectAsync().ConfigureAwait(false);
        }

        try
        {
            var recoveredDevice = await _context.ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.WaitingForBootloader,
                "wait for HID bootloader re-enumeration after soft reset",
                ct => _session.WaitForBootloaderDeviceAsync(targetDevicePath, targetLocationKey, ct),
                cancellationToken).ConfigureAwait(false);

            await _context.ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.Connecting,
                "reconnect HID transport after soft reset",
                ct => _session.ConnectWithRetryAsync(recoveredDevice, targetDevicePath, targetLocationKey, ct),
                cancellationToken).ConfigureAwait(false);

            var version = await _context.ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.Connecting,
                "request bootloader version after soft reset",
                _session.RequestVersionAsync,
                cancellationToken).ConfigureAwait(false);

            Logger.LogInformation("Bootloader health restored after JMP_TO_APP soft reset.");
            return version;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(
                ex,
                "Bootloader is still unhealthy after the JMP_TO_APP soft-reset recovery attempt.");
            ExceptionDispatchInfo.Capture(originalFailure).Throw();
            throw; // unreachable; satisfies flow analysis
        }
    }

    private async Task JumpToApplicationAndReconnectAsync(
        IStreamingDevice device,
        CancellationToken cancellationToken)
    {
        await _session.SendJumpToApplicationAsync(cancellationToken).ConfigureAwait(false);

        await _session.SafeDisconnectAsync().ConfigureAwait(false);
        await _context.WaitForSerialReconnectAsync(device, cancellationToken).ConfigureAwait(false);

        // Discard the race-winning serial handle from the USB CDC re-enumeration
        // window. On macOS the first SerialPort.Open() that succeeds after a PIC32
        // reset is typically a "shadow" handle: IsOpen==true, but the kernel
        // device-node isn't fully wired yet — writes silently drop and reads see
        // zero bytes. A fresh open after a brief settling delay yields a clean
        // binding. Symptom without this step: SCPI Sends after reconnect appear
        // to succeed but the device never responds (LEDs stay off, readiness
        // probe returns null indefinitely until budget expires).
        // Opt out by setting PostReconnectStaleHandleDelay = TimeSpan.Zero
        // (callers on platforms where the first open is already clean).
        if (Options.PostReconnectStaleHandleDelay > TimeSpan.Zero)
        {
            Logger.LogInformation(
                "Discarding race-winning serial handle; closing and re-opening after {Delay} to obtain a clean USB CDC binding.",
                Options.PostReconnectStaleHandleDelay);
            device.Disconnect();
            await Task.Delay(Options.PostReconnectStaleHandleDelay, cancellationToken).ConfigureAwait(false);
            await _context.WaitForSerialReconnectAsync(device, cancellationToken).ConfigureAwait(false);
        }

        // Wake the post-reset device. PIC32 application firmware boots
        // dormant (LEDs off, WiFi subsystem unpowered, won't answer LAN
        // queries) until SYSTem:POWer:STATe 1 is sent. InitializeAsync
        // handles that plus the rest of the standard init sequence
        // (echo off, stream format, etc.). Without this, callers writing
        // a "natural" probe like GetLanChipInfoAsync would silently fail
        // for tens of seconds because the device is still dormant.
        // Skipped for non-DaqifiDevice transports (e.g. test fakes); they
        // are responsible for their own readiness if needed.
        if (device is DaqifiDevice initializableDevice)
        {
            Logger.LogInformation("Waking post-reset device via InitializeAsync.");
            try
            {
                // Pass the update's token so a cancel during the post-reset wake isn't ignored
                // while InitializeAsync waits (up to ChannelPopulationTimeout) for channels.
                await initializableDevice.InitializeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Don't fail the firmware update outright — the readiness
                // probe (if configured) is the source of truth for "ready".
                // Surface the init failure as a warning so a probe timeout
                // later isn't mysterious.
                Logger.LogWarning(ex, "InitializeAsync after reconnect threw; continuing to readiness probe.");
            }
        }

        // Application-readiness probe (closes #145). Serial transport
        // re-enumeration succeeds well before the PIC32 application
        // firmware is actually ready to answer protobuf status queries;
        // if a downstream flow (LAN chip info, WiFi prep) starts before
        // the app is up, those queries fail and callers reimplement
        // their own retry. The probe is opt-in via options — when null,
        // the legacy "serial reopened == done" semantics apply.
        if (Options.PostReconnectReadinessProbe is { } probe)
        {
            await WaitForApplicationReadyAsync(device, probe, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForApplicationReadyAsync(
        IStreamingDevice device,
        Func<IStreamingDevice, CancellationToken, Task<bool>> probe,
        CancellationToken cancellationToken)
    {
        var totalTimeout = Options.PostReconnectReadinessTimeout;
        var retryDelay = Options.PostReconnectReadinessRetryDelay;

        // Surface the wait at Information level so observers tailing the
        // log can distinguish "stuck" from "deliberately polling". The
        // wait can take up to PostReconnectReadinessTimeout (default 30s);
        // without this, the JumpingToApp state appears hung beyond the
        // initial transport reopen.
        Logger.LogInformation(
            "Waiting up to {Timeout} for device to become application-ready (post-reconnect readiness probe).",
            totalTimeout);
        var waitStart = DateTime.UtcNow;

        using var timeoutCts = new CancellationTokenSource(totalTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        // Capture the most recent probe-thrown exception so a TimeoutException
        // can carry the underlying cause as InnerException. Without this,
        // deterministic probe failures (e.g. transport says it's open but
        // the device never responds to status queries) report only as
        // "timed out" — losing the actual error context unless Debug logs
        // are on.
        Exception? lastProbeException = null;

        // Tracks how many probe invocations have actually run. Distinct from
        // the loop iteration counter so the timeout messages don't claim
        // "attempt N" when the timeout fired before a probe ever executed.
        var probesExecuted = 0;
        while (true)
        {
            try
            {
                linkedToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Device did not become application-ready within {totalTimeout} (probes executed: {probesExecuted}). " +
                    "The transport reconnected but the readiness probe never returned true; the device may still be initializing or the firmware may have failed to start.",
                    lastProbeException);
            }

            try
            {
                probesExecuted++;
                // WaitAsync(linkedToken) enforces the timeout deadline even
                // when the probe ignores its own CancellationToken and would
                // otherwise hang or return after the budget elapses. When
                // the deadline fires, WaitAsync throws OperationCanceledException
                // immediately — we don't keep waiting for the rogue probe.
                var isReady = await probe(device, linkedToken)
                    .WaitAsync(linkedToken)
                    .ConfigureAwait(false);

                // Successful probe invocation (true OR false) means the most
                // recent attempt completed normally. Clear lastProbeException
                // so a later timeout doesn't carry forward a stale exception
                // from an earlier failed attempt as its InnerException.
                lastProbeException = null;

                if (isReady)
                {
                    var elapsed = DateTime.UtcNow - waitStart;
                    Logger.LogInformation(
                        "Device became application-ready after {Elapsed} on probe attempt {Attempt}.",
                        elapsed,
                        probesExecuted);
                    return;
                }
                Logger.LogDebug("Application-ready probe returned false on attempt {Attempt}; will retry.", probesExecuted);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Two cases reach here:
                // 1. The wait deadline fired (timeoutCts canceled) — surface
                //    as TimeoutException so callers see the readiness budget.
                // 2. The probe itself threw OperationCanceledException for
                //    some unrelated reason (its own internal CTS, etc). That
                //    must NOT crash the update loop — treat it as a probe
                //    failure and retry, same as any other thrown exception.
                if (timeoutCts.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Device did not become application-ready within {totalTimeout} (probes executed: {probesExecuted}). " +
                        "The wait for the readiness probe was canceled by the timeout — note the probe may ignore cancellation and continue running in the background.",
                        lastProbeException ?? ex);
                }

                lastProbeException = ex;
                Logger.LogDebug(
                    ex,
                    "Application-ready probe was canceled on attempt {Attempt}; treating as not-ready and retrying.",
                    probesExecuted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastProbeException = ex;
                Logger.LogDebug(
                    ex,
                    "Application-ready probe threw on attempt {Attempt}; treating as not-ready and retrying.",
                    probesExecuted);
            }

            try
            {
                await Task.Delay(retryDelay, linkedToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Device did not become application-ready within {totalTimeout} (probes executed: {probesExecuted}). " +
                    "The transport reconnected but the readiness probe never returned true; the device may still be initializing or the firmware may have failed to start.",
                    lastProbeException);
            }
        }
    }
}
