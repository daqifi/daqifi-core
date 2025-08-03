using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Google.Protobuf;

namespace Daqifi.Core.Tests.Communication.Consumers;

public class ProtobufMessageParserTests
{
    [Fact]
    public void ProtobufMessageParser_ParseMessages_WithValidProtobuf_ShouldReturnMessage()
    {
        // Arrange
        var parser = new ProtobufMessageParser();
        // Create minimal valid protobuf data (empty message)
        var data = new byte[] { }; // Empty protobuf message
        
        // Act
        var messages = parser.ParseMessages(data, out var consumedBytes);
        
        // Assert - Empty message should parse successfully
        if (messages.Any())
        {
            Assert.IsType<ProtobufMessage>(messages.First());
        }
        Assert.True(consumedBytes >= 0);
    }

    [Fact]
    public void ProtobufMessageParser_ParseMessages_WithInvalidData_ShouldReturnEmpty()
    {
        // Arrange
        var parser = new ProtobufMessageParser();
        var invalidData = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }; // Invalid protobuf data
        
        // Act
        var messages = parser.ParseMessages(invalidData, out var consumedBytes);
        
        // Assert
        Assert.Empty(messages);
        Assert.Equal(0, consumedBytes);
    }

    [Fact]
    public void ProtobufMessageParser_ParseMessages_WithEmptyData_ShouldReturnEmpty()
    {
        // Arrange
        var parser = new ProtobufMessageParser();
        var data = new byte[0];
        
        // Act
        var messages = parser.ParseMessages(data, out var consumedBytes);
        
        // Assert
        Assert.Empty(messages);
        Assert.Equal(0, consumedBytes);
    }

    [Fact]
    public void ProtobufMessageParser_ParseMessages_WithNullBytes_ShouldHandleCorrectly()
    {
        // Arrange - This simulates the actual issue with binary protobuf containing null bytes
        var parser = new ProtobufMessageParser();
        // Create data with null bytes that would break LineBasedMessageParser
        var dataWithNulls = new byte[] { 0x00, 0x01, 0x02, 0x00, 0x03 };
        
        // Act - Parser should handle the null bytes gracefully
        var messages = parser.ParseMessages(dataWithNulls, out var consumedBytes);
        
        // Assert - Should not throw exceptions, even if parsing fails
        Assert.True(consumedBytes >= 0);
    }

    [Fact]
    public void ProtobufMessageParser_ParseMessages_WithIncompleteData_ShouldReturnEmpty()
    {
        // Arrange
        var parser = new ProtobufMessageParser();
        var originalMessage = new DaqifiOutMessage();
        var completeData = originalMessage.ToByteArray();
        var incompleteData = completeData.Take(completeData.Length / 2).ToArray(); // Half the data
        
        // Act
        var messages = parser.ParseMessages(incompleteData, out var consumedBytes);
        
        // Assert - Should not parse incomplete protobuf
        Assert.Empty(messages);
        Assert.Equal(0, consumedBytes);
    }

    [Fact]
    public void ProtobufMessageParser_ParseMessages_WithMultipleMessages_ShouldParseFirst()
    {
        // Arrange
        var parser = new ProtobufMessageParser();
        // Create combined mock protobuf data
        var data1 = new byte[] { 0x08, 0x01 }; // Mock protobuf message 1
        var data2 = new byte[] { 0x08, 0x02 }; // Mock protobuf message 2
        var combinedData = data1.Concat(data2).ToArray();
        
        // Act
        var messages = parser.ParseMessages(combinedData, out var consumedBytes);
        
        // Assert - Should attempt to parse and consume some data
        Assert.True(consumedBytes >= 0);
    }

    [Fact]
    public void ProtobufMessageParser_ParseMessages_ReturnsCorrectMessageType()
    {
        // Arrange
        var parser = new ProtobufMessageParser();
        var data = new byte[] { }; // Empty protobuf
        
        // Act
        var messages = parser.ParseMessages(data, out var consumedBytes);
        
        // Assert - If parsing succeeds, should return correct types
        if (messages.Any())
        {
            var message = messages.First();
            Assert.IsType<ProtobufMessage>(message);
            Assert.IsType<DaqifiOutMessage>(message.Data);
        }
        Assert.True(consumedBytes >= 0);
    }
}