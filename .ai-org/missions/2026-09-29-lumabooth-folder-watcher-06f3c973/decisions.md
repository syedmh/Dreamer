# TCFUploader Architecture Decisions

# ADR-008: Bound reconciliation traversal and retain completed dedupe records
Date: 2026-09-29     Status: Accepted

## Context
Recursive directory descent allowed watched-root depth to consume process call stack. Completed
fingerprints also grow over the deployment lifetime, but deleting them would weaken restart dedupe.

## Decision
Traverse directories with an explicit heap stack, cap descent at 1,024 levels, report skipped
children through bounded diagnostics, and continue sibling traversal. Retain completed fingerprint
records until a bounded or segmented durable deduplication index is designed; document and monitor
the resulting state-growth risk rather than applying unsafe time-based deletion.

## Consequences
Attacker-controlled directory depth cannot exhaust the call stack. Traversal memory and open
enumerators are bounded by the explicit depth limit. Completed state can still grow over long-lived
high-volume operation, but restart dedupe semantics remain intact.

## Alternatives considered
- Recursive traversal with a larger thread stack - rejected because depth remains attacker-driven.
- Time- or count-based deletion of completed records - rejected because old unchanged files could be
  uploaded again after restart.

# ADR-001: Use one executable project with internal component boundaries
Date: 2026-09-29     Status: Accepted

## Context
The target directory is empty. The product has one command-line host, one upstream integration, and
no reuse requirement, but it requires deterministic tests and replaceable platform seams.

## Decision
Create one `net8.0` executable and one MSTest project. Keep production components in explicit
folders behind internal interfaces, use manual constructor composition, and expose internals only
to the test assembly.

## Consequences
Deployment is one executable with no runtime NuGet dependencies, while policy and orchestration
remain independently testable. The executable assembly contains both policy and adapters.

## Alternatives considered
- Separate Core/Infrastructure/CLI projects - rejected because there is no second consumer and the
  extra project boundaries add ceremony without changing deployment or ownership.
- Put all behavior in `Program.cs` - rejected because state, timing, watcher, and HTTP recovery would
  be difficult to test safely.

# ADR-002: Snapshot stable sources into immutable durable spool files
Date: 2026-09-29     Status: Accepted

## Context
The uploader must stream content, detect changed files, send exact byte size, survive restart, and
never modify source files. A source can change between hashing, PUT, and POST.

## Decision
After three stable observations, stream the source into a state-directory spool file while hashing
and counting bytes. Persist that immutable payload as the authoritative content until POST is
durably completed or the fingerprint is permanently rejected.

## Consequences
Fingerprint, PUT bytes, and POST size remain consistent and restart does not depend on the source
still existing. Pending uploads consume local disk equal to payload size.

## Alternatives considered
- Upload the source directly after hashing - rejected because it can change between passes and
  cannot reliably resume after deletion or modification.
- Buffer content in memory - rejected because file sizes are unbounded and streamed upload is
  required.

# ADR-003: Persist an atomic fingerprint state machine
Date: 2026-09-29     Status: Accepted

## Context
Restart after PUT must resume POST, completed content must be skipped, corrupt state must fail
closed, and upstream has no idempotency key.

## Decision
Persist versioned JSON keyed by a SHA-256 version ID derived from normalized relative path, length,
UTC last-write timestamp, and content SHA-256. Use `PendingPut`, `PutComplete`, and `Completed`
states; retain bounded-cycle failure outcomes without turning them into terminal completion.
Replace the file atomically after flushing, validate the entire document and active spool files
before starting intake, and hold an exclusive per-event/per-root process lock.

## Consequences
Known PUT success is not repeated after restart, completed content is durable, and corrupt state
cannot silently cause duplicate or misdirected uploads. A crash after accepted POST but before the
Completed write can duplicate POST, which is consistent with accepted at-least-once delivery.

## Alternatives considered
- SQLite - rejected because single-process JSON replacement is sufficient and avoids a persistence
  subsystem.
- Best-effort recovery from malformed JSON or backup - rejected because requirements demand
  fail-closed behavior.
- Path-keyed completion only - rejected because rename/copy events would defeat completed
  fingerprint skipping.

# ADR-004: Make reconciliation authoritative and serialize uploads
Date: 2026-09-29     Status: Accepted

