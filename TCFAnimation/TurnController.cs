using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace TCFAnimation;

public partial class TurnController : Node2D
{
    public const float BaseWalkSpeedPixelsPerSecond = 240.0f;
    public const double WalkSpeedMultiplierStep = 0.25;
    public const double MinimumWalkSpeedMultiplier = 0.25;
    public const double MaximumWalkSpeedMultiplier = 3.0;

    private const int ViewportHeight = 1080;
    private const float CelebrationCenterX = 960.0f;

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
    private readonly Texture2D[] _schoolLeftFrames = new Texture2D[3];
    private readonly Texture2D[] _schoolRightFrames = new Texture2D[3];
    private readonly Texture2D[] _schoolLeftWalkFrames =
        new Texture2D[DirectionalTurnStateMachine.LeftWalkFrameCount];
    private readonly Texture2D[] _schoolRightWalkFrames =
        new Texture2D[DirectionalTurnStateMachine.RightWalkFrameCount];
    private readonly Texture2D[] _schoolClapFrames =
        new Texture2D[DirectionalTurnStateMachine.ClapFrameCount];
    private readonly Texture2D[] _schoolCrossArmFrames =
        new Texture2D[DirectionalTurnStateMachine.CrossArmFrameCount];
    private readonly Texture2D[] _schoolCrossArmReleaseFrames =
        new Texture2D[DirectionalTurnStateMachine.CrossArmReleaseFrameCount];
    private readonly Texture2D[] _schoolBackgroundTextures =
        new Texture2D[SchoolSceneStateMachine.MaximumSchoolNumber];
    private readonly SchoolBackgroundLayout[] _schoolBackgroundLayouts =
        new SchoolBackgroundLayout[
            SchoolSceneStateMachine.MaximumSchoolNumber];
    private Texture2D _smallLogoTexture = null!;
    private DirectionalTurnStateMachine _turn = null!;
    private readonly SchoolSceneStateMachine _schoolScene = new();
    private readonly CelebrationStateMachine _celebration = new();

    private Sprite2D _schoolBackground = null!;
    private Sprite2D _character = null!;
    private DialogueUi _dialogueUi = null!;
    private FireworksLayer _fireworks = null!;
    private LogoRainLayer _logoRain = null!;
    private ActionLegendUi _actionLegend = null!;
    private ActionMessageCatalog _actionMessages =
        ActionMessageCatalog.Empty;
    private ActionMessageLoadStatus _actionMessageLoadStatus =
        ActionMessageLoadStatus.MissingFile;
    private string? _pendingSchoolActionMessage;
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
            LoadAnimationConfig();
            _turn = new DirectionalTurnStateMachine(
                AnimationConfig.Current.TurnFps);

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

            _schoolBackground = GetNode<Sprite2D>("SchoolBackground");
            _character = GetNode<Sprite2D>("Character");
            _dialogueUi = GetNode<DialogueUi>("DialogueUi");
            _schoolBackground.ZIndex = -2;

            _fireworks = new FireworksLayer
            {
                Name = "FireworksLayer",
            };
            AddChild(_fireworks);
            MoveChild(_fireworks, _character.GetIndex());

            _smallLogoTexture = GD.Load<Texture2D>(
                "res://Frames/Effects/logo-small.png")
                ?? throw new InvalidOperationException(
                    "Could not load the required small logo texture.");
            _logoRain = new LogoRainLayer
            {
                Name = "LogoRainLayer",
            };
            _logoRain.Initialize(_smallLogoTexture);
            _logoRain.Finished += ApplyCurrentFrame;
            AddChild(_logoRain);
            MoveChild(_logoRain, _character.GetIndex());

            _actionLegend = new ActionLegendUi
            {
                Name = "ActionLegendUi",
            };
            AddChild(_actionLegend);

            LoadActionMessages();

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
            LoadFrames(
                "SchoolCharacter/LeftTurn",
                _schoolLeftFrames);
            LoadFrames(
                "SchoolCharacter/RightTurn",
                _schoolRightFrames);
            LoadWalkFrames(
                "SchoolCharacter/LeftWalk",
                _schoolLeftWalkFrames);
            LoadWalkFrames(
                "SchoolCharacter/RightWalk",
                _schoolRightWalkFrames);
            LoadGestureFrames(
                "SchoolCharacter/Clap",
                "clap",
                _schoolClapFrames);
            LoadGestureFrames(
                "SchoolCharacter/CrossArm",
                "cross",
                _schoolCrossArmFrames);
            LoadGestureFrames(
                "SchoolCharacter/CrossArmRelease",
                "release",
                _schoolCrossArmReleaseFrames);

