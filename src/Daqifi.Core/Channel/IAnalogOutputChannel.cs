namespace Daqifi.Core.Channel;

/// <summary>
/// An analog-output (DAC) channel: a channel the host drives to a voltage rather than one the
/// device measures. Available on NQ3 hardware only.
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> an <see cref="IAnalogChannel"/>. Every acquisition path in the library
/// selects its channels with <c>is IAnalogChannel</c> or <c>Type == ChannelType.Analog</c> — the
/// ADC enable bitmask, the stream decoder, the sample-rate model — and a DAC channel belongs in
/// none of them. It shares only <see cref="IChannel"/>, which is what puts it in the device's
/// channel collection and gives it the sample/notification plumbing used here to report the value
/// currently driven on the pin.
/// </remarks>
public interface IAnalogOutputChannel : IChannel
{
    /// <summary>
    /// Gets the DAC resolution in bits, as stated by the device's capability document.
    /// </summary>
    int ResolutionBits { get; }

    /// <summary>
    /// Gets the lowest voltage this channel accepts.
    /// </summary>
    double MinimumVoltage { get; }

    /// <summary>
    /// Gets the highest voltage this channel accepts.
    /// </summary>
    double MaximumVoltage { get; }

    /// <summary>
    /// Gets a value indicating whether <see cref="MinimumVoltage"/>/<see cref="MaximumVoltage"/>
    /// came from the device's own self-description. When <c>false</c> the device stated no range
    /// and the defaults are in use, so a rejected write may reflect Core's assumption rather than
    /// the hardware's real limit.
    /// </summary>
    bool RangeIsAssumed { get; }

    /// <summary>
    /// Gets the voltage most recently latched onto this channel by this library, or <c>null</c> if
    /// none has been. This is what Core commanded, not a hardware measurement — the DAC7718 has no
    /// readback path, and the device's own <c>SOURce:VOLTage:LEVel?</c> likewise answers with the
    /// last value it was told.
    /// </summary>
    double? OutputVoltage { get; }

    /// <summary>
    /// Gets the voltage staged for this channel and not yet latched, or <c>null</c> when nothing is
    /// pending. Staged values take effect together on the next latch.
    /// </summary>
    double? PendingVoltage { get; }

    /// <summary>
    /// Returns whether <paramref name="voltage"/> lies within this channel's accepted range.
    /// </summary>
    /// <param name="voltage">The voltage to test, in volts.</param>
    /// <returns><c>true</c> when the voltage is finite and within the range, inclusive.</returns>
    bool IsInRange(double voltage);
}
