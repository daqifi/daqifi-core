using System.Diagnostics;
using System.Text.Json;
using Daqifi.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// What a caller is told when its arguments never bind (#917).
/// </summary>
/// <remarks>
/// <para>
/// A tool that runs and fails explains itself, because its body turns the failure into an
/// <see cref="McpException"/>. Binding fails earlier, in the SDK's parameter marshaller, so the
/// body is never entered: before this the reason went to stderr, which an MCP client discards,
/// and the caller got "An error occurred invoking '&lt;tool&gt;'." The caller is a model and the
/// error text is the only thing it can correct itself with, so these pin the two things it needs
/// — the name of the parameter that was missing, and the name and expected type of the one that
/// was wrong.
/// </para>
/// <para>
/// The cases feed <see cref="ArgumentBindingErrors.Explain"/> the exception the real marshaller
/// really threw, obtained by invoking the registered tool, rather than one written out here: the
/// message wording and which exception type comes out of which failure are the SDK's to choose.
/// <see cref="ArgumentBindingErrorTests.TheReason_ReachesAClientInTheToolResult"/> then proves
/// the filter is wired up and the text survives the trip to a client.
/// </para>
/// </remarks>
public class ArgumentBindingErrorTests
{
    public static TheoryData<string, string, string> BindingFailures() => new()
    {
        // The issue's own two cases: the parameter name a caller had to read stderr to learn...
        { "get_device_status", "{}", "deviceId is required." },
        { "set_sample_rate", """{"deviceId":"serial:FAKE"}""", "sampleRateHz is required." },
        // ...and the type it should have sent, which the marshaller's JsonException does not
        // name at all (its path is only "$": each argument is deserialized on its own).
        { "set_sample_rate", """{"deviceId":"serial:FAKE","sampleRateHz":"fast"}""", "sampleRateHz must be an integer." },
        { "set_sample_rate", """{"deviceId":"serial:FAKE","sampleRateHz":1.5}""", "sampleRateHz must be an integer." },
        { "set_sample_rate", """{"deviceId":"serial:FAKE","sampleRateHz":null}""", "sampleRateHz must be an integer." },
        // Too large for the parameter fails the same way a fraction does, and one sentence covers
        // both: what the caller has to do is send a sample rate that exists.
        {
            "set_sample_rate",
            """{"deviceId":"serial:FAKE","sampleRateHz":99999999999999999999}""",
            "sampleRateHz must be an integer."
        },
        { "get_device_status", """{"deviceId":5}""", "deviceId must be a string." },
        { "set_analog_output", """{"deviceId":"serial:FAKE","channel":0,"volts":"high"}""", "volts must be a number." },
        {
            "set_digital_output",
            """{"deviceId":"serial:FAKE","channel":1,"high":"yes"}""",
            "high must be true or false."
        },
        {
            "configure_analog_channels",
            """{"deviceId":"serial:FAKE","enabledChannels":"0,1"}""",
            "enabledChannels must be an array."
        },
        // A list of the wrong thing is still a list, so the item type is worth saying. The
        // marshaller's path for this one is "$[0]", which still does not name the parameter.
        {
            "configure_analog_channels",
            """{"deviceId":"serial:FAKE","enabledChannels":[true,false]}""",
            "enabledChannels must be an array of integers."
        },
        // The marshaller takes an explicit null for a string. Blaming it for the sibling's error
        // would be the very failure #917 is about, just moved: a caller told to fix the one
        // argument that was already right.
        {
            "configure_analog_channels",
            """{"deviceId":null,"enabledChannels":"0,1"}""",
            "enabledChannels must be an array."
        },
        {
            "start_sd_logging",
            """{"deviceId":"serial:FAKE","fileName":7}""",
            "fileName must be a string."
        },
    };

    [Theory]
    [MemberData(nameof(BindingFailures))]
    public async Task ABindingFailure_NamesTheParameterAndWhatItWanted(
        string tool,
        string argumentsJson,
        string expected)
    {
        Assert.Equal(expected, await ExplainAsync(tool, argumentsJson));
    }

