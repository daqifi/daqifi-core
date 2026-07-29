using Daqifi.Core.Device.Capabilities;
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
    /// The board <see cref="Capabilities"/> was last derived from, or <c>null</c> when it has
    /// never been derived. Tracked separately from <see cref="DeviceType"/> — which has a public
    /// setter — so <see cref="UpdateFromProtobuf"/> can tell "already built for this board" from
    /// "the board was assigned but the capabilities were never built for it".
    /// </summary>
    private DeviceType? _capabilitiesBoard;

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
    /// Gets the device's own capability document (<c>CONFigure:CAPabilities:JSON?</c>), or
    /// <c>null</c> when it has not been read — the device predates the query, could not answer, or
    /// <see cref="DaqifiDevice.ReadCapabilityDocumentAsync"/> has not run yet. Set through
    /// <see cref="ApplyCapabilityDocument"/>.
    /// </summary>
    /// <remarks>
    /// Carries the full parsed document, including the figures <see cref="DeviceCapabilities"/>
    /// has no field for — the conservative streaming envelope, the cap for the currently enabled
    /// channel set, and the device's rate-prediction model.
    /// </remarks>
    public CapabilityDocument? CapabilityDocument { get; private set; }

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
        _capabilitiesBoard = source._capabilitiesBoard;
        // The document is immutable once parsed, so the reference is safe to share — and copying
        // it matters: without it, the target's next status message would rebuild Capabilities from
        // the board table with nothing to re-overlay, silently discarding the device's own values.
        CapabilityDocument = source.CapabilityDocument;
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
    /// Applies a capability document read from the device, overlaying it onto the board-derived
    /// <see cref="Capabilities"/> and retaining it for later re-application.
    /// </summary>
    /// <remarks>
    /// The overlay is re-applied on every subsequent <see cref="UpdateFromProtobuf"/>, so a status
    /// message cannot revert the device's own values to the board table's.
    /// </remarks>
    /// <param name="document">The parsed capability document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <c>null</c>.</exception>
    public void ApplyCapabilityDocument(CapabilityDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        CapabilityDocument = document;
        document.MergeInto(Capabilities);
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

            // Rebuild the board-derived capabilities only when they are not already built for this
            // board. Status messages repeat the part number, and rebuilding on each one discarded
            // everything learned since — the channel counts that a status message with no port
            // fields does not restore, and any capability-document overlay.
            var detectedDeviceType = DeviceTypeDetector.DetectFromPartNumber(message.DevicePn);
            DeviceType = detectedDeviceType;
            if (_capabilitiesBoard != detectedDeviceType)
            {
                // Drop the retained document only when this object moves between two *known*
                // boards — a reconnect to a different unit through the same instance. The document
                // describes the board it was read from, so re-applying it there would overlay the
                // previous device's flags, channel counts and rate ceiling onto the new one. The
                // overlay is durable across status messages by design; it is not durable across a
                // board change.
                //
                // A transition involving Unknown is not a board change. Per ADR 0001, Unknown means
                // "not yet known", not "a different board": before the first status message there
                // is no previous board at all, and an unrecognized part number says nothing about
                // the device having been swapped. In both directions the document was still read
                // from this device, and discarding it would regress capabilities — notably
                // MaxSamplingRate back to the stale board-table ceiling — for no gain.
                var previousBoard = _capabilitiesBoard;
                if (previousBoard is not null
                    && previousBoard != DeviceType.Unknown
                    && detectedDeviceType != DeviceType.Unknown)
                {
                    CapabilityDocument = null;
                }

                Capabilities = DeviceCapabilities.FromDeviceType(detectedDeviceType);
                _capabilitiesBoard = detectedDeviceType;
            }
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

        // Re-apply the device's own capability document last, so it stays the authority over both
        // the board table and the status message's port counts for the fields it states. This is
        // what makes the merge durable: without it a status frame would win by arriving later.
        CapabilityDocument?.MergeInto(Capabilities);

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
