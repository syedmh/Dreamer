# TCFAnimation

Godot 4.5.1 Mono/C# directional turn, walking, dialogue, clap, and
cross-arm animation. The project targets .NET 8 and uses a 1920x1080 logical
viewport.

## Run

From any current working directory:

```bat
C:\path\to\Dreamer\TCFAnimation\run-animation.bat
```

Both `run-animation.bat` and `export-release.ps1` resolve Godot in the same
order: nonempty `GODOT_EXE`, the project-local bundled executable, `godot4` on
`PATH`, then `godot` on `PATH`. An explicitly configured `GODOT_EXE` is
authoritative and never falls back when missing or invalid. Candidate bytes
must match the release provenance before execution; console candidates also
require the authenticated companion editor, and export additionally requires
the authenticated Windows release template. Byte-identical configured or
`PATH` copies may use a different canonical path or basename. The authenticated
executable must then run `--version` successfully, report exact version
`4.5.1`, and contain a dot-delimited `mono` token. Successful resolution prints
`GODOT_SELECTED source=... version=... executable=...`; resolution failure
prints `GODOT_RESOLUTION_FAIL` and exits 1.

## Controls

- **Left/Right Arrow**: turn from front through 45° to 90°, then walk while the
  key remains held. Releasing or reversing returns through the existing turn
  sequence; holding both arrows is neutral.
- **+/-**: adjust movement and walk playback together from `0.25x` to `3.0x`
  in `0.25x` steps. At `1.0x`, movement is 240 design pixels/second and both
  six-frame walks play at 6 FPS. Turn timing remains fixed at 8 FPS.
- **C**: start the front-idle clap. The six clap assets are played in the
  fixed 15-step order `0,1,2,3,4,3,2,1,2,3,4,3,2,1,0` at 8 FPS. Directional
  input interrupts the clap.
- **X**: from front idle, play `cross_00..02` at 8 FPS and hold `cross_02`.
  Press X again to play `release_00..05` at 8 FPS and return to the same
  directional front pose. Clap and directional input are blocked throughout
  crossing, hold, and release; held arrows are not queued.
- **Enter**: open dialogue input. Submit with Enter or cancel with Escape.
  Input is capped at 500 Unicode scalar values (matching Godot's code-point
  character model without splitting UTF-16 surrogate pairs), normalized on
  submission, wrapped to at most four lines, and clamped inside the logical
  viewport.
- **P**: hide the current speech bubble while dialogue input is closed.
- **F11** or **Alt+Enter**: toggle fullscreen. **Escape** exits fullscreen
  while dialogue input is closed.

There is intentionally **no waving action, control, runtime state, or release
resource**. Files whose names begin with `Waving` are obsolete, unrelated
working-tree files and must not be added to the runtime or export.

## Runtime geometry

Every runtime frame is a 512x864 opaque RGBA PNG on exact black outside its
regenerated character matte. The sprite canvas center is x=256 and runtime
scale is `1.25`.

The measured walk extrema are:

- left-walk visible minimum x=74
- right-walk visible maximum x=437

`AnimationGeometry` therefore clamps the character center to **x=227.5** at
the left edge and **x=1693.75** at the right edge of the 1920-wide viewport.
Runtime uses these constants directly and does not scan textures.

## Active runtime assets

The release contains exactly 33 frame PNGs:

| Directory | Count | Files |
|---|---:|---|
| `Frames/LeftTurn` | 3 | `turn_0.png` through `turn_2.png` |
| `Frames/RightTurn` | 3 | `turn_0.png` through `turn_2.png` |
| `Frames/LeftWalk` | 6 | `walk_00.png` through `walk_05.png` |
| `Frames/RightWalk` | 6 | `walk_00.png` through `walk_05.png` |
| `Frames/Clap` | 6 | `clap_00.png` through `clap_05.png` |
| `Frames/CrossArm` | 3 | `cross_00.png` through `cross_02.png` |
| `Frames/CrossArmRelease` | 6 | `release_00.png` through `release_05.png` |

