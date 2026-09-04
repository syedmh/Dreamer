using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace TCFAnimation;

public partial class TurnController : Node2D
{
    public const double TurnAnimationFps = 8.0;
    public const float BaseWalkSpeedPixelsPerSecond = 240.0f;
    public const double WalkSpeedMultiplierStep = 0.25;
    public const double MinimumWalkSpeedMultiplier = 0.25;
    public const double MaximumWalkSpeedMultiplier = 3.0;

    private const int ViewportHeight = 1080;

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
    private string? _captureRoot;
    private string? _capturePath;
    private int _captureCountdown;
    private double _walkSpeedMultiplier = 1.0;
    private bool _startupTerminating;

    public override void _Ready()
    {
        string[] arguments = OS.GetCmdlineUserArgs();
        bool verifyRuntime = IsRuntimeVerificationRequested(arguments);
        bool captureModeRequested = IsCaptureModeRequested(arguments);
        try
        {
            if (verifyRuntime)
            {
                ValidateRuntimeVerificationArguments(arguments);
            }

            CaptureRootResolution? captureRoot = null;
            if (captureModeRequested)
            {
                CaptureRootContext captureRootContext =
                    CaptureRootContext.FromGodotFeatures(
                    OS.HasFeature("standalone"),
                    OS.HasFeature("template"),
                    OS.GetExecutablePath(),
                    AppContext.BaseDirectory,
                    ProjectSettings.GlobalizePath("res://"));
                captureRoot = CaptureRootResolver.ResolveRoot(
                    captureRootContext);
                GD.Print($"CAPTURE_ROOT kind={captureRoot.Value.Kind}");
            }
            CaptureModeConfiguration? captureMode =
                ParseCaptureMode(arguments, captureRoot?.Root);

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

            _character.Position = new Vector2(
                AnimationGeometry.ViewportWidth / 2.0f,
                ViewportHeight / 2.0f);
            _character.Scale =
                Vector2.One * AnimationGeometry.CharacterScale;
            _character.TextureFilter = TextureFilterEnum.Nearest;

            if (verifyRuntime)
            {
                VerifyRuntimeAndQuit();
                return;
            }

            ConfigureCaptureMode(captureMode);
            ApplyCurrentFrame();
        }
        catch (Exception exception) when (verifyRuntime)
        {
            _startupTerminating = true;
            GD.PushError(
                $"RUNTIME_SMOKE_FAIL {exception.GetType().Name}: "
                + exception.Message);
            GetTree().Quit(1);
        }
        catch (Exception exception) when (captureModeRequested)
        {
            _startupTerminating = true;
            GD.PushError(
                $"CAPTURE_MODE_FAIL error={exception.GetType().Name} "
                + $"message={ToSingleLine(exception.Message)}");
            GetTree().Quit(1);
        }
    }

    public override void _Process(double delta)
    {
        if (_startupTerminating)
        {
            return;
        }

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
            Rect2 visibleRect = GetViewport().GetVisibleRect();
            AnimationSafeCenters safeCenters =
                AnimationGeometry.CalculateSafeCenters(
                    visibleRect.Position.X,
                    visibleRect.Size.X,
                    AnimationGeometry.CanvasCenterX,
                    MathF.Abs(_character.Scale.X),
                    AnimationGeometry.LeftWalkVisibleX,
                    AnimationGeometry.RightWalkVisibleX);
            float nextX =
                _character.Position.X
                - walkSpeedPixelsPerSecond * (float)delta;

            if (nextX <= safeCenters.Left)
            {
                nextX = safeCenters.Left;
                edgeChanged = _turn.NotifyLeftEdgeReached();
            }

            _character.Position = new Vector2(nextX, _character.Position.Y);
        }
        else if (_turn.IsWalkingRight)
        {
            Rect2 visibleRect = GetViewport().GetVisibleRect();
            AnimationSafeCenters safeCenters =
                AnimationGeometry.CalculateSafeCenters(
                    visibleRect.Position.X,
                    visibleRect.Size.X,
                    AnimationGeometry.CanvasCenterX,
                    MathF.Abs(_character.Scale.X),
                    AnimationGeometry.LeftWalkVisibleX,
                    AnimationGeometry.RightWalkVisibleX);
            float nextX =
                _character.Position.X
                + walkSpeedPixelsPerSecond * (float)delta;

            if (nextX >= safeCenters.Right)
            {
                nextX = safeCenters.Right;
                edgeChanged = _turn.NotifyRightEdgeReached();
            }

            _character.Position = new Vector2(nextX, _character.Position.Y);
        }

