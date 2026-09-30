# TCFAnimation Independent Post-Fix Test Engineer Gate

Date: **2026-09-04**  
Role: **Independent Test Engineer**  
Project: `C:\Users\syedhu\source\repos\Dreamer\TCFAnimation`  
Mission: `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c`

## TEST RESULT

### Command

Commands were executed against the exact current working tree and the release
freshly reproduced from that tree. No production source was edited, and no
commit or push was performed.

```powershell
# Toolchain
dotnet --info
python --version
& .\.tools\godot-4.5.1-mono\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe --version

# .NET build and test harness
dotnet build TCFAnimation.sln -c Debug --nologo
dotnet build TCFAnimation.sln -c Release --nologo
dotnet test TCFAnimation.sln -c Release --no-build --nologo
dotnet run --project ControllerProbe\ControllerProbe.csproj -c Release --no-build

# Python syntax
python -B -c "import pathlib,sys; files=[pathlib.Path(p) for p in sys.argv[1:]]; [compile(p.read_text(encoding='utf-8'),str(p),'exec') for p in files]; print(f'PYTHON_SYNTAX_PASS files={len(files)}')" `
  FrameExtraction\extract_clap.py `
  FrameExtraction\extract_cross_arm.py `
  FrameExtraction\extract_directional_turns.py `
  FrameExtraction\extract_right_walk.py `
  FrameExtraction\file_integrity.py `
  FrameExtraction\foreground_cutout.py `
  FrameExtraction\validate_release.py

# Two fresh-process extraction passes
python -B FrameExtraction\extract_directional_turns.py
python -B FrameExtraction\extract_right_walk.py
python -B FrameExtraction\extract_clap.py
python -B FrameExtraction\extract_cross_arm.py
python -B FrameExtraction\extract_clap.py --validate-height-assets
python -B FrameExtraction\extract_cross_arm.py --validate-height-assets
# The same four normal extractor commands were then executed a second time.
# SHA-256 maps were captured before, after pass 1, and after pass 2.

# Read-only validator, surrounded by complete project-tree SHA-256 snapshots
python -B FrameExtraction\validate_release.py

# Formatting and scoped Git checks
dotnet format TCFAnimation.sln --verify-no-changes --no-restore --verbosity minimal
git -C C:\Users\syedhu\source\repos\Dreamer diff --check -- TCFAnimation
git -C C:\Users\syedhu\source\repos\Dreamer diff --cached --name-only -- TCFAnimation

# Godot import and startup
& .\.tools\godot-4.5.1-mono\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe `
  --headless --path . --import
& .\.tools\godot-4.5.1-mono\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe `
  --headless --path . --quit-after 2

# Release reproduction from an unrelated current directory
Push-Location $env:TEMP
& C:\Users\syedhu\source\repos\Dreamer\TCFAnimation\export-release.ps1
Pop-Location

# Post-export validation and runtime smoke
python -B FrameExtraction\validate_release.py
Start-Process -FilePath Build\TCFAnimation.exe `
  -ArgumentList @("--headless", "--", "--verify-runtime") `
  -NoNewWindow -Wait -PassThru
Start-Process -FilePath Build\TCFAnimation.exe `
  -ArgumentList @("--headless", "--", "--verify-runtime", "--capture-path", "forbidden.png") `
  -NoNewWindow -Wait -PassThru

# Final identities and hygiene
Get-FileHash -Algorithm SHA256 `
  Build\TCFAnimation.exe, `
  Build\data_TCFAnimation_windows_x86_64\TCFAnimation.dll
Get-ChildItem Build -Recurse -File -Filter *.pdb
```

### Result

Real summary output:

```text
.NET SDK 10.0.400; target runtime .NET 8.0.30
Python 3.13.15
Godot 4.5.1.stable.mono.official.f62fdbde1

Debug:   Build succeeded. 0 Warning(s), 0 Error(s). Exit 0.
Release: Build succeeded. 0 Warning(s), 0 Error(s). Exit 0.
dotnet test: exit 0; the solution has no separate test-SDK project.

CONTROLLER_PROBE_PASS assertions=489 left_turn=true right_turn=true
returns=true direct_reversal_both_ways=true both_held_neutral=true
left_walk_6_frames=true right_walk_6_frames=true left_edge_latch=true
right_edge_latch=true geometry_extrema=74,437
geometry_centers=227.5,1693.75 fps_turn=8 fps_left_walk=6
fps_right_walk=6 adjustable_walk_speed=true clap_6_frames=true
clap_15_steps=true fps_clap=8 clap_one_shot=true clap_interruptible=true
cross_arm_3_frames=true fps_cross_arm=8 crossed_hold_cross_02=true
release_6_frames=true release_source=CrossArm4
fps_cross_arm_release=8 cross_arm_direction_lock=true
dialogue_input=true dialogue_layout=true global_input=true
capture_root=true fixed_runtime_frames=33 wave_assets=false

