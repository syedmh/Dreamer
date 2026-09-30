# LumaBooth Folder Watcher Implementation Plan

Date: 2026-09-29  
Mission: `2026-09-29-lumabooth-folder-watcher-06f3c973`  
Status: READY FOR IMPLEMENTATION  
Scope: `C:\Users\syedhu\source\repos\Dreamer\TCFUploader` only, plus evidence updates in this mission folder.

## Execution Rules

- One implementation developer executes T1-T10 in dependency order. Tasks do not concurrently edit
  the same file.
- The developer writes the tests assigned to each task with the production code. Independent agents
  run T11-T14; they do not certify unexecuted commands.
- No task may edit files outside `TCFUploader` or this mission folder. Existing unrelated worktree
  changes are read-only.
- No commit, push, history rewrite, production deployment, or live request is authorized.
- Runtime code uses only .NET 8 platform libraries. NuGet references are permitted only in the test
  project and are limited to the versions frozen in T1.
- A task stops immediately on a contract conflict, required edit outside its file ownership, secret
  exposure, unexpected live network access, state invariant ambiguity, or a failing dependency.

## Task Table

| ID | Owner | Depends on | Exclusive file ownership | Acceptance evidence |
|---|---|---|---|---|
| T1 | developer | - | `TCFUploader.slnx`; `Directory.Build.props`; `Directory.Packages.props`; `src\TCFUploader\TCFUploader.csproj`; `src\TCFUploader\Properties\AssemblyInfo.cs`; `src\TCFUploader\Configuration\UploaderConstants.cs`; `src\TCFUploader\Configuration\RuntimeOptions.cs`; `src\TCFUploader\Hosting\ExitCodes.cs`; `src\TCFUploader\Time\IClock.cs`; `src\TCFUploader\Time\SystemClock.cs`; `tests\TCFUploader.Tests\TCFUploader.Tests.csproj` | Restore succeeds; Release build of the empty scaffold succeeds; production project has no `PackageReference`; test project contains only `Microsoft.NET.Test.Sdk 18.9.0`, `MSTest.TestFramework 4.4.0`, and `MSTest.TestAdapter 4.4.0`; internals are visible only to `TCFUploader.Tests`. |
| T2 | developer | T1 | `src\TCFUploader\Cli\CliOptions.cs`; `src\TCFUploader\Cli\TokenSource.cs`; `tests\TCFUploader.Tests\Cli\CliOptionsTests.cs`; `tests\TCFUploader.Tests\Cli\TokenSourceTests.cs` | Named tests prove the strict argument matrix, canonical folder validation, help/version behavior, environment-token precedence, bounded redirected-stdin parsing, rejection of token arguments/control characters, and secret-free diagnostics. Covers CLI/token portions of AC-01, AC-02, AC-18, and AC-22. |
| T3 | developer | T1 | `src\TCFUploader\State\UploaderState.cs`; `src\TCFUploader\State\UploadItemState.cs`; `src\TCFUploader\State\UploadStatus.cs`; `src\TCFUploader\State\StateRepository.cs`; `tests\TCFUploader.Tests\State\StateRepositoryTests.cs` | State tests prove exclusive locking, scope derivation, V1 load/create, every status invariant, active-spool size/hash validation, fail-closed corrupt/unknown/mismatched state, atomic replacement, persistence-failure propagation, path traversal rejection, and orphan cleanup. Covers state portions of AC-01, AC-12, AC-15, AC-16, AC-22. |
| T4 | developer | T1 | `src\TCFUploader\Discovery\FileChange.cs`; `src\TCFUploader\Discovery\IChangeFeed.cs`; `src\TCFUploader\Discovery\FileChangeFeed.cs`; `src\TCFUploader\Discovery\Reconciler.cs`; `src\TCFUploader\Discovery\StabilityTracker.cs`; `src\TCFUploader\Files\FileObservation.cs`; `tests\TCFUploader.Tests\Discovery\StabilityTrackerTests.cs`; `tests\TCFUploader.Tests\Discovery\ReconcilerTests.cs` | Tests prove recursive startup/reconciliation discovery, reparse-point exclusion, inaccessible-child behavior, root-fatal behavior, case-insensitive candidate identity, rename/delete handling, exactly three qualifying observations at least two seconds apart, reset on change/open failure, and immediate reconciliation flags for watcher/inbox overflow. Covers AC-03 through AC-06 and discovery portions of AC-21/AC-22. |
| T5 | developer | T1, T3, T4 | `src\TCFUploader\Files\IFileSnapshotter.cs`; `src\TCFUploader\Files\FileSnapshotter.cs`; `src\TCFUploader\Files\ContentTypeMap.cs`; `src\TCFUploader\Upload\UploadWork.cs`; `src\TCFUploader\Upload\UploadScheduler.cs`; `tests\TCFUploader.Tests\Files\FileSnapshotterTests.cs`; `tests\TCFUploader.Tests\Files\ContentTypeMapTests.cs`; `tests\TCFUploader.Tests\Upload\UploadSchedulerTests.cs` | Tests prove one-pass streamed spool copy/hash/byte count, final source-stat recheck, source preservation, random persisted key format, exact fingerprint encoding, extension/content-type policy, active/completed dedupe, bounded upload channel capacity 64, and eventual re-admission through reconciliation. Covers snapshot/scheduling portions of AC-05, AC-07, AC-08, AC-16, AC-17, AC-20, AC-21. |
| T6 | developer | T1 | `src\TCFUploader\Upload\LumaBoothClient.cs`; `src\TCFUploader\Upload\RetryPolicy.cs`; `src\TCFUploader\Upload\UploadFailure.cs`; `tests\TCFUploader.Tests\TestDoubles\FakeClock.cs`; `tests\TCFUploader.Tests\TestDoubles\ScriptedHttpMessageHandler.cs`; `tests\TCFUploader.Tests\Upload\LumaBoothClientTests.cs`; `tests\TCFUploader.Tests\Upload\RetryPolicyTests.cs` | Scripted-handler tests prove exact PUT/POST contracts, streaming request content, bounded JSON parsing, URL resolution and host validation, no redirects, credential host allowlist, response classification, five-attempt bound, Retry-After/backoff cap, jitter bounds, timeout/cancellation, and no secret/body leakage. Covers AC-07 through AC-14 and HTTP portions of AC-18/AC-20. |
| T7 | developer | T3, T5, T6 | `src\TCFUploader\Upload\UploadWorker.cs`; `tests\TCFUploader.Tests\Upload\UploadWorkerTests.cs`; `tests\TCFUploader.Tests\Integration\RestartRecoveryTests.cs` | Tests prove one reader/one active HTTP operation, `PendingPut -> PutComplete -> Completed` persistence ordering, spool deletion only after completion, fatal 401/403 behavior, file-scoped failure persistence, no second PUT after restart from `PutComplete`, completed skip, changed version reprocessing, and resumable canceled work. Covers AC-12 through AC-16 and worker portions of AC-19. |
| T8 | developer | T2, T3, T4, T5, T7 | `src\TCFUploader\Hosting\WatcherCoordinator.cs`; `src\TCFUploader\Program.cs`; `tests\TCFUploader.Tests\Hosting\WatcherCoordinatorTests.cs` | Coordinator tests prove startup ordering, no watcher/network before validation, immediate startup scan, two-second observation loop, 60-second reconciliation, bounded callback inbox 1,024, intake stop before drain, first/second Ctrl+C semantics, 30-second grace, fatal exit-code propagation, and no new snapshot/network work after shutdown begins. Covers AC-01, AC-02, AC-03, AC-04, AC-06, AC-19, AC-21. |
| T9 | developer | T8 | `tests\TCFUploader.Tests\Integration\EndToEndContractTests.cs`; `tests\TCFUploader.Tests\Integration\ResourceBoundsTests.cs`; `tests\TCFUploader.Tests\Integration\SecurityBoundaryTests.cs`; `tests\TCFUploader.Tests\Integration\StartupValidationTests.cs`; `tests\TCFUploader.Tests\Integration\SourcePreservationTests.cs` | Real temp-directory tests with an injected handler prove one byte-identical PUT followed by one exact multipart POST and durable completion, startup rejection matrix, notification-loss recovery, large-file memory behavior, burst bounds/eventual reconciliation, state-write failure shutdown, source preservation, secret-free logs/state/errors, and refusal to send credentials to unallowlisted hosts. Completes automated AC-01 through AC-24 coverage without live network. |
| T10 | developer | T9 | `README.md` | README contains PowerShell restore/build/test/run/publish commands; secure environment and redirected-stdin token examples; fixed event/endpoints; defaults; lifecycle/log fields; exit codes; state path and fail-closed recovery; retry behavior; source-retention guarantee; at-least-once POST limitation; troubleshooting; AC-01 through AC-25 mapping to exact test methods or live-smoke evidence. |
| T11 | test-engineer | T10 | Evidence only; no production-file edits. Test-only remediation requires a new developer task. | Independently runs targeted and full Release tests, records total/passed/failed/skipped counts, maps AC-01 through AC-24 to executed test names, and returns PASS only with zero failures. |
| T12 | code-reviewer + security-engineer | T10 | Read-only review; no file edits. | Code Reviewer returns `APPROVED`. Security Engineer returns no unresolved Critical/High findings and specifically verifies token acquisition, redirect policy, host allowlist, path containment, state/spool validation, log redaction, and request-body bounds. |
| T13 | qa-engineer | T11, T12 | Evidence only; no production-file edits. | Runs the local no-live-network user journeys: empty startup/Ctrl+C, startup backlog, nested live arrival, watcher-loss reconciliation, restart from `PutComplete`, completed restart skip, and grace-expiry resume. Returns PASS with reproducible commands and observed exit codes. |
| T14 | engineering-judge | T11, T12, T13 | Mission evidence only. | Verifies every Definition of Done item from real evidence. AC-25 remains explicitly `BLOCKED/AWAITING CTO TOKEN` unless separately authorized and executed; all other applicable items must pass before `APPROVED`. |

