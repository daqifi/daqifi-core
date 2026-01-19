# DAQiFi Device Interfaces

This document provides examples and usage guidance for the DAQiFi device interfaces in the `daqifi-core` library.

## Overview

The device interfaces provide a consistent API for discovering, connecting to, and communicating with DAQiFi hardware devices. They abstract away hardware implementation details and provide a clean interface for application developers.

## Quick Start

The simplest way to connect to a DAQiFi device is using the `DaqifiDeviceFactory`:

```csharp
using Daqifi.Core.Device;
using Daqifi.Core.Communication.Producers;

// Connect to a device (handles transport, connection, and initialization)
using var device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);

// Subscribe to incoming data
device.MessageReceived += (sender, e) =>
{
    if (e.Message.Data is DaqifiOutMessage message)
    {
        Console.WriteLine($"Timestamp: {message.MsgTimeStamp}");
        Console.WriteLine($"Analog values: {string.Join(", ", message.AnalogInData)}");
    }
};

// Configure channels and start streaming
device.Send(ScpiMessageProducer.EnableAdcChannels("0000000011")); // Enable first 2 channels
device.Send(ScpiMessageProducer.StartStreaming(100)); // 100 Hz sample rate

await Task.Delay(TimeSpan.FromSeconds(10)); // Stream for 10 seconds

device.Send(ScpiMessageProducer.StopStreaming);
```

## Core Interfaces

### IDevice

The base interface for all DAQiFi devices, providing fundamental connection and communication capabilities:

- `Name` - Device identifier
- `IpAddress` - Network address (for WiFi devices)
- `IsConnected` - Connection status
- `Status` - Detailed connection status (Disconnected, Connecting, Connected, Lost)
- `Connect()` / `Disconnect()` - Connection management
- `Send<T>(IOutboundMessage<T>)` - Send commands to device
- `StatusChanged` event - Connection status notifications
- `MessageReceived` event - Incoming data notifications

### IStreamingDevice

Extends `IDevice` with data streaming functionality for devices that support continuous data acquisition. Note: This interface is primarily implemented in the desktop application; the core library provides the base `DaqifiDevice` class.

## Implementation Classes

### DaqifiDevice

The primary device class that provides:

- TCP connection management via transport layer
- Message producer/consumer for bidirectional communication
- Device initialization sequence
- Protocol buffer message handling
- Channel population from device status

### DaqifiDeviceFactory

Static factory class for simplified device connections:

| Method | Description |
|--------|-------------|
| `ConnectTcpAsync(host, port, options?, token?)` | Connect by hostname |
| `ConnectTcpAsync(ipAddress, port, options?, token?)` | Connect by IP address |
| `ConnectTcp(...)` | Synchronous versions |
| `ConnectFromDeviceInfoAsync(deviceInfo, options?, token?)` | Connect from discovery result |

### DeviceConnectionOptions

Configuration for connection behavior:

```csharp
var options = new DeviceConnectionOptions
{
    DeviceName = "My DAQiFi",           // Device identifier
    ConnectionRetry = new ConnectionRetryOptions
    {
        MaxAttempts = 3,
        ConnectionTimeout = TimeSpan.FromSeconds(10)
    },
    InitializeDevice = true              // Run init sequence after connect
};
```

Pre-configured presets:
- `DeviceConnectionOptions.Default` - Standard settings
- `DeviceConnectionOptions.Fast` - Quick connection, fewer retries
- `DeviceConnectionOptions.Resilient` - More retries, longer timeouts

## Usage Examples

### Device Discovery and Connection

```csharp
using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;

// Discover devices on the network
using var finder = new WiFiDeviceFinder();
var devices = await finder.DiscoverAsync(TimeSpan.FromSeconds(5));

foreach (var deviceInfo in devices)
{
    Console.WriteLine($"Found: {deviceInfo.Name} at {deviceInfo.IPAddress}:{deviceInfo.Port}");
}

// Connect to the first discovered device
if (devices.Any())
{
    using var device = await DaqifiDeviceFactory.ConnectFromDeviceInfoAsync(devices.First());
    // Device is ready to use
}
```

### Manual Device Connection (Advanced)

For cases where you need more control over the connection process:

