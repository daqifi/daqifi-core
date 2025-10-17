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
}

/// <summary>
/// Device type enumeration.
/// </summary>
public enum DeviceType
{
    /// <summary>
    /// Unknown device type.
    /// </summary>
    Unknown,

    /// <summary>
    /// DAQiFi device.
    /// </summary>
    Daqifi,

    /// <summary>
    /// Nyquist device.
    /// </summary>
    Nyquist
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
