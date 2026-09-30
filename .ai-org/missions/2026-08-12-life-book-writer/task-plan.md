# Life Book Writer Implementation Task Plan

Date: 2026-08-12  
Status: IMPLEMENTATION-ready  
Execution model: one developer, serialized work; no parallel developer split

## Frozen implementation contracts

These contracts are fixed for the implementation. Any required change stops execution and returns
the plan to the Tech Lead.

### Files and responsibilities

```text
BookWriter/
  index.html                 semantic application shell, dialogs/live regions, ordered scripts, CSP
  css/app.css                responsive, print, focus, contrast, reduced-motion styles
  js/namespace.js            creates window.BookWriter and no other global
  js/domain.js               collection factories, commands, ordering, search, book assembly
  js/validation.js           collection/backup validation and stable error codes
  js/persistence.js          localStorage snapshot, revision checks, storage-event subscription
  js/prompts.js              immutable respectful prompt catalogue
  js/backup.js               backup serialization and detached restore inspection/preparation
  js/export.js               standalone escaped HTML and deterministic UTF-8 text
  js/ui.js                   safe DOM rendering, dialogs, focus, event-to-intent translation
  js/app.js                  boot, authoritative state, transitions, autosave, component composition
  tests/browser-tests.html   direct-open dependency-free test page
  tests/test-runner.js       assertions, fixtures, named automated AC tests
  tests/fixtures/*.json      restore and data-fidelity fixtures
  tests/manual-checklist.md  reproducible browser, offline, keyboard, failure and performance checks
  README.md                  opening, privacy, backup/restore/export/erase and risk disclosure
```

Classic script load order is:

```text
namespace.js -> domain.js -> validation.js -> persistence.js -> prompts.js ->
backup.js -> export.js -> ui.js -> app.js
```

No ES modules, dependency manifests, generated files, remote assets, `fetch`, service workers, or
server-only behavior may be introduced.

### Namespace and result contract

- The only global is `window.BookWriter`.
- Each component attaches one object named `Domain`, `Validation`, `Persistence`, `Prompts`,
  `Backup`, `Export`, `UI`, or `App`.
- Expected data/user failures never throw. They return:

```js
{ ok: true, value: any }
{ ok: false, error: { code: string, message: string, details?: any } }
```

- Stable error codes:
  `STORAGE_UNAVAILABLE`, `STORAGE_QUOTA`, `STALE_REVISION`, `INVALID_JSON`, `WRONG_FORMAT`,
  `UNSUPPORTED_VERSION`, `FILE_TOO_LARGE`, `INVALID_SCHEMA`, `INVALID_REFERENCE`,
  `DUPLICATE_ID`, `DOWNLOAD_FAILED`.

### Public JavaScript APIs

```text
Domain.createEmptyCollection(now, ids) -> Collection
Domain.apply(collection, command, now) -> Result<Collection>
Domain.search(collection, query, filters) -> MemoryId[]
Domain.assembleBook(collection) -> BookSection[]

Validation.validateCollection(candidate) -> Result<Collection>
Validation.validateBackup(candidate, byteLength) -> Result<RestorePreview>

Persistence.load() -> Result<Collection|null>
Persistence.save(collection, expectedRevision) -> Result<{revision, savedAt}>
Persistence.erase(expectedRevision) -> Result<void>
Persistence.subscribeExternalChange(callback) -> unsubscribe

Backup.serialize(collection, appVersion, exportedAt) -> string
Backup.inspect(file) -> Promise<Result<RestorePreview>>
Backup.commit(preview, expectedRevision) -> Result<Collection>

Export.toHtml(collection) -> string
Export.toText(collection) -> string
```

`ids` is `{ collectionId(), memoryId(), chapterId() }`. `now` and `exportedAt` are ISO-8601 strings.
`filters` is `{ chapterId?: string|null, theme?: string }`; `chapterId: null` means unassigned.
`BookSection` is `{ type: "frontMatter"|"chapter", id?: string, title: string, paragraphs: string[] }`.

`RestorePreview` is:

```js
{
  collection: Collection,
  memoryCount: number,
  chapterCount: number,
  exportedAt: string,
  appVersion: string,
  sourceByteLength: number
}
```

`Backup.commit` is pure restore preparation: it clones the validated preview collection and sets its
revision to `expectedRevision`. `App` then calls `Persistence.save` exactly once. The UI replaces
the authoritative state only after that save succeeds.

### Domain command contract

`Domain.apply` accepts only these command shapes:

```text
UPDATE_BOOK             { fields }
CREATE_MEMORY           { id, fields }
UPDATE_MEMORY           { memoryId, fields }
DELETE_MEMORY           { memoryId, confirmationToken: "DELETE_MEMORY" }
CREATE_CHAPTER          { id, title }
RENAME_CHAPTER          { chapterId, title }
MOVE_CHAPTER            { chapterId, direction: "up"|"down" }
DELETE_CHAPTER          { chapterId, confirmationToken: "DELETE_CHAPTER" }
MOVE_MEMORY             { memoryId, targetChapterId: string|null, targetIndex: number }
UPDATE_PROMPT_RESPONSE  { memoryId, promptId, text }
UPDATE_NARRATIVE        { memoryId, text }
ERASE_COLLECTION        { confirmationToken: "ERASE_ALL", newCollectionId }
```

Commands preserve the schema invariants. Chapter deletion appends its ordered memory IDs to the end
of `unassignedMemoryIds`. Narrative fallback is computed only by `assembleBook` and exports.

### Data, storage, backup and security constants

- Stored schema: architecture `Collection` schema v1, without renaming or optionalizing fields.
- Storage key: `dreamer.bookWriter.collection.v1`.
- Backup format: `dreamer.life-memoir.backup`, `formatVersion: 1`, MIME `application/json`.
- Backup filename: `life-memoir-backup-YYYY-MM-DD.json`.
- Restore byte limit: 10 MiB (`10 * 1024 * 1024`).
- Export filenames: `life-memoir-YYYY-MM-DD.html` and `life-memoir-YYYY-MM-DD.txt`.
- App version: `1.0.0`.
- Autosave debounce: 300 ms after the latest edit.
- `Persistence.save` compares the stored revision with `expectedRevision`, writes a cloned snapshot
  with revision `expectedRevision + 1`, and returns that revision only after successful `setItem`.
- Confirmed force overwrite first reads the current stored revision and uses it as the next
  `expectedRevision`; no merge or silent overwrite is allowed.
- Authored/imported values use `textContent`, form `value`, or created text nodes. User-derived
  `innerHTML`, `insertAdjacentHTML`, dynamic code, dynamic scripts, and network APIs are prohibited.

## Acceptance criteria ownership

| Criteria | Primary task | Supporting task |
|---|---|---|
| AC-1 | T5 | T1, T9 |
| AC-2 | T5 | T2, T3, T4 |
| AC-3 | T2 | T5 |
| AC-4 | T6 | T2, T4 |
| AC-5 | T6 | T2 |
| AC-6 | T5 | T2 |
| AC-7 | T6 | T2 |
| AC-8 | T4 | T5 |
| AC-9 | T7 | T3, T4 |
| AC-10 | T7 | T3 |
| AC-11 | T7 | T4, T5 |
| AC-12 | T8 | T2, T3 |
| AC-13 | T9 | T1 |
| AC-14 | T9 | T5-T8 |
| AC-15 | T9 | T2, T4, T6 |
| AC-16 | T4 | T5 |
| AC-17 | T8 | T2, T4, T5 |
| AC-18 | T4 | T5, T9 |

## Tasks

