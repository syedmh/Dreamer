using TCFAnimation;
using System.Text;

const double TurnFps = 8.0;

RunTurnCases();
RunAnimationGeometryCases();
RunLeftWalkCases();
RunRightWalkCases();
RunWalkPlaybackSpeedCases();
RunClapCases();
RunCrossArmCases();
RunEdgeCases();
RunValidationCases();
RunDialogueCases();
RunDialogueLayoutCases();
RunCaptureRootCases();
RunCapturePathCases();
RunGlobalInputCases();
RunAvatarPresenceCases();
RunSchoolGeometryCases();
RunSchoolSceneCases();
RunActionMessageCases();
RunCelebrationCases();
RunFireworksCases();
RunLogoRainCases();
RunLegendAndPresentationInputCases();
RunAnimationConfigCases();

Console.WriteLine(
    $"CONTROLLER_PROBE_PASS assertions={ProbeAssertions.Count} "
    + "left_turn=true right_turn=true returns=true "
    + "direct_reversal_both_ways=true both_held_neutral=true "
    + "left_walk_6_frames=true right_walk_6_frames=true "
    + "left_edge_latch=true right_edge_latch=true "
    + "geometry_extrema=74,437 geometry_centers=227.5,1693.75 "
    + "fps_turn=8 fps_left_walk=6 fps_right_walk=6 "
    + "adjustable_walk_speed=true "
    + "clap_6_frames=true clap_15_steps=true fps_clap=8 clap_one_shot=true "
    + "clap_interruptible=true "
    + "cross_arm_3_frames=true fps_cross_arm=8 crossed_hold_cross_02=true "
    + "release_6_frames=true release_source=CrossArm4 fps_cross_arm_release=8 "
    + "cross_arm_direction_lock=true dialogue_input=true "
    + "dialogue_layout=true global_input=true "
    + "avatar_presence=true initial_blank=true d_exit=true e_enter=true "
    + "capture_root=true "
    + "school_geometry=all_6 school_choreography=all_6 "
    + "school_ids=1..6 invalid_ids=-1,0,7 "
    + "school_switching=serialized dialogue_digits=true "
    + "school_preposition_full_span_seconds=6 "
    + "school_entry_seconds=8 school_clap_seconds=10 "
    + "school_exit_seconds=8 entry_direction=right_to_left "
    + "fixed_runtime_frames=33 school_overlay_frames=33 "
    + "action_messages=true celebration_seconds=30 "
    + "fireworks_deterministic=true logo_rain_seconds=10 "
    + "legend=true neon_background=true c_manual_clap=true "
    + "l_legend=true "
    + "wave_assets=false");
return;

static void RunSchoolGeometryCases()
{
    foreach (int schoolNumber in Enumerable.Range(1, 6))
    {
        (float sourceWidth, float sourceHeight) =
            SchoolSourceSize(schoolNumber);
        SchoolBackgroundLayout layout = SchoolLayout(schoolNumber);
        AssertTrue(
            $"school {schoolNumber} 1920x1080 cover has no gaps",
            layout.DisplayWidth + 0.001f >= 1920.0f
            && layout.DisplayHeight + 0.001f >= 1080.0f);
        AssertNear(
            $"school {schoolNumber} aspect ratio preserved",
            layout.DisplayWidth / layout.DisplayHeight,
            sourceWidth / sourceHeight,
            0.0001);
        AssertNear(
            $"school {schoolNumber} center x",
            layout.CenterX,
            960.0,
            0.001);
        AssertNear(
            $"school {schoolNumber} off-left right edge",
            layout.OffscreenLeftX + layout.DisplayWidth / 2.0f,
            0.0,
            0.001);
        AssertNear(
            $"school {schoolNumber} off-right left edge",
            layout.OffscreenRightX - layout.DisplayWidth / 2.0f,
            1920.0,
            0.001);
        float entryStartX =
            SchoolSceneGeometry.BackgroundCenterX(layout, 1.0);
        float entryMidX =
            SchoolSceneGeometry.BackgroundCenterX(layout, 0.5);
        float entryEndX =
            SchoolSceneGeometry.BackgroundCenterX(layout, 0.0);
        AssertTrue(
            $"school {schoolNumber} entry and exit are monotonic",
            entryStartX > entryMidX
            && entryMidX > entryEndX
            && entryEndX < entryMidX
            && entryMidX < entryStartX);
        AssertTrue(
            $"school {schoolNumber} right endpoint is fully hidden",
            entryStartX == layout.OffscreenRightX
            && entryStartX - layout.DisplayWidth / 2.0f + 0.001f
                >= 1920.0f);
    }

    AssertThrows<ArgumentOutOfRangeException>(
        "school cover rejects zero viewport",
        () => SchoolSceneGeometry.CalculateAspectCover(
            0.0f,
            1080.0f,
            1908.0f,
            824.0f));
    AssertThrows<ArgumentOutOfRangeException>(
        "school cover rejects invalid texture",
        () => SchoolSceneGeometry.CalculateAspectCover(
            1920.0f,
            1080.0f,
            float.NaN,
            824.0f));
    SchoolBackgroundLayout validationLayout = SchoolLayout(1);
    AssertThrows<ArgumentOutOfRangeException>(
        "school right-offset rejects progress above one",
        () => SchoolSceneGeometry.BackgroundCenterX(
            validationLayout,
            1.01));
    AssertThrows<ArgumentOutOfRangeException>(
        "school right-offset rejects negative progress",
        () => SchoolSceneGeometry.BackgroundCenterX(
            validationLayout,
            -0.01));
}

static void RunAvatarPresenceCases()
{
    AnimationConfig.Install(AnimationConfig.Defaults);
    AnimationOffscreenCenters offscreen =
        AnimationGeometry.DefaultOffscreenCenters;
    AssertNear(
        "left offscreen center hides visible right edge",
        offscreen.Left,
        -226.25,
        0.001);
    AssertNear(
        "right offscreen center hides visible left edge",
        offscreen.Right,
        2147.5,
        0.001);

    AvatarPresenceStateMachine presence = new();
    AssertTrue(
        "Avatar starts completely hidden",
        presence.Phase == AvatarPresencePhase.Hidden
        && !presence.IsVisible
        && !presence.IsTransitioning);
    AssertTrue(
        "D is inert while Avatar is hidden",
        !presence.StartExit(
            960.0,
            offscreen.Left,
            offscreen.Right));

    AssertTrue(
        "E starts entry from right",
        presence.StartEnterFromRight(offscreen.Right, 960.0)
        && presence.IsVisible
        && presence.IsTransitioning
        && presence.IsWalkingLeft);
    AssertNear(
        "E begins fully offscreen right",
        presence.CharacterX,
        offscreen.Right,
        0.001);
    AssertTrue(
        "repeated E preserves active entrance",
        !presence.StartEnterFromRight(offscreen.Right, 960.0));
    presence.Advance(
        AnimationConfig.Current.AvatarEntrySeconds / 2.0);
    AssertNear(
        "E reaches midpoint at half duration",
        presence.CharacterX,
        (offscreen.Right + 960.0) / 2.0,
        0.001);
    AssertTrue(
        "E uses left walking frames",
        presence.IsWalkingLeft
        && presence.CurrentWalkFrame
            is >= 0
            and < DirectionalTurnStateMachine.LeftWalkFrameCount);
    presence.Advance(
        AnimationConfig.Current.AvatarEntrySeconds / 2.0);
    AssertTrue(
        "E ends visible and centered",
        presence.Phase == AvatarPresencePhase.Visible
        && presence.IsVisible
        && !presence.IsTransitioning);
    AssertNear(
        "E stops at exact screen center",
        presence.CharacterX,
        960.0,
        0.001);

    AssertTrue(
        "D exits left when left edge is nearer",
        presence.StartExit(
            960.0,
            offscreen.Left,
            offscreen.Right)
        && presence.Phase == AvatarPresencePhase.ExitingLeft
        && presence.IsWalkingLeft);
    presence.Advance(
        AnimationConfig.Current.AvatarExitFullSpanSeconds);
    AssertTrue(
        "D hides Avatar after left exit",
        presence.Phase == AvatarPresencePhase.Hidden
        && !presence.IsVisible);
    AssertNear(
        "left exit reaches fully offscreen center",
        presence.CharacterX,
        offscreen.Left,
        0.001);

    presence.SetVisible(1700.0);
    AssertTrue(
        "D exits right when right edge is nearer",
        presence.StartExit(
            1700.0,
            offscreen.Left,
            offscreen.Right)
        && presence.Phase == AvatarPresencePhase.ExitingRight
        && presence.IsWalkingRight);
    presence.Advance(
        AnimationConfig.Current.AvatarExitFullSpanSeconds);
    AssertTrue(
        "D hides Avatar after right exit",
        presence.Phase == AvatarPresencePhase.Hidden
        && !presence.IsVisible);
    AssertNear(
        "right exit reaches fully offscreen center",
        presence.CharacterX,
        offscreen.Right,
        0.001);

    presence.SetVisible(960.625);
    presence.StartExit(
        960.625,
        offscreen.Left,
        offscreen.Right);
    AssertEqual(
        "exact nearest-edge tie resolves left",
        presence.Phase,
        AvatarPresencePhase.ExitingLeft);
    AssertTrue(
        "E interrupts an exit and restarts at right edge",
        presence.StartEnterFromRight(offscreen.Right, 960.0)
        && presence.Phase == AvatarPresencePhase.EnteringFromRight);
    AssertNear(
        "interrupted E restarts fully offscreen right",
        presence.CharacterX,
        offscreen.Right,
        0.001);
}

