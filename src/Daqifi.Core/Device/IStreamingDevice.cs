using System.Collections.Generic;
using Daqifi.Core.Channel;

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Represents a device that supports data streaming.
    /// </summary>
    public interface IStreamingDevice : IDevice
    {
        /// <summary>
        /// Gets or sets the streaming frequency in Hz.
        /// </summary>
        int StreamingFrequency { get; set; }

        /// <summary>
        /// Gets a value indicating whether the device is currently streaming data.
        /// </summary>
        bool IsStreaming { get; }

        /// <summary>
        /// Starts streaming data from the device.
        /// </summary>
        void StartStreaming();

        /// <summary>
        /// Stops streaming data from the device.
        /// </summary>
        void StopStreaming();

        /// <summary>
        /// Enables a single channel and reconfigures the device accordingly.
        /// </summary>
        /// <param name="channel">The channel to enable. Must belong to this device's <c>Channels</c> collection.</param>
        /// <remarks>
        /// For analog channels the device-level ADC enable bitmask is recomputed over all enabled
        /// analog channels and sent to the device. For digital channels the global DIO enable state
        /// is updated. The channel's <see cref="IChannel.IsEnabled"/> flag is set to <c>true</c>.
        /// </remarks>
        void EnableChannel(IChannel channel);

        /// <summary>
        /// Enables multiple channels in a single operation, sending at most one command per channel type.
        /// </summary>
        /// <param name="channels">The channels to enable. Each must belong to this device's <c>Channels</c> collection.</param>
        /// <remarks>
        /// Each channel's <see cref="IChannel.IsEnabled"/> flag is set to <c>true</c>. The device then
        /// receives at most one command per affected channel type: the recomputed ADC enable bitmask for
        /// analog channels and the global DIO enable for digital channels. The input is enumerated once.
        /// </remarks>
        void EnableChannels(IEnumerable<IChannel> channels);

        /// <summary>
        /// Disables a single channel and reconfigures the device accordingly.
        /// </summary>
        /// <param name="channel">The channel to disable. Must belong to this device's <c>Channels</c> collection.</param>
        /// <remarks>
        /// Sets the channel's <see cref="IChannel.IsEnabled"/> flag to <c>false</c>. For analog channels the
        /// ADC enable bitmask is recomputed over the remaining enabled analog channels; for digital channels
        /// the global DIO enable state is updated.
        /// </remarks>
        void DisableChannel(IChannel channel);

        /// <summary>
        /// Disables all channels on the device.
        /// </summary>
        void DisableAllChannels();

        /// <summary>
        /// Sets the direction (input or output) of a digital I/O channel.
        /// </summary>
        /// <param name="channel">The digital channel. Must belong to this device's <c>Channels</c> collection.</param>
        /// <param name="direction">The direction to apply. Must be <see cref="ChannelDirection.Input"/> or <see cref="ChannelDirection.Output"/>.</param>
        void SetDioDirection(IChannel channel, ChannelDirection direction);

        /// <summary>
        /// Sets the output state (high or low) of a digital I/O channel.
        /// </summary>
        /// <param name="channel">The digital channel. Must belong to this device's <c>Channels</c> collection.</param>
        /// <param name="value"><c>true</c> to drive the output high; <c>false</c> to drive it low.</param>
        void SetDioValue(IChannel channel, bool value);

        /// <summary>
        /// Sets the analog output (DAC) voltage of a channel and applies it immediately.
        /// </summary>
        /// <param name="channelNumber">The analog output channel number. DAC channels are addressed by
        /// number; they are not part of the <c>Channels</c> collection (which holds analog inputs).</param>
        /// <param name="voltage">The output voltage, in volts. Must be a finite number.</param>
        /// <remarks>
        /// Analog output is available on NQ3 hardware only. Each call stages the level and then latches it
        /// immediately, so it is not suitable for synchronized multi-channel updates.
        /// </remarks>
        void SetAnalogOutput(int channelNumber, double voltage);

        /// <summary>
        /// Reboots the device and disconnects from it.
        /// </summary>
        /// <remarks>
        /// Sends the reboot command and then tears down the local connection, since the device
        /// drops its link while restarting. Reconnect once the device is back online.
        /// </remarks>
        void Reboot();
    }
}
