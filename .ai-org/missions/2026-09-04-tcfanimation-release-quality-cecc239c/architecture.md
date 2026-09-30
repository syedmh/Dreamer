# TCFAnimation Release-Quality Architecture

Date: 2026-09-04  
Status: PASS — the current direction is viable; only contained remediation and
release evidence are required.

No CTO-reserved decision is required. The only known residual security
tradeoff is the same-user capture TOCTOU race already accepted by recovered
requirement A-01; this design introduces no new exception.

## Scope and conclusion

The authoritative uncommitted `TCFAnimation` design can satisfy R-01 through
R-19 without a rewrite, new service, new datastore, new framework, or breaking
contract. Keep the current single-scene Godot runtime, pure C# state/model
classes, deterministic Python extraction pipeline, selected-resource export,
and read-only validator. The gaps below are local integration and testability
defects, not reasons to change the product direction.

## Current state — verified FACTs

- **FACT:** `project.godot` selects `Main.tscn`, a 1920x1080 logical viewport,
  black clear color, nearest texture filtering, and Godot 4.5 C# features
  (`project.godot:13-20,29-34`).
- **FACT:** `Main.tscn` is a single scene with `TurnController` on the root,
  `Character` as a `Sprite2D`, and `DialogueUi` as a `CanvasLayer`
  (`Main.tscn:3-13`).
- **FACT:** `TurnController` is the Godot adapter. It resolves the scene nodes,
  loads seven frame groups, positions/scales the sprite, advances movement,
  applies textures, handles global/character keys, parses QA command-line
  modes, and owns capture publication (`TurnController.cs:41-79,91-172,
  174-273,291-328,330-449,451-622`).
- **FACT:** Animation behavior is framework-independent in
  `DirectionalTurnStateMachine`; it owns turn, walk, clap, cross/hold/release,
  edge-latch, interruption, and held-direction blocking state
  (`DirectionalTurnStateMachine.cs:5-48,69-179,179-425,429-578`).
- **FACT:** Runtime geometry is framework-independent and derives safe centers
  from calibrated visible extrema 74 and 437, producing default centers 227.5
  and 1693.75 (`AnimationGeometry.cs:5-26,26-76`).
- **FACT:** Dialogue state, scalar-safe bounding, normalization, wrapping,
  ellipsis, sizing, and viewport clamping are pure C#; `DialogueUi` adapts that
  model to Godot controls and follows the character head anchor
  (`DialogueModel.cs:8-115,118-326`; `DialogueUi.cs:15-100,103-222,224-344`).
- **FACT:** The runtime loads exactly 33 frames across seven groups and has no
  Waving state or loader (`TurnController.cs:19-31,54-63,291-328`;
  `DirectionalTurnStateMachine.cs:27-39`; `Frames/*` contains 33 PNGs).
- **FACT:** Capture paths are restricted to direct-child `.png` names below
  project-local `Captures`, reject Windows-dangerous names/path forms and
  existing targets, and validate reparse points before publication
  (`CapturePathPolicy.cs:7-67,70-171,174-271`).
- **FACT:** Capture writes a unique same-directory staging file with
  `CreateNew`, `WriteThrough`, flush-to-disk, and a no-overwrite move; failure
  cleanup targets only that staging file (`TurnController.cs:548-622`).
- **FACT:** Four Python extractors use immutable source hashes, calibrated
  generation, fixed RNG where applicable, compression level 9, and opt-in
  `--evidence`; the walk extractor writes exact horizontal mirrors
  (`FrameExtraction/extract_directional_turns.py:36-160,163-206,250-382,
  385-433`; `FrameExtraction/extract_right_walk.py:26-140,142-182,187-303,
  395-470,526-603`; `FrameExtraction/extract_clap.py:36-188,341-557,713-821`;
  `FrameExtraction/extract_cross_arm.py:102-184,187-240,316-470,787-1040`).
- **FACT:** `validate_release.py` is a read-only, fail-closed validator for six
  source contracts, the exact frame tree, in-memory regeneration, lower-anatomy
  negatives, mirroring/extrema, the export allowlist, and any present built
  package (`FrameExtraction/validate_release.py:32-100,468-624,658-883,
  886-960,1068-1257`).
- **FACT:** The Windows export preset is selected-resource based and names
  `Main.tscn` plus exactly 33 active PNGs; C# source content, debug symbols, and
  embedded build outputs are disabled (`export_presets.cfg:9-10,25-28`).
- **FACT:** Release builds disable project PDB generation, and
  `ControllerProbe` links the pure state/model/geometry/capture code rather
  than depending on Godot (`TCFAnimation.csproj:1-13`;
  `ControllerProbe/ControllerProbe.csproj:1-13`).
- **FACT:** Both launchers root project paths at their own script locations and
  propagate the invoked process result; export cleaning is guarded to the
  project-local `Build` directory (`run-animation.bat:4-37`;
  `export-release.ps1:3-54`).
- **FACT:** There is no persistence, network service, authentication boundary,
  queue, cache, or external runtime dependency beyond Godot/.NET and packaged
  resources. Dialogue and captures remain local to the process/filesystem.

## Desired state and component boundaries

