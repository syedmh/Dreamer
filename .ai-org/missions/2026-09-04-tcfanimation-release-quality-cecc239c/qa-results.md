# TCFAnimation Independent Foreground E2E QA Re-run

Date: 2026-09-04  
Role: independent `cto-engineering-org:qa-engineer`  
Release executable:
`C:\Users\syedhu\source\repos\Dreamer\TCFAnimation\Build\TCFAnimation.exe`

## QA RESULT

Environment: Windows console session 1, active/unlocked; Windows_NT; primary
logical display 1536x960 at 125% DPI (1920x1200 physical pixels); Godot
4.5.1 Mono/OpenGL 3.3 on NVIDIA RTX PRO 1000 Blackwell Generation Laptop
GPU. The suite used real foreground Win32 `SendInput` physical scan codes,
verified PID/HWND focus, clipboard `CF_UNICODETEXT`, per-monitor-v2 DPI-aware
physical client coordinates, DWM composition synchronization, and real
desktop pixel captures. No mocks, production code changes, real credentials,
or customer data were used.

Qualifying command:

```powershell
$env:PYTHONIOENCODING='utf-8'
python -B C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\run-final-foreground-e2e-fixed.py
```

Qualifying result:

```text
FOREGROUND_ASSERTIONS_PASS assertions=51 passed=51 failed=0 screenshots=85 unique_hashes=48 processes=12
FOREGROUND_E2E_PASS screenshots=85 focus_checks=104 focus_all=true processes=12
exit=0
```

### Scenario 1: Release integrity before execution — PASS

Steps:

```powershell
Get-FileHash -Algorithm SHA256 C:\Users\syedhu\source\repos\Dreamer\TCFAnimation\Build\TCFAnimation.exe
```

Expected: `6C1EEA789E89897BECFCD6A90A5ABDB4B591E162EF1C1E780FDAD82E3B83C96F`.

Actual: exact expected SHA-256; executable size 100,185,624 bytes.

Evidence: release assertion in
`evidence\qa-final\qa-foreground-fixed\foreground-e2e-results.json`.

### Scenario 2: Directional movement, edges, and reversal — PASS

Steps: launch the exported release; hold/release Left and Right; reverse
direction; hold both arrows; capture the physical client after composition.

Expected: visible turn/walk progression, calibrated edge arrival, front
return, reversal, and neutral both-held behavior.

Actual:

```text
idle centroid x=640.58
left-walk centroid x=482.89
left-edge centroid x=152.58
right-edge centroid x=1130.07
directional captures=10, unique hashes=7
```

Evidence:
`runtime\directional-*.png`; assertions
`directional.left-traversal-and-edge`,
`directional.left-release-returns-front-at-edge`,
`directional.reversal-and-both-held-neutral`, and
`directional.right-traversal-and-edge`.

### Scenario 3: Speed boundaries and W regression — PASS

Steps: use Numpad `-` to reach 0.25x, traverse for 1.4 seconds; use Numpad
`+` to reach 3.00x, traverse for 1.4 seconds; press W at rest.

Expected: exact speed logs, maximum-speed travel greater than four times
minimum-speed travel, and W is a visual no-op.

Actual:

```text
minimum=0.25x, 60px/s, 1.5 FPS
maximum=3.00x, 720px/s, 18 FPS
minimum displacement=49.52 px
maximum displacement=546.30 px
ratio=11.03
W changed_pixels=0, max_channel_delta=0
```

Evidence: `runtime\speed-*.png`, `runtime\w-noop-*.png`, and captured process
stdout in `foreground-e2e-results.json`.

### Scenario 4: Exact clap sequence and interruption — PASS

Steps: press C using a physical scan code; retain the first rendered frame
with a pre-flush/held-key synchronization inside its 125 ms interval; sample
the animation faster than 8 FPS; classify rendered silhouettes; interrupt
with Right and retry C while moving.

Expected:
`0,1,2,3,4,3,2,1,2,3,4,3,2,1,0`, then front return; Right interrupts;
C is ignored while moving.

Actual:

```text
classified order=0,1,2,3,4,3,2,1,2,3,4,3,2,1,0
classification IoU scores=0.98403..0.98691
clap samples=18, unique hashes=6
```

Evidence: `runtime\clap-sample-*.png`,
`runtime\clap-direction-interrupted.png`; assertions
`clap.exact-15-step-asset-order` and
`clap.return-and-directional-interruption`.

### Scenario 5: Cross-arm hold, release, locks, and fresh input — PASS

Steps: press X; capture exact entry frames; hold; attempt C and held Left;
press X again; capture the six release frames; retain Left through release;
release and freshly press Left.

Expected: exact entry `0,1,2`; stable held frame; C/Left blocked; exact
release `0,1,2,3,4,5`; held arrow not queued; fresh Left moves.

Actual:

```text
entry frames=0,1,2; scores=0.98481..0.98828
release frames=0,1,2,3,4,5; scores=0.97992..0.98574
held blocked-input changed_pixels=0
cross entry unique hashes=3/3
cross release unique hashes=6/6
fresh Left centroid moved 640.58 -> 574.64
```

