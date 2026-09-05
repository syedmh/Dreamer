# TCFAnimation

Godot 4.5.1 Mono/C# directional turn, walking, dialogue, JSON-driven action
messages, a timed celebration with procedural fireworks, a control legend,
clap/cross-arm playback, and six scripted school platform-scene animations.
The project targets .NET 8 and uses a 1920x1080 logical viewport.

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
- **C**: from front idle, play the approved 15-step clap once at 8 FPS.
  Directional input interrupts the clap, while scripted school and celebration
  choreography suppress manual clapping. While dialogue input is open, typed
  `C` remains ordinary text.
- **L**: show or hide the high-contrast action legend. Key echo is ignored,
  and the legend is hidden by default to preserve a clean presentation.
  While dialogue input is open, typed `L` remains ordinary text.
- **X**: from front idle, play `cross_00..02` at 8 FPS and hold `cross_02`.
  Press X again to play `release_00..05` at 8 FPS and return to the same
  directional front pose. Clap and directional input are blocked throughout
  crossing, hold, and release; held arrows are not queued.
- **Physical top-row 1 through 6**: select the matching `School1.png` through
  `School6.png`. When the school scene is inactive, first run
  `PreparingEntryLeft` on pure black. The Avatar visibly uses the approved
  left walk from its current x to the calibrated left x=`227.5`; a full-span
  pre-position is 6.0 seconds and partial distances scale linearly. If already
  left, preparation completes immediately. `Entering` then lasts 8.0 seconds:
  the selected aspect-cover school background starts fully offscreen-right and moves
  left to center while the approved right walk moves the Avatar from
  x=`227.5` to x=`1693.75`. They complete together. The exact 15-step clap
  then repeats at 8 FPS for exactly 10.0 seconds; the first update at or
  beyond 10.0 seconds returns to front idle on the right. From a black crossed
  hold, any physical top-row `1` through `6` first plays the normal six-frame
  release at 8 FPS, selects the requested school, then performs the same left
  pre-position if needed before entry. The matching configured action message
  is shown only when entry reaches the exact right endpoint and the 10-second
  clap begins. Empty or missing messages leave the existing bubble unchanged.
- **Physical top-row 0**: while a school is entering, clapping, or active,
  first run
  `PreparingExitRight`. The background remains at its current visible
  position while the Avatar uses the approved right walk from its current x
  to calibrated right x=`1693.75`; a full-span pre-position is 6.0 seconds
  and partial distances scale linearly. If already right, preparation
  completes immediately. If `0` interrupted a partial entry,
  `NormalizingExitBackground` then holds the Avatar in a clean right-front
  pose and smoothly continues the background left to exact center at the same
  background speed as entry. Its duration is
  `currentRightOffsetProgress * 8.0` seconds. `Exiting` then lasts 8.0 seconds:
  the background moves right from center to fully offscreen-right while the
  Avatar uses the left walk from x=`1693.75` to x=`227.5`; they complete
  together. Exit ends on exact black, automatically plays `cross_00..02` at
  8 FPS, and holds `cross_02`; **X** releases that hold. `0` always hides a
  visible bubble immediately, even when no school exit is eligible or while
  celebration choreography is active.
- **Q**: cancel any school choreography, hide the school background, switch
  to black, start procedural fireworks immediately behind the Avatar, release
  crossed arms when necessary, and visibly walk the Avatar
  to exact center x=`960`. The walk uses the correct directional six-frame
  sheet at 6 FPS and a distance-scaled duration based on the existing
  6.0-second full-span speed; it never teleports. At center, the configured
  `Q` message is shown, the approved 15-step clap repeats at 8 FPS for exactly
  30.0 seconds (240 frame intervals, ending on its clean front boundary), and
  the Avatar automatically plays `cross_00..02`. Transparent celebration
  frames allow the deterministic colorful bursts to remain visible behind
  the Avatar throughout walking, clapping, crossing, and the final hold.
- **R**: while celebration fireworks are active, stop, clear, and hide them
  immediately, cancel the remaining celebration choreography, move the Avatar
  to exact center, and restore its normal front-standing pose. The configured
  `R` message is shown when non-empty. `R` otherwise does nothing. A later
  **Q** starts the full celebration again from that centered standing pose.
