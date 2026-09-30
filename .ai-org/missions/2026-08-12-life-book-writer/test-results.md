# Independent Test Results

Date: 2026-08-12

## TEST RESULT

### Commands

Run from `C:\Users\syedhu\source\repos\Dreamer\BookWriter`.

1. Fresh-profile Edge baseline using `msedge.exe --headless=new
   --allow-file-access-from-files --virtual-time-budget=5000 --dump-dom
   file:///.../tests/browser-tests.html`, with stdout captured in memory.
2. Fresh-profile Chrome baseline using `chrome.exe --headless=new
   --allow-file-access-from-files --virtual-time-budget=5000 --dump-dom
   file:///.../tests/browser-tests.html`, with stdout captured in memory.
3. The same Edge and Chrome commands after adding eight independent boundary/failure tests, using
   `--virtual-time-budget=8000`.
4. PowerShell required-file, JSON-fixture, and exact script-order validation.
5. Ripgrep prohibited network/dynamic-code/external-reference scan over application HTML/CSS/JS.

### Results

```text
Edge baseline:
29 passed, 0 failed, 29 total.
NFR-8 timings — load 5.7 ms; search 0.8 ms; reorder 1.2 ms; save 4.2 ms.

Chrome baseline:
29 passed, 0 failed, 29 total.
NFR-8 timings — load 0.0 ms; search 0.0 ms; reorder 0.0 ms; save 0.0 ms.

Edge expanded:
37 passed, 0 failed, 37 total.

Chrome expanded:
37 passed, 0 failed, 37 total.

Static:
PASS static: 15 required files, 7 valid JSON fixtures plus malformed fixture, 9 ordered scripts.

Prohibited/external scan:
No matches found.
```

Passed: 37
Failed: 0
Skipped: 0

Chrome virtual-time measurements and the final expanded Edge virtual-time measurements were zero
and are not performance evidence. The non-zero Edge baseline measurements are the recorded NFR-8
evidence.

### Added independent tests

- same-list memory ordering survives persistence/reload
- invalid domain input does not mutate its caller
- malformed stored bytes block replacement
- failed erase preserves application and unrelated keys
- backup round trip preserves every supported field and ordering
- failed confirmed restore write is atomic
- unsupported and dangling-reference restores never touch storage
- empty-collection exports remain standalone/readable

## Acceptance-criteria coverage

| Criterion | Executed evidence | Result |
|---|---|---|
| AC-1 | Fresh-profile direct-open boot plus static zero-network-capability scan | PARTIAL: no Network-panel journey |
| AC-2 | Unicode/imprecise-date domain and persistence reload tests | PASS |
| AC-3 | Required-field search/filter test | PASS |
| AC-4 | Ordering tests, including persisted same-list reorder | PASS |
| AC-5 | Confirm/cancel command behavior and preserved unassigned memories | PASS at domain level |
| AC-6 | Prompt immutability, narrative fallback, explicit composition tests | PASS at component level |
| AC-7 | Ordered book assembly and fallback tests | PASS |
| AC-8 | Successful save plus quota/unavailable retained-caller tests | PASS at component level |
| AC-9 | Complete backup inspect/commit round trip | PARTIAL: no downloaded backup into separate GUI profile |
| AC-10 | Malformed, wrong type, oversized, unsupported, duplicate/dangling reference tests | PASS |
| AC-11 | Preview data, detached commit, one-write replacement, atomic failed write | PARTIAL: cancel/confirm UI not executed |
| AC-12 | Escaped/scriptless HTML, safe DOM rendering, deterministic Unicode text | PARTIAL: downloaded files not opened |
| AC-13 | Static absence of network APIs/references | GAP: every P0 GUI journey offline not executed |
| AC-14 | Native labeled controls/dialog code inspected | GAP: keyboard-only journey not executed |
| AC-15 | Edge non-zero NFR-8 timings below thresholds | PASS |
| AC-16 | Stale revision and storage-event tests | PARTIAL: real two-tab UI choices not executed |
| AC-17 | App-key-only erase and failed erase tests | PARTIAL: cancel/confirm UI and first-run return not executed |
| AC-18 | Unavailable storage normalization and first-run direct boot | PARTIAL: unavailable-storage boot UI not executed |

## Failures

No automated test failures or confirmed production defects.

