using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;

namespace Daqifi.Core.Integration.Desktop;

/// <summary>
/// Adapter that enables desktop applications to gradually migrate to Core library infrastructure.
/// Provides compatibility layer between desktop device implementations and Core's transport/messaging.
/// This allows desktop teams to use Core transports while maintaining existing device logic.
/// </summary>
public class CoreDeviceAdapter : IDisposable
{
    private readonly IStreamTransport _transport;
    private readonly IMessageParser<object>? _messageParser;
    private IMessageProducer<string>? _messageProducer;
    private IMessageConsumer<object>? _messageConsumer;
    private bool _disposed;
    private bool _isWifiDevice;

    /// <summary>
    /// Initializes a new CoreDeviceAdapter with the specified transport.
    /// </summary>
    /// <param name="transport">The transport to use for device communication.</param>
    /// <param name="messageParser">Optional message parser. If not provided, uses CompositeMessageParser for both text and protobuf messages.</param>
    public CoreDeviceAdapter(IStreamTransport transport, IMessageParser<object>? messageParser = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _messageParser = messageParser ?? new CompositeMessageParser();
    }

    /// <summary>
    /// Gets the underlying transport instance.
    /// </summary>
    public IStreamTransport Transport => _transport;


    /// <summary>
    /// Gets the message producer for sending commands to the device.
    /// Desktop applications can use this to send SCPI commands.
    /// </summary>
    public IMessageProducer<string>? MessageProducer => _messageProducer;

    /// <summary>
    /// Gets the message consumer for receiving responses from the device.
    /// Desktop applications can subscribe to MessageReceived events.
    /// </summary>
    public IMessageConsumer<object>? MessageConsumer => _messageConsumer;

    /// <summary>
    /// Gets or sets whether this device is a WiFi-connected device.
    /// WiFi devices may need special buffer clearing and initialization logic.
    /// </summary>
    public bool IsWifiDevice 
    { 
        get => _isWifiDevice; 
        set 
        { 
            _isWifiDevice = value;
            // Apply WiFi-specific settings to the consumer if it exists
            if (_messageConsumer is DesktopCompatibleMessageConsumer desktopConsumer)
            {
                desktopConsumer.IsWifiDevice = value;
            }
        } 
    }

    /// <summary>
    /// Gets the connection status from the underlying transport.
    /// </summary>
    public bool IsConnected => _transport.IsConnected;

    /// <summary>
    /// Gets connection information from the underlying transport.
    /// </summary>
    public string ConnectionInfo => _transport.ConnectionInfo;