- **Enter**: open dialogue input. Submit with Enter or cancel with Escape.
  Input is capped at 500 Unicode scalar values (matching Godot's code-point
  character model without splitting UTF-16 surrogate pairs), normalized on
  submission, wrapped to at most four lines, and clamped inside the logical
  viewport.
- **P**: hide the current speech bubble while dialogue input is closed.
- The legend lists Left/Right, +/-, 1-6, 0, Q, R, X, C, Enter, P, L,
  F11/Alt+Enter, and Escape, remains above black/school/fireworks visuals, and
  stays visible until toggled.
- **F11** or **Alt+Enter**: toggle fullscreen. **Escape** exits fullscreen
  while dialogue input is closed.

**School switching is intentionally serialized:** physical `1` through `6`
are consumed and ignored while any school is preparing, entering, clapping,
active, preparing exit, normalizing, exiting, or crossing. Press physical `0`
to return to black, then press the next school key. A new school may also be
selected directly from the final black crossed hold, which auto-releases and
re-enters that selection.

During scripted school release, entry/exit pre-position, partial-entry
background normalization, synchronized entry/exit, clap, and final crossing,
arrows, **C**, **X**, **+/-**, and dialogue opening are suppressed. **L** still
toggles the legend and fullscreen controls remain global. While dialogue is
already open, numeric characters, `C`, `L`, `Q`, and `R` remain ordinary text input
and never trigger choreography.
At school idle, ordinary
walk/cross/speed/dialogue controls are enabled over the school background;
`0` resets the pose to a clean front state and begins right pre-position. At
the final black crossed hold, dialogue and speed controls remain available,
arrows remain blocked like the normal crossed hold, **L** toggles the legend,
**X** releases, and
any `1` through `6` auto-releases, pre-positions left if needed, then enters
the selected school.

During Q release/walk/clap/cross/fireworks phases, arrows, **X**, **1-6**,
school exit, **+/-**, and dialogue opening are suppressed. `0` still hides the
bubble without changing celebration state, **L** still toggles the legend,
fullscreen controls remain global, **Q** is ignored, and **R** can stop the
fireworks and restore the centered front-standing pose during any active
celebration phase.

## Action message configuration

`ActionMessages.json` is an editable runtime resource with exact supported
keys `"1"` through `"6"`, `"Q"`, and `"R"`. Values are normalized with the
same whitespace/display rules as dialogue and bounded to 500 Unicode scalar
values without splitting surrogate pairs. At runtime, missing, null,
non-string, empty, whitespace-only, and unknown entries are ignored safely;
they never create an empty bubble. Invalid UTF-8, invalid JSON, or a non-object
root logs one visible `ACTION_MESSAGES_LOAD_FAIL` and continues with no
configured action messages. The shipped release validator requires all eight
editable defaults to be non-empty strings.

School messages appear at right-entry completion, the Q message appears at
center before the 30-second clap, and the R message appears only when R
actually stops active fireworks. User Enter submission still replaces action
text, and P or 0 hides the bubble without erasing its stored text.

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

School choreography stores Avatar progress `c` over `[0,1]`, where 0 is the
calibrated left center and 1 is the calibrated right center:
`characterX = 227.5 + (1693.75 - 227.5) * c`. Character-only pre-position uses
`6.0 * distanceFraction` seconds. Background right-offset progress `b` is 0
at center and 1 fully offscreen-right:
`backgroundX = viewportCenterX + (offscreenRightX - viewportCenterX) * b`,
where `offscreenRightX = viewportRight + displayedBackgroundWidth / 2`.
Entry drives `b: 1 -> 0` and `c: 0 -> 1`; exit drives `b: 0 -> 1` and
`c: 1 -> 0`.

All six `School*.png` sources are JPEG containers despite their extensions.
The reproducible preparation step preserves each source byte-for-byte and
writes true RGB PNGs to `Frames/Backgrounds/school1.png` through
`school6.png`. Runtime calculates aspect-cover independently from the selected
texture; backgrounds use linear filtering and character sprites remain nearest
filtered.

