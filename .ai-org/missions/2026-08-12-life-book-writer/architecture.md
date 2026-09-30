# Life Book Writer Architecture

Date: 2026-08-12  
Status: Proposed for implementation

## Current state

- **FACT:** `BookWriter/` is empty; there is no application entry point, data model, configuration, dependency manifest, test harness, or prior BookWriter ADR.
- **FACT:** The mission requires memory capture, ordered chapters, guided expansion, local persistence, backup/restore, durable export, and no installation (`.ai-org/active-mission.json:8-14`).
- **FACT:** All content must remain local, work offline, execute without dependencies/build tools, and render authored/imported text as non-executable content (`requirements.md:51-60`).
- **FACT:** Direct opening of `index.html` is an explicit delivery constraint. No .NET SDK, Node.js, or npm is available.
- **FACT:** Restore must validate before mutation, preview replacement, and leave current data unchanged on invalid input (`requirements.md:37-40`, `requirements.md:72-75`).
- **FACT:** The supported workload is 500 memories, 50 chapters, and 500,000 characters (`requirements.md:58`).
- **FACT:** Existing repository ignore rules cover generated build artifacts and dependency directories, but impose no BookWriter architecture (`../../.gitignore:1-64`).

## Design

Build a static, single-page application made only of hand-authored HTML, CSS, and classic JavaScript
files. `index.html` loads scripts in dependency order; scripts attach narrow APIs to one
`window.BookWriter` namespace. Do not use ES modules, `fetch`, service workers, external assets, or
runtime network APIs because the application must run consistently from `file://`.

Dependency direction:

```text
index.html
  -> app.js (composition/state machine)
     -> ui.js
     -> domain.js
     -> persistence.js
     -> validation.js
     -> backup.js / export.js
     -> prompts.js

domain, validation, prompts: browser-storage independent
persistence: depends on validation/domain contracts, never on UI
ui: renders supplied state and emits intent callbacks, never writes storage
```

Keep one authoritative in-memory collection. User edits update that state immediately, preserve the
visible draft, and schedule a debounced snapshot save. Persistence success advances the revision and
saved indicator; failure leaves the draft intact and enters an actionable save-error state.

## Files

```text
BookWriter/
  index.html                 semantic shell, CSP, ordered classic script tags
  css/app.css                responsive layout, print rules, focus/contrast styles
  js/namespace.js            creates window.BookWriter only
  js/domain.js               collection factories, immutable-style commands, ordering/search
  js/validation.js           collection/backup validators and normalized error codes
  js/persistence.js          localStorage adapter, revision checks, storage-event handling
  js/prompts.js              fixed respectful prompt catalogue; no generated prose
  js/backup.js               versioned JSON serialization, restore parse/preview
  js/export.js               safe standalone HTML and UTF-8 text rendering
  js/ui.js                   DOM rendering, focus management, dialogs, event delegation
  js/app.js                  boot/composition, state transitions, autosave orchestration
  tests/browser-tests.html   dependency-free domain/validation/export test runner
  tests/test-runner.js       small assertion runner and named AC-focused tests
  tests/fixtures/*.json      valid, malformed, unsupported, oversized, script-shaped fixtures
  tests/manual-checklist.md  offline, browser, keyboard, responsive, storage-failure procedures
  README.md                  opening, privacy, backup, restore, export, erasure, known risks
```

No manifest, generated bundle, web server, framework, font, icon service, or package lock is needed.

## Component contracts

All public functions return `{ ok: true, value }` or
`{ ok: false, error: { code, message, details? } }`; expected user/data failures do not throw.
Programmer errors may throw during tests.

