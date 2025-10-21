namespace Daqifi.Core.Channel;

/// <summary>
/// Represents a digital input/output channel.
/// </summary>
public interface IDigitalChannel : IChannel
{
    /// <summary>
    /// Gets or sets the output value for digital output channels (true = high, false = low).
    /// </summary>
    bool OutputValue { get; set; }

    /// <summary>
    /// Gets whether the digital input is currently high (true) or low (false).
    /// </summary>
    bool IsHigh { get; }
}
