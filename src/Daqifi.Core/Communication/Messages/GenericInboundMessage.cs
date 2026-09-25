namespace Daqifi.Core.Communication.Messages;

/// <summary>
/// Represents a generic message containing incoming data from the DAQiFi device.
/// This is a generic wrapper that can hold any type of data.
/// </summary>
/// <remarks>
/// Device routing calls <c>ProtobufProtocolHandler.Handle(DaqifiOutMessage)</c> and does not
/// wrap frames in this type (issue #490).
/// </remarks>
/// <typeparam name="T">The type of the data payload.</typeparam>
[Obsolete($"Use {nameof(Daqifi.Core.Device.Protocol.ProtobufProtocolHandler)}.{nameof(Daqifi.Core.Device.Protocol.ProtobufProtocolHandler.Handle)}({nameof(DaqifiOutMessage)}) instead. This type will be removed in a future major version.")]
public class GenericInboundMessage<T> : IInboundMessage<T>
{
    /// <summary>
    /// Gets the data associated with the message.
    /// </summary>
    public T Data { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="GenericInboundMessage{T}"/> class.
    /// </summary>
    /// <param name="data">The data received from the device.</param>
    public GenericInboundMessage(T data)
    {
        Data = data;
    }
}
