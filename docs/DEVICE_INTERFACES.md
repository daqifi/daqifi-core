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
await using var device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);

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
- `Connect()` / `Disconnect()` - Blocking connection management
- `ConnectAsync(CancellationToken)` / `DisconnectAsync(CancellationToken)` - Non-blocking, cancellable
  connection management. These are genuine interface members, not default-interface-method shims over
  the blocking calls — every implementer honors the token.
- `IAsyncDisposable.DisposeAsync()` - `IDevice` extends `IAsyncDisposable`, so any consumer coding
  against the interface (not just the concrete `DaqifiDevice`) can tear a device down with
  `await using`/`await device.DisposeAsync()` — no cast required.
- `Send<T>(IOutboundMessage<T>)` - Send commands to device
- `StatusChanged` event - Connection status notifications
- `MessageReceived` event - Incoming data notifications
- `ErrorOccurred` event - Background read/parse/decode failures (see [Error Surface](#error-surface))

### IStreamingDevice

Extends `IDevice` with data streaming functionality for devices that support continuous data acquisition:
starting/stopping a stream, per-channel enable/disable, digital I/O, PWM, and analog output.
`DaqifiStreamingDevice` implements it in this library — see [DaqifiStreamingDevice](#daqifistreamingdevice)
below.

Every one of those operations — plus `Reboot()` — has an `...Async(CancellationToken)` twin declared
directly on the interface, so a consumer holding only an `IStreamingDevice` reference gets the same
cancellable API surface as one holding the concrete `DaqifiStreamingDevice`:

```csharp
if (device is IStreamingDevice streamingDevice)
{
    await streamingDevice.EnableChannelAsync(channel, cancellationToken);
    await streamingDevice.StartStreamingAsync(cancellationToken);
}
```

The synchronous methods (`StartStreaming()`, `EnableChannel()`, etc.) remain, unchanged, for existing
callers — the `Async` members are additive, not a replacement. Unlike `Connect`/`Disconnect`, most of
this surface has no genuine async machinery underneath: `DaqifiStreamingDevice`'s streaming/channel/DIO
/PWM/output/reboot commands are fire-and-forget writes with nothing to await, so the `Async` twin is a
thin, cancellable wrapper around the same single write.

`IStreamingDevice` also extends `IConfirmingDeviceAdministration`, so the confirming calibration calls
(`SaveAdcCalibrationAsync`, `LoadAdcCalibrationAsync`, `SetAdcCalibrationSlopeAsync`, and the rest —
each sends the same firmware primitive as its `void` counterpart and then confirms the device accepted
it) are reachable directly off an `IStreamingDevice` reference too, with no separate cast needed.

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
`DaqifiDeviceFactory` constructs for a connection, and the type its `Connect*` methods return
directly (no cast required). It implements:

- `IStreamingDevice` — streaming start/stop, channel enable/disable, digital I/O, PWM, analog output
  (see [Channel Management](#channel-management) below)
- `INetworkConfigurable` — WiFi/LAN configuration (see the
  [Network configuration](../README.md#network-configuration) recipe in the root README)
- `ISdCardOperations` — list/download/delete/format SD card contents and start/stop on-device logging
- `ILanChipInfoProvider` — WiFi-module firmware/version info used during firmware updates
- `IDeviceDiagnostics` — system log, runtime log levels, command history, and performance counters
  (see [Device Diagnostics](#device-diagnostics) below)

### BootloaderSessionDevice

Stands in for a device that is **already sitting in its bootloader**, so a manual bootloader-only
firmware update can go through the same `IFirmwareUpdateService` entry points as a normal update.
Every operation is a no-op and every collection is empty — a device in bootloader mode has no SCPI
transport to talk to.

```csharp
await using var session = new BootloaderSessionDevice("DAQiFi Bootloader");
await firmwareUpdateService.UpdateFirmwareAsync(session, hexFilePath, progress);
```

Three of its behaviours are load-bearing rather than incidental, because the PIC32 update flow
touches the device before it ever reaches the bootloader:

| Member | Value | Why |
|--------|-------|-----|
| `IsConnected` | starts `true` | The flow rejects a disconnected device before it starts |
| `IsStreaming` | always `false` | Lets the flow skip its `StopStreaming()` call |
| `Send(...)` | discards silently | The flow sends force-bootloader unconditionally; throwing would abort a valid update |

Prefer this over hand-writing an `IStreamingDevice` stub in your own application. It is the only
hand-written implementation of that interface shipped with the product, so a member added to
`IStreamingDevice` is resolved here — in the change that adds it — instead of breaking your build on
your next Core upgrade.

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
    InitializeDevice = true,             // Run init sequence after connect
    PreserveActiveStream = false         // Take control of the device (see the warning below)
};
```

Pre-configured presets:
- `DeviceConnectionOptions.Default` - Standard settings
- `DeviceConnectionOptions.Fast` - Quick connection, fewer retries
- `DeviceConnectionOptions.Resilient` - More retries, longer timeouts
- `DeviceConnectionOptions.Observing` - Connect without disturbing a stream already running
  (see [Connecting stops any stream already running](#connecting-stops-any-stream-already-running))

> **Connecting stops whatever the device was streaming.** With the default options, initialization
> takes control of the device. If another session — another app, another process, another machine —
> was already streaming from that unit, its data stops, silently.
> See [Connecting stops any stream already running](#connecting-stops-any-stream-already-running).

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
    await using var device = await DaqifiDeviceFactory.ConnectFromDeviceInfoAsync(devices.First());
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

`SwitchToNew` opens the replacement *before* dropping the existing connection, so switching to a
transport that turns out not to answer leaves the original in place rather than costing you both.
The trade-off is that switching to a connection needing the same resource the old one holds (the
same serial port) fails instead of freeing it first — for that, `Remove(key)` then `ConnectAsync`.
The policy is asked once per connection attempt, not again after connecting.

**Ownership and liveness.** The registry disconnects and disposes every device it removes,
including duplicates it rejects — once a device is passed to `ConnectAsync` or `Register`, don't
dispose it yourself. Registrations whose device stops reporting `IsConnected` are pruned before
every registration attempt (and by `PruneDisconnected()` on demand). Pruning covers a physically
unplugged USB device: the transport detects the drop itself (see
[Detecting a dropped connection](#detecting-a-dropped-connection)) and stops reporting
`IsConnected`, within the bounds documented there. The registry does not reconnect on its own.

All members are safe to call from any thread; reads return snapshots, and the duplicate policy is
always invoked without the internal lock held, so a policy that blocks on a user prompt never
blocks other threads. Two concurrent `ConnectAsync` calls for the *same* physical device may both
open a connection — the loser is detected after connecting and disposed — so serialize your own
calls if a single connect attempt matters.

### Connecting stops any stream already running

A DAQiFi device has **one** acquisition: one ADC, one sample rate, one destination interface. There
is no per-connection stream. So the connect-time initialization sequence, which stops streaming,
sets the power state, fixes the stream format, and (over USB) routes the stream to this connection,
acts on the device as a whole — not on your connection.

That is the right default when your session owns the device: it also clears a stream orphaned by a
previously crashed session, so a stale acquisition never leaks into a fresh one. But when a
**second** session connects to a device that is already streaming, the same sequence ends the first
session's acquisition. Neither side is told. The first app's data simply stops.

Realistic ways to hit this, all silent to the victim:

- A desktop app on USB while a script or second app talks to the same unit over WiFi
- Two instances of the same application
- A second viewer attaching to a unit a logger is already streaming

**If you only need to look, connect as an observer.** `PreserveActiveStream` skips every
initialization command that writes global stream state:

```csharp
// A secondary session that must not disturb whoever is already streaming.
var device = await DaqifiDeviceFactory.ConnectTcpAsync(
    ip, DaqifiDeviceFactory.DefaultTcpDataPort, DeviceConnectionOptions.Observing);

// Connecting manually? Set it before InitializeAsync.
device.PreserveActiveStream = true;
await device.InitializeAsync();
```

| Initialization step | Default | `PreserveActiveStream` |
|---|---|---|
| `SYSTem:ECHO -1` | sent | sent — text-mode only, no stream state |
| `SYSTem:StopStreamData` | sent | **skipped** — this is what kills the other session |
| `SYSTem:POWer:STATe 1` | sent | **skipped** |
| `SYSTem:STReam:FORmat 0` | sent | **skipped** |
| `SYSTem:STReam:INTerface` (USB routing) | sent | **skipped** — would steal the stream |
| `SYSTem:SYSInfoPB?` + capability query | sent | sent — read-only |

The observing session is fully usable for status, metadata, and channel inspection, and reaches
`DeviceState.Ready` exactly as a normal connection does. What it is **not** is configured to stream:
the format and destination interface are left as the other session set them, and frames keep going
wherever they were already going. A session that later wants to stream itself has to take control,
which necessarily stops the other one — reconnect with the default options for that.

**Limits.** This is a courtesy, not arbitration. It stops *this* library from clobbering a stream;
it cannot stop anything else from doing so, and the firmware does not currently reject or announce
a second controlling session. Within one process, prefer
[`DaqifiDeviceRegistry`](#managing-multiple-devices-daqifideviceregistry) — it refuses to open the
same physical unit twice at all, so the conflict never arises. Across processes there is no
protection beyond both sides opting in to `PreserveActiveStream`.

### Manual Device Connection (Advanced)

For cases where you need more control over the connection process:

```csharp
using Daqifi.Core.Device;
using Daqifi.Core.Communication.Transport;

// Create and connect transport manually. Every step takes a CancellationToken, so a user who
// cancels stops the attempt where it stands instead of waiting out the retries.
var transport = new TcpStreamTransport("192.168.1.100", 9760);
await transport.ConnectAsync(new ConnectionRetryOptions { MaxAttempts = 3 }, cancellationToken);

// Create device with transport
await using var device = new DaqifiDevice("My Device", transport);
await device.ConnectAsync(cancellationToken);

// InitializeAsync takes control of the device and stops any stream it was already running.
// Set device.PreserveActiveStream = true first if another session may be streaming — see
// "Connecting stops any stream already running" above.
await device.InitializeAsync(cancellationToken: cancellationToken);

// Now ready to send commands
device.Send(ScpiMessageProducer.GetDeviceInfo);
```

### Connecting and disconnecting without blocking

`ConnectAsync`, `DisconnectAsync` and `DisposeAsync` are the non-blocking forms of `Connect`,
`Disconnect` and `Dispose`. Prefer them on a UI thread: the synchronous disconnect waits (up to ten
seconds) for any command exchange still in flight, which on a UI thread is a visible freeze.

```csharp
// Disposal never blocks the caller — this is the recommended pattern.
await using var device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);
```

A cancelled `ConnectAsync` throws `OperationCanceledException` and leaves nothing half-open — if the
transport had already come up when the cancel landed, it is closed again.

`DisconnectAsync` treats its token differently, and deliberately: cancelling it skips the wait for an
in-flight command exchange and proceeds straight to teardown. It never aborts the disconnect and
never throws `OperationCanceledException`, because a teardown abandoned part-way would leave the
device in an indeterminate state. On return the device is always disconnected.

The synchronous `Connect`, `Disconnect` and `Dispose` remain, unchanged, for existing callers.

`ConnectAsync` and `DisconnectAsync` are declared directly on `IDevice` (not default-interface-method
shims over `Connect`/`Disconnect`), and `IDevice` extends `IAsyncDisposable`, so all three are reachable
through the interface with no cast to `DaqifiDevice`:

```csharp
IDevice device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);
await device.DisconnectAsync(cancellationToken);
await device.DisposeAsync();
```

### Error Handling

```csharp
try
{
    await using var device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);
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

#### Telling "the device went away" apart from a bug

Every device operation opens with a connectivity guard. When it fails, it throws
`DeviceNotConnectedException` — a typed exception, so a disconnect can be classified without
matching on the exception message:

```csharp
using Daqifi.Core.Device;
using Daqifi.Core.Device.SdCard;

var sdCard = (ISdCardOperations)device;

try
{
    IReadOnlyList<SdCardFileInfo> files = await sdCard.GetSdCardFilesAsync();
}
catch (DeviceNotConnectedException ex)
{
    // Ordinary and expected: the user pressed Disconnect mid-refresh, or WiFi dropped.
    // Log at warning with reconnect guidance — do NOT raise an error-tracker issue.
    logger.LogWarning(ex, ex.IsShuttingDown
        ? "Device is disconnecting; skipping refresh."
        : "Device is not connected. Reconnect and try again.");
}
catch (InvalidOperationException ex)
{
    // Everything else on this path really is a defect — e.g. calling a text command
    // re-entrantly, or using a device constructed without a transport.
    logger.LogError(ex, "Bug: invalid SD card operation.");
}
```

Notes:

- `DeviceNotConnectedException` derives from `InvalidOperationException`, which is what these
  guards threw before, so existing `catch (InvalidOperationException)` blocks keep working. Order
  the `catch` clauses most-specific-first, as above.
- `IsShuttingDown` is `true` when the guard fired because a `Disconnect()` or `Dispose()` is in
  flight (or already finished) rather than because the device was never connected. Both mean "the
  device is unavailable"; the flag is there for callers that want to suppress a retry prompt when
  the user initiated the disconnect themselves.
- `TransportNotConnectedException` is its sibling, not its base: it reports that the underlying
  stream is gone (a serial unplug, a dropped TCP socket) while the device still believed it was
  connected. Catch `DeviceNotConnectedException` for the device-state case and
  `TransportNotConnectedException` for the transport case — or both, since they classify the same
  way for reporting purposes.

### Connection Status Monitoring

```csharp
await using var device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);

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
            Console.WriteLine("Connection lost");
            break;
        case ConnectionStatus.Retrying:
            Console.WriteLine("Reconnecting...");   // only with reconnect enabled
            break;
        case ConnectionStatus.Failed:
            Console.WriteLine("Reconnection gave up");
            break;
    }
};
```

See [Reconnecting automatically after a drop](#reconnecting-automatically-after-a-drop) for the
`Retrying` and `Failed` states, which a device only ever reports once reconnection is turned on.

### Detecting a dropped connection

`ConnectionStatus.Lost` means the connection ended without anyone asking it to — the USB cable was
pulled, the device lost power, the network dropped. `Disconnect()` reports
`ConnectionStatus.Disconnected` instead, never `Lost`, so the two are always distinguishable.

Neither OS handle is a liveness signal on its own: `SerialPort.IsOpen` stays `true` after a USB
device is physically unplugged, and `TcpClient.Connected` describes the last completed operation
rather than the current link. The transports therefore watch for a drop directly, and the detection
time is bounded:

| Transport | Drop | Reported within |
|---|---|---|
| Serial | Cable unplugged / device re-enumerated | **~3 s** — port-presence poll (1 s cadence, 2 consecutive misses) |
| Serial | Reads or writes failing while traffic flows | **< 1 s** — 5 consecutive I/O failures with no successful transfer between them |
| TCP | Peer closed, connection reset | **< 1 s** — same I/O fault escalation |
| TCP | Link silently severed (power loss, WiFi drop) | **~20 s** — TCP keep-alive (10 s idle, 3 s probes, 3 retries) |

Both serial detectors are cross-platform and behave identically on Windows, macOS, and Linux: port
presence is `SerialPort.GetPortNames()` (falling back to the device node on Unix), with no WMI or
other OS-specific device-change watcher. The presence poll is armed only if the port is visible to
that probe at connect time, so an exotic port-name spelling disables the check rather than
producing a false drop.

A single failed read never disconnects anything. Escalation requires a *run* of failures with no
successful transfer in between, so a stream that glitches and recovers stays connected. A read or
write *timeout* is not a failure at all — it is what an idle or momentarily busy device looks
like. To disable the serial presence poll and rely on I/O escalation alone, construct the transport
with `livenessCheckInterval: TimeSpan.Zero`.

When a drop is detected the transport closes its handle, so `IsConnected` already reads `false` by
the time `StatusChanged` fires. The device's message consumer and producer are *not* stopped
automatically — call `Disconnect()` (or `Dispose()`) once you have handled `Lost` to release them.

`Lost` is raised on an internal thread — the reader loop or the liveness timer, whichever detected
the drop — so treat the handler like any background callback: do the minimum, and push teardown or
reconnection onto your own thread rather than blocking inside it.

```csharp
device.StatusChanged += (_, e) =>
{
    if (e.Status != ConnectionStatus.Lost) return;
    _ = Task.Run(() => device.Disconnect());   // don't tear down inside the callback
};
```

Because that callback runs on a background thread, a UI handler that touches a bound control from it
throws — so the raise is isolated. A `StatusChanged` subscriber that throws cannot stop the
transition, cannot stop automatic reconnection from starting, and cannot keep the transport from
releasing its handle; the exception is reported on `ErrorOccurred` as
`DeviceErrorSource.StatusNotification` and written to the device logger. Isolation is per raise, not
per subscriber: the first handler to throw ends that one notification for the rest of the invocation
list.

Custom transports can opt into the same escalation by implementing `ITransportHealthSink`; the
reader and writer loops report every read/write outcome to a transport that does.

The failures that feed this escalation are also reported individually, as they happen, on
`IDevice.ErrorOccurred` — see [Error Surface](#error-surface). That event is diagnostics only;
`ConnectionStatus.Lost` remains the single signal that means "the connection is over".

### Reconnecting automatically after a drop

By default a drop is where the story ends: `Lost` is reported and nothing else happens. Set
`ReconnectOptions` and the device will rebuild the session by itself — reconnect the transport,
re-initialize, put the channel configuration back, and restart a stream that was interrupted — with
no code from you in the loop.

```csharp
device.ReconnectOptions = ReconnectOptions.Default;   // 5 attempts, 1 s backing off to 30 s

device.Reconnected += (_, e) =>
    Console.WriteLine($"back after {e.Outage.TotalSeconds:0.#}s (attempt {e.AttemptNumber})");

device.ReconnectFailed += (_, e) =>
    Console.WriteLine($"gave up after {e.AttemptsMade} attempts: {e.LastError?.Message}");
```

`ReconnectOptions.Fast` and `ReconnectOptions.Resilient` are ready-made policies for links that
blip briefly and for unattended long runs respectively; build your own for anything else.
`ReconnectOptions.Disabled` (the default) says so explicitly.

**What you can watch.** `ReconnectAttempt` fires before each attempt with its number and the wait
that precedes it; `Reconnected` fires once the session is fully back; `ReconnectFailed` fires when
it stops without one. The status follows along, so a UI can show progress without subscribing to
anything new:

| Status | Meaning |
|---|---|
| `Lost` | The drop was detected. Also where a cancelled reconnect leaves the device. |
| `Retrying` | Waiting out the backoff before the next attempt. |
| `Connected` | The session is back, configuration and stream included. |
| `Failed` | Every attempt failed. Terminal — nothing more will be tried. |

Running out of attempts is deliberately hard to miss: as well as `ReconnectFailed` and the `Failed`
status, it is logged as an error and raised on `ErrorOccurred` with source
`DeviceErrorSource.Reconnect`, carrying a `DeviceReconnectFailedException` whose inner exception is
whatever ended the final attempt.

**What gets restored.** Only what the library itself owns:

- the set of enabled channels (analog and digital),
- the streaming frequency, and
- an active stream, unless `ResumeStreaming` is turned off.

It does not matter whether you established that state through the typed API
(`EnableChannels`, `StartStreaming`) or by sending the SCPI yourself with
`Send(ScpiMessageProducer.StartStreaming(...))` — the device recognizes its own streaming and
ADC-enable commands whichever way they were sent, so a session driven entirely by raw commands is
restored just the same. The one exception is the global DIO enable: it is a single switch for the
whole port rather than a per-channel mask, so sending it directly tells the device nothing about
*which* digital channels you wanted. Use `EnableChannels` for those.

**What does not.** Everything else is the device's own state, and Core does not presume to know
what it should be after an outage of unknown length:

- DIO directions and output levels, PWM enable/duty/frequency, analog outputs, and calibration
  written only to device RAM;
- an SD card logging session — the device keeps logging or does not, entirely on its own;
- **any operation that was in flight.** An SD card download interrupted by a drop fails, and is
  neither resumed nor retried; run it again once `Reconnected` says the device is back.

A resumed stream is a genuinely new session: timestamp reconstruction re-anchors and the gap
detector resets, because the device's tick counter may well have restarted while it was away.
`Reconnected.Outage` is the measure of the interruption, not a `GapDetected` event.

**Same endpoint only.** Reconnection re-opens the endpoint the device was already using. It cannot
follow a device that moved: a serial device that comes back on a different port path, or one whose
IP address changed after a reboot, is a new endpoint and needs a fresh `DaqifiDeviceFactory`
connect. Failing over from one transport to another (USB to WiFi, say) is out of scope.

**Stopping it.** `CancelReconnect()` stops the loop at its next checkpoint and leaves the device on
`Lost`; `Disconnect()` and `Dispose()` do the same and then tear down. A caller-issued `Connect()`
or `Disconnect()` always wins — the loop unwinds without touching the session the caller
established, and a `Disconnect()` issued from inside a `Lost` handler stops the reconnect before it
even starts.

Which is the other half of the rule: with reconnect enabled, **stop tearing down on `Lost`
yourself**. The teardown shown under [Detecting a dropped
connection](#detecting-a-dropped-connection) is for devices without a reconnect policy. Here the
device does it for you, between attempts, and doing it as well just cancels the recovery you asked
for.

```csharp
device.ReconnectOptions = new ReconnectOptions
{
    Enabled = true,
    MaxAttempts = 10,
    InitialDelay = TimeSpan.FromSeconds(2),
    MaxDelay = TimeSpan.FromMinutes(1),
    ResumeStreaming = true
};
```

All three events are raised on a background thread, and a handler that throws is caught and
ignored — it cannot stop a reconnect in progress.

### Working with Device Metadata

After initialization, device metadata is populated:

```csharp
await using var device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);

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

await using var device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);

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
await using var device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);

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
await using var device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);

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

`Channels`/`GetChannelsSnapshot()`, along with the rest of the members below, are declared directly
on `IStreamingDevice` (#333) — no cast to the concrete device type needed, whether `device` came
from `DaqifiDeviceFactory` or is held only as an `IStreamingDevice` reference.

```csharp
using Daqifi.Core.Channel;

// Channels are populated after a status message is received from the device. Channels itself is
// a live view that can be repopulated concurrently on the consumer thread, so a snapshot (rather
// than the live property) is what's safe to run LINQ queries against off that thread.
var channels = device.GetChannelsSnapshot();
var ai0 = channels.First(c => c.Type == ChannelType.Analog && c.ChannelNumber == 0);
var ai2 = channels.First(c => c.Type == ChannelType.Analog && c.ChannelNumber == 2);

// Enable analog input channels (the device receives the combined bitmask, e.g. "5").
device.EnableChannels(new[] { ai0, ai2 });

// Disable a single channel — the recomputed mask reflects the remaining enabled channels.
device.DisableChannel(ai0);

// Turn everything off.
device.DisableAllChannels();

// Digital I/O: set direction and drive an output.
var dio1 = channels.First(c => c.Type == ChannelType.Digital && c.ChannelNumber == 1);
device.SetDioDirection(dio1, ChannelDirection.Output);
device.SetDioValue(dio1, true); // drive high

// PWM on a capable channel (IDigitalChannel.IsPwmCapable). Duty is per channel; the
// frequency is device-wide because one hardware timer drives every PWM channel.
var pwm = channels.OfType<IDigitalChannel>().First(c => c.IsPwmCapable);
device.SetPwmDutyCycle(pwm, 25);  // 1-100 %
device.SetPwmFrequency(1000);     // 6-50000 Hz, shared by all PWM channels
device.SetPwmEnabled(pwm, true);  // start; SetPwmEnabled(pwm, false) stops (pin goes high-impedance)

// Analog output (NQ3 only) — addressed by channel number; staged value is applied immediately.
device.SetAnalogOutput(0, 2.5); // DAC channel 0 to 2.5 V

// Reboot the device (also disconnects, since the device drops its link while restarting).
device.Reboot();
```

> Channel objects passed to the enable/disable and DIO methods must belong to the device's
> `Channels` collection (so the internal state and bitmask stay in sync). Analog-output (DAC)
> channels are not part of `Channels`, so `SetAnalogOutput` takes a channel number directly.

Every method above has a cancellable `...Async` twin (`EnableChannelsAsync`, `DisableChannelAsync`,
`SetDioValueAsync`, `SetPwmEnabledAsync`, `SetAnalogOutputAsync`, `RebootAsync`, and the rest), declared
directly on `IStreamingDevice`:

```csharp
await streamingDevice.EnableChannelsAsync(new[] { ai0, ai2 }, cancellationToken);
await streamingDevice.SetDioValueAsync(dio1, true, cancellationToken);
await streamingDevice.RebootAsync(cancellationToken);
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
device.Send(ScpiMessageProducer.EnableAdcChannels("255")); // Enable 8 channels (bitmask 0xFF = 255)
device.Send(ScpiMessageProducer.DisableDeviceEcho);
device.Send(ScpiMessageProducer.SetProtobufStreamFormat);
```

## Device Diagnostics

`IDeviceDiagnostics` (implemented by `DaqifiStreamingDevice`) is a typed wrapper over the firmware's
logging and diagnostics SCPI surface — the system log, runtime log levels, SCPI command history,
error-queue depth, and streaming/memory performance counters. These values originate **on the
device**; this is not a client-side instrumentation framework.

`DaqifiDeviceFactory` returns `DaqifiStreamingDevice` directly, which implements
`IDeviceDiagnostics` — no cast needed to reach these members.

```csharp
using Daqifi.Core.Device.Diagnostics;

await using var device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);

// System log (reading the log also clears the device buffer).
IReadOnlyList<SystemLogEntry> log = await device.GetSystemLogAsync();
foreach (var entry in log) Console.WriteLine(entry.Message);
await device.ClearSystemLogAsync();

// Runtime log levels (0 = None, 1 = Error, 2 = Info, 3 = Debug). The returned
// setting reflects the level actually applied, which a module's ceiling may cap.
LogLevelSetting applied = await device.SetLogLevelAsync("STREAM", 2);
Console.WriteLine($"{applied.Module}: {applied.Level} (ceiling {applied.Ceiling})");

// SCPI command history (oldest first — the device numbers lines backwards from
// the present, so the newest command is last) and error-queue depth (non-destructive).
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

`DaqifiDevice` and `DaqifiStreamingDevice` are safe to use from multiple threads. The contract has
three parts, and the third one is the part people get wrong.

### 1. Any single operation is safe to call from any thread

Enable a channel on one thread, drive a DIO pin on another, run a text query on a third — nothing
you can do with individual calls will produce corrupt SCPI on the wire, and a query's reply always
belongs to that query. Core enforces this itself:

- Every string command goes onto one background queue drained by one writer thread, so two commands
  can never be spliced together mid-command.
- Text queries (SD card listings, diagnostics, LAN chip info, the capability document — anything
  that reads a reply) run one at a time per device. They take the device's operation lock, and a
  `Send()` from another thread while one is running is **held back and delivered afterwards** rather
  than written into the middle of somebody else's answer.

```csharp
// Safe. Nothing here needs coordinating.
Parallel.For(0, 10, i => device.Send(ScpiMessageProducer.GetDeviceInfo));
```

### 2. A sequence that must not be split goes in `RunExclusiveAsync`

The one thing a single call cannot express is "these commands belong together". Two threads each
doing set-direction-then-set-value can have their commands interleaved, and the pin ends up in a
state neither thread asked for. Wrap the sequence:

```csharp
await device.RunExclusiveAsync(_ =>
{
    streaming.SetDioDirection(channel, ChannelDirection.Output);
    streaming.SetDioValue(channel, true);
    return Task.CompletedTask;
});

// Bodies can be async and can return a value. Text queries nest freely inside —
// the lock is reentrant on the same logical flow.
var files = await device.RunExclusiveAsync(async ct =>
{
    await sd.StartSdCardLoggingAsync(cancellationToken: ct);
    return await sd.GetSdCardFilesAsync(ct);
});
```

While the body runs, other threads' `Send()` calls are deferred (they still return immediately) and
other threads' text queries wait. Keep bodies short, and do not start background work inside one:
a `Task.Run` launched from the body inherits the flow's ownership of the lock, so its commands would
*not* be deferred and could still interleave.

`RunExclusiveAsync` is per device. Two devices always run in parallel — there is no global lock.

### 3. Connect / Disconnect / Dispose serialize themselves, but do not wait forever

The device never drives its transport from two threads at once, so you do not have to funnel
lifecycle calls through one thread. What you should know:

- `Connect()` throws `TimeoutException` if another connect or disconnect is still in flight after
  10 seconds. Nothing was opened, so retrying is safe.
- `Disconnect()` / `Dispose()` give an in-flight text query or `RunExclusiveAsync` block a bounded
  courtesy wait (10 seconds) and then tear down regardless. A teardown that waited forever would
  hang on a wedged serial port, which is worse. Calling `Disconnect()` from *inside* your own
  `RunExclusiveAsync` body is fine and does not wait at all.
- Deferred sends belonging to an operation that was still running when the device went away are
  logged and dropped, consistent with `Send()` never having guaranteed delivery.

### What is *not* covered

- Streaming callbacks (`StreamSamplesAsync`, sample events, decode) never take the operation lock
  and are never blocked by it. A live stream keeps flowing while control operations run.
- `IChannel` objects are mutable and shared. Reading `channel.IsEnabled` while another thread
  reconfigures it can give you a torn view; use `GetChannelsSnapshot()` for a consistent one.
- Two `DaqifiDevice` instances pointed at the *same* physical unit over two transports are two
  independent locks and will fight. Use `DaqifiDeviceRegistry`, which detects that duplicate.

### Reference implementation

`src/Daqifi.Mcp/DaqifiAgent.cs` is the worked example of a fully concurrent consumer: the MCP
transport dispatches tool calls in parallel, and every multi-command tool goes through
`RunExclusiveAsync`. Its remaining `SemaphoreSlim` guards only its own connection registry, not the
devices.

## Delivery Failures

`Send()` is fire-and-forget: it queues the message and returns before the background thread
writes it, so delivery is not guaranteed. If the write fails — including a timeout, meaning the
device isn't draining its receive buffer right now — `Send()` does not throw. `DaqifiDevice` logs
a warning through its `ILogger` for every failed write, and code that needs to react to a
delivery failure (not just read it from logs) can subscribe to `DaqifiDevice.SendFailed`:

```csharp
device.SendFailed += (_, e) =>
{
    Console.WriteLine($"Delivery failed (timeout: {e.IsTimeout}): {e.Error.Message}");
};
```

A single failed write does not stop the queue: the producer keeps draining the remaining
messages regardless of whether anything observes the failure.

`Send()` also never blocks — including when another thread holds the device (see Thread Safety
above), where the message is held back and queued as soon as that finishes. So "returned" has always
meant "accepted", never "delivered", and now it can also mean "delivered a little later".

## Error Surface

Reading from the device and decoding its frames both happen on background threads, where an
exception has nobody to throw to. Those failures used to be invisible: a stream that could not be
read and a stream that could not be decoded both looked exactly like a device sending nothing.
`IDevice.ErrorOccurred` is the one place to answer *"why am I getting no samples"*.

```csharp
device.ErrorOccurred += (_, e) =>
{
    Console.WriteLine($"[{e.Source}] {e.Error.Message}");
    if (e.SuppressedCount > 0)
    {
        Console.WriteLine($"  ...and {e.SuppressedCount} more like it since the last report");
    }
};
```

`DeviceErrorEventArgs.Source` says which stage failed:

| Source | What it means |
|---|---|
| `MessageConsumer` | A read from the transport stream failed, a frame could not be parsed, or a `MessageReceived` subscriber threw |
| `StreamDecode` | A streaming frame could not be decoded into channel samples. That frame is dropped; the stream continues |
| `StatusNotification` | A `StatusChanged` subscriber threw. The status transition itself still completed |

### Observational, never escalating

Raising this event changes nothing about what the device does. There is no tear-down, no retry, no
`Status` change, and per-frame isolation is unchanged — a single malformed frame is still dropped on
its own without disturbing the stream. Deciding that a link is genuinely dead is a separate
mechanism with its own signal: `ConnectionStatus.Lost` on `StatusChanged` (see
[Detecting a dropped connection](#detecting-a-dropped-connection)). The two often fire together on a
real drop — the same failing reads feed both — but neither implies the other, and an `ErrorOccurred`
on its own is not a reason to reconnect.

Every error is also written to the device's `ILogger` at warning level, so it stays visible with no
subscriber attached.

### Throttle policy

A systematic failure repeats at the frame rate, so raises are collapsed per **bucket**, where a
bucket is (`Source`, exception type):

- The **first** occurrence in a bucket is always raised, immediately.
- After that, a bucket raises **at most once every five seconds**. Occurrences in between are
  counted and reported as `SuppressedCount` on the next raise — so a storm shows up as a number
  rather than as thousands of events.
- Buckets are independent: a *new* kind of failure is raised at once even while another kind is
  being collapsed.
- Connecting resets the throttle, so a reconnect reports its first failure immediately.

### Decode failure counter

`DaqifiStreamingDevice.DecodeFailureCount` is the always-on companion to the event: the number of
frames whose decode threw and was discarded during the current streaming session. It is reset by
`StartStreaming()`, and a healthy stream leaves it at zero — so a non-zero value while samples are
missing is the fastest confirmation that frames are arriving but not decoding.

```csharp
device.StartStreaming();
// ...
if (device.DecodeFailureCount > 0)
{
    Console.WriteLine($"{device.DecodeFailureCount} frame(s) failed to decode this session");
}
```

`ErrorOccurred` is raised on a background thread, so handlers should do the minimum and push real
work elsewhere. A handler that throws is caught and ignored — it can never disturb reading or
streaming.

## Features

- **Simple Factory API**: Single-call connection with `DaqifiDeviceFactory`
- **Multi-Device Registry**: `DaqifiDeviceRegistry` tracks the live device set and detects the same
  unit connected over two transports
- **Clean Abstraction**: Hardware details hidden behind well-defined interfaces
- **Event-Driven**: Status changes and messages handled via events
- **Observable Failures**: Background read and decode errors surface on `ErrorOccurred` instead of
  failing silently
- **Opt-in Auto-Reconnect**: A dropped connection can rebuild itself — transport, initialization,
  channel configuration and stream — with no consumer code
- **Type Safety**: Generic message types provide compile-time safety
- **Retry Support**: Built-in connection retry with exponential backoff
- **Thread-Safe Sending**: Background message queue for thread-safe command sending
- **Cross-Platform**: Compatible with .NET 9.0 and .NET 10.0
