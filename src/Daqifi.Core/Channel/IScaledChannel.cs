namespace Daqifi.Core.Channel;

/// <summary>
/// A channel that can convert its readings into engineering units — the seam a caller uses to say
/// "this terminal has a 0-100 PSI transducer on it" and have every subsequent sample carry the
/// pressure alongside the volts.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a separate capability interface rather than members on <see cref="IChannel"/> or
/// <see cref="IAnalogChannel"/>: those are implemented outside this library, and widening them
/// would break every existing implementation. Test for it the way the rest of the library tests for
/// optional device capabilities — <c>if (channel is IScaledChannel scaled)</c>.
/// </para>
/// <para>
/// Implemented by <see cref="AnalogChannel"/>. Digital channels deliberately do not implement it:
/// their samples are a pin's 0/1 state, and there is no engineering quantity to convert.
/// </para>
/// </remarks>
public interface IScaledChannel
{
    /// <summary>
    /// Gets or sets the engineering-unit conversion applied to this channel's samples, or
    /// <c>null</c> for no conversion and no unit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set it and every sample decoded from then on carries it; samples already taken keep the
    /// scaling that was in force when they were decoded, so historical readings are never
    /// retroactively reinterpreted.
    /// </para>
    /// <para>
    /// Left <c>null</c> until something states a unit. Reading the device's capability document
    /// (<see cref="Device.DaqifiDevice.ReadCapabilityDocumentAsync"/>) fills in the device-reported
    /// unit as an identity scaling — a unit label with no arithmetic — and never overwrites a
    /// scaling a caller has already set.
    /// </para>
    /// </remarks>
    ChannelScaling? Scaling { get; set; }

    /// <summary>
    /// Gets the unit this channel's scaled samples are expressed in, or <c>null</c> when no unit is
    /// known. Shorthand for <c>Scaling?.Unit</c>.
    /// </summary>
    string? Unit => Scaling?.Unit;
}
