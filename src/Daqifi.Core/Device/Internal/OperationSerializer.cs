using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// Owns the device's operation lock and the backlog of sends parked behind it — extracted from
/// <see cref="DaqifiDevice"/> so the device delegates rather than hosts it (issue #480).
/// </summary>
/// <remarks>
/// <para>
/// Three things coordinate here, and they only work as one unit. The <b>lock</b> makes an
/// operation exclusive; the <b>generation</b> distinguishes "this flow holds the lock" from
/// "this flow holds the lock in the session that is still current"; and the <b>backlog</b> parks
/// the sends that arrive from other flows while an operation owns the device, so they go out in
/// order afterwards instead of landing inside somebody else's exchange (issue #342).
/// </para>
/// <para>
/// The comments below say which reported defect each rule closes. They were written against a
/// long history of ordering bugs and are the reason the sequences look the way they do — read
/// them before moving anything.
/// </para>
/// </remarks>
internal sealed class OperationSerializer : IDisposable
{
    /// <summary>
    /// How often <see cref="DrainOutboundQueueAsync"/> re-asks the producer whether it has gone
    /// idle. Was an unnamed <c>10</c>; named because it is now driven by
    /// <see cref="IOperationSerializationHost.TimeProvider"/> (issue #637). Unchanged in length.
    /// </summary>
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly IOperationSerializationHost _host;

    // THE device operation lock. Originally introduced to serialize ExecuteTextCommandAsync
    // calls device-wide (closes #186): multiple callers — e.g. concurrent GetSdCardFilesAsync /
    // DrainErrorQueueAsync / GetSystemInfoAsync — would otherwise race the protobuf-consumer
    // pause/swap/restart sequence on the same stream and either intermix SCPI bytes on the wire
    // or interleave reply lines between callers' returned lists.
    //
    // It is now also what RunExclusiveAsync takes (closes #342), so a caller can declare a
    // multi-command sequence indivisible and have text exchanges, deferred Send()s and teardown
    // all coordinate against the same one lock. Deliberately ONE lock rather than an operation
    // lock layered over the text-exchange lock: two locks would need an ordering, and the code
    // that would have to respect it (Disconnect, Dispose, the reconnect loop, every SD
    // operation) is exactly the code that must never deadlock.
    //
    // SemaphoreSlim chosen over Lock because the holders are async; counter is (1, 1) for
    // mutual exclusion. Not reentrant, so re-entry is tracked by _operationLockGeneration below.
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    /// <summary>
    /// The session generation in which the current logical flow acquired
    /// <see cref="_operationLock"/>, or 0 when it does not hold it.
    /// </summary>
    /// <remarks>
    /// Set by <see cref="RunExclusiveAsync{TResult}"/> and by the text exchange.
    /// <see cref="AsyncLocal{T}"/> rather than a thread id so it survives an <c>await</c>
    /// resuming on another thread — the same technique the device's <c>_isInsideTextExchange</c>
    /// and <see cref="LifecycleGate"/>'s re-entry flag use.
    /// <para>
    /// A generation rather than a bool because holding the lock and owning the <i>current
    /// session</i> are different questions, and teardown separates them. See
    /// <see cref="HoldsOperationLock"/> and <see cref="OwnsCurrentSession"/>.
    /// </para>
    /// </remarks>
    private readonly AsyncLocal<int> _operationLockGeneration = new();

    /// <summary>
    /// Bumped by every teardown, retiring the ownership of any flow that took the lock in an
    /// earlier session. Starts at 1 so that 0 always means "does not hold the lock".
    /// </summary>
    private int _operationGeneration = 1;

    /// <summary>
    /// Guards <see cref="_operationInFlight"/> and <see cref="_deferredSends"/> as one unit.
    /// </summary>
    /// <remarks>
    /// They have to move together or the deferral leaks: checking the flag and parking the
    /// message in two steps lets an operation finish in between, leaving a message in a list
    /// nobody will ever flush.
    /// </remarks>
    private readonly object _deferralGate = new();

    /// <summary>True while some flow owns the operation lock.</summary>
    private bool _operationInFlight;

