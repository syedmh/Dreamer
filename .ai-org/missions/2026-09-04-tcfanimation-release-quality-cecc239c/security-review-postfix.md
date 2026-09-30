# TCFAnimation Independent Post-Fix Security Gate

Date: 2026-09-04  
Role: independent Security Engineer  
Repository: `C:\Users\syedhu\source\repos\Dreamer`  
Project: `C:\Users\syedhu\source\repos\Dreamer\TCFAnimation`  
Branch/HEAD: `main` / `7192e3930656eb09bb0ac63c4b7322d903982f00`  
Reviewed state: current uncommitted TCFAnimation source and the exact current
Windows release after the Alt+Enter early-input fix

## SECURITY RESULT

Scope: Current TCFAnimation runtime and early/global input routing; dialogue
Unicode bounds and safe rendering; capture-root selection, direct-child path
containment, overwrite prevention, reparse handling, staging and publication
TOCTOU; batch/PowerShell process invocation; Godot executable, companion, and
export-template provenance; guarded Build cleanup and isolated release
staging; export allow-list and package contents; immutable raw-source
integrity and import handling; Python/NuGet dependencies and vulnerability
feeds; secrets and sensitive-data exposure. Sibling projects and unrelated
repository changes were not reviewed.

Critical: **0**  
High: **0**  
Medium: **0**  
Low: **0**  
Informational: **1**

Blocking findings:

None.

Conclusion: **PASS**

## Exact release identity

The reviewed release files are regular, non-reparse files and exactly match
the identities supplied for this post-fix gate:

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `Build\TCFAnimation.exe` | 100,185,624 | `6C1EEA789E89897BECFCD6A90A5ABDB4B591E162EF1C1E780FDAD82E3B83C96F` |
| `Build\data_TCFAnimation_windows_x86_64\TCFAnimation.dll` | 78,848 | `1C62F905EF43B76BD5C5E068ED5737F563F0F574E9CFE3E7539A19FF35AD2518` |

The hashes were checked before security execution and again after the
launcher, capture, staging, export, runtime, and package checks. They remained
identical. Final cleanup was `stages=0 captures=0 staging=0`.

## Threat model

### Actors

- A local user supplying runtime capture and dialogue-preview arguments.
- A same-user process able to race writable capture or tooling paths.
- A local actor attempting to substitute a Godot executable, its companion,
  the export template, a stage input, a source image, or a Python package.
- A recipient executing the produced Windows release.

### Entry points and trust boundaries

- Godot early input, focused LineEdit input, and unhandled gameplay input.
- `--capture-frame`, `--capture-direction`, `--capture-path`,
  `--dialogue-preview`, and `--verify-runtime`.
- `GODOT_EXE`, bundled/PATH Godot selection, process arguments, provenance
  JSON, and export-template location.
- Release-stage manifest entries, project files, immutable source sheets,
  generated runtime frames, `.import` data, Python lock data, and generated
  PCK/managed payloads.

### Security-sensitive sinks and assets

- Fullscreen/window state changes and dialogue/gameplay input ownership.
- Directory creation, staging-file creation, durable PNG writes, cleanup, and
  final no-overwrite file publication.
- Process execution and destructive Build/stage cleanup.
- Native image decoders, Godot import/export, packaged resource selection,
  and downstream executable execution.
- Integrity of six immutable source sheets, 33 active frames, release
  executable/assembly, and exclusion of development/raw/evidence content.

### Applicability

The application exposes no network listener, account system, tenant boundary,
privileged service, database, template engine, HTML renderer, or remote
deserialization endpoint. AuthN/AuthZ, SQL/NoSQL injection, CORS, TLS, and
object-level authorization are therefore not applicable to this release.

## Security assessment

### Early input and global routing

**PASS.** `TurnController._Input` evaluates F11, Alt+Enter, and fullscreen
Escape before focused controls and calls `SetInputAsHandled` for resolved
global actions (`TurnController.cs:202-244`). The pure policy accepts only
pressed, non-echo events in the early-input phase
(`GlobalInputPolicy.cs:29-63`). `DialogueUi` explicitly leaves Alt+Enter for
the global route while retaining plain Enter and editing Escape
(`DialogueUi.cs:46-88`). Gameplay remains in `_UnhandledKeyInput` and is
suppressed while dialogue editing is active
(`TurnController.cs:247-326`). The independent probe passed all routing
combinations and ended:

`CONTROLLER_PROBE_PASS assertions=489 ... dialogue_input=true dialogue_layout=true global_input=true capture_root=true`

No input path reaches a command, file, network, or dynamic-code sink.

### Dialogue Unicode, bounds, and rendering