| Component | Responsibility | Allowed dependencies |
|---|---|---|
| `Main.tscn` | Stable scene composition and node names | Godot resources only |
| `TurnController` | Godot lifecycle, texture application, movement, global shortcuts, QA modes | Godot + pure runtime policies |
| `DirectionalTurnStateMachine` | Deterministic animation/gesture state | .NET only |
| `AnimationGeometry` | Calibrated viewport-safe movement bounds | .NET only |
| `DialogueModel` / `DialogueLayout` | Dialogue state and pure text/layout rules | .NET only |
| `DialogueUi` | Godot rendering/input adapter for dialogue | Godot + dialogue model |
| `GlobalInputPolicy` (small new pure policy) | Testable fullscreen/Escape arbitration across dialogue state | .NET only |
| `CapturePathPolicy` | Validate capture names, containment, reparse points, and publication preconditions | .NET filesystem only |
| Extraction scripts | Deterministically regenerate only 33 active PNGs from six immutable sheets | Pinned Python packages |
| `validate_release.py` | Read-only source/frame/export/package proof | Project files and pinned Python packages |
| `ControllerProbe` | Fast executable assertions over pure runtime policies | Linked pure C# files only |
| Export/launch scripts | Resolve supported Godot, root paths, clean/export, and propagate failures | Windows shell + Godot |

Dependency direction remains **Godot adapters → pure policies/models**. Python
asset tooling remains independent of the runtime assembly. Do not move frame
generation into Godot or make runtime behavior depend on validator internals.

## Binding contracts

### Runtime resources

- The runtime resource manifest is exactly seven groups/33 PNGs:
  `LeftTurn(3)`, `RightTurn(3)`, `LeftWalk(6)`, `RightWalk(6)`, `Clap(6)`,
  `CrossArm(3)`, `CrossArmRelease(6)`.
- Paths and file names remain the current `res://Frames/<group>/<name>.png`
  convention. Missing or wrong-sized resources fail startup/smoke; there is no
  fallback image and no Waving resource.
- The scene-node contract remains `Main/Character` and `Main/DialogueUi`.
  Renaming either is a breaking internal integration change and is not part of
  remediation.

### Animation and dialogue

- Keep `DirectionalTurnStateMachine.Advance(leftHeld, rightHeld, delta,
  walkPlaybackMultiplier)` as the behavioral authority. Turn timing is fixed;
  the multiplier affects walk cadence and movement only.
- Keep clap and cross-arm sequence constants in the state machine; rendering
  selects textures from state and must not duplicate transition logic.
- Keep dialogue submission bounded to 500 Unicode scalar values before
  normalization. Empty normalized text closes without replacing the bubble.
- Input precedence is:
  1. app-global fullscreen shortcuts;
  2. dialogue submit/cancel/hide;
  3. character gesture/speed input;
  4. continuously sampled directional movement.
- `F11` and `Alt+Enter` toggle fullscreen whether dialogue is open or closed.
  `Alt+Enter` never reaches dialogue submission. `Escape` cancels dialogue when
  editing; otherwise it exits fullscreen. Character actions remain suppressed
  while editing.

`GlobalInputPolicy` shall expose a Godot-independent decision:

```csharp
GlobalInputAction Resolve(
    GlobalInputKey key,
    bool altPressed,
    bool dialogueEditing,
    bool fullscreen);
```

`GlobalInputAction` is `None`, `ToggleFullscreen`, or `ExitFullscreen`.
`GlobalInputKey` needs only `F11`, `Enter`, `Escape`, and `Other`. This is an
internal test seam, not a public product API.

### Capture and command-line modes

- `--verify-runtime` is read-only and mutually exclusive with every
  `--capture-*` argument. Success/failure markers and process exit codes are
  authoritative.
- Capture requires one valid `--capture-frame` and one valid
  `--capture-path=<direct-child.png>`; invalid or incompatible mode
  inputs fail before normal interaction begins.
- Startup/mode validation failures shall emit a stable failure marker and call
  `GetTree().Quit(1)`; they must not rely on an unhandled `_Ready` exception to
  terminate the engine.
- Publication remains staging-file + flushed close + same-directory
  no-overwrite move. It never deletes or replaces an existing final target.

### Extraction, validation, and export

- Each source sheet is immutable by SHA-256, format/mode, and dimensions.
  Normal extractor runs may rewrite only the 33 active PNGs. Evidence requires
  explicit `--evidence` and stays under ignored `.ai-org`.
- Determinism proof runs every official extractor twice in separate Python
  processes, snapshots all six source hashes before/after, and compares both
  decoded pixels and encoded PNG bytes for all 33 outputs.
- `validate_release.py` remains read-only and keeps its exact terminal success
  marker. It may inspect `Build` if present but never extracts the PCK.
- `export_presets.cfg` remains an ordered allowlist of 34 resources. The
  allowlist, not broad excludes, is the package security boundary.
- `export-release.ps1` cleans only project-local ignored `Build`, exports the
  Windows preset, verifies `Build/TCFAnimation.exe` exists, and returns the
  Godot failure code. The release gate then runs the read-only validator and
  the exported `--verify-runtime` smoke as separate fail-closed steps.

### Compatibility

- No external API, schema, save data, or persistent data changes exist.
- Current controls, resource names, CLI argument names, success markers, and
  export preset name remain compatible.
- The new pure input policy and assertion count are internal additions.

## Concrete gaps requiring contained remediation

1. **Fullscreen is currently suppressed during dialogue editing.**
   `TurnController` returns on `IsEditing` before evaluating F11/Alt+Enter
   (`TurnController.cs:182-200`), while `DialogueUi` deliberately leaves
   Alt+Enter unhandled (`DialogueUi.cs:57-72`). This violates the required
   dialogue-open fullscreen combinations. Route app-global shortcuts before
   the character-suppression check and cover them through `GlobalInputPolicy`.
2. **ControllerProbe has no numeric assertion count and no pure AC-10 seam.**
   It prints named coverage only (`ControllerProbe/Program.cs:6-33`), and its
   assertion helpers do not increment shared state
   (`ControllerProbe/Program.cs:1437-1695`). Increment exactly once per passed
   assertion and print `assertions=<positive integer>`; link/test the pure
   global-input policy.
