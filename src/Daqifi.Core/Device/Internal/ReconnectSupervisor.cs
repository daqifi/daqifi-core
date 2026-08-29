using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// Rebuilds a device session that was lost, on the policy the device was given (issue #379).
/// </summary>
/// <remarks>
/// <para>
/// Extracted verbatim from <see cref="DaqifiDevice"/> (issue #480). The device keeps the public
/// surface — <c>ReconnectOptions</c>, <c>IsReconnecting</c>, <c>CancelReconnect</c> and the three
/// events — and delegates the whole of the mechanism here: the single-flight guard, the session
/// epoch, the backoff ladder, the supersede/unwind protocol, and the give-up report.
/// </para>
/// <para>
/// <b>The session epoch is the load-bearing idea.</b> A caller's <c>Connect</c>, <c>Disconnect</c>
/// or <c>Dispose</c> bumps it; the loop captures it when it starts and re-checks it before every
/// step that touches device state. That is what lets a teardown return immediately instead of
/// blocking on a loop that may be calling back into it, and it is why cancellation alone is not
/// enough — an attempt already inside a blocking transport connect runs to completion regardless,
/// so the loop has to be able to discover afterwards that the session it just built is not wanted.
/// </para>
/// </remarks>
internal sealed class ReconnectSupervisor
{
    private readonly IReconnectHost _host;

    private ReconnectOptions _options = new();

    // 0 = idle, 1 = a reconnect loop is running. Guards against a second loop being started by
    // the Lost that a failing attempt's own teardown can produce.
    private int _reconnectRunning;

    // Volatile: written by the thread that starts a loop and by the loop's own cleanup, read by
    // Cancel from any thread. A stale read only ever delays a cancellation, never corrupts one —
    // the epoch check is what actually stops the loop — but there is no reason to leave even that
    // on the table.
    private volatile CancellationTokenSource? _reconnectCts;

    // Bumped by every caller-issued Connect/Disconnect/Dispose. The reconnect loop captures it
    // when it starts and re-checks before each step that touches device state; a change means
    // the caller has moved on and the loop must unwind. This is what keeps the loop from
    // re-opening a transport the caller just closed, without Disconnect() having to block on a
    // loop that may be calling back into it.
    private int _sessionEpoch;

    internal ReconnectSupervisor(IReconnectHost host) => _host = host;

    /// <summary>
    /// The policy for re-establishing a session after <see cref="ConnectionStatus.Lost"/>.
    /// Never null — the device's public property rejects that before it reaches here.
    /// </summary>
    internal ReconnectOptions Options
    {
        get => _options;
        set => _options = value;
    }

    /// <summary>Whether a reconnect loop is running right now.</summary>
    internal bool IsRunning => Volatile.Read(ref _reconnectRunning) != 0;

    /// <summary>
    /// Stops any reconnect in progress. Safe to call at any time, including when nothing is
    /// reconnecting. The loop unwinds at its next checkpoint — it does not interrupt a connect
    /// attempt already in flight — and returns immediately rather than waiting for it.
    /// </summary>
    internal void Cancel()
    {
        try
        {
            _reconnectCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The loop finished and disposed its own token source. Nothing to cancel.
        }
    }

    /// <summary>
    /// Cancels any reconnect in progress <i>and</i> declares the session it was rebuilding
    /// obsolete, so the unwinding loop leaves the caller's new session strictly alone.
    /// </summary>
    internal void Supersede()
    {
        Interlocked.Increment(ref _sessionEpoch);
        Cancel();
    }

