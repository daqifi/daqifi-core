using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Transport;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device.Internal
{
    /// <summary>
    /// Runs a text (SCPI) exchange on the device's stream: takes the operation lock, swaps the
    /// protobuf consumer out for a line-based one, collects the reply lines, and puts everything
    /// back — extracted from <see cref="DaqifiDevice"/> so the device delegates rather than hosts it
    /// (issue #344).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the primitive nearly every non-streaming device operation is built on: the SD card
    /// operations, the diagnostics, the LAN chip info and the confirming administration commands all
    /// reach it through <see cref="IDeviceOperationHost.ExecuteTextCommandAsync"/>. It is also the
    /// most order-sensitive code in the device — the prepare/finalize pairing, the outbound drain,
    /// the stale-line boundary and the consumer restart each close a specific reported defect, and
    /// the comments below say which. Read them before moving anything.
    /// </para>
    /// <para>
    /// The engine holds no state of its own beyond its host. Everything it coordinates —
    /// the operation lock, the re-entrancy flag, the consumer, the transport — belongs to the device
    /// and is reached through <see cref="ITextExchangeHost"/>, whose remarks explain why three of
    /// those members must stay synchronous.
    /// </para>
    /// </remarks>
    internal sealed class TextExchangeEngine
    {
        private readonly ITextExchangeHost _host;

        internal TextExchangeEngine(ITextExchangeHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>
        /// Temporarily pauses the protobuf message consumer to allow raw byte access to the
        /// underlying transport stream. The consumer is restored when the returned action completes
        /// or is disposed.
        /// </summary>
        /// <param name="rawAction">
        /// An async function that receives the transport stream and performs raw I/O.
        /// The protobuf consumer will not read from the stream while this action is executing.
        /// </param>
        /// <param name="cancellationToken">A cancellation token to observe.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the device has no transport-based connection.</exception>
        internal async Task ExecuteRawCaptureAsync(
            Func<Stream, CancellationToken, Task> rawAction,
            CancellationToken cancellationToken = default)
        {
            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            var transport = _host.Transport;
            if (transport == null)
            {
                throw new InvalidOperationException("ExecuteRawCaptureAsync requires a transport-based connection.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Stop the protobuf consumer so it doesn't compete for stream bytes
                SuspendInboundConsumer();

                // Hand the stream to the caller for raw I/O
                await rawAction(transport.Stream, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                RestartMessageConsumerAfterSwap();
            }
        }

        /// <summary>
        /// Executes a text-based command by temporarily switching from the protobuf consumer to a
        /// line-based text consumer, collecting text responses, then restoring the protobuf consumer.
        /// </summary>
        /// <remarks>
        /// The parameter contract — including what the prepare and finalize phases guarantee — is
        /// documented on <see cref="DaqifiDevice.ExecuteTextCommandAsync(Action, int, int, CancellationToken, Func{CancellationToken, Task}, Func{Task})"/>,
        /// the seam callers actually use.
        /// </remarks>
        internal async Task<IReadOnlyList<string>> ExecuteAsync(
            Func<CancellationToken, Task>? prepareAsync,
            Func<Task>? finalizeAsync,
            Func<CancellationToken, Task> setupActionAsync,
            int responseTimeoutMs,
            int completionTimeoutMs,
            CancellationToken cancellationToken)
        {
            if (responseTimeoutMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(responseTimeoutMs), responseTimeoutMs, "Timeout must be positive.");
            if (completionTimeoutMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(completionTimeoutMs), completionTimeoutMs, "Timeout must be positive.");

            cancellationToken.ThrowIfCancellationRequested();

            // Async-context re-entrancy detection: a setupAction that calls
            // ExecuteTextCommandAsync on the same device would corrupt the
            // consumer swap mid-flight. Surface as a clean exception rather
            // than wedging on the operation lock forever.
            // AsyncLocal flows across await thread hops so this catches
            // re-entry even when the inner call resumes on a different
            // thread than the outer call.
            if (_host.IsInsideTextExchange)
            {
                throw new InvalidOperationException(
                    "ExecuteTextCommandAsync is not re-entrant on the same device; "
                    + "do not call it from inside a setupAction callback.");
            }

            // The exchange runs under the device's operation lock. A flow that already owns it —
            // one inside RunExclusiveAsync, typically — runs nested rather than waiting on a
            // semaphore it is itself holding, and leaves the release to the owner.
            var ownsLock = !_host.HoldsOperationLock;
            if (ownsLock)
            {
                try
                {
                    await _host.WaitForOperationLockAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ObjectDisposedException ex)
                {
                    // Dispose() raced ahead of us and disposed the semaphore.
                    // Surface the same clean failure as the post-acquisition
                    // shutdown check below, instead of leaking a low-level
                    // teardown exception to callers. The original is kept as
                    // InnerException so this rare race stays diagnosable.
                    throw new DeviceNotConnectedException(
                        "ExecuteTextCommandAsync cannot run because the device is disposed.",
                        ex,
                        isShuttingDown: true);
                }

                // Claimed in this frame, not behind an await: an AsyncLocal write flows forward to
                // this frame's callees but never back to its caller, and the callees are exactly who
                // must see it. See the remarks on ITextExchangeHost.
                _host.EnterOperationLockOwnership();
            }

            _host.IsInsideTextExchange = true;

            // Whether the exchange got past validation and so owes its finalize phase, and whether
            // it is on its way out normally rather than with an exception unwinding. Both are read
            // only by the finalize block in the outer finally below.
            var exchangeStarted = false;
            var completedNormally = false;
            try
            {
                // All validation runs INSIDE the lock so a competing thread
                // calling DisconnectAsync() / Dispose() while we're blocked
                // on the acquisition above doesn't leave us with a stale
                // transport / consumer reference (closes the TOCTOU window
                // documented in #186).
                if (_host.IsShuttingDown)
                {
                    throw new DeviceNotConnectedException(
                        "ExecuteTextCommandAsync cannot run while the device is "
                        + "disposing or disconnecting.",
                        isShuttingDown: true);
                }

                if (!_host.IsConnected)
                {
                    throw new DeviceNotConnectedException();
                }

                var transport = _host.Transport;
                if (transport == null)
                {
                    throw new InvalidOperationException("ExecuteTextCommandAsync requires a transport-based connection.");
                }

                // The device-level IsConnected check above is status-based and can still report
                // Connected when the underlying transport has dropped (e.g. a serial port closed
                // by an unplug or a DTR-triggered MCU reset mid-connect). Detect that here and
                // fail with the typed transport-disconnected exception, rather than dereferencing
                // Stream below and surfacing the framework's raw "BaseStream is only available
                // when the port is open." message (issue #238).
                if (!transport.IsConnected)
                {
                    throw new TransportNotConnectedException(
                        "Device transport is no longer connected.");
                }

                // Past validation: from here on the exchange acts on the device, so its finalize
                // phase (if any) is owed however this ends — including a prepare phase that failed
                // part-way and left the device half-way into the state it was establishing.
                exchangeStarted = true;

                var sw = Stopwatch.StartNew();

                // Prepare phase, if any. Deliberately here: inside the lock, so no competing text
                // exchange can interleave between it and the setup action below and undo the state
                // it establishes; and before the consumer swap, so the wait it typically needs
                // cannot widen the stale-line boundary taken further down. Any device output it
                // provokes goes to the protobuf consumer, which is still running at this point.
                if (prepareAsync != null)
                {
                    await prepareAsync(cancellationToken).ConfigureAwait(false);

                    Log(logger => logger.LogDebug("[ExecuteTextCommandAsync] Prepare phase completed at {ElapsedMs}ms", sw.ElapsedMilliseconds));
                }

                // Let anything queued before this exchange opened reach the wire while the protobuf
                // consumer is still the one reading, so its reply is not mistaken for an answer to
                // a command this exchange is about to send (issue #342). New sends from other
                // threads are already parked by this point, so the queue can only shrink.
                //
                // Deliberately OUTSIDE the swap's try/finally below: this is the one step here that
                // can throw (a cancelled token) before the consumer has been stopped, and that
                // finally restarts the consumer — which on a consumer that was never stopped means
                // subscribing the inbound handler a second time and dispatching every frame twice.
                await _host.DrainOutboundQueueAsync(cancellationToken).ConfigureAwait(false);

                var collectedLines = new List<string>();
                var stream = transport.Stream;
                int? originalReadTimeout = null;

                // Number of lines that were already in flight when this exchange opened — see the
                // note at the point it is captured, below.
                var staleLineCount = 0;

                try
                {
                    if (stream.CanTimeout)
                    {
                        try
                        {
                            originalReadTimeout = stream.ReadTimeout;
                            stream.ReadTimeout = Math.Min(500, Math.Max(100, responseTimeoutMs / 4));
                        }
                        catch
                        {
                            // Some streams may not allow setting read timeout; ignore.
                            originalReadTimeout = null;
                        }
                    }

                    // Stop the protobuf consumer so it doesn't compete for stream bytes.
                    // The serial transport sets ReadTimeout=500ms after connect, so the
                    // consumer thread's blocking Read will unblock within 500ms.
                    SuspendInboundConsumer();

                    Log(logger => logger.LogDebug("[ExecuteTextCommandAsync] Protobuf consumer stopped at {ElapsedMs}ms", sw.ElapsedMilliseconds));

                    // Create a temporary text consumer on the same stream
                    using var textConsumer = new StreamMessageConsumer<string>(
                        transport.Stream,
                        new LineBasedMessageParser(),
                        healthSink: transport as ITransportHealthSink);

                    textConsumer.MessageReceived += (_, e) =>
                    {
                        collectedLines.Add(e.Message.Data);
                    };

                    // The protobuf consumer is stopped for the duration of this exchange, so
                    // without this a read failure during a text command (an unplug mid-SD-listing,
                    // say) would be the one background failure with nowhere to go (issue #378).
                    //
                    // Scoped rather than a bare '+=' because this consumer can outlive the block:
                    // its stop and dispose are both time-bounded and may return with the reader
                    // thread still parked in an un-returning read. A live thread roots the consumer,
                    // which would root the device through the handler — retaining the whole object
                    // graph and, worse, letting a zombie reader keep raising errors on a device that
                    // has since been disconnected. 'using' disposes in reverse declaration order, so
                    // this detaches before textConsumer itself is disposed, on every exit path
                    // including a cancellation or a throwing setup action.
                    using var textConsumerErrors = _host.SubscribeConsumerErrors(textConsumer);

                    textConsumer.Start();
                    // ConfigureAwait(false): the lock is held, so resuming on a captured
                    // sync context (e.g. UI thread) would deadlock if that thread calls Disconnect().
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);

                    Log(logger => logger.LogDebug("[ExecuteTextCommandAsync] Text consumer started at {ElapsedMs}ms", sw.ElapsedMilliseconds));

                    // Mark the boundary between "already in flight" and "answers to this exchange".
                    // Anything captured before the setup action has sent anything is a late reply to
                    // an EARLIER command, or line noise — never a response to a command this exchange
                    // has yet to send. Those lines are dropped from the result below.
                    //
                    // Position matters as much as content: a caller that keys off response content —
                    // e.g. the SD listing's end-of-listing terminator (#396) — would otherwise accept
                    // a stale line as proof that the device answered a query it never even received,
                    // and report a complete listing for a device that has gone silent.
                    staleLineCount = collectedLines.Count;
                    if (staleLineCount > 0)
                    {
                        Log(logger => logger.LogDebug(
                            "[ExecuteTextCommandAsync] Discarding {StaleLineCount} line(s) received before this exchange sent anything",
                            staleLineCount));
                    }

                    // Execute the setup action (sends SCPI commands). ConfigureAwait(false)
                    // matches the surrounding lock-protected awaits.
                    await setupActionAsync(cancellationToken).ConfigureAwait(false);

                    Log(logger => logger.LogDebug("[ExecuteTextCommandAsync] Setup action completed at {ElapsedMs}ms", sw.ElapsedMilliseconds));

                    // Wait for responses using a two-phase inactivity-based timeout:
                    // Phase 1: Wait up to responseTimeoutMs for the first response.
                    // Phase 2: After receiving data, wait completionTimeoutMs of inactivity to finish.
                    var lastMessageTime = DateTime.UtcNow;
                    var maxWait = TimeSpan.FromMilliseconds(responseTimeoutMs * 5);
                    var startTime = DateTime.UtcNow;
                    var hasReceivedAny = false;

                    while (DateTime.UtcNow - startTime < maxWait)
                    {
                        var previousCount = collectedLines.Count;
                        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                        if (collectedLines.Count > previousCount)
                        {
                            lastMessageTime = DateTime.UtcNow;
                            if (!hasReceivedAny)
                            {
                                hasReceivedAny = true;
                                Log(logger => logger.LogDebug("[ExecuteTextCommandAsync] First response at {ElapsedMs}ms", sw.ElapsedMilliseconds));
                            }
                        }

                        var elapsed = DateTime.UtcNow - lastMessageTime;

                        if (hasReceivedAny)
                        {
                            // Phase 2: short completion timeout after first data
                            if (elapsed >= TimeSpan.FromMilliseconds(completionTimeoutMs))
                            {
                                break;
                            }
                        }
                        else
                        {
                            // Phase 1: full initial timeout waiting for first data
                            if (elapsed >= TimeSpan.FromMilliseconds(responseTimeoutMs))
                            {
                                break;
                            }
                        }
                    }

                    Log(logger => logger.LogDebug("[ExecuteTextCommandAsync] Collection complete at {ElapsedMs}ms, {LineCount} lines", sw.ElapsedMilliseconds, collectedLines.Count));

                    // Stop the text consumer
                    textConsumer.StopSafely();
                }
                finally
                {
                    if (originalReadTimeout.HasValue && stream.CanTimeout)
                    {
                        try
                        {
                            stream.ReadTimeout = originalReadTimeout.Value;
                        }
                        catch
                        {
                            // Ignore failures when restoring timeout.
                        }
                    }

                    // Restart the protobuf consumer
                    RestartMessageConsumerAfterSwap();

                    Log(logger => logger.LogDebug("[ExecuteTextCommandAsync] Total elapsed: {ElapsedMs}ms", sw.ElapsedMilliseconds));
                }

                // The text consumer is stopped by this point, so the list is no longer being
                // appended to concurrently and can be re-projected safely.
                var result = staleLineCount > 0
                    ? collectedLines.Skip(staleLineCount).ToList()
                    : collectedLines;

                completedNormally = true;
                return result;
            }
            finally
            {
                // Finalize phase, if any — the mirror of the prepare phase above, and deliberately
                // still inside the lock: an exchange that switches shared device state on the way in
                // has to switch it back before anything else can run, or the pairing is only half
                // serialized (#407). It runs after the protobuf consumer has been restarted, just as
                // the prepare phase ran before the consumer was swapped out.
                // A failure here is never thrown from this point: doing so would abandon the rest of
                // the finally, leaking the lock this exchange holds. It is held until after the
                // release below and dealt with there.
                Exception? finalizeFailure = null;
                if (exchangeStarted && finalizeAsync != null)
                {
                    try
                    {
                        await finalizeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        finalizeFailure = ex;
                    }
                }

                _host.IsInsideTextExchange = false;

                // Only the flow that took the lock releases it; a nested exchange leaves it to the
                // RunExclusiveAsync block that owns it. Parked sends are flushed before the release
                // so they are queued ahead of whatever runs next, and the release absorbs a Dispose
                // that already tore the semaphore down (Dispose acquires the lock first, but
                // proceeds anyway if that acquisition times out).
                if (ownsLock)
                {
                    _host.ExitOperationLockOwnership();
                }

                if (finalizeFailure != null)
                {
                    if (completedNormally)
                    {
                        // Nothing else is unwinding, so a failed restore is the only failure there
                        // is. Surface it rather than report a success the device never got back
                        // from — the caller's next command would run against the wrong state.
                        // Rethrown only now, with the lock already released, so a failed restore
                        // cannot also wedge the device.
                        ExceptionDispatchInfo.Capture(finalizeFailure).Throw();
                    }

                    // Otherwise an exception is already on its way to the caller, and it is the one
                    // that explains what went wrong. Replacing it with this one would lose the
                    // diagnosis, so the cleanup failure is logged instead: cleanup never hides the
                    // failure that caused the cleanup.
                    Log(logger => logger.LogError(
                        finalizeFailure,
                        "The text exchange's finalize phase failed while another failure was already "
                        + "unwinding. The original failure is being surfaced to the caller; the device "
                        + "may be left in the state the prepare phase established."));
                }
            }
        }

        /// <summary>
        /// Detaches the device's inbound handler and stops the protobuf consumer so it does not
        /// compete for stream bytes while a swap is in progress.
        /// </summary>
        /// <remarks>
        /// The consumer is snapshotted once. The field behind it is mutable — a teardown nulls it —
        /// and the exchange's lock does not exclude teardown, which proceeds anyway once its bounded
        /// courtesy wait expires. Re-reading it per step could therefore detach from one instance and
        /// stop another, or dereference null. This mirrors what
        /// <see cref="RestartMessageConsumerAfterSwap"/> already does for the same reason.
        /// </remarks>
        private void SuspendInboundConsumer()
        {
            var consumer = _host.MessageConsumer;
            if (consumer == null)
            {
                return;
            }

            _host.DetachInboundHandler(consumer);
            var stopped = consumer.StopSafely(timeoutMs: 1000);
            if (!stopped)
            {
                consumer.Stop();
            }
        }

        /// <summary>
        /// Restarts the protobuf consumer after a swap (raw capture or text exchange) has stopped it.
        /// </summary>
        /// <remarks>
        /// The stop paths join the reader thread with a bounded timeout, so a reader parked in a slow
        /// blocking <see cref="Stream.Read(byte[], int, int)"/> can still be alive here.
        /// <see cref="StreamMessageConsumer{T}.Start"/> absorbs that case by waiting a grace period
        /// for the stopped reader to exit, which is what keeps a normal connect from failing with
        /// "a previous consumer thread has not yet exited" (issue #383).
        /// <para>
        /// If it still refuses, the reader's read is not returning at all — the stream is stuck.
        /// Deliberately do <b>not</b> recover by binding a fresh consumer to that same stream: a new
        /// instance would be a second concurrent reader on it, which is exactly the framing
        /// corruption the guard exists to prevent, and it would block on the stuck stream anyway.
        /// The consumer is left stopped; the operation's own failure (or the next
        /// <see cref="DaqifiDevice.Connect"/>) surfaces the problem honestly.
        /// </para>
        /// <para>
        /// Never throws: it runs from <c>finally</c> blocks, where an exception would mask the real
        /// failure already unwinding. The consumer is also snapshotted once up front — the
        /// text-exchange path holds the operation lock (which <see cref="DaqifiDevice.Disconnect"/>
        /// waits on), but the raw-capture path does not, so a concurrent teardown could otherwise
        /// null the field between reads.
        /// </para>
        /// </remarks>
        private void RestartMessageConsumerAfterSwap()
        {
            var consumer = _host.MessageConsumer;
            if (consumer == null)
            {
                return;
            }

            try
            {
                consumer.Start();
                _host.AttachInboundHandler(consumer);
            }
            catch (ConsumerThreadNotExitedException ex)
            {
                Log(logger => logger.LogError(
                    ex,
                    "The previous message consumer thread did not exit, so the consumer was left stopped. "
                    + "The device stream appears stuck; a reconnect is required to resume inbound messages."));
            }
            catch (Exception ex)
            {
                // e.g. ObjectDisposedException from a concurrent Dispose(). Swallow rather than let
                // it escape a finally block and replace the operation's real exception.
                Log(logger => logger.LogError(ex, "Failed to restart the message consumer after a stream swap."));
            }
        }

        /// <summary>
        /// Logs through the device's logger without letting a throwing logger take down an exchange —
        /// the same isolation <c>DaqifiDevice.SafeLog</c> gives the rest of the device.
        /// </summary>
        private void Log(Action<ILogger> logAction)
        {
            try
            {
                logAction(_host.Logger);
            }
            catch
            {
                // A logger that throws is not permitted to take down device operation.
            }
        }
    }
}
