using Daqifi.Core.Communication.Consumers;
using System.Text;

namespace Daqifi.Core.Tests.Communication.Consumers;

public class LineBasedMessageParserTests
{
    [Fact]
    public void LineBasedMessageParser_ParseMessages_WithSingleLine_ShouldReturnOneMessage()
    {
        // Arrange
        var parser = new LineBasedMessageParser();
        var data = Encoding.UTF8.GetBytes("Hello World\r\n");
        
        // Act
        var messages = parser.ParseMessages(data, out var consumedBytes);
        
        // Assert
        Assert.Single(messages);
        Assert.Equal("Hello World", messages.First().Data);
        Assert.Equal(data.Length, consumedBytes);
    }

    [Fact]
    public void LineBasedMessageParser_ParseMessages_WithMultipleLines_ShouldReturnMultipleMessages()
    {
        // Arrange
        var parser = new LineBasedMessageParser();
        var data = Encoding.UTF8.GetBytes("Line 1\r\nLine 2\r\nLine 3\r\n");
        
        // Act
        var messages = parser.ParseMessages(data, out var consumedBytes);
        
        // Assert
        Assert.Equal(3, messages.Count());
        Assert.Equal("Line 1", messages.ElementAt(0).Data);
        Assert.Equal("Line 2", messages.ElementAt(1).Data);
        Assert.Equal("Line 3", messages.ElementAt(2).Data);
        Assert.Equal(data.Length, consumedBytes);
    }

    [Fact]
    public void LineBasedMessageParser_ParseMessages_WithIncompleteMessage_ShouldNotConsumeIncomplete()
    {
        // Arrange
        var parser = new LineBasedMessageParser();
        var data = Encoding.UTF8.GetBytes("Complete Line\r\nIncomplete");
        
        // Act
        var messages = parser.ParseMessages(data, out var consumedBytes);
        
        // Assert
        Assert.Single(messages);
        Assert.Equal("Complete Line", messages.First().Data);
        Assert.Equal(15, consumedBytes); // "Complete Line\r\n" length
    }

    [Fact] 
    public void LineBasedMessageParser_ParseMessages_WithEmptyLines_ShouldIgnoreEmpty()
    {
        // Arrange
        var parser = new LineBasedMessageParser();
        var data = Encoding.UTF8.GetBytes("Line 1\r\n\r\nLine 2\r\n");
        
        // Act
        var messages = parser.ParseMessages(data, out var consumedBytes);
        
        // Assert
        Assert.Equal(2, messages.Count());
        Assert.Equal("Line 1", messages.ElementAt(0).Data);
        Assert.Equal("Line 2", messages.ElementAt(1).Data);
    }

    [Fact]
    public void LineBasedMessageParser_ParseMessages_WithCustomLineEnding_ShouldWork()
    {
        // Arrange
        var parser = new LineBasedMessageParser("\n"); // LF only
        var data = Encoding.UTF8.GetBytes("Line 1\nLine 2\n");
        
        // Act
        var messages = parser.ParseMessages(data, out var consumedBytes);
        
        // Assert
        Assert.Equal(2, messages.Count());
        Assert.Equal("Line 1", messages.ElementAt(0).Data);
        Assert.Equal("Line 2", messages.ElementAt(1).Data);
    }

    [Fact]
    public void LineBasedMessageParser_ParseMessages_WithNoData_ShouldReturnEmpty()
    {
        // Arrange
        var parser = new LineBasedMessageParser();
        var data = new byte[0];
        
        // Act
        var messages = parser.ParseMessages(data, out var consumedBytes);
        
        // Assert
        Assert.Empty(messages);
        Assert.Equal(0, consumedBytes);
    }
}