## Context
`FileSystemWatcher` can coalesce or lose events, startup files must be found, and uploads must use
one bounded worker.

## Decision
Treat watcher events as hints. Run an immediate recursive scan, observe candidates every two
seconds, and reconcile every 60 seconds or immediately after inbox/watcher overflow. Feed a bounded
upload channel with exactly one reader.

## Consequences
Missed events are eventually repaired and network concurrency is one. Delivery can be delayed by
the stability window, queue backlog, or the reconciliation interval.

## Alternatives considered
- Trust watcher events - rejected because overflow and event loss would permanently miss files.
- Parallel upload workers - rejected by the requirement and because they increase duplicate and
  shutdown complexity.
- Polling only - rejected because normal live changes should not wait up to 60 seconds.

# ADR-005: Fix and allowlist every credential-bearing endpoint
Date: 2026-09-29     Status: Accepted

## Context
The bearer token must never be persisted or disclosed, redirects can forward credentials, and the
tool is only for one specified event.

## Decision
Compile the event and three expected hosts into constants, provide no endpoint/event/token CLI
override, disable redirects, validate HTTPS/default-port/host before each credential-bearing
request, and validate returned media URLs against the B2 host before persistence.

## Consequences
The tool cannot be reused for another event without a code change, but malformed responses and
arguments cannot redirect credentials or uploads.

## Alternatives considered
- Configurable endpoints/event - rejected because flexibility expands the credential-exfiltration
  and wrong-event blast radius with no current product need.
- Automatic redirects - rejected because redirect targets are outside the established trust
  boundary.

# ADR-006: Generate and persist a random remote key per file version
Date: 2026-09-29     Status: Accepted

## Context
Each path/version requires a unique URL-safe remote object key. Retries and restart must target the
same key, while source path data must not be exposed upstream.

## Decision
Generate 128 random bits with `RandomNumberGenerator`, encode them as lowercase hexadecimal with a
strictly sanitized extension, and persist the resulting key with `PendingPut` before the first
network attempt.

## Consequences
Keys are opaque, collision-resistant, and stable across retries without revealing local paths.
State persistence must succeed before PUT can start.

## Alternatives considered
- Derive the key from content/version hashes - rejected because the frozen requirement calls for a
  unique random key per version and deterministic keys expose correlation.
- Generate a new key for every PUT attempt - rejected because an ambiguous failure would create
  multiple remote objects for one version.

# ADR-007: Use a checksummed write-through journal for large state stores
Date: 2026-09-29     Status: Accepted

## Context
Atomic replacement of the complete V1 JSON document for every durable transition caused
quadratic write amplification under the honest 4,096-file AC-21 workload and exposed intermittent
Windows replacement failures. The V1 state machine, fail-closed validation, and durable transition
ordering must remain unchanged.

## Decision
Keep `state.v1.json` as the canonical V1 snapshot. Through 128 items, retain atomic full-document
replacement. Above 128 items, or whenever a journal already exists, append each fingerprint
replacement as an ordered newline-delimited record containing a SHA-256 checksum, using
write-through and `Flush(true)` before acknowledging the transition.

On startup, acquire the existing exclusive lock, load the V1 snapshot, replay and checksum-validate
complete records in order, validate the entire reconstructed state and active spool set, atomically
compact to V1 JSON, and then delete the journal. Treat an unterminated final record as an interrupted,
unacknowledged append; reject any invalid complete record. A journal without its base snapshot is
invalid and must be preserved for operator recovery. Clean shutdown performs the same compaction.
Do not downgrade to a pre-journal binary while a journal exists.

## Consequences
Durable transitions become O(record size) rather than O(total state size), making the honest AC-21
workload practical without changing the schema or upload ordering. Recovery performs linear replay
and full validation, and journal storage grows with transitions between compactions. Atomic
compaction remains required at recovery and clean shutdown.

## Alternatives considered
- Continue replacing the full JSON document for every transition - rejected because measured write
  amplification prevented the required workload and increased Windows replacement failures.
- Introduce SQLite or another persistence dependency - rejected as a larger migration than needed
  for a single-process, single-writer tool.
- Journal every store - rejected because snapshot replacement remains simpler for small stores.
- Relax flushing or batch acknowledged transitions - rejected because it would violate durable
  stage ordering and could repeat PUT after an acknowledged checkpoint.
