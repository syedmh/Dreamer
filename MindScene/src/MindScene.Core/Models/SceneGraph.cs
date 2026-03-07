namespace MindScene.Core.Models;

/// <summary>
/// Fully resolved scene graph — the structured representation after NLP parsing.
/// </summary>
public record ResolvedScene
{
    public required string SceneId { get; init; }
    public required SettingDescription Setting { get; init; }
    public List<ActorPlacement> Actors { get; init; } = [];
    public List<PropPlacement> Props { get; init; } = [];
    public List<AnimationDirective> Directives { get; init; } = [];
    public List<CameraInstruction> CameraDirections { get; init; } = [];
    public List<DialogueLine> Dialogue { get; init; } = [];
    public float TotalDurationSeconds { get; init; }
    public AnimationStyle Style { get; init; }
    public int Fps { get; init; } = 24;
    public ResolutionPreset Resolution { get; init; } = ResolutionPreset.Hd720p;
}

public record ActorPlacement
{
    public required ActorDefinition Actor { get; init; }
    public float X { get; init; }          // 0.0 to 1.0 (relative to frame width)
    public float Y { get; init; }          // 0.0 to 1.0 (relative to frame height)
    public float Scale { get; init; } = 1.0f;
    public float ZLayer { get; init; } = 0.5f;  // 0=background, 1=foreground
    public string FacingDirection { get; init; } = "front";  // front, left, right, back
    public string Pose { get; init; } = "standing";
    public EmotionVector Emotion { get; init; } = EmotionVector.Neutral;
}

public record PropPlacement(
    string Name,
    string Description,
    float X,
    float Y,
    float Scale = 1.0f,
    float ZLayer = 0.3f);

public record AnimationDirective
{
    public required string ActorName { get; init; }
    public required AnimationAction Action { get; init; }
    public float StartSeconds { get; init; }
    public float DurationSeconds { get; init; }
    public string? TargetPosition { get; init; }
    public EmotionVector? TargetEmotion { get; init; }
    public Dictionary<string, object> Parameters { get; init; } = [];
}

public enum AnimationAction
{
    Idle,
    Walk,
    Run,
    Sit,
    Stand,
    TurnLeft,
    TurnRight,
    Gesture,
    PointAt,
    Nod,
    ShakeHead,
    LookAt,
    LookAway,
    React,
    EmotionTransition,
    Speak,
    Custom
}
