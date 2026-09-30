# QA RESULT

**Environment:** Microsoft Windows 11 Enterprise 64-bit, version 10.0.26200
(build 26200), interactive desktop session 1, .NET SDK 10.0.401. The Release executable
`C:\Users\syedhu\source\repos\Dreamer\TCFPreview\src\TCFPreview.WinForms\bin\Release\net10.0-windows\TCFPreview.WinForms.exe`
was run directly. PowerShell 7 in STA mode drove the real WinForms controls and native folder
pickers through Windows UI Automation, foreground keyboard/mouse input, and Win32 button messages.
No production code, mocks, real credentials, or repository source data were used.

Disposable fixture root:
`C:\Users\syedhu\.copilot\session-state\47498a37-d823-488b-b8c2-0f953510d910\files\tcfpreview-qa-20260929`.
The final evidence run is recorded in `logs\qa-results.json`; state snapshots and screenshots are
in the same `logs` folder.

## Scenario 1: Startup [PASS]

- **Actions:** Launch the Release executable and inspect the initial form through UI Automation.
- **Expected:** Initial guidance, no selected photo, `0 of 0`, and disabled Previous/Next/Copy.
- **Actual:** PID 56332 opened `TCF Photo Preview`; status was
  `Choose a source folder to begin.`, filename was `No photo selected`, position was `0 of 0`,
  and Previous/Next/Copy were disabled.
- **Evidence:** `logs\scenario-01-startup.json`, `logs\scenario-01-startup.png`.

## Scenario 2: Source selection, first preview, and navigation boundaries [PASS]

- **Actions:** Use Source Browse to select `source-main` containing `01-red.png`,
  `02-green.jpg`, and `03-blue.bmp`; inspect the first preview; navigate Next twice and Previous
  once.
- **Expected:** Deterministic filename order, first preview/name/`1 of 3`, and disabled navigation
  at each boundary.
- **Actual:** `01-red.png` loaded as `1 of 3`; the preview center sampled RGB `255,0,0`.
  Navigation reached `03-blue.bmp`, `3 of 3`, with Next disabled, then returned to
  `02-green.jpg`, `2 of 3`.
- **Evidence:** `logs\scenario-02-navigation.json`,
  `logs\scenario-02-navigation-middle.png`.

## Scenario 3: Destination selection and byte-identical copy [PASS]

- **Actions:** Use Destination Browse to select `destination`; click Copy for
  `02-green.jpg`; compare source and destination SHA-256.
- **Expected:** Original filename retained and copied bytes identical.
- **Actual:** `destination\02-green.jpg` was created. Source and destination SHA-256 were both
  `3E95C2B2987B0F5C523C9575B66F23013DFFDB32E9FCD13BE33546BD8C8E1B94`.
- **Evidence:** `logs\scenario-03-copy.json`.

## Scenario 4: Repeated copy collision [PASS]

- **Actions:** Click Copy again for the same current photo; hash the original and numbered copy.
- **Expected:** No overwrite; create `02-green (1).jpg`.
- **Actual:** `02-green (1).jpg` was created. The original hash was unchanged and the numbered
  copy matched the source hash.
- **Evidence:** `logs\scenario-04-collision.json`.

## Scenario 5: Cancel folder picker preserves state [PASS]

- **Actions:** Open and cancel Source Browse, then open and cancel Destination Browse.
- **Expected:** Source, destination, current photo, and position remain unchanged.
- **Actual:** State remained `02-green.jpg`, `2 of 3`, with the same source and destination paths.
- **Evidence:** `logs\scenario-05-picker-cancel.json`.

## Scenario 6: Corrupt supported file and recovery [PASS]

- **Actions:** Select `source-corrupt`; navigate from `01-good.png` to
  `02-corrupt.png`, then to `03-good.bmp`.
- **Expected:** Corrupt file remains in the catalog with a visible error; navigation to a valid
  image recovers.
- **Actual:** At `2 of 3`, the corrupt file showed
  `Preview unavailable: Parameter is not valid.` Next remained enabled, and navigation to
  `03-good.bmp` restored `Photo ready.`.
- **Evidence:** `logs\scenario-06-corrupt-recovery.json`,
  `logs\scenario-06-recovered.png`.

## Scenario 7: Empty source folder [PASS]

- **Actions:** Select `source-empty`.
- **Expected:** No-photo state, visible empty-folder status, and disabled Previous/Next/Copy.
- **Actual:** The app showed `No photo selected`, `0 of 0`, and
  `No supported photos were found in this folder.` Previous/Next/Copy were disabled.
- **Evidence:** `logs\scenario-07-empty.json`, `logs\scenario-07-empty.png`.

## Scenario 8: Deleted current source [PASS]

- **Actions:** Select `source-deleted`; delete current `01-current.png` externally; navigate to
  `02-other.bmp` and back; click Copy on the deleted catalog entry.
- **Expected:** Visible preview and copy failures without losing navigation context.
- **Actual:** Returning to `01-current.png`, `1 of 2`, showed a visible
  `Preview unavailable: Could not find file ...` error. Copy then showed
  `Copy failed: Source photo does not exist.` Next remained available.
- **Evidence:** `logs\scenario-08-deleted-current.json`.

