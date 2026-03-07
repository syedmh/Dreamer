using ModelContextProtocol.Server;
using System.ComponentModel;

namespace DayStartMCP.Tools;

[McpServerToolType]
public static class BodReportTools
{
    private static readonly string BodDirectory = @"c:/Users/syedhu/work/syedhu/bod";

    [McpServerTool, Description(
        "Save the completed BOD (Beginning of Day) report as a markdown file. " +
        "Saves to c:/Users/syedhu/work/syedhu/bod/<mon-dd-yyyy>.md using today's date. " +
        "Creates the directory if it does not exist. Returns the saved file path.")]
    public static string SaveBodReport(
        [Description("Full markdown content of the BOD report to save")] string reportContent)
    {
        try
        {
            Directory.CreateDirectory(BodDirectory);

            var today = DateTime.Today;
            var fileName = $"{today:MMM-dd-yyyy}".ToLowerInvariant() + ".md";
            var filePath = Path.Combine(BodDirectory, fileName);

            File.WriteAllText(filePath, reportContent);

            return $"BOD report saved to syedhu/bod/{fileName}";
        }
        catch (Exception ex)
        {
            return $"Error saving BOD report: {ex.Message}";
        }
    }

    [McpServerTool, Description(
        "Get the expected file path for today's BOD report without writing anything. " +
        "Useful for checking if a report already exists for today.")]
    public static string GetBodReportPath()
    {
        var today = DateTime.Today;
        var fileName = $"{today:MMM-dd-yyyy}".ToLowerInvariant() + ".md";
        var filePath = Path.Combine(BodDirectory, fileName);
        var exists = File.Exists(filePath);
        return exists
            ? $"Report already exists: syedhu/bod/{fileName}"
            : $"No report yet for today. Will save to: syedhu/bod/{fileName}";
    }
}
