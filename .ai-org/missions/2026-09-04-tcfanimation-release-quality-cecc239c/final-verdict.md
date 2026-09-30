# Final Engineering Verdict

MISSION:              Fix the school-transition neon blackout: keys 1-6 must retain the enabled neon as the background beneath the scrolling school while the Avatar walks left then right; black is permitted only when neon is explicitly disabled; complete test and review gates.

REQUIREMENTS:         PASS
- Keys 1-6 choreography remains implemented by `SchoolSceneStateMachine`; the scoped fix does not alter movement timing or school selection.
- Neon enabled: `NeonLogoBackground.IsShowing` now equals explicit `IsEnabled`; `TurnController.UpdateSchoolVisuals` no longer couples neon visibility to any school sprite visibility state.
- Correct compositing: runtime sets neon `ZIndex=-3` and school `ZIndex=-2`, so every offscreen/partial school transition exposes enabled neon beneath it.
- Neon disabled: `Toggle()` clears `IsEnabled`, `UpdateVisibility()` hides the neon, and uncovered stage regions remain black.
- Coverage evidence: independent graphical testing reports 132/132 captures, 66/66 neon on/off pairs, all schools 1-6 at 11 snapshots each, and 1,007/1,007 assertions/cases. Independent QA reports 6/6 scenarios, 36 captures, and 14/14 pixel comparisons, including initial blank and N/O/I independence.

IMPLEMENTATION:       PASS
- Independently inspected the complete three-file diff. `NeonLogoBackground` removes `_blackStageVisible` and `SetBlackStageVisible`; `TurnController` removes the school-visibility gate. README accurately documents the resulting layer behavior.
- Runtime smoke now constructs school 1 at `EntryStart` and requires both the school sprite and enabled neon to be visible, then confirms explicit neon-off state.
- No TODO, stub, disabled check, deleted test, weakened security control, or residual reference to the removed coupling was found. Scoped `git diff --check` passed.

TESTS:                PASS
- Judge rerun: `.\build.bat` exited 0 with all 8/8 stages exactly once. Debug and Release each produced 0 warnings and 0 errors.
- Judge-confirmed output: 940 PASS lines and `CONTROLLER_PROBE_PASS assertions=940`; package validator printed `ASSET_RELEASE_CHECK_PASS frames=33 school_overlays=33 backgrounds=6 sources=12 read_only=true export_resources=77 artifact_manifest=checked_if_present`; exported smoke printed `RUNTIME_SMOKE_PASS ... neon_background=true`; final marker `BUILD_ALL_PASS`.
- Fresh artifacts: EXE 117,428,200 bytes, SHA-256 `019CD018B7AA74D7C0EBE893E9502DA563C368FE589F74DD557CF105092B1EE5`; DLL 182,784 bytes, SHA-256 `B4DF5F79CC2CF8E60B05CAEFBE11D334716198A08B53974EFD833DDE152BE079`.
- Supplemental independent graphical and QA counts above report zero failures.

SECURITY:             N/A
- This scoped change only removes an internal render-visibility coupling. It adds no authentication, secret, network, persistence, dependency, filesystem, or external-input trust boundary. Existing security controls are untouched.

CODE REVIEW:          PASS
- Independent review is reported APPROVED with no findings; judge inspection agrees that explicit neon state is now the sole visibility authority and z-order provides the required composition.

E2E:                  PASS
- Exported runtime smoke passed from the fresh build. Independent rendered-pixel E2E covers every school, transition snapshots, neon enabled/disabled pairs, initial blank behavior, and control independence with zero failures.

DEFINITION OF DONE:   PASS
1. Root cause removed without changing requested choreography: PASS.
2. Enabled neon remains beneath full, partial, and offscreen school states for schools 1-6: PASS — code/layer proof plus 66 transition snapshot pairs.
3. Explicit neon-off state exposes black and initial blank remains correct: PASS — pixel comparisons and toggle/runtime-smoke checks.
4. Debug/Release, controller probe, package validation, and exported smoke: PASS — judge reran the complete 8-stage command.
5. Independent test, code-review, and QA phases: PASS — all report zero failures/blockers.
6. Documentation and working-tree constraints: PASS — README matches behavior; only the three intended tracked files are modified; no commit or push was performed.

RISKS:                Rendered E2E was exercised on the available Windows/Godot environment; untested GPU/driver combinations remain a low residual rendering risk. The fix remains uncommitted by explicit request.

REMAINING WORK:       none

FINAL: APPROVED