static void RunSchoolSceneCases()
{
    SchoolBackgroundLayout school1Layout = SchoolLayout(1);
    SchoolSceneStateMachine scene = new();
    AssertEqual(
        "school starts in normal black",
        scene.Phase,
        SchoolScenePhase.NormalBlack);
    AssertTrue(
        "normal black enables normal controls",
        scene.CanUseNormalTurnControls
        && !scene.SuppressesOrdinaryInput
        && !scene.IsSchoolVisible);
    AssertNear(
        "pre-position full-span timing is explicit",
        SchoolSceneStateMachine.CharacterPrePositionFullSpanDurationSeconds,
        6.0,
        0.000001);
    AssertTrue(
        "released 1 is ignored",
        !scene.TryStartEntry(
            1,
            school1Layout,
            pressed: false,
            echo: false,
            dialogueEditing: false,
            currentCharacterProgress: 0.5));
    AssertTrue(
        "echoed 1 is ignored",
        !scene.TryStartEntry(
            1,
            school1Layout,
            pressed: true,
            echo: true,
            dialogueEditing: false,
            currentCharacterProgress: 0.5));
    AssertTrue(
        "1 remains typeable in dialogue",
        !scene.TryStartEntry(
            1,
            school1Layout,
            pressed: true,
            echo: false,
            dialogueEditing: true,
            currentCharacterProgress: 0.5));

    AssertTrue(
        "key1 from center starts left pre-position",
        scene.TryStartEntry(
            1,
            school1Layout,
            pressed: true,
            echo: false,
            dialogueEditing: false,
            currentCharacterProgress: 0.5));
    AssertEqual(
        "school entry preparation phase",
        scene.Phase,
        SchoolScenePhase.PreparingEntryLeft);
    AssertTrue(
        "entry preparation suppresses ordinary input on black",
        scene.SuppressesOrdinaryInput
        && !scene.IsSchoolVisible
        && !scene.UsesTransparentCharacter
        && scene.CharacterAnimation
            == SchoolCharacterAnimation.WalkLeft);
    AssertNear(
        "entry preparation keeps background off-right",
        scene.BackgroundRightOffsetProgress,
        1.0,
        0.000001);
    AssertNear(
        "entry preparation starts character center",
        scene.CharacterProgress,
        0.5,
        0.000001);
    AssertNear(
        "center to left preparation duration is half span",
        scene.CurrentPhaseDurationSeconds,
        3.0,
        0.000001);
    AssertTrue(
        "1 ignored during entry preparation",
        !scene.TryStartEntry(
            2,
            SchoolLayout(2),
            pressed: true,
            echo: false,
            dialogueEditing: false,
            currentCharacterProgress: 0.5));

    scene.Advance(1.5);
    AssertNear(
        "entry preparation walks left visibly",
        scene.CharacterProgress,
        0.25,
        0.000001);
    AssertNear(
        "entry preparation background remains hidden off-right",
        scene.BackgroundRightOffsetProgress,
        1.0,
        0.000001);
    scene.Advance(1.5);
    AssertEqual(
        "entry preparation completes into entry",
        scene.Phase,
        SchoolScenePhase.Entering);
    AssertNear(
        "entry begins at calibrated left",
        scene.CharacterProgress,
        0.0,
        0.000001);
    AssertNear(
        "entry begins with background offscreen right",
        scene.BackgroundRightOffsetProgress,
        1.0,
        0.000001);

    SchoolSceneStateMachine leftReady = new();
    AssertTrue(
        "key1 from left enters immediately",
        leftReady.TryStartEntry(
            1,
            school1Layout,
            true,
            false,
            false,
            currentCharacterProgress: 0.0));
    AssertEqual(
        "left-ready key1 skips preparation",
        leftReady.Phase,
        SchoolScenePhase.Entering);

    SchoolSceneStateMachine rightStart = new();
    AssertTrue(
        "key1 from right starts full left pre-position",
        rightStart.TryStartEntry(
            1,
            school1Layout,
            true,
            false,
            false,
            currentCharacterProgress: 1.0));
    AssertEqual(
        "right-start key1 uses left walk on black",
        rightStart.CharacterAnimation,
        SchoolCharacterAnimation.WalkLeft);
    AssertNear(
        "right-start key1 uses full six-second preparation",
        rightStart.CurrentPhaseDurationSeconds,
        6.0,
        0.000001);
    rightStart.Advance(3.0);
    AssertNear(
        "right-start key1 reaches midpoint without background",
        rightStart.CharacterProgress,
        0.5,
        0.000001);
    AssertTrue(
        "right-start key1 keeps school hidden",
        !rightStart.IsSchoolVisible);

    double priorEntryBackground =
        scene.BackgroundRightOffsetProgress;
    scene.Advance(4.0);
    AssertNear(
        "entry background synchronized midpoint",
        scene.BackgroundRightOffsetProgress,
        0.5,
        0.000001);
    AssertNear(
        "entry character synchronized midpoint",
        scene.CharacterProgress,
        0.5,
        0.000001);
    AssertTrue(
        "entry background progress decreases while avatar increases",
        scene.BackgroundRightOffsetProgress < priorEntryBackground
        && scene.CharacterProgress > 0.0);
    AssertEqual(
        "entry uses six-frame walk cadence",
        scene.CurrentAnimationFrame,
        0);

    scene.Advance(4.0);
    AssertEqual(
        "entry completion starts clap immediately",
        scene.Phase,
        SchoolScenePhase.Clapping);
    AssertNear(
        "entry completion centers background",
        scene.BackgroundRightOffsetProgress,
        0.0,
        0.000001);
    AssertNear(
        "entry completion places character right",
        scene.CharacterProgress,
        1.0,
        0.000001);
    AssertEqual(
        "clap starts at exact first frame",
        scene.CurrentAnimationFrame,
        0);

    for (
        int step = 1;
        step < DirectionalTurnStateMachine.ClapPlaybackStepCount * 2;
        step++
    )
    {
        scene.Advance(
            1.0 / DirectionalTurnStateMachine.ClapAnimationFps);
        AssertEqual(
            $"school repeated clap frame step {step}",
            scene.CurrentAnimationFrame,
            DirectionalTurnStateMachine.GetClapFrameForStep(
                step
                % DirectionalTurnStateMachine.ClapPlaybackStepCount));
    }

    SchoolSceneStateMachine exactClap = new();
    exactClap.TryStartEntry(1, school1Layout, true, false, false, 0.0);
    exactClap.Advance(SchoolSceneStateMachine.EntryDurationSeconds);
    exactClap.Advance(
        SchoolSceneStateMachine.ClapDurationSeconds - 0.001);
    AssertEqual(
        "school clap remains active before ten seconds",
        exactClap.Phase,
        SchoolScenePhase.Clapping);
    exactClap.Advance(0.001);
    AssertEqual(
        "school clap returns front at exactly ten seconds",
        exactClap.Phase,
        SchoolScenePhase.SchoolIdle);
    AssertTrue(
        "school idle restores ordinary controls but ignores 1",
        exactClap.CanUseNormalTurnControls
        && !exactClap.SuppressesOrdinaryInput
        && !exactClap.TryStartEntry(
            2,
            SchoolLayout(2),
            true,
            false,
            false,
            1.0)
        && exactClap.SelectedSchoolNumber == 1);

    SchoolSceneStateMachine interruptedClap = new();
    interruptedClap.TryStartEntry(
        1,
        school1Layout,
        true,
        false,
        false,
        0.0);
    interruptedClap.Advance(
        SchoolSceneStateMachine.EntryDurationSeconds + 2.0);
    AssertTrue(
        "echoed 0 is ignored",
        !interruptedClap.TryStartExit(
            pressed: true,
            echo: true,
            dialogueEditing: false,
            currentCharacterProgress: 1.0));
    AssertTrue(
        "0 remains typeable in dialogue",
        !interruptedClap.TryStartExit(
            pressed: true,
            echo: false,
            dialogueEditing: true,
            currentCharacterProgress: 1.0));
    AssertTrue(
        "released 0 is ignored",
        !interruptedClap.TryStartExit(
            pressed: false,
            echo: false,
            dialogueEditing: false,
            currentCharacterProgress: 1.0));
    AssertTrue(
        "0 interrupts clap and exits immediately when already right",
        interruptedClap.TryStartExit(
            pressed: true,
            echo: false,
            dialogueEditing: false,
            currentCharacterProgress: 1.0));
    AssertEqual(
        "right-ready key0 skips exit preparation",
        interruptedClap.Phase,
        SchoolScenePhase.Exiting);
    AssertEqual(
        "clap interruption enters left walk",
        interruptedClap.CharacterAnimation,
        SchoolCharacterAnimation.WalkLeft);
    AssertNear(
        "full school exit lasts eight seconds",
        interruptedClap.ExitDurationSeconds,
        SchoolSceneStateMachine.ExitTravelDurationSeconds,
        0.000001);
    interruptedClap.Advance(4.0);
    AssertNear(
        "exit background midpoint",
        interruptedClap.BackgroundRightOffsetProgress,
        0.5,
        0.000001);
    AssertNear(
        "exit character midpoint",
        interruptedClap.CharacterProgress,
        0.5,
        0.000001);
    interruptedClap.Advance(4.0);
    AssertEqual(
        "exit completes to crossing",
        interruptedClap.Phase,
        SchoolScenePhase.CrossingFinal);
    AssertTrue(
        "exit completion is pure black",
        !interruptedClap.IsSchoolVisible
        && !interruptedClap.UsesTransparentCharacter);
    AssertNear(
        "exit background fully right",
        interruptedClap.BackgroundRightOffsetProgress,
        1.0,
        0.000001);
    AssertNear(
        "exit character at calibrated left",
        interruptedClap.CharacterProgress,
        0.0,
        0.000001);
    AssertEqual(
        "auto cross starts frame zero",
        interruptedClap.CurrentAnimationFrame,
        0);
    interruptedClap.Advance(0.125);
    AssertEqual(
        "auto cross advances frame one",
        interruptedClap.CurrentAnimationFrame,
        1);
    interruptedClap.Advance(0.125);
    AssertEqual(
        "auto cross advances frame two",
        interruptedClap.CurrentAnimationFrame,
        2);
    interruptedClap.Advance(0.125);
    AssertEqual(
        "auto cross holds final frame",
        interruptedClap.Phase,
        SchoolScenePhase.BlackCrossHold);
    AssertEqual(
        "auto cross hold pins cross 02",
        interruptedClap.CurrentAnimationFrame,
        2);

    AssertTrue(
        "1 auto-releases final cross hold",
        interruptedClap.TryStartEntry(
            6,
            SchoolLayout(6),
            true,
            false,
            false,
            0.0));
    AssertEqual(
        "re-entry replaces selected school identity",
        interruptedClap.SelectedSchoolNumber,
        6);
    AssertEqual(
        "re-entry begins with release",
        interruptedClap.Phase,
        SchoolScenePhase.ReleasingForEntry);
    interruptedClap.Advance(
        DirectionalTurnStateMachine.CrossArmReleaseFrameCount
        / DirectionalTurnStateMachine.CrossArmReleaseAnimationFps);
    AssertEqual(
        "left-held re-entry starts immediately after complete release",
        interruptedClap.Phase,
        SchoolScenePhase.Entering);
    AssertNear(
        "re-entry restarts off-right",
        interruptedClap.BackgroundRightOffsetProgress,
        1.0,
        0.000001);

    SchoolSceneStateMachine existingCrossAtRight = new();
    AssertTrue(
        "key1 releases an existing normal-black cross hold",
        existingCrossAtRight.TryStartEntry(
            3,
            SchoolLayout(3),
            true,
            false,
            false,
            currentCharacterProgress: 0.75,
            existingCrossHold: true));
    existingCrossAtRight.Advance(
        DirectionalTurnStateMachine.CrossArmReleaseFrameCount
        / DirectionalTurnStateMachine.CrossArmReleaseAnimationFps);
    AssertEqual(
        "existing hold release continues into left pre-position",
        existingCrossAtRight.Phase,
        SchoolScenePhase.PreparingEntryLeft);
    AssertNear(
        "existing hold keeps its character position through release",
        existingCrossAtRight.CharacterProgress,
        0.75,
        0.000001);

    SchoolSceneStateMachine prepareExit = new();
    prepareExit.SetDevelopmentSnapshot(
        1,
        school1Layout,
        SchoolSceneSnapshot.EntryEnd);
    AssertTrue(
        "key0 from left-side school idle prepares right first",
        prepareExit.TryStartExit(true, false, false, 0.25));
    AssertEqual(
        "key0 enters right preparation",
        prepareExit.Phase,
        SchoolScenePhase.PreparingExitRight);
    AssertTrue(
        "exit preparation holds visible centered school",
        prepareExit.IsSchoolVisible
        && prepareExit.UsesTransparentCharacter
        && prepareExit.CharacterAnimation
            == SchoolCharacterAnimation.WalkRight);
    AssertNear(
        "exit preparation duration scales remaining character distance",
        prepareExit.CurrentPhaseDurationSeconds,
        4.5,
        0.000001);
    prepareExit.Advance(2.25);
    AssertNear(
        "exit preparation walks toward right",
        prepareExit.CharacterProgress,
        0.625,
        0.000001);
    AssertNear(
        "exit preparation holds centered background",
        prepareExit.BackgroundRightOffsetProgress,
        0.0,
        0.000001);
    prepareExit.Advance(2.25);
    AssertEqual(
        "centered background exits after right preparation",
        prepareExit.Phase,
        SchoolScenePhase.Exiting);

    SchoolSceneStateMachine reversed = new();
    reversed.TryStartEntry(1, school1Layout, true, false, false, 0.0);
    reversed.Advance(2.0);
    AssertNear(
        "partial entry has expected background progress",
        reversed.BackgroundRightOffsetProgress,
        0.75,
        0.000001);
    AssertNear(
        "partial entry has expected character progress",
        reversed.CharacterProgress,
        0.25,
        0.000001);
    AssertTrue(
        "0 reconciles an in-progress entry",
        reversed.TryStartExit(true, false, false, 0.25));
    AssertEqual(
        "partial entry first prepares avatar right",
        reversed.Phase,
        SchoolScenePhase.PreparingExitRight);
    AssertNear(
        "partial entry background does not snap on 0",
        reversed.BackgroundRightOffsetProgress,
        0.75,
        0.000001);
    reversed.Advance(2.25);
    AssertNear(
        "partial entry background remains held during right preparation",
        reversed.BackgroundRightOffsetProgress,
        0.75,
        0.000001);
    AssertNear(
        "partial entry avatar visibly moves right during preparation",
        reversed.CharacterProgress,
        0.625,
        0.000001);
    reversed.Advance(2.25);
    AssertEqual(
        "partial entry normalizes background after avatar reaches right",
        reversed.Phase,
        SchoolScenePhase.NormalizingExitBackground);
    AssertNear(
        "normalization starts without background teleport",
        reversed.BackgroundRightOffsetProgress,
        0.75,
        0.000001);
    AssertNear(
        "normalization holds avatar at right",
        reversed.CharacterProgress,
        1.0,
        0.000001);
    AssertNear(
        "normalization preserves entry background speed",
        reversed.CurrentPhaseDurationSeconds,
        6.0,
        0.000001);
    reversed.Advance(3.0);
    AssertNear(
        "normalization moves background smoothly toward center",
        reversed.BackgroundRightOffsetProgress,
        0.375,
        0.000001);
    reversed.Advance(3.0);
    AssertEqual(
        "normalization completes into synchronized exit",
        reversed.Phase,
        SchoolScenePhase.Exiting);
    AssertNear(
        "normalized exit starts centered",
        reversed.BackgroundRightOffsetProgress,
        0.0,
        0.000001);
    reversed.Advance(4.0);
    AssertNear(
        "reconciled exit background moves right",
        reversed.BackgroundRightOffsetProgress,
        0.5,
        0.000001);
    AssertNear(
        "reconciled exit avatar moves left",
        reversed.CharacterProgress,
        0.5,
        0.000001);

    SchoolSceneStateMachine released = new();
    released.SetDevelopmentSnapshot(
        4,
        SchoolLayout(4),
        SchoolSceneSnapshot.FinalCrossHold);
    AssertTrue(
        "X semantics release final cross hold",
        released.TryReleaseFinalCrossHold(true, false, false));
    released.Advance(
        DirectionalTurnStateMachine.CrossArmReleaseFrameCount
        / DirectionalTurnStateMachine.CrossArmReleaseAnimationFps);
    AssertEqual(
        "X release returns to normal black",
        released.Phase,
        SchoolScenePhase.NormalBlack);
    AssertTrue(
        "normal black clears selected school identity and geometry",
        released.SelectedSchoolNumber is null
        && released.SelectedBackgroundLayout is null);

    SchoolSceneStateMachine largeDelta = new();
    largeDelta.TryStartEntry(
        1,
        school1Layout,
        true,
        false,
        false,
        1.0);
    largeDelta.Advance(1000.0);
    AssertEqual(
        "large delta stops deterministically at school idle",
        largeDelta.Phase,
        SchoolScenePhase.SchoolIdle);
    AssertThrows<ArgumentOutOfRangeException>(
        "school rejects negative delta",
        () => largeDelta.Advance(-0.001));
    AssertThrows<ArgumentOutOfRangeException>(
        "school rejects NaN delta",
        () => largeDelta.Advance(double.NaN));
    AssertThrows<ArgumentOutOfRangeException>(
        "school exit rejects invalid character progress",
        () =>
        {
            SchoolSceneStateMachine invalid = new();
            invalid.TryStartEntry(
                1,
                school1Layout,
                true,
                false,
                false,
                0.0);
            invalid.TryStartExit(true, false, false, double.PositiveInfinity);
        });
    AssertThrows<ArgumentOutOfRangeException>(
        "school entry rejects invalid character progress",
        () =>
        {
            SchoolSceneStateMachine invalid = new();
            invalid.TryStartEntry(
                1,
                school1Layout,
                true,
                false,
                false,
                double.NaN);
        });
    SchoolSceneStateMachine largeInterruptedExit = new();
    largeInterruptedExit.TryStartEntry(
        1,
        school1Layout,
        true,
        false,
        false,
        0.0);
    largeInterruptedExit.Advance(2.0);
    largeInterruptedExit.TryStartExit(true, false, false, 0.25);
    largeInterruptedExit.Advance(1000.0);
    AssertEqual(
        "large delta completes reconciled exit deterministically",
        largeInterruptedExit.Phase,
        SchoolScenePhase.BlackCrossHold);
    AssertNear(
        "large reconciled exit ends with background off-right",
        largeInterruptedExit.BackgroundRightOffsetProgress,
        1.0,
        0.000001);
    AssertNear(
        "large reconciled exit ends with avatar left",
        largeInterruptedExit.CharacterProgress,
        0.0,
        0.000001);

    (SchoolSceneSnapshot Snapshot, SchoolScenePhase Phase,
        double Background, double Character,
        SchoolCharacterAnimation Animation, bool Visible)[] snapshots =
    [
        (
            SchoolSceneSnapshot.EntryPreparationMid,
            SchoolScenePhase.PreparingEntryLeft,
            1.0,
            0.5,
            SchoolCharacterAnimation.WalkLeft,
            false
        ),
        (
            SchoolSceneSnapshot.EntryPreparationComplete,
            SchoolScenePhase.PreparingEntryLeft,
            1.0,
            0.0,
            SchoolCharacterAnimation.WalkLeft,
            false
        ),
        (
            SchoolSceneSnapshot.EntryStart,
            SchoolScenePhase.Entering,
            1.0,
            0.0,
            SchoolCharacterAnimation.WalkRight,
            true
        ),
        (
            SchoolSceneSnapshot.EntryMid,
            SchoolScenePhase.Entering,
            0.5,
            0.5,
            SchoolCharacterAnimation.WalkRight,
            true
        ),
        (
            SchoolSceneSnapshot.EntryEnd,
            SchoolScenePhase.SchoolIdle,
            0.0,
            1.0,
            SchoolCharacterAnimation.Normal,
            true
        ),
        (
            SchoolSceneSnapshot.ExitPreparationMid,
            SchoolScenePhase.PreparingExitRight,
            0.0,
            0.5,
            SchoolCharacterAnimation.WalkRight,
            true
        ),
        (
            SchoolSceneSnapshot.ExitNormalizationMid,
            SchoolScenePhase.NormalizingExitBackground,
            0.375,
            1.0,
            SchoolCharacterAnimation.Normal,
            true
        ),
        (
            SchoolSceneSnapshot.ExitMid,
            SchoolScenePhase.Exiting,
            0.5,
            0.5,
            SchoolCharacterAnimation.WalkLeft,
            true
        ),
        (
            SchoolSceneSnapshot.ExitEnd,
            SchoolScenePhase.CrossingFinal,
            1.0,
            0.0,
            SchoolCharacterAnimation.CrossArm,
            false
        ),
        (
            SchoolSceneSnapshot.FinalCrossHold,
            SchoolScenePhase.BlackCrossHold,
            1.0,
            0.0,
            SchoolCharacterAnimation.CrossArm,
            false
        ),
    ];
    foreach (var snapshot in snapshots)
    {
        SchoolSceneStateMachine captured = new();
        captured.SetDevelopmentSnapshot(
            5,
            SchoolLayout(5),
            snapshot.Snapshot);
        AssertEqual(
            $"school snapshot {snapshot.Snapshot} phase",
            captured.Phase,
            snapshot.Phase);
        AssertNear(
            $"school snapshot {snapshot.Snapshot} background",
            captured.BackgroundRightOffsetProgress,
            snapshot.Background,
            0.000001);
        AssertNear(
            $"school snapshot {snapshot.Snapshot} character",
            captured.CharacterProgress,
            snapshot.Character,
            0.000001);
        AssertEqual(
            $"school snapshot {snapshot.Snapshot} animation",
            captured.CharacterAnimation,
            snapshot.Animation);
        AssertEqual(
            $"school snapshot {snapshot.Snapshot} visibility",
            captured.IsSchoolVisible,
            snapshot.Visible);
        AssertEqual(
            $"school snapshot {snapshot.Snapshot} identity",
            captured.SelectedSchoolNumber,
            5);
        AssertTrue(
            $"school snapshot {snapshot.Snapshot} suppresses ordinary input",
            captured.SuppressesOrdinaryInput
            || snapshot.Phase
                is SchoolScenePhase.SchoolIdle
                or SchoolScenePhase.BlackCrossHold);
    }

    foreach (int schoolNumber in Enumerable.Range(1, 6))
    {
        VerifyCompleteSchoolSequence(schoolNumber);
    }
    VerifyPartialEntryGeometryDoesNotSnap(1);
    VerifyPartialEntryGeometryDoesNotSnap(2);

    foreach (int invalidSchoolNumber in new[] { -1, 0, 7 })
    {
        AssertThrows<ArgumentOutOfRangeException>(
            $"school rejects invalid selection {invalidSchoolNumber}",
            () => new SchoolSceneStateMachine().TryStartEntry(
                invalidSchoolNumber,
                school1Layout,
                true,
                false,
                false,
                0.0));
    }
}

static (float Width, float Height) SchoolSourceSize(int schoolNumber)
{
    return schoolNumber switch
    {
        1 => (1908.0f, 824.0f),
        2 => (1536.0f, 1024.0f),
        3 or 4 or 5 or 6 => (1540.0f, 1021.0f),
        _ => throw new ArgumentOutOfRangeException(nameof(schoolNumber)),
    };
}

static SchoolBackgroundLayout SchoolLayout(int schoolNumber)
{
    (float width, float height) = SchoolSourceSize(schoolNumber);
    return SchoolSceneGeometry.CalculateAspectCover(
        1920.0f,
        1080.0f,
        width,
        height);
}

static void VerifyCompleteSchoolSequence(int schoolNumber)
{
    SchoolBackgroundLayout layout = SchoolLayout(schoolNumber);
    SchoolSceneStateMachine scene = new();
    AssertTrue(
        $"school {schoolNumber} starts from black",
        scene.TryStartEntry(
            schoolNumber,
            layout,
            true,
            false,
            false,
            0.5));
    AssertEqual(
        $"school {schoolNumber} selected during preparation",
        scene.SelectedSchoolNumber,
        schoolNumber);
    AssertNear(
        $"school {schoolNumber} uses selected hidden-right geometry",
        scene.CurrentBackgroundCenterX,
        layout.OffscreenRightX,
        0.001);
    scene.Advance(3.0);
    AssertEqual(
        $"school {schoolNumber} enters after preparation",
        scene.Phase,
        SchoolScenePhase.Entering);
    scene.Advance(SchoolSceneStateMachine.EntryDurationSeconds);
    AssertEqual(
        $"school {schoolNumber} reaches clap",
        scene.Phase,
        SchoolScenePhase.Clapping);
    AssertNear(
        $"school {schoolNumber} is centered for clap",
        scene.CurrentBackgroundCenterX,
        layout.CenterX,
        0.001);
    AssertTrue(
        $"school {schoolNumber} ignores another selection while active",
        !scene.TryStartEntry(
            schoolNumber == 6 ? 1 : schoolNumber + 1,
            SchoolLayout(schoolNumber == 6 ? 1 : schoolNumber + 1),
            true,
            false,
            false,
            1.0)
        && scene.SelectedSchoolNumber == schoolNumber);
    scene.Advance(SchoolSceneStateMachine.ClapDurationSeconds);
    AssertEqual(
        $"school {schoolNumber} reaches idle",
        scene.Phase,
        SchoolScenePhase.SchoolIdle);
    AssertTrue(
        $"school {schoolNumber} begins visible right preparation",
        scene.TryStartExit(true, false, false, 0.25)
        && scene.Phase == SchoolScenePhase.PreparingExitRight
        && scene.SelectedSchoolNumber == schoolNumber);
    scene.Advance(4.5);
    AssertEqual(
        $"school {schoolNumber} exits after right preparation",
        scene.Phase,
        SchoolScenePhase.Exiting);
    scene.Advance(SchoolSceneStateMachine.ExitTravelDurationSeconds);
    AssertEqual(
        $"school {schoolNumber} reaches final crossing",
        scene.Phase,
        SchoolScenePhase.CrossingFinal);
    AssertNear(
        $"school {schoolNumber} exit is fully offscreen right",
        scene.CurrentBackgroundCenterX,
        layout.OffscreenRightX,
        0.001);
    scene.Advance(
        DirectionalTurnStateMachine.CrossArmFrameCount
        / DirectionalTurnStateMachine.CrossArmAnimationFps);
    AssertEqual(
        $"school {schoolNumber} reaches black cross hold",
        scene.Phase,
        SchoolScenePhase.BlackCrossHold);

    int reentrySchool = schoolNumber == 6 ? 1 : schoolNumber + 1;
    AssertTrue(
        $"school {reentrySchool} re-enters from final cross hold",
        scene.TryStartEntry(
            reentrySchool,
            SchoolLayout(reentrySchool),
            true,
            false,
            false,
            0.0));
    AssertEqual(
        $"school {reentrySchool} replaces final-hold selection",
        scene.SelectedSchoolNumber,
        reentrySchool);
}

