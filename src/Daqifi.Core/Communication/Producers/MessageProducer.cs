using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using static Daqifi.Core.Internal.DiagnosticGuard;

namespace Daqifi.Core.Communication.Producers;

/// <summary>
/// Thread-safe implementation of IMessageProducer that handles queuing and sending messages.
/// Uses a background thread to process messages from a concurrent queue.
/// </summary>
/// <typeparam name="T">The type of message data to produce.</typeparam>
public class MessageProducer<T> : IMessageProducer<T>
{
    /// <summary>
    /// How long <see cref="Stop"/> and <see cref="StopSafely"/> wait for the writer thread to exit.
    /// </summary>
    private const int StopJoinTimeoutMs = 1000;

    /// <summary>
    /// Grace period, in milliseconds, that <see cref="Start"/> waits for a stopped-but-not-yet-exited
    /// writer thread before refusing to start.
    /// </summary>
    /// <remarks>
    /// A writer that has been asked to stop exits as soon as its in-flight write returns, so waiting
    /// for that is always correct, and it is what lets a restart succeed instead of failing the
    /// caller. Only a writer whose write does not return at all outlasts this, and that means the
    /// stream itself is stuck.
    /// </remarks>
    private const int StaleWriterGraceMs = 1000;

    /// <summary>
    /// Where the producer is in its lifecycle. <see cref="_state"/> is the one place this lives.
    /// </summary>
    private enum RunState
    {
        /// <summary>No writer thread exists. The only state a new writer may be started from.</summary>
        Stopped,

        /// <summary>The writer thread is processing messages.</summary>
        Running,

        /// <summary>A stop was requested and the writer thread has not exited yet.</summary>
        StopRequested,
    }

    private readonly Stream _stream;
    private readonly ILogger<MessageProducer<T>> _logger;
    private readonly ITransportHealthSink? _healthSink;
    private readonly ConcurrentQueue<IOutboundMessage<T>> _messageQueue;
    private readonly ManualResetEventSlim _messageAvailable = new(false);

    /// <summary>
    /// Waitable form of <see cref="IsIdle"/>. Starts set: a newly constructed producer has
    /// nothing queued and is not writing.
    /// </summary>
    /// <remarks>
    /// <see cref="StopSafely"/> waits on this with its caller-supplied timeout instead of
    /// polling the queue with <c>Thread.Sleep(10)</c>. Disconnect/Dispose used to sit on that
    /// 10 ms tick for as long as anything was still queued behind an in-flight write, even
    /// though the background loop already knew when it had gone idle. The event is the
    /// waitable half of the same state <see cref="IsIdle"/> reports.
    /// </remarks>
    private readonly ManualResetEventSlim _idle = new(initialState: true);

    /// <summary>
    /// Makes <see cref="Send"/>'s reset-and-enqueue one step as far as the background loop's
    /// idle signal is concerned. See <see cref="SignalIdleIfQuiet"/>.
    /// </summary>
    private readonly object _idleSync = new();

    /// <summary>
    /// Guards every change to <see cref="_state"/>, <see cref="_producerThread"/> and
    /// <see cref="_disposed"/>, so two calls to <see cref="Start"/> cannot both start a writer.
    /// </summary>
    /// <remarks>
    /// Only ever held for those few assignments, never across a <see cref="Thread.Join(int)"/> or
    /// anything else that waits on the writer. The writer takes this lock on its way out, and its
    /// <see cref="SendFailed"/> subscribers can call back into <see cref="Stop"/> or
    /// <see cref="Start"/>, so a wait on the writer with this held could only time out.
    /// </remarks>
    private readonly object _lifecycleSync = new();

    /// <summary>
    /// The lifecycle state. <see cref="_draining"/> is deliberately not part of it: that flag says
    /// whether a batch is in flight, which is a different question with its own ordering contract
    /// against the queue (see <see cref="IsIdle"/>), and it changes on every batch within
    /// <see cref="RunState.Running"/> and during <see cref="RunState.StopRequested"/> alike.
    /// </summary>
    private volatile RunState _state = RunState.Stopped;