## Execution Waves

```text
wave 1: T1
  -> wave 2: T2, T3, T4, T6 (logical independence, but one developer executes serially)
  -> wave 3: T5
  -> wave 4: T7
  -> wave 5: T8
  -> wave 6: T9
  -> wave 7: T10
  -> wave 8: T11 + T12 (independent validation in parallel)
  -> wave 9: remediation if any blocking gate fails; rerun invalidated gates
  -> wave 10: T13
  -> wave 11: T14
```

The implementation developer must keep the tree building between waves. Logical parallelism is
documented for dependency clarity only; the requested implementation shape uses one developer.

## Frozen Contracts

### Project and build contract

- Production target: `net8.0`, `OutputType=Exe`, nullable enabled, implicit usings enabled,
  deterministic output, warnings as errors, latest C# language version.
- Test target: `net8.0`, MSTest, project reference to production, not packable.
- `Directory.Packages.props` centrally pins only:
  `Microsoft.NET.Test.Sdk=18.9.0`, `MSTest.TestFramework=4.4.0`,
  `MSTest.TestAdapter=4.4.0`.
- Production contains no `PackageReference` and no third-party runtime assembly.
- All implementation types are `internal` unless required by the .NET host. Tests use
  `[assembly: InternalsVisibleTo("TCFUploader.Tests")]`.

