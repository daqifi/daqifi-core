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

    /// <summary>
    /// The unit the log file held <see cref="AnalogValues"/> in, and therefore which conversion
    /// produced them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SdCardAnalogValueUnit.RawAdcCodes"/> means the file held ADC codes and
    /// <see cref="AnalogValues"/> is the converted voltage — or the codes themselves when the
    /// parse had no device configuration to convert them with.
    /// <see cref="SdCardAnalogValueUnit.Volts"/> and
    /// <see cref="SdCardAnalogValueUnit.Millivolts"/> mean the device had already applied its
    /// calibration, so no ADC conversion was applied.
    /// </para>
    /// <para>
    /// Declared as an <c>init</c> property (not a primary-constructor parameter) so the
    /// record's generated constructor and <see cref="Deconstruct"/> signatures stay
    /// binary-compatible with consumers compiled before this property was added.
    /// </para>
    /// </remarks>
    public SdCardAnalogValueUnit SourceAnalogValueUnit { get; init; } = SdCardAnalogValueUnit.RawAdcCodes;
}
