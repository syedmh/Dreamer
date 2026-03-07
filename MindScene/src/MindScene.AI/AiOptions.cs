namespace MindScene.AI;

public class AiOptions
{
    public const string SectionName = "AI";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "claude-sonnet-4-6";
    public int MaxTokens { get; set; } = 4096;
}