static void VerifyPartialEntryGeometryDoesNotSnap(int schoolNumber)
{
    SchoolBackgroundLayout layout = SchoolLayout(schoolNumber);
    SchoolSceneStateMachine scene = new();
    scene.TryStartEntry(
        schoolNumber,
        layout,
        true,
        false,
        false,
        0.0);
    scene.Advance(2.0);
    float before = scene.CurrentBackgroundCenterX;
    scene.TryStartExit(true, false, false, 0.25);
    float after = scene.CurrentBackgroundCenterX;
    AssertNear(
        $"school {schoolNumber} partial-entry 0 does not snap background",
        after,
        before,
        0.001);
    scene.Advance(4.5);
    AssertNear(
        $"school {schoolNumber} normalization starts at held position",
        scene.CurrentBackgroundCenterX,
        before,
        0.001);
}

static void RunGlobalInputCases()
{
    AssertSingleGlobalRoute(
        "Alt+Enter closed routes once before focused controls",
        GlobalInputKey.Enter,
        altPressed: true,
        dialogueEditing: false,
        fullscreen: false,
        GlobalInputAction.ToggleFullscreen);
    AssertSingleGlobalRoute(
        "Alt+Enter editing routes once before focused controls",
        GlobalInputKey.Enter,
        altPressed: true,
        dialogueEditing: true,
        fullscreen: true,
        GlobalInputAction.ToggleFullscreen);
    AssertSingleGlobalRoute(
        "F11 editing routes once before focused controls",
        GlobalInputKey.F11,
        altPressed: false,
        dialogueEditing: true,
        fullscreen: false,
        GlobalInputAction.ToggleFullscreen);

    DialogueModel routeDialogue = new();
    DialogueAction opened = routeDialogue.HandleKey(
        DialogueKey.Enter,
        echo: false);
    const string exactDraft = "  Keep\tthis exact text!  ";
    GlobalInputAction editingToggle = GlobalInputPolicy.Resolve(
        GlobalInputPhase.EarlyInput,
        GlobalInputKey.Enter,
        pressed: true,
        echo: false,
        altPressed: true,
        dialogueEditing: routeDialogue.IsEditing,
        fullscreen: true);
    AssertTrue(
        "Alt+Enter preserves open editor state without dialogue action",
        opened.Opened
        && editingToggle == GlobalInputAction.ToggleFullscreen
        && routeDialogue.IsEditing
        && routeDialogue.SuppressCharacterInput
        && !routeDialogue.IsBubbleVisible
        && routeDialogue.BubbleText.Length == 0);

    GlobalInputAction firstEscape = GlobalInputPolicy.Resolve(
        GlobalInputPhase.EarlyInput,
        GlobalInputKey.Escape,
        pressed: true,
        echo: false,
        altPressed: false,
        dialogueEditing: routeDialogue.IsEditing,
        fullscreen: true);
    DialogueAction cancelled = routeDialogue.HandleKey(
        DialogueKey.Escape,
        echo: false,
        exactDraft);
    GlobalInputAction secondEscape = GlobalInputPolicy.Resolve(
        GlobalInputPhase.EarlyInput,
        GlobalInputKey.Escape,
        pressed: true,
        echo: false,
        altPressed: false,
        dialogueEditing: routeDialogue.IsEditing,
        fullscreen: true);
    AssertTrue(
        "Escape remains two-stage while editing fullscreen",
        firstEscape == GlobalInputAction.None
        && cancelled.Closed
        && !routeDialogue.IsEditing
        && secondEscape == GlobalInputAction.ExitFullscreen);

    GlobalInputAction plainEnter = GlobalInputPolicy.Resolve(
        GlobalInputPhase.EarlyInput,
        GlobalInputKey.Enter,
        pressed: true,
        echo: false,
        altPressed: false,
        dialogueEditing: false,
        fullscreen: false);
    DialogueAction plainEnterAction = routeDialogue.HandleKey(
        DialogueKey.Enter,
        echo: false);
    AssertTrue(
        "plain Enter remains owned by dialogue",
        plainEnter == GlobalInputAction.None
        && plainEnterAction.Opened
        && routeDialogue.IsEditing);

    bool[] booleanValues = [false, true];
    foreach (bool dialogueEditing in booleanValues)
    {
        foreach (bool fullscreen in booleanValues)
        {
            foreach (bool altPressed in booleanValues)
            {
                AssertEqual(
                    $"F11 toggles fullscreen dialogue={dialogueEditing} "
                    + $"fullscreen={fullscreen} alt={altPressed}",
                    GlobalInputPolicy.Resolve(
                        GlobalInputPhase.EarlyInput,
                        GlobalInputKey.F11,
                        pressed: true,
                        echo: false,
                        altPressed,
                        dialogueEditing,
                        fullscreen),
                    GlobalInputAction.ToggleFullscreen);
            }

            AssertEqual(
                $"Alt+Enter toggles fullscreen dialogue={dialogueEditing} "
                + $"fullscreen={fullscreen}",
                GlobalInputPolicy.Resolve(
                    GlobalInputPhase.EarlyInput,
                    GlobalInputKey.Enter,
                    pressed: true,
                    echo: false,
                    altPressed: true,
                    dialogueEditing,
                    fullscreen),
                GlobalInputAction.ToggleFullscreen);
            AssertEqual(
                $"Escape arbitration dialogue={dialogueEditing} "
                + $"fullscreen={fullscreen}",
                GlobalInputPolicy.Resolve(
                    GlobalInputPhase.EarlyInput,
                    GlobalInputKey.Escape,
                    pressed: true,
                    echo: false,
                    altPressed: false,
                    dialogueEditing,
                    fullscreen),
                !dialogueEditing && fullscreen
                    ? GlobalInputAction.ExitFullscreen
                    : GlobalInputAction.None);
            AssertEqual(
                $"plain Enter is not global dialogue={dialogueEditing} "
                + $"fullscreen={fullscreen}",
                GlobalInputPolicy.Resolve(
                    GlobalInputPhase.EarlyInput,
                    GlobalInputKey.Enter,
                    pressed: true,
                    echo: false,
                    altPressed: false,
                    dialogueEditing,
                    fullscreen),
                GlobalInputAction.None);
            AssertEqual(
                $"other key is not global dialogue={dialogueEditing} "
                + $"fullscreen={fullscreen}",
                GlobalInputPolicy.Resolve(
                    GlobalInputPhase.EarlyInput,
                    GlobalInputKey.Other,
                    pressed: true,
                    echo: false,
                    altPressed: true,
                    dialogueEditing,
                    fullscreen),
                GlobalInputAction.None);
        }
    }

    AssertEqual(
        "released global key is ignored",
        GlobalInputPolicy.Resolve(
            GlobalInputPhase.EarlyInput,
            GlobalInputKey.F11,
            pressed: false,
            echo: false,
            altPressed: false,
            dialogueEditing: true,
            fullscreen: false),
        GlobalInputAction.None);
    AssertEqual(
        "echoed global key is ignored",
        GlobalInputPolicy.Resolve(
            GlobalInputPhase.EarlyInput,
            GlobalInputKey.Enter,
            pressed: true,
            echo: true,
            altPressed: true,
            dialogueEditing: true,
            fullscreen: true),
        GlobalInputAction.None);
}

static void AssertSingleGlobalRoute(
    string stepName,
    GlobalInputKey key,
    bool altPressed,
    bool dialogueEditing,
    bool fullscreen,
    GlobalInputAction expectedAction)
{
    GlobalInputAction earlyAction = GlobalInputPolicy.Resolve(
        GlobalInputPhase.EarlyInput,
        key,
        pressed: true,
        echo: false,
        altPressed,
        dialogueEditing,
        fullscreen);
    GlobalInputAction unhandledAction = GlobalInputPolicy.Resolve(
        GlobalInputPhase.UnhandledKeyInput,
        key,
        pressed: true,
        echo: false,
        altPressed,
        dialogueEditing,
        fullscreen);
    AssertTrue(
        stepName,
        earlyAction == expectedAction
        && unhandledAction == GlobalInputAction.None);
}

static void RunAnimationGeometryCases()
{
    AnimationSafeCenters centers = AnimationGeometry.DefaultSafeCenters;
    AssertEqual("left walk visible extremum", AnimationGeometry.LeftWalkVisibleX, 74.0f);
    AssertEqual("right walk visible extremum", AnimationGeometry.RightWalkVisibleX, 437.0f);
    AssertEqual("left safe center", centers.Left, 227.5f);
    AssertEqual("right safe center", centers.Right, 1693.75f);

    AnimationSafeCenters shifted = AnimationGeometry.CalculateSafeCenters(
        viewportLeft: 50.0f,
        viewportWidth: AnimationGeometry.ViewportWidth,
        canvasCenterX: AnimationGeometry.CanvasCenterX,
        characterScale: AnimationGeometry.CharacterScale,
        leftVisibleX: AnimationGeometry.LeftWalkVisibleX,
        rightVisibleX: AnimationGeometry.RightWalkVisibleX);
    AssertEqual("shifted left safe center", shifted.Left, 277.5f);
    AssertEqual("shifted right safe center", shifted.Right, 1743.75f);

    AssertThrows<ArgumentOutOfRangeException>(
        "geometry rejects zero viewport width",
        () => AnimationGeometry.CalculateSafeCenters(
            0.0f,
            0.0f,
            256.0f,
            1.25f,
            74.0f,
            437.0f));
    AssertThrows<ArgumentOutOfRangeException>(
        "geometry rejects nonpositive scale",
        () => AnimationGeometry.CalculateSafeCenters(
            0.0f,
            1920.0f,
            256.0f,
            0.0f,
            74.0f,
            437.0f));
    AssertThrows<ArgumentOutOfRangeException>(
        "geometry rejects extrema that do not bracket center",
        () => AnimationGeometry.CalculateSafeCenters(
            0.0f,
            1920.0f,
            256.0f,
            1.25f,
            300.0f,
            437.0f));
}

static void RunTurnCases()
{
    DirectionalTurnStateMachine turn = NewState();
    double step = turn.FrameDurationSeconds;

    AssertPose("start uses left front", turn, TurnDirection.Left, 0);

    AssertChanged("left advances to L1", turn.Advance(true, false, step));
    AssertPose("left L1", turn, TurnDirection.Left, 1);
    AssertChanged("left advances to L2", turn.Advance(true, false, step));
    AssertPose("left L2 remains visible", turn, TurnDirection.Left, 2);
    AssertChanged("following call enters left walk", turn.Advance(true, false, 0.0));
    AssertWalk("left walk starts at frame zero", turn, TurnDirection.Left, 0);

    AssertChanged("release left walk exposes L2", turn.Advance(false, false, 0.0));
    AssertPose("released left walk pose", turn, TurnDirection.Left, 2);
    turn.Advance(false, false, step);
    AssertPose("left return L1", turn, TurnDirection.Left, 1);
    turn.Advance(false, false, step);
    AssertPose("left return L0", turn, TurnDirection.Left, 0);

    AssertChanged("right selects R0", turn.Advance(false, true, 0.0));
    AssertPose("right sheet front", turn, TurnDirection.Right, 0);
    turn.Advance(false, true, step);
    AssertPose("right R1", turn, TurnDirection.Right, 1);
    turn.Advance(false, true, step);
    AssertPose("right R2 remains visible", turn, TurnDirection.Right, 2);
    AssertChanged("following call enters right walk", turn.Advance(false, true, 0.0));
    AssertWalk("right walk starts at frame zero", turn, TurnDirection.Right, 0);

    AssertChanged("release right walk exposes R2", turn.Advance(false, false, 0.0));
    AssertPose("released right walk pose", turn, TurnDirection.Right, 2);
    turn.Advance(false, false, step);
    AssertPose("right return R1", turn, TurnDirection.Right, 1);
    turn.Advance(false, false, step);
    AssertPose("right return R0", turn, TurnDirection.Right, 0);

    turn.Advance(false, true, step);
    turn.Advance(false, true, step);
    AssertPose("prepare right reversal at R2", turn, TurnDirection.Right, 2);
    turn.Advance(true, false, step);
    AssertPose("right reversal R1", turn, TurnDirection.Right, 1);
    turn.Advance(true, false, step);
    AssertPose("right reversal R0", turn, TurnDirection.Right, 0);
    turn.Advance(true, false, 0.0);
    AssertPose("right reversal selects L0", turn, TurnDirection.Left, 0);
    turn.Advance(true, false, step);
    AssertPose("right reversal turns to L1", turn, TurnDirection.Left, 1);
    turn.Advance(true, false, step);
    AssertPose("right reversal completes L2", turn, TurnDirection.Left, 2);

    turn.Advance(true, true, step);
    AssertPose("both held returns through L1", turn, TurnDirection.Left, 1);
    turn.Advance(true, true, step);
    AssertPose("both held reaches L0", turn, TurnDirection.Left, 0);
    AssertChanged(
        "both held stays neutral",
        turn.Advance(true, true, step * 4.0),
        expected: false);
    AssertPose("both held remains L0", turn, TurnDirection.Left, 0);

    turn.Advance(true, false, step);
    turn.Advance(true, false, step);
    AssertPose("prepare left reversal at L2", turn, TurnDirection.Left, 2);
    turn.Advance(false, true, step);
    AssertPose("left reversal L1", turn, TurnDirection.Left, 1);
    turn.Advance(false, true, step);
    AssertPose("left reversal L0", turn, TurnDirection.Left, 0);
    turn.Advance(false, true, 0.0);
    AssertPose("left reversal selects R0", turn, TurnDirection.Right, 0);
    turn.Advance(false, true, step);
    AssertPose("left reversal turns to R1", turn, TurnDirection.Right, 1);
    turn.Advance(false, true, step);
    AssertPose("left reversal completes R2", turn, TurnDirection.Right, 2);
}

static void RunLeftWalkCases()
{
    DirectionalTurnStateMachine turn = EnterWalk(TurnDirection.Left);
    double walkStep = turn.LeftWalkFrameDurationSeconds;

    for (
        int expectedFrame = 1;
        expectedFrame < DirectionalTurnStateMachine.LeftWalkFrameCount;
        expectedFrame++
    )
    {
        AssertChanged(
            $"left walk advances to frame {expectedFrame}",
            turn.Advance(true, false, walkStep));
        AssertWalk(
            $"left walk frame {expectedFrame}",
            turn,
            TurnDirection.Left,
            expectedFrame);
    }

    AssertChanged("left walk wraps", turn.Advance(true, false, walkStep));
    AssertWalk("left walk wrapped frame zero", turn, TurnDirection.Left, 0);
    AssertChanged(
        "left multi-frame delta changes frame",
        turn.Advance(true, false, walkStep * 5.0));
    AssertWalk("left multi-frame reaches five", turn, TurnDirection.Left, 5);
    AssertChanged(
        "left full-cycle delta preserves texture",
        turn.Advance(
            true,
            false,
            walkStep * DirectionalTurnStateMachine.LeftWalkFrameCount),
        expected: false);
    AssertWalk("left full-cycle wraps in place", turn, TurnDirection.Left, 5);

    AssertChanged("left release cancels walking", turn.Advance(false, false, 0.0));
    AssertPose("left release restores L2", turn, TurnDirection.Left, 2);
    AssertWalkReset("left release resets walk frame", turn);

    turn = EnterWalk(TurnDirection.Left);
    AssertChanged("both held cancels left walk", turn.Advance(true, true, walkStep));
    AssertPose("both held restores L2", turn, TurnDirection.Left, 2);
    turn.Advance(true, true, turn.FrameDurationSeconds);
    AssertPose("both held returns through L1", turn, TurnDirection.Left, 1);

    turn = EnterWalk(TurnDirection.Left);
    AssertChanged("right cancels left walk", turn.Advance(false, true, walkStep));
    AssertPose("right cancellation restores L2", turn, TurnDirection.Left, 2);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    AssertPose("right reversal from left walk reaches L1", turn, TurnDirection.Left, 1);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    AssertPose("right reversal from left walk reaches L0", turn, TurnDirection.Left, 0);
    turn.Advance(false, true, 0.0);
    AssertPose("right reversal from left walk selects R0", turn, TurnDirection.Right, 0);
}

