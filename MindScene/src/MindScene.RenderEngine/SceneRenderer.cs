using Microsoft.Extensions.Logging;
using MindScene.ActorSystem;
using MindScene.Core.Common;
using MindScene.Core.Interfaces;
using MindScene.Core.Models;
using MindScene.SceneGraph;
using SkiaSharp;

namespace MindScene.RenderEngine;

public class SceneRenderer : IRenderer
{
    private readonly BackgroundRenderer _background = new();
    private readonly CameraController _camera = new();
    private readonly CharacterRenderer _character;
    private readonly SubtitleRenderer _subtitles = new();
    private readonly Dictionary<string, AnimationStateMachine> _stateMachines = [];
    private readonly ILogger<SceneRenderer> _logger;
    private float _lastTime;

    public SceneRenderer(CharacterRenderer character, ILogger<SceneRenderer> logger)
    {
        _character = character;
        _logger = logger;
    }

    public async Task<Result<byte[]>> RenderFrameAsync(
        ResolvedScene scene,
        float timeSeconds,
        CancellationToken cancellationToken = default)
    {
        var (w, h) = scene.Resolution.ToDimensions();
        try
        {
            var pixels = RenderFrame(scene, timeSeconds, w, h);
            return Result.Ok(pixels);
        }
        catch (Exception ex)
        {
            return Result.Fail<byte[]>($"Frame render failed at {timeSeconds:F3}s: {ex.Message}");
        }
        finally { await Task.CompletedTask; }
    }

    public async Task<Result<string>> RenderToFramesAsync(
        ResolvedScene scene,
        string outputDirectory,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var (w, h) = scene.Resolution.ToDimensions();
        int totalFrames = (int)(scene.TotalDurationSeconds * scene.Fps);
        float frameDuration = 1f / scene.Fps;

        _logger.LogInformation("Rendering {Frames} frames at {W}x{H} @ {Fps}fps", totalFrames, w, h, scene.Fps);

        // Ensure state machines exist for all actors
        foreach (var actor in scene.Actors)
            _stateMachines.TryAdd(actor.Actor.Name, new AnimationStateMachine());

        var timeline = new SceneTimeline(scene);
        _lastTime = 0f;

        for (int frameIndex = 0; frameIndex < totalFrames; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float timeSeconds = frameIndex * frameDuration;
            float deltaTime = timeSeconds - _lastTime;
            _lastTime = timeSeconds;

            try
            {
                var pixels = RenderFrameWithTimeline(scene, timeline, timeSeconds, deltaTime, w, h);
                var framePath = Path.Combine(outputDirectory, $"frame_{frameIndex:D6}.png");
                await SaveFrameAsync(pixels, framePath, w, h, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Frame {Index} failed, using blank frame", frameIndex);
            }

            progress?.Report((float)(frameIndex + 1) / totalFrames);

            if (frameIndex % 24 == 0)
                _logger.LogDebug("Rendered frame {Frame}/{Total} ({Pct:F0}%)",
                    frameIndex + 1, totalFrames, (frameIndex + 1) * 100.0 / totalFrames);
        }

        _logger.LogInformation("Rendering complete: {Frames} frames saved to {Dir}", totalFrames, outputDirectory);
        return Result.Ok(outputDirectory);
    }

    private byte[] RenderFrame(ResolvedScene scene, float timeSeconds, int w, int h)
    {
        var timeline = new SceneTimeline(scene);
        float delta = timeSeconds - _lastTime;
        _lastTime = timeSeconds;
        return RenderFrameWithTimeline(scene, timeline, timeSeconds, delta, w, h);
    }

    private byte[] RenderFrameWithTimeline(
        ResolvedScene scene, SceneTimeline timeline,
        float timeSeconds, float deltaTime, int w, int h)
    {
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        canvas.Save();

        // Layer 1: Background
        _background.Draw(canvas, scene, timeSeconds, w, h);

        // Apply camera transform
        _camera.ApplyTransform(canvas, scene, timeSeconds, w, h);

        // Layer 2: Characters (sorted by Z-layer)
        var sortedActors = scene.Actors.OrderBy(a => a.ZLayer).ToList();
        foreach (var placement in sortedActors)
        {
            if (!_stateMachines.TryGetValue(placement.Actor.Name, out var sm))
            {
                sm = new AnimationStateMachine();
                _stateMachines[placement.Actor.Name] = sm;
            }

            var state = timeline.GetActorState(placement.Actor.Name, timeSeconds);
            var frame = sm.GetFrame(state, timeSeconds, deltaTime);

            _character.Draw(canvas, placement.Actor, frame,
                placement.X, placement.Y, placement.Scale, w, h);
        }

        canvas.Restore();

        // Layer 3: Subtitles (always on top, no camera transform)
        _subtitles.Draw(canvas, scene, timeSeconds, w, h);

        // Apply lighting overlay
        ApplyLightingOverlay(canvas, scene.Setting, w, h);

        using var image = surface.Snapshot();
        using var pixmap = image.PeekPixels();
        return pixmap.GetPixelSpan().ToArray();
    }

    private void ApplyLightingOverlay(SKCanvas canvas, SettingDescription setting, int w, int h)
    {
        var overlayColor = GetLightingOverlay(setting.TimeOfDay, setting.LightingMood);
        if (overlayColor.Alpha == 0) return;
        using var paint = new SKPaint { Color = overlayColor };
        canvas.DrawRect(0, 0, w, h, paint);
    }

    private static SKColor GetLightingOverlay(string timeOfDay, string lighting) =>
        timeOfDay.ToLowerInvariant() switch
        {
            "night" => new SKColor(0, 5, 30, 80),
            "evening" => new SKColor(30, 0, 40, 60),
            "dusk" => new SKColor(60, 20, 0, 40),
            _ => lighting.ToLowerInvariant() switch
            {
                "dim" or "dark" => new SKColor(0, 0, 0, 70),
                "harsh" => new SKColor(255, 255, 200, 15),
                _ => SKColors.Transparent
            }
        };

    private static async Task SaveFrameAsync(byte[] pixels, string path, int w, int h, CancellationToken ct)
    {
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        var handle = bitmap.GetPixels();
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, handle, pixels.Length);
        using var encodedData = bitmap.Encode(SKEncodedImageFormat.Png, 90);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 65536, useAsync: true);
        await encodedData.AsStream().CopyToAsync(stream, ct);
    }
}