    /// <summary>
    /// The backlog: sends parked by <see cref="DaqifiDevice.Send{T}"/> because another flow held
    /// the operation lock. Replayed, in order, by that flow on its way out.
    /// </summary>
    /// <remarks>
    /// Non-null means "a backlog exists", and that is itself a reason to keep deferring — a
    /// message sent straight out while this is draining would overtake messages parked before
    /// it. It therefore stays non-null (and may be empty) for the whole drain, and is nulled
    /// only in the same locked moment the drain observes it empty.
    /// </remarks>
    private Queue<Action>? _deferredSends;

    /// <summary>Backing field for <see cref="DroppedDeferredSendCount"/>.</summary>
    private long _droppedDeferredSendCount;

    /// <summary>
    /// Whether the current backlog has already reported an overflow, so a sender that keeps
    /// overflowing it produces one log line rather than one per dropped message. Guarded by
    /// <see cref="_deferralGate"/>, and cleared wherever the backlog itself is.
    /// </summary>
    private bool _deferralOverflowReported;

    internal OperationSerializer(IOperationSerializationHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <inheritdoc cref="DaqifiDevice.DroppedDeferredSendCount"/>
    internal long DroppedDeferredSendCount => Interlocked.Read(ref _droppedDeferredSendCount);

    /// <summary>
    /// True when this flow acquired the operation lock — in <i>any</i> session.
    /// </summary>
    /// <remarks>
    /// This is the re-entrancy question, and it must stay session-blind. A flow that holds the
    /// semaphore must never be told to wait for it, whatever has happened to the session in the
    /// meantime: <c>ExecuteTextCommandAsync</c> waits on it without a timeout, so answering
    /// "no" to a flow that really does hold it is not a degraded result, it is a hang.
    /// </remarks>
    internal bool HoldsOperationLock => _operationLockGeneration.Value != 0;

    /// <summary>
    /// True when this flow acquired the operation lock in the session that is still current.
    /// </summary>
    /// <remarks>
    /// This is the authority question, and it is the one <see cref="DaqifiDevice.Send{T}"/> asks
    /// before skipping deferral. Exclusivity is a property of a session: once the transport has
    /// been torn down and a new one opened, a flow still running from the old session is not the
    /// owner of the new one and its sends must queue behind that session's rules like anybody
    /// else's. Without this, a flow that outlived its teardown would keep bypassing deferral
    /// into a session it has no claim on.
    /// </remarks>
    private bool OwnsCurrentSession =>
        _operationLockGeneration.Value != 0
        && _operationLockGeneration.Value == Volatile.Read(ref _operationGeneration);

    /// <inheritdoc cref="DaqifiDevice.RunExclusiveAsync{TResult}(Func{CancellationToken, Task{TResult}}, CancellationToken)"/>
    internal async Task<TResult> RunExclusiveAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        // Already ours: run nested, exactly as a reentrant monitor would. This is what lets an
        // exclusive block call GetSdCardFilesAsync — which opens a text exchange on this same
        // lock — instead of deadlocking against a non-reentrant semaphore.
        if (HoldsOperationLock)
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        await AcquireOperationLockAsync(cancellationToken).ConfigureAwait(false);

        // Set HERE rather than inside the helper above: an async method's AsyncLocal writes do
        // not flow back to its caller, only forward to its callees. Assigning it in this frame
        // is what makes the body — and everything the body awaits — see the ownership.
        _operationLockGeneration.Value = Volatile.Read(ref _operationGeneration);
        MarkOperationInFlight();

        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLockGeneration.Value = 0;
            FlushDeferredSends();
            ReleaseOperationLock();
        }
    }

    /// <summary>
    /// Waits for the operation lock on behalf of the text exchange.
    /// </summary>
    /// <remarks>
    /// Deliberately does <b>not</b> translate a disposed semaphore: it throws
    /// <see cref="ObjectDisposedException"/> and lets the engine report the failure in its own
    /// words, matching what callers of the text exchange have always seen.
    /// </remarks>
    internal Task WaitForOperationLockAsync(CancellationToken cancellationToken) =>
        _operationLock.WaitAsync(cancellationToken);

    /// <summary>
    /// Records that this flow now owns the operation lock, and starts deferring other threads'
    /// sends behind it.
    /// </summary>
    /// <remarks>
    /// Synchronous, and must stay that way: the <see cref="AsyncLocal{T}"/> write below flows
    /// forward from whichever frame performs it. Called synchronously from the text exchange
    /// engine's own frame it lands there, exactly as the inline assignment in
    /// <see cref="RunExclusiveAsync{TResult}"/> does; behind an <c>await</c> it would land in a
    /// frame nobody reads and the exchange would stop recognising its own ownership.
    /// </remarks>
    internal void EnterOperationLockOwnership()
    {
        _operationLockGeneration.Value = Volatile.Read(ref _operationGeneration);
        MarkOperationInFlight();
    }

    /// <summary>
    /// Gives up ownership: clears this flow's claim, replays the sends parked while it ran, and
    /// releases the lock.
    /// </summary>
    /// <remarks>Synchronous for the same reason as <see cref="EnterOperationLockOwnership"/>.</remarks>
    internal void ExitOperationLockOwnership()
    {
        _operationLockGeneration.Value = 0;
        FlushDeferredSends();
        ReleaseOperationLock();
    }

    /// <summary>
    /// Waits for the operation lock, translating a disposed semaphore into the same clean
    /// failure every other caller of this lock reports.
    /// </summary>
    private async Task AcquireOperationLockAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            throw new DeviceNotConnectedException(
                "No operation can run exclusively on this device because it is disposed.",
                ex,
                isShuttingDown: true);
        }
    }

    /// <summary>
    /// Publishes "an operation owns the device" so <see cref="DaqifiDevice.Send{T}"/> starts
    /// deferring.
    /// </summary>
    private void MarkOperationInFlight()
    {
        lock (_deferralGate)
        {
            _operationInFlight = true;
        }
    }

    /// <summary>
    /// How many parked messages one operation will replay on its way out before handing the
    /// rest to a background flush.
    /// </summary>
    /// <remarks>
    /// A bound is needed because the operation holding the device is the one doing the
    /// replaying, and senders can refill the backlog while it works — without a bound, a fast
    /// enough sender would keep that operation from ever returning, with the operation lock
    /// held and everything else queued behind it.
    /// </remarks>
    private const int MaxDeferredSendsPerFlush = 64;

    /// <summary>
    /// Sends everything parked while the operation ran, in order, and only then stops deferring.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called before the semaphore is released, so the parked messages are queued ahead of
    /// whatever the next operation does.
    /// </para>
    /// <para>
    /// Deferral stays <b>on</b> for the whole replay. Clearing the flag first and replaying
    /// afterwards would leave a window where another thread sees "nothing in flight", sends
    /// straight to the producer, and overtakes messages parked before it — losing exactly the
    /// ordering this mechanism exists to keep.
    /// </para>
    /// <para>
    /// Every replay runs <i>outside</i> <see cref="_deferralGate"/> — always, with no exception
    /// for a final round. A replayed send can be a blocking stream write, and
    /// <see cref="DaqifiDevice.Send{T}"/> takes that same gate, so replaying under it would make
    /// <c>Send()</c> block on I/O: the exact property deferral was chosen over waiting to
    /// preserve. Messages are therefore taken one at a time, and the backlog stays non-null
    /// while the drain runs, which is what keeps a concurrent send parking behind it instead of
    /// overtaking it.
    /// </para>
    /// <para>
    /// Bounded by <see cref="MaxDeferredSendsPerFlush"/> so a fast sender cannot keep this
    /// operation from returning. Past that the rest is handed to a background flush, which takes
    /// the operation lock exactly as any operation does — so it can never replay into somebody
    /// else's exchange — and drains the same way. The backlog stays non-null across the handoff,
    /// so ordering survives it.
    /// </para>
    /// <para>
    /// A parked send that fails is logged and dropped rather than thrown: the caller was told
    /// the message was accepted before this point, and <see cref="DaqifiDevice.Send{T}"/> has
    /// never guaranteed delivery. The usual case is a device that disconnected while the
    /// operation ran, where throwing here would surface a teardown as the operation's failure.
    /// </para>
    /// </remarks>
    private void FlushDeferredSends()
    {
        for (var sent = 0; sent < MaxDeferredSendsPerFlush; sent++)
        {
            Action next;
            lock (_deferralGate)
            {
                if (_deferredSends == null || _deferredSends.Count == 0)
                {
                    // Drained. Deferral stops here, in the same locked moment that emptiness is
                    // observed, so no message can be parked into a backlog nobody will drain.
                    _deferredSends = null;
                    _operationInFlight = false;
                    _deferralOverflowReported = false;
                    return;
                }

                next = _deferredSends.Dequeue();
            }

            // Outside the gate on purpose: this can block on a stream write, and Send() takes
            // the same gate. The backlog is still non-null, so a send arriving now parks behind
            // what is being replayed rather than overtaking it.
            ReplayDeferredSend(next);
        }

        HandOffRemainingDrain();
    }

    /// <summary>
    /// Hands an unfinished backlog to a background flush so the current operation can return.
    /// </summary>
    /// <remarks>
    /// An empty exclusive operation <i>is</i> a flush: it takes the operation lock, and its own
    /// exit path drains the backlog exactly as this one did. Going through the lock is the point
    /// — a bare background replay could write into a text exchange that started in the meantime.
    /// <see cref="_operationInFlight"/> is deliberately left set and the backlog left non-null,
    /// so sends keep deferring across the handoff and ordering holds; whichever operation next
    /// reaches its exit path (this background one, or a real one that got the lock first) clears
    /// them.
    /// </remarks>
    private void HandOffRemainingDrain()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RunExclusiveAsync<object?>(_ => Task.FromResult<object?>(null), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The only way in is a disposed device, where nothing else can be running and
                // these sends can never reach anything. Drop the backlog rather than leave
                // Send() deferring into one nobody will drain.
                lock (_deferralGate)
                {
                    _deferredSends = null;
                    _operationInFlight = false;
                    _deferralOverflowReported = false;
                }

                SafeLog(() => _host.Logger.LogWarning(
                    ex,
                    "Messages deferred while an exclusive operation was running were dropped: "
                    + "the device went away before they could be sent."));
            }
        });
    }

    /// <summary>Sends one parked message, never throwing.</summary>
    private void ReplayDeferredSend(Action send)
    {
        try
        {
            send();
        }
        catch (Exception ex)
        {
            SafeLog(() => _host.Logger.LogWarning(
                ex,
                "A message deferred while an exclusive operation was running could not be "
                + "sent afterwards; it was dropped."));
        }
    }

    /// <summary>
    /// Returns deferral to its resting state: nothing parked, nothing deferring.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called when a session is torn down. The parked commands were addressed to a connection
    /// that no longer exists, and — the part that matters — the "an operation owns the device"
    /// flag has to go with them.
    /// </para>
    /// <para>
    /// Both, or neither. Dropping the backlog while leaving the flag set is the same hazard
    /// wearing a different hat: the next session would park every
    /// <see cref="DaqifiDevice.Send{T}"/> into a fresh backlog with nothing left to drain it,
    /// and because <c>Send()</c> reports success the moment it parks, the caller would be told
    /// the command was on its way while it silently went nowhere. Teardown is exactly where that
    /// gets stranded, because the operation whose completion would normally clear the flag is
    /// the one that failed to finish inside the bounded wait.
    /// </para>
    /// <para>
    /// The generation bump is what makes the reset safe when a wedged operation is still
    /// holding the lock. Clearing the flag alone would leave that flow bypassing deferral —
    /// <see cref="TryDeferSend{T}"/> asks <see cref="OwnsCurrentSession"/> <i>before</i> it
    /// looks at the flag — so it would keep sending into the next session as though it still
    /// owned it. Retiring the generation ends that claim: the flow keeps the semaphore (only it
    /// can release it, and its exit path still must) but stops counting as the owner of a
    /// session that has been replaced.
    /// </para>
    /// <para>
    /// Note what is deliberately <b>not</b> done here: the reset is unconditional, and in
    /// particular is not gated on teardown having acquired the lock. Gating it would strand
    /// <see cref="_operationInFlight"/> exactly when the lock could not be taken — the case a
    /// bounded teardown exists for — leaving the next session deferring into a backlog with no
    /// drainer. And it would not achieve isolation either, because the stale flow's bypass does
    /// not consult that flag at all. What the flag governs is every <i>other</i> flow; gating it
    /// would silence them and leave the stale one talking.
    /// </para>
    /// </remarks>
    internal void ResetDeferralState()
    {
        // Retire the outgoing session's ownership before reopening the gate, so no flow can be
        // both "not deferring" and "not the owner" at the same instant.
        Interlocked.Increment(ref _operationGeneration);

        lock (_deferralGate)
        {
            _deferredSends = null;
            _operationInFlight = false;
            _deferralOverflowReported = false;
        }
    }

    private void ReleaseOperationLock()
    {
        try
        {
            _operationLock.Release();
        }
        catch (ObjectDisposedException)
        {
            // Raced a Dispose that already tore the semaphore down.
        }
    }

    /// <summary>
    /// Parks a send if it must not go out yet, and reports whether it did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two reasons to park. An operation owns the device, so writing now would land inside its
    /// exchange; or a backlog is still being replayed, so writing now would overtake messages
    /// parked before this one.
    /// </para>
    /// <para>
    /// The flow that owns the lock is never deferred — those are the operation's own commands,
    /// and parking them would leave the operation waiting for itself.
    /// </para>
    /// <para>
    /// The backlog is capped at <see cref="DaqifiDevice.MaxDeferredSends"/> and overflows by
    /// discarding its <b>oldest</b> entries. Drop-oldest is the right way round for what
    /// actually gets parked: these are level-setting commands — set this pin, set that duty
    /// cycle — where the newest instruction is the one the caller currently wants and an older
    /// one it has already superseded is worth less than the memory it costs. Every discarded
    /// message counts into <see cref="DaqifiDevice.DroppedDeferredSendCount"/>.
    /// </para>
    /// <para>
    /// Never blocks: this holds the gate for a queue insert (plus, at the cap, one dequeue) and
    /// nothing else, which is what lets <see cref="DaqifiDevice.Send{T}"/> promise it will not
    /// block. The overflow log deliberately happens after the gate is released, so a slow logger
    /// cannot turn a fire-and-forget send into a wait.
    /// </para>
    /// </remarks>
    internal bool TryDeferSend<T>(IOutboundMessage<T> message)
    {
        if (OwnsCurrentSession)
        {
            return false;
        }

        // Read once, outside the gate: the seam is a property, and reading it again for the log
        // below could report a different number than the one that was actually enforced.
        // Clamped so an override can never drive Dequeue past empty.
        var cap = Math.Max(1, _host.MaxDeferredSends);

        bool reportOverflow;
        lock (_deferralGate)
        {
            if (!_operationInFlight && _deferredSends == null)
            {
                return false;
            }

            var backlog = _deferredSends ??= new Queue<Action>();

            // A loop rather than a single test, so a cap that shrank still converges on the
            // first append after it did.
            var dropped = 0;
            while (backlog.Count >= cap)
            {
                backlog.Dequeue();
                dropped++;
            }

            if (dropped > 0)
            {
                Interlocked.Add(ref _droppedDeferredSendCount, dropped);
            }

            // One line per overflowing backlog, not per dropped message: the sender that
            // overflows a backlog usually goes on overflowing it thousands of times.
            reportOverflow = dropped > 0 && !_deferralOverflowReported;
            if (reportOverflow)
            {
                _deferralOverflowReported = true;
            }

            backlog.Enqueue(() => _host.SendNow(message));
        }

        if (reportOverflow)
        {
            SafeLog(() => _host.Logger.LogWarning(
                "The backlog of messages deferred while an exclusive operation runs reached its "
                + "cap of {Cap}; the oldest are being dropped. See DroppedDeferredSendCount.",
                cap));
        }

        return true;
    }

    /// <summary>
    /// Best-effort coordination with an in-flight operation before teardown: acquire the lock so
    /// the transport is not torn out from under a text exchange that is using it.
    /// </summary>
    /// <remarks>
    /// The lock IS released by <see cref="ReleaseAfterTeardown"/> when acquired (so a future
    /// <c>Connect</c> followed by <c>ExecuteTextCommandAsync</c> isn't blocked); a stuck
    /// exchange that holds past the timeout drops to the <c>_isDisconnecting</c> validation path
    /// inside the exchange.
    /// </remarks>
    /// <param name="wait">How long to wait before tearing down regardless.</param>
    /// <returns><c>true</c> when the lock was acquired and must be released after teardown.</returns>
    internal bool TryAcquireForTeardown(TimeSpan wait)
    {
        // This flow already owns the lock — a Disconnect() from inside RunExclusiveAsync, or
        // from a StatusChanged handler raised within one. Waiting would burn the whole teardown
        // budget on a lock we are holding ourselves and then tear down anyway; run nested
        // instead, and leave the release to the owner. Reported as "not acquired" precisely so
        // the teardown does not release a lock it never took.
        if (HoldsOperationLock)
        {
            return false;
        }

        try
        {
            return _operationLock.Wait(wait);
        }
        catch (ObjectDisposedException)
        {
            // Disconnect called after Dispose — nothing to coordinate.
            return false;
        }
    }

    /// <inheritdoc cref="TryAcquireForTeardown"/>
    /// <param name="wait">How long to wait before tearing down regardless.</param>
    /// <param name="cancellationToken">
    /// Shortens the wait. A cancellation is swallowed rather than propagated: teardown must
    /// still run, and the in-flight exchange sees <c>_isDisconnecting == true</c> and bails out
    /// on its own — the same outcome as letting the wait time out.
    /// </param>
    internal async Task<bool> TryAcquireForTeardownAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        // See TryAcquireForTeardown: re-entry from a flow that already owns the lock runs
        // nested rather than waiting on itself.
        if (HoldsOperationLock)
        {
            return false;
        }

        try
        {
            return await _operationLock.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // DisconnectAsync called after Dispose — nothing to coordinate.
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Releases the lock a teardown took through <see cref="TryAcquireForTeardown"/>.
    /// </summary>
    internal void ReleaseAfterTeardown()
    {
        try
        {
            _operationLock.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Waits, briefly, for messages queued before this exchange to reach the wire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// New sends from other threads are already parked by the time this runs, but anything
    /// queued just before the exchange opened is still on its way out. Written after the
    /// consumer swap, its reply would land in this exchange's lines instead of the protobuf
    /// consumer's — a reply matched to the wrong request. Letting the outbound side go quiet
    /// first puts those replies back where they belong.
    /// </para>
    /// <para>
    /// Waits on <see cref="IMessageProducer{T}.IsIdle"/>, never on
    /// <c>QueuedMessageCount == 0</c>: the count drops as soon as a message is dequeued, which
    /// is <i>before</i> it is written, so a count of zero can mean "still writing". That is the
    /// very case this barrier exists to catch.
    /// </para>
    /// <para>
    /// Bounded, because a device that is not draining its receive buffer must not stall the
    /// exchange: the stale-line boundary still covers what slips through.
    /// </para>
    /// </remarks>
    internal async Task DrainOutboundQueueAsync(CancellationToken cancellationToken)
    {
        var producer = _host.MessageProducer;
        if (producer == null)
        {
            return;
        }

        // Measured on the host's clock (issue #637), and on its monotonic timestamp rather
        // than a wall clock: DateTime.UtcNow stepping mid-drain — NTP, a correction — would
        // either cut this barrier short or hang it well past its budget, and the barrier is
        // what keeps an earlier command's reply out of the next exchange (issue #342).
        var clock = _host.TimeProvider;
        var startedAt = clock.GetTimestamp();
        var budget = _host.OutboundDrainWait;
        while (!producer.IsIdle && clock.GetElapsedTime(startedAt) < budget)
        {
            await Task.Delay(DrainPollInterval, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs <paramref name="logAction"/> without letting a throwing logger take down an
    /// operation — the same isolation <c>DaqifiDevice.SafeLog</c> gives the rest of the device.
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

    public void Dispose()
    {
        _operationLock.Dispose();
    }
}
