using System;

namespace TCFAnimation;

public readonly record struct AnimationSafeCenters(
    float Left,
    float Right);

public readonly record struct AnimationOffscreenCenters(
    float Left,
    float Right);

public static class AnimationGeometry
{
    public const float ViewportWidth = 1920.0f;
    public const float CanvasCenterX = 256.0f;
    public const float CharacterScale = 1.25f;
    public const float LeftWalkVisibleX = 74.0f;
    public const float RightWalkVisibleX = 437.0f;
    public const float GirlLeftVisibleX = 108.0f;
    public const float GirlTopVisibleY = 57.0f;
    public const float GirlRightVisibleX = 406.0f;
    public const float GirlBottomVisibleY = 840.0f;

    public static AnimationSafeCenters DefaultSafeCenters =>
        CalculateSafeCenters(
            viewportLeft: 0.0f,
            viewportWidth: ViewportWidth,
            canvasCenterX: CanvasCenterX,
            characterScale: CharacterScale,
            leftVisibleX: LeftWalkVisibleX,
            rightVisibleX: RightWalkVisibleX);

    public static AnimationOffscreenCenters DefaultOffscreenCenters =>
        CalculateOffscreenCenters(
            viewportLeft: 0.0f,
            viewportWidth: ViewportWidth,
            canvasCenterX: CanvasCenterX,
            characterScale: CharacterScale,
            leftVisibleX: LeftWalkVisibleX,
            rightVisibleX: RightWalkVisibleX);

    public static AnimationOffscreenCenters GirlOffscreenCenters =>
        CalculateOffscreenCenters(
            viewportLeft: 0.0f,
            viewportWidth: ViewportWidth,
            canvasCenterX: CanvasCenterX,
            characterScale: CharacterScale,
            leftVisibleX: GirlLeftVisibleX,
            rightVisibleX: GirlRightVisibleX);

    public static AnimationSafeCenters CalculateSafeCenters(
        float viewportLeft,
        float viewportWidth,
        float canvasCenterX,
        float characterScale,
        float leftVisibleX,
        float rightVisibleX)
    {
        if (!float.IsFinite(viewportLeft))
        {
            throw new ArgumentOutOfRangeException(nameof(viewportLeft));
        }

        if (!float.IsFinite(viewportWidth) || viewportWidth <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        }

        if (!float.IsFinite(canvasCenterX))
        {
            throw new ArgumentOutOfRangeException(nameof(canvasCenterX));
        }

        if (!float.IsFinite(characterScale) || characterScale <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(characterScale));
        }

        if (
            !float.IsFinite(leftVisibleX)
            || !float.IsFinite(rightVisibleX)
            || leftVisibleX > canvasCenterX
            || rightVisibleX < canvasCenterX
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(leftVisibleX),
                "Visible extrema must bracket the canvas center.");
        }

        float left =
            viewportLeft
            + (canvasCenterX - leftVisibleX) * characterScale;
        float right =
            viewportLeft
            + viewportWidth
            - (rightVisibleX - canvasCenterX) * characterScale;
        if (left > right)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewportWidth),
                "Viewport is too narrow for the scaled visible bounds.");
        }

        return new AnimationSafeCenters(left, right);
    }

    public static AnimationOffscreenCenters CalculateOffscreenCenters(
        float viewportLeft,
        float viewportWidth,
        float canvasCenterX,
        float characterScale,
        float leftVisibleX,
        float rightVisibleX)
    {
        _ = CalculateSafeCenters(
            viewportLeft,
            viewportWidth,
            canvasCenterX,
            characterScale,
            leftVisibleX,
            rightVisibleX);

        float left =
            viewportLeft
            - (rightVisibleX - canvasCenterX) * characterScale;
        float right =
            viewportLeft
            + viewportWidth
            + (canvasCenterX - leftVisibleX) * characterScale;
        return new AnimationOffscreenCenters(left, right);
    }
}
