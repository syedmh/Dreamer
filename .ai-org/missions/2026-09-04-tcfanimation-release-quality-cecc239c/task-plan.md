# TCFAnimation Release-Quality Task Plan

Date: 2026-09-04  
Status: REWORK CYCLE 1 — RW1-RW4 SELF-VERIFICATION PASS; INDEPENDENT GATES PENDING  
Scope: `C:\Users\syedhu\source\repos\Dreamer\TCFAnimation` plus this mission
directory only.

The recovered uncommitted `TCFAnimation` diff is authoritative. No task may
reset, replace, broadly regenerate, or discard it. `Avatar.jpg`,
`Clapping.png`, `CrossArm.png`, `CrossArm2.png`, `WalkRightSheet.png`,
`Waving*.png`, and their sidecars are unrelated untracked files: do not edit,
delete, stage, load, validate as runtime resources, or export them. Waving must
remain absent from runtime state, controls, frame manifests, and release
resources.

## Frozen implementation contracts

These contracts are fixed before implementation. A developer may not change
them during a parallel task. Any required contract change returns to the Tech
Lead before further implementation.

### F1 — Pure global-input policy

Add `TCFAnimation/GlobalInputPolicy.cs` with no Godot dependency:

```csharp
namespace TCFAnimation;

public enum GlobalInputAction
{
    None,
    ToggleFullscreen,
    ExitFullscreen,
}

public enum GlobalInputKey
{
    F11,
    Enter,
    Escape,
    Other,
}

public static class GlobalInputPolicy
{
    public static GlobalInputAction Resolve(
        GlobalInputKey key,
        bool altPressed,
        bool dialogueEditing,
        bool fullscreen);
}
```

The decision table is binding:

| Input | Dialogue | Window state | Result |
|---|---|---|---|
| F11, with or without Alt | open or closed | either | `ToggleFullscreen` |
| Alt+Enter | open or closed | either | `ToggleFullscreen` |
| Escape | closed | fullscreen | `ExitFullscreen` |
| Escape | closed | windowed | `None` |
| Escape | open | either | `None` (owned by `DialogueUi`) |
| Enter without Alt or `Other` | open or closed | either | `None` |

`TurnController._UnhandledKeyInput` shall validate pressed/non-echo key input,
map the physical key, resolve this policy, and execute a non-`None` global
action **before** checking `DialogueUi.IsEditing`. It shall call
`SetInputAsHandled()` exactly once for either global action and return. Only
after a `None` result may dialogue editing suppress character controls.
`DialogueUi` keeps ownership of dialogue submit/cancel/hide and continues to
leave Alt+Enter unhandled. No other key gains handled semantics.

### F2 — ControllerProbe assertion accounting

`ControllerProbe.csproj` shall link `..\GlobalInputPolicy.cs`. The probe shall
cover every row of F1, including the four dialogue/window combinations for
F11, Alt+Enter, and Escape.

One shared assertion counter shall increment exactly once only after each
assertion helper succeeds. This includes `AssertBubbleSize`, `AssertPose`,
`AssertWalk`, `AssertClap`, `AssertCrossing`, `AssertCrossedHold`,
`AssertReleasing`, `AssertWalkReset`, `AssertNotWalking`, `AssertChanged`,
`AssertTrue`, `AssertEqual`, and the matching-exception path of
`AssertThrows`. Failed assertions do not increment. The terminal line remains
one line and starts:

```text
CONTROLLER_PROBE_PASS assertions=<positive integer>
```

The existing named capability fields remain, with `global_input=true` added.
The numeric value must equal the number of successful assertion-helper
invocations in that run.

### F3 — Startup and capture-mode failures

The recognized developer capture-mode arguments remain:

- `--capture-frame=<integer>`
- `--capture-path=<direct-child.png>`
- optional `--capture-direction=left|right`
- optional `--dialogue-preview=short|long|left|right|input`

Any capture-family or dialogue-preview argument activates capture-mode
validation. Exactly one frame and one path are required; each optional
argument may occur at most once. Duplicate options, unknown `--capture-*`
options, malformed values, missing pairs, unsafe/existing paths, and
`--dialogue-preview` without a capture pair fail before normal interaction.
`--verify-runtime` is mutually exclusive with every `--capture-*` argument and
with `--dialogue-preview`.

Runtime verification failures retain `RUNTIME_SMOKE_FAIL`. Capture publication
I/O failures retain `CAPTURE_FAILED`. Invalid startup/capture-mode parsing uses
the distinct stable prefix:

```text
CAPTURE_MODE_FAIL error=<ExceptionType> message=<single-line message>
```

It must set the terminating guard, call `GetTree().Quit(1)`, perform no
capture, and never enter normal interaction. Valid no-mode startup is
unchanged.

### F4 — Godot selection and release export

Both launchers use the same source precedence:

1. `GODOT_EXE`, when non-empty;
2. the project-local bundled Godot 4.5.1 Mono executable;
3. `godot4` on `PATH`;
4. `godot` on `PATH`.

An explicitly configured `GODOT_EXE` is authoritative: if it is missing,
cannot execute, or is unsupported, fail without falling back. After selecting
any candidate, run `--version`; require exit code zero, exact major/minor/patch
`4.5.1`, and a dot-delimited `mono` token. Reject non-Mono, other versions, or
unparseable output before launch/export.

On success, both scripts emit:

```text
GODOT_SELECTED source=<GODOT_EXE|bundled|PATH> version=<reported version> executable=<resolved path>
```

Resolution/version failures emit a line beginning
`GODOT_RESOLUTION_FAIL` and exit 1. Both scripts remain rooted at their own
location and work from an unrelated current directory. `run-animation.bat`
returns the launched Godot process exit code.

`export-release.ps1` retains the guarded project-local `Build` cleanup. A
nonzero Godot export exit must be returned unchanged. After a zero export
exit, require `Build/TCFAnimation.exe` to be a nonempty file before printing:

```text
RELEASE_EXPORT_PASS output=<absolute path> clean_build=true godot_version=<reported version>
```

A missing/empty executable emits `RELEASE_EXPORT_FAIL reason=missing_output`
and exits 1. Manifest validation and exported runtime smoke remain separate,
subsequent fail-closed gates.

### F5 — Evidence ownership

Implementation tasks may edit only their listed project files. Validation
tasks are read-only with respect to source and may create only ignored
`TCFAnimation/Build`, `TCFAnimation/Captures`, or mission evidence under:

```text
.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/
```

No task may write extractor evidence into runtime/resource directories.

## Work packages

T1  Freeze remediation contracts and authoritative baseline
    owner:        tech-lead
    objective:    Convert the approved requirements/architecture and the six
                  contained gaps into collision-free implementation and
                  validation work, without changing the application.
    files:        .ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/task-plan.md
    depends_on:   -
    parallel_ok:  no
    exit_criteria: F1-F5 are explicit; every later task has bounded file
                  ownership, dependencies, commands/observable evidence, and
                  the authoritative uncommitted diff remains untouched.
    status:       DONE

T2  Remediate runtime input, probe accounting, and mode failure handling
    owner:        developer
    objective:    Close architecture gaps 1-3 exactly against F1-F3 while
                  preserving all current animation, dialogue, capture
                  publication, resource, and handled-event behavior.
    files:        TCFAnimation/GlobalInputPolicy.cs (new)
                  TCFAnimation/TurnController.cs
                  TCFAnimation/ControllerProbe/ControllerProbe.csproj
                  TCFAnimation/ControllerProbe/Program.cs
    depends_on:   T1
    parallel_ok:  no
    exit_criteria: Debug and Release builds pass; ControllerProbe passes with
                  `assertions=N`, N > 0, count matching successful helper
                  invocations, existing named fields, and
                  `global_input=true`; all F1 matrix rows are asserted;
                  process-level invalid capture/mode samples print
                  `CAPTURE_MODE_FAIL` and exit 1; valid normal, capture, and
                  runtime-verification modes retain their existing success
                  markers and behavior; scoped diff shows no edits outside
                  the four owned paths.
    status:       DONE
    evidence:     2026-09-04 developer self-verification (not an independent
                  gate): Debug and Release solution builds passed with 0
                  warnings/errors; ControllerProbe passed with
                  assertions=466 and 466 PASS helper lines plus
                  global_input=true; F1 matrix coverage is in the probe;
                  invalid capture-mode samples exited 1 with
                  CAPTURE_MODE_FAIL; verify-plus-capture/preview exited 1
                  with RUNTIME_SMOKE_FAIL; normal startup, raw-project runtime
                  smoke, and a valid windowed capture retained exit 0 and
                  their existing markers. dotnet format verify and scoped
                  diff check passed.

