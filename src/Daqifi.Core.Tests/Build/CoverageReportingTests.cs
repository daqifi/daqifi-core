using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Daqifi.Core.Tests.Build;

/// <summary>
/// Guards the coverage-collection decision from issue #689 and the one relationship it depends
/// on: where <c>Daqifi.Core.Tests</c> writes its coverage reports has to be where the CI
/// workflow looks for them.
/// </summary>
/// <remarks>
/// Coverage used to be produced by <c>coverlet.msbuild</c>, whose <c>GenerateCoverageResult</c>
/// target runs after the test run and could fail the job on an intermittent truncated read of
/// its own hit file - a red build with 4,063 passing tests and, on a merge-queue run, a
/// dequeued PR. Coverage is reporting-only and deliberately has no build-failing threshold
/// (issue #641), so it moved to <c>coverlet.collector</c>, which runs inside the test host and
/// has no post-test step to fail.
///
/// The cost of that move is the report path. <c>coverlet.msbuild</c> put the framework in the
/// file name; the collector always writes <c>coverage.cobertura.xml</c> under a per-run GUID
/// directory, so the framework survives only in the results directory the project chooses. That
/// makes the project and <c>.github/workflows/ci.yml</c> two halves of one arrangement, each
/// correct in isolation and useless if they disagree - the failure mode being a silent "No
/// coverage reports were produced by this run.", which by design cannot fail a build and so
/// would go unnoticed. This test is what notices.
///
/// The repository root is captured at build time as an assembly-metadata attribute; see
/// <c>Daqifi.Core.Tests.csproj</c>.
/// </remarks>
public class CoverageReportingTests
{
    /// <summary>The collector's friendly name, as VSTest knows it.</summary>
    private const string CollectorName = "XPlat Code Coverage";

    private static string RepositoryRoot =>
        Path.GetFullPath(
            typeof(CoverageReportingTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "RepositoryRoot")
                .Value!);

    private static string TestProjectPath =>
        Path.Combine(RepositoryRoot, "src", "Daqifi.Core.Tests", "Daqifi.Core.Tests.csproj");

    private static XDocument TestProject => XDocument.Load(TestProjectPath);

