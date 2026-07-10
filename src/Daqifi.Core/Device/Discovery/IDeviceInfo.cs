using System.Net;

#nullable enable

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Interface representing discovered device metadata.
/// </summary>
public interface IDeviceInfo
{
    /// <summary>
    /// Gets the device name/hostname.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the device serial number.
    /// </summary>
    string SerialNumber { get; }

    /// <summary>
    /// Gets the firmware version.
    /// </summary>
    string FirmwareVersion { get; }

    /// <summary>
    /// Gets the IP address (for network devices).
    /// </summary>
    IPAddress? IPAddress { get; }

    /// <summary>
    /// Gets the MAC address (for network devices).
    /// </summary>
    string? MacAddress { get; }

    /// <summary>
    /// Gets the TCP port (for network devices).
    /// </summary>
    int? Port { get; }

    /// <summary>
    /// Gets the local interface address that discovered this device (for network devices).
    /// Used for binding TCP connections in multi-NIC scenarios.
    /// </summary>
    IPAddress? LocalInterfaceAddress { get; }

    /// <summary>
    /// Gets the device type.
    /// </summary>
    DeviceType Type { get; }

    /// <summary>
    /// Gets a value indicating whether the device is powered on.
    /// </summary>
    bool IsPowerOn { get; }

    /// <summary>
    /// Gets the connection type (WiFi, Serial, HID).
    /// </summary>
    ConnectionType ConnectionType { get; }

    /// <summary>
    /// Gets the port name (for serial devices).
    /// </summary>
    string? PortName { get; }

    /// <summary>
    /// Gets the device path (for HID devices).
    /// </summary>
    string? DevicePath { get; }

    /// <summary>
    /// Gets the USB physical-location key (e.g. <c>Port_#0001.Hub_#0001</c>), or null if it
    /// could not be resolved. Stable for a given physical USB port across a device's
    /// transitions between transports (e.g. serial app mode ⇄ HID bootloader mode) and
    /// re-enumerations, so it can be used to correlate the same physical unit and disambiguate
    /// multiple identical devices (same VID/PID, no serial number). Resolved via
    /// <see cref="IUsbLocationProvider"/>; Windows-only in v1, always null elsewhere.
    /// </summary>
    /// <remarks>
    /// Implemented as a default interface property (so adding it does not break existing
    /// implementers — the same technique used by
    /// <see cref="Daqifi.Core.Firmware.IFirmwareUpdateService"/>'s targeted-update overloads):
    /// an external <c>IDeviceInfo</c> implementation compiled before this member existed
    /// inherits this default (null, meaning "not resolved") without recompiling.
    /// <see cref="DeviceInfo"/> overrides it with a real stored value.
    /// </remarks>
    string? LocationKey => null;
}

/// <summary>
/// Device type enumeration.
/// </summary>
public enum DeviceType
{
    /// <summary>
    /// Unknown device type.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Nyquist 1 device.
    /// </summary>
    Nyquist1 = 1,

    /// <summary>
    /// Nyquist 2 device.
    /// </summary>
    Nyquist2 = 2,

    /// <summary>
    /// Nyquist 3 device.
    /// </summary>
    Nyquist3 = 3
}

/// <summary>
/// Connection type enumeration.
/// </summary>
public enum ConnectionType
{
    /// <summary>
    /// Unknown connection type.
    /// </summary>
    Unknown,

    /// <summary>
    /// WiFi/TCP connection.
    /// </summary>
    WiFi,

    /// <summary>
    /// Serial/USB connection.
    /// </summary>
    Serial,

    /// <summary>
    /// USB HID connection (bootloader mode).
    /// </summary>
    Hid
}
