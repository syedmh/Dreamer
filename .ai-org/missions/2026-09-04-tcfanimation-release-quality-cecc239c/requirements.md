# TCFAnimation Release-Quality Requirements

## Contract

- **REQUIREMENT R-01 — Authoritative baseline:** The mission shall continue from `main` at recovered HEAD `cdcc5d5` plus the existing uncommitted `TCFAnimation` work; it shall not reset, discard, or blindly regenerate that work. The unrelated untracked `Avatar.jpg`, `Clapping.png`, `CrossArm.png`, `CrossArm2.png`, `WalkRightSheet.png`, `Waving*.png`, and sidecars shall not be staged, deleted, edited, or exported.
  - **AC-01 [Working-tree audit]:** GIVEN the final mission tree, WHEN status and scoped diffs are compared with the recovered baseline, THEN intentional in-progress files remain present and the listed unrelated files are unchanged and un-staged.
- **REQUIREMENT R-02 — Scope safety:** Work and evidence shall be limited to `TCFAnimation` and this mission directory. No commit, push, checkout, reset, clean, rebase, or other history mutation shall occur.
  - **AC-02 [Mission audit]:** GIVEN the mission activity log and final repository state, WHEN audited, THEN there is no sibling-project modification, history mutation, commit, or push.
- **FACT F-01:** The current product baseline is Godot 4.5.1 Mono/C#, .NET 8, `Main.tscn`, and a 1920x1080 logical viewport (`TCFAnimation.csproj:1-3`; `project.godot:13-20`; `Main.tscn:3-13`).
- **FACT F-02:** The documented/runtime asset contract is seven groups totaling exactly 33 frames; Waving is intentionally absent (`README.md:43-45,62-77`; `FrameExtraction/validate_release.py:71-91`).
- **FACT F-03:** Current controls, timings, dialogue limits, fullscreen behavior, geometry, source-sheet hashes, validation commands, export policy, capture policy, and smoke outputs are documented in `README.md:19-60,79-154,181-236`.
- **FACT F-04:** The existing validator independently checks the exact frame tree, PNG properties, lower-anatomy negatives/components, deterministic regeneration, walk mirroring/extrema, selected export resources, and built package hygiene (`FrameExtraction/validate_release.py:475-545,551-568,598-624,848-883,886-960,1204-1253`).
- **FACT F-05:** `ControllerProbe` currently prints a named capability summary but not a numeric assertion count (`ControllerProbe/Program.cs:19-33`).

## Functional and release requirements

### Animation and interaction

- **REQUIREMENT R-03 — Exact active resources:** Runtime and release shall load only the 33 frame PNGs: LeftTurn 3, RightTurn 3, LeftWalk 6, RightWalk 6, Clap 6, CrossArm 3, CrossArmRelease 6.
  - **AC-03 [Automated]:** GIVEN the project tree, export whitelist, and built package, WHEN release validation runs, THEN each contains the exact expected frame names/counts, `Main.tscn` is present, and no Waving resource/state/control is present.
- **REQUIREMENT R-04 — Frame format and matte quality:** Every active frame shall be PNG/RGBA, 512x864, fully opaque, with exact RGB `(0,0,0)` outside the character matte. No floor/panel/shadow/reflection/label/divider/halo/detached fragment/matte bite/hole/clipping/artificial charcoal plateau/unnatural gray footwear may remain.
  - **AC-04A [Automated]:** GIVEN all 33 frames, WHEN `python -B FrameExtraction\validate_release.py` runs, THEN it rejects any wrong format, dimensions, alpha, non-black background, forbidden lower component/floor run, matte overrun, detached anatomy, artificial charcoal plateau, or failed negative control.
  - **AC-04B [Manual/visual]:** GIVEN every frame at runtime display size and zoom, WHEN reviewed, THEN none of the listed artifacts is visible and findings identify the frame and defect category.
