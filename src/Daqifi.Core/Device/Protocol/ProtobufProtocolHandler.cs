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
    public bool CanHandle(IInboundMessage<object> message)
    {
        return message.Data is DaqifiOutMessage;
    }

    /// <summary>
    /// Processes the specified protobuf message and routes it to the appropriate handler.
    /// </summary>
    /// <param name="message">The message to process.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task HandleAsync(IInboundMessage<object> message)
    {
        if (message.Data is not DaqifiOutMessage pbMessage)
        {
            return Task.CompletedTask;
        }

        var messageType = DetectMessageType(pbMessage);

        switch (messageType)
        {
            case ProtobufMessageType.Status:
                _statusMessageHandler?.Invoke(pbMessage);
                break;

            case ProtobufMessageType.Stream:
                _streamMessageHandler?.Invoke(pbMessage);
                break;

            case ProtobufMessageType.SdCard:
                _sdCardMessageHandler?.Invoke(pbMessage);
                break;

            case ProtobufMessageType.Error:
                _errorMessageHandler?.Invoke(pbMessage);
                break;

            case ProtobufMessageType.Unknown:
            default:
                // Unknown message type - could log here if needed
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Detects the type of a protobuf message based on its contents.
    /// </summary>
    /// <param name="message">The protobuf message to analyze.</param>
    /// <returns>The detected message type.</returns>
    public static ProtobufMessageType DetectMessageType(DaqifiOutMessage message)
    {
        // Status messages contain device configuration information
        if (IsStatusMessage(message))
        {
            return ProtobufMessageType.Status;
        }

        // Streaming messages contain analog/digital data with timestamps
        if (IsStreamMessage(message))
        {
            return ProtobufMessageType.Stream;
        }

        // SD card messages are typically text-based and handled separately
        // (This is a placeholder - actual SD card messages come as text responses, not protobuf)

        // Error messages contain error status
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
        // Status messages contain channel configuration information
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
        // Stream messages contain timestamp and data
        return message.MsgTimeStamp != 0 &&
               (message.AnalogInData.Count > 0 || message.DigitalData.Length > 0);
    }
}
