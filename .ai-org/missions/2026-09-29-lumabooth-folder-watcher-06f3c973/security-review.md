# Final Defensive Security Gate

Date: 2026-09-29

## Verdict

**PASS** — zero unresolved Critical or High findings.

Scope: current filesystem tree under `TCFUploader`, with emphasis on storage-fatal admission,
credentialed network attempt suppression, iterative traversal, resource bounds, credential
handling, path/state integrity, and dependency exposure.

| Severity | Count | Finding |
|---|---:|---|
| Critical | 0 | None |
| High | 0 | None |
| Medium | 1 | Completed fingerprints are retained indefinitely to preserve restart dedupe. |
| Low | 1 | Event POST can be repeated after ambiguous upstream success and local persistence failure. |
| Informational | 1 | Package advisory results are limited to the configured NuGet sources. |

## Findings

### [MEDIUM] Completed-state growth remains unbounded

Location: `TCFUploader/src/TCFUploader/State/StateRepository.cs:306`,
`TCFUploader/src/TCFUploader/State/StateRepository.cs:368-370`

Issue: New fingerprints are appended to `state.Items`; completion changes status but does not
remove or compact the durable fingerprint record.

Attack path: A local actor or continuously producing source can create many unique stable file
versions under the watched root -> each version is durably added -> completed records accumulate
in the state snapshot. Existing queue, candidate, spool, journal, and response bounds do not bound
the total completed-item index.

Impact: Long-running deployments can experience increasing state-file size, startup validation
work, persistence cost, and local disk consumption. This is an availability/resource risk, not a
credential disclosure or network authorization bypass.

Fix: Introduce a bounded segmented durable dedupe index or equivalent compaction design before
deleting completed fingerprints. Do not remove records without preserving restart dedupe.

Confidence: High

### [LOW] Ambiguous POST success can produce a duplicate event

Location: `TCFUploader/src/TCFUploader/Upload/UploadWorker.cs:75-103`

Issue: The event API has no idempotency key or status lookup. If POST succeeds upstream but local
completion persistence fails, restart can repeat POST.

Attack path: Credentialed POST reaches the fixed Fotoshare endpoint -> upstream accepts it ->
response or local completion persistence is lost -> durable state remains `PutComplete` -> restart
retries POST.

Impact: Duplicate event registration for one file version. The path does not expose the bearer
token or permit requests to an attacker-controlled host.

Fix: Use an upstream idempotency key/status lookup if the service adds one; otherwise preserve the
documented at-least-once behavior.

Confidence: High

## Closure Evidence

- **Storage-fatal admission is globally fail-closed.** `WatcherCoordinator` creates one shared
  `AttemptAdmission` at `Hosting/WatcherCoordinator.cs:108`, passes it into the worker at
  `Hosting/WatcherCoordinator.cs:121-125`, and stops it on worker/storage failures at
  `Hosting/WatcherCoordinator.cs:131-149`, snapshot storage failure at
  `Hosting/WatcherCoordinator.cs:243`, duplicate cleanup failure at
  `Hosting/WatcherCoordinator.cs:275`, and pending-state persistence failure at
  `Hosting/WatcherCoordinator.cs:294`.
- **No later credentialed operation can cross the closed gate.** `AttemptAdmission.TryStart` and
  `Stop` use the same lock at `Upload/AttemptAdmission.cs:17-29,48-55`. Both PUT and POST enter
  through that gate at `Upload/RetryPolicy.cs:29-36`; retry delay is linked to the same stopped
  token at `Upload/RetryPolicy.cs:53`. An operation already linearized before closure may finish,
  but queued PUT, subsequent POST, and retry attempts cannot start after closure.
- **Race regressions prove the required behavior.**
  `Hosting/WatcherCoordinatorTests.cs:561`, `:567`, and `:573` cover duplicate cleanup I/O,
  duplicate cleanup access denial, and pending-state persistence failure. Each asserts one
  already-admitted request and no queued request. `:435` covers completed-spool cleanup failure,
  durable completion, and suppression of later network work.
