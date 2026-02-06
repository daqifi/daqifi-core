namespace Daqifi.Core.Firmware;

/// <summary>
/// Information about a firmware release available on GitHub.
/// </summary>
public sealed class FirmwareReleaseInfo
{
    /// <summary>
    /// The parsed firmware version.
    /// </summary>
    public required FirmwareVersion Version { get; init; }

    /// <summary>
    /// The raw tag name from the release (e.g. "v3.2.0").
    /// </summary>
    public required string TagName { get; init; }

    /// <summary>
    /// Whether this is a pre-release.
    /// </summary>
    public required bool IsPreRelease { get; init; }

    /// <summary>
    /// Release notes / body text.
    /// </summary>
    public string? ReleaseNotes { get; init; }

    /// <summary>
    /// URL to download the primary firmware asset (e.g. .hex file).
    /// </summary>
    public string? DownloadUrl { get; init; }

    /// <summary>
    /// File name of the primary firmware asset.
    /// </summary>
    public string? AssetFileName { get; init; }

    /// <summary>
    /// Size of the primary firmware asset in bytes, if known.
    /// </summary>
    public long? AssetSize { get; init; }

    /// <summary>
    /// The release publication date.
    /// </summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>
    /// URL to download the release source as a zip archive.
    /// </summary>
    public string? ZipballUrl { get; init; }
}
