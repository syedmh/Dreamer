namespace MindScene.Core.Models;

public record DialogueLine
{
    public required string Speaker { get; init; }
    public required string Text { get; init; }
    public EmotionVector Emotion { get; init; } = EmotionVector.Neutral;
    public string Action { get; init; } = string.Empty;
    public TimeCode Timing { get; init; } = TimeCode.Auto;
    public string? AudioClipPath { get; init; }
    public List<Viseme> LipSyncData { get; init; } = [];
}

public record TimeCode(float StartSeconds, float DurationSeconds)
{
    public static readonly TimeCode Auto = new(-1f, -1f);
    public bool IsAuto => StartSeconds < 0;
    public float EndSeconds => StartSeconds + DurationSeconds;
}

public record Viseme(string Shape, float StartSeconds, float DurationSeconds)
{
    // Mouth shapes for lip-sync
    public static readonly string[] AllShapes = ["rest", "mbp", "fv", "th", "dtn", "kng", "ch", "ss", "ee", "ih", "oh", "oo", "aa", "ae", "er"];
}