| School | Source size | Scale | Displayed size | Fully offscreen-right center x |
|---:|---:|---:|---:|---:|
| 1 | 1908x824 | 1.310679612 | 2500.776699x1080 | 3170.388350 |
| 2 | 1536x1024 | 1.25 | 1920x1280 | 2880 |
| 3 | 1540x1021 | 1.246753247 | 1920x1272.935065 | 2880 |
| 4 | 1540x1021 | 1.246753247 | 1920x1272.935065 | 2880 |
| 5 | 1540x1021 | 1.246753247 | 1920x1272.935065 | 2880 |
| 6 | 1540x1021 | 1.246753247 | 1920x1272.935065 | 2880 |

## Active runtime assets

The release contains the original 33 opaque black-canvas frame PNGs:

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

School mode adds 33 shared generated alpha-matted counterparts under
`Frames/SchoolCharacter` plus six backgrounds under `Frames/Backgrounds`.
Their RGB is
identical to the validated originals, while alpha comes from the same
regenerated extraction mattes. This removes the opaque 512x864 black rectangle
without chroma-keying dark pixels: all matte foreground, including exact-black
and near-black shoe/clothing pixels, stays fully opaque.

## Immutable source sheets

Extraction is permitted to read, but never normalize or rewrite, these twelve
authoritative source files:

| Source | Container/mode | Dimensions | SHA-256 |
|---|---|---:|---|
| `LTurning.png` | PNG/RGBA | 1086x1448 | `9EDD38F303B17CD043EDCCABF2E6C2BC50F182B9A2B918B4BDECF1B2861E3A91` |
| `RTurning.png` | PNG/RGB | 1086x1448 | `2D20B97B4BC630DBFF9D6DD932F3314A9BC6FE0587013E6C12E72BF1D40D5840` |
| `RWalking2.png` | JPEG/RGB | 1536x1024 | `CD56287A4830D068292793256DBEB5A29E1EB9D888520A5339FE3957E7B7FA3A` |
| `Clapping2.png` | JPEG/RGB | 1086x1448 | `FBB46FBEAD0D5815F4E23307240535C600C29D0DF650B493137D05E762319C00` |
| `CrossArm3.png` | JPEG/RGB | 1086x1448 | `276413B76F13D4940FD8B746D3AEA13D27922A47EACD750DCCC6FF622A6A8192` |
| `CrossArm4.png` | JPEG/RGB | 1086x1448 | `475614A7B2DB0B7469FA88E9B7B5F5C8548F8095B56DAD99F6270098E9174A3F` |
| `School1.png` | JPEG/RGB | 1908x824 | `2FED5C0AD5D5927BF22272634F2E879703436291962A46CBB5CB96C4434A986A` |
| `School2.png` | JPEG/RGB | 1536x1024 | `81468B2FA3E392F0EDDBC6F4FA9C5A961597C99F83ACA7669EAAC9938CC00A4C` |
| `School3.png` | JPEG/RGB | 1540x1021 | `D231C7EC01DC340313DF1F3E261F5EDDD138118F80DDA935E3599A1895212727` |
| `School4.png` | JPEG/RGB | 1540x1021 | `036F8ACC3084CF2FFEF366E0E939F7802F082E2D21A4A7A236C5488FAB7C7190` |
| `School5.png` | JPEG/RGB | 1540x1021 | `5F6CE380A5B9198B7D5B18AAF91258E9EDC57AFD78B11931E9E9ADE4B9AB7AB1` |
| `School6.png` | JPEG/RGB | 1540x1021 | `5F6CE380A5B9198B7D5B18AAF91258E9EDC57AFD78B11931E9E9ADE4B9AB7AB1` |

The files with JPEG containers retain their historical `.png` names. Godot
`keep` import metadata keeps all source sheets, including `School1.png`
through `School6.png`, out of
runtime use.

Regenerate or verify all six runtime backgrounds and the shared character
overlays:

```powershell
python -B FrameExtraction\prepare_school_assets.py
python -B FrameExtraction\prepare_school_assets.py --check
```

