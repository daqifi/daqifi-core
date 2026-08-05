using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device.Internal
{
    /// <summary>
    /// What a lifecycle operation does when it cannot have the gate to itself. Running anyway is
    /// deliberately not an option: the whole point of the gate is that two threads must never drive
    /// the transport at once, and a guarantee with a "proceed regardless" branch is not a guarantee.
    /// </summary>
    internal enum LifecycleContention
    {
        /// <summary>
        /// Give up and throw rather than run alongside. For <see cref="DaqifiDevice.Connect"/>:
        /// nothing has been opened, so failing costs the caller a retry and nothing else.
        /// </summary>
        Fail,

        /// <summary>
        /// Give up and report it, leaving the operation in flight alone. For
        /// <see cref="DaqifiDevice.Disconnect"/>: the caller is told nothing was torn down rather
        /// than being blocked forever behind a holder that may never return.
        /// </summary>
        Abandon
    }

    /// <summary>
    /// Serializes connect against disconnect, on both the synchronous and the asynchronous paths,
    /// extracted from <see cref="DaqifiDevice"/> so the device delegates rather than hosts it
    /// (issue #379).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Automatic reconnection (issue #379) introduced a second thread that opens and closes the
    /// transport, and cancellation is not synchronization: <c>SupersedeReconnect</c> asks the loop
    /// to stop and returns immediately, but a loop already inside a blocking <c>Connect()</c>
    /// cannot be interrupted and will run to completion. Without this, a caller's
    /// <see cref="DaqifiDevice.Disconnect"/> could be opening and closing the same serial port
    /// concurrently, and both threads could build and start a message consumer — leaving two
    /// readers on one stream, the framing corruption the device refuses to risk anywhere else.
    /// </para>
    /// <para>
    /// Narrow on purpose. This is an internal lifecycle invariant — the device never drives its own
    /// transport from two threads at once — and deliberately <b>not</b> the general per-device
    /// operation serialization of issue #342, which has to decide ordering across the whole public
    /// API and interacts with <c>_textExchangeLock</c>. Nothing here changes what any public method
    /// does when uncontended.
    /// </para>
    /// <para>
    /// A semaphore rather than a monitor because <see cref="DaqifiDevice.ConnectAsync"/> and
    /// <see cref="DaqifiDevice.DisconnectAsync"/> hold it across <c>await</c>, which a monitor
    /// cannot do — its continuation may resume on a different thread. Semaphores are not reentrant,
    /// so re-entry is tracked separately by <see cref="_isInsideLifecycleOperation"/>.
    /// </para>
    /// <para>
    /// The semaphore is deliberately never disposed, matching the device's own resource teardown,
    /// which has never disposed it. The <see cref="ObjectDisposedException"/> handlers below are
    /// therefore defensive rather than reachable today; they are kept so a future teardown that
    /// does dispose it degrades to "nothing left to serialize against" instead of throwing out of
    /// a disconnect.
    /// </para>
    /// </remarks>
    internal sealed class LifecycleGate
    {
        private readonly ILogger _logger;
        private readonly Func<string> _deviceName;
        private readonly Func<TimeSpan> _connectTimeout;
        private readonly Func<TimeSpan> _teardownTimeout;

        /// <summary>
        /// The gate itself. One permit: a lifecycle operation either has it or waits for it.
        /// </summary>
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

        /// <summary>
        /// True while the current logical flow already holds <see cref="_lifecycleLock"/>.
        /// </summary>
        /// <remarks>
        /// Both connect and disconnect raise <see cref="DaqifiDevice.StatusChanged"/> from inside
        /// their critical section, and a consumer handler calling
        /// <see cref="DaqifiDevice.Disconnect"/> from there is re-entry on the same flow — which
        /// runs nested today with no lock at all and must keep working rather than deadlock against
        /// a non-reentrant semaphore. <see cref="AsyncLocal{T}"/> rather than a thread id so it
        /// survives an <c>await</c> resuming on another thread, the same technique
        /// <c>_isInsideTextExchange</c> already uses on the device.
        /// </remarks>
        private readonly AsyncLocal<bool> _isInsideLifecycleOperation = new();

        /// <summary>
        /// Creates a gate that logs against the owning device and reads its contention timeouts.
        /// </summary>
        /// <param name="logger">The device's logger; contention reports go here.</param>
        /// <param name="deviceName">
        /// Reads the owning device's current name for those reports. A delegate rather than a
        /// captured string because the name can change during the device's lifetime, and a report
        /// naming the wrong device is worse than one naming none.
        /// </param>
        /// <param name="connectTimeout">
        /// Reads <see cref="DaqifiDevice.LifecycleLockTimeout"/> at the moment of contention.
        /// </param>
        /// <param name="teardownTimeout">
        /// Reads <see cref="DaqifiDevice.TeardownLockTimeout"/> at the moment of contention.
        /// </param>
        /// <remarks>
        /// The two timeouts arrive as delegates, not values, and that is load-bearing rather than
        /// stylistic. They are <c>virtual</c> on the device precisely so a test can shorten them,
        /// and a test subclass sets its override through an <c>init</c> property — which runs
        /// <i>after</i> the base constructor that builds this gate. Reading them here would capture
        /// the base defaults (10 s / 30 s) and silently ignore every override.
        /// </remarks>
        internal LifecycleGate(
            ILogger logger,
            Func<string> deviceName,
            Func<TimeSpan> connectTimeout,
            Func<TimeSpan> teardownTimeout)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _deviceName = deviceName ?? throw new ArgumentNullException(nameof(deviceName));
            _connectTimeout = connectTimeout ?? throw new ArgumentNullException(nameof(connectTimeout));
            _teardownTimeout = teardownTimeout ?? throw new ArgumentNullException(nameof(teardownTimeout));
        }

        /// <summary>
        /// The wait a contention policy allows, and what to do when it runs out.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The two callers want opposite things from contention, so neither a shared timeout nor a
        /// shared fallback would suit both.
        /// </para>
        /// <para>
        /// <b><see cref="DaqifiDevice.Connect"/> fails.</b> It waits
        /// <see cref="DaqifiDevice.LifecycleLockTimeout"/> and then throws. Opening a second
        /// connection alongside one already in flight is exactly what this gate exists to prevent —
        /// both threads would find no message consumer, both would build and start one, and the
        /// loser's reader would be left running on the same stream, silently corrupting frame
        /// boundaries for the rest of the session. A caller who gets a
        /// <see cref="TimeoutException"/> instead has lost nothing: no handle was opened, no state
        /// changed, and they can try again.
        /// </para>
        /// <para>
        /// <b><see cref="DaqifiDevice.Disconnect"/> abandons.</b> It waits
        /// <see cref="DaqifiDevice.TeardownLockTimeout"/> — far longer, because a teardown that
        /// gives up early is a teardown that did not happen — and then reports that it did not run,
        /// leaving the holder alone. It must not throw (<c>Dispose</c> depends on it) and must not
        /// run alongside (that is the corruption above). An unbounded wait is not an option either:
        /// <c>SerialPort.Open</c> is called synchronously with no timeout and can wedge in
        /// uncancellable native I/O — a hazard this codebase already knows well enough to have
        /// built a process-wide port quarantine around it in
        /// <see cref="Daqifi.Core.Device.Discovery.SerialDeviceFinder"/> — so waiting on it forever
        /// would turn <c>Dispose</c> into a permanent block. Abandoning the stuck operation is the
        /// house answer to uncancellable native I/O here.
        /// </para>
        /// </remarks>
        private TimeSpan ContentionWait(LifecycleContention onContention) =>
            onContention == LifecycleContention.Abandon ? _teardownTimeout() : _connectTimeout();

        /// <summary>
        /// Builds the failure for a connect that could not have the gate to itself.
        /// </summary>
        private TimeoutException LifecycleTimeout(TimeSpan timeout)
        {
            SafeLog(() => _logger.LogError(
                "[Lifecycle] Device '{DeviceName}' could not take the connect/disconnect lock "
                + "within {TimeoutSeconds}s; refusing to connect alongside the operation in flight.",
                _deviceName(),
                timeout.TotalSeconds));

            return new TimeoutException(
                $"Device '{_deviceName()}' could not start connecting within "
                + $"{timeout.TotalSeconds:0.#}s because another connect or disconnect "
                + "was still in progress. Nothing was opened; retry once it has finished.");
        }

        /// <summary>Reports a teardown that gave up waiting for a stuck lifecycle operation.</summary>
        private void LogAbandonedTeardown(TimeSpan timeout) =>
            SafeLog(() => _logger.LogError(
                "[Lifecycle] Device '{DeviceName}' could not take the connect/disconnect lock "
                + "within {TimeoutSeconds}s, so nothing was torn down. A connect is most likely "
                + "wedged in uncancellable native I/O; it will release its own session when it "
                + "returns.",
                _deviceName(),
                timeout.TotalSeconds));

        /// <summary>
        /// Runs a lifecycle operation under <see cref="_lifecycleLock"/>, never alongside another.
        /// See <see cref="ContentionWait"/> for the per-policy semantics.
        /// </summary>
        /// <returns><c>true</c> if the operation ran; <c>false</c> if the wait was abandoned.</returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="operation"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="TimeoutException">
        /// Thrown when <paramref name="onContention"/> is <see cref="LifecycleContention.Fail"/> and
        /// another lifecycle operation held the gate for the whole timeout.
        /// </exception>
        internal bool Run(Action operation, LifecycleContention onContention)
        {
            // Validated before anything else so a null delegate is reported as misuse rather than
            // as a NullReferenceException from one of the three call sites below, and so it can
            // never take the gate on its way to failing. Matches the constructor's guards and the
            // entry-point guards on the sibling collaborators.
            ArgumentNullException.ThrowIfNull(operation);

            // Re-entry from inside the critical section (a StatusChanged handler calling back in)
            // proceeds without acquiring, exactly as a reentrant monitor would.
            if (_isInsideLifecycleOperation.Value)
            {
                operation();
                return true;
            }

            var timeout = ContentionWait(onContention);
            var acquired = false;

            try
            {
                acquired = _lifecycleLock.Wait(timeout);
            }
            catch (ObjectDisposedException)
            {
                // Disposed underneath us; there is nothing left to serialize against.
                operation();
                return true;
            }

            if (!acquired)
            {
                if (onContention == LifecycleContention.Abandon)
                {
                    LogAbandonedTeardown(timeout);
                    return false;
                }

                throw LifecycleTimeout(timeout);
            }

            _isInsideLifecycleOperation.Value = true;
            try
            {
                operation();
                return true;
            }
            finally
            {
                _isInsideLifecycleOperation.Value = false;
                ReleaseLifecycleLock();
            }
        }

        /// <inheritdoc cref="Run"/>
        /// <remarks>
        /// Being an <c>async</c> method, the <see cref="ArgumentNullException"/> surfaces on the
        /// returned task rather than at the call, which is the framework's own convention for
        /// async argument validation and is what every call site here observes anyway.
        /// </remarks>
        internal async Task<bool> RunAsync(
            Func<Task> operation,
            LifecycleContention onContention,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(operation);

            if (_isInsideLifecycleOperation.Value)
            {
                await operation().ConfigureAwait(false);
                return true;
            }

            var timeout = ContentionWait(onContention);
            var isTeardown = onContention == LifecycleContention.Abandon;
            var acquired = false;

            // A teardown's token is NOT allowed to govern this wait. Issue #341 defines what the
            // token means for DisconnectAsync — it shortens the courtesy wait for an in-flight
            // command exchange, never aborts the disconnect, and never surfaces as an
            // OperationCanceledException — and this gate is a second, later wait that contract
            // never covered. Passing the token here made a cancelled DisconnectAsync skip teardown
            // altogether and report Disconnected with the transport still open and the message
            // pumps still running; and because SemaphoreSlim throws for an already-cancelled token
            // even when the semaphore is free, that happened on every cancelled disconnect, not
            // just a contended one. The wait stays bounded by TeardownLockTimeout, which is what
            // protects against a genuinely wedged holder (issue #379). The token still reaches
            // AcquireTextExchangeLockForTeardownAsync inside the teardown, where it means what
            // #341 says it means.
            //
            // The connect path is the opposite case and does honour the token: ConnectAsync is
            // documented to be abandonable and to throw OperationCanceledException.
            var acquireToken = isTeardown ? CancellationToken.None : cancellationToken;

            try
            {
                acquired = await _lifecycleLock.WaitAsync(timeout, acquireToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                await operation().ConfigureAwait(false);
                return true;
            }

            if (!acquired)
            {
                if (isTeardown)
                {
                    LogAbandonedTeardown(timeout);
                    return false;
                }

                throw LifecycleTimeout(timeout);
            }

            _isInsideLifecycleOperation.Value = true;
            try
            {
                await operation().ConfigureAwait(false);
                return true;
            }
            finally
            {
                _isInsideLifecycleOperation.Value = false;
                ReleaseLifecycleLock();
            }
        }

        private void ReleaseLifecycleLock()
        {
            try
            {
                _lifecycleLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // Raced a Dispose that already tore the semaphore down.
            }
        }

        /// <summary>
        /// Runs a logging call, swallowing any exception a misbehaving <see cref="ILogger"/> throws.
        /// A consumer-supplied logger must never affect device operation — least of all here, where
        /// the only logging happens on a path that is already reporting trouble. Mirrors
        /// <c>DaqifiDevice.SafeLog</c>.
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
}
