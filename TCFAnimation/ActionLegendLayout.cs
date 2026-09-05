using System;

namespace TCFAnimation;

public readonly record struct LegendSize(float Width, float Height);

public static class ActionLegendLayout
{
    public const float SafeMargin = 24.0f;
    public const float PreferredWidth = 650.0f;
    public const float PreferredHeight = 840.0f;

    public static readonly string[] Entries =
    [
        "← / →   Walk left / right",
        "+ / −   Walk speed",
        "1–6     School scenes",
        "0       Exit school + hide bubble",
        "D       Exit nearest screen edge",
        "E       Enter from right",
        "F       Start 30s celebration",
        "S       Stop celebration",
        "R       Rain logos for 10s",
        "N       Toggle neon logo background",
        "O       Toggle neon animation",
        "I       Toggle neon color cycle",
        "X       Cross / release (normal)",
        "C       Clap (normal)",
        "Enter   Speak",
        "P       Hide bubble",
        "L       Show / hide legend",
        "F11 / Alt+Enter   Fullscreen",
        "Escape  Cancel input / windowed",
    ];

    public static LegendSize Calculate(float viewportWidth, float viewportHeight)
    {
        if (
            !float.IsFinite(viewportWidth)
            || !float.IsFinite(viewportHeight)
            || viewportWidth <= SafeMargin * 2.0f
            || viewportHeight <= SafeMargin * 2.0f
        )
        {
            throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        }

        return new LegendSize(
            MathF.Min(PreferredWidth, viewportWidth - SafeMargin * 2.0f),
            MathF.Min(PreferredHeight, viewportHeight - SafeMargin * 2.0f));
    }
}
