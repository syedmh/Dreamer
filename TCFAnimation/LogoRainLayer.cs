using System;
using Godot;

namespace TCFAnimation;

public partial class LogoRainLayer : Node2D
{
    public const int RuntimeSeed = 0x4C4F474F;
    public const int CaptureSeed = unchecked((int)0xCECC239Du);

    private readonly LogoRainSimulation _simulation = new();
    private Texture2D? _logo;

    public event Action? Finished;

    public bool IsActive => _simulation.IsActive;

    public int ActiveDropCount => _simulation.ActiveDropCount;

    public void Initialize(Texture2D logo)
    {
        _logo = logo ?? throw new ArgumentNullException(nameof(logo));
    }

    public override void _Ready()
    {
        ZIndex = -1;
        Visible = false;
        SetProcess(false);
    }

    public override void _Process(double delta)
    {
        bool wasActive = _simulation.IsActive;
        _simulation.Advance(delta);
        if (wasActive && !_simulation.IsActive)
        {
            Visible = false;
            SetProcess(false);
            Finished?.Invoke();
        }

        QueueRedraw();
    }

    public void Start(int seed = RuntimeSeed)
    {
        if (_logo is null)
        {
            throw new InvalidOperationException(
                "Logo rain must be initialized before it starts.");
        }

        _simulation.Start(seed);
        Visible = true;
        SetProcess(true);
        QueueRedraw();
    }

    public void StopAndClear()
    {
        _simulation.Stop();
        Visible = false;
        SetProcess(false);
        QueueRedraw();
    }

    public void AdvanceForCapture(double deltaSeconds)
    {
        _simulation.Advance(deltaSeconds);
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_logo is null || !_simulation.IsActive)
        {
            return;
        }

        Vector2 textureSize = _logo.GetSize();
        Rect2 centeredRect = new(-textureSize / 2.0f, textureSize);
        foreach (LogoDrop drop in _simulation.Snapshot())
        {
            DrawSetTransform(
                new Vector2(drop.X, drop.Y),
                drop.RotationRadians,
                Vector2.One * drop.Scale);
            DrawTextureRect(_logo, centeredRect, tile: false);
        }

        DrawSetTransform(Vector2.Zero, 0.0f, Vector2.One);
    }
}
