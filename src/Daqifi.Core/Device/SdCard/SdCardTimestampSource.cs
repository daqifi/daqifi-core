namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Where the timestamp clock frequency used to convert an SD card log's raw tick counts
/// into wall-clock times came from.
/// </summary>
/// <remarks>
/// <para>
/// Every timestamp in a parsed session is a tick count divided by this frequency, so a
/// frequency that does not match the recording device rescales the entire session by a
/// constant factor. That failure is invisible in the data itself — the samples still look
/// evenly spaced, just at the wrong rate — which is why the source is reported alongside
/// <see cref="SdCardLogSession.TimestampFrequency"/> rather than left implicit.
/// </para>
/// <para>
/// <see cref="Fallback"/> is the value to watch for: it means nothing in the file and no
/// connected device supplied a frequency, so
/// <see cref="SdCardParseOptions.FallbackTimestampFrequency"/> was used as a guess.
/// </para>
/// </remarks>
public enum SdCardTimestampSource
{
    /// <summary>
    /// No frequency was available and the fallback was disabled
    /// (<see cref="SdCardParseOptions.FallbackTimestampFrequency"/> set to zero), so tick
    /// counts were not converted to elapsed time at all.
    /// </summary>
    None = 0,

    /// <summary>
    /// The frequency was read from the log file itself. This is the most trustworthy source:
    /// it is what the device recorded at the time the log was written.
    /// </summary>
    LogFile = 1,

    /// <summary>
    /// The file carried no frequency, so the one reported by a live device via
    /// <see cref="SdCardParseOptions.ConfigurationOverride"/> was used. Firmware v3.7.2 and
    /// earlier embed no frequency in SD card logs but do report one in their live status
    /// message, which makes this the normal source when a log is parsed straight after
    /// download from the device that wrote it.
    /// </summary>
    Device = 2,

    /// <summary>
    /// Neither the file nor a connected device supplied a frequency, so
    /// <see cref="SdCardParseOptions.FallbackTimestampFrequency"/> was used. The reconstructed
    /// timestamps are only as accurate as that guess — if it does not match the recording
    /// device's clock, every timestamp in the session is scaled by a constant factor.
    /// </summary>
    Fallback = 3
}