    public static TheoryData<string, string, string> EnumMismatches() => new()
    {
        {
            "set_digital_direction",
            """{"deviceId":"serial:FAKE","channel":1,"direction":"sideways"}""",
            "direction must be one of 'input', 'output'."
        },
        // Exactly what the schema lists: no aliases, no other case, no number behind the name.
        {
            "set_digital_direction",
            """{"deviceId":"serial:FAKE","channel":1,"direction":"OUTPUT"}""",
            "direction must be one of 'input', 'output'."
        },
        {
            "set_digital_direction",
            """{"deviceId":"serial:FAKE","channel":1,"direction":1}""",
            "direction must be one of 'input', 'output'."
        },
        // The one the marshaller would have let through: it reads a list as flags, so this bound,
        // to output.
        {
            "set_digital_direction",
            """{"deviceId":"serial:FAKE","channel":1,"direction":"input, output"}""",
            "direction must be one of 'input', 'output'."
        },
        {
            "start_sd_logging",
            """{"deviceId":"serial:FAKE","format":"parquet"}""",
            "format must be one of 'protobuf', 'json', 'csv'."
        },
        {
            "get_hardware_info",
            """{"deviceType":"nyquist1, nyquist2"}""",
            "deviceType must be one of 'unknown', 'nyquist1', 'nyquist2', 'nyquist3'."
        },
    };

    [Theory]
    [MemberData(nameof(EnumMismatches))]
    public void AValueOutsideTheAdvertisedEnum_IsRefusedWithTheValuesThatAreInIt(
        string tool, string argumentsJson, string expected)
    {
        Assert.Equal(expected, ArgumentBindingErrors.EnumMismatch(SchemaOf(tool), Arguments(argumentsJson)));
    }

    [Theory]
    [InlineData("set_digital_direction", """{"deviceId":"serial:FAKE","channel":1,"direction":"output"}""")]
    [InlineData("set_digital_direction", """{"deviceId":"serial:FAKE","channel":1}""")]
    [InlineData("start_sd_logging", """{"deviceId":"serial:FAKE"}""")]
    [InlineData("get_hardware_info", "{}")]
    [InlineData("get_hardware_info", """{"deviceType":null}""")]
    [InlineData("get_hardware_info", """{"deviceType":"nyquist2"}""")]
    public void AValueInTheAdvertisedEnum_OrNoneAtAll_IsNotRefused(string tool, string argumentsJson)
    {
        Assert.Null(ArgumentBindingErrors.EnumMismatch(SchemaOf(tool), Arguments(argumentsJson)));
    }

    public static TheoryData<string, string> CallsThatBind() => new()
    {
        // The marshaller is more forgiving than the schema it advertises, and anything it accepts
        // must never be reported as a mismatch — otherwise an argument that bound perfectly well
        // gets the blame for another one's error. These are the leniencies it actually has.
        { "set_sample_rate", """{"deviceId":"serial:FAKE","sampleRateHz":"100"}""" },
        { "configure_analog_channels", """{"deviceId":"serial:FAKE","enabledChannels":["0","1"]}""" },
        { "configure_analog_channels", """{"deviceId":"serial:FAKE","enabledChannels":null}""" },
        { "get_device_status", """{"deviceId":null}""" },
        { "start_sd_logging", """{"deviceId":"serial:FAKE","fileName":null}""" },
        // An argument no parameter is named for is ignored rather than refused, which is how a
        // caller that guessed "portName" gets as far as the body at all.
        { "get_device_status", """{"deviceId":"serial:FAKE","portName":"/dev/cu.usbmodem2101"}""" },
    };

    [Theory]
    [MemberData(nameof(CallsThatBind))]
    public async Task ACallTheMarshallerAccepts_IsNotReportedAsABindingFailure(
        string tool,
        string argumentsJson)
    {
        // Reaching the body is what proves it bound: the fake device is registered under
        // AgentHarness.DeviceId, so a bound call either succeeds or fails on the device.
        var failure = await Record.ExceptionAsync(() => InvokeAsync(tool, argumentsJson));

        Assert.True(
            failure is null or McpException,
            $"expected the call to bind, but the marshaller threw {failure?.GetType().Name}: {failure?.Message}");
    }

    [Fact]
    public async Task AFailureInsideTheToolBody_StillCarriesItsOwnMessage()
    {
        // A tool that was entered and refused the call is already explaining itself, and must
        // keep doing so verbatim.
        var failure = await Assert.ThrowsAsync<McpException>(
            () => InvokeAsync("set_sample_rate", """{"deviceId":"serial:FAKE","sampleRateHz":0}"""));

        Assert.Equal("sampleRateHz must be >= 1.", failure.Message);
    }

    [Fact]
    public void AJsonFailureThatIsNotAboutTheArguments_IsLeftAlone()
    {
        // Every tool is UseStructuredContent = true, so serializing a result throws JsonException
        // too — the same type binding throws. The arguments are the only evidence worth deciding
        // on: when they all fit, nothing here can explain the failure and it is handed back as
        // it is rather than rewritten into binding language.
        var schema = AdvertisedMcpTools.All.Single(t => t.Name == "set_sample_rate").InputSchema;
        var arguments = Arguments("""{"deviceId":"serial:FAKE","sampleRateHz":100}""");

        var explained = ArgumentBindingErrors.Explain(
            new JsonException("The JSON value could not be converted to System.Int32. Path: $"),
            schema,
            arguments);

        Assert.Null(explained);
    }

