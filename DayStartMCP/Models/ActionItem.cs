using YamlDotNet.Serialization;

namespace DayStartMCP.Models;

public class ActionItem
{
    [YamlMember(Alias = "id")]
    public int Id { get; set; }

    [YamlMember(Alias = "description")]
    public string Description { get; set; } = string.Empty;

    [YamlMember(Alias = "owner")]
    public string Owner { get; set; } = "Syedhu";

    [YamlMember(Alias = "source")]
    public string Source { get; set; } = string.Empty;

    [YamlMember(Alias = "date_added")]
    public DateTime DateAdded { get; set; }

    [YamlMember(Alias = "due")]
    public DateTime? Due { get; set; }

    [YamlMember(Alias = "status")]
    public string Status { get; set; } = "open";

    [YamlMember(Alias = "project")]
    public string? Project { get; set; }

    [YamlMember(Alias = "workstream")]
    public string? Workstream { get; set; }

    [YamlMember(Alias = "completed_date")]
    public DateTime? CompletedDate { get; set; }
}

public class ActionsFile
{
    [YamlMember(Alias = "actions")]
    public List<ActionItem> Actions { get; set; } = [];
}
