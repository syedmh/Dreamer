using MindScene.Core.Models;
using SkiaSharp;

namespace MindScene.RenderEngine;

/// <summary>
/// Applies camera transformations (pan, zoom, shake) to the canvas for a given time.
/// </summary>
public class CameraController
{
    private readonly Random _shakeRng = new(42);

    public void ApplyTransform(SKCanvas canvas, ResolvedScene scene, float timeSeconds, int width, int height)
    {
        var activeInstructions = scene.CameraDirections
            .Where(c => timeSeconds >= c.StartSeconds && timeSeconds <= c.StartSeconds + c.DurationSeconds)
            .ToList();

        if (activeInstructions.Count == 0) return;

        float translateX = 0f, translateY = 0f, scaleF = 1f;
        float pivotX = width / 2f, pivotY = height / 2f;

        foreach (var instruction in activeInstructions)
        {
            var t = (timeSeconds - instruction.StartSeconds) / instruction.DurationSeconds;
            t = EaseInOut(t);

            switch (instruction.Movement)
            {
                case CameraMovement.Pan:
                    translateX += Lerp(-width * 0.08f, width * 0.08f, t);
                    break;
                case CameraMovement.ZoomIn:
                    var targetZoom = instruction.ZoomFactor ?? 1.3f;
                    scaleF = Lerp(1f, targetZoom, t);
                    break;
                case CameraMovement.ZoomOut:
                    scaleF = Lerp(instruction.ZoomFactor ?? 1.3f, 1f, t);
                    break;
                case CameraMovement.Shake:
                    var intensity = (1f - t) * 6f;
                    translateX += (float)(_shakeRng.NextDouble() * 2 - 1) * intensity;
                    translateY += (float)(_shakeRng.NextDouble() * 2 - 1) * intensity;
                    break;
                case CameraMovement.Dolly:
                    translateX += Lerp(0, width * 0.05f, t);
                    translateY += Lerp(0, -height * 0.02f, t);
                    break;
            }
        }

        if (Math.Abs(scaleF - 1f) > 0.001f)
        {
            canvas.Translate(pivotX, pivotY);
            canvas.Scale(scaleF);
            canvas.Translate(-pivotX, -pivotY);
        }

        if (Math.Abs(translateX) > 0.1f || Math.Abs(translateY) > 0.1f)
            canvas.Translate(translateX, translateY);
    }

    private static float EaseInOut(float t) => t < 0.5f ? 2f * t * t : 1f - (float)Math.Pow(-2f * t + 2f, 2) / 2f;
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