static void RunRightWalkCases()
{
    DirectionalTurnStateMachine turn = EnterWalk(TurnDirection.Right);
    double walkStep = turn.RightWalkFrameDurationSeconds;

    for (
        int expectedFrame = 1;
        expectedFrame < DirectionalTurnStateMachine.RightWalkFrameCount;
        expectedFrame++
    )
    {
        AssertChanged(
            $"right walk advances to frame {expectedFrame}",
            turn.Advance(false, true, walkStep));
        AssertWalk(
            $"right walk frame {expectedFrame}",
            turn,
            TurnDirection.Right,
            expectedFrame);
    }

    AssertChanged("right walk wraps", turn.Advance(false, true, walkStep));
    AssertWalk("right walk wrapped frame zero", turn, TurnDirection.Right, 0);
    AssertChanged(
        "right multi-frame delta changes frame",
        turn.Advance(false, true, walkStep * 4.0));
    AssertWalk("right multi-frame reaches four", turn, TurnDirection.Right, 4);
    AssertChanged(
        "right full-cycle delta preserves texture",
        turn.Advance(
            false,
            true,
            walkStep * DirectionalTurnStateMachine.RightWalkFrameCount),
        expected: false);
    AssertWalk("right full-cycle wraps in place", turn, TurnDirection.Right, 4);

    AssertChanged("right release cancels walking", turn.Advance(false, false, 0.0));
    AssertPose("right release restores R2", turn, TurnDirection.Right, 2);
    AssertWalkReset("right release resets walk frame", turn);

    turn = EnterWalk(TurnDirection.Right);
    AssertChanged("both held cancels right walk", turn.Advance(true, true, walkStep));
    AssertPose("both held restores R2", turn, TurnDirection.Right, 2);
    turn.Advance(true, true, turn.FrameDurationSeconds);
    AssertPose("both held returns through R1", turn, TurnDirection.Right, 1);

    turn = EnterWalk(TurnDirection.Right);
    AssertChanged("left cancels right walk", turn.Advance(true, false, walkStep));
    AssertPose("left cancellation restores R2", turn, TurnDirection.Right, 2);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    AssertPose("left reversal from right walk reaches R1", turn, TurnDirection.Right, 1);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    AssertPose("left reversal from right walk reaches R0", turn, TurnDirection.Right, 0);
    turn.Advance(true, false, 0.0);
    AssertPose("left reversal from right walk selects L0", turn, TurnDirection.Left, 0);
}

static void RunWalkPlaybackSpeedCases()
{
    double[] multipliers = [0.25, 0.5, 1.0, 2.0, 3.0];

    foreach (TurnDirection direction in Enum.GetValues<TurnDirection>())
    {
        foreach (double multiplier in multipliers)
        {
            DirectionalTurnStateMachine turn = EnterWalk(direction);
            double baseWalkStep = direction == TurnDirection.Left
                ? turn.LeftWalkFrameDurationSeconds
                : turn.RightWalkFrameDurationSeconds;
            bool leftHeld = direction == TurnDirection.Left;
            bool rightHeld = direction == TurnDirection.Right;

            AssertChanged(
                $"{direction} walk advances one frame at {multiplier:0.##}x",
                turn.Advance(
                    leftHeld,
                    rightHeld,
                    baseWalkStep / multiplier,
                    multiplier));
            AssertWalk(
                $"{direction} walk playback is proportional at "
                + $"{multiplier:0.##}x",
                turn,
                direction,
                1);
        }
    }

    DirectionalTurnStateMachine turning = NewState();
    double halfTurnStep = turning.FrameDurationSeconds / 2.0;
    AssertChanged(
        "walk multiplier does not accelerate first half of turn",
        turning.Advance(true, false, halfTurnStep, 3.0),
        expected: false);
    AssertPose(
        "turn remains at L0 after half step with 3x walk multiplier",
        turning,
        TurnDirection.Left,
        0);
    AssertChanged(
        "walk multiplier does not slow second half of turn",
        turning.Advance(true, false, halfTurnStep, 0.25));
    AssertPose(
        "turn reaches L1 after one fixed 8 FPS interval",
        turning,
        TurnDirection.Left,
        1);
}

static void RunClapCases()
{
    int[] expectedOrder = [0, 1, 2, 3, 4, 3, 2, 1, 2, 3, 4, 3, 2, 1, 0];

    if (DirectionalTurnStateMachine.ClapFrameCount != 6)
    {
        throw new InvalidOperationException(
            "Clapping2 runtime must load exactly six selected source poses.");
    }
    if (
        DirectionalTurnStateMachine.ClapPlaybackStepCount
        != expectedOrder.Length
    )
    {
        throw new InvalidOperationException(
            "Clapping2 runtime must use the requested 15-step sequence.");
    }
    if (DirectionalTurnStateMachine.ClapAnimationFps != 8.0)
    {
        throw new InvalidOperationException(
            "Clapping2 runtime must remain fixed at 8 FPS.");
    }

    DirectionalTurnStateMachine turn = NewState();
    double clapStep = turn.ClapFrameDurationSeconds;

    for (
        int step = 0;
        step < DirectionalTurnStateMachine.ClapPlaybackStepCount;
        step++
    )
    {
        if (
            DirectionalTurnStateMachine.GetClapFrameForStep(step)
            != expectedOrder[step]
        )
        {
            throw new InvalidOperationException(
                $"clap playback step {step} did not map to Clapping2 "
                + $"runtime frame {expectedOrder[step]}.");
        }
    }

    AssertChanged("front idle starts clap", turn.TryStartClap());
    AssertClap("clap starts at source frame zero", turn, 0, 0);
    AssertChanged(
        "active clap does not restart",
        turn.TryStartClap(),
        expected: false);
    for (
        int expectedStep = 1;
        expectedStep < DirectionalTurnStateMachine.ClapPlaybackStepCount;
        expectedStep++
    )
    {
        double multiplier = expectedStep % 2 == 0 ? 0.25 : 3.0;
        AssertChanged(
            $"clap advances to step {expectedStep} at walk {multiplier:0.##}x",
            turn.Advance(false, false, clapStep, multiplier));
        AssertClap(
            $"clap step {expectedStep} ignores walk multiplier",
            turn,
            expectedStep,
            expectedOrder[expectedStep]);
    }

    AssertChanged(
        "clap completes after final frame duration",
        turn.Advance(false, false, clapStep, 3.0));
    AssertPose(
        "completed clap returns to normal left front",
        turn,
        TurnDirection.Left,
        DirectionalTurnStateMachine.FrontFrame);

    turn.Advance(false, true, 0.0);
    AssertPose("clap restriction setup selects R0", turn, TurnDirection.Right, 0);
    AssertChanged(
        "clap ignored while directional request is active at front",
        turn.TryStartClap(),
        expected: false);
    turn.Advance(false, false, 0.0);
    AssertChanged("right front idle can clap", turn.TryStartClap());
    for (
        int expectedStep = 1;
        expectedStep < DirectionalTurnStateMachine.ClapPlaybackStepCount;
        expectedStep++
    )
    {
        turn.Advance(false, false, clapStep);
    }
    turn.Advance(false, false, clapStep);
    AssertPose(
        "completed clap returns to normal right front",
        turn,
        TurnDirection.Right,
        DirectionalTurnStateMachine.FrontFrame);

    AssertChanged("right front starts clap for left cancellation", turn.TryStartClap());
    AssertChanged(
        "left cancels clap immediately",
        turn.Advance(true, false, 0.0));
    AssertPose(
        "left clap cancellation selects left front",
        turn,
        TurnDirection.Left,
        0);

    turn.Advance(false, false, 0.0);
    AssertChanged("left front starts clap for right cancellation", turn.TryStartClap());
    AssertChanged(
        "right cancels clap immediately",
        turn.Advance(false, true, 0.0));
    AssertPose(
        "right clap cancellation selects right front",
        turn,
        TurnDirection.Right,
        0);

    turn.Advance(false, false, 0.0);
    turn.Advance(true, false, 0.0);
    turn.Advance(false, false, 0.0);
    AssertChanged("left front idle starts another clap", turn.TryStartClap());
    AssertChanged(
        "same-direction arrow cancels clap",
        turn.Advance(true, false, 0.0));
    AssertPose(
        "same-direction clap cancellation restores left front",
        turn,
        TurnDirection.Left,
        0);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    AssertPose(
        "left behavior proceeds after clap cancellation",
        turn,
        TurnDirection.Left,
        1);

    AssertChanged(
        "clap ignored while turning",
        turn.TryStartClap(),
        expected: false);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    turn.Advance(true, false, 0.0);
    AssertWalk("clap restriction setup enters left walk", turn, TurnDirection.Left, 0);
    AssertChanged(
        "clap ignored while walking",
        turn.TryStartClap(),
        expected: false);
    turn.Advance(false, false, 0.0);
    turn.Advance(false, false, turn.FrameDurationSeconds);
    AssertPose("clap restriction setup is returning", turn, TurnDirection.Left, 1);
    AssertChanged(
        "clap ignored while returning",
        turn.TryStartClap(),
        expected: false);

    turn = EnterWalk(TurnDirection.Right);
    turn.NotifyRightEdgeReached();
    turn.Advance(false, true, turn.FrameDurationSeconds);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    AssertPose("clap edge latch reaches front", turn, TurnDirection.Right, 0);
    AssertTrue("right edge latch remains active at front", turn.IsRightEdgeLatched);
    AssertChanged(
        "clap ignored while edge latch is active",
        turn.TryStartClap(),
        expected: false);
    turn.Advance(false, false, 0.0);
    AssertChanged("clap starts after edge latch release", turn.TryStartClap());
    AssertClap("post-latch clap starts at zero", turn, 0, 0);
}

static void RunCrossArmCases()
{
    if (DirectionalTurnStateMachine.CrossArmFrameCount != 3)
    {
        throw new InvalidOperationException(
            "cross-arm runtime must load exactly three crossing frames.");
    }
    if (DirectionalTurnStateMachine.CrossArmReleaseFrameCount != 6)
    {
        throw new InvalidOperationException(
            "cross-arm release must load all six selected CrossArm4 frames.");
    }

    DirectionalTurnStateMachine turn = NewState();
    double crossStep = turn.CrossArmFrameDurationSeconds;
    double releaseStep = turn.CrossArmReleaseFrameDurationSeconds;

    AssertChanged("front idle starts cross-arm transition", turn.TryToggleCrossArms());
    AssertCrossing("cross-arm transition starts at frame zero", turn, 0);
    AssertChanged(
        "repeated X cannot restart crossing",
        turn.TryToggleCrossArms(),
        expected: false);
    AssertChanged(
        "C ignored while crossing",
        turn.TryStartClap(),
        expected: false);
    AssertChanged(
        "left ignored while crossing",
        turn.Advance(true, false, crossStep / 2.0),
        expected: false);
    AssertCrossing("crossing remains on frame zero after half step", turn, 0);

    AssertChanged(
        "crossing reaches frame one",
        turn.Advance(false, false, crossStep / 2.0, 3.0));
    AssertCrossing("cross-arm frame one", turn, 1);
    for (
        int expectedFrame = 2;
        expectedFrame < DirectionalTurnStateMachine.CrossArmFrameCount;
        expectedFrame++
    )
    {
        double multiplier = expectedFrame % 2 == 0 ? 0.25 : 3.0;
        AssertChanged(
            $"crossing reaches frame {expectedFrame} at walk "
            + $"{multiplier:0.##}x",
            turn.Advance(false, false, crossStep, multiplier));
        AssertCrossing(
            $"cross-arm frame {expectedFrame} ignores walk multiplier",
            turn,
            expectedFrame);
    }

    AssertChanged(
        "crossing enters persistent hold after final frame duration",
        turn.Advance(false, false, crossStep, 0.25));
    AssertCrossedHold("crossed hold uses exact final frame", turn);
    AssertChanged(
        "large delta does not churn crossed hold",
        turn.Advance(false, false, 1_000_000.0, 3.0),
        expected: false);
    AssertCrossedHold("crossed hold persists across large delta", turn);
    AssertChanged(
        "C ignored while crossed",
        turn.TryStartClap(),
        expected: false);
    AssertChanged(
        "left ignored while crossed",
        turn.Advance(true, false, turn.FrameDurationSeconds * 4.0),
        expected: false);
    AssertCrossedHold("left cannot turn crossed hold", turn);
    AssertChanged(
        "right ignored while crossed",
        turn.Advance(false, true, turn.FrameDurationSeconds * 4.0),
        expected: false);
    AssertCrossedHold("right cannot turn crossed hold", turn);

    AssertChanged(
        "second X from fully crossed hold starts release",
        turn.TryToggleCrossArms(false, true));
    AssertReleasing("release starts at frame zero", turn, 0);
    AssertChanged(
        "repeated X cannot restart or reverse release",
        turn.TryToggleCrossArms(),
        expected: false);
    AssertChanged(
        "C ignored while releasing",
        turn.TryStartClap(),
        expected: false);

    for (
        int expectedFrame = 1;
        expectedFrame < DirectionalTurnStateMachine.CrossArmReleaseFrameCount;
        expectedFrame++
    )
    {
        bool leftHeld = expectedFrame == 1;
        bool rightHeld = expectedFrame == 2;
        double multiplier = expectedFrame % 2 == 0 ? 3.0 : 0.25;
        AssertChanged(
            $"release reaches frame {expectedFrame} while arrows are ignored",
            turn.Advance(leftHeld, rightHeld, releaseStep, multiplier));
        AssertReleasing(
            $"release frame {expectedFrame} ignores walk multiplier",
            turn,
            expectedFrame);
    }

    AssertChanged(
        "release completes only after final frame duration",
        turn.Advance(false, true, releaseStep, 3.0));
    AssertPose(
        "release returns to normal left front",
        turn,
        TurnDirection.Left,
        DirectionalTurnStateMachine.FrontFrame);
    AssertChanged(
        "held arrow from release cannot queue a turn",
        turn.Advance(false, true, turn.FrameDurationSeconds * 4.0),
        expected: false);
    AssertPose(
        "queued right remains blocked after release",
        turn,
        TurnDirection.Left,
        DirectionalTurnStateMachine.FrontFrame);
    AssertChanged(
        "releasing arrows clears post-cross direction lock",
        turn.Advance(false, false, 0.0),
        expected: false);
    AssertChanged(
        "fresh right input selects right front",
        turn.Advance(false, true, 0.0));
    AssertPose(
        "fresh right works after crossed release",
        turn,
        TurnDirection.Right,
        DirectionalTurnStateMachine.FrontFrame);

    turn.Advance(false, false, 0.0);
    AssertChanged("right front idle can cross arms", turn.TryToggleCrossArms());
    for (
        int frame = 1;
        frame < DirectionalTurnStateMachine.CrossArmFrameCount;
        frame++
    )
    {
        turn.Advance(false, false, crossStep);
    }
    turn.Advance(false, false, crossStep);
    AssertCrossedHold("right-front crossing reaches hold", turn);
    AssertChanged("right-front hold starts release", turn.TryToggleCrossArms());
    for (
        int frame = 1;
        frame < DirectionalTurnStateMachine.CrossArmReleaseFrameCount;
        frame++
    )
    {
        turn.Advance(false, false, releaseStep);
    }
    turn.Advance(false, false, releaseStep);
    AssertPose(
        "right-front release restores right front",
        turn,
        TurnDirection.Right,
        DirectionalTurnStateMachine.FrontFrame);

    turn = NewState();
    AssertChanged("clap setup starts", turn.TryStartClap());
    AssertChanged(
        "X rejected from clap",
        turn.TryToggleCrossArms(),
        expected: false);
    turn.Advance(true, false, 0.0);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    AssertChanged(
        "X rejected while turning",
        turn.TryToggleCrossArms(),
        expected: false);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    turn.Advance(true, false, 0.0);
    AssertWalk("cross restriction setup enters walk", turn, TurnDirection.Left, 0);
    AssertChanged(
        "X rejected while walking",
        turn.TryToggleCrossArms(),
        expected: false);
    turn.Advance(false, false, 0.0);
    turn.Advance(false, false, turn.FrameDurationSeconds);
    AssertPose(
        "cross restriction setup is returning",
        turn,
        TurnDirection.Left,
        1);
    AssertChanged(
        "X rejected while returning",
        turn.TryToggleCrossArms(),
        expected: false);

    turn = EnterWalk(TurnDirection.Left);
    turn.NotifyLeftEdgeReached();
    turn.Advance(true, false, turn.FrameDurationSeconds);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    AssertTrue("cross restriction edge latch remains active", turn.IsLeftEdgeLatched);
    AssertChanged(
        "X rejected while edge-latched",
        turn.TryToggleCrossArms(),
        expected: false);
}

