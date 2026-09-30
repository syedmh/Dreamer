# TCFPreview Final Independent Test Gate

## Verdict

PASS. Release restore and build succeeded with zero warnings and errors. The focused
lifecycle, copy/publication, and image-loading suites passed 21/21. The complete 37-test
suite then passed 10 consecutive times under method-level parallel execution: 370 passed,
0 failed, 0 skipped.

## Commands and counts

### Release restore and build

```powershell
dotnet restore .\TCFPreview.slnx -p:Configuration=Release
dotnet build .\TCFPreview.slnx -c Release --no-restore
```

```text
All projects are up-to-date for restore.
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### Focused close lifecycle

```powershell
dotnet test .\tests\TCFPreview.Tests\TCFPreview.Tests.csproj -c Release --no-build --filter "Name~MainForm_CloseDuringCopy|Name~MainForm_DisposedDuringCopy" --logger "console;verbosity=normal"
```

```text
Total tests: 2
     Passed: 2
 Total time: 0.5934 Seconds
```

Passed: 2. Failed: 0. Skipped: 0.

### Focused copy, cancellation, cleanup, and publication

```powershell
dotnet test .\tests\TCFPreview.Tests\TCFPreview.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~TCFPreview.Tests.PhotoCopierTests" --logger "console;verbosity=normal"
```

```text
Total tests: 11
     Passed: 11
 Total time: 0.4572 Seconds
```

Passed: 11. Failed: 0. Skipped: 0.

### Focused detached image loading

```powershell
dotnet test .\tests\TCFPreview.Tests\TCFPreview.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~TCFPreview.Tests.DetachedImageLoaderTests" --logger "console;verbosity=normal"
```

```text
Total tests: 8
     Passed: 8
 Total time: 0.5316 Seconds
```

Passed: 8. Failed: 0. Skipped: 0.

### Ten consecutive complete-suite runs

```powershell
$passed = 0
for ($i = 1; $i -le 10; $i++) {
    dotnet test .\tests\TCFPreview.Tests\TCFPreview.Tests.csproj -c Release --no-build --logger "console;verbosity=minimal"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $passed++
}
```

Each run reported:

```text
Failed: 0, Passed: 37, Skipped: 0, Total: 37
```

Aggregate: 10/10 consecutive runs passed; 370 passed, 0 failed, 0 skipped.

## Focused evidence

### Close during copy is owned through cancellation and cleanup

`MainForm_CloseDuringCopyCancelsAndWaitsForCleanupBeforeClosing` verifies that closing:

- cancels the active copy token;
- cancels the initial close request;
- keeps the form undisposed while copy cleanup is blocked;
- exposes an incomplete deferred-close task during cleanup; and
- closes only after the active copy and cleanup have completed.

`MainForm_DisposedDuringCopyDoesNotUpdateDisposedControls` verifies completion cannot race a
disposed form and update dead controls.

### Cancellation and publication artifact ownership

`CopyAsync_CancellationRemovesPartialDestinationAndStagingFiles` writes partial staging data,
cancels, and asserts `Directory.GetFiles(destination.Path)` is empty before the temporary
directory is disposed. Therefore neither a final photo nor a `.tcfpreview-*.tmp` artifact
remains.

`CopyAsync_CopyFailureRemovesPartialDestinationAndStagingFiles` makes the same zero-artifact
assertion after a copy failure. Successful copy and collision tests separately assert no
`.tcfpreview-*.tmp` files remain.

`CopyAsync_SuccessfulPublicationDoesNotAttemptStagingCleanup` injects a deletion action that
fails the test if called after publication. `CopyAsync_ReusedStagingPathAfterPublicationIsNotDeleted`
recreates the old staging path after the move and proves the replacement file remains. Together
these tests demonstrate that cleanup stops owning the staging path immediately after successful
publication.

### Parallel BMP encoder flake

The assembly remains configured for method-level parallelization with 16 workers, while
`DetachedImageLoaderTests` is marked `[DoNotParallelize]`. Its eight format/unlock cases passed
in the focused run, including both BMP tests. The full suite passed 10 consecutive parallelized
runs with no encoder failure: observed flake rate 0/10 runs and 0/370 test executions.

## Residual gaps

- The close lifecycle test uses an injected copy delegate while `PhotoCopier` cancellation cleanup
  is tested independently. This proves both sides of the contract but is not a single UI-to-real-
  filesystem integration test.
- No interactive desktop automation was run; this gate covers the automated Release test contract.
- No production or test source was modified by this gate. Only this mission evidence file changed.

## Invalid-destination recovery revalidation

### Verdict

PASS. The invalid-destination regression and affected copy lifecycle/accessibility tests passed
5/5. The Release build succeeded with zero warnings and errors. The complete 38-test suite passed
three consecutive times: 114 passed, 0 failed, 0 skipped.

### Commands and counts

```powershell
dotnet build .\TCFPreview.slnx -c Release --nologo
```

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

```powershell
dotnet test .\tests\TCFPreview.Tests\TCFPreview.Tests.csproj -c Release --no-build --nologo --filter "Name=MainForm_InvalidDestinationRestoresBrowseAndNavigationControls|Name=MainForm_CloseDuringCopyCancelsAndWaitsForCleanupBeforeClosing|Name=MainForm_DisposedDuringCopyDoesNotUpdateDisposedControls|Name=MainForm_StatusLabelIsNamedPoliteLiveRegion|Name=UpdateStatusLabel_WhenTextChangesNotifiesLiveRegionOnce" --logger "console;verbosity=normal"
```

```text
Test Run Successful.
Total tests: 5
     Passed: 5
 Total time: 0.5642 Seconds
```

```powershell
$runs = 3
for ($i = 1; $i -le $runs; $i++) {
    dotnet test .\tests\TCFPreview.Tests\TCFPreview.Tests.csproj -c Release --no-build --nologo --logger "console;verbosity=minimal"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
```

```text
Run 1: Failed: 0, Passed: 38, Skipped: 0, Total: 38
Run 2: Failed: 0, Passed: 38, Skipped: 0, Total: 38
Run 3: Failed: 0, Passed: 38, Skipped: 0, Total: 38
```

### Focused proof

`MainForm_InvalidDestinationRestoresBrowseAndNavigationControls` deletes the selected destination
after the form has a current photo and enabled Copy state, then invokes the real
`MainForm.CopyCurrentPhotoAsync` control-state path. It proves:

- the copy delegate is not called;
- status remains `Choose a valid destination folder before copying.`;
- Source Browse and Destination Browse recover to enabled;
- valid Next navigation recovers to enabled;
- boundary Previous remains disabled; and
- Copy remains disabled because the destination is invalid.

The implementation clears `_copyInProgress` before applying
`MainFormPresentation.InvalidDestinationCopyStatus`, so the presentation is recomputed from the
non-busy state while retaining the corrective status.

The focused slice also passed close cancellation/cleanup, disposed-control race protection,
polite live-region configuration, and one-notification-per-status-change behavior.

### Residual gaps

- This revalidation did not repeat the real desktop UI Automation scenario that originally found
  the defect; proof is through the WinForms form instance and real control properties.
- The prior UIA provider gap remains: the native status text is observable, but that environment
  returned `Unsupported Property` for LiveSetting.
