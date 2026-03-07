using MindScene.Core.Models;
using MindScene.SceneGraph;
using SkiaSharp;

namespace MindScene.RenderEngine;

/// <summary>
/// Renders the background layer: gradient sky, environment shapes, lighting, and particle effects.
/// </summary>
public class BackgroundRenderer
{
    private readonly List<Particle> _particles = [];
    private readonly Random _rng = new(42);
    private float _particleTimer;

    public void Draw(SKCanvas canvas, ResolvedScene scene, float timeSeconds, int width, int height)
    {
        var setting = scene.Setting;
        DrawSkyGradient(canvas, setting, width, height);
        DrawEnvironmentElements(canvas, setting, width, height);
        DrawAtmosphericEffects(canvas, setting, timeSeconds, width, height);
    }

    private void DrawSkyGradient(SKCanvas canvas, SettingDescription setting, int w, int h)
    {
        var (topColor, bottomColor) = GetSkyColors(setting.TimeOfDay, setting.LightingMood);
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(0, h * 0.6f),
            [topColor, bottomColor], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(0, 0, w, h * 0.65f, paint);
    }

    private void DrawEnvironmentElements(SKCanvas canvas, SettingDescription setting, int w, int h)
    {
        var template = setting.EnvironmentTemplate.ToLowerInvariant();
        var layout = SpatialResolver.GetLayout(template);

        switch (template)
        {
            case "rooftop":
                DrawRooftopEnvironment(canvas, setting, w, h);
                break;
            case "cafe":
                DrawCafeEnvironment(canvas, setting, w, h);
                break;
            case "forest":
                DrawForestEnvironment(canvas, w, h);
                break;
            default:
                DrawGenericEnvironment(canvas, setting, w, h);
                break;
        }

        // Ground/floor
        DrawFloor(canvas, setting, layout, w, h);
    }

    private void DrawRooftopEnvironment(SKCanvas canvas, SettingDescription setting, int w, int h)
    {
        // City skyline silhouette
        using var buildingPaint = new SKPaint { Color = new SKColor(20, 25, 40), IsAntialias = true };
        var buildingRng = new Random(12345);
        for (int i = 0; i < 20; i++)
        {
            float bx = (float)(i * w / 20.0 + buildingRng.NextDouble() * 30 - 15);
            float bw = buildingRng.Next(30, 80);
            float bh = buildingRng.Next(60, 200);
            canvas.DrawRect(bx, h * 0.55f - bh, bw, bh, buildingPaint);

            // Windows
            using var windowPaint = new SKPaint { Color = new SKColor(255, 240, 180, 180) };
            for (int wy = 0; wy < bh - 10; wy += 15)
            for (int wx = 5; wx < bw - 5; wx += 12)
                if (buildingRng.NextDouble() > 0.3)
                    canvas.DrawRect(bx + wx, h * 0.55f - bh + wy, 6, 8, windowPaint);
        }

        // Moon
        if (setting.TimeOfDay is "night" or "evening")
        {
            using var moonPaint = new SKPaint { Color = new SKColor(255, 248, 210, 230), IsAntialias = true };
            canvas.DrawCircle(w * 0.78f, h * 0.12f, 35f, moonPaint);
            using var moonShadow = new SKPaint { Color = new SKColor(30, 35, 60, 200), IsAntialias = true };
            canvas.DrawCircle(w * 0.78f + 15f, h * 0.12f - 8f, 32f, moonShadow);
        }

        // Stars
        if (setting.TimeOfDay is "night" or "evening")
        {
            using var starPaint = new SKPaint { Color = new SKColor(255, 255, 240, 200) };
            var starRng = new Random(9876);
            for (int i = 0; i < 80; i++)
                canvas.DrawCircle(starRng.Next(0, w), starRng.Next(0, (int)(h * 0.4f)), 1.5f, starPaint);
        }
    }

    private void DrawCafeEnvironment(SKCanvas canvas, SettingDescription setting, int w, int h)
    {
        // Wall
        using var wallPaint = new SKPaint { Color = new SKColor(195, 175, 145) };
        canvas.DrawRect(0, 0, w, h, wallPaint);

        // Wainscoting
        using var wainscotPaint = new SKPaint { Color = new SKColor(155, 130, 100) };
        canvas.DrawRect(0, h * 0.6f, w, h * 0.4f, wainscotPaint);

        // Large window with rain
        using var windowFramePaint = new SKPaint { Color = new SKColor(80, 60, 40) };
        using var windowGlassPaint = new SKPaint { Color = new SKColor(150, 180, 210, 120) };
        canvas.DrawRect(w * 0.05f, h * 0.1f, w * 0.35f, h * 0.45f, windowFramePaint);
        canvas.DrawRect(w * 0.07f, h * 0.12f, w * 0.31f, h * 0.41f, windowGlassPaint);

        // Pendant lamp
        using var lampPaint = new SKPaint { Color = new SKColor(220, 200, 160) };
        canvas.DrawCircle(w * 0.5f, h * 0.15f, 20f, lampPaint);
        using var cordPaint = new SKPaint { Color = new SKColor(60, 50, 40), IsStroke = true, StrokeWidth = 2f };
        canvas.DrawLine(w * 0.5f, 0, w * 0.5f, h * 0.15f, cordPaint);
    }

