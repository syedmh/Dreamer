# TCFUploader Implementation Architecture

Date: 2026-09-29  
Status: Accepted design  
Scope: New .NET 8 command-line watcher under `TCFUploader`; design only.

## Verified current state

- **FACT-01:** `TCFUploader` exists and contains no files (verified by direct directory listing on
  2026-09-29). There is no entry point, project, state schema, configuration, or test harness to
  preserve.
- **FACT-02:** Repository-wide ignores already exclude .NET build outputs, NuGet package folders,
  temporary files, logs, and SQLite files (`.gitignore:1-34`).
- **FACT-03:** A sibling .NET 8 system enables nullable reference types, implicit usings,
  deterministic output, warnings-as-errors, and latest language version in shared build properties
  (`HusayniaSMS/Directory.Build.props:1-9`).
- **FACT-04:** That system uses SDK-style `src`/`tests` projects and MSTest with
  `Microsoft.NET.Test.Sdk`, `MSTest.TestFramework`, and `MSTest.TestAdapter`
  (`HusayniaSMS/HusayniaSMS.sln:5-15`;
  `HusayniaSMS/tests/HusayniaSMS.Tests/HusayniaSMS.Tests.csproj:1-17`).
- **FACT-05:** Cached Microsoft test packages include `Microsoft.NET.Test.Sdk 18.9.0`,
  `MSTest.TestFramework 4.4.0`, and `MSTest.TestAdapter 4.4.0`; the machine has
  `Microsoft.NETCore.App.Ref 8.0.31`, so the installed SDK can target `net8.0`.
- **FACT-06:** The worktree has many unrelated modified and untracked paths outside `TCFUploader`;
  implementation must constrain writes to `TCFUploader` and this mission folder.

## Architecture

Create one production executable project and one test project. Keep component boundaries as folders
and internal interfaces inside the executable assembly; add `InternalsVisibleTo` for tests. A third
production library would add deployment and navigation overhead without a second host or reusable
domain.

Dependency direction:

```text
Program / Composition
    -> WatcherCoordinator
       -> Discovery (startup scan, FileSystemWatcher, reconciliation)
       -> StabilityTracker
       -> Snapshotter
       -> UploadQueue (bounded Channel<UploadWork>, one reader)
       -> UploadWorker
          -> StateRepository
          -> LumaBoothClient

Tests -> internal production contracts and adapters
```

Component responsibilities:

| Component | Responsibility |
|---|---|
| `CliOptions` / `TokenSource` | Strict argument parsing, folder validation, token acquisition, exit codes. |
| `WatcherCoordinator` | Own lifecycle, startup ordering, periodic observation/reconciliation, intake stop, and graceful shutdown. |
| `FileChangeFeed` | Thin `FileSystemWatcher` adapter; callbacks only enqueue paths and never do I/O. |
| `Reconciler` | Recursive enumeration at startup and every 60 seconds; skips reparse points and re-registers missed paths. |
| `StabilityTracker` | Per-path `(length,lastWriteUtc)` observations; three equal observations, each at least two seconds after the prior qualifying observation. |
| `FileSnapshotter` | Opens a stable source read-only, streams it to an immutable spool file while computing SHA-256 and exact byte count, then rechecks source metadata. |
| `StateRepository` | Validates, locks, loads, and atomically replaces versioned JSON state. It is the only state writer. |
| `UploadScheduler` | Deduplicates fingerprints and writes eligible work to a bounded channel. |
| `UploadWorker` | The sole channel reader; performs PUT then POST and persists every durable stage transition. |
| `LumaBoothClient` | Constructs allowlisted HTTPS requests, streams bodies, parses bounded JSON, and classifies responses. |
| `RetryPolicy` | Five-attempt transient retry burst with injected clock/delay and cancellation. |

