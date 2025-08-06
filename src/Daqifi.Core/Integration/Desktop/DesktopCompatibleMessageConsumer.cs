using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;

namespace Daqifi.Core.Integration.Desktop;

/// <summary>
/// Desktop-compatible message consumer that provides exact compatibility with existing desktop applications.
/// This consumer wraps the core StreamMessageConsumer and provides the specific event signatures and 
/// behaviors that desktop applications expect.
/// </summary>
public class DesktopCompatibleMessageConsumer : IMessageConsumer<object>
{
    private readonly StreamMessageConsumer<object> _coreConsumer;
    private bool _isWifiDevice;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the DesktopCompatibleMessageConsumer.
    /// </summary>
    /// <param name="stream">The stream to consume messages from.</param>
    /// <param name="messageParser">The message parser to use (ignored - uses DesktopCompatibleMessageParser).</param>
    public DesktopCompatibleMessageConsumer(Stream stream, IMessageParser<object> messageParser)
    {
        // Always use the desktop-compatible parser to ensure proper DaqifiOutMessage casting
        var desktopParser = new DesktopCompatibleMessageParser();
        _coreConsumer = new StreamMessageConsumer<object>(stream, desktopParser);
        
        // Forward events from core consumer to desktop-compatible events
        _coreConsumer.MessageReceived += OnCoreMessageReceived;
        _coreConsumer.ErrorOccurred += OnCoreErrorOccurred;
    }

    /// <summary>
    /// Gets or sets whether this consumer is handling a WiFi device.
    /// WiFi devices may need special handling for buffer clearing and initialization.
    /// </summary>
    public bool IsWifiDevice 
    { 
        get => _isWifiDevice; 
        set => _isWifiDevice = value; 
    }

    /// <summary>
    /// Gets a value indicating whether the consumer is currently running.
    /// </summary>
    public bool IsRunning => _coreConsumer.IsRunning;

    /// <summary>
    /// Gets the number of bytes currently queued for processing.
    /// </summary>
    public int QueuedMessageCount => _coreConsumer.QueuedMessageCount;

    /// <summary>
    /// Occurs when a message is received from the device.
    /// This event signature matches what desktop applications expect.
    /// </summary>
    public event EventHandler<MessageReceivedEventArgs<object>>? MessageReceived;

    /// <summary>
    /// Occurs when an error occurs during message processing.
    /// </summary>
    public event EventHandler<MessageConsumerErrorEventArgs>? ErrorOccurred;

    /// <summary>
    /// Starts the message consumer.
    /// </summary>
    public void Start()
    {
        _coreConsumer.Start();
    }

    /// <summary>
    /// Stops the message consumer immediately.
    /// </summary>
    public void Stop()
    {
        _coreConsumer.Stop();
    }

    /// <summary>
    /// Stops the message consumer safely, waiting for current processing to complete.
    /// This method signature matches desktop application expectations.
    /// </summary>
    /// <param name="timeoutMs">Maximum time to wait for processing to complete.</param>
    /// <returns>True if stopped cleanly, false if timeout occurred.</returns>
    public bool StopSafely(int timeoutMs = 1000)
    {
        return _coreConsumer.StopSafely(timeoutMs);
    }

    /// <summary>
    /// Clears any buffered data from the stream.
    /// This is particularly important for WiFi devices that may have stale data.
    /// </summary>
    public void ClearBuffer()
    {
        // This method is called by desktop applications, particularly for WiFi devices
        // The actual buffer clearing is handled by the CoreDeviceAdapter's ClearBuffer method
        // which has access to the transport stream
    }

    private void OnCoreMessageReceived(object? sender, MessageReceivedEventArgs<object> e)
    {
        // Forward the event with the exact same signature desktop expects
        MessageReceived?.Invoke(this, e);
    }

    private void OnCoreErrorOccurred(object? sender, MessageConsumerErrorEventArgs e)
    {
        // Forward the error event
        ErrorOccurred?.Invoke(this, e);
    }

    /// <summary>
    /// Disposes the message consumer and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _coreConsumer.MessageReceived -= OnCoreMessageReceived;
            _coreConsumer.ErrorOccurred -= OnCoreErrorOccurred;
            _coreConsumer?.Dispose();
            _disposed = true;
        }
    }
}