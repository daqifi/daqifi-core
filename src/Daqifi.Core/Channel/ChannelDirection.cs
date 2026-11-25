namespace Daqifi.Core.Channel;

/// <summary>
/// Represents the direction of data flow for a channel.
/// </summary>
public enum ChannelDirection
{
    /// <summary>
    /// Input channel (reads data from device).
    /// </summary>
    Input = 0,

    /// <summary>
    /// Output channel (writes data to device).
    /// </summary>
    Output = 1
}
