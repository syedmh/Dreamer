using System;
using Godot;

namespace TCFAnimation;

public partial class TurnController : Node2D
{
    public const double TurnAnimationFps = 8.0;
    public const float BaseWalkSpeedPixelsPerSecond = 240.0f;
    public const double WalkSpeedMultiplierStep = 0.25;
    public const double MinimumWalkSpeedMultiplier = 0.25;
    public const double MaximumWalkSpeedMultiplier = 3.0;

    private const int ViewportWidth = 1920;
    private const int ViewportHeight = 1080;
    private const float CharacterScale = 1.25f;
    private const float RuntimeCanvasCenterX = 256.0f;
    private const float VisibleLeftX = 79.0f;
    private const float VisibleRightX = 432.0f;

    private readonly Texture2D[] _leftFrames = new Texture2D[3];
    private readonly Texture2D[] _rightFrames = new Texture2D[3];
    private readonly Texture2D[] _leftWalkFrames =
        new Texture2D[DirectionalTurnStateMachine.LeftWalkFrameCount];
    private readonly Texture2D[] _rightWalkFrames =
        new Texture2D[DirectionalTurnStateMachine.RightWalkFrameCount];
    private readonly Texture2D[] _clapFrames =
        new Texture2D[DirectionalTurnStateMachine.ClapFrameCount];
    private readonly Texture2D[] _crossArmFrames =
        new Texture2D[DirectionalTurnStateMachine.CrossArmFrameCount];
    private readonly Texture2D[] _crossArmReleaseFrames =
        new Texture2D[DirectionalTurnStateMachine.CrossArmReleaseFrameCount];
    private readonly DirectionalTurnStateMachine _turn =
        new(TurnAnimationFps);

    private Sprite2D _character = null!;
    private DialogueUi _dialogueUi = null!;
    private string? _capturePath;
    private int _captureCountdown;
    private double _walkSpeedMultiplier = 1.0;

    public override void _Ready()
    {
        _character = GetNode<Sprite2D>("Character");
        _dialogueUi = GetNode<DialogueUi>("DialogueUi");

        LoadFrames("LeftTurn", _leftFrames);
        LoadFrames("RightTurn", _rightFrames);
        LoadWalkFrames("LeftWalk", _leftWalkFrames);
        LoadWalkFrames("RightWalk", _rightWalkFrames);
        LoadGestureFrames("Clap", "clap", _clapFrames);
        LoadGestureFrames("CrossArm", "cross", _crossArmFrames);
        LoadGestureFrames(
            "CrossArmRelease",
            "release",
            _crossArmReleaseFrames);

        _character.Position = new Vector2(ViewportWidth / 2.0f, ViewportHeight / 2.0f);
        _character.Scale = Vector2.One * CharacterScale;
        _character.TextureFilter = TextureFilterEnum.Nearest;

        ConfigureCaptureMode();
        ApplyCurrentFrame();
    }

