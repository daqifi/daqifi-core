using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Integration.Desktop;

namespace Daqifi.Core.Integration.Desktop.Examples;

/// <summary>
/// Demonstrates how to integrate Core library with existing desktop applications.
/// This example shows practical patterns that desktop developers can copy directly.
/// </summary>
public class DesktopIntegrationExample
{
    /// <summary>
    /// Example: Replace existing WiFi device connection with Core adapter.
    /// This pattern can be used in DaqifiStreamingDevice class.
    /// </summary>
    public static async Task<bool> ConnectToWiFiDeviceExample()
    {
        // Replace your existing TCP connection code with this:
        using var device = CoreDeviceAdapter.CreateTcpAdapter("192.168.1.100", 12345);
        
        // Subscribe to events before connecting
        device.MessageReceived += (sender, args) => {
            var response = args.Message.Data?.ToString()?.Trim() ?? "";
            Console.WriteLine($"Device: {response}");
            
            // Handle specific responses as your existing code does
            if (response.StartsWith("DAQiFi"))
            {
                Console.WriteLine("Device identification received");
            }
        };
        
        device.ConnectionStatusChanged += (sender, args) => {
            if (args.IsConnected)
            {
                Console.WriteLine($"Connected to {args.ConnectionInfo}");
            }
            else
            {
                Console.WriteLine($"Disconnected: {args.Error?.Message ?? "Unknown reason"}");
            }
        };
        
        // Connect and send initialization commands
        if (await device.ConnectAsync())
        {
            // Send commands exactly as before
            device.Write("*IDN?");
            device.Write("ECHO OFF");
            device.Write("PWR ON");
            device.Write("FORMAT PROTOBUF");
            
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Example: Replace existing USB/Serial device connection with Core adapter.
    /// This pattern can be used in SerialStreamingDevice class.
    /// </summary>
    public static async Task<bool> ConnectToUsbDeviceExample()
    {
        // Get available ports (replace your existing port enumeration)
        var availablePorts = CoreDeviceAdapter.GetAvailableSerialPorts();
        
        if (availablePorts.Length == 0)
        {
            Console.WriteLine("No serial ports available");
            return false;
        }
        
        // Try connecting to the first available port
        using var device = CoreDeviceAdapter.CreateSerialAdapter(availablePorts[0], 115200);
        
        device.MessageReceived += (sender, args) => {
            var response = args.Message.Data?.ToString()?.Trim() ?? "";
            Console.WriteLine($"USB Device: {response}");
        };
        
        if (await device.ConnectAsync())
        {
            Console.WriteLine($"Connected to USB device on {availablePorts[0]}");
            
            // Initialize device for SD card operations
            device.Write("STORAGE:SD:ENABLE");
            device.Write("LAN:DISABLE"); // SD and LAN can't both be enabled
            
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Example: Adapter pattern for existing AbstractStreamingDevice class.
    /// Shows how to minimally modify existing desktop code.
    /// </summary>
    public class ModernStreamingDevice : IDisposable
    {
        private CoreDeviceAdapter? _coreAdapter;
        private bool _isStreaming;
        
        // Keep existing properties for compatibility
        public string IpAddress { get; set; } = "";
        public int Port { get; set; } = 12345;
        public string DeviceSerialNo { get; set; } = "";
        public bool IsConnected => _coreAdapter?.IsConnected ?? false;
        public bool IsStreaming => _isStreaming;
        
        // Adapter provides these interfaces that desktop code expects
        public IMessageProducer<string>? MessageProducer => _coreAdapter?.MessageProducer;  
        public IMessageConsumer<object>? MessageConsumer => _coreAdapter?.MessageConsumer;
        
        /// <summary>
        /// Replace existing Connect() method with this implementation.
        /// </summary>
        public bool Connect()
        {
            try
            {
                _coreAdapter = CoreDeviceAdapter.CreateTcpAdapter(IpAddress, Port);
                
                // Wire up events that existing desktop code expects
                _coreAdapter.MessageReceived += OnMessageReceived;
                _coreAdapter.ConnectionStatusChanged += OnConnectionStatusChanged;
                _coreAdapter.ErrorOccurred += OnErrorOccurred;
                
                return _coreAdapter.Connect();
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Replace existing Disconnect() method with this implementation.
        /// </summary>
        public bool Disconnect()
        {
            try
            {
                _isStreaming = false;
                return _coreAdapter?.Disconnect() ?? true;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Replace existing Write() method with this implementation.
        /// </summary>
        public bool Write(string command)
        {
            return _coreAdapter?.Write(command) ?? false;
        }
        
        /// <summary>
        /// Example of how existing streaming logic integrates.
        /// </summary>
        public void InitializeStreaming()
        {
            if (!IsConnected || _isStreaming) return;
            
            // Send existing initialization commands
            Write("DATA:RATE 1000");
            Write("STREAM:START");
            
            _isStreaming = true;
        }
        
        public void StopStreaming()
        {
            if (!_isStreaming) return;
            
            Write("STREAM:STOP");
            _isStreaming = false;
        }
        
        // Event handlers that translate Core events to desktop patterns
        private void OnMessageReceived(object? sender, MessageReceivedEventArgs<object> e)
        {
            var message = e.Message.Data;
            
            // Handle different message types
            if (message is DaqifiOutMessage protobufMsg)
            {
                // Process binary protobuf data - existing desktop code patterns
                Console.WriteLine($"Received protobuf message: {protobufMsg}");
                // Call existing protobuf processing methods here
            }
            else if (message is string textMsg)
            {
                // Handle text responses - existing desktop code patterns
                Console.WriteLine($"Received text: {textMsg}");
                // Call existing text processing methods here
            }
            else
            {
                // Handle other message types
                Console.WriteLine($"Received message: {message}");
            }
        }
        
        private void OnConnectionStatusChanged(object? sender, TransportStatusEventArgs e)
        {
            if (!e.IsConnected)
            {
                _isStreaming = false;
                
                // Handle disconnection as existing code does
                Console.WriteLine($"Device disconnected: {e.Error?.Message ?? "Unknown reason"}");
            }
        }
        
        private void OnErrorOccurred(object? sender, MessageConsumerErrorEventArgs e)
        {
            Console.WriteLine($"Device communication error: {e.Error.Message}");
            
            // Implement existing error recovery logic
            HandleCommunicationError(e.Error);
        }
        
        // Placeholder methods that would contain existing desktop logic
        private void ProcessProtobufMessage(byte[]? rawData)
        {
            // Existing protobuf processing logic goes here
            Console.WriteLine($"Processing protobuf message ({rawData?.Length ?? 0} bytes)");
        }
        
        private void ProcessTextResponse(string response)
        {
            // Existing text response processing logic goes here
            Console.WriteLine($"Processing text response: {response}");
        }
        
        private void HandleCommunicationError(Exception error)
        {
            // Existing error handling logic goes here
            Console.WriteLine($"Handling communication error: {error.Message}");
        }
        
        public void Dispose()
        {
            StopStreaming();
            Disconnect();
            _coreAdapter?.Dispose();
        }
    }
    
    /// <summary>
    /// Example: Device discovery using Core transports.
    /// Shows how to replace existing UDP broadcast discovery.
    /// </summary>
    public static async Task<List<string>> DiscoverWiFiDevicesExample()
    {
        var discoveredDevices = new List<string>();
        var tasks = new List<Task>();
        
        // Try common IP ranges (replace existing UDP broadcast approach)
        for (int subnet = 0; subnet <= 1; subnet++)
        {
            for (int host = 1; host <= 254; host++)
            {
                var ip = $"192.168.{subnet}.{host}";
                tasks.Add(TryDiscoverDevice(ip, 12345));
            }
        }
        
        // Wait for all discovery attempts (with reasonable timeout)
        await Task.WhenAll(tasks);
        
        return discoveredDevices;
        
        async Task TryDiscoverDevice(string ip, int port)
        {
            try
            {
                using var device = CoreDeviceAdapter.CreateTcpAdapter(ip, port);
                
                // Short timeout for discovery
                var connectTask = device.ConnectAsync();
                if (await Task.WhenAny(connectTask, Task.Delay(1000)) == connectTask && await connectTask)
                {
                    // Send identification request
                    device.Write("*IDN?");
                    
                    // Wait briefly for response
                    await Task.Delay(100);
                    
                    discoveredDevices.Add($"{ip}:{port}");
                    Console.WriteLine($"Discovered device at {ip}:{port}");
                    
                    await device.DisconnectAsync();
                }
            }
            catch
            {
                // Ignore discovery failures
            }
        }
    }
    
    /// <summary>
    /// Example: Robust device manager with reconnection logic.
    /// Shows production-ready patterns for desktop applications.
    /// </summary>
    public class RobustDeviceManager : IDisposable
    {
        private CoreDeviceAdapter? _device;
        private Timer? _reconnectTimer;
        private readonly string _host;
        private readonly int _port;
        private bool _disposed;
        
        public event Action<string>? DeviceMessageReceived;
        public event Action<bool>? ConnectionStatusChanged;
        
        public bool IsConnected => _device?.IsConnected ?? false;
        
        public RobustDeviceManager(string host, int port)
        {
            _host = host;
            _port = port;
        }
        
        public async Task<bool> StartAsync()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RobustDeviceManager));
            
            return await ConnectWithRetryAsync();
        }
        
        public void Stop()
        {
            _reconnectTimer?.Dispose();
            _reconnectTimer = null;
            
            _device?.Disconnect();
        }
        
        public bool SendCommand(string command)
        {
            return _device?.Write(command) ?? false;
        }
        
        private async Task<bool> ConnectWithRetryAsync(int maxRetries = 3)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    _device?.Dispose();
                    _device = CoreDeviceAdapter.CreateTcpAdapter(_host, _port);
                    
                    _device.MessageReceived += (sender, args) => {
                        DeviceMessageReceived?.Invoke(args.Message.Data?.ToString() ?? "");
                    };
                    
                    _device.ConnectionStatusChanged += (sender, args) => {
                        ConnectionStatusChanged?.Invoke(args.IsConnected);
                        
                        if (!args.IsConnected && args.Error != null)
                        {
                            // Start reconnection timer
                            _reconnectTimer?.Dispose();
                            _reconnectTimer = new Timer(async _ => await AttemptReconnect(), 
                                                     null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
                        }
                    };
                    
                    _device.ErrorOccurred += (sender, args) => {
                        Console.WriteLine($"Device error: {args.Error.Message}");
                    };
                    
                    if (await _device.ConnectAsync())
                    {
                        Console.WriteLine($"Connected to {_host}:{_port} on attempt {attempt}");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Connection attempt {attempt} failed: {ex.Message}");
                    
                    if (attempt < maxRetries)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // Exponential backoff
                        await Task.Delay(delay);
                    }
                }
            }
            
            return false;
        }
        
        private async Task AttemptReconnect()
        {
            if (_disposed) return;
            
            try
            {
                if (await ConnectWithRetryAsync(1))
                {
                    _reconnectTimer?.Dispose();
                    _reconnectTimer = null;
                    Console.WriteLine("Reconnection successful");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Reconnection failed: {ex.Message}");
            }
        }
        
        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _device?.Dispose();
                _disposed = true;
            }
        }
    }
    
    /// <summary>
    /// Complete example showing how to use the robust device manager.
    /// </summary>
    public static async Task RobustDeviceExample()
    {
        using var deviceManager = new RobustDeviceManager("192.168.1.100", 12345);
        
        // Subscribe to events
        deviceManager.DeviceMessageReceived += message => {
            Console.WriteLine($"Received: {message}");
        };
        
        deviceManager.ConnectionStatusChanged += connected => {
            Console.WriteLine($"Connection status: {(connected ? "Connected" : "Disconnected")}");
        };
        
        // Start with automatic retry and reconnection
        if (await deviceManager.StartAsync())
        {
            // Send commands
            deviceManager.SendCommand("*IDN?");
            deviceManager.SendCommand("PWR ON");
            deviceManager.SendCommand("DATA:RATE 1000");
            
            // Keep running for demo
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
        
        // Cleanup is automatic with 'using'
    }
}