        if (stateChanged || edgeChanged)
        {
            ApplyCurrentFrame();
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (_startupTerminating)
        {
            return;
        }

        if (@event is not InputEventKey keyEvent)
        {
            return;
        }

        GlobalInputKey globalKey = keyEvent.PhysicalKeycode switch
        {
            Key.F11 => GlobalInputKey.F11,
            Key.Enter => GlobalInputKey.Enter,
            Key.Escape => GlobalInputKey.Escape,
            _ => GlobalInputKey.Other,
        };
        GlobalInputAction globalAction = GlobalInputPolicy.Resolve(
            GlobalInputPhase.EarlyInput,
            globalKey,
            keyEvent.Pressed,
            keyEvent.Echo,
            keyEvent.AltPressed,
            _dialogueUi.IsEditing,
            IsFullscreen());
        if (globalAction == GlobalInputAction.None)
        {
            return;
        }

        if (globalAction == GlobalInputAction.ToggleFullscreen)
        {
            ToggleFullscreen();
        }
        else
        {
            DisplayServer.WindowSetMode(
                DisplayServer.WindowMode.Windowed);
        }

        GetViewport().SetInputAsHandled();
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (_startupTerminating)
        {
            return;
        }

        if (
            @event is not InputEventKey keyEvent
            || !keyEvent.Pressed
            || keyEvent.Echo
        )
        {
            return;
        }

        if (_dialogueUi.IsEditing)
        {
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

    private static bool IsRuntimeVerificationRequested(
        IReadOnlyList<string> arguments)
    {
        return arguments.Contains(
            "--verify-runtime",
            StringComparer.Ordinal);
    }

    private static bool IsCaptureModeRequested(
        IReadOnlyList<string> arguments)
    {
        return arguments.Any(
            argument =>
                argument.StartsWith(
                    "--capture-",
                    StringComparison.Ordinal)
                || argument.StartsWith(
                    "--dialogue-preview",
                    StringComparison.Ordinal));
    }

    private static void ValidateRuntimeVerificationArguments(
        IReadOnlyList<string> arguments)
    {
        string? incompatibleArgument = arguments.FirstOrDefault(
            argument =>
                argument.StartsWith(
                    "--capture-",
                    StringComparison.Ordinal)
                || argument.StartsWith(
                    "--dialogue-preview",
                    StringComparison.Ordinal));
        if (incompatibleArgument is not null)
        {
            throw new ArgumentException(
                "--verify-runtime is incompatible with capture mode; "
                + $"found {incompatibleArgument}.");
        }
    }

    private void VerifyRuntimeAndQuit()
    {
        Window rootWindow = GetTree().Root;
        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
        Vector2 expectedViewport = new(
            AnimationGeometry.ViewportWidth,
            ViewportHeight);
        if (viewportSize != expectedViewport)
        {
            throw new InvalidOperationException(
                $"Viewport is {viewportSize.X}x{viewportSize.Y}; "
                + $"expected {expectedViewport.X}x{expectedViewport.Y}.");
        }
        if (
            rootWindow.ContentScaleSize != new Vector2I(1920, 1080)
            || rootWindow.ContentScaleMode
                != Window.ContentScaleModeEnum.CanvasItems
            || rootWindow.ContentScaleAspect
                != Window.ContentScaleAspectEnum.Keep
        )
        {
            throw new InvalidOperationException(
                "Viewport content scaling is "
                + $"{rootWindow.ContentScaleSize.X}x"
                + $"{rootWindow.ContentScaleSize.Y} "
                + $"{rootWindow.ContentScaleMode}/"
                + $"{rootWindow.ContentScaleAspect}; expected "
                + "1920x1080 CanvasItems/Keep.");
        }

        IReadOnlyList<Texture2D[]> frameGroups =
        [
            _leftFrames,
            _rightFrames,
            _leftWalkFrames,
            _rightWalkFrames,
            _clapFrames,
            _crossArmFrames,
            _crossArmReleaseFrames,
        ];
        Vector2 expectedTextureSize = new(512.0f, 864.0f);
        int frameCount = 0;
        foreach (Texture2D[] group in frameGroups)
        {
            foreach (Texture2D texture in group)
            {
                if (texture is null)
                {
                    throw new InvalidOperationException(
                        "A required runtime texture was not loaded.");
                }

                if (texture.GetSize() != expectedTextureSize)
                {
                    throw new InvalidOperationException(
                        $"Texture {texture.ResourcePath} is "
                        + $"{texture.GetWidth()}x{texture.GetHeight()}; "
                        + "expected 512x864.");
                }

                frameCount++;
            }
        }

        if (frameCount != 33)
        {
            throw new InvalidOperationException(
                $"Loaded {frameCount} runtime frames; expected 33.");
        }

        GD.Print(
            "RUNTIME_SMOKE_PASS frames=33 dialogue_ui=true "
            + "viewport_fit=1920x1080:CanvasItems:Keep");
        GetTree().Quit();
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

    private static CaptureModeConfiguration? ParseCaptureMode(
        IReadOnlyList<string> arguments,
        string? captureRoot)
    {
        int? captureFrame = null;
        TurnDirection captureDirection = TurnDirection.Left;
        string? capturePath = null;
        string? dialoguePreview = null;
        int frameOptionCount = 0;
        int directionOptionCount = 0;
        int pathOptionCount = 0;
        int dialogueOptionCount = 0;
        bool captureModeRequested = IsCaptureModeRequested(arguments);

        foreach (string argument in arguments)
        {
            const string framePrefix = "--capture-frame=";
            const string directionPrefix = "--capture-direction=";
            const string pathPrefix = "--capture-path=";
            const string dialoguePrefix = "--dialogue-preview=";

            if (argument.StartsWith(framePrefix, StringComparison.Ordinal))
            {
                EnsureSingleOption(
                    ref frameOptionCount,
                    "--capture-frame");
                string value = argument[framePrefix.Length..];
                if (!int.TryParse(value, out int parsedFrame))
                {
                    throw new ArgumentException($"Invalid capture frame: {value}");
                }

                captureFrame = parsedFrame;
            }
            else if (argument.StartsWith(directionPrefix, StringComparison.Ordinal))
            {
                EnsureSingleOption(
                    ref directionOptionCount,
                    "--capture-direction");
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
                EnsureSingleOption(
                    ref pathOptionCount,
                    "--capture-path");
                if (captureRoot is null)
                {
                    throw new InvalidOperationException(
                        "Capture root was not resolved for capture mode.");
                }
                capturePath = CapturePathPolicy.Resolve(
                    captureRoot,
                    argument[pathPrefix.Length..]);
            }
            else if (argument.StartsWith(dialoguePrefix, StringComparison.Ordinal))
            {
                EnsureSingleOption(
                    ref dialogueOptionCount,
                    "--dialogue-preview");
                dialoguePreview = argument[dialoguePrefix.Length..];
                ValidateDialoguePreview(dialoguePreview);
            }
            else if (
                argument.StartsWith(
                    "--capture-",
                    StringComparison.Ordinal)
            )
            {
                throw new ArgumentException(
                    $"Unknown capture option: {argument}");
            }
            else if (
                argument.StartsWith(
                    "--dialogue-preview",
                    StringComparison.Ordinal)
            )
            {
                throw new ArgumentException(
                    $"Malformed dialogue preview option: {argument}");
            }
        }

        if (!captureModeRequested)
        {
            return null;
        }

        if (captureFrame is null || capturePath is null)
        {
            throw new ArgumentException(
                "Capture mode requires both --capture-frame and --capture-path.");
        }

        if (
            captureFrame.Value is
                < DirectionalTurnStateMachine.FrontFrame
                or > DirectionalTurnStateMachine.FullTurnFrame
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(captureFrame),
                captureFrame.Value,
                "Capture frame must be between 0 and 2.");
        }

        return new CaptureModeConfiguration(
            captureFrame.Value,
            captureDirection,
            captureRoot
                ?? throw new InvalidOperationException(
                    "Capture root was not resolved for capture mode."),
            capturePath,
            dialoguePreview);
    }

    private void ConfigureCaptureMode(
        CaptureModeConfiguration? captureMode)
    {
        if (captureMode is null)
        {
            return;
        }

        _captureRoot = captureMode.Root;
        _capturePath = captureMode.Path;
        _turn.Reset(captureMode.Direction, captureMode.Frame);
        ConfigureDialoguePreview(captureMode.DialoguePreview);
        _captureCountdown = 3;
    }

    private static void EnsureSingleOption(
        ref int optionCount,
        string optionName)
    {
        optionCount++;
        if (optionCount > 1)
        {
            throw new ArgumentException(
                $"Duplicate capture mode option: {optionName}");
        }
    }

    private static void ValidateDialoguePreview(string preview)
    {
        if (
            preview is not (
                "short"
                or "long"
                or "left"
                or "right"
                or "input")
        )
        {
            throw new ArgumentException(
                $"Invalid dialogue preview: {preview}");
        }
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
                _character.Position = new Vector2(
                    AnimationGeometry.DefaultSafeCenters.Left,
                    _character.Position.Y);
                _dialogueUi.ShowPreviewText("Still inside the edge!");
                return;
            case "right":
                _character.Position = new Vector2(
                    AnimationGeometry.DefaultSafeCenters.Right,
                    _character.Position.Y);
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
        string? stagingPath = null;
        bool published = false;
        try
        {
            Image? image = GetViewport().GetTexture().GetImage();
            if (image is null || image.IsEmpty())
            {
                throw new InvalidOperationException(
                    "Viewport capture requires an active rendering backend.");
            }
            string projectRoot = _captureRoot
                ?? throw new InvalidOperationException(
                    "Capture root is unavailable during publication.");
            string captureDirectory =
                CapturePathPolicy.GetCaptureDirectory(projectRoot);
            Directory.CreateDirectory(captureDirectory);
            CapturePathPolicy.EnsureNoReparsePoints(projectRoot);

            byte[] png = image.SavePngToBuffer();

            CapturePathPolicy.EnsureNoReparsePoints(projectRoot);
            stagingPath = CapturePathPolicy.CreateStagingPath(
                projectRoot,
                _capturePath!);
            CapturePathPolicy.EnsureNoReparsePoints(projectRoot);
            using FileStream output = new(
                stagingPath,
                FileMode.CreateNew,
                System.IO.FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            output.Write(png);
            output.Flush(flushToDisk: true);
            output.Close();

            CapturePathPolicy.EnsureSafeToPublish(
                projectRoot,
                _capturePath!,
                stagingPath);
            File.Move(stagingPath, _capturePath!, overwrite: false);
            published = true;

            GD.Print(
                $"CAPTURE_SAVED direction={_turn.CurrentDirection} "
                + $"frame={_turn.CurrentFrame} path={_capturePath}");
            GetTree().Quit();
        }
        catch (Exception exception)
        {
            _startupTerminating = true;
            GD.PushError(
                $"CAPTURE_FAILED error={exception.GetType().Name} "
                + $"message={ToSingleLine(exception.Message)} "
                + $"path={_capturePath}");
            GetTree().Quit(1);
        }
        finally
        {
            if (!published && stagingPath is not null)
            {
                TryDeleteStagingFile(stagingPath);
            }
        }
    }

    private static void TryDeleteStagingFile(string stagingPath)
    {
        try
        {
            File.Delete(stagingPath);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning(
                $"CAPTURE_STAGING_CLEANUP_FAILED "
                + $"error={exception.GetType().Name} path={stagingPath}");
        }
    }

    private static string ToSingleLine(string message)
    {
        return message
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    private sealed record CaptureModeConfiguration(
        int Frame,
        TurnDirection Direction,
        string Root,
        string Path,
        string? DialoguePreview);
}