- **REQUIREMENT R-05 — Direction and walking:** Left/Right shall animate front→45°→90° at 8 FPS, then walk in the held direction using six frames at 6 FPS at `1.0x`; release/reversal shall return through the turn sequence; simultaneous Left+Right shall be neutral. Walking shall stop at safe centers x=227.5/1693.75 without visible clipping.
  - **AC-05 [Automated + E2E]:** GIVEN each directional input sequence, WHEN exercised at front, half-turn, side, walk, reversal, simultaneous-key, and viewport-edge boundaries, THEN state/frame order and edge centers match the contract and the exported character remains fully visible.
- **REQUIREMENT R-06 — Speed control:** `+`/`-` (including keypad equivalents) shall change movement and walk playback together by `0.25x`, clamp to `0.25x..3.0x`, produce 240 design px/s and 6 FPS at `1.0x`, and shall not change the 8 FPS turn timing.
  - **AC-06 [Automated + E2E]:** GIVEN values below, within, and above the limits, WHEN speed keys are pressed, THEN the multiplier clamps correctly and movement/FPS scale together while turn timing remains fixed.
- **REQUIREMENT R-07 — Clap:** `C` shall start only from front idle; assets shall play at 8 FPS in the 15-step order `0,1,2,3,4,3,2,1,2,3,4,3,2,1,0`; directional input shall interrupt it.
  - **AC-07 [Automated + E2E]:** GIVEN idle, turning, walking, cross-arm, and active-clap states, WHEN `C` or directional input is applied, THEN clap starts only when allowed, follows the exact order, and interruption returns control without a stale gesture frame.
- **REQUIREMENT R-08 — Cross/hold/release:** `X` from front idle shall play `cross_00..02` at 8 FPS, hold `cross_02`, then on the next valid `X` play `release_00..05` at 8 FPS and return to the same directional front pose. Clap and directional input shall be blocked during crossing/hold/release, and held arrows shall not be queued.
  - **AC-08 [Automated + E2E]:** GIVEN every entry, hold, release, interruption, reset, and held-arrow boundary, WHEN inputs are applied, THEN the exact frame/state sequence occurs and movement resumes only after arrows have been released.
- **REQUIREMENT R-09 — Dialogue:** `Enter` shall open input; `Enter` shall submit; `Escape` shall cancel. Input shall be limited to 500 Unicode scalar values without splitting surrogate pairs, whitespace-normalized, wrapped to at most four lines with ellipsis when needed, and clamped inside the 1920x1080 viewport while its tail follows the character. Empty normalized submissions shall close without showing a new bubble. `P` shall hide a visible bubble only while input is closed.
  - **AC-09 [Automated + E2E + visual]:** GIVEN empty, whitespace, 500/501-scalar, supplementary-Unicode, oversized-word, multi-line, and left/right-edge cases, WHEN submitted/cancelled/hidden, THEN model state, bounded text, layout, tail position, and viewport margins match the contract.
- **REQUIREMENT R-10 — Input arbitration and fullscreen:** Character controls shall be suppressed while dialogue is editing. `F11` and `Alt+Enter` shall toggle fullscreen; `Escape` shall exit fullscreen only while dialogue input is closed; `Alt+Enter` shall not submit dialogue.
  - **AC-10 [Automated + E2E]:** GIVEN windowed/fullscreen and dialogue-open/closed combinations, WHEN the relevant keys are pressed, THEN exactly one intended action occurs and no character action leaks through.

### Extraction, validation, capture, and release

- **REQUIREMENT R-11 — Immutable sources:** The six source sheets and their documented container/mode/dimensions/SHA-256 values shall remain byte-identical before and after every extractor run.
  - **AC-11 [Automated]:** GIVEN pre-run hashes, WHEN each official extractor runs in a fresh process, THEN all six post-run hashes equal the documented hashes.