T3  Harden launcher resolution, export output proof, and operator documentation
    owner:        developer
    objective:    Close architecture gaps 4-5 exactly against F4 and document
                  the selected-engine contract without changing export
                  allowlisting or release policy.
    files:        TCFAnimation/run-animation.bat
                  TCFAnimation/export-release.ps1
                  TCFAnimation/README.md
    depends_on:   T2
    parallel_ok:  no
    exit_criteria: From an unrelated cwd, both scripts report the same
                  precedence and `GODOT_SELECTED` data; valid bundled and
                  valid `GODOT_EXE` cases accept only 4.5.1 Mono; missing,
                  non-Mono, wrong-version, and failing-version-command
                  candidates emit `GODOT_RESOLUTION_FAIL` and exit 1; an
                  invalid explicit `GODOT_EXE` never falls back; launcher
                  process exit propagation is proven; export returns the
                  Godot failure code, rejects a missing/empty exe after a
                  zero export exit, and prints `RELEASE_EXPORT_PASS` only
                  after the real nonempty exe exists; README agrees with F4;
                  no export preset/resource changes occur.
    status:       DONE
    evidence:     2026-09-04 developer self-verification (not an independent
                  gate): fake-engine process tests from an unrelated cwd
                  proved GODOT_EXE authority, 4.5.1 Mono acceptance,
                  wrong-version/non-Mono/version-command/missing configured
                  rejection, launcher exit 7 propagation, export exit 23
                  propagation, missing-output rejection, and pass only after
                  a nonempty exe. A real unrelated-cwd bundled export selected
                  4.5.1.stable.mono.official.f62fdbde1, created the nonempty
                  Build/TCFAnimation.exe, and printed RELEASE_EXPORT_PASS.
                  Post-export validator and actual exported runtime smoke
                  passed with their exact terminal markers.

