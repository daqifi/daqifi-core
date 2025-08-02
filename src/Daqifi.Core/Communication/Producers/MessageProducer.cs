using Daqifi.Core.Communication.Messages;
using System.Collections.Concurrent;

namespace Daqifi.Core.Communication.Producers;

/// <summary>
/// Basic implementation of IMessageProducer that handles queuing and sending messages.
/// This version provides the foundation for thread-safe message production.
/// </summary>
/// <typeparam name="T">The type of message data to produce.</typeparam>
public class MessageProducer<T> : IMessageProducer<T>
{
    private readonly Stream _stream;
    private readonly ConcurrentQueue<IOutboundMessage<T>> _messageQueue;
    private volatile bool _isRunning;
    private bool _disposed;

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
    /// Starts the message producer. In this basic version, messages are sent immediately.
    /// </summary>
    public void Start()
    {
        ThrowIfDisposed();
        _isRunning = true;
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
    }

    /// <summary>
    /// Stops the message producer safely, processing remaining messages first.
    /// </summary>
    /// <param name="timeoutMs">Maximum time to wait for pending messages in milliseconds.</param>
    /// <returns>True if all messages were processed, false if timeout occurred.</returns>
    public bool StopSafely(int timeoutMs = 1000)
    {
        var startTime = DateTime.UtcNow;
        
        // Process remaining messages with timeout
        while (!_messageQueue.IsEmpty)
        {
            if ((DateTime.UtcNow - startTime).TotalMilliseconds > timeoutMs)
            {
                Stop(); // Force stop if timeout
                return false;
            }
            
            // Try to process one more message
            if (_messageQueue.TryDequeue(out var message))
            {
                try
                {
                    WriteMessageToStream(message);
                }
                catch
                {
                    // Ignore errors during shutdown
                }
            }
            
            Thread.Sleep(10);
        }
        
        Stop();
        return true;
    }

    /// <summary>
    /// Queues a message for sending. In this basic version, sends immediately if running.
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
        
        // For now, process immediately (Step 2 will add background threading)
        ProcessQueuedMessages();
    }

    /// <summary>
    /// Processes all currently queued messages.
    /// </summary>
    private void ProcessQueuedMessages()
    {
        while (_messageQueue.TryDequeue(out var message))
        {
            try
            {
                WriteMessageToStream(message);
            }
            catch (Exception ex)
            {
                // TODO: Add proper logging in Step 2  
                // For now, re-throw to maintain error visibility
                throw new InvalidOperationException($"Failed to send message: {ex.Message}", ex);
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