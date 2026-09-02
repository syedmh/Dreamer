using System;
using Godot;

namespace TCFAnimation;

public partial class TurnController : Node2D
{
    public const double TurnAnimationFps = 8.0;

    private const int ViewportWidth = 3840;
    private const int ViewportHeight = 2160;
    private const float CharacterScale = 2.5f;

    private readonly Texture2D[] _leftFrames = new Texture2D[3];
    private readonly Texture2D[] _rightFrames = new Texture2D[3];
    private readonly DirectionalTurnStateMachine _turn =
        new(TurnAnimationFps);

    private Sprite2D _character = null!;
    private string? _capturePath;
    private int _captureCountdown;

    public override void _Ready()
    {
        _character = GetNode<Sprite2D>("Character");

        LoadFrames("LeftTurn", _leftFrames);
        LoadFrames("RightTurn", _rightFrames);

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

        bool leftHeld = Input.IsPhysicalKeyPressed(Key.Left);
        bool rightHeld = Input.IsPhysicalKeyPressed(Key.Right);
        if (_turn.Advance(leftHeld, rightHeld, delta))
        {
            ApplyCurrentFrame();
        }
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

    private void ApplyCurrentFrame()
    {
        Texture2D[] frames = _turn.CurrentDirection == TurnDirection.Left
            ? _leftFrames
            : _rightFrames;
        _character.Texture = frames[_turn.CurrentFrame];
    }

    private void ConfigureCaptureMode()
    {
        int? captureFrame = null;
        TurnDirection captureDirection = TurnDirection.Left;

        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            const string framePrefix = "--capture-frame=";
            const string directionPrefix = "--capture-direction=";
            const string pathPrefix = "--capture-path=";

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
        _captureCountdown = 3;
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
