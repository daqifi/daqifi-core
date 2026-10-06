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
    /// Every tool the server registers. All of them return records or lists, so all of them need
    /// the flag — this is not a place to opt out. The names come from
    /// <see cref="AdvertisedMcpTools"/>, not a copied list, so a tool added through
    /// <c>WithDaqifiTools</c> is covered here without a second edit.
    /// </summary>
    public static TheoryData<string> AdvertisedToolNames()
    {
        var data = new TheoryData<string>();
        foreach (var tool in AdvertisedMcpTools.All)
        {
            data.Add(tool.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AdvertisedToolNames))]
    public void Tool_AdvertisesAnOutputSchema(string name)
    {
        // Read from the registered tool, i.e. what a client sees in tools/list. The attribute
        // getter can tell unset (false) from true, but it cannot tell us the schema actually made
        // it onto the wire.
        var schema = AdvertisedMcpTools.All.Single(t => t.Name == name).OutputSchema;

        Assert.True(
            schema.HasValue,
            $"{name} has no OutputSchema; set UseStructuredContent = true on [McpServerTool].");
        Assert.Equal(JsonValueKind.Object, schema.Value.ValueKind);
    }

    [Fact]
    public async Task NullField_IsStillWritten_SoTheResultMatchesItsOutputSchema()
    {
        // The generated schema lists every record property as required, a nullable one as
        // ["T","null"], but the SDK's default serializer drops null properties. A result with a
        // null field would then be missing a required key, and a client that checks results
        // against the schema rejects it. get_server_info with the version check off is the one
        // tool that returns a null field (latestVersion) with no device attached.
        var services = new ServiceCollection();
        services.AddSingleton(new VersionStatus(
            new ServerOptions { VersionCheck = false },
            new StubLatestVersionSource()));
        services.AddMcpServer().WithDaqifiTools();
        await using var provider = services.BuildServiceProvider();
        var tool = provider.GetServices<McpServerTool>()
            .Single(t => t.ProtocolTool.Name == "get_server_info");
        await using var server = McpServer.Create(
            new StreamServerTransport(Stream.Null, Stream.Null),
            new McpServerOptions(),
            serviceProvider: provider);
        var request = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Method = RequestMethods.ToolsCall },
            new CallToolRequestParams { Name = "get_server_info" })
        {
            Services = provider,
        };

        var result = await tool.InvokeAsync(request, CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);
        var structured = result.StructuredContent.Value;
        var required = tool.ProtocolTool.OutputSchema!.Value.GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();
        Assert.Contains("latestVersion", required);
        foreach (var name in required)
        {
            Assert.True(
                structured.TryGetProperty(name, out _),
                $"structuredContent is missing required '{name}': {structured.GetRawText()}");
        }

        Assert.Equal(JsonValueKind.Null, structured.GetProperty("latestVersion").ValueKind);

        // The text block is serialized with the same options, so it spells the null out too.
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        using var textJson = JsonDocument.Parse(text);
        Assert.Equal(JsonValueKind.Null, textJson.RootElement.GetProperty("latestVersion").ValueKind);
    }
}