- **Recursive stack exhaustion is closed.** Traversal uses `Stack<DirectoryFrame>` at
  `Discovery/Reconciler.cs:118`, defaults to a maximum depth of 1,024 at `:11`, skips children when
  `frame.Depth >= maxTraversalDepth` at `:174`, disposes frames at `:132`, `:145`, and `:201`, and
  checks cancellation during traversal at `:94` and `:124`. Skipped diagnostic samples are capped
  at 64 per batch at `:49`.
- **Depth behavior is regression-tested.** `Discovery/ReconcilerTests.cs:21` proves deep supported
  traversal without call-stack recursion; `:41` proves over-depth branches are skipped while a
  sibling remains discoverable.
- **Credential and endpoint controls remain intact.** Redirects are disabled at
  `Hosting/WatcherCoordinator.cs:66`; bearer credentials are attached only after fixed HTTPS,
  default-port, exact-host validation at `Upload/LumaBoothClient.cs:26-28,65-67,116-118`; JSON
  response size is bounded at `:109`; token input is bounded to 16 KiB at
  `Cli/TokenSource.cs:13,33,45`. Tests at `Upload/LumaBoothClientTests.cs:51` and
  `Integration/SecurityBoundaryTests.cs:18` verify URL allowlisting and secret-free logs/state.
- **State, path, and resource controls remain intact.** Private ACL and pinned storage identity are
  rechecked throughout mutations at `State/StateRepository.cs:301,322,343,363,382,393,456,522,594`
  and centrally at `:756-761`. Upload and change queues are bounded at
  `Upload/UploadScheduler.cs:12` and `Hosting/WatcherCoordinator.cs:76`; candidate tracking,
  spool bytes/free-space reserve, JSON response size, journal bytes/records, and journal record
  size retain explicit bounds in `Configuration/RuntimeOptions.cs`.

## Executed Checks

```text
dotnet restore .\TCFUploader.slnx --locked-mode
Result: exit 0; all projects up to date.

dotnet build .\TCFUploader.slnx --configuration Release --no-restore
Result: exit 0; 0 warnings, 0 errors.

dotnet test .\tests\TCFUploader.Tests\TCFUploader.Tests.csproj --configuration Release
  --no-build --no-restore --filter <9 security/storage/traversal tests>
Result: 9 passed, 0 failed, 0 skipped.

dotnet test .\TCFUploader.slnx --configuration Release --no-build --no-restore
Result: 94 passed, 0 failed, 0 skipped.

dotnet list .\src\TCFUploader\TCFUploader.csproj package
  --include-transitive --vulnerable --no-restore
Result: no vulnerable production packages reported by configured sources.

dotnet list .\tests\TCFUploader.Tests\TCFUploader.Tests.csproj package
  --include-transitive --vulnerable --no-restore
Result: no vulnerable test packages reported by configured sources.
```

No live credentials, production endpoints, or third-party systems were used.

## Residual Risks

- Completed-state growth remains the single Medium availability risk and is intentionally retained
  until dedupe-safe compaction exists.
- POST remains at-least-once after ambiguous upstream acceptance; this is a Low integrity risk.
- NuGet vulnerability coverage is limited to the configured package sources.
- `TCFUploader` remains untracked by the enclosing repository, so this gate validates the current
  filesystem tree rather than a committed diff.

SECURITY RESULT

Scope: Storage-fatal admission, traversal, credentials, path/state integrity, resource bounds,
dependencies, and targeted regressions.

Critical: 0   High: 0   Medium: 1   Low: 1   Informational: 1

Blocking findings: None

Conclusion: PASS

STATUS: PASS
SUMMARY: Zero unresolved Critical or High findings; storage-fatal admission and recursive traversal
remediations are verified.
WORK_COMPLETED: Static threat-boundary review plus local Release build, focused security tests, full
suite, and production/test dependency audits.
EVIDENCE: Source locations and command results recorded above.
ARTIFACTS: `.ai-org/missions/2026-09-29-lumabooth-folder-watcher-06f3c973/security-review.md`
FINDINGS: One Medium completed-state growth risk; one Low at-least-once POST risk.
RISKS: Long-term state growth, ambiguous-success duplicate POST, configured-source advisory scope.
BLOCKERS: None.
NEXT_ACTION: Preserve the residual-risk record and proceed to final mission judgment.
