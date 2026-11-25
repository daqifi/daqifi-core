using System.Text;
using Daqifi.Core.Device;
using Microsoft.Extensions.Logging;
using static Daqifi.Core.Internal.DiagnosticGuard;

namespace Daqifi.Core.Firmware;

/// <summary>
/// State, progress and retry plumbing shared by the two independent update flows
/// (<see cref="Pic32FirmwareUpdater"/> and <see cref="WifiModuleUpdater"/>) behind
/// <see cref="FirmwareUpdateService"/>. Owns the update state machine, the last-reported
/// progress percentage, the per-state timeout / retry wrappers and the exception + recovery
/// guidance construction, so neither flow has to know about the other.
/// </summary>
internal sealed class FirmwareUpdateContext
{
    private static readonly IReadOnlyDictionary<FirmwareUpdateState, IReadOnlySet<FirmwareUpdateState>> AllowedTransitions
        = new Dictionary<FirmwareUpdateState, IReadOnlySet<FirmwareUpdateState>>
        {
            [FirmwareUpdateState.Idle] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.PreparingDevice,
                FirmwareUpdateState.Complete,
                FirmwareUpdateState.Failed
            },
            [FirmwareUpdateState.PreparingDevice] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.WaitingForBootloader,
                FirmwareUpdateState.Programming,
                FirmwareUpdateState.Failed
            },
            [FirmwareUpdateState.WaitingForBootloader] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.Connecting,
                FirmwareUpdateState.Failed
            },
            [FirmwareUpdateState.Connecting] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.ErasingFlash,
                FirmwareUpdateState.Failed
            },
            [FirmwareUpdateState.ErasingFlash] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.Programming,
                FirmwareUpdateState.CleaningUp,
                FirmwareUpdateState.Failed
            },
            [FirmwareUpdateState.Programming] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.Verifying,
                FirmwareUpdateState.ReconnectingAfterFlash,
                FirmwareUpdateState.JumpingToApp,
                FirmwareUpdateState.CleaningUp,
                FirmwareUpdateState.Failed
            },
            [FirmwareUpdateState.Verifying] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.JumpingToApp,
                FirmwareUpdateState.Complete,
                FirmwareUpdateState.CleaningUp,
                FirmwareUpdateState.Failed
            },
            // Terminal leg of the WiFi flow: the WINC image is already flashed and verified,
            // so the only outcomes are a completed update or a reconnect failure. No cleanup
            // path — there is no half-flashed PIC32 application to re-erase.
            [FirmwareUpdateState.ReconnectingAfterFlash] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.Complete,
                FirmwareUpdateState.Failed
            },
            [FirmwareUpdateState.JumpingToApp] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.Complete,
                FirmwareUpdateState.Failed
            },
            // Cleanup (re-erase) runs after a failure in a flash-touching state.
            // It either leaves the device in a clean bootloader state (Recovered)
            // or, if the re-erase itself fails, falls through to Failed.
            [FirmwareUpdateState.CleaningUp] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.Recovered,
                FirmwareUpdateState.Failed
            },
            [FirmwareUpdateState.Complete] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.Idle
            },
            [FirmwareUpdateState.Failed] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.Idle
            },
            // Recovered is a terminal failure state (the update did not install
            // firmware) but the device is safe; it only resets for the next run.
            [FirmwareUpdateState.Recovered] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.Idle
            }
        };

    // The service instance reported as the `sender` of StateChanged, so subscribers keep
    // seeing the public facade rather than this internal collaborator.
    private readonly object _eventSender;

    internal FirmwareUpdateContext(
        object eventSender,
        ILogger logger,
        FirmwareUpdateServiceOptions options)
    {
        _eventSender = eventSender;
        Logger = logger;
        Options = options;
    }

    internal ILogger Logger { get; }

    internal FirmwareUpdateServiceOptions Options { get; }

    /// <summary>
    /// Supplies the extra diagnostic detail appended to a
    /// <see cref="FirmwareUpdateState.WaitingForBootloader"/> timeout message (poll attempts,
    /// requested target, last enumeration error). Owned by the PIC32 flow and wired up by
    /// <see cref="FirmwareUpdateService"/>; null yields the bare timeout message.
    /// </summary>
    internal Func<string>? WaitingForBootloaderTimeoutDetailProvider { get; set; }

    internal FirmwareUpdateState CurrentState { get; private set; } = FirmwareUpdateState.Idle;

    internal string CurrentOperation { get; private set; } = "Idle";

    internal double LastReportedPercent { get; private set; }

    internal event EventHandler<FirmwareUpdateStateChangedEventArgs>? StateChanged;

    internal void ResetProgress() => LastReportedPercent = 0;

    internal void ReportProgress(
        IProgress<FirmwareUpdateProgress>? progress,
        FirmwareUpdateState state,
        double percentComplete,
        string currentOperation,
        long bytesWritten,
        long totalBytes)
    {
        var clampedPercent = Math.Clamp(percentComplete, 0, 100);
        LastReportedPercent = clampedPercent;

        progress?.Report(new FirmwareUpdateProgress
        {
            State = state,
            PercentComplete = clampedPercent,
            CurrentOperation = currentOperation,
            BytesWritten = Math.Max(0, bytesWritten),
            TotalBytes = Math.Max(0, totalBytes)
        });
    }

    /// <param name="nextState">The state to move to.</param>
    /// <param name="operation">A short description of the operation the transition starts or ends.</param>
    /// <param name="flashCondition">
    /// The condition of the PIC32 application flash, supplied on the transitions that end an
    /// operation that did not complete (Failed / Recovered) so observers can read it as a value.
    /// </param>
    internal void TransitionToState(
        FirmwareUpdateState nextState,
        string operation,
        Pic32FlashCondition? flashCondition = null)
    {
        if (CommitTransition(nextState, operation, flashCondition) is { } args)
        {
            LogTransition(args);
            StateChanged?.Invoke(_eventSender, args);
        }
    }

    /// <summary>
    /// Ends a successful operation: moves to <see cref="FirmwareUpdateState.Complete"/> and
    /// reports 100% progress. Observers are isolated (see <see cref="EndOperation"/>): the work on
    /// the device already succeeded, and letting a consumer callback escape would send the caller
    /// into the failure path, which reports a failure that did not happen and then cannot even
    /// reach Failed (Complete → Failed is not a legal transition).
    /// </summary>
    internal void CompleteOperation(
        IProgress<FirmwareUpdateProgress>? progress,
        string operation,
        long bytesWritten,
        long totalBytes)
        => EndOperation(FirmwareUpdateState.Complete, progress, operation, null, 100, bytesWritten, totalBytes);

    /// <summary>
    /// Ends a PIC32 cleanup whose re-erase succeeded: moves to
    /// <see cref="FirmwareUpdateState.Recovered"/> and reports the final progress. Observers are
    /// isolated (see <see cref="EndOperation"/>): the device is clean, and a consumer callback must
    /// not turn that into a half-flashed verdict (Recovered → Failed is not a legal transition).
    /// </summary>
    internal void RecoverOperation(
        IProgress<FirmwareUpdateProgress>? progress,
        string operation,
        Pic32FlashCondition flashCondition,
        double percentComplete,
        long totalBytes)
        => EndOperation(FirmwareUpdateState.Recovered, progress, operation, flashCondition, percentComplete, 0, totalBytes);

    /// <summary>
    /// Ends an operation that did not complete: moves to <see cref="FirmwareUpdateState.Failed"/>
    /// and reports the final progress. Observers are isolated (see <see cref="EndOperation"/>): the
    /// caller is about to surface the real outcome (a <see cref="FirmwareUpdateException"/> carrying
    /// the flash condition and recovery guidance, or the caller's own
    /// <see cref="OperationCanceledException"/>), and a consumer callback must not replace it.
    /// </summary>
    /// <param name="progress">The caller's progress sink, if any.</param>
    /// <param name="operation">Why the operation ended; reported on both the event and the progress.</param>
    /// <param name="flashCondition">The condition of the PIC32 application flash at the end.</param>
    /// <param name="percentComplete">The percentage to freeze the final progress report at.</param>
    /// <param name="totalBytes">The total size of the image the operation was writing.</param>
    internal void FailOperation(
        IProgress<FirmwareUpdateProgress>? progress,
        string operation,
        Pic32FlashCondition flashCondition,
        double percentComplete,
        long totalBytes)
        => EndOperation(FirmwareUpdateState.Failed, progress, operation, flashCondition, percentComplete, 0, totalBytes);

    /// <summary>
    /// The one implementation of a terminal transition (Complete / Recovered / Failed) plus its
    /// final progress report, so the three endings cannot drift apart. Once the transition has
    /// committed, nothing a consumer callback does can change the outcome the caller sees:
    /// <list type="bullet">
    /// <item>Each <see cref="StateChanged"/> subscriber is invoked on its own and a throw is logged
    /// and swallowed. A multicast delegate stops at its first exception, so invoking it whole
    /// would leave every later subscriber without the terminal event — and on a cancel, which
    /// throws no <see cref="FirmwareUpdateException"/>, that event's flash condition is the only
    /// typed signal that the board may be half-flashed.</item>
    /// <item>A throwing progress sink is logged and swallowed, and still runs after a throwing
    /// subscriber.</item>
    /// <item>Every log call after the commit is best-effort (<see cref="Daqifi.Core.Internal.DiagnosticGuard.SafeLog"/>):
    /// the logger is consumer code too, and a provider that throws on the transition line must not
    /// skip the event and the report, or send a successful update into the failure path.</item>
    /// </list>
    /// An illegal transition still throws, before anything is committed or any observer runs.
    /// </summary>
    private void EndOperation(
        FirmwareUpdateState terminalState,
        IProgress<FirmwareUpdateProgress>? progress,
        string operation,
        Pic32FlashCondition? flashCondition,
        double percentComplete,
        long bytesWritten,
        long totalBytes)
    {
        var args = CommitTransition(terminalState, operation, flashCondition);

        if (args != null)
        {
            SafeLog(() => LogTransition(args));
        }

        if (args != null && StateChanged is { } handler)
        {
            foreach (var subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((EventHandler<FirmwareUpdateStateChangedEventArgs>)subscriber)(_eventSender, args);
                }
                catch (Exception ex)
                {
                    SafeLog(() => Logger.LogWarning(
                        ex,
                        "A StateChanged subscriber threw on the {State} transition; the outcome is unchanged.",
                        terminalState));
                }
            }
        }

        try
        {
            ReportProgress(progress, terminalState, percentComplete, operation, bytesWritten, totalBytes);
        }
        catch (Exception ex)
        {
            SafeLog(() => Logger.LogWarning(
                ex,
                "A progress sink threw on the {State} report; the outcome is unchanged.",
                terminalState));
        }
    }

    /// <summary>
    /// Validates and commits a transition, returning the event to raise — or null when
    /// <paramref name="nextState"/> is already the current state, which only refreshes the operation
    /// text and raises nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The transition is not allowed.</exception>
    private FirmwareUpdateStateChangedEventArgs? CommitTransition(
        FirmwareUpdateState nextState,
        string operation,
        Pic32FlashCondition? flashCondition)
    {
        if (CurrentState == nextState)
        {
            CurrentOperation = operation;
            return null;
        }

        if (!AllowedTransitions.TryGetValue(CurrentState, out var allowedStates) ||
            !allowedStates.Contains(nextState))
        {
            throw new InvalidOperationException(
                $"Invalid firmware update transition: {CurrentState} -> {nextState}.");
        }

        var previousState = CurrentState;
        CurrentState = nextState;
        CurrentOperation = operation;

        return new FirmwareUpdateStateChangedEventArgs(previousState, nextState, operation)
        {
            FlashCondition = flashCondition
        };
    }

    private void LogTransition(FirmwareUpdateStateChangedEventArgs args)
    {
        Logger.LogInformation(
            "Firmware update state transition: {PreviousState} -> {CurrentState} ({Operation})",
            args.PreviousState,
            args.CurrentState,
            args.Operation);
    }

    internal void ResetIfTerminalState()
    {
        if (CurrentState is FirmwareUpdateState.Complete
            or FirmwareUpdateState.Failed
            or FirmwareUpdateState.Recovered)
        {
            TransitionToState(FirmwareUpdateState.Idle, "Resetting state for next firmware update operation.");
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/>, retrying up to <paramref name="maximumAttempts"/> times while
    /// <paramref name="isTransient"/> classifies the failure as retryable.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumAttempts"/> is less than 1. Rejected rather than clamped: a caller asking
    /// for zero attempts has a bug, and silently returning success would skip a flash-critical step
    /// (erase, program, verify) with no failure signal at all.
    /// </exception>
    internal async Task ExecuteWithRetryAsync(
        string operation,
        int maximumAttempts,
        TimeSpan retryDelay,
        Func<CancellationToken, Task> action,
        Func<Exception, bool> isTransient,
        CancellationToken cancellationToken)
    {
        if (maximumAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumAttempts),
                maximumAttempts,
                $"Operation '{operation}' requires at least one attempt.");
        }

        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await action(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < maximumAttempts && isTransient(ex))
            {
                Logger.LogWarning(
                    ex,
                    "Operation '{Operation}' failed on attempt {Attempt}/{MaxAttempts}; retrying in {DelayMs} ms.",
                    operation,
                    attempt,
                    maximumAttempts,
                    retryDelay.TotalMilliseconds);

                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task ExecuteWithStateTimeoutAsync(
        FirmwareUpdateState state,
        string operation,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        var timeout = Options.GetStateTimeout(state);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await action(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(BuildStateTimeoutMessage(state, operation, timeout));
        }
    }

    internal async Task<T> ExecuteWithStateTimeoutAsync<T>(
        FirmwareUpdateState state,
        string operation,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var timeout = Options.GetStateTimeout(state);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            return await action(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(BuildStateTimeoutMessage(state, operation, timeout));
        }
    }

    /// <summary>
    /// Reconnects the device's serial transport, polling until it reports connected.
    /// This loop is bounded by the caller's state timeout via <see cref="ExecuteWithStateTimeoutAsync"/>.
    /// </summary>
    internal async Task WaitForSerialReconnectAsync(
        IStreamingDevice device,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (device.IsConnected)
            {
                return;
            }

            try
            {
                device.Connect();
                if (device.IsConnected)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Serial reconnect attempt failed.");
            }

            await Task.Delay(Options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static void EnsureDeviceConnected(IStreamingDevice device)
    {
        if (!device.IsConnected)
        {
            throw new InvalidOperationException("Device must be connected before starting firmware update.");
        }
    }

    // failureSubject names the operation that failed, so the message is honest about what
    // the caller actually ran. Diagnostics pass their own subject: a health check or soft
    // reset must not report "Firmware update failed" to a consumer (e.g. a recovery dialog)
    // that deliberately probed the bootloader *instead of* starting an update.
    internal FirmwareUpdateException CreateFirmwareUpdateException(
        FirmwareUpdateState failedState,
        string failedOperation,
        Exception exception,
        Pic32FlashCondition flashCondition,
        string? recoveryGuidance = null,
        string failureSubject = "Firmware update")
    {
        if (exception is FirmwareUpdateException firmwareUpdateException)
        {
            // Already a fully-contextualized firmware exception (carries its own
            // state, operation and guidance), so it reaches the caller as thrown.
            // The bootloader search raises one (AmbiguousBootloaderTargetException,
            // #837); nothing past it does, so the caller-supplied guidance below
            // never has to be merged in here.
            return firmwareUpdateException;
        }

        var message = $"{failureSubject} failed in state '{failedState}' while {failedOperation}.";

        return new FirmwareUpdateException(
            failedState,
            failedOperation,
            message,
            recoveryGuidance ?? BuildRecoveryGuidance(failedState),
            exception)
        {
            FlashCondition = flashCondition
        };
    }

    internal static string BuildRecoveryGuidance(FirmwareUpdateState failedState)
    {
        return failedState switch
        {
            FirmwareUpdateState.PreparingDevice =>
                "Ensure the device is connected over USB and not currently busy streaming.",
            FirmwareUpdateState.WaitingForBootloader =>
                "The device did not enter bootloader mode. Try unplugging/replugging USB, then retry.",
            FirmwareUpdateState.Connecting =>
                "Bootloader was found but HID connection failed. Check USB cable stability and retry.",
            FirmwareUpdateState.ErasingFlash =>
                "Flash erase failed. Retry update; if this persists, power-cycle the device and re-enter bootloader mode.",
            FirmwareUpdateState.Programming =>
                "Programming failed. Retry update while keeping USB connected; device may still be recoverable in bootloader mode.",
            FirmwareUpdateState.Verifying =>
                "Flash verification failed — the device's flash CRC did not match the firmware image. " +
                "Retry the update and confirm the expected firmware package was selected.",
            FirmwareUpdateState.ReconnectingAfterFlash =>
                "The firmware was flashed and verified successfully; only reconnecting to the device " +
                "afterwards timed out. Unplug and replug USB, then reconnect — the update itself does " +
                "not need to be re-run.",
            FirmwareUpdateState.JumpingToApp =>
                "The device did not return to application mode. Power-cycle the device and reconnect.",
            _ =>
                "Retry the update. If repeated failures occur, reconnect the device and attempt manual bootloader recovery."
        };
    }

    internal static string FormatExceptionSummary(Exception exception)
    {
        var builder = new StringBuilder();
        var current = exception;
        var firstSegment = true;

        while (current != null)
        {
            if (!firstSegment)
            {
                builder.Append(" | Inner ");
            }

            builder.Append(current.GetType().Name);
            builder.Append(": ");
            builder.Append(current.Message);
            current = current.InnerException;
            firstSegment = false;
        }

        return builder.ToString();
    }

    private string BuildStateTimeoutMessage(
        FirmwareUpdateState state,
        string operation,
        TimeSpan timeout)
    {
        var message =
            $"State '{state}' timed out while attempting to {operation} after {timeout.TotalSeconds:F1} seconds.";

        if (state != FirmwareUpdateState.WaitingForBootloader ||
            WaitingForBootloaderTimeoutDetailProvider is not { } detailProvider)
        {
            return message;
        }

        return $"{message} {detailProvider()}";
    }
}
