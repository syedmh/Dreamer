using System.Text.Json.Serialization;

namespace MindScene.Core.Models;

/// <summary>
/// Top-level input model for a scene to be animated.
/// </summary>
public record SceneInput
{
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("actors")]
    public List<ActorDefinition> Actors { get; init; } = [];

    [JsonPropertyName("dialogue")]
    public List<DialogueLine> Dialogue { get; init; } = [];

    [JsonPropertyName("setting")]
    public SettingDescription Setting { get; init; } = SettingDescription.Default;

    [JsonPropertyName("camera")]
    public List<CameraInstruction> CameraDirections { get; init; } = [];

    [JsonPropertyName("duration")]
    public float? DurationSeconds { get; init; }

    [JsonPropertyName("style")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AnimationStyle Style { get; init; } = AnimationStyle.Anime;

    [JsonPropertyName("mood")]
    public string Mood { get; init; } = "neutral";

    [JsonPropertyName("fps")]
    public int Fps { get; init; } = 24;

    [JsonPropertyName("resolution")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ResolutionPreset Resolution { get; init; } = ResolutionPreset.Hd720p;
}
