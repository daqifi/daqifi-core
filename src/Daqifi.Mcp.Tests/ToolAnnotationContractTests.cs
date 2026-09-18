using System.Reflection;
using Daqifi.Mcp.Tools;
using ModelContextProtocol.Server;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// Contract tests for the MCP tool annotations advertised to clients. SDK 2.2 defaults
/// <c>ReadOnly=false</c>, <c>Destructive=true</c>, <c>OpenWorld=true</c> — and only copies a
/// hint into <c>tools/list</c> when the attribute backing field is actually set. Left unset,
/// <c>get_server_info</c> and <c>list_channels</c> look like mutating, destructive, open-world
/// calls, so clients that honor hints confirm-prompt or hide the wrong surface.
/// </summary>
public class ToolAnnotationContractTests
{
    /// <summary>
    /// Every tool, with the hints it must advertise. A tool added to <see cref="DaqifiTools"/>
    /// without a row here fails <see cref="EveryAdvertisedTool_HasARowInTheHintTable"/> rather
    /// than shipping SDK defaults; a row whose values disagree with the attribute fails
    /// <see cref="Tool_AdvertisesTheHintsClientsShouldHonor"/>.
    /// </summary>
    public static TheoryData<string, bool, bool, bool> ExpectedHints()
    {
        var data = new TheoryData<string, bool, bool, bool>();
        foreach (var (name, readOnly, destructive, openWorld) in Expected)
        {
            data.Add(name, readOnly, destructive, openWorld);
        }

        return data;
    }

    // OpenWorld is false across the board: this server talks to DAQiFi devices (and itself), not
    // an unpredictable set of entities. Destructive marks the calls a client should confirm: the
    // irreversible delete_sd_file, and every tool that changes what the device drives onto a pin
    // (digital level/direction, PWM, DAC), since that reaches whatever circuit is wired to it.
    // Channel configuration and the sample rate stay non-destructive — they lose no data and are
    // undone by calling the same tool again. ReadOnly is
    // true for pure reads; read_channel_values / capture_samples stay false because they start
    // streaming when the device is idle. download_sd_file writes host files, so it is not ReadOnly
    // even though --read-only still allows it.
    private static readonly (string Name, bool ReadOnly, bool Destructive, bool OpenWorld)[] Expected =
    {
        ("get_server_info", true, false, false),
        ("discover_devices", true, false, false),
        ("connect_device", false, false, false),
        ("disconnect_device", false, false, false),
        ("list_connected_devices", true, false, false),
        ("get_device_status", true, false, false),
        ("list_channels", true, false, false),
        ("configure_analog_channels", false, false, false),
        ("configure_digital_channels", false, false, false),
        ("set_digital_direction", false, true, false),
        ("set_digital_output", false, true, false),
        ("set_pwm_output", false, true, false),
        ("disable_pwm", false, true, false),
        ("list_analog_outputs", true, false, false),
        ("set_analog_output", false, true, false),
        ("latch_analog_outputs", false, true, false),
        ("read_analog_output", true, false, false),
        ("set_sample_rate", false, false, false),
        ("start_sd_logging", false, false, false),
        ("stop_sd_logging", false, false, false),
        ("list_sd_files", true, false, false),
        ("get_sd_storage", true, false, false),
        ("download_sd_file", false, false, false),
        ("delete_sd_file", false, true, false),
        ("read_channel_values", false, false, false),
        ("capture_samples", false, false, false),
    };

    [Theory]
    [MemberData(nameof(ExpectedHints))]
    public void Tool_AdvertisesTheHintsClientsShouldHonor(
        string name, bool readOnly, bool destructive, bool openWorld)
    {
        // McpServerTool.Create is what WithToolsFromAssembly uses; ProtocolTool.Annotations is
        // what a client sees. Attribute getters cannot tell "unset" from "set to the SDK default",
        // so this is the only way to catch a tool that never set Destructive or OpenWorld at all.
        var annotations = McpServerTool.Create(Method(name)).ProtocolTool.Annotations;

        Assert.NotNull(annotations);
        Assert.Equal(readOnly, annotations.ReadOnlyHint);
        Assert.Equal(destructive, annotations.DestructiveHint);
        Assert.Equal(openWorld, annotations.OpenWorldHint);
    }

    [Fact]
    public void EveryAdvertisedTool_HasARowInTheHintTable()
    {
        var advertised = ToolMethods()
            .Select(NameOf)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        var tabulated = Expected
            .Select(h => h.Name)
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
                BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    private static string NameOf(MethodInfo method) =>
        method.GetCustomAttribute<McpServerToolAttribute>()!.Name
        ?? throw new InvalidOperationException($"{method.Name} has no tool name.");
}
