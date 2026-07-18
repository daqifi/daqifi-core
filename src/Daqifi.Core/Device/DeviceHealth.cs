namespace Daqifi.Core.Device;

/// <summary>
/// Health/telemetry values decoded from a device status message: battery charge,
/// board temperature, and the raw power/device status codes. These update as new
/// status messages arrive (including the periodic ones emitted during streaming),
/// so a snapshot reflects the most recent reading Core has seen.
/// </summary>
/// <remarks>
/// The underlying protobuf fields are proto3 scalars with no explicit presence, so a
/// value of <c>0</c> is indistinguishable from "not reported". <see cref="BatteryPercent"/>
/// and <see cref="BoardTemperatureCelsius"/> are therefore nullable and only assigned
/// when the message actually carries the field; the raw <see cref="PowerStatus"/> and
/// <see cref="DeviceStatus"/> codes default to <c>0</c>.
/// </remarks>
public class DeviceHealth
{
    /// <summary>
    /// Gets or sets the battery charge as a percentage (0-100), or <c>null</c> if the most
    /// recent status message did not report it.
    /// </summary>
    public int? BatteryPercent { get; set; }

    /// <summary>
    /// Gets or sets the board temperature in degrees Celsius, or <c>null</c> if the most
    /// recent status message did not report it.
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
