using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;

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

    /// <summary>
    /// Registers every tool in this assembly with <see cref="SerializerOptions"/>, and the
    /// <c>tools/call</c> filter that explains an argument-binding failure (issue #917).
    /// </summary>
    /// <remarks>
    /// The tools are registered exactly as the SDK builds them. An earlier attempt wrapped each
    /// one in a <c>DelegatingMcpServerTool</c>, which costs the wire contract: the SDK reshapes
    /// <c>outputSchema</c> for the protocol version a client negotiates by testing the
    /// registered instance's concrete type, so a wrapped tool silently skipped the reshaping and
    /// six tools advertised a non-object <c>outputSchema</c> while still returning the
    /// <c>{"result": ...}</c> envelope. The filter leaves the instances alone.
    /// </remarks>
    internal static IMcpServerBuilder WithDaqifiTools(this IMcpServerBuilder builder) =>
        builder
            .WithToolsFromAssembly(typeof(DaqifiTools).Assembly, SerializerOptions)
            .ExplainArgumentBindingFailures();

    /// <summary>
    /// Registers every MCP resource in this assembly — the published board specifications a user
    /// can attach to a session.
    /// </summary>
    /// <remarks>
    /// No serializer options to pass: these resources return JSON they serialized themselves, so
    /// that the resource and <c>get_hardware_info</c> spell the same field the same way.
    /// </remarks>
    internal static IMcpServerBuilder WithDaqifiResources(this IMcpServerBuilder builder) =>
        builder.WithResourcesFromAssembly(typeof(DaqifiTools).Assembly);

    /// <summary>
    /// Registers the ready-made measurement jobs a client offers as starting points — only the
    /// ones <paramref name="options"/> allows.
    /// </summary>
    /// <remarks>
    /// Registered class by class rather than from the assembly, which is the whole point: a
    /// <c>--read-only</c> server must not list a job that opens by enabling channels, because it
    /// will refuse that on the first tool call. See <see cref="Prompts.ReadOnlyPrompts"/>.
    /// </remarks>
    internal static IMcpServerBuilder WithDaqifiPrompts(this IMcpServerBuilder builder, ServerOptions options)
    {
        // The Type overload rather than WithPrompts<T>: these are static classes, which cannot be
        // type arguments. Typed as IEnumerable<Type> on purpose — a Type[] binds instead to
        // WithPrompts<TPromptType>(builder, instance, options), taking the array itself as a
        // prompt instance. That compiles, registers nothing, and leaves prompts/list answering
        // "method not available".
        IEnumerable<Type> promptTypes = options.ReadOnly
            ? [typeof(Prompts.ReadOnlyPrompts)]
            : [typeof(Prompts.ReadOnlyPrompts), typeof(Prompts.DaqifiPrompts)];

        return builder.WithPrompts(promptTypes, SerializerOptions);
    }

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
