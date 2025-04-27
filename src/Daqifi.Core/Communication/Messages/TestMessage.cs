namespace Daqifi.Core.Communication.Messages;

/// <summary>
/// Represents a message containing incoming data from the DAQiFi device,
/// formatted as a string.
/// Implements IInboundMessage.
/// </summary>
public class TextMessage : IInboundMessage<string>
{
    /// <summary>
    /// Gets the data associated with the message, which is a string
    /// received from the device.
    /// </summary>
    public string Data { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="TextMessage"/> class
    /// to wrap incoming device data.
    /// </summary>
    /// <param name="message">The string received from the device.</param>
    public TextMessage(string message)
    {
        Data = message;
    }
}