# DAQiFi Device Interfaces

This document provides examples and usage guidance for the DAQiFi device interfaces that were migrated from `daqifi-desktop` to establish the foundation for device interaction in the `daqifi-core` library.

## Overview

The device interfaces provide a consistent API for discovering, connecting to, and communicating with DAQiFi hardware devices. They abstract away hardware implementation details and provide a clean interface for application developers.

## Core Interfaces

### IDevice
The base interface for all DAQiFi devices, providing fundamental connection and communication capabilities.

### IStreamingDevice
Extends `IDevice` with data streaming functionality for devices that support continuous data acquisition.

## Implementation Classes

### DaqifiDevice
The base implementation of `IDevice` that provides core device functionality.

### DaqifiStreamingDevice
Extends `DaqifiDevice` with streaming capabilities, implementing `IStreamingDevice`.

## Usage Examples

### Basic Device Connection

```csharp
using Daqifi.Core.Device;
using System.Net;

// Create a device instance
var device = new DaqifiDevice("My DAQiFi Device", IPAddress.Parse("192.168.1.100"));

// Subscribe to status changes
device.StatusChanged += (sender, args) => 
{
    Console.WriteLine($"Device status changed to: {args.Status}");
};

// Subscribe to messages
device.MessageReceived += (sender, args) => 
{
    Console.WriteLine($"Message received: {args.Message.Data}");
};

// Connect to the device
device.Connect();

// Send a message
device.Send(ScpiMessageProducer.GetDeviceInfo);

// Disconnect when done
device.Disconnect();
```

### Streaming Device Usage

```csharp
using Daqifi.Core.Device;
using Daqifi.Core.Communication.Producers;
using System.Net;

// Create a streaming device
var streamingDevice = new DaqifiStreamingDevice("My Streaming Device", IPAddress.Parse("192.168.1.100"));

// Configure streaming frequency
streamingDevice.StreamingFrequency = 1000; // 1000 Hz

// Connect to the device
streamingDevice.Connect();

// Start streaming data
streamingDevice.StartStreaming();
Console.WriteLine($"Started streaming at {streamingDevice.StreamingFrequency} Hz");

// Stream for some time...
await Task.Delay(TimeSpan.FromSeconds(10));

// Stop streaming
streamingDevice.StopStreaming();
Console.WriteLine("Stopped streaming");

// Disconnect
streamingDevice.Disconnect();
```

### Error Handling

```csharp
try
{
    var device = new DaqifiDevice("Test Device");
    
    // This will throw an exception because device is not connected
    device.Send(ScpiMessageProducer.GetDeviceInfo);
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    // Output: Error: Device is not connected.
}
```

### Custom Device Implementation

```csharp
using Daqifi.Core.Device;
using Daqifi.Core.Communication.Messages;

public class CustomDaqifiDevice : DaqifiDevice
{
    public CustomDaqifiDevice(string name, IPAddress? ipAddress = null) 
        : base(name, ipAddress)
    {
    }

    public override void Send<T>(IOutboundMessage<T> message)
    {
        // Add custom logging
        Console.WriteLine($"Sending message: {message.Data}");
        
        // Call base implementation
        base.Send(message);
    }

    protected override void OnMessageReceived(IInboundMessage<object> message)
    {
        // Add custom message processing
        Console.WriteLine($"Processing received message: {message.Data}");
        
        // Call base implementation to fire events
        base.OnMessageReceived(message);
    }
}
```

## Connection Status Monitoring

```csharp
var device = new DaqifiDevice("Monitor Device");

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
    }
};

// Check connection status
if (device.IsConnected)
{
    Console.WriteLine("Device is currently connected");
}
```

## Message Types

The device interfaces work with strongly-typed messages:

```csharp
// String-based SCPI messages
device.Send(ScpiMessageProducer.GetDeviceInfo);
device.Send(ScpiMessageProducer.StartStreaming(1000));

// Custom message types can be created
public class CustomMessage : IOutboundMessage<MyDataType>
{
    public MyDataType Data { get; set; }
    
    public byte[] GetBytes()
    {
        // Custom serialization logic
        return Encoding.UTF8.GetBytes(Data.ToString());
    }
}

device.Send(new CustomMessage { Data = myData });
```

## Thread Safety

The device implementations are not thread-safe by default. For multi-threaded scenarios, ensure proper synchronization:

```csharp
private readonly object _deviceLock = new object();

public void SafeSendMessage(IOutboundMessage<string> message)
{
    lock (_deviceLock)
    {
        if (device.IsConnected)
        {
            device.Send(message);
        }
    }
}
```

## Integration with Application

```csharp
public class DeviceManager
{
    private readonly List<IDevice> _devices = new();
    
    public void AddDevice(IDevice device)
    {
        device.StatusChanged += OnDeviceStatusChanged;
        device.MessageReceived += OnDeviceMessageReceived;
        _devices.Add(device);
    }
    
    public async Task ConnectAllDevicesAsync()
    {
        foreach (var device in _devices)
        {
            device.Connect();
            // Add delay or wait for connection status
            await Task.Delay(1000);
        }
    }
    
    private void OnDeviceStatusChanged(object sender, DeviceStatusEventArgs e)
    {
        var device = (IDevice)sender;
        Console.WriteLine($"Device '{device.Name}' status: {e.Status}");
    }
    
    private void OnDeviceMessageReceived(object sender, MessageReceivedEventArgs e)
    {
        var device = (IDevice)sender;
        Console.WriteLine($"Device '{device.Name}' received: {e.Message.Data}");
    }
}
```

## Features

- **Clean Abstraction**: Hardware details are hidden behind well-defined interfaces
- **Event-Driven**: Status changes and message reception are handled via events
- **Type Safety**: Generic message types provide compile-time safety
- **Extensible**: Interfaces can be implemented for custom device behaviors
- **Modern C#**: Uses nullable reference types and modern C# features
- **Cross-Platform**: Compatible with .NET 8.0 and 9.0

## Next Steps

This interface foundation enables:
- Device discovery services
- Connection management
- Message consumer implementation  
- Channel configuration
- Protocol-specific implementations (TCP, UDP, Serial, etc.) 