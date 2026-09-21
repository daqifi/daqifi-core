using System.ComponentModel;
using System.Reflection;
using Daqifi.Mcp.Tools;
using ModelContextProtocol.Server;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// The agent-visible copy on each tool: one job and its failure mode, plus the README session
/// rules on the handshake. Novels belong in the MCP README, not in <c>tools/list</c>, which every
/// client pays for on every request.
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
    public void ServerInstructions_AreTheReadmeSessionRules()
    {
        var text = new ServerOptions().Instructions;

        Assert.Contains("discover_devices", text);
        Assert.Contains("firmware #703", text);
        Assert.Contains("Nyquist 3", text);
    }

    [Fact]
    public void ServerInstructions_MentionReadOnly_OnlyWhenTheServerIsReadOnly()
    {
        // Sent unconditionally this is both noise and a hint that writes might be refused on a
        // server where they will not be — on text every session pays for at initialize.
        Assert.DoesNotContain("--read-only", new ServerOptions().Instructions);

        var readOnly = new ServerOptions { ReadOnly = true }.Instructions;
        Assert.Contains("--read-only", readOnly);
        Assert.Contains("read_channel_values", readOnly);
        Assert.Contains("capture_samples", readOnly);
    }

    [Theory]
    [InlineData(nameof(DaqifiTools.DiscoverDevices), "timeoutMs")]
    [InlineData(nameof(DaqifiTools.ReadChannelValues), "timeoutMs")]
    [InlineData(nameof(DaqifiTools.CaptureSamples), "durationMs")]
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
