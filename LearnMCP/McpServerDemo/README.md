# McpServerDemo — Learn MCP with C#

A simple **Model Context Protocol (MCP) server** built in C# to help you understand how MCP works.

## What is MCP?

MCP (Model Context Protocol) is an open standard that lets AI assistants (like GitHub Copilot, Claude, etc.) call **tools** you define. Think of it as a plugin system for AI:

```
┌──────────────┐    JSON-RPC over STDIO    ┌──────────────────┐
│  AI Client   │ ◄──────────────────────► │  MCP Server      │
│  (Copilot,   │    "call GetWeather"      │  (this project)  │
│   Claude)    │    ◄── result ──►         │                  │
└──────────────┘                           └──────────────────┘
```

The AI decides **when** to call your tools based on the user's question and the tool descriptions you provide.

## Project Structure

```
McpServerDemo/
├── Program.cs                  # Server bootstrap (3 lines of setup!)
├── Tools/
│   ├── TimeTools.cs            # Tool 1: Get current time
│   ├── CalculatorTools.cs      # Tool 2: Math operations
│   └── WeatherTools.cs         # Tool 3: Fake weather data
└── McpServerDemo.csproj        # Project file with NuGet packages
```

## Key Concepts

| Concept | How It Works |
|---|---|
| **`[McpServerToolType]`** | Marks a class as containing MCP tools |
| **`[McpServerTool]`** | Exposes a method as a callable tool |
| **`[Description]`** | Tells the AI what the tool/parameter does |
| **STDIO Transport** | Server reads JSON-RPC from stdin, writes to stdout |
| **Auto-discovery** | `WithToolsFromAssembly()` finds all tools automatically |

## How to Run

```bash
dotnet build
dotnet run
```

## How to Use with an AI Client

### Visual Studio / GitHub Copilot
Add to your MCP configuration (`.vscode/mcp.json` or VS settings):

```json
{
  "servers": {
    "McpServerDemo": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\McpServerDemo"]
    }
  }
}
```

### Claude Desktop
Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "McpServerDemo": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\McpServerDemo"]
    }
  }
}
```

## Available Tools

| Tool | Description | Parameters |
|---|---|---|
| `GetCurrentTime` | Get the current date/time for a city | `city` (string) |
| `Calculate` | Perform math: add, subtract, multiply, divide | `operation` (string), `a` (number), `b` (number) |
| `GetWeather` | Get simulated weather for a city | `city` (string) |
