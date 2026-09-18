using System.Reflection;
using System.Text.Json;
using Daqifi.Mcp.Tools;
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
        // McpServerTool.Create is what WithToolsFromAssembly uses; ProtocolTool.OutputSchema is
        // what a client sees in tools/list. The attribute getter can tell unset (false) from
        // true, but it cannot tell us the schema actually made it onto the wire.
        var schema = McpServerTool.Create(Method(name)).ProtocolTool.OutputSchema;

        Assert.True(
            schema.HasValue,
            $"{name} has no OutputSchema; set UseStructuredContent = true on [McpServerTool].");
        Assert.Equal(JsonValueKind.Object, schema.Value.ValueKind);
    }

    [Fact]
    public void EveryAdvertisedTool_HasARowInTheStructuredContentTable()
    {
        var advertised = ToolMethods()
            .Select(NameOf)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        var tabulated = Expected
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(tabulated, advertised);
    }

    private static MethodInfo Method(string name) =>
        ToolMethods().Single(m => NameOf(m) == name);

    // Mirrors WithToolsFromAssembly: every [McpServerToolType] in the server assembly, not just
    // DaqifiTools, so a tool added in a new class cannot skip this contract.
    private static IEnumerable<MethodInfo> ToolMethods() =>
        typeof(DaqifiTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    private static string NameOf(MethodInfo method) =>
        method.GetCustomAttribute<McpServerToolAttribute>()!.Name
        ?? throw new InvalidOperationException($"{method.Name} has no tool name.");
}
