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
    /// Gets or sets whether this consumer is connected to a WiFi device.
    /// WiFi devices may require buffer clearing due to residual data on connection.
    /// </summary>
    public bool IsWifiDevice { get; set; }

    /// <summary>
    /// Gets a value indicating whether the consumer is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Gets the number of bytes currently in the message buffer.
    /// </summary>
    public int QueuedMessageCount => _messageBuffer.Count;

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
        _consumerThread?.Join(1000);
        _consumerThread = null;
        _messageBuffer.Clear();
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
        _consumerThread = null;

        return stopped;
    }

    /// <summary>
    /// Clears any buffered data from the stream and internal buffers.
    /// Useful for WiFi devices that may have residual data on connection.
    /// </summary>
    public void ClearBuffer()
    {
        ThrowIfDisposed();

        // Clear internal message buffer
        _messageBuffer.Clear();

        // Drain any available data from the stream (if it's a NetworkStream)
        try
        {
            if (_stream.CanRead && _stream is System.Net.Sockets.NetworkStream networkStream)
            {
                var tempBuffer = new byte[_buffer.Length];
                while (networkStream.DataAvailable)
                {
                    _stream.Read(tempBuffer, 0, tempBuffer.Length);
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

                // Add received data to message buffer
                for (int i = 0; i < bytesRead; i++)
                {
                    _messageBuffer.Add(_buffer[i]);
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
        var bufferData = _messageBuffer.ToArray();
        var messages = _messageParser.ParseMessages(bufferData, out var consumedBytes);

        // Remove consumed bytes from buffer
        if (consumedBytes > 0)
        {
            _messageBuffer.RemoveRange(0, Math.Min(consumedBytes, _messageBuffer.Count));
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