static void RunEdgeCases()
{
    DirectionalTurnStateMachine turn = NewState();
    AssertChanged(
        "left edge outside left walk is no-op",
        turn.NotifyLeftEdgeReached(),
        expected: false);
    AssertChanged(
        "right edge outside right walk is no-op",
        turn.NotifyRightEdgeReached(),
        expected: false);

    turn = EnterWalk(TurnDirection.Left);
    AssertChanged("left edge stops left walking", turn.NotifyLeftEdgeReached());
    AssertPose("left edge restores L2", turn, TurnDirection.Left, 2);
    AssertTrue("left edge sets latch", turn.IsLeftEdgeLatched);
    AssertChanged(
        "right edge during latched left pose is no-op",
        turn.NotifyRightEdgeReached(),
        expected: false);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    AssertPose("latched held left forces L1", turn, TurnDirection.Left, 1);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    AssertPose("latched held left forces L0", turn, TurnDirection.Left, 0);
    turn.Advance(true, false, turn.FrameDurationSeconds * 4.0);
    AssertPose("latched held left remains L0", turn, TurnDirection.Left, 0);
    AssertNotWalking("latched held left cannot restart", turn);
    AssertTrue("left latch remains while left held", turn.IsLeftEdgeLatched);

    turn.Advance(false, true, 0.0);
    AssertTrue("releasing left clears only left latch", !turn.IsLeftEdgeLatched);
    AssertPose("right can select R0 after left latch", turn, TurnDirection.Right, 0);

    turn = EnterWalk(TurnDirection.Right);
    AssertChanged("right edge stops right walking", turn.NotifyRightEdgeReached());
    AssertPose("right edge restores R2", turn, TurnDirection.Right, 2);
    AssertTrue("right edge sets latch", turn.IsRightEdgeLatched);
    AssertChanged(
        "left edge during latched right pose is no-op",
        turn.NotifyLeftEdgeReached(),
        expected: false);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    AssertPose("latched held right forces R1", turn, TurnDirection.Right, 1);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    AssertPose("latched held right forces R0", turn, TurnDirection.Right, 0);
    turn.Advance(false, true, turn.FrameDurationSeconds * 4.0);
    AssertPose("latched held right remains R0", turn, TurnDirection.Right, 0);
    AssertNotWalking("latched held right cannot restart", turn);
    AssertTrue("right latch remains while right held", turn.IsRightEdgeLatched);

    turn.Advance(true, false, 0.0);
    AssertTrue("releasing right clears right latch", !turn.IsRightEdgeLatched);
    AssertPose("left can select L0 after right latch", turn, TurnDirection.Left, 0);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    turn.Advance(true, false, 0.0);
    AssertWalk(
        "left still walks after right edge operations",
        turn,
        TurnDirection.Left,
        0);

    turn.Advance(false, false, 0.0);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    turn.Advance(false, true, 0.0);
    AssertWalk(
        "right restarts after latch release",
        turn,
        TurnDirection.Right,
        0);
}

static void RunValidationCases()
{
    int runtimeFrameCount =
        3
        + 3
        + DirectionalTurnStateMachine.LeftWalkFrameCount
        + DirectionalTurnStateMachine.RightWalkFrameCount
        + DirectionalTurnStateMachine.ClapFrameCount
        + DirectionalTurnStateMachine.CrossArmFrameCount
        + DirectionalTurnStateMachine.CrossArmReleaseFrameCount;
    AssertEqual("runtime frame count remains fixed", runtimeFrameCount, 33);
    AssertTrue(
        "runtime state contract has no wave",
        Enum.GetNames<TurnDirection>().All(
            name => !name.Contains("wave", StringComparison.OrdinalIgnoreCase))
        && Enum.GetNames<FrontGesture>().All(
            name => !name.Contains("wave", StringComparison.OrdinalIgnoreCase))
        && Enum.GetNames<CrossArmPhase>().All(
            name => !name.Contains("wave", StringComparison.OrdinalIgnoreCase)));

    AssertThrows<ArgumentOutOfRangeException>(
        "constructor rejects zero fps",
        () => new DirectionalTurnStateMachine(0.0));
    AssertThrows<ArgumentOutOfRangeException>(
        "constructor rejects infinite fps",
        () => new DirectionalTurnStateMachine(double.PositiveInfinity));

    DirectionalTurnStateMachine turn = NewState();
    AssertThrows<ArgumentOutOfRangeException>(
        "advance rejects negative delta",
        () => turn.Advance(true, false, -0.001));
    AssertThrows<ArgumentOutOfRangeException>(
        "advance rejects NaN delta",
        () => turn.Advance(true, false, double.NaN));
    AssertThrows<ArgumentOutOfRangeException>(
        "advance rejects infinite delta",
        () => turn.Advance(true, false, double.PositiveInfinity));
    AssertThrows<ArgumentOutOfRangeException>(
        "advance rejects zero walk multiplier",
        () => turn.Advance(true, false, 0.0, 0.0));
    AssertThrows<ArgumentOutOfRangeException>(
        "advance rejects negative walk multiplier",
        () => turn.Advance(true, false, 0.0, -0.25));
    AssertThrows<ArgumentOutOfRangeException>(
        "advance rejects NaN walk multiplier",
        () => turn.Advance(true, false, 0.0, double.NaN));
    AssertThrows<ArgumentOutOfRangeException>(
        "advance rejects infinite walk multiplier",
        () => turn.Advance(
            true,
            false,
            0.0,
            double.PositiveInfinity));

    AssertChanged(
        "large turn delta reaches L2",
        turn.Advance(true, false, 1_000_000.0));
    AssertPose("large turn delta stops at L2", turn, TurnDirection.Left, 2);
    AssertNotWalking("large turn delta does not spill into left walk", turn);
    AssertChanged(
        "later call enters left walk after large delta",
        turn.Advance(true, false, 1_000_000.0));
    AssertWalk("large left entry starts at zero", turn, TurnDirection.Left, 0);
    AssertChanged(
        "large left walk delta uses deterministic modulo",
        turn.Advance(
            true,
            false,
            1_000_000.0 + turn.LeftWalkFrameDurationSeconds * 1.5));
    AssertWalk("large left walk endpoint", turn, TurnDirection.Left, 1);

    turn = EnterWalk(TurnDirection.Right);
    AssertChanged(
        "large right walk delta uses deterministic modulo",
        turn.Advance(
            false,
            true,
            1_000_000.0 + turn.RightWalkFrameDurationSeconds * 4.5));
    AssertWalk("large right walk endpoint", turn, TurnDirection.Right, 4);
}

static void RunDialogueCases()
{
    DialogueModel dialogue = new();

    AssertEqual(
        "empty dialogue normalizes empty",
        DialogueLayout.NormalizeText(string.Empty),
        string.Empty);
    AssertEqual(
        "one-character dialogue remains intact",
        DialogueLayout.NormalizeText("x"),
        "x");

    foreach (int length in new[] { 0, 1, 499, 500, 501 })
    {
        DialogueModel boundary = new();
        boundary.HandleKey(DialogueKey.Enter, echo: false);
        DialogueAction boundaryAction = boundary.HandleKey(
            DialogueKey.Enter,
            echo: false,
            new string('x', length));
        int expectedLength = Math.Min(
            length,
            DialogueLayout.MaximumInputCharacters);
        AssertTrue(
            $"dialogue length {length} is bounded to {expectedLength}",
            boundaryAction.Closed
            && boundaryAction.Submitted == (expectedLength > 0)
            && boundary.IsBubbleVisible == (expectedLength > 0)
            && boundary.BubbleText.Length == expectedLength);
    }

    const string supplementaryCharacter = "\U0001F600";
    foreach (
        (
            string name,
            string draft,
            string expected,
            int expectedScalarCount
        ) in new[]
        {
            (
                "500 supplementary characters",
                string.Concat(
                    Enumerable.Repeat(
                        supplementaryCharacter,
                        DialogueLayout.MaximumInputCharacters)),
                string.Concat(
                    Enumerable.Repeat(
                        supplementaryCharacter,
                        DialogueLayout.MaximumInputCharacters)),
                DialogueLayout.MaximumInputCharacters
            ),
            (
                "501 supplementary characters",
                string.Concat(
                    Enumerable.Repeat(
                        supplementaryCharacter,
                        DialogueLayout.MaximumInputCharacters + 1)),
                string.Concat(
                    Enumerable.Repeat(
                        supplementaryCharacter,
                        DialogueLayout.MaximumInputCharacters)),
                DialogueLayout.MaximumInputCharacters
            ),
            (
                "499 ASCII and one supplementary character",
                new string(
                    'x',
                    DialogueLayout.MaximumInputCharacters - 1)
                    + supplementaryCharacter,
                new string(
                    'x',
                    DialogueLayout.MaximumInputCharacters - 1)
                    + supplementaryCharacter,
                DialogueLayout.MaximumInputCharacters
            ),
        }
    )
    {
        DialogueModel boundary = new();
        boundary.HandleKey(DialogueKey.Enter, echo: false);
        DialogueAction boundaryAction = boundary.HandleKey(
            DialogueKey.Enter,
            echo: false,
            draft);
        AssertTrue(
            $"{name} submits successfully",
            boundaryAction.Closed
            && boundaryAction.Submitted
            && boundary.IsBubbleVisible);
        AssertTrue(
            $"{name} retains valid UTF-16",
            IsValidUtf16(boundary.BubbleText));
        AssertTrue(
            $"{name} retains expected Unicode scalars",
            boundary.BubbleText == expected
            && boundary.BubbleText.EnumerateRunes().Count()
                == expectedScalarCount);
    }

    DialogueAction action = dialogue.HandleKey(DialogueKey.Enter, echo: false);
    AssertTrue("Enter closed opens input", action.Opened && dialogue.IsEditing);
    AssertTrue(
        "character input suppressed while editing",
        dialogue.SuppressCharacterInput);

    action = dialogue.HandleKey(
        DialogueKey.Enter,
        echo: false,
        "  Hello   brave   world!  ");
    AssertTrue(
        "Enter open submits normalized non-empty text",
        action.Submitted
        && !dialogue.IsEditing
        && dialogue.IsBubbleVisible
        && dialogue.BubbleText == "Hello brave world!");
    AssertTrue(
        "character input resumes after submit",
        !dialogue.SuppressCharacterInput);

    dialogue.HandleKey(DialogueKey.Enter, echo: false);
    action = dialogue.HandleKey(DialogueKey.Enter, echo: false, " \t\r\n ");
    AssertTrue(
        "whitespace submit preserves existing bubble",
        action.Closed
        && !action.Submitted
        && dialogue.IsBubbleVisible
        && dialogue.BubbleText == "Hello brave world!");

    dialogue.HandleKey(DialogueKey.Enter, echo: false);
    action = dialogue.HandleKey(DialogueKey.Escape, echo: false, "discard me");
    AssertTrue(
        "Escape closes without changing bubble",
        action.Closed
        && dialogue.BubbleText == "Hello brave world!"
        && dialogue.IsBubbleVisible);

    action = dialogue.HandleKey(DialogueKey.P, echo: false);
    AssertTrue(
        "P closed hides bubble",
        action.Handled
        && action.Hidden
        && !dialogue.IsBubbleVisible
        && dialogue.BubbleText == "Hello brave world!");

    dialogue.HandleKey(DialogueKey.Enter, echo: false);
    action = dialogue.HandleKey(DialogueKey.P, echo: false, "p");
    AssertTrue(
        "P open remains text input and does not hide",
        !action.Handled && dialogue.IsEditing);

    action = dialogue.HandleKey(DialogueKey.Enter, echo: true, "ignored");
    AssertTrue(
        "key echo is ignored",
        !action.Handled
        && dialogue.IsEditing
        && dialogue.BubbleText == "Hello brave world!");
    dialogue.HandleKey(DialogueKey.Escape, echo: false);
}

static void RunActionMessageCases()
{
    ActionMessageLoadResult valid = ActionMessageCatalog.Load(
        Encoding.UTF8.GetBytes(
            """
            {
              "1": "  Welcome   to School 1! ",
              "2": "",
              "3": "   ",
              "4": null,
              "5": 42,
              "6": false,
              "F": "Celebrate!",
              "S": "Finished.",
              "unknown": "ignored"
            }
            """));
    AssertEqual(
        "action messages valid load status",
        valid.Status,
        ActionMessageLoadStatus.Success);
    AssertEqual(
        "action messages normalize display whitespace",
        valid.Catalog.Get("1"),
        "Welcome to School 1!");
    AssertTrue(
        "action messages ignore empty whitespace null and non-string values",
        valid.Catalog.Get("2") is null
        && valid.Catalog.Get("3") is null
        && valid.Catalog.Get("4") is null
        && valid.Catalog.Get("5") is null
        && valid.Catalog.Get("6") is null);
    AssertTrue(
        "action messages preserve F and S",
        valid.Catalog.Get("F") == "Celebrate!"
        && valid.Catalog.Get("S") == "Finished.");
    AssertTrue(
        "action messages ignore unknown keys and missing keys",
        valid.Catalog.Get("unknown") is null
        && ActionMessageCatalog.Load(
            Encoding.UTF8.GetBytes("""{"F":"Only F"}"""))
            .Catalog.Get("1") is null);

    string supplementary = "\U0001F680";
    string oversized =
        string.Concat(
            Enumerable.Repeat(
                supplementary,
                DialogueLayout.MaximumInputCharacters + 1));
    ActionMessageLoadResult bounded = ActionMessageCatalog.Load(
        Encoding.UTF8.GetBytes(
            $"{{\"F\":\"{oversized}\"}}"));
    string boundedMessage = bounded.Catalog.Get("F")!;
    AssertTrue(
        "action messages truncate at 500 Unicode scalars without splitting",
        boundedMessage.EnumerateRunes().Count()
            == DialogueLayout.MaximumInputCharacters
        && IsValidUtf16(boundedMessage));

    ActionMessageLoadResult invalidJson = ActionMessageCatalog.Load(
        Encoding.UTF8.GetBytes("""{"F":"""));
    AssertTrue(
        "action messages report invalid JSON with empty safe catalog",
        invalidJson.Status == ActionMessageLoadStatus.InvalidJson
        && invalidJson.Error is not null
        && invalidJson.Catalog.Get("F") is null);
    ActionMessageLoadResult invalidSchema = ActionMessageCatalog.Load(
        Encoding.UTF8.GetBytes("""["not","an","object"]"""));
    AssertEqual(
        "action messages report invalid root schema",
        invalidSchema.Status,
        ActionMessageLoadStatus.InvalidSchema);
    ActionMessageLoadResult invalidUtf8 = ActionMessageCatalog.Load(
        new byte[] { 0x7B, 0x22, 0x51, 0x22, 0x3A, 0x22, 0xFF, 0x22, 0x7D });
    AssertEqual(
        "action messages report invalid UTF-8",
        invalidUtf8.Status,
        ActionMessageLoadStatus.InvalidUtf8);

    DialogueModel dialogue = new();
    AssertTrue(
        "configured action text shows without opening editor",
        dialogue.ShowActionText("  Action   message  ")
        && dialogue.IsBubbleVisible
        && !dialogue.IsEditing
        && dialogue.BubbleText == "Action message");
    AssertTrue(
        "empty configured action text is ignored without replacing bubble",
        !dialogue.ShowActionText(" \t ")
        && dialogue.IsBubbleVisible
        && dialogue.BubbleText == "Action message");
    AssertTrue(
        "explicit bubble hide preserves stored text",
        dialogue.HideBubble()
        && !dialogue.IsBubbleVisible
        && dialogue.BubbleText == "Action message");
}