3. **Invalid capture/startup arguments do not have deterministic termination.**
   `_Ready` catches only when runtime verification is active
   (`TurnController.cs:43-87`), but capture parsing can throw multiple argument
   or path exceptions (`TurnController.cs:451-510`). Add a capture-mode startup
   failure boundary that logs a stable marker and exits nonzero.
4. **The export success marker can be emitted without an explicit output
   existence check.** `export-release.ps1` checks Godot's exit code and then
   prints success (`export-release.ps1:47-54`). Require the expected executable
   to exist before success; keep manifest validation and runtime smoke as
   separate post-export gates.
5. **Godot resolution is not one consistent supported-version contract.**
   `run-animation.bat` prefers bundled Godot before `GODOT_EXE`
   (`run-animation.bat:6-16`), while export prefers `GODOT_EXE`
   (`export-release.ps1:20-29`), and neither proves the selected executable is
   Godot 4.5.1 Mono. Make precedence explicit and consistent, reject an invalid
   configured executable clearly, and record/check the selected version in
   launcher/export E2E.
6. **Fresh-process two-pass determinism and actual exported E2E are not yet
   evidenced.** The validator proves in-memory regeneration against disk and
   built artifacts when present (`validate_release.py:658-883,1204-1257`), but
   R-12/R-17/R-19 still require external process orchestration and manual QA.
   This is a validation-plan gap, not a runtime redesign.

## Failure model and observability

- Missing nodes/resources or wrong texture dimensions fail the runtime smoke;
  normal release is blocked by validator/smoke before QA.
- Invalid state-machine arguments throw immediately in the pure core and are
  covered by `ControllerProbe`.
- Extractor source/hash/geometry/output drift fails before a pass marker.
- Export engine resolution, export exit, missing executable, package manifest,
  and runtime smoke are independent fail-closed gates.
- Required stable markers: `CONTROLLER_PROBE_PASS assertions=N`,
  `ASSET_RELEASE_CHECK_PASS ...`, `RELEASE_EXPORT_PASS ...`,
  `RUNTIME_SMOKE_PASS ...`, `CAPTURE_SAVED ...`, and a stable capture/startup
  failure marker. No external telemetry is required for this local app.

## Security and reliability

- Trust boundaries are command-line input, filesystem paths, immutable source
  sheets, and the export resource allowlist. Dialogue text is local display
  data only and is neither persisted nor transmitted.
- Capture containment, device-name rejection, reparse checks, exclusive staging
  creation, flush, and no-overwrite publication are retained.
- The same-user path-swap TOCTOU race documented in R-14/A-01 remains accepted
  for this developer-only mode. This mission does not broaden that tradeoff or
  expose capture as a network/service feature.
- Export allowlisting limits accidental data disclosure from source sheets,
  Waving/unrelated root files, mission evidence, source code, and debug paths.

## Validation strategy

1. Build Debug/Release; format; scoped `git diff --check`.
2. Run `ControllerProbe` and require positive assertion count plus named
   coverage for R-03/05/06/07/08/09/10/14.
3. Snapshot source and active-frame hashes; run all four extractors twice in
   fresh processes without `--evidence`; compare source hashes, output set,
   decoded pixels, and PNG bytes.
4. Run read-only validator and its negative controls.
5. Run Godot 4.5.1 Mono headless import/startup.
6. From an unrelated cwd, test both launchers, clean export, output existence,
   post-export validator, and actual exported `--verify-runtime`.
7. Run Windows capture positive/negative/reparse/permission/publication cases.
8. Execute exported-runtime interaction journeys and the complete 33-frame
   visual matrix. Store evidence in the mission directory only.

## Tradeoffs and alternatives

- **Optimized for:** preserving proven work, deterministic release contents,
  pure-core testability, minimal blast radius, and fail-closed local tooling.
- **Given up:** a generalized animation graph, data-driven manifest system,
  plugin framework, or reusable launcher library. The project is too small to
  justify those abstractions.
- **Rejected — rewrite around Godot AnimationPlayer/AnimationTree:** it would
  replace working state semantics and expand regression risk without solving
  extraction, capture, or package hygiene.
- **Rejected — broad export plus deny filters:** new or unrelated assets could
  leak by default; the exact allowlist is safer and already implemented.
- **Rejected — runtime texture scanning for edge geometry:** calibrated
  constants are deterministic, probeable, and validated against extraction.
- **Rejected — new test framework/service:** the existing console probe,
  Python validator, process-level Windows checks, and manual visual QA cover
  the requirements with lower complexity.

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| Input fixes cause duplicate handling | Preserve Godot handled-event semantics; test every dialogue/fullscreen matrix cell in probe and exported E2E |
| Capture startup error leaves engine running | One top-level mode boundary emits marker and quits with code 1 |
| Export succeeds with stale/extra files | Guarded clean, output existence check, read-only manifest validator |
| Extractor calibration changes visual anchors | Immutable sources, two-pass hashes, independent lower-anatomy validator, complete visual matrix |
| Waving/unrelated sheets leak into release | Exact 34-resource allowlist and built-manifest deny checks |
| Tool version drift changes output | Pinned Python packages; validate/report Godot 4.5.1 Mono selection |
| Accepted same-user capture race is exploited | Keep capture developer-only and project-local; do not claim OS-level isolation |

## Migration and rollback

There is no data/schema migration. Deliver incrementally: add the pure input
policy and probe assertions; correct runtime ordering/error boundaries; harden
launcher/export checks; then execute gates. Each remediation is backward
compatible and can be rolled back independently by reverting only that
mission-owned change; the recovered uncommitted frame/extraction work must not
be reset or regenerated blindly.

