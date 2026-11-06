using Daqifi.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// The prompts exactly as the server advertises them: registered through the same
/// <c>WithDaqifiPrompts</c> call Program.cs makes, so every prompt the server can list is covered
/// here — whatever class it lives in — and nothing it would not list is.
/// </summary>
internal static class AdvertisedMcpPrompts
{
    internal static IReadOnlyList<McpServerPrompt> All { get; } = BuildAdvertisedPrompts();

    private static IReadOnlyList<McpServerPrompt> BuildAdvertisedPrompts()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().WithDaqifiPrompts();
        using var provider = services.BuildServiceProvider();
        return provider.GetServices<McpServerPrompt>().ToList();
    }
}
