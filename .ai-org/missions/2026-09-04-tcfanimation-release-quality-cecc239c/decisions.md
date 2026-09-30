# ADR-001: Extend the recovered single-scene architecture
Date: 2026-09-04     Status: Accepted

## Context
The authoritative baseline is the current uncommitted TCFAnimation work. It
already separates Godot adapters from pure C# animation, geometry, dialogue,
and capture policies, and it contains the regenerated 33-frame set.

## Decision
Preserve and incrementally remediate the current single-scene runtime and pure
policy seams. Do not restart, rewrite, or restore Waving. Add only the small
pure global-input policy needed to make dialogue/fullscreen arbitration
testable.

## Consequences
Existing controls, frame names, scene shape, and test investment remain valid.
Developers must preserve the current diff and may change only concrete defects.

## Alternatives considered
- Restart from recovered HEAD — rejected because it discards authoritative
  in-progress work.
- Replace the controller with AnimationTree/AnimationPlayer — rejected because
  it expands regression scope without addressing release requirements.

# ADR-002: Use an exact selected-resource release boundary
Date: 2026-09-04     Status: Accepted

## Context
The project contains source sheets, extractors, probes, mission evidence,
obsolete/unrelated root images, and Waving files that must not ship.

## Decision
Keep the Windows export as an ordered allowlist of `Main.tscn` plus exactly 33
active frame PNGs. Clean only project-local ignored `Build`, disable source and
debug-symbol payloads, and independently validate the produced package
manifest and managed directory.

## Consequences
New runtime resources must be deliberately added to both the preset and
validator. Accidental files fail closed rather than shipping by default.

## Alternatives considered
- Export all resources with excludes — rejected because omissions leak content.
- Post-build deletion of unwanted files — rejected because it is less
  auditable and can leave embedded payloads.

# ADR-003: Keep capture local and publish by no-overwrite staging
Date: 2026-09-04     Status: Accepted

## Context
Capture is a developer QA feature receiving untrusted command-line file names.
R-14 requires project-local containment, reparse checks, no overwrite, durable
staging, and cleanup. A residual malicious same-user path-swap race is already
accepted by requirement A-01.

## Decision
Keep capture limited to direct-child `.png` files under project-local
`Captures`. Publish through a unique exclusive same-directory staging file,
flush and close it, revalidate, then move without overwrite. Fail mode parsing
and publication deterministically with a nonzero process exit.

## Consequences
Existing targets are preserved and ordinary traversal/reparse attacks fail
closed. This is not an OS-isolated secure drop location and must remain a local
developer-only feature.

## Alternatives considered
- Accept arbitrary output paths — rejected because it crosses the project
  trust boundary.
- Add a privileged helper or isolated service — rejected as disproportionate
  infrastructure and cost for developer QA.

# ADR-004: Authenticate approved Godot bytes before first execution
Date: 2026-09-04     Status: Accepted

## Context
Configured and PATH executable locations are untrusted inputs. The current
launchers execute a candidate to obtain a self-reported version, and the batch
launcher reparses `GODOT_EXE` as command source. Both are High-severity code
execution paths.

## Decision
Keep configured/bundled/PATH precedence, but require canonical `.exe`, exact
size, repository-pinned streaming SHA-256, and required-companion verification
before any candidate executes. Verify the export template before export.
Move all environment access and invocation to PowerShell; retain
`run-animation.bat` only as a fixed-PowerShell compatibility shim that never
reads or expands `GODOT_EXE`.

## Consequences
Only byte-identical approved Godot 4.5.1 Mono distributions can run or export.
Future approved engine builds require an explicit provenance update. Existing
arbitrary-cwd, configured executable, PATH, exact-version, and exit-code
contracts remain.

## Alternatives considered
- Trust `--version` — rejected because it executes the subject before trust.
- Authenticode signer policy — rejected because it approves a wider set than
  this release requires and does not pin the exact tested build.
- Remove configured/PATH support — rejected because exact-byte approval
  preserves portability without accepting arbitrary execution.
- Escape or deny-list batch metacharacters — rejected as parser-fragile.

# ADR-005: Export from an exact isolated release project
Date: 2026-09-04     Status: Accepted

## Context
Selected resources exclude development payloads, but Godot packages
project-wide UID metadata from the development import cache. That cache
discloses Waving, unrelated root-sheet, source-sheet, and development names.

## Decision
Construct a fresh project under a unique guarded Build stage from an exact
manifest of runtime config, scene, seven C# files, existing C# UID sidecars,
and 33 frames. Copy no `.godot` or `.import` state. Import and export from that
stage, publish to the existing `Build\TCFAnimation.exe`, then remove the stage
before success.

## Consequences
Engine metadata is derived only from release-owned inventory. Export takes an
additional import step and temporary disk space, but no development cache or
unrelated source is mutated. A missing manifest dependency fails the release
rather than silently broadening the copy.

## Alternatives considered
- Delete/sanitize the development UID cache — rejected because it mutates
  developer state and remains a negative boundary.
