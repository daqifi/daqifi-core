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
}
