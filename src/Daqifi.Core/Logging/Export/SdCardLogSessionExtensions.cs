using System;
using Daqifi.Core.Device.SdCard;

namespace Daqifi.Core.Logging.Export;

/// <summary>
/// Export helpers for a parsed SD-card log.
/// </summary>
public static class SdCardLogSessionExtensions
{
    /// <summary>
    /// Wraps a parsed log as an <see cref="ISampleSource"/> that <see cref="CsvExporter"/> can
    /// export, deriving the channel layout from the log's own device configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The log's own <see cref="SdCardLogSession.DeviceConfig"/> wins, field by field;
    /// <paramref name="fallbackConfiguration"/> fills in only where the log carries no
    /// configuration at all, or carries one whose serial number is absent — null <i>or</i> blank,
    /// since a blank serial is a gap rather than an answer (#627). That ordering matters
    /// because the fallback is normally the live device the log was downloaded from, which may not
    /// be the device that recorded it — a log that identifies itself is the better witness. A log
    /// that states zero analog ports is taken at its word rather than widened from the fallback,
    /// which is what <see cref="SdCardParseOptions.ConfigurationOverride"/> is for. With no
    /// configuration on either side the serial becomes <c>"unknown"</c> and the analog width
    /// becomes zero, which exports the digital column alone rather than inventing a width.
    /// </para>
    /// <para>
    /// A configuration reporting a negative analog port count — only reachable from a corrupt
    /// status message, since the field is unsigned on the wire — is read as no analog ports rather
    /// than throwing. It is parsed data being normalized, not a caller's argument being validated:
    /// the digital column of a damaged log is still worth exporting. The constructor this calls
    /// does reject a negative count, because there it <i>is</i> the caller's argument.
    /// </para>
    /// <para>
    /// <b>The export states the unit of its analog columns.</b> An SD export carried raw ADC
    /// counts where a live recording of the same channel carried volts, and neither file said
    /// which, so the two could not be read together (issue #903). The unit is
    /// <see cref="SampleValueUnits.Volts"/> when the parse had the device's resolution
    /// to convert the log's values with, and <see cref="SampleValueUnits.Unscaled"/>
    /// when it did not.
    /// </para>
    /// <para>
    /// It is the <i>parse</i> that decides that, not this call:
    /// <paramref name="fallbackConfiguration"/> fills in channel keys, it does not convert values
    /// that have already been read. Supply the device through
    /// <see cref="SdCardParseOptions.ForDevice"/> so the parse itself converts them, and the
    /// export comes out in volts like a live recording.
    /// </para>
    /// <para>
    /// Nothing is read here. The returned source enumerates the session lazily when the exporter
    /// asks it to, subject to the single-cursor rule described on
    /// <see cref="SdCardLogSampleSource"/>.
    /// </para>
    /// </remarks>
    /// <param name="session">The parsed log to export.</param>
    /// <param name="fallbackConfiguration">
    /// Device context to fill gaps the log itself does not carry, normally
    /// <see cref="SdCardDeviceConfiguration.FromDevice"/> for the connected device. Optional.
    /// </param>
    /// <param name="deviceName">
    /// The device name to build channel keys from. Null or blank becomes
    /// <see cref="SdCardLogSampleSource.DefaultDeviceName"/>, since a log carries no device name.
    /// </param>
    /// <returns>A source over <see cref="SdCardLogSession.Samples"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    public static SdCardLogSampleSource AsSampleSource(
        this SdCardLogSession session,
        SdCardDeviceConfiguration? fallbackConfiguration = null,
        string? deviceName = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        var config = session.DeviceConfig;

        // A blank serial in the log is a gap, not an answer — the same rule the SD-card
        // configuration merge applies (#627). Coalescing on null alone would let a log that
        // carries an empty serial field shadow the connected device's real one, and the caller
        // would get "unknown" in every channel key with a perfectly good serial in hand.
        var serial = Stated(config?.DeviceSerialNumber) ?? Stated(fallbackConfiguration?.DeviceSerialNumber);
        var analogCount = config?.AnalogPortCount ?? fallbackConfiguration?.AnalogPortCount ?? 0;

        // What the parse did to the values, which is what the file has to say. A resolution is
        // what SdCardAnalogScaling needs to convert a log's values; without one it returns them
        // untouched. The resolution is read off the session's own configuration — the one the
        // parse used — rather than off the fallback, which is what keeps the label and the values
        // from disagreeing: a configuration handed to this call arrives too late to convert
        // anything.
        var valueUnit = config?.Resolution is > 0
            ? SampleValueUnits.Volts
            : SampleValueUnits.Unscaled;

        return new SdCardLogSampleSource(
            session.Samples,
            serial,
            Math.Max(0, analogCount),
            deviceName)
        {
            ValueUnit = valueUnit
        };

        static string? Stated(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
