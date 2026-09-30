# Life Memoir Writer implementation evidence

Timestamp: 2026-08-12T16:23:07.3877980-07:00  
Implementation state: complete; ready for independent VALIDATION gates

## Scope completed

- T1: direct-open semantic shell, restrictive CSP, one namespace, responsive/accessibility baseline,
  and direct-open browser test harness.
- T2: immutable-style collection commands, exact ordering, search, book assembly, and respectful
  fixed prompts.
- T3: strict collection/backup validation, stable error codes, invariant checks, and seven JSON
  fixtures.
- T4: one-snapshot local persistence, revision checks, storage error normalization, erase
  verification, storage-event subscription, and injected test storage.
- T5: first run, memory CRUD, search/filter, prompts, narrative editing/composition, autosave, and
  retained-draft failure guidance.
- T6: chapter CRUD/reorder, memory assignment/reorder, delete-to-unassigned confirmation, ordered
  book review, and explicit stale-tab choices.
- T7: versioned backup, detached restore inspection, 10 MiB limit, preview/cancel, and one-write
  confirmed replacement.
- T8: escaped standalone HTML, deterministic UTF-8 text, and application-key-only confirmed erase.
- T9: responsive/print/reduced-motion styles, README disclosures, AC-1 through AC-18 checklist,
  static hardening checks, browser tests, and NFR-8 dataset.

## Executed evidence

### Required files, fixture syntax, prohibited patterns, external references, script order

Command run from `BookWriter`:

```powershell
$required = @(
  'index.html','css/app.css','js/namespace.js','js/domain.js','js/validation.js',
  'js/persistence.js','js/prompts.js','js/backup.js','js/export.js','js/ui.js','js/app.js',
  'tests/browser-tests.html','tests/test-runner.js','tests/manual-checklist.md','README.md'
)
# Test-Path, ConvertFrom-Json, prohibited-pattern, external-reference, and exact script-order checks
```

Real output:

```text
PASS static: 15 required files, 7 JSON fixtures, 9 ordered scripts, zero forbidden/external hits.
```

The prohibited application scan covered network APIs, dynamic code, user-derived HTML sinks, and
dynamic script creation. The external-reference scan covered application HTML, CSS, and JavaScript.

### Dependency-free browser tests

Edge command used `msedge.exe --headless=new --allow-file-access-from-files --dump-dom` with a fresh
user-data directory and `tests/browser-tests.html`.

Real Edge output:

```text
29 passed, 0 failed, 29 total.
NFR-8 timings — load 6.0 ms; search 0.7 ms; reorder 1.5 ms; save 4.0 ms.
```

Chrome command used `chrome.exe --headless=new --allow-file-access-from-files --dump-dom` with a
fresh user-data directory and the same direct-open test page.

Real Chrome output:

```text
29 passed, 0 failed, 29 total.
```

Chrome reported virtual-time timing values of zero under `--virtual-time-budget`; those values are
not used as performance evidence. Edge's non-zero measurements are the implementation performance
record.

Named automated coverage includes domain commands/invariants, all searchable fields, prompt
neutrality, narrative fallback/composition, strict validation, unavailable/quota/stale persistence,
storage events, app-key-only erase, backup/restore preparation and one-write replacement, malformed
and oversized restore, HTML/text escaping, script-shaped DOM rendering, Unicode, and the NFR-8
dataset.

### Direct-open application boot

Edge opened `index.html` directly through `file://` with a fresh browser profile. Logging was
enabled and output was filtered for uncaught errors and boot markers.

Real output:

```text
<strong id="save-status" role="status" aria-live="polite">Not saved yet</strong>
<section id="first-run" class="first-run card">
```

No `Uncaught` line was emitted. This proves scripts loaded in the frozen order and the first-run
state booted in the executed Edge environment.

### Environment

```text
Machine=DESKTOP-4IR2HRB
OS=Microsoft Windows NT 10.0.26200.0
Edge=151.0.4129.78
Chrome=151.0.7922.138
FirefoxInstalled=False
```

## Requirement evidence mapping

- AC-2 through AC-12 and AC-15 through AC-18 have named automated component/regression coverage
  where deterministic browser automation is feasible.
