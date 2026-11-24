using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Daqifi.Mcp.Tools;
using ModelContextProtocol.Server;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// The agent-visible copy on each tool: one job and its failure mode. Novels belong in the MCP
/// README, not in <c>tools/list</c>, which every client pays for on every request. The session
/// rules sent on the handshake are pinned by <see cref="ServerInstructionsHandshakeTests"/>.
/// </summary>
public class ToolDescriptionContractTests
{
    /// <summary>
    /// A guardrail, not a style rule: the descriptions this replaced ran past 900 characters, so
    /// this still catches an essay while leaving room for a job, its failure mode, and how to read
    /// the result.
    /// </summary>
    private const int MaxToolDescriptionChars = 450;
    private const int MaxParameterDescriptionChars = 200;

    [Fact]
    public void GetDeviceStatus_MentionsAnalogAndDigitalChannels()
    {
        var description = ToolDescription(nameof(DaqifiTools.GetDeviceStatus));

        Assert.Contains("analog", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("digital", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DownloadSdFile_WarnsUnlessTheFirmwareIsKnownToHaveTheFix()
    {
        // Firmware 3.7.3 fixed #703, so the warning is scoped. But discovery can report an empty
        // or "Unknown" version, so the default has to be the safe one: retrieve first unless the
        // device is known to be on 3.7.3 or later.
        var description = ToolDescription(nameof(DaqifiTools.DownloadSdFile));

        Assert.Contains("#703", description);
        Assert.Contains("Unless the device reports firmware 3.7.3 or later", description);
    }

    [Fact]
    public void SetPwmOutputFrequency_DescribesADeviceDependentMinimumNotAFixedRange()
    {
        // From firmware 3.9.0 the floor is the unit's own, computed from its clock (core#958), so
        // a fixed "6-50000" would steer an agent away from valid frequencies on some units and
        // into rejected ones on others.
        var description = ParameterDescription(nameof(DaqifiTools.SetPwmOutput), "frequencyHz");

        Assert.DoesNotContain("6-50000", description);
        Assert.Contains("3.9.0", description);
        Assert.Contains("6 Hz", description);
        Assert.Contains("50000", description);
    }

    [Theory]
    [InlineData(nameof(DaqifiTools.DiscoverDevices), "timeoutMs")]
    [InlineData(nameof(DaqifiTools.ReadChannelValues), "timeoutMs")]
    [InlineData(nameof(DaqifiTools.CaptureSamples), "durationMs")]
    [InlineData(nameof(DaqifiTools.DriveAndCapture), "durationMs")]
    [InlineData(nameof(DaqifiTools.DriveAndCapture), "delayMs")]
    [InlineData(nameof(DaqifiTools.DriveAndCapture), "pulseWidthMs")]
    public void TimeoutParameters_StateTheirClampWithoutRetellingTheBench(string method, string parameter)
    {
        var description = ParameterDescription(method, parameter);

        // The clamp is the part an agent cannot get anywhere else — a budget below the floor is
        // silently raised, so a caller that does not know the floor misreads what it asked for.
        Assert.Contains("clamped to", description);
        Assert.InRange(description.Length, 1, MaxParameterDescriptionChars);
    }

    [Fact]
    public void EveryToolDescription_IsOneJobAndAFailureMode()
    {
        foreach (var method in ToolMethods())
        {
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
            Assert.False(string.IsNullOrWhiteSpace(description), method.Name);
            Assert.True(
                description!.Length <= MaxToolDescriptionChars,
                $"{method.Name} is {description.Length} chars");
        }
    }

    [Fact]
    public void EveryParameterDescription_StaysShortOfANovel()
    {
        foreach (var method in ToolMethods())
        {
            foreach (var parameter in method.GetParameters())
            {
                var description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description;
                if (description is null)
                {
                    continue;
                }

                Assert.True(
                    description.Length <= MaxParameterDescriptionChars,
                    $"{method.Name}.{parameter.Name} is {description.Length} chars");
            }
        }
    }

    [Fact]
    public void Prose_SpellsEveryNameTheWayTheWireDoes()
    {
        // Keys are camelCase and tool names are snake_case, so a snake_case word in prose is a
        // tool, a prompt or an enum value, and a camelCase one is a parameter or a result field.
        // Anything else is a name that reads as callable and is not ("device_id", issue #1042).
        var schemas = AdvertisedMcpTools.All
            .SelectMany(t => new[] { t.InputSchema, t.OutputSchema!.Value })
            .ToList();
        var keys = schemas.SelectMany(PropertyNames).ToHashSet(StringComparer.Ordinal);
        var snakeCase = AdvertisedMcpTools.All.Select(t => t.Name)
            .Concat(AdvertisedMcpPrompts.All.Select(p => p.ProtocolPrompt.Name))
            .Concat(schemas.SelectMany(EnumValues))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var text in AgentVisibleProse())
        {
            foreach (Match word in Regex.Matches(text, @"\b[a-z]+(?:_[a-z]+)+\b"))
            {
                Assert.True(snakeCase.Contains(word.Value), $"'{word.Value}' names nothing on the wire: {text}");
            }

            foreach (Match word in Regex.Matches(text, @"\b[a-z]+(?:[A-Z][a-z]+)+\b"))
            {
                Assert.True(keys.Contains(word.Value), $"'{word.Value}' names nothing on the wire: {text}");
            }
        }
    }

    private static IEnumerable<string> AgentVisibleProse()
    {
        yield return new ServerOptions { ReadOnly = true }.Instructions;

        foreach (var method in ToolMethods())
        {
            yield return method.GetCustomAttribute<DescriptionAttribute>()!.Description;
            foreach (var parameter in method.GetParameters())
            {
                if (parameter.GetCustomAttribute<DescriptionAttribute>() is { } description)
                {
                    yield return description.Description;
                }
            }
        }
    }

    private static IEnumerable<string> PropertyNames(JsonElement schema) =>
        Nodes(schema)
            .Where(n => n.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object)
            .SelectMany(n => n.GetProperty("properties").EnumerateObject().Select(p => p.Name));

    private static IEnumerable<string> EnumValues(JsonElement schema) =>
        Nodes(schema)
            .Where(n => n.TryGetProperty("enum", out var e) && e.ValueKind == JsonValueKind.Array)
            .SelectMany(n => n.GetProperty("enum").EnumerateArray())
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString()!);

    private static IEnumerable<JsonElement> Nodes(JsonElement node) =>
        node.ValueKind switch
        {
            JsonValueKind.Object => node.EnumerateObject().SelectMany(p => Nodes(p.Value)).Prepend(node),
            JsonValueKind.Array => node.EnumerateArray().SelectMany(Nodes),
            _ => [],
        };

    private static IEnumerable<MethodInfo> ToolMethods() =>
        typeof(DaqifiTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    private static string ToolDescription(string methodName) =>
        typeof(DaqifiTools).GetMethod(methodName)!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

    private static string ParameterDescription(string methodName, string parameterName) =>
        typeof(DaqifiTools).GetMethod(methodName)!
            .GetParameters()
            .Single(p => p.Name == parameterName)
            .GetCustomAttribute<DescriptionAttribute>()!.Description;
}