Probe run 1: 489 PASS lines, exit 0.
Probe run 2: 489 PASS lines, exit 0.
Flake rate: 0/2 runs.

PASS Alt+Enter closed routes once before focused controls
PASS Alt+Enter editing routes once before focused controls
PASS Alt+Enter preserves open editor state without dialogue action
PASS Alt+Enter toggles fullscreen dialogue=False fullscreen=False
PASS Alt+Enter toggles fullscreen dialogue=False fullscreen=True
PASS Alt+Enter toggles fullscreen dialogue=True fullscreen=False
PASS Alt+Enter toggles fullscreen dialogue=True fullscreen=True

PYTHON_SYNTAX_PASS files=7

Extractor pass 1: all four scripts exit 0.
Extractor pass 2: all four scripts exit 0.
Source count=6; frame count=33.
Pass 1 source hashes changed=0.
Pass 1 frame hashes changed from pre-run current outputs=0.
Pass 2 source hashes changed from baseline=0.
Pass 2 frame hashes changed from pass 1=0.
Final frame hashes changed from initial=0.
Clap height validation exit=0.
Cross-arm height validation exit=0.

ASSET_RELEASE_CHECK_PASS frames=33 sources=6 read_only=true
export_resources=34 artifact_manifest=checked_if_present
Validator exit=0.
Validator project-tree files before=3123, after=3123, changed=0.
All eight validator adversarial controls were rejected for their intended
reason, including turning-landmark, walking-torso, walking-baseline,
stationary-shift, floor-fragment, shoe-anchor, and shared-plate mutations.

dotnet format exit=0.
git diff --check -- TCFAnimation exit=0.
Staged TCFAnimation path count=0.

Godot import exit=0; ERROR: line count=0.
Godot startup exit=0; ERROR: line count=0.

GODOT_SELECTED source=bundled version=4.5.1.stable.mono.official.f62fdbde1
RELEASE_STAGE_READY seed_files=50 preimport_files=51 solution_length=994
RELEASE_EXPORT_PASS output=...\Build\TCFAnimation.exe clean_build=true
isolated_stage=true import_exit=0 import_errors=0 export_exit=0
export_errors=0 exe_bytes=100185624 managed_dll_bytes=78848
EXPORT_RELEASE_EXIT=0

pack_manifest_read=TCFAnimation.exe pack_version=3 engine=4.5.1 entries=73
pack_payload_ok=TCFAnimation.exe runtime_scene=1 runtime_scripts=2
runtime_textures=33 import_metadata=33 engine_metadata=4
denied_payloads=false source_content=false metadata_denied_tokens=0
managed_payload_ok=pdb_absent absolute_project_paths=false
project_assemblies=1
artifact_validation=pass artifacts=1 manifest_only=true
extraction_artifacts=false

RUNTIME_SMOKE_PASS frames=33 dialogue_ui=true
viewport_fit=1920x1080:CanvasItems:Keep
Runtime smoke exit=0.
Runtime smoke plus --capture-path exit=1 with the expected incompatibility:
RUNTIME_SMOKE_FAIL ArgumentException: --verify-runtime is incompatible with
capture mode; found --capture-path.

EXE bytes=100185624
EXE SHA-256=6C1EEA789E89897BECFCD6A90A5ABDB4B591E162EF1C1E780FDAD82E3B83C96F
EXE expected match=true

DLL bytes=78848
DLL SHA-256=1C62F905EF43B76BD5C5E068ED5737F563F0F574E9CFE3E7539A19FF35AD2518
DLL expected match=true

