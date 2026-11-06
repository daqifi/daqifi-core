namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// The unit a log file held a sample's analog values in, and therefore which conversion the
/// parser applied to reach volts.
/// </summary>
/// <remarks>
/// Read this on every sample that matters. Applying the wrong conversion changes a reading by
/// a factor of 1000 or more, and the number alone does not reveal it — 12 is a plausible ADC
/// code, a plausible millivolt reading, and an implausible but silent voltage.
/// </remarks>
public enum SdCardAnalogValueUnit
{
    /// <summary>
    /// The file held raw ADC codes. The reported values are those codes converted with the
    /// device's resolution, port range, calibration, and internal scale — or the codes
    /// themselves when no device configuration was available to convert them with.
    /// </summary>
    RawAdcCodes = 0,

    /// <summary>
    /// The file held volts, written with a decimal point. The device had already applied its
    /// calibration, so the reported values are the file's values unchanged.
    /// </summary>
    Volts = 1,

    /// <summary>
    /// The file held whole-number millivolts. The device had already applied its calibration,
    /// so the reported values are the file's values divided by 1000.
    /// </summary>
    Millivolts = 2
}