Every left-walk PNG is the exact encoded horizontal mirror of its matching
right-walk PNG.

## Immutable source sheets

Extraction is permitted to read, but never normalize or rewrite, these six
authoritative source files:

| Source | Container/mode | Dimensions | SHA-256 |
|---|---|---:|---|
| `LTurning.png` | PNG/RGBA | 1086x1448 | `9EDD38F303B17CD043EDCCABF2E6C2BC50F182B9A2B918B4BDECF1B2861E3A91` |
| `RTurning.png` | PNG/RGB | 1086x1448 | `2D20B97B4BC630DBFF9D6DD932F3314A9BC6FE0587013E6C12E72BF1D40D5840` |
| `RWalking2.png` | JPEG/RGB | 1536x1024 | `CD56287A4830D068292793256DBEB5A29E1EB9D888520A5339FE3957E7B7FA3A` |
| `Clapping2.png` | JPEG/RGB | 1086x1448 | `FBB46FBEAD0D5815F4E23307240535C600C29D0DF650B493137D05E762319C00` |
| `CrossArm3.png` | JPEG/RGB | 1086x1448 | `276413B76F13D4940FD8B746D3AEA13D27922A47EACD750DCCC6FF622A6A8192` |
| `CrossArm4.png` | JPEG/RGB | 1086x1448 | `475614A7B2DB0B7469FA88E9B7B5F5C8548F8095B56DAD99F6270098E9174A3F` |

The files with JPEG containers retain their historical `.png` names. Godot
import metadata keeps source sheets out of runtime use.

## Artifact-free release validation

For CPython 3.13 on Windows x86-64, `requirements.txt` is the sole release
dependency lock. Its only index is the explicit HTTPS Microsoft PyPI proxy
`https://packagefeedproxy.microsoft.io/pypi/simple/`; there is no automatic
fallback or extra index. Exact package versions and Windows wheel hashes remain
the artifact acceptance boundary.

Disable ambient pip configuration and `PIP_*` overrides before acquiring the
three locked wheels:

```powershell
Get-ChildItem Env: |
    Where-Object Name -Like "PIP_*" |
    ForEach-Object { Remove-Item -LiteralPath ("Env:" + $_.Name) }
$env:PIP_CONFIG_FILE = "nul"
$proxy = "https://packagefeedproxy.microsoft.io/pypi/simple/"
$wheelhouse = Join-Path $env:TEMP (
    "TCFAnimation-release-wheelhouse-" + [guid]::NewGuid()
)

python -m pip download --disable-pip-version-check --no-cache-dir `
    --index-url $proxy --require-hashes --only-binary=:all: `
    --platform win_amd64 --python-version 3.13 --implementation cp `
    --dest $wheelhouse -r requirements.txt
python -m pip install --dry-run --ignore-installed `
    --no-index --find-links $wheelhouse --require-hashes `
    --only-binary=:all: -r requirements.txt
