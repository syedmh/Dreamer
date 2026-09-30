# Independent Final Test Verification

Date: 2026-09-29

## Verdict

**PASS**

The final remediation was independently verified from the current filesystem tree. No production
files were edited, no live upstream request or token was used, and no commit or push was performed.

## Commands and Results

```powershell
dotnet restore .\TCFUploader.slnx --locked-mode
```

- Exit 0; all projects were up to date.

```powershell
dotnet build .\TCFUploader.slnx --configuration Release --no-restore
```

- Exit 0; 0 warnings, 0 errors.

```powershell
dotnet test .\TCFUploader.slnx --configuration Release --no-build --no-restore `
  --filter "<nine exact admission/storage/traversal regression names>"
```

- 9 passed, 0 failed, 0 skipped.
- Repeated 10 times: 90 passed executions, 0 failed, 0 skipped.

```powershell
dotnet test .\TCFUploader.slnx --configuration Release --no-build --no-restore
```

- 94 passed, 0 failed, 0 skipped.

```powershell
dotnet publish .\src\TCFUploader\TCFUploader.csproj `
  --configuration Release --runtime win-x64 --self-contained false `
  --no-restore `
  --output .\artifacts\independent-final-verification-20260929-034451\publish-win-x64
```

- Exit 0.
- Exactly five production files.
- No testhost, MSTest, or `TCFUploader.Tests` payload.
- Published and ZIP-extracted executables both returned help successfully.
- All five extracted payload SHA-256 hashes matched the published files.

```powershell
dotnet list .\src\TCFUploader\TCFUploader.csproj package `
  --include-transitive --vulnerable --no-restore
```

- Exit 0; no vulnerable packages reported by the configured sources.

## Focused Evidence

- `PendingStatePersistenceFailure_ClosesAdmissionBeforeQueuedHttpStarts`: an admitted PUT completes
  and persists `PutComplete`; queued and newly discovered work remain `PendingPut`; request count
  stays at one after the storage fatal.
- `DuplicateCleanupIoFailure_ClosesAdmissionBeforeQueuedHttpStarts` and
  `DuplicateCleanupAccessFailure_ClosesAdmissionBeforeQueuedHttpStarts`: the admitted PUT settles,
  while queued PUT and follow-on POST do not start; request count remains one.
- `CompletedSpoolDeletionFailure_PreservesCompletionAndStopsFurtherNetworkWork` and
  `CompletedSpoolIdentityDrift_PreservesCompletionAndReturnsSecretFreeFatal`: completed state is
  retained and further network work is stopped.
- `FirstSignal_BeforePutAttemptAdmission_StartsNoRequestAndRemainsPendingPut` and
  `FirstSignal_BeforePostAttemptAdmission_StartsNoRequestAndRemainsPutComplete`: operations losing
  admission start zero HTTP requests and preserve resumable state.
- `IterativeScan_FindsFileAtDeepSupportedDepthWithoutCallStackRecursion`: a 240-level tree is
  traversed iteratively.
- `IterativeScan_DepthLimitSkipsDeeperTreeAndContinuesWithSibling`: configured depth cap is enforced,
  the deeper child is reported skipped, and sibling traversal continues.

Source inspection also confirmed that `AttemptAdmission.TryStart` and `Stop` share one lock,
storage-fatal paths call `attemptAdmission.Stop()` before `intakeCts.Cancel()`, PUT and POST retries
share that admission object, and the default reconciler traversal cap is 1,024.

## Defects

None found.

## Residual Gaps

- Per constraint, no live Fotoshare token or upstream contract request was used.
- Windows path-length constraints prevent constructing a literal 1,024-level filesystem tree in
  this environment. The iterative behavior was executed at 240 levels, cap behavior was executed
  with a reduced injected limit, and the production default of 1,024 was verified by source
  inspection. This is not blocking for the requested remediation.
- `TCFUploader` remains untracked by the enclosing repository, so this verdict applies to the
  current filesystem tree rather than a committed diff.

## Evidence Artifacts

- `artifacts\independent-final-verification-20260929-034451\focused-final.trx`
  - SHA-256: `D67E2A747396360125AB998F017D4C38A942EF3663712F667C6B980076F93BB1`
- `artifacts\independent-final-verification-20260929-034451\full-release-final.trx`
  - SHA-256: `D8C2B4386B964AE87224BF1C0ED9CFDEFDA7074F173868A9F6FA566C7557539E`
- `artifacts\independent-final-verification-20260929-034451\TCFUploader-win-x64.zip`
  - SHA-256: `082D4D4B16630697E386B01DF97B26F83EF5F50BB5DEEF05E2DA8ACC4EB13550`

STATUS: PASS
SUMMARY: Final storage-fatal admission and iterative traversal remediation independently verified.
WORK_COMPLETED: Release restore/build/full test/publish/package audit; focused race and traversal tests; 10-run focused repetition.
EVIDENCE: 94/94 full suite; 9/9 focused suite; 90/90 repeated focused executions; clean publish and matching ZIP.
ARTIFACTS: This report and the evidence files listed above.
FINDINGS: No defects or flakes found.
RISKS: No live upstream validation; exact 1,024-level physical tree not constructible on this Windows path.
BLOCKERS: None.
NEXT_ACTION: Final mission judgment may accept this test gate.
