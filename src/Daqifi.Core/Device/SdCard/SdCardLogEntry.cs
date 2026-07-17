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
    IReadOnlyList<uint>? AnalogTimestamps)
{
    /// <summary>
    /// <c>true</c> when <see cref="Timestamp"/> was reconstructed from a real device tick for this
    /// entry; <c>false</c> when no usable device timestamp was available (e.g. a zero message
    /// timestamp or an unknown tick rate) and the session's base time was substituted instead.
    /// </summary>
    /// <remarks>
    /// Declared as an <c>init</c> property (not a primary-constructor parameter) so the record's
    /// generated constructor and <see cref="Deconstruct"/> signatures stay binary-compatible with
    /// consumers compiled before this property was added.
    /// </remarks>
    public bool HasDeviceTimestamp { get; init; } = true;
}
