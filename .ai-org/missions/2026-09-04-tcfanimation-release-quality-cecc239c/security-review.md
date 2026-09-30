# TCFAnimation RC3/RC4 Final Independent Security Review

Date: 2026-09-04  
Repository: `C:\Users\syedhu\source\repos\Dreamer`  
Project: `C:\Users\syedhu\source\repos\Dreamer\TCFAnimation`  
Branch/HEAD: `main` / `cdcc5d50eabc4d29dce8609041e3a1063dc44806`  
Reviewed state: authoritative uncommitted working tree and current generated
Windows package

## SECURITY RESULT

Scope: RC3 capture-context selection and root retention; capture path and
publication boundaries; RC4 row-640 evidence/validator correction; current
runtime, extraction, validation, launch, export, staging, provenance,
dependency-lock, package, and Build surfaces touched by those changes. Sibling
projects and repository history were not reviewed.

Critical: **0**  
High: **0**  
Medium: **0**  
Low: **0**  
Informational: **1**

Blocking findings:

None.

Conclusion: **PASS**

## Threat model

### Actors

- A local user supplying capture-mode command-line values.
- A same-user process able to modify or race writable filesystem paths.
- A local actor attempting to substitute a Godot executable, companion,
  export template, release-stage input, source sheet, frame, or Python wheel.
- A recipient inspecting or executing the generated Windows package.

### Entry points

- Godot runtime feature flags `standalone` and `template`.
- `OS.GetExecutablePath()`, `AppContext.BaseDirectory`, and globalized
  `res://`.
- `--capture-frame`, `--capture-path`, `--capture-direction`, and
  `--dialogue-preview`.
- Six immutable source images, generated frame files, export preset,
  stage manifest, provenance JSON, and dependency lock.

### Trust boundaries and dangerous sinks

- Runtime-derived paths cross into directory creation, staging-file creation,
  durable writes, cleanup, and final no-overwrite publication.
- Configured/bundled/PATH tool candidates cross into process execution only
  after provenance validation.
- Source images cross into Pillow/OpenCV native decoders only after exact-size,
  regular-file, non-reparse, streaming SHA-256 checks.
- The exact 50-file source seed plus one fixed generated solution cross into
  Godot import/export and the final package.
- PCK metadata crosses into bounded parsing and denied-token inspection.

### Assets

- Containment and integrity of executable-adjacent or project-local captures.
- Existing capture files and unrelated user files.
- Integrity of the six immutable source sheets and 33 active frames.
- Confidentiality of excluded source sheets, Waving assets, development
  tooling, mission evidence, credentials, and local paths.
- Integrity and provenance of the release executable, managed assembly, Godot
  toolchain, template, and Python wheels.

### Security assumptions

- This local application exposes no remote service, account, tenant, or
  privileged operation; AuthN/AuthZ and object-level authorization are not
  applicable.
- Requirement A-01 explicitly accepts the residual same-user capture TOCTOU
  race for this developer-only feature.
- The `template` compatibility feature is treated as an export-template
  runtime signal. The actual approved Godot editor run selected
  `project_resource`; the actual exported binary selected
  `executable_adjacent`.

## Security disposition

### Capture-context construction and template compatibility

**PASS.**

- `CaptureRootContext.FromGodotFeatures` selects standalone behavior when
  either Godot's `standalone` or export-template compatibility feature is
  present (`TCFAnimation/CapturePathPolicy.cs:7-24`).
- An actual approved Godot project/editor process emitted
  `CAPTURE_ROOT kind=project_resource`; the actual exported executable emitted
  `CAPTURE_ROOT kind=executable_adjacent`. Both headless requests then failed
  only because no rendering backend was available, and neither created a
  target or staging file.
- Even when the compatibility feature selects the standalone branch, the
  resolver does not use the project resource root. It validates the
  application base, current process executable, executable directory, and
  either the direct executable base or the exact direct-child
  `data_<executable-name>_windows_x86_64` layout
  (`TCFAnimation/CapturePathPolicy.cs:37-129`). Missing, relative, reparse,
  non-regular, or unexpected-layout inputs throw; there is no alternate or
  user-directory fallback.
