namespace Daqifi.Core.Firmware;

/// <summary>
/// A WiFi firmware package downloaded and extracted by
/// <see cref="IFirmwareDownloadService.DownloadWifiFirmwareAsync"/>.
/// </summary>
public sealed record WifiFirmwareDownloadResult
{
    /// <summary>
    /// Gets the full path of the folder the package was extracted to.
    /// </summary>
    public required string ExtractedPath { get; init; }

    /// <summary>
    /// Gets the version of the package, as its release tag spells it (for example <c>19.7.7</c>).
    /// </summary>
    public required string Version { get; init; }
}