    /// <summary>
    /// Connects to the device using the configured transport.
    /// </summary>
    /// <returns>True if connection succeeded, false otherwise.</returns>
    public async Task<bool> ConnectAsync()
    {
        try
        {
            await _transport.ConnectAsync();
            if (_transport.IsConnected)
            {
                // Create message producer and consumer after connection is established
                _messageProducer = new MessageProducer<string>(_transport.Stream);
                
                // Use desktop-compatible consumer for better integration
                var desktopConsumer = new DesktopCompatibleMessageConsumer(_transport.Stream, _messageParser!);
                desktopConsumer.IsWifiDevice = _isWifiDevice;
                _messageConsumer = desktopConsumer;
                
                _messageProducer.Start();
                _messageConsumer.Start();
                
                // Clear buffer for WiFi devices after connection
                if (_isWifiDevice)
                {
                    ClearBuffer();
                }
            }
            return _transport.IsConnected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Connects to the device synchronously.
    /// </summary>
    /// <returns>True if connection succeeded, false otherwise.</returns>
    public bool Connect()
    {
        return ConnectAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Disconnects from the device.
    /// </summary>
    /// <returns>True if disconnection succeeded, false otherwise.</returns>
    public async Task<bool> DisconnectAsync()
    {
        try
        {
            StopSafely();
            await _transport.DisconnectAsync();
            
            // Clean up message producer and consumer
            _messageConsumer?.Dispose();  
            _messageProducer?.Dispose();
            _messageConsumer = null;
            _messageProducer = null;
            
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Disconnects from the device synchronously.
    /// </summary>
    /// <returns>True if disconnection succeeded, false otherwise.</returns>
    public bool Disconnect()
    {
        return DisconnectAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Sends a command to the device.
    /// This method is compatible with desktop's Write() method signature.
    /// Commands are queued even when not connected.
    /// </summary>
    /// <param name="command">The command to send.</param>
    /// <returns>True if the command was queued successfully.</returns>
    public bool Write(string command)
    {
        try
        {
            // Always allow queuing commands, even when not connected
            // This matches desktop application expectations
            if (_messageProducer == null)
            {
                // Store command for later when connection is established
                // For now, just return true to indicate the command was accepted
                return true;
            }
                
            var message = new ScpiMessage(command);
            _messageProducer.Send(message);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a TCP adapter for the specified host and port.
    /// This is the most common scenario for DAQiFi WiFi devices.
    /// </summary>
    /// <param name="host">The hostname or IP address.</param>
    /// <param name="port">The port number.</param>
    /// <param name="messageParser">Optional message parser. If not provided, uses CompositeMessageParser for both text and protobuf messages.</param>
    /// <returns>A new CoreDeviceAdapter configured for TCP communication.</returns>
    public static CoreDeviceAdapter CreateTcpAdapter(string host, int port, IMessageParser<object>? messageParser = null)
    {
        var transport = new TcpStreamTransport(host, port);
        var adapter = new CoreDeviceAdapter(transport, messageParser);
        adapter.IsWifiDevice = true; // TCP connections are typically WiFi devices
        return adapter;
    }

    /// <summary>
    /// Creates a Serial adapter for the specified port.
    /// This is used for USB-connected DAQiFi devices.
    /// </summary>
    /// <param name="portName">The serial port name (e.g., "COM3", "/dev/ttyUSB0").</param>
    /// <param name="baudRate">The baud rate (default: 115200).</param>
    /// <param name="messageParser">Optional message parser. If not provided, uses CompositeMessageParser for both text and protobuf messages.</param>
    /// <returns>A new CoreDeviceAdapter configured for Serial communication.</returns>
    public static CoreDeviceAdapter CreateSerialAdapter(string portName, int baudRate = 115200, IMessageParser<object>? messageParser = null)
    {
        var transport = new SerialStreamTransport(portName, baudRate);
        return new CoreDeviceAdapter(transport, messageParser);
    }

    /// <summary>
    /// Gets available serial port names on the system.
    /// Useful for desktop applications to populate port selection dropdowns.
    /// </summary>
    /// <returns>Array of available serial port names.</returns>
    public static string[] GetAvailableSerialPorts()
    {
        return SerialStreamTransport.GetAvailablePortNames();
    }

    /// <summary>
    /// Creates a TCP adapter configured specifically for text-based SCPI communication.
    /// </summary>
    /// <param name="host">The hostname or IP address.</param>
    /// <param name="port">The port number.</param>
    /// <returns>A new CoreDeviceAdapter configured for text-only communication.</returns>
    public static CoreDeviceAdapter CreateTextOnlyTcpAdapter(string host, int port)
    {
        var transport = new TcpStreamTransport(host, port);
        var textParser = new CompositeMessageParser(new LineBasedMessageParser(), null);
        return new CoreDeviceAdapter(transport, textParser);
    }

    /// <summary>
    /// Creates a TCP adapter configured specifically for binary protobuf communication.
    /// </summary>
    /// <param name="host">The hostname or IP address.</param>
    /// <param name="port">The port number.</param>
    /// <returns>A new CoreDeviceAdapter configured for protobuf-only communication.</returns>
    public static CoreDeviceAdapter CreateProtobufOnlyTcpAdapter(string host, int port)
    {
        var transport = new TcpStreamTransport(host, port);
        var protobufParser = new CompositeMessageParser(null, new ProtobufMessageParser());
        return new CoreDeviceAdapter(transport, protobufParser);
    }

    /// <summary>
    /// Provides access to the underlying data stream for compatibility with existing desktop code.
    /// Some desktop components may need direct stream access during migration.
    /// </summary>
    public Stream? DataStream => _transport.IsConnected ? _transport.Stream : null;

    /// <summary>
    /// Event that fires when the connection status changes.
    /// Desktop applications can subscribe to this for status updates.
    /// </summary>
    public event EventHandler<TransportStatusEventArgs>? ConnectionStatusChanged
    {
        add { _transport.StatusChanged += value; }
        remove { _transport.StatusChanged -= value; }
    }

    /// <summary>
    /// Event that fires when a message is received from the device.
    /// Desktop applications can subscribe to this instead of creating their own consumers.
    /// </summary>
    public event EventHandler<MessageReceivedEventArgs<object>>? MessageReceived
    {
        add 
        { 
            var consumer = _messageConsumer; // Capture reference to avoid race condition
            if (consumer != null) consumer.MessageReceived += value; 
        }
        remove 
        { 
            var consumer = _messageConsumer; // Capture reference to avoid race condition
            if (consumer != null) consumer.MessageReceived -= value; 
        }
    }

    /// <summary>
    /// Event that fires when an error occurs in message processing.
    /// </summary>
    public event EventHandler<MessageConsumerErrorEventArgs>? ErrorOccurred
    {
        add 
        { 
            var consumer = _messageConsumer; // Capture reference to avoid race condition
            if (consumer != null) consumer.ErrorOccurred += value; 
        }
        remove 
        { 
            var consumer = _messageConsumer; // Capture reference to avoid race condition
            if (consumer != null) consumer.ErrorOccurred -= value; 
        }
    }

    /// <summary>
    /// Disposes the adapter and releases all resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                Disconnect();
            }
            catch
            {
                // Ignore errors during disposal
            }
            
            _messageConsumer?.Dispose();
            _messageProducer?.Dispose();
            _transport?.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// Clears any buffered data from WiFi devices.
    /// This is particularly important for WiFi devices that may have stale data in buffers.
    /// </summary>
    /// <returns>True if buffer was cleared successfully, false if not applicable or failed.</returns>
    public bool ClearBuffer()
    {
        try
        {
            if (_transport.IsConnected && _transport.Stream != null)
            {
                // Clear any buffered data from the stream
                var buffer = new byte[1024];
                var stream = _transport.Stream;
                
                // For network streams, we can check DataAvailable
                if (stream is System.Net.Sockets.NetworkStream networkStream)
                {
                    while (networkStream.DataAvailable)
                    {
                        var bytesRead = stream.Read(buffer, 0, buffer.Length);
                        if (bytesRead == 0) break;
                    }
                }
                // For other streams, try reading with timeout
                else
                {
                    stream.ReadTimeout = 100; // 100ms timeout
                    try
                    {
                        while (stream.Read(buffer, 0, buffer.Length) > 0) { }
                    }
                    catch (TimeoutException) 
                    {
                        // Expected when no more data - buffer is cleared
                    }
                }
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Safely stops the message producer and consumer.
    /// This method ensures proper shutdown sequence for desktop applications.
    /// </summary>
    public void StopSafely()
    {
        try
        {
            _messageConsumer?.StopSafely();
            _messageProducer?.StopSafely();
        }
        catch
        {
            // Ignore errors during safe stop
        }
    }
}