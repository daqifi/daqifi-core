using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Daqifi.Mcp;

/// <summary>
/// Where the newest published <c>Daqifi.Mcp</c> version comes from. Abstracted so the staleness
/// logic can be tested without reaching nuget.org.
/// </summary>
public interface ILatestVersionSource
{
    /// <summary>
    /// The highest stable published version, or null if that could not be determined.
    /// Implementations report a failure by throwing; null means "the feed answered, and had
    /// nothing usable in it".
    /// </summary>
    Task<string?> GetLatestStableVersionAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Asks nuget.org which versions of <c>Daqifi.Mcp</c> exist.
/// </summary>
/// <remarks>
/// Uses the flat container's version index — the cheapest endpoint nuget.org offers, a static
/// JSON document served from the CDN with no search service behind it. The request carries no
/// identity beyond a user agent naming the running version.
/// </remarks>
public sealed class NuGetLatestVersionSource : ILatestVersionSource
{
    /// <summary>The flat-container version index. The path segment must be lower-cased.</summary>
    private const string VersionIndexUrl =
        "https://api.nuget.org/v3-flatcontainer/daqifi.mcp/index.json";

    /// <summary>
    /// Short by design. This runs once at startup and a slow answer is worth nothing — the point
    /// is a one-line notice, not a gate on the server coming up.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public async Task<string?> GetLatestStableVersionAsync(CancellationToken cancellationToken)
    {
        // One request per process lifetime, so a client per call costs nothing that pooling would
        // save, and disposing it here means no socket is held for the life of the server.
        using var http = new HttpClient { Timeout = RequestTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"daqifi-mcp/{ServerVersion.Current}");

        using var response = await http.GetAsync(VersionIndexUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);

        if (!document.RootElement.TryGetProperty("versions", out var versions)
            || versions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return ServerVersion.SelectLatestStable(versions.EnumerateArray().Select(v => v.GetString()));
    }
}

/// <summary>
/// The server's answer to "which version am I, and is it current?" (issue #727).
/// </summary>
/// <remarks>
/// The check runs at most once per process: <see cref="Start"/> kicks it off at startup so the
/// notice reaches stderr without anyone asking, and <see cref="GetAsync"/> hands the same result
/// to the <c>get_server_info</c> tool. A check that never ran is started by the first
/// <see cref="GetAsync"/> instead, so the tool answers even when startup skipped it.
/// </remarks>
public sealed class VersionStatus
{
    private readonly ServerOptions _options;
    private readonly ILatestVersionSource _source;
    private readonly ILogger<VersionStatus>? _logger;
    private readonly object _checkGate = new();
    private Task<ServerVersionInfo>? _check;

    public VersionStatus(ServerOptions options, ILatestVersionSource source, ILogger<VersionStatus>? logger = null)
    {
        _options = options;
        _source = source;
        _logger = logger;
    }

    /// <summary>Begins the check without waiting for it. Safe to call more than once.</summary>
    /// <param name="cancellationToken">
    /// Cancels the nuget.org GET if this call is the one that starts it. Startup passes the
    /// host stopping token so a shutdown in flight does not wait out the request timeout.
    /// </param>
    public void Start(CancellationToken cancellationToken = default) => _ = GetOrStart(cancellationToken);

    /// <summary>
    /// The version report, awaiting the in-flight check if it has not finished. Bounded by the
    /// source's own timeout, so this cannot hang a tool call indefinitely.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels waiting for the report, and cancels the nuget.org GET when this call is the one
    /// that starts it. A cancelled attempt is not cached, so the next caller gets a real check.
    /// </param>
    public async Task<ServerVersionInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await GetOrStart(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A previous attempt was cancelled; do not inherit that as this caller's result.
            }
        }
    }

    private Task<ServerVersionInfo> GetOrStart(CancellationToken cancellationToken)
    {
        var existing = Volatile.Read(ref _check);
        if (existing is { IsCanceled: false })
        {
            return existing;
        }

        lock (_checkGate)
        {
            if (_check is { IsCanceled: false })
            {
                return _check;
            }

            return _check = RunCheckAsync(cancellationToken);
        }
    }

    private async Task<ServerVersionInfo> RunCheckAsync(CancellationToken cancellationToken)
    {
        var current = ServerVersion.Current;

        if (!_options.VersionCheck)
        {
            return ServerVersionInfo.CheckDisabled(current);
        }

        string? latest;
        try
        {
            latest = await _source.GetLatestStableVersionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A cancelled GET is not "nuget.org was unreachable" — let it surface as a
            // cancellation so it is not cached as a verdict. GetOrStart treats a cancelled
            // task as "no check has run", so the next caller starts a real one. Deliberately
            // not clearing _check here: another caller may already have replaced it with a
            // live check, and nulling that would start a third request for no reason.
            throw;
        }
        catch (Exception ex)
        {
            // Being offline is not an error worth shouting about; the server works either way.
            // HttpClient.Timeout also lands here: it throws TaskCanceledException without
            // the caller token being cancelled, which must stay "unavailable" not OCE.
            _logger?.LogDebug(ex, "Could not check nuget.org for a newer {Package}.", ServerVersion.PackageId);
            return ServerVersionInfo.CheckUnavailable(current);
        }

        if (latest is null)
        {
            return ServerVersionInfo.CheckUnavailable(current);
        }

        var info = ServerVersionInfo.Checked(current, latest);
        if (info.UpdateAvailable)
        {
            // Warning, not Information: the minimum log level is Warning precisely so stderr stays
            // quiet, and this is the one startup line that is worth breaking that silence for.
            _logger?.LogWarning(
                "{Package} {Current} is running, but {Latest} is published. Newer versions expose tools this one does not. Update with `{Command}`, then restart your MCP client. Pass --no-version-check to stop checking.",
                ServerVersion.PackageId, current, latest, ServerVersion.UpdateCommand);
        }

        return info;
    }
}
