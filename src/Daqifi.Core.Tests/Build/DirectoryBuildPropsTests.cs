using System.Reflection;
using System.Xml.Linq;

namespace Daqifi.Core.Tests.Build;

/// <summary>
/// Guards the repo-wide build configuration added for issue #638: the properties that live in
/// the root <c>Directory.Build.props</c> must not also be declared in an individual
/// <c>.csproj</c>.
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

    /// <summary>
    /// Property names declared in the root <c>Directory.Build.props</c>, read from the file itself
    /// so that adding a property there automatically extends the coverage below.
    /// </summary>
    private static IReadOnlyList<string> CentralizedPropertyNames() =>
        XDocument.Load(DirectoryBuildPropsPath)
            .Descendants("PropertyGroup")
            .Elements()
            .Select(e => e.Name.LocalName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

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
    public void DirectoryBuildProps_CentralizesTheSharedProperties()
    {
        var centralized = CentralizedPropertyNames();

        Assert.Contains("ImplicitUsings", centralized);
        Assert.Contains("Nullable", centralized);
        Assert.Contains("TreatWarningsAsErrors", centralized);
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
            "A property centralized in Directory.Build.props is redeclared per-project, which " +
            "silently overrides the shared value: " + string.Join("; ", offenders));
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
}
