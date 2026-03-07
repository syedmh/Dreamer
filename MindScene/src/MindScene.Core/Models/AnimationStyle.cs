namespace MindScene.Core.Models;

public enum AnimationStyle
{
    Realistic,
    Anime,
    Pixar,
    Watercolor,
    Noir,
    Cartoon,
    Sketch
}

public enum RenderQuality
{
    Preview,
    Standard,
    High
}

public enum ExportFormat
{
    Mp4,
    WebM,
    Gif,
    PngSequence
}

public enum ResolutionPreset
{
    Sd480p,
    Hd720p,
    FullHd1080p,
    Uhd4K
}

public static class ResolutionPresetExtensions
{
    public static (int Width, int Height) ToDimensions(this ResolutionPreset preset) => preset switch
    {
        ResolutionPreset.Sd480p => (854, 480),
        ResolutionPreset.Hd720p => (1280, 720),
        ResolutionPreset.FullHd1080p => (1920, 1080),
        ResolutionPreset.Uhd4K => (3840, 2160),
        _ => (1280, 720)
    };
}
