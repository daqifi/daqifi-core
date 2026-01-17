using System;
using System.Collections.Generic;
using Daqifi.Core.Channel;

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Provides data for the <see cref="DaqifiDevice.ChannelsPopulated"/> event.
    /// </summary>
    public class ChannelsPopulatedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the list of channels that were populated.
        /// </summary>
        public IReadOnlyList<IChannel> Channels { get; }

        /// <summary>
        /// Gets the number of analog channels that were populated.
        /// </summary>
        public int AnalogChannelCount { get; }

        /// <summary>
        /// Gets the number of digital channels that were populated.
        /// </summary>
        public int DigitalChannelCount { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ChannelsPopulatedEventArgs"/> class.
        /// </summary>
        /// <param name="channels">The list of populated channels.</param>
        /// <param name="analogChannelCount">The number of analog channels populated.</param>
        /// <param name="digitalChannelCount">The number of digital channels populated.</param>
        public ChannelsPopulatedEventArgs(IReadOnlyList<IChannel> channels, int analogChannelCount, int digitalChannelCount)
        {
            Channels = channels;
            AnalogChannelCount = analogChannelCount;
            DigitalChannelCount = digitalChannelCount;
        }
    }
}