Use manual constructor composition in `Program`; do not add a dependency-injection or logging
package. Use only .NET platform APIs at runtime: `FileSystemWatcher`, `Channel<T>`, `HttpClient`,
`System.Text.Json`, `SHA256`, and ordinary file APIs.

### Processing flow

1. Parse `--folder`, acquire the single-instance lock, load and fully validate state, then acquire
   the token. No watcher or network call starts before state validation succeeds.
2. Requeue durable `PendingPut` and `PutComplete` records whose spool files validate.
3. Start the single upload worker, `FileSystemWatcher`, two-second observation loop, and 60-second
   reconciliation loop; run an immediate recursive startup scan.
4. A stable source is copied once to `%LOCALAPPDATA%\TCFUploader\... \spool` using a temporary file.
   Hash and size are computed during the copy. The final source stat must still match the stable
   observation; otherwise discard the spool and restart observations.
5. Finalize the spool with an atomic rename and compute the version ID from normalized relative
   path, observed length, observed UTC last-write timestamp, and content SHA-256. If that exact
   version is already `Completed` or active, discard the duplicate spool. Otherwise generate and
   persist its random remote key and `PendingPut` state before queueing.
6. PUT streams the spool `FileStream` through `StreamContent`. On validated success, persist
   `PutComplete` with the returned media URL before attempting POST.
7. POST sends multipart fields `uploadFileField`, `imgWidth=0`, `imgHeight=0`, and
   `imgSize=<exact spool bytes>`. Persist `Completed` only after HTTP 2xx and JSON
   `success=true`, then delete the spool.
8. A crash after PUT resumes POST from `PutComplete`. A crash after accepted POST but before the
   `Completed` write can duplicate POST; this is the unavoidable documented at-least-once window.

The spool is authoritative upload content. Source changes after snapshot create a new fingerprint
and upload; source files are never renamed, moved, truncated, or deleted.

## Contracts

### CLI

```text
TCFUploader.exe --folder <existing-directory>
TCFUploader.exe --help
TCFUploader.exe --version
```

- The event is intentionally fixed to `-P1Y143qUTagjT1hDguA`; there is no event or endpoint override.
- `--folder` is required exactly once. Unknown, positional, duplicate, or token arguments fail.
- Resolve the folder with `Path.GetFullPath`; require an existing directory and reject overlap with
  the state directory. Comparisons use Windows `OrdinalIgnoreCase`.
- Token precedence: non-empty `LUMABOOTH_FOTOSHARE_TOKEN`, otherwise redirected stdin. Interactive
  prompting is not supported. Read at most 16 KiB from stdin, trim outer CR/LF, and reject empty
  values, embedded CR/LF/control characters, or additional non-whitespace lines.
- Exit codes: `0` clean shutdown; `2` CLI/token/configuration error; `3` invalid/corrupt/incompatible
  state or active-instance lock; `4` upstream authentication/authorization failure; `5` forced
  shutdown after grace expiry; `1` other fatal runtime failure.
- Logs go to console only, with UTC timestamp, level, operation, relative path where useful, and a
  12-character fingerprint prefix. Never log request headers, token, stdin, full response bodies,
  or multipart bodies.

### Discovery and scheduling

```csharp
internal readonly record struct FileObservation(long Length, DateTime LastWriteUtc);
internal readonly record struct UploadWork(string Fingerprint);

internal interface IChangeFeed
{
    event Action<FileChange> Changed;
    void Start();
    void Stop();
}

internal interface IFileSnapshotter
{
    Task<SnapshotResult> TrySnapshotAsync(
        string path, FileObservation expected, CancellationToken cancellationToken);
}
```

- `FileSystemWatcher`: `IncludeSubdirectories=true`; notify file name, size, last write, and creation
  time; use a 64 KiB internal buffer. Created/Changed/Renamed register the current path; Deleted
  removes its candidate. `InternalBufferOverflowException` sets an immediate-reconciliation flag.
