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
    /// Every build file that can put a <c>PackageReference</c> or a target framework into a
    /// project here: the projects themselves, plus the two repo-root files MSBuild imports into
    /// all of them.
    /// </summary>
    /// <remarks>
    /// The imported files matter to the sweeps below even though neither declares a
    /// <c>PackageReference</c> today. An item added to <c>Directory.Build.props</c> or
    /// <c>Directory.Build.targets</c> reaches every evaluated project without appearing in any
    /// <c>.csproj</c>, so a sweep that read only the projects would report clean while every
    /// package in the repo carried the reference.
    /// </remarks>
    private static IReadOnlyList<string> BuildFilesThatCanDeclareThem()
    {
        var files = new List<string> { DirectoryBuildPropsPath, DirectoryBuildTargetsPath };
        files.AddRange(ProjectFiles());
        return files;
    }

    /// <summary>
    /// Whether an MSBuild element or attribute name matches <paramref name="expected"/>. MSBuild
    /// names are case-insensitive but <see cref="XDocument"/> lookups are not, so every raw-XML
    /// comparison in this class has to say so explicitly.
    /// </summary>
    private static bool IsMsBuildName(string actual, string expected) =>
        actual.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static string? MsBuildAttribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => IsMsBuildName(a.Name.LocalName, name))?.Value;

    /// <summary>
    /// The package ids a build file references, covering both the <c>Include</c> form and the
    /// <c>Update</c> form (which sets metadata on a reference supplied from elsewhere).
    /// </summary>
    private static IEnumerable<string> PackageReferenceIdsIn(XDocument document) =>
        document.Descendants()
            .Where(e => IsMsBuildName(e.Name.LocalName, "PackageReference"))
            .Select(e => MsBuildAttribute(e, "Include") ?? MsBuildAttribute(e, "Update"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!);

    /// <summary>
    /// The target framework monikers a build file declares. <c>TargetFrameworks</c> is
    /// semicolon-separated, so it contributes one entry per moniker.
    /// </summary>
    private static IEnumerable<string> TargetFrameworkMonikersIn(XDocument document) =>
        document.Descendants("PropertyGroup")
            .Elements()
            .Where(e => IsMsBuildName(e.Name.LocalName, "TargetFramework")
                        || IsMsBuildName(e.Name.LocalName, "TargetFrameworks"))
            .SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// Applies <paramref name="reader"/> to every build file, tagging each result with the file
    /// it came from so a failure message can name the offender.
    /// </summary>
    private static IReadOnlyList<(string File, string Value)> SweepBuildFiles(
        Func<XDocument, IEnumerable<string>> reader)
    {
        var found = new List<(string, string)>();

        foreach (var file in BuildFilesThatCanDeclareThem())
        {
            var relativePath = Path.GetRelativePath(RepositoryRoot, file);

            foreach (var value in reader(XDocument.Load(file)))
            {
                found.Add((relativePath, value));
            }
        }

        return found;
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
    public void NoBuildFilePinsTheSourceLinkPackageTheSdkAlreadyBundles()
    {
        // Issue #661: the .NET SDK has bundled SourceLink since .NET 8, so on the monikers this
        // repo targets an explicit Microsoft.SourceLink.* PackageReference does nothing the SDK
        // is not already doing. Daqifi.Mcp has never carried one and still emits a full
        // sourcelink.json, which is what showed the pin on Daqifi.Core to be redundant. The cost
        // of leaving one in is a version to keep bumping plus the false implication that
        // SourceLink would stop working without it.
        var offenders = SweepBuildFiles(PackageReferenceIdsIn)
            .Where(found => found.Value.StartsWith("Microsoft.SourceLink.", StringComparison.OrdinalIgnoreCase))
            .Select(found => $"{found.File} references {found.Value}")
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
        var offenders = SweepBuildFiles(TargetFrameworkMonikersIn)
            .Where(found => NetVersionOf(found.Value) is not { Major: >= 8 })
            .Select(found => $"{found.File} targets {found.Value}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "A target framework older than net8.0 does not get SourceLink from the SDK, so " +
            "NoBuildFilePinsTheSourceLinkPackageTheSdkAlreadyBundles no longer holds for: " +
            string.Join("; ", offenders));
    }

    [Fact]
    public void TheSweepCoversTheImportedBuildFilesAndEveryProject()
    {
        // Guards both checks above against a scope gap rather than a value gap: if the file list
        // silently stopped including the imported build files, a PackageReference added to one of
        // them would reach every project while the no-pin test still reported clean.
        var swept = BuildFilesThatCanDeclareThem().Select(Path.GetFileName).ToList();

        Assert.Contains("Directory.Build.props", swept);
        Assert.Contains("Directory.Build.targets", swept);
        Assert.Contains("Daqifi.Core.csproj", swept);
        Assert.Contains("Daqifi.Mcp.csproj", swept);
    }

    [Fact]
    public void TheTargetFrameworkSweepSeesTheMonikersTheRepoActuallyTargets()
    {
        // Guards the framework check against going vacuous: if the sweep stopped finding monikers
        // it would pass by looking at nothing rather than by the monikers being current.
        var monikers = SweepBuildFiles(TargetFrameworkMonikersIn)
            .Select(found => found.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("net9.0", monikers);
        Assert.Contains("net10.0", monikers);
    }

    [Fact]
    public void ThePackageReferenceSweepSeesTheReferencesTheProjectsActuallyDeclare()
    {
        // The same anti-vacuous guard for the PackageReference sweep.
        var ids = SweepBuildFiles(PackageReferenceIdsIn)
            .Select(found => found.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("Google.Protobuf", ids);
        Assert.Contains("HidSharp", ids);
    }

    [Fact]
    public void TheRawXmlSweeps_MatchMsBuildNamesCaseInsensitively()
    {
        // MSBuild element, item and attribute names are case-insensitive, so <targetframework>
        // declares the same property as <TargetFramework> and <packagereference include="..."/>
        // declares the same item as <PackageReference Include="..."/>. XDocument lookups are not
        // case-insensitive, so an exact-name match would skip those declarations and let a
        // pre-net8.0 target or a re-added SourceLink pin walk straight past the guards. Same
        // reasoning as CentralizedPropertyNames_AreMatchedCaseInsensitively above, which is why
        // the property sweep there already uses OrdinalIgnoreCase.
        var document = XDocument.Parse(
            """
            <Project>
              <PropertyGroup>
                <targetframework>netstandard2.0</targetframework>
              </PropertyGroup>
              <ItemGroup>
                <packagereference include="Microsoft.SourceLink.GitHub" Version="10.0.400" />
              </ItemGroup>
            </Project>
            """);

        Assert.Equal(new[] { "netstandard2.0" }, TargetFrameworkMonikersIn(document).ToArray());
        Assert.Equal(new[] { "Microsoft.SourceLink.GitHub" }, PackageReferenceIdsIn(document).ToArray());
    }
}
