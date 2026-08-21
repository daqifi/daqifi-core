namespace Daqifi.Core.Channel;

/// <summary>
/// Represents a single data sample from a channel.
/// </summary>
public interface IDataSample
{
    /// <summary>
    /// Gets the host (system) timestamp when the sample was taken. For streamed samples this is
    /// reconstructed from the device clock (rollover-aware) rather than the arrival time.
    /// </summary>
    DateTime Timestamp { get; }

    /// <summary>
    /// Gets or sets the scaled value of the sample (e.g. volts for an analog channel, or 0/1 for
    /// a digital channel).
    /// </summary>
    double Value { get; set; }

    /// <summary>
    /// Gets the raw device value this sample was decoded from, when one exists: the raw ADC count
    /// for a calibration-scaled analog sample, or the 0/1 bit for a digital sample. It is
    /// <c>null</c> when the device supplied an already-scaled value (e.g. the USB pre-scaled
    /// float path) or when the sample was not produced by the decode pipeline.
    /// </summary>
    int? RawValue { get; }

    /// <summary>
    /// Gets the raw device timestamp (clock ticks) of the stream frame this sample was decoded
    /// from, taken verbatim from the device, or <c>null</c> for samples not produced from a
    /// stream frame. Unlike <see cref="Timestamp"/> this value is not rollover-adjusted.
    /// </summary>
    uint? DeviceTimestamp { get; }

    /// <summary>
    /// Gets the engineering-unit conversion that was in force on the channel when this sample was
    /// decoded, or <c>null</c> when the channel had none. Kept on the sample rather than read back
    /// from the channel so a later reconfiguration cannot reinterpret a reading that has already
    /// been taken.
    /// </summary>
    /// <remarks>
    /// Defaulted here so that adding engineering-unit support did not break implementations of this
    /// interface outside the library: one that predates it reports "no scaling", which is exactly
    /// what it has.
    /// </remarks>
    ChannelScaling? Scaling => null;

    /// <summary>
    /// Gets <see cref="Value"/> converted into engineering units by <see cref="Scaling"/>, or
    /// <see cref="Value"/> itself when the channel has no scaling configured.
    /// </summary>
    /// <remarks>
    /// Derived on read rather than stored, so it stays consistent with <see cref="Value"/> even
    /// though that property has a setter. Nothing is ever mutated in place to produce it.
    /// </remarks>
    double ScaledValue => Scaling is { } scaling ? scaling.Apply(Value) : Value;

    /// <summary>
    /// Gets the unit <see cref="ScaledValue"/> is expressed in (e.g. <c>"V"</c>, <c>"PSI"</c>), or
    /// <c>null</c> when no unit is known.
    /// </summary>
    string? Unit => Scaling?.Unit;
}
