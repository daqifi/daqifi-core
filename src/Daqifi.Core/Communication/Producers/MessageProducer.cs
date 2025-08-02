using Daqifi.Core.Communication.Messages;
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
    private readonly ConcurrentQueue<IOutboundMessage<T>> _messageQueue;
    private volatile bool _isRunning;
    private bool _disposed;
    private Thread? _producerThread;

    /// <summary>
    /// Initializes a new instance of the MessageProducer class.
    /// </summary>
    /// <param name="stream">The stream to write messages to.</param>
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    public MessageProducer(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
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
        
        // Double-check running state after enqueue to avoid race condition
        if (!_isRunning)
        {
            // If stopped after enqueuing, we should still honor the contract
            // The message is queued and will be processed when/if restarted
        }
    }

    /// <summary>
    /// Background thread method that continuously processes queued messages.
    /// </summary>
    private void ProcessMessages()
    {
        while (_isRunning)
        {
            try
            {
                // Sleep first to avoid busy waiting
                Thread.Sleep(100);
                
                // Process all available messages
                while (_messageQueue.TryDequeue(out var message))
                {
                    try
                    {
                        WriteMessageToStream(message);
                    }
                    catch (Exception)
                    {
                        // Log error but continue processing other messages
                        // TODO: Add proper logging system in future step
                        // For now, silently continue to match desktop behavior during shutdown
                    }
                }
            }
            catch (Exception)
            {
                // Protect the background thread from unexpected exceptions
                // TODO: Add proper logging system in future step
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
        if (!_disposed)
        {
            StopSafely();
            _disposed = true;
        }
    }
}