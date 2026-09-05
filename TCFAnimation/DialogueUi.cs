using System;
using Godot;

namespace TCFAnimation;

public partial class DialogueUi : CanvasLayer
{
    private const float HeadAnchorOffsetY = -330.0f;

    private static readonly Color BubbleFill = new("fffdf4");
    private static readonly Color Ink = new("111111");
    private static readonly Color InputPanelFill = new("18202b");
    private static readonly Color InputAccent = new("f4c95d");

    private readonly DialogueModel _model = new();

    private Sprite2D _character = null!;
    private ComicBubbleControl _bubble = null!;
    private MarginContainer _inputRoot = null!;
    private LineEdit _lineEdit = null!;

    public bool IsEditing => _model.IsEditing;

    public bool OpeningSuppressed { get; set; }

    public override void _Ready()
    {
        Layer = 20;
        _character = GetNode<Sprite2D>("../Character");
        BuildBubble();
        BuildInput();
        SetProcessInput(true);
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        _ = delta;
        Vector2 headAnchor =
            GetViewport().GetCanvasTransform()
            * (_character.GlobalPosition + new Vector2(0.0f, HeadAnchorOffsetY));
        _bubble.SetContent(
            _model.BubbleText,
            _model.IsBubbleVisible,
            headAnchor);
    }

    public override void _Input(InputEvent @event)
    {
        if (
            @event is not InputEventKey keyEvent
            || !keyEvent.Pressed
            || keyEvent.Echo
        )
        {
            return;
        }

        if (
            keyEvent.PhysicalKeycode == Key.Enter
            && keyEvent.AltPressed
        )
        {
            return;
        }

        if (
            OpeningSuppressed
            && !_model.IsEditing
            && keyEvent.PhysicalKeycode == Key.Enter
        )
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        DialogueKey key = keyEvent.PhysicalKeycode switch
        {
            Key.Enter => DialogueKey.Enter,
            Key.Escape => DialogueKey.Escape,
            Key.P => DialogueKey.P,
            _ => DialogueKey.Other,
        };
        DialogueAction action = _model.HandleKey(
            key,
            echo: false,
            _lineEdit?.Text ?? string.Empty);

        if (action.Opened)
        {
            OpenInput();
        }
        else if (action.Closed)
        {
            CloseInput();
        }

        if (action.Handled)
        {
            GetViewport().SetInputAsHandled();
        }
    }

    public void ShowPreviewText(string text)
    {
        _model.SetPreviewText(text);
    }

    public bool ShowActionText(string? text)
    {
        return _model.ShowActionText(text);
    }

    public bool HideBubble()
    {
        return _model.HideBubble();
    }

    public bool IsBubbleVisible => _model.IsBubbleVisible;

    public void OpenPreviewInput()
    {
        _model.OpenPreviewInput();
        OpenInput();
    }

    private void BuildBubble()
    {
        _bubble = new ComicBubbleControl
        {
            Name = "ComicBubble",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _bubble.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_bubble);
    }