- AC-1 direct-open first-run boot is executed; its network-panel portion remains interactive.
- AC-13, AC-14, full AC-16 two-tab UI behavior, clean-profile download/restore, downloaded-file
  opening, and the 360/768/1440 compatibility matrix are specified reproducibly in
  `tests/manual-checklist.md` and remain for independent interactive validation.
- Firefox compatibility is not claimed because Firefox is not installed.

## Residual risks and validation handoff

1. Interactive keyboard-only, focus-trap, network-panel, responsive, two-tab, file-download,
   clean-profile restore, and exported-file opening journeys were not executed in this
   implementation session.
2. Current stable Firefox must be installed and run by the compatibility gate.
3. `file://` local-storage policy can differ under enterprise browser policy; boot and failure
   handling are implemented, but those policy variants need interactive validation.
4. Independent test, security, code-review, E2E, and judge gates remain pending by design.

## T12 restore-limit separation — 2026-08-12

- Root cause: `Validation.validateCollection()` combined schema/referential invariants with
  restore-only resource ceilings, and persistence plus backup serialization both called it.
- Fix: local collection validation now enforces schema/invariants without restore caps;
  `validateBackup()` alone enables byte and structural/entity/text import limits before cloning or
  rendering.
- Fidelity regression: a collection with 501 memories, 51 chapters, and a memory text field over
  the restore limit saves, reloads, and serializes byte-for-byte at the collection-object level.
- Atomic restore regression: the same serialized JSON is rejected with actionable split guidance
  before preview/render; in-memory state and stored bytes remain unchanged with zero writes.
- Verification: Edge `48 passed, 0 failed, 48 total`; Chrome `48 passed, 0 failed, 48 total`;
  required-file, fixture, script-order, prohibited-pattern, and external-reference scans passed.

## T10 remediation — 2026-08-12

### Regression reproduction before fixes

Command:

```powershell
$chrome='C:\Program Files\Google\Chrome\Application\chrome.exe'; $profile=Join-Path $env:TEMP ('bw-chrome-pre-'+[guid]::NewGuid()); $url='file:///C:/Users/syedhu/source/repos/Dreamer/BookWriter/tests/browser-tests.html'; $command='\"' + $chrome + '\" --headless=new --allow-file-access-from-files --user-data-dir=\"' + $profile + '\" --virtual-time-budget=12000 --dump-dom \"' + $url + '\"'; $dom=& $env:ComSpec /c $command; $summary=[regex]::Match($dom,'<p id=\"test-summary\"[^>]*>(.*?)</p>','Singleline').Groups[1].Value; $fails=[regex]::Matches($dom,'<li class=\"fail\">FAIL — (.*?): (.*?)</li>','Singleline') | ForEach-Object { $_.Groups[1].Value + ': ' + $_.Groups[2].Value }; Write-Output $summary; if($fails){ $fails | ForEach-Object { Write-Output $_ } }
```

Real output:

```text
37 passed, 7 failed, 44 total.
book: export assembly preserves every supported memory field: Cannot read properties of undefined (reading '0')
restore: structured chapter and memory limits are rejected before mutation: Cannot read properties of undefined (reading 'code')
restore: structured list, field, and aggregate limits are rejected before mutation: Cannot read properties of undefined (reading 'code')
export: HTML and text preserve every supported memory field and escape them: Expected condition to be true.
app: creating a memory while filters are active keeps the draft visible: Values differ. Expected "202e9c98-14c1-42a4-a4c9-cc3b138ecdd1" but got "20000000-0000-4000-8000-000000000001".
app: erase cancels pending autosave and remains first-run after the delay: Values differ. Expected null but got "{\"schemaVersion\":1,\"collectionId\":\"5ba7f4a3-a03a-412f-9563-505b75c4774c\",\"revision\":1,\"createdAt\":\"2026-08-12T23:44:51.806Z\",\"updatedAt\":\"2026-08-12T23:44:51.806Z\",\"book\":{\"title\":\"\",\"subtitle\":\"\",\"authorName\":\"\",\"dedication\":\"\",\"preface\":\"\"},\"chapters\":[],\"unassignedMemoryIds\":[],\"memories\":{},\"settings\":{\"backupReminderDismissedAt\":null}}".
app: storage-key deletion clears in-memory data and blocks stale overwrite resurrection: Values differ. Expected true but got false.
```

### Post-fix browser suites

Chrome command:

```powershell
$chrome='C:\Program Files\Google\Chrome\Application\chrome.exe'; $profile=Join-Path $env:TEMP ('bw-chrome-final-'+[guid]::NewGuid()); $url='file:///C:/Users/syedhu/source/repos/Dreamer/BookWriter/tests/browser-tests.html'; $command='\"' + $chrome + '\" --headless=new --allow-file-access-from-files --user-data-dir=\"' + $profile + '\" --virtual-time-budget=15000 --dump-dom \"' + $url + '\"'; $dom=& $env:ComSpec /c $command; $summary=[regex]::Match($dom,'<p id=\"test-summary\"[^>]*>(.*?)</p>','Singleline').Groups[1].Value; $metrics=[regex]::Match($dom,'<p id=\"performance-metrics\">(.*?)</p>','Singleline').Groups[1].Value; Write-Output $summary; if($metrics){ Write-Output $metrics }
```

Chrome output:

```text
44 passed, 0 failed, 44 total.
NFR-8 timings — load 0.0 ms; search 0.0 ms; reorder 0.0 ms; save 0.0 ms.
```

Edge command:

```powershell
$edge='C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'; $profile=Join-Path $env:TEMP ('bw-edge-final-'+[guid]::NewGuid()); $url='file:///C:/Users/syedhu/source/repos/Dreamer/BookWriter/tests/browser-tests.html'; $command='\"' + $edge + '\" --headless=new --allow-file-access-from-files --user-data-dir=\"' + $profile + '\" --virtual-time-budget=15000 --dump-dom \"' + $url + '\"'; $dom=& $env:ComSpec /c $command; $summary=[regex]::Match($dom,'<p id=\"test-summary\"[^>]*>(.*?)</p>','Singleline').Groups[1].Value; $metrics=[regex]::Match($dom,'<p id=\"performance-metrics\">(.*?)</p>','Singleline').Groups[1].Value; Write-Output $summary; if($metrics){ Write-Output $metrics }
```

Edge output:

```text
[11996:41456:0812/164812.697:ERROR:chrome\browser\task_manager\providers\fallback_task_provider.cc:126] Every renderer should have at least one task provided by a primary task provider. If a "Renderer" fallback task is shown, it is a bug. If you have repro steps, please file a new bug and tag it as a dependency of crbug.com/40528867.
44 passed, 0 failed, 44 total.
NFR-8 timings — load 0.0 ms; search 0.0 ms; reorder 0.0 ms; save 0.0 ms.
```

### Static integrity/security checks

Command:

```powershell
$root='C:\Users\syedhu\source\repos\Dreamer\BookWriter';
$required=@('index.html','css/app.css','js/namespace.js','js/domain.js','js/validation.js','js/persistence.js','js/prompts.js','js/backup.js','js/export.js','js/ui.js','js/app.js','tests/browser-tests.html','tests/test-runner.js','tests/manual-checklist.md','README.md');
$missing=$required | Where-Object { -not (Test-Path (Join-Path $root $_)) };
if($missing){ throw ('Missing required files: ' + ($missing -join ', ')) }
$fixtureDir=Join-Path $root 'tests\fixtures';
$fixtures=Get-ChildItem $fixtureDir -Filter '*.json';
foreach($fixture in $fixtures){ Get-Content -Raw $fixture.FullName | ConvertFrom-Json | Out-Null }
$expectedScripts=@('js/namespace.js','js/domain.js','js/validation.js','js/persistence.js','js/prompts.js','js/backup.js','js/export.js','js/ui.js','js/app.js');
$indexScripts=[regex]::Matches((Get-Content -Raw (Join-Path $root 'index.html')),'<script src="([^"]+)"') | ForEach-Object { $_.Groups[1].Value };
$testScripts=[regex]::Matches((Get-Content -Raw (Join-Path $root 'tests\browser-tests.html')),'<script src="([^"]+)"') | ForEach-Object { $_.Groups[1].Value.Replace('../','').Replace('test-runner.js','tests/test-runner.js') };
if([string]::Join('|',$indexScripts) -ne [string]::Join('|',$expectedScripts)){ throw 'index.html script order mismatch.' }
$expectedTestScripts=$expectedScripts + 'tests/test-runner.js';
if([string]::Join('|',$testScripts) -ne [string]::Join('|',$expectedTestScripts)){ throw 'browser-tests.html script order mismatch.' }
$scanFiles=@((Join-Path $root 'index.html'),(Join-Path $root 'tests\browser-tests.html')) + (Get-ChildItem (Join-Path $root 'css') -File).FullName + (Get-ChildItem (Join-Path $root 'js') -File).FullName;
$prohibitedPattern='fetch\(|XMLHttpRequest|WebSocket|EventSource|navigator\.sendBeacon|eval\(|new Function|innerHTML\s*=|insertAdjacentHTML|document\.write|createElement\(("|\x27)script\1\)|setTimeout\(\s*["\x27]|setInterval\(\s*["\x27]';
$externalPattern='https?://|url\(\s*["\x27]?https?://';
$prohibitedHits=Select-String -Path $scanFiles -Pattern $prohibitedPattern;
$externalHits=Select-String -Path $scanFiles -Pattern $externalPattern;
if($prohibitedHits){ throw ('Prohibited pattern hits: ' + (($prohibitedHits | Select-Object -First 5 | ForEach-Object { $_.Path + ':' + $_.LineNumber }) -join ', ')) }
if($externalHits){ throw ('External reference hits: ' + (($externalHits | Select-Object -First 5 | ForEach-Object { $_.Path + ':' + $_.LineNumber }) -join ', ')) }
Write-Output ('PASS static: {0} required files, {1} JSON fixtures, {2} ordered app scripts, {3} ordered test scripts, zero prohibited/external hits.' -f $required.Count,$fixtures.Count,$expectedScripts.Count,$expectedTestScripts.Count)
```

