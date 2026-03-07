namespace MindScene.Core.Models;

public record VoiceProfile(
    string VoiceId = "default",
    float Pitch = 1.0f,
    float Speed = 1.0f,
    string Accent = "neutral",
    string Gender = "neutral",
    string Description = "")
{
    public static readonly VoiceProfile Default = new();

    public static VoiceProfile FromDescription(string description)
    {
        var lower = description.ToLowerInvariant();
        var pitch = lower.Contains("high") || lower.Contains("feminine") ? 1.3f :
                    lower.Contains("low") || lower.Contains("deep") ? 0.7f : 1.0f;
        var speed = lower.Contains("slow") || lower.Contains("hesitant") ? 0.85f :
                    lower.Contains("fast") || lower.Contains("rapid") ? 1.2f : 1.0f;
        var gender = lower.Contains("femin") || lower.Contains("woman") || lower.Contains("girl") ? "female" :
                     lower.Contains("mascu") || lower.Contains("man") || lower.Contains("boy") ? "male" : "neutral";
        return new VoiceProfile(Pitch: pitch, Speed: speed, Gender: gender, Description: description);
    }
}