```text
Domain.createEmptyCollection(now, ids) -> Collection
Domain.apply(collection, command, now) -> Result<Collection>
Domain.search(collection, query, filters) -> MemoryId[]
Domain.assembleBook(collection) -> BookSection[]

Validation.validateCollection(candidate) -> Result<Collection> (schema/invariants only)
Validation.validateBackup(candidate, byteLength) -> Result<RestorePreview> (restore resource limits)

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

Commands include book metadata updates; memory create/update/delete; chapter
create/rename/reorder/delete; assign/move/reorder memory; prompt-response update; and erase. Chapter
deletion requires a confirmation token supplied by the UI command and moves its ordered memory IDs
to the end of the unassigned list.

Error codes are stable and testable: `STORAGE_UNAVAILABLE`, `STORAGE_QUOTA`,
`STALE_REVISION`, `INVALID_JSON`, `WRONG_FORMAT`, `UNSUPPORTED_VERSION`, `FILE_TOO_LARGE`,
`INVALID_SCHEMA`, `INVALID_REFERENCE`, `DUPLICATE_ID`, and `DOWNLOAD_FAILED`. UI text may evolve
without changing these codes.

## Data contracts

### Stored collection schema v1

One JSON snapshot is stored under `dreamer.bookWriter.collection.v1`.

```json
{
  "schemaVersion": 1,
  "collectionId": "uuid",
  "revision": 12,
  "createdAt": "2026-08-12T00:00:00.000Z",
  "updatedAt": "2026-08-12T00:00:00.000Z",
  "book": {
    "title": "",
    "subtitle": "",
    "authorName": "",
    "dedication": "",
    "preface": ""
  },
  "chapters": [
    {
      "id": "uuid",
      "title": "Childhood",
      "createdAt": "ISO-8601",
      "updatedAt": "ISO-8601",
      "memoryIds": ["uuid"]
    }
  ],
  "unassignedMemoryIds": ["uuid"],
  "memories": {
    "uuid": {
      "id": "uuid",
      "title": "",
      "memoryText": "",
      "dateText": "",
      "people": [],
      "places": [],
      "themes": [],
      "sensoryDetails": "",
      "promptResponses": {
        "context": "",
        "people": "",
        "setting": "",
        "senses": "",
        "emotions": "",
        "significance": "",
        "beforeAfter": ""
      },
      "narrativeText": "",
      "createdAt": "ISO-8601",
      "updatedAt": "ISO-8601"
    }
  },
  "settings": {
    "backupReminderDismissedAt": null
  }
}
```

Invariants:

1. IDs are application-generated UUID strings and unique within their entity type.
2. Chapter array order is chapter order. Each chapter's `memoryIds` order is manuscript order.
3. Every memory ID occurs exactly once across all chapter lists and `unassignedMemoryIds`.
4. Every referenced memory exists; unknown and duplicate references reject restore.
5. Empty/imprecise `dateText` is valid. User text remains Unicode strings with no HTML semantics.
6. Narrative fallback is computed in book view/export: use `narrativeText`, otherwise `memoryText`.
7. `revision` increases by one only after a successful persistent write.

### Backup v1

Filename: `life-memoir-backup-YYYY-MM-DD.json`; UTF-8 JSON; MIME
`application/json`. The wrapper distinguishes a portable backup from arbitrary JSON:

```json
{
  "format": "dreamer.life-memoir.backup",
  "formatVersion": 1,
  "exportedAt": "ISO-8601",
  "appVersion": "1.0.0",
  "collection": {}
}
```

Restore reads at most 10 MiB, parses into a detached candidate, validates wrapper/version/schema,
all invariants, and import-only resource limits, then shows memory/chapter counts and replacement
warning. Resource limits protect the restore trust boundary and are not applied to normal local
authoring, persistence/load, backup serialization, or readable export. Only confirmation calls one
`localStorage.setItem`; the UI swaps to restored state only after that write succeeds. Cancel,
parse failure, validation failure, and write failure do not modify the current stored snapshot.

Backups never claim trust through an application-generated marker because JSON files are forgeable.
A locally authored collection above restore-defense limits remains persistable and exports to a
complete, untruncated backup, but this version rejects that file if it is later imported. The
actionable restore error directs the author to split very large working collections before restore;
streamed/incremental restore and automated splitting remain out of scope.

Future schema readers may migrate older supported versions in memory and write only after
confirmation. Version 1 rejects newer versions rather than guessing. Backup v1 is a compatibility
commitment; additive fields must be ignored only when explicitly documented, while missing required
fields remain invalid.

### Readable exports

- HTML: standalone UTF-8 document, semantic headings/sections, embedded minimal print CSS, no
  script, external URL, form, or active content. Every user value is HTML-escaped before joining.
- Text: UTF-8 with deterministic headings, blank-line paragraph separation, ordered chapters, and
  narrative fallback. Use `Blob` downloads; never place user text into `innerHTML`.
- Backup is fidelity/restore data; HTML/text are durable reading formats, not restore inputs.

## State and failure model

```text
BOOTING -> FIRST_RUN | READY | STORAGE_BLOCKED
READY -> DIRTY -> SAVING -> SAVED
SAVING -> SAVE_ERROR (draft retained) -> SAVING on retry/edit
READY/DIRTY -> RESTORE_READING -> RESTORE_INVALID | RESTORE_PREVIEW
RESTORE_PREVIEW -> READY (cancel) | SAVING_REPLACEMENT -> READY | SAVE_ERROR
READY/DIRTY -> CONFLICTED when stored revision > loaded revision
CONFLICTED -> RELOAD_EXTERNAL | FORCE_OVERWRITE_CONFIRMED
READY -> ERASE_CONFIRM -> READY (cancel) | FIRST_RUN (successful erase)
```

- Autosave debounce target: 300 ms after the latest input; status feedback occurs immediately.
- `setItem` is the single snapshot commit boundary. No partial entity writes occur.
- On quota/security/serialization failure, retain the current in-memory draft, show the error, offer
  retry and an emergency backup download from memory, and make no recovery promise.
- A `storage` event marks the tab conflicted. Before every save, compare expected and stored
  revisions. A stale tab never silently writes; reload or explicitly confirmed overwrite are the
  only choices. No merge is attempted.
- Search normalizes case once per query and checks the required fields. For the supported dataset,
  an O(n) scan is simpler and sufficient; do not add an index until measurement disproves this.
- Reorder controls use explicit Move up/Move down actions (keyboard accessible); drag-and-drop may
  be added only as a redundant enhancement.

## Security and privacy

- Trust boundaries are authored form input, imported backup bytes, browser storage, and downloaded
  files. Validate schema and referential invariants at import and before persistence; apply
  structural/entity/text resource limits only to imported backup bytes before cloning/rendering.
  Treat all memoir strings as untrusted text.
- Render with `textContent`, `value`, and DOM node creation. Prohibit user-derived `innerHTML`,
  `insertAdjacentHTML`, `eval`, `Function`, dynamic script creation, remote URLs, and network APIs.
- Add a restrictive meta CSP: local scripts/styles only; `connect-src 'none'`; no objects, frames,
  forms, media, or remote images. Export HTML contains its own `default-src 'none'` CSP.
- There is no authentication boundary: anyone with access to the browser profile or backup file can
  read the memoir. Disclose that local storage and backups are unencrypted.
- Do not log memoir text, imported content, search terms, or exports. There is no telemetry.
- Destructive actions identify impact and require confirmation. Erase removes only the application
  key, then verifies absence before resetting UI.

## Accessibility, responsive behavior, observability

- Use semantic landmarks, one `h1`, logical headings, explicit labels/instructions, native buttons
  and inputs, live regions for save/errors, focus return after dialogs, and visible `:focus-visible`.
- Destructive/restore dialogs trap focus, support Escape/cancel, name their purpose, and move focus
  to the error/summary when opened. Never rely on color alone.
- Layout is single-column at 360 px and becomes list/editor or organizer columns when space permits;
  controls wrap and text areas resize vertically. No fixed content widths that cause page scrolling.
- Use system fonts and CSS custom properties with AA contrast. Respect reduced motion.
- Operational visibility is local only: saved/saving/error/conflict status, last successful save
  time, collection counts, and stable error code. Console diagnostics may include error code/stack
  but never memoir content.

## Migration and compatibility

There is no existing BookWriter data or API to migrate. Initial delivery creates schema/backup v1.
For future changes: read old version -> validate -> migrate a clone -> preserve the original stored
snapshot until the migrated snapshot saves successfully. Rollback is restore from the user's v1
backup; never destructively rewrite an unsupported version. Public compatibility consists of the
v1 backup format and the direct-open `index.html` behavior, not internal JavaScript APIs.

## Tradeoffs and risks

| Decision | Optimized for | Cost / mitigation |
|---|---|---|
| `localStorage`, one snapshot | Direct-file compatibility, atomic/simple writes, easy backup | Typical quota is limited and `file://` behavior is browser-owned. Supported dataset is well below common limits; detect availability/quota and prioritize backup. |
| Classic namespaced scripts | Reliable `file://` loading without a server/build | Less encapsulation than modules. Enforce one namespace and explicit load order. |
| Normalized snapshot with ordered ID lists | Referential validation and cheap reorder | More validation than nested records; central validator owns invariants. |
| Full snapshot autosave | Atomicity and simple restore/conflict checks | Serializes the collection per save. Debounce and verify against NFR-8 before adding complexity. |
| Last-writer prevention, no merge | Prevents silent loss with small scope | User must choose reload or overwrite; message consequences clearly. |
| Fixed prompts | Privacy, emotional safety, deterministic behavior | No generated prose; narrative remains author-controlled as required. |

