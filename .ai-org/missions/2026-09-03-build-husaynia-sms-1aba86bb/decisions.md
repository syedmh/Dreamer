# Decisions

## D-001 — Proceed with CTO-approved defaults
- **Date:** 2026-09-03
- **Decision:** Build Windows Forms on `net8.0-windows`, use official Twilio SDK, established CSV parser, Windows DPAPI, and no real SMS validation.
- **Reason:** CTO is unavailable and explicitly approved these defaults.

## D-002 — Preserve legacy and unrelated work
- **Date:** 2026-09-03
- **Decision:** Do not migrate or modify `.ai-org/active-mission.json`; do not touch sibling projects or unrelated working-tree changes.
- **Reason:** Session isolation and explicit mission constraints.

# ADR-003: Use a three-project inward dependency structure
Date: 2026-09-03     Status: Accepted

## Context
The target is empty, the application has one Windows executable, and requirements demand both
provider isolation and strong no-live-send testing.

## Decision
Create `HusayniaSMS.Core`, `HusayniaSMS.WinForms`, and `HusayniaSMS.Tests`. Core owns pure policies
and provider-neutral contracts; WinForms owns the composition root, passive UI, and CSV/DPAPI/Twilio
adapters; Tests depends on both. Use manual constructor injection rather than a DI framework.

## Consequences
Business and batching behavior can be tested without WinForms or Twilio. The adapter assembly is not
separately reusable, which is acceptable because no second host is required.

## Alternatives considered
- Logic directly in forms - rejected because async, confirmation, close, and failure behavior would
  be difficult to test deterministically.
- A fourth Infrastructure project - rejected because it adds a boundary and project with no
  independent deployment or reuse need.

# ADR-004: Send sequentially with one active batch and no automatic retries
Date: 2026-09-03     Status: Accepted

## Context
Cancellation must stop additional recipients, repeated submits must not duplicate batches, partial
failures must be retained, and a network timeout may be ambiguous about provider acceptance.

## Decision
Use an immutable confirmed recipient snapshot, a non-queuing single-active-batch gate, and one
asynchronous Twilio request at a time in snapshot order. Check cancellation before each start, allow
the one in-flight request to settle, apply a 30-second transport timeout, and never retry
automatically.

## Consequences
Cancellation and totals are deterministic and duplicate-send risk is minimized. Large batches take
longer than parallel fan-out, and transient failures require a new explicit confirmation.

## Alternatives considered
- Parallel sends - rejected because it expands in-flight blast radius and weakens cancellation.
- Automatic transient retries - rejected because an ambiguous timeout can turn a retry into a
  duplicate SMS.

# ADR-005: Store versioned local JSON with DPAPI CurrentUser protection
Date: 2026-09-03     Status: Accepted

## Context
The app needs restart persistence for Twilio setup, the auth token must not be plaintext, settings
must be per-user, and no backend/database is allowed.

## Decision
Store `settings.v1.json` under `%LOCALAPPDATA%\HusayniaSMS`, containing non-secret Account SID and
sender plus base64 DPAPI ciphertext for the token. Use `DataProtectionScope.CurrentUser`, atomic
temp-file replacement, descriptor-only UI loading, and safe recovery when JSON or ciphertext cannot
be read.

## Consequences
The settings file is outside the repository and cannot be decrypted by another Windows user under
normal DPAPI guarantees. Processes running as the same user remain inside the trust boundary, and a
Windows profile reset requires token re-entry.

## Alternatives considered
- Plain application/user settings - rejected because they do not protect the auth token.
- Windows Credential Manager - rejected because the accepted requirement specifically selects
  DPAPI and Credential Manager adds a second storage API.
- Backend secret storage - rejected as out of scope and operationally disproportionate.

# ADR-006: Pin official provider/parser packages centrally
Date: 2026-09-03     Status: Accepted

## Context
The requirements mandate the official Twilio SDK and an established CSV parser with exact versions.
The target has no inherited package management.

## Decision
Use central package management with `Twilio 8.0.0`, `CsvHelper 33.1.0`,
`System.Security.Cryptography.ProtectedData 10.0.11`, `Microsoft.NET.Test.Sdk 18.9.0`,
`MSTest.TestFramework 4.4.0`, and `MSTest.TestAdapter 4.4.0`.

## Consequences
Versions are auditable and reproducible. Upgrades are explicit maintenance changes and must rerun
CSV, transport classification, DPAPI, and no-live-send gates.

## Alternatives considered
- Floating/ranged versions - rejected because they violate AC-25 and make builds non-reproducible.
- Handwritten CSV parsing or direct HTTP calls - rejected because quoted CSV and Twilio protocol
  behavior are already solved by required official/established libraries.

# ADR-007: Make CSV import transactional and duplicates ineligible
Date: 2026-09-03     Status: Accepted

## Context
File-level failures must not partially replace the grid, row-level invalid data must remain visible,
and duplicate numbers must not create duplicate sends.

## Decision
Parse the full file into a candidate list, validate each row, mark later occurrences of a valid
trimmed number as duplicates, and replace UI state only on complete success. Cap one import at
100,000 logical records to bound desktop memory use.

