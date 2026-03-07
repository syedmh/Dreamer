using MindScene.Core.Models;

namespace MindScene.SceneGraph;

/// <summary>
/// Resolves natural-language spatial relationships into concrete frame coordinates.
/// </summary>
public static class SpatialResolver
{
    private static readonly Dictionary<string, EnvironmentLayout> Layouts = new()
    {
        ["cafe"] = new EnvironmentLayout(
            BackgroundLayers: ["cafe_exterior", "cafe_window_rain", "cafe_interior_bg"],
            FloorY: 0.72f, WallY: 0.25f,
            DefaultActorPositions: [(0.3f, 0.68f), (0.65f, 0.68f)]),
        ["rooftop"] = new EnvironmentLayout(
            BackgroundLayers: ["city_skyline_night", "rooftop_floor", "rooftop_railing"],
            FloorY: 0.75f, WallY: 0.15f,
            DefaultActorPositions: [(0.35f, 0.7f), (0.6f, 0.7f)]),
        ["office"] = new EnvironmentLayout(
            BackgroundLayers: ["office_wall", "office_desk", "office_items"],
            FloorY: 0.7f, WallY: 0.2f,
            DefaultActorPositions: [(0.25f, 0.65f), (0.7f, 0.65f)]),
        ["forest"] = new EnvironmentLayout(
            BackgroundLayers: ["forest_bg", "forest_mid", "forest_fg"],
            FloorY: 0.78f, WallY: 0.1f,
            DefaultActorPositions: [(0.3f, 0.72f), (0.6f, 0.72f)]),
        ["bedroom"] = new EnvironmentLayout(
            BackgroundLayers: ["bedroom_wall", "bedroom_furniture"],
            FloorY: 0.73f, WallY: 0.22f,
            DefaultActorPositions: [(0.4f, 0.68f)]),
        ["generic"] = new EnvironmentLayout(
            BackgroundLayers: ["background_gradient"],
            FloorY: 0.72f, WallY: 0.2f,
            DefaultActorPositions: [(0.3f, 0.68f), (0.65f, 0.68f)])
    };

    public static EnvironmentLayout GetLayout(string template)
    {
        var key = template.ToLowerInvariant();
        return Layouts.TryGetValue(key, out var layout) ? layout : Layouts["generic"];
    }

    public static (float X, float Y) ResolveRelativePosition(string description, int actorIndex, EnvironmentLayout layout)
    {
        var lower = description.ToLowerInvariant();

        float x = actorIndex < layout.DefaultActorPositions.Count
            ? layout.DefaultActorPositions[actorIndex].X
            : 0.3f + actorIndex * 0.2f;
        float y = layout.FloorY;

        if (lower.Contains("corner")) x = actorIndex == 0 ? 0.15f : 0.85f;
        else if (lower.Contains("center") || lower.Contains("middle")) x = 0.5f;
        else if (lower.Contains("left")) x = 0.25f;
        else if (lower.Contains("right")) x = 0.75f;

        if (lower.Contains("window")) y = layout.WallY + 0.1f;
        else if (lower.Contains("sit") || lower.Contains("booth") || lower.Contains("chair")) y = layout.FloorY + 0.05f;

        return (x, y);
    }
}

public record EnvironmentLayout(
    List<string> BackgroundLayers,
    float FloorY,
    float WallY,
    List<(float X, float Y)> DefaultActorPositions);
