using System;
using Godot;

namespace TCFAnimation;

public partial class FireworksLayer : Node2D
{
    public const int RuntimeSeed = 0x544346;
    public const int CaptureSeed = unchecked((int)0xCECC239Cu);

    private static readonly Color[] Palette =
    [
        new("ff4f81"),
        new("ffd84d"),
        new("47e6ff"),
        new("8aff80"),
        new("b783ff"),
        new("ff8b3d"),
    ];

    private readonly FireworksSimulation _simulation = new();

    public bool IsActive => _simulation.IsActive;

    public int ActiveBurstCount => _simulation.ActiveBurstCount;

    public override void _Ready()
    {
        ZIndex = -1;
        Visible = false;
        SetProcess(false);
    }

    public override void _Process(double delta)
    {
        _simulation.Advance(delta);
        QueueRedraw();
    }

    public void Start(int seed = RuntimeSeed)
    {
        _simulation.Start(seed);
        Visible = true;
        SetProcess(true);
        QueueRedraw();
    }

    public void StopAndClear()
    {
        _simulation.Stop();
        SetProcess(false);
        Visible = false;
        QueueRedraw();
    }

    public void AdvanceForCapture(double deltaSeconds)
    {
        _simulation.Advance(deltaSeconds);
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!_simulation.IsActive)
        {
            return;
        }

        foreach (FireworkBurst burst in _simulation.Snapshot())
        {
            DrawBurst(burst);
        }
    }

    private void DrawBurst(FireworkBurst burst)
    {
        float progress = Math.Clamp(
            burst.AgeSeconds / burst.DurationSeconds,
            0.0f,
            1.0f);
        float expansion = 1.0f - MathF.Pow(1.0f - progress, 3.0f);
        float fade = 1.0f - SmoothStep(0.48f, 1.0f, progress);
        float radius = burst.MaximumRadius * expansion;
        Vector2 center = new(burst.Center.X, burst.Center.Y);
        Color primary = WithAlpha(
            Palette[burst.PaletteIndex % Palette.Length],
            fade);
        Color secondary = WithAlpha(
            Palette[(burst.PaletteIndex + 2) % Palette.Length],
            fade * 0.86f);

        DrawCircle(
            center,
            8.0f + 8.0f * (1.0f - progress),
            WithAlpha(Colors.White, fade),
            filled: true,
            width: -1.0f,
            antialiased: true);

        for (int ray = 0; ray < burst.RayCount; ray++)
        {
            uint rayBits = Mix(burst.SparkSeed, unchecked((uint)ray));
            float jitter =
                ((rayBits & 0xFFFFu) / 65535.0f - 0.5f)
                * (MathF.Tau / burst.RayCount * 0.42f);
            float angle =
                burst.RotationRadians
                + MathF.Tau * ray / burst.RayCount
                + jitter;
            Vector2 direction = new(MathF.Cos(angle), MathF.Sin(angle));
            float lengthScale =
                0.76f + ((rayBits >> 16) & 0xFFu) / 255.0f * 0.34f;
            float gravity = progress * progress * 54.0f;
            Vector2 start =
                center + direction * radius * 0.20f
                + new Vector2(0.0f, gravity * 0.20f);
            Vector2 end =
                center + direction * radius * lengthScale
                + new Vector2(0.0f, gravity);
            Color color = ray % 3 == 0 ? secondary : primary;
            float width = MathF.Max(1.5f, 7.0f * (1.0f - progress));
            DrawLine(start, end, color, width, antialiased: true);
            DrawCircle(
                end,
                MathF.Max(2.0f, 5.5f * (1.0f - progress)),
                color,
                filled: true,
                width: -1.0f,
                antialiased: true);

            if (ray % 2 == 0)
            {
                Vector2 spark =
                    center + direction * radius * (0.48f + lengthScale * 0.25f)
                    + new Vector2(0.0f, gravity * 0.62f);
                DrawCircle(
                    spark,
                    MathF.Max(1.5f, 3.5f * fade),
                    WithAlpha(Colors.White, fade * 0.82f),
                    filled: true,
                    width: -1.0f,
                    antialiased: true);
            }
        }
    }

    private static uint Mix(uint seed, uint value)
    {
        uint mixed = seed ^ (value + 0x9E3779B9u + (seed << 6) + (seed >> 2));
        mixed ^= mixed >> 16;
        mixed *= 0x7FEB352Du;
        mixed ^= mixed >> 15;
        mixed *= 0x846CA68Bu;
        return mixed ^ (mixed >> 16);
    }

    private static float SmoothStep(float from, float to, float value)
    {
        float normalized = Math.Clamp((value - from) / (to - from), 0.0f, 1.0f);
        return normalized * normalized * (3.0f - 2.0f * normalized);
    }

    private static Color WithAlpha(Color color, float alpha)
    {
        return new Color(color.R, color.G, color.B, Math.Clamp(alpha, 0.0f, 1.0f));
    }
}
