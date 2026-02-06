namespace Daqifi.Core.Firmware;

/// <summary>
/// Service for checking and downloading firmware from a release repository.
/// </summary>
public interface IFirmwareDownloadService
{
    /// <summary>
    /// Gets the latest firmware release information without downloading.
    /// </summary>
    /// <param name="includePreRelease">Whether to include pre-release versions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The latest release info, or null if none found.</returns>
    Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(
        bool includePreRelease = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether an update is available for the given device firmware version.
    /// </summary>
    /// <param name="deviceVersionString">The device's current firmware version string.</param>
    /// <param name="includePreRelease">Whether to include pre-release versions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
        string deviceVersionString,
        bool includePreRelease = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the firmware .hex file for the latest release to the specified directory.
    /// </summary>
    /// <param name="destinationDirectory">Directory to save the file to. Created if it doesn't exist.</param>
    /// <param name="includePreRelease">Whether to include pre-release versions.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The full path to the downloaded file, or null if no release/asset found.</returns>
    Task<string?> DownloadLatestFirmwareAsync(
        string destinationDirectory,
        bool includePreRelease = false,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a firmware file from a specific release by tag name.
    /// </summary>
    /// <param name="tagName">The release tag (e.g. "v3.2.0").</param>
    /// <param name="destinationDirectory">Directory to save the file to.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The full path to the downloaded file, or null if not found.</returns>
    Task<string?> DownloadFirmwareByTagAsync(
        string tagName,
        string destinationDirectory,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads and extracts a WiFi firmware package (.zip) for the latest release.
    /// </summary>
    /// <param name="destinationDirectory">Directory to extract files to.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuple of (extracted folder path, version string), or null if not found.</returns>
    Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
        string destinationDirectory,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates the cached release data, forcing the next call to query the API.
    /// </summary>
    void InvalidateCache();
}