## Conclusion

**FAIL** — the automated/component implementation is green, but the objective requires independent
validation of all MVP acceptance criteria. AC-13 and AC-14 have no executed journey evidence, and
AC-1, AC-9, AC-11, AC-12, AC-16, AC-17, and AC-18 remain partially validated. Firefox is not
installed, so the required compatibility gate is also incomplete.

## T10 independent validation — 2026-08-12 16:50 PDT

### Commands executed

```text
"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" --headless=new --allow-file-access-from-files --user-data-dir="C:\Users\syedhu\AppData\Local\Temp\bw-edge-indep-967b6bdd-b1d1-4bf9-be35-b6544f86e355" --virtual-time-budget=15000 --dump-dom "file:///C:/Users/syedhu/source/repos/Dreamer/BookWriter/tests/browser-tests.html"
"C:\Program Files\Google\Chrome\Application\chrome.exe" --headless=new --allow-file-access-from-files --user-data-dir="C:\Users\syedhu\AppData\Local\Temp\bw-chrome-indep-f2f3e75c-e47c-4cb8-b305-c4c6f6926c2c" --virtual-time-budget=15000 --dump-dom "file:///C:/Users/syedhu/source/repos/Dreamer/BookWriter/tests/browser-tests.html"
PowerShell static scan covering required files, JSON fixtures, exact app/test script order, prohibited patterns, and external references.
```

### Results

```text
Edge:
44 passed, 0 failed, 44 total.
NFR-8 timings — load 0.0 ms; search 0.0 ms; reorder 0.0 ms; save 0.0 ms.

Chrome:
44 passed, 0 failed, 44 total.
NFR-8 timings — load 0.0 ms; search 0.0 ms; reorder 0.0 ms; save 0.0 ms.

Static:
REQUIRED_FILES: PASS 15/15
FIXTURES: PASS 7 valid JSON fixtures
APP_SCRIPT_ORDER: PASS 9 scripts
TEST_SCRIPT_ORDER: PASS 10 scripts
PROHIBITED_SCAN: PASS 0 hits
EXTERNAL_SCAN: PASS 0 hits
```

### Targeted regression mapping

- Complete field-by-field escaped export -> `tests/test-runner.js:477`, `tests/test-runner.js:883`
- Active filters -> new memory visible/editable -> `tests/test-runner.js:950`
- Edit -> erase -> timer expiry -> `tests/test-runner.js:983`
- Cross-tab app-key deletion clears state/cancels stale resurrection -> `tests/test-runner.js:1008`
- Structured restore limits with atomic rejection -> `tests/test-runner.js:776`, `tests/test-runner.js:808`

### Independent findings

- `js/validation.js:181-193` contains prompt-count validation, but no executed regression in
  `tests/test-runner.js` exercises a prompt-count overflow path; the structured restore limit
  coverage executed chapter count, memory count, one text-list count, field length, aggregate
  content, and no-mutation checks, but not prompt-count overflow.
- `tests/fixtures/malformed.json` parses as valid JSON; the filename/manual checklist label is
  misleading for a malformed-JSON scenario.

### Independent verdict

**FAIL** — the full Edge and Chrome suites and static scans passed, but the requested T10 evidence
does not fully execute the structured restore prompt-count regression named in the validation
objective.

## T11 independent validation — 2026-08-12 17:10 PDT

### Commands executed

```powershell
$browser='C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$profile=Join-Path $env:TEMP ('bw-edge-t11-indep-'+[guid]::NewGuid())
$url='file:///C:/Users/syedhu/source/repos/Dreamer/BookWriter/tests/browser-tests.html'
$targets=@(
  'restore: prompt-count overflow is rejected before mutating current state or storage',
  'app: force overwrite treats a missing stored key as terminal erase before the null event arrives',
  'book: export assembly preserves every supported memory field',
  'restore: structured chapter and memory limits are rejected before mutation',
  'restore: structured list, field, and aggregate limits are rejected before mutation',
  'app: creating a memory while filters are active keeps the draft visible',
  'app: erase cancels pending autosave and remains first-run after the delay',
  'app: storage-key deletion clears in-memory data and blocks stale overwrite resurrection'
)
$dom=(& $browser --headless=new --allow-file-access-from-files --user-data-dir=$profile --virtual-time-budget=20000 --dump-dom $url 2>&1 | Out-String)
$summary=[regex]::Match($dom,'<p id="test-summary"[^>]*>(.*?)</p>','Singleline').Groups[1].Value.Trim()
$metrics=[regex]::Match($dom,'<p id="performance-metrics">(.*?)</p>','Singleline').Groups[1].Value.Trim()
Write-Output $summary
if($metrics){ Write-Output $metrics }
foreach($target in $targets){
  if($dom -match [regex]::Escape('PASS — ' + $target)){
    Write-Output ('PASS — ' + $target)
  } elseif($dom -match ('FAIL — ' + [regex]::Escape($target) + ': (.*?)</li>')){
    Write-Output ('FAIL — ' + $target + ': ' + $matches[1])
  } else {
    Write-Output ('MISSING — ' + $target)
  }
}
```