Primary residual risks:

1. Browser/profile clearing or device loss destroys the working copy. Mitigate with first-run
   disclosure, persistent backup guidance, visible last-backup information, and portable exports.
2. `file://` storage policy can differ by browser or enterprise policy. Detect storage on boot and
   block editing only when durable save is unavailable, while allowing restore inspection/export of
   an in-memory draft where possible.
3. Quota exhaustion can occur above the supported dataset. Never discard the draft; expose an
   emergency backup from current memory.
4. A malformed implementation of HTML export could create XSS. Centralize escaping and include
   script-shaped fixtures in automated and manual browser tests.
5. A single snapshot may eventually become too large. Reassess IndexedDB only if measured supported
   data breaches performance/quota targets; do not pre-emptively add dual persistence.

## Ordered implementation plan

1. Create semantic shell, local-only CSP, responsive/focus CSS, namespace, and first-run/storage
   capability check.
2. Implement schema factories, validator/invariants, commands, ordering, search, and dependency-free
   browser tests.
3. Implement the single-snapshot persistence adapter, debounce orchestration, save states, injected
   failure seams, revision checks, and cross-tab conflict flow.
4. Implement memory editor and fixed prompt/narrative workflow with safe DOM rendering.
5. Implement chapter CRUD, assignment, keyboard reorder, deletion-to-unassigned confirmation, and
   assembled book view.
6. Implement versioned backup, detached restore validation/preview/replace, size limit, and fixtures.
7. Implement escaped standalone HTML and UTF-8 text exports plus erasure flow.
8. Complete accessibility/responsive polish, README, named AC-1..AC-18 test mapping, browser/offline
   matrix, storage-failure tests, and NFR-8 performance fixture/evidence.

## Architecture verdict

**PASS.** The design satisfies the architecture gate without a runtime service, dependency, build
step, network call, or irreversible migration. No CTO escalation is required: the design introduces
no external service, material infrastructure cost, breaking existing contract, or destructive
migration.