static void RunCelebrationCases()
{
    AnimationSafeCenters centers = AnimationGeometry.DefaultSafeCenters;
    double fullSpan = centers.Right - centers.Left;

    CelebrationStateMachine left = new();
    AssertTrue(
        "F starts from left without teleport",
        left.TryStart(
            centers.Left,
            960.0,
            fullSpan,
            existingCrossHold: false)
        && left.Phase == CelebrationPhase.WalkingToCenter
        && left.WalkDirection == TurnDirection.Right
        && left.CharacterX == centers.Left
        && left.IsFireworksActive
        && left.FireworksStartSerial == 1);
    double expectedLeftDuration =
        (960.0 - centers.Left) / fullSpan
        * CelebrationStateMachine.FullSpanWalkDurationSeconds;
    AssertNear(
        "F left distance-scaled duration",
        left.CurrentPhaseDurationSeconds,
        expectedLeftDuration,
        0.000001);
    left.Advance(expectedLeftDuration / 2.0);
    AssertTrue(
        "F left walk advances continuously",
        left.CharacterX > centers.Left
        && left.CharacterX < 960.0
        && left.Phase == CelebrationPhase.WalkingToCenter);
    left.Advance(expectedLeftDuration / 2.0);
    AssertTrue(
        "F left reaches exact center and begins clap",
        left.CharacterX == 960.0
        && left.Phase == CelebrationPhase.Clapping
        && left.CenterArrivalSerial == 1);

    CelebrationStateMachine right = new();
    AssertTrue(
        "F starts from right with left walk",
        right.TryStart(
            centers.Right,
            960.0,
            fullSpan,
            existingCrossHold: false)
        && right.WalkDirection == TurnDirection.Left
        && right.CharacterX == centers.Right);
    AssertNear(
        "F right distance-scaled duration",
        right.CurrentPhaseDurationSeconds,
        (centers.Right - 960.0) / fullSpan
            * CelebrationStateMachine.FullSpanWalkDurationSeconds,
        0.000001);

    CelebrationStateMachine centered = new();
    AssertTrue(
        "F at center continues immediately into clap",
        centered.TryStart(960.0, 960.0, fullSpan, false)
        && centered.Phase == CelebrationPhase.Clapping
        && centered.CenterArrivalSerial == 1);
    for (int step = 0;
        step < DirectionalTurnStateMachine.ClapPlaybackStepCount;
        step++)
    {
        CelebrationStateMachine frameProbe = new();
        frameProbe.TryStart(960.0, 960.0, fullSpan, false);
        frameProbe.Advance(
            step / DirectionalTurnStateMachine.ClapAnimationFps);
        AssertEqual(
            $"celebration clap step {step} uses approved order",
            frameProbe.CurrentAnimationFrame,
            DirectionalTurnStateMachine.GetClapFrameForStep(step));
    }
    CelebrationStateMachine repeatedFrame = new();
    repeatedFrame.TryStart(960.0, 960.0, fullSpan, false);
    repeatedFrame.Advance(
        DirectionalTurnStateMachine.ClapPlaybackStepCount
        / DirectionalTurnStateMachine.ClapAnimationFps);
    AssertEqual(
        "celebration clap repeats approved 15-step sequence",
        repeatedFrame.CurrentAnimationFrame,
        DirectionalTurnStateMachine.GetClapFrameForStep(0));

    CelebrationStateMachine stoppedDuringClap = new();
    stoppedDuringClap.TryStart(960.0, 960.0, fullSpan, false);
    AssertTrue(
        "S stops fireworks and immediately returns to standing",
        stoppedDuringClap.TryStopFireworks()
        && !stoppedDuringClap.IsFireworksActive
        && stoppedDuringClap.Phase == CelebrationPhase.Inactive
        && stoppedDuringClap.CharacterX == 960.0
        && stoppedDuringClap.CharacterAnimation
            == CelebrationCharacterAnimation.Normal);

    CelebrationStateMachine stoppedDuringWalk = new();
    stoppedDuringWalk.TryStart(centers.Left, 960.0, fullSpan, false);
    stoppedDuringWalk.Advance(0.25);
    AssertTrue(
        "S centers a walking Avatar before restoring standing pose",
        stoppedDuringWalk.TryStopFireworks()
        && stoppedDuringWalk.Phase == CelebrationPhase.Inactive
        && stoppedDuringWalk.CharacterX == 960.0);

    centered.Advance(
        CelebrationStateMachine.ClapDurationSeconds - 0.001);
    AssertTrue(
        "celebration remains clapping before exact 30-second boundary",
        centered.Phase == CelebrationPhase.Clapping
        && centered.CurrentAnimationFrame
            == DirectionalTurnStateMachine.GetClapFrameForStep(14));
    centered.Advance(0.001);
    AssertTrue(
        "celebration crosses exactly at 30 seconds on clean front boundary",
        centered.Phase == CelebrationPhase.Crossing
        && centered.CharacterX == 960.0
        && centered.CurrentAnimationFrame == 0);
    centered.Advance(
        DirectionalTurnStateMachine.CrossArmFrameCount
        / DirectionalTurnStateMachine.CrossArmAnimationFps);
    AssertTrue(
        "fireworks remain active through completed cross hold",
        centered.Phase == CelebrationPhase.FireworksHold
        && centered.CurrentAnimationFrame
            == DirectionalTurnStateMachine.CrossArmFrameCount - 1
        && centered.FireworksStartSerial == 1);
    AssertTrue(
        "S stops only active fireworks",
        centered.TryStopFireworks()
        && centered.Phase == CelebrationPhase.Inactive
        && centered.CharacterAnimation
            == CelebrationCharacterAnimation.Normal
        && !centered.TryStopFireworks());
    AssertTrue(
        "F restarts immediately from the restored center standing pose",
        centered.TryStart(960.0, 960.0, fullSpan, false)
        && centered.Phase == CelebrationPhase.Clapping
        && centered.IsFireworksActive
        && centered.FireworksStartSerial == 2);

    CelebrationStateMachine crossedLeft = new();
    AssertTrue(
        "F from crossed left releases before walking",
        crossedLeft.TryStart(
            centers.Left,
            960.0,
            fullSpan,
            existingCrossHold: true)
        && crossedLeft.Phase
            == CelebrationPhase.ReleasingForCelebration
        && crossedLeft.CharacterX == centers.Left);
    crossedLeft.Advance(
        DirectionalTurnStateMachine.CrossArmReleaseFrameCount
        / DirectionalTurnStateMachine.CrossArmReleaseAnimationFps);
    AssertTrue(
        "F crossed release transitions into correctly directed walk",
        crossedLeft.Phase == CelebrationPhase.WalkingToCenter
        && crossedLeft.WalkDirection == TurnDirection.Right
        && crossedLeft.CharacterX == centers.Left);

    CelebrationStateMachine stoppedSnapshot = new();
    stoppedSnapshot.SetDevelopmentSnapshot(
        CelebrationSnapshot.StoppedWithMessage,
        960.0);
    AssertTrue(
        "stopped capture uses centered inactive standing pose",
        stoppedSnapshot.Phase == CelebrationPhase.Inactive
        && stoppedSnapshot.CharacterX == 960.0
        && stoppedSnapshot.CharacterAnimation
            == CelebrationCharacterAnimation.Normal
        && !stoppedSnapshot.TryReleaseStoppedHold());

    SchoolSceneStateMachine school = new();
    school.SetDevelopmentSnapshot(
        3,
        SchoolLayout(3),
        SchoolSceneSnapshot.Clap);
    school.CancelToBlack(0.42);
    AssertTrue(
        "school cancellation clears selection background and choreography",
        school.Phase == SchoolScenePhase.NormalBlack
        && school.SelectedSchoolNumber is null
        && school.SelectedBackgroundLayout is null
        && !school.IsSchoolVisible
        && Math.Abs(school.CharacterProgress - 0.42) < 0.000001);

    SchoolSceneStateMachine entryMessageTiming = new();
    entryMessageTiming.SetDevelopmentSnapshot(
        1,
        SchoolLayout(1),
        SchoolSceneSnapshot.EntryStart);
    entryMessageTiming.Advance(
        SchoolSceneStateMachine.EntryDurationSeconds - 0.001);
    AssertTrue(
        "school action message event does not fire before right endpoint",
        !entryMessageTiming.EntryEndpointReachedOnLastAdvance);
    entryMessageTiming.Advance(0.001);
    AssertTrue(
        "school action message event fires at entry completion",
        entryMessageTiming.EntryEndpointReachedOnLastAdvance
        && entryMessageTiming.Phase == SchoolScenePhase.Clapping
        && entryMessageTiming.CharacterProgress == 1.0);
    entryMessageTiming.Advance(0.001);
    AssertTrue(
        "school action message event is one advance only",
        !entryMessageTiming.EntryEndpointReachedOnLastAdvance);
}

static void RunFireworksCases()
{
    FireworksSimulation first = new();
    FireworksSimulation second = new();
    first.Start(12345);
    second.Start(12345);
    first.Advance(2.4);
    second.Advance(2.4);
    FireworkBurst[] firstSnapshot = first.Snapshot().ToArray();
    FireworkBurst[] secondSnapshot = second.Snapshot().ToArray();
    AssertTrue(
        "fireworks use deterministic seeded simulation",
        firstSnapshot.SequenceEqual(secondSnapshot));
    AssertTrue(
        "fireworks bursts stay bounded and include the Avatar center zone",
        firstSnapshot.Length is > 0 and <= FireworksSimulation.MaximumBurstCount
        && firstSnapshot.All(
            burst =>
                burst.Center.Y is >= 115.0f and <= 505.0f
                && burst.Center.X is >= 150.0f and <= 1770.0f
                && burst.RayCount
                    is >= FireworksSimulation.MinimumRayCount
                    and <= FireworksSimulation.MaximumRayCount)
        && firstSnapshot.Any(
            burst => burst.Center.X is > 760.0f and < 1160.0f));
    FireworksSimulation different = new();
    different.Start(54321);
    different.Advance(2.4);
    AssertTrue(
        "fireworks seed changes burst geometry",
        !firstSnapshot.SequenceEqual(different.Snapshot()));
    first.Stop();
    AssertTrue(
        "fireworks stop clears all bursts immediately",
        !first.IsActive
        && first.ActiveBurstCount == 0
        && first.Snapshot().Count == 0);
    AssertThrows<ArgumentOutOfRangeException>(
        "fireworks reject invalid delta",
        () => second.Advance(double.NaN));
}

static void RunLogoRainCases()
{
    LogoRainSimulation first = new();
    LogoRainSimulation second = new();
    first.Start(12345);
    second.Start(12345);
    first.Advance(3.2);
    second.Advance(3.2);
    LogoDrop[] firstSnapshot = first.Snapshot().ToArray();
    AssertTrue(
        "logo rain uses deterministic random placement",
        firstSnapshot.SequenceEqual(second.Snapshot()));
    AssertTrue(
        "logo rain keeps small drops inside bounded geometry",
        firstSnapshot.Length is > 0 and <= LogoRainSimulation.MaximumDropCount
        && firstSnapshot.All(
            drop =>
                drop.X is >= -120.0f and <= 2040.0f
                && drop.Y <= 1200.0f
                && drop.FallSpeed is >= 145.0f and <= 290.0f
                && drop.Scale is >= 0.62f and <= 1.0f));
    AssertTrue(
        "logo rain guarantees randomized drops through the Avatar corridor",
        firstSnapshot.Any(
            drop =>
                drop.X is >= 820.0f and <= 1100.0f
                && drop.Y is >= 0.0f and <= 1080.0f));
    first.Advance(LogoRainSimulation.DurationSeconds - 3.201);
    AssertTrue(
        "logo rain remains active immediately before ten seconds",
        first.IsActive && first.IsSpawning);
    first.Advance(0.001);
    AssertTrue(
        "logo rain stops spawning at ten seconds but visible drops keep falling",
        first.IsActive
        && !first.IsSpawning
        && first.ActiveDropCount > 0);
    LogoDrop lastSpawned = first.Snapshot()[^1];
    first.Advance(0.25);
    AssertTrue(
        "visible logos continue moving downward after spawning stops",
        first.IsActive
        && first.Snapshot()[^1].Y > lastSpawned.Y);
    first.Advance(10.0);
    AssertTrue(
        "logo rain ends only after every logo falls below the screen",
        !first.IsActive
        && !first.IsSpawning
        && first.ActiveDropCount == 0
        && first.Snapshot().Count == 0);
    first.Start(99);
    first.Advance(0.5);
    first.Start(99);
    AssertTrue(
        "R restarts logo rain from a fresh deterministic sequence",
        first.IsActive
        && first.IsSpawning
        && first.ElapsedSeconds == 0.0
        && first.ActiveDropCount == 14);
    AssertThrows<ArgumentOutOfRangeException>(
        "logo rain rejects invalid delta",
        () => second.Advance(double.NaN));
}

static void RunLegendAndPresentationInputCases()
{
    AssertTrue(
        "effects select transparent Avatar compositing",
        PresentationInputPolicy.ShouldUseTransparentCharacter(
            schoolOverlay: false,
            logoRainActive: true)
        && PresentationInputPolicy.ShouldUseTransparentCharacter(
            schoolOverlay: true,
            logoRainActive: false)
        && PresentationInputPolicy.ShouldUseTransparentCharacter(
            schoolOverlay: false,
            logoRainActive: false,
            neonBackgroundVisible: true)
        && !PresentationInputPolicy.ShouldUseTransparentCharacter(
            schoolOverlay: false,
            logoRainActive: false));
    AssertEqual(
        "legend contains exact active entry count",
        ActionLegendLayout.Entries.Length,
        19);
    AssertTrue(
        "legend accurately lists D E F S R N O I C L and fullscreen controls",
        ActionLegendLayout.Entries.Any(line => line.StartsWith("D "))
        && ActionLegendLayout.Entries.Any(line => line.StartsWith("E "))
        &&
        ActionLegendLayout.Entries.Any(line => line.StartsWith("F "))
        && ActionLegendLayout.Entries.Any(line => line.StartsWith("S "))
        && ActionLegendLayout.Entries.Any(line => line.StartsWith("R "))
        && ActionLegendLayout.Entries.Any(line => line.StartsWith("N "))
        && ActionLegendLayout.Entries.Any(line => line.StartsWith("O "))
        && ActionLegendLayout.Entries.Any(line => line.StartsWith("I "))
        && ActionLegendLayout.Entries.Any(line => line.StartsWith("C "))
        && ActionLegendLayout.Entries.Any(line => line.StartsWith("L "))
        && ActionLegendLayout.Entries.Any(
            line => line.StartsWith("F11 / Alt+Enter")));
    foreach (
        (float width, float height) in new[]
        {
            (1920.0f, 1080.0f),
            (1280.0f, 720.0f),
            (640.0f, 360.0f),
        })
    {
        LegendSize size = ActionLegendLayout.Calculate(width, height);
        AssertTrue(
            $"legend stays inside {width}x{height} viewport",
            size.Width > 0.0f
            && size.Height > 0.0f
            && size.Width
                <= width - ActionLegendLayout.SafeMargin * 2.0f
            && size.Height
                <= height - ActionLegendLayout.SafeMargin * 2.0f);
    }

    PresentationInputDecision l = PresentationInputPolicy.Resolve(
        PresentationKey.L,
        pressed: true,
        echo: false,
        dialogueEditing: false,
        CelebrationPhase.Clapping);
    AssertTrue(
        "L toggles legend during celebration",
        l.ToggleLegend
        && !l.StartCelebration
        && !l.StopFireworks
        && !l.AllowSchoolAction);
    AssertEqual(
        "L echo is ignored",
        PresentationInputPolicy.Resolve(
            PresentationKey.L,
            true,
            echo: true,
            dialogueEditing: false,
            CelebrationPhase.Inactive),
        default);
    AssertTrue(
        "O toggles neon animation during celebration",
        PresentationInputPolicy.Resolve(
            PresentationKey.O,
            true,
            echo: false,
            dialogueEditing: false,
            CelebrationPhase.Clapping).ToggleNeonAnimation);
    AssertEqual(
        "O typed while dialogue editing remains ordinary text",
        PresentationInputPolicy.Resolve(
            PresentationKey.O,
            true,
            echo: false,
            dialogueEditing: true,
            CelebrationPhase.Inactive),
        default);
    AssertTrue(
        "I toggles neon color cycling during celebration",
        PresentationInputPolicy.Resolve(
            PresentationKey.I,
            true,
            echo: false,
            dialogueEditing: false,
            CelebrationPhase.Clapping).ToggleNeonColorCycle);
    AssertEqual(
        "I typed while dialogue editing remains ordinary text",
        PresentationInputPolicy.Resolve(
            PresentationKey.I,
            true,
            echo: false,
            dialogueEditing: true,
            CelebrationPhase.Inactive),
        default);
    AssertTrue(
        "D requests nearest-edge Avatar exit",
        PresentationInputPolicy.Resolve(
            PresentationKey.D,
            true,
            false,
            false,
            CelebrationPhase.Clapping).ExitAvatar);
    AssertTrue(
        "E requests right-side Avatar entry",
        PresentationInputPolicy.Resolve(
            PresentationKey.E,
            true,
            false,
            false,
            CelebrationPhase.Clapping).EnterAvatar);
    AssertEqual(
        "D and E typed while dialogue editing remain ordinary text",
        PresentationInputPolicy.Resolve(
            PresentationKey.D,
            true,
            false,
            dialogueEditing: true,
            CelebrationPhase.Inactive),
        default);
    AssertEqual(
        "E typed while dialogue editing remains ordinary text",
        PresentationInputPolicy.Resolve(
            PresentationKey.E,
            true,
            false,
            dialogueEditing: true,
            CelebrationPhase.Inactive),
        default);
    AssertEqual(
        "L typed while dialogue editing remains ordinary text",
        PresentationInputPolicy.Resolve(
            PresentationKey.L,
            true,
            echo: false,
            dialogueEditing: true,
            CelebrationPhase.Inactive),
        default);
    AssertTrue(
        "N toggles neon background during celebration",
        PresentationInputPolicy.Resolve(
            PresentationKey.N,
            true,
            echo: false,
            dialogueEditing: false,
            CelebrationPhase.Clapping).ToggleNeonBackground);
    AssertEqual(
        "N typed while dialogue editing remains ordinary text",
        PresentationInputPolicy.Resolve(
            PresentationKey.N,
            true,
            echo: false,
            dialogueEditing: true,
            CelebrationPhase.Inactive),
        default);
    AssertEqual(
        "C no longer toggles legend",
        PresentationInputPolicy.Resolve(
            PresentationKey.C,
            true,
            echo: false,
            dialogueEditing: false,
            CelebrationPhase.Inactive),
        default);

    PresentationInputDecision zeroDuringCelebration =
        PresentationInputPolicy.Resolve(
            PresentationKey.Zero,
            true,
            false,
            false,
            CelebrationPhase.Clapping);
    AssertTrue(
        "0 hides bubble but cannot corrupt active celebration",
        zeroDuringCelebration.HideBubble
        && !zeroDuringCelebration.AllowSchoolAction);
    AssertTrue(
        "school keys suppressed throughout active celebration",
        !PresentationInputPolicy.Resolve(
            PresentationKey.School,
            true,
            false,
            false,
            CelebrationPhase.WalkingToCenter).AllowSchoolAction
        && !PresentationInputPolicy.Resolve(
            PresentationKey.School,
            true,
            false,
            false,
            CelebrationPhase.FireworksHold).AllowSchoolAction);
    AssertTrue(
        "S accepted while immediate fireworks are active",
        PresentationInputPolicy.Resolve(
            PresentationKey.S,
            true,
            false,
            false,
            CelebrationPhase.Clapping).StopFireworks
        && !PresentationInputPolicy.Resolve(
            PresentationKey.S,
            true,
            false,
            false,
            CelebrationPhase.StoppedCrossHold).StopFireworks);
    AssertTrue(
        "F ignored while active and accepted after S",
        !PresentationInputPolicy.Resolve(
            PresentationKey.F,
            true,
            false,
            false,
            CelebrationPhase.Crossing).StartCelebration
        && PresentationInputPolicy.Resolve(
            PresentationKey.F,
            true,
            false,
            false,
            CelebrationPhase.StoppedCrossHold).StartCelebration);
    AssertTrue(
        "R starts logo rain independently during celebration",
        PresentationInputPolicy.Resolve(
            PresentationKey.R,
            true,
            false,
            false,
            CelebrationPhase.Clapping).StartLogoRain);
    AssertEqual(
        "R typed while dialogue editing remains ordinary text",
        PresentationInputPolicy.Resolve(
            PresentationKey.R,
            true,
            false,
            dialogueEditing: true,
            CelebrationPhase.Inactive),
        default);
}