```powershell
$browser='C:\Program Files\Google\Chrome\Application\chrome.exe'
$profile=Join-Path $env:TEMP ('bw-chrome-t11-indep-'+[guid]::NewGuid())
$url='file:///C:/Users/syedhu/source/repos/Dreamer/BookWriter/tests/browser-tests.html'
$targets=@(
  'restore: prompt-count overflow is rejected before mutating current state or storage',
  'app: force overwrite treats a missing stored key as terminal erase before the null event arrives',
  'book: export assembly preserves every supported memory field',
  'restore: structured chapter and memory limits are rejected before mutation',
  'restore: structured list, field, and aggregate limits are rejected before mutation',
  'app: creating a memory while filters are active keeps the draft visible',
  'app: erase cancels pending autosave and remains first-run after the delay',
  'app: storage-key deletion clears in-memory data and blocks stale overwrite resurrection'
)
$dom=(& $browser --headless=new --allow-file-access-from-files --user-data-dir=$profile --virtual-time-budget=20000 --dump-dom $url 2>&1 | Out-String)
$summary=[regex]::Match($dom,'<p id="test-summary"[^>]*>(.*?)</p>','Singleline').Groups[1].Value.Trim()
$metrics=[regex]::Match($dom,'<p id="performance-metrics">(.*?)</p>','Singleline').Groups[1].Value.Trim()
Write-Output $summary
if($metrics){ Write-Output $metrics }
foreach($target in $targets){
  if($dom -match [regex]::Escape('PASS — ' + $target)){
    Write-Output ('PASS — ' + $target)
  } elseif($dom -match ('FAIL — ' + [regex]::Escape($target) + ': (.*?)</li>')){
    Write-Output ('FAIL — ' + $target + ': ' + $matches[1])
  } else {
    Write-Output ('MISSING — ' + $target)
  }
}
```

