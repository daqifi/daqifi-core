using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;

namespace Daqifi.Mcp.Tools;

/// <summary>
/// Registers the tool surface with the MCP server. Program.cs and the contract tests both go
/// through <see cref="WithDaqifiTools"/>, so what the tests pin is what a client is served.
/// </summary>
internal static class ToolRegistration
{
    /// <summary>
    /// The options every tool result is serialized with, for both the text block and
    /// <c>structuredContent</c>. They are the SDK defaults except that null properties are
    /// written. The SDK omits them by default, but the <c>outputSchema</c> it generates lists
    /// every record property as required (a nullable one as <c>["T","null"]</c>). Left on the
    /// default, a result with a null field has a required key missing, and a client that checks
    /// results against the schema rejects it.
    /// </summary>
    internal static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    /// <summary>Registers every tool in this assembly with <see cref="SerializerOptions"/>.</summary>
    internal static IMcpServerBuilder WithDaqifiTools(this IMcpServerBuilder builder) =>
        builder.WithToolsFromAssembly(typeof(DaqifiTools).Assembly, SerializerOptions);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.MakeReadOnly();
        return options;
    }
}