T4  Prove two-pass fresh-process extraction determinism
    owner:        test-engineer
    objective:    Close the deterministic-extraction evidence portion of gap
                  6 without changing the accepted frame contents or immutable
                  sources.
    files:        .ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/determinism/*
    depends_on:   T3
    parallel_ok:  no
    exit_criteria: Record pre-run hashes for all six immutable sheets and all
                  33 active PNGs; run each of the four official extractors,
                  without `--evidence`, once per pass in two separate
                  fresh-process passes; after each pass record source hashes,
                  exact active-file inventory, encoded SHA-256, and a
                  canonical decoded-RGBA pixel hash for every frame; pass-1
                  and pass-2 manifests are identical, both match the pre-run
                  accepted 33-frame bytes/pixels, source hashes remain the
                  documented values, left walk pixels are exact mirrors, and
                  no non-frame or evidence artifact is created. Any mismatch
                  fails and stops later gates; do not regenerate blindly.
    status:       PENDING

T5  Run independent automated and runtime gate
    owner:        test-engineer
    objective:    Independently verify the remediated source, pure policies,
                  asset contracts, mode failures, and Godot startup before
                  exported visual QA.
    files:        .ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/tests/*
    depends_on:   T4
    parallel_ok:  yes
    exit_criteria: Record command, selected tool/version, exit code, and
                  relevant marker for Debug/Release builds, ControllerProbe,
                  `dotnet format ... --verify-no-changes --no-restore`,
                  project-scoped `git diff --check`, the read-only release
                  validator (including its built-in negative controls and
                  exact terminal marker), and Godot 4.5.1 Mono headless
                  import/startup from an unrelated cwd. Execute the F1 matrix
                  in the probe and an invalid-mode process matrix covering
                  verify-plus-capture/preview, missing pair, bad frame,
                  bad direction, bad preview, duplicate option, unknown
                  capture option, and unsafe/existing path. All invalid modes
                  exit 1 with the stable expected marker and no capture.
    status:       PENDING

T6  Review implementation correctness and diff preservation
    owner:        code-reviewer
    objective:    Independently review gaps 1-5 for correctness, handled-event
                  semantics, failure behavior, exit propagation, compatibility,
                  and preservation of the authoritative current diff.
    files:        read-only: TCFAnimation/GlobalInputPolicy.cs
                  read-only: TCFAnimation/TurnController.cs
                  read-only: TCFAnimation/DialogueUi.cs
                  read-only: TCFAnimation/ControllerProbe/ControllerProbe.csproj
                  read-only: TCFAnimation/ControllerProbe/Program.cs
                  read-only: TCFAnimation/run-animation.bat
                  read-only: TCFAnimation/export-release.ps1
                  read-only: TCFAnimation/README.md
                  evidence: .ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/code-review.md
    depends_on:   T4
    parallel_ok:  yes
    exit_criteria: APPROVED with no unresolved correctness/reliability blocker;
                  review explicitly confirms F1-F4, no Waving/runtime-resource
                  expansion, no weakened capture/export policy, and no edits
                  outside T2/T3 ownership.
    status:       PENDING

T7  Review security boundaries
    owner:        security-engineer
    objective:    Independently verify that mode parsing, capture safety,
                  executable selection, guarded cleanup, and selected-resource
                  export remain fail-closed.
    files:        read-only: TCFAnimation/CapturePathPolicy.cs
                  read-only: TCFAnimation/TurnController.cs
                  read-only: TCFAnimation/run-animation.bat
                  read-only: TCFAnimation/export-release.ps1
                  read-only: TCFAnimation/export_presets.cfg
                  read-only: TCFAnimation/FrameExtraction/validate_release.py
                  evidence: .ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/security-review.md
    depends_on:   T4
    parallel_ok:  yes
    exit_criteria: PASS with no unresolved Critical/High finding; explicit
                  confirmation that configured executable failure cannot
                  silently fall back, capture cannot escape/overwrite, cleanup
                  remains project-local, the 34-resource allowlist is
                  unchanged, and the accepted same-user TOCTOU limitation is
                  neither widened nor misrepresented.
    status:       PENDING

T8  Execute exported Windows and complete visual E2E
    owner:        qa-engineer
    objective:    Close the exported-runtime and visual-evidence portion of
                  gap 6 against AC-05 through AC-10, AC-14, AC-15, AC-17, and
                  AC-19.
    files:        generated only: TCFAnimation/Build/*
                  generated only: TCFAnimation/Captures/*
                  evidence: .ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/e2e/*
    depends_on:   T5,T6,T7
    parallel_ok:  no
    exit_criteria: From an unrelated cwd, run the launcher and a clean release
                  export with recorded `GODOT_SELECTED` 4.5.1 Mono data;
                  prove the nonempty exe exists before
                  `RELEASE_EXPORT_PASS`; run post-export
                  `validate_release.py`; run the actual
                  `Build/TCFAnimation.exe --headless -- --verify-runtime` and
                  record `RUNTIME_SMOKE_PASS frames=33 dialogue_ui=true`;
                  prove incompatible verify/capture arguments fail. Exercise
                  exported turn/walk/reverse/edge, speed, clap interruption,
                  cross/hold/release/held-arrow, dialogue, fullscreen
                  arbitration, no-W behavior, and the full capture
                  positive/invalid/existing/permission/reparse/publication
                  matrix. Complete every R-19 frame/boundary at runtime size
                  and zoom with screenshots/contact sheets, visible-height,
                  head/torso/lowest-foot measurements and deltas, artifact
                  findings, source-art attribution where needed, and explicit
                  PASS/FAIL. No source/resource file is edited.
    status:       PENDING

T9  Remediate failed gates without widening scope
    owner:        developer | relevant specialist
    objective:    Fix only evidence-backed failures from T4-T8 and rerun every
                  invalidated gate.
    files:        assigned by Tech Lead after a failure; one writer per file;
                  never the protected unrelated sheets/Waving files
    depends_on:   any failed T4,T5,T6,T7,T8
    parallel_ok:  no
    exit_criteria: Root cause and failing evidence are attached; remediation
                  is minimal; all invalidated and regression gates pass; task
                  plan is re-frozen before any new parallel work.
    status:       PENDING (conditional; dispatch only after a failed gate)

T10 Judge the complete Definition of Done
    owner:        engineering-judge
    objective:    Decide whether every approved requirement and Definition of
                  Done item is proven by real final-tree evidence.
    files:        read-only project and mission evidence;
                  evidence: .ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/judgment.md
    depends_on:   T5,T6,T7,T8
    parallel_ok:  no
    exit_criteria: APPROVED only if every item in requirements.md and
                  definition-of-done.md has reproducible evidence, no stale
                  blocker remains, the authoritative diff/unrelated files are
                  preserved, and no commit/push/history mutation occurred.
    status:       PENDING

## Execution waves

```text
wave 0: T1 (done; contracts frozen)
  -> wave 1: T2
  -> wave 2: T3
  -> wave 3: T4
  -> wave 4: T5, T6, T7 (parallel independent gates)
  -> wave 5: T8
  -> wave 6a: T9 only if a gate fails, then rerun invalidated gates
  -> wave 6b: T10 after all required gates pass
```

Use one developer for both T2 and T3, sequentially. This is the safest and
recommended ownership: the runtime adapter, probe contract, process markers,
launcher behavior, and README form one integration surface, and the scope is
small enough that a second developer would add coordination and diff-collision
risk without useful throughput. Parallelism begins only at the independent
read-only/command-running gates in wave 4.

## Dispatch brief for wave 1

Dispatch T2 first with F1-F3 copied verbatim. Require the developer to inspect
the current contents and preserve all unrelated hunks in the authoritative
uncommitted files. Do not ask the developer to regenerate assets, modify
launchers, run visual QA, or touch the protected root sheets/Waving files.

- 2026-09-04 Planning DONE: 10 work packages and frozen contracts recorded. One developer owns serial remediation to avoid shared-file collisions.
- 2026-09-04 Implementation DONE: T2 and T3 completed with developer
  self-verification; independent T4-T10 gates remain pending.

- 2026-09-04 Implementation DONE: developer remediated six gaps; Debug/Release self-builds pass, probe reports 466 assertions, launcher/export matrices pass, real export and runtime smoke pass, release validator passes.
- 2026-09-04 Validation IN_PROGRESS: independent test, code-review, and security gates dispatched in parallel.

- 2026-09-04 T4 TESTS PASS: 508 passed, 0 failed, 0 mandatory skipped; 33 frames deterministic over two fresh-process passes; 466 probe assertions reconciled; export/runtime/package checks passed.
- 2026-09-04 T5 REVIEW FAIL: validator omitted enforcement of empty include_filter/exclude_filter; CHANGES_REQUIRED.
- 2026-09-04 T6 SECURITY FAIL: 0 Critical, 2 High, 2 Medium, 1 Low, 1 Informational. Blocking: unauthenticated GODOT_EXE execution and batch command injection. Rework cycle 1 started.
- 2026-09-04 Rework architecture requested for pinned Godot provenance, parser-safe batch delegation, export metadata isolation, validator negative controls, and bounded streaming source hashes.

---

## Rework cycle 1 — binding remediation plan

Date: 2026-09-04  
Status: TECH-LEAD PASS — implementation interfaces frozen; ready for RW1.

The prior T4 automated evidence remains useful but launcher, export/package,
validator, security, and all downstream evidence are invalidated. Product
runtime behavior and the accepted 33 frame bytes are not invalidated.

### Frozen rework contracts

#### RW-F1 — Godot provenance before execution

- Add `release-provenance.json` with the exact engine/companion/template sizes
  and SHA-256 values in the rework architecture.
- Add shared `release-tooling.ps1` functions
  `Get-StreamingSha256` and `Get-ApprovedGodot`.
- Preserve source precedence and authoritative `GODOT_EXE`, but reject any
  non-regular/non-`.exe`/reparse/unapproved candidate before first execution.
- Verify the console companion when applicable and verify the Windows release
  template before export.
- Run the existing exact 4.5.1/dot-token `mono` check only after provenance.
- Preserve markers, arbitrary-cwd behavior, and process exit propagation.

#### RW-F2 — Batch is compatibility-only

- Add `run-animation.ps1` as the runtime launcher.
- `run-animation.bat` invokes only the literal fixed Windows PowerShell path
  and the script-relative `run-animation.ps1`; it never expands, parses,
  validates, echoes, or executes `GODOT_EXE`, and never uses `call`.
- All executable paths and arguments are data in PowerShell argument arrays.

#### RW-F3 — Exact isolated release stage

- Add `release-stage-manifest.txt` with only the four project/config files,
  seven runtime C# files, their currently present UID sidecars, and exact 33
  frame PNGs specified in `architecture.md`.
- Export from a new `Build\.release-stage-<GUID>\project` with no copied
  `.godot` or `.import` state. Require pre-import enumeration to equal the
  manifest.
- Import the stage, export to the unchanged
  `Build\TCFAnimation.exe`, remove the exact stage, then and only then emit
  `RELEASE_EXPORT_PASS`.
- Failure cleanup touches only guarded Build/stage paths. Cleanup failure
  blocks success and removes the generated Build tree; source and unrelated
  files are never staged, deleted, or changed.

#### RW-F4 — Validator closes configuration and metadata gaps

- `validate_release.py` requires exactly empty `include_filter` and
  `exclude_filter`, with separate in-memory negative controls for both.
- PCK entries retain bounded offset and size. Engine/import metadata content
  is scanned for the complete denied-token classes in `architecture.md`;
  4 MiB per entry and 16 MiB aggregate are hard caps.
- Add a metadata denied-token negative control.
- Preserve the exact selected-resource order/count, 33-frame payload,
  expected engine metadata count, managed-code checks, read-only operation,
  and terminal success marker.

#### RW-F5 — Bounded source integrity and dependency hashes

- Add `FrameExtraction/file_integrity.py` with exact-size precheck and 1 MiB
  streaming SHA-256.
- Add the six exact source sizes from `architecture.md` to validator/extractor
  source contracts and use the helper before Pillow/OpenCV and after
  extraction.
- Hash-lock the release Python requirements and document/install them with
  `--require-hashes`.
- Preserve all six source bytes and current 33 frame bytes/pixels.

### Downstream rework gates

The final RW1-RW4 definitions are in the Tech Lead brief below. The remaining
independent gates retain their architecture-approved ownership:

RW5  Independent remediation tests  
     owner: test-engineer  
     depends_on: RW1,RW2,RW3,RW4  
     exit: execute all ten required validation categories in
           `architecture.md`; rerun builds/probe, validator/read-only,
           determinism, actual export, and exported runtime smoke; report
           pass/fail counts. No visual QA is required in this rework gate.

RW6  Independent code review  
     owner: code-reviewer  
     depends_on: RW5  
     exit: APPROVED; explicitly verify exact-empty filter enforcement,
           pre-execution provenance, no batch parsing, exact stage manifest,
           cleanup semantics, bounded readers, and behavior compatibility.

RW7  Independent security review  
     owner: security-engineer  
     depends_on: RW5  
     exit: PASS with no Critical/High; reproduce prior two attacks as blocked;
           inspect actual PCK metadata for denied tokens; assess source hashing,
           template/companion provenance, dependency hashes, and stage cleanup.

RW8  Resume QA and final judgment  
     owner: qa-engineer then engineering-judge  
     depends_on: RW6,RW7  
     exit: rerun only invalidated exported-runtime/package portions plus the
           still-outstanding visual/E2E scope; judge against final evidence.

### Downstream execution order

```text
RW5 -> RW6 + RW7 -> RW8
```

Independent review gates may run in parallel only after RW5 passes.

---

## Rework cycle 1 — final developer implementation brief

Date: 2026-09-04  
Status: PASS — binding implementation order and tests frozen.

This section is the sole dispatchable definition of RW1-RW4. It does not
change ADR-004 through ADR-006 or RW-F1 through RW-F5. One developer owns
RW1-RW4 serially. No RW implementation task may
edit product/runtime C#, scenes, the export resource list, source sheets,
runtime frames, unrelated root sheets, Waving files, or capture behavior.

### Rework-wide immutable baseline and file boundary

Before RW1, record a machine-readable pre-rework inventory under
`evidence/rework/baseline/` containing relative path, byte length, and
uppercase SHA-256 for:

- all six immutable source sheets;
- all 33 accepted `Frames` PNGs;
- all product/runtime files not assigned to RW1-RW4, including
  `Main.tscn`, `project.godot`, `TCFAnimation.csproj`, all runtime C# and
  existing runtime C# UID sidecars;
- the unrelated root sheets and sidecars named by R-01.

The inventory excludes generated `.godot`, `Build`, `Captures`, `bin`, `obj`,
and mission evidence. After every RW task, compare the protected inventory.
The final comparison must report zero missing, extra, length-changed, or
hash-changed protected files. The accepted 33 encoded PNG bytes and decoded
pixels remain exactly those already recorded in `test-results.md`.

The complete implementation write allowlist is:

```text
TCFAnimation/release-provenance.json
TCFAnimation/release-tooling.ps1
TCFAnimation/run-animation.ps1
TCFAnimation/run-animation.bat
TCFAnimation/release-stage-manifest.txt
TCFAnimation/export-release.ps1
TCFAnimation/FrameExtraction/file_integrity.py
TCFAnimation/FrameExtraction/validate_release.py
TCFAnimation/FrameExtraction/extract_directional_turns.py
TCFAnimation/FrameExtraction/extract_right_walk.py
TCFAnimation/FrameExtraction/extract_clap.py
TCFAnimation/FrameExtraction/extract_cross_arm.py
TCFAnimation/requirements.txt
TCFAnimation/README.md
.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework/**
```

No other path may change. Evidence subdirectories are task-owned:
`RW1/`, `RW2/`, `RW3/`, and `RW4/`; tasks must not overwrite another task's
evidence.

### Frozen interface A — provenance document and authenticated selection

`release-provenance.json` is UTF-8 and has schema version 1. Approval records
are content identities, not absolute-path allowlists and not separate records
for bundled/configured/PATH locations. Therefore an approved configured or
PATH copy is accepted at any canonical location and with any `.exe` basename
only when its exact size and SHA-256 match one executable identity. The
returned `Source` records discovery provenance; it does not participate in
the hash decision.

The binding logical schema and values are:

```json
{
  "schemaVersion": 1,
  "supportedVersion": "4.5.1",
  "requiredVersionToken": "mono",
  "executables": [
    {
      "id": "godot-console-win64",
      "fileName": "Godot_v4.5.1-stable_mono_win64_console.exe",
      "size": 197640,
      "sha256": "FD5C88FD05AFC7C2D965777320DEAEC5A80C31363C0BB33D7B48025376F96C5F",
      "companionId": "godot-editor-win64"
    },
    {
      "id": "godot-editor-win64",
      "fileName": "Godot_v4.5.1-stable_mono_win64.exe",
      "size": 163933704,
      "sha256": "C369B7B92C30100F3EEDE92410BD02A4BB024562860DEE94C33399BEA1C77C9B"
    }
  ],
  "exportTemplate": {
    "id": "windows-release-x86_64",
    "relativePath": "editor_data/export_templates/4.5.1.stable.mono/windows_release_x86_64.exe",
    "size": 96965120,
    "sha256": "9186C4AA21D659035A6BCC33BEA644D7399DDA42AC24F9E0D3A9E5965E92E00F"
  }
}
```

The implementation may format the JSON differently, but the parsed key
values, record count, ids, and relationships must be exact. Missing,
duplicate-id, malformed, unknown-id, non-integer-size, or non-64-hex hash
records fail closed before candidate execution.

The binding PowerShell functions are:

```powershell
Get-StreamingSha256 `
    -LiteralPath <absolute file path> `
    -ExpectedLength <Int64>
# Returns one uppercase 64-character SHA-256 string.
# Rejects missing, non-regular, directory, reparse-point, or size-mismatched
# input before reading file content. Reads in bounded chunks.

Get-ApprovedGodot `
    -ProjectRoot <absolute project root> `
    -Purpose Run|Export
# Returns exactly:
# [pscustomobject]@{
#   Executable = <canonical absolute approved candidate path>
#   Source     = "GODOT_EXE" | "bundled" | "PATH"
#   Version    = <the one accepted nonempty --version line>
#   Sha256     = <matched approved executable hash>
# }
```

`release-tooling.ps1` contains function definitions only: dot-sourcing it
must not resolve tools, execute a process, clean/create directories, emit
success/failure markers, or call `exit`. Helper-owned failures throw an
`InvalidOperationException` whose `Data["Reason"]` is a stable token and whose
optional `Data["Source"]` and `Data["Executable"]` values are safe marker
fields. Entry scripts catch these failures, emit exactly one
`GODOT_RESOLUTION_FAIL`, and exit 1.

Selection is strictly:

1. nonempty `$env:GODOT_EXE`;
2. the existing project-local bundled console path;
3. the first command resolved for `godot4`;
4. the first command resolved for `godot`.

An invalid resolved item at any level is authoritative for that level and is
not executed or bypassed. In particular, configured failure never falls back;
an existing invalid `godot4` never falls through to `godot`. Aliases,
functions, scripts, `.cmd`, `.bat`, `.ps1`, directories, non-`.exe` files,
reparse points, and non-regular files fail. PATH resolution must not choose a
later command after the first resolved command fails provenance.

After a candidate matches one executable identity:

- when it matches `godot-console-win64`, resolve the companion from the
  candidate's directory using the editor record's exact `fileName`; verify
  the companion size/hash before the console candidate executes;
- when it matches `godot-editor-win64`, no companion is required;
- for `Purpose Export`, resolve the template from the candidate's directory
  plus `exportTemplate.relativePath`; verify it before Build cleanup or any
  Godot execution;
- configured/PATH Run copies may be relocated; console copies still require
  the exact sibling companion, and Export copies require the complete pinned
  relative template layout. No per-machine path is written to provenance.

Only then invoke `--version`. Accept exactly one trimmed nonempty output line,
require its first three dot-delimited tokens to be ordinal
`4`, `5`, `1`, and require an ordinal `mono` token. No candidate-supplied
output is accepted as identity evidence.

Stable provenance reasons are:

```text
not_found
configured_missing
candidate_not_application
candidate_not_exe
candidate_reparse
size_mismatch
unapproved_hash
companion_missing
companion_unapproved
template_missing
template_unapproved
version_command_failed
version_unparseable
unsupported_version
provenance_invalid
```

`run-animation.bat` is compatibility-only and contains no reference to the
text `GODOT_EXE`, no `call`, no delayed expansion, and no candidate
resolution. Its only executable command is the literal:

```bat
"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0run-animation.ps1"
```

It returns that process exit code. `run-animation.ps1` derives the root from
`$PSScriptRoot`, reads `$env:GODOT_EXE` only as a PowerShell string, calls
`Get-ApprovedGodot -Purpose Run`, emits the existing `GODOT_SELECTED` fields
with optional trailing `sha256=...`, and invokes the approved executable with
the argument array `@("--path", $ProjectRoot)`.

### Frozen interface B — exact isolated stage

The shared staging functions consumed by `export-release.ps1` are:

```powershell
Read-ReleaseStageManifest `
    -ProjectRoot <absolute project root> `
    -LiteralPath <absolute manifest path>
# Returns [string[]] in file order after validating the exact manifest below.

New-ReleaseStage `
    -ProjectRoot <absolute project root> `
    -BuildRoot <absolute guarded Build path> `
    -ManifestEntries <string[]>
# Returns:
# [pscustomobject]@{
#   StageRoot       = <Build\.release-stage-<GUID>>
#   StageProjectRoot = <StageRoot\project>
# }
# Creates the GUID stage exclusively, copies only literal manifest files, and
# proves the pre-import recursive file set equals the manifest exactly.

New-ReleaseStageSolution `
    -BuildRoot <absolute guarded Build path> `
    -StageRoot <exact stage root returned above> `
    -StageProjectRoot <exact stage project root returned above> `
    -ManifestEntries <validated 50-entry string[]>
# Re-proves the 50-file seed, creates only TCFAnimation.sln from the fixed
# ADR-007 bytes, and returns Path, Length, Sha256 after proving the exact
# 51-file pre-import inventory.

Remove-ReleaseStage `
    -BuildRoot <absolute guarded Build path> `
    -StageRoot <exact stage root returned above>
# Removes only that stage after containment/name/reparse validation.
```

These helpers also only throw structured failures; they do not emit terminal
markers or call `exit`.

`release-stage-manifest.txt` is UTF-8 without BOM, one project-relative
forward-slash path per line, with no blank lines, comments, surrounding
whitespace, duplicates, absolute paths, drive/UNC/device forms, `.` or `..`
segments, globs, or directory entries. Its exact 50-line order is:

```text
project.godot
Main.tscn
TCFAnimation.csproj
export_presets.cfg
AnimationGeometry.cs
CapturePathPolicy.cs
DialogueModel.cs
DialogueUi.cs
DirectionalTurnStateMachine.cs
GlobalInputPolicy.cs
TurnController.cs
AnimationGeometry.cs.uid
CapturePathPolicy.cs.uid
DialogueModel.cs.uid
DialogueUi.cs.uid
DirectionalTurnStateMachine.cs.uid
TurnController.cs.uid
Frames/LeftTurn/turn_0.png
Frames/LeftTurn/turn_1.png
Frames/LeftTurn/turn_2.png
Frames/RightTurn/turn_0.png
Frames/RightTurn/turn_1.png
Frames/RightTurn/turn_2.png
Frames/LeftWalk/walk_00.png
Frames/LeftWalk/walk_01.png
Frames/LeftWalk/walk_02.png
Frames/LeftWalk/walk_03.png
Frames/LeftWalk/walk_04.png
Frames/LeftWalk/walk_05.png
Frames/RightWalk/walk_00.png
Frames/RightWalk/walk_01.png
Frames/RightWalk/walk_02.png
Frames/RightWalk/walk_03.png
Frames/RightWalk/walk_04.png
Frames/RightWalk/walk_05.png
Frames/Clap/clap_00.png
Frames/Clap/clap_01.png
Frames/Clap/clap_02.png
Frames/Clap/clap_03.png
Frames/Clap/clap_04.png
Frames/Clap/clap_05.png
Frames/CrossArm/cross_00.png
Frames/CrossArm/cross_01.png
Frames/CrossArm/cross_02.png
Frames/CrossArmRelease/release_00.png
Frames/CrossArmRelease/release_01.png
Frames/CrossArmRelease/release_02.png
Frames/CrossArmRelease/release_03.png
Frames/CrossArmRelease/release_04.png
Frames/CrossArmRelease/release_05.png
```

This resolves C# UID behavior:

- the six listed UID sidecars are the only seed UID files copied;
- `GlobalInputPolicy.cs.uid` is intentionally absent from the manifest and
  pre-import stage, even if a later development-project Godot run creates one;
- Godot may create `GlobalInputPolicy.cs.uid` and `.godot`/import state only
  inside the stage after the exact seed check;
- generated stage files are never copied back, never added to the seed
  manifest, and disappear with stage cleanup;
- the development project's UID files and `.godot` tree are snapshotted
  before/after export and must be byte-identical.

Copy validation checks every source file and ancestor with literal paths and
rejects reparse points; it never follows a link. Pre-import equality compares
the recursive stage **file set and count** to the 50 manifest entries after
normalizing separators. Only after that proof, generate the release-owned
`TCFAnimation.sln` defined by ADR-007 and
`architecture.md` (“RW2 amendment — deterministic stage-local solution”).
The canonical solution is UTF-8 without BOM with CRLF plus final CRLF, length
994, SHA-256
`FE3E86D84D18FD948E059483F5CB09B956E4F7B91A0B33FACD337727A2F4EC79`,
and contains exactly one project reference: `TCFAnimation.csproj`. The helper
uses `CreateNew`, never reads/copies the repository solution, never invokes
`dotnet`, then proves the pre-import inventory is exactly the original 50
entries plus `TCFAnimation.sln`. Directories do not count, and enumeration
order is not used as filesystem evidence.

`export-release.ps1` performs this exact sequence:

1. derive/guard the project root and Build path;
2. authenticate candidate, companion when applicable, and template with
   `Get-ApprovedGodot -Purpose Export`;
3. only after authentication, guarded-clean and recreate Build;
4. read the exact manifest and create/copy/prove the 50-file seed;
5. generate/validate the fixed minimal solution and prove the 51-file
   pre-import stage;
6. run approved Godot
   `--headless --path <stage-project> --import`;
7. run approved Godot
   `--headless --path <stage-project> --export-release
   "Windows Desktop" <root>\Build\TCFAnimation.exe`;
8. require zero child exits, no captured Godot line beginning `ERROR:`, a
   nonempty regular non-reparse final executable, and a nonempty regular
   non-reparse
   `Build\data_TCFAnimation_windows_x86_64\TCFAnimation.dll`;
9. remove the exact stage;
10. prove no `.release-stage-*` remains, recheck both output files, then emit
   `RELEASE_EXPORT_PASS`.

Godot import/export failures emit one `RELEASE_EXPORT_FAIL` with reason
`stage_import_failed` or `stage_export_failed` and return the nonzero Godot
exit code unchanged. Orchestration failures exit 1 with one of:

```text
stage_manifest_invalid
stage_create_failed
stage_copy_failed
stage_inventory_mismatch
stage_solution_failed
missing_output
stage_cleanup_failed
```

The `finally` path always attempts exact-stage removal. Cleanup failure
suppresses success and triggers a second guarded clean of generated Build.
No source path is deleted. Provenance failure emits only
`GODOT_RESOLUTION_FAIL`, occurs before Build cleanup, and preserves an
existing Build sentinel byte-for-byte.

The selected export contract remains exactly the current 34 ordered resources:
`res://Main.tscn` followed by the same 33 frame paths/order shown above.
`include_filter=""`, `exclude_filter=""`, final
`Build\TCFAnimation.exe`, four expected engine metadata entries, and all
capture/runtime contracts remain unchanged.

### Frozen interface C — validator metadata and preset checks

In `validate_release.py`, define:

```python
@dataclass(frozen=True)
class PackEntry:
    data_offset: int  # absolute offset in the containing .pck/.exe
    size: int

def read_pack_manifest(path: Path) -> dict[str, PackEntry]:
    ...

def read_pack_entry(
    path: Path,
    pack_start: int,
    pack_size: int,
    entry: PackEntry,
    maximum_size: int = 4 * 1024 * 1024,
) -> bytes:
    ...
```

For PCK v3, the stored entry offset is relative to `pack_start`; convert it to
an absolute `data_offset` and prove the complete range lies within
`[pack_start, pack_start + pack_size)`. Reject negative/overflowing/out-of-pack
ranges and any metadata entry above 4 MiB before allocation/read. Sum all
engine and `*.import` metadata sizes and reject an aggregate above 16 MiB
before reading any metadata content.

The preset parser operates on preset 0's assignment section and requires
exactly one anchored `include_filter="<value>"` and exactly one anchored
`exclude_filter="<value>"`; both values must be the empty string. In-memory
controls independently prove rejection of a nonempty include, a nonempty
exclude, and a duplicate assignment.

Inspect the content of all four exact engine metadata entries and all 33
`*.import` entries. For matching only, normalize `\` to `/` in both single-byte
and UTF-16LE forms and perform case-insensitive searches for:

```text
Waving
Avatar.jpg
Clapping.png
CrossArm.png
CrossArm2.png
WalkRightSheet.png
LTurning.png
RTurning.png
RWalking2.png
Clapping2.png
CrossArm3.png
CrossArm4.png
ControllerProbe/
FrameExtraction/
.ai-org/
.tools/
.vs/
Build/
Captures/
README.md
requirements.txt
run-animation
export-release
```

In-memory controls must independently reject ASCII/UTF-8
`res://Waving.png`, UTF-16LE
`C:\repo\TCFAnimation\FrameExtraction\validate_release.py`, an entry of
4 MiB + 1 byte, an aggregate of 16 MiB + 1 byte, and an out-of-pack range.
The controls use `BytesIO` or mission-local temporary bytes only and do not
extract or modify the artifact.

The validator remains read-only and preserves exact selected-resource order,
33 texture/import counts, four engine metadata entries, two expected script
placeholders, managed payload checks, and the terminal
`ASSET_RELEASE_CHECK_PASS` line.

### Frozen interface D — exact-size source integrity

`FrameExtraction/file_integrity.py` exposes only:

```python
def sha256_file(
    path: Path,
    expected_size: int,
    chunk_size: int = 1 << 20,
) -> str:
    """Fail on non-regular/size mismatch, then return uppercase streaming SHA-256."""
```

It rejects missing, symlink/reparse, non-regular, nonpositive
`expected_size`, and nonpositive `chunk_size`; performs the exact-size check
before opening for content reads; hashes in chunks no larger than the supplied
chunk size; verifies EOF and unchanged final size; and returns uppercase hex.
Callers use the default 1 MiB chunk size.

The six source contracts are:

| Source | Exact bytes | SHA-256 |
|---|---:|---|
| `LTurning.png` | 1502641 | `9EDD38F303B17CD043EDCCABF2E6C2BC50F182B9A2B918B4BDECF1B2861E3A91` |
| `RTurning.png` | 1390487 | `2D20B97B4BC630DBFF9D6DD932F3314A9BC6FE0587013E6C12E72BF1D40D5840` |
| `RWalking2.png` | 495754 | `CD56287A4830D068292793256DBEB5A29E1EB9D888520A5339FE3957E7B7FA3A` |
| `Clapping2.png` | 654450 | `FBB46FBEAD0D5815F4E23307240535C600C29D0DF650B493137D05E762319C00` |
| `CrossArm3.png` | 628721 | `276413B76F13D4940FD8B746D3AEA13D27922A47EACD750DCCC6FF622A6A8192` |
| `CrossArm4.png` | 632179 | `475614A7B2DB0B7469FA88E9B7B5F5C8548F8095B56DAD99F6270098E9174A3F` |

Add the exact size to each extractor's existing immutable source record.
Every validator/extractor precheck calls `sha256_file` before
`Image.open`/`cv2.imread`; every extractor post-run immutability check calls
the same helper. Output-frame byte comparisons may remain in memory.

### Frozen interface E — one authoritative Python release lock

Keep `requirements.txt` as the single authoritative release lock; do not add
a second lock file. It must contain:

- exactly one `--index-url https://pypi.org/simple`;
- `--only-binary=:all:`;
- exactly `numpy==2.5.2`, `opencv-python==5.0.0.93`, and `Pillow==12.3.0`;
- SHA-256 hashes for the approved CPython 3.13 Windows x86-64 wheel artifact
  used for each package;
- no extra index, trusted host, editable, VCS, local-path, direct-URL, range,
  wildcard, or unhashed package requirement.

`validate_release.py` parses this file read-only and rejects drift from the
directives, package set/versions, or hash presence/shape. README installation
uses:

```powershell
python -m pip install --require-hashes -r requirements.txt
```

The dependency evidence uses a fresh mission-local download directory:

```powershell
python -m pip download --require-hashes --only-binary=:all: `
  --dest <fresh-evidence-directory> -r requirements.txt
python -m pip install --dry-run --require-hashes --only-binary=:all: `
  -r requirements.txt
```

Both commands must resolve only the three locked distributions from the
approved index. The downloaded filenames and computed SHA-256 values are
recorded and must match the lock.

### Frozen self-test matrix

All tests run from an unrelated current working directory where applicable.
Temporary candidates/projects are created only under the owning
`evidence/rework/RW*/` directory and removed after their result is recorded.
No unapproved candidate may be intentionally executed merely to observe its
behavior.

#### RW1 provenance and batch tests

| Case | Required result |
|---|---|
| Crafted quoted/metacharacter `GODOT_EXE` containing a sentinel command | batch exit 1; exactly one `GODOT_RESOLUTION_FAIL`; no `GODOT_SELECTED`; sentinel absent |
| Sentinel-bearing `.cmd`/`.ps1`/fake-version executable that would write on first instruction | exit 1 before execution; reason is candidate type or `unapproved_hash`; sentinel absent |
| One-byte-mutated official executable | exit 1 `unapproved_hash`; no version-derived success marker |
| Valid official console with missing or mutated sibling editor | exit 1 `companion_missing`/`companion_unapproved`; no `GODOT_SELECTED` |
| Valid official candidate with missing or mutated release template under `Purpose Export` | exit 1 `template_missing`/`template_unapproved`; existing Build sentinel unchanged |
| Missing/invalid authoritative configured candidate while bundled bytes exist | exit 1; no fallback marker/launch |
| Byte-identical approved configured editor copy at a different canonical path/basename | `Source=GODOT_EXE`; accepted hash/version |
| Byte-identical approved `godot4.exe` PATH copy at a different canonical path | `Source=PATH`; accepted hash/version |
| Approved bundled distribution | accepted 4.5.1 Mono and expected hash |
| Approved candidate against a temporary invalid project | launcher returns the actual nonzero Godot child exit |

The malicious executable fixture must create its sentinel before printing a
fake accepted version if it ever runs. Test setup deletes the sentinel first;
the result records `sentinel_before=false` and `sentinel_after=false`. Static
inspection additionally proves `run-animation.bat` contains neither
`GODOT_EXE` nor `call`.

#### RW2 stage/export tests

| Case | Required result |
|---|---|
| Manifest parser | exact 50 ordered lines; six seed C# UIDs; no `GlobalInputPolicy.cs.uid`; no denied path class |
| Copied-seed inventory | exactly 50 files and no solution, `.godot`, `.import`, source sheet, Waving/root sheet, tooling, probe, README, Build, Captures, or mission entry |
| Generated solution | `CreateNew`; exact 994-byte/hash contract; only `TCFAnimation.csproj`; no `ControllerProbe`; post-generation pre-import inventory is exactly 51 files |
| Existing/tampered/generated-extra solution | one `stage_solution_failed`, exit 1, no Godot execution, no success/output/stage/source change |
| Stage import | may generate stage-local GlobalInputPolicy UID/cache only; development UID/cache inventory unchanged |
| Real export | zero child exits; no `ERROR:` diagnostics; final nonempty `Build\TCFAnimation.exe` and final nonempty `Build\data_TCFAnimation_windows_x86_64\TCFAnimation.dll`; stage absent before one `RELEASE_EXPORT_PASS` |
| Development `.godot/uid_cache.bin` and unrelated/Waving roots present | exported metadata contains no denied token and selected payload remains 34 resources/33 frames |
| Engine, companion, or template provenance failure | occurs before Build cleanup; Build sentinel unchanged; no stage |
| Import failure | one `stage_import_failed`; child exit preserved; no success/stage/source change |
| Export failure | one `stage_export_failed`; child exit preserved; no success/stage/source change |
| Zero-exit Godot error diagnostic | `stage_import_failed` or `stage_export_failed`, exit 1, no success/stage/output |
| Zero-exit/missing executable or managed assembly fixture | `missing_output`, exit 1, no success/stage |
| First cleanup attempt failure | `stage_cleanup_failed`, exit 1, no success; guarded retry leaves no stage/generated Build; outside-Build sentinel unchanged |

Failure fixtures use approved Godot bytes or function-level orchestration
fixtures; they must never substitute an unapproved executable after
authentication.

#### RW3 validator and integrity tests

| Case | Required result |
|---|---|
| Nonempty include filter | in-memory rejection |
| Nonempty exclude filter | in-memory rejection |
| Duplicate include/exclude assignment | in-memory rejection |
| ASCII/UTF-8 Waving metadata | in-memory rejection |
| UTF-16LE development metadata | in-memory rejection |
| Metadata entry 4 MiB + 1 | rejected before entry read/allocation |
| Metadata aggregate 16 MiB + 1 | rejected before any metadata read |
| Out-of-pack descriptor | rejected before seek/read |
| Sparse source with wrong exact size | rejected by `sha256_file` before hashing/Pillow/OpenCV; decode sentinel absent |
| Six real sources | exact size/hash pass before decode and after extraction |
| Two fresh extraction passes | six source hashes unchanged; all 33 encoded/decoded frame hashes unchanged; no non-frame output |
| Actual isolated package | exact manifest counts; all metadata scanned; denied-token count zero |
| Validator read-only snapshot | zero files created, removed, or changed |

#### RW4 lock/documentation tests

| Case | Required result |
|---|---|
| Static lock parse | one approved index, binary-only, exact three versions, valid hashes, no forbidden directive/source |
| `pip download --require-hashes` | exit 0; exactly three approved artifacts; computed hashes match |
| `pip install --dry-run --require-hashes` | exit 0; no unhashed/transitive requirement |
| Tampered hash in a mission-local lock copy | pip exits nonzero with hash mismatch |
| README command audit | no unhashed install command; authenticated selection, configured/PATH copy semantics, isolated stage, UID behavior, metadata scan, and unchanged final output path documented |
| Final preservation | protected baseline unchanged; selected 34 resources unchanged; Waving absent from runtime/export; final path remains `Build\TCFAnimation.exe` |

### Final RW1-RW4 work packages

RW1  Implement authenticated Godot selection and parser-safe launcher
    owner:        developer
    objective:    Land provenance schema, bounded PowerShell hashing,
                  authenticated candidate/companion/template selection, and
                  the fixed batch-to-PowerShell runtime launcher.
    files:        TCFAnimation/release-provenance.json
                  TCFAnimation/release-tooling.ps1
                  TCFAnimation/run-animation.ps1
                  TCFAnimation/run-animation.bat
                  TCFAnimation/export-release.ps1
                  .ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework/RW1/**
    depends_on:   -
    parallel_ok:  no
    exit_criteria: Frozen interface A and every RW1 matrix row pass; malicious
                  fixtures prove sentinel absence; configured and PATH copies
                  use the same content identities; provenance failure occurs
                  before Build cleanup; protected baseline is unchanged.
    status:       PASS
    evidence:     `evidence/rework/RW1/self-test-results.json` records 13/13
                  passing provenance, injection, authoritative-failure,
                  configured/PATH/bundled selection, template-before-clean,
                  and child-exit tests. `evidence/rework/RW1/protected-comparison.json`
                  records 74/74 protected files unchanged. The exact rerun
                  exited 0 with `RW1_SELF_TEST_PASS tests=13`.

RW2  Implement exact isolated release staging
    owner:        developer
    objective:    Export only from the exact 50-file copied seed plus the one
                  fixed stage-generated minimal solution, isolate all
                  generated Godot UID/import state, preserve final output and
                  selected-resource contracts, and fail closed on false
                  positive output or cleanup.
    files:        TCFAnimation/release-stage-manifest.txt
                  TCFAnimation/release-tooling.ps1
                  TCFAnimation/export-release.ps1
                  .ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework/RW2/**
    depends_on:   RW1
    parallel_ok:  no
    exit_criteria: Frozen interface B as amended by ADR-007 and every RW2
                  matrix row pass; exact 50-file copied seed, fixed solution,
                  51-file pre-import stage, and UID behavior are evidenced;
                  actual export contains no source content or
                  development/unrelated metadata token; managed output and
                  runtime smoke pass; no stage remains; protected baseline is
                  unchanged.
    status:       PASS
    evidence:     `evidence/rework/RW2/self-test-results.json` records 16/16
                  passing tests. The copied seed is exactly 50 files; the
                  generated solution is exactly 994 bytes with SHA-256
                  FE3E86D84D18FD948E059483F5CB09B956E4F7B91A0B33FACD337727A2F4EC79;
                  pre-import inventory is 51 files; real isolated export,
                  managed DLL proof, development-state isolation, exported
                  runtime smoke, all failure/cleanup fixtures, and the 74-file
                  protected baseline pass.

RW3  Harden release validator and immutable source hashing
    owner:        developer
    objective:    Enforce empty filters, bounded metadata descriptors/scans
                  and negative controls, and exact-size streaming integrity
                  before every source decode and after extraction.
    files:        TCFAnimation/FrameExtraction/file_integrity.py
                  TCFAnimation/FrameExtraction/validate_release.py
                  TCFAnimation/FrameExtraction/extract_directional_turns.py
                  TCFAnimation/FrameExtraction/extract_right_walk.py
                  TCFAnimation/FrameExtraction/extract_clap.py
                  TCFAnimation/FrameExtraction/extract_cross_arm.py
                  .ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework/RW3/**
    depends_on:   RW2
    parallel_ok:  no
    exit_criteria: Frozen interfaces C/D and every RW3 matrix row pass;
                  validator remains read-only; six sources and all 33 frame
                  bytes/pixels are unchanged; actual isolated package passes.
    status:       PASS
    evidence:     `evidence/rework/RW3/self-test-results.json` records 10/10
                  passing tests. Empty-filter negatives, bounded metadata
                  descriptors/scans, single-byte and UTF-16LE denied-token
                  controls, exact-size pre-open rejection, six streaming
                  source checks, two deterministic 33-frame extraction passes,
                  actual package inspection, validator read-only behavior, and
                  the protected baseline pass.

RW4  Hash-lock release Python dependencies and synchronize documentation
    owner:        developer
    objective:    Make requirements.txt the sole hash-locked release
                  dependency authority, validate it, and document the exact
                  authenticated and isolated release workflow.
    files:        TCFAnimation/requirements.txt
                  TCFAnimation/README.md
                  TCFAnimation/FrameExtraction/validate_release.py
                  .ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework/RW4/**
    depends_on:   RW3
    parallel_ok:  no
    exit_criteria: Frozen interface E and every RW4 matrix row pass; hash
                  mismatch negative control fails; final protected inventory,
                  selected 34 resources, runtime/Waving absence, capture
                  contract, and `Build\TCFAnimation.exe` path are unchanged.
    status:       PASS
    evidence:     `evidence/rework/RW4/self-test-results.json` records 12/12
                  passing tests under ADR-008. Ambient `PIP_*` settings were
                  removed and `PIP_CONFIG_FILE=nul`; pip downloaded only from
                  the explicit Microsoft proxy into a fresh wheelhouse. The
                  exact three CPython 3.13 Windows x86-64 filenames and hashes
                  matched, offline `--no-index --find-links --require-hashes`
                  dry-run and target install passed, imports reported the
                  locked versions, the tampered hash failed closed, the
                  validator/readme/final-artifact checks passed, and the
                  74-file protected baseline remained unchanged.

### Execution waves and dispatch

```text
wave RW-1: RW1
  -> wave RW-2: RW2
  -> wave RW-3: RW3
  -> wave RW-4: RW4
  -> existing RW5 independent remediation tests
  -> existing RW6 + RW7 independent reviews in parallel
  -> existing RW8 QA/judgment