## Consequences
Users keep the last good grid after malformed/unreadable imports and can correct visible row
problems. Import requires memory proportional to the file and rejects files above the documented
bound.

## Alternatives considered
- Stream rows directly into the live grid - rejected because failure would leave partial state.
- Silently deduplicate or send every duplicate - rejected because it either hides source errors or
  risks accidental repeated SMS.

# ADR-008: Provide a fail-closed safe-demo composition
Date: 2026-09-03     Status: Accepted

## Context
Automated and realistic QA must exercise success, delay, partial failure, auth failure, cancellation,
and close behavior without credentials, network, or a real SMS.

## Decision
Add explicit `--safe-demo` composition with deterministic scripted scenarios, a persistent no-send
banner, separate LocalApplicationData settings, and no live fallback. Unknown command-line
arguments or scenarios fail closed. Production remains the no-argument official-Twilio composition.

## Consequences
QA exercises the real UI/orchestrator safely, and the fake/live boundary is auditable. The
executable has one additional documented mode and scenario parser.

## Alternatives considered
- QA only through unit tests - rejected because it does not validate the real WinForms journey.
- A separate demo executable - rejected because it duplicates host/UI wiring.
- Twilio test credentials - rejected because requirements prohibit reliance on live Twilio calls
  and network access.

# ADR-009: Add sender mode to the existing V1 settings contract
Date: 2026-09-04     Status: Proposed

## Context
The shipped settings contract contains one `senderNumber` value and no sender mode. Existing saved
From-number settings and DPAPI ciphertext must remain usable, while the rejected mission must add
Messaging Service SID support without a destructive migration.

## Decision
Keep schema version 1, `settings.v1.json`, and the legacy `senderNumber` JSON property. Add an
optional string-serialized `senderMode`; treat only an absent/null mode as legacy
`FromPhoneNumber`, and write an explicit mode on the next setup save. The legacy `senderNumber`
property stores the one active sender value for either mode.

## Consequences
Existing settings load without rewrite, token re-encryption, or path changes. New From settings are
readable by the old binary; new Messaging Service settings fail the old binary's E.164 preflight
safely. The persisted DTO retains a historically inaccurate property name, which must be isolated
from domain naming and documented.

## Alternatives considered
- Schema version 2/new file - rejected because the additive field is sufficient and a version bump
  creates unnecessary migration and rollback work.
- Add separate persisted From and Messaging Service values - rejected because stale dual values
  weaken the exactly-one invariant.
- Infer mode from the value - rejected because the UI requirement is an explicit mode choice and
  cross-mode mistakes should produce validation errors.

# ADR-010: Carry one typed sender choice to a pure Twilio options factory
Date: 2026-09-04     Status: Proposed

## Context
The current Core request carries only `From`, and the production adapter directly initializes
`CreateMessageOptions.From`. The remediation must prove, without network access, that exactly one
of `From` or `MessagingServiceSid` reaches the Twilio SDK.

## Decision
Introduce `TwilioSenderMode` plus one `SenderValue` across setup, descriptor, credentials, and SMS
request contracts. Add an internal pure factory that creates `CreateMessageOptions`, sets `Body`,
and switches on the mode to set exactly one sender property. Test that factory directly; keep the
existing transport, factory, safe-demo composition, and batch lifecycle.

## Consequences
Invalid both/neither states are removed from normal domain construction, and provider mapping is
deterministically testable without credentials or network. Internal constructors and tests require
an atomic source update, but no external service/API compatibility boundary is changed.

## Alternatives considered
- Two nullable sender properties in Core - rejected because every caller would need to re-enforce
  the exactly-one invariant.
- Mock or wrap the entire Twilio client - rejected as a larger abstraction introduced only for one
  deterministic mapping test.
- Put mode switching in WinForms or the controller - rejected because provider option mapping
  belongs at the Twilio adapter boundary.

# ADR-011: Replace the read-only CSV importer boundary with one Core CSV store contract
Date: 2026-09-04     Status: Accepted

## Context
Contact editing requires both transactional loading and explicit persistence, while conflict and
failure outcomes must remain testable without binding Core or the controller to `System.IO`.
`IContactCsvImporter` exposes only rows/status and cannot carry a loaded-file version.

## Decision
Define one provider-neutral `IContactCsvStore` in Core with typed asynchronous load/save results and
an opaque-to-callers `ContactCsvVersion` containing length, UTC last-write time, and SHA-256.
Replace `IContactCsvImporter` after all in-solution callers migrate. Keep CsvHelper parsing/writing,
hashing, paths, temporary files, and replacement in one WinForms infrastructure adapter.

## Consequences
The controller can test load, save, conflicts, and failures through one boundary, and CSV policy
does not depend on WinForms or filesystem APIs. The internal interface change must be applied to all
callers/test doubles atomically, but no published API or file schema changes.

## Alternatives considered
- Add a separate `IContactCsvWriter` - rejected because load/save versioning and status semantics
  would be split across abstractions that always change together.
