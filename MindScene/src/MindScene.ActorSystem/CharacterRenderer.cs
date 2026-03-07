using MindScene.Core.Models;
using SkiaSharp;

namespace MindScene.ActorSystem;

/// <summary>
/// Renders a character as a layered sprite composite using SkiaSharp.
/// In the MVP, characters are drawn procedurally using geometric shapes.
/// </summary>
public class CharacterRenderer
{
    private readonly AnimationStateMachine _stateMachine = new();

    /// <summary>
    /// Draws a character onto the canvas at the given frame position.
    /// </summary>
    public void Draw(
        SKCanvas canvas,
        ActorDefinition actor,
        AnimationFrame frame,
        float centerX,
        float baseY,
        float scale,
        int frameWidth,
        int frameHeight)
    {
        var emotion = EmotionVector.FromLabel(frame.FaceSprite.Replace("face_", "").Replace("_speaking", ""));
        var pxX = centerX * frameWidth;
        var pxY = (baseY * frameHeight) + frame.BodyOffsetY;
        var charHeight = 180f * scale;
        var charWidth = 80f * scale;

        DrawBody(canvas, actor, emotion, frame, pxX, pxY, charWidth, charHeight);
        DrawHead(canvas, actor, emotion, frame, pxX, pxY - charHeight * 0.55f + frame.HeadOffsetY, charWidth, scale);
    }

    private void DrawBody(SKCanvas canvas, ActorDefinition actor, EmotionVector emotion,
        AnimationFrame frame, float x, float y, float width, float height)
    {
        var bodyColor = GetOutfitColor(actor.Visual.OutfitDescription);
        using var bodyPaint = new SKPaint { Color = bodyColor, IsAntialias = true };

        // Torso
        var torsoRect = new SKRect(x - width * 0.45f, y - height * 0.4f, x + width * 0.45f, y);
        canvas.DrawRoundRect(torsoRect, 8f, 8f, bodyPaint);

        // Arms — position varies with action
        DrawArms(canvas, frame, x, y, width, height, bodyPaint);

        // Legs
        using var legPaint = new SKPaint { Color = DarkenColor(bodyColor, 0.7f), IsAntialias = true };
        var leftLeg = new SKRect(x - width * 0.4f, y - 5f, x - width * 0.05f, y + height * 0.42f);
        var rightLeg = new SKRect(x + width * 0.05f, y - 5f, x + width * 0.4f, y + height * 0.42f);
        canvas.DrawRoundRect(leftLeg, 5f, 5f, legPaint);
        canvas.DrawRoundRect(rightLeg, 5f, 5f, legPaint);
    }

    private void DrawArms(SKCanvas canvas, AnimationFrame frame, float x, float y,
        float width, float height, SKPaint paint)
    {
        // Left arm
        float leftAngle = frame.BodySprite.Contains("gesture") ? -30f : 15f;
        float rightAngle = frame.BodySprite.Contains("gesture") ? 50f : -15f;

        DrawArm(canvas, x - width * 0.45f, y - height * 0.3f, leftAngle, width * 0.2f, height * 0.35f, paint);
        DrawArm(canvas, x + width * 0.45f, y - height * 0.3f, rightAngle, width * 0.2f, height * 0.35f, paint);
    }

    private void DrawArm(SKCanvas canvas, float x, float y, float angleDeg, float w, float h, SKPaint paint)
    {
        canvas.Save();
        canvas.RotateDegrees(angleDeg, x, y);
        canvas.DrawRoundRect(new SKRect(x - w / 2f, y, x + w / 2f, y + h), 4f, 4f, paint);
        canvas.Restore();
    }

    private void DrawHead(SKCanvas canvas, ActorDefinition actor, EmotionVector emotion,
        AnimationFrame frame, float x, float y, float width, float scale)
    {
        var headRadius = width * 0.55f;
        var skinColor = GetSkinColor(actor.Visual.SkinTone);

        using var headPaint = new SKPaint { Color = skinColor, IsAntialias = true };
        canvas.Save();
        canvas.RotateDegrees(frame.HeadRotation, x, y);

        // Head circle
        canvas.DrawCircle(x, y, headRadius, headPaint);

        // Hair
        DrawHair(canvas, actor, x, y, headRadius);

        // Eyes
        DrawEyes(canvas, emotion, frame, x, y, headRadius);

        // Mouth
        DrawMouth(canvas, emotion, frame, x, y, headRadius);

        canvas.Restore();
    }

    private void DrawHair(SKCanvas canvas, ActorDefinition actor, float x, float y, float r)
    {
        var hairColor = GetHairColor(actor.Visual.HairColor);
        using var hairPaint = new SKPaint { Color = hairColor, IsAntialias = true };

        if (actor.Visual.HairStyle == "long")
        {
            // Long hair sides
            canvas.DrawOval(new SKRect(x - r * 1.1f, y - r * 0.6f, x - r * 0.5f, y + r * 1.2f), hairPaint);
            canvas.DrawOval(new SKRect(x + r * 0.5f, y - r * 0.6f, x + r * 1.1f, y + r * 1.2f), hairPaint);
        }
        // Top of hair
        canvas.DrawOval(new SKRect(x - r * 0.95f, y - r * 1.1f, x + r * 0.95f, y - r * 0.1f), hairPaint);
    }

