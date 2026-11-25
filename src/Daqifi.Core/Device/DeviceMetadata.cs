using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Core.Device.Network;

namespace Daqifi.Core.Device;

/// <summary>
/// An immutable snapshot of what a DAQiFi device has reported about itself.
/// </summary>
/// <remarks>
/// A device replaces its whole snapshot when it learns something new, so one snapshot never mixes
/// two status messages, whichever thread reads it. Keep a reference to one only while you want its
/// values to stand still; read <see cref="IStreamingDevice.Metadata"/> again for the latest. Code
/// that keeps its own copy derives changes with a <c>with</c> expression.
/// </remarks>
public sealed record DeviceMetadata
{
    /// <summary>
    /// Gets the device part number.
    /// </summary>
    public string PartNumber { get; init; } = string.Empty;

    /// <summary>
    /// Gets the device serial number.
    /// </summary>
    public string SerialNumber { get; init; } = string.Empty;

    /// <summary>
    /// Gets the firmware version.
    /// </summary>
    public string FirmwareVersion { get; init; } = string.Empty;

    /// <summary>
    /// Gets the hardware revision.
    /// </summary>
    public string HardwareRevision { get; init; } = string.Empty;

    /// <summary>
    /// Gets the device type.
    /// </summary>
    public DeviceType DeviceType { get; init; } = DeviceType.Unknown;

    /// <summary>
    /// Gets the device capabilities.
    /// </summary>
    public DeviceCapabilities Capabilities { get; init; } = new();

    /// <summary>
    /// Gets the device's own capability document (<c>CONFigure:CAPabilities:JSON?</c>), or
    /// <c>null</c> when it has not been read — the device predates the query, could not answer, or
    /// <see cref="DaqifiDevice.ReadCapabilityDocumentAsync"/> has not run yet.
    /// </summary>
    /// <remarks>
    /// Carries the full parsed document, including the figures <see cref="DeviceCapabilities"/>
    /// has no field for — the conservative streaming envelope, the cap for the currently enabled
    /// channel set, and the device's rate-prediction model.
    /// </remarks>
    public CapabilityDocument? CapabilityDocument { get; init; }

    /// <summary>
    /// Gets the most recent device health telemetry (battery, board temperature,
    /// power/device status) decoded from a status message. Status is request-only (issue #535):
    /// streaming frames do not carry health, so this holds the last requested reading (the one
    /// captured during <see cref="DaqifiDevice.InitializeAsync"/>, unless something has asked
    /// since) — call <see cref="DaqifiDevice.RefreshDeviceStatusAsync"/> to request a new one
    /// (its remarks cover the case where a late reply to an earlier request answers it).
    /// </summary>
    public DeviceHealth Health { get; init; } = new();

    /// <summary>
    /// Gets the IP address of the device.
    /// </summary>
    public string IpAddress { get; init; } = string.Empty;

    /// <summary>
    /// Gets the MAC address of the device.
    /// </summary>
    public string MacAddress { get; init; } = string.Empty;

    /// <summary>
    /// Gets the WiFi SSID.
    /// </summary>
    public string Ssid { get; init; } = string.Empty;

    /// <summary>
    /// Gets the device hostname.
    /// </summary>
    public string HostName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the user-defined friendly name of the device.
    /// </summary>
    public string FriendlyName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the TCP port for device communication.
    /// </summary>
    public int DevicePort { get; init; }

    /// <summary>
    /// Gets the WiFi security type the device reported. It is <c>null</c> until a status
    /// message reports the WiFi settings, and when the device reports a type Core does not know.
    /// </summary>
    public WifiSecurityType? WifiSecurityMode { get; init; }

    /// <summary>
    /// Gets the WiFi mode the device reported. It is <c>null</c> until a status message
    /// reports the WiFi settings, and when the device reports a mode Core does not know.
    /// </summary>
    public WifiMode? WifiInfrastructureMode { get; init; }

