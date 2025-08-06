# Desktop Integration Guide

This guide helps DAQiFi Desktop applications integrate with the new Core library infrastructure while maintaining compatibility with existing code.

## Recent Improvements (v0.4.1)

### Enhanced Drop-In Replacement Capability
The `CoreDeviceAdapter` has been significantly improved to address GitHub issue #39 and provide true drop-in replacement capability for desktop applications:

#### ✅ **WiFi Device Auto-Detection**
- TCP adapters automatically set `IsWifiDevice = true`
- Serial adapters remain `IsWifiDevice = false`
- Can be manually overridden if needed

#### ✅ **Desktop-Compatible Message Events**
- Message events now provide the exact types desktop applications expect
- Direct casting to `DaqifiOutMessage` and other types works seamlessly
- No more complex wrapper code required

#### ✅ **WiFi-Specific Features**
- `ClearBuffer()` method for clearing stale data from WiFi connections
- `StopSafely()` method for proper shutdown sequences
- Automatic buffer clearing after WiFi device connection

#### ✅ **Minimal Code Changes Required**
Desktop applications can now use CoreDeviceAdapter with minimal changes:

```csharp
// Before: Complex adapter pattern with 143+ lines of code
using var legacyWrapper = new LegacyDeviceWrapper(device);
// ... extensive setup code ...

// After: Direct drop-in replacement
using var device = CoreDeviceAdapter.CreateTcpAdapter("192.168.1.100", 12345);

// WiFi features work automatically
if (device.IsWifiDevice) 
{
    device.ClearBuffer(); // Clear any stale data
}

// Message handling works exactly as before
device.MessageReceived += (sender, args) => 
{
    if (args.Message.Data is DaqifiOutMessage protobuf)
    {
        // Direct casting works - no wrapper needed!
        var deviceSerial = protobuf.DeviceSn;
        var channelCount = protobuf.AnalogInPortNum;
    }
};
```

## Overview

The `CoreDeviceAdapter` provides a compatibility layer that allows desktop applications to:
- Use Core's modern transport layer (TCP and Serial)
- Benefit from Core's thread-safe message handling
- Maintain existing device interface patterns
- Gradually migrate to Core infrastructure

## Quick Start

### WiFi Device Connection

```csharp
// Replace existing TCP connection code with:
using var device = CoreDeviceAdapter.CreateTcpAdapter("192.168.1.100", 12345);

if (device.Connect())
{
    // Use device.Write() just like before
    device.Write("*IDN?");
    device.Write("DATA:RATE 1000");
    
    // Subscribe to messages
    device.MessageReceived += (sender, args) => {
        var response = args.Message.Data;
        Console.WriteLine($"Device responded: {response}");
    };
}
```

### USB/Serial Device Connection

```csharp
// Replace existing serial connection code with:
var availablePorts = CoreDeviceAdapter.GetAvailableSerialPorts();
using var device = CoreDeviceAdapter.CreateSerialAdapter("COM3", 115200);

if (device.Connect())
{
    device.Write("*IDN?");
    // Handle responses same as WiFi
}
```

## Migration Strategy

### Phase 1: Drop-in Replacement
Replace your existing message producer/consumer with CoreDeviceAdapter:

**Before:**
```csharp
public class DaqifiStreamingDevice 
{
    public IMessageProducer MessageProducer { get; set; }
    public IMessageConsumer MessageConsumer { get; set; }
    
    public bool Connect() 
    {
        // Custom TCP/Serial connection logic
        var stream = CreateConnection();
        MessageConsumer = new MessageConsumer(stream);
        MessageProducer = new MessageProducer(stream);
        return true;
    }
}
```

**After:**
```csharp
public class DaqifiStreamingDevice 
{
    private CoreDeviceAdapter _coreAdapter;
    
    public IMessageProducer MessageProducer => _coreAdapter.MessageProducer;
    public IMessageConsumer MessageConsumer => _coreAdapter.MessageConsumer;
    
    public bool Connect() 
    {
        _coreAdapter = CoreDeviceAdapter.CreateTcpAdapter(IpAddress, Port);
        return _coreAdapter.Connect();
    }
}
```

### Phase 2: Event-Driven Architecture
Migrate from polling to event-driven message handling:

```csharp
public class DeviceManager
{
    private CoreDeviceAdapter _device;
    
    public void InitializeDevice(string host, int port)
    {
        _device = CoreDeviceAdapter.CreateTcpAdapter(host, port);
        
        // Replace manual message polling with events
        _device.MessageReceived += OnDeviceMessageReceived;
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
        _device.ErrorOccurred += OnDeviceError;
        
        _device.Connect();
    }
    
    private void OnDeviceMessageReceived(object sender, MessageReceivedEventArgs<string> e)
    {
        // Process device response
        var response = e.Message.Data;
        HandleDeviceResponse(response);
    }
    
    private void OnConnectionStatusChanged(object sender, TransportStatusEventArgs e)
    {
        if (e.IsConnected)
        {
            // Device connected - start initialization
            InitializeDeviceState();
        }
        else if (e.Error != null)
        {
            // Handle connection errors
            Logger.Error($"Connection failed: {e.Error.Message}");
        }
    }
}
```

