using System.Reflection;
using Daqifi.Core.Device.Protocol;
using Xunit;

namespace Daqifi.Core.Tests.Device.Protocol;

/// <summary>
/// Unit tests for the <see cref="ProtobufProtocolHandler"/> class.
/// </summary>
public class ProtobufProtocolHandlerTests
{
    [Fact]
    public void Handle_WithStatusMessage_CallsStatusHandler()
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

        // Act
        handler.Handle(statusMessage);

        // Assert
        Assert.True(statusHandlerCalled);
        Assert.NotNull(receivedMessage);
        Assert.Equal(8u, receivedMessage.AnalogInPortNum);
        Assert.Equal(16u, receivedMessage.DigitalPortNum);
    }

    [Fact]
    public void Handle_WithStreamMessage_CallsStreamHandler()
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

        // Act
        handler.Handle(streamMessage);

        // Assert
        Assert.True(streamHandlerCalled);
        Assert.NotNull(receivedMessage);
        Assert.Equal(12345u, receivedMessage.MsgTimeStamp);
        Assert.Equal(2, receivedMessage.AnalogInData.Count);
    }

    [Fact]
    public void Handle_WithFloatStreamMessage_CallsStreamHandler()
    {
        // Arrange - a frame carrying pre-scaled floats (AnalogInDataFloat). No supported firmware
        // sends this shape on any transport, but the protocol defines it and detection must cover it.
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

        // Act
        handler.Handle(streamMessage);

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
    [InlineData(0u, 0u, 0u, 12345u, 0, 0, true, ProtobufMessageType.Stream)]    // float data (protocol-only shape)
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
    /// (issue #490).
    /// </summary>
    [Theory]
    [InlineData(ProtobufMessageType.Status, "status")]
    [InlineData(ProtobufMessageType.Stream, "stream")]
    [InlineData(ProtobufMessageType.Error, "error")]
    [InlineData(ProtobufMessageType.Unknown, null)]
    public void Handle_RoutesToTheMatchingHandler(ProtobufMessageType messageType, string? expectedRoute)
    {
        var routed = new List<string>();
        var handler = HandlerRecording(routed);
        var message = MessageOfType(messageType);

        handler.Handle(message);

        if (expectedRoute is null)
        {
            Assert.Empty(routed);
        }
        else
        {
            Assert.Equal([expectedRoute], routed);
        }
    }

    [Fact]
    public void Handle_RejectsANullMessage()
    {
        var handler = new ProtobufProtocolHandler();

        Assert.Throws<ArgumentNullException>(() => handler.Handle(null!));
    }

    /// <summary>
    /// <see cref="ObsoleteAttribute"/> is not recorded in <c>PublicAPI.*.txt</c>, so nothing but
    /// this notices if these #490 leftovers stop pointing callers at
    /// <c>ProtobufProtocolHandler.Handle(DaqifiOutMessage)</c>.
    /// </summary>
    [Theory]
    [InlineData("Daqifi.Core.Communication.Messages.GenericInboundMessage`1", null)]
    [InlineData("Daqifi.Core.Device.Protocol.IProtocolHandler", "CanHandle")]
    [InlineData("Daqifi.Core.Device.Protocol.IProtocolHandler", "HandleAsync")]
    [InlineData("Daqifi.Core.Device.Protocol.ProtobufProtocolHandler", "CanHandle")]
    [InlineData("Daqifi.Core.Device.Protocol.ProtobufProtocolHandler", "HandleAsync")]
    public void ObsoleteInboundRoutingSurface_PointsAtTypedHandle(string typeName, string? memberName)
    {
        var type = typeof(ProtobufProtocolHandler).Assembly.GetType(typeName, throwOnError: true)!;

        MemberInfo member = memberName is null
            ? type
            : Assert.Single(type.GetMember(memberName));

        var obsolete = member.GetCustomAttribute<ObsoleteAttribute>();

        Assert.True(
            obsolete != null,
            $"{typeName}{(memberName is null ? "" : "." + memberName)} is no longer marked obsolete. "
            + "Callers should be pointed at ProtobufProtocolHandler.Handle(DaqifiOutMessage).");
        Assert.Contains(
            "ProtobufProtocolHandler.Handle(DaqifiOutMessage)",
            obsolete!.Message,
            StringComparison.Ordinal);
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
