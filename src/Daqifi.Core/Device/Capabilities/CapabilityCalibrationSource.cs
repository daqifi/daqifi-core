namespace Daqifi.Core.Device.Capabilities;

/// <summary>
/// Which calibration coefficients a device has in effect, as reported in
/// <see cref="CapabilityCalibrationStatus.Source"/>.
/// </summary>
public enum CapabilityCalibrationSource
{
    /// <summary>
    /// <c>"unknown"</c>: the device could not get a consistent view of its coefficients, which
    /// happens only while a calibration is being loaded. This is not the same as
    /// <see cref="None"/>; re-read the document rather than assuming either. A value this version of
    /// daqifi-core does not recognize also maps here, since it, too, establishes nothing.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// <c>"none"</c>: every channel's coefficients are identity (slope 1, intercept 0), so readings
    /// are unscaled. Reported whether the device was never calibrated or its calibration happens to
    /// be identity.
    /// </summary>
    None,

    /// <summary><c>"factory"</c>: non-identity coefficients from the factory calibration.</summary>
    Factory,

    /// <summary><c>"user"</c>: non-identity coefficients from the user calibration.</summary>
    User
}
