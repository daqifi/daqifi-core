using ModelContextProtocol.Client;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// The session rules only help if they reach the client in the <c>initialize</c> result, and
/// what hands them to the SDK is <c>Program.cs</c>, not <see cref="ServerOptions"/>. So these
/// start the real server over stdio and read <c>instructions</c> off the handshake the way a
/// client does: a test that only read <see cref="ServerOptions.Instructions"/> would stay green
/// with the registration deleted.
/// </summary>
public class ServerInstructionsHandshakeTests
{
    /// <summary>
    /// Generous because it covers starting a .NET process on a shared CI runner. A healthy start
    /// answers in about a second; this only bounds a server that never answers.
    /// </summary>
    private static readonly TimeSpan HandshakeBudget = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task TheHandshake_CarriesTheSessionRules()
    {
        var instructions = await InstructionsFromHandshakeAsync("--no-version-check");

        Assert.NotNull(instructions);
        Assert.Contains("discover_devices", instructions);
        Assert.Contains("Retrieve before you stream", instructions);
        // Firmware 3.7.3 fixed the SD-buffer collapse (#703), so the rule has to say which
        // devices it is about rather than present it as how every device behaves.
        Assert.Contains("firmware below 3.7.3", instructions);
        Assert.Contains("Nyquist 3", instructions);
        // Sent to a writable server, the read-only rule is noise and a hint that writes might be
        // refused where they will not be.
        Assert.DoesNotContain("--read-only", instructions);
    }

    [Fact]
    public async Task TheHandshake_OfAReadOnlyServer_AlsoStatesTheReadOnlyRule()
    {
        var instructions = await InstructionsFromHandshakeAsync("--read-only", "--no-version-check");

        Assert.NotNull(instructions);
        Assert.Contains("discover_devices", instructions);
        Assert.Contains("--read-only", instructions);
        Assert.Contains("read_channel_values", instructions);
        Assert.Contains("capture_samples", instructions);
    }

    private static async Task<string?> InstructionsFromHandshakeAsync(params string[] serverArgs)
    {
        // The test project references Daqifi.Mcp, so its build (and runtimeconfig) sits beside
        // the tests for whichever framework is running them.
        var server = Path.Combine(AppContext.BaseDirectory, "daqifi-mcp.dll");
        Assert.True(File.Exists(server), $"The server build is not next to the tests: {server}");

        using var timeout = new CancellationTokenSource(HandshakeBudget);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "daqifi-mcp",
            Command = DotnetHost(),
            Arguments = [server, .. serverArgs],
            // On dispose the SDK waits this long for the server to exit before it closes stdin,
            // the thing that would make it exit, and then kills it. Nothing here needs a graceful
            // shutdown, so do not pay the 5 s default on every test.
            ShutdownTimeout = TimeSpan.FromSeconds(1),
        });

        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        return client.ServerInstructions;
    }

    /// <summary>
    /// The muxer that is running these tests, which is known to have the runtime the server needs.
    /// </summary>
    private static string DotnetHost()
    {
        var fromCli = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(fromCli) && File.Exists(fromCli))
        {
            return fromCli;
        }

        var self = Environment.ProcessPath;
        if (self is not null
            && string.Equals(Path.GetFileNameWithoutExtension(self), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return self;
        }

        return "dotnet";
    }
}
