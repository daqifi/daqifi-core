namespace Daqifi.Core.Channel;

/// <summary>
/// Represents the type of a channel.
/// </summary>
public enum ChannelType
{
    /// <summary>
    /// Digital channel (binary on/off).
    /// </summary>
    Digital,

    /// <summary>
    /// Analog channel (continuous value).
    /// </summary>
    Analog,

    /// <summary>
    /// Analog output (DAC) channel — a continuous value the host drives onto a pin rather than
    /// one the device measures. Modelled by <see cref="AnalogOutputChannel"/> and populated from
    /// the device's capability document; available on NQ3 hardware only.
    /// </summary>
    /// <remarks>
    /// Deliberately a distinct member rather than <see cref="Analog"/> with
    /// <see cref="ChannelDirection.Output"/>: every acquisition path in the library selects its
    /// channels with <c>Type == Analog</c> (the ADC enable bitmask, the stream decoder, the
    /// sample-rate model), and a DAC channel belongs in none of them.
    /// </remarks>
    AnalogOutput
}
