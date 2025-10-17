# DAQiFi Core Library

The DAQiFi Core Library is a .NET library designed to simplify interaction with DAQiFi devices. It provides a robust and intuitive API for discovering, connecting to, and communicating with DAQiFi hardware, making it an ideal choice for developers building applications that require data acquisition and device control.

## Features

### ✅ Available Now
- **Device Discovery**: Find DAQiFi devices connected via WiFi, USB/Serial, or HID bootloader mode
- **Transport Layer**: UDP and Serial communication support with async/await patterns
- **Protocol Buffers**: Efficient binary message serialization for device communication
- **Cross-Platform**: Compatible with .NET 8.0 and .NET 9.0

### 🚧 In Development
- **Connection Management**: Establish and manage connections with minimal effort
- **Channel Configuration**: Support for both analog and digital channels
- **Data Streaming**: Stream data from devices in real-time
- **Firmware Updates**: Manage firmware updates seamlessly

## Getting Started

### Installation

```shell
dotnet add package Daqifi.Core
```

### Quick Start: Device Discovery

```csharp
using Daqifi.Core.Device.Discovery;

// Discover WiFi devices on your network
using var wifiFinder = new WiFiDeviceFinder();
wifiFinder.DeviceDiscovered += (sender, e) =>
{
    var device = e.DeviceInfo;
    Console.WriteLine($"Found: {device.Name} at {device.IPAddress}");
    Console.WriteLine($"  Serial: {device.SerialNumber}");
    Console.WriteLine($"  Firmware: {device.FirmwareVersion}");
};

var devices = await wifiFinder.DiscoverAsync(TimeSpan.FromSeconds(5));
Console.WriteLine($"Discovery complete. Found {devices.Count()} device(s)");

// Discover USB/Serial devices
using var serialFinder = new SerialDeviceFinder();
var serialDevices = await serialFinder.DiscoverAsync();

foreach (var device in serialDevices)
{
    Console.WriteLine($"Serial Port: {device.PortName}");
}
```

### Advanced Discovery Options

```csharp
// Use cancellation tokens for fine-grained control
using var cts = new CancellationTokenSource();
cts.CancelAfter(TimeSpan.FromSeconds(10));

try
{
    var devices = await wifiFinder.DiscoverAsync(cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Discovery was cancelled");
}

// Discover on custom UDP port (default is 30303)
using var customFinder = new WiFiDeviceFinder(port: 12345);
var customDevices = await customFinder.DiscoverAsync();
```

### Supported Devices

The library automatically detects DAQiFi hardware:
- **Nyquist 1**: DAQiFi's data acquisition device
- **Nyquist 3**: DAQiFi's advanced data acquisition device

Both devices are identified by their part number in the discovery response.

### Connection Types

- **WiFi**: Network-connected devices discovered via UDP broadcast
- **Serial**: USB-connected devices enumerated via serial ports
- **HID**: Devices in bootloader mode (requires platform-specific HID library)

## Real-World Usage

This library powers the [DAQiFi Desktop](https://github.com/daqifi/daqifi-desktop) application and is designed for:
- Custom data acquisition applications
- Automated testing systems
- Industrial monitoring solutions
- Research and development tools
- Any application requiring DAQiFi device integration

## Requirements

- .NET 8.0 or .NET 9.0
- For WiFi discovery: UDP port 30303 must be accessible (firewall configuration may be required)
- For Serial discovery: Appropriate USB drivers for your platform
- Admin privileges may be required for firewall configuration on Windows

## Publishing

This library follows semantic versioning (MAJOR.MINOR.PATCH):
- MAJOR: Breaking changes
- MINOR: New features (backwards compatible)
- PATCH: Bug fixes

Releases are automated via GitHub Actions:
1. Create a new GitHub Release
2. Tag it with `vX.Y.Z` (e.g. `v1.0.0`)
3. For pre-releases, use `-alpha.1`, `-beta.1`, `-rc.1` suffixes
4. Publishing to NuGet happens automatically on release

