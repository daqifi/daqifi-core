using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using System.Text;

namespace Daqifi.Core.Tests.Communication.Producers;

public class MessageProducerTests
{
    [Fact]
    public void MessageProducer_Constructor_ShouldInitializeCorrectly()
    {
        // Arrange
        using var stream = new MemoryStream();
        
        // Act
        using var producer = new MessageProducer<string>(stream);
        
        // Assert
        Assert.Equal(0, producer.QueuedMessageCount);
        Assert.False(producer.IsRunning);
    }

    [Fact]
    public void MessageProducer_Start_ShouldSetRunningState()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);
        
        // Act
        producer.Start();
        
        // Assert
        Assert.True(producer.IsRunning);
    }

    [Fact]
    public void MessageProducer_Stop_ShouldClearRunningState()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);
        producer.Start();
        
        // Act
        producer.Stop();
        
        // Assert
        Assert.False(producer.IsRunning);
    }

    [Fact]
    public void MessageProducer_Send_WhenRunning_ShouldWriteToStream()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);
        var message = new ScpiMessage("TEST:COMMAND");
        
        producer.Start();
        
        // Act
        producer.Send(message);
        
        // Assert
        var written = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("TEST:COMMAND", written);
    }

    [Fact]
    public void MessageProducer_Send_WhenNotRunning_ShouldThrowException()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);
        var message = new ScpiMessage("TEST:COMMAND");
        
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => producer.Send(message));
    }

    [Fact]
    public void MessageProducer_Send_WithNullMessage_ShouldThrowException()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);
        producer.Start();
        
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => producer.Send(null!));
    }

    [Fact]
    public void MessageProducer_StopSafely_ShouldProcessRemainingMessages()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);
        var message1 = new ScpiMessage("COMMAND1");
        var message2 = new ScpiMessage("COMMAND2");
        
        producer.Start();
        producer.Send(message1);
        producer.Send(message2);
        
        // Act
        var result = producer.StopSafely();
        
        // Assert
        Assert.True(result);
        var written = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("COMMAND1", written);
        Assert.Contains("COMMAND2", written);
    }
}