namespace Daqifi.Core.Communication.Messages;

/// <summary>
/// Represents a message to be sent to the DAQiFi device (Outbound).
/// Defines the contract for serializing the message data into bytes.
/// </summary>
/// <typeparam name="T">The type of the data payload.</typeparam>
public interface IOutboundMessage<T>
{
    /// <summary>
    /// Gets or sets the payload data to be sent.
    /// </summary>
    T Data { get; set;}

    /// <summary>
    /// Converts the message data into a byte array suitable for transmission.
    /// </summary>
    /// <returns>A byte array representing the message.</returns>
    byte[] GetBytes();
} 