T1  Create direct-open foundation and test harness
    owner:        developer
    objective:    Create the semantic local-only shell, namespace, baseline responsive/accessibility
                  styles, and a dependency-free browser test runner that can report named passes
                  and failures when opened through file://.
    files:        BookWriter/index.html; BookWriter/css/app.css;
                  BookWriter/js/namespace.js; BookWriter/tests/browser-tests.html;
                  BookWriter/tests/test-runner.js
    depends_on:   -
    parallel_ok:  no
    requirements: FR-18; NFR-1, NFR-2, NFR-3, NFR-5, NFR-7
    exit_criteria: index.html and browser-tests.html open directly; the CSP and exact script order
                  are present; only window.BookWriter is introduced; the test page displays a
                  deliberate passing and failing assertion without any dependency or network use.
    verification: PowerShell path/script-order/forbidden-pattern checks; manually open both HTML
                  files with network disabled and record the harness result.
    status:       PENDING

T2  Implement collection domain and prompt catalogue
    owner:        developer
    objective:    Implement schema factories, immutable-style commands, ordering, search, book
                  assembly, fixed respectful prompts, and deterministic automated tests.
    files:        BookWriter/js/domain.js; BookWriter/js/prompts.js;
                  BookWriter/tests/test-runner.js
    depends_on:   T1
    parallel_ok:  no
    requirements: FR-1 through FR-9; NFR-8, NFR-10; AC-3 through AC-7
    exit_criteria: every frozen command preserves unique/exactly-once references; deletion,
                  assignment and reorder behavior is deterministic; search covers all required
                  fields; prompt text is optional/neutral; book assembly uses narrative fallback;
                  named domain tests pass in the browser runner.
    verification: run browser-tests.html and confirm all `domain:*`, `search:*`, `prompt:*`, and
                  `book:*` tests pass.
    status:       PENDING

T3  Implement strict validation and fixtures
    owner:        developer
    objective:    Validate collection schema, backup wrapper/version/size and all reference
                  invariants before data reaches persistence or UI state.
    files:        BookWriter/js/validation.js; BookWriter/tests/test-runner.js;
                  BookWriter/tests/fixtures/valid-backup.json;
                  BookWriter/tests/fixtures/malformed.json;
                  BookWriter/tests/fixtures/wrong-format.json;
                  BookWriter/tests/fixtures/unsupported-version.json;
                  BookWriter/tests/fixtures/invalid-reference.json;
                  BookWriter/tests/fixtures/duplicate-id.json;
                  BookWriter/tests/fixtures/script-shaped.json
    depends_on:   T2
    parallel_ok:  no
    requirements: FR-12 through FR-14; NFR-4, NFR-9; AC-9 through AC-12
    exit_criteria: validators return normalized clones or stable errors; required/missing fields,
                  duplicate IDs, dangling/duplicate references, wrong format, unsupported version
                  and oversized input are rejected; fixtures parse where intended; named negative
                  tests pass without mutating their inputs.
    verification: `Get-ChildItem tests/fixtures/*.json | Get-Content -Raw | ConvertFrom-Json` for
                  valid JSON fixtures; run all `validation:*` browser tests.
    status:       PENDING

T4  Implement atomic local persistence and conflict handling
    owner:        developer
    objective:    Implement the validated single-snapshot adapter, revision checks, error
                  normalization, storage events, and test seams for unavailable/quota/stale writes.
    files:        BookWriter/js/persistence.js; BookWriter/tests/test-runner.js
    depends_on:   T3
    parallel_ok:  no
    requirements: FR-10, FR-11, FR-17; NFR-8; AC-8, AC-16, AC-18
    exit_criteria: load/save/erase use only the frozen key; one setItem is the save boundary;
                  revisions advance only after success; failed writes retain caller data; stale
                  writes are rejected; storage events notify subscribers; injected failure and
                  two-tab/revision tests pass.
    verification: run all `persistence:*`, `storage-failure:*`, and `stale-revision:*` browser
                  tests, then perform the manual two-tab storage-event check.
    status:       PENDING