```

The release evidence additionally computes every downloaded wheel's SHA-256
and requires the exact three locked filename/hash pairs before the offline
install check. Do not use `--trusted-host`, TLS bypasses, source
distributions, direct URLs, or credential-bearing index URLs. Keep temporary
wheelhouses outside runtime/resource directories.

Run the read-only asset and export-policy validator:

```powershell
python -B FrameExtraction\validate_release.py
```

Success ends with:

```text
ASSET_RELEASE_CHECK_PASS frames=33 sources=6 read_only=true export_resources=34 artifact_manifest=checked_if_present
```

The validator does not create, modify, or delete files. It verifies immutable
source formats and hashes; exact frame directories, names, counts, dimensions,
opacity, exact-black backgrounds, source-RGB preservation, independent
validator-owned lower-anatomy anchors, explicit floor negatives and sole
envelopes, component attachment and edge invariants, and the absence of
artificial charcoal histogram plateaus; deterministic in-memory pixel and
PNG-byte regeneration; exact walk mirroring and extrema; and the exact
selected-resource export whitelist. It owns the row-640 stationary-plate
constant and continuity thresholds independently from the extractor, measures
lower-band binary silhouette symmetric difference/union plus actual vest
shoulder and waist landmarks, and runs reason-specific mutations for an
attached dark reflection strip, a one-pixel-bridged cool fragment, dark-shoe
anchor deletion, a shared-plate pixel change, a 5 px stationary shift, a
15 px walking torso shift, and a 17 px walking shoe-baseline shift.
The extractor never lifts dark shoe pixels to a synthetic gray value: retained
footwear uses unchanged source RGB from a separately seeded, tightly bounded
per-shoe GrabCut path with ankle-proximity checks and calibrated reflection
rejection. Lower support rectangles can bound or reject that segmentation but
are never foreground seeds or unconditional output unions. Before export it
validates configuration only. When
`Build\TCFAnimation.exe` or
`Build\TCFAnimation.pck` exists, it also reads the embedded/final PCK manifest
in place without extracting files, rejects unexpected payload entries, and
checks the managed release directory for PDBs and absolute project paths.
Godot-generated UID and script-class caches are treated as engine metadata;
their internal references to non-exported development files do not make those
files runtime payloads.

Run the code checks:

```powershell
dotnet build TCFAnimation.sln -c Debug
dotnet build TCFAnimation.sln -c Release
dotnet run --project ControllerProbe\ControllerProbe.csproj -c Release
dotnet format TCFAnimation.sln --verify-no-changes --no-restore
git diff --check -- .
```

`ControllerProbe` covers the geometry constants and edge centers, turn/walk
frame counts and ordering, speed boundaries, dialogue lengths and layout
errors (including Rune-based supplementary Unicode splitting, measurement,
and ellipsis boundaries), capture-path policy,
clap/cross/release interruption and reset boundaries, and the fixed
33-frame/no-wave contract.

## Regenerate runtime frames

Normal regeneration rewrites only the 33 active runtime PNGs. It does not
write contact sheets, overlays, `.ai-org`, or other evidence artifacts:

```powershell
python FrameExtraction\extract_directional_turns.py
python FrameExtraction\extract_right_walk.py
python FrameExtraction\extract_clap.py
python FrameExtraction\extract_cross_arm.py
python -B FrameExtraction\validate_release.py
```

The read-only height-only checks are:

```powershell
python FrameExtraction\extract_clap.py --validate-height-assets
python FrameExtraction\extract_cross_arm.py --validate-height-assets
```

Optional visual evidence is developer-only and is not part of release
validation. Explicit `--evidence` on the walk, clap, or cross-arm extractor may
write review images under `.ai-org`; omit it for normal regeneration and every
release-readiness command.

## Export and runtime smoke test

`export_presets.cfg` uses Godot selected resources. Its positive list is
exactly `res://Main.tscn` plus the 33 active frame PNGs. It does not use broad
include/exclude filters: both filter assignments must exist exactly once and
be empty. Source sheets, extractors, `ControllerProbe`,
`.ai-org`, `Build`, README files, obsolete root images, and all Waving files
are excluded from the release resource list. The preset explicitly disables
C# script content and debug-symbol export. Release and ExportRelease builds
also disable compiler debug symbols, preventing a PDB or an absolute local PDB
path from being emitted in the project assembly.

