using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace Daqifi.Core.Communication.Producers;

/// <summary>
/// Thread-safe implementation of IMessageProducer that handles queuing and sending messages.
/// Uses a background thread to process messages from a concurrent queue.
/// </summary>
/// <typeparam name="T">The type of message data to produce.</typeparam>
public class MessageProducer<T> : IMessageProducer<T>
{
    private readonly Stream _stream;
    private readonly ILogger<MessageProducer<T>> _logger;
    private readonly ITransportHealthSink? _healthSink;
    private readonly ConcurrentQueue<IOutboundMessage<T>> _messageQueue;
    private readonly ManualResetEventSlim _messageAvailable = new(false);
    private volatile bool _isRunning;

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
    private bool _disposed;
    private Thread? _producerThread;

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
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Number of times the background loop has woken from its wait over the life of this instance.
    /// A producer with nothing to send must never wake at all, so this stays at its value for as
    /// long as the queue is empty and no stop has been requested.
    /// </summary>
    /// <remarks>
    /// Monotonic, and deliberately not reset by <see cref="Start"/>: a stop whose
    /// <see cref="Thread.Join(int)"/> timed out leaves the previous background thread alive and
    /// still able to increment this, so a per-run counter would be a counter with a race in it. The
    /// property it exists to express — that an idle producer does not accumulate wakeups — is a
    /// statement about the value not changing, which a cumulative count states just as well.
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
    public void Start()
    {
        ThrowIfDisposed();
        
        if (_isRunning)
            return; // Already running
            
        _isRunning = true;
        _producerThread = new Thread(ProcessMessages)
        {
            IsBackground = true,
            Name = $"MessageProducer-{typeof(T).Name}"
        };
        _producerThread.Start();
    }

    /// <summary>
    /// Stops the message producer immediately, clearing any pending messages.
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
        _messageAvailable.Set();

        // Clear the queue
        while (_messageQueue.TryDequeue(out _))
        {
            // Empty the queue
        }

        // Wait for thread to finish
        _producerThread?.Join(1000);
        _producerThread = null;
    }

    /// <summary>
    /// Stops the message producer safely, waiting for pending messages to be processed.
    /// </summary>
    /// <param name="timeoutMs">Maximum time to wait for pending messages in milliseconds.</param>
    /// <returns>True if all messages were processed, false if timeout occurred.</returns>
    public bool StopSafely(int timeoutMs = 1000)
    {
        if (!_isRunning)
            return true;
            
        var startTime = DateTime.UtcNow;
        
        // Wait for queue to empty with timeout
        while (!_messageQueue.IsEmpty)
        {
            if ((DateTime.UtcNow - startTime).TotalMilliseconds > timeoutMs)
            {
                // Timeout - force stop
                Stop();
                return false;
            }
            
            // Give the background thread time to process
            Thread.Sleep(10);
        }
        
        // Queue is empty, now stop normally
        _isRunning = false;
        _messageAvailable.Set();
        _producerThread?.Join(1000);
        _producerThread = null;
        
        return true;
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
            
        if (!_isRunning)
            throw new InvalidOperationException("Message producer is not running. Call Start() first.");

        _messageQueue.Enqueue(message);
        _messageAvailable.Set();
    }

    /// <summary>
    /// Background thread method that continuously processes queued messages.
    /// </summary>
    private void ProcessMessages()
    {
        try
        {
            while (_isRunning)
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
                    //   - Stop() and StopSafely() clear _isRunning and then set, and the while
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
            // StopSafely() block until timeout.
            _isRunning = false;
            SafeLog(() => _logger.LogError(ex, "MessageProducer background loop terminated abnormally."));
        }
    }

    /// <summary>
    /// Invokes a logging action, swallowing any exception thrown by the logger
    /// itself. A faulting logger must never be allowed to terminate the background
    /// processing loop or leave the producer in an inconsistent state.
    /// </summary>
    private static void SafeLog(Action logAction)
    {
        try
        {
            logAction();
        }
        catch
        {
            // A logger that throws is not permitted to take down the producer.
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
        if (!_disposed)
        {
            StopSafely();
            _messageAvailable.Dispose();
            _disposed = true;
        }
    }
}