    /// <summary>
    /// Records what has to be recorded at the instant a drop is detected, and reports the epoch
    /// that drop belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The epoch is read <i>before</i> the caller raises <see cref="ConnectionStatus.Lost"/>,
    /// because a handler is free to call Connect or Disconnect synchronously from inside it; if
    /// it does, this drop must not start a reconnect for a session the caller has already moved
    /// on from.
    /// </para>
    /// <para>
    /// The snapshot is taken for the same reason: raising Lost runs consumer handlers
    /// synchronously on this thread, and a handler is entirely entitled to start tearing the
    /// device down — by which point what the session looked like is no longer recoverable. It is
    /// a no-op unless reconnect is enabled, so the default path does exactly what it did before.
    /// Skipped while a reconnect is already running: a drop during an attempt would otherwise
    /// overwrite the snapshot of the session being restored with the empty half-built one, and
    /// the loop would go on to restore nothing.
    /// </para>
    /// </remarks>
    /// <returns>The session epoch as it stood when the drop was detected.</returns>
    internal int PrepareForDrop()
    {
        var epochAtDrop = Volatile.Read(ref _sessionEpoch);

        if (Options.Enabled && !IsRunning)
        {
            try
            {
                _host.CaptureSessionSnapshot();
            }
            catch (Exception ex)
            {
                SafeLog(() => _host.Logger.LogWarning(
                    ex, "[Reconnect] Capturing the session state after a drop failed; the session cannot be restored."));
            }
        }

        return epochAtDrop;
    }

