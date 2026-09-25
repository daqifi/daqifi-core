using Daqifi.Core.Communication.Messages;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.Protocol;

/// <summary>
/// Defines a handler for processing device protocol messages.
/// </summary>
public interface IProtocolHandler
{
    /// <summary>
    /// Determines whether this handler can process the specified message.
    /// </summary>
    /// <param name="message">The message to evaluate.</param>
    /// <returns><c>true</c> if this handler can process the message; otherwise, <c>false</c>.</returns>
    [Obsolete($"Use {nameof(ProtobufProtocolHandler)}.{nameof(ProtobufProtocolHandler.Handle)}({nameof(DaqifiOutMessage)}) instead. This member will be removed in a future major version.")]
    bool CanHandle(IInboundMessage<object> message);

    /// <summary>
    /// Processes the specified message.
    /// </summary>
    /// <param name="message">The message to process.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [Obsolete($"Use {nameof(ProtobufProtocolHandler)}.{nameof(ProtobufProtocolHandler.Handle)}({nameof(DaqifiOutMessage)}) instead. This member will be removed in a future major version.")]
    Task HandleAsync(IInboundMessage<object> message);
}
