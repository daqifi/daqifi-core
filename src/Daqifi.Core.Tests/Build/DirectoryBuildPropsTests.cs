using System.Reflection;
using System.Xml.Linq;

namespace Daqifi.Core.Tests.Build;

/// <summary>
/// Guards the repo-wide build configuration: the properties that live in the root
/// <c>Directory.Build.props</c> (issue #638) or the root <c>Directory.Build.targets</c>
/// (issue #644) must not also be declared in an individual <c>.csproj</c>.
/// </summary>
/// <remarks>
/// The drift these tests exist to prevent is not hypothetical. Before #638 the same three
/// properties were hand-copied into four project files and had already diverged -
/// <c>Daqifi.Mcp</c> was the only project without <c>TreatWarningsAsErrors</c>, so a new warning
/// in the shipping MCP tool built green. A project-local redeclaration is what makes that
/// possible: it silently wins over the shared value, and nothing in the build complains.
///
/// The repository root is captured at build time as an assembly-metadata attribute (see
/// <c>Daqifi.Core.Tests.csproj</c>) rather than discovered by walking up from the test binary,
/// so the tests do not depend on where the output directory happens to sit.
/// </remarks>
public class DirectoryBuildPropsTests
{
    private static string RepositoryRoot =>
        Path.GetFullPath(
            typeof(DirectoryBuildPropsTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "RepositoryRoot")
                .Value!);

    private static string DirectoryBuildPropsPath => Path.Combine(RepositoryRoot, "Directory.Build.props");

    private static string DirectoryBuildTargetsPath => Path.Combine(RepositoryRoot, "Directory.Build.targets");

