using System.Net;
using System.Text;
using System.Text.Json;
using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

public class GitHubFirmwareDownloadServiceTests : IDisposable
{
    private readonly MockHttpMessageHandler _handler = new();
    private readonly HttpClient _httpClient;
    private readonly string _tempDir;

    public GitHubFirmwareDownloadServiceTests()
    {
        _httpClient = new HttpClient(_handler);
        _tempDir = Path.Combine(Path.GetTempPath(), "daqifi-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    #region GetLatestReleaseAsync

    [Fact]
    public async Task GetLatestReleaseAsync_ReturnsLatestNonDraftRelease()
    {
        var releases = BuildReleasesJson(
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: "firmware-3.2.0.hex"),
            MakeRelease("v3.1.0", draft: false, prerelease: false, hexAsset: "firmware-3.1.0.hex"));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);

        var service = CreateService();
        var result = await service.GetLatestReleaseAsync();

        Assert.NotNull(result);
        Assert.Equal("v3.2.0", result.TagName);
        Assert.Equal(new FirmwareVersion(3, 2, 0, null, 0), result.Version);
        Assert.Equal("firmware-3.2.0.hex", result.AssetFileName);
        Assert.False(result.IsPreRelease);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_SkipsDrafts()
    {
        var releases = BuildReleasesJson(
            MakeRelease("v4.0.0", draft: true, prerelease: false, hexAsset: "firmware-4.0.0.hex"),
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: "firmware-3.2.0.hex"));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);

        var service = CreateService();
        var result = await service.GetLatestReleaseAsync();

