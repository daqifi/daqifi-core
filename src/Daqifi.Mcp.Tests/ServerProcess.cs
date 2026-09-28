namespace Daqifi.Mcp.Tests;

/// <summary>
/// Where the tests that start the real server process find it, and the runtime to run it on.
/// </summary>
internal static class ServerProcess
{
    /// <summary>
    /// The server build. The test project references Daqifi.Mcp, so its build (and runtimeconfig)
    /// sits beside the tests for whichever framework is running them.
    /// </summary>
    public static string ServerAssembly()
    {
        var server = Path.Combine(AppContext.BaseDirectory, "daqifi-mcp.dll");
        Assert.True(File.Exists(server), $"The server build is not next to the tests: {server}");
        return server;
    }

    /// <summary>
    /// The muxer that is running these tests, which is known to have the runtime the server needs.
    /// </summary>
    public static string DotnetHost()
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