```

The developer must stop after any failed task, preserve its evidence, mark the
task `FAILED` or `BLOCKED`, and return to the Tech Lead before beginning the
next wave. Interface changes are not negotiated inside implementation. The
current dispatch is RW2 with this entire final brief and ADR-004 through
ADR-007.

- 2026-09-04 RW1 PASS: 13/13 provenance and parser-safety tests.
- 2026-09-04 RW2 BLOCKER RESOLVED ARCHITECTURALLY: ADR-007 preserves the
  exact 50-file copied seed and authorizes one fixed, validated, stage-local
  minimal solution before import. RW2 is ready to resume; RW3 and RW4 remain
  serially dependent on its executed pass evidence.
- 2026-09-04 RW2 PASS: 16/16 stage/export self-tests, real isolated export,
  managed DLL proof, and exported runtime smoke.
- 2026-09-04 RW3 PASS: 10/10 validator/integrity self-tests, including two
  deterministic extraction passes and read-only actual-package validation.
- 2026-09-04 RW4 BLOCKED: implementation and eight local/static checks pass,
  but direct authenticated transfer from `files.pythonhosted.org` fails during
  TLS negotiation, blocking the two required approved-index pip proofs.

- 2026-09-04 RW2 PASS (16/16) and RW3 PASS (10/10). RW4 implementation complete but evidence blocked by external TLS failure to files.pythonhosted.org; machine-configured Microsoft proxy supplied byte-identical hash-matching wheels. Architect decision requested for approved-index amendment.

- 2026-09-04 Architecture amendment PASS: explicit authenticated Microsoft PyPI proxy approved as sole release index, with exact hashes/wheel-only target and offline install proof; no CTO decision required. Accidental unsuffixed agent artifact folder removed; canonical decision recorded here.
- 2026-09-04 RW4 PASS: 12/12 amended proxy/lock tests passed. The explicit
  Microsoft proxy supplied exactly the three frozen wheels; hashes matched;
  offline dry-run, install, and import passed; tampered hash rejection and
  protected-baseline checks passed.
- 2026-09-04 FINAL DEVELOPER SELF-VERIFICATION PASS: RW1 13/13, RW2 16/16,
  RW3 10/10, and RW4 12/12 (51/51 total). Debug/Release builds, 466/466 probe
  reconciliation, Python compile/import, read-only validator, format/diff,
  Godot import/startup, unrelated-cwd batch/export, exported runtime smoke,
  package/metadata hygiene, protected files, and stale-stage checks passed.
  Final artifact remains `TCFAnimation/Build/TCFAnimation.exe`; independent
  RW5-RW8 gates remain out of scope and pending.

- 2026-09-04 Rework implementation complete: RW1 13/13, RW2 16/16, RW3 10/10, RW4 12/12; combined developer matrix 51/51. Final isolated release validates and smokes. Independent RW5 retest dispatched.

- 2026-09-04 RW5 TEST PASS: 540 passed, 0 failed, 0 mandatory skipped. Reproduced prior attacks as blocked, 466 assertions, two-pass 33-frame determinism, isolated export, runtime smoke, dependency lock, package metadata hygiene. RW6/RW7 dispatched in parallel.

- 2026-09-04 RW6 CODE REVIEW APPROVED: no findings; prior filter gap resolved.
- 2026-09-04 RW7 SECURITY PASS: Critical 0, High 0, Medium 0, Low 0, Informational 1 accepted capture TOCTOU. QA/E2E started.

- 2026-09-04 QA/E2E FAIL: 19 scenarios, 12 pass, 7 fail. Blocking capture root, viewport clipping, visual artifacts, continuity pops, and unproven interactive dialogue editing. Rework cycle 2 started; incident-debugger dispatched.

## Rework cycle 2 tasks

| ID | Task | Owner | Status | Depends | Exit |
|---|---|---|---|---|---|
| RC2-T1 | Exported capture-root resolver and positive/negative process tests | developer | DONE | debug | 8/8 process cases pass; GUI capture publishes under `Build\Captures`; no-overwrite/path errors are specific; headless rendering absence fails once and exits |
| RC2-T2 | Logical viewport fit/letterbox configuration and size matrix | developer | DONE | debug | Runtime asserts 1920x1080 `CanvasItems/Keep`; six exported captures fit 16:9 without clipping |
| RC2-T3 | Pose-specific floor/reflection exclusions and validator adversarial controls | developer | DONE | debug | Exact defect ROIs black; shoe anchors preserved; two adversarial in-envelope controls rejected; 33 frames deterministic |
| RC2-T4 | Canonical front and cross/release lower plate/final-anchor calibration | developer | DONE | RC2-T3 | Front bytes identical; shared seven-frame lower plate; stationary/moving thresholds and all 45 boundaries pass |
| RC2-T5 | Full automated regression/export/package rerun | test-engineer | PENDING | RC2-T1..T4 | All mandatory tests pass with new frame hashes |
| RC2-T6 | Code and security re-review | code-reviewer/security-engineer | PENDING | RC2-T5 | APPROVED / PASS |
| RC2-T7 | Unlocked/real-control exported QA and complete 33-frame visual rerun | qa-engineer | PENDING | RC2-T5,RC2-T6 | All scenarios and visual matrix pass |
| RC2-T8 | Final judgment | engineering-judge | PENDING | RC2-T7 | APPROVED |

## Rework cycle 2 developer execution evidence

Date: 2026-09-04. Scope: RC2-T1 through RC2-T4 only.

### Implementation locations

- Capture resolver and portable-layout validation:
  `TCFAnimation/CapturePathPolicy.cs:7-129`.
- Single root computation/reuse, viewport runtime assertion, and bounded
  capture failure:
  `TCFAnimation/TurnController.cs:36,55-60,400-472,649,719-792`.
- Pure capture-root probes:
  `TCFAnimation/ControllerProbe/Program.cs:17,36,1452-1553`.
- 1920x1080 logical viewport, 1280x720 override, keep aspect:
  `TCFAnimation/project.godot:19-24`.
- Source-coordinate exclusions:
  `foreground_cutout.py:66-95,427-631`,
  `extract_directional_turns.py:112-191,280-300`,
  `extract_clap.py:127-158,365-417`,
  `extract_cross_arm.py:155-212,356-408`.
- Canonical front/lower plate/final-anchor calibration:
  `extract_directional_turns.py:357-372,538`,
  `extract_cross_arm.py:58,710-938`.
- Independent adversarial, continuity, package, and viewport validation:
  `validate_release.py:414-431,654-682,852,1270-1360,1362-1417,2128`.

### Exact commands and results

```powershell
dotnet build .\TCFAnimation.sln -c Debug
dotnet build .\TCFAnimation.sln -c Release
```

Both: exit 0, 0 warnings, 0 errors.

```powershell
dotnet run --project .\ControllerProbe\ControllerProbe.csproj `
  -c Release --no-build
```