Capture mode is developer QA only. `--capture-path` accepts a `.png` file name,
not a directory or absolute path. Development runs resolve it under the
filesystem project root's `Captures` directory. Standalone/exported runs
select the standalone branch from Godot's `standalone` feature (with the
Godot 4 export-template feature as the runtime compatibility signal), validate
`AppContext.BaseDirectory` against Godot's fixed
executable-adjacent managed-data layout, then resolve under the executable
directory's `Captures` child (for the release artifact, `Build\Captures`).
The selected root is resolved once and reused through validation, staging,
and publication. Capture startup prints only
`CAPTURE_ROOT kind=executable_adjacent|project_resource`; there is no fallback
to a user directory. Move the portable
release to a writable location if its executable directory is read-only. UNC,
device, drive-relative, nested, and traversal paths are rejected. Windows
reserved aliases, including `CONIN$`, `CONOUT$`,
`COM1`-`COM9`, `LPT1`-`LPT9`, and the superscript-digit `COM`/`LPT` aliases,
are also rejected.

Capture requires an active rendering backend. A `--headless` capture request
fails once with `CAPTURE_FAILED` and exits nonzero without creating a target
or staging file.

Capture publication writes and flushes a unique staging file inside the
validated `Captures` directory, closes it, and then uses a same-directory
no-overwrite move to publish the final PNG atomically. Existing final files
are never removed or replaced, and failed publication removes only its staging
file. The project-root and capture-directory path components are rejected when
they are Windows reparse points after directory creation, before staging-file
creation/open, and immediately before publication. Standard .NET path checks
cannot eliminate a malicious same-user process swapping a path component in
the intervals between those checks and filesystem operations; protecting
against that residual TOCTOU race requires OS-level access isolation.

The export script also roots itself at its own location when invoked by
absolute path from an unrelated current working directory. From the project
directory, the full release sequence is:

```powershell
$godot = ".tools\godot-4.5.1-mono\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe"
& $godot --headless --path . --import
& $godot --headless --path . --quit-after 2
.\export-release.ps1
python -B FrameExtraction\validate_release.py
$smoke = Start-Process -FilePath "Build\TCFAnimation.exe" -ArgumentList @("--headless", "--", "--verify-runtime") -NoNewWindow -Wait -PassThru
if ($smoke.ExitCode -ne 0) { throw "Runtime smoke failed with exit code $($smoke.ExitCode)." }
```

The release script authenticates Godot, its companion, and the release
template before touching `Build`. It then guarded-cleans that generated
directory and copies exactly the 50 entries in
`release-stage-manifest.txt` into a GUID-owned isolated stage. After proving
that copied seed, it creates a fixed 994-byte UTF-8-without-BOM, CRLF
`TCFAnimation.sln` inside the stage. The generated solution references only
`TCFAnimation.csproj`; the repository solution and `ControllerProbe` never
cross the staging boundary. The resulting 51-file pre-import inventory is
proved before Godot import, so import-generated UID/cache state remains
stage-local and is never copied back.

Import and export must both exit zero and emit no line beginning `ERROR:`.
Success additionally requires a nonempty regular
`Build\TCFAnimation.exe` and
`Build\data_TCFAnimation_windows_x86_64\TCFAnimation.dll`, complete stage
cleanup, and no remaining `.release-stage-*` directory. Only then does the
script print one `RELEASE_EXPORT_PASS`. Any failure is fail-closed and removes
partial generated output. The final executable path remains exactly
`Build\TCFAnimation.exe`.

The validator reads the final embedded PCK in place without extraction. It
checks exact payload counts, bounded entry descriptors, a 4 MiB per-metadata
limit, a 16 MiB aggregate metadata limit, all four engine metadata entries,
all 33 import metadata entries, one-byte C# placeholders, and denied
development/unrelated tokens in both single-byte and UTF-16LE forms.

The exported smoke test is read-only, rejects capture arguments, loads
`Main.tscn`, resolves the character and dialogue nodes, loads all 33 textures,
checks the 1920x1080 viewport and 512x864 texture sizes, and exits successfully
only after printing:

```text
RUNTIME_SMOKE_PASS frames=33 dialogue_ui=true
```

Manual visual QA is intentionally not claimed by these automated checks.