    private void DrawForestEnvironment(SKCanvas canvas, int w, int h)
    {
        // Trees
        using var trunkPaint = new SKPaint { Color = new SKColor(70, 50, 30) };
        using var leavesPaint = new SKPaint { Color = new SKColor(40, 100, 50, 220), IsAntialias = true };

        for (int i = 0; i < 8; i++)
        {
            float tx = i * w / 7.5f;
            float th = 150f + (i % 3) * 60f;
            canvas.DrawRect(tx - 15, h * 0.6f - th, 30, th, trunkPaint);
            canvas.DrawCircle(tx, h * 0.6f - th, 60f + (i % 2) * 20f, leavesPaint);
        }
    }

    private void DrawGenericEnvironment(SKCanvas canvas, SettingDescription setting, int w, int h)
    {
        using var groundPaint = new SKPaint { Color = GetGroundColor(setting.Mood) };
        canvas.DrawRect(0, h * 0.55f, w, h * 0.45f, groundPaint);
    }

    private void DrawFloor(SKCanvas canvas, SettingDescription setting, EnvironmentLayout layout, int w, int h)
    {
        float floorY = layout.FloorY * h;
        using var floorPaint = new SKPaint { Color = GetFloorColor(setting.EnvironmentTemplate), IsAntialias = false };
        canvas.DrawRect(0, floorY, w, h - floorY, floorPaint);
    }

    private void DrawAtmosphericEffects(SKCanvas canvas, SettingDescription setting,
        float timeSeconds, int w, int h)
    {
        if (setting.Weather is "rainy" or "rain") DrawRain(canvas, timeSeconds, w, h);
        if (setting.EnvironmentTemplate is "rooftop") DrawCherryBlossoms(canvas, timeSeconds, w, h);
    }

    private void DrawRain(SKCanvas canvas, float time, int w, int h)
    {
        using var rainPaint = new SKPaint
        {
            Color = new SKColor(180, 200, 230, 120),
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = 1.2f
        };

        var rng = new Random((int)(time * 30));
        for (int i = 0; i < 80; i++)
        {
            float rx = rng.Next(0, w);
            float ry = (rng.Next(0, h) + time * 400f) % h;
            canvas.DrawLine(rx, ry, rx - 3f, ry + 15f, rainPaint);
        }
    }

    private void DrawCherryBlossoms(SKCanvas canvas, float time, int w, int h)
    {
        _particleTimer += 0.016f; // assume ~60fps
        if (_particleTimer > 0.1f)
        {
            _particleTimer = 0f;
            if (_particles.Count < 30)
                _particles.Add(new Particle(
                    X: _rng.Next(0, w),
                    Y: -10f,
                    VX: (float)(_rng.NextDouble() - 0.5) * 40f,
                    VY: (float)(_rng.NextDouble() * 30 + 20),
                    Size: (float)(_rng.NextDouble() * 5 + 3),
                    Rotation: (float)(_rng.NextDouble() * 360),
                    RotSpeed: (float)(_rng.NextDouble() * 90 - 45)));
        }

        using var petalPaint = new SKPaint { Color = new SKColor(255, 183, 197, 200), IsAntialias = true };

        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            var newP = p with
            {
                X = p.X + p.VX * 0.016f,
                Y = p.Y + p.VY * 0.016f,
                Rotation = p.Rotation + p.RotSpeed * 0.016f
            };

            canvas.Save();
            canvas.RotateDegrees(newP.Rotation, newP.X, newP.Y);
            canvas.DrawOval(new SKRect(newP.X - newP.Size, newP.Y - newP.Size * 0.6f,
                newP.X + newP.Size, newP.Y + newP.Size * 0.6f), petalPaint);
            canvas.Restore();

            if (newP.Y > h)
                _particles.RemoveAt(i);
            else
                _particles[i] = newP;
        }
    }

    private static (SKColor Top, SKColor Bottom) GetSkyColors(string timeOfDay, string lighting) =>
        timeOfDay.ToLowerInvariant() switch
        {
            "night" => (new SKColor(5, 10, 35), new SKColor(20, 30, 60)),
            "evening" => (new SKColor(25, 20, 60), new SKColor(60, 40, 80)),
            "dawn" => (new SKColor(255, 150, 100), new SKColor(255, 200, 150)),
            "dusk" => (new SKColor(200, 80, 50), new SKColor(240, 140, 80)),
            "afternoon" => (new SKColor(100, 160, 230), new SKColor(180, 210, 240)),
            _ => (new SKColor(100, 150, 220), new SKColor(170, 200, 235)) // day
        };

    private static SKColor GetGroundColor(string mood) => mood.ToLowerInvariant() switch
    {
        "tense" or "ominous" => new SKColor(50, 50, 55),
        "romantic" => new SKColor(80, 60, 80),
        "peaceful" => new SKColor(80, 120, 80),
        _ => new SKColor(70, 80, 90)
    };

    private static SKColor GetFloorColor(string template) => template.ToLowerInvariant() switch
    {
        "cafe" => new SKColor(140, 110, 80),
        "rooftop" => new SKColor(80, 80, 85),
        "office" => new SKColor(180, 175, 165),
        "forest" => new SKColor(60, 90, 50),
        _ => new SKColor(100, 100, 105)
    };
}

internal record Particle(float X, float Y, float VX, float VY, float Size, float Rotation, float RotSpeed);
