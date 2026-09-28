using System.Diagnostics;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// What an operator sees when a launch flag is wrong. <see cref="ServerOptions.Parse"/> throwing is
/// pinned in <c>ServerOptionsTests</c>; turning that into a usage error rather than a crash is
/// <c>Program.cs</c>'s job, so these start the real server process to check it.
/// </summary>
public class StartupArgumentTests
{
    /// <summary>
    /// Generous because it covers starting a .NET process on a shared CI runner. A rejected flag
    /// exits before the host is built, in well under a second; this only bounds a process that
    /// never exits.
    /// </summary>
    private static readonly TimeSpan ExitBudget = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData("--read-onyl")]
    [InlineData("--max-sample-rate-hz", "--read-only")]
    public async Task ABadFlag_StopsTheServer_WithAUsageErrorNamingIt(params string[] serverArgs)
    {
        var (exitCode, stderr) = await RunServerAsync(serverArgs);

        Assert.Equal(2, exitCode);
        Assert.Contains($"'{serverArgs[^1]}'", stderr);
        Assert.Contains("--help", stderr);
        Assert.DoesNotContain("Unhandled exception", stderr);
    }

    private static async Task<(int ExitCode, string Stderr)> RunServerAsync(string[] serverArgs)
    {
        // The test project references Daqifi.Mcp, so its build (and runtimeconfig) sits beside
        // the tests for whichever framework is running them.
        var server = Path.Combine(AppContext.BaseDirectory, "daqifi-mcp.dll");
        Assert.True(File.Exists(server), $"The server build is not next to the tests: {server}");

        var startInfo = new ProcessStartInfo(DotnetHost())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(server);
        foreach (var arg in serverArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        // End of stdin ends a server that did start, so a flag that fails to stop it shows up as
        // a wrong exit code rather than as the whole budget spent waiting.
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(ExitBudget);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"The server was still running after {ExitBudget.TotalSeconds:0} s.");
        }

        // Nothing may reach stdout: it is the MCP JSON-RPC stream.
        Assert.Equal(string.Empty, await stdout);
        return (process.ExitCode, await stderr);
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
