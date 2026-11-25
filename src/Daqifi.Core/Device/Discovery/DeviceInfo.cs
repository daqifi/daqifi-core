using System.Net;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Represents discovered device metadata.
/// </summary>
/// <remarks>
/// Immutable, because one instance is shared: a <see cref="ContinuousDeviceFinder"/> hands the same
/// instances it keeps in its live set to every subscriber. Use a <c>with</c> expression to get a
/// changed copy. Two instances are equal when every property is equal, including
/// <see cref="LocalInterfaceAddress"/>, so equality answers "is this the same report", not "is this
/// the same device"; compare a serial number or a <see cref="DeviceIdentity"/> for that.
/// </remarks>
public sealed record DeviceInfo : IDeviceInfo
{
    /// <summary>
    /// Gets the device name/hostname.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the device serial number.
    /// </summary>
    public string SerialNumber { get; init; } = string.Empty;

    /// <summary>
    /// Gets the firmware version as the device reported it. See <see cref="IDeviceInfo.FirmwareVersion"/>.
    /// </summary>
    public string FirmwareVersion { get; init; } = string.Empty;

    /// <summary>
    /// Gets the IP address (for network devices).
    /// </summary>
    public IPAddress? IPAddress { get; init; }

    /// <summary>
    /// Gets the MAC address (for network devices).
    /// </summary>
    public string? MacAddress { get; init; }

    /// <summary>
    /// Gets the TCP port (for network devices).
    /// </summary>
    public int? Port { get; init; }

    /// <summary>
    /// Gets the local interface address that discovered this device (for network devices).
    /// Used for binding TCP connections in multi-NIC scenarios.
    /// </summary>
    public IPAddress? LocalInterfaceAddress { get; init; }

    /// <summary>
    /// Gets the device type.
    /// </summary>
    public DeviceType Type { get; init; } = DeviceType.Unknown;

    /// <summary>
    /// Gets a value indicating whether the device is powered on.
    /// </summary>
    public bool IsPowerOn { get; init; } = true;

    /// <summary>
    /// Gets the connection type.
    /// </summary>
    public ConnectionType ConnectionType { get; init; } = ConnectionType.Unknown;

    /// <summary>
    /// Gets the port name (for serial devices).
    /// </summary>
    public string? PortName { get; init; }

    /// <summary>
    /// Gets the device path (for HID devices).
    /// </summary>
    public string? DevicePath { get; init; }

    /// <summary>
    /// Gets the USB physical-location key (e.g. <c>Port_#0001.Hub_#0001</c>), or null
    /// if it could not be resolved. See <see cref="IDeviceInfo.LocationKey"/>.
    /// </summary>
    public string? LocationKey { get; init; }

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
