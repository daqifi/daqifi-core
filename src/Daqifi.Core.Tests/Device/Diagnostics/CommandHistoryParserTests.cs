using System;
using Daqifi.Core.Device.Diagnostics;

namespace Daqifi.Core.Tests.Device.Diagnostics;

public class CommandHistoryParserTests
{
    [Fact]
    public void Parse_StripsHeaderAndNumericPrefix()
    {
        // Matches the SYSTem:LOG:CMDHistory? format: header + "<n>: <command>" lines.
        var lines = new[]
        {
            "Last 3 commands:",
            "3: SYSTem:LOG:TEST",
            "2: SYSTem:STReam:STATS?",
            "1: SYSTem:MEMory:FREE?",
        };

        var commands = CommandHistoryParser.Parse(lines);

        Assert.Equal(new[]
        {
            "SYSTem:LOG:TEST",
            "SYSTem:STReam:STATS?",
            "SYSTem:MEMory:FREE?",
        }, commands);
    }

    [Fact]
    public void Parse_PreservesColonsWithinCommand()
    {
        var lines = new[] { "Last 1 commands:", "1: SYSTem:LOG:LEVel STREAM,2" };

        var commands = CommandHistoryParser.Parse(lines);

        Assert.Equal(new[] { "SYSTem:LOG:LEVel STREAM,2" }, commands);
    }

    [Fact]
    public void Parse_WhenNoHistoryMarker_ReturnsEmpty()
    {
        Assert.Empty(CommandHistoryParser.Parse(new[] { "No command history" }));
    }

    [Fact]
    public void Parse_TrimsLineEndings()
    {
        var lines = new[] { "Last 1 commands:\r", "1: *IDN?\r" };

        var commands = CommandHistoryParser.Parse(lines);

        Assert.Equal(new[] { "*IDN?" }, commands);
    }

    [Fact]
    public void Parse_WhenEmpty_ReturnsEmpty()
    {
        Assert.Empty(CommandHistoryParser.Parse(Array.Empty<string>()));
    }

    [Fact]
    public void Parse_WhenNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CommandHistoryParser.Parse(null!));
    }
}
