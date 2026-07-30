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
    private bool _disposed;
    private Thread? _producerThread;

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

    /// <summary>
    /// Gets a value indicating whether the producer is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

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
                    // Wait for a message to be enqueued or timeout after 100ms
                    _messageAvailable.Wait(100);
                    _messageAvailable.Reset();

                    // Process all available messages
                    while (_messageQueue.TryDequeue(out var message))
                    {
                        try
                        {
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