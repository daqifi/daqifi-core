namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Resolves a stable USB physical-location key for a serial port name (e.g.
/// <c>COM9</c>) or a HID device interface path. Unlike <see cref="IUsbPortDescriptorProvider"/>
/// (which identifies WHAT is plugged in) this identifies WHERE it is plugged in — a
/// topology-derived key that stays the same for the same physical USB port across a
/// device's transitions between transports (e.g. serial app mode ⇄ HID bootloader mode)
/// and re-enumerations, letting callers correlate the same physical unit and disambiguate
/// multiple identical devices (same VID/PID, no serial number) plugged into different ports.
/// </summary>
/// <remarks>
/// Implementations are platform-specific: Windows resolves the key via
/// <c>DEVPKEY_Device_LocationInfo</c>. Platforms without an implementation fall back to
/// <see cref="NullUsbLocationProvider"/>, which returns null for every input.
/// </remarks>
public interface IUsbLocationProvider
{
    /// <summary>
    /// Returns the USB physical-location key for <paramref name="portNameOrDevicePath"/>,
    /// or <c>null</c> if it can't be resolved.
    /// </summary>
    /// <param name="portNameOrDevicePath">
    /// A serial port name (e.g. <c>COM9</c>) or a HID device interface path
    /// (e.g. <c>HidDeviceInfo.DevicePath</c>).
    /// </param>
    string? GetLocationKey(string portNameOrDevicePath);
}