static void RunAnimationConfigCases()
{
    AnimationConfig shipped = AnimationConfig.Load(
        File.ReadAllBytes(
            Path.Combine(
                Directory.GetCurrentDirectory(),
                "AnimationConfig.json")));
    AssertEqual(
        "shipped JSON timing configuration matches documented defaults",
        shipped,
        AnimationConfig.Defaults);

    AnimationConfig custom = AnimationConfig.Load(
        Encoding.UTF8.GetBytes(
            """
            {
              "turnFps": 9.0,
              "walkFps": 7.0,
              "clapFps": 10.0,
              "crossArmFps": 11.0,
              "crossArmReleaseFps": 12.0,
              "avatarEntrySeconds": 6.5,
              "avatarExitFullSpanSeconds": 7.5,
              "neonIntensity": 0.65,
              "neonHueCycleSeconds": 9.5,
              "neonPulseSeconds": 2.25,
              "schoolPrepositionSeconds": 4.0,
              "schoolEntrySeconds": 5.0,
              "schoolClapSeconds": 6.0,
              "schoolBackgroundNormalizationSeconds": 7.0,
              "schoolExitSeconds": 8.0,
              "celebrationWalkSeconds": 9.0,
              "celebrationClapSeconds": 20.0,
              "fireworksSpawnIntervalSeconds": 0.4,
              "fireworksBurstSeconds": 1.5,
              "logoRainSpawnSeconds": 12.0,
              "logoRainSpawnIntervalSeconds": 0.2
            }
            """));
    AnimationConfig.Install(custom);
    DirectionalTurnStateMachine configuredTurn =
        new(custom.TurnFps);
    FireworksSimulation configuredFireworks = new();
    configuredFireworks.Start(101);
    LogoRainSimulation configuredLogoRain = new();
    configuredLogoRain.Start(202);
    configuredLogoRain.Advance(0.19);
    int logoCountBeforeConfiguredInterval =
        configuredLogoRain.ActiveDropCount;
    configuredLogoRain.Advance(0.01);
    AssertTrue(
        "JSON timing configuration drives all public timing surfaces",
        configuredTurn.FrameDurationSeconds == 1.0 / 9.0
        && configuredTurn.LeftWalkFrameDurationSeconds == 1.0 / 7.0
        && configuredTurn.ClapFrameDurationSeconds == 1.0 / 10.0
        && configuredTurn.CrossArmFrameDurationSeconds == 1.0 / 11.0
        && configuredTurn.CrossArmReleaseFrameDurationSeconds == 1.0 / 12.0
        && AnimationConfig.Current.AvatarEntrySeconds == 6.5
        && AnimationConfig.Current.AvatarExitFullSpanSeconds == 7.5
        && AnimationConfig.Current.NeonIntensity == 0.65
        && AnimationConfig.Current.NeonHueCycleSeconds == 9.5
        && AnimationConfig.Current.NeonPulseSeconds == 2.25
        && SchoolSceneStateMachine.CharacterPrePositionFullSpanDurationSeconds
            == 4.0
        && SchoolSceneStateMachine.EntryDurationSeconds == 5.0
        && SchoolSceneStateMachine.ClapDurationSeconds == 6.0
        && SchoolSceneStateMachine.BackgroundNormalizationFullSpanDurationSeconds
            == 7.0
        && SchoolSceneStateMachine.ExitTravelDurationSeconds == 8.0
        && CelebrationStateMachine.FullSpanWalkDurationSeconds == 9.0
        && CelebrationStateMachine.ClapDurationSeconds == 20.0
        && configuredFireworks.Snapshot().All(
            burst => burst.DurationSeconds == 1.5f)
        && LogoRainSimulation.DurationSeconds == 12.0
        && configuredLogoRain.ActiveDropCount
            == logoCountBeforeConfiguredInterval + 1);
    AssertThrows<InvalidDataException>(
        "timing configuration rejects missing values",
        () => AnimationConfig.Load(
            Encoding.UTF8.GetBytes("""{"turnFps":8}""")));
    AssertThrows<InvalidDataException>(
        "timing configuration rejects unknown values",
        () => AnimationConfig.Load(
            Encoding.UTF8.GetBytes(
                """
                {
                  "turnFps": 8,
                  "walkFps": 6,
                  "clapFps": 8,
                  "crossArmFps": 8,
                  "crossArmReleaseFps": 8,
                  "avatarEntrySeconds": 6,
                  "avatarExitFullSpanSeconds": 6,
                  "neonIntensity": 0.7,
                  "neonHueCycleSeconds": 8,
                  "neonPulseSeconds": 2.5,
                  "schoolPrepositionSeconds": 6,
                  "schoolEntrySeconds": 8,
                  "schoolClapSeconds": 10,
                  "schoolBackgroundNormalizationSeconds": 8,
                  "schoolExitSeconds": 8,
                  "celebrationWalkSeconds": 6,
                  "celebrationClapSeconds": 30,
                  "fireworksSpawnIntervalSeconds": 0.48,
                  "fireworksBurstSeconds": 1.7,
                  "logoRainSpawnSeconds": 10,
                  "logoRainSpawnIntervalSeconds": 0.12,
                  "unexpected": 1
                }
                """)));
    AnimationConfig.Install(AnimationConfig.Defaults);
}

static bool IsValidUtf16(string text)
{
    for (int index = 0; index < text.Length; index++)
    {
        char current = text[index];
        if (char.IsHighSurrogate(current))
        {
            if (
                index + 1 >= text.Length
                || !char.IsLowSurrogate(text[index + 1])
            )
            {
                return false;
            }

            index++;
        }
        else if (char.IsLowSurrogate(current))
        {
            return false;
        }
    }

    return true;
}

static void RunDialogueLayoutCases()
{
    static float Measure(string text) => text.Length * 18.0f;

    string shortText = DialogueLayout.WrapText(
        "Hi!",
        Measure,
        DialogueLayout.MaximumBodyWidth
            - DialogueLayout.HorizontalPadding * 2.0f,
        DialogueLayout.MaximumLines);
    string mediumText = DialogueLayout.WrapText(
        "A medium sentence should wrap using measured word widths.",
        Measure,
        DialogueLayout.MaximumBodyWidth
            - DialogueLayout.HorizontalPadding * 2.0f,
        DialogueLayout.MaximumLines);
    string longText = DialogueLayout.WrapText(
        string.Join(' ', Enumerable.Repeat("extraordinary", 100)),
        Measure,
        DialogueLayout.MaximumBodyWidth
            - DialogueLayout.HorizontalPadding * 2.0f,
        DialogueLayout.MaximumLines);

    AssertBubbleSize("short text size", shortText, Measure, 42.0f);
    AssertBubbleSize("medium text size", mediumText, Measure, 84.0f);
    AssertBubbleSize("very long text size", longText, Measure, 500.0f);
    AssertTrue(
        "very long text is capped and ellipsized",
        longText.Split('\n').Length == DialogueLayout.MaximumLines
        && longText.EndsWith('…'));

    string oversizedWord = DialogueLayout.WrapText(
        new string('x', 20),
        text => text.Length * 10.0f,
        35.0f,
        10);
    AssertTrue(
        "oversized word is split into bounded segments",
        oversizedWord.Split('\n').All(line => MeasureWidth(line) <= 35.0f));

    List<string> supplementarySplitMeasurements = [];
    string supplementaryOversizedWord = DialogueLayout.WrapText(
        "😀😀",
        text =>
        {
            supplementarySplitMeasurements.Add(text);
            return text.Length * 10.0f;
        },
        10.0f,
        10);
    List<string> supplementaryEllipsisMeasurements = [];
    string supplementaryEllipsis = DialogueLayout.WrapText(
        "ab😀 z",
        text =>
        {
            supplementaryEllipsisMeasurements.Add(text);
            return text.Length * 10.0f;
        },
        40.0f,
        1);
    AssertTrue(
        "supplementary ellipsis preserves Unicode scalar boundaries",
        IsValidUtf16(supplementaryEllipsis)
        && supplementaryEllipsisMeasurements.All(IsValidUtf16));
    AssertTrue(
        "supplementary oversized word preserves Unicode scalar boundaries",
        IsValidUtf16(supplementaryOversizedWord)
        && supplementaryOversizedWord
            .Split('\n')
            .All(line => line.EnumerateRunes().Count() == 1)
        && supplementarySplitMeasurements.All(IsValidUtf16));

    List<string> invalidInputMeasurements = [];
    string sanitizedInvalidInput = DialogueLayout.WrapText(
        "a\uD83Db",
        text =>
        {
            invalidInputMeasurements.Add(text);
            return text.Length * 10.0f;
        },
        100.0f,
        2);
    AssertTrue(
        "invalid UTF-16 input is sanitized before measurement",
        IsValidUtf16(sanitizedInvalidInput)
        && sanitizedInvalidInput.Contains(Rune.ReplacementChar.ToString())
        && invalidInputMeasurements.All(IsValidUtf16));

    AssertThrows<ArgumentNullException>(
        "wrap rejects null width measurement",
        () => DialogueLayout.WrapText("text", null!, 100.0f, 4));
    AssertThrows<ArgumentOutOfRangeException>(
        "wrap rejects zero maximum width",
        () => DialogueLayout.WrapText("text", Measure, 0.0f, 4));
    AssertThrows<ArgumentOutOfRangeException>(
        "wrap rejects nonfinite maximum width",
        () => DialogueLayout.WrapText(
            "text",
            Measure,
            float.PositiveInfinity,
            4));
    AssertThrows<ArgumentOutOfRangeException>(
        "wrap rejects zero maximum lines",
        () => DialogueLayout.WrapText("text", Measure, 100.0f, 0));

    foreach (
        (string name, DialoguePoint anchor) in new[]
        {
            ("center", new DialoguePoint(960.0f, 290.0f)),
            ("left edge", new DialoguePoint(221.25f, 290.0f)),
            ("right edge", new DialoguePoint(1700.0f, 290.0f)),
        }
    )
    {
        BubbleLayoutResult layout = DialogueLayout.PlaceBubble(
            anchor,
            new DialogueSize(680.0f, 238.0f));
        AssertTrue(
            $"{name} bubble stays inside viewport",
            layout.Body.X >= DialogueLayout.SafeMargin
            && layout.Body.Y >= DialogueLayout.SafeMargin
            && layout.Body.Right
                <= DialogueLayout.ViewportWidth - DialogueLayout.SafeMargin
            && layout.Body.Bottom
                <= DialogueLayout.ViewportHeight - DialogueLayout.SafeMargin);
        AssertTrue(
            $"{name} tail target remains character anchor",
            layout.TailTarget == anchor);
    }

    static float MeasureWidth(string text) => text.Length * 10.0f;
}