Output:

```text
PASS static: 15 required files, 7 JSON fixtures, 9 ordered app scripts, 10 ordered test scripts, zero prohibited/external hits.
```

## T11 remediation — 2026-08-12

### Root cause closed

- `js/app.js:626-629` now treats `Persistence.load()` returning `null` during
  `forceOverwrite()` as terminal erase handling, clears pending saves, resets first-run state, and
  performs no replacement write.
- `tests/test-runner.js:863-896` adds a named restore-validation regression for prompt-count
  overflow that exceeds `Validation.LIMITS.promptCount` and proves the current state and storage are
  unchanged.
- `tests/test-runner.js:1049-1088` adds the missing deletion-before-storage-event ordering
  regression for force-overwrite.
- `tests/fixtures/malformed.json:1-4` is now genuinely malformed JSON, and
  `tests/manual-checklist.md:114` now describes it accurately.

### Regression reproduction before the app fix

Command:

```powershell
$chrome='C:\Program Files\Google\Chrome\Application\chrome.exe'; $profile=Join-Path $env:TEMP ('bw-chrome-pre-t11-'+[guid]::NewGuid()); $url='file:///C:/Users/syedhu/source/repos/Dreamer/BookWriter/tests/browser-tests.html'; $command='\"' + $chrome + '\" --headless=new --allow-file-access-from-files --user-data-dir=\"' + $profile + '\" --virtual-time-budget=15000 --dump-dom \"' + $url + '\"'; $dom=& $env:ComSpec /c $command; $summary=[regex]::Match($dom,'<p id=\"test-summary\"[^>]*>(.*?)</p>','Singleline').Groups[1].Value; $fails=[regex]::Matches($dom,'<li class=\"fail\">FAIL — (.*?): (.*?)</li>','Singleline') | ForEach-Object { $_.Groups[1].Value + ': ' + $_.Groups[2].Value }; Write-Output $summary; if($fails){ $fails | ForEach-Object { Write-Output $_ } }
```

Output:

```text
45 passed, 1 failed, 46 total.
app: force overwrite treats a missing stored key as terminal erase before the null event arrives: Values differ. Expected null but got "{\"schemaVersion\":1,\"collectionId\":\"10000000-0000-4000-8000-000000000001\",\"revision\":2,\"createdAt\":\"2026-08-12T20:00:00.000Z\",\"updatedAt\":\"2026-08-13T00:07:02.615Z\",\"book\":{\"title\":\"\",\"subtitle\":\"\",\"authorName\":\"\",\"dedication\":\"\",\"preface\":\"\"},\"chapters\":[{\"id\":\"30000000-0000-4000-8000-000000000001\",\"title\":\"Beginnings\",\"createdAt\":\"2026-08-12T20:00:00.000Z\",\"updatedAt\":\"2026-08-12T20:00:00.000Z\",\"memoryIds\":[\"20000000-0000-4000-8000-000000000001\"]}],\"unassignedMemoryIds\":[\"20000000-0000-4000-8000-000000000002\"],\"memories\":{\"20000000-0000-4000-8000-000000000001\":{\"id\":\"20000000-0000-4000-8000-000000000001\",\"title\":\"Sensitive local draft\",\"memoryText\":\"First paragraph.\\n\\nSecond paragraph with 東京.\",\"dateText\":\"Sometime in the 1980s\",\"people\":[\"Amma\"],\"places\":[\"Seattle\"],\"themes\":[\"Family\"],\"sensoryDetails\":\"Salt air\",\"promptResponses\":{\"context\":\"\",\"people\":\"\",\"setting\":\"\",\"senses\":\"\",\"emotions\":\"\",\"significance\":\"\",\"beforeAfter\":\"\"},\"narrativeText\":\"\",\"createdAt\":\"2026-08-12T20:00:00.000Z\",\"updatedAt\":\"2026-08-13T00:07:02.615Z\"},\"20000000-0000-4000-8000-000000000002\":{\"id\":\"20000000-0000-4000-8000-000000000002\",\"title\":\"School day\",\"memoryText\":\"A blue classroom.\",\"dateText\":\"\",\"people\":[\"Mr. Lee\"],\"places\":[\"School\"],\"themes\":[\"Learning\"],\"sensoryDetails\":\"Chalk dust\",\"promptResponses\":{\"context\":\"\",\"people\":\"\",\"setting\":\"\",\"senses\":\"\",\"emotions\":\"\",\"significance\":\"\",\"beforeAfter\":\"\"},\"narrativeText\":\"\",\"createdAt\":\"2026-08-12T20:00:00.000Z\",\"updatedAt\":\"2026-08-12T20:00:00.000Z\"}},\"settings\":{\"backupReminderDismissedAt\":null}}".
```

### Final browser suites

Chrome command:

```powershell
$chrome='C:\Program Files\Google\Chrome\Application\chrome.exe'; $profile=Join-Path $env:TEMP ('bw-chrome-final-t11-'+[guid]::NewGuid()); $url='file:///C:/Users/syedhu/source/repos/Dreamer/BookWriter/tests/browser-tests.html'; $command='\"' + $chrome + '\" --headless=new --allow-file-access-from-files --user-data-dir=\"' + $profile + '\" --virtual-time-budget=15000 --dump-dom \"' + $url + '\"'; $dom=(& $env:ComSpec /c $command | Out-String); $summary=[regex]::Match($dom,'<p id=\"test-summary\"[^>]*>(.*?)</p>','Singleline').Groups[1].Value; $metrics=[regex]::Match($dom,'<p id=\"performance-metrics\">(.*?)</p>','Singleline').Groups[1].Value; Write-Output $summary; if($metrics){ Write-Output $metrics }
```

Chrome output:

```text
46 passed, 0 failed, 46 total.
NFR-8 timings — load 0.0 ms; search 0.0 ms; reorder 0.0 ms; save 0.0 ms.
```

Edge command:

```powershell
$edge='C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'; $profile=Join-Path $env:TEMP ('bw-edge-final-t11-'+[guid]::NewGuid()); $url='file:///C:/Users/syedhu/source/repos/Dreamer/BookWriter/tests/browser-tests.html'; $command='\"' + $edge + '\" --headless=new --allow-file-access-from-files --user-data-dir=\"' + $profile + '\" --virtual-time-budget=15000 --dump-dom \"' + $url + '\"'; $dom=(& $env:ComSpec /c $command | Out-String); $summary=[regex]::Match($dom,'<p id=\"test-summary\"[^>]*>(.*?)</p>','Singleline').Groups[1].Value; $metrics=[regex]::Match($dom,'<p id=\"performance-metrics\">(.*?)</p>','Singleline').Groups[1].Value; Write-Output $summary; if($metrics){ Write-Output $metrics }
```

Edge output:

```text
46 passed, 0 failed, 46 total.
NFR-8 timings — load 0.0 ms; search 0.0 ms; reorder 0.0 ms; save 0.0 ms.
```

### Static integrity / fixture checks after the malformed-fixture correction

Command:

```powershell
$root='C:\Users\syedhu\source\repos\Dreamer\BookWriter'; $required=@('index.html','css/app.css','js/namespace.js','js/domain.js','js/validation.js','js/persistence.js','js/prompts.js','js/backup.js','js/export.js','js/ui.js','js/app.js','tests/browser-tests.html','tests/test-runner.js','tests/manual-checklist.md','README.md'); $missing=$required | Where-Object { -not (Test-Path (Join-Path $root $_)) }; if($missing){ throw ('Missing required files: ' + ($missing -join ', ')) }; $fixtureDir=Join-Path $root 'tests\fixtures'; $validFixtures=Get-ChildItem $fixtureDir -Filter '*.json' | Where-Object { $_.Name -ne 'malformed.json' } | Sort-Object Name; foreach($fixture in $validFixtures){ Get-Content -Raw $fixture.FullName | ConvertFrom-Json | Out-Null }; $malformedPath=Join-Path $fixtureDir 'malformed.json'; $malformedRejected=$false; try { Get-Content -Raw $malformedPath | ConvertFrom-Json | Out-Null } catch { $malformedRejected=$true }; if(-not $malformedRejected){ throw 'malformed.json parsed successfully.' }; $expectedScripts=@('js/namespace.js','js/domain.js','js/validation.js','js/persistence.js','js/prompts.js','js/backup.js','js/export.js','js/ui.js','js/app.js'); $indexScripts=[regex]::Matches((Get-Content -Raw (Join-Path $root 'index.html')),'<script src="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }; $testScripts=[regex]::Matches((Get-Content -Raw (Join-Path $root 'tests\browser-tests.html')),'<script src="([^"]+)"') | ForEach-Object { $_.Groups[1].Value.Replace('../','').Replace('test-runner.js','tests/test-runner.js') }; if([string]::Join('|',$indexScripts) -ne [string]::Join('|',$expectedScripts)){ throw 'index.html script order mismatch.' }; $expectedTestScripts=$expectedScripts + 'tests/test-runner.js'; if([string]::Join('|',$testScripts) -ne [string]::Join('|',$expectedTestScripts)){ throw 'browser-tests.html script order mismatch.' }; $scanFiles=@((Join-Path $root 'index.html'),(Join-Path $root 'tests\browser-tests.html')) + (Get-ChildItem (Join-Path $root 'css') -File).FullName + (Get-ChildItem (Join-Path $root 'js') -File).FullName; $prohibitedPattern='fetch\(|XMLHttpRequest|WebSocket|EventSource|navigator\.sendBeacon|eval\(|new Function|innerHTML\s*=|insertAdjacentHTML|document\.write|createElement\(("|\x27)script\1\)|setTimeout\(\s*["\x27]|setInterval\(\s*["\x27]'; $externalPattern='https?://|url\(\s*["\x27]?https?://'; $prohibitedHits=Select-String -Path $scanFiles -Pattern $prohibitedPattern; $externalHits=Select-String -Path $scanFiles -Pattern $externalPattern; if($prohibitedHits){ throw ('Prohibited pattern hits: ' + (($prohibitedHits | Select-Object -First 5 | ForEach-Object { $_.Path + ':' + $_.LineNumber }) -join ', ')) }; if($externalHits){ throw ('External reference hits: ' + (($externalHits | Select-Object -First 5 | ForEach-Object { $_.Path + ':' + $_.LineNumber }) -join ', ')) }; Write-Output ('REQUIRED_FILES: PASS {0}/{0}' -f $required.Count); Write-Output ('FIXTURES: PASS {0} valid JSON fixtures + malformed.json rejected as invalid JSON' -f $validFixtures.Count); Write-Output ('APP_SCRIPT_ORDER: PASS {0} scripts' -f $expectedScripts.Count); Write-Output ('TEST_SCRIPT_ORDER: PASS {0} scripts' -f $expectedTestScripts.Count); Write-Output 'PROHIBITED_SCAN: PASS 0 hits'; Write-Output 'EXTERNAL_SCAN: PASS 0 hits'
```

Output:

```text
REQUIRED_FILES: PASS 15/15
FIXTURES: PASS 6 valid JSON fixtures + malformed.json rejected as invalid JSON
APP_SCRIPT_ORDER: PASS 9 scripts
TEST_SCRIPT_ORDER: PASS 10 scripts
PROHIBITED_SCAN: PASS 0 hits
EXTERNAL_SCAN: PASS 0 hits
```
