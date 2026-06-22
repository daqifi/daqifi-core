using Daqifi.Core.Communication.Messages;
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
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    public MessageProducer(Stream stream, ILogger<MessageProducer<T>>? logger = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _logger = logger ?? NullLogger<MessageProducer<T>>.Instance;
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
                        }
                        catch (Exception ex)
                        {
                            // Surface the failure but keep draining the queue so a single
                            // bad write doesn't stall the remaining messages.
                            SafeLog(() => _logger.LogWarning(ex, "Failed to write message to the stream; continuing with remaining queued messages."));
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