### Constants and runtime options

```csharp
internal static class UploaderConstants
{
    internal const string EventId = "-P1Y143qUTagjT1hDguA";
    internal static readonly Uri PutBaseUri =
        new("https://w.fotoshare.co/upload_file/");
    internal static readonly Uri PostUri =
        new("https://fotoshare.co/api/event/-P1Y143qUTagjT1hDguA/upload");
    internal static readonly Uri MediaBaseUri =
        new("https://fotoshare.s3.us-east-005.backblazeb2.com/");
}

internal sealed record RuntimeOptions(
    TimeSpan ObservationInterval,
    TimeSpan ReconciliationInterval,
    TimeSpan HttpAttemptTimeout,
    TimeSpan ConnectTimeout,
    TimeSpan ShutdownGracePeriod,
    TimeSpan FailedCycleDelay,
    int MaxAttempts,
    int ChangeInboxCapacity,
    int UploadQueueCapacity,
    int MaxJsonResponseBytes)
{
    internal static RuntimeOptions Default { get; }
}
```

Defaults are exactly: 2 seconds, 60 seconds, 10 minutes, 30 seconds, 30 seconds, 60 seconds,
5 attempts, 1,024 paths, 64 uploads, and 65,536 JSON bytes.

### CLI and token contract

```csharp
internal sealed record CliOptions(string WatchedRoot);

internal static class CliOptionsParser
{
    internal static CliParseResult Parse(string[] args);
}

internal abstract record CliParseResult
{
    internal sealed record Run(CliOptions Options) : CliParseResult;
    internal sealed record ShowHelp : CliParseResult;
    internal sealed record ShowVersion : CliParseResult;
    internal sealed record Error(string Code, string Message) : CliParseResult;
}

internal static class TokenSource
{
    internal static Task<TokenReadResult> ReadAsync(
        Func<string?> readEnvironment,
        TextReader stdin,
        bool isInputRedirected,
        CancellationToken cancellationToken);
}

internal abstract record TokenReadResult
{
    internal sealed record Success(string Token) : TokenReadResult;
    internal sealed record Error(string Code, string Message) : TokenReadResult;
}
```

- CLI syntax is only `--folder <existing-directory>`, `--help`, or `--version`.
- Unknown, positional, duplicate, event, endpoint, or token arguments return exit code 2.
- Environment variable `LUMABOOTH_FOTOSHARE_TOKEN` wins when non-empty. Otherwise stdin must be
  redirected. Read at most 16 KiB, trim outer CR/LF, reject empty input, embedded CR/LF/control
  characters, or additional non-whitespace lines.
- The token is held only in memory and never included in an exception, state, log, command line,
  request URI, or default `HttpClient` header.

### Time contract

```csharp
internal interface IClock
{
    DateTime UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    int NextJitterMilliseconds(int exclusiveUpperBound);
}
```

Production uses `RandomNumberGenerator.GetInt32`; tests use deterministic queued time and jitter.

### Path, discovery, and stability contract

```csharp
internal enum FileChangeKind { Upsert, Delete, Overflow }

internal readonly record struct FileChange(FileChangeKind Kind, string? FullPath);
internal readonly record struct FileObservation(long Length, DateTime LastWriteUtc);

internal interface IChangeFeed : IDisposable
{
    event Action<FileChange>? Changed;
    void Start();
    void Stop();
}

internal sealed class Reconciler
{
    internal Task<ReconcileResult> ScanAsync(
        string watchedRoot,
        CancellationToken cancellationToken);
}

internal sealed record ReconcileResult(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> SkippedChildren);

internal sealed class StabilityTracker
{
    internal void Register(string canonicalFullPath);
    internal void Remove(string canonicalFullPath);
    internal IReadOnlyList<StableCandidate> Observe(
        DateTime observedUtc,
        Func<string, FileObservation?> tryObserveAndOpen);
}

internal readonly record struct StableCandidate(
    string CanonicalFullPath,
    FileObservation Observation);
```

- Canonical path keys use `Path.GetFullPath`, directory separator `\`, and
  `StringComparer.OrdinalIgnoreCase`.
- Recursive scans skip every file or directory with `FileAttributes.ReparsePoint`.
- Watcher settings are fixed: `IncludeSubdirectories=true`, 64 KiB internal buffer, and notify
  file name, size, last write, and creation time.
- Callbacks perform no filesystem or network I/O. They call nonblocking `TryWrite` on the bounded
  1,024-item inbox; callback overflow and `InternalBufferOverflowException` both request an
  immediate full reconciliation.
- A signature change resets count to one. A match qualifies only at counts 1, 2, and 3 where each
  qualifying observation is at least `ObservationInterval` after the previous qualifying one.
  Missing, locked, unreadable, or changed files do not consume upload retries.

### Snapshot, fingerprint, remote key, and scheduling contract

```csharp
internal interface IFileSnapshotter
{
    Task<SnapshotResult> TrySnapshotAsync(
        string canonicalFullPath,
        string normalizedRelativePath,
        FileObservation expected,
        CancellationToken cancellationToken);
}

internal abstract record SnapshotResult
{
    internal sealed record Ready(SnapshotDescriptor Snapshot) : SnapshotResult;
    internal sealed record RetryLater(string OutcomeCode) : SnapshotResult;
}

