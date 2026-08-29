using System.Reflection;
using System.Xml.Linq;

namespace Daqifi.Core.Tests.Build;

/// <summary>
/// Guards the code-style enforcement added for issue #484: the
/// <c>EnforceCodeStyleInBuild</c> property in the root <c>Directory.Build.props</c> and the two
/// rule severities in the root <c>.editorconfig</c> that it makes effective.
/// </summary>
/// <remarks>
/// <para>
/// The analyzers are the real guard - IDE0161 and CA1707 fail the build on a violation. What
/// they cannot guard is their own wiring, and this pair has an unusually quiet way of coming
/// undone. Drop <c>EnforceCodeStyleInBuild</c> and the IDE* rules stop running outside an
/// editor; set either severity to <c>none</c> or <c>silent</c> and the rule keeps running but
/// stops reporting. Neither turns anything red: the build goes green with the style unpoliced,
/// and the drift the issue documented resumes one cloned file at a time.
/// </para>
/// <para>
/// Redeclaration is not re-checked here. <see cref="DirectoryBuildPropsTests"/> already asserts
/// that no <c>.csproj</c> redeclares a property centralized at the repo root, and it reads the
/// property names out of <c>Directory.Build.props</c> itself, so <c>EnforceCodeStyleInBuild</c>
/// joined that coverage the moment it was added there.
/// </para>
/// <para>
/// Two candidate tests are deliberately absent for failing the repo's bar that a guard must be
/// able to fail on a <b>green</b> build. One asserted that no source file uses a block-scoped
/// namespace: with IDE0161 wired the compiler reaches every file it would scan, and reaches it
/// first. The other asserted that <c>csharp_style_namespace_declarations</c> says
/// <c>file_scoped</c>: every way of changing that answer - flipping it to <c>block_scoped</c>,
/// or deleting the line and falling back to the <c>block_scoped</c> default - turns the build
/// red on the whole repo instead. Both would only have restated a build error.
/// </para>
/// </remarks>
public class CodeStyleEnforcementTests
{
    private static string RepositoryRoot =>
        Path.GetFullPath(
            typeof(CodeStyleEnforcementTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "RepositoryRoot")
                .Value!);

    private static string DirectoryBuildPropsPath =>
        Path.Combine(RepositoryRoot, "Directory.Build.props");

    private static string EditorConfigPath => Path.Combine(RepositoryRoot, ".editorconfig");

    /// <summary>
    /// The severities a rule is given in <c>.editorconfig</c>, across every section that mentions
    /// it. Returned as a list rather than a single value because a rule may legitimately be
    /// configured more than once - RS0041 is repo-wide by default and scoped to <c>none</c> for
    /// one generated file - and a second entry that silences a rule everywhere has to be visible
    /// to the assertions rather than hidden behind a "first match wins" read.
    /// </summary>
    private static IReadOnlyList<string> SeveritiesOf(string ruleId) =>
        File.ReadAllLines(EditorConfigPath)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith($"dotnet_diagnostic.{ruleId}.severity", StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..].Trim())
            .ToList();

    [Fact]
    public void DirectoryBuildProps_EnablesCodeStyleEnforcementInTheBuild()
    {
        // Without this the IDE* severities in .editorconfig are advisory: a violation is a
        // squiggle for whoever opens the file and nothing at all in CI.
        var value = XDocument.Load(DirectoryBuildPropsPath)
            .Descendants("PropertyGroup")
            .Elements()
            .Where(e => e.Name.LocalName.Equals("EnforceCodeStyleInBuild", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value.Trim())
            .SingleOrDefault();

        Assert.True(
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase),
            "Directory.Build.props no longer sets EnforceCodeStyleInBuild=true, so the IDE* code "
            + $"style rules do not run during the build (found: {value ?? "no declaration"}).");
    }

    [Theory]
    // IDE0161: file-scoped namespaces, repo-wide.
    [InlineData("IDE0161")]
    // CA1707: no underscores in public member names, scoped to Daqifi.Core.
    [InlineData("CA1707")]
    public void EditorConfig_GivesTheStyleRuleABuildFailingSeverity(string ruleId)
    {
        var severities = SeveritiesOf(ruleId);

        Assert.True(severities.Count > 0,
            $"The root .editorconfig no longer configures {ruleId}, so it falls back to its "
            + "default severity and stops failing the build.");

        // 'warning' is what TreatWarningsAsErrors turns into a failure; 'error' would do as well.
        // 'none', 'silent' and 'suggestion' all leave the build green on a violation, which is
        // the same as not having the rule.
        Assert.All(severities, severity =>
            Assert.True(
                severity is "warning" or "error",
                $"{ruleId} is configured as '{severity}' in .editorconfig. Only 'warning' or "
                + "'error' fail the build; anything else silently stops enforcing it."));
    }
}
