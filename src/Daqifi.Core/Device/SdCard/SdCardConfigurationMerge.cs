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
    /// values are primary; the override fills in gaps (zero or null fields).
    /// </summary>
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
            DeviceSerialNumber: parsed.DeviceSerialNumber ?? overrideConfig.DeviceSerialNumber,
            DevicePartNumber: parsed.DevicePartNumber ?? overrideConfig.DevicePartNumber,
            FirmwareRevision: parsed.FirmwareRevision ?? overrideConfig.FirmwareRevision,
            CalibrationValues: parsed.CalibrationValues ?? overrideConfig.CalibrationValues,
            Resolution: parsed.Resolution > 0 ? parsed.Resolution : overrideConfig.Resolution,
            PortRange: parsed.PortRange ?? overrideConfig.PortRange,
            InternalScaleM: parsed.InternalScaleM ?? overrideConfig.InternalScaleM);
    }
}
