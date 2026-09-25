using Daqifi.Core.Communication.Messages;
using System;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.Protocol;

/// <summary>
/// Handles protobuf-based protocol messages from DAQiFi devices.
/// </summary>
public class ProtobufProtocolHandler : IProtocolHandler
{
    private readonly Action<DaqifiOutMessage>? _statusMessageHandler;
    private readonly Action<DaqifiOutMessage>? _streamMessageHandler;
    private readonly Action<DaqifiOutMessage>? _sdCardMessageHandler;
    private readonly Action<DaqifiOutMessage>? _errorMessageHandler;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtobufProtocolHandler"/> class.
    /// </summary>
    /// <param name="statusMessageHandler">Optional handler for status messages.</param>
    /// <param name="streamMessageHandler">Optional handler for streaming messages.</param>
    /// <param name="sdCardMessageHandler">Optional handler for SD card messages.</param>
    /// <param name="errorMessageHandler">Optional handler for error messages.</param>
    public ProtobufProtocolHandler(
        Action<DaqifiOutMessage>? statusMessageHandler = null,
        Action<DaqifiOutMessage>? streamMessageHandler = null,
        Action<DaqifiOutMessage>? sdCardMessageHandler = null,
        Action<DaqifiOutMessage>? errorMessageHandler = null)
    {
        _statusMessageHandler = statusMessageHandler;
        _streamMessageHandler = streamMessageHandler;
        _sdCardMessageHandler = sdCardMessageHandler;
        _errorMessageHandler = errorMessageHandler;
    }

    /// <summary>
    /// Determines whether this handler can process the specified message.
    /// </summary>
    /// <param name="message">The message to evaluate.</param>
    /// <returns><c>true</c> if the message is a DaqifiOutMessage; otherwise, <c>false</c>.</returns>
    [Obsolete($"Use {nameof(ProtobufProtocolHandler)}.{nameof(Handle)}({nameof(DaqifiOutMessage)}) instead. This member will be removed in a future major version.")]
    public bool CanHandle(IInboundMessage<object> message)
    {
        return message.Data is DaqifiOutMessage;
    }

    /// <summary>
    /// Processes the specified protobuf message and routes it to the appropriate handler.
    /// </summary>
    /// <remarks>
    /// Unwraps <paramref name="message"/> and calls <see cref="Handle(DaqifiOutMessage)"/>.
    /// </remarks>
    /// <param name="message">The message to process.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [Obsolete($"Use {nameof(ProtobufProtocolHandler)}.{nameof(Handle)}({nameof(DaqifiOutMessage)}) instead. This member will be removed in a future major version.")]
    public Task HandleAsync(IInboundMessage<object> message)
    {
        if (message.Data is not DaqifiOutMessage pbMessage)
        {
            return Task.CompletedTask;
        }

        Handle(pbMessage);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Classifies <paramref name="message"/> and routes it to the matching handler.
    /// </summary>
    /// <remarks>
    /// Routing is entirely synchronous. A caller that already holds a typed
    /// <see cref="DaqifiOutMessage"/> has no reason to wrap it in an
    /// <see cref="IInboundMessage{T}"/> first. On a streaming device that wrapper was allocated
    /// for every frame (issue #490). <c>DaqifiDevice</c> calls this method directly.
    /// </remarks>
    /// <param name="message">The protobuf message to route.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <c>null</c>.</exception>
    public void Handle(DaqifiOutMessage message)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var messageType = DetectMessageType(message);

        switch (messageType)
        {
            case ProtobufMessageType.Status:
                _statusMessageHandler?.Invoke(message);
                break;

            case ProtobufMessageType.Stream:
                _streamMessageHandler?.Invoke(message);
                break;

            case ProtobufMessageType.SdCard:
                _sdCardMessageHandler?.Invoke(message);
                break;

            case ProtobufMessageType.Error:
                _errorMessageHandler?.Invoke(message);
                break;

            case ProtobufMessageType.Unknown:
            default:
                break;
        }
    }

    /// <summary>
    /// Detects the type of a protobuf message based on its contents.
    /// </summary>
    /// <param name="message">The protobuf message to analyze.</param>
    /// <returns>The detected message type.</returns>
    public static ProtobufMessageType DetectMessageType(DaqifiOutMessage message)
    {
        if (IsStatusMessage(message))
        {
            return ProtobufMessageType.Status;
        }

        if (IsStreamMessage(message))
        {
            return ProtobufMessageType.Stream;
        }

        // Never returns SdCard: SD card command responses (listings, downloads) arrive on the
        // text/raw-byte path, not as live protobuf frames, so no DaqifiOutMessage shape marks one.

        if (message.DeviceStatus != 0)
        {
            return ProtobufMessageType.Error;
        }

        return ProtobufMessageType.Unknown;
    }

    /// <summary>
    /// Determines if a message is a status message containing device configuration.
    /// </summary>
    /// <param name="message">The message to check.</param>
    /// <returns><c>true</c> if the message is a status message; otherwise, <c>false</c>.</returns>
    private static bool IsStatusMessage(DaqifiOutMessage message)
    {
        return message.DigitalPortNum != 0 ||
               message.AnalogInPortNum != 0 ||
               message.AnalogOutPortNum != 0;
    }

    /// <summary>
    /// Determines if a message is a streaming data message.
    /// </summary>
    /// <param name="message">The message to check.</param>
    /// <returns><c>true</c> if the message contains streaming data; otherwise, <c>false</c>.</returns>
    private static bool IsStreamMessage(DaqifiOutMessage message)
    {
        // Stream messages contain timestamp and data.
        // Supported firmware sends AnalogInData (raw integer ADC counts) on every transport, but
        // the protocol also defines AnalogInDataFloat (pre-scaled floats). Both must be detected.
        return message.MsgTimeStamp != 0 &&
               (message.AnalogInData.Count > 0 ||
                message.AnalogInDataFloat.Count > 0 ||
                message.DigitalData.Length > 0);
    }
}
