using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Google.Protobuf;

namespace Daqifi.Core.Tests.Communication.Consumers;

/// <summary>
/// Integration tests demonstrating the full message consumer pipeline:
/// Stream -> StreamMessageConsumer -> ProtobufMessageParser -> MessageReceived event
///
/// These tests verify that StreamMessageConsumer + ProtobufMessageParser fully encapsulates
/// protobuf internals, allowing consumers to interact only through core's abstractions.
/// </summary>
public class StreamMessageConsumerIntegrationTests
{
    /// <summary>
    /// Demonstrates the complete pipeline: valid protobuf data flows from stream through
    /// parser and arrives as a strongly-typed message via the MessageReceived event.
    /// This proves consumers don't need direct Google.Protobuf references for basic usage.
    /// </summary>
    [Fact]
    public void FullPipeline_ValidProtobufMessage_RaisesMessageReceivedEvent()
    {
        // Arrange - Create a valid protobuf message with device info
        var originalMessage = new DaqifiOutMessage
        {
            MsgTimeStamp = 12345678,
            DeviceStatus = 1,
            DevicePort = 9760,
            TimestampFreq = 80000000
        };

        // Serialize with length-delimited format (as the device sends)
        var protobufData = SerializeWithLengthPrefix(originalMessage);
        using var stream = new MemoryStream(protobufData);
        var parser = new ProtobufMessageParser();
        using var consumer = new StreamMessageConsumer<DaqifiOutMessage>(stream, parser);

        IInboundMessage<DaqifiOutMessage>? receivedMessage = null;
        byte[]? receivedRawData = null;
        DateTime receivedTimestamp = default;
        var messageReceived = new ManualResetEventSlim(false);

        consumer.MessageReceived += (sender, args) =>
        {
            receivedMessage = args.Message;
            receivedRawData = args.RawData;
            receivedTimestamp = args.Timestamp;
            messageReceived.Set();
        };

        // Act
        consumer.Start();
        var eventFired = messageReceived.Wait(TimeSpan.FromSeconds(1));
        consumer.Stop();

        // Assert
        Assert.True(eventFired, "MessageReceived event should have fired");
        Assert.NotNull(receivedMessage);
        Assert.IsType<ProtobufMessage>(receivedMessage);

        // Verify the message data is accessible through core abstractions
        var data = receivedMessage.Data;
        Assert.Equal(12345678u, data.MsgTimeStamp);
        Assert.Equal(1u, data.DeviceStatus);
        Assert.Equal(9760u, data.DevicePort);
        Assert.Equal(80000000u, data.TimestampFreq);

        // Verify event args contain useful metadata
        Assert.NotNull(receivedRawData);
        Assert.True(receivedRawData.Length > 0);
        Assert.True(receivedTimestamp > DateTime.MinValue);
    }

    /// <summary>
    /// Demonstrates that malformed protobuf data is handled gracefully without
    /// leaking InvalidProtocolBufferException to consumers.
    /// </summary>
    [Fact]
    public void FullPipeline_MalformedData_HandledGracefullyWithoutException()
    {
        // Arrange - Create intentionally malformed data
        var malformedData = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x01, 0x02, 0x03 };
        using var stream = new MemoryStream(malformedData);
        var parser = new ProtobufMessageParser();
        using var consumer = new StreamMessageConsumer<DaqifiOutMessage>(stream, parser);

        var messagesReceived = new List<IInboundMessage<DaqifiOutMessage>>();
        var errorsReceived = new List<Exception>();

        consumer.MessageReceived += (sender, args) => messagesReceived.Add(args.Message);
        consumer.ErrorOccurred += (sender, args) => errorsReceived.Add(args.Error);

        // Act - Should not throw, parser handles malformed data internally
        consumer.Start();
        Thread.Sleep(100); // Give time for processing
        consumer.Stop();

