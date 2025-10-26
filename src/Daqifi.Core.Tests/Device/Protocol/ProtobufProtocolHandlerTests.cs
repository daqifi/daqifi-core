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
    public async void HandleAsync_WithStatusMessage_CallsStatusHandler()
    {
        // Arrange
        var statusHandlerCalled = false;
        DaqifiOutMessage receivedMessage = null;

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
    public async void HandleAsync_WithStreamMessage_CallsStreamHandler()
    {
        // Arrange
        var streamHandlerCalled = false;
        DaqifiOutMessage receivedMessage = null;

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
    public async void HandleAsync_WithNonProtobufMessage_DoesNotCallHandlers()
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

    [Theory]
    [InlineData(8u, 0u, 0u, 0u, 0, 0, ProtobufMessageType.Status)]
    [InlineData(0u, 16u, 0u, 0u, 0, 0, ProtobufMessageType.Status)]
    [InlineData(0u, 0u, 2u, 0u, 0, 0, ProtobufMessageType.Status)]
    [InlineData(0u, 0u, 0u, 12345u, 1, 0, ProtobufMessageType.Stream)]
    [InlineData(0u, 0u, 0u, 12345u, 0, 1, ProtobufMessageType.Stream)]
    [InlineData(0u, 0u, 0u, 0u, 0, 0, ProtobufMessageType.Unknown)]
    public void DetectMessageType_ReturnsCorrectType(
        uint analogInPortNum,
        uint digitalPortNum,
        uint analogOutPortNum,
        uint msgTimeStamp,
        int analogDataCount,
        int digitalDataLength,
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
}