- The callback inbox is a bounded channel of 1,024 paths using nonblocking `TryWrite`; overflow also
  sets the reconciliation flag. The upload channel capacity is 64, `SingleReader=true`,
  `SingleWriter=false`, and waits when full. Only one upload worker reads it.
- Recursive enumeration skips reparse points to prevent loops and watched-root escape. Root
  missing/inaccessible is fatal. An inaccessible child is logged and skipped for that scan.
- Candidate keys are canonical full paths with `OrdinalIgnoreCase`. The two-second observer checks
  all candidates. Any signature change resets the count to one. A candidate qualifies only after
  three matching observations whose qualifying timestamps are each at least two seconds apart.

### HTTP

Fixed endpoints:

```text
PUT  https://w.fotoshare.co/upload_file/{remote-key}
POST https://fotoshare.co/api/event/-P1Y143qUTagjT1hDguA/upload
BASE https://fotoshare.s3.us-east-005.backblazeb2.com/
```

- Remote key is unique, random, and path-free:
  `tcfuploader-<32 lowercase hex characters><sanitized lowercase extension>`. Generate 16 bytes
  with `RandomNumberGenerator.Fill`, persist the key before the first PUT, and reuse it for every
  retry/resume of that version. Percent-encode it as one URI path segment. Sanitized extension is
  `.` plus at most 16 ASCII alphanumeric characters; otherwise omit it.
- Content type is selected from a small fixed extension map for common image/video types; unknown
  extensions use `application/octet-stream`. Persist the selected value with the snapshot.
- Set bearer authorization on each PUT/POST request, not `DefaultRequestHeaders`.
- Disable automatic redirects. Require HTTPS, default port, no userinfo, and exact host match before
  sending credentials. Any 3xx is rejected.
- PUT response must be 2xx JSON with one non-empty string `url`. Resolve relative values against the
  fixed BASE, then require the resolved URI to be HTTPS, default port, no userinfo/fragment, and host
  exactly `fotoshare.s3.us-east-005.backblazeb2.com`. Never fetch that URL.
- POST response must be 2xx JSON with Boolean `success=true`; all other shapes are protocol errors.
- Parse at most 64 KiB per JSON response using `ResponseHeadersRead`; do not include bodies in errors.
- `SocketsHttpHandler.ConnectTimeout=30s`; the default per-attempt PUT and POST timeout is ten
  minutes, implemented with linked cancellation so Ctrl+C interrupts promptly. Keep timeout,
  observation interval, reconciliation interval, retry count, and shutdown grace in one immutable
  options record injectable by tests; expose only `--folder` in the initial CLI.

## State Model

State root:

```text
%LOCALAPPDATA%\TCFUploader\
  events\<event-id-sha256-prefix>\<watched-root-sha256-prefix>\
    instance.lock
    state.v1.json
    state.v1.journal
    spool\
      <fingerprint>.payload
```

`instance.lock` is held with `FileShare.None` for process lifetime. The state path includes hashes,
not raw event/folder names, but the JSON retains canonical values for collision and scope validation.

```json
{
  "schemaVersion": 1,
  "eventId": "-P1Y143qUTagjT1hDguA",
  "watchedRoot": "C:\\absolute\\canonical\\folder",
  "updatedUtc": "2026-09-29T03:59:24Z",
  "items": {
    "<lowercase-version-id-sha256>": {
      "status": "pendingPut | putComplete | completed",
      "relativePath": "subfolder\\photo.jpg",
      "observedLength": 12345,
      "observedLastWriteUtc": "...",
      "contentSha256": "<lowercase-sha256>",
      "byteLength": 12345,
      "contentType": "image/jpeg",
      "extension": ".jpg",
      "spoolFile": "spool\\<version-id>.payload",
      "remoteKey": "tcfuploader-<random>...",
      "remoteUrl": "https://...",
      "createdUtc": "...",
      "updatedUtc": "...",
      "completedUtc": "...",
      "nextEligibleUtc": "...",
      "lastCycleOutcome": "http_413",
      "lastCycleAttempts": 1
    }
  }
}
```