The script validates every school source byte count, JPEG/RGB container,
dimensions, and SHA-256 before and after generation. It writes six
deterministic background PNGs and reconstructs all 33 validated character
mattes in memory without duplicating overlays per school or modifying the
original opaque runtime frames.

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
ASSET_RELEASE_CHECK_PASS frames=33 school_overlays=33 backgrounds=6 sources=12 read_only=true export_resources=74 artifact_manifest=checked_if_present
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
33-frame/no-wave contract. It also covers all six per-image aspect-cover
geometries; all valid and invalid school IDs; identity retention and clearing;
physical-key restrictions; synchronized
right-to-left background entry and left-to-right exit; six-second
distance-scaled Avatar pre-position in both directions; repeated 15-step clap
order and exact 10-second boundary; partial-entry interruption with held
background, smooth same-speed normalization to center, and no teleport; final
automatic cross/hold; auto-release/re-entry; invalid and large deltas;
snapshot geometry; and scripted input suppression.
It additionally covers action-message schema tolerance and failure statuses,
500-scalar normalization, school endpoint message timing, explicit school
cancellation, left/right/center/crossed celebration starts, non-teleporting
distance-scaled travel, the exact 30.0-second/8 FPS clap boundary, cross and
fireworks activation, R idempotence, Q restart, deterministic bounded
fireworks simulation, C/L/0/Q/R arbitration, and legend entries/layout bounds.

## Regenerate runtime frames

Normal regeneration rewrites only the 33 active runtime PNGs. It does not
write contact sheets, overlays, `.ai-org`, or other evidence artifacts:

```powershell
python FrameExtraction\extract_directional_turns.py
python FrameExtraction\extract_right_walk.py
python FrameExtraction\extract_clap.py
python FrameExtraction\extract_cross_arm.py
python -B FrameExtraction\prepare_school_assets.py
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
exactly `res://Main.tscn`, the 33 original frames, the 33 shared transparent
counterparts, the six prepared school backgrounds, and
`res://ActionMessages.json` (74 resources total). It
does not use broad include/exclude filters: both filter assignments must exist
exactly once and be empty. Source sheets, extractors, `ControllerProbe`,
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
directory and copies exactly the 106 entries in
`release-stage-manifest.txt` into a GUID-owned isolated stage. After proving
that copied seed, it creates a fixed 994-byte UTF-8-without-BOM, CRLF
`TCFAnimation.sln` inside the stage. The generated solution references only
`TCFAnimation.csproj`; the repository solution and `ControllerProbe` never
cross the staging boundary. The resulting 107-file pre-import inventory is
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
all 72 import metadata entries, the runtime JSON resource, two one-byte
scene-script placeholders, and denied development/unrelated tokens in both
single-byte and UTF-16LE forms.

The exported smoke test is read-only, rejects capture arguments, loads
`Main.tscn`, resolves the background, character, and dialogue nodes, loads all
66 character textures plus all six school backgrounds, checks every source
dimension and aspect-cover/offscreen geometry, checks the
1920x1080 viewport and 512x864 character texture sizes, validates all eight
action messages, the 13-entry hidden legend, the 30-second celebration
contract, and the inactive fireworks layer, and exits successfully
only after printing:

```text
RUNTIME_SMOKE_PASS character_textures=66 school_backgrounds=6 dimensions=1908x824,1536x1024,1540x1021,1540x1021,1540x1021,1540x1021 dialogue_ui=true action_messages=8 legend_entries=13 celebration_seconds=30 fireworks_layer=true viewport_fit=1920x1080:CanvasItems:Keep
```

Deterministic developer capture snapshots use
`--capture-school=<1..6>:entry-prep-mid|entry-prep-left|entry-start|entry-mid|entry-end|clap|exit-prep-mid|exit-normalize-mid|exit-mid|exit-end|cross-hold`
with the existing `--capture-path=<file.png>` option. Omitting `<1..6>:` keeps
the legacy School1 default. The preparation snapshots show black
right-to-left Avatar staging and visible-school left-to-right Avatar staging;
`exit-normalize-mid` shows partial-entry reconciliation without changing
release resources.

`entry-end` and `clap` school snapshots show the configured school message.
Celebration snapshots use
`--capture-celebration=walk|clap|fireworks|stopped`; fireworks use a fixed
capture seed. Add `--capture-legend` to any base snapshot to show the legend,
or `--capture-hide-bubble` to prove 0-style bubble dismissal. Capture mode
still requires exactly one base selector: `--capture-frame`,
`--capture-school`, or `--capture-celebration`.
