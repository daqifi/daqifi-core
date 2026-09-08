using System.Reflection;

namespace Daqifi.Mcp;

/// <summary>
/// What version of this server is running, and how that compares with what is published on
/// nuget.org (issue #727).
/// </summary>
/// <remarks>
/// A stale install is not a cosmetic problem here: the tool surface grows release to release, so
/// an old <c>Daqifi.Mcp</c> is missing whole capabilities rather than being merely behind. An
/// agent driving one draws the reasonable — and wrong — conclusion that the product cannot do
/// what the missing tools do. Nothing signalled that before, so this type exists to let the
/// server answer "which version am I, and is it current?".
/// </remarks>
public static class ServerVersion
{
    /// <summary>The NuGet package this server ships as.</summary>
    public const string PackageId = "Daqifi.Mcp";

    /// <summary>The command that brings a global tool install up to date.</summary>
    public const string UpdateCommand = "dotnet tool update -g " + PackageId;

    /// <summary>
    /// The running version, e.g. <c>1.7.0</c>.
    /// </summary>
    /// <remarks>
    /// Read from the assembly's informational version, which is where the release build's
    /// <c>-p:Version=</c> ends up. SourceLink appends <c>+&lt;commit sha&gt;</c> to it; that
    /// suffix is trimmed so the value is directly comparable with a NuGet version string. A
    /// build with no version stamped in (a plain local <c>dotnet run</c>) reports the SDK's
    /// <c>1.0.0</c> default, which is honest — that build really is not a released version.
    /// </remarks>
    public static string Current { get; } = ReadCurrent();

    private static string ReadCurrent()
    {
        var informational = typeof(ServerVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(ServerVersion).Assembly.GetName().Version?.ToString() ?? "unknown";
        }

        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }

    /// <summary>
    /// The highest stable version in <paramref name="versions"/>, or null if none of them parses.
    /// </summary>
    /// <remarks>
    /// Pre-release versions are skipped outright: nobody should be nudged from a stable install
    /// onto a <c>-beta</c>, and <c>dotnet tool update</c> would not install one anyway.
    /// </remarks>
    public static string? SelectLatestStable(IEnumerable<string?> versions)
    {
        string? best = null;
        Version? bestParsed = null;

        foreach (var candidate in versions)
        {
            if (candidate is null || !TryParse(candidate, out var parsed, out var prerelease) || prerelease)
            {
                continue;
            }

            if (bestParsed is null || parsed > bestParsed)
            {
                best = candidate;
                bestParsed = parsed;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is a later version than <paramref name="current"/>.
    /// </summary>
    /// <remarks>
    /// Returns false when either side is unparseable, so an unrecognised version string can never
    /// produce a spurious "you are out of date". A pre-release counts as older than the release
    /// that shares its numbers, which is the one place this differs from a plain numeric compare.
    /// </remarks>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (candidate is null || current is null
            || !TryParse(candidate, out var candidateVersion, out var candidatePrerelease)
            || !TryParse(current, out var currentVersion, out var currentPrerelease))
        {
            return false;
        }

        if (candidateVersion != currentVersion)
        {
            return candidateVersion > currentVersion;
        }

        // Same numbers: 1.7.0 is newer than 1.7.0-rc.1, and nothing else here is.
        return currentPrerelease && !candidatePrerelease;
    }

    /// <summary>
    /// Splits a semantic version into its numeric core and whether it carries a pre-release tag.
    /// Build metadata (<c>+sha</c>) is discarded; it never affects precedence.
    /// </summary>
    private static bool TryParse(string version, out Version parsed, out bool prerelease)
    {
        parsed = null!;
        prerelease = false;

        var core = version.Trim();
        var plus = core.IndexOf('+');
        if (plus >= 0)
        {
            core = core[..plus];
        }

        var dash = core.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = true;
            core = core[..dash];
        }

        if (!Version.TryParse(core, out var raw))
        {
            return false;
        }

        // Version treats an omitted component as -1, so 1.7.0 and 1.7.0.0 would not compare equal.
        parsed = new Version(raw.Major, raw.Minor, Math.Max(raw.Build, 0), Math.Max(raw.Revision, 0));
        return true;
    }
}
