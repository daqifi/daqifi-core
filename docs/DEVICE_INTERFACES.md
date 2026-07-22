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

Extends `IDevice` with data streaming functionality for devices that support continuous data acquisition:
starting/stopping a stream, per-channel enable/disable, digital I/O, PWM, and analog output.
`DaqifiStreamingDevice` implements it in this library — see [DaqifiStreamingDevice](#daqifistreamingdevice)
below.

## Implementation Classes

### DaqifiDevice

The primary device class that provides:

- TCP connection management via transport layer
- Message producer/consumer for bidirectional communication
- Device initialization sequence
- Protocol buffer message handling
- Channel population from device status

### DaqifiStreamingDevice

Extends `DaqifiDevice` with the full streaming/configuration surface — this is the class
`DaqifiDeviceFactory` actually constructs for a connection. It implements:

- `IStreamingDevice` — streaming start/stop, channel enable/disable, digital I/O, PWM, analog output
  (see [Channel Management](#channel-management) below)
- `INetworkConfigurable` — WiFi/LAN configuration (see the
  [Network configuration](../README.md#network-configuration) recipe in the root README)
- `ISdCardOperations` — list/download/delete/format SD card contents and start/stop on-device logging
- `ILanChipInfoProvider` — WiFi-module firmware/version info used during firmware updates
- `IDeviceDiagnostics` — system log, runtime log levels, command history, and performance counters
  (see [Device Diagnostics](#device-diagnostics) below)

### DaqifiDeviceFactory

Static factory class for simplified device connections:

| Method | Description |
|--------|-------------|
| `ConnectTcpAsync(host, port, options?, token?)` | Connect by hostname |
| `ConnectTcpAsync(ipAddress, port, options?, token?)` | Connect by IP address |
| `ConnectTcp(...)` | Synchronous versions |
| `ConnectSerialAsync(portName, options?, token?)` | Connect over serial/USB at the default baud rate (9600) |
| `ConnectSerialAsync(portName, baudRate, options?, token?)` | Connect over serial/USB at an explicit baud rate |
| `ConnectSerial(...)` | Synchronous versions |
| `ConnectFromDeviceInfoAsync(deviceInfo, options?, token?)` | Connect from discovery result |
| `ConnectFromDeviceInfo(...)` | Synchronous version |

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

### USB Physical-Location Correlation

`IDeviceInfo.LocationKey` is a stable, topology-derived identifier for the physical USB port a
device is plugged into (e.g. `Port_#0001.Hub_#0001` on Windows). Unlike `PortName`, `DevicePath`,
and `SerialNumber` — which are transport-scoped and don't survive a device switching identities —
`LocationKey` stays the same for the same physical port across a device's transitions between
serial (app) mode and HID bootloader mode, and across re-enumerations. Use it to correlate the
same physical unit across a firmware update's mode switch, or to disambiguate multiple identical
HID bootloaders (same VID/PID, no serial number) plugged into different ports:

```csharp
using var serialFinder = new SerialDeviceFinder();
var device = (await serialFinder.DiscoverAsync()).First();
var targetLocation = device.LocationKey; // resolved while still in serial/app mode

// ...device reboots into bootloader mode via ForceBootloader...

// Target the bootloader that came from the SAME physical port, even though its
// HID device path didn't exist until after the reboot.
await firmwareUpdateService.UpdateFirmwareAsync(
    device, hexFilePath, progress: null, targetDevicePath: null, targetLocationKey: targetLocation);
```

`LocationKey` is resolved via `IUsbLocationProvider` and is Windows-only in v1 (Linux/macOS
resolve to `null`, same as `IUsbPortDescriptorProvider`'s cross-platform fallback pattern).

> **Verification status:** the serial ⇄ HID-bootloader stability claim above is this feature's
> core design assumption ([#285](https://github.com/daqifi/daqifi-core/issues/285)), but it has
> **not yet been empirically confirmed on Windows hardware** in this repo — the environment this
> was built in has no Windows machine. `WindowsUsbLocationProvider`'s WMI query path is likewise
> unverified against a real device (CI runs `ubuntu-latest` only, so only the platform-independent
> parsing/fallback logic has automated coverage). Confirm both on a Windows bench with real
> hardware before relying on this for anything safety-critical.

### Discover Across All Transports (Recommended)

To "find any DAQiFi on WiFi or USB" in one call, use `AllTransportsDeviceFinder` — it runs the
per-transport finders concurrently and returns a single deduplicated set. Because it is itself an
`IDeviceFinder`, wrapping it in a `ContinuousDeviceFinder` (below) gives deduplicated *continuous*
discovery across every transport for free.

```csharp
using Daqifi.Core.Device.Discovery;

// One-shot across WiFi + serial:
using var finder = AllTransportsDeviceFinder.CreateDefault();
var devices = await finder.DiscoverAsync(TimeSpan.FromSeconds(3));

// Or the common "find one and connect" case in a single call:
var device = await DaqifiDeviceFactory.DiscoverAndConnectAsync(
    filter: d => d.ConnectionType == ConnectionType.Serial,   // optional; first match otherwise
    timeout: TimeSpan.FromSeconds(5));
```

A transport finder that fails (e.g. WiFi discovery with no network) is logged and skipped, so the
other transports still return. Deduplication reuses `ContinuousDeviceFinder`'s per-transport
identity, so the same physical unit reachable over both WiFi and USB appears as two connection
options; pass a custom `identitySelector` (e.g. by serial number) to collapse them.

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
To track WiFi, Serial, and HID together, prefer wrapping an `AllTransportsDeviceFinder` (above)
in a single `ContinuousDeviceFinder` — one cadence, one deduplicated live set across every
transport. (Only reach for the advanced path of one `ContinuousDeviceFinder` *per* transport when
you need a different interval per transport.) Devices are deduplicated per transport: the same
physical unit seen over both WiFi and Serial appears as two distinct connection options.
`continuous.Devices` returns a thread-safe snapshot of the current set at any time.

### Managing Multiple Devices (`DaqifiDeviceRegistry`)

Multi-device is the normal DAQ case, and `DaqifiDeviceFactory` hands back one device at a time
without tracking the live set. `DaqifiDeviceRegistry` is that missing layer: a thread-safe set of
connected devices that owns their lifetime, raises add/remove events, and — the part every
consumer otherwise reimplements — recognizes **the same physical unit reached over two transports
at once**, the classic "already connected via USB, now discovered over WiFi" support trap.

```csharp
using Daqifi.Core.Device;

using var registry = new DaqifiDeviceRegistry();

registry.DeviceAdded += (_, e) => devices.Add(e.Registration);       // bind to your UI list
registry.DeviceRemoved += (_, e) => devices.Remove(e.Registration);  // e.Reason says why

foreach (var info in await finder.DiscoverAsync(TimeSpan.FromSeconds(3)))
{
    var result = await registry.ConnectAsync(info);

    // Not an error: the unit was already connected over another transport, and the registry
    // handed back that live connection instead of opening a redundant second one.
    if (result.Outcome == DeviceRegistrationOutcome.DuplicateRejected)
    {
        logger.LogInformation("{Name} is already connected as {Key}", info.Name, result.Key);
    }
}

// Later: the registry disconnects and disposes whatever it removes.
registry.Remove(key);
```

**Keys vs. identity.** Two separate concepts run through the API:

| | What it is | Used for |
|---|---|---|
| **Key** (`DeviceRegistration.Key`) | The handle you look a device up by. Defaults to `DeviceIdentity.Key`; pass your own to `ConnectAsync`/`Register` if you already mint device ids. | `TryGet`, `Remove` |
| **Identity** (`DeviceIdentity`) | The fingerprint of the physical unit: serial number → MAC address → USB `LocationKey`. | Duplicate detection |

Identity matching walks those three discriminators and is decided by the first one **both** sides
report: serial numbers compare case-insensitively and decisively (different serials are different
units, full stop), MAC comparison ignores separators, and the USB location key is the last resort
for identical units that report no serial. A device that reports none of the three never matches
another — two unidentifiable devices are treated as two devices, not one.

**Duplicate policy.** The check runs twice: once from `IDeviceInfo` before connecting (free to
reject — nothing is open yet) and again from `DaqifiDevice.Metadata` afterwards, because a
serial-port device's serial number is often only known once it has answered its first status
message. With no callback set the existing connection wins and the new one is rejected. To decide
yourself — for example by prompting the user:

```csharp
registry.DuplicatePolicy = check =>
{
    var message = $"{check.Existing.Device.Name} is already connected via " +
                  $"{check.ExistingConnectionType}. Switch to {check.NewConnectionType}?";

    return PromptUser(message)
        ? DuplicateDeviceAction.SwitchToNew   // drop the existing connection, keep the new one
        : DuplicateDeviceAction.KeepExisting; // or Cancel to abandon the attempt entirely
};
```

**Ownership and liveness.** The registry disconnects and disposes every device it removes,
including duplicates it rejects — once a device is passed to `ConnectAsync` or `Register`, don't
dispose it yourself. Registrations whose device stops reporting `IsConnected` are pruned before
every registration attempt (and by `PruneDisconnected()` on demand). The registry does not
reconnect on its own.

> **Know this limit:** pruning is only as good as the transport's drop detection.
> `SerialStreamTransport.IsConnected` reports the OS handle's state, which stays open after a USB
> device is physically unplugged, so that device keeps reporting `IsConnected` and is *not* pruned —
> a later `ConnectAsync` under the same key hands back the dead handle rather than reconnecting.
> Devices you `Disconnect()` yourself, and drops a transport does report, prune as described.

All members are safe to call from any thread; reads return snapshots, and the duplicate policy is
always invoked without the internal lock held, so a policy that blocks on a user prompt never
blocks other threads. Two concurrent `ConnectAsync` calls for the *same* physical device may both
open a connection — the loser is detected after connecting and disposed — so serialize your own
calls if a single connect attempt matters.

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
Console.WriteLine($"Analog Inputs: {caps.AnalogInputChannels}");
Console.WriteLine($"Digital I/O: {caps.DigitalChannels}");
```

### Streaming Data

Three ways to consume streamed data: decoded per-channel samples via `IChannel.SampleReceived`
(event, recommended for most consumers), a pull-based async stream via `StreamSamplesAsync`
(`await foreach` with cancellation and backpressure), or the raw protobuf frame via `MessageReceived`
(for hand-decoding or bridging into another pipeline).

#### Per-channel samples (recommended)

While a stream is active, `DaqifiStreamingDevice` decodes every frame and raises `SampleReceived` on
each enabled channel — no protobuf field names or ADC bitmasks to interpret client-side. Decoding is
gated on the device's own `IsStreaming` flag and each channel's `IsEnabled` flag, so this only fires
when streaming is started via `StartStreaming()`/channels are enabled via `EnableChannel(s)` — sending
the equivalent raw SCPI commands directly (as in the raw-frame example below) drives the hardware but
never sets that local state, so `SampleReceived` would not fire.

```csharp
using Daqifi.Core.Channel;
using Daqifi.Core.Device;

// DaqifiDeviceFactory methods return the base DaqifiDevice type, but the constructed instance is
// always a DaqifiStreamingDevice — cast (or pattern-match with `is`) to reach its streaming API.
using var device = (DaqifiStreamingDevice)await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);

var ai0 = device.GetChannelsSnapshot().First(c => c.Type == ChannelType.Analog && c.ChannelNumber == 0);
ai0.SampleReceived += (sender, e) =>
{
    Console.WriteLine($"{e.Channel.Name}: {e.Sample.Value} (raw: {e.Sample.RawValue}, {e.Sample.Timestamp})");
};

device.EnableChannel(ai0);
device.StreamingFrequency = 100; // Hz
device.StartStreaming();

await Task.Delay(TimeSpan.FromSeconds(10));

device.StopStreaming();
```

`IDataSample.Value` is already scaled (volts for analog, 0/1 for digital). `RawValue` carries the raw
ADC count or bit when one exists (`null` for the USB pre-scaled float path), and `DeviceTimestamp`
carries the raw device tick count alongside the rollover-adjusted host `Timestamp`. A stray frame that
arrives outside a streaming session is still re-raised via `MessageReceived` but is not decoded into
samples. `GetChannelsSnapshot()` is used above (rather than the live `Channels` property) because the
channel list can be repopulated concurrently when a new device status message arrives.

#### Live async stream (`await foreach`)

`StreamSamplesAsync` exposes the same decoded samples as an `IAsyncEnumerable<LiveSample>`, so a
consumer can pull them with `await foreach` — the idiom the SD-card/export paths already use — with
cancellation and backpressure instead of hand-building an event/queue bridge. Each `LiveSample` pairs
the decoded `IDataSample` with the `IChannel` that produced it.

```csharp
using var device = (DaqifiStreamingDevice)await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);

device.EnableChannels(device.GetChannelsSnapshot().Where(c => c.Type == ChannelType.Analog));
device.StreamingFrequency = 100; // Hz
device.StartStreaming();

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
await foreach (var s in device.StreamSamplesAsync(cts.Token))
{
    Console.WriteLine($"{s.Channel.Name}: {s.Sample.Value} (tick {s.Sample.DeviceTimestamp})");
}
```

Samples are buffered in a bounded channel (`DefaultLiveSampleBufferCapacity`, override via the
`bufferCapacity` argument) with a **drop-oldest** overflow policy — if the consumer falls behind, the
oldest buffered samples are discarded (memory never grows unbounded) and `DroppedLiveSampleCount`
increments; the decode thread is never blocked. Cancelling the token ends the enumeration promptly
(surfaced as `OperationCanceledException`) and unsubscribes, but does **not** stop the device stream —
call `StopStreaming()` for that. This is additive: `SampleReceived` and `MessageReceived` are unaffected.

#### Raw protobuf frames

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

`DaqifiDeviceFactory` returns the base `DaqifiDevice` type, so pattern-match to `IStreamingDevice`
to reach these members — the same way as `INetworkConfigurable` below.

```csharp
using Daqifi.Core.Channel;

if (device is IStreamingDevice streamingDevice)
{
    // Channels are populated after a status message is received from the device.
    // Channels itself lives on the DaqifiDevice base class, so it's read from `device`.
    var ai0 = device.Channels.First(c => c.Type == ChannelType.Analog && c.ChannelNumber == 0);
    var ai2 = device.Channels.First(c => c.Type == ChannelType.Analog && c.ChannelNumber == 2);

    // Enable analog input channels (the device receives the combined bitmask, e.g. "5").
    streamingDevice.EnableChannels(new[] { ai0, ai2 });

    // Disable a single channel — the recomputed mask reflects the remaining enabled channels.
    streamingDevice.DisableChannel(ai0);

    // Turn everything off.
    streamingDevice.DisableAllChannels();

    // Digital I/O: set direction and drive an output.
    var dio1 = device.Channels.First(c => c.Type == ChannelType.Digital && c.ChannelNumber == 1);
    streamingDevice.SetDioDirection(dio1, ChannelDirection.Output);
    streamingDevice.SetDioValue(dio1, true); // drive high

    // PWM on a capable channel (IDigitalChannel.IsPwmCapable). Duty is per channel; the
    // frequency is device-wide because one hardware timer drives every PWM channel.
    var pwm = device.Channels.OfType<IDigitalChannel>().First(c => c.IsPwmCapable);
    streamingDevice.SetPwmDutyCycle(pwm, 25);  // 1-100 %
    streamingDevice.SetPwmFrequency(1000);     // 6-50000 Hz, shared by all PWM channels
    streamingDevice.SetPwmEnabled(pwm, true);  // start; SetPwmEnabled(pwm, false) stops (pin goes high-impedance)

    // Analog output (NQ3 only) — addressed by channel number; staged value is applied immediately.
    streamingDevice.SetAnalogOutput(0, 2.5); // DAC channel 0 to 2.5 V

    // Reboot the device (also disconnects, since the device drops its link while restarting).
    streamingDevice.Reboot();
}
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

`DaqifiDeviceFactory` returns the base `DaqifiDevice` type, so pattern-match to `IDeviceDiagnostics`
to reach these members — the same way as `INetworkConfigurable` below.

```csharp
using Daqifi.Core.Device.Diagnostics;

using var device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);

if (device is IDeviceDiagnostics diagnostics)
{
    // System log (reading the log also clears the device buffer).
    IReadOnlyList<SystemLogEntry> log = await diagnostics.GetSystemLogAsync();
    foreach (var entry in log) Console.WriteLine(entry.Message);
    await diagnostics.ClearSystemLogAsync();

    // Runtime log levels (0 = None, 1 = Error, 2 = Info, 3 = Debug). The returned
    // setting reflects the level actually applied, which a module's ceiling may cap.
    LogLevelSetting applied = await diagnostics.SetLogLevelAsync("STREAM", 2);
    Console.WriteLine($"{applied.Module}: {applied.Level} (ceiling {applied.Ceiling})");

    // SCPI command history (newest first) and error-queue depth (non-destructive).
    IReadOnlyList<string> history = await diagnostics.GetCommandHistoryAsync();
    int queuedErrors = await diagnostics.GetSystemErrorCountAsync();

    // Performance counters. Headline fields are typed (nullable when the running
    // firmware doesn't emit them); the full set is available via Values.
    StreamStats stream = await diagnostics.GetStreamStatsAsync();
    Console.WriteLine($"Samples: {stream.TotalSamplesStreamed}, dropped: {stream.QueueDroppedSamples}");

    MemoryDiagnostics mem = await diagnostics.GetMemoryDiagnosticsAsync();
    Console.WriteLine($"Heap free: {mem.HeapFree}/{mem.HeapTotal}");
    foreach (var (key, value) in mem.Values) Console.WriteLine($"{key} = {value}");
}
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
- **Multi-Device Registry**: `DaqifiDeviceRegistry` tracks the live device set and detects the same
  unit connected over two transports
- **Clean Abstraction**: Hardware details hidden behind well-defined interfaces
- **Event-Driven**: Status changes and messages handled via events
- **Type Safety**: Generic message types provide compile-time safety
- **Retry Support**: Built-in connection retry with exponential backoff
- **Thread-Safe Sending**: Background message queue for thread-safe command sending
- **Cross-Platform**: Compatible with .NET 9.0 and .NET 10.0