`CONTROLLER_PROBE_PASS assertions=476 ... capture_root=true ...`

```powershell
python -B -m py_compile `
  .\FrameExtraction\foreground_cutout.py `
  .\FrameExtraction\extract_directional_turns.py `
  .\FrameExtraction\extract_right_walk.py `
  .\FrameExtraction\extract_clap.py `
  .\FrameExtraction\extract_cross_arm.py `
  .\FrameExtraction\validate_release.py
```

Exit 0.

The four extractors ran in the required order twice. Evidence
`evidence/rework2/developer/determinism-comparison.json` records:
`sources_identical=true`, `frames_identical=true`, sources 6, frames 33,
zero mismatches.

```powershell
python -B .\FrameExtraction\validate_release.py
```

Exit 0:

```text
negative_control_ok=in_envelope_dark_rectangle rejected=true
negative_control_ok=detached_cool_fragment rejected=true
viewport_fit_ok=logical:1920x1080 override:1280x720
continuity_ok=boundaries:45 front_identity=bytes_and_pixels
shared_lower_plate=7 seam_y=700
pack_payload_ok=TCFAnimation.exe ... denied_payloads=false
ASSET_RELEASE_CHECK_PASS frames=33 sources=6 read_only=true
```

```powershell
.\export-release.ps1
```

