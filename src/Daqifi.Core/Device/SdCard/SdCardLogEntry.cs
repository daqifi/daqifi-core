using System;
using System.Collections.Generic;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Represents a single sample from an SD card log file.
/// </summary>
/// <param name="Timestamp">Reconstructed absolute timestamp for this sample.</param>
/// <param name="AnalogValues">Analog channel readings.</param>
/// <param name="DigitalData">Digital port state.</param>
/// <param name="AnalogTimestamps">Per-channel timestamps if available.</param>
public sealed record SdCardLogEntry(
    DateTime Timestamp,
    IReadOnlyList<double> AnalogValues,
    uint DigitalData,
    IReadOnlyList<uint>? AnalogTimestamps);
