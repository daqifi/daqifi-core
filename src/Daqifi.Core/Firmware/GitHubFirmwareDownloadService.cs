using System.IO.Compression;
using System.Text.Json;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Downloads firmware releases from GitHub Releases API.
/// </summary>
public sealed class GitHubFirmwareDownloadService : IFirmwareDownloadService
{
    private const string DEFAULT_USER_AGENT = "DaqifiFirmwareUpdater/1.0";
    private const int DOWNLOAD_BUFFER_SIZE = 8192;

    private readonly HttpClient _httpClient;
    private readonly string _firmwareRepoApiUrl;
    private readonly string _wifiRepoApiUrl;
    private readonly TimeSpan _cacheTtl;

    private List<JsonElement>? _cachedReleases;
    private DateTime _cacheTimestamp;

    private List<JsonElement>? _cachedWifiReleases;
    private DateTime _wifiCacheTimestamp;

    /// <summary>
    /// Creates a new firmware download service.
    /// </summary>
    /// <param name="httpClient">HttpClient to use for API calls. Caller is responsible for lifetime.</param>
    /// <param name="firmwareRepo">GitHub repository in "owner/repo" format for main firmware. Defaults to "daqifi/daqifi-nyquist-firmware".</param>
    /// <param name="wifiRepo">GitHub repository for WiFi firmware. Defaults to "daqifi/winc1500-Manual-UART-Firmware-Update".</param>
    /// <param name="cacheTtl">How long to cache release data. Defaults to 60 minutes.</param>
    public GitHubFirmwareDownloadService(
        HttpClient httpClient,
        string firmwareRepo = "daqifi/daqifi-nyquist-firmware",
        string wifiRepo = "daqifi/winc1500-Manual-UART-Firmware-Update",
        TimeSpan? cacheTtl = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _firmwareRepoApiUrl = $"https://api.github.com/repos/{firmwareRepo}/releases";
        _wifiRepoApiUrl = $"https://api.github.com/repos/{wifiRepo}/releases";
        _cacheTtl = cacheTtl ?? TimeSpan.FromMinutes(60);

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(DEFAULT_USER_AGENT);
        }
    }

    /// <inheritdoc />
    public async Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(
        bool includePreRelease = false,
        CancellationToken cancellationToken = default)
    {
        var releases = await GetFirmwareReleasesAsync(cancellationToken);
        return FindLatestRelease(releases, includePreRelease, ".hex");
    }

    /// <inheritdoc />
    public async Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
        string deviceVersionString,
        bool includePreRelease = false,
        CancellationToken cancellationToken = default)
    {
        var hasCurrent = FirmwareVersion.TryParse(deviceVersionString, out var deviceVersion);
        var latest = await GetLatestReleaseAsync(includePreRelease, cancellationToken);

        if (latest == null)
        {
            return new FirmwareUpdateCheckResult
            {
                UpdateAvailable = false,
                DeviceVersion = hasCurrent ? deviceVersion : null,
                LatestRelease = null
            };
        }

        var updateAvailable = !hasCurrent || latest.Version > deviceVersion;

        return new FirmwareUpdateCheckResult
        {
            UpdateAvailable = updateAvailable,
            DeviceVersion = hasCurrent ? deviceVersion : null,
            LatestRelease = latest
        };
    }

    /// <inheritdoc />
    public async Task<string?> DownloadLatestFirmwareAsync(
        string destinationDirectory,
        bool includePreRelease = false,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var release = await GetLatestReleaseAsync(includePreRelease, cancellationToken);
        if (release?.DownloadUrl == null || release.AssetFileName == null) return null;

        return await DownloadFileAsync(
            release.DownloadUrl, destinationDirectory, release.AssetFileName,
            release.AssetSize, progress, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string?> DownloadFirmwareByTagAsync(
        string tagName,
        string destinationDirectory,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var releases = await GetFirmwareReleasesAsync(cancellationToken);

        FirmwareReleaseInfo? release = null;
        foreach (var element in releases)
        {
            var tag = element.GetProperty("tag_name").GetString()?.Trim();
            if (!string.Equals(tag, tagName, StringComparison.OrdinalIgnoreCase)) continue;

            release = ParseReleaseElement(element, ".hex");
            break;
        }

        if (release?.DownloadUrl == null || release.AssetFileName == null) return null;

        return await DownloadFileAsync(
            release.DownloadUrl, destinationDirectory, release.AssetFileName,
            release.AssetSize, progress, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
        string destinationDirectory,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var releases = await GetWifiReleasesAsync(cancellationToken);
        var release = FindLatestRelease(releases, includePreRelease: false, assetExtension: null);
        if (release == null) return null;

        var zipballUrl = GetZipballUrl(releases, release.TagName);
        if (zipballUrl == null) return null;

        progress?.Report(0);

        Directory.CreateDirectory(destinationDirectory);
        var zipFileName = $"wifi-firmware-{release.TagName}.zip";
        var zipFilePath = Path.Combine(destinationDirectory, zipFileName);

        // Download the zipball
        using var response = await _httpClient.GetAsync(zipballUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var fileStream = new FileStream(zipFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[DOWNLOAD_BUFFER_SIZE];
            long bytesRead = 0;
            int read;
            while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                bytesRead += read;
                if (totalBytes > 0)
                {
                    progress?.Report((int)((double)bytesRead / totalBytes * 80));
                }
            }
        }

        progress?.Report(85);

        // Extract
        var extractPath = Path.Combine(destinationDirectory, $"wifi-firmware-{release.TagName}");
        if (Directory.Exists(extractPath))
        {
            Directory.Delete(extractPath, true);
        }

        ZipFile.ExtractToDirectory(zipFilePath, extractPath);
        progress?.Report(100);

        return (extractPath, release.TagName);
    }

    /// <inheritdoc />
    public void InvalidateCache()
    {
        _cachedReleases = null;
        _cachedWifiReleases = null;
    }

    private async Task<List<JsonElement>> GetFirmwareReleasesAsync(CancellationToken cancellationToken)
    {
        if (_cachedReleases != null && DateTime.UtcNow - _cacheTimestamp < _cacheTtl)
        {
            return _cachedReleases;
        }

        var elements = await FetchReleasesFromApiAsync(_firmwareRepoApiUrl, cancellationToken);
        _cachedReleases = elements;
        _cacheTimestamp = DateTime.UtcNow;
        return elements;
    }

    private async Task<List<JsonElement>> GetWifiReleasesAsync(CancellationToken cancellationToken)
    {
        if (_cachedWifiReleases != null && DateTime.UtcNow - _wifiCacheTimestamp < _cacheTtl)
        {
            return _cachedWifiReleases;
        }

        var elements = await FetchReleasesFromApiAsync(_wifiRepoApiUrl, cancellationToken);
        _cachedWifiReleases = elements;
        _wifiCacheTimestamp = DateTime.UtcNow;
        return elements;
    }

    private async Task<List<JsonElement>> FetchReleasesFromApiAsync(string apiUrl, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(apiUrl, cancellationToken);

        if ((int)response.StatusCode == 403 && response.Headers.Contains("X-RateLimit-Reset"))
        {
            var resetValue = response.Headers.GetValues("X-RateLimit-Reset").FirstOrDefault();
            if (long.TryParse(resetValue, out var resetUnix))
            {
                var resetTime = DateTimeOffset.FromUnixTimeSeconds(resetUnix).UtcDateTime;
                throw new HttpRequestException(
                    $"GitHub API rate limit exceeded. Resets at {resetTime:u}.");
            }
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        var elements = new List<JsonElement>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            elements.Add(element.Clone());
        }

        return elements;
    }

    private static FirmwareReleaseInfo? FindLatestRelease(
        List<JsonElement> releases,
        bool includePreRelease,
        string? assetExtension)
    {
        FirmwareReleaseInfo? best = null;

        foreach (var element in releases)
        {
            if (element.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean())
                continue;

            var isPreRelease = element.TryGetProperty("prerelease", out var preProp) && preProp.GetBoolean();
            if (!includePreRelease && isPreRelease)
                continue;

            var release = ParseReleaseElement(element, assetExtension);
            if (release == null) continue;

            if (best == null || release.Version > best.Version)
            {
                best = release;
            }
        }

        return best;
    }

    private static FirmwareReleaseInfo? ParseReleaseElement(JsonElement element, string? assetExtension)
    {
        var tagName = element.GetProperty("tag_name").GetString()?.Trim();
        if (string.IsNullOrEmpty(tagName)) return null;

        if (!FirmwareVersion.TryParse(tagName, out var version)) return null;

        var isPreRelease = element.TryGetProperty("prerelease", out var preProp) && preProp.GetBoolean();
        var releaseNotes = element.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : null;

        DateTimeOffset? publishedAt = null;
        if (element.TryGetProperty("published_at", out var pubProp))
        {
            var pubStr = pubProp.GetString();
            if (pubStr != null && DateTimeOffset.TryParse(pubStr, out var parsed))
            {
                publishedAt = parsed;
            }
        }

        // Find matching asset
        string? downloadUrl = null;
        string? assetFileName = null;
        long? assetSize = null;

        if (assetExtension != null && element.TryGetProperty("assets", out var assetsProp))
        {
            foreach (var asset in assetsProp.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                if (name != null && name.EndsWith(assetExtension, StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    assetFileName = name;
                    assetSize = asset.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : null;
                    break;
                }
            }
        }

        return new FirmwareReleaseInfo
        {
            Version = version,
            TagName = tagName,
            IsPreRelease = isPreRelease,
            ReleaseNotes = releaseNotes,
            DownloadUrl = downloadUrl,
            AssetFileName = assetFileName,
            AssetSize = assetSize,
            PublishedAt = publishedAt
        };
    }

    private static string? GetZipballUrl(List<JsonElement> releases, string tagName)
    {
        foreach (var element in releases)
        {
            var tag = element.GetProperty("tag_name").GetString()?.Trim();
            if (string.Equals(tag, tagName, StringComparison.OrdinalIgnoreCase))
            {
                return element.TryGetProperty("zipball_url", out var zipProp) ? zipProp.GetString() : null;
            }
        }

        return null;
    }

    private async Task<string> DownloadFileAsync(
        string url,
        string destinationDirectory,
        string fileName,
        long? expectedSize,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var filePath = Path.Combine(destinationDirectory, fileName);

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? expectedSize ?? -1;

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[DOWNLOAD_BUFFER_SIZE];
        long bytesRead = 0;
        int read;
        while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            bytesRead += read;
            if (totalBytes > 0)
            {
                progress?.Report((int)((double)bytesRead / totalBytes * 100));
            }
        }

        // Verify file size if expected size was known
        if (expectedSize.HasValue && bytesRead != expectedSize.Value)
        {
            throw new InvalidOperationException(
                $"Downloaded file size ({bytesRead} bytes) does not match expected size ({expectedSize.Value} bytes).");
        }

        progress?.Report(100);
        return filePath;
    }
}