- **REQUIREMENT R-12 — Deterministic extraction:** Normal extraction shall rewrite only the 33 active frame PNGs. Repeated fresh-process regeneration shall produce identical pixels and encoded PNG bytes. Each LeftWalk frame shall be the exact horizontal pixel mirror of its matching RightWalk frame. Evidence images shall be opt-in only and outside release resources.
  - **AC-12 [Automated]:** GIVEN two clean fresh-process regeneration passes, WHEN hashes are compared, THEN every active output hash matches between passes, source hashes are unchanged, no non-frame artifact is written without `--evidence`, and mirroring/extrema checks pass.
- **REQUIREMENT R-13 — Read-only release validator:** `validate_release.py` shall be read-only and shall prove the source, frame, regeneration, alignment/lower-anatomy, mirroring, export-policy, and built-artifact contracts. Success shall end with `ASSET_RELEASE_CHECK_PASS frames=33 sources=6 read_only=true export_resources=34 artifact_manifest=checked_if_present`.
  - **AC-13 [Automated]:** GIVEN a valid tree, WHEN run from a fresh process, THEN the exact pass summary is printed and no file is created, modified, or deleted; GIVEN each validator negative control, THEN it fails for the intended reason.
- **REQUIREMENT R-14 — Safe capture mode:** Capture shall accept only a direct-child `.png` filename under project-local `Captures`; reject absolute, UNC, device, drive-relative, nested, traversal, invalid, reserved-device, wrong-extension, and existing-target paths; reject reparse-point path components; publish via a flushed unique same-directory staging file and no-overwrite move; and remove only its own staging file after failure.
  - **AC-14 [Automated + Windows E2E]:** GIVEN valid and invalid path classes plus existing files, denied permissions, publication failure, and reparse-point cases, WHEN capture is attempted, THEN valid capture publishes once, invalid/unsafe capture exits nonzero, existing files remain byte-identical, and no orphan staging file remains.
  - **ASSUMPTION A-01:** The documented residual same-user TOCTOU race is accepted for this developer-only feature; OS-level directory isolation is outside this mission.
- **REQUIREMENT R-15 — Launcher portability:** `run-animation.bat` and `export-release.ps1` shall work when invoked from an arbitrary current working directory, using their own script/project location. Godot resolution shall prefer the supported configured/bundled executable and fail clearly when unavailable.
  - **AC-15 [Windows E2E]:** GIVEN a cwd outside the project, WHEN each launcher is invoked, THEN it targets this project, propagates the process exit code, and does not read/write a sibling project.
- **REQUIREMENT R-16 — Selected-resource export:** The Windows preset shall export `Main.tscn` plus exactly the 33 active frame resources, with no broad include/exclude filter, source sheet, extractor, probe, mission/evidence, Build, README, obsolete root image, or Waving payload. C# source content and debug symbols shall be disabled.
  - **AC-16 [Automated]:** GIVEN `export_presets.cfg` and the final package manifest, WHEN validated, THEN the whitelist has exactly 34 ordered resources, denied content is absent, managed payload contains no PDB/debug symbols or absolute project path, and executable/PCK manifests agree when both exist.
- **REQUIREMENT R-17 — Clean Windows release and smoke:** Export shall clean only the project-local ignored `Build` directory, create `Build/TCFAnimation.exe`, and the actual exported executable shall launch on Windows. Its read-only `--verify-runtime` mode shall reject capture arguments, load `Main.tscn`, resolve Character/DialogueUi, load all 33 512x864 textures in a 1920x1080 viewport, and print `RUNTIME_SMOKE_PASS frames=33 dialogue_ui=true`.
  - **AC-17 [Automated + Windows E2E]:** GIVEN invocation from an arbitrary cwd, WHEN export, post-export validation, smoke, and representative interactive journeys run, THEN all exit zero, exact pass markers are recorded, and package hygiene remains valid.
