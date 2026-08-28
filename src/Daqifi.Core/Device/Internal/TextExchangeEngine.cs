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
        /// Takes exclusive use of the device and hands the transport stream to the caller for raw
        /// byte access, with the protobuf consumer paused for the duration. Everything is restored
        /// when the action completes, however it completes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Runs under the device's operation lock, on the same protocol
        /// <see cref="ExecuteAsync"/> uses — including nested re-entry for a flow that already holds
        /// it. Until #493 this was the one path that took the stream without excluding anything: a
        /// status poll from another thread would acquire the exchange lock uncontended and put a
        /// second reader on the same stream mid-capture, and a plain <see cref="DaqifiDevice.Send{T}"/>
        /// was not even deferred, so its reply landed inside the captured bytes. The capture window
        /// is therefore also what makes other flows' sends defer; they are replayed on the way out.
        /// </para>
        /// <para>
        /// Captures can be long — an SD download's budget is 30 minutes — so a competing text
        /// exchange now waits that long rather than corrupting the transfer. Callers who cannot
        /// wait should pass a cancellation token: it is observed while queueing for the lock.
        /// </para>
        /// </remarks>
        /// <param name="rawAction">
        /// An async function that receives the transport stream and performs raw I/O.
        /// The protobuf consumer will not read from the stream while this action is executing.
        /// </param>
        /// <param name="cancellationToken">A cancellation token to observe.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected, disconnecting or disposed.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the device has no transport-based connection.</exception>
        /// <exception cref="TransportNotConnectedException">Thrown when the transport dropped while this capture waited for the lock.</exception>
        /// <exception cref="OperationCanceledException">Thrown when cancelled, including while waiting for the lock.</exception>
        internal async Task ExecuteRawCaptureAsync(
            Func<Stream, CancellationToken, Task> rawAction,
            CancellationToken cancellationToken = default)
        {
            _host.EnsureConnected(cancellationToken);

            // A capture is a consumer swap, so it obeys the same nesting rule an exchange does: one
            // swap at a time per flow. The lock does not cover this — a nested call runs nested by
            // design — and without the guard the inner swap's finally would restart the protobuf
            // consumer while the outer capture still owns the stream, putting a second reader on it.
            if (_host.IsInsideTextExchange)
            {
                throw new InvalidOperationException(
                    "ExecuteRawCaptureAsync is not re-entrant: this flow is already inside a "
                    + "consumer swap — another raw capture, or a text exchange — and both take "
                    + "the device's message consumer and its stream.");
            }

            // A flow that already owns the lock — an exclusive block, or the SD operations' own
            // prepare/restore exchanges (#407) — runs nested rather than waiting on a semaphore it
            // is itself holding, and leaves the release to the owner. Same rule as ExecuteAsync.
            var ownsLock = !_host.HoldsOperationLock;
            if (ownsLock)
            {
                try
                {
                    await _host.WaitForOperationLockAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ObjectDisposedException ex)
                {
                    // Dispose() raced ahead of us and disposed the semaphore. Surface the same clean
                    // failure as the post-acquisition shutdown check below, with the original kept
                    // as InnerException so the race stays diagnosable.
                    throw new DeviceNotConnectedException(
                        "ExecuteRawCaptureAsync cannot run because the device is disposed.",
                        ex,
                        isShuttingDown: true);
                }

                // Claimed in this frame, not behind an await: an AsyncLocal write flows forward to
                // this frame's callees but never back to its caller, and the callees — the raw
                // action and every Send() it makes — are exactly who must see it. See the remarks
                // on ITextExchangeHost. This is also what starts deferring other flows' sends.
                _host.EnterOperationLockOwnership();
            }

            // Claims the swap for this flow, so anything the raw action calls that would swap the
            // consumer again is caught by the guard above (or by ExecuteAsync's) rather than
            // restarting the consumer underneath this capture.
            _host.IsInsideTextExchange = true;

            try
            {
                // All validation runs INSIDE the lock. Everything checked before the wait was
                // checked against a session that may have been torn down while this flow queued
                // behind another operation — the same TOCTOU window #186 closed for the text
                // exchange, and a wider one here because captures are long.
                if (_host.IsShuttingDown)
                {
                    throw new DeviceNotConnectedException(
                        "ExecuteRawCaptureAsync cannot run while the device is disposing or disconnecting.",
                        isShuttingDown: true);
                }

                _host.EnsureConnected();

                var transport = _host.Transport;
                if (transport == null)
                {
                    throw new InvalidOperationException("ExecuteRawCaptureAsync requires a transport-based connection.");
                }

                // Device-level IsConnected is status-based and can still report Connected when the
                // underlying transport has dropped. Fail typed here rather than dereferencing
                // Stream below and surfacing the framework's raw "BaseStream is only available when
                // the port is open." (issue #238, the same check ExecuteAsync makes).
                if (!transport.IsConnected)
                {
                    throw new TransportNotConnectedException(
                        "Device transport is no longer connected.");
                }

                // Let anything queued before this capture opened reach the wire while the protobuf
                // consumer is still the one reading (issue #342). Deferral only parks sends that
                // arrive from here on; a command queued microseconds earlier would otherwise be
                // written mid-capture and its reply read as captured content.
                //
                // Deliberately OUTSIDE the swap's try/finally below, for the same reason as in
                // ExecuteAsync: this is the one step that can throw (a cancelled token) before the
                // consumer has been stopped, and that finally restarts the consumer — which on a
                // consumer that was never stopped means subscribing the inbound handler a second
                // time and dispatching every frame twice.
                await _host.DrainOutboundQueueAsync(cancellationToken).ConfigureAwait(false);

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
            finally
            {
                _host.IsInsideTextExchange = false;

                // Only the flow that took the lock releases it; a nested capture leaves that to the
                // block that owns it. The release replays the sends parked while the capture ran,
                // before the semaphore is handed on, so they are queued ahead of whatever runs next.
                if (ownsLock)
                {
                    _host.ExitOperationLockOwnership();
                }
            }
        }

        /// <summary>
        /// Executes a text-based command by temporarily switching from the protobuf consumer to a
        /// line-based text consumer, collecting text responses, then restoring the protobuf consumer.
        /// </summary>
        /// <remarks>
        /// The parameter contract — including what the prepare and finalize phases guarantee — is
        /// documented on <see cref="DaqifiDevice.ExecuteTextCommandAsync(Action, int, int, CancellationToken, Func{CancellationToken, Task}, Func{Task}, bool)"/>,
        /// the seam callers actually use.
        /// </remarks>
        internal async Task<IReadOnlyList<string>> ExecuteAsync(
            Func<CancellationToken, Task>? prepareAsync,
            Func<Task>? finalizeAsync,
            Func<CancellationToken, Task> setupActionAsync,
            int responseTimeoutMs,
            int completionTimeoutMs,
            CancellationToken cancellationToken,
            bool keepBlankLines = false)
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
            // The flag covers a raw capture's swap too, so this also catches an
            // exchange opened from inside one — same corruption, same answer.
            if (_host.IsInsideTextExchange)
            {
                throw new InvalidOperationException(
                    "ExecuteTextCommandAsync is not re-entrant on the same device; "
                    + "do not call it from inside a setupAction callback or a raw capture.");
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

                _host.EnsureConnected();

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

                var collectedLines = new List<CollectedLine>();

                // The list is appended to from the text consumer's reader thread and read from
                // this one, so every touch of it goes through this gate. Stopping the consumer is
                // not enough on its own: StopSafely and Dispose are both time-bounded and can
                // return with the reader still parked in an un-returning read, which the remarks
                // on RestartMessageConsumerAfterSwap already say out loud. Without the gate, a
                // line arriving in that window while the result is being projected throws
                // "collection was modified" out of an exchange that had otherwise succeeded.
                var collectedLinesGate = new object();

                // How the exchange learns that a line arrived, instead of asking every 50ms.
                // Guarded by the gate above, which is what makes the handshake lossless: a line is
                // added to the list and the waiter is taken under the same lock the wait loop
                // registers under, so a line that lands between the loop's last look and its next
                // wait completes that wait rather than being missed by it.
                //
                // Deliberately a TaskCompletionSource rather than a SemaphoreSlim: the signal is
                // raised on the text consumer's reader thread, which can outlive this exchange (its
                // stop and dispose are both time-bounded, as the remarks on
                // RestartMessageConsumerAfterSwap say). A semaphore would have to be disposed while
                // that thread might still signal it; a TCS never needs disposing and TrySetResult
                // on a stale one is a no-op.
                //
                // RunContinuationsAsynchronously matters as much: without it the reader thread
                // would run this exchange's continuation itself, i.e. resume the exchange —
                // including the consumer stop that joins that very thread — on the thread being
                // joined.
                TaskCompletionSource<bool>? lineWaiter = null;

                int CollectedLineCount()
                {
                    lock (collectedLinesGate)
                    {
                        return collectedLines.Count;
                    }
                }

                var stream = transport.Stream;
                int? originalReadTimeout = null;

                // Number of lines that were already in flight when this exchange opened — see the
                // note at the point it is captured, below.
                var staleLineCount = 0;

                // Number of lines collected by the time the setup action finished sending — see the
                // note at the point it is captured, below (issue #553).
                var sentBoundaryLineCount = 0;

                // What the outbound writer looked like when this exchange opened, or null on a
                // device that cannot say — see the note at the point it is captured, below
                // (issue #593).
                OutboundWriterSample? writerBoundary = null;

                // A blank this exchange can prove is not its own answer: one that was already
                // collected when the setup action finished queueing the command (issue #553), or
                // one that arrived while a command was queued behind a writer that had not written
                // anything for this exchange yet (issue #593). Either way the device had not been
                // asked yet, and a blank only ever means "end of dump" — there is nothing for it to
                // terminate.
                //
                // The two rules are complementary rather than nested: the line count catches a
                // blank that beat the setup action's return, the writer sample catches one that
                // beat the queued command onto the wire. Neither is a wait, and neither moves the
                // boundary the exchange's safety rests on.
                //
                // Both halves of the writer sample are load-bearing. A write count that has not
                // moved is not on its own evidence that nothing was asked: a setup action that
                // sends by another route, or sends nothing at all and expects the device to be
                // mid-dump already, leaves it exactly as still. Requiring work to be outstanding as
                // well narrows the rule to the case actually in question — something handed to the
                // writer and not yet written.
                //
                // Content lines are deliberately subject to neither rule: the wider staleLineCount
                // boundary is all that applies to them, so a genuinely fast reply is never at risk
                // (the decision recorded in #553). A device whose writer cannot be sampled falls
                // back to the line-count rule alone, which is what it had before.
                //
                // Declared out here rather than beside its first use because both the wait loop
                // and the projection ask it, and they sit on opposite sides of the consumer swap's
                // try/finally.
                //
                // Split in two because the terminator short-circuit below asks the same question of
                // a content line (#667 item 4). It may: declining to short-circuit only leaves the
                // exchange waiting exactly as long as it used to, so applying the narrower rule
                // there costs a fast reply nothing — unlike applying it to the projection, which
                // would discard one.
                bool ArrivedBeforeSend(int index, CollectedLine collected) =>
                    index < sentBoundaryLineCount
                    || (writerBoundary.HasValue
                        && collected.WriterAtArrival.HasValue
                        && collected.WriterAtArrival.Value.HasWorkOutstanding
                        && collected.WriterAtArrival.Value.StartedWrites
                            <= writerBoundary.Value.StartedWrites);

                bool IsPreSendBlank(int index, CollectedLine collected) =>
                    collected.Text.Length == 0 && ArrivedBeforeSend(index, collected);

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

                    // Create a temporary text consumer on the same stream.
                    //
                    // Both parser settings exist because this loop decides that the device has
                    // answered by counting the lines the parser produces, so any reply the parser
                    // does not turn into a line is a reply this exchange cannot see — and it then
                    // waits out its whole first-response timeout for an answer already sitting in
                    // the buffer (issue #538). The firmware sends two shapes that used to vanish:
                    //
                    //  * a bare-LF reply. Most replies are CRLF, but SYSTem:LOG:CLEar and
                    //    SYSTem:LOG:TEST answer "Log cleared\n" / "Added test log messages\n".
                    //    Splitting on "\n" reads those AND the CRLF ones — the CR ends up at the
                    //    end of the line and is trimmed off with the surrounding whitespace.
                    //  * a blank line. SYSTem:LOG? terminates its dump with one, so an empty log
                    //    arrives as a lone CRLF and nothing else.
                    //
                    // The blanks are filtered out of the result below, so callers see the same
                    // lines they always did; only this loop's "did anything arrive?" question sees
                    // them. Splitting on LF also means an embedded bare LF now ends a line rather
                    // than sitting inside one, which is what a line-based text protocol means by it.
                    using var textConsumer = new StreamMessageConsumer<string>(
                        transport.Stream,
                        new LineBasedMessageParser(lineEnding: "\n") { EmitEmptyLines = true },
                        healthSink: transport as ITransportHealthSink);

                    // MessageParsed rather than MessageReceived: only the parsed line matters here,
                    // and the raw-buffer snapshot the other event carries is a copy per read that
                    // nothing would read (issue #490).
                    textConsumer.MessageParsed += parsed =>
                    {
                        // Each line is stamped with what the outbound writer looked like when it
                        // was parsed. Read here rather than at projection time because it is a
                        // fact about the line's arrival that is gone a moment later, and it is
                        // what lets the projection recognise a line that reached the wire before
                        // this exchange's command did (issue #593). Sampling, on the reader's own
                        // thread, costs nothing and delays nothing — deliberately unlike the drain
                        // that #593 rules out.
                        var writer = _host.SampleOutboundWriter();

                        TaskCompletionSource<bool>? waiter;
                        lock (collectedLinesGate)
                        {
                            collectedLines.Add(new CollectedLine(parsed.Data, writer));

                            // Taken and cleared under the same lock as the append, so the wait loop
                            // either sees the new line or is woken for it — never neither.
                            waiter = lineWaiter;
                            lineWaiter = null;
                        }

                        // Completed outside the lock: the continuation is queued rather than run
                        // here, but keeping the gate while releasing it would still let the wait
                        // loop's own gated reads contend with this reader thread for no reason.
                        waiter?.TrySetResult(true);
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
                    staleLineCount = CollectedLineCount();

                    // Captured with the line boundary above, and for the same reason: it marks
                    // "before this exchange". A line stamped with no more started writes than this
                    // was parsed before any command of this exchange had begun going out, so it
                    // cannot be an answer to one — which is the half of the boundary a line count
                    // cannot express, because the setup action returns when the command is
                    // *queued*, not when it is written (issue #593).
                    writerBoundary = _host.SampleOutboundWriter();
                    if (staleLineCount > 0)
                    {
                        Log(logger => logger.LogDebug(
                            "[ExecuteTextCommandAsync] Discarding {StaleLineCount} line(s) received before this exchange sent anything",
                            staleLineCount));
                    }

                    // Test-only seam (issue #553): a no-op on the real device, so this changes
                    // nothing here. It exists so a test double can release a line into the transport
                    // in the capture-to-send window below — normally sub-millisecond, and otherwise
                    // unreachable by any delay-based or access-counted test double.
                    _host.OnStaleLineBoundaryCaptured();

                    // Execute the setup action (sends SCPI commands). ConfigureAwait(false)
                    // matches the surrounding lock-protected awaits.
                    await setupActionAsync(cancellationToken).ConfigureAwait(false);

                    // A second boundary, captured only to catch a blank line that arrived in the
                    // window above: after the stale-line boundary was captured, but before the
                    // command actually went out (issue #553). A blank cannot be a terminator for a
                    // command the device has not yet received, so any blank at or before this point
                    // is necessarily a leftover from an earlier exchange. Content lines are left on
                    // the wider boundary above — narrowing it would risk discarding a genuinely fast
                    // reply, and nothing needs it narrowed. On a bench Nq1 the real terminator lands
                    // safely after this point, 10/10 runs, so the blank rule costs nothing either.
                    //
                    // Captured right here, and it must never be moved later — in particular not
                    // after also draining the outbound queue to wait for the write to physically
                    // land (setupActionAsync only guarantees the command was enqueued to
                    // MessageProducer, not written yet). That stricter boundary looks like a
                    // tightening of the rule below and is in fact an inversion of it: measured on a
                    // real Nq1, the device answers roughly 6ms after the write, faster than the
                    // drain's own 10ms poll tick, so draining first lands the device's genuine
                    // terminator on the far side of this boundary and the filter below discards it
                    // as stale. SYSTem:LOG? then reports "the device did not answer" for a device
                    // that answered on time, 10/10 runs. What makes this boundary safe is not
                    // precision about when the write landed — it is being strictly earlier than any
                    // reply can physically exist. See #593, which records that argument in full.
                    //
                    // The window that leaves — queued but not yet written — is closed without moving
                    // this boundary at all, by the writer sample each collected line carries (see
                    // the MessageParsed handler above and the projection below). Asking a line what
                    // the writer was doing when it arrived answers the same question a later
                    // boundary was meant to answer, without making any reply wait for the answer.
                    //
                    // The ~2-2.5x slowdown a drain also shows on the bench is the engine, not the
                    // device: the wait loop below infers "the device answered" from a count increase
                    // observed inside the loop, so a line that landed before its first poll is
                    // invisible to it and the exchange sits out the full responseTimeoutMs. That is
                    // #592, and with it fixed a drain costs nothing (1058ms vs 1056ms) — it is still
                    // wrong, for the reason above, just not slow.
                    sentBoundaryLineCount = CollectedLineCount();

                    // Test-only seam (issue #632), the counterpart of the one above: a no-op on the
                    // real device, so this changes nothing here. It exists so a test double can
                    // release a line into the transport at a point the pre-send rules provably do
                    // not cover, instead of guessing at that point with a wall-clock delay racing
                    // this exchange's own response timeout.
                    _host.OnSendBoundaryCaptured();

                    Log(logger => logger.LogDebug("[ExecuteTextCommandAsync] Setup action completed at {ElapsedMs}ms", sw.ElapsedMilliseconds));

                    // Whether anything beyond staleLineCount would actually survive the projection
                    // below as evidence this exchange got an answer -- i.e. not a blank that will be
                    // dropped as a pre-send leftover. A raw count comparison against staleLineCount
                    // is NOT equivalent to this: such a blank bumps the count but is discarded
                    // later, and treating it as evidence would flip the wait loop into the short
                    // completion-timeout phase before the real response has a chance to arrive.
                    bool HasResponseEvidenceBeyondStaleBoundary()
                    {
                        lock (collectedLinesGate)
                        {
                            for (var i = staleLineCount; i < collectedLines.Count; i++)
                            {
                                if (!IsPreSendBlank(i, collectedLines[i]))
                                {
                                    return true;
                                }
                            }

                            return false;
                        }
                    }

                    // Whether any line in [from, to) is the reply that ends this exchange — the
                    // answer to a query the setup action sent last precisely so the exchange would
                    // have something to finish on (issue #667 item 4). Without this, those exchanges
                    // sit out their whole completion window after the terminator has already
                    // arrived: 500ms on every connect, 1000ms on every SD listing and every
                    // confirmed administration command, all of it spent waiting for lines that by
                    // construction are never coming.
                    //
                    // Two guards stand in front of the question, and they are what make asking it
                    // safe. A terminator-shaped line that arrived before this exchange sent anything
                    // is a leftover from an EARLIER exchange -- the case TrySplitAtSdListTerminator
                    // scans from the end for, whose comment records that stopping at the first match
                    // reports an empty card. Such a line is skipped here, so it cannot end the
                    // exchange, and the projection below still hands the caller every line it
                    // collected: this decides only when to stop listening, never what is returned.
                    // What the caller does with a response holding two terminator-shaped lines is
                    // unchanged, and TrySplitAtSdListTerminator remains the authority on which of
                    // them is real.
                    //
                    // The residual case, and the reason the completion window stays as a fallback
                    // rather than being tightened: a stale reply the device only emits AFTER this
                    // exchange's commands have begun going out is not distinguishable from ours by
                    // either guard. It has to be read from the buffer more than the 50ms consumer
                    // settle above plus the send boundary after it, so it is narrow -- but it is not
                    // impossible, and it is why nothing here is allowed to change the result.
                    bool TryFindTerminator(int from, int to)
                    {
                        var candidates = new List<string>();
                        lock (collectedLinesGate)
                        {
                            var start = Math.Max(from, staleLineCount);
                            var end = Math.Min(to, collectedLines.Count);
                            for (var i = start; i < end; i++)
                            {
                                var collected = collectedLines[i];
                                if (collected.Text.Length == 0 || ArrivedBeforeSend(i, collected))
                                {
                                    continue;
                                }

                                candidates.Add(collected.Text);
                            }
                        }

                        // Asked with the gate released: the host's answer is documented as cheap and
                        // lock-free, but the reader thread has to be able to take this gate, and
                        // nothing that calls out of the engine may hold it.
                        foreach (var candidate in candidates)
                        {
                            if (_host.IsExchangeTerminatorReply(candidate))
                            {
                                return true;
                            }
                        }

                        return false;
                    }

                    // Wait for responses using a two-phase inactivity-based timeout:
                    // Phase 1: Wait up to responseTimeoutMs for the first response.
                    // Phase 2: After receiving data, wait completionTimeoutMs of inactivity to finish.
                    //
                    // The two phases and their budgets are exactly what they have always been. What
                    // changed (issue #485) is how the loop waits: it used to tick every 50ms and
                    // compare line counts, which meant a device that had finished answering was
                    // still held for up to a tick past its completion window, and the first reply
                    // was noticed up to a tick late. Waiting on the arrival signal instead ends the
                    // exchange the moment the inactivity window actually expires. No timeout is
                    // shortened here and no wait is skipped — only the quantisation is gone.
                    //
                    // Timed off the Stopwatch rather than DateTime.UtcNow: these are elapsed-time
                    // deadlines, and a wall clock that steps (NTP, a DST-less clock correction)
                    // would move them under a request already in flight.
                    var waitStart = sw.Elapsed;
                    var lastMessageAt = waitStart;
                    var maxWait = TimeSpan.FromMilliseconds(responseTimeoutMs * 5);

                    // A reply can land between sentBoundaryLineCount being captured just above and
                    // this loop's first poll -- single-digit milliseconds on a real device, but
                    // enough. Without this seed, hasReceivedAny only flips on a count *increase
                    // observed inside the loop*, so a reply that already arrived by the time the
                    // loop starts is invisible to it: the exchange then sits out the full
                    // responseTimeoutMs instead of the short completionTimeoutMs -- an ~8x stall on
                    // some diagnostics reads, though never a wrong answer, since maxWait still
                    // bounds collection either way (issue #592).
                    //
                    // Seeded via HasResponseEvidenceBeyondStaleBoundary rather than a raw count
                    // comparison against staleLineCount: a blank that arrived before the command
                    // did bumps the count but is dropped later as a pre-send leftover (#553, #593),
                    // and treating that bump as evidence would flip this into the short completion-
                    // timeout phase before the real response arrives, discarding it early.
                    var hasReceivedAny = HasResponseEvidenceBeyondStaleBoundary();
                    if (hasReceivedAny)
                    {
                        Log(logger => logger.LogDebug("[ExecuteTextCommandAsync] First response already present entering wait loop"));
                    }

                    // Lines already collected at this point are the seed above, not news. The old
                    // loop expressed the same thing by taking its "previous count" from here.
                    var observedLineCount = CollectedLineCount();

                    // Checked before the loop as well as inside it, and for the same reason
                    // hasReceivedAny is seeded above: on a device that answers faster than the
                    // exchange gets here, the terminator has already landed, and a check that only
                    // ran on a subsequent arrival would never see it — leaving exactly the wait this
                    // is meant to remove, on exactly the fast devices it helps most (#592).
                    var terminatorSeen = TryFindTerminator(0, observedLineCount);
                    if (terminatorSeen)
                    {
                        Log(logger => logger.LogDebug("[ExecuteTextCommandAsync] Terminator reply already present entering wait loop"));
                    }

                    while (!terminatorSeen)
                    {
                        var now = sw.Elapsed;

                        // The overall ceiling, unchanged: collection never runs longer than this
                        // however busy the device is, so a device that talks forever cannot hold
                        // the operation lock forever.
                        var remainingOverall = maxWait - (now - waitStart);
                        if (remainingOverall <= TimeSpan.Zero)
                        {
                            break;
                        }

                        // The inactivity window currently in force — the full first-response
                        // timeout until something arrives, the short completion timeout after.
                        var inactivityBudget =
                            TimeSpan.FromMilliseconds(hasReceivedAny ? completionTimeoutMs : responseTimeoutMs)
                            - (now - lastMessageAt);
                        if (inactivityBudget <= TimeSpan.Zero)
                        {
                            break;
                        }

                        var waitFor = inactivityBudget < remainingOverall ? inactivityBudget : remainingOverall;

                        Task lineArrived;
                        lock (collectedLinesGate)
                        {
                            // Registered under the gate the handler appends under, so there is no
                            // window in which a line can arrive unnoticed AND unsignalled. A line
                            // already waiting is handled without waiting at all.
                            if (collectedLines.Count > observedLineCount)
                            {
                                lineArrived = Task.CompletedTask;
                            }
                            else
                            {
                                lineWaiter ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                                lineArrived = lineWaiter.Task;
                            }
                        }

                        try
                        {
                            // Awaited with collectedLinesGate released — deliberately, and the
                            // registration above is what makes that safe: the waiter was taken
                            // under the gate, so a line arriving in this window completes the task
                            // already in hand rather than being missed. Nothing may await while
                            // holding that gate; the reader thread has to be able to take it.
                            //
                            // ConfigureAwait(false) for the same reason as every other await here:
                            // this exchange holds the device's operation lock across it, so
                            // resuming on a captured sync context would deadlock if that thread
                            // calls Disconnect().
                            await lineArrived.WaitAsync(waitFor, cancellationToken).ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            // The window expired with nothing new. Not an error — it is how both
                            // phases end. Fall through: the loop re-reads the clock and the line
                            // count, so a line that landed in the same instant is still counted.
                        }

                        var currentCount = CollectedLineCount();
                        if (currentCount > observedLineCount)
                        {
                            var previousLineCount = observedLineCount;
                            observedLineCount = currentCount;
                            lastMessageAt = sw.Elapsed;

                            // A count increase is not the same thing as an answer: the new line can
                            // be a leftover blank that the projection below will discard. Asking
                            // the same question the projection asks keeps the two from disagreeing
                            // — otherwise the loop stops early on a line that then vanishes, and
                            // the exchange reports silence for a device that was still answering.
                            if (!hasReceivedAny && HasResponseEvidenceBeyondStaleBoundary())
                            {
                                hasReceivedAny = true;
                                Log(logger => logger.LogDebug("[ExecuteTextCommandAsync] First response at {ElapsedMs}ms", sw.ElapsedMilliseconds));
                            }

                            // Only the lines that just arrived are examined: an earlier one that was
                            // going to end this exchange already did, and re-reading the whole
                            // response on every arrival would make a long SD listing quadratic in
                            // the number of lines it collects.
                            if (TryFindTerminator(previousLineCount, currentCount))
                            {
                                terminatorSeen = true;
                                Log(logger => logger.LogDebug("[ExecuteTextCommandAsync] Terminator reply at {ElapsedMs}ms; ending collection", sw.ElapsedMilliseconds));
                            }
                        }
                    }

                    // Test-only seam (issue #634), the third of these and a no-op on the real
                    // device. It publishes which branch the loop above just left by: a test that
                    // cares whether the exchange recognised the reply can then ask, instead of
                    // inferring it from how long the whole call took on a machine whose scheduler
                    // it does not control.
                    _host.OnReplyWaitCompleted(hasReceivedAny);

                    var collectedLineCount = CollectedLineCount();
                    Log(logger => logger.LogDebug("[ExecuteTextCommandAsync] Collection complete at {ElapsedMs}ms, {LineCount} lines", sw.ElapsedMilliseconds, collectedLineCount));

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

                // The text consumer has been stopped and disposed by this point, but both are
                // time-bounded, so the projection takes the gate rather than trusting that the
                // reader thread is really gone — see the note where the gate is declared.
                //
                // The blank lines the parser was asked to emit are dropped here. They are evidence
                // for the wait loop above and nothing more: every caller of this seam parses
                // content, and none of them ever saw a blank line before (#538). The stale skip
                // runs first, so a blank line that arrived before this exchange sent anything is
                // discarded as stale rather than counted as content — same rule as any other line.
                List<string> result;
                lock (collectedLinesGate)
                {
                    var afterStale = collectedLines
                        .Select((collected, index) => (collected, index))
                        .Skip(staleLineCount)
                        // Blank lines that arrived before this exchange's command did are dropped
                        // here, before the keepBlankLines projection below ever sees them — a blank
                        // is only ever an end-of-dump terminator, and there is nothing for it to
                        // terminate yet. Content lines are untouched: only the wider staleLineCount
                        // boundary above applies to them (issues #553 and #593).
                        .Where(t => !IsPreSendBlank(t.index, t.collected))
                        .Select(t => t.collected.Text);
                    // keepBlankLines is how a caller asks to see the firmware's
                    // end-of-dump blank line. SYSTem:LOG? terminates its dump with
                    // one unconditionally, so its presence is the difference between
                    // "the device answered and its log is empty" and "the device did
                    // not answer at all" -- two states that are otherwise identical
                    // from here (issue #543). Default stays false: every other caller
                    // parses content and has never seen a blank line (#538).
                    result = (keepBlankLines
                                ? afterStale
                                : afterStale.Where(line => line.Length > 0))
                        .ToList();
                }

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
        /// failure already unwinding. The consumer is also snapshotted once up front: both swap
        /// paths hold the operation lock (which <see cref="DaqifiDevice.Disconnect"/> waits on), but
        /// that wait is bounded and teardown proceeds anyway once it expires, so a concurrent
        /// teardown could still null the field between reads.
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

        /// <summary>
        /// One line the text consumer produced, together with what the outbound writer had already
        /// started when the line was parsed.
        /// </summary>
        /// <param name="Text">The parsed line, exactly as the caller will see it.</param>
        /// <param name="WriterAtArrival">
        /// <see cref="ITextExchangeHost.SampleOutboundWriter"/> taken as the line arrived, or
        /// <c>null</c> when the device could not say. It is a fact about the line's arrival and
        /// nothing else can recover it afterwards, which is why it is taken here rather than
        /// re-read at projection time.
        /// </param>
        private readonly record struct CollectedLine(string Text, OutboundWriterSample? WriterAtArrival);
    }
}
