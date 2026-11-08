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
    /// <summary>What a writable server lists.</summary>
    internal static IReadOnlyList<McpServerPrompt> All { get; } = Advertised(new ServerOptions());

    /// <summary>What a <c>--read-only</c> server lists, which is deliberately less.</summary>
    internal static IReadOnlyList<McpServerPrompt> ReadOnly { get; } =
        Advertised(new ServerOptions { ReadOnly = true });

    private static IReadOnlyList<McpServerPrompt> Advertised(ServerOptions options)
    {
        var services = new ServiceCollection();
        services.AddMcpServer().WithDaqifiPrompts(options);
        using var provider = services.BuildServiceProvider();
        return provider.GetServices<McpServerPrompt>().ToList();
    }
}
