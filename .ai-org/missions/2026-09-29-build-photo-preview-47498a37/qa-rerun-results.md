# QA RESULT

**Environment:** Windows 11 Enterprise 64-bit, build 26200, interactive desktop session.
The real Release executable was launched directly:
`C:\Users\syedhu\source\repos\Dreamer\TCFPreview\src\TCFPreview.WinForms\bin\Release\net10.0-windows\TCFPreview.WinForms.exe`.
The tested binary was 162,816 bytes, last written `2026-09-30T02:33:45.3258915Z`,
with product version `1.0.0+53befabd6fb32b9d7c9fc26d6cc6a2ce65eccc97`.
PowerShell 7.6.6 in STA mode drove the real WinForms controls and native folder pickers through
Windows UI Automation, foreground keyboard input, and Win32 button messages.

Disposable fixtures contained `01-red.png` and `02-green.png`. The execution ran from
`2026-09-30T02:41:34.2821149Z` through `2026-09-30T02:41:55.7700260Z`
(`September 29, 2026` local time).

## Scenario 1: Deleted destination recovery in the real desktop app [PASS]

- **Actions:** Launch PID 41396; select the two-photo source; navigate Next to
  `02-green.png`, `2 of 2`; select `destination-deleted`; verify Copy is enabled; delete that
  directory externally; click Copy.
- **Expected:** Corrective status; Source Browse and Destination Browse enabled; the valid
  navigation direction enabled; Copy disabled.
- **Actual:** The app displayed `Choose a valid destination folder before copying.` Source Browse
  was enabled, Destination Browse was enabled, Previous was enabled, Next remained correctly
  disabled at the final-item boundary, and Copy was disabled. The deleted destination remained
  absent.
- **Evidence:** Before deletion, the state was `02-green.png`, `2 of 2`, Previous enabled,
  Copy enabled, and both Browse buttons enabled. After the failed Copy attempt, the same photo and
  position remained selected, Previous and both Browse buttons were enabled, and Copy was disabled.
  See `logs\qa-rerun-results.json` and `logs\invalid-destination-recovered.png` under the retained
  evidence folder.

## Scenario 2: Same-process replacement destination, successful copy, and navigation regression [PASS]

- **Actions:** Without restarting PID 41396, use Destination Browse to select
  `destination-replacement`; click Copy; compare source and destination length and SHA-256; click
  Previous and then Next.
- **Expected:** Replacement destination becomes usable, copy succeeds byte-for-byte, and navigation
  still moves between both photos.
- **Actual:** The app enabled Copy after the replacement destination was selected and displayed
  `Copied as 02-green.png.` The source and copied file were both 270 bytes with SHA-256
  `718BCBBD927BBB78488015B74C3FAE83E700463D742F48D764CE4C98065D7EE5`.
  Previous moved to `01-red.png`, `1 of 2`, with Next enabled; Next returned to
  `02-green.png`, `2 of 2`, with Previous enabled.
- **Evidence:** `logs\qa-rerun-results.json` contains all UI states and filesystem measurements.
  `logs\copy-success-and-navigation.png` shows the final real-app state.

## Cleanup

- The exact app PID 41396 exited cleanly.
- No other `TCFPreview.WinForms` process remained.
- The exact disposable `work` fixture tree, including source and destination directories, was
  removed.
- Retained evidence:
  `C:\Users\syedhu\.copilot\session-state\47498a37-d823-488b-b8c2-0f953510d910\files\tcfpreview-rerun-20260929\logs`.

**Scenarios:** 2 run, 2 passed, 0 failed.

**Conclusion: PASS.** The previously failing invalid-destination recovery works in the actual
Windows desktop application, and the user can select a new destination without restarting, copy
successfully with byte-identical output, and continue navigating photos.

---

**STATUS:** PASS  
**SUMMARY:** Real Windows desktop rerun verified the destination-recovery fix and primary journey.  
**WORK_COMPLETED:** Executed the exact prior failure sequence, recovered in the same process,
completed a verified copy, exercised Previous/Next, and cleaned the process and fixtures.  
**EVIDENCE:** This report, retained JSON state/file proof, and two real-window screenshots.  
**ARTIFACTS:** `.ai-org\missions\2026-09-29-build-photo-preview-47498a37\qa-rerun-results.md`  
**FINDINGS:** No blocking or usability regression observed in the requested scope.  
**RISKS:** This concise rerun does not repeat the other ten previously passing scenarios.  
**BLOCKERS:** None.  
**NEXT_ACTION:** Release gate may treat the formerly failing recovery scenario as passed.
