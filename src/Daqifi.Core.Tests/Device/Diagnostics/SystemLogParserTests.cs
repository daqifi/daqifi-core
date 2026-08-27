using System;
using System.Linq;
using Daqifi.Core.Device.Diagnostics;

namespace Daqifi.Core.Tests.Device.Diagnostics;

public class SystemLogParserTests
{
    [Fact]
    public void Parse_ReturnsOneEntryPerNonEmptyLine_InOrder()
    {
        // Matches the messages SYSTem:LOG:TEST injects.
        var lines = new[]
        {
            "Test log message 1",
            "Test error message",
            "Test info message",
            "Test message 0",
        };

        var entries = SystemLogParser.Parse(lines);

        Assert.Equal(4, entries.Count);
        Assert.Equal("Test log message 1", entries[0].Message);
        Assert.Equal("Test error message", entries[1].Message);
        Assert.Equal("Test message 0", entries[3].Message);
    }

    [Fact]
    public void Parse_SkipsBlankLinesAndTrimsLineEndings()
    {
        var lines = new[] { "first\r", "", "   ", "second\r" };

        var entries = SystemLogParser.Parse(lines);

        Assert.Equal(new[] { "first", "second" }, entries.Select(e => e.Message));
    }

    [Fact]
    public void Parse_DropsScpiErrorAndStatusLines()
    {
        var lines = new[]
        {
            "**ERROR: -113,\"Undefined header\"",
            "Error!! something bad",
            "Real log line",
        };

        var entries = SystemLogParser.Parse(lines);

        Assert.Single(entries);
        Assert.Equal("Real log line", entries[0].Message);
    }

    [Fact]
    public void Parse_KeepsLogContentThatMerelyMentionsError()
    {
        // "error" inside the message must not trigger the error-line filter
        // (only true SCPI error / firmware status prefixes are dropped).
        var lines = new[] { "Test error message", "ADC saturation error detected" };

        var entries = SystemLogParser.Parse(lines);

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void Parse_DropsLinesCarryingStreamBytes()
    {
        // Issue #682: read mid-stream, the firmware's protobuf frames split the reply into
        // hundreds of mangled lines and every one of them used to become a log entry.
        var lines = new[]
        {
            "Test log message 1",
            "\uFFFD\uFFFD\u0008\u0001\uFFFD\u0002",
            "\u0000\u0012\u0004junk",
            "Test info message",
        };

        var entries = SystemLogParser.Parse(lines);

        Assert.Equal(new[] { "Test log message 1", "Test info message" }, entries.Select(e => e.Message));
    }

    [Fact]
    public void Parse_DropsTheBoundaryEntryThatArrivesWithNoiseWeldedOn()
    {
        // The single real entry the frame bytes land on top of goes with them. Documented
        // cost of the filter: losing 1 of 8 beats fabricating 715 (issue #682).
        var lines = new[] { " * \uFFFD\uFFFDTest log message 1", "Test error message" };

        var entries = SystemLogParser.Parse(lines);

        Assert.Equal(new[] { "Test error message" }, entries.Select(e => e.Message));
    }

    [Fact]
    public void Parse_KeepsCleanLinesWithTabsAndCarriageReturns()
    {
        // The filter must be inert on the normal (idle) path: tab is a real firmware
        // delimiter, and a trailing CR from a CRLF line ending is not evidence of binary.
        var lines = new[] { "12:00:01\tTest log message 1\r", "12:00:02\tTest info message\r" };

        var entries = SystemLogParser.Parse(lines);

        Assert.Equal(
            new[] { "12:00:01\tTest log message 1", "12:00:02\tTest info message" },
            entries.Select(e => e.Message));
    }

    [Theory]
    // Control characters that .NET also classifies as whitespace: an unrestricted Trim() would
    // erase them at a line edge and hand a clean-looking string to the corruption check.
    [InlineData("\u000B")] // vertical tab
    [InlineData("\u000C")] // form feed
    [InlineData("\u0085")] // NEL
    public void Parse_DropsNoiseWhoseControlBytesSitAtTheLineEdge(string controlChar)
    {
        var lines = new[]
        {
            controlChar + "stream-junk",
            "trailing-junk" + controlChar,
            "Test info message",
        };

        var entries = SystemLogParser.Parse(lines);

        Assert.Equal("Test info message", Assert.Single(entries).Message);
    }

    [Fact]
    public void Parse_WhenEveryLineIsStreamNoise_ReturnsEmpty()
    {
        var lines = new[] { "\uFFFD\u0008\u0001", "\u0000\u0012\uFFFD" };

        Assert.Empty(SystemLogParser.Parse(lines));
    }

    [Fact]
    public void Parse_WhenEmpty_ReturnsEmpty()
    {
        Assert.Empty(SystemLogParser.Parse(Array.Empty<string>()));
    }

    [Fact]
    public void Parse_WhenNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SystemLogParser.Parse(null!));
    }
}