- Put the writer in `MainController` - rejected because filesystem atomicity and CsvHelper are
  infrastructure concerns.
- Put `System.IO` implementation in Core - rejected because it reverses the existing inward
  dependency direction.

# ADR-012: Use optimistic file versions and fail-safe sibling-temp replacement
Date: 2026-09-04     Status: Accepted

## Context
Explicit Save CSV must not truncate the original on failure or silently overwrite another editor's
changes. Files may be local, redirected, removable, read-only, externally modified, or externally
deleted.

## Decision
Capture a load-time version from length, UTC last-write time, and SHA-256. Require the expected
version on overwrite, compare again immediately before commit, and return typed modified/deleted/
appeared conflicts. Write strict UTF-8 without BOM to a unique sibling temp file, flush to disk,
then use a same-volume atomic replace/rename. If a safe replacement primitive is unavailable, fail
and preserve the original; never use delete-then-move or direct truncating writes.

## Consequences
Ordinary external changes are detected and all user resolutions are explicit. Saves hash up to the
existing 10 MiB file limit and cannot eliminate the final cross-process TOCTOU window, but they
substantially reduce silent overwrite risk without a lock service or database.

## Alternatives considered
- Compare timestamp only - rejected because timestamp granularity and same-length edits can miss
  changes.
- Hold a long exclusive file lock - rejected because it is hostile to editors/network shares and
  still does not provide portable replace semantics.
- Overwrite directly or delete then rename - rejected because a crash/failure can expose a partial
  or missing original.
- Add a database/version service - rejected as disproportionate and explicitly out of scope.

# ADR-013: Keep edits in a session-local document with monotonic ordinals and explicit dirty guards
Date: 2026-09-04     Status: Accepted

## Context
The current controller and send results correlate rows by `ImportOrdinal`. Add/edit/delete must
preserve identity long enough for selection and result behavior while avoiding a broad batch
contract rewrite. The CTO requires explicit Save and Save/Discard/Cancel handling.

## Decision
Retain `ImportOrdinal` as the session-local row identity. Imports allocate 1..N; Add allocates a
monotonically increasing ordinal; Edit preserves it; Delete never renumbers or reuses it. Keep an
in-memory document with path, file version, dirty state, and next ordinal. Checked SMS recipients
remain distinct from highlighted edit/delete rows. Guard Import, Refresh, and exit with
Save/Discard/Cancel, and preserve the old document transactionally until a replacement load
succeeds.

## Consequences
Existing batch progress/snapshot contracts remain intact and mutations have deterministic ordering.
Ordinals are not stable across reload and retain a historical name, but neither leaks into CSV.
Unsaved edits intentionally disappear only after an explicit Discard/exit decision.

## Alternatives considered
- Introduce GUID row IDs throughout batching/UI - rejected because identity need is process-local
  and the migration would touch more contracts without user benefit.
- Renumber after delete - rejected because it can misapply selection or send progress to another
  row.
- Autosave after every edit - rejected by the CTO's explicit-save decision and because it increases
  data-loss/conflict risk.
- Direct grid editing - rejected by the CTO's dialog decision and because field/duplicate errors
  are harder to present consistently.

# ADR-014: Separate designer construction from runtime form configuration
Date: 2026-09-04     Status: Proposed

## Context
`ContactDialog` is fully code-built and requires runtime validation dependencies in its only
constructor. `MainForm` has designer files but no parameterless constructor, contains
designer-hostile local controls/helper calls, and instantiates a custom grid type nested in the
form. ErrorProvider icons are placed to the right of text boxes that already consume the dialog's
right edge.

## Decision
Use standard `.cs` + `.Designer.cs` + `.resx` partial forms. Add public, IntelliSense-hidden,
parameterless constructors that only call `InitializeComponent`; keep and chain the existing
runtime constructors, guard callbacks until runtime configuration/controller attachment, and never
create fake dependencies for the designer. Reserve a dedicated scaled ErrorProvider gutter, move
the grid subclass to a top-level internal type, convert designer locals to named fields, and add
explicit SDK `SubType`/`DependentUpon` `Update` metadata for deterministic Visual Studio nesting.

## Consequences
Both forms can be opened and cosmetically edited in the Visual Studio WinForms Designer while
production composition and behavior remain unchanged. The public constructor surface grows and the
designer files become mechanically larger, but unconfigured instances are inert and hidden from
ordinary IntelliSense. Future layout changes must be made through the designer and validated at
system font, 125% scaling, and large fonts.

## Alternatives considered
- Fake request, validator, or controller objects in design constructors - rejected because they
  weaken production boundaries and can execute runtime behavior in the designer.
- Keep runtime-built layout and add only constructors - rejected because ContactDialog still would
  not be editable and MainForm would not round-trip reliably.
- Custom designer SDK/package - rejected because standard WinForms partials solve the requirement
  with no new dependency.
- Left-side error icons or negative padding - rejected because they avoid rather than fix the
  bounds invariant and reduce field/error clarity.
- Design-time replacement with a plain `DataGridView` - rejected because design and runtime would
  have different control trees.
