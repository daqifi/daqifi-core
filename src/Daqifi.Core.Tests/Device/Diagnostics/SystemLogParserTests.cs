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