**PASS.** Input is bounded to 500 Unicode scalar values by rune enumeration
before normalization (`DialogueModel.cs:95-115,150-166`). The LineEdit also
has a 500-character UI bound (`DialogueUi.cs:164-174`). Wrapping and
ellipsis fitting operate on runes rather than splitting UTF-16 surrogate
pairs (`DialogueModel.cs:171-305`). Submitted text is assigned to a Godot
`Label.Text`; there is no HTML/markup evaluation, shell interpolation,
format-string execution, or persistence sink (`DialogueUi.cs:245-315`).
Rendering allocation is bounded by the input and four-line layout limits.

### Capture path containment, overwrite, reparse, and publication

**PASS with the accepted informational residual below.**

- Capture names must be direct-child `.png` file names. Absolute, UNC/device,
  drive-relative, nested, traversal, alternate-stream, invalid-character,
  trailing-dot/space, reserved-device, wrong-extension, and existing-target
  values fail closed (`CapturePathPolicy.cs:171-228`).
- Standalone capture roots are executable-adjacent only after validating the
  executable/base-directory relationship; editor capture uses the globalized
  project resource root. Selected roots and executable files must be regular
  and non-reparse (`CapturePathPolicy.cs:7-164`).
- Staging uses a random 32-hex token, a direct-child same-directory path,
  `FileMode.CreateNew`, `FileShare.None`, `WriteThrough`, and disk flush.
  Publication rechecks containment, staging-file type, reparse components,
  and target nonexistence before `File.Move(..., overwrite:false)`
  (`CapturePathPolicy.cs:230-331`; `TurnController.cs:746-816`).
- The exact release capture matrix passed eight cases:
  `CAPTURE_MATRIX_PASS cases=8 ... no_overwrite=true staging=0 cleanup=true`.
- The context/reparse/read-only matrix passed four cases:
  `RC3_CAPTURE_CONTEXT_PASS cases=4 development=project_resource standalone=executable_adjacent reparse=fail_closed readonly=fail_closed`.

### Batch and PowerShell process invocation

**PASS.** `run-animation.bat` is a fixed three-line shim that invokes the
fixed system Windows PowerShell path and does not interpolate or parse
`GODOT_EXE` (`run-animation.bat:1-3`). `run-animation.ps1` obtains an approved
executable object and invokes its exact path with a fixed argument array
(`run-animation.ps1:1-38`). Export invocation uses `UseShellExecute=false`;
the executable path is separate from a Windows-command-line encoder for
arguments (`release-tooling.ps1:1336-1496`).

The launcher/provenance attack suite passed 13/13, including batch metacharacter
injection, script candidate rejection, mutated executable/companion rejection,
authoritative configured-path failure, template validation before Build
cleanup, and child exit-code propagation:

`RW1_SELF_TEST_PASS tests=13`

### Provenance and hash authentication

**PASS for the local build boundary.** Provenance JSON is parsed with an exact
schema and compared against frozen executable, companion, and template
identities (`release-tooling.ps1:68-207`). Candidate files must be regular,
non-reparse `.exe` files; size and streaming SHA-256 are verified before use
(`release-tooling.ps1:209-371`). The selected Godot executable reported:

`4.5.1.stable.mono.official.f62fdbde1`

Both Run and Export selection authenticated bundled SHA-256:

`FD5C88FD05AFC7C2D965777320DEAEC5A80C31363C0BB33D7B48025376F96C5F`

Export additionally authenticated the frozen companion and release template
before destructive Build cleanup or execution.

### Export staging and destructive guards

**PASS.** Build deletion is restricted to the exact project-local `Build`
path and rejects a reparse Build root (`export-release.ps1:27-82`). The
release-stage manifest is an exact, ordered 50-file allow-list with strict
UTF-8, path, duplicate, containment, and regular-file checks
(`release-tooling.ps1:653-810`). Source paths and stage inventory reject
reparse points. Stage removal requires an exact direct child of Build named
`.release-stage-<32 hex>` (`release-tooling.ps1:812-900`). The generated
solution is fixed-byte/fixed-hash and created with `CreateNew`
(`release-tooling.ps1:1125-1324`).

The staging/failure suite independently passed the manifest, seed,
fixed-solution, unexpected-inventory, real-export, development-state,
runtime-smoke, import/export failure, zero-exit diagnostic failure,
missing-output, and cleanup-failure cases. Its final historical
`protected-baseline` assertion reported 25 changed files because that fixture
describes an earlier pre-mission snapshot; therefore the overall legacy
script exited 1 and was not treated as a qualifying whole-suite PASS. The
current project status, protected source hashes, exact release hashes,
validator, and cleanup state were rechecked separately after this command.