- The probe explicitly proves that a nonempty `ProjectResourceRoot` is ignored
  after standalone/template selection and that an editor feature set with
  neither flag selects the project resource root
  (`TCFAnimation/ControllerProbe/Program.cs:1454-1544`).
- The root is resolved once at startup, stored once in the immutable capture
  configuration and `_captureRoot`, and reused for capture directory,
  staging, reparse, and publication operations
  (`TCFAnimation/TurnController.cs:55-69,645-660,737-768`).
- The new root-selection marker logs only the fixed kind value
  `project_resource` or `executable_adjacent`, not either root value
  (`TCFAnimation/TurnController.cs:66`). Existing local capture diagnostics
  can include the final local target path and Godot stack traces, but no
  credential, file content, or newly selected resource-root value is emitted,
  and the application has no remote logging/telemetry sink.

### Capture containment and publication

**PASS.**

- Capture names remain direct-child `.png` names. Absolute, UNC, device,
  drive-relative, nested, traversal, alternate-stream, invalid, trailing
  dot/space, reserved-device, wrong-extension, and existing-target forms are
  rejected (`TCFAnimation/CapturePathPolicy.cs:143-233`).
- Publication retains root-to-leaf reparse checks, a GUID-named
  same-directory staging file, `FileMode.CreateNew`, `FileShare.None`,
  `WriteThrough`, disk flush, publication revalidation, and
  `File.Move(..., overwrite:false)`
  (`TCFAnimation/CapturePathPolicy.cs:235-331`;
  `TCFAnimation/TurnController.cs:737-768`).
- The current exported capture matrix passed eight of eight positive,
  invalid, escape, existing-target, and cleanup cases:
  `CAPTURE_MATRIX_PASS cases=8 ... no_overwrite=true staging=0 cleanup=true`.
- The RC3 context attack matrix passed development/standalone selection, an
  actual `Build\Captures` junction rejection with zero target files, and an
  ACL read-only portable copy with no fallback and no capture directory:
  `RC3_CAPTURE_CONTEXT_PASS cases=4 ... reparse=fail_closed
  readonly=fail_closed`.

### Row-640 extraction, validation, and evidence changes

**PASS for security.**

- Production extraction and the independent release validator retain separate
  fixed row-640 constants
  (`TCFAnimation/FrameExtraction/extract_cross_arm.py:58`;
  `TCFAnimation/FrameExtraction/validate_release.py:126`).
- The repaired evidence generator owns a third local expected value and checks
  both imported production values before producing evidence
  (`.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework2/developer/generate_t3_t4_evidence.py:27,246-260`).
- The changed production path is deterministic image-array processing over
  fixed project-relative sources and outputs. Its CLI accepts only boolean
  evidence/validation switches and a fixed three-value evidence-stage choice;
  it introduces no subprocess, shell, network, deserialization, dynamic
  evaluation, or caller-selected production input/output path.
- The canonical test report records 682 passed, 0 failed, and 0 skipped. The
  focused RC4 proof generated the complete evidence twice with 85/85 output
  hashes identical, rejected a production seam mutation to 641, and preserved
  release, protected assets, and the complete project tree.

### Release, package, provenance, and dependencies

**PASS.**

- `Get-ApprovedGodot -Purpose Export` authenticated the bundled Godot
  executable, its required companion, and the export template before
  execution or Build cleanup. The selected engine reported
  `4.5.1.stable.mono.official.f62fdbde1` and the pinned executable SHA-256.
- The compatibility batch launcher is a three-line fixed PowerShell shim and
  never reads or reparses `GODOT_EXE`
  (`TCFAnimation/run-animation.bat:1-3`). Runtime invocation uses a PowerShell
  argument array (`TCFAnimation/run-animation.ps1:19-35`).