    /// <summary>
    /// Starts the reconnect loop on a background thread, if the policy allows one and none is
    /// already running.
    /// </summary>
    /// <remarks>
    /// Called from the transport's status callback, which runs on the reader loop or the
    /// liveness timer — so the work is handed to the thread pool rather than done inline.
    /// </remarks>
    /// <param name="expectedEpoch">
    /// The session epoch observed before the drop was announced. A mismatch means a caller
    /// connected or disconnected in the meantime — including from inside their own
    /// <c>StatusChanged</c> handler — and this drop is no longer theirs to recover from.
    /// </param>
    internal void BeginIfEnabled(int expectedEpoch)
    {
        if (!Options.Enabled || !_host.HasTransport || _host.IsDisposed || _host.IsDisconnecting)
        {
            return;
        }

        // Only ever start from a device that is actually sitting on a lost connection with the
        // session it was lost from still current.
        if (_host.Status != ConnectionStatus.Lost || Volatile.Read(ref _sessionEpoch) != expectedEpoch)
        {
            return;
        }

        // One loop at a time. A failing attempt tears the transport down again, which can
        // produce another Lost; without this that would fork a second loop racing the first.
        if (Interlocked.CompareExchange(ref _reconnectRunning, 1, 0) != 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _reconnectCts = cts;

        var epoch = Volatile.Read(ref _sessionEpoch);
        var options = Options;

        _ = Task.Run(async () =>
        {
            var wasCanceled = false;

            try
            {
                await RunReconnectLoopAsync(options, epoch, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The loop handles its own failures; anything escaping is a bug in it, and must
                // not become an unobserved task exception.
                SafeLog(() => _host.Logger.LogError(ex, "[Reconnect] The reconnect loop terminated unexpectedly."));
            }
            finally
            {
                wasCanceled = cts.IsCancellationRequested;

                // Clear the token source before releasing the running flag, so a loop started
                // by the next drop cannot have its own source nulled out from under it.
                _reconnectCts = null;
                cts.Dispose();
                Interlocked.Exchange(ref _reconnectRunning, 0);
            }

            // A drop that landed in the moments between this loop finishing its work and
            // releasing the running flag would have been skipped by the single-flight guard,
            // stranding the device on Lost with nothing trying to bring it back. Pick it up.
            // Exhausting the attempts settles on Failed, and cancellation is excluded here, so
            // neither can retrigger.
            if (!wasCanceled)
            {
                BeginIfEnabled(Volatile.Read(ref _sessionEpoch));
            }
        });
    }

    /// <summary>
    /// Attempts, with backoff, to rebuild the session that was just lost.
    /// </summary>
    /// <param name="options">
    /// The policy as it stood when the drop was detected. Assigning a new
    /// <see cref="ReconnectOptions"/> mid-flight therefore leaves this loop on the terms it
    /// started under and applies from the next drop; mutating the instance already assigned
    /// does reach it, since the loop holds that same object.
    /// </param>
    /// <param name="epoch">The session epoch at the time of the drop.</param>
    /// <param name="cancellationToken">Cancelled by <see cref="Cancel"/>.</param>
    private async Task RunReconnectLoopAsync(
        ReconnectOptions options,
        int epoch,
        CancellationToken cancellationToken)
    {
        // Elapsed on the device's clock (issue #637): this is a duration reported to the
        // caller on ReconnectedEventArgs, and the backoff ladder below is counted in seconds,
        // so a test can walk a full ladder with a fake clock instead of waiting one out.
        var clock = _host.TimeProvider;
        var startedAt = clock.GetTimestamp();
        Exception? lastError = null;
        var attempt = 0;

        SafeLog(() => _host.Logger.LogInformation(
            "[Reconnect] Device '{DeviceName}' lost its connection; reconnecting (up to {MaxAttempts} attempt(s)).",
            _host.Name,
            options.MaxAttempts));

        while (attempt < options.MaxAttempts)
        {
            attempt++;

            if (IsSessionStale(epoch) || cancellationToken.IsCancellationRequested)
            {
                ReportReconnectStopped(epoch, attempt - 1, lastError, wasCanceled: true);
                return;
            }

            var delay = options.CalculateDelay(attempt);
            _host.RaiseReconnectAttempt(new ReconnectAttemptEventArgs(
                attempt, options.MaxAttempts, delay, lastError));

            try
            {
                // Tear down what is left of the dead session first: the producer and consumer
                // are still bound to a stream that is gone, and Connect() only rebuilds them
                // once they have been nulled. Reported as Retrying, not Disconnected — nobody
                // asked for this teardown.
                _host.DisconnectCore(ConnectionStatus.Retrying);

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, clock, cancellationToken).ConfigureAwait(false);
                }

                if (IsSessionStale(epoch))
                {
                    ReportReconnectStopped(epoch, attempt, lastError, wasCanceled: true);
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                _host.ConnectCore();

                // Connect() blocks for as long as opening the port takes, so a caller can — and
                // on a slow serial open, will — land squarely in the middle of it. There is now
                // a live transport and a running reader that this loop built, so bailing out
                // here is not enough on its own: if the caller wants the device down, the
                // session has to be taken back down with it.
                if (AbandonIfSuperseded(epoch, attempt, lastError))
                {
                    return;
                }

                await _host.InitializeAsync(cancellationToken).ConfigureAwait(false);

                var streamingResumed = await _host.RestoreSessionSnapshotAsync(
                    options, cancellationToken).ConfigureAwait(false);

                // Same again before declaring victory: initialization and restore are several
                // seconds of SCPI round-trips, and a session the caller has since disowned must
                // not be handed to them as a successful reconnect.
                if (AbandonIfSuperseded(epoch, attempt, lastError))
                {
                    return;
                }

                SafeLog(() => _host.Logger.LogInformation(
                    "[Reconnect] Device '{DeviceName}' reconnected on attempt {Attempt}.", _host.Name, attempt));

                _host.RaiseReconnected(new ReconnectedEventArgs(
                    attempt, clock.GetElapsedTime(startedAt), streamingResumed));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ReportReconnectStopped(epoch, attempt, lastError, wasCanceled: true);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                SafeLog(() => _host.Logger.LogWarning(
                    ex,
                    "[Reconnect] Attempt {Attempt} of {MaxAttempts} to reconnect device '{DeviceName}' failed.",
                    attempt,
                    options.MaxAttempts,
                    _host.Name));
            }
        }

        ReportReconnectStopped(epoch, attempt, lastError, wasCanceled: false);
    }

    /// <summary>
    /// Checks whether a caller has superseded the session this loop is rebuilding and, if so,
    /// unwinds whatever the loop has already brought up before reporting that it stopped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called at the points where the loop is holding a live session of its own making — after
    /// <see cref="IReconnectHost.ConnectCore"/>, and again once initialization and restore are
    /// done. Both are preceded by long blocking work, which is exactly when a caller's
    /// <c>Disconnect</c> lands, so a check beforehand cannot substitute for this one.
    /// </para>
    /// <para>
    /// What "unwind" means depends on what the caller wanted, which is why the epoch alone is
    /// not enough to decide it. After a <c>Disconnect</c> or a disposal the live session is the
    /// loop's own doing and is torn straight back down — leaving it up is a device the caller
    /// closed quietly coming back to life. After a caller's own <c>Connect</c> the session
    /// belongs to them, and tearing it down would be the same bug in reverse, so it is left
    /// alone and only logged.
    /// </para>
    /// </remarks>
    /// <returns><c>true</c> if the loop must stop.</returns>
    private bool AbandonIfSuperseded(int epoch, int attempt, Exception? lastError)
    {
        if (!IsSessionStale(epoch))
        {
            return false;
        }

        if (_host.CallerWantsDisconnected || _host.IsDisposed)
        {
            SafeLog(() => _host.Logger.LogInformation(
                "[Reconnect] Device '{DeviceName}' was disconnected while a reconnect attempt was in flight; "
                + "closing the connection the attempt had established.",
                _host.Name));

            try
            {
                _host.DisconnectCore(ConnectionStatus.Disconnected);
            }
            catch (Exception ex)
            {
                // Best-effort unwind of an abandoned attempt. A transport already disposed out
                // from under us can throw here, and there is nothing left to salvage by
                // letting that escape into the loop's retry logic.
                SafeLog(() => _host.Logger.LogDebug(
                    ex, "[Reconnect] Closing the abandoned reconnect attempt's connection failed."));
            }
        }
        else
        {
            SafeLog(() => _host.Logger.LogWarning(
                "[Reconnect] Device '{DeviceName}' was reconnected by its caller while an automatic "
                + "reconnect attempt was in flight; leaving the caller's connection alone.",
                _host.Name));
        }

        ReportReconnectStopped(epoch, attempt, lastError, wasCanceled: true);
        return true;
    }

    /// <summary>
    /// True once a caller-issued connect, disconnect or disposal has superseded the session the
    /// reconnect loop was started for.
    /// </summary>
    /// <remarks>
    /// Deliberately does not consult the device's <c>_isDisconnecting</c> flag: the loop's own
    /// teardown between attempts sets that flag, so reading it here would make the loop consider
    /// itself stale. A caller-issued <c>Disconnect</c> bumps the epoch before it sets the flag,
    /// which is what this actually needs to see.
    /// </remarks>
    private bool IsSessionStale(int epoch) =>
        _host.IsDisposed || Volatile.Read(ref _sessionEpoch) != epoch;

    /// <summary>
    /// Settles the device after a reconnect loop that did not restore the session, and reports
    /// why.
    /// </summary>
    /// <remarks>
    /// Exhausting the attempts is terminal and loud: <see cref="ConnectionStatus.Failed"/>, a
    /// logged error, and an <c>ErrorOccurred</c> raise. Being cancelled is neither — the caller
    /// asked for it — so the device is simply left reporting <see cref="ConnectionStatus.Lost"/>,
    /// which is the truth. Neither touches the device's status at all once the session is stale,
    /// since by then the status belongs to whatever the caller did next.
    /// </remarks>
    private void ReportReconnectStopped(int epoch, int attemptsMade, Exception? lastError, bool wasCanceled)
    {
        if (!IsSessionStale(epoch))
        {
            // An attempt can fail after the transport is back up (a re-initialization that
            // times out, say), so tear the half-built session down rather than leaving a live
            // handle and a running reader behind a terminal status.
            _host.DisconnectCore(wasCanceled ? ConnectionStatus.Lost : ConnectionStatus.Failed);
        }

        if (wasCanceled)
        {
            SafeLog(() => _host.Logger.LogInformation(
                "[Reconnect] Reconnection of device '{DeviceName}' was cancelled after {AttemptsMade} attempt(s).",
                _host.Name,
                attemptsMade));
        }
        else
        {
            SafeLog(() => _host.Logger.LogError(
                lastError,
                "[Reconnect] Device '{DeviceName}' could not be reconnected after {AttemptsMade} attempt(s); giving up.",
                _host.Name,
                attemptsMade));

            // Terminal failure has to be impossible to miss, so it goes to the device error
            // surface as well as to this group's own event (issue #379 / #378).
            _host.RaiseDeviceError(
                DeviceErrorSource.Reconnect,
                new DeviceReconnectFailedException(_host.Name, attemptsMade, lastError));
        }

        _host.RaiseReconnectFailed(new ReconnectFailedEventArgs(attemptsMade, lastError, wasCanceled));
    }

    /// <summary>
    /// Runs a log call, swallowing anything it throws — a logger is not permitted to take down a
    /// reconnect, the same isolation <c>DaqifiDevice.SafeLog</c> gives the rest of the device.
    /// </summary>
    private static void SafeLog(Action logAction)
    {
        try
        {
            logAction();
        }
        catch
        {
            // A logger that throws is not permitted to take down device operation.
        }
    }
}