---

# Rework cycle 1 — authenticated tooling and isolated export

Date: 2026-09-04  
Status: PASS — contained remediation; no CTO decision required.

This section supersedes only the launcher trust and export-hygiene portions of
the original design. Runtime controls, capture behavior, scene composition,
the 33 accepted frames, the 34-resource allowlist, and final
`Build\TCFAnimation.exe` contract do not change.

## Current state — additional verified FACTs

- **FACT:** `run-animation.bat` expands `%GODOT_EXE%` into batch command text
  and executes it through `call` before any independent authenticity check
  (`run-animation.bat:11-18,47-54,80`).
- **FACT:** `export-release.ps1` treats `GODOT_EXE`, bundled, and `PATH`
  candidates as executable after only existence/resolution checks, then trusts
  the candidate's own `--version` output (`export-release.ps1:28-76`).
- **FACT:** The bundled console executable is 197,640 bytes with SHA-256
  `FD5C88FD05AFC7C2D965777320DEAEC5A80C31363C0BB33D7B48025376F96C5F`;
  its sibling editor executable is 163,933,704 bytes with SHA-256
  `C369B7B92C30100F3EEDE92410BD02A4BB024562860DEE94C33399BEA1C77C9B`.
- **FACT:** The bundled Windows release template is 96,965,120 bytes with
  SHA-256
  `9186C4AA21D659035A6BCC33BEA644D7399DDA42AC24F9E0D3A9E5965E92E00F`.
- **FACT:** The selected-resource preset currently declares empty
  `include_filter` and `exclude_filter` values, but the validator checks only
  `export_filter` and `export_files`, not those two filter assignments
  (`export_presets.cfg:8-12`;
  `FrameExtraction/validate_release.py:886-907`).
- **FACT:** package validation admits `.godot/uid_cache.bin` as engine metadata
  and excludes all engine metadata from denied-token inspection
  (`FrameExtraction/validate_release.py:1088-1119,1142-1147`). The security
  review found excluded Waving, root-sheet, and development names inside that
  packaged entry (`security-review.md`, “Exported UID metadata” finding).
- **FACT:** the validator and all four source extractors compute source hashes
  with `Path.read_bytes()`, allocating in proportion to file size
  (`FrameExtraction/validate_release.py:262-263`;
  `extract_directional_turns.py:163-164`;
  `extract_right_walk.py:142-143`; `extract_clap.py:144-145`;
  `extract_cross_arm.py:195-196`).
- **FACT:** the six immutable source byte sizes are: `LTurning.png` 1,502,641;
  `RTurning.png` 1,390,487; `RWalking2.png` 495,754; `Clapping2.png`
  654,450; `CrossArm3.png` 628,721; and `CrossArm4.png` 632,179 bytes.
- **FACT:** `Main.tscn` directly references only `TurnController.cs` and
  `DialogueUi.cs`; the controller loads only the seven established frame
  groups under `res://Frames` (`Main.tscn:1-13`;
  `TurnController.cs:19-31,41-79,317-346`).
- **FACT:** independent automated tests passed 508/508, so this rework must
  preserve behavior and invalidate only launcher/export/security/package
  evidence (`test-results.md`, Counts and Result sections).

## Desired state

The release path has three explicit trust boundaries:

1. **Tool selection:** candidate paths are untrusted data. A candidate becomes
   executable only after canonicalization, exact-size precheck, streaming
   SHA-256 comparison with repository-pinned records, and required-companion
   verification. Self-reported version is checked only after authenticity.
2. **Export project:** Godot imports and exports from a newly created,
   release-owned project containing only an exact staging manifest. It never
   opens the development project's `.godot` cache, `.import` sidecars, source
   sheets, Waving/root assets, probes, tooling, or mission files.
3. **Artifact validation:** the validator checks the preset's empty filters,
   the exact staging manifest, all PCK metadata contents for denied inventory
   tokens, and the existing resource/managed payload contracts.

No service, framework, datastore, or third-party library is added.

## Delta and component contracts

### Authenticated Godot selection

Add these release-tooling files:

- `release-provenance.json` — repository-pinned schema version 1, supported
  version `4.5.1`, required dot token `mono`, approved executable records, and
  the Windows release-template record.
- `release-tooling.ps1` — side-effect-free function definitions for bounded
  hashing, candidate resolution/authentication, version parsing, and staging.
- `run-animation.ps1` — PowerShell runtime-launch entry point.
- `release-stage-manifest.txt` — ordered, exact project-copy manifest.

`release-provenance.json` shall contain these executable identities:

| Kind | Exact size | SHA-256 | Additional requirement |
|---|---:|---|---|
| Godot console | 197640 | `FD5C88FD05AFC7C2D965777320DEAEC5A80C31363C0BB33D7B48025376F96C5F` | sibling editor executable must match the next row |
| Godot editor | 163933704 | `C369B7B92C30100F3EEDE92410BD02A4BB024562860DEE94C33399BEA1C77C9B` | none |
| Windows release template | 96965120 | `9186C4AA21D659035A6BCC33BEA644D7399DDA42AC24F9E0D3A9E5965E92E00F` | required before export, relative to the approved Godot distribution |

The shared PowerShell interface is binding:

```powershell
Get-ApprovedGodot -ProjectRoot <absolute path> -Purpose Run|Export
# -> PSCustomObject:
#    Executable, Source (GODOT_EXE|bundled|PATH), Version, Sha256

Get-StreamingSha256 -LiteralPath <absolute file> -ExpectedLength <int64>
# -> uppercase 64-character SHA-256; size mismatch fails before hashing
```