    public override void _Process(double delta)
    {
        if (_capturePath is not null)
        {
            if (--_captureCountdown <= 0)
            {
                CaptureFrameAndQuit();
            }

            return;
        }

        if (_dialogueUi.IsEditing)
        {
            return;
        }

        bool leftHeld = Input.IsPhysicalKeyPressed(Key.Left);
        bool rightHeld = Input.IsPhysicalKeyPressed(Key.Right);
        bool stateChanged =
            _turn.Advance(leftHeld, rightHeld, delta, _walkSpeedMultiplier);
        bool edgeChanged = false;
        float walkSpeedPixelsPerSecond =
            BaseWalkSpeedPixelsPerSecond * (float)_walkSpeedMultiplier;

        if (_turn.IsWalkingLeft)
        {
            float viewportLeft = GetViewport().GetVisibleRect().Position.X;
            float minimumCenterX =
                viewportLeft
                + (RuntimeCanvasCenterX - VisibleLeftX)
                * MathF.Abs(_character.Scale.X);
            float nextX =
                _character.Position.X
                - walkSpeedPixelsPerSecond * (float)delta;

            if (nextX <= minimumCenterX)
            {
                nextX = minimumCenterX;
                edgeChanged = _turn.NotifyLeftEdgeReached();
            }

            _character.Position = new Vector2(nextX, _character.Position.Y);
        }
        else if (_turn.IsWalkingRight)
        {
            Rect2 visibleRect = GetViewport().GetVisibleRect();
            float viewportRight = visibleRect.Position.X + visibleRect.Size.X;
            float maximumCenterX =
                viewportRight
                - (VisibleRightX - RuntimeCanvasCenterX)
                * MathF.Abs(_character.Scale.X);
            float nextX =
                _character.Position.X
                + walkSpeedPixelsPerSecond * (float)delta;

            if (nextX >= maximumCenterX)
            {
                nextX = maximumCenterX;
                edgeChanged = _turn.NotifyRightEdgeReached();
            }

            _character.Position = new Vector2(nextX, _character.Position.Y);
        }

        if (stateChanged || edgeChanged)
        {
            ApplyCurrentFrame();
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (
            @event is not InputEventKey keyEvent
            || !keyEvent.Pressed
            || keyEvent.Echo
            || _dialogueUi.IsEditing
        )
        {
            return;
        }

        bool toggleFullscreen =
            keyEvent.PhysicalKeycode == Key.F11
            || (
                keyEvent.PhysicalKeycode == Key.Enter
                && keyEvent.AltPressed
            );
        if (toggleFullscreen)
        {
            ToggleFullscreen();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (
            keyEvent.PhysicalKeycode == Key.Escape
            && IsFullscreen()
        )
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (keyEvent.PhysicalKeycode == Key.X)
        {
            bool stateChanged = _turn.TryToggleCrossArms(
                Input.IsPhysicalKeyPressed(Key.Left),
                Input.IsPhysicalKeyPressed(Key.Right));
            if (stateChanged)
            {
                ApplyCurrentFrame();
            }

            return;
        }

        if (keyEvent.PhysicalKeycode == Key.C)
        {
            bool clapStarted = _turn.TryStartClap(
                Input.IsPhysicalKeyPressed(Key.Left),
                Input.IsPhysicalKeyPressed(Key.Right));
            if (clapStarted)
            {
                ApplyCurrentFrame();
            }

            return;
        }

        double adjustment = keyEvent.Keycode switch
        {
            Key.Plus => WalkSpeedMultiplierStep,
            Key.Equal when keyEvent.ShiftPressed => WalkSpeedMultiplierStep,
            Key.KpAdd => WalkSpeedMultiplierStep,
            Key.Minus => -WalkSpeedMultiplierStep,
            Key.KpSubtract => -WalkSpeedMultiplierStep,
            _ => 0.0,
        };

        if (adjustment == 0.0)
        {
            return;
        }

        double nextMultiplier = Math.Clamp(
            _walkSpeedMultiplier + adjustment,
            MinimumWalkSpeedMultiplier,
            MaximumWalkSpeedMultiplier);
        if (nextMultiplier == _walkSpeedMultiplier)
        {
            return;
        }

        _walkSpeedMultiplier = nextMultiplier;
        double movementSpeed =
            BaseWalkSpeedPixelsPerSecond * _walkSpeedMultiplier;
        double walkFps =
            DirectionalTurnStateMachine.LeftWalkAnimationFps
            * _walkSpeedMultiplier;
        GD.Print(
            FormattableString.Invariant(
                $"WALK_SPEED multiplier={_walkSpeedMultiplier:0.00}x movement={movementSpeed:0.##}px/s walk_fps={walkFps:0.##}"));
    }

    private static bool IsFullscreen()
    {
        DisplayServer.WindowMode mode = DisplayServer.WindowGetMode();
        return mode is
            DisplayServer.WindowMode.Fullscreen
            or DisplayServer.WindowMode.ExclusiveFullscreen;
    }

    private static void ToggleFullscreen()
    {
        DisplayServer.WindowSetMode(
            IsFullscreen()
                ? DisplayServer.WindowMode.Windowed
                : DisplayServer.WindowMode.Fullscreen);
    }

    private static void LoadFrames(string directory, Texture2D[] destination)
    {
        for (int index = 0; index < destination.Length; index++)
        {
            string path = $"res://Frames/{directory}/turn_{index}.png";
            destination[index] = GD.Load<Texture2D>(path)
                ?? throw new InvalidOperationException(
                    $"Could not load required frame: {path}");
        }
    }

    private static void LoadWalkFrames(
        string directory,
        Texture2D[] destination)
    {
        for (int index = 0; index < destination.Length; index++)
        {
            string path = $"res://Frames/{directory}/walk_{index:00}.png";
            destination[index] = GD.Load<Texture2D>(path)
                ?? throw new InvalidOperationException(
                    $"Could not load required frame: {path}");
        }
    }

    private static void LoadGestureFrames(
        string directory,
        string prefix,
        Texture2D[] destination)
    {
        for (int index = 0; index < destination.Length; index++)
        {
            string path =
                $"res://Frames/{directory}/{prefix}_{index:00}.png";
            destination[index] = GD.Load<Texture2D>(path)
                ?? throw new InvalidOperationException(
                    $"Could not load required frame: {path}");
        }
    }

    private void ApplyCurrentFrame()
    {
        if (_turn.IsCrossingArms || _turn.IsCrossArmsHeld)
        {
            // The held state pins this index to cross_02, the third and final
            // crossing frame (human source pose 6 from CrossArm3.png).
            _character.Texture =
                _crossArmFrames[_turn.CurrentCrossArmFrame];
            return;
        }

        if (_turn.IsReleasingCrossArms)
        {
            _character.Texture =
                _crossArmReleaseFrames[_turn.CurrentCrossArmReleaseFrame];
            return;
        }

        if (_turn.IsClapping)
        {
            _character.Texture = _clapFrames[_turn.CurrentClapFrame];
            return;
        }

        if (_turn.IsWalkingLeft)
        {
            _character.Texture = _leftWalkFrames[_turn.CurrentWalkFrame];
            return;
        }

        if (_turn.IsWalkingRight)
        {
            _character.Texture = _rightWalkFrames[_turn.CurrentWalkFrame];
            return;
        }

        Texture2D[] frames = _turn.CurrentDirection == TurnDirection.Left
            ? _leftFrames
            : _rightFrames;
        _character.Texture = frames[_turn.CurrentFrame];
    }

    private void ConfigureCaptureMode()
    {
        int? captureFrame = null;
        TurnDirection captureDirection = TurnDirection.Left;
        string? dialoguePreview = null;

        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            const string framePrefix = "--capture-frame=";
            const string directionPrefix = "--capture-direction=";
            const string pathPrefix = "--capture-path=";
            const string dialoguePrefix = "--dialogue-preview=";

            if (argument.StartsWith(framePrefix, StringComparison.Ordinal))
            {
                string value = argument[framePrefix.Length..];
                if (!int.TryParse(value, out int parsedFrame))
                {
                    throw new ArgumentException($"Invalid capture frame: {value}");
                }

                captureFrame = parsedFrame;
            }
            else if (argument.StartsWith(directionPrefix, StringComparison.Ordinal))
            {
                string value = argument[directionPrefix.Length..];
                captureDirection = value.ToLowerInvariant() switch
                {
                    "left" => TurnDirection.Left,
                    "right" => TurnDirection.Right,
                    _ => throw new ArgumentException(
                        $"Invalid capture direction: {value}"),
                };
            }
            else if (argument.StartsWith(pathPrefix, StringComparison.Ordinal))
            {
                _capturePath = argument[pathPrefix.Length..];
            }
            else if (argument.StartsWith(dialoguePrefix, StringComparison.Ordinal))
            {
                dialoguePreview = argument[dialoguePrefix.Length..];
            }
        }

        if (captureFrame.HasValue != (_capturePath is not null))
        {
            throw new ArgumentException(
                "Capture mode requires both --capture-frame and --capture-path.");
        }

        if (captureFrame is null)
        {
            return;
        }

        _turn.Reset(captureDirection, captureFrame.Value);
        ConfigureDialoguePreview(dialoguePreview);
        _captureCountdown = 3;
    }

    private void ConfigureDialoguePreview(string? preview)
    {
        switch (preview)
        {
            case null:
                return;
            case "short":
                _dialogueUi.ShowPreviewText("Dream big!");
                return;
            case "long":
                _dialogueUi.ShowPreviewText(
                    "A brave idea becomes real one careful step at a time, "
                    + "especially when friends build it together.");
                return;
            case "left":
                _character.Position = new Vector2(221.25f, _character.Position.Y);
                _dialogueUi.ShowPreviewText("Still inside the edge!");
                return;
            case "right":
                _character.Position = new Vector2(1700.0f, _character.Position.Y);
                _dialogueUi.ShowPreviewText("The tail still points to me.");
                return;
            case "input":
                _dialogueUi.OpenPreviewInput();
                return;
            default:
                throw new ArgumentException(
                    $"Invalid dialogue preview: {preview}");
        }
    }

    private void CaptureFrameAndQuit()
    {
        Image image = GetViewport().GetTexture().GetImage();
        Error result = image.SavePng(_capturePath!);

        if (result == Error.Ok)
        {
            GD.Print(
                $"CAPTURE_SAVED direction={_turn.CurrentDirection} "
                + $"frame={_turn.CurrentFrame} path={_capturePath}");
            GetTree().Quit();
            return;
        }

        GD.PushError($"CAPTURE_FAILED error={result} path={_capturePath}");
        GetTree().Quit(1);
    }
}