Exit 0:

```text
RELEASE_STAGE_READY seed_files=50 preimport_files=51
RELEASE_EXPORT_PASS ... isolated_stage=true import_exit=0 import_errors=0
export_exit=0 export_errors=0 exe_bytes=100192168
managed_dll_bytes=75264
```

Final executable SHA-256:
`DE3A85361D9B9D80D6E0E9E9A1168D74F71551CD5FF1A3E3F597D7A407502209`.

```powershell
evidence\rework2\developer\run-capture-matrix.ps1
```

`CAPTURE_MATRIX_PASS cases=8 positive_bytes=149185
root=...\Build\Captures no_overwrite=true staging=0 cleanup=true`.
The positive capture is
`evidence/rework2/developer/after/capture-positive.png`; the release
`Build\Captures` directory is empty/removed after the test.

```powershell
evidence\rework2\developer\run-viewport-capture-matrix.ps1
```

`VIEWPORT_CAPTURE_MATRIX_PASS cases=6` with actual captures:
1280x720, 1920x1080, 1536x864 for requested 1536x960, 1536x864,
1280x720, and 1000x562 for requested 1000x800. Every capture retained
uniform 16:9 content and showed the right-edge character/dialogue fully
inside.

```powershell
Push-Location C:\Windows
& <final Build\TCFAnimation.exe> --headless -- --verify-runtime
Pop-Location
```