Invariants:

- Dictionary key is a lowercase 64-character version ID: SHA-256 over an unambiguous encoding of
  case-folded normalized relative path, observed byte length, observed UTC last-write ticks, and
  content SHA-256. Preserve the original normalized relative path separately for logs.
- The content hash is always lowercase SHA-256. The version ID, not content hash alone, is the
  completion identity: the same bytes at another path are a separate version.
- `PendingPut` requires a validated spool file and no `remoteUrl`.
- `PutComplete` requires a validated spool file and allowlisted `remoteUrl`.
- `Completed` has no required spool file and is never queued.
- `remoteUrl` is persisted only after validation. `lastCycleOutcome` is a bounded enum-like code,
  never raw response text or an exception containing headers.

Persistence protocol: `state.v1.json` remains the canonical V1 state document. Stores containing
at most 128 items persist each transition by serializing to a unique same-directory temp file with
write-through enabled, flushing it to disk, and atomically replacing `state.v1.json`. Once a store
exceeds 128 items, or while a journal already exists, each transition is appended as an ordered
newline-delimited replacement record to `state.v1.journal`. Each record includes a SHA-256 checksum
and is flushed with write-through before the transition is acknowledged.

Startup acquires the exclusive instance lock, loads the V1 base snapshot, replays complete journal
records in order, verifies every checksum, and validates the entire reconstructed state and active
spool set before intake or network access. An unterminated final record is treated as an interrupted
unacknowledged append; malformed or checksum-invalid complete records fail closed. Recovery and
clean shutdown atomically compact the reconstructed state to `state.v1.json` before deleting the
journal. Replaying a retained journal after successful replacement is idempotent.

Missing state initializes V1 only when no journal exists. A journal without its base snapshot,
empty or invalid JSON, unknown version, scope mismatch, invalid URI/status/invariant, missing active
spool, size mismatch, or spool hash mismatch causes exit code 3 before intake/network. Do not
silently repair or fall back to a backup. After a valid load, delete only unreferenced spool/temp
files under the resolved state scope. Pre-journal binaries must not open a store while
`state.v1.journal` exists; rollback requires successful compaction by a journal-aware binary or
restoration of a consistent state-directory backup.

Transition ordering:

```text
spool finalized -> persist PendingPut -> enqueue
PUT accepted -> validate URL -> persist PutComplete -> POST
POST accepted -> persist Completed -> delete spool
```

## Failure Handling

Transient: `HttpRequestException`, connect/read timeout not caused by shutdown, HTTP 408, 429, and
500-599. Each PUT or POST processing cycle makes at most five attempts. Honor valid `Retry-After`
up to 60 seconds; otherwise delay 1s, 2s, 4s, 8s plus injected 0-250ms jitter. After attempt five,
persist the unchanged durable stage, outcome, attempt count, and `nextEligibleUtc=now+60s`;
reconciliation or a later startup may begin a new bounded cycle.

Non-transient/file-scoped: other 4xx, redirect, oversized/malformed response, invalid URL, invalid
JSON, PUT missing URL, or POST `success!=true`. Make one attempt in that cycle, preserve
`PendingPut` or `PutComplete`, persist the outcome and `nextEligibleUtc=now+60s`, and continue
watching other files. The same stable version is eligible in a later reconciliation/startup cycle;
it is never marked complete or discarded merely because one cycle failed.

Fatal/process-scoped:

- 401/403: stop intake, preserve the current stage, cancel queued network starts, and exit 4.
- invalid/corrupt state, missing active spool, state lock conflict: no watcher/network; exit 3.
- any runtime state persistence failure: stop intake immediately and exit 1; do not start another
  network step from state that was not durably committed.
