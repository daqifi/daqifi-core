using System.ComponentModel;
using System.Reflection;
using Daqifi.Mcp.Tools;
using ModelContextProtocol.Server;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// The agent-visible copy on each tool: one job and its failure mode, plus the four README
/// session rules on the handshake. Novels belong in the MCP README, not in <c>tools/list</c>.
/// </summary>
public class ToolDescriptionContractTests
{
    /// <summary>
    /// Long enough for a job plus a failure mode; short enough that the handshake-bench essays
    /// cannot sneak back onto a tool or parameter.
    /// </summary>
    private const int MaxToolDescriptionChars = 400;
    private const int MaxParameterDescriptionChars = 200;

    [Fact]
    public void GetDeviceStatus_MentionsAnalogAndDigitalChannels()
    {
        var description = ToolDescription(nameof(DaqifiTools.GetDeviceStatus));

        Assert.Contains("analog", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("digital", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServerInstructions_AreTheFourReadmeSessionRules()
    {
        var text = ServerOptions.Instructions;

        Assert.Contains("discover_devices", text);
        Assert.Contains("firmware #703", text);
        Assert.Contains("Nyquist 3", text);
        Assert.Contains("--read-only", text);
        Assert.Contains("read_channel_values", text);
        Assert.Contains("capture_samples", text);
    }

    [Theory]
    [InlineData(nameof(DaqifiTools.DiscoverDevices), "timeoutMs")]
    [InlineData(nameof(DaqifiTools.ReadChannelValues), "timeoutMs")]
    [InlineData(nameof(DaqifiTools.CaptureSamples), "durationMs")]
    public void TimeoutParameters_DoNotRetellHandshakeBenches(string method, string parameter)
    {
        var description = ParameterDescription(method, parameter);

        Assert.DoesNotContain("slowest supported hosts", description);
        Assert.DoesNotContain("bench-measured", description);
        Assert.DoesNotContain("~100 ms", description);
        Assert.DoesNotContain("1 kHz", description);
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
