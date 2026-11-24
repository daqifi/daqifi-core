using System;

namespace Daqifi.Core.Communication.Messages;

/// <summary>
/// Handles protobuf-based protocol messages from DAQiFi devices.
/// </summary>
public sealed class ProtobufProtocolHandler
{
    private readonly Action<DaqifiOutMessage>? _statusMessageHandler;
    private readonly Action<DaqifiOutMessage>? _streamMessageHandler;
    private readonly Action<DaqifiOutMessage>? _errorMessageHandler;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtobufProtocolHandler"/> class.
    /// </summary>
    /// <param name="statusMessageHandler">Optional handler for status messages.</param>
    /// <param name="streamMessageHandler">Optional handler for streaming messages.</param>
    /// <param name="errorMessageHandler">Optional handler for error messages.</param>
    public ProtobufProtocolHandler(
        Action<DaqifiOutMessage>? statusMessageHandler = null,
        Action<DaqifiOutMessage>? streamMessageHandler = null,
        Action<DaqifiOutMessage>? errorMessageHandler = null)
    {
        _statusMessageHandler = statusMessageHandler;
        _streamMessageHandler = streamMessageHandler;
        _errorMessageHandler = errorMessageHandler;
    }

    /// <summary>
    /// Classifies <paramref name="message"/> and routes it to the matching handler.
    /// </summary>
    /// <remarks>
    /// Routing is entirely synchronous and takes the typed <see cref="DaqifiOutMessage"/>
    /// directly, so a streaming device allocates no wrapper per frame (issue #490).
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
