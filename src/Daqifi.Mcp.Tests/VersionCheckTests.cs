using Daqifi.Mcp.Tools;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// Tests for the staleness reporting added in #727. The failure these guard against is silent by
/// nature — a server several releases behind looks like hardware that cannot do the thing — so
/// what matters is that a stale install says so, and that an install which is current, or whose
/// staleness is simply unknown, never claims an update it cannot substantiate.
/// </summary>
public class ServerVersionTests
{
    [Fact]
    public void Current_IsAPlainVersionWithNoBuildMetadata()
    {
        // SourceLink appends "+<sha>"; left on, it would never compare equal to a NuGet version.
        Assert.False(string.IsNullOrWhiteSpace(ServerVersion.Current));
        Assert.DoesNotContain('+', ServerVersion.Current);
        Assert.True(Version.TryParse(ServerVersion.Current.Split('-')[0], out _), ServerVersion.Current);
    }

    [Theory]
    [InlineData("1.7.0", "1.2.0")]
    [InlineData("1.7.0", "0.28.0")]   // the drift that prompted the issue
    [InlineData("1.7.1", "1.7.0")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.7.0", "1.7.0-rc.1")]
    public void IsNewer_LaterVersion_IsReported(string candidate, string current)
    {
        Assert.True(ServerVersion.IsNewer(candidate, current));
    }

    [Theory]
    [InlineData("1.7.0", "1.7.0")]
    [InlineData("1.7.0", "1.8.0")]
    [InlineData("0.28.0", "1.7.0")]
    [InlineData("1.7.0-rc.1", "1.7.0")]   // a pre-release is not an upgrade from the release
    [InlineData("1.7.0", "1.7.0+abc123")] // build metadata never affects precedence
    public void IsNewer_NotLater_IsNotReported(string candidate, string current)
    {
        Assert.False(ServerVersion.IsNewer(candidate, current));
    }

    [Theory]
    [InlineData("latest", "1.7.0")]
    [InlineData("1.7.0", "nightly")]
    [InlineData(null, "1.7.0")]
    [InlineData("1.7.0", null)]
    public void IsNewer_UnparseableEitherSide_IsNotReported(string? candidate, string? current)
    {
        // Better to say nothing than to tell a current install it is out of date.
        Assert.False(ServerVersion.IsNewer(candidate, current));
    }

    [Fact]
    public void SelectLatestStable_PicksTheHighestByVersionNotByOrder()
    {
        // The NuGet index is ascending today, but "10.0.0" sorts before "9.0.0" as a string, so a
        // last-element or lexicographic answer would eventually be wrong.
        var versions = new[] { "1.0.0", "10.0.0", "9.0.0", "1.7.0" };

        Assert.Equal("10.0.0", ServerVersion.SelectLatestStable(versions));
    }

    [Fact]
    public void SelectLatestStable_SkipsPreReleasesAndJunk()
    {
        var versions = new[] { "1.7.0", "2.0.0-beta.1", "not-a-version", null };

        Assert.Equal("1.7.0", ServerVersion.SelectLatestStable(versions));
    }

    [Fact]
    public void SelectLatestStable_NothingUsable_IsNull()
    {
        Assert.Null(ServerVersion.SelectLatestStable(new[] { "2.0.0-beta.1", "junk" }));
    }
}

/// <summary>
/// A <see cref="ILatestVersionSource"/> that answers from memory, so the staleness logic is
/// exercised without a request to nuget.org.
/// </summary>
internal sealed class StubLatestVersionSource(string? latest = null, Exception? failure = null)
    : ILatestVersionSource
{
    internal int CallCount { get; private set; }

    public Task<string?> GetLatestStableVersionAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return failure is null ? Task.FromResult(latest) : Task.FromException<string?>(failure);
    }
}

public class VersionStatusTests
{
    private static VersionStatus NewStatus(ILatestVersionSource source, bool versionCheck = true) =>
        new(new ServerOptions { VersionCheck = versionCheck }, source);

    [Fact]
    public async Task ANewerPublishedVersion_IsReportedWithTheUpdateCommand()
    {
        // The running version is whatever this build stamped in, so name a version above it.
        var newer = Bump(ServerVersion.Current);
        var info = await NewStatus(new StubLatestVersionSource(newer)).GetAsync();

        Assert.Equal("ok", info.VersionCheck);
        Assert.True(info.UpdateAvailable);
        Assert.Equal(newer, info.LatestVersion);
        Assert.Equal(ServerVersion.Current, info.Version);
        Assert.Equal("Daqifi.Mcp", info.PackageId);
        Assert.Contains(ServerVersion.UpdateCommand, info.Message);
    }

