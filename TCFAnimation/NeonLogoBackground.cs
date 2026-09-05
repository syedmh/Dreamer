using System;
using Godot;

namespace TCFAnimation;

public partial class NeonLogoBackground : Node2D
{
    public const float DisplayScale = 0.75f;
    public const float TopMargin = 48.0f;

    private const string ShaderCode =
        """
        shader_type canvas_item;
        render_mode unshaded;

        uniform float phase_seconds = 0.0;
        uniform float pulse_seconds = 2.5;
        uniform float hue_cycle_seconds = 8.0;
        uniform float base_intensity = 0.7;
        uniform float animation_amount = 0.0;
        uniform float color_cycle_amount = 0.0;

        void fragment()
        {
            vec4 source = texture(TEXTURE, UV);
            float source_luminance = max(source.r, max(source.g, source.b));
            float green_mask =
                smoothstep(
                    0.05,
                    0.35,
                    source.g - max(source.r, source.b)
                );
            vec3 cycling_color =
                (
                    0.55
                    + 0.45
                        * cos(
                            vec3(0.0, 2.094, 4.188)
                            + phase_seconds
                                * 6.28318530718
                                / hue_cycle_seconds
                            + UV.x * 3.5
                        )
                )
                * source_luminance;
            vec3 preserved_color =
                mix(
                    source.rgb,
                    cycling_color,
                    green_mask * color_cycle_amount
                );
            float pulse =
                1.0
                + animation_amount
                    * 0.08
                    * sin(
                        phase_seconds * 6.28318530718 / pulse_seconds
                        + UV.x * 8.0
                        - UV.y * 3.0
                    );
            COLOR =
                vec4(
                    preserved_color * base_intensity * pulse,
                    source.a
                );
        }
        """;

    private Texture2D? _texture;
    private ShaderMaterial? _material;
    private Rect2 _destination;
    private Vector2 _lastViewportSize;
    private double _phaseSeconds;

    public bool IsEnabled { get; private set; }

    public bool IsAnimationEnabled { get; private set; }

    public bool IsColorCycleEnabled { get; private set; }

    public bool IsShowing => IsEnabled;

    public void Initialize(Texture2D texture)
    {
        _texture = texture ?? throw new ArgumentNullException(nameof(texture));
        Shader shader = new()
        {
            Code = ShaderCode,
        };
        _material = new ShaderMaterial
        {
            Shader = shader,
        };
        _material.SetShaderParameter(
            "pulse_seconds",
            (float)AnimationConfig.Current.NeonPulseSeconds);
        _material.SetShaderParameter(
            "hue_cycle_seconds",
            (float)AnimationConfig.Current.NeonHueCycleSeconds);
        _material.SetShaderParameter(
            "base_intensity",
            (float)AnimationConfig.Current.NeonIntensity);
        _material.SetShaderParameter("animation_amount", 0.0f);
        _material.SetShaderParameter("color_cycle_amount", 0.0f);
        Material = _material;
        TextureFilter = TextureFilterEnum.Linear;
    }

    public override void _Ready()
    {
        if (_texture is null || _material is null)
        {
            throw new InvalidOperationException(
                "The neon background must be initialized before it enters the scene.");
        }

        ZIndex = -3;
        Visible = false;
        SetProcess(false);
        GetViewport().SizeChanged += OnViewportSizeChanged;
        UpdateLayout(GetViewport().GetVisibleRect().Size);
    }

    public override void _Process(double delta)
    {
        if (!double.IsFinite(delta) || delta < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(delta));
        }

        _phaseSeconds += delta;
        _material!.SetShaderParameter(
            "phase_seconds",
            (float)_phaseSeconds);
        QueueRedraw();
    }

    public bool Toggle()
    {
        IsEnabled = !IsEnabled;
        UpdateVisibility();
        return IsEnabled;
    }

    public bool ToggleAnimation()
    {
        IsAnimationEnabled = !IsAnimationEnabled;
        _material!.SetShaderParameter(
            "animation_amount",
            IsAnimationEnabled ? 1.0f : 0.0f);
        UpdateProcessing();
        QueueRedraw();
        return IsAnimationEnabled;
    }

    public bool ToggleColorCycle()
    {
        IsColorCycleEnabled = !IsColorCycleEnabled;
        _material!.SetShaderParameter(
            "color_cycle_amount",
            IsColorCycleEnabled ? 1.0f : 0.0f);
        UpdateProcessing();
        QueueRedraw();
        return IsColorCycleEnabled;
    }

    public override void _Draw()
    {
        if (_texture is not null && IsShowing)
        {
            DrawTextureRect(_texture, _destination, tile: false);
        }
    }

    private void UpdateLayout(Vector2 viewportSize)
    {
        if (
            !float.IsFinite(viewportSize.X)
            || !float.IsFinite(viewportSize.Y)
            || viewportSize.X <= 0.0f
            || viewportSize.Y <= 0.0f
        )
        {
            throw new ArgumentOutOfRangeException(nameof(viewportSize));
        }

        Vector2 textureSize = _texture!.GetSize();
        float scale = MathF.Min(
            viewportSize.X / textureSize.X,
            viewportSize.Y / textureSize.Y)
            * DisplayScale;
        Vector2 displaySize = textureSize * scale;
        _destination = new Rect2(
            new Vector2(
                (viewportSize.X - displaySize.X) / 2.0f,
                MathF.Min(
                    TopMargin,
                    MathF.Max(0.0f, viewportSize.Y - displaySize.Y))),
            displaySize);
        _lastViewportSize = viewportSize;
        QueueRedraw();
    }

    private void OnViewportSizeChanged()
    {
        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
        if (viewportSize != _lastViewportSize)
        {
            UpdateLayout(viewportSize);
        }
    }

    private void UpdateVisibility()
    {
        Visible = IsShowing;
        UpdateProcessing();
        QueueRedraw();
    }

    private void UpdateProcessing()
    {
        SetProcess(
            IsShowing
            && (IsAnimationEnabled || IsColorCycleEnabled));
    }
}