    /// <summary>
    /// True while the background thread is part-way through draining a batch, including the time
    /// it spends inside the blocking stream write.
    /// </summary>
    /// <remarks>
    /// Claimed <b>before</b> the dequeue that starts a batch, not after: a message leaves the queue
    /// before it is written, so setting this afterwards would leave an instant where the queue is
    /// empty and nothing yet reports a write in progress — exactly the gap
    /// <see cref="IsIdle"/> exists to close. Claiming it early is only half of that guarantee; the
    /// other half is the order <see cref="IsIdle"/> reads the two fields in.
    /// </remarks>
    private volatile bool _draining;
    private volatile bool _disposed;

    /// <summary>
    /// The writer thread: set whenever <see cref="_state"/> is not <see cref="RunState.Stopped"/>.
    /// Cleared only by the writer itself, as the last thing it does, so a stop whose join timed out
    /// cannot lose track of a thread that is still writing.
    /// </summary>
    private volatile Thread? _producerThread;

    /// <summary>
    /// Number of times the background loop has returned from its wait. Exposed for tests: an idle
    /// producer must not accumulate wakeups (issue #491).
    /// </summary>
    private long _wakeCount;

    /// <summary>
    /// Number of writes the background loop has started. See <see cref="StartedWriteCount"/>.
    /// </summary>
    private long _startedWriteCount;

