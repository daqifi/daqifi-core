namespace Daqifi.Core.Device;

/// <summary>
/// Health/telemetry values decoded from a device status message: battery charge,
/// board temperature, and the raw power/device status codes. These update as new
/// status messages arrive, so a snapshot reflects the most recent reading Core has seen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Status messages are not periodic.</b> The device sends one when asked and at no other
/// time — a connected link carries stream frames only, and the firmware builds each of those
/// from four tags, none of them health. So these values are captured during
/// <c>InitializeAsync</c> and then stay put until something asks again. Measured on an Nq1
/// running 3.7.2: 1,587 inbound messages over 65 s, exactly one of which carried health, and
/// only because it was requested (issue #535).
/// </para>
/// <para>
/// Call <see cref="DaqifiDevice.RefreshDeviceStatusAsync"/> to get a current reading. An
/// earlier revision of this summary said these values included "the periodic ones emitted
/// during streaming"; there are none, and a consumer who believed it would display a battery
/// percentage frozen at connect.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// <b>No shipping firmware populates this.</b> The encoder's <c>temp_status</c> case is empty,
    /// so the field is never assigned, stays 0, and proto3 omits it — checked at v3.5.0, v3.6.0,
    /// v3.7.0, v3.7.1 and v3.7.2, i.e. the whole range Core supports. Against real hardware this
    /// is <c>null</c> and cannot be anything else; it is kept because the wire field exists and a
    /// future firmware may fill it (issue #535).
    /// </remarks>
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