    /// <summary>
    /// Returns this metadata with a capability document read from the device overlaid onto
    /// <see cref="Capabilities"/> and retained for later re-application.
    /// </summary>
    /// <remarks>
    /// The overlay is re-applied by every subsequent <see cref="WithStatus"/>, so a status message
    /// cannot revert the device's own values to the board table's.
    /// </remarks>
    /// <param name="document">The parsed capability document.</param>
    internal DeviceMetadata WithCapabilityDocument(CapabilityDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return this with
        {
            CapabilityDocument = document,
            Capabilities = document.OverlayOnto(Capabilities)
        };
    }

    /// <summary>
    /// Returns this metadata updated from a status message. A field the message does not report
    /// keeps its last known value.
    /// </summary>
    /// <param name="message">The protobuf message containing device information.</param>
    internal DeviceMetadata WithStatus(DaqifiOutMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var reportsBoard = !string.IsNullOrWhiteSpace(message.DevicePn);
        var board = reportsBoard ? DeviceTypeDetector.DetectFromPartNumber(message.DevicePn) : DeviceType;

        // Rebuild the board-derived capabilities only when the board changed. Status messages
        // repeat the part number, and rebuilding on each one discarded everything learned since —
        // the channel counts that a status message with no port fields does not restore, and any
        // capability-document overlay.
        var capabilities = board == DeviceType ? Capabilities : DeviceCapabilities.FromDeviceType(board);

        // Drop the retained document only on a move between two *known* boards — a reconnect to a
        // different unit through the same device object. The document describes the board it was
        // read from, so re-applying it there would overlay the previous device's flags, channel
        // counts and rate ceiling onto the new one. The overlay is durable across status messages
        // by design; it is not durable across a board change.
        //
        // A transition involving Unknown is not a board change. Per ADR 0001, Unknown means "not
        // yet known", not "a different board": before the first status message there is no
        // previous board at all, and an unrecognized part number says nothing about the device
        // having been swapped. In both directions the document was still read from this device,
        // and discarding it would regress capabilities — notably MaximumSampleRateHz back to the stale
        // board-table ceiling — for no gain.
        var boardWasSwapped =
            board != DeviceType && board != DeviceType.Unknown && DeviceType != DeviceType.Unknown;
        var document = boardWasSwapped ? null : CapabilityDocument;

        capabilities = capabilities with
        {
            AnalogInputChannelCount = message.AnalogInPortNum > 0
                ? (int)message.AnalogInPortNum
                : capabilities.AnalogInputChannelCount,
            AnalogOutputChannelCount = message.AnalogOutPortNum > 0
                ? (int)message.AnalogOutPortNum
                : capabilities.AnalogOutputChannelCount,
            DigitalChannelCount = message.DigitalPortNum > 0
                ? (int)message.DigitalPortNum
                : capabilities.DigitalChannelCount
        };

        // Re-apply the device's own capability document last, so it stays the authority over both
        // the board table and the status message's port counts for the fields it states. This is
        // what makes the merge durable: without it a status frame would win by arriving later.
        if (document is not null)
        {
            capabilities = document.OverlayOnto(capabilities);
        }

        var ip = NetworkAddressHelper.GetIpAddressString(message);
        var mac = NetworkAddressHelper.GetMacAddressString(message);

        return this with
        {
            PartNumber = reportsBoard ? message.DevicePn : PartNumber,
            DeviceType = board,
            Capabilities = capabilities,
            CapabilityDocument = document,
            SerialNumber = message.DeviceSn != 0 ? message.DeviceSn.ToString() : SerialNumber,
            FirmwareVersion = Reported(message.DeviceFwRev, FirmwareVersion),
            HardwareRevision = Reported(message.DeviceHwRev, HardwareRevision),
            Ssid = Reported(message.Ssid, Ssid),
            HostName = Reported(message.HostName, HostName),
            FriendlyName = message.FriendlyDeviceName.Length > 0 ? message.FriendlyDeviceName : FriendlyName,
            DevicePort = message.DevicePort != 0 ? (int)message.DevicePort : DevicePort,

            // Security mode 0 is a real value (an open network), but proto3 never encodes a 0, so
            // an open network arrives as "field 60 absent" -- the same as a message that has no
            // WiFi settings at all. The infrastructure mode tells the two apart: the firmware
            // encodes fields 60 and 61 together from the same WiFi settings (SYSTem:SYSInfoPB?),
            // and field 61 is never 0 (1 = STA, 4 = AP), so its presence proves the message
            // reported the WiFi settings. The discovery reply and the SD log metadata carry the
            // part number and firmware version but neither WiFi field, so they keep the last known
            // mode (#986).
            WifiSecurityMode = message.WifiSecurityMode > 0 || message.WifiInfMode > 0
                ? ToWifiSecurityType(message.WifiSecurityMode)
                : WifiSecurityMode,
            WifiInfrastructureMode = message.WifiInfMode > 0
                ? ToWifiMode(message.WifiInfMode)
                : WifiInfrastructureMode,
            IpAddress = ip.Length > 0 ? ip : IpAddress,
            MacAddress = mac.Length > 0 ? mac : MacAddress,
            Health = HealthAfter(message)
        };
    }

