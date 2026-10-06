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
    public void LineBasedMessageParser_Constructor_WithEmptyLineEnding_ShouldThrowArgumentException()
    {
        // An empty line ending encodes to zero bytes, which would make ParseMessages
        // spin forever (searchStart never advances). Fail fast at construction instead.
        var ex = Assert.Throws<ArgumentException>(() => new LineBasedMessageParser(""));
        Assert.Equal("lineEnding", ex.ParamName);
    }

    [Fact]
    public void LineBasedMessageParser_Constructor_WithNullLineEnding_ShouldThrowArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new LineBasedMessageParser(null!));
        Assert.Equal("lineEnding", ex.ParamName);
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

    // ── Reading both line endings off one wire, and seeing blank lines (#538). The DAQiFi
    // firmware answers most commands with CRLF but a few with a bare LF, and terminates its
    // SYSTem:LOG? dump with a blank line. The SCPI text exchange has to recognise all three. ──

    [Fact]
    public void LineBasedMessageParser_WithLineFeedEnding_ReadsCarriageReturnLineFeedDataUnchanged()
    {
        // The property the text exchange relies on: splitting on "\n" is not a choice between the
        // two endings, it reads both. The carriage return lands at the end of the line's slice and
        // is trimmed off with the rest of the trailing whitespace.
        var parser = new LineBasedMessageParser("\n");
        var data = Encoding.UTF8.GetBytes("Line 1\r\nLine 2\r\n");

        var messages = parser.ParseMessages(data, out var consumedBytes);

        Assert.Equal(2, messages.Count());
        Assert.Equal("Line 1", messages.ElementAt(0).Data);
        Assert.Equal("Line 2", messages.ElementAt(1).Data);
        Assert.Equal(data.Length, consumedBytes);
    }

    [Fact]
    public void LineBasedMessageParser_WithLineFeedEnding_ReadsBothEndingsInOneResponse()
    {
        // Exactly what the firmware sends: "Log cleared\n" is a bare-LF ack, the counter query
        // answers CRLF. A CRLF-only parser never sees the first one at all.
        var parser = new LineBasedMessageParser("\n");
        var data = Encoding.UTF8.GetBytes("Log cleared\n0\r\nAdded test log messages\n");

        var messages = parser.ParseMessages(data, out _);

        Assert.Equal(
            new[] { "Log cleared", "0", "Added test log messages" },
            messages.Select(m => m.Data));
    }

    [Fact]
    public void LineBasedMessageParser_WithLineFeedEnding_WhenTheCarriageReturnArrivesInAnEarlierRead_DoesNotSplitTheLine()
    {
        // A CRLF straddling two stream reads must still produce one line, not a line plus a blank
        // one: the CR is never a terminator by itself, so the first read consumes nothing.
        var parser = new LineBasedMessageParser("\n");

        var firstMessages = parser.ParseMessages(Encoding.UTF8.GetBytes("Line 1\r"), out var firstConsumed);
        Assert.Empty(firstMessages);
        Assert.Equal(0, firstConsumed);

        var messages = parser.ParseMessages(Encoding.UTF8.GetBytes("Line 1\r\n"), out var consumedBytes);

        Assert.Single(messages);
        Assert.Equal("Line 1", messages.First().Data);
        Assert.Equal(8, consumedBytes);
    }

    [Fact]
    public void LineBasedMessageParser_ByDefault_StillDropsBlankLines()
    {
        // The new behaviour is opt-in: anyone already using this parser keeps the old result.
        var parser = new LineBasedMessageParser("\n");
        var data = Encoding.UTF8.GetBytes("\r\n");

        var messages = parser.ParseMessages(data, out var consumedBytes);

        Assert.Empty(messages);
        Assert.Equal(data.Length, consumedBytes);
    }

    [Fact]
    public void LineBasedMessageParser_WhenEmittingEmptyLines_ReportsABlankLineAsAnEmptyMessage()
    {
        // The lone CRLF an empty SYSTem:LOG? answers with. It carries no content, but it is proof
        // that the device answered — which is the whole reason the exchange asks for it (#538).
        var parser = new LineBasedMessageParser("\n") { EmitEmptyLines = true };
        var data = Encoding.UTF8.GetBytes("\r\n");

        var messages = parser.ParseMessages(data, out var consumedBytes);

        Assert.Single(messages);
        Assert.Equal(string.Empty, messages.First().Data);
        Assert.Equal(data.Length, consumedBytes);
    }

    [Fact]
    public void LineBasedMessageParser_WhenEmittingEmptyLines_ReportsAWhitespaceOnlyLineAsAnEmptyMessage()
    {
        // Whitespace-only and empty are the same thing to a caller that only reads content, so
        // they arrive the same way rather than as two cases every consumer has to handle.
        var parser = new LineBasedMessageParser("\n") { EmitEmptyLines = true };

        var messages = parser.ParseMessages(Encoding.UTF8.GetBytes("   \t \r\n"), out _);

        Assert.Single(messages);
        Assert.Equal(string.Empty, messages.First().Data);
    }

    [Fact]
    public void LineBasedMessageParser_WhenEmittingEmptyLines_KeepsContentLinesUntouched()
    {
        // The populated log dump: entries, then the terminating blank line.
        var parser = new LineBasedMessageParser("\n") { EmitEmptyLines = true };

        var messages = parser.ParseMessages(Encoding.UTF8.GetBytes("Test error message\r\n\r\n"), out _);

        Assert.Equal(new[] { "Test error message", string.Empty }, messages.Select(m => m.Data));
    }

    [Fact]
    public void LineBasedMessageParser_WhenEmittingEmptyLines_StillLeavesAnIncompleteLineInTheBuffer()
    {
        // Emitting blank lines must not turn "no terminator yet" into "an empty line arrived":
        // a partial line is still not a line.
        var parser = new LineBasedMessageParser("\n") { EmitEmptyLines = true };

        var messages = parser.ParseMessages(Encoding.UTF8.GetBytes("Incomplete"), out var consumedBytes);

        Assert.Empty(messages);
        Assert.Equal(0, consumedBytes);
    }
}