namespace Daqifi.Mcp;

/// <summary>
/// Launch-time configuration for the MCP server, parsed from CLI flags.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>
    /// When true, mutating tools (configure, set sample rate, start/stop logging) are refused.
    /// Discovery, connection, and read-only introspection remain available.
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// Optional upper bound enforced by <c>set_sample_rate</c>; requests above it are rejected.
    /// Null means only the device's hardware ceiling applies.
    /// </summary>
    public int? MaxSampleRateHz { get; init; }

    /// <summary>
    /// When true (the default), the server asks nuget.org once at startup whether a newer
    /// <c>Daqifi.Mcp</c> has been published, logs a one-line notice to stderr if so, and reports
    /// it from <c>get_server_info</c> (issue #727).
    /// </summary>
    /// <remarks>
    /// On by default because the failure it catches is silent: an install several releases behind
    /// exposes fewer tools than the current one, and neither the user nor the agent has any other
    /// signal that this is what they are looking at. It is one anonymous request to a static
    /// nuget.org index at startup, carrying no device data, and <c>--no-version-check</c> turns it
    /// off for anyone who would rather the server made no network call at all.
    /// </remarks>
    public bool VersionCheck { get; init; } = true;

    public static ServerOptions Parse(string[] args)
    {
        var readOnly = false;
        var versionCheck = true;
        int? maxRate = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--read-only":
                    readOnly = true;
                    break;
                case "--no-version-check":
                    versionCheck = false;
                    break;
                case "--max-sample-rate-hz" when i + 1 < args.Length:
                    // Ignore non-positive values; a cap of <= 0 would otherwise reject every rate.
                    if (int.TryParse(args[++i], out var rate) && rate >= 1)
                    {
                        maxRate = rate;
                    }
                    break;
            }
        }

        return new ServerOptions { ReadOnly = readOnly, MaxSampleRateHz = maxRate, VersionCheck = versionCheck };
    }

    public const string HelpText =
        """
        daqifi-mcp — Model Context Protocol server for DAQiFi devices

        Usage:
          daqifi-mcp [options]

        The server speaks MCP over stdio. Point an MCP-aware client (Claude Desktop,
        Claude Code, Cursor, Codex, ...) at the `daqifi-mcp` command.

        Options:
          --read-only               Expose discovery/introspection only; block configuration and logging.
          --max-sample-rate-hz <n>  Reject set_sample_rate requests above <n> Hz.
          --no-version-check        Do not ask nuget.org at startup whether a newer release exists.
          -h, --help                Show this help and exit.
        """;
}
