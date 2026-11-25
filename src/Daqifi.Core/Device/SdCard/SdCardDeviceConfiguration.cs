using System.Collections.Generic;
using System.Linq;
using Daqifi.Core.Channel;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Device context extracted from a status message in an SD card log file.
/// </summary>
public sealed record SdCardDeviceConfiguration
{
    /// <summary>
    /// Number of analog input ports.
    /// </summary>
    public required int AnalogPortCount { get; init; }

    /// <summary>
    /// Number of digital ports.
    /// </summary>
    public required int DigitalPortCount { get; init; }

    /// <summary>
    /// Clock frequency for tick-to-time conversion.
    /// </summary>
    public required uint TimestampFrequency { get; init; }

    /// <summary>
    /// Device serial number, if present, in decimal as <see cref="DeviceMetadata.SerialNumber"/>
    /// reports it. CSV and JSON logs state it in hex; the parsers convert it.
    /// </summary>
    public required string? DeviceSerialNumber { get; init; }

    /// <summary>
    /// Device part number, if present.
    /// </summary>
    public required string? DevicePartNumber { get; init; }

    /// <summary>
    /// Firmware revision string, if present.
    /// </summary>
    public required string? FirmwareVersion { get; init; }

    /// <summary>
    /// Per-channel calibration slope/intercept pairs, if present.
    /// </summary>
    public required IReadOnlyList<(double Slope, double Intercept)>? CalibrationValues { get; init; }

    /// <summary>
    /// ADC resolution (e.g., 65535 for 16-bit), used for raw ADC scaling.
    /// </summary>
    public uint Resolution { get; init; }

    /// <summary>
    /// Per-channel voltage port range values, if present.
    /// </summary>
    public IReadOnlyList<double>? PortRange { get; init; }

    /// <summary>
    /// Per-channel internal scale factors, if present.
    /// </summary>
    public IReadOnlyList<double>? InternalScaleM { get; init; }

    /// <summary>
    /// Creates an <see cref="SdCardDeviceConfiguration"/> from a connected device's
    /// channel configuration. This captures the calibration, resolution, port range,
    /// internal scale, and timestamp clock values needed to interpret an SD card log file.
    /// </summary>
    /// <remarks>
    /// The device's own <see cref="DaqifiDevice.TimestampFrequency"/> is included because
    /// firmware v3.7.2 and earlier write no timestamp frequency into SD card logs while still
    /// reporting one in their live status message. Passed to
    /// <see cref="SdCardParseOptions.ConfigurationOverride"/>, it fills that gap; a log that
    /// does state its own frequency still wins, so this is a backstop and never an override of
    /// better information. It is <c>0</c> when the device has not reported a frequency, which
    /// leaves the parser's fallback in charge exactly as before.
    /// </remarks>
    /// <param name="device">A connected and initialized device.</param>
    /// <returns>A configuration snapshot, or <c>null</c> if the device has no analog channels.</returns>
    public static SdCardDeviceConfiguration? FromDevice(DaqifiDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        // Taken through GetChannelsSnapshot rather than the live Channels view: this runs on
        // whatever thread the caller is on (a download about to be parsed, an MCP tool dispatch)
        // while the device's consumer thread can be repopulating the collection from a status
        // message. Folding the live view there is exactly what DaqifiDevice.Channels documents as
        // unsafe — it throws "Collection was modified" mid-enumeration. One snapshot also makes
        // the analog and digital counts below describe the same instant instead of two.
        var channels = device.GetChannelsSnapshot();
        var analogChannels = channels.OfType<IAnalogChannel>().ToList();
        if (analogChannels.Count == 0)
        {
            return null;
        }

        var digitalCount = channels.Count(c => c.Type == ChannelType.Digital);
        var firstAnalog = analogChannels[0];

        return new SdCardDeviceConfiguration
        {
            AnalogPortCount = analogChannels.Count,
            DigitalPortCount = digitalCount,
            TimestampFrequency = device.TimestampFrequency,
            DeviceSerialNumber = device.Metadata.SerialNumber,
            DevicePartNumber = device.Metadata.PartNumber,
            FirmwareVersion = device.Metadata.FirmwareVersion,
            CalibrationValues = analogChannels
                .Select(ch => (ch.CalibrationM, ch.CalibrationB))
                .ToArray(),
            Resolution = firstAnalog.Resolution,
            PortRange = analogChannels.Select(ch => ch.PortRange).ToArray(),
            InternalScaleM = analogChannels.Select(ch => ch.InternalScaleM).ToArray()
        };
    }
}