Selection precedence remains `GODOT_EXE`, bundled, `godot4`, then `godot`.
`GODOT_EXE` remains authoritative. Resolution accepts only a canonical regular
`.exe`; scripts, aliases, functions, reparse-point candidates, directories,
and unapproved bytes fail with `GODOT_RESOLUTION_FAIL` and exit 1. The first
resolved candidate at a precedence level is not executed or silently bypassed
when provenance fails. For a console candidate, its approved sibling editor
binary is verified before the console wrapper executes. Export additionally
verifies the approved template before cleaning or staging.

Only after all applicable hashes pass may the helper invoke `--version`.
Version output remains exactly one nonempty line, exact `4.5.1`, and a
dot-delimited `mono` token. Success keeps the existing `GODOT_SELECTED` fields
and may append `sha256=<approved hash>`.

`run-animation.bat` becomes a thin compatibility shim. It shall not read,
test, echo, expand, or execute `GODOT_EXE` and shall not use `call`; it invokes
the literal system executable
`C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe` with
`-NoProfile -NonInteractive -ExecutionPolicy Bypass -File
"%~dp0run-animation.ps1"` and returns that process exit code.
`run-animation.ps1` reads `$env:GODOT_EXE` as data and invokes the approved
binary with a PowerShell argument array. Arbitrary-cwd behavior is retained by
deriving the project root from `$PSScriptRoot`.

### Isolated export staging

`export-release.ps1` continues to own guarded cleanup of project-local
`Build`, but it exports from `Build\.release-stage-<GUID>\project`, not from
the development project. It creates the stage only after Godot and template
provenance succeed and after the guarded Build clean.

`release-stage-manifest.txt` contains only:

- `project.godot`, `Main.tscn`, `TCFAnimation.csproj`,
  `export_presets.cfg`;
- `AnimationGeometry.cs`, `CapturePathPolicy.cs`, `DialogueModel.cs`,
  `DialogueUi.cs`, `DirectionalTurnStateMachine.cs`,
  `GlobalInputPolicy.cs`, `TurnController.cs`;
- the existing `.uid` sidecars for all listed C# files that currently have
  one; absence of a `GlobalInputPolicy.cs.uid` is intentional and any generated
  UID remains inside the stage;
- the exact current 33 PNG paths in the seven runtime frame groups.

The manifest permits no glob, directory entry, parent segment, absolute path,
duplicate, symlink/reparse point, `.godot`, `.import`, Build, Captures,
Waving/root sheet, source sheet, extractor, probe, README, tooling, or mission
entry. Copy uses literal paths and never follows reparse points. Before Godot
runs, recursive stage enumeration must equal the manifest exactly.

The export sequence is:

1. authenticate Godot, companion, and release template;
2. guarded clean/create `Build`;
3. create a unique stage with exclusive ownership;
4. copy and enumerate the exact manifest;
5. run approved Godot `--headless --path <stage> --import`;
6. run approved Godot `--headless --path <stage> --export-release
   "Windows Desktop" <root>\Build\TCFAnimation.exe`;
7. require zero exit and a nonempty final executable;
8. remove the exact stage before emitting `RELEASE_EXPORT_PASS`.

The stage deliberately starts without `.godot` or `.import` files. Godot
therefore builds an import cache and UID cache from release-owned files only.
The selected-resource preset and managed-code compilation behavior remain
unchanged because the stage retains the same relative project, scene, script,
and frame paths.

On any failure, emit one stable `RELEASE_EXPORT_FAIL reason=<token>` when the
failure is owned by orchestration; preserve a nonzero Godot exit code for
import/export process failures. A `finally` block removes only the exact
GUID-named stage. No success marker is allowed while a stage remains. If stage
cleanup fails, fail closed and guarded-clean the generated Build tree so a
source-bearing stage cannot be mistaken for a release. No file outside Build
is deleted or modified.

### Validator, metadata, and bounded hashing

`validate_release.py` shall parse exactly one `include_filter` and one
`exclude_filter` assignment in preset 0 and require both values to be the
empty string. Its pure preset-text validator shall run in-memory negative
controls for nonempty include and exclude filters.

Change the PCK manifest value from size-only to a bounded entry descriptor
containing data offset and size. Add a bounded entry reader and scan the
contents of all engine metadata and all `*.import` metadata, normalizing `\`
to `/` and checking case-insensitive UTF-8/ASCII and UTF-16LE forms. Reject at
least these path/name classes:

- `Waving` and every unrelated root sheet:
  `Avatar.jpg`, `Clapping.png`, `CrossArm.png`, `CrossArm2.png`,
  `WalkRightSheet.png`;
- all six immutable source-sheet names;
- `ControllerProbe/`, `FrameExtraction/`, `.ai-org/`, `.tools/`, `.vs/`,
  `Build/`, `Captures/`, `README.md`, `requirements.txt`,
  `run-animation`, and `export-release`.

Each metadata entry is capped at 4 MiB and aggregate inspected metadata at
16 MiB; unreasonable sizes fail before allocation. Add an in-memory negative
control containing `res://Waving.png` and a development path and require the
metadata scanner to reject it. Actual isolated exports must still contain the
expected four engine metadata entries, but their contents must be free of all
denied tokens.

Add `FrameExtraction/file_integrity.py` with:

```python
def sha256_file(path: Path, expected_size: int, chunk_size: int = 1 << 20) -> str:
    """Fail on non-regular/size mismatch, then return uppercase streaming SHA-256."""
```