- Release staging remains an exact 50-file literal seed plus the one
  fixed-byte, exact-hash generated solution. Reparse points, unexpected
  inventory, cleanup failure, Godot error diagnostics, missing executable, and
  missing managed assembly fail closed
  (`TCFAnimation/release-tooling.ps1:710-1324`;
  `TCFAnimation/export-release.ps1:72-283`).
- The selected export boundary remains `Main.tscn` plus exactly 33 active PNGs,
  with empty include/exclude filters, embedded PCK, source content disabled,
  and debug symbols disabled (`TCFAnimation/export_presets.cfg:1-29`).
- The current validator passed all source/frame/continuity/export/dependency/
  package checks. The package has 73 PCK entries: one scene, two one-byte
  script placeholders, 33 textures, 33 import metadata records, and four
  engine metadata records. It found no denied payload, source content, denied
  metadata token, PDB, or absolute project path.
- Current generated artifacts:
  - `TCFAnimation/Build/TCFAnimation.exe`: 100,185,896 bytes, SHA-256
    `6DA4537724DFD05564BD8191E3905499CEB6E057DB56B0CA45B7212D9C5C1AAA`.
  - `TCFAnimation/Build/data_TCFAnimation_windows_x86_64/TCFAnimation.dll`:
    78,336 bytes, SHA-256
    `EA9CFB0D15853B1340962E2F3E13630B7C739E9918D352C2E76B8FE8DED65A2A`.
- Build inventory contained 187 files, zero denied source/tooling files, zero
  capture directories, and zero release stages.
- Python `pip-audit` reported `No known vulnerabilities found`. NuGet audit
  reported no vulnerable packages for `TCFAnimation` or `ControllerProbe`
  using the configured Microsoft package sources.
- A scoped secret-pattern scan found one syntactic match, manually confirmed
  as the fixed non-secret staging-token test constant at
  `TCFAnimation/ControllerProbe/Program.cs:1270`; no credential or private-key
  material was found.

## All findings

### [INFORMATIONAL] Accepted same-user capture TOCTOU residual remains

Location:     `TCFAnimation/CapturePathPolicy.cs:274-331`;
`TCFAnimation/TurnController.cs:743-768`

Issue:        Reparse/existence validation and subsequent path-based staging
creation or final move are separate filesystem operations. A malicious
process already running as the same user can attempt to swap a checked path
component during those intervals.

Attack path:  Same-user process with write access to the executable/project
directory -> observe or race a developer capture -> replace a checked path
component after validation but before staging creation or final
`File.Move`.

Impact:       Capture denial, redirection, or publication manipulation within
the attacker's existing same-user filesystem authority. There is no remote,
cross-user, service, or elevation boundary in the current feature.

Fix:          No remediation is required under accepted requirement A-01. If
capture later crosses a user, service, or elevation boundary, protect the
directory with OS-enforced ACL isolation and use handle-relative no-follow
operations rather than path revalidation.

Confidence:   High

## STATUS

STATUS:          **PASS**

SUMMARY:

The RC3 capture-context change is fail-closed. The actual approved editor and
exported runtime selected the expected distinct root kinds; template
compatibility cannot make capture use a caller-controlled resource root, and
the standalone branch validates the executable/base-directory relationship
before retaining one executable-adjacent root. Capture path containment,
reparse rejection, exclusive staging, and no-overwrite publication remain
effective. The row-640 RC4 correction is deterministic evidence/data
processing and adds no execution or caller-controlled path sink. Current
provenance, dependencies, selected-resource package, metadata, and Build
hygiene passed. There are zero unresolved Critical or High findings.

WORK_COMPLETED:

- Read canonical final `test-results.md`, prior `security-review.md`,
  `architecture.md`, ADRs in `decisions.md`, `requirements.md`, and
  `task-plan.md`.
- Reviewed the actual current capture/runtime, extraction, validator,
  launcher, release-tooling, export, provenance, dependency-lock, package, and
  Build surfaces.
- Re-ran the 481-assertion ControllerProbe.
- Re-ran the four-case RC3 development/exported context, reparse, and read-only
  attack matrix.
