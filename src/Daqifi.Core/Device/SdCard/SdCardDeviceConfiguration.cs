using System.Collections.Generic;
using System.Linq;
using Daqifi.Core.Channel;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Device context extracted from a status message in an SD card log file.
/// </summary>
/// <param name="AnalogPortCount">Number of analog input ports.</param>
/// <param name="DigitalPortCount">Number of digital ports.</param>
/// <param name="TimestampFrequency">Clock frequency for tick-to-time conversion.</param>
/// <param name="DeviceSerialNumber">Device serial number, if present.</param>
/// <param name="DevicePartNumber">Device part number, if present.</param>
/// <param name="FirmwareRevision">Firmware revision string, if present.</param>
/// <param name="CalibrationValues">Per-channel calibration slope/intercept pairs, if present.</param>
/// <param name="Resolution">ADC resolution (e.g., 65535 for 16-bit), used for raw ADC scaling.</param>
/// <param name="PortRange">Per-channel voltage port range values, if present.</param>
/// <param name="InternalScaleM">Per-channel internal scale factors, if present.</param>
public sealed record SdCardDeviceConfiguration(
    int AnalogPortCount,
    int DigitalPortCount,
    uint TimestampFrequency,
    string? DeviceSerialNumber,
    string? DevicePartNumber,
    string? FirmwareRevision,
    IReadOnlyList<(double Slope, double Intercept)>? CalibrationValues,
    uint Resolution = 0,
    IReadOnlyList<double>? PortRange = null,
    IReadOnlyList<double>? InternalScaleM = null)
{
    /// <summary>
    /// Creates an <see cref="SdCardDeviceConfiguration"/> from a connected device's
    /// channel configuration. This captures the calibration, resolution, port range,
    /// and internal scale values needed to convert raw ADC data in SD card log files.
    /// </summary>
    /// <param name="device">A connected and initialized device.</param>
    /// <returns>A configuration snapshot, or <c>null</c> if the device has no analog channels.</returns>
    public static SdCardDeviceConfiguration? FromDevice(DaqifiDevice device)
    {
        var analogChannels = device.Channels.OfType<IAnalogChannel>().ToList();
        if (analogChannels.Count == 0)
        {
            return null;
        }

        var digitalCount = device.Channels.Count(c => c.Type == ChannelType.Digital);
        var firstAnalog = analogChannels[0];

        return new SdCardDeviceConfiguration(
            AnalogPortCount: analogChannels.Count,
            DigitalPortCount: digitalCount,
            TimestampFrequency: 0, // Let the parser use file-embedded or fallback frequency
            DeviceSerialNumber: device.Metadata.SerialNumber,
            DevicePartNumber: device.Metadata.PartNumber,
            FirmwareRevision: device.Metadata.FirmwareVersion,
            CalibrationValues: analogChannels
                .Select(ch => (ch.CalibrationM, ch.CalibrationB))
                .ToArray(),
            Resolution: firstAnalog.Resolution,
            PortRange: analogChannels.Select(ch => ch.PortRange).ToArray(),
            InternalScaleM: analogChannels.Select(ch => ch.InternalScaleM).ToArray());
    }
}
