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

    private static Tool Advertised(string name) =>
        AdvertisedTools.Single(t => t.Name == name);

    /// <summary>
    /// The tools exactly as the server advertises them: registered through the same
    /// <c>WithToolsFromAssembly</c> call Program.cs makes, so every tool the server can list is
    /// covered here — whatever class it lives in — and nothing it would not list is.
    /// </summary>
    private static readonly IReadOnlyList<Tool> AdvertisedTools = BuildAdvertisedTools();

    private static IReadOnlyList<Tool> BuildAdvertisedTools()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().WithToolsFromAssembly(typeof(DaqifiTools).Assembly);
        using var provider = services.BuildServiceProvider();
        return provider.GetServices<McpServerTool>().Select(t => t.ProtocolTool).ToList();
    }
}
