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
/// <param name="HasDeviceTimestamp">
/// <c>true</c> when <see cref="Timestamp"/> was reconstructed from a real device tick for this
/// entry; <c>false</c> when no usable device timestamp was available (e.g. a zero message
/// timestamp, a missing CSV timestamp column, or an unknown tick rate) and the session's base
/// time was substituted instead.
/// </param>
public sealed record SdCardLogEntry(
    DateTime Timestamp,
    IReadOnlyList<double> AnalogValues,
    uint DigitalData,
    IReadOnlyList<uint>? AnalogTimestamps,
    bool HasDeviceTimestamp = true);
