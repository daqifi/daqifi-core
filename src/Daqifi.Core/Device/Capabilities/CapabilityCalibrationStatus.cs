namespace Daqifi.Core.Device.Capabilities;

/// <summary>
/// The <c>identity.cal</c> block of the capability document: whether the device holds a factory
/// calibration, and which coefficients are in effect. Published by firmware v3.9.0 and newer.
/// </summary>
/// <remarks>
/// Firmware before v3.9.0 wrote identity coefficients into the factory slot at boot, so a device
/// that was never calibrated could not be told apart from one that was. A <c>null</c> value means
/// the document did not state it.
/// </remarks>
public sealed class CapabilityCalibrationStatus
{
    /// <summary>
    /// Gets whether the device's factory calibration slot is populated, or <c>null</c> when not
    /// stated. This says only that a factory calibration exists, not that it is in use; see
    /// <see cref="Source"/> for that.
    /// </summary>
    public bool? FactoryPresent { get; init; }

    /// <summary>
    /// Gets which calibration coefficients are in effect, or <c>null</c> when not stated.
    /// </summary>
    public CapabilityCalibrationSource? Source { get; init; }
}