    private void BuildInput()
    {
        _inputRoot = new MarginContainer
        {
            Name = "TextEntry",
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _inputRoot.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        _inputRoot.Position = new Vector2(-700.0f, -212.0f);
        _inputRoot.Size = new Vector2(676.0f, 188.0f);
        AddChild(_inputRoot);

        PanelContainer panel = new()
        {
            Name = "Panel",
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        StyleBoxFlat panelStyle = new()
        {
            BgColor = InputPanelFill,
            BorderColor = InputAccent,
            BorderWidthLeft = 4,
            BorderWidthTop = 4,
            BorderWidthRight = 4,
            BorderWidthBottom = 4,
            CornerRadiusTopLeft = 18,
            CornerRadiusTopRight = 18,
            CornerRadiusBottomLeft = 18,
            CornerRadiusBottomRight = 18,
            ContentMarginLeft = 24.0f,
            ContentMarginTop = 18.0f,
            ContentMarginRight = 24.0f,
            ContentMarginBottom = 18.0f,
        };
        panel.AddThemeStyleboxOverride("panel", panelStyle);
        _inputRoot.AddChild(panel);

        VBoxContainer column = new()
        {
            Name = "Content",
        };
        column.AddThemeConstantOverride("separation", 10);
        panel.AddChild(column);

        Label prompt = new()
        {
            Text = "What should the character say?",
        };
        prompt.AddThemeFontSizeOverride("font_size", 28);
        prompt.AddThemeColorOverride("font_color", Colors.White);
        column.AddChild(prompt);

        _lineEdit = new LineEdit
        {
            Name = "Message",
            PlaceholderText = "Type a message…",
            MaxLength = DialogueLayout.MaximumInputCharacters,
            CustomMinimumSize = new Vector2(620.0f, 58.0f),
        };
        _lineEdit.AddThemeFontSizeOverride("font_size", 28);
        _lineEdit.AddThemeColorOverride("font_color", Ink);
        _lineEdit.AddThemeColorOverride(
            "font_placeholder_color",
            new Color(0.30f, 0.30f, 0.30f));
        StyleBoxFlat editStyle = new()
        {
            BgColor = BubbleFill,
            BorderColor = Colors.Black,
            BorderWidthLeft = 3,
            BorderWidthTop = 3,
            BorderWidthRight = 3,
            BorderWidthBottom = 3,
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10,
            ContentMarginLeft = 14.0f,
            ContentMarginRight = 14.0f,
        };
        _lineEdit.AddThemeStyleboxOverride("normal", editStyle);
        _lineEdit.AddThemeStyleboxOverride("focus", editStyle);
        column.AddChild(_lineEdit);

        Label hint = new()
        {
            Text = "Enter: say it    Esc: cancel",
        };
        hint.AddThemeFontSizeOverride("font_size", 22);
        hint.AddThemeColorOverride("font_color", InputAccent);
        column.AddChild(hint);

        _inputRoot.Hide();
    }

    private void OpenInput()
    {
        _lineEdit.Text = string.Empty;
        _inputRoot.Show();
        _lineEdit.GrabFocus();
        _lineEdit.CaretColumn = 0;
    }

    private void CloseInput()
    {
        _inputRoot.Hide();
        _lineEdit.ReleaseFocus();
        _lineEdit.Text = string.Empty;
    }
}

public partial class ComicBubbleControl : Control
{
    private const int FontSize = 34;
    private const float TailHalfWidth = 22.0f;
    private const float OutlineWidth = 5.0f;

    private readonly Label _label = new()
    {
        MouseFilter = MouseFilterEnum.Ignore,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Rect2 _body;
    private Vector2 _tailTarget;
    private string _sourceText = string.Empty;

    public override void _Ready()
    {
        AddChild(_label);
        _label.AddThemeFontSizeOverride("font_size", FontSize);
        _label.AddThemeColorOverride("font_color", new Color("111111"));
    }

    public void SetContent(string text, bool visible, Vector2 headAnchor)
    {
        Visible = visible;
        if (!visible)
        {
            return;
        }

        Font font = ThemeDB.FallbackFont;
        float maximumTextWidth =
            DialogueLayout.MaximumBodyWidth
            - DialogueLayout.HorizontalPadding * 2.0f;
        string wrapped = DialogueLayout.WrapText(
            text,
            value => font.GetStringSize(
                value,
                HorizontalAlignment.Left,
                -1.0f,
                FontSize).X,
            maximumTextWidth,
            DialogueLayout.MaximumLines);
        Vector2 measured = font.GetMultilineStringSize(
            wrapped,
            HorizontalAlignment.Left,
            -1.0f,
            FontSize);
        DialogueSize bodySize = DialogueLayout.CalculateBodySize(
            new DialogueSize(measured.X, measured.Y));
        BubbleLayoutResult layout = DialogueLayout.PlaceBubble(
            new DialoguePoint(headAnchor.X, headAnchor.Y),
            bodySize);
        Rect2 nextBody = new(
            layout.Body.X,
            layout.Body.Y,
            layout.Body.Width,
            layout.Body.Height);
        Vector2 nextTailTarget = new(
            layout.TailTarget.X,
            layout.TailTarget.Y);

        if (
            wrapped == _sourceText
            && nextBody == _body
            && nextTailTarget == _tailTarget
        )
        {
            return;
        }

        _sourceText = wrapped;
        _body = nextBody;
        _tailTarget = nextTailTarget;
        _label.Text = wrapped;
        _label.Position = _body.Position + new Vector2(
            DialogueLayout.HorizontalPadding,
            DialogueLayout.VerticalPadding);
        _label.Size = _body.Size - new Vector2(
            DialogueLayout.HorizontalPadding * 2.0f,
            DialogueLayout.VerticalPadding * 2.0f);
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!Visible || _body.Size.X <= 0.0f || _body.Size.Y <= 0.0f)
        {
            return;
        }

        float tailBaseX = Math.Clamp(
            _tailTarget.X,
            _body.Position.X + 42.0f,
            _body.End.X - 42.0f);
        Vector2[] outlineTail =
        [
            new(tailBaseX - TailHalfWidth - OutlineWidth, _body.End.Y - 4.0f),
            new(tailBaseX + TailHalfWidth + OutlineWidth, _body.End.Y - 4.0f),
            _tailTarget,
        ];
        Vector2[] fillTail =
        [
            new(tailBaseX - TailHalfWidth, _body.End.Y - 6.0f),
            new(tailBaseX + TailHalfWidth, _body.End.Y - 6.0f),
            _tailTarget - new Vector2(0.0f, 7.0f),
        ];
        DrawColoredPolygon(outlineTail, Colors.Black);
        DrawColoredPolygon(fillTail, new Color("fffdf4"));

        StyleBoxFlat bodyStyle = new()
        {
            BgColor = new Color("fffdf4"),
            BorderColor = Colors.Black,
            BorderWidthLeft = (int)OutlineWidth,
            BorderWidthTop = (int)OutlineWidth,
            BorderWidthRight = (int)OutlineWidth,
            BorderWidthBottom = (int)OutlineWidth,
            CornerRadiusTopLeft = 28,
            CornerRadiusTopRight = 34,
            CornerRadiusBottomLeft = 30,
            CornerRadiusBottomRight = 24,
        };
        DrawStyleBox(bodyStyle, _body);
    }
}
