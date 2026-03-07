using MindScene.Core.Models;
using SkiaSharp;

namespace MindScene.RenderEngine;

public class SubtitleRenderer
{
    private static readonly SKFont TextFont = new(SKTypeface.Default, 22f);
    private static readonly SKFont NameFont = new(SKTypeface.FromFamilyName(null, SKFontStyle.Bold), 18f);

    public void Draw(SKCanvas canvas, ResolvedScene scene, float timeSeconds, int width, int height)
    {
        var activeLine = scene.Dialogue.FirstOrDefault(d =>
            !d.Timing.IsAuto &&
            timeSeconds >= d.Timing.StartSeconds &&
            timeSeconds <= d.Timing.EndSeconds);

        if (activeLine is null) return;

        using var bgPaint = new SKPaint { Color = new SKColor(0, 0, 0, 170) };
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var namePaint = new SKPaint { Color = new SKColor(255, 220, 100), IsAntialias = true };

        var text = activeLine.Text;
        var speaker = activeLine.Speaker;
        var subtitleY = height - 80f;
        var padding = 12f;

        var textWidth = TextFont.MeasureText(text, textPaint);
        var nameWidth = NameFont.MeasureText(speaker, namePaint);
        var bgWidth = Math.Max(textWidth, nameWidth) + padding * 2;
        var bgX = (width - bgWidth) / 2f;

        canvas.DrawRoundRect(new SKRect(bgX, subtitleY - 42f, bgX + bgWidth, subtitleY + padding), 6f, 6f, bgPaint);
        canvas.DrawText(speaker, bgX + padding, subtitleY - 22f, SKTextAlign.Left, NameFont, namePaint);
        canvas.DrawText(text, bgX + padding, subtitleY, SKTextAlign.Left, TextFont, textPaint);
    }
}