internal sealed record SnapshotDescriptor(
    string Fingerprint,
    string RelativePath,
    long ObservedLength,
    DateTime ObservedLastWriteUtc,
    string ContentSha256,
    long ByteLength,
    string ContentType,
    string Extension,
    string SpoolFile);

internal readonly record struct UploadWork(string Fingerprint);
```

- Snapshot copies source to a unique temp file under state `spool`, using asynchronous sequential
  streaming, while computing SHA-256 and exact byte length. It then re-stats the source. A mismatch
  or read race deletes only the incomplete temp file and returns `RetryLater`.
- Normalized relative path is the `Path.GetRelativePath` result using `\`, with no rooted path,
  empty segment, `.` segment, `..` segment, alternate data stream, or path outside the root.
- Fingerprint preimage is binary and unambiguous:
  1. four-byte little-endian byte count of UTF-8 `normalizedRelativePath.ToUpperInvariant()`;
  2. those UTF-8 bytes;
  3. eight-byte little-endian observed length;
  4. eight-byte little-endian UTC `Ticks`;
  5. 32 raw content-SHA-256 bytes.
  Fingerprint is the lowercase hex SHA-256 of that preimage.
- Remote key is `tcfuploader-` plus 16 cryptographically random bytes as 32 lowercase hex
  characters, plus a sanitized extension. Extension is lowercase `.` plus at most 16 ASCII
  alphanumeric characters; otherwise it is omitted. The key is persisted before PUT.
- Content-type map must at least cover `.jpg/.jpeg`, `.png`, `.gif`, `.webp`, `.heic/.heif`,
  `.mp4`, `.mov`, `.avi`, `.mkv`, and `.webm`; all other values use
  `application/octet-stream`.
- Upload channel is `Channel.CreateBounded<UploadWork>` with capacity 64,
  `FullMode=Wait`, `SingleReader=true`, `SingleWriter=false`.

### State and spool contract

```csharp
internal enum UploadStatus { PendingPut, PutComplete, Completed }

internal sealed record UploaderState(
    int SchemaVersion,
    string EventId,
    string WatchedRoot,
    DateTime UpdatedUtc,
    Dictionary<string, UploadItemState> Items);

internal sealed record UploadItemState(
    UploadStatus Status,
    string RelativePath,
    long ObservedLength,
    DateTime ObservedLastWriteUtc,
    string ContentSha256,
    long ByteLength,
    string ContentType,
    string Extension,
    string SpoolFile,
    string RemoteKey,
    string? RemoteUrl,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? CompletedUtc,
    DateTime? NextEligibleUtc,
    string? LastCycleOutcome,
    int LastCycleAttempts);

internal sealed class StateRepository : IAsyncDisposable
{
    internal static Task<StateOpenResult> OpenAsync(
        string watchedRoot,
        string eventId,
        IClock clock,
        CancellationToken cancellationToken);

    internal IReadOnlyList<UploadWork> GetResumableWork(DateTime nowUtc);
    internal bool ContainsActiveOrCompleted(string fingerprint);
    internal Task AddPendingAsync(
        SnapshotDescriptor snapshot,
        string remoteKey,
        CancellationToken cancellationToken);
    internal Task MarkPutCompleteAsync(
        string fingerprint,
        Uri remoteUrl,
        CancellationToken cancellationToken);
    internal Task RecordCycleFailureAsync(
        string fingerprint,
        string outcome,
        int attempts,
        DateTime nextEligibleUtc,
        CancellationToken cancellationToken);
    internal Task MarkCompletedAsync(
        string fingerprint,
        CancellationToken cancellationToken);
}

internal abstract record StateOpenResult
{
    internal sealed record Success(StateRepository Repository) : StateOpenResult;
    internal sealed record Invalid(string Code, string Message) : StateOpenResult;
    internal sealed record Locked(string Code, string Message) : StateOpenResult;
}
```

- State root is `%LOCALAPPDATA%\TCFUploader\events\<event-scope>\<root-scope>`.
  Each scope is the first 16 lowercase hexadecimal characters of SHA-256 over UTF-8 event ID or
  canonical root respectively. JSON stores full canonical values and rejects a collision/mismatch.
- `instance.lock` is held with `FileShare.None` for the repository lifetime.
- JSON uses camelCase strings exactly as shown in `architecture.md`; status values are
  `pendingPut`, `putComplete`, and `completed`; `schemaVersion` is exactly 1.
- `PendingPut` requires a valid spool and null `remoteUrl`; `PutComplete` requires a valid spool and
  validated allowlisted `remoteUrl`; `Completed` requires completion time and does not require a
  spool.
- State writes use a unique same-directory temp file, `FileOptions.WriteThrough`, `Flush(true)`,
  then `File.Move(temp, state, true)`. Only `StateRepository` writes state.
- Missing state creates V1. Empty/unreadable/invalid JSON, unknown schema/status, scope mismatch,
  invalid path/URI, invariant failure, missing active spool, byte mismatch, or hash mismatch exits
  3 before watcher or network startup. No automatic reset or backup fallback is allowed.
- After a valid load, only unreferenced files inside the resolved spool directory may be deleted.

### HTTP, retry, and worker contract

```csharp
internal sealed class LumaBoothClient
{
    internal Task<PutResult> PutAsync(
        UploadItemState item,
        string absoluteSpoolPath,
        string token,
        CancellationToken cancellationToken);

    internal Task<PostResult> PostAsync(
        UploadItemState item,
        string token,
        CancellationToken cancellationToken);
}

internal abstract record PutResult
{
    internal sealed record Success(Uri RemoteUrl) : PutResult;
    internal sealed record Failure(UploadFailure Error) : PutResult;
}

