using Daqifi.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// The resources exactly as the server advertises them: registered through the same
/// <c>WithDaqifiResources</c> call Program.cs makes, so every resource the server can list is
/// covered here — whatever class it lives in — and nothing it would not list is.
/// </summary>
internal static class AdvertisedMcpResources
{
    internal static IReadOnlyList<McpServerResource> All { get; } = BuildAdvertisedResources();

    private static IReadOnlyList<McpServerResource> BuildAdvertisedResources()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().WithDaqifiResources();
        using var provider = services.BuildServiceProvider();
        return provider.GetServices<McpServerResource>().ToList();
    }
}