    [Fact]
    public void AnArgumentExceptionFromElsewhere_IsLeftAlone()
    {
        // The tool bodies all return through Guard/GuardAsync, which turns everything into an
        // McpException, so no body failure reaches this today. That is their doing, not ours: a
        // tool added later without the guard must not have a real failure relabelled.
        var schema = AdvertisedMcpTools.All.Single(t => t.Name == "get_device_status").InputSchema;

        var explained = ArgumentBindingErrors.Explain(
            new ArgumentException("Serial port 'COM9' is already open.", "portName"),
            schema,
            Arguments("""{"deviceId":"serial:FAKE"}"""));

        Assert.Null(explained);
    }

    /// <summary>
    /// The reason has to survive the trip to a client, which is the part stderr failed. So this
    /// starts the real server over stdio and reads the <c>tools/call</c> result the way a client
    /// does, which is also the only way to know the filter is registered at all.
    /// </summary>
    [Fact]
    public async Task TheReason_ReachesAClientInTheToolResult()
    {
        using var timeout = new CancellationTokenSource(ServerBudget);
        await using var client = await ConnectAsync(timeout.Token);

        var missing = await client.CallToolAsync(
            "get_device_status", cancellationToken: timeout.Token);
        var wrongType = await client.CallToolAsync(
            "set_sample_rate",
            new Dictionary<string, object?> { ["deviceId"] = "serial:nope", ["sampleRateHz"] = "fast" },
            cancellationToken: timeout.Token);

        var outsideEnum = await client.CallToolAsync(
            "get_hardware_info",
            new Dictionary<string, object?> { ["deviceType"] = "nyquist1, nyquist2" },
            cancellationToken: timeout.Token);

        Assert.True(outsideEnum.IsError);
        Assert.Contains("deviceType must be one of", TextOf(outsideEnum), StringComparison.Ordinal);
        Assert.True(missing.IsError);
        Assert.Contains("deviceId is required.", TextOf(missing), StringComparison.Ordinal);
        Assert.True(wrongType.IsError);
        Assert.Contains("sampleRateHz must be an integer.", TextOf(wrongType), StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>outputSchema</c> a client on a pre-SEP-2106 protocol version is served, read off
    /// the wire rather than from <c>McpServerTool.ProtocolTool</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Those versions require an object, so the SDK wraps any other return type in a
    /// <c>{"result": ...}</c> envelope that newer ones do not get. Every tool returns a named
    /// record, so there is nothing to wrap and every client is served the same schema.
    /// </para>
    /// <para>
    /// The protocol version has to be pinned: the SDK's own client probes with
    /// <c>server/discover</c> and lands on 2026-07-28, which never wraps. Hence raw JSON-RPC with
    /// <c>initialize</c>.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("2024-11-05")]
    [InlineData("2025-06-18")]
    [InlineData("2025-11-25")]
    public async Task EveryToolAdvertisesItsOwnRecord_ToAClientOnALegacyProtocolVersion(
        string protocolVersion)
    {
        using var timeout = new CancellationTokenSource(ServerBudget);

        var tools = await ToolsListAsync(protocolVersion, timeout.Token);

        Assert.Equal(AdvertisedMcpTools.All.Count, tools.Count);
        Assert.All(tools, tool =>
        {
            var name = tool.GetProperty("name").GetString();
            Assert.True(
                tool.TryGetProperty("outputSchema", out var schema),
                $"{name} advertises no outputSchema.");
            Assert.True(
                schema.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString() == "object",
                $"{name} advertises a non-object outputSchema: {schema.GetRawText()}");
        });

        // And it is the tool's own record, not the envelope: a client sees one shape whatever
        // version it negotiated (issue #1042).
        Assert.All(tools, tool => Assert.False(
            tool.GetProperty("outputSchema").GetProperty("properties").TryGetProperty("result", out _),
            $"{tool.GetProperty("name").GetString()} is wrapped in the SDK's result envelope."));
    }

    /// <summary>
    /// The <c>tools</c> array the real server answers <c>tools/list</c> with, in a session that
    /// negotiated <paramref name="protocolVersion"/> through <c>initialize</c>.
    /// </summary>
    private static async Task<List<JsonElement>> ToolsListAsync(
        string protocolVersion,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ServerProcess.DotnetHost())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(ServerProcess.ServerAssembly());
        start.ArgumentList.Add("--no-version-check");

        using var server = Process.Start(start)!;
        try
        {
            await SendAsync(
                server,
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "initialize",
                    @params = new
                    {
                        protocolVersion,
                        capabilities = new { },
                        clientInfo = new { name = "daqifi-mcp-tests", version = "1.0.0" },
                    },
                }),
                cancellationToken);
            var handshake = await ReadResponseAsync(server, 1, cancellationToken);
            Assert.Equal(
                protocolVersion,
                handshake.GetProperty("result").GetProperty("protocolVersion").GetString());

            await SendAsync(
                server,
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
                cancellationToken);
            await SendAsync(
                server,
                """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
                cancellationToken);
            var listed = await ReadResponseAsync(server, 2, cancellationToken);

            return [.. listed.GetProperty("result").GetProperty("tools").EnumerateArray()];
        }
        finally
        {
            server.Kill(entireProcessTree: true);
        }
    }

    private static async Task SendAsync(Process server, string json, CancellationToken cancellationToken)
    {
        await server.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
        await server.StandardInput.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// The response to the request with this id. Anything else the server writes — a
    /// notification, a log line that reached stdout — is skipped rather than mistaken for it.
    /// </summary>
    private static async Task<JsonElement> ReadResponseAsync(
        Process server,
        int id,
        CancellationToken cancellationToken)
    {
        while (await server.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            JsonElement message;
            try
            {
                message = JsonSerializer.Deserialize<JsonElement>(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("id", out var responseId)
                && responseId.ValueKind == JsonValueKind.Number
                && responseId.GetInt32() == id)
            {
                if (message.TryGetProperty("error", out var error))
                {
                    Assert.Fail($"the server answered request {id} with an error: {error.GetRawText()}");
                }

                return message;
            }
        }

        throw new InvalidOperationException(
            $"the server closed its output before answering request {id}: "
            + await server.StandardError.ReadToEndAsync(cancellationToken));
    }

    /// <summary>
    /// Generous because it covers starting a .NET process on a shared CI runner; a healthy start
    /// answers in about a second. See <see cref="ServerInstructionsHandshakeTests"/>.
    /// </summary>
    private static readonly TimeSpan ServerBudget = TimeSpan.FromSeconds(60);

    private static async Task<McpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "daqifi-mcp",
            Command = ServerProcess.DotnetHost(),
            Arguments = [ServerProcess.ServerAssembly(), "--no-version-check"],
            ShutdownTimeout = TimeSpan.FromSeconds(1),
        });
        // Wait for server/discover rather than race the initialize fallback on a cold start.
        var options = new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan };
        return await McpClient.CreateAsync(transport, options, cancellationToken: cancellationToken);
    }

    private static JsonElement SchemaOf(string tool) =>
        AdvertisedMcpTools.All.Single(t => t.Name == tool).InputSchema;

    private static string TextOf(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static Dictionary<string, JsonElement> Arguments(string argumentsJson) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson)!;

    /// <summary>
    /// What the filter would say about the failure this call really produces: the marshaller's
    /// own exception, put through the same derivation the filter uses.
    /// </summary>
    private static async Task<string?> ExplainAsync(string tool, string argumentsJson)
    {
        var failure = await Record.ExceptionAsync(() => InvokeAsync(tool, argumentsJson));

        Assert.NotNull(failure);
        Assert.True(
            failure is ArgumentException or JsonException,
            $"expected a binding failure, got {failure.GetType().Name}: {failure.Message}");

        return ArgumentBindingErrors.Explain(
            failure,
            AdvertisedMcpTools.All.Single(t => t.Name == tool).InputSchema,
            Arguments(argumentsJson));
    }

    /// <summary>
    /// Calls <paramref name="tool"/> through the registered <see cref="McpServerTool"/>, i.e. the
    /// same object the server's <c>tools/call</c> handler invokes, with the arguments as a client
    /// would send them. The handler is what absorbs a binding failure, so invoked directly the
    /// marshaller's exception comes straight out.
    /// </summary>
    private static async Task<CallToolResult> InvokeAsync(string tool, string argumentsJson)
    {
        var services = new ServiceCollection();
        services.AddSingleton(AgentHarness.WithConnectedDevice().Agent);
        services.AddMcpServer().WithDaqifiTools();
        await using var provider = services.BuildServiceProvider();
        var registered = provider.GetServices<McpServerTool>()
            .Single(t => t.ProtocolTool.Name == tool);
        await using var server = McpServer.Create(
            new StreamServerTransport(Stream.Null, Stream.Null),
            new McpServerOptions(),
            serviceProvider: provider);
        var request = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Method = RequestMethods.ToolsCall },
            new CallToolRequestParams { Name = tool, Arguments = Arguments(argumentsJson) })
        {
            Services = provider,
        };

        return await registered.InvokeAsync(request, CancellationToken.None);
    }
}
