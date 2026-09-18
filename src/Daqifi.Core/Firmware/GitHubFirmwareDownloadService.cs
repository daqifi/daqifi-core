using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Downloads firmware releases from GitHub Releases API.
/// </summary>
public sealed class GitHubFirmwareDownloadService : IFirmwareDownloadService
{
    private static readonly string DEFAULT_USER_AGENT = BuildDefaultUserAgent();
    private const int DOWNLOAD_BUFFER_SIZE = 8192;

    private static string BuildDefaultUserAgent()
    {
        // Prefer InformationalVersion so prerelease SemVer suffixes (e.g. "1.0.0-beta.1")
        // survive into GitHub API traffic for rate-limit/abuse investigations.
        // AssemblyVersion is numeric-only and would truncate them.
        var assembly = typeof(GitHubFirmwareDownloadService).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "unknown";
        return $"DaqifiFirmwareUpdater/{version}";
    }

    private readonly HttpClient _httpClient;
    private readonly string _firmwareRepoApiUrl;
    private readonly string _wifiRepoApiUrl;
    private readonly TimeSpan _cacheTtl;

    // One cache per repository, so a slow firmware lookup never holds up a WiFi one.
    private readonly ReleaseCache _firmwareCache = new();
    private readonly ReleaseCache _wifiCache = new();

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
        var releases = await GetFirmwareReleasesAsync(cancellationToken).ConfigureAwait(false);
        return FindLatestRelease(releases, includePreRelease, ".hex");
    }

    /// <inheritdoc />
    public async Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
        string deviceVersionString,
        bool includePreRelease = false,
        CancellationToken cancellationToken = default)
    {
        var hasCurrent = FirmwareVersion.TryParse(deviceVersionString, out var deviceVersion);
        var latest = await GetLatestReleaseAsync(includePreRelease, cancellationToken).ConfigureAwait(false);

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
        var release = await GetLatestReleaseAsync(includePreRelease, cancellationToken).ConfigureAwait(false);
        if (release?.DownloadUrl == null || release.AssetFileName == null) return null;

        return await DownloadFileAsync(
            release.DownloadUrl, destinationDirectory, release.AssetFileName,
            release.AssetSize, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> DownloadFirmwareByTagAsync(
        string tagName,
        string destinationDirectory,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var releases = await GetFirmwareReleasesAsync(cancellationToken).ConfigureAwait(false);

        FirmwareReleaseInfo? release = null;
        foreach (var element in releases)
        {
            if (element.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean())
                continue;

            var tag = element.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString()?.Trim() : null;
            if (!string.Equals(tag, tagName, StringComparison.OrdinalIgnoreCase)) continue;

            release = ParseReleaseElement(element, ".hex");
            break;
        }

        if (release?.DownloadUrl == null || release.AssetFileName == null) return null;

        return await DownloadFileAsync(
            release.DownloadUrl, destinationDirectory, release.AssetFileName,
            release.AssetSize, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FirmwareReleaseInfo?> GetLatestWifiReleaseAsync(
        CancellationToken cancellationToken = default)
    {
        var releases = await GetWifiReleasesAsync(cancellationToken).ConfigureAwait(false);
        return FindLatestRelease(releases, includePreRelease: false, assetExtension: null);
    }

    /// <inheritdoc />
    public async Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
        string destinationDirectory,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var releases = await GetWifiReleasesAsync(cancellationToken).ConfigureAwait(false);
        var release = FindLatestRelease(releases, includePreRelease: false, assetExtension: null);
        if (release?.ZipballUrl == null) return null;

        var zipballUrl = release.ZipballUrl;

        progress?.Report(0);

        Directory.CreateDirectory(destinationDirectory);
        var zipFileName = $"wifi-firmware-{release.TagName}.zip";
        var zipFilePath = Path.Combine(destinationDirectory, zipFileName);

        // Download the zipball
        using var response = await _httpClient.GetAsync(zipballUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (contentStream.ConfigureAwait(false))
        {
            var fileStream = new FileStream(zipFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (fileStream.ConfigureAwait(false))
            {
                var buffer = new byte[DOWNLOAD_BUFFER_SIZE];
                long bytesRead = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    bytesRead += read;
                    if (totalBytes > 0)
                    {
                        progress?.Report((int)((double)bytesRead / totalBytes * 80));
                    }
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

        // Validate extracted entries don't escape the target directory (Zip Slip protection)
        var fullExtractPath = Path.GetFullPath(extractPath);
        foreach (var entry in Directory.GetFileSystemEntries(extractPath, "*", SearchOption.AllDirectories))
        {
            if (!Path.GetFullPath(entry).StartsWith(fullExtractPath, StringComparison.Ordinal))
            {
                Directory.Delete(extractPath, true);
                throw new InvalidOperationException(
                    $"Zip archive contains path traversal entry: {entry}");
            }
        }

        // Clean up the downloaded zip file
        File.Delete(zipFilePath);
        progress?.Report(100);

        return (extractPath, release.TagName);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Never waits for a refresh that is in flight: it only clears the cached lists, and a
    /// refresh that started before this call does not publish its result afterwards.
    /// </remarks>
    public void InvalidateCache()
    {
        _firmwareCache.Invalidate();
        _wifiCache.Invalidate();
    }

    private Task<List<JsonElement>> GetFirmwareReleasesAsync(CancellationToken cancellationToken) =>
        GetReleasesAsync(_firmwareCache, _firmwareRepoApiUrl, cancellationToken);

    private Task<List<JsonElement>> GetWifiReleasesAsync(CancellationToken cancellationToken) =>
        GetReleasesAsync(_wifiCache, _wifiRepoApiUrl, cancellationToken);

    /// <summary>
    /// Returns the cached release list for one repository, fetching it at most once however
    /// many callers miss the cache at the same time.
    /// </summary>
    /// <remarks>
    /// Concurrent callers (several devices checking for updates at once) used to all see a
    /// stale cache and each make their own GitHub request, spending the unauthenticated rate
    /// limit several times over. The refresh gate makes the first caller fetch and the rest
    /// wait for, and then reuse, its result.
    /// </remarks>
    private async Task<List<JsonElement>> GetReleasesAsync(
        ReleaseCache cache, string apiUrl, CancellationToken cancellationToken)
    {
        if (cache.TryGetFresh(_cacheTtl, out var cached))
        {
            return cached;
        }

        await cache.RefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while this one waited for the gate.
            if (cache.TryGetFresh(_cacheTtl, out cached))
            {
                return cached;
            }

            var generation = cache.Generation;
            var elements = await FetchReleasesFromApiAsync(apiUrl, cancellationToken).ConfigureAwait(false);
            cache.Publish(elements, generation);
            return elements;
        }
        finally
        {
            cache.RefreshGate.Release();
        }
    }

    /// <summary>
    /// The cached release list for one repository.
    /// </summary>
    /// <remarks>
    /// The list and its timestamp live in one immutable entry swapped by a single reference
    /// write, so a reader can never pair a new list with an old timestamp. <see cref="RefreshGate"/>
    /// is held across the network request and only ever awaited asynchronously; the short
    /// <c>lock</c> guards nothing but the generation check and the swap, so
    /// <see cref="Invalidate"/> never blocks on the network. The gate is not disposed: the
    /// service has no shutdown path, and <c>AvailableWaitHandle</c> is never touched.
    /// </remarks>
    private sealed class ReleaseCache
    {
        private readonly object _sync = new();
        private Entry? _entry;
        private int _generation;

        public SemaphoreSlim RefreshGate { get; } = new(1, 1);

        public int Generation
        {
            get
            {
                lock (_sync)
                {
                    return _generation;
                }
            }
        }

        public bool TryGetFresh(TimeSpan ttl, out List<JsonElement> releases)
        {
            var entry = Volatile.Read(ref _entry);
            if (entry != null && DateTime.UtcNow - entry.FetchedAtUtc < ttl)
            {
                releases = entry.Releases;
                return true;
            }

            releases = null!;
            return false;
        }

        /// <summary>
        /// Stores a fetched list, unless the cache was invalidated after the fetch began.
        /// </summary>
        public void Publish(List<JsonElement> releases, int generationAtFetchStart)
        {
            lock (_sync)
            {
                if (_generation == generationAtFetchStart)
                {
                    Volatile.Write(ref _entry, new Entry(releases, DateTime.UtcNow));
                }
            }
        }

        public void Invalidate()
        {
            lock (_sync)
            {
                _generation++;
                Volatile.Write(ref _entry, null);
            }
        }

        private sealed record Entry(List<JsonElement> Releases, DateTime FetchedAtUtc);
    }

    private async Task<List<JsonElement>> FetchReleasesFromApiAsync(string apiUrl, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(apiUrl, cancellationToken).ConfigureAwait(false);

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

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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

            // Only consider releases that have a downloadable asset when an extension is specified
            if (assetExtension != null && release.DownloadUrl == null) continue;

            if (best == null || release.Version > best.Version)
            {
                best = release;
            }
        }

        return best;
    }

    private static FirmwareReleaseInfo? ParseReleaseElement(JsonElement element, string? assetExtension)
    {
        if (!element.TryGetProperty("tag_name", out var tagProp)) return null;
        var tagName = tagProp.GetString()?.Trim();
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
                if (!asset.TryGetProperty("name", out var nameProp)) continue;
                var name = nameProp.GetString();
                if (name == null || !name.EndsWith(assetExtension, StringComparison.OrdinalIgnoreCase)) continue;

                downloadUrl = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
                assetFileName = name;
                assetSize = asset.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : null;
                break;
            }
        }

        var zipballUrl = element.TryGetProperty("zipball_url", out var zipProp) ? zipProp.GetString() : null;

        return new FirmwareReleaseInfo
        {
            Version = version,
            TagName = tagName,
            IsPreRelease = isPreRelease,
            ReleaseNotes = releaseNotes,
            DownloadUrl = downloadUrl,
            AssetFileName = assetFileName,
            AssetSize = assetSize,
            PublishedAt = publishedAt,
            ZipballUrl = zipballUrl
        };
    }

    private async Task<string> DownloadFileAsync(
        string url,
        string destinationDirectory,
        string fileName,
        long? expectedSize,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        // Sanitize fileName to prevent path traversal
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(safeName))
        {
            throw new ArgumentException("Invalid file name.", nameof(fileName));
        }

        Directory.CreateDirectory(destinationDirectory);
        var filePath = Path.Combine(destinationDirectory, safeName);

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? expectedSize ?? -1;

        var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        long bytesRead = 0;
        await using (contentStream.ConfigureAwait(false))
        {
            var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (fileStream.ConfigureAwait(false))
            {
                var buffer = new byte[DOWNLOAD_BUFFER_SIZE];
                int read;
                while ((read = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    bytesRead += read;
                    if (totalBytes > 0)
                    {
                        progress?.Report((int)((double)bytesRead / totalBytes * 100));
                    }
                }
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