        Assert.NotNull(result);
        Assert.Equal("v3.2.0", result.TagName);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ExcludesPreReleaseByDefault()
    {
        var releases = BuildReleasesJson(
            MakeRelease("v4.0.0b1", draft: false, prerelease: true, hexAsset: "firmware-4.0.0b1.hex"),
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: "firmware-3.2.0.hex"));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);

        var service = CreateService();
        var result = await service.GetLatestReleaseAsync(includePreRelease: false);

        Assert.NotNull(result);
        Assert.Equal("v3.2.0", result.TagName);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_IncludesPreReleaseWhenRequested()
    {
        var releases = BuildReleasesJson(
            MakeRelease("v4.0.0b1", draft: false, prerelease: true, hexAsset: "firmware-4.0.0b1.hex"),
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: "firmware-3.2.0.hex"));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);

        var service = CreateService();
        var result = await service.GetLatestReleaseAsync(includePreRelease: true);

        Assert.NotNull(result);
        // 4.0.0b1 is still less than 4.0.0 release, but greater than 3.2.0
        Assert.Equal("v4.0.0b1", result.TagName);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ReturnsNull_WhenNoReleases()
    {
        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, "[]");

        var service = CreateService();
        var result = await service.GetLatestReleaseAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ParsesReleaseNotes()
    {
        var releases = BuildReleasesJson(
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: "firmware.hex",
                body: "Bug fixes and improvements"));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);

        var service = CreateService();
        var result = await service.GetLatestReleaseAsync();

        Assert.Equal("Bug fixes and improvements", result?.ReleaseNotes);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ParsesAssetSize()
    {
        var releases = BuildReleasesJson(
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: "firmware.hex", assetSize: 123456));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);

        var service = CreateService();
        var result = await service.GetLatestReleaseAsync();

        Assert.Equal(123456, result?.AssetSize);
    }

    #endregion

    #region CheckForUpdateAsync

    [Fact]
    public async Task CheckForUpdateAsync_UpdateAvailable_WhenNewerVersionExists()
    {
        var releases = BuildReleasesJson(
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: "firmware.hex"));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);

        var service = CreateService();
        var result = await service.CheckForUpdateAsync("3.1.0");

        Assert.True(result.UpdateAvailable);
        Assert.NotNull(result.DeviceVersion);
        Assert.Equal(new FirmwareVersion(3, 1, 0, null, 0), result.DeviceVersion);
        Assert.NotNull(result.LatestRelease);
        Assert.Equal("v3.2.0", result.LatestRelease.TagName);
    }

    [Fact]
    public async Task CheckForUpdateAsync_NoUpdate_WhenSameVersion()
    {
        var releases = BuildReleasesJson(
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: "firmware.hex"));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);

        var service = CreateService();
        var result = await service.CheckForUpdateAsync("3.2.0");

        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdateAsync_NoUpdate_WhenDeviceIsNewer()
    {
        var releases = BuildReleasesJson(
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: "firmware.hex"));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);

        var service = CreateService();
        var result = await service.CheckForUpdateAsync("4.0.0");

        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdateAsync_UpdateAvailable_WhenDeviceVersionUnparseable()
    {
        var releases = BuildReleasesJson(
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: "firmware.hex"));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);

        var service = CreateService();
        var result = await service.CheckForUpdateAsync("unknown");

        Assert.True(result.UpdateAvailable);
        Assert.Null(result.DeviceVersion);
    }

    [Fact]
    public async Task CheckForUpdateAsync_NoUpdate_WhenNoReleases()
    {
        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, "[]");

        var service = CreateService();
        var result = await service.CheckForUpdateAsync("3.2.0");

        Assert.False(result.UpdateAvailable);
        Assert.Null(result.LatestRelease);
    }

    #endregion

    #region Caching

    [Fact]
    public async Task Cache_SecondCallUsesCachedData()
    {
        var releases = BuildReleasesJson(
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: "firmware.hex"));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);

        var service = CreateService();

        var result1 = await service.GetLatestReleaseAsync();
        var result2 = await service.GetLatestReleaseAsync();

        Assert.Equal(1, _handler.RequestCount);
        Assert.Equal(result1?.TagName, result2?.TagName);
    }

    [Fact]
    public async Task InvalidateCache_ForcesNewApiCall()
    {
        var releases = BuildReleasesJson(
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: "firmware.hex"));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);

        var service = CreateService();

        await service.GetLatestReleaseAsync();
        service.InvalidateCache();
        await service.GetLatestReleaseAsync();

        Assert.Equal(2, _handler.RequestCount);
    }

    [Fact]
    public async Task Cache_ExpiresAfterTtl()
    {
        var releases = BuildReleasesJson(
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: "firmware.hex"));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);

        // Use a 0-second TTL so cache expires immediately
        var service = new GitHubFirmwareDownloadService(_httpClient, cacheTtl: TimeSpan.Zero);

        await service.GetLatestReleaseAsync();
        await service.GetLatestReleaseAsync();

        Assert.Equal(2, _handler.RequestCount);
    }

    #endregion

    #region DownloadLatestFirmwareAsync

    [Fact]
    public async Task DownloadLatestFirmwareAsync_DownloadsHexFile()
    {
        var hexContent = ":020000040000FA";
        var releases = BuildReleasesJson(
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: "firmware-3.2.0.hex",
                assetSize: Encoding.UTF8.GetByteCount(hexContent)));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);
        _handler.SetResponse("https://github.com/daqifi/daqifi-nyquist-firmware/releases/download/v3.2.0/firmware-3.2.0.hex",
            HttpStatusCode.OK, hexContent);

        var service = CreateService();

        var filePath = await service.DownloadLatestFirmwareAsync(_tempDir);

        Assert.NotNull(filePath);
        Assert.True(File.Exists(filePath));
        Assert.Equal("firmware-3.2.0.hex", Path.GetFileName(filePath));
    }

    [Fact]
    public async Task DownloadLatestFirmwareAsync_ReturnsNull_WhenNoRelease()
    {
        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, "[]");

        var service = CreateService();
        var result = await service.DownloadLatestFirmwareAsync(_tempDir);

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadLatestFirmwareAsync_ReturnsNull_WhenNoHexAsset()
    {
        var releases = BuildReleasesJson(
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: null));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);

        var service = CreateService();
        var result = await service.DownloadLatestFirmwareAsync(_tempDir);

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadLatestFirmwareAsync_VerifiesFileSize()
    {
        var releases = BuildReleasesJson(
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: "firmware.hex", assetSize: 99999));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);
        _handler.SetResponse("https://github.com/daqifi/daqifi-nyquist-firmware/releases/download/v3.2.0/firmware.hex",
            HttpStatusCode.OK, "short");

        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DownloadLatestFirmwareAsync(_tempDir));
    }

    #endregion

    #region DownloadFirmwareByTagAsync

    [Fact]
    public async Task DownloadFirmwareByTagAsync_DownloadsSpecificVersion()
    {
        var hexContent = "hex content here";
        var releases = BuildReleasesJson(
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: "firmware-3.2.0.hex"),
            MakeRelease("v3.1.0", draft: false, prerelease: false, hexAsset: "firmware-3.1.0.hex",
                assetSize: Encoding.UTF8.GetByteCount(hexContent)));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);
        _handler.SetResponse("https://github.com/daqifi/daqifi-nyquist-firmware/releases/download/v3.1.0/firmware-3.1.0.hex",
            HttpStatusCode.OK, hexContent);

        var service = CreateService();
        var filePath = await service.DownloadFirmwareByTagAsync("v3.1.0", _tempDir);

        Assert.NotNull(filePath);
        Assert.Contains("firmware-3.1.0.hex", filePath);
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public async Task DownloadFirmwareByTagAsync_ReturnsNull_WhenTagNotFound()
    {
        var releases = BuildReleasesJson(
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: "firmware.hex"));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);

        var service = CreateService();
        var result = await service.DownloadFirmwareByTagAsync("v9.9.9", _tempDir);

        Assert.Null(result);
    }

    #endregion

    #region Error Handling

    [Fact]
    public async Task GetLatestReleaseAsync_ThrowsOnRateLimit()
    {
        _handler.SetRateLimitResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases");

        var service = CreateService();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.GetLatestReleaseAsync());
        Assert.Contains("rate limit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ThrowsOnServerError()
    {
        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.InternalServerError, "Server Error");

        var service = CreateService();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.GetLatestReleaseAsync());
    }

    [Fact]
    public async Task DownloadLatestFirmwareAsync_ThrowsOnDownloadFailure()
    {
        var releases = BuildReleasesJson(
            MakeRelease("v3.2.0", draft: false, prerelease: false, hexAsset: "firmware.hex"));

        _handler.SetResponse("https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/releases",
            HttpStatusCode.OK, releases);
        _handler.SetResponse("https://github.com/daqifi/daqifi-nyquist-firmware/releases/download/v3.2.0/firmware.hex",
            HttpStatusCode.NotFound, "Not Found");

        var service = CreateService();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.DownloadLatestFirmwareAsync(_tempDir));
    }

    #endregion

    #region Configurable Repository

    [Fact]
    public async Task Constructor_UsesCustomRepository()
    {
        var releases = BuildReleasesJson(
            MakeRelease("v1.0.0", draft: false, prerelease: false, hexAsset: "custom.hex"));

        _handler.SetResponse("https://api.github.com/repos/myorg/myrepo/releases",
            HttpStatusCode.OK, releases);

        var service = new GitHubFirmwareDownloadService(_httpClient, firmwareRepo: "myorg/myrepo");
        var result = await service.GetLatestReleaseAsync();

        Assert.NotNull(result);
        Assert.Equal("v1.0.0", result.TagName);
    }

    #endregion

    #region Helpers

    private GitHubFirmwareDownloadService CreateService() => new(_httpClient);

    private static string MakeRelease(
        string tagName,
        bool draft,
        bool prerelease,
        string? hexAsset,
        string? body = null,
        long? assetSize = null)
    {
        var assets = new List<object>();
        if (hexAsset != null)
        {
            assets.Add(new
            {
                name = hexAsset,
                browser_download_url = $"https://github.com/daqifi/daqifi-nyquist-firmware/releases/download/{tagName}/{hexAsset}",
                size = assetSize ?? Encoding.UTF8.GetByteCount(hexAsset)
            });
        }

        var release = new Dictionary<string, object?>
        {
            ["tag_name"] = tagName,
            ["draft"] = draft,
            ["prerelease"] = prerelease,
            ["body"] = body,
            ["published_at"] = "2025-01-15T10:00:00Z",
            ["assets"] = assets,
            ["zipball_url"] = $"https://api.github.com/repos/daqifi/daqifi-nyquist-firmware/zipball/{tagName}"
        };

        return JsonSerializer.Serialize(release);
    }

    private static string BuildReleasesJson(params string[] releases)
    {
        return "[" + string.Join(",", releases) + "]";
    }

    #endregion
}

/// <summary>
/// Simple mock HttpMessageHandler for testing.
/// </summary>
internal class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode StatusCode, string Content, Dictionary<string, string>? Headers)> _responses = new();

    public int RequestCount { get; private set; }

    public void SetResponse(string url, HttpStatusCode statusCode, string content)
    {
        _responses[url] = (statusCode, content, null);
    }

    public void SetRateLimitResponse(string url)
    {
        var resetTime = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString();
        _responses[url] = (HttpStatusCode.Forbidden, "Rate limit exceeded",
            new Dictionary<string, string> { ["X-RateLimit-Reset"] = resetTime });
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        var url = request.RequestUri!.ToString();

        if (_responses.TryGetValue(url, out var entry))
        {
            var response = new HttpResponseMessage(entry.StatusCode)
            {
                Content = new StringContent(entry.Content, Encoding.UTF8, "application/json")
            };

            if (entry.Headers != null)
            {
                foreach (var header in entry.Headers)
                {
                    response.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return Task.FromResult(response);
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("Not found")
        });
    }
}
