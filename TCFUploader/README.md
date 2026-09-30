# TCFUploader

TCFUploader is a .NET 8 Windows command-line watcher for the fixed LumaBooth event
`-P1Y143qUTagjT1hDguA`. It recursively watches one folder for `.jpg`, `.jpeg`, and `.png` files
(case-insensitive), waits for accepted files to become stable, spools immutable snapshots outside
the watched tree, uploads each snapshot to Fotoshare, and durably resumes incomplete work after
restart. All other file types are ignored for new discovery and snapshots.

## Build, test, run, and publish

```powershell
dotnet restore .\TCFUploader.slnx
dotnet build .\TCFUploader.slnx --configuration Release --no-restore
dotnet test .\TCFUploader.slnx --configuration Release --no-build
dotnet run --project .\src\TCFUploader\TCFUploader.csproj -- --folder C:\Photos --browser-login
dotnet publish .\src\TCFUploader\TCFUploader.csproj --configuration Release --runtime win-x64 --self-contained false
```

`--browser-login` is the recommended interactive mode on Windows. After the state store is loaded,
validated, and locked, TCFUploader uses a valid `LUMABOOTH_FOTOSHARE_TOKEN` first, then valid
redirected standard input, and launches a browser only when neither ordinary source is present.
It opens the exact LumaBooth event upload page in a new isolated Microsoft Edge InPrivate profile,
or Google Chrome Incognito profile when Edge is unavailable. Sign in normally in that window.
The login wait is limited to 10 minutes, and the first **Ctrl+C** cancels it.

Browser login requires an installed Microsoft Edge or Google Chrome. Edge is preferred. To use an
exact executable instead, set `TCFUPLOADER_BROWSER_PATH` to an existing browser executable:

```powershell
$env:TCFUPLOADER_BROWSER_PATH = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
TCFUploader --folder C:\Photos --browser-login
```

The browser uses a uniquely named private profile below
`%LOCALAPPDATA%\TCFUploader\browser-auth`, protected with current-user-only ACLs. Chromium remote
debugging binds only to `127.0.0.1` on an ephemeral port. TCFUploader considers only
`https://dash.lumabooth.com/` page targets and evaluates a narrow expression that returns only
`state.settings.fotoshare_token` from the `user-settings` localStorage record, with
`settings.fotoshare_token` accepted for compatibility. It does not collect credentials, cookies,
Firebase tokens, or other storage. The token is validated in memory, never printed, persisted,
logged, included in exceptions, or placed on a process command line. The launched browser process
tree is terminated and the exact isolated profile is deleted on every exit path. If cleanup cannot
be confirmed, TCFUploader fails closed and does not start uploads.

For noninteractive operation, set the process environment variable:

```powershell
[Environment]::SetEnvironmentVariable(
  'LUMABOOTH_FOTOSHARE_TOKEN',
  (Read-Host -MaskInput 'Fotoshare token'),
  'Process')
TCFUploader --folder C:\Photos
```

Or avoid retaining the token in the process environment by redirecting a trusted token-producing
command while leaving `LUMABOOTH_FOTOSHARE_TOKEN` unset:

```powershell
& .\Get-FotoshareToken.ps1 | TCFUploader --folder C:\Photos
```

All sources are limited to one 16 KiB token with no control characters. Never put the token on the
command line. Without `--browser-login`, existing environment/stdin behavior is unchanged. Stop the
watcher with **Ctrl+C**; a second Ctrl+C forces immediate cancellation. The first signal atomically
closes PUT/POST attempt admission and interrupts any retry backoff. An attempt admitted before that
close is active and may finish; an attempt that loses the admission race sends no request. An
already-active PUT may finish and persist
`PutComplete`, but POST is then deferred for restart rather than started after shutdown was
requested.

## Fixed contract and defaults

- PUT: `https://w.fotoshare.co/upload_file/{random-key}`
- POST: `https://fotoshare.co/api/event/-P1Y143qUTagjT1hDguA/upload`
- Returned media URLs: HTTPS default-port URLs on
  `fotoshare.s3.us-east-005.backblazeb2.com` only
- Accepted new files: `.jpg`, `.jpeg`, and `.png` only, case-insensitively. Files such as GIF,
  WebP, HEIC, videos, text files, extensionless files, and deceptive names such as
  `photo.jpg.exe` are ignored. Persisted resumable work created by an earlier version is still
  validated and processed.
- Three unchanged observations, at least 2 seconds apart
- Full startup scan; reconciliation every 60 seconds
- One upload worker; 1,024 change hints; 64 queued uploads
- Discovery is streamed in batches of at most 4,096 files and candidate tracking is capped at
  4,096 entries. A reconciliation cycle keeps its streaming directory-enumeration position between
  batches, with only one file of lookahead, rather than restarting from a lexical cursor. New names
  inserted earlier in lexical order therefore cannot repeatedly reset progress ahead of a
  pre-existing file; arrivals not observed by the active filesystem enumeration are picked up by
  the next periodic cycle. The algorithm does not trust or claim fairness from attacker-controlled
  creation or last-write timestamps. Full candidate sets request another reconciliation batch
  rather than retaining an unbounded namespace in memory. A candidate admission is limited to six
  qualifying observations; continuously changing entries rotate out, and spool-capacity failures
  relinquish their slot and rely on reconciliation. This prevents an early non-progressing set from
  permanently starving later paths. Directory descent is iterative rather than call-stack
  recursive and is capped at 1,024 levels; deeper children are skipped and reported through the
  bounded discovery diagnostics while sibling traversal continues.