All six source contracts gain the exact byte sizes recorded above. The
validator and four extractors use this helper before Pillow/OpenCV opens a
source and for their post-run immutability check. A size mismatch fails before
content hashing or native image decoding. Accepted output-frame byte
comparisons may remain bounded in memory because their dimensions/count are
already constrained.

Hash-lock release Python tooling in `requirements.txt` (or a single
authoritative `requirements-release.lock` referenced by README) with exact
versions, approved index URL, and SHA-256 hashes, and install with
`--require-hashes`. This closes the existing Low supply-chain finding without
adding a package or service.

## Compatibility and observability

- Product/runtime APIs, controls, CLI user arguments, capture contract,
  scene/resource paths, animation bytes, and final output path are unchanged.
- Existing markers remain stable:
  `GODOT_RESOLUTION_FAIL`, `GODOT_SELECTED`,
  `RELEASE_EXPORT_FAIL`, `RELEASE_EXPORT_PASS`, and
  `ASSET_RELEASE_CHECK_PASS`.
- Add reason tokens sufficient to distinguish `unapproved_hash`,
  `size_mismatch`, `companion_unapproved`, `template_unapproved`,
  `stage_manifest_invalid`, `stage_import_failed`, and
  `stage_cleanup_failed`. Do not log file contents, environment values other
  than the already selected canonical executable path, or local source data.

## Required validation

1. Crafted metacharacter/quote `GODOT_EXE` values create no sentinel and exit
   1 through the batch shim.
2. A fake executable that prints an accepted version and a one-byte-mutated
   copy of the official executable are rejected before first execution.
3. Approved bundled, approved configured-copy, and approved PATH-copy cases
   select 4.5.1 Mono and preserve child exit codes from an unrelated cwd.
4. Missing/invalid authoritative `GODOT_EXE` never falls back.
5. Export succeeds only with approved engine, companion, and release template;
   mutations of each fail before Build cleanup.
6. Source project `.godot/uid_cache.bin` and unrelated root/Waving files may
   exist, but the isolated package metadata contains none of their denied
   tokens. The 34-resource/33-frame and managed payload checks remain exact.
7. In-memory filter controls reject nonempty include and exclude filters; the
   metadata control rejects Waving and development tokens.
8. A sparse/oversized source substitute fails the exact-size check without
   proportional allocation or Pillow/OpenCV invocation; all six real sources
   and two-pass 33-frame determinism still pass.
9. Import failure, export failure, missing output, and cleanup failure emit no
   success marker, leave no stage, and do not modify any source/resource file.
10. Hash-locked dependency resolution succeeds with `--require-hashes`.

## Tradeoffs and rejected alternatives

- **Optimized for:** pre-execution authenticity, parser-safe environment
  handling, minimum package disclosure, deterministic release inputs, and
  preserving all product behavior.
- **Cost accepted:** only byte-identical approved Godot 4.5.1 Mono builds are
  selectable; a legitimate repackaged build requires a reviewed provenance
  update.
- **Rejected — version string or Authenticode alone:** version is supplied by
  the executable being evaluated, and a signer-only policy is broader than
  the exact approved release.
- **Rejected — remove `GODOT_EXE`/PATH support:** simpler, but needlessly
  breaks the existing configured/portable workflow when exact-byte approval
  can preserve it safely.
- **Rejected — sanitize batch metacharacters:** deny lists remain parser
  fragile; the batch file must not parse the value at all.
- **Rejected — delete or edit development UID caches before export:** that
  mutates developer state and can still race or miss metadata. Isolated
  construction provides a positive boundary.
- **Rejected — post-export binary string scrubbing:** it can corrupt packs and
  treats symptoms rather than controlling import provenance.
- **Rejected — broad staging copy with excludes:** a missed exclude recreates
  the leak; an exact manifest matches the existing positive export policy.

## Risks and mitigation

| Risk | Mitigation |
|---|---|
| Approved console wrapper loads a substituted sibling | Verify the approved sibling editor executable before the wrapper executes |
| Export template substitutes output after engine approval | Pin and verify the release template before Build cleanup/export |
| Stage misses a compile/runtime dependency | Exact manifest includes every current root C# compile item and all 33 frames; raw-stage import, export, validator, and runtime smoke are required |
| Stage cleanup leaves source in Build | No success before cleanup; cleanup failure fails closed and guarded-cleans Build |
| PCK metadata parser allocates hostile sizes | Per-entry and aggregate caps before reads |
| Strict hashes reject a legitimate future Godot patch | Intentional fail-closed behavior; update provenance in a reviewed change |
| Lock file drifts from documented requirements | One authoritative release lock and a validator/CI parse check |

## Migration and rollback

There is no data, schema, save, or public contract migration. Forward
migration is a tooling-only replacement: land provenance/helper/shim first,
then isolated export, then validator/hash/lock changes, and rerun every
invalidated release/security gate. Rollback is file-level reversion and Build
regeneration; it does not touch frames or source sheets. The previous launcher
and development-project export are not releasable rollback modes because they
restore the two High findings and UID leak.

# RW2 amendment — deterministic stage-local solution

Date: 2026-09-04  
Status: PASS — contained correction to ADR-005; no CTO decision required.

This amendment supersedes only the RW2 clauses that required the complete
pre-import stage to remain identical to the 50 copied files and that proceeded
directly from seed validation to Godot import. The 50-file copied-source
allowlist, selected 34-resource export, final output path, and all application
contracts remain unchanged.

## Current state — verified FACTs

- **FACT:** the manifest and `Get-FrozenReleaseStageEntries` contain exactly
  the same 50 ordered files and omit `TCFAnimation.sln`
  (`release-stage-manifest.txt:1-50`; `release-tooling.ps1:654-708`).
