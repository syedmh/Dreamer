using Godot;

namespace TCFAnimation;

public partial class ActionLegendUi : CanvasLayer
{
    private static readonly Color PanelFill = new("111827e8");
    private static readonly Color Border = new("ffd84d");
    private static readonly Color Ink = new("ffffff");
    private static readonly Color Heading = new("47e6ff");

    private Control _root = null!;
    private PanelContainer _panel = null!;
    private Vector2 _lastViewportSize;

    public bool IsLegendVisible => _panel.Visible;

    public override void _Ready()
    {
        Layer = 30;
        BuildLegend();
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        _ = delta;
        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
        if (viewportSize != _lastViewportSize)
        {
            _lastViewportSize = viewportSize;
            ApplyLayout(viewportSize);
        }
    }

    public void Toggle()
    {
        SetLegendVisible(!_panel.Visible);
    }

    public void SetLegendVisible(bool visible)
    {
        _panel.Visible = visible;
    }

    private void BuildLegend()
    {
        _root = new Control
        {
            Name = "ActionLegendRoot",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        _panel = new PanelContainer
        {
            Name = "ActionLegendPanel",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        StyleBoxFlat style = new()
        {
            BgColor = PanelFill,
            BorderColor = Border,
            BorderWidthLeft = 4,
            BorderWidthTop = 4,
            BorderWidthRight = 4,
            BorderWidthBottom = 4,
            CornerRadiusTopLeft = 18,
            CornerRadiusTopRight = 18,
            CornerRadiusBottomLeft = 18,
            CornerRadiusBottomRight = 18,
            ContentMarginLeft = 22.0f,
            ContentMarginTop = 18.0f,
            ContentMarginRight = 22.0f,
            ContentMarginBottom = 18.0f,
        };
        _panel.AddThemeStyleboxOverride("panel", style);
        _root.AddChild(_panel);

        ScrollContainer scroll = new()
        {
            Name = "ViewportSafeScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        scroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _panel.AddChild(scroll);

        VBoxContainer content = new()
        {
            Name = "Content",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        content.AddThemeConstantOverride("separation", 5);
        scroll.AddChild(content);

        Label title = new()
        {
            Text = "ACTION LEGEND",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        title.AddThemeFontSizeOverride("font_size", 31);
        title.AddThemeColorOverride("font_color", Heading);
        content.AddChild(title);

        Label divider = new()
        {
            Text = "━━━━━━━━━━━━━━━━━━━━",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        divider.AddThemeFontSizeOverride("font_size", 20);
        divider.AddThemeColorOverride("font_color", Border);
        content.AddChild(divider);

        foreach (string entry in ActionLegendLayout.Entries)
        {
            Label line = new()
            {
                Text = entry,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            line.AddThemeFontSizeOverride("font_size", 21);
            line.AddThemeColorOverride("font_color", Ink);
            content.AddChild(line);
        }

        _panel.Hide();
        ApplyLayout(GetViewport().GetVisibleRect().Size);
    }

    private void ApplyLayout(Vector2 viewportSize)
    {
        LegendSize size = ActionLegendLayout.Calculate(
            viewportSize.X,
            viewportSize.Y);
        _panel.Position = new Vector2(
            ActionLegendLayout.SafeMargin,
            ActionLegendLayout.SafeMargin);
        _panel.Size = new Vector2(size.Width, size.Height);
    }
}