### Export allow-list, raw inputs, and package data exposure

**PASS.** The export preset selects only `Main.tscn` plus 33 active runtime
PNGs, uses an embedded PCK, and disables script source content and debug
symbols (`export_presets.cfg:1-31`). Raw root images, development extractors,
Waving assets, source sheets, evidence, scripts, project files, and PDBs are
not packaged. The exact release contained 187 files, zero reparse entries,
zero forbidden source/tooling files, and only the expected managed
`deps.json` and `runtimeconfig.json` metadata files.

The package validator reported:

- `pack_manifest_read=TCFAnimation.exe pack_version=3 engine=4.5.1 entries=73`
- `pack_payload_ok=... runtime_scene=1 runtime_scripts=2 runtime_textures=33 import_metadata=33 engine_metadata=4 denied_payloads=false source_content=false metadata_denied_tokens=0`
- `managed_payload_ok=pdb_absent absolute_project_paths=false project_assemblies=1`
- `ASSET_RELEASE_CHECK_PASS frames=33 sources=6 read_only=true export_resources=34 artifact_manifest=checked_if_present`

The six immutable source sheets were regular, non-reparse files and matched
their exact frozen size/SHA-256 contracts before decoder use. The extraction
integrity helper performs bounded streaming hashing with pre-open,
open-handle, and post-close file checks (`FrameExtraction/file_integrity.py:17-77`).
JPEG data stored under `.png` source names is accepted only where the frozen
contract explicitly requires a JPEG container and exact dimensions/hash.
Untracked root source/import assets are excluded from the release-stage and
export allow-lists.

### Dependencies, supply chain, secrets, and configuration

**PASS.**

- `requirements.txt` uses one approved Microsoft proxy index,
  `--only-binary=:all:`, exact versions, and SHA-256 hashes for three expected
  packages. The release validator confirmed the exact lock contract.
- `python -m pip_audit -r requirements.txt --disable-pip`:
  `No known vulnerabilities found`.
- NuGet audit for `TCFAnimation.csproj` and `ControllerProbe.csproj` reported
  no vulnerable packages using the configured Microsoft proxy and local SDK
  package source.
- Managed release dependencies are limited to `GodotSharp/4.5.1`,
  `Microsoft.NETCore.App` runtime pack `8.0.30`, and the application assembly.
  Unsafe BinaryFormatter serialization is disabled in runtime configuration.
- Scoped secret scan: `SECRET_SCAN_FILES=36 HITS=0`. No credential, private
  key, bearer token, connection string, or hardcoded secret was found.
- No application network client/listener, authentication store, unsafe
  deserializer, weak/custom cryptography, or remote telemetry sink was found.

## All findings

### [INFORMATIONAL] Accepted same-user capture TOCTOU remains

Location:     `TCFAnimation/CapturePathPolicy.cs:274-331`;
`TCFAnimation/TurnController.cs:758-815`

Issue:        Reparse/existence validation and subsequent directory creation,
staging creation, final move, or failure cleanup are separate path-based
filesystem operations. They cannot provide handle-relative no-follow
atomicity against a process with concurrent write access to the same
directories.

Attack path:  A malicious process already executing as the same Windows user
and able to modify the executable/project directory races a developer capture
by replacing a checked path component after validation and before staging,
publication, or cleanup.

Impact:       Capture denial, redirection, or publication manipulation within
the attacker's existing same-user filesystem authority. This creates no
remote, cross-user, service, privilege-elevation, or tenant boundary.

Fix:          No remediation is required under accepted mission assumption
A-01 while capture remains a developer/local same-user feature. If the
feature becomes remote, elevated, service-hosted, cross-user, or writes into
a directory controlled by another principal, enforce ACL isolation and use
handle-relative no-follow filesystem operations.

Confidence:   High

## Accepted residual risks

- The same-user capture TOCTOU described above is explicitly accepted under
  A-01 and must be reopened if the trust boundary changes.
- `TCFAnimation.exe` is not Authenticode signed. The supplied SHA-256 proves
  the exact local artifact reviewed here, but publisher identity and
  downstream distribution-channel authenticity require a trusted hash
  channel or code signing.
- Local capture failure/success diagnostics can contain local target paths
  and Godot error details. The application has no remote telemetry sink; do
  not forward raw diagnostics to an untrusted service without redaction.
- This verdict is bound to the exact source state and EXE/DLL hashes above.
  Any change to runtime input routing, capture publication, release tooling,
  provenance, dependencies, stage allow-list, export resources, or either
  binary invalidates the gate.

## Commands and evidence

