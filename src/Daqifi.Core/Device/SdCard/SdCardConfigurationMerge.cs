namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Merges an override <see cref="SdCardDeviceConfiguration"/> into a file-derived one.
/// </summary>
/// <remarks>
/// The CSV and JSON SD card log parsers each carried an identical copy of this merge logic
/// (the binary/protobuf parser's variant compares differently and is intentionally not folded
/// in here); it now lives once so the two formats can't drift out of sync with each other.
/// </remarks>
internal static class SdCardConfigurationMerge
{
    /// <summary>
    /// Merges <paramref name="overrideConfig"/> into <paramref name="parsed"/>. File-derived
    /// values are primary; the override fills in gaps.
    /// </summary>
    /// <remarks>
    /// A gap is a field the file states no value for: a non-positive number, a null reference, or
    /// a blank string. Blank counts because a text header spells a field's label out even when it
    /// has no value to put after it — <c>"# Serial Number:"</c> alone on a line parses to
    /// <see cref="string.Empty"/>, not null — and an empty label carries no more information than
    /// an absent one, so it must not shadow the real serial a connected device reported. The
    /// binary parser's own merge already treats an empty part number or firmware revision this way.
    /// <para>
    /// The list fields keep the plain null test. Both text parsers hand those in as literal null,
    /// so an empty-but-non-null list cannot reach here, and whether an explicitly empty channel
    /// list means "absent" is a separate question, worth answering when a parser can produce one.
    /// </para>
    /// </remarks>
    public static SdCardDeviceConfiguration Merge(
        SdCardDeviceConfiguration parsed,
        SdCardDeviceConfiguration? overrideConfig)
    {
        if (overrideConfig == null)
        {
            return parsed;
        }

        return new SdCardDeviceConfiguration(
            AnalogPortCount: parsed.AnalogPortCount > 0 ? parsed.AnalogPortCount : overrideConfig.AnalogPortCount,
            DigitalPortCount: parsed.DigitalPortCount > 0 ? parsed.DigitalPortCount : overrideConfig.DigitalPortCount,
            TimestampFrequency: parsed.TimestampFrequency > 0 ? parsed.TimestampFrequency : overrideConfig.TimestampFrequency,
            DeviceSerialNumber: Stated(parsed.DeviceSerialNumber) ?? overrideConfig.DeviceSerialNumber,
            DevicePartNumber: Stated(parsed.DevicePartNumber) ?? overrideConfig.DevicePartNumber,
            FirmwareRevision: Stated(parsed.FirmwareRevision) ?? overrideConfig.FirmwareRevision,
            CalibrationValues: parsed.CalibrationValues ?? overrideConfig.CalibrationValues,
            Resolution: parsed.Resolution > 0 ? parsed.Resolution : overrideConfig.Resolution,
            PortRange: parsed.PortRange ?? overrideConfig.PortRange,
            InternalScaleM: parsed.InternalScaleM ?? overrideConfig.InternalScaleM);
    }

    /// <summary>
    /// Returns <paramref name="value"/> when the file actually stated something, or
    /// <see langword="null"/> when it left a gap for the override to fill.
    /// </summary>
    private static string? Stated(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
