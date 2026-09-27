using System.Text.Json;
using Daqifi.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// Contract tests for the structured-content flag advertised to clients. SDK 2.2 serializes a
/// POCO/list return as a single text <c>ContentBlock</c> unless
/// <c>UseStructuredContent=true</c> is set on <see cref="McpServerToolAttribute"/>. Every tool
/// here returns a record or list, so leaving the flag off means clients never see
/// <c>outputSchema</c> / <c>structuredContent</c>.
/// </summary>
public class ToolStructuredContentContractTests
{
    /// <summary>
    /// Every tool that must advertise structured content. A tool added to
    /// <see cref="DaqifiTools"/> without a row here fails
    /// <see cref="EveryAdvertisedTool_HasARowInTheStructuredContentTable"/> rather than shipping
    /// as a text blob; a row whose tool never set the flag fails
    /// <see cref="Tool_AdvertisesAnOutputSchema"/>.
    /// </summary>
    public static TheoryData<string> ExpectedTools()
    {
        var data = new TheoryData<string>();
        foreach (var name in Expected)
        {
            data.Add(name);
        }

        return data;
    }

    // All 26 tools return records or lists; all of them need the flag. The table is the
    // completeness check, not a place to opt out — a new tool belongs here and on the attribute.
    private static readonly string[] Expected =
    {
        "get_server_info",
        "discover_devices",
        "connect_device",
        "disconnect_device",
        "list_connected_devices",
        "get_device_status",
        "list_channels",
        "configure_analog_channels",
        "configure_digital_channels",
        "set_digital_direction",
        "set_digital_output",
        "set_pwm_output",
        "disable_pwm",
        "list_analog_outputs",
        "set_analog_output",
        "latch_analog_outputs",
        "read_analog_output",
        "set_sample_rate",
        "start_sd_logging",
        "stop_sd_logging",
        "list_sd_files",
        "get_sd_storage",
        "download_sd_file",
        "delete_sd_file",
        "read_channel_values",
        "capture_samples",
    };

    [Theory]
    [MemberData(nameof(ExpectedTools))]
    public void Tool_AdvertisesAnOutputSchema(string name)
    {
        // Read from the registered tool, i.e. what a client sees in tools/list. The attribute
        // getter can tell unset (false) from true, but it cannot tell us the schema actually made
        // it onto the wire.
        var schema = Advertised(name).OutputSchema;

        Assert.True(
            schema.HasValue,
            $"{name} has no OutputSchema; set UseStructuredContent = true on [McpServerTool].");
        Assert.Equal(JsonValueKind.Object, schema.Value.ValueKind);
    }

    [Fact]
    public void EveryAdvertisedTool_HasARowInTheStructuredContentTable()
    {
        var advertised = AdvertisedTools
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        var tabulated = Expected
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(tabulated, advertised);
    }

    [Fact]
    public async Task NullField_IsStillWritten_SoTheResultMatchesItsOutputSchema()
    {
        // The generated schema lists every record property as required, a nullable one as
        // ["T","null"], but the SDK's default serializer drops null properties. A result with a
        // null field would then be missing a required key, and a client that checks results
        // against the schema rejects it. get_server_info with the version check off is the one
        // tool that returns a null field (latestVersion) with no device attached.
        var services = new ServiceCollection();
        services.AddSingleton(new VersionStatus(
            new ServerOptions { VersionCheck = false },
            new StubLatestVersionSource()));
        services.AddMcpServer().WithDaqifiTools();
        await using var provider = services.BuildServiceProvider();
        var tool = provider.GetServices<McpServerTool>()
            .Single(t => t.ProtocolTool.Name == "get_server_info");
        await using var server = McpServer.Create(
            new StreamServerTransport(Stream.Null, Stream.Null),
            new McpServerOptions(),
            serviceProvider: provider);
        var request = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Method = RequestMethods.ToolsCall },
            new CallToolRequestParams { Name = "get_server_info" })
        {
            Services = provider,
        };

        var result = await tool.InvokeAsync(request, CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);
        var structured = result.StructuredContent.Value;
        var required = tool.ProtocolTool.OutputSchema!.Value.GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();
        Assert.Contains("latestVersion", required);
        foreach (var name in required)
        {
            Assert.True(
                structured.TryGetProperty(name, out _),
                $"structuredContent is missing required '{name}': {structured.GetRawText()}");
        }

        Assert.Equal(JsonValueKind.Null, structured.GetProperty("latestVersion").ValueKind);

        // The text block is serialized with the same options, so it spells the null out too.
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        using var textJson = JsonDocument.Parse(text);
        Assert.Equal(JsonValueKind.Null, textJson.RootElement.GetProperty("latestVersion").ValueKind);
    }

    private static Tool Advertised(string name) =>
        AdvertisedTools.Single(t => t.Name == name);

    /// <summary>
    /// The tools exactly as the server advertises them: registered through the same
    /// <c>WithDaqifiTools</c> call Program.cs makes, so every tool the server can list is
    /// covered here — whatever class it lives in — and nothing it would not list is.
    /// </summary>
    private static readonly IReadOnlyList<Tool> AdvertisedTools = BuildAdvertisedTools();

    private static IReadOnlyList<Tool> BuildAdvertisedTools()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().WithDaqifiTools();
        using var provider = services.BuildServiceProvider();
        return provider.GetServices<McpServerTool>().Select(t => t.ProtocolTool).ToList();
    }
}