internal abstract record PostResult
{
    internal sealed record Success : PostResult;
    internal sealed record Failure(UploadFailure Error) : PostResult;
}

internal sealed record UploadFailure(
    string OutcomeCode,
    bool IsTransient,
    bool IsAuthenticationFatal,
    TimeSpan? RetryAfter);

internal sealed class RetryPolicy
{
    internal Task<RetryResult<T>> ExecuteAsync<T>(
        Func<int, CancellationToken, Task<T>> attempt,
        Func<T, UploadFailure?> classify,
        CancellationToken cancellationToken);
}

internal sealed record RetryResult<T>(
    T LastResult,
    int Attempts,
    UploadFailure? Failure);
```

- `SocketsHttpHandler.AllowAutoRedirect=false`; `ConnectTimeout=30 seconds`.
- Validate HTTPS, default port, no userinfo, and exact host immediately before adding bearer
  credentials. PUT host is only `w.fotoshare.co`; POST host is only `fotoshare.co`.
- Authorization is set on each `HttpRequestMessage`, never on default headers.
- PUT uses one percent-encoded remote-key path segment, `StreamContent`, exact content type, and
  no full-file buffering.
- Successful PUT is 2xx plus JSON object containing one non-empty string `url`. Relative values
  resolve against the fixed B2 base. Resolved URL must be HTTPS/default-port/no-userinfo/no-fragment
  and exact host `fotoshare.s3.us-east-005.backblazeb2.com`.
- POST is multipart form-data with `uploadFileField`, `imgSize`, `imgWidth=0`, and `imgHeight=0`;
  success is 2xx plus JSON Boolean `success=true`.
- JSON is read through a bounded 65,536-byte stream. Do not log or embed response bodies in errors.
- Transient failures are `HttpRequestException`, per-attempt timeout not caused by shutdown, 408,
  429, and 500-599. Other 4xx, 3xx, malformed/oversized JSON, invalid URL, and protocol-shape errors
  are non-transient. 401/403 also set `IsAuthenticationFatal=true`.
- Maximum attempts per step per cycle is five. Honor valid Retry-After up to 60 seconds; otherwise
  delay 1, 2, 4, and 8 seconds plus 0-250 ms jitter, with every delay capped at 60 seconds.
- Worker transition order is immutable:
  `persist PendingPut -> PUT -> validate/persist PutComplete -> POST -> persist Completed -> delete spool`.
- A persistence failure is process-fatal and prevents the next network step. A successful PUT is
  never repeated after `PutComplete` is durable. A POST accepted upstream but not durably completed
  may be repeated after restart; this is the documented at-least-once window.

### Coordinator, shutdown, logging, and exit contract

```csharp
internal sealed class WatcherCoordinator
{
    internal Task<int> RunAsync(
        string watchedRoot,
        string token,
        CancellationToken firstSignal,
        CancellationToken immediateSignal);
}
```

- Startup order is: canonicalize/validate folder and state separation; acquire state lock; load and
  fully validate state/spools; acquire token; compose client/worker; enqueue resumable durable work;
  start one worker; start watcher/observation/reconciliation; perform immediate startup scan.
- No watcher or network request exists before startup validation succeeds.
- First Ctrl+C stops intake, watcher, and reconciliation and prevents a new snapshot or network
  operation. Only the active operation may finish within 30 seconds. Deadline cancellation exits 5.
  Second Ctrl+C cancels immediately. Clean first-signal drain exits 0.
- Exit codes are fixed: 0 clean shutdown; 1 other fatal runtime error; 2 CLI/token/configuration;
  3 invalid/corrupt/incompatible state or lock; 4 upstream 401/403; 5 forced shutdown.
- Logs are console-only and one line each:
  `<UTC-O timestamp> <LEVEL> operation=<code> path="<relative-or-dash>" stage=<code> attempt=<n-or-0> fingerprint=<12-char-or-dash> outcome=<code>`.
  Values must be escaped as data. Never emit token, Authorization header, stdin, request headers,
  multipart body, full response body, or absolute source path.

## Automated Acceptance-Criterion Mapping

Test methods may be split into data rows, but these exact AC-prefixed names must exist in the named
files so evidence is searchable.

| AC | Required named automated evidence |
|---|---|
| AC-01 | `StartupValidationTests.AC01_InvalidStartupInputs_ExitBeforeWatcherOrHttp` |
| AC-02 | `WatcherCoordinatorTests.AC02_EmptyFolder_RunsUntilCleanCancellationWithoutHttp` |
| AC-03 | `EndToEndContractTests.AC03_StartupBacklog_UploadsEachVersionOnceAndSkipsRestart` |
| AC-04 | `EndToEndContractTests.AC04_NestedLiveArrival_ProcessesAfterStability` |
| AC-05 | `StabilityTrackerTests.AC05_UnstableLockedOrMissingFile_DoesNotSchedulePut` |
| AC-06 | `EndToEndContractTests.AC06_NotificationLossOrOverflow_ReconciliationRecoversFile` |
| AC-07 | `LumaBoothClientTests.AC07_Put_UsesPersistedKeyExactBytesTypeAndBearerHeaderOnly` |
| AC-08 | `ContentTypeMapTests.AC08_UnknownExtension_UsesOctetStream` |
| AC-09 | `LumaBoothClientTests.AC09_RelativePutUrl_ResolvesAgainstFixedMediaBase` |
| AC-10 | `LumaBoothClientTests.AC10_AbsolutePutUrl_AcceptsOnlyAllowlistedHttpsMediaHost` |
| AC-11 | `LumaBoothClientTests.AC11_Post_UsesExactEndpointMultipartFieldsTokenAndByteLength` |
| AC-12 | `UploadWorkerTests.AC12_PostSuccess_PersistsCompletedAndSkipsRestart` |
| AC-13 | `UploadWorkerTests.AC13_InvalidResponses_RemainIncompleteAndLogPreciseStage` |
| AC-14 | `RetryPolicyTests.AC14_TransientAndPermanentFailures_RespectAttemptAndDelayBounds` |
| AC-15 | `RestartRecoveryTests.AC15_PutCompleteRestart_ResumesPostWithoutPut` |
| AC-16 | `RestartRecoveryTests.AC16_FingerprintChange_ReprocessesWhileUnchangedSkips` |
| AC-17 | `SourcePreservationTests.AC17_AllOutcomes_LeaveSourceMetadataAndBytesUnchanged` |
| AC-18 | `SecurityBoundaryTests.AC18_LogsStateErrorsAndRequests_NeverExposeSecret` |
| AC-19 | `WatcherCoordinatorTests.AC19_CtrlC_GracefulCompletionOrForcedResumableCancellation` |
| AC-20 | `ResourceBoundsTests.AC20_LargeFile_StreamsWithSublinearManagedMemoryAndExactBytes` |
| AC-21 | `ResourceBoundsTests.AC21_NotificationBurst_RemainsBoundedAndReconciliationCompletes` |
| AC-22 | `StateRepositoryTests.AC22_WindowsPathEquivalence_DeduplicatesAndRejectsStateOverlap` |
| AC-23 | Build/dependency commands in the validation section plus `README` evidence table |
| AC-24 | `EndToEndContractTests.AC24_MockContract_OnePutThenOnePostAndDurableCompletion` |
| AC-25 | Manual authorized live-smoke evidence only; never part of the default test suite |

For AC-20, compare managed-memory growth while uploading a deterministic file at least 128 MiB.
The test passes when received bytes/hash match and peak managed-memory growth is less than 32 MiB
above the pre-upload stabilized baseline. The test must be tagged `TestCategory("Resource")`.

For AC-21, inject at least 4,096 distinct notification paths into the 1,024-item inbox, assert the
inbox and upload channel never exceed their configured capacities, assert at most one HTTP request
is active, then trigger reconciliation and prove every stable file reaches completed state.

## Validation Commands and Expected Evidence

Run from:

```powershell
Set-Location C:\Users\syedhu\source\repos\Dreamer\TCFUploader
```

### Per-wave developer validation

```powershell
dotnet restore .\TCFUploader.slnx
dotnet build .\TCFUploader.slnx --configuration Release --no-restore
dotnet test .\TCFUploader.slnx --configuration Release --no-build --filter "TestCategory!=Resource"
```

Expected: every command exits 0; build reports 0 warnings and 0 errors; tests report 0 failed.
T1 may have zero tests. From T2 onward, the developer records the test total after each wave.

### Full independent automated validation

```powershell
dotnet restore .\TCFUploader.slnx --force-evaluate
dotnet build .\TCFUploader.slnx --configuration Release --no-restore
dotnet test .\TCFUploader.slnx --configuration Release --no-build --logger "trx;LogFileName=TCFUploader.trx"
dotnet publish .\src\TCFUploader\TCFUploader.csproj --configuration Release --runtime win-x64 --self-contained false --no-build --output "$env:TEMP\TCFUploader-publish"
```

Expected: all commands exit 0; 0 build warnings/errors; 0 failed tests; TRX records the exact
executed AC-prefixed names; publish output contains the executable, DLL, deps/runtimeconfig files,
and no test assemblies.

### Runtime dependency inspection

```powershell
dotnet list .\src\TCFUploader\TCFUploader.csproj package --include-transitive
if (Select-String -Path .\src\TCFUploader\TCFUploader.csproj -Pattern '<PackageReference' -Quiet) { throw 'Production PackageReference found' }
$assets = Get-Content .\src\TCFUploader\obj\project.assets.json -Raw | ConvertFrom-Json
$runtimePackages = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' })
if ($runtimePackages.Count -ne 0) { $runtimePackages.Name; throw 'Third-party/runtime packages found in production assets' }
```

Expected: `dotnet list` reports no packages for the production project; the PowerShell checks emit
no package names and exit 0.

### Scope and secret checks

```powershell
git -C C:\Users\syedhu\source\repos\Dreamer --no-pager status --short -- TCFUploader .ai-org\missions\2026-09-29-lumabooth-folder-watcher-06f3c973
git -C C:\Users\syedhu\source\repos\Dreamer --no-pager diff --check -- TCFUploader .ai-org\missions\2026-09-29-lumabooth-folder-watcher-06f3c973
git -C C:\Users\syedhu\source\repos\Dreamer --no-pager diff --name-only -- TCFUploader
Get-ChildItem . -Recurse -File | Select-String -Pattern 'Authorization:\s*Bearer|LUMABOOTH_FOTOSHARE_TOKEN\s*='
```

Expected: changed implementation paths are under `TCFUploader`; mission evidence changes are under
this mission folder; `diff --check` exits 0; the secret scan finds no literal Authorization header
or assigned token value. Do not inspect, clean, stage, or alter unrelated paths.

### No-live-network automated-test rule

- All automated HTTP tests inject `ScriptedHttpMessageHandler`.
- The default scripted handler throws on any request not explicitly queued by a test.
- No automated test reads `LUMABOOTH_FOTOSHARE_TOKEN`.
- No automated test resolves or contacts `w.fotoshare.co`, `fotoshare.co`, or the B2 media host.
- Any unexpected live DNS/network attempt is an immediate test and mission failure.

### Authorized AC-25 live smoke

This is a separate, CTO-authorized activity after all local gates pass. It is not authorized by this
plan. When authorized, use a uniquely named disposable source file, supply the token only through
the environment or redirected stdin, capture secret-free logs, verify appearance on event
`-P1Y143qUTagjT1hDguA`, verify durable `completed` state, and prove the source file was not deleted
or modified. Never place the token in the command line, evidence artifact, shell history, or log.

## Stop Conditions

Implementation or validation stops and reports `BLOCKED` when any of the following occurs:

1. A requirement would require changing the fixed event, endpoint allowlist, delivery semantics,
   state schema, retry bound, queue bound, or source-preservation rule.
2. A task requires editing a file outside its exclusive ownership, `TCFUploader`, or this mission
   folder. Replan ownership before continuing.
3. State cannot prove the durable transition ordering or an active spool cannot be validated.
4. Any token, Authorization header, multipart body, or full upstream response body appears in
   logs, state, exceptions, test evidence, or source.
5. Any automated test attempts live network access or requires a production credential.
6. Restore introduces a production package, build has a warning/error, any automated test fails,
   publish fails, or `diff --check` fails.
7. A Critical/High security finding, Code Reviewer `CHANGES_REQUIRED`, Test Engineer failure, or QA
   failure remains unresolved. Create a developer remediation task and rerun every invalidated gate.
8. AC-25 is requested without a separately supplied CTO authorization/token. Report it as the only
   expected external live-contract blocker; never invent credentials or bypass the gate.

## Plan Exit

This plan is ready for dispatch at T1. The first implementation wave may begin without another
architecture or product decision. The mission is not complete until T11-T14 finish and the
Engineering Judge evaluates the Definition of Done from executed evidence.

## Rework wave 2 — 2026-09-29

- [DONE] T15 (developer): close HIGH/MEDIUM security and correctness/ops findings; add executable coverage for all named gaps; preserve source and unrelated worktree changes.
- [FAIL] T16 (test-engineer): independent Release restore/build/test/publish/package inspection and acceptance-gap verification.
- [PASS_WITH_FINDINGS] T17 (security-engineer): independent security rerun; zero unresolved Critical/High and verify substantive Medium remediation.
- [FAIL] T18 (code-reviewer): independent correctness/maintainability rerun.
- [PENDING] T19 (qa-engineer, if needed): integrated no-live-network user journeys.
- [PENDING] T20 (engineering-judge): independent evidence judgment; mission must remain REWORK/ready-for-rerun, not COMPLETED.

## Rework wave 3 — independent gate findings

- [DONE] T21 (developer): implement fair bounded candidate admission; no-new-network-step shutdown boundary; snapshot-time pinned spool validation; generation-aware journal compaction/replay with delete-failure regression; fail closed on pre-existing ACL drift; restore 4,096 AC-21 evidence; add POST logging, body-disconnect retry, coordinator AC-16, state-root replacement tests.
- [FAIL/PASS/FAIL] T22/T23/T24: rerun independent test/security/code-review gates.
- [PENDING] T25: judge evidence while leaving mission REWORK/ready-for-independent-rerun.

## Rework wave 4 — second independent rerun

- [DONE] T26 (developer): gate every retry attempt against first-signal shutdown; restore 4,096 real-file PUT/POST/completion AC-21 at 1,024/64 bounds; replace ordinal reconciliation continuation with churn-safe eventual-coverage design and regression; add active StateRepository mutation after state-root replacement with artifact preservation; stabilize destructive identity test without weakening it.
- [FAIL/FAIL/FAIL] T27/T28/T29: independent test/security/code-review rerun.
- [PENDING] T30: engineering judge, with final state remaining REWORK/ready-for-independent-rerun per CTO directive.

## Rework wave 5 — final substantive gate blockers

- [DONE] T31 (developer): make retry backoff interruptible by first signal without recording failure; atomically serialize first-attempt admission with shutdown for PUT/POST; replace reconciliation cursor with bounded fair algorithm that reaches a pre-existing stable target while a full batch is inserted ahead on every pass; correct the regression to exercise that exact adversarial case.
- [PASS/PASS/FAIL] T32/T33/T34: final independent test/security/code-review rerun.
- [PENDING] T35: independent engineering judge; leave mission REWORK/ready-for-rerun per CTO objective, not COMPLETED.

## Rework wave 6 — journal cancellation resumability

- [DONE] T36 (developer): make journal append commit atomic/recoverable under forced cancellation so a partial append cannot make prior durable state unreopenable; add large-store AC-19 regression forcing interruption between record data and commit and proving restart at prior durable stage.
- [DONE] T37/T38/T39: rerun independent test/security/code-review gates invalidated by state persistence change.
- [DONE] T40: engineering judge; leave final mission state REWORK/ready-for-independent-rerun, not COMPLETED.

## Rework wave 7 — second-round independent review findings

- [DONE] T41 (developer): surface completed-spool cleanup failures as fatal after durable completion;
  stop further network admission; distinguish newly created snapshot payloads; release only newly
  created completed duplicates; preserve active payloads; add four direct regressions.
- [DONE] T42 (test-engineer): Release restore/build/full test/focused regression/publish/package
  inspection. Result: 89/89 full, 4/4 focused, five-file publish, zero test assemblies, zero
  production packages, ZIP mismatch count zero.
- [DONE] T43 (qa-engineer): current local/injected journey rerun. Result: main 18/18, overflow 1/1,
  AC19 1/1, corrected one-request durable-`PutComplete` shutdown evidence, published CLI smokes pass.
- [DONE] T44 (code-reviewer): APPROVED with both HIGH findings closed.
- [DONE] T45 (engineering judge): release rework evidence approved while retaining mission state
  `REWORK`; no authorization for AC-25, deployment, commit/push, or mission completion.

## Current readiness status — 2026-09-29

- Independent Test Gate: PASS — 89/89, 0 failed, 0 skipped; focused second-round regressions 4/4.
- Independent QA Gate: PASS — main 18/18, overflow 1/1, focused AC19 1/1.
- Independent Code Review: APPROVED.
- Release publish/package inspection: PASS — exactly five production files, zero test assemblies,
  five-entry ZIP with zero content mismatches.
- Mission state intentionally remains **REWORK / ready-for-independent-rerun**, not COMPLETED.
  AC-25/live upload remains out of scope and unauthorized.

## Rework wave 9 final status — fatal-transition admission closure

- [DONE] T51/T54: centralized all fatal transitions through shared admission closure; added
  deterministic feed.Start, watched-root, general coordinator, and feed.Stop regressions. The
  feed.Stop test exits intake without cancellation and fails when its admission closure is removed.
- [DONE] T57/T58/T61: diagnosed and fixed identity-drift test races. Final root cause was retained
  watched source reconciliation opening a spool temp while the test injected a directory move.
  Removing the already-persisted watched source produced fail-before 4/100 and pass-after 200/200.
- [PASS] T62 final independent test gate: 306/306 executed validations; identity stress 100/100;
  fatal focus 4/4; feed.Stop 10/10; full Release 96/96 twice; build/publish/package/ZIP/smoke pass.
- [PASS] T63 final independent code review: APPROVED with no Critical/High/Medium findings.
- [PENDING] Current Engineering Judge judgment. Earlier T40/T45 judgments are historical and
  superseded for the current tree. Mission remains **REWORK**, not COMPLETED.

## Rework wave 9 — fatal-transition admission closure

- [IN PROGRESS] T51 (developer): centralize fatal transition handling so admission closes before intake cancellation or scheduler completion; add deterministic feed.Start, watched-root, general coordinator, and feed.Stop fatal regressions including queued post-PUT POST evidence.
- [PENDING] T52/T53 (test-engineer/code-reviewer): independent validation after implementation.
- [PENDING] T54: final independent engineering judgment intentionally deferred; mission remains REWORK.
- [DONE] T51 developer evidence: centralized fatal admission closure; focused 4/4, coordinator 21/21, full Release 96/96 self-tests.
- [IN PROGRESS] T52/T53: independent test and code-review gates.
- [PASS] T52 independent test: Release 96/96; focused 4/4 and 10 repeated iterations; build/publish/package/ZIP checks passed.
- [FAIL] T53 independent code review: feed.Stop regression was invalid because immediate cancellation independently stopped worker/post/queue; test would pass if feed.Stop admission closure were removed.
- [IN PROGRESS] T54 developer rework: create deterministic feed.Stop failure seam allowing admitted PUT to complete, proving admission gate alone blocks POST and queued PUT.
- [DONE] T54 developer: added internal intake-stop seam; corrected feed.Stop test permits admitted PUT completion; mutation removing feed.Stop admission closure failed with 4 requests; restored full 96/96.
- [IN PROGRESS] T55/T56 independent test and code-review reruns.
- [FAIL] T55 independent test rerun: feed.Stop 10/10 and four fatal 4/4 passed; first full suite 95/96 due intermittent CompletedSpoolIdentityDrift restoration DirectoryNotFoundException, rerun 96/96 and isolated 10/10.
- [PASS] T56 independent code review: APPROVED for fatal admission fix and corrected regression.
- [IN PROGRESS] T57 incident-debugger: prove root cause of intermittent identity-drift fixture disappearance before remediation.
- [DONE] T57 diagnosis: production cleanup/disposal eliminated; test's unnecessary destructive spool-original restoration is fragile and exact DNF was reproduced under stress. Durable fix is direct persisted-state/payload validation without restore/reopen.
- [IN PROGRESS] T58 developer: apply minimal fixture-root stabilization without weakening identity-drift coverage.
- [DONE] T58 developer: removed identity-drift restore/reopen; direct persisted completed-state and moved payload assertions; isolated 100/100, parallel process stress 50/50, full suite 288/288 across three runs.
- [IN PROGRESS] T59/T60 final independent test and code-review gates.
- [FAIL] T59 final independent test: fatal 4/4, feed.Stop 10/10, two full suites 192/192, publish/package PASS; identity-drift isolated stress failed 2/38 because moved payload vanished.
- [PASS] T60 final code review: APPROVED.
- [IN PROGRESS] T61 incident-debugger: instrument isolated test to identify actual deletion actor and causal sequence; prior fixture-only explanation is insufficient.
- [DONE] T61 root cause/fix: retained identity.bin was re-snapshotted during initial reconciliation, leaving an open spool temp that made injected Directory.Move fail; remove watched source after pending persistence. Fail-before 4/100 concurrent, pass-after 200/200; coordinator 21/21.
- [IN PROGRESS] T62/T63 final independent test and code-review gates after proven root fix.
- [PASS] T62 final independent test: 306/306; identity stress 100/100; fatal focus 4/4;
  feed.Stop 10/10; full Release 96/96 twice; release/package/ZIP/smoke checks passed.
- [PASS] T63 final independent code review: APPROVED with no Critical/High/Medium findings.
- [PENDING] Final independent Engineering Judge judgment by explicit CTO instruction.
  **Current mission state: REWORK. Do not mark COMPLETED.**
