# DAQiFi Core Library

The DAQiFi Core Library is a .NET library designed to simplify interaction with DAQiFi devices. It provides a robust and intuitive API for discovering, connecting to, and communicating with DAQiFi hardware, making it an ideal choice for developers building applications that require data acquisition and device control.

## Roadmap Key Features
- **Device Discovery**: Easily find DAQiFi devices connected via USB, serial, or WiFi.
- **Connection Management**: Establish and manage connections with minimal effort.
- **Channel Configuration**: Support for both analog and digital channels, with easy configuration.
- **Data Streaming**: Stream data from devices in real-time.
- **Firmware Updates**: Manage firmware updates seamlessly.

## Getting Started

### Installation

```shell
dotnet add package Daqifi.Core
```

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

