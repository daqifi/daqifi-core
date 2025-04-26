namespace Daqifi.Core.Communication.Messages;

/// <summary>
/// Represents a message containing incoming data from the DAQiFi device,
/// formatted using Google Protocol Buffers (Protobuf).
/// The name DaqifiOutMessage signifies data coming "out" from the device.
/// Implements IInboundMessage.
/// </summary>
public class ProtobufMessage : IInboundMessage<DaqifiOutMessage>
{
    /// <summary>
    /// Gets the data associated with the message, which is a DaqifiOutMessage
    /// received from the device.
    /// </summary>
    public DaqifiOutMessage Data { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtobufMessage"/> class
    /// to wrap incoming device data.
    /// </summary>
    /// <param name="message">The DaqifiOutMessage received from the device.</param>
    public ProtobufMessage(DaqifiOutMessage message)
    {
        Data = message;
    }
}