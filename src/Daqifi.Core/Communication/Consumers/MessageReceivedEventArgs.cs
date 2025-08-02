using Daqifi.Core.Communication.Messages;

namespace Daqifi.Core.Communication.Consumers;

/// <summary>
/// Provides data for message received events.
/// </summary>
/// <typeparam name="T">The type of message data.</typeparam>
public class MessageReceivedEventArgs<T> : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the MessageReceivedEventArgs class.
    /// </summary>
    /// <param name="message">The received message.</param>
    /// <param name="rawData">The raw data that was parsed to create the message.</param>
    public MessageReceivedEventArgs(IInboundMessage<T> message, byte[] rawData)
    {
        Message = message;
        RawData = rawData;
        Timestamp = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets the parsed message.
    /// </summary>
    public IInboundMessage<T> Message { get; }

    /// <summary>
    /// Gets the raw data that was received.
    /// </summary>
    public byte[] RawData { get; }

    /// <summary>
    /// Gets the timestamp when the message was received.
    /// </summary>
    public DateTime Timestamp { get; }
}