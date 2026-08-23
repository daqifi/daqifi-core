using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

/// <summary>
/// Direct tests for the WINC flash progress parser. It was previously exercised only through
/// <see cref="WifiModuleUpdaterTests"/>, and only via the two phase-start markers — none of the
/// logic that actually drives the bar (block-address coverage, the pre-flash guard, the
/// verify-range rebase, monotonicity) had a test that could fail.
/// </summary>
public class WifiFlashProgressParserTests
{
    // Percentages below are the parser's exact arithmetic:
    //   write band  = 5..60, read band = 60..78, verify band = 78..100
    //   covered     = highestAddress - rangeStart + 0x8000, over a default range of 0x80000

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Observe_BlankLine_ReportsNoProgress(string line)
    {
        var parser = new WifiFlashProgressParser();

        Assert.Null(parser.Observe(line));
    }

    [Fact]
    public void Observe_BeforeTheDeviceFlashStarts_IgnoresTheImageBuildOutput()
    {
        // The tool's local image-build phase races to 100% in seconds. If any of it reached the
        // bar, the write phase would start out already pinned near the top and then sit there
        // for the several minutes the real flash takes.
        var parser = new WifiFlashProgressParser();

        Assert.Null(parser.Observe("written 262144 bytes (50%)"));
        Assert.Null(parser.Observe("0x000000:[wwwwwwww] 0x008000:[wwwwwwww]"));
        Assert.Null(parser.Observe("0x078000:[wwwwwwww]"));
        Assert.Null(parser.Observe("written 524288 bytes (100%)"));

        // The first thing that moves the bar is the write marker, at the bottom of its band.
        AssertPercent(5, parser.Observe("begin write operation"));
    }

    [Fact]
    public void Observe_AfterTheFlashStarts_StillIgnoresPercentagesInTheToolsText()
    {
        var parser = new WifiFlashProgressParser();
        parser.Observe("begin write operation");

        Assert.Null(parser.Observe("written 524288 bytes (100%)"));
    }

    [Fact]
    public void Observe_DuringWrite_AdvancesFromTheHighestAddressOnTheLine()
    {
        var parser = new WifiFlashProgressParser();
        AssertPercent(5, parser.Observe("begin write operation"));

        // 0x18000 covered of 0x80000 (+ one block) = 25% of the write band.
        AssertPercent(18.75, parser.Observe("0x000000:[wwwwwwww] 0x018000:[wwwwwwww]"));
    }

    [Fact]
    public void Observe_WhenAnAddressGoesBackward_HoldsTheBarInsteadOfRewindingIt()
    {
        var parser = new WifiFlashProgressParser();
        parser.Observe("begin write operation");
        AssertPercent(18.75, parser.Observe("0x018000:[wwwwwwww]"));

        Assert.Null(parser.Observe("0x008000:[wwwwwwww]"));

        // ... and the next genuine advance is still reported.
        AssertPercent(22.1875, parser.Observe("0x020000:[wwwwwwww]"));
    }

    [Fact]
    public void Observe_WhenAnEarlierPhaseMarkerReappears_KeepsTheLaterPhasesBand()
    {
        var parser = new WifiFlashProgressParser();
        parser.Observe("begin write operation");
        AssertPercent(60, parser.Observe("begin read operation"));

        Assert.Null(parser.Observe("begin write operation"));

        // 0x40000 covered = 56.25% of the read band (60..78), not of the write band — a phase
        // that regressed would map this to ~35 and silently drop it as a backward move.
        AssertPercent(70.125, parser.Observe("0x040000:[rrrrrrrr]"));
    }

    [Fact]
    public void Observe_WhenTheToolAnnouncesANonZeroVerifyRange_MeasuresCoverageFromThatBase()
    {
        var parser = new WifiFlashProgressParser();

        // The range line itself carries no progress.
        Assert.Null(parser.Observe("verify range 0x00080000 to 0x00100000"));
        AssertPercent(78, parser.Observe("begin verify operation"));

        // Block addresses are absolute. Treated as offsets they would exceed the range on the
        // very first line and jam the bar at 100 for the whole verify pass.
        AssertPercent(79.375, parser.Observe("0x080000:[vvvvvvvv]"));
    }

    [Fact]
    public void Observe_WhenTheRangeIsFullyCovered_ReportsOneHundredOnceAndStopsThere()
    {
        var parser = new WifiFlashProgressParser();
        AssertPercent(78, parser.Observe("begin verify operation"));

        AssertPercent(100, parser.Observe("0x078000:[vvvvvvvv]"));

        // An address past the assumed range must not push the bar over 100 or re-report it.
        Assert.Null(parser.Observe("0x100000:[vvvvvvvv]"));
    }

    private static void AssertPercent(double expected, double? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected, actual.GetValueOrDefault(), 6);
    }
}