## Benefits of Migration

### 1. Improved Reliability
- Thread-safe message queuing prevents lost messages
- Automatic connection error handling and recovery
- Proper resource disposal and cleanup

### 2. Cross-Platform Support
- Same code works on Windows, macOS, and Linux
- Consistent behavior across different transport types
- Modern .NET 8.0+ targeting

### 3. Better Testing
- Mockable transport layer for unit testing
- Comprehensive error scenarios covered
- Integration tests verify end-to-end functionality

### 4. Enhanced Debugging
- Detailed connection status information
- Structured error reporting with context
- Built-in logging integration points

## Common Patterns

### Device Discovery with Core Transports

```csharp
public class CoreDeviceFinder
{
    public async Task<List<DaqifiDevice>> DiscoverDevicesAsync()
    {
        var devices = new List<DaqifiDevice>();
        
        // Try common IP ranges for WiFi devices
        var tasks = new List<Task>();
        for (int i = 1; i < 255; i++)
        {
            var ip = $"192.168.1.{i}";
            tasks.Add(TryConnectToDevice(ip, 12345));
        }
        
        await Task.WhenAll(tasks);
        return devices;
        
        async Task TryConnectToDevice(string ip, int port)
        {
            using var adapter = CoreDeviceAdapter.CreateTcpAdapter(ip, port);
            if (await adapter.ConnectAsync())
            {
                devices.Add(new DaqifiDevice { IpAddress = ip, Port = port });
                await adapter.DisconnectAsync();
            }
        }
    }
}
```

### Error Handling Best Practices

```csharp
public class RobustDeviceManager
{
    private CoreDeviceAdapter _device;
    private Timer _reconnectTimer;
    
    public async Task<bool> ConnectWithRetryAsync(string host, int port, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _device = CoreDeviceAdapter.CreateTcpAdapter(host, port);
                _device.ErrorOccurred += OnDeviceError;
                
                if (await _device.ConnectAsync())
                {
                    Logger.Info($"Connected to {host}:{port} on attempt {attempt}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Connection attempt {attempt} failed: {ex.Message}");
                
                if (attempt < maxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt))); // Exponential backoff
                }
            }
        }
        
        return false;
    }
    
    private void OnDeviceError(object sender, MessageConsumerErrorEventArgs e)
    {
        Logger.Error($"Device error: {e.Error.Message}");
        
        // Start reconnection timer
        _reconnectTimer?.Dispose();
        _reconnectTimer = new Timer(AttemptReconnect, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    }
}
```

## Performance Considerations

### Message Throughput
The Core library handles high-frequency messages efficiently:

```csharp
// Good: Batch commands when possible
var commands = new[] { "*IDN?", "DATA:RATE 1000", "STREAM:START" };
foreach (var cmd in commands)
{
    device.Write(cmd);
}

// Better: Use async for non-blocking sends
await Task.Run(() => {
    foreach (var cmd in commands)
    {
        device.Write(cmd);
    }
});
```

### Memory Management
The adapter automatically manages resources:

```csharp
// Always use 'using' for automatic disposal
using var device = CoreDeviceAdapter.CreateTcpAdapter("192.168.1.100", 12345);

// Or manually dispose in finally blocks
CoreDeviceAdapter device = null;
try
{
    device = CoreDeviceAdapter.CreateTcpAdapter("192.168.1.100", 12345);
    // Use device...
}
finally
{
    device?.Dispose();
}
```

## Troubleshooting

### Common Issues

1. **Connection Timeouts**
   - Check firewall settings
   - Verify device IP address and port
   - Test with ping/telnet first

2. **Message Loss**
   - Subscribe to ErrorOccurred events
   - Check QueuedMessageCount for backlog
   - Implement message acknowledgment patterns

3. **Threading Issues**
   - All adapter methods are thread-safe
   - Event handlers run on background threads
   - Use Dispatcher.Invoke for UI updates

### Debug Mode
Enable detailed logging during development:

```csharp
// Add this to your app startup
#if DEBUG
    // Enable Core library debug logging
    Daqifi.Core.Logging.EnableDebugMode();
#endif
```

## Next Steps

1. **Start Small**: Replace one device connection at a time
2. **Test Thoroughly**: Use the provided test patterns
3. **Monitor Performance**: Compare before/after metrics
4. **Provide Feedback**: Report issues to the Core team

For additional support, see the main [DAQiFi Core documentation](../../README.md) or create an issue in the GitHub repository.