- Binary scrub after export — rejected as unsafe and non-causal.
- Broad copy with excludes — rejected because new files leak by default.

# ADR-006: Bound file-integrity and metadata inspection
Date: 2026-09-04     Status: Accepted

## Context
Source hashing uses whole-file allocation, preset validation omits empty
include/exclude enforcement, and accepted engine metadata is not inspected for
denied inventory strings. Python package versions are pinned but artifacts are
not hash-locked.

## Decision
Use exact byte-size prechecks plus 1 MiB streaming SHA-256 for immutable source
files and approved release tools. Parse and reject nonempty export filters.
Read PCK metadata through bounded offsets/sizes, scan it for denied tokens, and
add in-memory negative controls. Hash-lock the release Python requirements.

## Consequences
Oversized substitutions fail before proportional allocation or native decode,
and package-hygiene claims include metadata contents. Integrity metadata must
be maintained when an approved source/tool/package intentionally changes.

## Alternatives considered
- Maximum size without exact size — rejected because exact immutable sizes are
  known and stronger.
- Continue excluding engine metadata from scans — rejected because that is the
  demonstrated leak location.
- Leave package artifact trust to TLS/version pins — rejected because hashes
  close the finding without a new dependency.

# ADR-007: Generate one fixed minimal solution inside the owned release stage
Date: 2026-09-04     Status: Accepted

## Context
Godot 4.5.1 Mono requires `TCFAnimation.sln` to compile C# during export but
does not create it during headless import. Without it, Godot returns zero,
embeds C# source, and produces an unusable executable. The repository solution
also references excluded `ControllerProbe`, while the exact 50-file copied
seed is a security boundary that must not broaden.

## Decision
Keep the copied seed at exactly 50 files. After proving that seed and before
Godot import, generate one release-owned `TCFAnimation.sln` from fixed UTF-8
without-BOM/CRLF bytes containing only `TCFAnimation.csproj`; require length
994 and SHA-256
`FE3E86D84D18FD948E059483F5CB09B956E4F7B91A0B33FACD337727A2F4EC79`,
then prove the pre-import stage contains exactly the seed plus that solution.
Never copy the repository solution or invoke `dotnet` to generate it.

## Consequences
The positive copy boundary and probe exclusion remain exact, while Mono can
compile the staged project. The generated file is coupled to the approved
Godot/MSBuild toolchain and must be reviewed if that provenance changes.
Export success now also requires no Godot error diagnostics, a nonempty final
executable, a nonempty final `TCFAnimation.dll`, and successful stage cleanup.

## Alternatives considered
- Copy the repository solution — rejected because it references
  `ControllerProbe\ControllerProbe.csproj`.
- Add a release solution as manifest entry 51 — rejected because the source
  copy boundary must remain exactly 50 files.
- Run `dotnet new sln`/`dotnet sln add` — rejected because SDK version and
  generated output would become extra, mutable release inputs.
- Trust zero exit and a nonempty executable — rejected because RW2 reproduced
  a zero-exit, nonempty, source-bearing artifact that crashes.

# ADR-008: Use the authenticated Microsoft PyPI proxy as the sole release index
Date: 2026-09-04     Status: Accepted

Direct PyPI CDN TLS fails in this environment. The existing HTTPS Microsoft proxy may be used explicitly as the only release index because exact wheel hashes remain the final artifact acceptance boundary. Ambient pip configuration is disabled for evidence, downloads are hash-verified, and installation is repeated offline. Proxy failure or changed artifacts fail closed. No credentials are recorded.

# ADR-009: Exported captures are executable-adjacent and fail closed
Date: 2026-09-04     Status: Accepted

Standalone developer captures use `<executable directory>\Captures`; development runs use the filesystem project root. The same resolved root is used throughout publication. No silent user-directory fallback is allowed.

Implementation note: in the Godot 4.5.1 Mono Windows export,
`AppContext.BaseDirectory` resolves to the executable-adjacent managed-data
directory (`data_TCFAnimation_windows_x86_64`). The resolver therefore treats
that value as an authenticated layout input: it requires the exact fixed
managed-directory name and direct-parent relationship to the current regular
process executable, then returns the executable directory as the portable
root. An unexpected layout fails closed rather than walking to an arbitrary
parent.

# ADR-010: Preserve logical 1920x1080 and fit physical clients
Date: 2026-09-04     Status: Accepted

Use a 1280x720 window override plus `canvas_items` aspect `keep`. Gameplay and dialogue remain in logical coordinates; Godot owns the uniform physical transform and letterboxing.

# ADR-011: Remove source reflections with pose-specific exclusions and canonical continuity plates
Date: 2026-09-04     Status: Accepted

Do not alter immutable sources or recolor footwear. Exclude proven source reflection regions before composition, validate silhouette negatives, make both front files identical to RightTurn/turn_0, and share a cleaned canonical lower-body plate across cross-hold-release while aligning final visible upper-body anchors.
