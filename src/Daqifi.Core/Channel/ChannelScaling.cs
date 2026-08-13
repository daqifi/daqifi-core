namespace Daqifi.Core.Channel;

/// <summary>
/// A channel's engineering-unit conversion: the linear transform
/// <c>scaled = value * <see cref="Gain"/> + <see cref="Offset"/></c> and the unit label the result
/// is expressed in.
/// </summary>
/// <remarks>
/// <para>
/// This sits <em>above</em> the device's own calibration. The decode pipeline turns a raw ADC count
/// into volts using the per-channel calibration coefficients
/// (<see cref="IAnalogChannel.GetScaledValue"/>); a <see cref="ChannelScaling"/> then turns those
/// volts into whatever the transducer wired to the terminal actually measures — 12.4 PSI rather
/// than 2.48 V. Gain and offset are the user's transducer constants, not the device's.
/// </para>
/// <para>
/// Immutable, and deliberately so: a sample keeps a reference to the scaling that was in force when
/// it was decoded, so a later reconfiguration cannot silently reinterpret readings that were
/// already taken. Sharing one instance across every sample of a channel is what keeps this free of
/// per-sample allocation in the decode path.
/// </para>
/// <para>
/// <see cref="Gain"/> and <see cref="Offset"/> are validated when the instance is constructed —
/// on the caller's thread, where an exception is a usable error message. <see cref="Apply"/> itself
/// never throws and never returns a non-finite value: a configuration whose arithmetic overflows
/// for a particular reading degrades to the unscaled value for that reading rather than poisoning
/// the stream, because it runs on the decode thread where there is nobody to catch it.
/// </para>
/// </remarks>
public sealed record ChannelScaling
{
    /// <summary>
    /// The no-op transform: gain 1, offset 0, no unit. Useful as a base for
    /// <see cref="WithUnit"/> when only a unit label is known.
    /// </summary>
    public static ChannelScaling Identity { get; } = new(1.0, 0.0);

    /// <summary>
    /// Gets the multiplier applied to the channel's value. A gain of <c>0</c> is permitted and
    /// flattens the channel to a constant <see cref="Offset"/>; it is the caller's business, not an
    /// error.
    /// </summary>
    public double Gain { get; }

    /// <summary>Gets the constant added after <see cref="Gain"/> has been applied.</summary>
    public double Offset { get; }

    /// <summary>
    /// Gets the unit the scaled value is expressed in (e.g. <c>"V"</c>, <c>"PSI"</c>), or
    /// <c>null</c> when no unit is known. Blank input is normalized to <c>null</c> so
    /// "not stated" has exactly one representation.
    /// </summary>
    public string? Unit { get; }

    /// <summary>
    /// Gets a value indicating whether this transform leaves values untouched (gain 1, offset 0).
    /// A unit label alone does not make a scaling non-identity.
    /// </summary>
    public bool IsIdentity => Gain == 1.0 && Offset == 0.0;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelScaling"/> class.
    /// </summary>
    /// <param name="gain">The multiplier applied to the channel's value.</param>
    /// <param name="offset">The constant added after the gain is applied.</param>
    /// <param name="unit">The unit the scaled value is expressed in; blank is treated as unstated.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="gain"/> or <paramref name="offset"/> is not finite. A NaN or
    /// infinite coefficient would make every future reading meaningless, so it is rejected here
    /// rather than silently discarded per-sample by <see cref="Apply"/>.
    /// </exception>
    public ChannelScaling(double gain, double offset = 0.0, string? unit = null)
    {
        if (!double.IsFinite(gain))
        {
            throw new ArgumentOutOfRangeException(nameof(gain), gain, "Gain must be a finite number.");
        }

        if (!double.IsFinite(offset))
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset must be a finite number.");
        }

        Gain = gain;
        Offset = offset;
        Unit = Normalize(unit);
    }

    /// <summary>
    /// Applies the transform to <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The channel value to convert, typically volts.</param>
    /// <returns>
    /// The converted value, or <paramref name="value"/> unchanged when the conversion would produce
    /// a non-finite result (an overflow, or an infinite input). Raw values keep flowing rather than
    /// the stream filling with NaN.
    /// </returns>
    public double Apply(double value)
    {
        var scaled = value * Gain + Offset;
        return double.IsFinite(scaled) ? scaled : value;
    }

    /// <summary>
    /// Returns a copy of this scaling with a different unit label, leaving
    /// <see cref="Gain"/> and <see cref="Offset"/> as they are.
    /// </summary>
    /// <param name="unit">The unit the scaled value is expressed in; blank is treated as unstated.</param>
    /// <returns>This instance when the unit is already the requested one; otherwise a new instance.</returns>
    public ChannelScaling WithUnit(string? unit)
    {
        var normalized = Normalize(unit);
        return normalized == Unit ? this : new ChannelScaling(Gain, Offset, normalized);
    }

    private static string? Normalize(string? unit)
        => string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
}
