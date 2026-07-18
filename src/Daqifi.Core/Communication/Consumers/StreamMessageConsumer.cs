using Daqifi.Core.Communication.Messages;
using System.Text;

namespace Daqifi.Core.Communication.Consumers;

/// <summary>
/// Stream-based message consumer that reads messages from a stream using background processing.
/// Handles line-based text protocols (like SCPI responses) and binary data parsing.
/// </summary>
/// <typeparam name="T">The type of message data to consume.</typeparam>
public class StreamMessageConsumer<T> : IMessageConsumer<T>
{
    private readonly Stream _stream;
    private readonly IMessageParser<T> _messageParser;
    private readonly byte[] _buffer;
    private readonly List<byte> _messageBuffer;

    /// <summary>
    /// Guards every access to <see cref="_messageBuffer"/>. The consumer thread appends to and
    /// drains the buffer while callers can query <see cref="QueuedMessageCount"/> or request a
    /// clear on their own thread; <see cref="List{T}"/> is not safe for concurrent mutation.
    /// </summary>
    private readonly object _bufferLock = new();

    /// <summary>
    /// Set by <see cref="ClearBuffer"/> when the consumer thread is running, so the clear (buffer
    /// reset + stream drain) is performed on the consumer thread itself rather than racing it.
    /// </summary>
    private volatile bool _clearRequested;

