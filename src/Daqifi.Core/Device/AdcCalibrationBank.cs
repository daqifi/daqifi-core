namespace Daqifi.Core.Device;

/// <summary>
/// One of the two ADC calibration banks in the device's non-volatile memory. The values are the ones
/// the device sends and accepts.
/// </summary>
public enum AdcCalibrationBank
{
    /// <summary>
    /// The coefficients written at the factory.
    /// </summary>
    Factory = 0,

    /// <summary>
    /// The coefficients the user saved.
    /// </summary>
    User = 1
}
