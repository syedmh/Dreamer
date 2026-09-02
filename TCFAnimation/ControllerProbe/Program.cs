using TCFAnimation;

const double Fps = 8.0;
DirectionalTurnStateMachine turn = new(Fps);
double step = turn.FrameDurationSeconds;

AssertPose("start uses left front", turn, TurnDirection.Left, 0);

turn.Advance(leftHeld: true, rightHeld: false, deltaSeconds: step);
AssertPose("hold-left first step", turn, TurnDirection.Left, 1);

turn.Advance(leftHeld: true, rightHeld: false, deltaSeconds: step);
AssertPose("hold-left second step", turn, TurnDirection.Left, 2);

turn.Advance(leftHeld: true, rightHeld: false, deltaSeconds: step * 4.0);
AssertPose("hold-left remains full-left", turn, TurnDirection.Left, 2);

turn.Advance(leftHeld: false, rightHeld: false, deltaSeconds: step);
AssertPose("release-left first return step", turn, TurnDirection.Left, 1);

turn.Advance(leftHeld: false, rightHeld: false, deltaSeconds: step);
AssertPose("release-left completes on left front", turn, TurnDirection.Left, 0);

turn.Advance(leftHeld: false, rightHeld: true, deltaSeconds: step);
AssertPose("idle switch selects right front first", turn, TurnDirection.Right, 0);

turn.Advance(leftHeld: false, rightHeld: true, deltaSeconds: step);
AssertPose("hold-right first step", turn, TurnDirection.Right, 1);

turn.Advance(leftHeld: false, rightHeld: true, deltaSeconds: step);
AssertPose("hold-right second step", turn, TurnDirection.Right, 2);

turn.Advance(leftHeld: false, rightHeld: true, deltaSeconds: step * 4.0);
AssertPose("hold-right remains full-right", turn, TurnDirection.Right, 2);

turn.Advance(leftHeld: false, rightHeld: false, deltaSeconds: step);
AssertPose("release-right first return step", turn, TurnDirection.Right, 1);

turn.Advance(leftHeld: false, rightHeld: false, deltaSeconds: step);
AssertPose("release-right completes on right front", turn, TurnDirection.Right, 0);

turn.Advance(leftHeld: false, rightHeld: true, deltaSeconds: step);
AssertPose("prepare right half-turn", turn, TurnDirection.Right, 1);

turn.Advance(leftHeld: false, rightHeld: true, deltaSeconds: step);
AssertPose("prepare right full-turn", turn, TurnDirection.Right, 2);

turn.Advance(leftHeld: true, rightHeld: false, deltaSeconds: step);
AssertPose("direct reversal returns through right 45", turn, TurnDirection.Right, 1);

turn.Advance(leftHeld: true, rightHeld: false, deltaSeconds: step);
AssertPose("direct reversal reaches right front", turn, TurnDirection.Right, 0);

turn.Advance(leftHeld: true, rightHeld: false, deltaSeconds: step);
AssertPose("direct reversal switches to left front", turn, TurnDirection.Left, 0);

turn.Advance(leftHeld: true, rightHeld: false, deltaSeconds: step);
AssertPose("direct reversal turns outward to left 45", turn, TurnDirection.Left, 1);

turn.Advance(leftHeld: true, rightHeld: false, deltaSeconds: step);
AssertPose("direct reversal completes full-left", turn, TurnDirection.Left, 2);

turn.Advance(leftHeld: true, rightHeld: true, deltaSeconds: step);
AssertPose("both-held neutral returns through left 45", turn, TurnDirection.Left, 1);

turn.Advance(leftHeld: true, rightHeld: true, deltaSeconds: step);
AssertPose("both-held neutral reaches active front", turn, TurnDirection.Left, 0);

turn.Advance(leftHeld: true, rightHeld: true, deltaSeconds: step * 4.0);
AssertPose("both-held neutral remains active front", turn, TurnDirection.Left, 0);

turn.Advance(leftHeld: true, rightHeld: false, deltaSeconds: step);
AssertPose("prepare left half-turn", turn, TurnDirection.Left, 1);

turn.Advance(leftHeld: true, rightHeld: false, deltaSeconds: step);
AssertPose("prepare left full-turn", turn, TurnDirection.Left, 2);

turn.Advance(leftHeld: false, rightHeld: true, deltaSeconds: step);
AssertPose(
    "opposite direct reversal returns through left 45",
    turn,
    TurnDirection.Left,
    1);

turn.Advance(leftHeld: false, rightHeld: true, deltaSeconds: step);
AssertPose(
    "opposite direct reversal reaches left front",
    turn,
    TurnDirection.Left,
    0);

turn.Advance(leftHeld: false, rightHeld: true, deltaSeconds: step);
AssertPose(
    "opposite direct reversal switches to right front",
    turn,
    TurnDirection.Right,
    0);

turn.Advance(leftHeld: false, rightHeld: true, deltaSeconds: step);
AssertPose(
    "opposite direct reversal turns outward to right 45",
    turn,
    TurnDirection.Right,
    1);

turn.Advance(leftHeld: false, rightHeld: true, deltaSeconds: step);
AssertPose(
    "opposite direct reversal completes full-right",
    turn,
    TurnDirection.Right,
    2);

Console.WriteLine(
    "CONTROLLER_PROBE_PASS "
    + "left=true right=true returns=true direct_reversal_both_ways=true "
    + "both_held_neutral=true fps=8");
return;

static void AssertPose(
    string stepName,
    DirectionalTurnStateMachine state,
    TurnDirection expectedDirection,
    int expectedFrame)
{
    if (
        state.CurrentDirection != expectedDirection
        || state.CurrentFrame != expectedFrame
    )
    {
        throw new InvalidOperationException(
            $"{stepName}: expected {expectedDirection} frame {expectedFrame}, "
            + $"actual {state.CurrentDirection} frame {state.CurrentFrame}");
    }

    Console.WriteLine(
        $"PASS {stepName}: direction={state.CurrentDirection} "
        + $"frame={state.CurrentFrame}");
}
