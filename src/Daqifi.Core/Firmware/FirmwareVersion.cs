using System.Globalization;
using System.Text.RegularExpressions;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Represents a parsed firmware version with semantic versioning and pre-release support.
/// </summary>
public readonly record struct FirmwareVersion(
    int Major,
    int Minor,
    int Patch,
    string? PreLabel,
    int PreNumber) : IComparable<FirmwareVersion>
{
    private static readonly Regex VersionRegex = new(
        """
        (?ix)^\s* v?\s*
                  (?<maj>\d+) (?:\.(?<min>\d+))? (?:\.(?<pat>\d+))?
                  (?<suffix> [A-Za-z]+ \d* )?
                  \s*$
        """,
        RegexOptions.Compiled);

    /// <summary>
    /// Whether this version is a pre-release (alpha, beta, rc, etc.).
    /// </summary>
    public bool IsPreRelease => !string.IsNullOrEmpty(PreLabel);

    /// <inheritdoc />
    public int CompareTo(FirmwareVersion other)
    {
        var cmp = Major.CompareTo(other.Major);
        if (cmp != 0) return cmp;
        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0) return cmp;
        cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0) return cmp;

        var thisRank = GetPrecedenceRank(PreLabel);
        var otherRank = GetPrecedenceRank(other.PreLabel);
        if (thisRank != otherRank) return thisRank.CompareTo(otherRank);
        return PreNumber.CompareTo(other.PreNumber);
    }

    /// <summary>
    /// Attempts to parse a version string (e.g. "v3.2.0", "3.2.0b2", "v1.0.0rc1").
    /// </summary>
    public static bool TryParse(string? input, out FirmwareVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var m = VersionRegex.Match(input.Trim());
        if (!m.Success) return false;

        var major = int.Parse(m.Groups["maj"].Value, CultureInfo.InvariantCulture);
        var minor = int.TryParse(m.Groups["min"].Value, out var mi) ? mi : 0;
        var patch = int.TryParse(m.Groups["pat"].Value, out var pa) ? pa : 0;

        var suffix = m.Groups["suffix"].Success ? m.Groups["suffix"].Value : null;
        string? label = null;
        var preNum = 0;
        if (!string.IsNullOrEmpty(suffix))
        {
            var i = suffix.TakeWhile(char.IsLetter).Count();
            label = suffix[..i];
            var numPart = suffix[i..];
            _ = int.TryParse(numPart, out preNum);
        }

        version = new FirmwareVersion(major, minor, patch, label, preNum);
        return true;
    }

    /// <summary>
    /// Compares two version strings. Returns negative if left is older, positive if newer, zero if equal.
    /// Unparseable strings sort before valid versions.
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        var hasL = TryParse(left, out var l);
        var hasR = TryParse(right, out var r);
        if (!hasL && !hasR) return 0;
        if (!hasL) return -1;
        if (!hasR) return 1;
        return l.CompareTo(r);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var core = $"{Major}.{Minor}.{Patch}";
        if (!IsPreRelease) return core;
        return core + PreLabel + (PreNumber > 0 ? PreNumber.ToString(CultureInfo.InvariantCulture) : string.Empty);
    }

    /// <inheritdoc />
    public static bool operator <(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) < 0;
    /// <inheritdoc />
    public static bool operator >(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) > 0;
    /// <inheritdoc />
    public static bool operator <=(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) <= 0;
    /// <inheritdoc />
    public static bool operator >=(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) >= 0;

    private static int GetPrecedenceRank(string? label)
    {
        if (string.IsNullOrEmpty(label)) return 3; // release is highest
        return label.ToLowerInvariant() switch
        {
            "rc" or "releasecandidate" => 2,
            "b" or "beta" => 1,
            "a" or "alpha" or "pre" or "preview" or "dev" => 0,
            _ => 0
        };
    }
}
