using System.Text.Json.Serialization;

namespace MindScene.NLP.Dto;

internal record SceneDecompositionDto
{
    [JsonPropertyName("location")] public string Location { get; init; } = "unknown";
    [JsonPropertyName("timeOfDay")] public string TimeOfDay { get; init; } = "day";
    [JsonPropertyName("weather")] public string Weather { get; init; } = "clear";
    [JsonPropertyName("lighting")] public string Lighting { get; init; } = "neutral";
    [JsonPropertyName("atmosphere")] public string Atmosphere { get; init; } = "neutral";
    [JsonPropertyName("environmentTemplate")] public string EnvironmentTemplate { get; init; } = "generic";
    [JsonPropertyName("props")] public List<string> Props { get; init; } = [];
    [JsonPropertyName("ambientSound")] public string AmbientSound { get; init; } = "silence";
    [JsonPropertyName("actorCount")] public int ActorCount { get; init; } = 1;
}

internal record SpatialPlacementDto
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("x")] public float X { get; init; } = 0.5f;
    [JsonPropertyName("y")] public float Y { get; init; } = 0.7f;
    [JsonPropertyName("zLayer")] public float ZLayer { get; init; } = 0.5f;
    [JsonPropertyName("facingDirection")] public string FacingDirection { get; init; } = "front";
    [JsonPropertyName("pose")] public string Pose { get; init; } = "standing";
    [JsonPropertyName("scale")] public float Scale { get; init; } = 1.0f;
}

internal record AnimationDirectiveDto
{
    [JsonPropertyName("actorName")] public string ActorName { get; init; } = string.Empty;
    [JsonPropertyName("action")] public string Action { get; init; } = "Idle";
    [JsonPropertyName("startSeconds")] public float StartSeconds { get; init; }
    [JsonPropertyName("durationSeconds")] public float DurationSeconds { get; init; } = 2.0f;
    [JsonPropertyName("targetEmotion")] public EmotionDto? TargetEmotion { get; init; }
}

internal record EmotionDto
{
    [JsonPropertyName("joy")] public float Joy { get; init; }
    [JsonPropertyName("sadness")] public float Sadness { get; init; }
    [JsonPropertyName("anger")] public float Anger { get; init; }
    [JsonPropertyName("fear")] public float Fear { get; init; }
    [JsonPropertyName("surprise")] public float Surprise { get; init; }
    [JsonPropertyName("disgust")] public float Disgust { get; init; }
}
