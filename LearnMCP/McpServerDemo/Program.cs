using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// ============================================================
// MCP Server Bootstrap
// ============================================================
// The MCP server uses STDIO transport — it reads JSON-RPC messages
// from stdin and writes responses to stdout. This is how AI clients
// (like GitHub Copilot, Claude Desktop, etc.) communicate with it.

var builder = Host.CreateApplicationBuilder(args);

// Log to stderr so it doesn't interfere with the JSON-RPC protocol on stdout
builder.Logging.AddConsole(options =>
    options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()                  // Register the MCP server
    .WithStdioServerTransport()      // Use STDIO (stdin/stdout) for communication
    .WithToolsFromAssembly();        // Auto-discover all [McpServerTool] methods in this assembly

await builder.Build().RunAsync();