## Scenario 9: Destination removed before Copy [FAIL]

- **Actions:** Select `source-main` and `destination-missing`; delete the destination directory
  externally while Copy is enabled; click Copy.
- **Expected:** Visible corrective error, disabled Copy, and enabled Browse controls so the user can
  choose a replacement destination.
- **Actual:** The status correctly changed to
  `Choose a valid destination folder before copying.` and no output was created, but Source Browse,
  Destination Browse, Previous, and Next all remained disabled. The user cannot recover or select a
  valid destination without restarting the application.
- **Evidence:** `logs\scenario-09-missing-destination.json`. Before the attempt both Browse controls
  were enabled; afterward both were false while the destination did not exist.
- **Root cause:** In `src\TCFPreview.WinForms\MainForm.cs:126-130`, the invalid-destination early
  return applies the view while `_copyInProgress` is still true, calls `CompleteCopyOperation`, and
  returns without the post-completion `ApplyPresentation` performed by the normal `finally` path at
  lines 152-159. The visible control state remains permanently copy-in-progress.

## Scenario 10: Accessibility properties and live status [PASS WITH FINDING]

- **Actions:** Inspect the running form through Windows UI Automation and query the status live
  setting.
- **Expected:** Meaningful accessible names and inspectable live status where exposed by the UIA
  provider.
- **Actual:** UIA exposed `Source:`, `Destination:`, and `Status` names and the current native status
  text. The installed UIA client/provider returned `Unsupported Property` for LiveSetting, so the
  configured polite live-region value could not be independently observed.
- **Evidence:** `logs\scenario-10-accessibility.json`.

## Scenario 11: Close during large copy [PASS]

- **Actions:** In a fresh app instance, select a valid 80x60 PNG containing a 1 GiB standards-valid
  ancillary chunk (`1,073,742,092` bytes), select `destination-large`, click Copy, wait for the
  staging file to grow, then close the window and poll the process and destination.
- **Expected:** Close waits for cancellation cleanup; no final or `.tcfpreview-*.tmp` artifact
  remains.
- **Actual:** Close was requested with
  `.tcfpreview-252f258a71ae40ed9d3d2d6e38cec72d.tmp` at `17,039,360` bytes. At 2 ms after
  close the process and staging file still existed; by 53 ms the file was removed while the process
  remained alive; the process exited at 146 ms with zero destination files. No artifact was ever
  observed after process exit.
- **Evidence:** `logs\scenario-11-close-during-copy.json`.

## Scenario 12: README launch steps [PASS]

- **Actions:** From `C:\Users\syedhu\source\repos\Dreamer\TCFPreview`, run
  `dotnet run --project .\src\TCFPreview.WinForms\TCFPreview.WinForms.csproj -c Release`; inspect
  startup; close the exact app and dotnet host processes.
- **Expected:** The documented command launches the Release app successfully.
- **Actual:** Dotnet host PID 24312 launched app PID 64692 with the expected initial status and
  control state.
- **Evidence:** `logs\scenario-12-readme-launch.json`,
  `logs\scenario-12-readme-launch.png`, `logs\scenario-12-dotnet-run.stdout.txt`,
  `logs\scenario-12-dotnet-run.stderr.txt`.

## Usability findings

1. **Blocking recovery defect:** A destination that disappears before Copy leaves every navigation
   and Browse control disabled. Restart is the only recovery.
2. **Ambiguous corrupt-image text:** `Preview unavailable: Parameter is not valid.` is visible but
   does not identify the file as corrupt or unreadable.
3. **Accessibility discoverability:** Both folder buttons expose the same UIA name, `Browse...`;
   automation and assistive tooling must infer Source versus Destination from control order.
4. **Live-region verification gap:** Accessible names and status text are exposed, but this Windows
   UIA provider did not expose the configured LiveSetting property.

## Summary

**Scenarios:** 12 run, 11 passed, 1 failed.

**Conclusion: FAIL.** The primary browse, preview, navigation, copy, collision, corrupt/deleted
source recovery, empty-folder, cancellation cleanup, accessibility-name, and README launch journeys
were exercised successfully. Release QA cannot pass because the missing-destination recovery flow
leaves the application unusable until restart.

---

**STATUS:** FAIL  
**SUMMARY:** Real Windows desktop QA found one reproducible blocking recovery defect.  
**WORK_COMPLETED:** Executed all required scenarios against the real Release WinForms executable,
native folder pickers, and local filesystem.  
**EVIDENCE:** This report and the retained session `logs` directory.  
**ARTIFACTS:** `.ai-org\missions\2026-09-29-build-photo-preview-47498a37\qa-results.md`  
**FINDINGS:** Missing-destination copy attempt permanently disables Browse/navigation; three
additional usability/accessibility findings are listed above.  
**RISKS:** Users can become trapped after a destination is removed or becomes unavailable between
selection and Copy.  
**BLOCKERS:** Fix the invalid-destination early-return presentation state and rerun scenarios 9,
10, and the primary copy regression.  
**NEXT_ACTION:** Reapply the view after `_copyInProgress` is cleared on the invalid-destination
early return, then independently rerun the failed recovery scenario and affected regressions.
