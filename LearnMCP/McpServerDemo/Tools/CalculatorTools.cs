using ModelContextProtocol.Server;
using System.ComponentModel;

namespace McpServerDemo.Tools;

// ============================================================
// Tool 2: Calculator — Tool with multiple parameters
// ============================================================
// This shows how MCP tools can accept multiple parameters.
// The [Description] attribute on each parameter tells the AI
// what the parameter is for, so it can fill them in correctly.

[McpServerToolType]
public static class CalculatorTools
{
    [McpServerTool, Description("Perform a math operation on two numbers")]
    public static string Calculate(
        [Description("The math operation: add, subtract, multiply, or divide")] string operation,
        [Description("The first number")] double a,
        [Description("The second number")] double b)
    {
        var result = operation.ToLower() switch
        {
            "add" => a + b,
            "subtract" => a - b,
            "multiply" => a * b,
            "divide" when b != 0 => a / b,
            "divide" => double.NaN,
            _ => throw new ArgumentException($"Unknown operation: {operation}. Use add, subtract, multiply, or divide.")
        };

        return $"{a} {operation} {b} = {result}";
    }
}
