using Daqifi.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

// Resolve the agent before RunAsync: RunAsync disposes the host (and its service provider) in
// its own finally, so the agent must be captured while the provider is still alive.
var agent = host.Services.GetRequiredService<DaqifiAgent>();
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
