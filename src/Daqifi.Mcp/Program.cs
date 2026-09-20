using Daqifi.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.Error.WriteLine(ServerOptions.HelpText);
    return;
}

var options = ServerOptions.Parse(args);

// Do not pass args to the host builder: the command-line config provider would choke on
// value-less switches like "--read-only". Options are parsed above instead.
var builder = Host.CreateApplicationBuilder();

// CRITICAL: stdout is reserved for the MCP JSON-RPC stream. Route ALL logging to stderr,
// otherwise stray log lines corrupt the protocol and the client fails to connect.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<DaqifiAgent>();
builder.Services.AddSingleton<ILatestVersionSource, NuGetLatestVersionSource>();
builder.Services.AddSingleton<VersionStatus>();

builder.Services
    // Name the running version in the initialization handshake. Clients log and display it, so
    // "which daqifi-mcp am I talking to?" has an answer without calling a tool (issue #727).
    .AddMcpServer(o => o.ServerInfo = new Implementation
    {
        Name = "daqifi-mcp",
        Title = "DAQiFi",
        Version = ServerVersion.Current,
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

// Resolve the agent before RunAsync: RunAsync disposes the host (and its service provider) in
// its own finally, so the agent must be captured while the provider is still alive.
var agent = host.Services.GetRequiredService<DaqifiAgent>();

// Fire-and-forget: a stale install is worth a stderr line, but never worth delaying startup or
// failing it. The check is bounded by its own timeout and swallows its own failures. The host
// stopping token aborts the nuget GET if shutdown wins the race with the 5s request timeout.
host.Services.GetRequiredService<VersionStatus>()
    .Start(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);

try
{
    await host.RunAsync();
}
finally
{
    // The client closing stdio ends RunAsync; drain connected devices so serial ports are
    // released (and any in-progress SD capture is stopped) rather than left held until reap.
    await agent.ShutdownAsync();
}