T5  Implement app state and memory authoring UI
    owner:        developer
    objective:    Compose boot/first-run/storage-blocked states, authoritative in-memory state,
                  autosave, memory CRUD/search/filter, prompt responses, narrative editing and safe
                  accessible rendering.
    files:        BookWriter/index.html; BookWriter/css/app.css; BookWriter/js/ui.js;
                  BookWriter/js/app.js; BookWriter/tests/test-runner.js;
                  BookWriter/tests/manual-checklist.md
    depends_on:   T4
    parallel_ok:  no
    requirements: FR-1 through FR-4, FR-7, FR-8, FR-10, FR-11, FR-18, FR-19;
                  NFR-4, NFR-5, NFR-10; AC-1 through AC-3, AC-6, AC-8, AC-18
    exit_criteria: first run, create/edit/delete/search/filter, prompts and narrative work from
                  index.html; edits immediately remain visible while autosave reports
                  dirty/saving/saved/error; failure offers retry/emergency backup guidance; DOM
                  rendering contains no user-derived HTML sink; named UI-state tests and the memory
                  manual journey pass.
    verification: run browser tests; open index.html and execute the recorded first-run, restart,
                  Unicode, script-shaped text, save-failure and keyboard memory journeys.
    status:       PENDING

T6  Implement chapter organization and book review
    owner:        developer
    objective:    Add chapter CRUD, assignment, explicit move controls, chapter deletion
                  confirmation, conflict choice UI, and complete ordered book review.
    files:        BookWriter/index.html; BookWriter/css/app.css; BookWriter/js/ui.js;
                  BookWriter/js/app.js; BookWriter/tests/test-runner.js;
                  BookWriter/tests/manual-checklist.md
    depends_on:   T5
    parallel_ok:  no
    requirements: FR-5, FR-6, FR-9, FR-17; NFR-5, NFR-7, NFR-8;
                  AC-4, AC-5, AC-7, AC-14 through AC-16
    exit_criteria: chapter/memory order persists; chapter deletion cancel is a no-op and confirm
                  preserves memories as unassigned; move controls are keyboard operable; book view
                  is complete and ordered; stale-tab reload/confirmed-overwrite choices are clear;
                  associated automated and manual checks pass.
    verification: run `chapter:*`, `ordering:*`, `book:*`, and `conflict:*` tests; complete chapter
                  keyboard/restart/two-tab manual scenarios.
    status:       PENDING

T7  Implement backup and replace-only restore
    owner:        developer
    objective:    Download strict backup v1 and implement detached file inspection, actionable
                  validation errors, replacement preview, cancel, and one-write confirmed restore.
    files:        BookWriter/js/backup.js; BookWriter/index.html; BookWriter/css/app.css;
                  BookWriter/js/ui.js; BookWriter/js/app.js;
                  BookWriter/tests/test-runner.js; BookWriter/tests/manual-checklist.md
    depends_on:   T6
    parallel_ok:  no
    requirements: FR-12 through FR-14; NFR-4, NFR-9; AC-9 through AC-11
    exit_criteria: backup filename/wrapper are exact; inspect enforces the 10 MiB limit before
                  mutation; invalid/canceled restore leaves stored bytes unchanged; valid preview
                  shows counts/warning; confirmation performs one successful persistence write
                  before state replacement; clean-profile fidelity and all restore negative tests
                  pass.
    verification: run `backup:*` and `restore:*` browser tests; use the fixtures; perform populated
                  profile -> backup -> clean profile restore and compare fields/order/timestamps.
    status:       PENDING

T8  Implement durable exports and erase flow
    owner:        developer
    objective:    Produce safe standalone HTML and deterministic text downloads and implement
                  confirmed application-key-only erasure.
    files:        BookWriter/js/export.js; BookWriter/index.html; BookWriter/css/app.css;
                  BookWriter/js/ui.js; BookWriter/js/app.js;
                  BookWriter/tests/test-runner.js; BookWriter/tests/manual-checklist.md
    depends_on:   T7
    parallel_ok:  no
    requirements: FR-15, FR-16; NFR-4, NFR-9; AC-12, AC-17
    exit_criteria: HTML export is escaped, standalone, scriptless and contains `default-src
                  'none'`; text output/order is deterministic; Unicode/paragraphs survive; erase
                  cancel changes nothing; confirm removes only the app key, verifies absence and
                  returns to first run; automated and manual checks pass.
    verification: run `export:*` and `erase:*` browser tests; open exported files offline; test
                  script-shaped/Unicode content; compare localStorage before cancel/confirm.
    status:       PENDING