            for (
                int schoolNumber =
                    SchoolSceneStateMachine.MinimumSchoolNumber;
                schoolNumber <=
                    SchoolSceneStateMachine.MaximumSchoolNumber;
                schoolNumber++
            )
            {
                string path =
                    $"res://Frames/Backgrounds/school{schoolNumber}.png";
                Texture2D texture = GD.Load<Texture2D>(path)
                    ?? throw new InvalidOperationException(
                        $"Could not load required school background: {path}");
                int index = schoolNumber - 1;
                _schoolBackgroundTextures[index] = texture;
                _schoolBackgroundLayouts[index] =
                    SchoolSceneGeometry.CalculateAspectCover(
                        AnimationGeometry.ViewportWidth,
                        ViewportHeight,
                        texture.GetWidth(),
                        texture.GetHeight());
            }
            _schoolBackground.TextureFilter = TextureFilterEnum.Linear;
            _schoolBackground.Visible = false;

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
            UpdateSchoolVisuals();
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

        if (_celebration.IsActive)
        {
            int centerArrivalSerial = _celebration.CenterArrivalSerial;
            int fireworksStartSerial = _celebration.FireworksStartSerial;
            _celebration.Advance(delta);
            if (_celebration.CenterArrivalSerial != centerArrivalSerial)
            {
                ShowConfiguredActionMessage("F");
            }
            if (_celebration.FireworksStartSerial != fireworksStartSerial)
            {
                _fireworks.Start();
            }
            if (_celebration.Phase == CelebrationPhase.Inactive)
            {
                _turn.Reset(
                    TurnDirection.Left,
                    DirectionalTurnStateMachine.FrontFrame);
            }
            UpdateSchoolVisuals();
            ApplyCurrentFrame();
            return;
        }

        if (_schoolScene.SuppressesOrdinaryInput)
        {
            _schoolScene.Advance(delta);
            if (_schoolScene.EntryEndpointReachedOnLastAdvance)
            {
                _dialogueUi.ShowActionText(
                    _pendingSchoolActionMessage);
            }
            if (
                _schoolScene.Phase == SchoolScenePhase.SchoolIdle
                || _schoolScene.Phase == SchoolScenePhase.NormalBlack
            )
            {
                _turn.Reset(
                    _schoolScene.Phase == SchoolScenePhase.SchoolIdle
                        ? TurnDirection.Right
                        : TurnDirection.Left,
                    DirectionalTurnStateMachine.FrontFrame);
            }
            UpdateSchoolVisuals();
            ApplyCurrentFrame();
            return;
        }

        UpdateDialogueSuppression();

