using Daqifi.Core.Device.Network;

namespace Daqifi.Core.Device;

/// <summary>
/// Contains metadata and configuration information about a DAQiFi device.
/// </summary>
public class DeviceMetadata
{
    /// <summary>
    /// Gets or sets the device part number.
    /// </summary>
    public string PartNumber { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the device serial number.
    /// </summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the firmware version.
    /// </summary>
    public string FirmwareVersion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the hardware revision.
    /// </summary>
    public string HardwareRevision { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the device type.
    /// </summary>
    public DeviceType DeviceType { get; set; } = DeviceType.Unknown;

    private DeviceCapabilities _capabilities = new();
    private DeviceHealth _health = new();

    /// <summary>
    /// Gets or sets the device capabilities. Assigning <c>null</c> is coerced to a fresh instance so
    /// the status-processing path (which populates channel counts here) can never dereference null.
    /// </summary>
    public DeviceCapabilities Capabilities
    {
        get => _capabilities;
        set => _capabilities = value ?? new DeviceCapabilities();
    }

    /// <summary>
    /// Gets or sets the most recent device health telemetry (battery, board temperature,
    /// power/device status) decoded from a status message. Updated on each status message,
    /// including the periodic ones emitted during streaming. Assigning <c>null</c> is coerced to a
    /// fresh instance so <see cref="UpdateFromProtobuf"/> can never dereference null on the status path.
    /// </summary>
    public DeviceHealth Health
    {
        get => _health;
        set => _health = value ?? new DeviceHealth();
    }

    /// <summary>
    /// Gets or sets the IP address of the device.
    /// </summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the MAC address of the device.
    /// </summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the WiFi SSID.
    /// </summary>
    public string Ssid { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the device hostname.
    /// </summary>
    public string HostName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user-defined friendly name of the device.
    /// </summary>
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the TCP port for device communication.
    /// </summary>
    public int DevicePort { get; set; }

    /// <summary>
    /// Gets or sets the WiFi security mode.
    /// </summary>
    public uint WifiSecurityMode { get; set; }

    /// <summary>
    /// Gets or sets the WiFi infrastructure mode.
    /// </summary>
    public uint WifiInfrastructureMode { get; set; }

    /// <summary>
    /// Copies all field values from another <see cref="DeviceMetadata"/> instance into this one.
    /// </summary>
    /// <param name="source">The instance to copy field values from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <c>null</c>.</exception>
    public void CopyFrom(DeviceMetadata source)
    {
        ArgumentNullException.ThrowIfNull(source);

        PartNumber = source.PartNumber;
        SerialNumber = source.SerialNumber;
        FirmwareVersion = source.FirmwareVersion;
        HardwareRevision = source.HardwareRevision;
        DeviceType = source.DeviceType;
        Capabilities = source.Capabilities?.Clone() ?? new DeviceCapabilities();
        Health = source.Health?.Clone() ?? new DeviceHealth();
        IpAddress = source.IpAddress;
        MacAddress = source.MacAddress;
        Ssid = source.Ssid;
        HostName = source.HostName;
        FriendlyName = source.FriendlyName;
        DevicePort = source.DevicePort;
        WifiSecurityMode = source.WifiSecurityMode;
        WifiInfrastructureMode = source.WifiInfrastructureMode;
    }

    /// <summary>
    /// Updates the device metadata from a protobuf message.
    /// </summary>
    /// <param name="message">The protobuf message containing device information.</param>
    public void UpdateFromProtobuf(DaqifiOutMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.DevicePn))
        {
            PartNumber = message.DevicePn;
            DeviceType = DeviceTypeDetector.DetectFromPartNumber(message.DevicePn);
            Capabilities = DeviceCapabilities.FromDeviceType(DeviceType);
        }

        if (message.DeviceSn != 0)
        {
            SerialNumber = message.DeviceSn.ToString();
        }

        if (!string.IsNullOrWhiteSpace(message.DeviceFwRev))
        {
            FirmwareVersion = message.DeviceFwRev;
        }

        if (!string.IsNullOrWhiteSpace(message.DeviceHwRev))
        {
            HardwareRevision = message.DeviceHwRev;
        }

        if (!string.IsNullOrWhiteSpace(message.Ssid))
        {
            Ssid = message.Ssid;
        }

        if (!string.IsNullOrWhiteSpace(message.HostName))
        {
            HostName = message.HostName;
        }

        if (!string.IsNullOrEmpty(message.FriendlyDeviceName))
        {
            FriendlyName = message.FriendlyDeviceName;
        }

        if (message.DevicePort != 0)
        {
            DevicePort = (int)message.DevicePort;
        }

        if (message.WifiSecurityMode > 0)
        {
            WifiSecurityMode = message.WifiSecurityMode;
        }

        if (message.WifiInfMode > 0)
        {
            WifiInfrastructureMode = message.WifiInfMode;
        }

        var ip = NetworkAddressHelper.GetIpAddressString(message);
        if (ip.Length > 0)
        {
            IpAddress = ip;
        }

        var mac = NetworkAddressHelper.GetMacAddressString(message);
        if (mac.Length > 0)
        {
            MacAddress = mac;
        }

        // Update channel counts from message
        if (message.AnalogInPortNum > 0)
        {
            Capabilities.AnalogInputChannels = (int)message.AnalogInPortNum;
        }

        if (message.AnalogOutPortNum > 0)
        {
            Capabilities.AnalogOutputChannels = (int)message.AnalogOutPortNum;
        }

        if (message.DigitalPortNum > 0)
        {
            Capabilities.DigitalChannels = (int)message.DigitalPortNum;
        }

        // Update health telemetry. proto3 scalars have no explicit presence, so a value of 0
        // is indistinguishable from "not reported"; guard on non-zero (consistent with the
        // other fields above) so a partial status message never clobbers a known reading.

        // BattStatus is a uint documented as a battery percentage. Only accept an in-contract
        // 1..100 reading: this both filters nonsensical values (>100) and avoids the uint->int
        // wrap-to-negative a very large value would produce. Out-of-range readings are ignored
        // (treated as not reported), leaving the last-known value in place.
        if (message.BattStatus is >= 1 and <= 100)
        {
            Health.BatteryPercent = (int)message.BattStatus;
        }

        if (message.TempStatus != 0)
        {
            Health.BoardTemperatureCelsius = message.TempStatus;
        }

        if (message.PwrStatus != 0)
        {
            Health.PowerStatus = message.PwrStatus;
        }

        if (message.DeviceStatus != 0)
        {
            Health.DeviceStatus = message.DeviceStatus;
        }
    }
}