T9  Complete hardening, documentation and implementation evidence
    owner:        developer
    objective:    Close responsive, accessibility, offline, compatibility, performance, privacy
                  documentation and full AC mapping without adding dependencies.
    files:        BookWriter/index.html; BookWriter/css/app.css; BookWriter/js/*.js;
                  BookWriter/tests/browser-tests.html; BookWriter/tests/test-runner.js;
                  BookWriter/tests/manual-checklist.md; BookWriter/README.md;
                  .ai-org/missions/2026-08-12-life-book-writer/implementation-evidence.md
    depends_on:   T8
    parallel_ok:  no
    requirements: FR-1 through FR-19; NFR-1 through NFR-10; AC-1 through AC-18
    exit_criteria: all automated tests pass; manual checklist names AC-1 through AC-18 and records
                  browser versions/results; 360-1440 px and keyboard checks pass; browser network
                  inspection records zero requests; the NFR-8 fixture meets thresholds on a named
                  machine; README contains every documentation-gate disclosure; implementation
                  evidence gives commands/results and known residual risks.
    verification: run the complete browser runner; run the PowerShell static checks below; execute
                  the full manual checklist in current Chrome, Edge and Firefox; record timing,
                  compatibility, offline and network evidence.
    status:       PENDING

## Execution waves

One developer executes all implementation work serially:

```text
wave 1: T1
-> wave 2: T2
-> wave 3: T3
-> wave 4: T4
-> wave 5: T5
-> wave 6: T6
-> wave 7: T7
-> wave 8: T8
-> wave 9: T9
```

After T9, the VP dispatches independent test, security, code-review, E2E and judgment gates. Those
gates are not substitutes for the verification included in each implementation task.

## Rework wave 1

T10 Remediate validation findings
    owner:        developer
    objective:    Fix complete export fidelity, cancel autosave during erase, keep newly created
                  memories visible under active filters, treat cross-tab storage-key deletion as
                  immediate erase without resurrection, and bound structured restore complexity.
    files:        BookWriter/js/domain.js; BookWriter/js/export.js; BookWriter/js/persistence.js;
                  BookWriter/js/validation.js; BookWriter/js/app.js; BookWriter/js/ui.js;
                  BookWriter/tests/test-runner.js; BookWriter/README.md if disclosures change;
                  mission implementation/test evidence.
    depends_on:   T9 and the independent code-review/security findings
    parallel_ok:  no
    requirements: FR-13, FR-15 through FR-17; NFR-4, NFR-9; AC-10, AC-12, AC-16, AC-17
    exit_criteria: all five findings have focused automated regressions; the complete dependency-free
                  suite passes in fresh Edge and Chrome profiles; static/prohibited scans pass;
                  erase leaves the memoir key absent and stale tabs cannot resurrect it; rejected
                  restores are atomic with stable validation errors.
    verification: execute the full browser suite in Edge and Chrome plus required-file, fixture,
                  script-order, prohibited-pattern, and external-reference checks; record exact
                  counts and real output in mission evidence.
    status:       DONE
    evidence:     Seven focused regressions added; fresh developer Edge/Chrome suites each passed
                  44/44 and the required static/prohibited/external checks passed. See
                  implementation-evidence.md.

T11 Close independent validation failures
    owner:        developer
    objective:    Prevent force-overwrite resurrection when the storage key disappears before the
                  null storage event, add explicit prompt-count overflow regression coverage, and
                  align the malformed-JSON fixture/manual scenario.
    files:        BookWriter/js/app.js; BookWriter/tests/test-runner.js;
                  BookWriter/tests/fixtures/malformed.json; BookWriter/tests/manual-checklist.md;
                  mission implementation evidence.
    depends_on:   T10 and failed independent test/code-review gates
    parallel_ok:  no
    requirements: FR-13, FR-16, FR-17; AC-10, AC-16, AC-17
    exit_criteria: force overwrite aborts and resets first-run whenever persistence load finds the
                  memoir key absent, regardless of stale in-memory content; a named test covers
                  deletion-before-event ordering; a named test covers prompt-count overflow with
                  atomic rejection; the malformed manual scenario uses genuinely malformed JSON;
                  full Edge/Chrome suites and static checks pass.
    verification: rerun focused regressions, full fresh-profile Edge/Chrome suites, static checks,
                  independent test/security/review gates.
    status:       DONE
    evidence:     The deletion-before-event regression failed before the fix and passed after it;
                  prompt-count overflow atomicity is now covered; malformed.json is genuinely
                  invalid. Fresh developer Edge/Chrome suites passed 46/46 and static checks passed.
    failing_evidence: code review reproduced key recreation with
                  {"keyPresent":true,"setCalls":1,"stateFirstRun":false}; independent test gate
                  found no executed prompt-count overflow regression.

## T10/T11 independent validation result

- Tests: PASS — fresh Edge and Chrome profiles each ran `46 passed, 0 failed, 46 total`.
- Security: PASS — zero Critical, High, Medium, Low, or Informational findings in the remediation
  scope; both cross-tab deletion orderings were rechecked.
- Code review: APPROVED — the previously reproduced missing-key force-overwrite race is closed.
- Static: PASS — 15 required files, six valid fixtures plus expected malformed rejection, exact
  9-script application order and 10-script test order, zero prohibited or external-reference hits.
- Mission metadata: `state` remains `VALIDATION`; `reworkCount` is `1`.

## Reproducible validation commands

Run from `C:\Users\syedhu\source\repos\Dreamer\BookWriter`.

```powershell
# Required files
$required = @(
  'index.html','css/app.css','js/namespace.js','js/domain.js','js/validation.js',
  'js/persistence.js','js/prompts.js','js/backup.js','js/export.js','js/ui.js','js/app.js',
  'tests/browser-tests.html','tests/test-runner.js','tests/manual-checklist.md','README.md'
)
$missing = $required | Where-Object { -not (Test-Path $_) }
if ($missing) { throw "Missing: $($missing -join ', ')" }

# Fixtures that are intended to be valid JSON syntax
Get-ChildItem tests/fixtures/*.json | ForEach-Object {
  Get-Content $_.FullName -Raw | ConvertFrom-Json | Out-Null
}

# Prohibited application/network/dynamic-code patterns
$forbidden = 'fetch\s*\(|XMLHttpRequest|WebSocket|EventSource|sendBeacon|eval\s*\(|new\s+Function|' +
             'insertAdjacentHTML|\.innerHTML\s*=|createElement\s*\(\s*[''"]script'
$hits = Get-ChildItem index.html,js/*.js -File |
  Select-String -Pattern $forbidden -AllMatches
if ($hits) { $hits; throw 'Prohibited pattern found' }

# No external URL references in application shell/scripts/styles
$external = Get-ChildItem index.html,css/*.css,js/*.js -File |
  Select-String -Pattern 'https?://|//cdn\.|@import\s+url'
if ($external) { $external; throw 'External reference found' }

# Confirm the classic script order in index.html
$html = Get-Content index.html -Raw
$scripts = [regex]::Matches($html, '<script[^>]+src="([^"]+)"') |
  ForEach-Object { $_.Groups[1].Value }
$expected = @(
  'js/namespace.js','js/domain.js','js/validation.js','js/persistence.js',
  'js/prompts.js','js/backup.js','js/export.js','js/ui.js','js/app.js'
)
if (($scripts -join '|') -ne ($expected -join '|')) {
  throw "Unexpected script order: $($scripts -join ', ')"
}

# Direct-open automated tests and application
Start-Process (Resolve-Path 'tests/browser-tests.html')
Start-Process (Resolve-Path 'index.html')
```

Browser verification records the visible test summary because no Node/.NET runtime is assumed.
`tests/manual-checklist.md` must include exact steps, expected results, browser/version, machine,
timestamp, and pass/fail for every manual item.