Evidence: `runtime\cross-*.png`; all four `cross.*` assertions.

### Scenario 6: Dialogue input, focus, submission, suppression, and hide — PASS

Steps: press Enter to open the real LineEdit; paste and copy
`ASCII w/W | العربية | 😀🧪`; type P; restore the exact text; attempt C, X,
Right, Numpad `-`, and Numpad `+`; press plain Enter to submit; submit
whitespace; cancel replacement text with Escape; press P outside editing.

Expected: editor opens and owns focus; exact Unicode text round-trips and is
visibly rendered; P is typeable during editing; gameplay/speed/P-hide actions
are suppressed; plain Enter submits; whitespace and cancel preserve the
bubble; P outside editing hides it.

Actual:

```text
clipboard round-trip=ASCII w/W | العربية | 😀🧪
P while editing copied back as "p"
input text changed 6,090 rendered pixels
character bbox before/after blocked controls=[538,150,202,551]
no WALK_SPEED log while editing
plain Enter produced bubble bbox=[457,62,366,55]
whitespace and cancelled replacement changed_pixels=0
P outside editing removed the bubble
```

Evidence:
`runtime\dialogue-empty-input.png`,
`runtime\dialogue-exact-input.png`,
`runtime\dialogue-controls-after.png`,
`runtime\dialogue-exact-submitted.png`,
`runtime\dialogue-whitespace-submitted.png`,
`runtime\dialogue-cancelled.png`, and
`runtime\dialogue-hidden-p.png`.

### Scenario 7: Unicode scalar 500/501 boundary — PASS

Steps: paste/copy/submit 500 grinning-face Unicode scalars; then paste
500 grinning faces plus one test-tube scalar.

Expected: 500 accepted; the 501st scalar rejected without splitting UTF-16;
submitted rendering is identical.

Actual:

```text
500 input scalars=500, exact value=true
501 input scalars after policy=500
final test-tube absent=true
500-vs-501 submitted changed_pixels=0
```

Evidence: `runtime\dialogue-500-*.png`,
`runtime\dialogue-501-*.png`; assertion
`dialogue.500-and-501-unicode-scalar-policy`.

### Scenario 8: F11, focused Alt+Enter, and two-stage Escape — PASS

Steps:

1. With dialogue closed, press Alt+Enter twice.
2. Press Enter, type `fullscreen w/W 😀`, and verify it by clipboard.
3. Press F11 while LineEdit remains focused.
4. Copy the editor value, then press Alt+Enter once to go fullscreen to
   windowed; copy again.
5. Press Alt+Enter once to go windowed to fullscreen; copy again.
6. Press Escape once, then Escape a second time.

Expected:

- closed dialogue: Alt+Enter toggles windowed -> fullscreen -> windowed;
- F11 toggles without closing or changing the focused editor;
- each focused Alt+Enter chord toggles exactly once in both directions;
- editor stays open and focused, with exact text retained;
- first Escape closes/cancels the editor and remains fullscreen;
- second Escape returns to windowed.

Actual:

```text
closed Alt+Enter sizes: 1280x720 -> 1922x1200 -> 1280x720
focused sequence: 1280x720 -> 1922x1200 -> 1280x720 -> 1922x1200
clipboard before F11:                  fullscreen w/W 😀
clipboard after F11:                   fullscreen w/W 😀
clipboard after Alt+Enter to windowed: fullscreen w/W 😀
clipboard after Alt+Enter to fullscreen: fullscreen w/W 😀
editor panel after focused Alt+Enter: present=true, inside=true
first Escape: editor panel absent, client remains 1922x1200
second Escape: client 1280x720
```

Former defect outcome: **FIXED**. The previously failing
`dialogue.alt-enter-while-editing` assertion passed for both directions,
exactly once per chord, with text and LineEdit focus retained.

Evidence:

- `runtime\dialogue-alt-enter-closed-windowed.png`
- `runtime\dialogue-alt-enter-closed-fullscreen.png`
- `runtime\dialogue-alt-enter-closed-back-windowed.png`
- `runtime\dialogue-f11-open-fullscreen.png`
- `runtime\dialogue-alt-enter-open-windowed.png`
- `runtime\dialogue-alt-enter-open-fullscreen.png`
- `runtime\dialogue-first-escape-cancel-only.png`
- `runtime\dialogue-second-escape-exits-fullscreen.png`

Focused fullscreen evidence SHA-256:
`0559F1255CEBA9D16C40BA6DD780C44063DA5C89E5875B3C1BD05912E564D2A7`.

### Scenario 9: Bubble targeting and edge containment — PASS

Steps: move to calibrated left and right edges; submit dialogue; capture
windowed and fullscreen states.

Expected: character remains in the correct edge zone; bubble and tail remain
inside the client and track the character side.

Actual: all four combinations passed. Character centroid ratios were
approximately 0.119 at the left and 0.882 at the right. All bubble metrics
reported `inside=true`.

Evidence: `runtime\dialogue-left-edge-*.png` and
`runtime\dialogue-right-edge-*.png`.

### Scenario 10: Physical viewport matrix — PASS

