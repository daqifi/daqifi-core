using Daqifi.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// The tools exactly as the server advertises them: registered through the same
/// <c>WithDaqifiTools</c> call Program.cs makes, so every tool the server can list is
/// covered here — whatever class it lives in — and nothing it would not list is.
/// </summary>
internal static class AdvertisedMcpTools
{
    internal static IReadOnlyList<Tool> All { get; } = BuildAdvertisedTools();

    private static IReadOnlyList<Tool> BuildAdvertisedTools()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().WithDaqifiTools();
        using var provider = services.BuildServiceProvider();
        return provider.GetServices<McpServerTool>().Select(t => t.ProtocolTool).ToList();
    }
}