- Aggregate immutable spool data is capped at 10 GiB, with 2 GiB of filesystem free space reserved
  for the operating system and other applications.
- Five attempts per transient-failure cycle; 1/2/4/8 second exponential delays plus jitter,
  `Retry-After` honored, all delays capped at 60 seconds
- Ten-minute HTTP-attempt timeout; 30-second shutdown grace

The spool safeguards can be overridden with positive invariant-culture byte counts:

```powershell
$env:TCFUPLOADER_MAX_SPOOL_BYTES = '10737418240' # default: 10 GiB
$env:TCFUPLOADER_MIN_FREE_BYTES = '2147483648'  # default: 2 GiB; zero is allowed
```

Invalid values are configuration errors and exit with code `2`. A file is never partially admitted:
capacity is reserved before copying and free space is rechecked while copying. If aggregate capacity
is exhausted, the file remains untouched and is retried after reconciliation with
`outcome=spool_quota_exceeded`; if the free-space reserve would be crossed, the outcome is
`spool_free_space_reserved`. These are aggregate local-duplication limits, not an additional source
file-size rule.

Logs contain UTC timestamp, severity, operation, relative path, lifecycle stage, attempt,
12-character fingerprint prefix, and outcome. They never contain the token, authorization header,
multipart body, full response body, or absolute source path.

## State, recovery, and safety

State is stored below:

```text
%LOCALAPPDATA%\TCFUploader\events\<event-hash>\<watch-root-hash>\
```

The directory contains `instance.lock`, `state.v1.json`, immutable files under `spool`, and, while
large stores are active, a checksummed `state.v1.journal` that is compacted into `state.v1.json`
on clean shutdown, recovery, or when the journal reaches 1,024 records or 8 MiB. Individual journal
records are limited to 256 KiB and replay is streamed rather than wholly materialized. State snapshots
are flushed and atomically replaced; each journaled stage transition is flushed before it is
acknowledged. Every mutation has a monotonic sequence; the base snapshot records the highest covered
sequence and restart replays only strictly newer contiguous records. Therefore a retained older
journal cannot roll state back if base replacement succeeded but journal deletion failed. Each
prepared record and its terminator are appended as one non-cancellable write while the repository
persistence lock is held; an injected write failure rolls the append back to its prior durable
length before returning. A non-empty unterminated final journal record from outside that controlled
append path is treated as corruption, and the state, journal, and spool artifacts are preserved for
recovery analysis. A legacy base without a sequence is accepted as sequence zero only when no legacy
journal must be replayed; incompatible journal records fail closed.

The state and spool directory identities are pinned for the repository lifetime, protected with
current-user-only Windows ACLs, and revalidated before state or spool mutations. Existing stores are
validated for owner, protected inheritance, access rules, reparse ancestry, and directory identity
before any ACL is modified; drift fails closed and recovery artifacts are preserved. Snapshot
creation revalidates the pinned spool identity and ACL immediately before temporary-file creation,
each write, final rename, and cleanup. Corrupt, unreadable, incompatible, scope-mismatched state, a
missing/changed active spool, changed state/spool identity or permissions, an overlapping state/watch
path, or an active lock causes the process to fail closed. Do not delete state to hide corruption:
preserve it for diagnosis, stop all instances, copy it for investigation, and only remove or replace
it after an operator has explicitly accepted possible duplicate uploads.

Source files are never deleted, moved, renamed, truncated, or modified. Delivery is at least once:
if the POST succeeds upstream but the process stops before local completion is persisted, restart
may repeat the POST because the upstream API provides no idempotency key.

Windows reparse-point watch roots are rejected, reparse-point descendants are excluded, and each
source is revalidated before and after opening. The final path of the open Windows file handle must
remain under the approved physical root before bytes are accepted. A directory can still change
between enumeration or a watcher callback and the later open; that race is handled fail-closed by
discarding the candidate or snapshot. The implementation does not claim an atomic snapshot of the
entire directory tree.

Exit codes: `0` clean stop, `1` fatal runtime/state-write/root failure, `2` CLI/token/configuration
error, `3` invalid/corrupt/incompatible/locked state, `4` upstream 401/403, `5` forced shutdown.

## Troubleshooting

- **No token:** use `--browser-login`, set the process environment variable, or use redirected stdin.
- **Browser login unavailable:** install Edge or Chrome, or set `TCFUPLOADER_BROWSER_PATH` to the
  exact executable. Retry after timeout/cancellation. If private-profile cleanup fails, close only
  the launched browser and remove its isolated profile before retrying; uploads do not start.