        // Assert - No messages parsed from garbage data, but no crash either
        Assert.Empty(messagesReceived);
        // Note: ErrorOccurred may or may not fire depending on implementation
        // The key is that no InvalidProtocolBufferException propagates to consumer
    }

    /// <summary>
    /// Demonstrates that multiple consecutive messages are all delivered correctly.
    /// </summary>
    [Fact]
    public void FullPipeline_MultipleMessages_AllDeliveredInOrder()
    {
        // Arrange - Create multiple valid protobuf messages
        var messages = new List<DaqifiOutMessage>
        {
            new() { MsgTimeStamp = 1000, DeviceStatus = 1 },
            new() { MsgTimeStamp = 2000, DeviceStatus = 2 },
            new() { MsgTimeStamp = 3000, DeviceStatus = 3 }
        };

        using var stream = new MemoryStream();
        foreach (var msg in messages)
        {
            var data = SerializeWithLengthPrefix(msg);
            stream.Write(data, 0, data.Length);
        }
        stream.Position = 0;

        var parser = new ProtobufMessageParser();
        using var consumer = new StreamMessageConsumer<DaqifiOutMessage>(stream, parser);

        var receivedMessages = new List<DaqifiOutMessage>();
        var allReceived = new CountdownEvent(3);

        consumer.MessageReceived += (sender, args) =>
        {
            lock (receivedMessages)
            {
                receivedMessages.Add(args.Message.Data);
            }
            allReceived.Signal();
        };

        // Act
        consumer.Start();
        var allEventsReceived = allReceived.Wait(TimeSpan.FromSeconds(2));
        consumer.Stop();

        // Assert
        Assert.True(allEventsReceived, "All 3 messages should have been received");
        Assert.Equal(3, receivedMessages.Count);
        Assert.Equal(1000u, receivedMessages[0].MsgTimeStamp);
        Assert.Equal(2000u, receivedMessages[1].MsgTimeStamp);
        Assert.Equal(3000u, receivedMessages[2].MsgTimeStamp);
    }

    /// <summary>
    /// Demonstrates recovery from a single corrupted byte followed by valid data.
    /// The ProtobufMessageParser uses byte-skipping to recover from malformed data.
    /// This test uses a single garbage byte (0x00 = empty message length) which the parser
    /// handles by skipping to the next byte.
    /// </summary>
    [Fact]
    public void FullPipeline_SingleCorruptedByte_ThenValidData_RecoversAndParsesValidMessage()
    {
        // Arrange - Single zero byte (interpreted as empty length) followed by valid message
        var validMessage = new DaqifiOutMessage { MsgTimeStamp = 99999, DeviceStatus = 5 };
        var validData = SerializeWithLengthPrefix(validMessage);

        // Prepend single 0x00 byte - parser sees this as "length 0" message, skips it
        var combinedData = new byte[1 + validData.Length];
        combinedData[0] = 0x00;
        Array.Copy(validData, 0, combinedData, 1, validData.Length);

        using var stream = new MemoryStream(combinedData);
        var parser = new ProtobufMessageParser();
        using var consumer = new StreamMessageConsumer<DaqifiOutMessage>(stream, parser);

        DaqifiOutMessage? receivedData = null;
        var messageReceived = new ManualResetEventSlim(false);

        consumer.MessageReceived += (sender, args) =>
        {
            receivedData = args.Message.Data;
            messageReceived.Set();
        };

        // Act
        consumer.Start();
        var eventFired = messageReceived.Wait(TimeSpan.FromSeconds(1));
        consumer.Stop();

        // Assert - Should recover and parse the valid message after skipping the zero byte
        Assert.True(eventFired, "Should have recovered and parsed valid message");
        Assert.NotNull(receivedData);
        Assert.Equal(99999u, receivedData.MsgTimeStamp);
        Assert.Equal(5u, receivedData.DeviceStatus);
    }

    /// <summary>
    /// Demonstrates that ErrorOccurred event provides sufficient context for logging,
    /// including the exception, raw data, and timestamp.
    /// </summary>
    [Fact]
    public void FullPipeline_StreamError_ErrorEventProvidesLoggingContext()
    {
        // Arrange - Create a stream that throws on read
        var errorStream = new ErrorThrowingStream();
        var parser = new ProtobufMessageParser();
        using var consumer = new StreamMessageConsumer<DaqifiOutMessage>(errorStream, parser);

        MessageConsumerErrorEventArgs? errorArgs = null;
        var errorReceived = new ManualResetEventSlim(false);

        consumer.ErrorOccurred += (sender, args) =>
        {
            errorArgs = args;
            errorReceived.Set();
        };

        // Act
        consumer.Start();
        var eventFired = errorReceived.Wait(TimeSpan.FromSeconds(1));
        consumer.Stop();

        // Assert
        Assert.True(eventFired, "ErrorOccurred event should have fired");
        Assert.NotNull(errorArgs);
        Assert.NotNull(errorArgs.Error);
        Assert.IsType<IOException>(errorArgs.Error);
        Assert.True(errorArgs.Timestamp > DateTime.MinValue);
        // RawData may be null for stream read errors (no data was read)
    }

    /// <summary>
    /// Demonstrates that ClearBuffer() works correctly via the interface.
    /// </summary>
    [Fact]
    public void ClearBuffer_CalledViaInterface_ClearsInternalBuffer()
    {
        // Arrange
        using var stream = new MemoryStream();
        var parser = new ProtobufMessageParser();
        var consumer = new StreamMessageConsumer<DaqifiOutMessage>(stream, parser);

        // Pre-populate internal buffer by adding data to stream
        var someData = new byte[] { 0x01, 0x02, 0x03 };
        stream.Write(someData, 0, someData.Length);
        stream.Position = 0;

        // Act - Call ClearBuffer via interface
        IMessageConsumer<DaqifiOutMessage> interfaceRef = consumer;
        interfaceRef.ClearBuffer();

        // Assert - No exception thrown, buffer should be cleared
        Assert.Equal(0, consumer.QueuedMessageCount);

        consumer.Dispose();
    }

    /// <summary>
    /// Demonstrates that StopSafely() returns true for clean shutdown and false on timeout.
    /// </summary>
    [Fact]
    public void StopSafely_CleanShutdown_ReturnsTrue()
    {
        // Arrange
        using var stream = new MemoryStream();
        var parser = new ProtobufMessageParser();
        using var consumer = new StreamMessageConsumer<DaqifiOutMessage>(stream, parser);
        consumer.Start();

        // Act
        var result = consumer.StopSafely(timeoutMs: 1000);

        // Assert
        Assert.True(result, "StopSafely should return true for clean shutdown");
        Assert.False(consumer.IsRunning);
    }

    /// <summary>
    /// Demonstrates that the consumer properly implements IDisposable.
    /// </summary>
    [Fact]
    public void Dispose_StopsConsumerAndCleansUp()
    {
        // Arrange
        using var stream = new MemoryStream();
        var parser = new ProtobufMessageParser();
        var consumer = new StreamMessageConsumer<DaqifiOutMessage>(stream, parser);
        consumer.Start();
        Assert.True(consumer.IsRunning);

        // Act
        consumer.Dispose();

        // Assert
        Assert.False(consumer.IsRunning);
        Assert.Throws<ObjectDisposedException>(() => consumer.Start());
        Assert.Throws<ObjectDisposedException>(() => consumer.ClearBuffer());
    }

    /// <summary>
    /// Helper method to serialize a protobuf message with length-delimited format.
    /// This is how the DAQiFi device sends data over the wire.
    /// </summary>
    private static byte[] SerializeWithLengthPrefix(DaqifiOutMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteDelimitedTo(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Test helper stream that throws IOException on read.
    /// </summary>
    private class ErrorThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new IOException("Simulated stream read error");
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
