using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Daqifi.Mcp.Tools;

/// <summary>
/// Turns an argument-binding failure into a sentence the caller can act on (issue #917).
/// </summary>
/// <remarks>
/// <para>
/// The tool bodies in <see cref="DaqifiTools"/> turn their own failures into
/// <see cref="McpException"/>, whose message the SDK puts in the <c>tools/call</c> result. Binding
/// happens earlier, in the SDK's parameter marshaller, so the body is never entered: the
/// marshaller's <see cref="ArgumentException"/>/<see cref="JsonException"/> only reached the
/// server's own handler, which logged it to stderr — where an MCP client never looks — and
/// answered with a bare "An error occurred invoking '&lt;tool&gt;'." The caller of an MCP tool is
/// a model, and that text is the only thing it has to correct itself with.
/// </para>
/// <para>
/// This runs as a <c>tools/call</c> filter, which the SDK builds around the handler that invokes
/// the tool, so it sees the exception the handler would otherwise absorb while leaving the tool
/// registrations — and therefore <c>tools/list</c> — exactly as the SDK built them.
/// </para>
/// </remarks>
internal static class ArgumentBindingErrors
{
    /// <summary>
    /// How the marshaller names the parameter it had no value for. Matched rather than trusted:
    /// the name is used only when the tool's own schema agrees it is a parameter and the call
    /// really did omit it, so a reworded SDK message costs the nicety, not correctness.
    /// </summary>
    private static readonly Regex MissingParameterName = new(
        @"required parameter '(?<name>[^']+)'",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Adds the <c>tools/call</c> filter that explains a binding failure. An exception it cannot
    /// attribute to an argument is left alone, so a failure from anywhere else keeps whatever the
    /// SDK does with it today.
    /// </summary>
    internal static IMcpServerBuilder ExplainArgumentBindingFailures(this IMcpServerBuilder builder) =>
        builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException
                && Explain(ex, SchemaOf(context), context.Params?.Arguments) is { } message)
            {
                throw new McpException(message, ex);
            }
        }));

    /// <summary>
    /// The sentence to send back for a binding failure, or <see langword="null"/> when the
    /// exception cannot be pinned on one of the arguments and should be left as it is.
    /// </summary>
    internal static string? Explain(
        Exception ex,
        JsonElement inputSchema,
        IDictionary<string, JsonElement>? arguments)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object
            || !inputSchema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (ex is ArgumentException)
        {
            // The marshaller already knows which parameter it wanted; naming it is the whole fix.
            var match = MissingParameterName.Match(ex.Message);
            if (!match.Success)
            {
                return null;
            }

            var missing = match.Groups["name"].Value;
            return properties.TryGetProperty(missing, out _)
                && (arguments is null || !arguments.ContainsKey(missing))
                ? $"{missing} is required."
                : null;
        }

        // A wrong-typed value does not name the parameter: the marshaller deserializes each
        // argument on its own, so the JsonException's path is "$" (or "$[0]" inside an array).
        // The arguments that were sent are all the caller's, though, so the tool's schema says
        // which one does not fit. Deciding from the schema is also what keeps this narrow — a
        // JsonException from somewhere other than binding, such as serializing a result, leaves
        // every argument fitting and is handed back untouched.
        if (arguments is null)
        {
            return null;
        }

        foreach (var property in properties.EnumerateObject())
        {
            if (arguments.TryGetValue(property.Name, out var value)
                && Mismatch(property.Value, value) is { } expected)
            {
                return $"{property.Name} must be {expected}.";
            }
        }

        return null;
    }

    /// <summary>
    /// The input schema of the tool this call names, as the server registered it, or
    /// <c>default</c> when it cannot be found.
    /// </summary>
    private static JsonElement SchemaOf(RequestContext<CallToolRequestParams> context)
    {
        var name = context.Params?.Name;
        if (name is null || context.Services is null)
        {
            return default;
        }

        return context.Services.GetServices<McpServerTool>()
            .FirstOrDefault(tool => tool.ProtocolTool.Name == name)
            ?.ProtocolTool.InputSchema
            ?? default;
    }

    /// <summary>
    /// How <paramref name="schema"/> describes the value it wanted, when
    /// <paramref name="value"/> is not one — otherwise <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// "Fits" means what the marshaller actually accepts, not what JSON Schema says: its
    /// serializer reads a number from a string, and takes a JSON null for a string, array or
    /// object parameter. Being stricter here would blame a parameter that bound perfectly well
    /// for another one's error, which is the very thing #917 is about.
    /// </remarks>
    private static string? Mismatch(JsonElement schema, JsonElement value)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type))
        {
            return null;
        }

        var accepted = TypeNames(type);
        if (accepted.Count == 0 || accepted.Exists(t => Fits(t, value)))
        {
            // An array whose elements are wrong is still an array, so look one level in: the
            // marshaller's path for that is "$[0]", which still does not name the parameter.
            if (value.ValueKind == JsonValueKind.Array
                && schema.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Object
                && items.TryGetProperty("type", out var itemType))
            {
                var itemTypes = TypeNames(itemType);
                if (itemTypes.Count > 0
                    && value.EnumerateArray().Any(item => !itemTypes.Exists(t => Fits(t, item))))
                {
                    return $"an array of {Plural(Named(itemTypes))}";
                }
            }

            return null;
        }

        return Article(Named(accepted));
    }

    /// <summary>The JSON Schema type names a value may have, "null" included.</summary>
    private static List<string> TypeNames(JsonElement type) => type.ValueKind switch
    {
        JsonValueKind.String => [type.GetString()!],
        JsonValueKind.Array => [.. type.EnumerateArray()
            .Where(t => t.ValueKind == JsonValueKind.String)
            .Select(t => t.GetString()!)],
        _ => [],
    };

    /// <summary>
    /// The type to name in the message. A nullable parameter is written <c>["string","null"]</c>,
    /// and "must be null" is no help to anyone.
    /// </summary>
    private static string Named(List<string> types) =>
        types.Find(t => t != "null") ?? types[0];

    private static bool Fits(string type, JsonElement value) => type switch
    {
        // A JSON null binds to a string, array or object parameter; it is only the value types
        // below that refuse it.
        "string" => value.ValueKind is JsonValueKind.String or JsonValueKind.Null,
        "array" => value.ValueKind is JsonValueKind.Array or JsonValueKind.Null,
        "object" => value.ValueKind is JsonValueKind.Object or JsonValueKind.Null,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        // The serializer reads a number out of a string ("100" binds to an int parameter), so a
        // string that parses is not a mismatch.
        "number" => IsNumber(value),
        "integer" => IsNumber(value) && IsWholeNumber(value),
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static bool IsNumber(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => true,
        JsonValueKind.String => double.TryParse(
            value.GetString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out _),
        _ => false,
    };

    /// <summary>
    /// Whether the value is a whole number the marshaller can hold. A fraction and a number too
    /// large for an integer parameter fail the same way, and "must be an integer" covers both.
    /// </summary>
    private static bool IsWholeNumber(JsonElement value) => value.ValueKind == JsonValueKind.Number
        ? value.TryGetInt64(out _)
        : long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    private static string Article(string type) => type switch
    {
        "integer" => "an integer",
        "number" => "a number",
        "string" => "a string",
        "boolean" => "true or false",
        "array" => "an array",
        "object" => "an object",
        _ => $"of type {type}",
    };

    private static string Plural(string type) => type switch
    {
        "integer" => "integers",
        "number" => "numbers",
        "string" => "strings",
        "boolean" => "booleans",
        _ => $"{type} values",
    };
}
