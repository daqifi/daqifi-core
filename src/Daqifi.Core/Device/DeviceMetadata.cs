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

    /// <summary>
    /// Gets or sets the device capabilities.
    /// </summary>
    public DeviceCapabilities Capabilities { get; set; } = new DeviceCapabilities();

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
    /// Gets or sets the WiFi signal strength (RSSI) in dBm.
    /// </summary>
    public int? SignalStrength { get; set; }

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

        if (message.IpAddr != null && message.IpAddr.Length > 0)
        {
            var ipBytes = message.IpAddr.ToByteArray();
            IpAddress = string.Join(".", ipBytes);
        }

        if (message.MacAddr != null && message.MacAddr.Length > 0)
        {
            MacAddress = BitConverter.ToString(message.MacAddr.ToByteArray());
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

        if (message.SsidStrength > 0)
        {
            SignalStrength = (int)message.SsidStrength;
        }
    }
}