`RUNTIME_SMOKE_PASS frames=33 dialogue_ui=true
viewport_fit=1920x1080:CanvasItems:Keep`, exit 0.

Authenticated Godot development import and startup both exited 0. The import
recreated `GlobalInputPolicy.cs.uid` from cache; the generated sidecar was
removed immediately and is absent in the final tree.

Legacy RW1 security tests passed 13/13. RW2, RW3, and RW4 reruns passed every
functional assertion (15, 9, and 10 respectively) and then intentionally
tripped their pre-RC2 protected-baseline sentinel because 23 approved RC2
protected files changed. The scope-aware replacement
`evidence/rework2/developer/rc2-protected-comparison.json` passes:
74 checked, 0 missing, 23 expected changes, 0 unapproved changes.

### Visual evidence and measurements

- 33/33 active frames inspected at native, 2x, and feet crops.
- 45/45 boundaries inspected in individual and aggregate sheets.
- Stationary maxima: head 3.07 px, torso 3.79 px, baseline 2 px,
  lower-band difference 2.9593%.
- Moving maxima: head 5.90 px, torso 13.80 px, landmark 4 px,
  baseline 12 px, height 12 px.
- Six walk pairs remain exact mirrors.
- Six immutable source hashes remain the approved values.
- Stale release stages: 0; staging files: 0; Waving runtime/resource
  references: 0.

