namespace MindScene.Core.Models;

/// <summary>
/// Ekman's six basic emotions represented as a continuous vector (0.0 to 1.0).
/// </summary>
public record EmotionVector(
    float Joy = 0f,
    float Sadness = 0f,
    float Anger = 0f,
    float Fear = 0f,
    float Surprise = 0f,
    float Disgust = 0f)
{
    public static readonly EmotionVector Neutral = new();

    public static readonly EmotionVector Happy = new(Joy: 0.8f);
    public static readonly EmotionVector Sad = new(Sadness: 0.8f);
    public static readonly EmotionVector Angry = new(Anger: 0.8f);
    public static readonly EmotionVector Fearful = new(Fear: 0.8f);
    public static readonly EmotionVector Surprised = new(Surprise: 0.8f);
    public static readonly EmotionVector Disgusted = new(Disgust: 0.8f);

    /// <summary>Dominant emotion label for animation selection.</summary>
    public string DominantEmotion
    {
        get
        {
            var max = Math.Max(Joy, Math.Max(Sadness, Math.Max(Anger, Math.Max(Fear, Math.Max(Surprise, Disgust)))));
            if (max < 0.1f) return "neutral";
            if (Math.Abs(max - Joy) < 0.001f) return "joy";
            if (Math.Abs(max - Sadness) < 0.001f) return "sadness";
            if (Math.Abs(max - Anger) < 0.001f) return "anger";
            if (Math.Abs(max - Fear) < 0.001f) return "fear";
            if (Math.Abs(max - Surprise) < 0.001f) return "surprise";
            return "disgust";
        }
    }

    public float Intensity => Math.Max(Joy, Math.Max(Sadness, Math.Max(Anger, Math.Max(Fear, Math.Max(Surprise, Disgust)))));

    public EmotionVector Lerp(EmotionVector other, float t) => new(
        Joy: Joy + (other.Joy - Joy) * t,
        Sadness: Sadness + (other.Sadness - Sadness) * t,
        Anger: Anger + (other.Anger - Anger) * t,
        Fear: Fear + (other.Fear - Fear) * t,
        Surprise: Surprise + (other.Surprise - Surprise) * t,
        Disgust: Disgust + (other.Disgust - Disgust) * t);

    public static EmotionVector FromLabel(string label) => label.ToLowerInvariant() switch
    {
        "joy" or "happy" or "excited" or "pleased" => new(Joy: 0.8f),
        "sadness" or "sad" or "melancholic" or "resigned" => new(Sadness: 0.8f),
        "anger" or "angry" or "furious" or "frustrated" => new(Anger: 0.8f),
        "fear" or "fearful" or "scared" or "anxious" => new(Fear: 0.8f),
        "surprise" or "surprised" or "shocked" => new(Surprise: 0.8f),
        "disgust" or "disgusted" => new(Disgust: 0.8f),
        "nervous" or "tense" => new(Fear: 0.4f, Surprise: 0.2f),
        "remorseful" or "guilty" or "sorry" => new(Sadness: 0.6f, Fear: 0.2f),
        "bittersweet" => new(Joy: 0.3f, Sadness: 0.5f),
        _ => Neutral
    };
}