```powershell
$root='C:\Users\syedhu\source\repos\Dreamer\BookWriter'
$required=@(
  'index.html','css/app.css','js/namespace.js','js/domain.js','js/validation.js',
  'js/persistence.js','js/prompts.js','js/backup.js','js/export.js','js/ui.js','js/app.js',
  'tests/browser-tests.html','tests/test-runner.js','tests/manual-checklist.md','README.md'
)
$missing=$required | Where-Object { -not (Test-Path (Join-Path $root $_)) }
if($missing){ throw ('Missing required files: ' + ($missing -join ', ')) }
$fixtureDir=Join-Path $root 'tests\fixtures'
$validFixtures=Get-ChildItem $fixtureDir -Filter '*.json' | Where-Object { $_.Name -ne 'malformed.json' } | Sort-Object Name
foreach($fixture in $validFixtures){
  Get-Content -Raw $fixture.FullName | ConvertFrom-Json | Out-Null
}
$malformedPath=Join-Path $fixtureDir 'malformed.json'
$malformedRejected=$false
try {
  Get-Content -Raw $malformedPath | ConvertFrom-Json | Out-Null
} catch {
  $malformedRejected=$true
}
if(-not $malformedRejected){ throw 'malformed.json parsed successfully.' }
$expectedScripts=@('js/namespace.js','js/domain.js','js/validation.js','js/persistence.js','js/prompts.js','js/backup.js','js/export.js','js/ui.js','js/app.js')
$indexScripts=[regex]::Matches((Get-Content -Raw (Join-Path $root 'index.html')),'<script src="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
$testScripts=[regex]::Matches((Get-Content -Raw (Join-Path $root 'tests\browser-tests.html')),'<script src="([^"]+)"') | ForEach-Object { $_.Groups[1].Value.Replace('../','').Replace('test-runner.js','tests/test-runner.js') }
if([string]::Join('|',$indexScripts) -ne [string]::Join('|',$expectedScripts)){ throw 'index.html script order mismatch.' }
$expectedTestScripts=$expectedScripts + 'tests/test-runner.js'
if([string]::Join('|',$testScripts) -ne [string]::Join('|',$expectedTestScripts)){ throw 'browser-tests.html script order mismatch.' }
$scanFiles=@((Join-Path $root 'index.html'),(Join-Path $root 'tests\browser-tests.html')) + (Get-ChildItem (Join-Path $root 'css') -File).FullName + (Get-ChildItem (Join-Path $root 'js') -File).FullName
$prohibitedPattern='fetch\(|XMLHttpRequest|WebSocket|EventSource|navigator\.sendBeacon|eval\(|new Function|innerHTML\s*=|insertAdjacentHTML|document\.write|createElement\(("|\x27)script\1\)|setTimeout\(\s*["\x27]|setInterval\(\s*["\x27]'
$externalPattern='https?://|url\(\s*["\x27]?https?://'
$prohibitedHits=Select-String -Path $scanFiles -Pattern $prohibitedPattern
$externalHits=Select-String -Path $scanFiles -Pattern $externalPattern
if($prohibitedHits){ throw ('Prohibited pattern hits: ' + (($prohibitedHits | Select-Object -First 5 | ForEach-Object { $_.Path + ':' + $_.LineNumber }) -join ', ')) }
if($externalHits){ throw ('External reference hits: ' + (($externalHits | Select-Object -First 5 | ForEach-Object { $_.Path + ':' + $_.LineNumber }) -join ', ')) }
Write-Output ('REQUIRED_FILES: PASS {0}/{0}' -f $required.Count)
Write-Output ('FIXTURES: PASS {0} valid JSON fixtures + malformed.json rejected as invalid JSON' -f $validFixtures.Count)
Write-Output ('APP_SCRIPT_ORDER: PASS {0} scripts' -f $expectedScripts.Count)
Write-Output ('TEST_SCRIPT_ORDER: PASS {0} scripts' -f $expectedTestScripts.Count)
Write-Output 'PROHIBITED_SCAN: PASS 0 hits'
Write-Output 'EXTERNAL_SCAN: PASS 0 hits'
```

### Results

```text
Edge:
46 passed, 0 failed, 46 total.
NFR-8 timings — load 0.0 ms; search 0.0 ms; reorder 0.0 ms; save 0.0 ms.
PASS — restore: prompt-count overflow is rejected before mutating current state or storage
PASS — app: force overwrite treats a missing stored key as terminal erase before the null event arrives
PASS — book: export assembly preserves every supported memory field
PASS — restore: structured chapter and memory limits are rejected before mutation
PASS — restore: structured list, field, and aggregate limits are rejected before mutation
PASS — app: creating a memory while filters are active keeps the draft visible
PASS — app: erase cancels pending autosave and remains first-run after the delay
PASS — app: storage-key deletion clears in-memory data and blocks stale overwrite resurrection

Chrome:
46 passed, 0 failed, 46 total.
NFR-8 timings — load 0.0 ms; search 0.0 ms; reorder 0.0 ms; save 0.0 ms.
PASS — restore: prompt-count overflow is rejected before mutating current state or storage
PASS — app: force overwrite treats a missing stored key as terminal erase before the null event arrives
PASS — book: export assembly preserves every supported memory field
PASS — restore: structured chapter and memory limits are rejected before mutation
PASS — restore: structured list, field, and aggregate limits are rejected before mutation
PASS — app: creating a memory while filters are active keeps the draft visible
PASS — app: erase cancels pending autosave and remains first-run after the delay
PASS — app: storage-key deletion clears in-memory data and blocks stale overwrite resurrection

Static:
REQUIRED_FILES: PASS 15/15
FIXTURES: PASS 6 valid JSON fixtures + malformed.json rejected as invalid JSON
APP_SCRIPT_ORDER: PASS 9 scripts
TEST_SCRIPT_ORDER: PASS 10 scripts
PROHIBITED_SCAN: PASS 0 hits
EXTERNAL_SCAN: PASS 0 hits
```