```csharp
using Daqifi.Core.Device;
using Daqifi.Core.Communication.Transport;

// Create and connect transport manually
var transport = new TcpStreamTransport("192.168.1.100", 9760);
await transport.ConnectAsync(new ConnectionRetryOptions { MaxAttempts = 3 });

// Create device with transport
using var device = new DaqifiDevice("My Device", transport);
device.Connect();
await device.InitializeAsync();

// Now ready to send commands
device.Send(ScpiMessageProducer.GetDeviceInfo);
```

### Error Handling

```csharp
try
{
    using var device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);
    device.Send(ScpiMessageProducer.StartStreaming(100));
}
catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "port")
{
    Console.WriteLine("Invalid port number");
}
catch (SocketException ex)
{
    Console.WriteLine($"Connection failed: {ex.Message}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("Connection was cancelled");
}
```

### Connection Status Monitoring

```csharp
using var device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);

device.StatusChanged += (sender, args) =>
{
    switch (args.Status)
    {
        case ConnectionStatus.Disconnected:
            Console.WriteLine("Device disconnected");
            break;
        case ConnectionStatus.Connecting:
            Console.WriteLine("Connecting to device...");
            break;
        case ConnectionStatus.Connected:
            Console.WriteLine("Device connected successfully");
            break;
        case ConnectionStatus.Lost:
            Console.WriteLine("Connection lost - may attempt reconnection");
            break;
    }
};
```

### Working with Device Metadata

After initialization, device metadata is populated:

```csharp
using var device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);

// Access device information
Console.WriteLine($"Part Number: {device.Metadata.PartNumber}");
Console.WriteLine($"Serial Number: {device.Metadata.SerialNumber}");
Console.WriteLine($"Firmware: {device.Metadata.FirmwareVersion}");

// Check capabilities
var caps = device.Metadata.Capabilities;
Console.WriteLine($"Analog Inputs: {caps.AnalogInputCount}");
Console.WriteLine($"Digital I/O: {caps.DigitalPortCount}");
```

### Streaming Data

```csharp
using var device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);

var sampleCount = 0;
device.MessageReceived += (sender, e) =>
{
    if (e.Message.Data is DaqifiOutMessage msg && msg.AnalogInData.Count > 0)
    {
        sampleCount++;
        Console.WriteLine($"Sample {sampleCount}: {string.Join(", ", msg.AnalogInData)}");
    }
};

// Enable channels (binary mask: 1 = enabled)
device.Send(ScpiMessageProducer.EnableAdcChannels("0000000011")); // Channels 0 and 1

// Start streaming at 100 Hz
device.Send(ScpiMessageProducer.StartStreaming(100));

// Stream for 10 seconds
await Task.Delay(TimeSpan.FromSeconds(10));

// Stop streaming
device.Send(ScpiMessageProducer.StopStreaming);
Console.WriteLine($"Received {sampleCount} samples");
```

## SCPI Commands

Use `ScpiMessageProducer` for device commands:

```csharp
// Device control
device.Send(ScpiMessageProducer.TurnDeviceOn);
device.Send(ScpiMessageProducer.TurnDeviceOff);
device.Send(ScpiMessageProducer.GetDeviceInfo);

// Streaming control
device.Send(ScpiMessageProducer.StartStreaming(1000));  // Start at 1000 Hz
device.Send(ScpiMessageProducer.StopStreaming);

// Channel configuration
device.Send(ScpiMessageProducer.EnableAdcChannels("11111111")); // Enable 8 channels
device.Send(ScpiMessageProducer.DisableDeviceEcho);
device.Send(ScpiMessageProducer.SetProtobufStreamFormat);
```

## Thread Safety

The `DaqifiDevice` message producer uses a background thread with a concurrent queue, making `Send()` calls thread-safe. Multiple threads can safely send commands:

```csharp
// Safe to call from multiple threads
Parallel.For(0, 10, i =>
{
    device.Send(ScpiMessageProducer.GetDeviceInfo);
});
```

However, for connection state changes (Connect/Disconnect), coordinate access from a single thread or use proper synchronization.

## Features

- **Simple Factory API**: Single-call connection with `DaqifiDeviceFactory`
- **Clean Abstraction**: Hardware details hidden behind well-defined interfaces
- **Event-Driven**: Status changes and messages handled via events
- **Type Safety**: Generic message types provide compile-time safety
- **Retry Support**: Built-in connection retry with exponential backoff
- **Thread-Safe Sending**: Background message queue for thread-safe command sending
- **Cross-Platform**: Compatible with .NET 8.0 and .NET 9.0
