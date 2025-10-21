namespace Daqifi.Core.Channel;

/// <summary>
/// Represents a channel on a DAQiFi device.
/// </summary>
public interface IChannel
{
    /// <summary>
    /// Gets the channel number/index.
    /// </summary>
    int ChannelNumber { get; }

    /// <summary>
    /// Gets or sets the channel name.
    /// </summary>
    string Name { get; set; }

    /// <summary>
    /// Gets or sets whether the channel is enabled.
    /// </summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// Gets the channel type (Analog or Digital).
    /// </summary>
    ChannelType Type { get; }

    /// <summary>
    /// Gets or sets the channel direction (Input or Output).
    /// </summary>
    ChannelDirection Direction { get; set; }

    /// <summary>
    /// Gets the most recent data sample received on this channel.
    /// </summary>
    IDataSample? ActiveSample { get; }

    /// <summary>
    /// Event raised when a new sample is received on this channel.
    /// </summary>
    event EventHandler<SampleReceivedEventArgs>? SampleReceived;

    /// <summary>
    /// Sets the active sample for this channel and triggers the SampleReceived event.
    /// </summary>
    /// <param name="value">The raw or scaled value.</param>
    /// <param name="timestamp">The timestamp when the sample was taken.</param>
    void SetActiveSample(double value, DateTime timestamp);
}
