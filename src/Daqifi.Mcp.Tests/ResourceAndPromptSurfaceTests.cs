using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// The resources and prompts as a client actually receives them.
/// </summary>
/// <remarks>
/// Registration is the whole risk here. A resource class with the right attributes that nothing
/// ever registers compiles, passes any test that calls its methods directly, and serves nothing:
/// <c>resources/list</c> comes back empty and the data is invisible in every client. So these
/// start the real server over stdio and list and read through the protocol, the way a client
/// does. The data itself is pinned in <see cref="HardwareCatalogTests"/>.
/// </remarks>
public class ResourceAndPromptSurfaceTests
{
    /// <summary>
    /// Generous because it covers starting a .NET process on a shared CI runner. A healthy start
    /// answers in about a second; this only bounds a server that never answers.
    /// </summary>
    private static readonly TimeSpan HandshakeBudget = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task TheServer_ListsAResourcePerBoard()
    {
        using var timeout = new CancellationTokenSource(HandshakeBudget);
        await using var client = await ConnectAsync(timeout.Token);

        var resources = await client.ListResourcesAsync(cancellationToken: timeout.Token);

        Assert.Equal(
            new[]
            {
                "daqifi://hardware/nyquist1",
                "daqifi://hardware/nyquist2",
                "daqifi://hardware/nyquist3",
            },
            resources.Select(r => r.Uri).OrderBy(u => u, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReadingABoardResource_ReturnsItsPublishedSpecifications()
    {
        using var timeout = new CancellationTokenSource(HandshakeBudget);
        await using var client = await ConnectAsync(timeout.Token);

        var read = await client.ReadResourceAsync(
            new Uri("daqifi://hardware/nyquist3"),
            cancellationToken: timeout.Token);

        var contents = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));
        Assert.Equal("application/json", contents.MimeType);

        // Parsed rather than string-matched: the serializer escapes the '+' in the range as
        // +, which every JSON reader decodes and no client displays, but which a substring
        // assertion would trip over.
        using var parsed = JsonDocument.Parse(contents.Text);
        Assert.Equal("Nyquist3", parsed.RootElement.GetProperty("deviceType").GetString());

        // The facts an agent cannot get from the device: how many bits, what range, and that this
        // is the board whose channels are sampled at one instant.
        var inputs = parsed.RootElement.GetProperty("analogInputs");
        Assert.Equal(18, inputs.GetProperty("resolutionBits").GetInt32());
        Assert.Equal("-10 to +10 V", inputs.GetProperty("range").GetString());
        Assert.True(inputs.GetProperty("simultaneousSampling").GetBoolean());
    }

    [Fact]
    public async Task TheServer_ListsThePromptsForTheCommonJobs()
    {
        using var timeout = new CancellationTokenSource(HandshakeBudget);
        await using var client = await ConnectAsync(timeout.Token);

        var prompts = await client.ListPromptsAsync(cancellationToken: timeout.Token);

        Assert.Equal(
            new[]
            {
                "check_for_noise",
                "drive_and_measure",
                "read_voltage_now",
                "start_sd_log",
                "summarize_sd_log",
            },
            prompts.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        // A prompt with no description is a blank entry in the client's picker.
        Assert.All(prompts, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
    }

    [Fact]
    public async Task GettingAPrompt_FillsInTheArgumentsItWasGiven()
    {
        using var timeout = new CancellationTokenSource(HandshakeBudget);
        await using var client = await ConnectAsync(timeout.Token);

        var result = await client.GetPromptAsync(
            "check_for_noise",
            new Dictionary<string, object?> { ["channels"] = "4,5", ["durationMs"] = 3000 },
            cancellationToken: timeout.Token);

        var message = Assert.Single(result.Messages);
        // A user message, not an assistant one: this is the request the user would have typed.
        Assert.Equal(Role.User, message.Role);
        var text = Assert.IsType<TextContentBlock>(message.Content).Text;
        Assert.Contains("channels 4,5", text);
        Assert.Contains("3000 ms", text);
    }

    [Fact]
    public async Task GettingAPrompt_WithNoArguments_StillAsksForSomethingUseful()
    {
        using var timeout = new CancellationTokenSource(HandshakeBudget);
        await using var client = await ConnectAsync(timeout.Token);

        // Every argument has a default, so a client that offers the prompt as a one-click
        // starting point gets a complete request rather than an error.
        var result = await client.GetPromptAsync("summarize_sd_log", cancellationToken: timeout.Token);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Messages).Content).Text;
        Assert.Contains("list the files on the card", text);
        Assert.Contains("every channel", text);
    }

    private static async Task<McpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "daqifi-mcp",
            Command = ServerProcess.DotnetHost(),
            Arguments = [ServerProcess.ServerAssembly(), "--no-version-check"],
            // On dispose the SDK waits this long for the server to exit before it closes stdin,
            // the thing that would make it exit, and then kills it. Nothing here needs a graceful
            // shutdown, so do not pay the 5 s default on every test.
            ShutdownTimeout = TimeSpan.FromSeconds(1),
        });

        // Wait for server/discover under the handshake budget instead of letting the probe time
        // out and race the initialize fallback; see ServerInstructionsHandshakeTests.
        var options = new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan };

        return await McpClient.CreateAsync(transport, options, cancellationToken: cancellationToken);
    }
}