Steps: launch the unchanged executable with `--resolution` values 1000x800,
1280x720, 1536x864, and 1536x960; capture centered idle and right-edge
dialogue.

Expected: exact physical client size, fitted/scaled character, right edge
arrival, and contained bubble.

Actual:

| Requested | Actual client | Center character height | Bubble |
|---|---:|---:|---|
| 1000x800 | 1000x800 | 430 px | inside |
| 1280x720 | 1280x720 | 551 px | inside |
| 1536x864 | 1536x864 | 661 px | inside |
| 1536x960 | 1536x960 | 661 px, letterboxed | inside |

All eight viewport screenshots had unique SHA-256 values.

### Scenario 11: Process, focus, runtime-error, and cleanup checks — PASS

Expected: every foreground operation targets the intended PID/HWND; every
scenario process exits 0; no stderr or runtime error/fail/exception lines; no
release process remains.

Actual:

```text
focus checks=104 passed, 0 failed
processes=12, clean exits=12
stderr records with content=0
runtime error lines=0
remaining TCFAnimation processes=0
```

Evidence: `focusChecks` and `processes` arrays in
`foreground-e2e-results.json`.

### Scenario 12: Release integrity after execution — PASS

Command:

```powershell
Get-FileHash -Algorithm SHA256 C:\Users\syedhu\source\repos\Dreamer\TCFAnimation\Build\TCFAnimation.exe
```

Actual after run:
`6C1EEA789E89897BECFCD6A90A5ABDB4B591E162EF1C1E780FDAD82E3B83C96F`.

The executable remained byte-identical before and after QA.

## Totals

Scenarios: 12 run, 12 passed, 0 failed  
Assertions: 51 run, 51 passed, 0 failed  
Screenshots: 85 captured, 48 unique SHA-256 values  
Focus checks: 104 passed, 0 failed  
Processes: 12 run, 12 clean exits  
Conclusion: **PASS**

Changing-sequence uniqueness:

| Sequence | Captures | Unique hashes |
|---|---:|---:|
| Directional | 10 | 7 |
| Clap high-rate samples | 18 | 6 |
| Cross entry | 3 | 3 |
| Cross release | 6 | 6 |
| Dialogue | 26 | 15 |
| Viewport | 8 | 8 |

## QA harness retention/rework

Only mission-owned QA files were changed:

- `evidence\qa-final\qa-foreground-fixed\run-final-foreground-e2e-fixed.py`
- `evidence\qa-final\qa-foreground-fixed\analyze_foreground_e2e.py`

The expected executable hash was updated to the new release. Focused
fullscreen assertions were expanded, not weakened, to require both
Alt+Enter directions, one transition per chord, exact clipboard retention,
and visible editor presence. First-frame clap/cross capture was synchronized
using a pre-flush plus held physical scan code inside the 125 ms first-frame
interval; the exact sequence/template assertions and score thresholds were
preserved.

Canonical evidence:

- `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\foreground-e2e-results.json`
- `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\foreground-e2e-capture-results.json`
- `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\qa-foreground-results.md`
- `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\qa-foreground-fixed\runtime\*.png`
- Qualifying console transcript:
  `C:\Users\syedhu\AppData\Local\Temp\1788543938069-copilot-tool-output-21316-3fcdd25a-3d4f-4c9b-9336-014b18f0d705.txt`

---

STATUS: **PASS**

SUMMARY:

The complete repaired foreground E2E/visual suite passed against the new
release. The former Alt+Enter dialogue-focus production defect is fixed in
both fullscreen directions. All retained semantic, rendered-pixel, focus,
process-health, cleanup, and release-integrity gates passed.

WORK_COMPLETED:

- Verified the expected release SHA-256 before and after execution.
- Ran the complete foreground suite, not only the former defect.
- Proved real LineEdit focus/text retention across F11 and both focused
  Alt+Enter directions.
- Proved plain Enter submission and two-stage Escape ownership.
- Retained directional, speed/W, clap, cross, Unicode/scalar, bubble,
  viewport, stale-capture, focus, process, and runtime-error assertions.
- Captured real rendered physical client pixels with DPI-aware coordinates
  and DWM synchronization.
- Updated this canonical QA artifact to PASS.

EVIDENCE:

The QA RESULT above and the canonical evidence paths listed above.

ARTIFACTS:

`.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/qa-results.md`

FINDINGS:

1. The former focused Alt+Enter failure no longer reproduces.
2. No product failure remained in the qualifying complete run.
3. The DWM/OpenGL first-frame presentation requires phase-aware capture;
   pre-flush plus held scan-code synchronization made clap/cross evidence
   deterministic without relaxing semantic assertions.

RISKS:

- Validation was performed on one Windows/NVIDIA/125%-DPI environment; other
  GPU drivers, multi-monitor layouts, DPI scales, and OS versions were not
  exercised.
- Borderless fullscreen reports a 1922x1200 client on the 1920x1200 physical
  display because the window extends one pixel beyond each horizontal edge.
  Rendered-content and containment assertions passed.

BLOCKERS:

None.

NEXT_ACTION:

Proceed to the independent final release-readiness/judgment gate using this
QA PASS evidence. No commit or push was performed.
