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
    public void Parse_ReturnsOldestFirst_BecauseDeviceNumbersLinesBackwardsAndPrintsNewestLast()
    {
        // Bench-confirmed on a real Nq1 (fw 3.7.2): the "<n>:" prefix counts backwards
        // from the present, so "1:" is the NEWEST command and the firmware prints it last.
        // The decisive evidence is that SYSTem:LOG:CMDHistory? — necessarily the most recent
        // command when the device builds the reply — lands in the final slot of its own answer.
        var lines = new[]
        {
            "Last 3 commands:",
            "3: A",
            "2: B",
            "1: C",
        };

        var commands = CommandHistoryParser.Parse(lines);

        // C is the newest command; it is last. Do not "fix" this by reversing the list —
        // the device's order is a chronological transcript and consumers depend on it.
        Assert.Equal(new[] { "A", "B", "C" }, commands);
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