    private volatile bool _isRunning;
    private Thread? _consumerThread;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the StreamMessageConsumer class.
    /// </summary>
    /// <param name="stream">The stream to read messages from.</param>
    /// <param name="messageParser">The parser to convert raw data to messages.</param>
    /// <param name="bufferSize">The size of the read buffer in bytes.</param>
    public StreamMessageConsumer(Stream stream, IMessageParser<T> messageParser, int bufferSize = 4096)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _messageParser = messageParser ?? throw new ArgumentNullException(nameof(messageParser));
        _buffer = new byte[bufferSize];
        _messageBuffer = new List<byte>();
    }

    /// <summary>
    /// Gets a value indicating whether the consumer is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Gets the number of bytes currently in the message buffer.
    /// </summary>
    public int QueuedMessageCount
    {
        get
        {
            lock (_bufferLock)
            {
                return _messageBuffer.Count;
            }
        }
    }

    /// <summary>
    /// Occurs when a message is received and parsed from the device.
    /// </summary>
    public event EventHandler<MessageReceivedEventArgs<T>>? MessageReceived;

    /// <summary>
    /// Occurs when an error occurs during message processing.
    /// </summary>
    public event EventHandler<MessageConsumerErrorEventArgs>? ErrorOccurred;

    /// <summary>
    /// Starts the message consumer, beginning background message reading.
    /// </summary>
    public void Start()
    {
        ThrowIfDisposed();

        if (_isRunning)
            return; // Already running

        // A prior Stop()/StopSafely() whose Join timed out leaves the old reader thread alive.
        // Refuse to spawn a second reader against the same stream/buffer — two concurrent
        // Stream.Read loops would reintroduce the framing corruption this class guards against.
        if (_consumerThread is { IsAlive: true })
        {
            throw new InvalidOperationException(
                "Cannot start the consumer: a previous consumer thread has not yet exited.");
        }

        _clearRequested = false;
        _isRunning = true;
        _consumerThread = new Thread(ProcessMessages)
        {
            IsBackground = true,
            Name = $"MessageConsumer-{typeof(T).Name}"
        };
        _consumerThread.Start();
    }

    /// <summary>
    /// Stops the message consumer immediately.
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
        var stopped = _consumerThread?.Join(1000) ?? true;
        if (stopped)
        {
            // Only tear down once the reader has actually exited: clearing the buffer (and later
            // letting Start() reuse the slot) while the thread is still alive would race it.
            _consumerThread = null;
            lock (_bufferLock)
            {
                _messageBuffer.Clear();
            }
        }
    }

    /// <summary>
    /// Stops the message consumer safely, waiting for current processing to complete.
    /// </summary>
    /// <param name="timeoutMs">Maximum time to wait for processing to complete in milliseconds.</param>
    /// <returns>True if stopped cleanly, false if timeout occurred.</returns>
    public bool StopSafely(int timeoutMs = 1000)
    {
        if (!_isRunning)
            return true;

        _isRunning = false;
        var stopped = _consumerThread?.Join(timeoutMs) ?? true;
        if (stopped)
        {
            // Only clear once the reader has exited. Doing so unconditionally would (a) let a
            // later Start() spawn a second reader over a still-alive one, and (b) block past the
            // advertised timeout here if the reader is holding _bufferLock in a slow parse.
            _consumerThread = null;
            lock (_bufferLock)
            {
                _messageBuffer.Clear();
            }
        }

        return stopped;
    }

    /// <summary>
    /// Clears any buffered data from the stream and internal buffers. Useful for devices that may
    /// have residual data on connection (e.g. after a reconnect).
    /// </summary>
    /// <remarks>
    /// Safe to call while the consumer thread is running. When it is, the actual clear is marshaled
    /// onto the consumer thread — the buffer reset and stream drain happen there — so this never
    /// mutates <see cref="_messageBuffer"/> concurrently with the reader and never issues a second
    /// <see cref="Stream.Read(byte[], int, int)"/> that would overlap the reader's own read and
    /// corrupt message framing. In that case the clear takes effect on the next consumer-loop
    /// iteration rather than synchronously on return.
    /// <para>
    /// If a consumer thread was just stopped but hasn't fully exited yet (the stop path's Join is
    /// time-bounded), the inline stream drain is deferred until that thread provably exits; if it
    /// doesn't exit in time, only the in-memory buffer is cleared and the stream drain is skipped,
    /// so the caller's <see cref="Stream.Read(byte[], int, int)"/> can never overlap the reader's.
    /// </para>
    /// </remarks>
    public void ClearBuffer()
    {
        ThrowIfDisposed();

        if (_isRunning)
        {
            // The consumer thread owns the stream and the message buffer; hand the work to it.
            _clearRequested = true;
            return;
        }

        // Not running — but a just-stopped reader may still be finishing its final Read during the
        // stop path's time-bounded Join window. Drain the stream ourselves only once that thread
        // has provably exited, so our Read can't overlap its Read.
        var thread = _consumerThread;
        if (thread is { IsAlive: true } && !thread.Join(1000))
        {
            // Reader still alive; don't risk an overlapping Stream.Read. Clear just the in-memory
            // buffer (always lock-guarded) and skip the stream drain.
            lock (_bufferLock)
            {
                _messageBuffer.Clear();
            }
            return;
        }

        PerformClear();
    }

    /// <summary>
    /// Resets the message buffer and drains any residual bytes from the stream. Must only run on
    /// the thread that currently owns the stream/buffer (the consumer thread while running, or the
    /// caller of <see cref="ClearBuffer"/> when it is not).
    /// </summary>
    private void PerformClear()
    {
        lock (_bufferLock)
        {
            _messageBuffer.Clear();
        }

        // Drain any available data from the stream (if it's a NetworkStream)
        try
        {
            if (_stream.CanRead && _stream is System.Net.Sockets.NetworkStream networkStream)
            {
                var tempBuffer = new byte[_buffer.Length];
                while (networkStream.DataAvailable)
                {
                    _ = _stream.Read(tempBuffer, 0, tempBuffer.Length);
                }
            }
        }
        catch
        {
            // Ignore errors during buffer clearing
        }
    }

    /// <summary>
    /// Background thread method that continuously reads and processes messages.
    /// </summary>
    private void ProcessMessages()
    {
        while (_isRunning)
        {
            try
            {
                // Honor a pending ClearBuffer() request on this thread, so the buffer reset and the
                // stream drain never race the reader below.
                if (_clearRequested)
                {
                    _clearRequested = false;
                    PerformClear();
                }

                // Check if data is available to avoid blocking
                if (!_stream.CanRead)
                {
                    Thread.Sleep(10);
                    continue;
                }

                // Try to read data from stream
                int bytesRead = 0;
                try
                {
                    bytesRead = _stream.Read(_buffer, 0, _buffer.Length);
                }
                catch (TimeoutException)
                {
                    // Expected when no data is available within ReadTimeout; just loop
                    continue;
                }
                catch (Exception ex)
                {
                    OnErrorOccurred(ex);
                    Thread.Sleep(100); // Back off on error
                    continue;
                }

                if (bytesRead == 0)
                {
                    Thread.Sleep(10); // No data available, wait briefly
                    continue;
                }

                // Add received data to message buffer (guarded: a caller may be reading
                // QueuedMessageCount and ClearBuffer's drain runs on this same thread).
                lock (_bufferLock)
                {
                    for (int i = 0; i < bytesRead; i++)
                    {
                        _messageBuffer.Add(_buffer[i]);
                    }
                }

                // Try to parse complete messages from buffer
                ProcessMessageBuffer();
            }
            catch (Exception ex) when (_isRunning)
            {
                // Only report errors if we're still supposed to be running
                OnErrorOccurred(ex);
            }
        }
    }

    /// <summary>
    /// Processes the message buffer and extracts complete messages.
    /// </summary>
    private void ProcessMessageBuffer()
    {
        byte[] bufferData;
        IEnumerable<IInboundMessage<T>> messages;

        // Snapshot, parse, and drain the buffer under the lock; dispatch events outside it so a
        // subscriber callback never runs while the lock is held.
        lock (_bufferLock)
        {
            bufferData = _messageBuffer.ToArray();
            messages = _messageParser.ParseMessages(bufferData, out var consumedBytes);

            // Remove consumed bytes from buffer
            if (consumedBytes > 0)
            {
                _messageBuffer.RemoveRange(0, Math.Min(consumedBytes, _messageBuffer.Count));
            }
        }

        // Fire events for parsed messages
        foreach (var message in messages)
        {
            try
            {
                OnMessageReceived(message, bufferData);
            }
            catch (Exception ex)
            {
                OnErrorOccurred(ex);
            }
        }
    }

    /// <summary>
    /// Raises the MessageReceived event.
    /// </summary>
    /// <param name="message">The received message.</param>
    /// <param name="rawData">The raw data that was parsed.</param>
    protected virtual void OnMessageReceived(IInboundMessage<T> message, byte[] rawData)
    {
        MessageReceived?.Invoke(this, new MessageReceivedEventArgs<T>(message, rawData));
    }

    /// <summary>
    /// Raises the ErrorOccurred event.
    /// </summary>
    /// <param name="error">The error that occurred.</param>
    /// <param name="rawData">The raw data being processed when the error occurred.</param>
    protected virtual void OnErrorOccurred(Exception error, byte[]? rawData = null)
    {
        ErrorOccurred?.Invoke(this, new MessageConsumerErrorEventArgs(error, rawData));
    }

    /// <summary>
    /// Throws ObjectDisposedException if this instance has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(StreamMessageConsumer<T>));
    }

    /// <summary>
    /// Disposes the message consumer and releases resources.
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