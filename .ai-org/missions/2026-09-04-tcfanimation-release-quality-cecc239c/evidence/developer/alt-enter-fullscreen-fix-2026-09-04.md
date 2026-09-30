# Alt+Enter focused-dialogue production fix — developer evidence

Date: 2026-09-04

## Root cause proof

- `DialogueUi._Input` receives dialogue keyboard input before the unhandled-key
  phase. It intentionally returns for Alt+Enter without submitting or closing
  the editor.
- Global fullscreen arbitration was implemented only in
  `TurnController._UnhandledKeyInput`. A focused `LineEdit` consumes Enter
  before that phase, so Alt+Enter never reached the global handler while the
  editor was focused. F11 continued to work because the focused control did
  not consume it.
- A new route regression was run before the fix and failed with:

  `Alt+Enter editing routes once before focused controls: condition was false.`

  The stack identified `AssertSingleGlobalRoute` in
  `ControllerProbe/Program.cs`. This proved that the policy action was still
  associated with the late/unhandled route instead of exactly one early route.

## Production change and input flow

- `GlobalInputPolicy.cs`
  - Added `GlobalInputPhase`.
  - Global actions resolve only during `EarlyInput`.
  - Released and echo/repeat events resolve to `None`.
  - Existing F11, Alt+Enter, and editing-aware Escape arbitration remains
    centralized in the policy.
- `TurnController.cs`
  - Added early `_Input` global-key arbitration.
  - Applies the one resolved fullscreen action and marks the viewport input
    handled.
  - Removed global fullscreen arbitration from `_UnhandledKeyInput`; that
    method now remains responsible only for non-dialogue gameplay keys.
- `DialogueUi.cs` was not changed. Alt+Enter still never enters
  `DialogueModel.HandleKey`, so it cannot submit, close, normalize, or clear the
  `LineEdit`. The early global handler does not read or mutate the `LineEdit`;
  its text, editing state, and focus remain owned by the existing dialogue UI.
- Escape remains two-stage: while editing, the global policy returns `None` and
  `DialogueUi` cancels the editor; after editing closes, a subsequent Escape
  resolves to `ExitFullscreen`.

## Regression evidence

Baseline before adding the route regression:

```text
dotnet run --project .\ControllerProbe\ControllerProbe.csproj -c Release
CONTROLLER_PROBE_PASS assertions=481 ... dialogue_input=true ... global_input=true ...
```

Red phase before the production fix:

```text
dotnet run --project .\ControllerProbe\ControllerProbe.csproj -c Release
Unhandled exception. System.InvalidOperationException:
Alt+Enter editing routes once before focused controls: condition was false.
```

Final probe:

```text
dotnet run --project .\ControllerProbe\ControllerProbe.csproj -c Release
PASS Alt+Enter closed routes once before focused controls
PASS Alt+Enter editing routes once before focused controls
PASS F11 editing routes once before focused controls
PASS Alt+Enter preserves open editor state without dialogue action
PASS Escape remains two-stage while editing fullscreen
PASS plain Enter remains owned by dialogue
CONTROLLER_PROBE_PASS assertions=489 ... dialogue_input=true ... global_input=true ...
```

The global-input matrix also covers both fullscreen states, both dialogue
states, F11 with and without Alt, Alt+Enter, plain Enter, Escape arbitration,
other keys, released keys, and echoed keys. The phase assertion requires the
action in `EarlyInput` and `None` in `UnhandledKeyInput`, preventing duplicate
toggles.

## Build and validation evidence

```text
dotnet build .\TCFAnimation.sln -c Debug
Build succeeded. 0 Warning(s), 0 Error(s)

dotnet build .\TCFAnimation.sln -c Release
Build succeeded. 0 Warning(s), 0 Error(s)

dotnet format .\TCFAnimation.sln --verify-no-changes --no-restore
git --no-pager diff --check -- .
FORMAT_AND_DIFF_CHECK_PASS

Godot_v4.5.1-stable_mono_win64_console.exe --headless --path . --import
exit 0

Godot_v4.5.1-stable_mono_win64_console.exe --headless --path . --quit-after 2
exit 0

.\export-release.ps1
RELEASE_EXPORT_PASS ... clean_build=true ... isolated_stage=true
import_exit=0 import_errors=0 export_exit=0 export_errors=0
exe_bytes=100185624 managed_dll_bytes=78848

python -B .\FrameExtraction\validate_release.py
ASSET_RELEASE_CHECK_PASS frames=33 sources=6 read_only=true
export_resources=34 artifact_manifest=checked_if_present

Build\TCFAnimation.exe --headless -- --verify-runtime
RUNTIME_SMOKE_PASS frames=33 dialogue_ui=true
viewport_fit=1920x1080:CanvasItems:Keep
RUNTIME_SMOKE_EXIT=0
```

Release cleanup checks:

```text
RELEASE_STAGE_REMAINDERS=0
BUILD_PDB_COUNT=0
```

## Release hashes

- Previous defective `Build\TCFAnimation.exe`:
  `6421D47A982DD97427C4F2DCAC0F79F9E06F4D411AE8CEB7F4606A8EFB5EF9E9`
- Fixed `Build\TCFAnimation.exe`:
  `6C1EEA789E89897BECFCD6A90A5ABDB4B591E162EF1C1E780FDAD82E3B83C96F`
- Fixed managed assembly
  `Build\data_TCFAnimation_windows_x86_64\TCFAnimation.dll`:
  `1C62F905EF43B76BD5C5E068ED5737F563F0F574E9CFE3E7539A19FF35AD2518`

## Gate status

- No commit or push was performed.
- The prior 50/51 foreground QA result and all artifact-specific conclusions
  against the old executable hash are invalidated by this source and release
  change.
- Final foreground QA is intentionally not claimed. An attempted interactive
  window smoke from the CLI environment could not acquire a visible HWND even
  though the process remained alive, so the independent QA agent must rerun
  the repaired foreground harness against the fixed executable hash.
- Independent code review and final judgment over the prior source/artifact
  should also be rerun for this delta.
