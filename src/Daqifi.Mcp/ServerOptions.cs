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
    /// Optional upper bound applied to <c>set_sample_rate</c>. Null means no clamp.
    /// </summary>
    public int? MaxSampleRateHz { get; init; }

    public static ServerOptions Parse(string[] args)
    {
        var readOnly = false;
        int? maxRate = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--read-only":
                    readOnly = true;
                    break;
                case "--max-sample-rate-hz" when i + 1 < args.Length:
                    // Ignore non-positive values; a clamp of <= 0 would otherwise force an invalid rate.
                    if (int.TryParse(args[++i], out var rate) && rate >= 1)
                    {
                        maxRate = rate;
                    }
                    break;
            }
        }

        return new ServerOptions { ReadOnly = readOnly, MaxSampleRateHz = maxRate };
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
          --max-sample-rate-hz <n>  Clamp set_sample_rate requests to at most <n> Hz.
          -h, --help                Show this help and exit.
        """;
}
