namespace Daqifi.Core.Device;

/// <summary>
/// Health/telemetry values decoded from a device status message: battery charge,
/// board temperature, and the raw power/device status codes. These update as new
/// status messages arrive (including the periodic ones emitted during streaming),
/// so a snapshot reflects the most recent reading Core has seen.
/// </summary>
/// <remarks>
/// The underlying protobuf fields are proto3 scalars with no explicit presence, so a
/// value of <c>0</c> is indistinguishable from "not reported". To avoid dropping a known
/// reading when a partial status frame omits a field, each value is <b>sticky</b>: it holds
/// the last value the device actually reported until a new in-contract reading replaces it.
/// <see cref="BatteryPercent"/> and <see cref="BoardTemperatureCelsius"/> are therefore
/// nullable — <c>null</c> means "never reported since this instance was created" — and the raw
/// <see cref="PowerStatus"/> and <see cref="DeviceStatus"/> codes default to <c>0</c>.
/// </remarks>
public class DeviceHealth
{
    /// <summary>
    /// Gets or sets the battery charge as a percentage (1-100). This is the last in-contract
    /// reading the device reported (a value may therefore be older than the most recent status
    /// message, which can omit the field), or <c>null</c> if the device has not reported a valid
    /// battery level since this instance was created. Out-of-range readings are ignored rather
    /// than surfaced.
    /// </summary>
    public int? BatteryPercent { get; set; }

    /// <summary>
    /// Gets or sets the board temperature in degrees Celsius. This is the last value the device
    /// reported (which may be older than the most recent status message, since a frame can omit
    /// the field), or <c>null</c> if the device has not reported a temperature since this instance
    /// was created.
    /// </summary>
    public int? BoardTemperatureCelsius { get; set; }

    /// <summary>
    /// Gets or sets the raw power/charging status code as reported by the device
    /// (<c>PwrStatus</c>). Semantics are firmware-defined; <c>0</c> is the default/unreported value.
    /// </summary>
    public uint PowerStatus { get; set; }

    /// <summary>
    /// Gets or sets the raw device status code as reported by the device
    /// (<c>DeviceStatus</c>). Semantics are firmware-defined; <c>0</c> is the default/unreported value.
    /// </summary>
    public uint DeviceStatus { get; set; }

    /// <summary>
    /// Creates a deep copy of this <see cref="DeviceHealth"/> instance.
    /// </summary>
    /// <returns>A new <see cref="DeviceHealth"/> instance with the same values.</returns>
    public DeviceHealth Clone()
    {
        return new DeviceHealth
        {
            BatteryPercent = BatteryPercent,
            BoardTemperatureCelsius = BoardTemperatureCelsius,
            PowerStatus = PowerStatus,
            DeviceStatus = DeviceStatus
        };
    }
}