    /// <summary>
    /// Initializes a new instance of the MessageProducer class.
    /// </summary>
    /// <param name="stream">The stream to write messages to.</param>
    /// <param name="logger">
    /// Optional logger used to surface write failures and background-loop lifecycle
    /// events. When omitted, a <see cref="NullLogger{T}"/> is used so existing
    /// consumers behave exactly as before.
    /// </param>
    /// <param name="healthSink">
    /// Optional transport to report write outcomes to. A write that keeps failing is, like a read
    /// that keeps failing, evidence the device is gone; reporting it lets the transport escalate a
    /// dead link to a lost connection instead of the producer quietly draining into nothing
    /// (issue #382). When null, write failures are only logged, exactly as before.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    public MessageProducer(Stream stream, ILogger<MessageProducer<T>>? logger = null,
        ITransportHealthSink? healthSink = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _logger = logger ?? NullLogger<MessageProducer<T>>.Instance;
        _healthSink = healthSink;
        _messageQueue = new ConcurrentQueue<IOutboundMessage<T>>();
    }

    /// <summary>
    /// Gets the number of messages currently queued for sending.
    /// </summary>
    public int QueuedMessageCount => _messageQueue.Count;

    /// <inheritdoc />
    /// <remarks>
    /// The queue is read <b>before</b> the draining flag, and the order is load-bearing.
    /// These are two separate reads, so a caller can straddle the background thread's handover
    /// from "queued" to "being written"; only this order makes the straddle report busy rather
    /// than idle. Read the other way round, a caller could see <c>_draining</c> still false (the
    /// loop has woken but not yet claimed the batch), then — after the loop claims it and
    /// dequeues — see an empty queue, and conclude the producer was idle while a write was
    /// actually in flight. That is the same false "all quiet" that
    /// <see cref="QueuedMessageCount"/> gives on its own, which is the gap this property exists
    /// to close. In this order the straddle is harmless: whichever side of the handover the reads
    /// land on, one of them still reports work outstanding.
    /// </remarks>
    public bool IsIdle => _messageQueue.IsEmpty && !_draining;

    /// <summary>
    /// Gets a value indicating whether the producer is currently running.
    /// </summary>
    public bool IsRunning => _state == RunState.Running;

    /// <summary>
    /// Number of times the background loop has woken from its wait over the life of this instance.
    /// A producer with nothing to send must never wake at all, so this stays at its value for as
    /// long as the queue is empty and no stop has been requested.
    /// </summary>
    /// <remarks>
    /// Monotonic, and deliberately not reset by <see cref="Start"/>. The property it exists to
    /// express — that an idle producer does not accumulate wakeups — is a statement about the value
    /// not changing, which a cumulative count states just as well.
    /// </remarks>
    internal long WakeCount => Interlocked.Read(ref _wakeCount);

    /// <inheritdoc />
    /// <remarks>
    /// Incremented on the background thread immediately before the write, and read here with an
    /// interlocked read so a reader on another thread — the text exchange's line collector — cannot
    /// see a torn or stale value. The pairing is what makes the count a happens-before marker: the
    /// increment precedes the bytes leaving, so a reader that still sees the old value knows the
    /// bytes had not left when it looked.
    /// </remarks>
    public long? StartedWriteCount => Interlocked.Read(ref _startedWriteCount);

    /// <inheritdoc />
    public event EventHandler<MessageSendFailedEventArgs<T>>? SendFailed;

    /// <summary>
    /// Starts the message producer, beginning background message processing.
    /// </summary>
    /// <remarks>
    /// If a previous writer thread has been stopped but has not yet exited, this waits up to
    /// <see cref="StaleWriterGraceMs"/> for it rather than failing immediately.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Thrown when this instance has been disposed.</exception>
    /// <exception cref="ProducerThreadNotExitedException">
    /// Thrown when a previous writer thread is still alive after the grace period, or when this is
    /// called from that thread itself, so starting would put two concurrent writers on the same
    /// stream. A writer that outlasts the grace is one whose write is not returning at all, which
    /// means the stream is stuck. A fresh producer against that same stream would not help.
    /// </exception>
    public void Start()
    {
        while (true)
        {
            Thread staleWriter;
            lock (_lifecycleSync)
            {
                // Inside the lock, because Dispose sets the flag under it: no writer can be started
                // after disposal has begun.
                ThrowIfDisposed();

                if (_state == RunState.Running)
                    return; // Already running

                if (_state == RunState.Stopped)
                {
                    // The writer reads the state as soon as it runs, so publish that first.
                    _state = RunState.Running;
                    _producerThread = new Thread(ProcessMessages)
                    {
                        IsBackground = true,
                        Name = $"MessageProducer-{typeof(T).Name}"
                    };
                    _producerThread.Start();
                    return;
                }

                staleWriter = _producerThread!;
            }

            // A stop whose join timed out leaves the old writer alive. Starting another would put
            // two writers on one stream, where one message's bytes can land in the middle of
            // another's. Give it a bounded chance to finish its write and exit, with the lock
            // released so the writer can get out. If we ARE that writer (Start called from a
            // SendFailed handler after a stop), waiting could only time out, so refuse immediately.
            if (ReferenceEquals(staleWriter, Thread.CurrentThread) || !staleWriter.Join(StaleWriterGraceMs))
            {
                throw new ProducerThreadNotExitedException();
            }

            // The writer marked the producer stopped on its way out. Go round again rather than
            // start one here: another caller may have started a writer during that wait.
        }
    }

    /// <summary>
    /// Stops the message producer immediately, clearing any pending messages.
    /// </summary>
    /// <remarks>
    /// If the writer thread does not exit within the wait, the producer stays in the stop-requested
    /// state until it does, and <see cref="Start"/> will not start another writer alongside it.
    /// </remarks>
    public void Stop()
    {
        var writer = RequestStop();

        // Clear the queue
        while (_messageQueue.TryDequeue(out _))
        {
            // Empty the queue
        }

        // Wait for thread to finish
        JoinWriter(writer);
    }

    /// <summary>
    /// Stops the message producer safely, waiting for pending messages to be processed.
    /// </summary>
    /// <param name="timeoutMs">Maximum time to wait for pending messages in milliseconds.</param>
    /// <returns>True if all messages were processed, false if timeout occurred.</returns>
    /// <remarks>
    /// Waits on <see cref="IsIdle"/> — nothing queued <b>and</b> no write in flight — rather
    /// than on the queue being empty. A message leaves the queue before it is written, so an
    /// empty-queue wait can return while bytes are still on their way out; the idle wait
    /// covers that write, which is what "pending messages were sent" means. The wait is the
    /// idle event, not a sleep-poll: once the loop goes idle, this returns without sitting
    /// on a 10 ms tick.
    /// <para>
    /// As with <see cref="Stop"/>, a writer thread that does not exit within the wait is still
    /// tracked, and <see cref="Start"/> will not start another writer alongside it. Called from the
    /// writer thread itself (a <see cref="SendFailed"/> handler), this cannot wait for its own write
    /// to finish, so it stops at once and returns false.
    /// </para>
    /// </remarks>
    public bool StopSafely(int timeoutMs = 1000)
    {
        if (_state != RunState.Running)
            return true;

        if (ReferenceEquals(_producerThread, Thread.CurrentThread) || !_idle.Wait(Math.Max(0, timeoutMs)))
        {
            // Timeout - force stop
            Stop();
            return false;
        }

        // Idle: nothing queued and no write in flight. Stop the parked loop.
        JoinWriter(RequestStop());

        return true;
    }

    /// <summary>
    /// Asks a running writer to stop and wakes it if it is parked.
    /// </summary>
    /// <returns>The writer thread to wait for, or null if there is none.</returns>
    private Thread? RequestStop()
    {
        Thread? writer;
        lock (_lifecycleSync)
        {
            if (_state == RunState.Running)
            {
                _state = RunState.StopRequested;
            }

            writer = _producerThread;
        }

        _messageAvailable.Set();
        return writer;
    }

    /// <summary>
    /// Waits a bounded time for the writer to exit, unless the caller is the writer, which could
    /// only wait out the timeout on itself. It exits once its callback returns.
    /// </summary>
    private static void JoinWriter(Thread? writer)
    {
        if (writer is not null && !ReferenceEquals(writer, Thread.CurrentThread))
        {
            writer.Join(StopJoinTimeoutMs);
        }
    }

    /// <summary>
    /// Queues a message for sending. The background thread will process it asynchronously.
    /// </summary>
    /// <remarks>
    /// This call returns before the write happens, so delivery is not guaranteed: a write that
    /// fails on the background thread does not throw back here. Subscribe to
    /// <see cref="SendFailed"/> if the caller needs to know a specific message was not delivered.
    /// </remarks>
    /// <param name="message">The message to send.</param>
    /// <exception cref="ArgumentNullException">Thrown when message is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when producer is not running.</exception>
    public void Send(IOutboundMessage<T> message)
    {
        ThrowIfDisposed();
        
        if (message == null)
            throw new ArgumentNullException(nameof(message));
            
        if (_state != RunState.Running)
            throw new InvalidOperationException("Message producer is not running. Call Start() first.");

        // Reset and enqueue under the same lock the background loop takes to signal idle, so
        // the loop cannot find the queue empty and then signal idle after this message landed.
        // Without the lock, "check empty -> Send resets and enqueues -> set" left the event set
        // with a message queued, and a Disconnect straight after a Send (the usual "stop
        // streaming, then disconnect" teardown) could stop the loop before writing it.
        lock (_idleSync)
        {
            _idle.Reset();
            _messageQueue.Enqueue(message);
        }

        _messageAvailable.Set();
    }

    /// <summary>
    /// Background thread method that continuously processes queued messages.
    /// </summary>
    private void ProcessMessages()
    {
        try
        {
            while (_state == RunState.Running)
            {
                try
                {
                    // Park until something actually happens. There is no timeout: a connected but
                    // silent device used to cost 10 wakeups a second here, forever, per device
                    // (issue #491), and the timeout was never load-bearing.
                    //
                    // Every state change that this loop must observe sets the event, and
                    // ManualResetEventSlim is sticky, so none of them can be missed:
                    //   - Send() enqueues and then sets, so a set that is observed always has its
                    //     message already visible to the TryDequeue below;
                    //   - Stop() and StopSafely() move _state off Running and then set, and the while
                    //     condition above is re-read after this iteration, so a set consumed by the
                    //     Reset() below still exits the loop on the next pass;
                    //   - a set that arrives after Reset() but before the drain is picked up by the
                    //     drain itself, and one that arrives after the drain leaves the event set,
                    //     so the next wait returns immediately.
                    // The Reset() stays *before* the drain for that last reason: resetting after it
                    // would discard a set whose message the drain had already passed.
                    _messageAvailable.Wait();
                    Interlocked.Increment(ref _wakeCount);
                    _messageAvailable.Reset();

                    // Claimed around the whole batch rather than around each write, so there is
                    // never an instant where a message has left the queue but no write is yet
                    // reported in progress. Conservative in the other direction (it stays set
                    // between messages of a batch), which is the safe way to be wrong.
                    _draining = true;
                    try
                    {
                        // Process all available messages
                        while (_messageQueue.TryDequeue(out var message))
                        {
                            try
                            {
                                // Claimed BEFORE the write, and counted even if the write then
                                // throws. Readers on other threads use this as the marker for
                                // "nothing this producer was given can have been answered yet"
                                // (issue #593), so it has to rise no later than the bytes leave —
                                // a marker that rises afterwards can land, on a preempted thread,
                                // after a fast device has already replied.
                                Interlocked.Increment(ref _startedWriteCount);

                                WriteMessageToStream(message);

                                // A successful write clears any run of failures the transport has
                                // accumulated: the link is demonstrably alive.
                                _healthSink?.ReportIoSuccess();
                            }
                            catch (Exception ex)
                            {
                                // Surface the failure but keep draining the queue so a single
                                // bad write doesn't stall the remaining messages. Tell the transport
                                // too — it is the only component that can decide a run of failures
                                // means the device is gone rather than glitching.
                                //
                                // A write TIMEOUT is deliberately excluded: it means the device is not
                                // draining its receive buffer right now (busy, or flow-controlled), not
                                // that the link is gone. Treating it as evidence of a disconnect could
                                // tear down a healthy connection to a momentarily busy device — the
                                // same reason the reader loop treats a read timeout as benign.
                                var isTimeout = ex is TimeoutException;
                                if (!isTimeout)
                                {
                                    _healthSink?.ReportIoFault(ex);
                                }

                                // A timeout gets its own greppable message so "the write never
                                // happened because the device isn't draining" (busy/flow-controlled)
                                // can be told apart from any other write failure in the logs.
                                var logMessage = isTimeout
                                    ? "Timed out writing message to the stream; continuing with remaining queued messages."
                                    : "Failed to write message to the stream; continuing with remaining queued messages.";
                                SafeLog(() => _logger.LogWarning(ex, logMessage));

                                // The only signal a caller gets that this specific message was not
                                // delivered (issue #408). A throwing subscriber must not take down
                                // the background loop, so this goes through the same SafeLog guard
                                // used for the logger above.
                                SafeLog(() => SendFailed?.Invoke(this, new MessageSendFailedEventArgs<T>(message, ex)));
                            }
                        }
                    }
                    finally
                    {
                        _draining = false;
                        SignalIdleIfQuiet();
                    }
                }
                catch (Exception ex)
                {
                    // Protect the background thread from unexpected exceptions so the
                    // producer keeps running rather than dying silently.
                    SafeLog(() => _logger.LogError(ex, "Unexpected error in the MessageProducer background loop; the loop will continue running."));
                }
            }

            SafeLog(() => _logger.LogInformation("MessageProducer background loop exited cleanly after a stop was requested."));
        }
        catch (Exception ex)
        {
            // Last-resort handler. Every logging call in the loop is routed through
            // SafeLog, so a faulting logger can no longer unwind the loop and this
            // should be unreachable. If anything ever does escape, we must not leave
            // the producer advertising IsRunning=true while the background thread is
            // dead: that would let Send() enqueue messages that never drain and make
            // StopSafely() block until timeout. The finally below settles the state.
            _draining = false;
            _idle.Set();
            SafeLog(() => _logger.LogError(ex, "MessageProducer background loop terminated abnormally."));
        }
        finally
        {
            // The last thing this thread does, so "Stopped" means no writer can touch the stream
            // again. This cannot overwrite a newer writer's state: Start() only starts one from
            // Stopped, and nothing but this line gets there while this thread exists.
            lock (_lifecycleSync)
            {
                _state = RunState.Stopped;
                _producerThread = null;
            }
        }
    }

    /// <summary>
    /// Marks the producer idle if the queue is still empty after a drain.
    /// </summary>
    /// <remarks>
    /// The empty check and the Set happen under <see cref="_idleSync"/>, which
    /// <see cref="Send"/> also holds across its Reset and Enqueue. That makes the pair atomic
    /// with respect to a Send: either the message is already queued (no Set), or it is
    /// enqueued after the Set and its Reset follows. A set-then-recheck without the lock is
    /// not enough, because a waiter can wake on the transient Set before the recheck undoes it.
    /// </remarks>
    private void SignalIdleIfQuiet()
    {
        lock (_idleSync)
        {
            if (_messageQueue.IsEmpty)
            {
                _idle.Set();
            }
        }
    }

    /// <summary>
    /// Writes a message to the underlying stream.
    /// </summary>
    /// <param name="message">The message to write.</param>
    private void WriteMessageToStream(IOutboundMessage<T> message)
    {
        var bytes = message.GetBytes();
        _stream.Write(bytes, 0, bytes.Length);
        _stream.Flush(); // Ensure message is sent immediately
    }

    /// <summary>
    /// Throws ObjectDisposedException if this instance has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MessageProducer<T>));
    }

    /// <summary>
    /// Disposes the message producer and releases resources.
    /// </summary>
    public void Dispose()
    {
        // Set under the lock Start() checks it under, and before the stop below rather than after
        // the teardown, so a racing Start() cannot put a writer on events that are being disposed.
        lock (_lifecycleSync)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        StopSafely();
        _messageAvailable.Dispose();
        _idle.Dispose();
    }
}