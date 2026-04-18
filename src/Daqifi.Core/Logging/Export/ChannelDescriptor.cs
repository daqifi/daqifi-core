using Daqifi.Core.Channel;

namespace Daqifi.Core.Logging.Export;

/// <summary>
/// Identifies a channel within a logging session by device and channel name.
/// </summary>
/// <param name="DeviceName">The name of the device that owns this channel.</param>
/// <param name="DeviceSerialNo">The serial number of the device that owns this channel.</param>
/// <param name="ChannelName">The name of the channel.</param>
/// <param name="ChannelType">The type of the channel (analog or digital).</param>
public record ChannelDescriptor(string DeviceName, string DeviceSerialNo, string ChannelName, ChannelType ChannelType)
{
    /// <summary>
    /// Gets the composite key used to identify this channel in CSV headers and sample rows.
    /// Format: <c>{DeviceName}:{DeviceSerialNo}:{ChannelName}</c>.
    /// </summary>
    public string Key => $"{DeviceName}:{DeviceSerialNo}:{ChannelName}";
}
