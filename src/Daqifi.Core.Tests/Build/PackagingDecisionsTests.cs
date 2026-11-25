using System.Xml.Linq;
using Daqifi.Core.Tests.TestSupport;

namespace Daqifi.Core.Tests.Build;

/// <summary>
/// Pins the packaging decisions of ADR 0003 (issue #1053) that could be undone without anything
/// else going red.
/// </summary>
public class PackagingDecisionsTests
{
    private static string PathFromRoot(params string[] parts) =>
        Path.Combine([RepositoryRoot.FullPath, .. parts]);

    private static string? Property(string file, string name) =>
        XDocument.Load(file)
            .Descendants("PropertyGroup")
            .Elements()
            .Where(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value.Trim())
            .SingleOrDefault();

    private static string CoreProject => PathFromRoot("src", "Daqifi.Core", "Daqifi.Core.csproj");

    private static string BuildProps => PathFromRoot("Directory.Build.props");

    private static string ReleaseWorkflowText =>
        File.ReadAllText(PathFromRoot(".github", "workflows", "release.yml")).ReplaceLineEndings("\n");

    /// <summary>
    /// One job of release.yml, from its key to the next job's (or the end of the file), without
    /// comment lines: what matters is what runs.
    /// </summary>
    private static string Job(string name)
    {
        var lines = ReleaseWorkflowText.Split('\n');
        var start = Array.IndexOf(lines, $"  {name}:");
        Assert.True(start >= 0, $"release.yml has no {name} job.");

        return string.Join('\n', lines
            .Skip(start + 1)
            .TakeWhile(line => !(line.StartsWith("  ", StringComparison.Ordinal) && line.Length > 2 && char.IsLetter(line[2])))
            .Where(line => !line.TrimStart().StartsWith('#')));
    }

    [Fact]
    public void CoreAssembly_IsMarkedTrimmableAndAotCompatible()
    {
        // Read from the assembly that was built: IsAotCompatible turns on the analyzers and stamps
        // these, and losing it turns the analyzers off without a warning.
        var metadata = typeof(Daqifi.Core.Device.DaqifiDevice).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .ToDictionary(a => a.Key, a => a.Value);

        Assert.Equal("True", metadata.GetValueOrDefault("IsTrimmable"), ignoreCase: true);
        Assert.Equal("True", metadata.GetValueOrDefault("IsAotCompatible"), ignoreCase: true);
    }

    [Fact]
    public void BuildsThatAreNotReleases_AreAheadOfTheLastReleaseAndMarkedAsDev()
    {
        // With no prefix every local and CI build is 1.0.0. With no suffix a locally packed build
        // takes the next release's version, and NuGet's cache then serves it in place of the real
        // one.
        var prefix = Version.Parse(Property(BuildProps, "VersionPrefix") ?? "0.0.0");
        var baseline = Version.Parse(Property(CoreProject, "PackageValidationBaselineVersion") ?? "0.0.0");

        Assert.True(prefix > baseline, $"VersionPrefix {prefix} is not ahead of the released {baseline}.");
        Assert.Equal("dev", Property(BuildProps, "VersionSuffix"));
    }

    [Fact]
    public void ReleaseWorkflow_TakesTheVersionFromTheTagAtBuildAndAtPack()
    {
        // Both, because the packs are --no-build: the first stamps the assemblies, the second
        // names the packages. Without them a release would publish VersionPrefix-dev.
        var publish = Job("publish");

        Assert.Contains("dotnet build --configuration Release --no-restore /p:Version=${VERSION}", publish, StringComparison.Ordinal);
        var packs = publish.Split('\n').Where(l => l.Contains("run: dotnet pack", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, packs.Count);
        Assert.All(packs, pack => Assert.Contains("-p:PackageVersion=${VERSION}", pack, StringComparison.Ordinal));
    }

    [Fact]
    public void ReleaseWorkflow_AttachesWhatItPublished_WithoutGivingThePublishJobWriteAccess()
    {
        var publish = Job("publish");
        var assets = Job("release-assets");

        // Nothing reaches the release page unless every gate in `publish` passed and the push worked.
        Assert.Contains("\n    needs: publish\n", "\n" + assets, StringComparison.Ordinal);
        Assert.True(
            publish.IndexOf("actions/upload-artifact@", StringComparison.Ordinal)
                > publish.LastIndexOf("dotnet nuget push", StringComparison.Ordinal),
            "The packages must be handed to release-assets only after they were pushed.");

        Assert.Contains("gh release upload", assets, StringComparison.Ordinal);
        Assert.Contains("actions/attest@", assets, StringComparison.Ordinal);

        // It adds files. Editing or creating the release could undo the prerelease gate.
        Assert.DoesNotContain("gh release edit", ReleaseWorkflowText, StringComparison.Ordinal);
        Assert.DoesNotContain("gh release create", ReleaseWorkflowText, StringComparison.Ordinal);
        Assert.DoesNotContain("contents: write", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageIcon_ExistsAndIsSmallEnoughForNuGet()
    {
        var icon = Property(PathFromRoot("Directory.Build.targets"), "PackageIcon");
        Assert.NotNull(icon);

        var file = new FileInfo(PathFromRoot("assets", icon));
        Assert.True(file.Exists, $"{file.FullName} is missing.");
        Assert.True(file.Length <= 1024 * 1024, "nuget.org rejects an icon over 1 MB.");
    }
}
