# Final Operator End-to-End QA

Date: 2026-09-29

## Verdict

**PASS**

All 16 planned local/operator journeys passed without production credentials, DNS-dependent
Fotoshare calls, customer data, production code edits, or commit/push operations.

- Current Release suite: **96 passed, 0 failed, 0 skipped**
- Additional QA-only permanent-failure recovery journey: **1 passed, 0 failed, 0 skipped**
- Published-process CLI cases: **6 passed, 0 failed**
- Published executable SHA-256:
  `FF772E6ADD3EACB89779C615A9D2A04274DDEA1C7824A46D88BC06FC1999C4C5`

## Environment

- Windows, .NET 8, repository folder
  `C:\Users\syedhu\source\repos\Dreamer\TCFUploader`
- Published application:
  `artifacts\final-release-gate\publish-final\TCFUploader.exe`
- HTTP PUT/POST behavior: existing in-process scripted `HttpMessageHandler` harness; no live
  network endpoint or credential was used.
- Direct process behavior: published executable with isolated temporary watch and
  `%LOCALAPPDATA%` folders, a sentinel fake token, and Windows `CTRL_BREAK_EVENT`.
- Evidence folder: `artifacts\final-e2e-qa-20260929-0543`

## Journey Results

| # | Operator journey | Result | Reproducible evidence |
|---|---|---|---|
| 1 | Help, version, missing arguments, missing folder, and missing token | PASS | Published executable exits: help `0`, version `0`, no args `2`, missing folder `2`, missing token `2`. Diagnostics are concise and actionable. |
| 2 | Empty-folder monitoring and clean operator stop | PASS | Published executable logged `operation=monitoring ... outcome=active`, received a real Windows Ctrl+Break, logged `operation=shutdown ... outcome=clean`, exited `0`, sent no work, and left the watch folder empty. `AC02_EmptyFolder_RunsUntilCleanCancellationWithoutHttp` also passed. |
| 3 | Root and nested startup backlog, durable completion, restart dedupe | PASS | `AC03_StartupBacklog_UploadsEachVersionOnceAndSkipsRestart`: two files produced four requests, durable state had no resumable work, and restart produced zero requests. |
| 4 | Live nested arrival after monitoring starts | PASS | `AC04_NestedLiveArrival_ProcessesAfterStability`: nested file produced one PUT and one POST without restart. |
| 5 | Partial/changing and exclusively locked file stability | PASS | `AC05_UnstableLockedOrMissingFile_DoesNotSchedulePut` and `AC05_LockedFile_IsUnavailableUntilProducerReleasesIt`: no snapshot/PUT while unstable or locked; processing became eligible only after release and qualifying observations. |
| 6 | Lost notifications, overflow, and bounded reconciliation | PASS | `AC06_NotificationLossOrOverflow_ReconciliationRecoversFile` passed. `AC21_NotificationBurst_RemainsBoundedAndReconciliationCompletes` proved 4,096 files, 8,192 requests, inbox high-water 1,024, upload-queue high-water 64, max HTTP concurrency 1, 3,072 reconciliation requests, and 2 reconciliation executions. |
| 7 | Transient failure, bounded retry, then success | PASS | `AC14_BodyDisconnect_RetriesThroughWorkerAndCompletes` completed after one retry; `AC14_TransientAndPermanentFailures_RespectAttemptAndDelayBounds` and HTTP classification tests passed. |
| 8 | Permanent failure remains durable, then a later cycle recovers | PASS | QA-only `PermanentPutFailure_RemainsDurableAndLaterCycleRecovers`: first PUT returned HTTP 400, made one request, persisted `PendingPut` with `put_http_400`; the later cycle made PUT+POST and persisted `Completed` (three total requests). Temporary source was removed after execution. |
| 9 | Restart from durable `PutComplete` | PASS | `AC15_PutCompleteRestart_ResumesPostWithoutPut`: restart issued exactly one POST and no second PUT. |
| 10 | Unchanged skip and changed replacement | PASS | `AC16_Coordinator_ReplacedFileUploadsNewVersionAndUnchangedFileSkips`: unchanged restart issued zero requests; changed content issued a new PUT+POST; two completed fingerprints persisted. |
| 11 | Source preservation across success, retry, and failure | PASS | `AC17_AllOutcomes_LeaveSourceMetadataAndBytesUnchanged` and `Snapshot_StreamsHashesAndPreservesSource` passed. |
| 12 | Spool quota and free-space refusal | PASS | `Snapshot_AggregateQuotaAndFreeSpace_AreEnforcedBeforeAndDuringCopy` returned `spool_quota_exceeded` and `spool_free_space_reserved` with no residual spool files/accounting. `SnapshotCapacityOutcome_IsLoggedAndDoesNotIssueHttp` proved zero HTTP requests. |
| 13 | Corrupt, locked, missing-primary, and torn state fail closed | PASS | `State_FailsClosedOnCorruptionAndExclusiveLock`, corruption matrix, missing-primary journal, large-journal corruption, and `State_TornJournalTail_FailsClosedAndPreservesRecoveryArtifacts` all passed. |
| 14 | Graceful and forced shutdown while work is active | PASS | Direct empty-process Ctrl+Break exited `0`. `AC19_CtrlC_GracefulCompletionOrForcedResumableCancellation` proved graceful active PUT persistence as `PutComplete`, no post-signal admission, and forced grace expiry exit `5` with resumable incomplete state. PUT/POST retry-backoff shutdown tests also passed. |
| 15 | Fatal feed, watched-root, coordinator, and storage transitions suppress queued network work | PASS | Feed-start, feed-stop, watched-root-loss, general coordinator fatal, pending-state persistence failure, completed-spool deletion failure, completed-spool identity drift, and duplicate-cleanup I/O/access failure tests all passed; queued/follow-on PUT/POST work was not admitted after fatal closure. |
| 16 | Useful, structured, secret-free diagnostics | PASS | Logs include UTC time, severity, operation, path, stage, attempt, fingerprint, and outcome. `AC18_LogsStateErrorsAndRequests_NeverExposeSecret` passed. Fresh evidence scans found zero sentinel-token matches and zero `Authorization: Bearer` matches. |