- Re-ran the eight-case exported capture escape/no-overwrite matrix.
- Re-ran the production read-only release/package validator.
- Re-authenticated the pinned engine, companion, and export template.
- Re-ran Python and NuGet vulnerability audits, package/hash/hygiene checks,
  scoped secret-pattern review, project-scoped diff check, and final fixture
  cleanup checks.
- Updated this canonical final security verdict.

EVIDENCE:

- `CONTROLLER_PROBE_PASS assertions=481 ... capture_root=true
  fixed_runtime_frames=33 wave_assets=false`.
- `RC3_CAPTURE_CONTEXT_PASS cases=4 development=project_resource
  standalone=executable_adjacent reparse=fail_closed readonly=fail_closed`.
- `CAPTURE_MATRIX_PASS cases=8 positive_bytes=149185
  root=...\Build\Captures no_overwrite=true staging=0 cleanup=true`.
- `continuity_ok=boundaries:45 front_identity=bytes_and_pixels
  shared_lower_plate=7 seam_y=640`.
- `export_whitelist_ok=selected_resources count=34
  denied_development_assets=false source_content=false debug_symbols=false`.
- `requirements_lock_ok=index=microsoft_proxy binary_only=true packages=3
  python=cp313 platform=win_amd64 hashes=required`.
- `pack_manifest_read=TCFAnimation.exe pack_version=3 engine=4.5.1
  entries=73`.
- `pack_payload_ok=TCFAnimation.exe runtime_scene=1 runtime_scripts=2
  runtime_textures=33 import_metadata=33 engine_metadata=4
  denied_payloads=false source_content=false metadata_denied_tokens=0`.
- `managed_payload_ok=pdb_absent absolute_project_paths=false
  project_assemblies=1`.
- `ASSET_RELEASE_CHECK_PASS frames=33 sources=6 read_only=true
  export_resources=34 artifact_manifest=checked_if_present`.
- `PROVENANCE_PASS source=bundled
  version=4.5.1.stable.mono.official.f62fdbde1`.
- Python audit: `No known vulnerabilities found`.
- NuGet audit: no vulnerable packages in either project.
- `BUILD_HYGIENE files=187 denied=0 stages=0 captures=0`.
- `CLEANUP captures=0 stages=0 staging=0 pycache=0`.
- `git diff --check -- TCFAnimation`: exit 0.
- Canonical cumulative tests: **682 passed, 0 failed, 0 skipped**.

ARTIFACTS:

- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/security-review.md`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/test-results.md`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework3/tests/capture-context-results.json`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework2/developer/capture-matrix.json`
- `TCFAnimation/Build/TCFAnimation.exe`
- `TCFAnimation/Build/data_TCFAnimation_windows_x86_64/TCFAnimation.dll`

FINDINGS:

- Critical: none.
- High: none.
- Medium: none.
- Low: none.
- Informational: accepted same-user capture TOCTOU residual (A-01).
- Prior High launcher/code-execution findings remain resolved.
- Prior metadata disclosure, unbounded source hashing, and dependency
  integrity findings remain resolved.
- RC3/RC4 introduced no new execution, path escape, overwrite, secret, or
  package disclosure finding.

RISKS:

- The accepted same-user capture TOCTOU must be re-reviewed if capture becomes
  remote, cross-user, elevated, service-hosted, or writable by a different
  trust principal.
- Existing local diagnostic output may contain local target paths and Godot
  stack traces. It is not transmitted by this application and contains no
  credential or file content; do not forward raw diagnostics to an untrusted
  external telemetry sink without redaction.
- The executable is not Authenticode signed. This gate proves pinned local
  build provenance and package content, not downstream publisher identity.
- Changes to Godot feature-tag semantics, engine/template/wheel identities,
  capture-root/publication code, row-640 extraction/validation contracts,
  stage manifest, package parser, or export resources invalidate this review.

BLOCKERS:

None.

NEXT_ACTION:

Proceed to the final engineering judgment/release gate. Do not grant release
if any security-sensitive file or pinned artifact changes after this review.
