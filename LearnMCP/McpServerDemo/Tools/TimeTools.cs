using ModelContextProtocol.Server;
using System.ComponentModel;

namespace McpServerDemo.Tools;

// ============================================================
// Tool 1: Time Tools — Simple tool with one parameter
// ============================================================
// Each static class decorated with [McpServerToolType] is a container
// for MCP tools. Each method with [McpServerTool] becomes a callable
// tool that an AI client can invoke.

[McpServerToolType]
public static class TimeTools
{
    [McpServerTool, Description("Get the current date and time for a given city")]
    public static string GetCurrentTime(
        [Description("The name of the city")] string city)
    {
        // In a real app, you'd use a timezone API.
        // Here we just return the server's local time for simplicity.
        return $"The current time in {city} is {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
    }
}