    /// <summary>
    /// Property names declared in one of the repo-root build files, read from the file itself so
    /// that adding a property there automatically extends the coverage below.
    /// </summary>
    /// <remarks>
    /// Compared case-insensitively because MSBuild property names are: <c>&lt;Nullable&gt;</c> and
    /// <c>&lt;nullable&gt;</c> set the same property, so a redeclaration that differs only in casing
    /// still overrides the centralized value and still has to be caught.
    /// </remarks>
    private static HashSet<string> PropertyNamesIn(string buildFile) =>
        XDocument.Load(buildFile)
            .Descendants("PropertyGroup")
            .Elements()
            .Select(e => e.Name.LocalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every property name centralized at the repo root, across both build files.
    /// </summary>
    private static HashSet<string> CentralizedPropertyNames()
    {
        var names = PropertyNamesIn(DirectoryBuildPropsPath);
        names.UnionWith(PropertyNamesIn(DirectoryBuildTargetsPath));
        return names;
    }

    private static IReadOnlyList<string> ProjectFiles() =>
        Directory.GetFiles(Path.Combine(RepositoryRoot, "src"), "*.csproj", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void DirectoryBuildProps_ExistsAtRepositoryRoot()
    {
        Assert.True(File.Exists(DirectoryBuildPropsPath),
            $"Expected a repo-wide Directory.Build.props at {DirectoryBuildPropsPath}.");
    }

    [Fact]
    public void DirectoryBuildTargets_ExistsAtRepositoryRoot()
    {
        Assert.True(File.Exists(DirectoryBuildTargetsPath),
            $"Expected a repo-wide Directory.Build.targets at {DirectoryBuildTargetsPath}.");
    }

    [Fact]
    public void DirectoryBuildProps_CentralizesTheSharedProperties()
    {
        var centralized = PropertyNamesIn(DirectoryBuildPropsPath);

        Assert.Contains("ImplicitUsings", centralized);
        Assert.Contains("Nullable", centralized);
        Assert.Contains("TreatWarningsAsErrors", centralized);
    }

    [Fact]
    public void DirectoryBuildTargets_CentralizesTheSharedPackageMetadata()
    {
        // Issue #644: Daqifi.Mcp shipped to nuget.org with no license, project URL, repository
        // URL, tags or symbol package because these lived only in Daqifi.Core.csproj.
        var centralized = PropertyNamesIn(DirectoryBuildTargetsPath);

        Assert.Contains("Authors", centralized);
        Assert.Contains("PackageLicenseExpression", centralized);
        Assert.Contains("PackageProjectUrl", centralized);
        Assert.Contains("RepositoryUrl", centralized);
        Assert.Contains("RepositoryType", centralized);
        Assert.Contains("IncludeSymbols", centralized);
        Assert.Contains("PublishRepositoryUrl", centralized);
    }

    [Fact]
    public void DirectoryBuildTargets_LeavesPerPackageIdentityToTheProjects()
    {
        // PackageId, Description and PackageTags say which package this is; centralizing them
        // would give both packages the same identity.
        var centralized = PropertyNamesIn(DirectoryBuildTargetsPath);

        Assert.DoesNotContain("PackageId", centralized);
        Assert.DoesNotContain("Description", centralized);
        Assert.DoesNotContain("PackageTags", centralized);
    }

    [Fact]
    public void BothPackableProjects_DeclareTheirOwnPackageTags()
    {
        // The per-package half of #644: the tags are what a nuget.org search matches on, and
        // Daqifi.Mcp had none at all.
        foreach (var project in new[] { "Daqifi.Core", "Daqifi.Mcp" })
        {
            var path = Path.Combine(RepositoryRoot, "src", project, $"{project}.csproj");
            var tags = XDocument.Load(path)
                .Descendants("PackageTags")
                .Select(e => e.Value)
                .SingleOrDefault();

            Assert.False(string.IsNullOrWhiteSpace(tags), $"{project}.csproj declares no <PackageTags>.");
        }
    }

    [Fact]
    public void CentralizedPropertyNames_AreMatchedCaseInsensitively()
    {
        // MSBuild property names are case-insensitive, so <treatwarningsaserrors> overrides the
        // centralized value just as <TreatWarningsAsErrors> does. An ordinal comparison here
        // would let that redeclaration through.
        var centralized = CentralizedPropertyNames();

        Assert.Contains("treatwarningsaserrors", centralized);
        Assert.Contains("IMPLICITUSINGS", centralized);
    }

    [Fact]
    public void DirectoryBuildProps_DoesNotCentralizeTargetFramework()
    {
        // The projects legitimately differ here (issue #643), and a shared default would hide
        // that rather than settle it. Issue #638 calls this out explicitly.
        var centralized = CentralizedPropertyNames();

        Assert.DoesNotContain("TargetFramework", centralized);
        Assert.DoesNotContain("TargetFrameworks", centralized);
    }

    [Fact]
    public void NoProjectRedeclaresACentralizedProperty()
    {
        var centralized = CentralizedPropertyNames();
        var offenders = new List<string>();

        foreach (var project in ProjectFiles())
        {
            var declared = XDocument.Load(project)
                .Descendants("PropertyGroup")
                .Elements()
                .Select(e => e.Name.LocalName);

            foreach (var name in declared.Where(centralized.Contains))
            {
                offenders.Add($"{Path.GetRelativePath(RepositoryRoot, project)} declares <{name}>");
            }
        }

        Assert.True(offenders.Count == 0,
            "A property centralized in Directory.Build.props or Directory.Build.targets is " +
            "redeclared per-project, which silently overrides the shared value (props) or is " +
            "silently overridden by it (targets): " + string.Join("; ", offenders));
    }

    [Fact]
    public void EveryProjectInTheSolutionIsChecked()
    {
        // Guards the check above against silently going vacuous: if the project layout changes
        // and the enumeration stops finding anything, NoProjectRedeclaresACentralizedProperty
        // would pass by finding nothing rather than by the projects being clean.
        var names = ProjectFiles().Select(Path.GetFileName).ToList();

        Assert.Contains("Daqifi.Core.csproj", names);
        Assert.Contains("Daqifi.Core.Tests.csproj", names);
        Assert.Contains("Daqifi.Mcp.csproj", names);
        Assert.Contains("Daqifi.Mcp.Tests.csproj", names);
    }

    /// <summary>
    /// The package ids referenced by a project, covering both the <c>Include</c> form and the
    /// <c>Update</c> form (which sets metadata on a reference the SDK supplies implicitly).
    /// </summary>
    private static IEnumerable<string> PackageReferenceIdsIn(string project) =>
        XDocument.Load(project)
            .Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!);

    /// <summary>
    /// Every target framework moniker declared anywhere under <c>src</c>, paired with the project
    /// that declares it. <c>TargetFrameworks</c> is semicolon-separated, so it contributes one
    /// entry per moniker.
    /// </summary>
    private static IReadOnlyList<(string Project, string Moniker)> TargetFrameworkMonikers()
    {
        var monikers = new List<(string, string)>();

        foreach (var project in ProjectFiles())
        {
            var declared = XDocument.Load(project)
                .Descendants("PropertyGroup")
                .Elements()
                .Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                .SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            foreach (var moniker in declared)
            {
                monikers.Add((Path.GetRelativePath(RepositoryRoot, project), moniker));
            }
        }

        return monikers;
    }

    /// <summary>
    /// The .NET version a moniker names, or <see langword="null"/> if it does not name one at all.
    /// <c>net9.0-windows</c> is 9.0; <c>netstandard2.0</c> and <c>net472</c> are neither .NET Core
    /// nor .NET 5+, so they come back null and are treated as "older than 8" by the callers.
    /// </summary>
    private static Version? NetVersionOf(string moniker)
    {
        var withoutPlatform = moniker.Split('-')[0];

        if (!withoutPlatform.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Version.TryParse needs at least major.minor, so "472" (net472) and "standard2.0"
        // (netstandard2.0) both fail here rather than being mistaken for a .NET 5+ moniker.
        return Version.TryParse(withoutPlatform[3..], out var version) ? version : null;
    }

    [Fact]
    public void NoProjectPinsTheSourceLinkPackageTheSdkAlreadyBundles()
    {
        // Issue #661: the .NET SDK has bundled SourceLink since .NET 8, so on the monikers this
        // repo targets an explicit Microsoft.SourceLink.* PackageReference does nothing the SDK
        // is not already doing. Daqifi.Mcp has never carried one and still emits a full
        // sourcelink.json, which is what showed the pin on Daqifi.Core to be redundant. The cost
        // of leaving one in is a version to keep bumping plus the false implication that
        // SourceLink would stop working without it.
        var offenders = ProjectFiles()
            .SelectMany(project => PackageReferenceIdsIn(project)
                .Where(id => id.StartsWith("Microsoft.SourceLink.", StringComparison.OrdinalIgnoreCase))
                .Select(id => $"{Path.GetRelativePath(RepositoryRoot, project)} references {id}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "The .NET SDK bundles SourceLink for every target framework in this repo, so these " +
            "references are redundant pins: " + string.Join("; ", offenders));
    }

    [Fact]
    public void EveryTargetFrameworkIsNet8OrLater_WhichIsWhatMakesThatPinRedundant()
    {
        // The precondition the check above rests on, asserted rather than assumed. SourceLink is
        // only bundled from .NET 8 on, so a project that started targeting netstandard2.0 or
        // net472 would need the PackageReference back - and without this test that project would
        // silently ship without SourceLink while the guard above still passed.
        var offenders = TargetFrameworkMonikers()
            .Where(t => NetVersionOf(t.Moniker) is not { Major: >= 8 })
            .Select(t => $"{t.Project} targets {t.Moniker}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "A target framework older than net8.0 does not get SourceLink from the SDK, so " +
            "NoProjectPinsTheSourceLinkPackageTheSdkAlreadyBundles no longer holds for: " +
            string.Join("; ", offenders));
    }

    [Fact]
    public void TheTargetFrameworkSweepSeesTheMonikersTheRepoActuallyTargets()
    {
        // Guards the check above against going vacuous: if the sweep stopped finding monikers it
        // would pass by looking at nothing rather than by the monikers being current.
        var monikers = TargetFrameworkMonikers()
            .Select(t => t.Moniker)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("net9.0", monikers);
        Assert.Contains("net10.0", monikers);
    }

    [Fact]
    public void ThePackageReferenceSweepSeesTheReferencesTheProjectsActuallyDeclare()
    {
        // The same anti-vacuous guard for the PackageReference sweep.
        var ids = ProjectFiles()
            .SelectMany(PackageReferenceIdsIn)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("Google.Protobuf", ids);
        Assert.Contains("HidSharp", ids);
    }
}