    [Fact]
    public async Task TheRunningVersionBeingTheNewest_ReportsNoUpdate()
    {
        var info = await NewStatus(new StubLatestVersionSource(ServerVersion.Current)).GetAsync();

        Assert.Equal("ok", info.VersionCheck);
        Assert.False(info.UpdateAvailable);
        Assert.Equal(ServerVersion.Current, info.LatestVersion);
        Assert.Contains("up to date", info.Message);
    }

    [Fact]
    public async Task NuGetBeingUnreachable_SaysSoRatherThanClaimingUpToDate()
    {
        var info = await NewStatus(new StubLatestVersionSource(failure: new HttpRequestException("offline")))
            .GetAsync();

        Assert.Equal("unavailable", info.VersionCheck);
        Assert.False(info.UpdateAvailable);
        Assert.Null(info.LatestVersion);
        Assert.Equal(ServerVersion.Current, info.Version);
        Assert.Contains("unknown", info.Message);
    }

    [Fact]
    public async Task AnIndexWithNoUsableVersion_IsUnavailableNotUpToDate()
    {
        var info = await NewStatus(new StubLatestVersionSource(latest: null)).GetAsync();

        Assert.Equal("unavailable", info.VersionCheck);
        Assert.False(info.UpdateAvailable);
    }

    [Fact]
    public async Task NoVersionCheck_MakesNoRequestAndStillReportsTheRunningVersion()
    {
        var source = new StubLatestVersionSource("999.0.0");
        var status = NewStatus(source, versionCheck: false);

        status.Start();
        var info = await status.GetAsync();

        Assert.Equal(0, source.CallCount);
        Assert.Equal("disabled", info.VersionCheck);
        Assert.False(info.UpdateAvailable);
        Assert.Null(info.LatestVersion);
        Assert.Equal(ServerVersion.Current, info.Version);
    }

    [Fact]
    public async Task TheCheckRunsOncePerProcessNoMatterHowOftenItIsAsked()
    {
        // Startup starts it and the tool reads it; neither should cost a second request.
        var source = new StubLatestVersionSource(ServerVersion.Current);
        var status = NewStatus(source);

        status.Start();
        status.Start();
        await status.GetAsync();
        await status.GetAsync();

        Assert.Equal(1, source.CallCount);
    }

    [Fact]
    public async Task GetServerInfoTool_AnswersWithoutADeviceOrAConnection()
    {
        // The whole point of the tool: it works on a server that has never seen hardware, which
        // is exactly the state someone is in when they wonder why a tool is missing.
        var newer = Bump(ServerVersion.Current);

        var info = await DaqifiTools.GetServerInfo(NewStatus(new StubLatestVersionSource(newer)));

        Assert.True(info.UpdateAvailable);
        Assert.Equal(newer, info.LatestVersion);
    }

    /// <summary>The next major version above <paramref name="version"/>, as a version string.</summary>
    private static string Bump(string version)
    {
        var parsed = Version.Parse(version.Split('-', '+')[0]);
        return $"{parsed.Major + 1}.0.0";
    }
}

public class ServerOptionsVersionCheckTests
{
    [Fact]
    public void Parse_NoArgs_ChecksForUpdates()
    {
        Assert.True(ServerOptions.Parse(Array.Empty<string>()).VersionCheck);
    }

    [Fact]
    public void Parse_NoVersionCheckFlag_DisablesTheCheck()
    {
        Assert.False(ServerOptions.Parse(new[] { "--no-version-check" }).VersionCheck);
    }

    [Fact]
    public void Parse_NoVersionCheckFlag_LeavesTheOtherOptionsAlone()
    {
        var options = ServerOptions.Parse(new[] { "--no-version-check", "--read-only", "--max-sample-rate-hz", "500" });

        Assert.False(options.VersionCheck);
        Assert.True(options.ReadOnly);
        Assert.Equal(500, options.MaxSampleRateHz);
    }

    [Fact]
    public void HelpText_DocumentsTheOptOut()
    {
        // A network call the operator cannot find a way to turn off is the thing to avoid.
        Assert.Contains("--no-version-check", ServerOptions.HelpText);
    }
}