- **FACT:** `New-ReleaseStage` copies only those literal entries and proves
  recursive file-set equality before returning
  (`release-tooling.ps1:918-1063`).
- **FACT:** the repository solution cannot be a release seed because it
  includes both `TCFAnimation.csproj` and
  `ControllerProbe\ControllerProbe.csproj` (`TCFAnimation.sln:6-9`).
- **FACT:** the production exporter currently stops after import when the
  stage has no `TCFAnimation.sln`, emits `stage_export_failed`, and therefore
  cannot publish the known false-positive export (`export-release.ps1:128-151`).
- **FACT:** the RW2 reproduction proved that Godot 4.5.1 Mono import returns
  zero without generating a solution or managed output; export without the
  solution returns zero while embedding C# source and produces a crashing
  executable. The same stage with a generated solution containing only
  `TCFAnimation.csproj` produced one-byte script placeholders and passed
  runtime smoke (`evidence/rework/RW2/blocker.md:21-103`).
- **FACT:** release validation already rejects script entries larger than the
  one-byte placeholders and requires a `TCFAnimation.dll` in the built output
  (`FrameExtraction/validate_release.py:1150-1184`).

## Desired state and delta

Keep the positive boundary as two separately proven inventories:

1. **Copied seed:** exactly the existing 50 manifest entries, byte-for-byte
   copied from the project. No solution, probe, tool, cache, or generated file
   is copied.
2. **Release-owned generated input:** after the exact seed proof and before
   Godot import, create exactly one additional file,
   `<stage-project>\TCFAnimation.sln`, from fixed bytes owned by
   `release-tooling.ps1`.

Add this shared function:

```powershell
New-ReleaseStageSolution `
    -BuildRoot <absolute guarded Build path> `
    -StageRoot <exact stage root returned by New-ReleaseStage> `
    -StageProjectRoot <exact stage project root returned by New-ReleaseStage> `
    -ManifestEntries <validated 50-entry string[]>
# Creates TCFAnimation.sln with CreateNew semantics.
# Returns PSCustomObject: Path, Length, Sha256.
# Throws only a structured failure with Reason=stage_solution_failed.
```

The helper shall revalidate Build/stage containment and absence of reparse
points, prove the stage still equals the 50-file manifest, require the target
solution to be absent, and require the staged `TCFAnimation.csproj` to be a
regular non-reparse file. It shall not read or copy the repository
`TCFAnimation.sln`, invoke `dotnet`, derive content from the environment, or
enumerate another project. It writes UTF-8 without BOM, CRLF line endings, and
one final CRLF using exclusive `FileMode.CreateNew`/no-sharing semantics.

The exact generated content is:

```text
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "TCFAnimation", "TCFAnimation.csproj", "{C9221F19-51A6-4664-BC51-3755959B26FE}"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{C9221F19-51A6-4664-BC51-3755959B26FE}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{C9221F19-51A6-4664-BC51-3755959B26FE}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{C9221F19-51A6-4664-BC51-3755959B26FE}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{C9221F19-51A6-4664-BC51-3755959B26FE}.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
	GlobalSection(SolutionProperties) = preSolution
		HideSolutionNode = FALSE
	EndGlobalSection
EndGlobal
```

Its canonical byte contract is length `994` and SHA-256
`FE3E86D84D18FD948E059483F5CB09B956E4F7B91A0B33FACD337727A2F4EC79`.
After writing, the helper must reread through the existing bounded hash path,
verify that exact length/hash, and prove the pre-import stage file set is
exactly the 50 manifest entries plus `TCFAnimation.sln` (51 files). Any
existing, extra, missing, changed, linked, or differently encoded solution
fails closed.

The amended export sequence is:

1. authenticate Godot, companion, and release template;
2. guarded-clean/create `Build`;
3. create/copy/prove the exact 50-file seed;
4. generate and verify the one fixed stage-local solution; prove the exact
   51-file pre-import inventory;
5. run approved Godot import and export from the stage;
6. require each child exit to be zero and reject any captured Godot diagnostic
   line whose severity begins `ERROR:`;
7. require a regular non-reparse, nonempty
   `Build\TCFAnimation.exe` and a regular non-reparse, nonempty
   `Build\data_TCFAnimation_windows_x86_64\TCFAnimation.dll`;
8. remove the exact stage, prove no `.release-stage-*` remains, recheck both
   output files, and only then emit one `RELEASE_EXPORT_PASS`.

A zero-exit Godot process with an error-severity diagnostic, missing/empty
executable, or missing/empty managed assembly is an orchestration failure.
Use `stage_import_failed` or `stage_export_failed` for the corresponding
Godot diagnostic failure, `missing_output` for either missing/invalid final
file, and `stage_solution_failed` for creation or validation of the generated
solution. A nonzero Godot child exit remains unchanged; an exit-zero
false-positive returns 1. Existing single-marker and cleanup semantics remain.

Godot stdout and stderr must be redirected, concurrently drained, and relayed
without waiting for one stream to finish before reading the other. Retain only
the exit code, an error-line flag/count, and a bounded diagnostic tail for
failure reporting; do not accumulate unbounded child output in memory. Import
and export keep distinct diagnostic state so the failure reason identifies the
owning phase.

## Cross-cutting behavior

- **Idempotency/concurrency:** every run owns a GUID stage and creates the
  solution only with `CreateNew`; no shared generated solution exists.
- **Security/trust:** the repository solution and `ControllerProbe` never
  cross the staging boundary. Fixed code-owned bytes are not untrusted input.
- **Observability:** evidence records seed count/hash proof, generated
  solution length/hash, post-generation count, child exit/error count, final
  executable and managed-assembly sizes, cleanup result, validator result,
  and runtime-smoke marker; it must not record source content.
