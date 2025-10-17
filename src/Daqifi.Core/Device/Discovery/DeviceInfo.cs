using System.Net;

#nullable enable

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Represents discovered device metadata.
/// </summary>
public class DeviceInfo : IDeviceInfo
{
    /// <summary>
    /// Gets or sets the device name/hostname.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the device serial number.
    /// </summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the firmware version.
    /// </summary>
    public string FirmwareVersion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the IP address (for network devices).
    /// </summary>
    public IPAddress? IPAddress { get; set; }

    /// <summary>
    /// Gets or sets the MAC address (for network devices).
    /// </summary>
    public string? MacAddress { get; set; }

    /// <summary>
    /// Gets or sets the TCP port (for network devices).
    /// </summary>
    public int? Port { get; set; }

    /// <summary>
    /// Gets or sets the device type.
    /// </summary>
    public DeviceType Type { get; set; } = DeviceType.Unknown;

    /// <summary>
    /// Gets or sets a value indicating whether the device is powered on.
    /// </summary>
    public bool IsPowerOn { get; set; } = true;

    /// <summary>
    /// Gets or sets the connection type.
    /// </summary>
    public ConnectionType ConnectionType { get; set; } = ConnectionType.Unknown;

    /// <summary>
    /// Gets or sets the port name (for serial devices).
    /// </summary>
    public string? PortName { get; set; }

    /// <summary>
    /// Gets or sets the device path (for HID devices).
    /// </summary>
    public string? DevicePath { get; set; }

    /// <summary>
    /// Returns a string representation of the device info.
    /// </summary>
    /// <returns>A string describing the device.</returns>
    public override string ToString()
    {
        return ConnectionType switch
        {
            ConnectionType.WiFi => $"{Name} ({IPAddress}:{Port}) - {SerialNumber}",
            ConnectionType.Serial => $"{Name} ({PortName}) - {SerialNumber}",
            ConnectionType.Hid => $"{Name} (HID) - {SerialNumber}",
            _ => $"{Name} - {SerialNumber}"
        };
    }
}