### Targeted regression mapping

- Prompt-count overflow atomic rejection -> `tests/test-runner.js:863`, `js/validation.js:182-185`
- Missing-key force-overwrite-before-null-event -> `tests/test-runner.js:1049`, `js/app.js:624-626`
- Prior finding coverage retained -> `tests/test-runner.js:478`, `tests/test-runner.js:777`,
  `tests/test-runner.js:809`, `tests/test-runner.js:991`, `tests/test-runner.js:1024`,
  `tests/test-runner.js:1095`

### Independent verdict

**PASS** — fresh Edge and Chrome profiles each passed the full 46-test suite, static checks passed,
both T11 regressions executed, and all prior five-finding regressions remained covered.

## Rework validation rerun — 2026-08-12 17:23 PDT

### Commands

Fresh temporary profiles opened `tests/browser-tests.html` directly through `file://`:

```text
C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe
  --headless=new --allow-file-access-from-files
  --user-data-dir=<fresh-temp-profile>
  --virtual-time-budget=20000 --dump-dom <file-url>

C:\Program Files\Google\Chrome\Application\chrome.exe
  --headless=new --disable-background-timer-throttling
  --disable-renderer-backgrounding --allow-file-access-from-files
  --user-data-dir=<fresh-temp-profile>
  --virtual-time-budget=60000 --dump-dom <file-url>
```

The Chrome command was repeated once with another fresh profile. PowerShell also revalidated all
required files and fixtures and scanned application/test HTML, CSS, and JavaScript for prohibited
network, dynamic-code, unsafe-HTML, and external-reference patterns.

### Exact results

```text
Edge:
46 passed, 0 failed, 46 total.
NFR-8 timings — load 0.0 ms; search 0.0 ms; reorder 0.0 ms; save 0.0 ms.

Chrome run 1:
46 passed, 0 failed, 46 total.

Chrome run 2:
46 passed, 0 failed, 46 total.

Static:
REQUIRED_FILES: PASS 15/15
FIXTURES: PASS 6 valid + malformed rejected
PROHIBITED_EXTERNAL_SCAN: PASS 0 hits
```

An initial Chrome invocation with only a 20-second virtual-time budget dumped the page while its
summary still read `Running…`; it did not report a test failure. Increasing the budget and disabling
background throttling produced two consecutive complete 46/46 passes.

### Targeted remediation evidence

All targeted tests emitted explicit PASS results in both Edge and Chrome:

- `book: export assembly preserves every supported memory field`
- `export: HTML and text preserve every supported memory field and escape them`
- `app: creating a memory while filters are active keeps the draft visible`
- `app: erase cancels pending autosave and remains first-run after the delay`
- `app: storage-key deletion clears in-memory data and blocks stale overwrite resurrection`
- `app: force overwrite treats a missing stored key as terminal erase before the null event arrives`
- `restore: failed replacement write is atomic and preserves existing bytes`
- `restore: structured chapter and memory limits are rejected before mutation`
- `restore: structured list, field, and aggregate limits are rejected before mutation`
- `restore: prompt-count overflow is rejected before mutating current state or storage`

### Acceptance coverage reassessment

Automated coverage now passes AC-2 through AC-12 and AC-15 through AC-18 at the
domain/component/application-state level. AC-1 has direct-open/static local-only evidence.

Remaining interactive evidence belongs to the GUI/E2E/compatibility gates:

- AC-1 and AC-13: Network-panel observation while every P0 journey runs offline.
- AC-9: downloaded backup restored into a separate clean GUI browser profile.
- AC-11 and AC-17: human-visible cancel/confirmation dialog journeys.
- AC-12: downloaded HTML/text files opened independently.
- AC-14: complete keyboard-only/focus journey.
- AC-16: two real tabs exercising the visible conflict choices.
- AC-18: browser-policy-denied storage boot presentation.
- Current stable Firefox and responsive 360/768/1440 GUI matrix.

### Verdict

**PASS** — the independent automated test gate passes. Edge and Chrome each completed all 46 tests
with zero failures, the requested remediation regressions passed, and static checks remained clean.
The listed interactive items remain explicit QA/E2E/compatibility work rather than automated-test
failures.

