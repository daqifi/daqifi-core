using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// The wire contract as a checked-in file (issue #1043): every tool's name, input schema and output
/// schema, read off the real server. <c>Daqifi.Core</c> has <c>PublicAPI.Shipped.txt</c>; this is
/// the same thing for AI clients, so renaming a DTO property shows up as a diff to review.
/// </summary>
/// <remarks>
/// Descriptions are left out. They are prose, pinned by <see cref="ToolDescriptionContractTests"/>,
/// and rewording one is not a wire change; with them in, every wording fix would look like one.
/// <para>
/// To accept a change: run this test with <c>DAQIFI_UPDATE_MCP_SNAPSHOT=1</c> set, and commit
/// <c>tools-list.snapshot.json</c> with the change that caused it.
/// </para>
/// </remarks>
public class ToolsListSnapshotTests
{
    private const string SnapshotFileName = "tools-list.snapshot.json";
    private const string UpdateVariable = "DAQIFI_UPDATE_MCP_SNAPSHOT";

    /// <summary>See <see cref="ServerInstructionsHandshakeTests"/>.</summary>
    private static readonly TimeSpan ServerBudget = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public async Task ToolsList_MatchesTheCheckedInSnapshot()
    {
        var live = await LiveToolsListAsync();
        var path = Path.Combine(ProjectDirectory, SnapshotFileName);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            await File.WriteAllTextAsync(path, live);
            return;
        }

        var expected = File.Exists(path)
            ? (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n")
            : string.Empty;
        Assert.True(expected == live, Difference(expected, live));
    }

    [Fact]
    public void WithoutDescriptions_DropsTheProseAndKeepsAFieldOfThatName()
    {
        // The stripping must not be able to hide a wire change: a parameter called "description"
        // is an object in "properties", not a string.
        var schema = JsonNode.Parse(
            """{"description":"prose","properties":{"description":{"type":"string","description":"prose"}}}""")!;

        Assert.Equal(
            """{"properties":{"description":{"type":"string"}}}""",
            WithoutDescriptions(schema).ToJsonString());
    }

    private static async Task<string> LiveToolsListAsync()
    {
        using var timeout = new CancellationTokenSource(ServerBudget);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "daqifi-mcp",
            Command = ServerProcess.DotnetHost(),
            Arguments = [ServerProcess.ServerAssembly(), "--no-version-check"],
            ShutdownTimeout = TimeSpan.FromSeconds(1),
        });
        // Wait for server/discover rather than race the initialize fallback on a cold start.
        var options = new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan };
        await using var client = await McpClient.CreateAsync(transport, options, cancellationToken: timeout.Token);

        var tools = new JsonArray();
        foreach (var tool in (await client.ListToolsAsync(cancellationToken: timeout.Token))
            .Select(t => t.ProtocolTool)
            .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["inputSchema"] = WithoutDescriptions(JsonNode.Parse(tool.InputSchema.GetRawText())!),
                ["outputSchema"] = WithoutDescriptions(JsonNode.Parse(tool.OutputSchema!.Value.GetRawText())!),
            });
        }

        return tools.ToJsonString(Indented) + "\n";
    }

    private static JsonNode WithoutDescriptions(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["description"] is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                {
                    obj.Remove("description");
                }

                foreach (var child in obj.Select(p => p.Value).OfType<JsonNode>().ToList())
                {
                    WithoutDescriptions(child);
                }

                break;

            case JsonArray array:
                foreach (var child in array.OfType<JsonNode>())
                {
                    WithoutDescriptions(child);
                }

                break;
        }

        return node;
    }

    private static string Difference(string expected, string live)
    {
        var was = expected.Split('\n');
        var now = live.Split('\n');
        var line = 0;
        while (line < was.Length && line < now.Length && was[line] == now[line])
        {
            line++;
        }

        var tool = now.Take(line + 1).LastOrDefault(l => l.StartsWith("    \"name\":", StringComparison.Ordinal))?.Trim();
        return $"tools/list no longer matches {SnapshotFileName}, which is what AI clients depend on. "
            + $"First difference at line {line + 1} (near {tool}):\n"
            + $"  snapshot: {was.ElementAtOrDefault(line) ?? "(end of file)"}\n"
            + $"  server:   {now.ElementAtOrDefault(line) ?? "(end of file)"}\n"
            + $"If the change is intended, run this test with {UpdateVariable}=1 and commit the file.";
    }

    private static string ProjectDirectory =>
        typeof(ToolsListSnapshotTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "ProjectDirectory")
            .Value!;
}