- watched root disappears or becomes inaccessible: stop intake and exit 1 after graceful drain.
- unexpected coordinator/worker fault: stop intake, preserve state, exit 1.

Filesystem races (deleted, locked, or changed source) are not failures: discard any incomplete
snapshot, reset observations when the path still exists, and rely on reconciliation.

Ctrl+C: first signal stops watcher/reconciliation and prevents the worker from starting another
queued item. Allow only the already-active snapshot/state operation or HTTP request up to 30
seconds; queued items remain durable for restart. At deadline cancel active file/network I/O and
exit 5; durable stage ordering makes restart safe. A second Ctrl+C cancels immediately. No new
snapshot or network request starts after intake stop.

## File Plan

```text
TCFUploader\
  TCFUploader.slnx
  Directory.Build.props
  Directory.Packages.props
  README.md
  src\TCFUploader\
    TCFUploader.csproj
    Program.cs
    Properties\AssemblyInfo.cs
    Cli\CliOptions.cs
    Cli\TokenSource.cs
    Configuration\UploaderConstants.cs
    Hosting\ExitCodes.cs
    Hosting\WatcherCoordinator.cs
    Discovery\FileChange.cs
    Discovery\FileChangeFeed.cs
    Discovery\Reconciler.cs
    Discovery\StabilityTracker.cs
    Files\FileObservation.cs
    Files\FileSnapshotter.cs
    Files\ContentTypeMap.cs
    State\UploaderState.cs
    State\UploadItemState.cs
    State\UploadStatus.cs
    State\StateRepository.cs
    Upload\UploadWork.cs
    Upload\UploadScheduler.cs
    Upload\UploadWorker.cs
    Upload\LumaBoothClient.cs
    Upload\RetryPolicy.cs
    Upload\UploadFailure.cs
    Time\IClock.cs
    Time\SystemClock.cs
  tests\TCFUploader.Tests\
    TCFUploader.Tests.csproj
    Cli\CliOptionsTests.cs
    Cli\TokenSourceTests.cs
    Discovery\StabilityTrackerTests.cs
    Discovery\ReconcilerTests.cs
    Files\FileSnapshotterTests.cs
    State\StateRepositoryTests.cs
    Upload\LumaBoothClientTests.cs
    Upload\RetryPolicyTests.cs
    Upload\UploadWorkerTests.cs
    Hosting\WatcherCoordinatorTests.cs
    Integration\RestartRecoveryTests.cs
    TestDoubles\FakeClock.cs
    TestDoubles\ScriptedHttpMessageHandler.cs
```

`Directory.Build.props` mirrors the repository's modern .NET defaults and pins `TargetFramework`
per project. `Directory.Packages.props` contains only test dependencies:
`Microsoft.NET.Test.Sdk 18.9.0`, `MSTest.TestFramework 4.4.0`, and
`MSTest.TestAdapter 4.4.0`. The production project has no `PackageReference`.

## Test Plan

- **CLI/security:** strict argument matrix; env precedence; redirected stdin bounds; token never
  appears in captured stdout/stderr, state, exception messages, or HTTP diagnostics.
- **Stability:** exactly three observations; sub-two-second observations do not count; changes
  reset; delete/rename; case-insensitive dedupe; channel overflow triggers reconciliation.
- **Snapshot:** streamed copy/hash/size; source mutation resets; locked/deleted files retry; spool
  is outside watched root; completed/active fingerprint dedupe; source is never modified.
- **State:** every invariant and corrupt form fails closed; atomic replacement leaves prior valid
  file on pre-move fault; lock exclusion; scope mismatch; path traversal rejection; orphan cleanup;
  active spool size/hash validation.
- **HTTP contract:** exact URI, method, headers, multipart names/values, streamed request content,
  exact `imgSize`, relative URL resolution, allowlist rejection, redirect rejection, bounded JSON,
  malformed JSON, success false, and no bearer header to any non-allowlisted host.
