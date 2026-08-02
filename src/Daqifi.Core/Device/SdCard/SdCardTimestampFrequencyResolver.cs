namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Picks the timestamp clock frequency an SD card log parser converts tick counts with, and
/// records where it came from.
/// </summary>
/// <remarks>
/// The precedence is the same for every log format: the file's own frequency, then a live
/// device's, then the caller's fallback guess. The device only ever fills a gap the file left,
/// so a connected device can never override a self-describing log — but it does beat the
/// fallback, which is the whole point of supplying one.
/// </remarks>
internal static class SdCardTimestampFrequencyResolver
{
    /// <summary>
    /// Resolves the frequency to use, in Hz, together with its source. A zero argument means
    /// "not available" for each of the three inputs.
    /// </summary>
    /// <param name="fileFrequencyHz">Frequency embedded in the log file, or zero.</param>
    /// <param name="deviceFrequencyHz">
    /// Frequency from <see cref="SdCardParseOptions.ConfigurationOverride"/>, or zero.
    /// </param>
    /// <param name="fallbackFrequencyHz">
    /// <see cref="SdCardParseOptions.FallbackTimestampFrequency"/>, or zero to disable it.
    /// </param>
    /// <returns>The frequency to convert with, and the source it came from.</returns>
    public static (uint FrequencyHz, SdCardTimestampSource Source) Resolve(
        uint fileFrequencyHz,
        uint deviceFrequencyHz,
        uint fallbackFrequencyHz)
    {
        if (fileFrequencyHz > 0)
        {
            return (fileFrequencyHz, SdCardTimestampSource.LogFile);
        }

        if (deviceFrequencyHz > 0)
        {
            return (deviceFrequencyHz, SdCardTimestampSource.Device);
        }

        if (fallbackFrequencyHz > 0)
        {
            return (fallbackFrequencyHz, SdCardTimestampSource.Fallback);
        }

        return (0u, SdCardTimestampSource.None);
    }
}
