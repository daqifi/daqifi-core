using System.Reflection;
using System.Xml.Linq;

namespace Daqifi.Core.Tests.Build;

/// <summary>
/// Guards the target-framework decision from issue #643: the <c>Daqifi.Mcp</c> dotnet tool
/// must ship an asset for every framework <c>Daqifi.Core</c> supports, and its test project
/// must cover every framework the tool ships.
/// </summary>
/// <remarks>
/// The asymmetry this prevents already shipped. <c>Daqifi.Mcp</c> targeted <c>net9.0</c> alone
/// while <c>Daqifi.Core</c> multi-targeted <c>net9.0;net10.0</c>, so the published tool package
/// carried a single <c>tools/net9.0/any</c> asset: a machine with only the .NET 10 runtime had
/// to install .NET 9 as well before <c>dotnet tool install -g Daqifi.Mcp</c> would run, even
/// though the library underneath already supported .NET 10. Nothing in the build noticed —
/// each project's framework list is correct in isolation, and only the relationship between
/// them is wrong, which is exactly what no compiler checks.
///
/// The frameworks stay declared per-project rather than centralized in
/// <c>Directory.Build.props</c> (see the comment there), so this test is what keeps them in
/// step. The repository root is captured at build time as an assembly-metadata attribute; see
/// <c>Daqifi.Core.Tests.csproj</c>.
/// </remarks>
public class TargetFrameworkTests
{
    private static string RepositoryRoot =>
        Path.GetFullPath(
            typeof(TargetFrameworkTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "RepositoryRoot")
                .Value!);

    private static string ProjectPath(string project) =>
        Path.Combine(RepositoryRoot, "src", project, $"{project}.csproj");

    /// <summary>
    /// The target frameworks a project declares, from either <c>&lt;TargetFramework&gt;</c> or
    /// <c>&lt;TargetFrameworks&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Element names are matched case-insensitively because MSBuild property names are:
    /// <c>&lt;targetframeworks&gt;</c> sets the same property as <c>&lt;TargetFrameworks&gt;</c>,
    /// and a project that used the lower-case spelling would otherwise read as declaring no
    /// frameworks at all and pass every assertion below vacuously.
    /// </remarks>
    private static HashSet<string> TargetFrameworksOf(string project) =>
        TargetFrameworksIn(XDocument.Load(ProjectPath(project)));

    /// <summary>
    /// The parsing half of <see cref="TargetFrameworksOf"/>, split out so a test can feed it an
    /// in-memory project rather than reimplementing the query against one.
    /// </summary>
    private static HashSet<string> TargetFrameworksIn(XDocument project)
    {
        var properties = project
            .Descendants("PropertyGroup")
            .Elements()
            .Where(e => e.Name.LocalName.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase)
                     || e.Name.LocalName.Equals("TargetFrameworks", StringComparison.OrdinalIgnoreCase));

        return properties
            .SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string Describe(IEnumerable<string> frameworks) =>
        string.Join(";", frameworks.OrderBy(f => f, StringComparer.Ordinal));

    [Fact]
    public void DaqifiCore_DeclaresItsTargetFrameworks()
    {
        // Anti-vacuity: every comparison below is against this set, so a parse that silently
        // returned nothing would make the whole class pass by finding nothing.
        var core = TargetFrameworksOf("Daqifi.Core");

        Assert.NotEmpty(core);
        Assert.Contains("net9.0", core);
    }

    [Fact]
    public void DaqifiMcp_ShipsAnAssetForEveryFrameworkTheLibrarySupports()
    {
        // The point of #643. A `dotnet tool` package carries one self-contained asset folder
        // per target framework and the shim picks the highest the machine can run, so a
        // framework missing here is a machine that cannot run the tool without installing an
        // older runtime.
        var core = TargetFrameworksOf("Daqifi.Core");
        var mcp = TargetFrameworksOf("Daqifi.Mcp");

        var missing = core.Except(mcp, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.True(missing.Count == 0,
            $"Daqifi.Core targets {Describe(core)} but the Daqifi.Mcp tool ships only " +
            $"{Describe(mcp)}. A user whose machine has only {Describe(missing)} would have to " +
            "install an older runtime to run `daqifi-mcp` (issue #643).");
    }

    [Fact]
    public void DaqifiMcpTests_CoverEveryFrameworkTheToolShips()
    {
        // Otherwise the tool would publish an asset for a framework nothing ever ran.
        var mcp = TargetFrameworksOf("Daqifi.Mcp");
        var tests = TargetFrameworksOf("Daqifi.Mcp.Tests");

        var untested = mcp.Except(tests, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.True(untested.Count == 0,
            $"Daqifi.Mcp ships assets for {Describe(mcp)} but Daqifi.Mcp.Tests runs on " +
            $"{Describe(tests)}, leaving {Describe(untested)} shipped untested.");
    }

    [Fact]
    public void DaqifiCoreTests_CoverEveryFrameworkTheLibrarySupports()
    {
        var core = TargetFrameworksOf("Daqifi.Core");
        var tests = TargetFrameworksOf("Daqifi.Core.Tests");

        var untested = core.Except(tests, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.True(untested.Count == 0,
            $"Daqifi.Core targets {Describe(core)} but Daqifi.Core.Tests runs on " +
            $"{Describe(tests)}, leaving {Describe(untested)} shipped untested.");
    }

    [Theory]
    [InlineData("<Project><PropertyGroup><targetframeworks>net9.0;net10.0</targetframeworks></PropertyGroup></Project>")]
    [InlineData("<Project><PropertyGroup><TARGETFRAMEWORKS>net9.0;net10.0</TARGETFRAMEWORKS></PropertyGroup></Project>")]
    [InlineData("<Project><PropertyGroup><targetframework>net9.0</targetframework><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")]
    public void TargetFrameworkElements_AreMatchedCaseInsensitively(string project)
    {
        // Runs the real parser - not a copy of its query - over an in-memory project, so
        // switching TargetFrameworksIn to an ordinal element-name match fails here rather than
        // quietly making every lower-cased project read as targeting nothing, which would let
        // the relationship checks above pass vacuously.
        var frameworks = TargetFrameworksIn(XDocument.Parse(project));

        Assert.Equal("net10.0;net9.0", Describe(frameworks));
    }
}
