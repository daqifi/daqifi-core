# DAQiFi Core

> **Revolutionizing the data collection experience with convenient, portable device connectivity.**
>
> The official cross-platform .NET SDK for DAQiFi wireless data acquisition devices.

[![NuGet](https://img.shields.io/nuget/v/Daqifi.Core?style=flat-square&logo=nuget)](https://www.nuget.org/packages/Daqifi.Core)
[![Downloads](https://img.shields.io/nuget/dt/Daqifi.Core?style=flat-square)](https://www.nuget.org/packages/Daqifi.Core)
[![Build](https://img.shields.io/github/actions/workflow/status/daqifi/daqifi-core/ci.yml?style=flat-square&label=build)](https://github.com/daqifi/daqifi-core/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/github/license/daqifi/daqifi-core?style=flat-square)](LICENSE)
![.NET](https://img.shields.io/badge/.NET-9.0%20%7C%2010.0-512BD4?style=flat-square&logo=dotnet)
![Platforms](https://img.shields.io/badge/platforms-Windows%20%7C%20macOS%20%7C%20Linux-blue?style=flat-square)

**[daqifi.com](https://daqifi.com)** · **[DAQiFi Desktop](https://github.com/daqifi/daqifi-desktop)** · **[Report an issue](https://github.com/daqifi/daqifi-core/issues)**

---

## What is DAQiFi Core?

DAQiFi builds wireless data acquisition hardware designed to get out of the way so you can focus on the data, not the collection process.

**DAQiFi Core is how you integrate that hardware into your own .NET applications** — custom dashboards, automated test rigs, research pipelines, production-monitoring tools. Discover devices, connect over WiFi or USB, stream samples in real time, configure networks, push firmware updates — all from one async, strongly-typed .NET API.

Prefer a ready-made GUI? Check out [DAQiFi Desktop](https://github.com/daqifi/daqifi-desktop), which is built on top of this library.

Want to drive a device from an AI assistant? The repo also ships an **[MCP server](src/Daqifi.Mcp)** — point Claude, Cursor, Codex, or any MCP-aware client at it to discover, configure channels, set the sample rate, and run SD-card logging through plain conversation.

## See it in 30 seconds

```shell
dotnet add package Daqifi.Core
```

```csharp
using Daqifi.Core.Device;
using Daqifi.Core.Communication.Producers;

// Connect — transport and device initialization handled for you
using var device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760);

// Subscribe to incoming samples
device.MessageReceived += (_, e) =>
{
    if (e.Message.Data is DaqifiOutMessage msg)
        Console.WriteLine($"{msg.MsgTimeStamp}: {string.Join(", ", msg.AnalogInData)}");
};

// Enable analog channels via bitmask (0b11 = first 2 channels), then stream at 100 Hz
device.Send(ScpiMessageProducer.EnableAdcChannels("3"));
device.Send(ScpiMessageProducer.StartStreaming(100));
```

A real, working program — no GUI required.

## Common applications

DAQiFi hardware is in the field for work like:

- **Research labs** — moon regolith testing and similar materials studies
- **Medical R&D** — prosthetic socket pressure testing
- **Industrial monitoring** — wireless multi-channel sensing
- **Engineering education** — SCPI command structure and LabVIEW compatibility
- **Test automation** — scripted benchtop measurements

More examples at [daqifi.com](https://daqifi.com).

## Where DAQiFi Core fits

| Layer | What it is |
|---|---|
| Hardware | Nyquist 1 / Nyquist 3 — wireless DAQ devices (and their on-device firmware) |
| **SDK** | **DAQiFi Core — this library** |
| App | [DAQiFi Desktop](https://github.com/daqifi/daqifi-desktop) — GUI built on this SDK |
| Agent | [MCP server](src/Daqifi.Mcp) — drive a device from Claude / Cursor / any MCP client |
| Your code | Custom apps, dashboards, pipelines, test rigs |

## What you can do

| Capability | What it gives you |
|---|---|
| **Auto-discovery** | Find any DAQiFi on WiFi or USB in seconds — no IP hunting, no config files |
| **One-line connect** | `DaqifiDeviceFactory.ConnectTcpAsync(...)` wraps transport setup and device init; retries are opt-in via `DeviceConnectionOptions` |
| **Real-time streaming** | Event-driven async API; no polling loops to write |
| **Digital I/O** | Set any DIO pin as input or output and drive outputs high/low; inputs stream alongside analog data |
| **SD card operations** | List, download, delete, format, and start/stop SD logging over USB / serial |
| **Network configuration** | Push WiFi credentials and static LAN IPs from your app |
| **Firmware updates** | PIC32 and WiFi-module flashing with progress and cancellation |
| **Cross-platform** | .NET 9.0 and 10.0 on Windows, macOS, Linux |

## Quick recipes

### Connection options

Pick whichever transport fits your setup — each snippet is a standalone, copy-paste-ready starting point.

**TCP with a resilient retry preset** (5 retries, longer timeouts):

```csharp
using var device = await DaqifiDeviceFactory.ConnectTcpAsync(
    "192.168.1.100", 9760, DeviceConnectionOptions.Resilient);
```

**Serial / USB:**

```csharp
// Replace with your OS-specific port:
//   Windows: "COM3"   •   macOS: "/dev/cu.usbmodem1"   •   Linux: "/dev/ttyACM0"
using var device = await DaqifiDeviceFactory.ConnectSerialAsync("COM3");
```

**From a discovered device:**

```csharp
using var finder = new WiFiDeviceFinder();
var devices = await finder.DiscoverAsync(TimeSpan.FromSeconds(5));
using var device = await DaqifiDeviceFactory.ConnectFromDeviceInfoAsync(devices.First());
```

### Custom retry options

```csharp
using Daqifi.Core.Communication.Transport;

var options = new DeviceConnectionOptions
{
    DeviceName = "My DAQiFi",
    ConnectionRetry = new ConnectionRetryOptions
    {
        MaxAttempts = 3,
        ConnectionTimeout = TimeSpan.FromSeconds(10)
    },
    InitializeDevice = true
};
using var device = await DaqifiDeviceFactory.ConnectTcpAsync("192.168.1.100", 9760, options);
```

### Device discovery

```csharp
using Daqifi.Core.Device.Discovery;

// WiFi — UDP broadcast on port 30303 by default
using var wifiFinder = new WiFiDeviceFinder();
wifiFinder.DeviceDiscovered += (_, e) =>
    Console.WriteLine($"Found: {e.DeviceInfo.Name} at {e.DeviceInfo.IPAddress}");

var wifiDevices = await wifiFinder.DiscoverAsync(TimeSpan.FromSeconds(5));

// USB / Serial
using var serialFinder = new SerialDeviceFinder();
var serialDevices = await serialFinder.DiscoverAsync();
```

Need fine-grained control? Pass a `CancellationToken` or override the discovery port:

```csharp
using var cts = new CancellationTokenSource();
cts.CancelAfter(TimeSpan.FromSeconds(10));
var devices = await wifiFinder.DiscoverAsync(cts.Token);

using var customFinder = new WiFiDeviceFinder(discoveryPort: 12345);
```

### Digital output

Digital channels default to inputs. Flip one to output and drive it — the level is applied
immediately, and flipping back to input releases the pin to high-impedance.

```csharp
using Daqifi.Core.Channel;

var channels = device.GetChannelsSnapshot();
var dio3 = channels.First(c => c.Type == ChannelType.Digital && c.ChannelNumber == 3);

device.SetDioDirection(dio3, ChannelDirection.Output);
device.SetDioValue(dio3, true);   // drive high
device.SetDioValue(dio3, false);  // drive low

device.SetDioDirection(dio3, ChannelDirection.Input); // back to a streamed input
```

### Network configuration

Devices implementing `INetworkConfigurable` accept programmatic WiFi and LAN configuration. `Mode`, `Ssid`, and `Password` are always applied on every call; only `StaticIP`, `SubnetMask`, and `Gateway` honor `null` as "leave unchanged" — so DHCP-only callers can omit the static-IP fields without affecting their DHCP setup.

```csharp
using System.Net;
using Daqifi.Core.Device.Network;

if (device is INetworkConfigurable networkDevice)
{
    var config = new NetworkConfiguration
    {
        Ssid       = "MyNetwork",
        Password   = "secret",
        Mode       = WifiMode.ExistingNetwork,
        StaticIP   = IPAddress.Parse("192.168.1.42"),
        SubnetMask = IPAddress.Parse("255.255.255.0"),
        Gateway    = IPAddress.Parse("192.168.1.1"),
    };
    await networkDevice.UpdateNetworkConfigurationAsync(config);
}
```

### Firmware updates

`IFirmwareUpdateService` orchestrates both PIC32 and WiFi-module flashing with explicit state transitions and `IProgress<FirmwareUpdateProgress>` for UI / CLI reporting.

- `UpdateFirmwareAsync(...)` — PIC32 firmware flashing from a local Intel HEX file
- `UpdateWifiModuleAsync(...)` — WiFi module flashing via an external tool runner. Automatically checks the device's current WiFi-chip firmware against the latest GitHub release and skips the flash if already up to date.

> **Note:** The default WiFi flash tool config uses `winc_flash_tool.cmd` conventions. On macOS / Linux, supply a compatible executable and argument template via `FirmwareUpdateServiceOptions`.

## Supported devices

| Device | Channels | Resolution | Range |
|---|---|---|---|
| **Nyquist 1** | 16 analog in | 12-bit | 0–5 V |
| **Nyquist 3** | 8 analog in | 18-bit | ±10 V |

Both are auto-detected by part number during discovery.

Don't have one yet? **[See the DAQiFi lineup →](https://daqifi.com)**

## Connection types

- **WiFi** — discovered via UDP broadcast (port 30303)
- **Serial** — USB-connected, enumerated as serial ports
- **HID** — used during firmware updates (HidSharp backend)

## Requirements

- .NET 9.0 or .NET 10.0 on Windows, macOS, or Linux
- WiFi discovery: UDP port 30303 reachable (firewall may need configuring; admin may be required on Windows)
- Serial discovery: appropriate USB drivers for your platform

## Community & support

- [Open an issue](https://github.com/daqifi/daqifi-core/issues) for bugs or feature requests
- Reach the team via [daqifi.com](https://daqifi.com) for commercial integrations and custom hardware needs

## For maintainers

This library follows semantic versioning. Releases are automated via GitHub Actions:

1. Create a new GitHub Release
2. Tag it `vX.Y.Z` (pre-releases use `-alpha.1`, `-beta.1`, `-rc.1` suffixes)
3. Publishing to NuGet happens automatically on release

The same release also packs and publishes the **`Daqifi.Mcp`** MCP server as a .NET tool (`dotnet tool install -g Daqifi.Mcp`).

---

<p align="center">
  Built by <a href="https://daqifi.com">DAQiFi</a> · Licensed under <a href="LICENSE">MIT</a>
</p>