- **Exit 3:** stop competing instances; inspect and preserve state. The tool never resets it.
- **Files remain pending:** ensure producers release read locks and allow three observations.
- **Spool capacity outcomes:** free completed spool/state work normally, increase the configured
  aggregate limit only after checking disk capacity, or free enough space to restore the reserve.
- **401/403:** replace the in-memory token and restart; the token is not stored.
- **Repeated failures:** inspect the stage/outcome fields. Each later reconciliation starts a new
  bounded retry cycle.
- **Long-lived completed state:** completed fingerprints are retained to preserve restart dedupe.
  Monitor state-file size and startup latency for high-volume, long-lived deployments; no record
  should be removed without an equivalent durable deduplication index.

## Acceptance evidence

| AC | Automated evidence |
|---|---|
| AC-01 | `StartupValidationTests.AC01_InvalidStartupInputs_ExitBeforeWatcherOrHttp` |
| AC-02 | `WatcherCoordinatorTests.AC02_EmptyFolder_RunsUntilCleanCancellationWithoutHttp` |
| AC-03 | `EndToEndContractTests.AC03_StartupBacklog_UploadsEachVersionOnceAndSkipsRestart` and `EndToEndContractTests.StartupReconciliation_UploadsOnlyMixedCaseJpgJpegAndPng` |
| AC-04 | `EndToEndContractTests.AC04_NestedLiveArrival_ProcessesAfterStability` and `EndToEndContractTests.InjectedLiveEvents_IgnoreUnsupportedWithoutHttpStateOrSpoolActivity` |
| AC-05 | `StabilityTrackerTests.AC05_UnstableLockedOrMissingFile_DoesNotSchedulePut` and `FileSnapshotterTests.AC05_LockedFile_IsUnavailableUntilProducerReleasesIt` |
| AC-06 | `EndToEndContractTests.AC06_NotificationLossOrOverflow_ReconciliationRecoversFile` |
| AC-07 | `LumaBoothClientTests.AC07_Put_UsesPersistedKeyExactBytesTypeAndBearerHeaderOnly` |
| AC-08 | `ContentTypeMapTests.AcceptedExtensions_ExposeOnlyRequiredMimeTypes` and `ContentTypeMapTests.LegacyStoredContentTypes_RemainReadable` |
| AC-09 | `LumaBoothClientTests.AC09_RelativePutUrl_ResolvesAgainstFixedMediaBase` |
| AC-10 | `LumaBoothClientTests.AC10_AbsolutePutUrl_AcceptsOnlyAllowlistedHttpsMediaHost` |
| AC-11 | `LumaBoothClientTests.AC11_Post_UsesExactEndpointMultipartFieldsTokenAndByteLength` |
| AC-12 | `UploadWorkerTests.AC12_PostSuccess_PersistsCompletedAndSkipsRestart` |
| AC-13 | `UploadWorkerTests.AC13_InvalidResponses_RemainIncompleteAndLogPreciseStage` and `UploadWorkerTests.AC13_PostFailures_LogExactStageAndOutcomeCodes` |
| AC-14 | `RetryPolicyTests.AC14_TransientAndPermanentFailures_RespectAttemptAndDelayBounds` and `UploadWorkerTests.AC14_BodyDisconnect_RetriesThroughWorkerAndCompletes` |
| AC-15 | `RestartRecoveryTests.AC15_PutCompleteRestart_ResumesPostWithoutPut` |
| AC-16 | `RestartRecoveryTests.AC16_FingerprintChange_ReprocessesWhileUnchangedSkips` and `EndToEndContractTests.AC16_Coordinator_ReplacedFileUploadsNewVersionAndUnchangedFileSkips` |
| AC-17 | `SourcePreservationTests.AC17_AllOutcomes_LeaveSourceMetadataAndBytesUnchanged` |
| AC-18 | `SecurityBoundaryTests.AC18_LogsStateErrorsAndRequests_NeverExposeSecret` |
| AC-19 | `WatcherCoordinatorTests.AC19_CtrlC_GracefulCompletionOrForcedResumableCancellation` |
| AC-20 | `ResourceBoundsTests.AC20_LargeFile_StreamsWithSublinearManagedMemoryAndExactBytes` |
| AC-21 | `ResourceBoundsTests.AC21_NotificationBurst_RemainsBoundedAndReconciliationCompletes` (4,096 distinct notifications; production 1,024 inbox and 64 upload queue; dropped live upserts recovered by reconciliation) |
| AC-22 | `StateRepositoryTests.AC22_WindowsPathEquivalence_DeduplicatesAndRejectsStateOverlap` |
| AC-23 | `StartupValidationTests.AC23_ProductionProject_HasNoPackageReferenceOrRuntimePackages` plus Release build/publish inspection |
| AC-24 | `EndToEndContractTests.AC24_MockContract_OnePutThenOnePostAndDurableCompletion` |
| AC-25 | Manual authorized live smoke only; not run by automated tests |