- **Cleanup:** the solution is never copied back or separately retained. It
  is deleted only as part of guarded exact-stage removal, including every
  failure path.
- **Compatibility:** no runtime/API/data/config schema changes occur. The
  final artifact path and 50-file manifest remain byte-for-byte unchanged.

## Tradeoffs, risks, and migration

- **Chosen:** one fixed generated `.sln` after seed proof. This is the smallest
  change that satisfies Godot Mono without widening copied inputs or adding a
  `dotnet` dependency.
- **Rejected:** copy the repository solution, because it imports the excluded
  probe contract; add a 51st manifest file, because that weakens the exact
  copied-source boundary; invoke `dotnet new sln`, because output and tool
  version would become extra release inputs; continue trusting exit code plus
  a nonempty executable, because the blocker proves that contract false.
- **Risk:** a future Godot/MSBuild version may require different solution
  syntax. Mitigation is intentional fail-closed hash/semantic evidence and a
  reviewed amendment alongside provenance updates.
- **Risk:** a successful engine may emit an error-severity line for a newly
  tolerated condition. Mitigation is to investigate and explicitly amend the
  contract rather than silently accept ambiguous output.

Forward migration changes only `release-tooling.ps1`, `export-release.ps1`,
RW2 tests/evidence, and these mission documents. Rollback removes the helper
and restores the current fail-closed blocker; it must not restore
development-project export or copy the repository solution.

## RW4 approved-index amendment

Date: 2026-09-04. Status: Accepted.

Because direct `files.pythonhosted.org` TLS is unavailable in the validation environment, the machine-configured HTTPS Microsoft PyPI proxy (`https://packagefeedproxy.microsoft.io/pypi/simple/`) is the sole approved release index. It is not an automatic fallback. Release dependency acquisition must keep exact versions, CPython 3.13 Windows x86-64 wheels only, and all frozen SHA-256 hashes. Evidence commands must disable ambient pip configuration/overrides, download only from the explicit proxy into an isolated wheelhouse, verify the exact artifact hash set, then perform the installation check offline with `--no-index --find-links` and `--require-hashes`. No `--trusted-host`, disabled TLS, extra index, source distribution, unhashed artifact, or repository credential is allowed. Runtime/export behavior is unchanged.

# Rework cycle 2 — exported UX and visual quality

Date: 2026-09-04. Status: Accepted; no CTO decision required.

## Binding contracts

1. **Capture root:** add a pure capture-root resolver. Development runs use a nonempty filesystem globalization of `res://`; standalone/exported runs validate `AppContext.BaseDirectory` against the fixed Godot managed-data layout and resolve its direct parent/current executable directory as the portable release root, publishing only under that root's direct child `Captures`. The resolved root is computed once and reused for validation, staging, reparse checks, and publication. Missing, unsafe, unexpected-layout, reparse, or unwritable roots fail closed without fallback. Read-only installations must relocate the portable release rather than silently redirect.
2. **Viewport fit:** keep all gameplay/dialogue logic in the 1920x1080 logical coordinate space. Configure a 1280x720 window override with `canvas_items` stretch and aspect `keep`, so smaller and non-16:9 physical clients uniformly fit/letterbox without clipping or distortion. Do not feed physical pixels into movement or dialogue layout.
3. **Artifact removal:** immutable source sheets remain untouched. Add deterministic per-pose source-coordinate exclusion masks for every proven floor/reflection/detached-fragment region, projected through existing transforms before foreground composition. Strengthen validator-owned silhouette negatives inside current broad sole envelopes; retained footwear positive anchors remain mandatory. Do not brighten, recolor, synthesize, or blend shoe pixels.
4. **Front continuity:** `RightTurn/turn_0` is the canonical front image. Regeneration makes `LeftTurn/turn_0` byte-identical to it while preserving three files per turn group and existing runtime names.
5. **Cross/release continuity:** use a cleaned canonical front lower-body plate below a deterministic seam for `cross_02` and `release_00..05`; align upper bodies from final post-mask visible anchors, not configured crop centers. Candidate release x corrections begin `[+18,+11,+10,+14,+11,+9]` and must be recalibrated after compositing.
6. **Continuity thresholds:** stationary transitions require head and torso centroid deltas <=4 px, shoe baseline delta <=2 px, and stationary lower-band difference <=12%. Walking/turning transitions may allow head <=6 px, torso <=14 px, landmark movement <=10 px, shoe baseline <=16 px, and visible body-height delta <=18 px where motion is deliberate. All nonblack bounds remain strictly inside 512x864.
7. **Dialogue evidence:** no production change without a reproduced defect. QA must use an unlocked foreground session or trustworthy real-control automation with PID/focus verification (for example clipboard/UI Automation into the real LineEdit), not locked-session raw `PostMessage`, and must prove ASCII, Arabic, emoji, 500/501 scalars, submit/cancel/hide, and F11/Alt+Enter/Escape arbitration.

## Validation changes

- Add failing-before/fixed-after capture tests including one successful exported capture before invalid/no-overwrite cases.
- Test windowed/fullscreen fits at 1920x1080, 1536x960, 1536x864, 1280x720, and a non-16:9 client; center/edges/bubbles must remain in-client with uniform aspect.
- Add exact defect-ROI black checks plus positive footwear anchors, synthetic in-envelope floor block and detached cool-fragment rejection.
- Add pixel identity for both front files, cross/release plate integrity, stationary/walking boundary thresholds, and all 45 boundary measurements.
- Any frame regeneration invalidates deterministic hashes, visual QA, export/package validation, code/security review where affected, and final judgment.
