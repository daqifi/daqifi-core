using System.Reflection;
using System.Text;

namespace Daqifi.Core.Tests.TestSupport;

/// <summary>
/// Finds the lines of <c>Daqifi.Core</c> source that actually raise an
/// <see cref="ArgumentOutOfRangeException"/>, so a guard census can be checked against the source
/// rather than against itself.
/// </summary>
/// <remarks>
/// <para>
/// A census that only walks the guards it already lists cannot notice a guard nobody listed. That
/// is the hole this closes: the census declares which throw sites it covers, and the scan reads
/// the folder and says which throw sites exist. A new guard landing in a censused folder without a
/// census entry turns the comparison red.
/// </para>
/// <para>
/// First written inside <c>ExportGuardCensusTests</c> for issue #664's Logging/Export slice; lifted
/// here when the Channel slice needed the same scan, rather than copied. Every census past the
/// first shares this one implementation, so a fix to the matching rules — such as the narrowing
/// that stopped it counting <c>catch</c> and <c>typeof</c> mentions — reaches all of them.
/// </para>
/// </remarks>
internal static class RangeGuardSourceScanner
{
    /// <summary>
    /// The repository this test assembly was built from, taken from the <c>RepositoryRoot</c>
    /// assembly-metadata attribute that <c>Directory.Build.props</c> stamps in. Reading source from
    /// a test needs a path that does not depend on where the test binary happens to run from;
    /// <c>DirectoryBuildPropsTests</c> set the precedent.
    /// </summary>
    internal static string RepositoryRoot =>
        Path.GetFullPath(
            typeof(RangeGuardSourceScanner).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "RepositoryRoot")
                .Value!);

    /// <summary>
    /// The absolute path of a folder under <c>src/Daqifi.Core</c>, named by its path segments —
    /// for example <c>SourceDirectory("Logging", "Export")</c>.
    /// </summary>
    internal static string SourceDirectory(params string[] segments) =>
        Path.Combine([RepositoryRoot, "src", "Daqifi.Core", .. segments]);

    /// <summary>
    /// Every line under <paramref name="directory"/> that raises an
    /// <see cref="ArgumentOutOfRangeException"/> — the <c>throw new</c> form and the
    /// <c>ArgumentOutOfRangeException.ThrowIf*</c> helpers alike, so switching a guard to a helper
    /// does not slip it past a census. Subdirectories are included, so a guard cannot escape by
    /// moving one folder down.
    /// </summary>
    /// <returns>
    /// The throw sites, each named by its path relative to <paramref name="directory"/> with
    /// forward slashes, and its 1-based line number.
    /// </returns>
    /// <remarks>
    /// Matched narrowly on purpose. A mere mention of the type — <c>catch</c>, <c>typeof</c>, an
    /// XML-doc <c>&lt;exception&gt;</c> tag — is not a guard, and counting one would fail a census
    /// for a change that added no guard at all. A drift test that cries wolf is a drift test people
    /// switch off. Line and block comments are skipped for the same reason.
    /// </remarks>
    internal static IReadOnlyList<(string File, int Line)> ThrowSitesIn(string directory)
    {
        var sites = new List<(string File, int Line)>();

        var paths = Directory
            .GetFiles(directory, "*.cs", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var name = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
            var lines = File.ReadAllLines(path);
            var inBlockComment = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var code = StripComments(lines[i], ref inBlockComment);

                if (code.Contains($"throw new {nameof(ArgumentOutOfRangeException)}", StringComparison.Ordinal)
                    || code.Contains($"{nameof(ArgumentOutOfRangeException)}.ThrowIf", StringComparison.Ordinal))
                {
                    sites.Add((name, i + 1));
                }
            }
        }

        return sites;
    }

    /// <summary>
    /// Renders throw sites as one comparable line per file — <c>"File.cs: 2"</c> — so a census
    /// mismatch reads as which file gained or lost a guard rather than as two lists of line
    /// numbers. Files with no guards appear on neither side.
    /// </summary>
    internal static string SummarizeByFile(IEnumerable<string> files) =>
        string.Join(
            "; ",
            files
                .GroupBy(f => f, StringComparer.Ordinal)
                .Select(g => $"{g.Key}: {g.Count()}")
                .OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>
    /// Returns the code on <paramref name="line"/> with its comments removed, carrying
    /// <paramref name="inBlockComment"/> across lines. Deliberately naive — it does not understand
    /// string literals containing comment markers — which is safe here because the only thing done
    /// with the result is looking for a <c>throw</c>, and a literal spelling one out would be a
    /// stranger thing than this missing it.
    /// </summary>
    private static string StripComments(string line, ref bool inBlockComment)
    {
        var code = new StringBuilder();

        for (var i = 0; i < line.Length; i++)
        {
            if (inBlockComment)
            {
                if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                break;
            }

            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            code.Append(line[i]);
        }

        return code.ToString();
    }
}