- `Get-FileHash -Algorithm SHA256` and file-attribute/size checks on the EXE
  and DLL, before and after all checks.
- `Get-AuthenticodeSignature Build\TCFAnimation.exe`:
  `Status=NotSigned`.
- `dotnet run --project ControllerProbe\ControllerProbe.csproj -c Release --no-build --no-restore`:
  `CONTROLLER_PROBE_PASS assertions=489`.
- `python -B FrameExtraction\validate_release.py`: exit 0 and final
  `ASSET_RELEASE_CHECK_PASS`.
- Direct `Get-ApprovedGodot` Run/Export calls: pinned executable, companion,
  template, and version authenticated.
- `evidence\rework\RW1\self-test.ps1`:
  `RW1_SELF_TEST_PASS tests=13`.
- `evidence\rework3\tests\run-capture-context.ps1`:
  `RC3_CAPTURE_CONTEXT_PASS cases=4`.
- `evidence\qa-final\run-final-capture-matrix.ps1`:
  `CAPTURE_MATRIX_PASS cases=8`.
- `evidence\rework\RW2\self-test.ps1`: 15 current release/staging cases
  passed; stale historical protected-baseline comparison failed as documented
  above. Final hashes, validator, status, and cleanup were independently
  rechecked.
- Exact release `Build\TCFAnimation.exe --headless -- --verify-runtime`:
  `RUNTIME_SMOKE_PASS frames=33 dialogue_ui=true viewport_fit=1920x1080:CanvasItems:Keep`.
- `python -m pip_audit -r requirements.txt --disable-pip`:
  no known vulnerabilities.
- `dotnet list ... package --vulnerable --include-transitive --no-restore`
  for both projects: no vulnerable packages.
- Scoped secret scan: 36 files, 0 hits.
- Build/package inventory: 187 files, 0 forbidden source/tooling files,
  0 reparse points, 0 stages, 0 captures, 0 staging files.
- `git diff --check -- TCFAnimation`: exit 0.

---

STATUS:          **PASS**

SUMMARY:

The exact post-fix source and release pass the independent security gate.
Early F11/Alt+Enter/Escape routing is bounded and cannot reach a dangerous
sink; dialogue rendering is Unicode-safe and resource-bounded; capture
containment, reparse rejection, exclusive staging, and no-overwrite
publication remain effective; launcher/process invocation and Godot
provenance tests reject injection and substitution cases; release staging and
destructive cleanup are narrowly guarded; the package excludes raw,
development, evidence, debug, and absolute-path content; vulnerability and
secret scans are clean. There are zero unresolved Critical or High findings.

WORK_COMPLETED:

- Reviewed the current uncommitted runtime, dialogue, capture, launcher,
  provenance, staging, export, extraction-integrity, package, dependency, and
  configuration surfaces.
- Re-ran current policy, attack-matrix, provenance, package, runtime,
  vulnerability, secret, hash, signature, inventory, and cleanup checks.
- Re-authenticated the exact EXE/DLL after the real-export and runtime checks.
- Recorded the accepted same-user capture race and downstream unsigned-binary
  risk without weakening any control.
- Persisted this canonical post-fix security report.

EVIDENCE:        Exact hashes and command outputs recorded above; files
reviewed include `GlobalInputPolicy.cs`, `TurnController.cs`,
`DialogueModel.cs`, `DialogueUi.cs`, `CapturePathPolicy.cs`,
`run-animation.bat`, `run-animation.ps1`, `release-tooling.ps1`,
`export-release.ps1`, `release-provenance.json`,
`release-stage-manifest.txt`, `export_presets.cfg`, `requirements.txt`,
`TCFAnimation.csproj`, `ControllerProbe.csproj`,
`FrameExtraction/file_integrity.py`, relevant extractor source-validation
paths, and `FrameExtraction/validate_release.py`.

ARTIFACTS:       `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/security-review-postfix.md`

FINDINGS:

- Critical: 0.
- High: 0.
- Medium: 0.
- Low: 0.
- Informational: 1 accepted same-user capture TOCTOU residual.

RISKS:

- Accepted same-user capture TOCTOU under A-01.
- Unsigned executable requires a trusted hash-distribution channel or future
  Authenticode signing for downstream publisher identity.
- Local diagnostic paths/details should not be forwarded without redaction.
- Any security-sensitive source, dependency, provenance, allow-list, or
  binary hash change invalidates this verdict.

BLOCKERS:

None.

NEXT_ACTION:

Proceed to the remaining independent post-fix gates/final engineering
judgment only if the source tree and exact EXE/DLL hashes remain unchanged.
No commit or push was performed.
