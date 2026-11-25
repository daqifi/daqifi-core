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
    /// Gets whether the channel is enabled.
    /// </summary>
    /// <remarks>
    /// Use <c>IStreamingDevice.EnableChannel</c> and its siblings to change it. There is no setter,
    /// for the reason <see cref="Direction"/> gives: the stream decoder picks channels by this, so
    /// moving it without telling the device attributes readings to the wrong channel.
    /// </remarks>
    bool IsEnabled { get; }

    /// <summary>
    /// Gets the channel type. See <see cref="ChannelType"/> (Analog, Digital, or AnalogOutput).
    /// </summary>
    ChannelType Type { get; }

    /// <summary>
    /// Gets the channel direction (Input or Output).
    /// </summary>
    /// <remarks>
    /// On a digital channel this tracks the direction the <i>device</i> reports, resynced from
    /// every status message, so it stays right across a reconnect even though the pin's direction
    /// persists on the board and nothing in Core's init sequence resets it (#685). Use
    /// <c>IStreamingDevice.SetDioDirection</c> to change it. There is no setter: one could only
    /// move Core's view without moving the pin, and code that checked this before commanding the
    /// pin would then skip the command.
    /// </remarks>
    ChannelDirection Direction { get; }

    /// <summary>
    /// Gets the most recent data sample received on this channel.
    /// </summary>
    IDataSample? ActiveSample { get; }

    /// <summary>
    /// Event raised when a new sample is received on this channel.
    /// </summary>
    event EventHandler<SampleReceivedEventArgs>? SampleReceived;
}