        if (!_schoolScene.CanUseNormalTurnControls)
        {
            UpdateSchoolVisuals();
            ApplyCurrentFrame();
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

        int? selectedSchoolNumber =
            SchoolNumberFromPhysicalKey(keyEvent.PhysicalKeycode);
        PresentationKey presentationKey =
            keyEvent.PhysicalKeycode switch
            {
                Key.C => PresentationKey.C,
                Key.F => PresentationKey.F,
                Key.L => PresentationKey.L,
                Key.R => PresentationKey.R,
                Key.S => PresentationKey.S,
                Key.Key0 => PresentationKey.Zero,
                _ when selectedSchoolNumber is not null =>
                    PresentationKey.School,
                _ => PresentationKey.Other,
            };
        PresentationInputDecision decision =
            PresentationInputPolicy.Resolve(
                presentationKey,
                keyEvent.Pressed,
                keyEvent.Echo,
                dialogueEditing: false,
                _celebration.Phase);

        if (decision.ToggleLegend)
        {
            _actionLegend.Toggle();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (decision.HideBubble)
        {
            _dialogueUi.HideBubble();
            if (decision.AllowSchoolAction)
            {
                double characterProgress = CharacterProgressFromPosition();
                bool exitStarted = _schoolScene.TryStartExit(
                    pressed: true,
                    echo: false,
                    dialogueEditing: false,
                    characterProgress);
                if (exitStarted)
                {
                    _turn.Reset(
                        TurnDirection.Right,
                        DirectionalTurnStateMachine.FrontFrame);
                    UpdateSchoolVisuals();
                    ApplyCurrentFrame();
                }
            }

            GetViewport().SetInputAsHandled();
            return;
        }

        if (decision.StartLogoRain)
        {
            _logoRain.Start();
            ApplyCurrentFrame();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (presentationKey == PresentationKey.F)
        {
            if (decision.StartCelebration)
            {
                StartCelebration();
            }

            GetViewport().SetInputAsHandled();
            return;
        }

        if (presentationKey == PresentationKey.S)
        {
            if (
                decision.StopFireworks
                && _celebration.TryStopFireworks()
            )
            {
                _fireworks.StopAndClear();
                _character.Position = new Vector2(
                    CelebrationCenterX,
                    _character.Position.Y);
                _turn.Reset(
                    TurnDirection.Left,
                    DirectionalTurnStateMachine.FrontFrame);
                ShowConfiguredActionMessage("S");
                UpdateSchoolVisuals();
                ApplyCurrentFrame();
                GetViewport().SetInputAsHandled();
            }

            return;
        }

        if (selectedSchoolNumber is not null)
        {
            if (!decision.AllowSchoolAction)
            {
                GetViewport().SetInputAsHandled();
                return;
            }

            double characterProgress = CharacterProgressFromPosition();
            SchoolBackgroundLayout layout =
                _schoolBackgroundLayouts[selectedSchoolNumber.Value - 1];
            bool celebrationCrossHold =
                _celebration.IsStoppedCrossHold;
            bool entryStarted = _schoolScene.TryStartEntry(
                selectedSchoolNumber.Value,
                layout,
                pressed: true,
                echo: false,
                dialogueEditing: false,
                currentCharacterProgress: characterProgress,
                existingCrossHold:
                    celebrationCrossHold
                    || _turn.IsCrossArmsHeld);
            if (entryStarted)
            {
                if (celebrationCrossHold)
                {
                    _celebration.Cancel();
                }
                _pendingSchoolActionMessage =
                    _actionMessages.Get(
                        selectedSchoolNumber.Value.ToString());
                ApplySelectedSchoolBackground();
                _turn.Reset(
                    TurnDirection.Left,
                    DirectionalTurnStateMachine.FrontFrame);
                UpdateSchoolVisuals();
                ApplyCurrentFrame();
            }

            GetViewport().SetInputAsHandled();
            return;
        }

        if (
            keyEvent.PhysicalKeycode == Key.X
            && _celebration.IsStoppedCrossHold
        )
        {
            if (_celebration.TryReleaseStoppedHold())
            {
                UpdateSchoolVisuals();
                ApplyCurrentFrame();
                GetViewport().SetInputAsHandled();
            }

            return;
        }

        if (_celebration.SuppressesOrdinaryInput)
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        if (_schoolScene.SuppressesOrdinaryInput)
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        if (keyEvent.PhysicalKeycode == Key.C)
        {
            bool stateChanged = _turn.TryStartClap(
                Input.IsPhysicalKeyPressed(Key.Left),
                Input.IsPhysicalKeyPressed(Key.Right));
            if (stateChanged)
            {
                ApplyCurrentFrame();
            }

            GetViewport().SetInputAsHandled();
            return;
        }

        if (keyEvent.PhysicalKeycode == Key.X)
        {
            if (
                _schoolScene.TryReleaseFinalCrossHold(
                    pressed: true,
                    echo: false,
                    dialogueEditing: false)
            )
            {
                UpdateSchoolVisuals();
                ApplyCurrentFrame();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_schoolScene.IsFinalCrossHold)
            {
                return;
            }

            bool stateChanged = _turn.TryToggleCrossArms(
                Input.IsPhysicalKeyPressed(Key.Left),
                Input.IsPhysicalKeyPressed(Key.Right));
            if (stateChanged)
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

    private void UpdateSchoolVisuals()
    {
        _schoolBackground.Visible =
            !_celebration.IsActive
            && _schoolScene.IsSchoolVisible;
        if (_schoolScene.SelectedBackgroundLayout is { } layout)
        {
            _schoolBackground.Position = new Vector2(
                _schoolScene.CurrentBackgroundCenterX,
                layout.CenterY);
        }

        if (_celebration.IsActive)
        {
            _character.Position = new Vector2(
                (float)_celebration.CharacterX,
                _character.Position.Y);
        }
        else if (
            _schoolScene.Phase is not (
                SchoolScenePhase.NormalBlack
                or SchoolScenePhase.SchoolIdle)
        )
        {
            AnimationSafeCenters centers =
                AnimationGeometry.DefaultSafeCenters;
            float characterX =
                centers.Left
                + (centers.Right - centers.Left)
                * (float)_schoolScene.CharacterProgress;
            _character.Position =
                new Vector2(characterX, _character.Position.Y);
        }

        UpdateDialogueSuppression();
    }

    private void UpdateDialogueSuppression()
    {
        _dialogueUi.OpeningSuppressed =
            _schoolScene.SuppressesOrdinaryInput
            || _celebration.SuppressesOrdinaryInput;
    }

    private void StartCelebration()
    {
        double currentX = _character.Position.X;
        double characterProgress = CharacterProgressFromPosition();
        bool existingCrossHold =
            _celebration.IsStoppedCrossHold
            || _schoolScene.CharacterAnimation
                == SchoolCharacterAnimation.CrossArm
            || _turn.IsCrossArmsHeld;
        _schoolScene.CancelToBlack(characterProgress);
        _schoolBackground.Visible = false;
        _fireworks.StopAndClear();
        _pendingSchoolActionMessage = null;
        _dialogueUi.HideBubble();
        _turn.Reset(
            TurnDirection.Left,
            DirectionalTurnStateMachine.FrontFrame);

        AnimationSafeCenters centers =
            AnimationGeometry.DefaultSafeCenters;
        int centerArrivalSerial = _celebration.CenterArrivalSerial;
        bool started = _celebration.TryStart(
            currentX,
            CelebrationCenterX,
            centers.Right - centers.Left,
            existingCrossHold);
        if (!started)
        {
            return;
        }

        _fireworks.Start();
        if (_celebration.CenterArrivalSerial != centerArrivalSerial)
        {
            ShowConfiguredActionMessage("F");
        }
        UpdateSchoolVisuals();
        ApplyCurrentFrame();
    }

    private void LoadActionMessages()
    {
        const string path = "res://ActionMessages.json";
        if (!Godot.FileAccess.FileExists(path))
        {
            _actionMessageLoadStatus =
                ActionMessageLoadStatus.MissingFile;
            GD.PushError(
                "ACTION_MESSAGES_LOAD_FAIL status=MissingFile "
                + $"path={path}");
            return;
        }

        ActionMessageLoadResult result =
            ActionMessageCatalog.Load(
                Godot.FileAccess.GetFileAsBytes(path));
        _actionMessages = result.Catalog;
        _actionMessageLoadStatus = result.Status;
        if (!result.Succeeded)
        {
            GD.PushError(
                $"ACTION_MESSAGES_LOAD_FAIL status={result.Status} "
                + $"message={ToSingleLine(result.Error ?? "unknown")} "
                + $"path={path}");
            return;
        }

        GD.Print(
            "ACTION_MESSAGES_LOAD_PASS keys=1,2,3,4,5,6,F,S");
    }

    private static void LoadAnimationConfig()
    {
        const string path = "res://AnimationConfig.json";
        if (!Godot.FileAccess.FileExists(path))
        {
            throw new FileNotFoundException(
                "Required animation configuration is missing.",
                path);
        }

        AnimationConfig config = AnimationConfig.Load(
            Godot.FileAccess.GetFileAsBytes(path));
        AnimationConfig.Install(config);
        GD.Print(
            $"ANIMATION_CONFIG_LOAD_PASS school_entry={config.SchoolEntrySeconds:0.###} school_clap={config.SchoolClapSeconds:0.###} celebration_clap={config.CelebrationClapSeconds:0.###} logo_rain_spawn={config.LogoRainSpawnSeconds:0.###}");
    }

    private void ShowConfiguredActionMessage(string key)
    {
        _dialogueUi.ShowActionText(_actionMessages.Get(key));
    }

    private double CharacterProgressFromPosition()
    {
        AnimationSafeCenters centers = AnimationGeometry.DefaultSafeCenters;
        return Math.Clamp(
            (_character.Position.X - centers.Left)
            / (centers.Right - centers.Left),
            0.0f,
            1.0f);
    }

    private static int? SchoolNumberFromPhysicalKey(Key physicalKeycode)
    {
        return physicalKeycode switch
        {
            Key.Key1 => 1,
            Key.Key2 => 2,
            Key.Key3 => 3,
            Key.Key4 => 4,
            Key.Key5 => 5,
            Key.Key6 => 6,
            _ => null,
        };
    }

    private void ApplySelectedSchoolBackground()
    {
        int schoolNumber = _schoolScene.SelectedSchoolNumber
            ?? throw new InvalidOperationException(
                "School selection is unavailable.");
        SchoolBackgroundLayout layout =
            _schoolScene.SelectedBackgroundLayout
            ?? throw new InvalidOperationException(
                "School background layout is unavailable.");
        _schoolBackground.Texture =
            _schoolBackgroundTextures[schoolNumber - 1];
        _schoolBackground.Scale = Vector2.One * layout.Scale;
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
            _schoolLeftFrames,
            _schoolRightFrames,
            _schoolLeftWalkFrames,
            _schoolRightWalkFrames,
            _schoolClapFrames,
            _schoolCrossArmFrames,
            _schoolCrossArmReleaseFrames,
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

        if (frameCount != 66)
        {
            throw new InvalidOperationException(
                $"Loaded {frameCount} runtime frames; expected 66.");
        }

        if (_actionMessageLoadStatus != ActionMessageLoadStatus.Success)
        {
            throw new InvalidOperationException(
                "ActionMessages.json did not load successfully: "
                + _actionMessageLoadStatus);
        }
        foreach (
            string key in new[]
            {
                "1",
                "2",
                "3",
                "4",
                "5",
                "6",
                "F",
                "S",
            })
        {
            if (_actionMessages.Get(key) is null)
            {
                throw new InvalidOperationException(
                    $"Action message {key} is missing from the release config.");
            }
        }
        if (
            _fireworks.IsActive
            || _logoRain.IsActive
            || _actionLegend.IsLegendVisible
            || ActionLegendLayout.Entries.Length != 14
            || _smallLogoTexture.GetWidth() != 128
            || _smallLogoTexture.GetHeight() != 102
        )
        {
            throw new InvalidOperationException(
                "Celebration, fireworks, logo rain, or legend runtime state is invalid.");
        }

        string[] backgroundDimensions = new string[
            _schoolBackgroundTextures.Length];
        for (
            int index = 0;
            index < _schoolBackgroundTextures.Length;
            index++
        )
        {
            Texture2D texture = _schoolBackgroundTextures[index];
            SchoolBackgroundLayout layout = _schoolBackgroundLayouts[index];
            if (
                texture is null
                || layout.DisplayWidth + 0.001f
                    < AnimationGeometry.ViewportWidth
                || layout.DisplayHeight + 0.001f < ViewportHeight
                || layout.OffscreenRightX - layout.DisplayWidth / 2.0f
                    + 0.001f
                    < AnimationGeometry.ViewportWidth
            )
            {
                throw new InvalidOperationException(
                    $"School {index + 1} background geometry is invalid.");
            }

            backgroundDimensions[index] =
                $"{texture.GetWidth()}x{texture.GetHeight()}";
        }

        GD.Print(
            "RUNTIME_SMOKE_PASS character_textures=66 "
            + "school_backgrounds=6 dimensions="
            + string.Join(",", backgroundDimensions)
            + " dialogue_ui=true "
            + "action_messages=8 legend_entries=14 "
            + FormattableString.Invariant(
                $"celebration_seconds={CelebrationStateMachine.ClapDurationSeconds:0.###} ")
            + "fireworks_layer=true "
            + FormattableString.Invariant(
                $"logo_rain_seconds={LogoRainSimulation.DurationSeconds:0.###} ")
            + "logo_texture=128x102 "
            + "viewport_fit=1920x1080:CanvasItems:Keep");
        GetTree().Quit();
    }

    private void ApplyCurrentFrame()
    {
        if (_celebration.IsActive)
        {
            _character.Texture = GetCelebrationCharacterTexture(
                _celebration.CharacterAnimation,
                _celebration.CurrentAnimationFrame);
            return;
        }

        if (
            _schoolScene.CharacterAnimation
            != SchoolCharacterAnimation.Normal
        )
        {
            _character.Texture =
                GetSchoolSceneCharacterTexture(
                    _schoolScene.CharacterAnimation,
                    _schoolScene.CurrentAnimationFrame);
            return;
        }

        bool schoolOverlay =
            PresentationInputPolicy.ShouldUseTransparentCharacter(
                _schoolScene.UsesTransparentCharacter,
                _logoRain.IsActive);
        Texture2D[] crossArmFrames = schoolOverlay
            ? _schoolCrossArmFrames
            : _crossArmFrames;
        Texture2D[] crossArmReleaseFrames = schoolOverlay
            ? _schoolCrossArmReleaseFrames
            : _crossArmReleaseFrames;
        Texture2D[] clapFrames = schoolOverlay
            ? _schoolClapFrames
            : _clapFrames;
        Texture2D[] leftWalkFrames = schoolOverlay
            ? _schoolLeftWalkFrames
            : _leftWalkFrames;
        Texture2D[] rightWalkFrames = schoolOverlay
            ? _schoolRightWalkFrames
            : _rightWalkFrames;

        if (_turn.IsCrossingArms || _turn.IsCrossArmsHeld)
        {
            // The held state pins this index to cross_02, the third and final
            // crossing frame (human source pose 6 from CrossArm3.png).
            _character.Texture =
                crossArmFrames[_turn.CurrentCrossArmFrame];
            return;
        }

        if (_turn.IsReleasingCrossArms)
        {
            _character.Texture =
                crossArmReleaseFrames[
                    _turn.CurrentCrossArmReleaseFrame];
            return;
        }

        if (_turn.IsClapping)
        {
            _character.Texture = clapFrames[_turn.CurrentClapFrame];
            return;
        }

        if (_turn.IsWalkingLeft)
        {
            _character.Texture = leftWalkFrames[_turn.CurrentWalkFrame];
            return;
        }

        if (_turn.IsWalkingRight)
        {
            _character.Texture = rightWalkFrames[_turn.CurrentWalkFrame];
            return;
        }

        Texture2D[] frames = schoolOverlay
            ? (
                _turn.CurrentDirection == TurnDirection.Left
                    ? _schoolLeftFrames
                    : _schoolRightFrames
            )
            : (
                _turn.CurrentDirection == TurnDirection.Left
                    ? _leftFrames
                    : _rightFrames
            );
        _character.Texture = frames[_turn.CurrentFrame];
    }

    private Texture2D GetCelebrationCharacterTexture(
        CelebrationCharacterAnimation animation,
        int frame)
    {
        return animation switch
        {
            CelebrationCharacterAnimation.Normal =>
                _schoolLeftFrames[DirectionalTurnStateMachine.FrontFrame],
            CelebrationCharacterAnimation.WalkLeft =>
                _schoolLeftWalkFrames[frame],
            CelebrationCharacterAnimation.WalkRight =>
                _schoolRightWalkFrames[frame],
            CelebrationCharacterAnimation.Clap =>
                _schoolClapFrames[frame],
            CelebrationCharacterAnimation.CrossArm =>
                _schoolCrossArmFrames[frame],
            CelebrationCharacterAnimation.CrossArmRelease =>
                _schoolCrossArmReleaseFrames[frame],
            _ => throw new InvalidOperationException(
                $"Unsupported celebration animation: {animation}."),
        };
    }

    private Texture2D GetSchoolSceneCharacterTexture(
        SchoolCharacterAnimation animation,
        int frame)
    {
        bool schoolOverlay =
            PresentationInputPolicy.ShouldUseTransparentCharacter(
                _schoolScene.UsesTransparentCharacter,
                _logoRain.IsActive);
        return animation switch
        {
            SchoolCharacterAnimation.WalkRight =>
                (
                    schoolOverlay
                        ? _schoolRightWalkFrames
                        : _rightWalkFrames
                )[frame],
            SchoolCharacterAnimation.WalkLeft =>
                (
                    schoolOverlay
                        ? _schoolLeftWalkFrames
                        : _leftWalkFrames
                )[frame],
            SchoolCharacterAnimation.Clap =>
                (
                    schoolOverlay
                        ? _schoolClapFrames
                        : _clapFrames
                )[frame],
            SchoolCharacterAnimation.CrossArm =>
                (
                    schoolOverlay
                        ? _schoolCrossArmFrames
                        : _crossArmFrames
                )[frame],
            SchoolCharacterAnimation.CrossArmRelease =>
                (
                    schoolOverlay
                        ? _schoolCrossArmReleaseFrames
                        : _crossArmReleaseFrames
                )[frame],
            _ => throw new InvalidOperationException(
                $"Unsupported school character animation: {animation}."),
        };
    }

    private static CaptureModeConfiguration? ParseCaptureMode(
        IReadOnlyList<string> arguments,
        string? captureRoot)
    {
        int? captureFrame = null;
        TurnDirection captureDirection = TurnDirection.Left;
        string? capturePath = null;
        string? dialoguePreview = null;
        SchoolCaptureSelection? schoolCapture = null;
        CelebrationSnapshot? celebrationCapture = null;
        bool legendVisible = false;
        bool hideBubble = false;
        bool logoRainVisible = false;
        int frameOptionCount = 0;
        int directionOptionCount = 0;
        int pathOptionCount = 0;
        int dialogueOptionCount = 0;
        int schoolOptionCount = 0;
        int celebrationOptionCount = 0;
        int legendOptionCount = 0;
        int hideBubbleOptionCount = 0;
        int logoRainOptionCount = 0;
        bool captureModeRequested = IsCaptureModeRequested(arguments);

        foreach (string argument in arguments)
        {
            const string framePrefix = "--capture-frame=";
            const string directionPrefix = "--capture-direction=";
            const string pathPrefix = "--capture-path=";
            const string dialoguePrefix = "--dialogue-preview=";
            const string schoolPrefix = "--capture-school=";
            const string celebrationPrefix =
                "--capture-celebration=";

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
            else if (argument.StartsWith(schoolPrefix, StringComparison.Ordinal))
            {
                EnsureSingleOption(
                    ref schoolOptionCount,
                    "--capture-school");
                schoolCapture = ParseSchoolCapture(
                    argument[schoolPrefix.Length..]);
            }
            else if (
                argument.StartsWith(
                    celebrationPrefix,
                    StringComparison.Ordinal)
            )
            {
                EnsureSingleOption(
                    ref celebrationOptionCount,
                    "--capture-celebration");
                celebrationCapture = ParseCelebrationCapture(
                    argument[celebrationPrefix.Length..]);
            }
            else if (
                string.Equals(
                    argument,
                    "--capture-legend",
                    StringComparison.Ordinal)
            )
            {
                EnsureSingleOption(
                    ref legendOptionCount,
                    "--capture-legend");
                legendVisible = true;
            }
            else if (
                string.Equals(
                    argument,
                    "--capture-hide-bubble",
                    StringComparison.Ordinal)
            )
            {
                EnsureSingleOption(
                    ref hideBubbleOptionCount,
                    "--capture-hide-bubble");
                hideBubble = true;
            }
            else if (
                string.Equals(
                    argument,
                    "--capture-logo-rain",
                    StringComparison.Ordinal)
            )
            {
                EnsureSingleOption(
                    ref logoRainOptionCount,
                    "--capture-logo-rain");
                logoRainVisible = true;
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

        int baseSnapshotCount =
            (captureFrame is null ? 0 : 1)
            + (schoolCapture is null ? 0 : 1)
            + (celebrationCapture is null ? 0 : 1);
        if (capturePath is null || baseSnapshotCount != 1)
        {
            throw new ArgumentException(
                "Capture mode requires --capture-path and exactly one of "
                + "--capture-frame, --capture-school, or "
                + "--capture-celebration.");
        }

        if (
            captureFrame is not null
            && captureFrame.Value is
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
            captureFrame,
            captureDirection,
            captureRoot
                ?? throw new InvalidOperationException(
                    "Capture root was not resolved for capture mode."),
            capturePath,
            dialoguePreview,
            schoolCapture,
            celebrationCapture,
            legendVisible,
            hideBubble,
            logoRainVisible);
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
        if (captureMode.CelebrationCapture is not null)
        {
            _schoolScene.CancelToBlack(CharacterProgressFromPosition());
            _celebration.SetDevelopmentSnapshot(
                captureMode.CelebrationCapture.Value,
                CelebrationCenterX);
            _character.Position = new Vector2(
                (float)_celebration.CharacterX,
                _character.Position.Y);
            if (
                captureMode.CelebrationCapture
                    != CelebrationSnapshot.StoppedWithMessage
            )
            {
                _fireworks.Start(FireworksLayer.CaptureSeed);
                _fireworks.AdvanceForCapture(0.72);
                if (
                    captureMode.CelebrationCapture
                        != CelebrationSnapshot.WalkingToCenter
                )
                {
                    ShowConfiguredActionMessage("F");
                }
            }
            else if (
                captureMode.CelebrationCapture
                    == CelebrationSnapshot.StoppedWithMessage
            )
            {
                _fireworks.StopAndClear();
                ShowConfiguredActionMessage("S");
            }
        }
        else if (captureMode.SchoolCapture is not null)
        {
            SchoolCaptureSelection schoolCapture =
                captureMode.SchoolCapture;
            SchoolBackgroundLayout layout =
                _schoolBackgroundLayouts[schoolCapture.SchoolNumber - 1];
            _schoolScene.SetDevelopmentSnapshot(
                schoolCapture.SchoolNumber,
                layout,
                schoolCapture.Snapshot);
            ApplySelectedSchoolBackground();
            _turn.Reset(
                _schoolScene.CharacterAnimation
                        == SchoolCharacterAnimation.WalkRight
                    || schoolCapture.Snapshot
                        == SchoolSceneSnapshot.EntryEnd
                    || schoolCapture.Snapshot
                        == SchoolSceneSnapshot.ExitNormalizationMid
                    ? TurnDirection.Right
                    : TurnDirection.Left,
                DirectionalTurnStateMachine.FrontFrame);
            AnimationSafeCenters centers =
                AnimationGeometry.DefaultSafeCenters;
            _character.Position = new Vector2(
                centers.Left
                + (centers.Right - centers.Left)
                * (float)_schoolScene.CharacterProgress,
                _character.Position.Y);
            if (
                schoolCapture.Snapshot is
                    SchoolSceneSnapshot.EntryEnd
                    or SchoolSceneSnapshot.Clap
            )
            {
                ShowConfiguredActionMessage(
                    schoolCapture.SchoolNumber.ToString());
            }
        }
        else
        {
            _turn.Reset(
                captureMode.Direction,
                captureMode.Frame
                    ?? throw new InvalidOperationException(
                        "Capture frame is missing."));
        }
        ConfigureDialoguePreview(captureMode.DialoguePreview);
        if (captureMode.HideBubble)
        {
            _dialogueUi.HideBubble();
        }
        if (captureMode.LegendVisible)
        {
            _actionLegend.SetLegendVisible(true);
        }
        if (captureMode.LogoRainVisible)
        {
            _logoRain.Start(LogoRainLayer.CaptureSeed);
            _logoRain.AdvanceForCapture(3.2);
        }
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

    private static SchoolCaptureSelection ParseSchoolCapture(string value)
    {
        int separator = value.IndexOf(':');
        int schoolNumber = 1;
        string snapshotName = value;
        if (separator >= 0)
        {
            if (
                !int.TryParse(value[..separator], out schoolNumber)
                || schoolNumber is
                    < SchoolSceneStateMachine.MinimumSchoolNumber
                    or > SchoolSceneStateMachine.MaximumSchoolNumber
            )
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "School capture number must be between 1 and 6.");
            }

            snapshotName = value[(separator + 1)..];
        }

        SchoolSceneSnapshot snapshot = snapshotName switch
        {
            "entry-prep-mid" =>
                SchoolSceneSnapshot.EntryPreparationMid,
            "entry-prep-left" =>
                SchoolSceneSnapshot.EntryPreparationComplete,
            "entry-start" => SchoolSceneSnapshot.EntryStart,
            "entry-mid" => SchoolSceneSnapshot.EntryMid,
            "entry-end" => SchoolSceneSnapshot.EntryEnd,
            "clap" => SchoolSceneSnapshot.Clap,
            "exit-prep-mid" =>
                SchoolSceneSnapshot.ExitPreparationMid,
            "exit-normalize-mid" =>
                SchoolSceneSnapshot.ExitNormalizationMid,
            "exit-mid" => SchoolSceneSnapshot.ExitMid,
            "exit-end" => SchoolSceneSnapshot.ExitEnd,
            "cross-hold" => SchoolSceneSnapshot.FinalCrossHold,
            _ => throw new ArgumentException(
                $"Invalid school capture snapshot: {snapshotName}"),
        };
        return new SchoolCaptureSelection(schoolNumber, snapshot);
    }

    private static CelebrationSnapshot ParseCelebrationCapture(
        string value)
    {
        return value switch
        {
            "walk" => CelebrationSnapshot.WalkingToCenter,
            "clap" => CelebrationSnapshot.CenteredClapping,
            "fireworks" =>
                CelebrationSnapshot.CrossedWithFireworks,
            "stopped" => CelebrationSnapshot.StoppedWithMessage,
            _ => throw new ArgumentException(
                $"Invalid celebration capture snapshot: {value}"),
        };
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
                + $"frame={_turn.CurrentFrame} "
                + $"school_id={_schoolScene.SelectedSchoolNumber?.ToString() ?? "none"} "
                + $"school_phase={_schoolScene.Phase} "
                + $"celebration_phase={_celebration.Phase} "
                + $"legend={_actionLegend.IsLegendVisible} "
                + $"fireworks={_fireworks.IsActive} "
                + $"logo_rain={_logoRain.IsActive} path={_capturePath}");
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
        int? Frame,
        TurnDirection Direction,
        string Root,
        string Path,
        string? DialoguePreview,
        SchoolCaptureSelection? SchoolCapture,
        CelebrationSnapshot? CelebrationCapture,
        bool LegendVisible,
        bool HideBubble,
        bool LogoRainVisible);

    private sealed record SchoolCaptureSelection(
        int SchoolNumber,
        SchoolSceneSnapshot Snapshot);
}
