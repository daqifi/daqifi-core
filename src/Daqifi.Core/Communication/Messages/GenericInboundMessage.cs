namespace Daqifi.Core.Communication.Messages;

/// <summary>
/// Represents a generic message containing incoming data from the DAQiFi device.
/// This is a generic wrapper that can hold any type of data.
/// </summary>
/// <typeparam name="T">The type of the data payload.</typeparam>
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
