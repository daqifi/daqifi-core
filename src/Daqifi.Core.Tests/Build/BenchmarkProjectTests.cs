using System.Reflection;
using System.Xml.Linq;

namespace Daqifi.Core.Tests.Build;

/// <summary>
/// Guards the two things issue #640 asked the benchmark harness to stay out of: the test path and
/// the pack path.
/// </summary>
/// <remarks>
/// Both are one MSBuild property, and both fail quietly rather than loudly if the property goes
/// missing. Without <c>IsTestProject=false</c>, CI's <c>dotnet test Daqifi.Core.sln</c> walks into
/// a console project and asks it for tests; without <c>IsPackable=false</c>, the repo-root
/// <c>Directory.Build.targets</c> starts attaching package metadata to a measuring instrument and
/// a <c>dotnet pack</c> of the solution produces a Daqifi.Core.Benchmarks package. Neither would be
/// noticed by anything else here.
/// </remarks>
public class BenchmarkProjectTests
{
    private static string RepositoryRoot =>
        Path.GetFullPath(
            typeof(BenchmarkProjectTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "RepositoryRoot")
                .Value!);

    private static string BenchmarkProjectPath =>
        Path.Combine(RepositoryRoot, "src", "Daqifi.Core.Benchmarks", "Daqifi.Core.Benchmarks.csproj");

    /// <summary>
    /// The value of a property in the benchmark project, or <see langword="null"/> if it declares
    /// none. Element names are matched case-insensitively because MSBuild property names are.
    /// </summary>
    private static string? PropertyValue(string name) =>
        XDocument.Load(BenchmarkProjectPath)
            .Descendants("PropertyGroup")
            .Elements()
            .Where(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value.Trim())
            .LastOrDefault();

    [Fact]
    public void TheBenchmarkProjectExists()
    {
        // Anti-vacuity: every assertion below reads this file, so a moved or renamed project would
        // otherwise make them fail for the wrong reason - or, if the reads were made forgiving,
        // pass by finding nothing.
        Assert.True(File.Exists(BenchmarkProjectPath),
            $"Expected the benchmark harness at {BenchmarkProjectPath} (issue #640).");
    }

    [Fact]
    public void TheBenchmarkProjectIsExcludedFromTheTestPath()
    {
        Assert.Equal("false", PropertyValue("IsTestProject"), ignoreCase: true);
    }

    [Fact]
    public void TheBenchmarkProjectIsExcludedFromThePackPath()
    {
        Assert.Equal("false", PropertyValue("IsPackable"), ignoreCase: true);
    }

    [Fact]
    public void TheBenchmarkProjectReferencesBenchmarkDotNet()
    {
        // The harness is only a harness because of this reference: without it the project would
        // still build, still be excluded from test and pack, and measure nothing.
        var references = XDocument.Load(BenchmarkProjectPath)
            .Descendants()
            .Where(e => e.Name.LocalName.Equals("PackageReference", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Attributes()
                .FirstOrDefault(a => a.Name.LocalName.Equals("Include", StringComparison.OrdinalIgnoreCase))?.Value)
            .Where(id => id is not null);

        Assert.Contains("BenchmarkDotNet", references);
    }
}