    /// <summary>
    /// The health telemetry after a status message.
    /// </summary>
    /// <remarks>
    /// proto3 scalars have no explicit presence, so a value of 0 is indistinguishable from "not
    /// reported"; only a non-zero value replaces a reading, so a partial status message never
    /// clobbers a known one.
    /// </remarks>
    private DeviceHealth HealthAfter(DaqifiOutMessage message) => Health with
    {
        // BattStatus is a uint documented as a battery percentage. Only accept an in-contract
        // 1..100 reading: this both filters nonsensical values (>100) and avoids the uint->int
        // wrap-to-negative a very large value would produce. Out-of-range readings are ignored
        // (treated as not reported), leaving the last-known value in place.
        BatteryPercent = message.BattStatus is >= 1 and <= 100
            ? (int)message.BattStatus
            : Health.BatteryPercent,
        BoardTemperatureCelsius = message.TempStatus != 0
            ? message.TempStatus
            : Health.BoardTemperatureCelsius,
        PowerStatus = message.PwrStatus != 0 ? message.PwrStatus : Health.PowerStatus,
        DeviceStatus = message.DeviceStatus != 0 ? message.DeviceStatus : Health.DeviceStatus
    };

    /// <summary>The reported text, or <paramref name="current"/> when the message has none.</summary>
    private static string Reported(string reported, string current) =>
        string.IsNullOrWhiteSpace(reported) ? current : reported;

    /// <summary>
    /// Maps a wire security mode to its type. A mode Core does not know becomes <c>null</c> rather
    /// than keeping the last known type, which the device has just said is no longer in use.
    /// </summary>
    private static WifiSecurityType? ToWifiSecurityType(uint wireValue) => wireValue switch
    {
        (uint)WifiSecurityType.None => WifiSecurityType.None,
        (uint)WifiSecurityType.WpaPskPhrase => WifiSecurityType.WpaPskPhrase,

        // WPA_DEPRECATED: the firmware's older number for the same passphrase mode. Its SCPI
        // queries report it as 3, but the status message carries the stored number.
        4 => WifiSecurityType.WpaPskPhrase,
        _ => null,
    };

    /// <summary>
    /// Maps a wire infrastructure mode to its WiFi mode; a mode Core does not know becomes
    /// <c>null</c>, for the same reason as in <see cref="ToWifiSecurityType"/>.
    /// </summary>
    private static WifiMode? ToWifiMode(uint wireValue) => wireValue switch
    {
        (uint)WifiMode.ExistingNetwork => WifiMode.ExistingNetwork,
        (uint)WifiMode.SelfHosted => WifiMode.SelfHosted,
        _ => null,
    };
}