    private static string WorkflowText =>
        File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "ci.yml"));

    /// <summary>
    /// The value of an MSBuild property declared in the test project, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Matched case-insensitively because MSBuild property names are: a
    /// <c>&lt;collectcoverage&gt;</c> would set the same property as
    /// <c>&lt;CollectCoverage&gt;</c>, and an ordinal match would read it as absent.
    /// </remarks>
    private static string? Property(string name) =>
        TestProject
            .Descendants("PropertyGroup")
            .Elements()
            .LastOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim();

    private static HashSet<string> PackageReferences() =>
        TestProject
            .Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Coverage_IsCollectedInProcess()
    {
        // Anti-vacuity for the rest of the class: every assertion below is about the collector's
        // arrangement, and none of them mean anything if the collector is not what runs.
        Assert.Contains("coverlet.collector", PackageReferences());
        Assert.Equal(CollectorName, Property("VSTestCollect"));
    }

    [Fact]
    public void Coverage_DoesNotRunAsAPostTestMsBuildStep()
    {
        var packages = PackageReferences();

        Assert.False(packages.Contains("coverlet.msbuild"),
            "Daqifi.Core.Tests references coverlet.msbuild again. Its GenerateCoverageResult " +
            "target runs after the tests have already passed and can still fail the job " +
            "(issue #689); coverage is reporting-only and must not be able to do that.");

        // CollectCoverage is what switches that target on, so it is the property that brings the
        // failure mode back even if the package arrived transitively.
        Assert.Null(Property("CollectCoverage"));
    }

    [Fact]
    public void CoverageReports_LandWhereTheWorkflowLooksForThem()
    {
        var resultsDirectory = Property("VSTestResultsDirectory");
        Assert.NotNull(resultsDirectory);

        // Normalize the project-relative results directory to a repo-relative glob: strip the
        // $(MSBuildThisFileDirectory)../ prefix that anchors it at src/, and stand a wildcard in
        // for the framework, which expands once per inner build.
        var relative = resultsDirectory!
            .Replace("$(MSBuildThisFileDirectory)../", "src/", StringComparison.Ordinal)
            .Replace("$(TargetFramework)", "*", StringComparison.Ordinal)
            .Replace('\\', '/');

        Assert.Equal("src/coverage/*", relative);

        // The collector adds one per-run GUID directory of its own under that, then always names
        // the file coverage.cobertura.xml. This is the glob CI has to walk to recover the
        // framework from the path.
        var expectedGlob = $"{relative}/*/coverage.cobertura.xml";

        Assert.Contains(expectedGlob, WorkflowText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>sed</c> expression CI uses to read the expected frameworks, and the file it reads,
    /// lifted out of the workflow so the test runs the real thing rather than a copy of it.
    /// </summary>
    private static (string Expression, string File) FrameworkReadFromWorkflow()
    {
        var invocation = Regex.Match(WorkflowText, @"sed -n 's:(?<expression>.*?):\\1:p' (?<file>\S+)");

        Assert.True(invocation.Success,
            "The coverage step no longer reads the expected frameworks with the `sed -n 's:...:\\1:p' <project>` " +
            "form this test knows how to exercise. Either restore it or teach this test the new form - what " +
            "must not happen is CI reading the frameworks in a way nothing checks.");

        return (invocation.Groups["expression"].Value, invocation.Groups["file"].Value);
    }

    /// <summary>
    /// Translates a POSIX basic regular expression - what <c>sed</c> takes - into the equivalent
    /// .NET pattern, so the workflow's own expression can be run here.
    /// </summary>
    /// <remarks>
    /// Only the grouping difference is translated, because that is the only construct the
    /// expression uses. Anything else backslash-escaped would mean a rewrite this cannot
    /// faithfully reproduce, and quietly mistranslating it would be worse than failing.
    /// </remarks>
    private static string BasicRegexToDotNet(string expression)
    {
        var translated = expression.Replace(@"\(", "(", StringComparison.Ordinal)
                                   .Replace(@"\)", ")", StringComparison.Ordinal);

        Assert.DoesNotContain(@"\", translated, StringComparison.Ordinal);

        return translated;
    }

    [Fact]
    public void ExpectedFrameworks_StayReadableTheWayTheWorkflowReadsThem()
    {
        // CI warns about a report that went missing for one framework but not the other, and it
        // reads the list of frameworks to expect straight out of this project rather than
        // repeating it. Both halves of that can rot silently: a reformatted <TargetFrameworks>
        // or an edited sed expression would not break the build, it would just find nothing to
        // expect and warn about nothing. So run the workflow's own expression over the real
        // project file and check it still recovers the frameworks the project declares.
        var (expression, file) = FrameworkReadFromWorkflow();

        Assert.Equal("src/Daqifi.Core.Tests/Daqifi.Core.Tests.csproj", file);

        // The workflow splits the match on ';' (`tr ';' ' '`), so a change of separator has to
        // show up here too.
        Assert.Contains(@"tr ';' ' '", WorkflowText, StringComparison.Ordinal);

        var declared = TestProject
            .Descendants("PropertyGroup")
            .Elements()
            .Single(e => e.Name.LocalName == "TargetFrameworks")
            .Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var read = Regex
            .Match(File.ReadAllText(TestProjectPath), BasicRegexToDotNet(expression))
            .Groups[1]
            .Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.NotEmpty(declared);
        Assert.Equal(declared, read);
    }

    [Fact]
    public void CollectorConfiguration_IsTheOneTheProjectPointsAt()
    {
        var settingsPath = Property("RunSettingsFilePath");
        Assert.NotNull(settingsPath);

        var resolved = Path.Combine(
            RepositoryRoot,
            "src",
            "Daqifi.Core.Tests",
            settingsPath!.Replace("$(MSBuildThisFileDirectory)", string.Empty, StringComparison.Ordinal));

        Assert.True(File.Exists(resolved),
            $"Daqifi.Core.Tests points RunSettingsFilePath at '{settingsPath}', which does not " +
            "exist. VSTest ignores a missing run-settings file silently, so the collector would " +
            "fall back to its own defaults and quietly change what the reported numbers mean.");

        // The settings are only reached through the collector, so they have to name it, and the
        // format is what the summary script and the uploaded artifact both assume.
        var configuration = XDocument.Load(resolved)
            .Descendants("DataCollector")
            .Single(e => e.Attribute("friendlyName")?.Value == CollectorName);

        Assert.Equal("cobertura", configuration.Descendants("Format").Single().Value.Trim());
    }
}
