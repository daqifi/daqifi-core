namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// What an SD card log's whole-number analog values mean.
/// </summary>
/// <remarks>
/// <para>
/// Firmware writes an analog reading in one of three forms, and the choice is a global device
/// setting rather than something the file states (daqifi/daqifi-nyquist-firmware#1179):
/// </para>
/// <list type="bullet">
///   <item><description><c>CONF:ADC:USECal 2</c> — whole-number raw ADC codes.</description></item>
///   <item><description><c>CONF:VOLTage:PRECision 0</c> — whole-number millivolts, already calibrated by the device.</description></item>
///   <item><description><c>CONF:VOLTage:PRECision N</c> for N of 1 or more (the firmware default is 4) — volts with decimals, already calibrated by the device.</description></item>
/// </list>
/// <para>
/// A value written with a decimal point is volts, so the parser reads that form from the file
/// itself. The two whole-number forms look the same in the file, so the caller states which one
/// the recording used with <see cref="SdCardParseOptions.IntegerValueUnits"/>. Each sample then
/// reports the unit that was applied in
/// <see cref="SdCardLogEntry.SourceAnalogValueUnit"/>.
/// </para>
/// </remarks>
public enum SdCardIntegerValueUnits
{
    /// <summary>
    /// Whole numbers are raw ADC codes, as written in raw mode (<c>CONF:ADC:USECal 2</c>).
    /// The parser converts them to volts with the device's resolution, port range,
    /// calibration, and internal scale.
    /// </summary>
    RawAdcCodes = 0,

    /// <summary>
    /// Whole numbers are millivolts, as written at <c>CONF:VOLTage:PRECision 0</c>. The device
    /// has already applied its calibration, so the parser only divides by 1000 and does not
    /// convert them again.
    /// </summary>
    Millivolts = 1
}
