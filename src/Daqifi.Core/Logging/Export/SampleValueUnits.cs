namespace Daqifi.Core.Logging.Export;

/// <summary>
/// The unit names an <see cref="ISampleSource"/> reports through
/// <see cref="ISampleSource.ValueUnit"/>, and which <see cref="CsvExporter"/> writes into the
/// analog column headers.
/// </summary>
/// <remarks>
/// They live in one place because the live path and the SD-card path have to name the same unit
/// the same way. The two used to export the same channel in different units with nothing in
/// either file to say which (issue #903), and two spellings of "volts" would leave the files just
/// as hard to read together.
/// </remarks>
public static class SampleValueUnits
{
    /// <summary>
    /// Volts: the device's calibration has been applied, by the device itself for a live sample
    /// or by the parse for an SD-card log.
    /// </summary>
    public const string Volts = "V";

    /// <summary>
    /// The values are as the log held them, because the parse had no device resolution to convert
    /// them with.
    /// </summary>
    /// <remarks>
    /// It names what Core did rather than what the device meant, and that is deliberate: Core
    /// knows for certain that it applied no conversion, while what an unconverted whole number
    /// means — an ADC code or a millivolt reading — is a per-entry fact that a log does not state
    /// in its header (daqifi/daqifi-nyquist-firmware#1179). Read
    /// <see cref="Daqifi.Core.Device.SdCard.SdCardLogEntry.SourceAnalogValueUnit"/> for that. What
    /// matters in the exported file is that such a column can no longer be read as volts.
    /// </remarks>
    public const string Unscaled = "unscaled";
}