- **REQUIREMENT R-18 — Engineering quality gates:** Debug/Release builds, formatting, diff checks, ControllerProbe, headless Godot import/startup, independent test, code, security, QA/E2E, visual review, and engineering-judge gates shall pass. ControllerProbe success shall include a real positive assertion count and named summary. No stale quality blocker may remain.
  - **AC-18 [Gate evidence]:** GIVEN the final working tree, WHEN all commands and independent gates execute, THEN commands/versions/exit codes/output summaries are recorded, ControllerProbe reports `assertions=<positive integer>`, reviewers issue PASS/APPROVED with no unresolved blocking finding, and the Judge issues APPROVED.

## Visual acceptance matrix

**REQUIREMENT R-19 — Complete visual coverage:** Manual QA shall inspect every listed frame at runtime display size and zoom, and record apparent visible-bounds height, a consistently defined head anchor, torso anchor, and lowest-foot position for each frame. Deltas shall be recorded for every boundary below. Extraction/alignment defects fail; a source-art limitation may pass only when the corresponding source pose is cited and the visual reviewer explicitly accepts it.

- **AC-19 [Manual/visual]:** GIVEN the 33-frame inventory and exported runtime, WHEN the matrix below is completed, THEN every frame and boundary has native-size and zoom evidence, measurements/deltas, artifact findings, source-limitation attribution where applicable, and an explicit pass/fail result.

| Group / boundary | Frames or transitions that must be inspected | Required visual result |
|---|---|---|
| LeftTurn | `turn_0→turn_1→turn_2` and reverse | Smooth front/45°/90° silhouette, head/torso/feet continuity, no crop or matte artifact |
| RightTurn | `turn_0→turn_1→turn_2` and reverse | Same as LeftTurn |
| Front direction handoff | `LeftTurn/turn_0 ↔ RightTurn/turn_0` during direct reversal | No position/scale/background flash or unintended pose jump |
| LeftWalk | `walk_00→01→02→03→04→05→00`; `LeftTurn/turn_2 ↔ walk_00`; edge stop | Stable floor contact/cadence and apparent scale; no clipping or loop pop |
| RightWalk | `walk_00→01→02→03→04→05→00`; `RightTurn/turn_2 ↔ walk_00`; edge stop | Same as LeftWalk and visually mirrored |
| Clap | Front→`clap_00`; runtime playback order including each forward/reverse adjacency; final `clap_00`→same front | Hands/arms animate without detached fragments; body/head/feet remain stable; no entry/exit pop |
| CrossArm | Front→`cross_00→cross_01→cross_02`; repeated held `cross_02` | Continuous crossing motion and stable held pose |
| CrossArmRelease | `cross_02→release_00→01→02→03→04→05`→same front | Continuous release, stable height/anchors, no release-to-front pop |
| Dialogue overlay | Short/long/input bubbles at center and both movement edges, windowed and fullscreen | Bubble remains inside viewport, readable, unclipped, and tail tracks character |
| Exported runtime | Representative full journey across all groups in `Build/TCFAnimation.exe` | Rendering/cadence/input behavior matches editor/headless contracts with no development artifact visible |

- **ASSUMPTION A-02:** No universal numeric continuity tolerance was supplied. The release default is zero tolerance for extraction/canvas defects; measured pose variation traceable to the immutable source art is adjudicated and documented by manual visual QA.
- **OPEN QUESTION OQ-01 (non-blocking):** Should a future release define numeric per-anchor jump tolerances? **Recommended default:** retain measured/manual adjudication for this mission, then derive thresholds from its accepted measurements.
- **OPEN QUESTION:** No blocking ambiguity identified.

## Explicit exclusions

- **OUT OF SCOPE OOS-01:** Adding or restoring Waving behavior, controls, state, assets, or export resources.
- **OUT OF SCOPE OOS-02:** Editing application code/assets during this requirements-baseline task or running the full validation suite during analysis.
- **OUT OF SCOPE OOS-03:** Inspecting or changing sibling projects.
- **OUT OF SCOPE OOS-04:** Committing, pushing, releasing to a distribution channel, or mutating Git history.
- **OUT OF SCOPE OOS-05:** Re-authoring immutable source art or requiring OS-level mitigation for the documented residual capture TOCTOU race.
