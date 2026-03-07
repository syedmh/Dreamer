namespace MindScene.Core.Models;

public record SettingDescription
{
    public string Location { get; init; } = "unknown";
    public string TimeOfDay { get; init; } = "day";
    public string Weather { get; init; } = "clear";
    public string Mood { get; init; } = "neutral";
    public string LightingMood { get; init; } = "neutral";
    public List<string> Props { get; init; } = [];
    public string EnvironmentTemplate { get; init; } = "generic";

    public static readonly SettingDescription Default = new();
}

public record CameraInstruction(
    CameraMovement Movement,
    float StartSeconds,
    float DurationSeconds,
    string? Target = null,
    float? ZoomFactor = null);

public enum CameraMovement
{
    Static,
    Pan,
    Zoom,
    ZoomIn,
    ZoomOut,
    Track,
    Shake,
    Dolly,
    Arc
}
