using Daqifi.Core.Channel;

namespace Daqifi.Core.Export;

/// <summary>
/// Identifies a channel by device and channel name.
/// </summary>
/// <remarks>
/// <para>
/// <b>The serial number is the identity; the device name is a label.</b> Core's own sources always
/// write the serial in decimal, the form <see cref="Daqifi.Core.Device.DeviceMetadata.SerialNumber"/>
/// uses, so one board has one spelling however its samples were recorded (issue #915). The name is
/// whatever the host calls the device. It is not stored on the SD card, and discovery reports a
/// different one per transport, so it only matches across recordings when the same name is passed
/// to each.
/// </para>
/// <para>
/// The values are kept exactly as given. An application that supplies its own descriptors and
/// builds <see cref="SampleRow.ChannelKey"/> itself has to be able to rely on
/// <see cref="Key"/> being made of what it passed in, so nothing is normalized here.
/// </para>
/// </remarks>
/// <param name="DeviceName">The name of the device that owns this channel.</param>
/// <param name="DeviceSerialNumber">
/// The serial number of the device that owns this channel, in decimal as
/// <see cref="Daqifi.Core.Device.DeviceMetadata.SerialNumber"/> reports it.
/// </param>
/// <param name="ChannelName">The name of the channel.</param>
/// <param name="ChannelType">The type of the channel. See <see cref="Daqifi.Core.Channel.ChannelType"/> (Analog, Digital, or AnalogOutput).</param>
public sealed record ChannelDescriptor(string DeviceName, string DeviceSerialNumber, string ChannelName, ChannelType ChannelType)
{
    /// <summary>
    /// Gets the composite key used to identify this channel in CSV headers and sample rows.
    /// Format: <c>{DeviceName}:{DeviceSerialNumber}:{ChannelName}</c>.
    /// </summary>
    public string Key => $"{DeviceName}:{DeviceSerialNumber}:{ChannelName}";
}