Build PDB count=0.
Build source/document/script count=0.
Managed DLL absolute-project-path hits=0.
Transient release-stage directory count=0.
Export resource count=34.
Export Waving resource count=0.
Empty include_filter assignments=1.
Empty exclude_filter assignments=1.
Running exported process count=0.
```

Passed: **520** (**489 ControllerProbe assertions + 31 independently
executed command/invariant gates**)  
Failed: **0**  
Skipped: **0**

The 31 gate checks are: Debug build; Release build; solution test invocation;
two probe runs; Python syntax; eight extractor executions; two height-only
checks; source preservation; initial-output equivalence; second-pass
determinism; validator success; validator read-only behavior; format; scoped
diff; Godot import; Godot startup; unrelated-cwd export; post-export package
validation; positive runtime smoke; capture-argument negative smoke; exact
binary identities; and final package/stage/staging hygiene.

### Failures

None.

One preliminary ad hoc package-hygiene command assumed obsolete
`export_files/N=` formatting and therefore reported zero resources. The
canonical preset uses one `PackedStringArray`; the corrected parser found
exactly 34 resources. This was a test-harness query error, not a product
failure, and the production validator independently confirmed the same
34-resource contract.

### Coverage of acceptance criteria

| Criterion | Executed proof | Result |
|---|---|---|
| AC-01 / AC-02 working-tree and scope safety | Scoped status/diff checks; zero staged TCFAnimation paths; no commit/push/history operation; extractors changed no source or frame bytes | PASS |
| AC-03 exact 33-frame/no-wave contract | ControllerProbe, validator, 73-entry package manifest, 34-resource whitelist | PASS |
| AC-04A frame/matte automated quality | Production validator plus all intended adversarial negative controls | PASS |
| AC-05 direction/walking/edges | ControllerProbe assertions and runtime asset smoke | PASS |
| AC-06 speed bounds and coupled playback | ControllerProbe assertions | PASS |
| AC-07 clap sequence/interruption | ControllerProbe plus extractor/height checks | PASS |
| AC-08 cross/hold/release arbitration | ControllerProbe plus extractor/height checks | PASS |
| AC-09 dialogue scalar/layout/model boundaries | ControllerProbe assertions and runtime node smoke | PASS |
| AC-10 fullscreen/input arbitration | Early-input source assertions present; 7 Alt+Enter assertions pass on both probe runs; `TurnController._Input` route is exercised by policy tests | PASS |
| AC-11 immutable source sheets | All six expected SHA-256 values unchanged through both extraction passes | PASS |
| AC-12 deterministic extraction | Two complete fresh-process passes; 33 frame byte hashes unchanged between passes and from initial current outputs | PASS |
| AC-13 read-only validator | Exact pass marker; complete 3,123-file tree unchanged | PASS |
| AC-14 capture policy relevant to release smoke | ControllerProbe path/root assertions; runtime verification rejects incompatible capture arguments | PASS for requested automated scope |
| AC-15 arbitrary-cwd release launcher | `export-release.ps1` executed successfully from `%TEMP%` | PASS |
| AC-16 selected-resource export | 34 resources, no Waving, 73 package entries, no source content/PDB/absolute project path | PASS |
| AC-17 clean release and smoke | Fresh export, validator, exact hashes, and `RUNTIME_SMOKE_PASS` | PASS |
| AC-18 engineering test gates | Builds, 489 assertions, syntax, format, diff, Godot import/startup, export, validator, smoke, and hygiene all pass | PASS |
| AC-04B / AC-19 manual visual inspection | Not re-executed by this automated post-fix Test Engineer gate; no production frame bytes changed during this gate | NOT IN THIS GATE |

### Conclusion: PASS

The exact current source independently rebuilds the exact expected release.
The Alt+Enter early-input regression assertions are present and pass
consistently. No mandatory automated post-fix test failure, package-identity
mismatch, release-hygiene defect, or flake was observed.

---

STATUS: **PASS**

SUMMARY:  
The current TCFAnimation tree passed 489 ControllerProbe assertions twice and
31 additional independent command/invariant gates. A fresh unrelated-cwd
export reproduced the expected EXE and DLL byte identities exactly.

WORK_COMPLETED:

- Discovered and executed the repository's actual .NET, Python, Godot, export,
  validation, and smoke harnesses.
- Ran Debug and Release builds and the solution-level `dotnet test` command.
- Ran ControllerProbe twice and verified the focused Alt+Enter early-input
  regression assertions in source and output.
- Ran all four extractors twice, height-only checks, source/frame SHA-256
  comparisons, Python syntax checks, and the read-only release validator.
- Ran formatting, scoped diff, Godot import/startup, unrelated-cwd export,
  positive and negative runtime smoke checks, exact hash checks, and package
  hygiene checks.
- Persisted this canonical artifact.

EVIDENCE:  
The **TEST RESULT** above.

ARTIFACTS:

- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/test-results-postfix.md`

FINDINGS:

- No product or release failure.
- No flaky assertion in two complete ControllerProbe runs.
- No distinct test-SDK unit/integration project exists; ControllerProbe is the
  repository's canonical executable assertion harness.
- Manual visual AC-04B/AC-19 was not repeated by this automated gate.

RISKS:

- This gate confirms the requested Alt+Enter regression through the canonical
  early-input policy assertions and fresh binary reproduction. It does not
  replace a human foreground keyboard/focus E2E run or the mission's separate
  visual QA artifact.

BLOCKERS:

- None for this post-fix Test Engineer gate.

NEXT_ACTION:

- Proceed to the remaining independent release gates using EXE
  `6C1EEA789E89897BECFCD6A90A5ABDB4B591E162EF1C1E780FDAD82E3B83C96F`
  and DLL
  `1C62F905EF43B76BD5C5E068ED5737F563F0F574E9CFE3E7539A19FF35AD2518`.