## Commands and Evidence

Full current Release suite:

```powershell
dotnet test .\TCFUploader.slnx --configuration Release --no-build --no-restore `
  --results-directory .\artifacts\final-e2e-qa-20260929-0543 `
  --logger "trx;LogFileName=full-e2e.trx"
```

Observed:

```text
Passed: 96, Failed: 0, Skipped: 0, Total: 96, Duration: 2m 9s
```

Published process cases were driven by Python `subprocess` with
`CREATE_NEW_PROCESS_GROUP` and `signal.CTRL_BREAK_EVENT`:

```text
help:0, version:0, no_args:2, missing_folder:2, missing_token:2,
empty_monitor_ctrl_break:0
MONITORING_ACTIVE=True
WATCH_FILE_COUNT=0
SECRET_MATCHES=0
AUTHORIZATION_BEARER_MATCHES=0
```

Additional recovery-cycle test:

```powershell
dotnet test .\TCFUploader.slnx --configuration Release --no-restore `
  --filter "Name=PermanentPutFailure_RemainsDurableAndLaterCycleRecovers" `
  --results-directory .\artifacts\final-e2e-qa-20260929-0543 `
  --logger "trx;LogFileName=permanent-recovery.trx"
```

Observed: **1 passed, 0 failed, 0 skipped**. The temporary QA test source was deleted after
execution; production source was never modified.

| Evidence artifact | SHA-256 |
|---|---|
| `artifacts\final-e2e-qa-20260929-0543\full-e2e.trx` | `9AEF73E20F7F2085E10461C602C5E0CDB2F05EF63D8B5DAD718D6F7BB4932D1D` |
| `artifacts\final-e2e-qa-20260929-0543\permanent-recovery.trx` | `C34F30451019307DA351B41C2DB1FAF30F68B6DD033653A41CABAB238D760344` |
| `artifacts\final-e2e-qa-20260929-0543\published-cli-evidence.json` | `4BD9094B93CEF3A852712B9F0366755E5EA8E59283FDE0EAE0F10D2AC30728D3` |

## Defects

**None found.**

No confusing or secret-bearing console diagnostic was observed. Invalid startup cases identified
the invalid input, monitoring and shutdown were visible, capacity failures identified the exact
reason, and stage-specific upload/state failures remained diagnosable.

## Residual Gaps

- **AC-25 live Fotoshare smoke was not run because authorization was not provided.** This QA pass
  does not assert current production availability or undocumented upstream behavior.
- Active-request graceful/forced shutdown and fatal-transition timing were exercised through the
  injected acceptance harness; the direct published-process Ctrl+Break used an empty watch folder
  to guarantee no production network contact.
- POST remains at-least-once if upstream accepts it immediately before local durable completion,
  because the upstream contract provides no idempotency key or status lookup.

STATUS: PASS
SUMMARY: Final local operator QA passed every planned journey using the published CLI and isolated mocked acceptance harness.
WORK_COMPLETED: 96-test Release suite, six published-process cases, one explicit later-cycle recovery test, artifact hashing, and secret scans.
EVIDENCE: 97 automated tests plus six published-process cases passed; evidence paths and hashes are listed above.
ARTIFACTS: `qa-results.md` and `TCFUploader\artifacts\final-e2e-qa-20260929-0543\`.
FINDINGS: No defects.
RISKS: No authorized live upstream smoke; documented at-least-once POST ambiguity remains.
BLOCKERS: None for local/mock release QA.
NEXT_ACTION: Engineering judgment may consume this QA PASS; AC-25 requires separate explicit authorization.