- **Retries:** each transient category, permanent categories, five-attempt bound, Retry-After cap,
  backoff, cancellation, and ambiguous POST retry behavior.
- **Recovery:** persisted `PendingPut` resumes PUT; persisted `PutComplete` performs POST without
  PUT; `Completed` skips; changed bytes upload; crash-window simulation allows duplicate POST but
  never loses the durable stage.
- **Concurrency/shutdown:** prove one HTTP operation active at a time, bounded channels, no starts
  after Ctrl+C, graceful drain under 30 seconds, forced cancellation at deadline, second Ctrl+C.
- **Adapter smoke tests:** real temp-directory startup scan and reconciliation. Keep
  `FileSystemWatcher` timing tests minimal and tolerant; correctness tests use a fake change feed.
- **No-live-network gate:** all tests inject `HttpMessageHandler`; add a handler that fails any
  unplanned request. Tests never use a real token or endpoint.
- **Authorized live smoke:** outside the automated suite, run one uniquely named file with a
  CTO-supplied token against the fixed event, verify event appearance and durable completion, then
  scan captured logs for the token and `Authorization: Bearer`. Do not automate browser checks or
  credential acquisition.

Name tests with their acceptance criterion prefix (for example
`AC15_PutCompleteRestart_ResumesPostWithoutPut`) and maintain a README table mapping AC-01 through
AC-25 to test methods or the authorized live-smoke evidence.

Validation commands:

```text
dotnet restore TCFUploader.slnx
dotnet build TCFUploader.slnx --configuration Release --no-restore
dotnet test TCFUploader.slnx --configuration Release --no-build
```

## Risks / Tradeoffs

- **Local spool uses disk approximately equal to pending file bytes.** Accepted to guarantee that
  hash, PUT bytes, and POST size refer to one immutable payload and to enable crash recovery.
- **Single worker limits throughput.** Required; it bounds memory, network concurrency, and
  duplicate blast radius.
- **Repeated transient cycles can exceed five lifetime attempts.** Intentional: five bounds each
  burst, while reconciliation preserves eventual delivery during long outages.
- **POST timeout can be ambiguous and retry can duplicate the event upload.** Upstream has no
  idempotency key; at-least-once is accepted. The durable PUT checkpoint avoids repeating PUT after
  it is known successful.
- **Non-transient failures recur in later bounded cycles.** This can repeat operator-visible errors,
  but a 60-second eligibility delay prevents a tight loop and satisfies eventual retry requirements
  without exceeding five attempts in one processing cycle.
- **Completed fingerprints grow state indefinitely.** Required for durable skipping. For this
  contained tool, retain them permanently; do not add pruning that could reupload old content.
- **Fixed event/endpoints reduce reuse.** Intentional security boundary: no argument can redirect a
  bearer token or upload to the wrong event.

## Implementation Sequence

1. Scaffold solution, build properties, package versions, executable/test projects, strict CLI,
   constants, exit codes, and internal test visibility.
2. Implement state DTO validation, scope hashing, exclusive lock, atomic persistence, spool
   validation, and corruption tests before network code.
3. Implement observation, recursive reconciliation, watcher adapter, immutable snapshot/hash, and
   fingerprint deduplication with deterministic tests.
4. Implement allowlisted `LumaBoothClient`, bounded JSON parsing, exact multipart contract, and
   scripted-handler tests.
5. Implement retry classification/backoff and the single-reader upload worker with durable
   transition ordering and restart-recovery tests.
6. Compose coordinator lifecycle, bounded channels, startup ordering, 60-second reconciliation,
   Ctrl+C grace/second-signal behavior, and exit-code propagation.
7. Add README usage, token piping examples that do not expose tokens in process arguments, state
   recovery guidance, at-least-once warning, and source-retention guarantee.
8. Run restore/build/test in Release and inspect the diff to confirm only `TCFUploader` and the
   current mission artifacts changed.
