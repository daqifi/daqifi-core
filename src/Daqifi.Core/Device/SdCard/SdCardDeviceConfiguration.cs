using System.Collections.Generic;

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
public sealed record SdCardDeviceConfiguration(
    int AnalogPortCount,
    int DigitalPortCount,
    uint TimestampFrequency,
    string? DeviceSerialNumber,
    string? DevicePartNumber,
    string? FirmwareRevision,
    IReadOnlyList<(double Slope, double Intercept)>? CalibrationValues);
