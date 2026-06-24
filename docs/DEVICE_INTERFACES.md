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
device.Send(ScpiMessageProducer.EnableAdcChannels("3")); // Enable first 2 channels (bitmask 0b11 = 3)
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

### Continuous Discovery (Live Device Set)

`IDeviceFinder.DiscoverAsync` is a single pass. For a UI that shows a live, self-updating
list of devices, wrap a finder in a `ContinuousDeviceFinder`. It owns the scan cadence,
keeps a deduplicated set across passes, raises `DeviceDiscovered` the first time each device
appears, and raises `DeviceLost` once a device has been absent for a configurable number of
consecutive passes:

```csharp
using Daqifi.Core.Device.Discovery;

var continuous = new ContinuousDeviceFinder(
    new WiFiDeviceFinder(),
    new ContinuousDiscoveryOptions
    {
        PassTimeout = TimeSpan.FromSeconds(3), // listen window per pass
        Interval = TimeSpan.FromSeconds(1),    // gap between passes
        MissThreshold = 2                      // passes a device may be absent before "lost"
    });

continuous.DeviceDiscovered += (_, e) => devices.Add(e.DeviceInfo);     // bind to your UI list
continuous.DeviceLost += (_, e) => devices.RemoveBySerial(e.DeviceInfo);
continuous.ScanError += (_, e) => logger.LogWarning(e.Exception, "Discovery pass failed");

continuous.Start();
// ... later, when the view closes:
await continuous.StopAsync();
continuous.Dispose(); // also disposes the wrapped finder unless LeaveInnerFinderOpen is set
```

One instance wraps one finder, so it represents a single transport's cadence and live set.
To track WiFi, Serial, and HID together, create one `ContinuousDeviceFinder` per transport —
each with its own interval — and merge their events into a single collection. Devices are
deduplicated per transport: the same physical unit seen over both WiFi and Serial appears as
two distinct connection options. `continuous.Devices` returns a thread-safe snapshot of the
current set at any time.

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

// Enable channels (decimal bitmask: each bit enables a channel)
device.Send(ScpiMessageProducer.EnableAdcChannels("3")); // Channels 0 and 1 (bitmask 0b11 = 3)

// Start streaming at 100 Hz
device.Send(ScpiMessageProducer.StartStreaming(100));

// Stream for 10 seconds
await Task.Delay(TimeSpan.FromSeconds(10));

// Stop streaming
device.Send(ScpiMessageProducer.StopStreaming);
Console.WriteLine($"Received {sampleCount} samples");
```

### Channel Management

`IStreamingDevice` exposes device-level channel methods that operate over the device's own
`Channels` collection, so callers no longer need to hand-encode the ADC enable bitmask. Enabling
or disabling analog channels recomputes the full `ENAble:VOLTage:DC` bitmask internally; digital
channels are toggled via the global DIO enable.

```csharp
using Daqifi.Core.Channel;

// Channels are populated after a status message is received from the device.
var ai0 = device.Channels.First(c => c.Type == ChannelType.Analog && c.ChannelNumber == 0);
var ai2 = device.Channels.First(c => c.Type == ChannelType.Analog && c.ChannelNumber == 2);

// Enable analog input channels (the device receives the combined bitmask, e.g. "5").
device.EnableChannels(new[] { ai0, ai2 });

// Disable a single channel — the recomputed mask reflects the remaining enabled channels.
device.DisableChannel(ai0);

// Turn everything off.
device.DisableAllChannels();

// Digital I/O: set direction and drive an output.
var dio1 = device.Channels.First(c => c.Type == ChannelType.Digital && c.ChannelNumber == 1);
device.SetDioDirection(dio1, ChannelDirection.Output);
device.SetDioValue(dio1, true); // drive high

// Analog output (NQ3 only) — addressed by channel number; staged value is applied immediately.
device.SetAnalogOutput(0, 2.5); // DAC channel 0 to 2.5 V

// Reboot the device (also disconnects, since the device drops its link while restarting).
device.Reboot();
```

> Channel objects passed to the enable/disable and DIO methods must belong to the device's
> `Channels` collection (so the internal state and bitmask stay in sync). Analog-output (DAC)
> channels are not part of `Channels`, so `SetAnalogOutput` takes a channel number directly.

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
device.Send(ScpiMessageProducer.EnableAdcChannels("255")); // Enable 8 channels (bitmask 0xFF = 255)
device.Send(ScpiMessageProducer.DisableDeviceEcho);
device.Send(ScpiMessageProducer.SetProtobufStreamFormat);
```

## Device Diagnostics

`IDeviceDiagnostics` (implemented by `DaqifiStreamingDevice`) is a typed wrapper over the firmware's
logging and diagnostics SCPI surface — the system log, runtime log levels, SCPI command history,
error-queue depth, and streaming/memory performance counters. These values originate **on the
device**; this is not a client-side instrumentation framework.

```csharp
using Daqifi.Core.Device.Diagnostics;

using var device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);

// System log (reading the log also clears the device buffer).
IReadOnlyList<SystemLogEntry> log = await device.GetSystemLogAsync();
foreach (var entry in log) Console.WriteLine(entry.Message);
await device.ClearSystemLogAsync();

// Runtime log levels (0 = None, 1 = Error, 2 = Info, 3 = Debug). The returned
// setting reflects the level actually applied, which a module's ceiling may cap.
LogLevelSetting applied = await device.SetLogLevelAsync("STREAM", 2);
Console.WriteLine($"{applied.Module}: {applied.Level} (ceiling {applied.Ceiling})");

// SCPI command history (newest first) and error-queue depth (non-destructive).
IReadOnlyList<string> history = await device.GetCommandHistoryAsync();
int queuedErrors = await device.GetSystemErrorCountAsync();

// Performance counters. Headline fields are typed (nullable when the running
// firmware doesn't emit them); the full set is available via Values.
StreamStats stream = await device.GetStreamStatsAsync();
Console.WriteLine($"Samples: {stream.TotalSamplesStreamed}, dropped: {stream.QueueDroppedSamples}");

MemoryDiagnostics mem = await device.GetMemoryDiagnosticsAsync();
Console.WriteLine($"Heap free: {mem.HeapFree}/{mem.HeapTotal}");
foreach (var (key, value) in mem.Values) Console.WriteLine($"{key} = {value}");
```

Notes:
- The `StreamStats`/`MemoryDiagnostics` parsers are **forward-compatible**: the device emits a set of
  `Key=Value` lines whose membership grows between firmware versions, so every numeric pair is exposed
  through `Values` and the typed properties return `null` for fields the running firmware omits.
- Each call runs as a text command (the protobuf consumer is paused for the exchange, like the SD and
  LAN-chip queries). They do **not** stop streaming, so you can sample live counters — but parsing is
  most reliable when the device is not actively streaming. Avoid issuing them concurrently.
- A `DeviceDiagnosticsException` (carrying `RawDeviceResponse`) is thrown when the device returns a
  SCPI error or an unparseable response for the structured queries.
- `SYSTem:OS:Stats?` (FreeRTOS task stats) is intentionally **not** wrapped: it is commented out in the
  current firmware. It can be added once the firmware re-enables it.

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
- **Cross-Platform**: Compatible with .NET 9.0 and .NET 10.0
