using System;
using System.Collections.Generic;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Represents a single sample from an SD card log file.
/// </summary>
public sealed record SdCardLogEntry
{
    /// <summary>
    /// Reconstructed absolute timestamp for this sample.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Analog channel readings.
    /// </summary>
    public required IReadOnlyList<double> AnalogValues { get; init; }

    /// <summary>
    /// Digital port state.
    /// </summary>
    public required uint DigitalData { get; init; }

    /// <summary>
    /// Per-channel timestamps if available.
    /// </summary>
    public required IReadOnlyList<uint>? AnalogTimestamps { get; init; }

    /// <summary>
    /// <c>true</c> when <see cref="Timestamp"/> was reconstructed from a real device tick for this
    /// entry; <c>false</c> when no usable device timestamp was available (e.g. a zero message
    /// timestamp or an unknown tick rate) and the session's base time was substituted instead.
    /// </summary>
    public bool HasDeviceTimestamp { get; init; } = true;

    /// <summary>
    /// The unit the log file held <see cref="AnalogValues"/> in, and therefore which conversion
    /// produced them.
    /// </summary>
    /// <remarks>
    /// <see cref="SdCardAnalogValueUnit.RawAdcCodes"/> means the file held ADC codes and
    /// <see cref="AnalogValues"/> is the converted voltage — or the codes themselves when the
    /// parse had no device configuration to convert them with.
    /// <see cref="SdCardAnalogValueUnit.Volts"/> and
    /// <see cref="SdCardAnalogValueUnit.Millivolts"/> mean the device had already applied its
    /// calibration, so no ADC conversion was applied.
    /// </remarks>
    public SdCardAnalogValueUnit SourceAnalogValueUnit { get; init; } = SdCardAnalogValueUnit.RawAdcCodes;
}
