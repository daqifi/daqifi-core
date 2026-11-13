namespace Daqifi.Core.Device.Capabilities;

/// <summary>
/// The <c>identity</c> block of the capability document.
/// </summary>
/// <remarks>
/// Reported for diagnostics and cross-checking. daqifi-core does not derive
/// <see cref="DeviceMetadata.DeviceType"/> or <see cref="DeviceMetadata.FirmwareVersion"/> from it
/// — those come from the protobuf status message, which every supported device sends whether or
/// not it can answer the capability query, so keeping one source for them avoids two paths that
/// can disagree.
/// </remarks>
public sealed class CapabilityIdentity
{
    /// <summary>Gets the vendor name, e.g. <c>"DAQiFi"</c>.</summary>
    public string? Vendor { get; init; }

    /// <summary>Gets the product family, e.g. <c>"Nyquist"</c>.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the board variant, e.g. <c>"NQ1"</c>.</summary>
    public string? Variant { get; init; }

    /// <summary>Gets the board serial number as a hexadecimal string.</summary>
    public string? Serial { get; init; }

    /// <summary>Gets the firmware revision string, e.g. <c>"3.7.2"</c>.</summary>
    public string? FirmwareRevision { get; init; }

    /// <summary>Gets the hardware revision string, e.g. <c>"2.0.0"</c>.</summary>
    public string? HardwareRevision { get; init; }

    /// <summary>
    /// Gets the CRC32 of the flashed firmware image as eight hexadecimal digits, e.g.
    /// <c>"0E9DEE9C"</c>, or <c>null</c> when not stated (firmware before v3.8.0).
    /// </summary>
    /// <remarks>
    /// <see cref="FirmwareRevision"/> names a release, so every build between two releases reports
    /// the same one; this tells two builds apart. It covers program flash only, not saved
    /// settings, so it does not change when a setting is saved. The firmware repository's
    /// <c>tools/hexcrc.py</c> computes the same value from a <c>.hex</c> file.
    /// </remarks>
    public string? FirmwareCrc32 { get; init; }

    /// <summary>
    /// Gets whether the device holds a factory calibration and which coefficients are in effect, or
    /// <c>null</c> when not stated (firmware before v3.9.0).
    /// </summary>
    public CapabilityCalibrationStatus? Calibration { get; init; }
}