    private void DrawEyes(SKCanvas canvas, EmotionVector emotion, AnimationFrame frame,
        float x, float y, float r)
    {
        var isClosed = frame.EyeSprite.Contains("closed") || frame.EyeSprite.Contains("blink");
        using var eyeWhitePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var eyePupilPaint = new SKPaint { Color = SKColors.Black.WithAlpha(200), IsAntialias = true };

        float eyeY = y - r * 0.15f;
        float leftEyeX = x - r * 0.35f;
        float rightEyeX = x + r * 0.35f;
        float eyeW = r * 0.28f;
        float eyeH = isClosed ? r * 0.04f : r * 0.22f;

        // Adjust for emotion
        if (emotion.Sadness > 0.5f) eyeY += r * 0.06f;
        if (emotion.Anger > 0.5f) eyeH *= 0.7f;
        if (emotion.Surprise > 0.5f) eyeH *= 1.3f;

        foreach (var ex in new[] { leftEyeX, rightEyeX })
        {
            canvas.DrawOval(new SKRect(ex - eyeW, eyeY - eyeH, ex + eyeW, eyeY + eyeH), eyeWhitePaint);
            if (!isClosed)
                canvas.DrawCircle(ex, eyeY + eyeH * 0.1f, eyeW * 0.55f, eyePupilPaint);
        }

        // Eyebrows
        DrawEyebrows(canvas, emotion, leftEyeX, rightEyeX, eyeY, eyeW, r);
    }

    private void DrawEyebrows(SKCanvas canvas, EmotionVector emotion,
        float leftX, float rightX, float eyeY, float eyeW, float r)
    {
        using var browPaint = new SKPaint
        {
            Color = new SKColor(80, 45, 20, 200),
            IsAntialias = true,
            StrokeWidth = r * 0.1f,
            IsStroke = true,
            StrokeCap = SKStrokeCap.Round
        };

        float browY = eyeY - r * 0.28f;
        float tilt = emotion.Anger > 0.5f ? r * 0.12f :
                     emotion.Sadness > 0.5f ? -r * 0.1f : 0f;

        canvas.DrawLine(leftX - eyeW, browY + tilt, leftX + eyeW, browY - tilt, browPaint);
        canvas.DrawLine(rightX - eyeW, browY - tilt, rightX + eyeW, browY + tilt, browPaint);
    }

    private void DrawMouth(SKCanvas canvas, EmotionVector emotion, AnimationFrame frame,
        float x, float y, float r)
    {
        using var mouthPaint = new SKPaint
        {
            Color = new SKColor(180, 80, 80),
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = r * 0.09f,
            StrokeCap = SKStrokeCap.Round
        };

        float mouthY = y + r * 0.45f;
        float mouthW = r * 0.42f;

        // Choose mouth shape based on viseme or emotion
        var isOpen = frame.MouthSprite != "mouth_rest" && frame.MouthSprite != "mouth_closed";
        if (isOpen)
        {
            using var fillPaint = new SKPaint { Color = new SKColor(100, 30, 30), IsAntialias = true };
            canvas.DrawOval(new SKRect(x - mouthW * 0.6f, mouthY - r * 0.12f, x + mouthW * 0.6f, mouthY + r * 0.12f), fillPaint);
        }

        // Curve up for joy, down for sadness
        float curveDir = emotion.Joy > 0.5f ? -1f : emotion.Sadness > 0.5f ? 1f : 0f;
        var path = new SKPath();
        path.MoveTo(x - mouthW, mouthY);
        path.QuadTo(x, mouthY + curveDir * r * 0.15f, x + mouthW, mouthY);
        canvas.DrawPath(path, mouthPaint);
    }

    private static SKColor GetSkinColor(string tone) => tone.ToLowerInvariant() switch
    {
        "light" or "pale" => new SKColor(255, 224, 196),
        "medium" => new SKColor(224, 172, 130),
        "dark" or "brown" => new SKColor(160, 105, 70),
        "deep" => new SKColor(100, 65, 45),
        _ => new SKColor(224, 172, 130)
    };

    private static SKColor GetHairColor(string color) => color.ToLowerInvariant() switch
    {
        "black" => new SKColor(25, 20, 20),
        "brown" => new SKColor(90, 55, 30),
        "blonde" => new SKColor(220, 190, 110),
        "red" => new SKColor(165, 60, 35),
        "white" or "grey" => new SKColor(200, 195, 195),
        _ => new SKColor(90, 55, 30)
    };

    private static SKColor GetOutfitColor(string description)
    {
        var lower = description.ToLowerInvariant();
        if (lower.Contains("red")) return new SKColor(180, 50, 50);
        if (lower.Contains("blue")) return new SKColor(50, 80, 160);
        if (lower.Contains("green")) return new SKColor(50, 130, 70);
        if (lower.Contains("black")) return new SKColor(40, 40, 45);
        if (lower.Contains("white")) return new SKColor(240, 238, 235);
        if (lower.Contains("jacket")) return new SKColor(55, 50, 65);
        if (lower.Contains("scarf")) return new SKColor(180, 50, 50);
        return new SKColor(80, 100, 140); // default: muted blue
    }

    private static SKColor DarkenColor(SKColor c, float factor) =>
        new((byte)(c.Red * factor), (byte)(c.Green * factor), (byte)(c.Blue * factor));
}
