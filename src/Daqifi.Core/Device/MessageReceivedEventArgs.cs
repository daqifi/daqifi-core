using Daqifi.Core.Communication.Messages;
using System;

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Provides data for the <see cref="IDevice.MessageReceived"/> event.
    /// </summary>
    public class MessageReceivedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the inbound message.
        /// </summary>
        public IInboundMessage<object> Message { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="MessageReceivedEventArgs"/> class.
        /// </summary>
        /// <param name="message">The inbound message.</param>
        public MessageReceivedEventArgs(IInboundMessage<object> message)
        {
            Message = message;
        }
    }
} 