## Final 48-test rework gate — 2026-08-12 17:35 PDT

### Commands

Fresh temporary browser profiles directly opened `tests/browser-tests.html`:

```text
Edge:
msedge.exe --headless=new --disable-background-timer-throttling
  --disable-renderer-backgrounding --allow-file-access-from-files
  --user-data-dir=<fresh-profile> --virtual-time-budget=60000
  --dump-dom <file-url>

Chrome:
chrome.exe --headless=new --disable-background-timer-throttling
  --disable-renderer-backgrounding --run-all-compositor-stages-before-draw
  --allow-file-access-from-files --user-data-dir=<fresh-profile>
  --virtual-time-budget=120000 --dump-dom <file-url>
```

### Results

```text
Edge:
48 passed, 0 failed, 48 total.

Chrome:
48 passed, 0 failed, 48 total.
```

The first Chrome invocation dumped the DOM while the asynchronous runner still displayed
`Running…`; it did not report a failed test. A fresh-profile rerun with the longer virtual-time
budget completed all 48 tests.

### Boundary and retained regression evidence

Both browsers explicitly reported PASS for:

- oversized locally authored data persists and backup serialization preserves the complete value
- the same oversized backup is rejected only during restore
- oversized restore rejection occurs before preview/render/storage mutation
- field-complete escaped HTML/text exports
- script-shaped DOM rendering remains inert
- active filters do not hide a newly created memory draft
- erase cancels pending autosave and remains first-run
- storage-key deletion clears state and prevents stale resurrection
- missing-key force overwrite treats deletion as terminal
- failed restore writes preserve existing bytes
- chapter, memory, list, field, aggregate, and prompt structural limits reject atomically

### Verdict

**PASS** — 48/48 passed independently in both Edge and Chrome with zero failures. No production
code was modified by the test gate.

## T12 restore-limit separation rework — 2026-08-12

### Regression reproduction

After adding the local persistence/backup fidelity regression and before changing validation:

```text
Edge:
46 passed, 1 failed, 47 total.
FAIL — local data: restore limits do not constrain persistence or backup fidelity:
The collection exceeds the supported chapter count.
```

This demonstrated that a restore-only chapter threshold was reached through
`Persistence.save()` before local storage or emergency backup serialization.

### Final browser suites

Fresh temporary profiles opened `tests/browser-tests.html` directly through `file://` using:

```text
msedge.exe --headless=new --disable-background-timer-throttling
  --disable-renderer-backgrounding --allow-file-access-from-files
  --user-data-dir=<fresh-profile> --virtual-time-budget=120000 --dump-dom <file-url>

chrome.exe --headless=new --disable-background-timer-throttling
  --disable-renderer-backgrounding --disable-backgrounding-occluded-windows
  --allow-file-access-from-files --user-data-dir=<fresh-profile>
  --virtual-time-budget=600000 --dump-dom <file-url>
```

Exact results:

```text
Edge:
48 passed, 0 failed, 48 total.
NFR-8 timings — load 0.0 ms; search 0.0 ms; reorder 0.0 ms; save 0.0 ms.

Chrome:
48 passed, 0 failed, 48 total.
NFR-8 timings — load 0.0 ms; search 0.0 ms; reorder 0.0 ms; save 0.0 ms.
```

Both browsers explicitly passed:

- `local data: restore limits do not constrain persistence or backup fidelity`
- `restore: an oversized local backup is rejected before mutation or rendering`
- all prior structured restore-limit and cross-tab erase regressions

An earlier Chrome invocation with a 120,000 ms virtual-time budget dumped while the summary still
read `Running…`; it reported no failure. The fresh-profile retry above completed all 48 tests.

### Static scans

```text
REQUIRED_FILES: PASS 15/15
FIXTURES: PASS 6 valid JSON fixtures + malformed.json rejected as invalid JSON
APP_SCRIPT_ORDER: PASS 9 scripts
TEST_SCRIPT_ORDER: PASS 10 scripts
PROHIBITED_SCAN: PASS 0 hits
EXTERNAL_SCAN: PASS 0 hits
```

### T12 verdict

**PASS** — local collections above restore limits persist and produce complete, untruncated
backups; the identical oversized JSON is rejected at the untrusted restore boundary before preview,
render, state mutation, or storage writes.
