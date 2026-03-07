namespace MindScene.Core.Models;

public record ActorDefinition
{
    public required string Name { get; init; }
    public string PhysicalDescription { get; init; } = string.Empty;
    public int Age { get; init; } = 25;
    public string Gender { get; init; } = "unspecified";
    public string Build { get; init; } = "average";
    public PersonalityProfile Personality { get; init; } = PersonalityProfile.Default;
    public VoiceProfile Voice { get; init; } = VoiceProfile.Default;
    public EmotionVector InitialEmotion { get; init; } = EmotionVector.Neutral;
    public string DefaultPose { get; init; } = "standing";
    public List<string> Wardrobe { get; init; } = [];

    /// <summary>Derived visual attributes parsed from PhysicalDescription.</summary>
    public VisualAttributes Visual { get; init; } = VisualAttributes.Default;
}

public record PersonalityProfile(
    float Introversion = 0.5f,
    float Agreeableness = 0.5f,
    float Conscientiousness = 0.5f,
    float Neuroticism = 0.5f,
    float Openness = 0.5f)
{
    public static readonly PersonalityProfile Default = new();

    /// <summary>Gesture frequency multiplier based on personality.</summary>
    public float GestureFrequency => (1f - Introversion) * 0.6f + Openness * 0.4f;

    public static PersonalityProfile FromDescription(string description)
    {
        var lower = description.ToLowerInvariant();
        var introversion = lower.Contains("introvert") || lower.Contains("shy") || lower.Contains("quiet") ? 0.8f :
                           lower.Contains("extrovert") || lower.Contains("outgoing") ? 0.2f : 0.5f;
        var neuroticism = lower.Contains("nervous") || lower.Contains("anxious") || lower.Contains("hesitant") ? 0.8f :
                          lower.Contains("calm") || lower.Contains("relaxed") ? 0.2f : 0.5f;
        return new PersonalityProfile(Introversion: introversion, Neuroticism: neuroticism);
    }
}

public record VisualAttributes(
    string HairColor = "brown",
    string HairStyle = "medium",
    string SkinTone = "medium",
    string EyeColor = "brown",
    string OutfitDescription = "casual")
{
    public static readonly VisualAttributes Default = new();

    public static VisualAttributes FromDescription(string description)
    {
        var lower = description.ToLowerInvariant();

        var hairColor = lower.Contains("black hair") ? "black" :
                        lower.Contains("blonde") || lower.Contains("blond") ? "blonde" :
                        lower.Contains("red hair") || lower.Contains("auburn") ? "red" :
                        lower.Contains("white hair") || lower.Contains("grey hair") ? "white" : "brown";

        var hairStyle = lower.Contains("long hair") ? "long" :
                        lower.Contains("short hair") ? "short" :
                        lower.Contains("bald") ? "bald" : "medium";

        return new VisualAttributes(HairColor: hairColor, HairStyle: hairStyle);
    }
}
