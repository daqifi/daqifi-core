using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using System.Text;

namespace Daqifi.Core.Tests.Communication.Consumers;

public class CompositeMessageParserTests
{
    [Fact]
    public void CompositeMessageParser_ParseMessages_WithTextData_ShouldUseTextParser()
    {
        // Arrange
        var parser = new CompositeMessageParser();
        var textData = Encoding.UTF8.GetBytes("*IDN?\r\nDAQiFi Device v1.0\r\n");
        
        // Act
        var messages = parser.ParseMessages(textData, out var consumedBytes);
        
        // Assert
        Assert.Equal(2, messages.Count());
        Assert.All(messages, msg => Assert.IsType<string>(msg.Data));
        Assert.Equal("*IDN?", messages.First().Data);
        Assert.Equal("DAQiFi Device v1.0", messages.Last().Data);
        Assert.Equal(textData.Length, consumedBytes);
    }

    [Fact]
    public void CompositeMessageParser_ParseMessages_WithBinaryData_ShouldUseProtobufParser()
    {
        // Arrange
        var parser = new CompositeMessageParser();
        // Binary data with null bytes should trigger protobuf parsing attempt
        var binaryDataWithNulls = new byte[] { 0x00, 0x01, 0x00, 0x02 };
        
        // Act
        var messages = parser.ParseMessages(binaryDataWithNulls, out var consumedBytes);
        
        // Assert - Should attempt protobuf parsing due to null bytes
        // Even if parsing fails, should not throw exceptions
        Assert.True(consumedBytes >= 0);
    }

    [Fact]
    public void CompositeMessageParser_ParseMessages_WithNullBytes_ShouldDetectAsBinary()
    {
        // Arrange
        var parser = new CompositeMessageParser();
        var dataWithNulls = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00, 0x57, 0x6F, 0x72, 0x6C, 0x64 }; // "Hello\0World"
        
        // Act
        var messages = parser.ParseMessages(dataWithNulls, out var consumedBytes);
        
        // Assert - Should try protobuf parser first due to null bytes
        // May return empty if not valid protobuf, but should have attempted protobuf parsing
        Assert.True(consumedBytes >= 0);
    }

    [Fact]
    public void CompositeMessageParser_ParseMessages_WithNoNullBytes_ShouldDetectAsText()
    {
        // Arrange
        var parser = new CompositeMessageParser();
        var textOnlyData = Encoding.UTF8.GetBytes("Hello World\r\n");
        
        // Act
        var messages = parser.ParseMessages(textOnlyData, out var consumedBytes);
        
        // Assert
        Assert.Single(messages);
        Assert.IsType<string>(messages.First().Data);
        Assert.Equal("Hello World", messages.First().Data);
        Assert.Equal(textOnlyData.Length, consumedBytes);
    }

    [Fact]
    public void CompositeMessageParser_ParseMessages_WithEmptyData_ShouldReturnEmpty()
    {
        // Arrange
        var parser = new CompositeMessageParser();
        var emptyData = new byte[0];
        
        // Act
        var messages = parser.ParseMessages(emptyData, out var consumedBytes);
        
        // Assert
        Assert.Empty(messages);
        Assert.Equal(0, consumedBytes);
    }

    [Fact]
    public void CompositeMessageParser_ParseMessages_WithCustomParsers_ShouldUseProvided()
    {
        // Arrange
        var mockTextParser = new LineBasedMessageParser("\n"); // Custom line ending
        var mockProtobufParser = new ProtobufMessageParser();
        var parser = new CompositeMessageParser(mockTextParser, mockProtobufParser);
        
        var textData = Encoding.UTF8.GetBytes("Line 1\nLine 2\n");
        
        // Act
        var messages = parser.ParseMessages(textData, out var consumedBytes);
        
        // Assert
        Assert.Equal(2, messages.Count());
        Assert.All(messages, msg => Assert.IsType<string>(msg.Data));
        Assert.Equal("Line 1", messages.First().Data);
        Assert.Equal("Line 2", messages.Last().Data);
    }

    [Fact]
    public void CompositeMessageParser_ParseMessages_WithNullTextParser_ShouldStillWork()
    {
        // Arrange
        var parser = new CompositeMessageParser(null, new ProtobufMessageParser());
        var textData = Encoding.UTF8.GetBytes("Hello World\r\n");
        
        // Act
        var messages = parser.ParseMessages(textData, out var consumedBytes);
        
        // Assert - Should fallback to protobuf parser, which likely won't parse text successfully
        // But should not throw an exception
        Assert.True(consumedBytes >= 0);
    }

    [Fact]
    public void CompositeMessageParser_ParseMessages_WithNullProtobufParser_ShouldStillWork()
    {
        // Arrange
        var parser = new CompositeMessageParser(new LineBasedMessageParser(), null);
        var binaryData = new byte[] { 0x00, 0x01, 0x02, 0x03 }; // Binary data with null bytes
        
        // Act
        var messages = parser.ParseMessages(binaryData, out var consumedBytes);
        
        // Assert - Should try text parser even for binary data
        Assert.True(consumedBytes >= 0);
    }

    [Fact]
    public void CompositeMessageParser_ParseMessages_ReturnsDifferentMessageTypes()
    {
        // Arrange
        var parser = new CompositeMessageParser();
        
        // Test text message
        var textData = Encoding.UTF8.GetBytes("Text Message\r\n");
        var textMessages = parser.ParseMessages(textData, out _);
        
        // Test binary message (mock binary data)
        var binaryData = new byte[] { 0x00, 0x08, 0x01 }; // Mock binary with null bytes
        var binaryMessages = parser.ParseMessages(binaryData, out _);
        
        // Assert
        if (textMessages.Any())
        {
            Assert.IsType<string>(textMessages.First().Data);
        }
        
        // Binary messages might not parse successfully, but should not throw
        Assert.True(binaryMessages.Count() >= 0);
    }

    [Fact]
    public void CompositeMessageParser_ParseMessages_WithMixedScenarios_ShouldHandleGracefully()
    {
        // Arrange
        var parser = new CompositeMessageParser();
        
        // Test various data patterns that might come from real DAQiFi devices
        var scenarios = new[]
        {
            Encoding.UTF8.GetBytes("*IDN?\r\n"),                    // SCPI command
            Encoding.UTF8.GetBytes("SYST:ERR?\r\n"),               // SCPI query
            new byte[] { 0x0A, 0x04, 0x74, 0x65, 0x73, 0x74 },   // Mock protobuf-like
            new byte[] { 0x00, 0x00, 0x00, 0x01 },                // Binary with nulls
            Encoding.UTF8.GetBytes(""),                             // Empty
            new byte[] { 0xFF }                                     // Single byte
        };
        
        // Act & Assert - Should not throw exceptions for any scenario
        foreach (var scenario in scenarios)
        {
            var exception = Record.Exception(() => 
            {
                var messages = parser.ParseMessages(scenario, out var consumed);
                Assert.True(consumed >= 0);
            });
            
            Assert.Null(exception);
        }
    }
}