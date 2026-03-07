using DayStartMCP.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DayStartMCP.Tools;

[McpServerToolType]
public static class ActionItemsTools
{
    private static readonly string ActionsFilePath = @"c:/Users/syedhu/work/actions.yaml";

    private static readonly string[] LeadershipChain =
        ["Kristi", "Anand", "Jayu", "Brad", "Pretish", "Vanessa", "Jason", "Perry", "Rajesh"];

    [McpServerTool, Description(
        "Read open and in-progress action items from actions.yaml. " +
        "Returns items sorted by: overdue first, then by due date (soonest first), " +
        "then by date_added (oldest first), with null due-date items last. " +
        "Leadership-chain items are flagged as high-priority.")]
    public static string GetOpenActionItems()
    {
        if (!File.Exists(ActionsFilePath))
            return "No action items tracked yet. Use `/mycmd-actions add` or `/mycmd-eod` to start tracking.";

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .WithTagMapping("!!", typeof(object))
            .Build();

        ActionsFile actionsFile;
        try
        {
            var yaml = File.ReadAllText(ActionsFilePath);
            actionsFile = deserializer.Deserialize<ActionsFile>(yaml) ?? new ActionsFile();
        }
        catch (Exception ex)
        {
            return $"Error reading actions.yaml: {ex.Message}";
        }

        var today = DateTime.Today;

        var activeItems = actionsFile.Actions
            .Where(a => a.Status is "open" or "in-progress")
            .OrderBy(a => a.Due.HasValue ? 0 : 1)           // null due dates last
            .ThenBy(a => a.Due.HasValue && a.Due.Value < today ? 0 : 1) // overdue first
            .ThenBy(a => a.Due)
            .ThenBy(a => a.DateAdded)
            .ToList();

        if (activeItems.Count == 0)
            return "No open or in-progress action items found.";

        var overdue    = activeItems.Where(a => a.Due.HasValue && a.Due.Value < today).ToList();
        var dueToday   = activeItems.Where(a => a.Due.HasValue && a.Due.Value.Date == today).ToList();
        var upcoming   = activeItems.Where(a => a.Due.HasValue && a.Due.Value.Date > today).ToList();
        var noDueDate  = activeItems.Where(a => !a.Due.HasValue).ToList();
        var delegated  = activeItems.Where(a => !a.Owner.Equals("Syedhu", StringComparison.OrdinalIgnoreCase)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"### Open Action Items ({activeItems.Count} total, {overdue.Count} overdue)");
        sb.AppendLine();

        AppendSection(sb, "**Overdue (requires immediate attention):**", overdue, today);
        AppendSection(sb, "**Due Today:**", dueToday, today);
        AppendSection(sb, "**Upcoming:**", upcoming, today);
        AppendSection(sb, "**No Due Date:**", noDueDate, today);

        if (delegated.Count > 0)
        {
            sb.AppendLine("**Delegated to Others (for follow-up):**");
            foreach (var item in delegated)
                sb.AppendLine(FormatItem(item, today));
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendSection(StringBuilder sb, string header, List<ActionItem> items, DateTime today)
    {
        if (items.Count == 0) return;
        sb.AppendLine(header);
        foreach (var item in items)
            sb.AppendLine(FormatItem(item, today));
        sb.AppendLine();
    }

    private static string FormatItem(ActionItem item, DateTime today)
    {
        var isLeadership = LeadershipChain.Any(name =>
            item.Description.Contains(name, StringComparison.OrdinalIgnoreCase) ||
            item.Source.Contains(name, StringComparison.OrdinalIgnoreCase));

        var priority = isLeadership ? " [HIGH PRIORITY]" : string.Empty;

        var dueInfo = item.Due.HasValue
            ? item.Due.Value.Date < today
                ? $"Due: {item.Due:yyyy-MM-dd} ({(today - item.Due.Value).Days} day(s) overdue)"
                : $"Due: {item.Due:yyyy-MM-dd}"
            : "No due date";

        var owner = item.Owner.Equals("Syedhu", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" - Owner: {item.Owner}";

        return $"- [#{item.Id}]{priority} {item.Description} - {dueInfo} - Source: {item.Source}{owner}";
    }
}
