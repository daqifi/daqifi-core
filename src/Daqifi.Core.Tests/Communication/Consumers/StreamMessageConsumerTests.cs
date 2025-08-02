using Daqifi.Core.Communication.Consumers;
using System.Text;

namespace Daqifi.Core.Tests.Communication.Consumers;

public class StreamMessageConsumerTests
{
    [Fact]
    public void StreamMessageConsumer_Constructor_ShouldInitializeCorrectly()
    {
        // Arrange
        using var stream = new MemoryStream();
        var parser = new LineBasedMessageParser();
        
        // Act
        using var consumer = new StreamMessageConsumer<string>(stream, parser);
        
        // Assert
        Assert.False(consumer.IsRunning);
        Assert.Equal(0, consumer.QueuedMessageCount);
    }

    [Fact]
    public void StreamMessageConsumer_Start_ShouldSetRunningState()
    {
        // Arrange
        using var stream = new MemoryStream();
        var parser = new LineBasedMessageParser();
        using var consumer = new StreamMessageConsumer<string>(stream, parser);
        
        // Act
        consumer.Start();
        
        // Assert
        Assert.True(consumer.IsRunning);
        
        consumer.Stop();
    }

    [Fact]
    public void StreamMessageConsumer_Stop_ShouldClearRunningState()
    {
        // Arrange
        using var stream = new MemoryStream();
        var parser = new LineBasedMessageParser();
        using var consumer = new StreamMessageConsumer<string>(stream, parser);
        consumer.Start();
        
        // Act
        consumer.Stop();
        
        // Assert
        Assert.False(consumer.IsRunning);
    }

    [Fact]
    public void StreamMessageConsumer_MessageReceived_ShouldFireForValidMessages()
    {
        // Arrange
        var testData = Encoding.UTF8.GetBytes("Test Message\r\n");
        using var stream = new MemoryStream(testData);
        var parser = new LineBasedMessageParser();
        using var consumer = new StreamMessageConsumer<string>(stream, parser);
        
        string? receivedMessage = null;
        consumer.MessageReceived += (sender, args) => receivedMessage = args.Message.Data;
        
        // Act
        consumer.Start();
        Thread.Sleep(200); // Give time for processing
        consumer.Stop();
        
        // Assert
        Assert.Equal("Test Message", receivedMessage);
    }

    [Fact]
    public void StreamMessageConsumer_MultipleMessages_ShouldFireMultipleEvents()
    {
        // Arrange
        var testData = Encoding.UTF8.GetBytes("Message 1\r\nMessage 2\r\nMessage 3\r\n");
        using var stream = new MemoryStream(testData);
        var parser = new LineBasedMessageParser();
        using var consumer = new StreamMessageConsumer<string>(stream, parser);
        
        var receivedMessages = new List<string>();
        consumer.MessageReceived += (sender, args) => receivedMessages.Add(args.Message.Data);
        
        // Act
        consumer.Start();
        Thread.Sleep(300); // Give time for processing
        consumer.Stop();
        
        // Assert
        Assert.Equal(3, receivedMessages.Count);
        Assert.Contains("Message 1", receivedMessages);
        Assert.Contains("Message 2", receivedMessages);
        Assert.Contains("Message 3", receivedMessages);
    }

    [Fact]
    public void StreamMessageConsumer_ErrorHandling_ShouldFireErrorEvent()
    {
        // Arrange - Create a stream that will throw when read
        var errorStream = new ErrorThrowingStream();
        var parser = new LineBasedMessageParser();
        using var consumer = new StreamMessageConsumer<string>(errorStream, parser);
        
        Exception? capturedError = null;
        var errorReceived = false;
        consumer.ErrorOccurred += (sender, args) => 
        { 
            capturedError = args.Error;
            errorReceived = true;
        };
        
        // Act
        consumer.Start();
        
        // Wait for error with timeout
        var timeout = DateTime.UtcNow.AddMilliseconds(500);
        while (!errorReceived && DateTime.UtcNow < timeout)
        {
            Thread.Sleep(10);
        }
        
        consumer.Stop();
        
        // Assert
        Assert.True(errorReceived, "Error event should have been fired");
        Assert.NotNull(capturedError);
        Assert.IsType<InvalidOperationException>(capturedError);
    }

    [Fact]
    public void StreamMessageConsumer_StopSafely_ShouldReturnTrue()
    {
        // Arrange
        using var stream = new MemoryStream();
        var parser = new LineBasedMessageParser();
        using var consumer = new StreamMessageConsumer<string>(stream, parser);
        consumer.Start();
        
        // Act
        var result = consumer.StopSafely();
        
        // Assert
        Assert.True(result);
        Assert.False(consumer.IsRunning);
    }

    [Fact]
    public void StreamMessageConsumer_Dispose_ShouldCleanupResources()
    {
        // Arrange
        using var stream = new MemoryStream();
        var parser = new LineBasedMessageParser();
        var consumer = new StreamMessageConsumer<string>(stream, parser);
        consumer.Start();
        
        // Act
        consumer.Dispose();
        
        // Assert
        Assert.False(consumer.IsRunning);
        Assert.Throws<ObjectDisposedException>(() => consumer.Start());
    }

    // Helper class for testing error scenarios
    private class ErrorThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new InvalidOperationException("Test error for error handling");
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}