Developer evidence root:
`evidence/rework2/developer/`.

- 2026-09-04 RC2-T1..T4 developer PASS: capture matrix 8/8, viewport matrix 6/6, probe 476, all 33 frames regenerated twice deterministically, validator strengthened, 45/45 boundaries within thresholds, new secure export and smoke pass. RC2-T5 independent tests started.

- 2026-09-04 RC2-T5 TEST PASS: 589 passed, 0 mandatory failed, 0 skipped; capture positive-first matrix, 24 viewport containment cases, 33-frame determinism, 45 boundaries, secure export/package all pass. RC2-T6 reviews dispatched.

- 2026-09-04 RC2 security PASS (0 Critical/High/Medium/Low; 1 accepted informational). RC2 code review CHANGES_REQUIRED: explicit standalone context, independent row-640 plate, and complete adversarial/metric validator. Reviewer request to remove unrelated untracked sheets is rejected because CTO explicitly requires preserving them and no commit/staging is occurring. Rework cycle 3 started.

- 2026-09-04 RC3 developer PASS: explicit capture context, validator-owned row-640 plate, seven adversarial controls/independent metrics, probe 481, deterministic frames, 45 boundaries, new export. Independent RC3 test gate started.

- 2026-09-04 RC3 independent test: 681 pass, 1 fail, 0 skipped. Sole failure is stale canonical visual evidence generator referencing removed LOWER_BODY_SEAM_Y; all product/capture/row640/adversarial/export surfaces passed. Rework cycle 4 focused tooling fix.

- 2026-09-04 RC4 developer PASS: reproduced the sole generator failure, replaced the stale seam reference with an independent local `EXPECTED_STATIONARY_PLATE_SEAM_Y = 640`, asserted both imported production seam values remain 640 before output, regenerated the complete 33-frame/45-boundary visual evidence twice with 85/85 output hashes identical, verified 21 contact sheets, 45 individual and 5 aggregate boundary sheets, row-640 RGB/visibility equality for all 7 plate consumers, and passed the production validator, independent continuity harness, 39-file release preservation, 18-file protected-asset preservation, project diff check, and transient cleanup. Focused independent gate rerun remains pending.

- 2026-09-04 RC4 focused test PASS: 15/15 focused; cumulative RC3 682 passed, 0 failed, 0 skipped. Final code/security re-reviews dispatched.

- 2026-09-04 Final security PASS unchanged. Code review CHANGES_REQUIRED: Right_turn1_to_turn2 shoulder delta 16px bypasses <=10px turning landmark contract due label-based walk-only enforcement. Rework cycle 5 focused asset/validator/evidence correction.

- 2026-09-04 RC5 developer PASS: preserved the failing 16 px
  `Right_turn1_to_turn2` record and prior 41 px turning-mutation acceptance;
  replaced label-substring gating with explicit validator-owned stationary,
  turning, and walking boundary records; enforced shoulder/waist <=10 px for
  every moving boundary; added independently calibrated 41 px turning
  mutations to production and independent harnesses; replaced the visual
  generator's top-row proxy with independently measured green-vest shoulder
  and waist landmarks; and translated only `RightTurn/turn_1` upward by six
  whole pixels. Two full 33-frame extractor passes and two 85-file visual
  generator passes were deterministic. Production and independent validators
  passed all 45 boundaries with moving maxima head 5.898610, torso 13.796428,
  shoulder 10, waist 9, baseline 11, and height 12; the intended 41 px turning
  mutation was rejected. Debug/Release builds, 481-assertion probe, read-only
  validation, protected-asset preservation, secure isolated export, package
  validation, and exported runtime smoke passed. Final executable:
  100185624 bytes,
  `6421D47A982DD97427C4F2DCAC0F79F9E06F4D411AE8CEB7F4606A8EFB5EF9E9`.
  Independent code review/final QA/judge remain pending.

- 2026-09-04 RC5 code review APPROVED. Final RC2-T7/RC5 QA started against executable SHA-256 6421D47A982DD97427C4F2DCAC0F79F9E06F4D411AE8CEB7F4606A8EFB5EF9E9.

- 2026-09-04 Final QA: 20 scenarios, 16 pass, 0 fail, 4 blocked by locked console. All 33 frames and 45 boundaries human PASS; capture/viewport/animation/package pass. Foreground Unicode/dialogue and physical screen acceptance unproven. Judge dispatched for formal verdict.

- 2026-09-04 Engineering Judge REJECTED solely for external locked-session evidence gap. Mission BLOCKED pending console unlock and rerun of QA scenarios 17-20 against unchanged EXE hash 6421D47A982DD97427C4F2DCAC0F79F9E06F4D411AE8CEB7F4606A8EFB5EF9E9. No product failure found; no code change requested.