static void RunCapturePathCases()
{
    string projectRoot = Path.GetFullPath(
        Path.Combine("probe-root", "TCFAnimation"));
    string expectedCapturePath = Path.Combine(
        projectRoot,
        CapturePathPolicy.CaptureDirectoryName,
        "front.png");
    string capturePath = CapturePathPolicy.Resolve(
        projectRoot,
        "front.png",
        _ => false);
    AssertEqual(
        "capture path resolves under project-local directory",
        capturePath,
        expectedCapturePath);
    AssertTrue(
        "capture path accepts case-insensitive PNG extension",
        CapturePathPolicy.Resolve(projectRoot, "front.PNG", _ => false)
            .EndsWith("front.PNG", StringComparison.Ordinal));

    const string stagingToken = "0123456789abcdef0123456789abcdef";
    string stagingPath = CapturePathPolicy.CreateStagingPath(
        projectRoot,
        capturePath,
        stagingToken);
    AssertEqual(
        "capture staging path stays beside final path",
        Path.GetDirectoryName(stagingPath)!,
        Path.GetDirectoryName(capturePath)!);
    AssertEqual(
        "capture staging path uses deterministic safe name",
        Path.GetFileName(stagingPath),
        $".front.png.{stagingToken}{CapturePathPolicy.StagingFileExtension}");

    foreach (
        (string name, string requestedPath) in new[]
        {
            ("empty", string.Empty),
            ("absolute", @"C:\outside.png"),
            ("UNC", @"\\server\share\outside.png"),
            ("device", @"\\?\C:\outside.png"),
            ("parent traversal", @"..\outside.png"),
            ("nested directory", @"nested\outside.png"),
            ("drive-relative", @"C:outside.png"),
            ("reserved device name", "CON.png"),
            ("reserved printer device", "PRN.png"),
            ("reserved auxiliary device", "AUX.png"),
            ("reserved null device", "NUL.png"),
            ("reserved console input", "CONIN$.png"),
            ("reserved console output", "CONOUT$.png"),
            ("reserved clock device", "CLOCK$.png"),
            ("reserved COM1 device", "COM1.png"),
            ("reserved COM9 device", "COM9.png"),
            ("reserved LPT1 device", "LPT1.png"),
            ("reserved LPT9 device", "LPT9.png"),
            ("reserved superscript COM1 device", "COM¹.png"),
            ("reserved superscript COM2 device", "COM².png"),
            ("reserved superscript COM3 device", "COM³.png"),
            ("reserved superscript LPT1 device", "LPT¹.png"),
            ("reserved superscript LPT2 device", "LPT².png"),
            ("reserved superscript LPT3 device", "LPT³.png"),
            ("reserved device with trailing stem space", "CON .png"),
            ("alternate data stream", "front.png:payload"),
            ("trailing space", "front.png "),
            ("trailing dot", "front.png."),
            ("invalid file-name character", "outside?.png"),
            ("non-PNG", "outside.jpg"),
        }
    )
    {
        AssertThrows<ArgumentException>(
            $"capture path rejects {name} input",
            () => CapturePathPolicy.Resolve(
                projectRoot,
                requestedPath,
                _ => false));
    }

    AssertThrows<IOException>(
        "capture path refuses overwrite",
        () => CapturePathPolicy.Resolve(
            projectRoot,
            "existing.png",
            _ => true));

    AssertThrows<ArgumentException>(
        "capture staging rejects final path outside Captures",
        () => CapturePathPolicy.CreateStagingPath(
            projectRoot,
            Path.Combine(projectRoot, "outside.png"),
            stagingToken));
    AssertThrows<ArgumentException>(
        "capture staging rejects unsafe unique token",
        () => CapturePathPolicy.CreateStagingPath(
            projectRoot,
            capturePath,
            @"..\unsafe"));
    AssertThrows<ArgumentException>(
        "capture publication rejects staging name for another final",
        () => CapturePathPolicy.EnsureSafeToPublish(
            projectRoot,
            capturePath,
            CapturePathPolicy.CreateStagingPath(
                projectRoot,
                CapturePathPolicy.Resolve(
                    projectRoot,
                    "other.png",
                    _ => false),
                stagingToken),
            _ => FileAttributes.Normal,
            _ => false));

    List<string> inspectedComponents = [];
    CapturePathPolicy.EnsureNoReparsePoints(
        projectRoot,
        path =>
        {
            inspectedComponents.Add(path);
            return FileAttributes.Directory;
        });
    AssertTrue(
        "capture reparse inspection includes project root and Captures",
        inspectedComponents.Contains(projectRoot)
        && inspectedComponents.Contains(
            CapturePathPolicy.GetCaptureDirectory(projectRoot)));

    AssertThrows<IOException>(
        "capture policy rejects project-root reparse point",
        () => CapturePathPolicy.EnsureNoReparsePoints(
            projectRoot,
            path => string.Equals(
                path,
                projectRoot,
                StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : FileAttributes.Directory));
    string projectParent = Path.GetDirectoryName(projectRoot)!;
    AssertThrows<IOException>(
        "capture policy rejects ancestor reparse point",
        () => CapturePathPolicy.EnsureNoReparsePoints(
            projectRoot,
            path => string.Equals(
                path,
                projectParent,
                StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : FileAttributes.Directory));
    string captureDirectory =
        CapturePathPolicy.GetCaptureDirectory(projectRoot);
    AssertThrows<IOException>(
        "capture policy rejects Captures reparse point",
        () => CapturePathPolicy.EnsureNoReparsePoints(
            projectRoot,
            path => string.Equals(
                path,
                captureDirectory,
                StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : FileAttributes.Directory));

    CapturePathPolicy.EnsureSafeToPublish(
        projectRoot,
        capturePath,
        stagingPath,
        path => string.Equals(
            path,
            stagingPath,
            StringComparison.OrdinalIgnoreCase)
            ? FileAttributes.Normal
            : FileAttributes.Directory,
        _ => false);
    AssertTrue(
        "capture publication accepts regular staging file",
        true);
    AssertThrows<IOException>(
        "capture publication refuses final-file race",
        () => CapturePathPolicy.EnsureSafeToPublish(
            projectRoot,
            capturePath,
            stagingPath,
            path => string.Equals(
                path,
                stagingPath,
                StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Normal
                : FileAttributes.Directory,
            _ => true));
    AssertThrows<IOException>(
        "capture publication rejects staging reparse point",
        () => CapturePathPolicy.EnsureSafeToPublish(
            projectRoot,
            capturePath,
            stagingPath,
            path => string.Equals(
                path,
                stagingPath,
                StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.ReparsePoint
                : FileAttributes.Directory,
            _ => false));
}

static void RunCaptureRootCases()
{
    string developmentRoot = Path.GetFullPath(
        Path.Combine("probe-root", "development"));
    string standaloneRoot = Path.GetFullPath(
        Path.Combine("probe-root", "standalone"));
    string standaloneExecutable = Path.Combine(
        standaloneRoot,
        "TCFAnimation.exe");
    string standaloneManagedRoot = Path.Combine(
        standaloneRoot,
        "data_TCFAnimation_windows_x86_64");
    Func<string, bool> exists = _ => true;
    Func<string, FileAttributes> attributes = path =>
        string.Equals(
            path,
            standaloneExecutable,
            StringComparison.OrdinalIgnoreCase)
            ? FileAttributes.Normal
            : FileAttributes.Directory;

    CaptureRootContext standaloneFeatureContext =
        CaptureRootContext.FromGodotFeatures(
            hasStandaloneFeature: true,
            hasTemplateFeature: false,
            standaloneExecutable,
            standaloneManagedRoot,
            developmentRoot);
    AssertEqual(
        "Godot standalone feature selects standalone context",
        standaloneFeatureContext.IsStandalone,
        true);
    CaptureRootContext templateFeatureContext =
        CaptureRootContext.FromGodotFeatures(
            hasStandaloneFeature: false,
            hasTemplateFeature: true,
            standaloneExecutable,
            standaloneManagedRoot,
            developmentRoot);
    AssertEqual(
        "Godot 4 template feature selects standalone context",
        templateFeatureContext.IsStandalone,
        true);
    AssertEqual(
        "Godot editor feature set selects project context",
        CaptureRootContext.FromGodotFeatures(
            hasStandaloneFeature: false,
            hasTemplateFeature: false,
            standaloneExecutable,
            standaloneRoot,
            developmentRoot).IsStandalone,
        false);

    AssertEqual(
        "development capture root uses globalized res filesystem root",
        CaptureRootResolver.ResolveRoot(
            new CaptureRootContext(
                IsStandalone: false,
                ExecutablePath: standaloneExecutable,
                ApplicationBaseDirectory: standaloneRoot,
                ProjectResourceRoot: developmentRoot),
            exists,
            exists,
            attributes).Root,
        Path.TrimEndingDirectorySeparator(developmentRoot));
    AssertEqual(
        "development capture root kind is project resource",
        CaptureRootResolver.ResolveRoot(
            new CaptureRootContext(
                IsStandalone: false,
                ExecutablePath: standaloneExecutable,
                ApplicationBaseDirectory: standaloneRoot,
                ProjectResourceRoot: developmentRoot),
            exists,
            exists,
            attributes).Kind,
        CaptureRootResolver.ProjectResourceKind);
    AssertEqual(
        "standalone capture root ignores nonempty project resource root",
        CaptureRootResolver.ResolveRoot(
            new CaptureRootContext(
                IsStandalone: true,
                ExecutablePath: standaloneExecutable,
                ApplicationBaseDirectory: standaloneManagedRoot,
                ProjectResourceRoot: developmentRoot),
            exists,
            exists,
            attributes).Root,
        Path.TrimEndingDirectorySeparator(standaloneRoot));
    AssertEqual(
        "standalone capture root kind is executable adjacent",
        CaptureRootResolver.ResolveRoot(
            new CaptureRootContext(
                IsStandalone: true,
                ExecutablePath: standaloneExecutable,
                ApplicationBaseDirectory: standaloneManagedRoot,
                ProjectResourceRoot: developmentRoot),
            exists,
            exists,
            attributes).Kind,
        CaptureRootResolver.ExecutableAdjacentKind);
    AssertEqual(
        "standalone capture root accepts direct executable base directory",
        CaptureRootResolver.ResolveRoot(
            new CaptureRootContext(
                IsStandalone: true,
                ExecutablePath: standaloneExecutable,
                ApplicationBaseDirectory: standaloneRoot,
                ProjectResourceRoot: developmentRoot),
            exists,
            exists,
            attributes).Root,
        Path.TrimEndingDirectorySeparator(standaloneRoot));

    AssertThrows<ArgumentException>(
        "standalone capture root rejects empty executable base",
        () => CaptureRootResolver.ResolveRoot(
            new CaptureRootContext(
                IsStandalone: true,
                ExecutablePath: standaloneExecutable,
                ApplicationBaseDirectory: string.Empty,
                ProjectResourceRoot: developmentRoot),
            exists,
            exists,
            attributes));
    AssertThrows<ArgumentException>(
        "capture root rejects relative filesystem path",
        () => CaptureRootResolver.ResolveRoot(
            new CaptureRootContext(
                IsStandalone: false,
                ExecutablePath: standaloneExecutable,
                ApplicationBaseDirectory: standaloneRoot,
                ProjectResourceRoot: "relative-root"),
            exists,
            exists,
            attributes));
    AssertThrows<DirectoryNotFoundException>(
        "capture root rejects missing selected directory without fallback",
        () => CaptureRootResolver.ResolveRoot(
            new CaptureRootContext(
                IsStandalone: true,
                ExecutablePath: standaloneExecutable,
                ApplicationBaseDirectory: standaloneRoot,
                ProjectResourceRoot: developmentRoot),
            _ => false,
            exists,
            attributes));
    AssertThrows<IOException>(
        "capture root rejects selected root reparse point",
        () => CaptureRootResolver.ResolveRoot(
            new CaptureRootContext(
                IsStandalone: true,
                ExecutablePath: standaloneExecutable,
                ApplicationBaseDirectory: standaloneRoot,
                ProjectResourceRoot: developmentRoot),
            exists,
            exists,
            path => string.Equals(
                path,
                standaloneRoot,
                StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : attributes(path)));
    AssertThrows<IOException>(
        "capture root rejects selected root that is not a directory",
        () => CaptureRootResolver.ResolveRoot(
            new CaptureRootContext(
                IsStandalone: false,
                ExecutablePath: standaloneExecutable,
                ApplicationBaseDirectory: standaloneRoot,
                ProjectResourceRoot: developmentRoot),
            exists,
            exists,
            _ => FileAttributes.Normal));
    AssertThrows<FileNotFoundException>(
        "standalone capture root rejects missing process executable",
        () => CaptureRootResolver.ResolveRoot(
            new CaptureRootContext(
                IsStandalone: true,
                ExecutablePath: standaloneExecutable,
                ApplicationBaseDirectory: standaloneManagedRoot,
                ProjectResourceRoot: developmentRoot),
            exists,
            _ => false,
            attributes));
    AssertThrows<IOException>(
        "standalone capture root rejects unexpected managed layout",
        () => CaptureRootResolver.ResolveRoot(
            new CaptureRootContext(
                IsStandalone: true,
                ExecutablePath: standaloneExecutable,
                ApplicationBaseDirectory: Path.Combine(
                    standaloneRoot,
                    "unexpected"),
                ProjectResourceRoot: developmentRoot),
            exists,
            exists,
            attributes));
}

static void AssertBubbleSize(
    string stepName,
    string wrappedText,
    Func<string, float> measure,
    float measuredHeight)
{
    float widestLine = wrappedText
        .Split('\n')
        .Select(measure)
        .DefaultIfEmpty(0.0f)
        .Max();
    DialogueSize size = DialogueLayout.CalculateBodySize(
        new DialogueSize(widestLine, measuredHeight));
    bool isWithinBounds =
        size.Width >= DialogueLayout.MinimumBodyWidth
        && size.Width <= DialogueLayout.MaximumBodyWidth
        && size.Height >= DialogueLayout.MinimumBodyHeight
        && size.Height <= DialogueLayout.MaximumBodyHeight;
    if (!isWithinBounds)
    {
        throw new InvalidOperationException(
            $"{stepName}: bubble size {size.Width}x{size.Height} was outside "
            + "the supported layout bounds.");
    }

    ProbeAssertions.RecordSuccess();
    Console.WriteLine(
        $"PASS {stepName}: size={size.Width}x{size.Height}");
}

static DirectionalTurnStateMachine NewState()
{
    return new DirectionalTurnStateMachine(TurnFps);
}

static DirectionalTurnStateMachine EnterWalk(TurnDirection direction)
{
    DirectionalTurnStateMachine turn = NewState();
    bool leftHeld = direction == TurnDirection.Left;
    bool rightHeld = direction == TurnDirection.Right;

    if (direction == TurnDirection.Right)
    {
        turn.Advance(leftHeld, rightHeld, 0.0);
        AssertPose("right walk setup selects R0", turn, TurnDirection.Right, 0);
    }

    turn.Advance(leftHeld, rightHeld, turn.FrameDurationSeconds);
    turn.Advance(leftHeld, rightHeld, turn.FrameDurationSeconds);
    AssertPose(
        $"{direction} walk setup renders full turn",
        turn,
        direction,
        DirectionalTurnStateMachine.FullTurnFrame);
    AssertNotWalking($"{direction} setup does not spill into walking", turn);
    turn.Advance(leftHeld, rightHeld, 0.0);
    AssertWalk($"{direction} setup enters frame zero", turn, direction, 0);
    return turn;
}

static void AssertPose(
    string stepName,
    DirectionalTurnStateMachine state,
    TurnDirection expectedDirection,
    int expectedFrame)
{
    if (
        state.IsWalking
        || state.IsClapping
        || state.IsCrossArmActive
        || state.CurrentDirection != expectedDirection
        || state.CurrentFrame != expectedFrame
    )
    {
        throw new InvalidOperationException(
            $"{stepName}: expected non-walking {expectedDirection} "
            + $"frame {expectedFrame}, actual direction={state.CurrentDirection} "
            + $"frame={state.CurrentFrame} walkingLeft={state.IsWalkingLeft} "
            + $"walkingRight={state.IsWalkingRight} "
            + $"clapping={state.IsClapping} "
            + $"crossArmState={state.CrossArmState}");
    }

    ProbeAssertions.RecordSuccess();
    Console.WriteLine(
        $"PASS {stepName}: direction={state.CurrentDirection} "
        + $"frame={state.CurrentFrame}");
}

static void AssertWalk(
    string stepName,
    DirectionalTurnStateMachine state,
    TurnDirection expectedDirection,
    int expectedWalkFrame)
{
    bool expectedWalkingDirection = expectedDirection == TurnDirection.Left
        ? state.IsWalkingLeft && !state.IsWalkingRight
        : state.IsWalkingRight && !state.IsWalkingLeft;

    if (
        !state.IsWalking
        || !expectedWalkingDirection
        || state.ActiveGesture != FrontGesture.None
        || state.IsCrossArmActive
        || state.CurrentDirection != expectedDirection
        || state.CurrentFrame != DirectionalTurnStateMachine.FullTurnFrame
        || state.CurrentWalkFrame != expectedWalkFrame
    )
    {
        throw new InvalidOperationException(
            $"{stepName}: expected walking-{expectedDirection} "
            + $"frame {expectedWalkFrame}, actual direction={state.CurrentDirection} "
            + $"turnFrame={state.CurrentFrame} walkingLeft={state.IsWalkingLeft} "
            + $"walkingRight={state.IsWalkingRight} "
            + $"walkFrame={state.CurrentWalkFrame}");
    }

    ProbeAssertions.RecordSuccess();
    Console.WriteLine(
        $"PASS {stepName}: direction={state.CurrentDirection} "
        + $"walkFrame={state.CurrentWalkFrame}");
}

static void AssertClap(
    string stepName,
    DirectionalTurnStateMachine state,
    int expectedStep,
    int expectedSourceFrame)
{
    if (
        !state.IsClapping
        || state.IsCrossArmActive
        || state.IsWalking
        || state.CurrentFrame != DirectionalTurnStateMachine.FrontFrame
        || state.CurrentGestureStep != expectedStep
        || state.CurrentClapFrame != expectedSourceFrame
    )
    {
        throw new InvalidOperationException(
            $"{stepName}: expected clap step {expectedStep} source frame "
            + $"{expectedSourceFrame}, actual clapping={state.IsClapping} "
            + $"step={state.CurrentGestureStep} "
            + $"sourceFrame={state.CurrentClapFrame} "
            + $"turnFrame={state.CurrentFrame} walking={state.IsWalking}");
    }

    ProbeAssertions.RecordSuccess();
    Console.WriteLine(
        $"PASS {stepName}: clapStep={state.CurrentGestureStep} "
        + $"sourceFrame={state.CurrentClapFrame}");
}

static void AssertCrossing(
    string stepName,
    DirectionalTurnStateMachine state,
    int expectedFrame)
{
    if (
        !state.IsCrossingArms
        || state.IsCrossArmsHeld
        || state.IsReleasingCrossArms
        || state.IsWalking
        || state.ActiveGesture != FrontGesture.None
        || state.CurrentFrame != DirectionalTurnStateMachine.FrontFrame
        || state.CurrentCrossArmStep != expectedFrame
        || state.CurrentCrossArmFrame != expectedFrame
    )
    {
        throw new InvalidOperationException(
            $"{stepName}: expected crossing frame {expectedFrame}, "
            + $"actual state={state.CrossArmState} "
            + $"step={state.CurrentCrossArmStep} "
            + $"frame={state.CurrentCrossArmFrame} "
            + $"turnFrame={state.CurrentFrame}");
    }

    ProbeAssertions.RecordSuccess();
    Console.WriteLine(
        $"PASS {stepName}: crossArmFrame={state.CurrentCrossArmFrame}");
}

static void AssertCrossedHold(
    string stepName,
    DirectionalTurnStateMachine state)
{
    int expectedFrame = DirectionalTurnStateMachine.CrossArmFrameCount - 1;
    if (expectedFrame != 2)
    {
        throw new InvalidOperationException(
            "persistent crossed-arm hold must use cross_02.");
    }

    if (
        !state.IsCrossArmsHeld
        || state.IsCrossingArms
        || state.IsReleasingCrossArms
        || state.IsWalking
        || state.ActiveGesture != FrontGesture.None
        || state.CurrentFrame != DirectionalTurnStateMachine.FrontFrame
        || state.CurrentCrossArmStep != expectedFrame
        || state.CurrentCrossArmFrame != expectedFrame
    )
    {
        throw new InvalidOperationException(
            $"{stepName}: expected held cross-arm frame {expectedFrame}, "
            + $"actual state={state.CrossArmState} "
            + $"step={state.CurrentCrossArmStep} "
            + $"frame={state.CurrentCrossArmFrame}");
    }

    ProbeAssertions.RecordSuccess();
    Console.WriteLine(
        $"PASS {stepName}: crossArmHoldFrame={state.CurrentCrossArmFrame}");
}

static void AssertReleasing(
    string stepName,
    DirectionalTurnStateMachine state,
    int expectedFrame)
{
    if (
        !state.IsReleasingCrossArms
        || state.IsCrossingArms
        || state.IsCrossArmsHeld
        || state.IsWalking
        || state.ActiveGesture != FrontGesture.None
        || state.CurrentFrame != DirectionalTurnStateMachine.FrontFrame
        || state.CurrentCrossArmStep != expectedFrame
        || state.CurrentCrossArmReleaseFrame != expectedFrame
    )
    {
        throw new InvalidOperationException(
            $"{stepName}: expected release frame {expectedFrame}, "
            + $"actual state={state.CrossArmState} "
            + $"step={state.CurrentCrossArmStep} "
            + $"frame={state.CurrentCrossArmReleaseFrame} "
            + $"turnFrame={state.CurrentFrame}");
    }

    ProbeAssertions.RecordSuccess();
    Console.WriteLine(
        $"PASS {stepName}: crossArmReleaseFrame="
        + $"{state.CurrentCrossArmReleaseFrame}");
}

static void AssertWalkReset(
    string stepName,
    DirectionalTurnStateMachine state)
{
    if (state.IsWalking || state.CurrentWalkFrame != 0)
    {
        throw new InvalidOperationException(
            $"{stepName}: expected stopped frame zero, "
            + $"actual walkingLeft={state.IsWalkingLeft} "
            + $"walkingRight={state.IsWalkingRight} "
            + $"walkFrame={state.CurrentWalkFrame}");
    }

    ProbeAssertions.RecordSuccess();
    Console.WriteLine($"PASS {stepName}");
}

static void AssertNotWalking(
    string stepName,
    DirectionalTurnStateMachine state)
{
    if (state.IsWalking || state.IsWalkingLeft || state.IsWalkingRight)
    {
        throw new InvalidOperationException($"{stepName}: unexpectedly walking.");
    }

    ProbeAssertions.RecordSuccess();
    Console.WriteLine($"PASS {stepName}");
}

static void AssertChanged(
    string stepName,
    bool actual,
    bool expected = true)
{
    if (actual != expected)
    {
        throw new InvalidOperationException(
            $"{stepName}: expected changed={expected}, actual={actual}");
    }

    ProbeAssertions.RecordSuccess();
    Console.WriteLine($"PASS {stepName}: changed={actual}");
}

static void AssertTrue(string stepName, bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException($"{stepName}: condition was false.");
    }

    ProbeAssertions.RecordSuccess();
    Console.WriteLine($"PASS {stepName}");
}

static void AssertEqual<T>(string stepName, T actual, T expected)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected))
    {
        throw new InvalidOperationException(
            $"{stepName}: expected {expected}, actual {actual}");
    }

    ProbeAssertions.RecordSuccess();
    Console.WriteLine($"PASS {stepName}: value={actual}");
}

static void AssertNear(
    string stepName,
    double actual,
    double expected,
    double tolerance)
{
    if (
        !double.IsFinite(actual)
        || !double.IsFinite(expected)
        || !double.IsFinite(tolerance)
        || tolerance < 0.0
        || Math.Abs(actual - expected) > tolerance
    )
    {
        throw new InvalidOperationException(
            $"{stepName}: expected {expected} +/- {tolerance}, "
            + $"actual {actual}");
    }

    ProbeAssertions.RecordSuccess();
    Console.WriteLine($"PASS {stepName}: value={actual}");
}

static void AssertThrows<TException>(string stepName, Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        ProbeAssertions.RecordSuccess();
        Console.WriteLine($"PASS {stepName}: threw {typeof(TException).Name}");
        return;
    }

    throw new InvalidOperationException(
        $"{stepName}: expected {typeof(TException).Name}");
}

static class ProbeAssertions
{
    public static int Count { get; private set; }

    public static void RecordSuccess()
    {
        Count++;
    }
}
