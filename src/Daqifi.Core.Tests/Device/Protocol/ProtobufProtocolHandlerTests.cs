using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device.Protocol;
using Xunit;

namespace Daqifi.Core.Tests.Device.Protocol;

/// <summary>
/// Unit tests for the <see cref="ProtobufProtocolHandler"/> class.
/// </summary>
public class ProtobufProtocolHandlerTests
{
    [Fact]
    public void CanHandle_WithDaqifiOutMessage_ReturnsTrue()
    {
        // Arrange
        var handler = new ProtobufProtocolHandler();
        var message = new GenericInboundMessage<object>(new DaqifiOutMessage());

        // Act
        var result = handler.CanHandle(message);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void CanHandle_WithNonProtobufMessage_ReturnsFalse()
    {
        // Arrange
        var handler = new ProtobufProtocolHandler();
        var message = new GenericInboundMessage<object>("text message");

        // Act
        var result = handler.CanHandle(message);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task HandleAsync_WithStatusMessage_CallsStatusHandler()
    {
        // Arrange
        var statusHandlerCalled = false;
        DaqifiOutMessage? receivedMessage = null;

        var handler = new ProtobufProtocolHandler(
            statusMessageHandler: msg =>
            {
                statusHandlerCalled = true;
                receivedMessage = msg;
            });

        var statusMessage = new DaqifiOutMessage
        {
            AnalogInPortNum = 8,
            DigitalPortNum = 16
        };
        var inboundMessage = new GenericInboundMessage<object>(statusMessage);

        // Act
        await handler.HandleAsync(inboundMessage);

        // Assert
        Assert.True(statusHandlerCalled);
        Assert.NotNull(receivedMessage);
        Assert.Equal(8u, receivedMessage.AnalogInPortNum);
        Assert.Equal(16u, receivedMessage.DigitalPortNum);
    }

    [Fact]
    public async Task HandleAsync_WithStreamMessage_CallsStreamHandler()
    {
        // Arrange
        var streamHandlerCalled = false;
        DaqifiOutMessage? receivedMessage = null;

        var handler = new ProtobufProtocolHandler(
            streamMessageHandler: msg =>
            {
                streamHandlerCalled = true;
                receivedMessage = msg;
            });

        var streamMessage = new DaqifiOutMessage
        {
            MsgTimeStamp = 12345
        };
        streamMessage.AnalogInData.Add(100);
        streamMessage.AnalogInData.Add(200);

        var inboundMessage = new GenericInboundMessage<object>(streamMessage);

        // Act
        await handler.HandleAsync(inboundMessage);

        // Assert
        Assert.True(streamHandlerCalled);
        Assert.NotNull(receivedMessage);
        Assert.Equal(12345u, receivedMessage.MsgTimeStamp);
        Assert.Equal(2, receivedMessage.AnalogInData.Count);
    }

    [Fact]
    public async Task HandleAsync_WithNonProtobufMessage_DoesNotCallHandlers()
    {
        // Arrange
        var statusHandlerCalled = false;
        var streamHandlerCalled = false;

        var handler = new ProtobufProtocolHandler(
            statusMessageHandler: _ => statusHandlerCalled = true,
            streamMessageHandler: _ => streamHandlerCalled = true);

        var textMessage = new GenericInboundMessage<object>("text");

        // Act
        await handler.HandleAsync(textMessage);

        // Assert
        Assert.False(statusHandlerCalled);
        Assert.False(streamHandlerCalled);
    }

    [Fact]
    public async Task HandleAsync_WithFloatStreamMessage_CallsStreamHandler()
    {
        // Arrange - USB firmware sends pre-scaled float values (AnalogInDataFloat)
        var streamHandlerCalled = false;
        DaqifiOutMessage? receivedMessage = null;

        var handler = new ProtobufProtocolHandler(
            streamMessageHandler: msg =>
            {
                streamHandlerCalled = true;
                receivedMessage = msg;
            });

        var streamMessage = new DaqifiOutMessage
        {
            MsgTimeStamp = 99999
        };
        streamMessage.AnalogInDataFloat.Add(1.234f);
        streamMessage.AnalogInDataFloat.Add(2.345f);

        var inboundMessage = new GenericInboundMessage<object>(streamMessage);

        // Act
        await handler.HandleAsync(inboundMessage);

        // Assert
        Assert.True(streamHandlerCalled, "Stream handler should be called for AnalogInDataFloat messages");
        Assert.NotNull(receivedMessage);
        Assert.Equal(99999u, receivedMessage.MsgTimeStamp);
        Assert.Equal(2, receivedMessage.AnalogInDataFloat.Count);
    }

    [Theory]
    [InlineData(8u, 0u, 0u, 0u, 0, 0, false, ProtobufMessageType.Status)]
    [InlineData(0u, 16u, 0u, 0u, 0, 0, false, ProtobufMessageType.Status)]
    [InlineData(0u, 0u, 2u, 0u, 0, 0, false, ProtobufMessageType.Status)]
    [InlineData(0u, 0u, 0u, 12345u, 1, 0, false, ProtobufMessageType.Stream)]   // int data
    [InlineData(0u, 0u, 0u, 12345u, 0, 1, false, ProtobufMessageType.Stream)]   // digital data
    [InlineData(0u, 0u, 0u, 12345u, 0, 0, true, ProtobufMessageType.Stream)]    // float data (USB firmware)
    [InlineData(0u, 0u, 0u, 0u, 0, 0, false, ProtobufMessageType.Unknown)]
    public void DetectMessageType_ReturnsCorrectType(
        uint analogInPortNum,
        uint digitalPortNum,
        uint analogOutPortNum,
        uint msgTimeStamp,
        int analogDataCount,
        int digitalDataLength,
        bool hasFloatData,
        ProtobufMessageType expectedType)
    {
        // Arrange
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = analogInPortNum,
            DigitalPortNum = digitalPortNum,
            AnalogOutPortNum = analogOutPortNum,
            MsgTimeStamp = msgTimeStamp
        };

        for (int i = 0; i < analogDataCount; i++)
        {
            message.AnalogInData.Add(100);
        }

        if (digitalDataLength > 0)
        {
            message.DigitalData = Google.Protobuf.ByteString.CopyFrom(new byte[digitalDataLength]);
        }

        if (hasFloatData)
        {
            message.AnalogInDataFloat.Add(1.5f);
        }

        // Act
        var result = ProtobufProtocolHandler.DetectMessageType(message);

        // Assert
        Assert.Equal(expectedType, result);
    }

    [Fact]
    public void DetectMessageType_WithDeviceStatus_ReturnsError()
    {
        // Arrange
        var message = new DaqifiOutMessage
        {
            DeviceStatus = 1
        };

        // Act
        var result = ProtobufProtocolHandler.DetectMessageType(message);

        // Assert
        Assert.Equal(ProtobufMessageType.Error, result);
    }

    /// <summary>
    /// The typed entry point exists so a caller that already holds a <see cref="DaqifiOutMessage"/>
    /// does not have to wrap it just to be routed — an allocation per frame on a streaming device
    /// (issue #490). It must route identically to <see cref="ProtobufProtocolHandler.HandleAsync"/>,
    /// which is now built on it.
    /// </summary>
    [Theory]
    [InlineData(ProtobufMessageType.Status)]
    [InlineData(ProtobufMessageType.Stream)]
    [InlineData(ProtobufMessageType.Error)]
    [InlineData(ProtobufMessageType.Unknown)]
    public async Task Handle_RoutesTheSameWayHandleAsyncDoes(ProtobufMessageType messageType)
    {
        var viaHandle = new List<string>();
        var viaHandleAsync = new List<string>();

        var handleHandler = HandlerRecording(viaHandle);
        var handleAsyncHandler = HandlerRecording(viaHandleAsync);

        var message = MessageOfType(messageType);

        handleHandler.Handle(message);
        await handleAsyncHandler.HandleAsync(new GenericInboundMessage<object>(message));

        Assert.Equal(viaHandleAsync, viaHandle);
        Assert.Equal(
            messageType == ProtobufMessageType.Unknown ? 0 : 1,
            viaHandle.Count);
    }

    [Fact]
    public void Handle_RejectsANullMessage()
    {
        var handler = new ProtobufProtocolHandler();

        Assert.Throws<ArgumentNullException>(() => handler.Handle(null!));
    }

    private static ProtobufProtocolHandler HandlerRecording(List<string> routed) =>
        new(
            statusMessageHandler: _ => routed.Add("status"),
            streamMessageHandler: _ => routed.Add("stream"),
            sdCardMessageHandler: _ => routed.Add("sdcard"),
            errorMessageHandler: _ => routed.Add("error"));

    private static DaqifiOutMessage MessageOfType(ProtobufMessageType messageType)
    {
        switch (messageType)
        {
            case ProtobufMessageType.Status:
                return new DaqifiOutMessage { AnalogInPortNum = 16 };

            case ProtobufMessageType.Stream:
                var stream = new DaqifiOutMessage { MsgTimeStamp = 1000 };
                stream.AnalogInDataFloat.Add(1.0f);
                return stream;

            case ProtobufMessageType.Error:
                return new DaqifiOutMessage { DeviceStatus = 1 };

            default:
                return new DaqifiOutMessage();
        }
    }
}
