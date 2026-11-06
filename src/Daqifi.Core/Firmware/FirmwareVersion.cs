using System.Globalization;
using System.Text.RegularExpressions;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Represents a parsed firmware version with semantic versioning and pre-release support.
/// </summary>
/// <remarks>
/// Equality is the same ranking <see cref="CompareTo"/> applies, not a string match on
/// <see cref="PreLabel"/>. A pre-release label has aliases — <c>b</c> and <c>beta</c> name one
/// label, as do <c>rc</c> and <c>releasecandidate</c> — and the alias is also case-insensitive,
/// so <c>3.2.0b1</c>, <c>3.2.0beta1</c> and <c>3.2.0B1</c> are one version: they are equal, they
/// have the same hash code, and they collapse to a single entry in a
/// <see cref="System.Collections.Generic.HashSet{T}"/>, a dictionary key or
/// <c>Distinct()</c>. A label the type does not recognize carries no rank of its own, so two
/// unrecognized labels with the same number are equal too. <see cref="PreLabel"/> still
/// reports the raw label text the device sent, and so does <see cref="ToString"/>, which
/// normalizes only the leading <c>v</c> and the separator before the label: <c>3.8.0-rc1</c>
/// prints as <c>3.8.0rc1</c>.
/// </remarks>
public readonly record struct FirmwareVersion(
    int Major,
    int Minor,
    int Patch,
    string? PreLabel,
    int PreNumber) : IComparable<FirmwareVersion>
{
    // The shapes that reach here come from two places: a GitHub release tag, and whatever a
    // device reports in DeviceFwRev. Both use a fourth numeric part for a build number
    // ("3.8.0.1"), and both separate a pre-release label with a hyphen ("3.8.0-rc1") as often as
    // they run it straight on ("3.8.0rc1"). Every shape that parses is one fewer device in the
    // unknown state, where callers must fail closed.
    private static readonly Regex VersionRegex = new(
        """
        (?ix)^\s* v?\s*
                  (?<maj>\d+) (?:\.(?<min>\d+))? (?:\.(?<pat>\d+))? (?:\.(?<rev>\d+))?
                  (?: [-._]? (?<label> [A-Za-z]+ ) [-._]? (?<prenum> \d+ )? )?
                  \s*$
        """,
        RegexOptions.Compiled);

    /// <summary>
    /// Whether this version is a pre-release (alpha, beta, rc, etc.).
    /// </summary>
    public bool IsPreRelease => !string.IsNullOrEmpty(PreLabel);

    /// <summary>
    /// The fourth, build-number part of a four-part version ("3.8.0.1" has a revision of 1).
    /// Zero for the three-part versions the firmware releases use.
    /// </summary>
    /// <remarks>
    /// This is a separate member rather than a fifth positional parameter, because the primary
    /// constructor and <c>Deconstruct</c> of this record struct are shipped public API and
    /// adding a parameter to either would break every caller.
    /// </remarks>
    public int Revision { get; init; }

    /// <inheritdoc />
    public int CompareTo(FirmwareVersion other)
    {
        var cmp = Major.CompareTo(other.Major);
        if (cmp != 0) return cmp;
        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0) return cmp;
        cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0) return cmp;
        cmp = Revision.CompareTo(other.Revision);
        if (cmp != 0) return cmp;

        var thisRank = GetPrecedenceRank(PreLabel);
        var otherRank = GetPrecedenceRank(other.PreLabel);
        if (thisRank != otherRank) return thisRank.CompareTo(otherRank);
        return PreNumber.CompareTo(other.PreNumber);
    }

    /// <summary>
    /// Compares two versions by the same ranked key <see cref="CompareTo"/> uses, so a zero
    /// <see cref="CompareTo"/> result always means the two versions are equal. The compiler-
    /// generated equality would instead match <see cref="PreLabel"/> as a raw string, which
    /// made two spellings of one label (<c>3.2.0b1</c> and <c>3.2.0beta1</c>) unequal while
    /// <see cref="CompareTo"/> ranked them the same.
    /// </summary>
    /// <param name="other">The version to compare against.</param>
    /// <returns><c>true</c> when both versions name the same release.</returns>
    public bool Equals(FirmwareVersion other) => CompareTo(other) == 0;

    /// <summary>
    /// Hashes the same ranked key <see cref="Equals(FirmwareVersion)"/> compares, so every
    /// spelling of one label hashes alike.
    /// </summary>
    /// <returns>A hash code consistent with <see cref="Equals(FirmwareVersion)"/>.</returns>
    public override int GetHashCode() =>
        HashCode.Combine(Major, Minor, Patch, Revision, GetPrecedenceRank(PreLabel), PreNumber);

    /// <summary>
    /// Attempts to parse a version string (e.g. "v3.2.0", "3.2.0b2", "v1.0.0rc1",
    /// "3.8.0-rc1", "3.8.0.1").
    /// </summary>
    public static bool TryParse(string? input, out FirmwareVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var m = VersionRegex.Match(input.Trim());
        if (!m.Success) return false;

        // The regex matches an unbounded digit run per component, so a value that overflows Int32
        // (e.g. a corrupted/spoofed DeviceFwRev wire string) must NOT throw — a TryParse honors its
        // no-throw contract and reports an out-of-range version as unparseable so callers fail
        // closed on it. All three components are handled symmetrically: an ABSENT optional
        // minor/patch defaults to 0 (so "3" == 3.0.0), but a PRESENT component that overflows
        // rejects the whole version rather than silently truncating to 0.
        if (!TryParseComponent(m.Groups["maj"].Value, out var major)
            || !TryParseComponent(m.Groups["min"].Value, out var minor)
            || !TryParseComponent(m.Groups["pat"].Value, out var patch)
            || !TryParseComponent(m.Groups["rev"].Value, out var revision))
        {
            return false;
        }

        var label = m.Groups["label"].Success ? m.Groups["label"].Value : null;
        _ = int.TryParse(
            m.Groups["prenum"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var preNum);

        version = new FirmwareVersion(major, minor, patch, label, preNum) { Revision = revision };
        return true;
    }

    /// <summary>
    /// Parses one numeric version component. An empty value (an absent optional minor, patch or
    /// revision group) yields 0; a non-empty value must fit <see cref="int"/> — one that overflows
    /// makes the whole version unparseable (returns false) rather than silently truncating to 0.
    /// </summary>
    private static bool TryParseComponent(string value, out int result)
    {
        result = 0;
        return value.Length == 0
            || int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
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
        if (Revision != 0) core += "." + Revision.ToString(CultureInfo.InvariantCulture);
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
