using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Communication.Producers;

/// <summary>
/// Provides data for <see cref="IMessageProducer{T}.SendFailed"/>.
/// </summary>
/// <remarks>
/// <see cref="IMessageProducer{T}.Send"/> is fire-and-forget: the message is handed to a
/// background thread and the call returns before the write happens. Without this event, a
/// write that never reaches the device is indistinguishable to the caller from one that
/// was delivered (issue #408). Raising it is purely observational — the producer still
/// keeps draining the remaining queue exactly as it did before this event existed.
/// </remarks>
/// <typeparam name="T">The type of message data the producer sends.</typeparam>
public sealed class MessageSendFailedEventArgs<T> : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MessageSendFailedEventArgs{T}"/> class.
    /// </summary>
    /// <param name="message">The message whose write failed.</param>
    /// <param name="error">The exception the write threw.</param>
    public MessageSendFailedEventArgs(IOutboundMessage<T> message, Exception error)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Error = error ?? throw new ArgumentNullException(nameof(error));
        IsTimeout = error is TimeoutException;
        Timestamp = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets the message whose write failed.
    /// </summary>
    public IOutboundMessage<T> Message { get; }

    /// <summary>
    /// Gets the exception the write threw.
    /// </summary>
    public Exception Error { get; }

    /// <summary>
    /// Gets a value indicating whether <see cref="Error"/> was a <see cref="TimeoutException"/>.
    /// </summary>
    /// <remarks>
    /// A write timeout means the device is not draining its receive buffer right now (busy or
    /// flow-controlled), not that the link is gone — the same reasoning <see cref="ITransportHealthSink"/>
    /// reporting already applies. Subscribers that want to treat delivery failures differently
    /// from a transient timeout can branch on this flag instead of inspecting <see cref="Error"/>'s type.
    /// </remarks>
    public bool IsTimeout { get; }

    /// <summary>
    /// Gets the UTC timestamp when the failure was recorded.
    /// </summary>
    public DateTime Timestamp { get; }
}
