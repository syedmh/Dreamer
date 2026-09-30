# MVP Requirements — Privacy-First Life Memoir Writer

## Evidence and product contract

- **FACT:** The mission is to turn fragmented life memories into an enduring, chapter-structured collection (`.ai-org/active-mission.json:2-3`).
- **FACT:** The agreed capabilities are memory capture, chapter ordering, guided expansion, local persistence, backup/restore, durable export, and operation without installed dependencies (`.ai-org/active-mission.json:8-14`).
- **FACT:** Tests, security, review, and end-to-end gates are required and currently pending (`.ai-org/active-mission.json:16-20`).

## Users and journeys

- **REQUIREMENT UJ-1 (P0):** A sole memoir author can open the application, create a fragment, enrich it over time, and retain it between sessions.
- **REQUIREMENT UJ-2 (P0):** The author can create and reorder chapters, assign memories to chapters, and turn selected details into editable narrative prose.
- **REQUIREMENT UJ-3 (P0):** The author can back up the complete collection, restore it on another compatible browser, and export a readable memoir independent of the application.
- **REQUIREMENT UJ-4 (P0):** The author can understand where data is stored, use all core flows without a network connection, and erase local memoir data intentionally.

## Functional requirements

### Memory collection

- **REQUIREMENT FR-1 (P0):** Create, view, edit, and delete memory records. Each record supports a title, free-form memory text, optional date or date description, people, places, themes, sensory details, and creation/last-updated timestamps.
- **REQUIREMENT FR-2 (P0):** Dates may be uncertain or absent; the application must not require a precise calendar date.
- **REQUIREMENT FR-3 (P0):** List memories in a stable order and allow search across title, memory text, people, places, and themes.
- **REQUIREMENT FR-4 (P1):** Filter memories by chapter assignment and theme, including an explicit “unassigned” view.

### Chapters and narrative

- **REQUIREMENT FR-5 (P0):** Create, rename, reorder, and delete chapters; assign, move, and reorder memories within chapters.
- **REQUIREMENT FR-6 (P0):** Deleting a chapter must not delete its memories; affected memories become unassigned after explicit confirmation.
- **REQUIREMENT FR-7 (P0):** Each memory provides respectful, non-diagnostic prompts covering context, people, setting, senses, emotions, significance, and what happened before/after.
- **REQUIREMENT FR-8 (P0):** The author can write and edit narrative prose associated with a memory; prompts never overwrite source memory text or narrative without explicit user action.
- **REQUIREMENT FR-9 (P0):** A book view assembles title/front matter, ordered chapters, and ordered memory narratives for review before export.

### Persistence, portability, and control

- **REQUIREMENT FR-10 (P0):** All memoir content and settings persist locally in the browser and remain available after refresh and browser restart.
- **REQUIREMENT FR-11 (P0):** Saving is automatic after edits, with a visible saved/error state; a failed local write must retain the current on-screen text and explain how to back it up.
- **REQUIREMENT FR-12 (P0):** Backup downloads one portable, versioned file containing the complete collection and enough metadata to restore chapter and memory ordering.
- **REQUIREMENT FR-13 (P0):** Restore validates file type, structure, version, and required fields before changing stored data. Invalid or unsupported files leave existing data unchanged and show an actionable error.
- **REQUIREMENT FR-14 (P0):** A valid restore shows collection counts and requires confirmation before replacing current data.
- **REQUIREMENT FR-15 (P0):** Export produces both a self-contained HTML document and UTF-8 plain-text document containing the complete ordered memoir and no executable user-supplied markup.
- **REQUIREMENT FR-16 (P0):** “Erase all local data” requires explicit confirmation, clears memoir content and settings, and returns to the first-run state.
- **REQUIREMENT FR-17 (P1):** If another tab changes the same collection, the application must warn before a stale tab overwrites newer persisted content.

### First-run and guidance

- **REQUIREMENT FR-18 (P0):** First run presents an empty state explaining the private local-only model and offers clear actions to create a first memory or restore a backup.
- **REQUIREMENT FR-19 (P1):** Contextual help explains backup responsibility and that browser-data clearing or device loss can remove the working copy.

## Non-functional requirements

- **REQUIREMENT NFR-1 Privacy (P0):** Core use makes zero outbound network requests and sends no memoir content, metadata, analytics, or telemetry off-device.
- **REQUIREMENT NFR-2 Offline (P0):** After the application files are available locally, every P0 flow works with network access disabled.
- **REQUIREMENT NFR-3 Dependency-free operation (P0):** The delivered application opens in a supported browser without package installation, build tooling, account creation, cloud service, or paid API.
- **REQUIREMENT NFR-4 Safety (P0):** All entered/imported text is rendered and exported as content, not executable code; script-shaped input must not execute.
- **REQUIREMENT NFR-5 Accessibility (P0):** Core flows are keyboard operable, have visible focus, programmatic labels, logical headings, and contrast conforming to WCAG 2.1 AA.
- **REQUIREMENT NFR-6 Compatibility (P0):** Core flows pass on the latest stable desktop versions of Chrome, Edge, and Firefox available at test time.
- **REQUIREMENT NFR-7 Responsiveness (P1):** Core flows remain usable without horizontal page scrolling at viewport widths from 360 px through 1440 px.
- **REQUIREMENT NFR-8 Performance (P0):** With 500 memories, 50 chapters, and 500,000 characters total, initial local load completes within 2 seconds and save/search/reorder feedback appears within 300 ms on the test machine.
- **REQUIREMENT NFR-9 Data fidelity (P0):** Backup/restore and export preserve Unicode text, paragraph breaks, ordering, and all supported memoir fields without silent truncation.
- **REQUIREMENT NFR-10 Emotional respect (P0):** Prompts use neutral, optional language, do not claim clinical expertise, and allow the author to skip any prompt without blocking progress.

## Acceptance criteria

- **REQUIREMENT AC-1:** Given first run, when the app opens offline, then it explains local-only storage and offers “create memory” and “restore backup,” with no network request.
- **REQUIREMENT AC-2:** Given a memory with imprecise date text, Unicode, multiline prose, people, places, themes, and sensory details, when saved and the browser is restarted, then every value is restored unchanged.
- **REQUIREMENT AC-3:** Given multiple memories, when the author searches a term appearing in each supported searchable field, then every matching memory is returned and nonmatches are excluded.
- **REQUIREMENT AC-4:** Given ordered chapters and memories, when items are moved or reordered and the app is reopened, then the chosen order remains.
- **REQUIREMENT AC-5:** Given a chapter containing memories, when its deletion is confirmed, then the chapter is removed, its memories remain intact and unassigned, and cancel changes nothing.
- **REQUIREMENT AC-6:** Given a memory, when prompts are opened, answered, skipped, or revisited, then source text remains unchanged and narrative prose changes only through an explicit edit/action.
- **REQUIREMENT AC-7:** Given a complete collection, when book view opens, then it shows all chapters and narratives in persisted order and represents memories without narrative using their source memory text.
- **REQUIREMENT AC-8:** Given an edit, when local persistence succeeds, then a saved state appears; when persistence is forced to fail, then current on-screen text remains and an actionable error appears.
- **REQUIREMENT AC-9:** Given a populated collection, when backup is downloaded and restored into a clean browser profile, then all supported fields, timestamps, assignments, and ordering match the source.
- **REQUIREMENT AC-10:** Given malformed, wrong-type, oversized, or unsupported-version restore input, when restore is attempted, then existing data is byte-for-byte unchanged and a specific error is shown.
- **REQUIREMENT AC-11:** Given a valid backup and existing data, when restore preview is shown, then counts and replacement warning appear; cancel preserves existing data and confirm replaces it.
- **REQUIREMENT AC-12:** Given Unicode and script-shaped memoir text, when HTML and text exports are opened, then content and ordering are preserved, the documents are readable without the app, and no supplied script executes.
- **REQUIREMENT AC-13:** Given network access is blocked, when every P0 journey is exercised, then all succeed and browser developer tools show zero outbound requests.
- **REQUIREMENT AC-14:** Given keyboard-only operation, when completing create/edit/delete, chapter organization, backup, restore, export, and erase flows, then all controls are reachable, visibly focused, labeled, and operable.
- **REQUIREMENT AC-15:** Given the performance dataset in NFR-8, when load, save, search, and reorder are measured, then each meets its stated threshold.
- **REQUIREMENT AC-16:** Given two tabs with the same initial collection, when one saves and the stale tab later edits, then the stale tab warns before overwriting newer data.
- **REQUIREMENT AC-17:** Given “erase all local data,” when confirmation is canceled, then nothing changes; when confirmed, then no memoir record remains and first-run state appears.
- **REQUIREMENT AC-18:** Given browser storage is cleared or unavailable, when the app opens or saves, then it does not imply recovery is possible, explains the condition, and directs the author to restore/download a backup where possible.

## Assumptions, questions, and risks

- **ASSUMPTION A-1:** MVP is single-author and primarily single-tab; FR-17 prevents silent multi-tab loss rather than providing merge/conflict resolution.
- **ASSUMPTION A-2:** Restore replaces the current collection after preview/confirmation; merge restore is deferred.
- **ASSUMPTION A-3:** HTML plus UTF-8 plain text satisfies “durable, readable format”; PDF generation and publishing layouts are deferred.
- **ASSUMPTION A-4:** The measurable capacity in NFR-8 is an MVP support target, not a hard authoring limit.
- **ASSUMPTION A-5:** Approximate dates are stored as author-entered text; chronology inference is not required.
- **OPEN QUESTION (non-blocking):** Should a later release support encrypted backups? **Recommended default:** defer; clearly disclose that downloaded backups are not encrypted and rely on device/file-system protections.
- **RISK:** Browser storage can be cleared by the user, browser, or device policy; onboarding, persistent warnings, and verified backup/restore reduce but cannot eliminate this risk.
- **RISK:** “Lives forever” cannot be guaranteed by any local application; portability, open exports, and user-managed redundant backups are the achievable durability controls.
- **RISK:** Very large memoirs may exceed browser storage quotas; save-failure behavior and portable export/backup must prevent silent loss.

## Out of scope

- **OUT OF SCOPE:** Accounts, authentication, cloud sync/storage, telemetry, deployment infrastructure, collaboration, sharing permissions, and simultaneous co-authoring.
- **OUT OF SCOPE:** Generative AI, external APIs, automated prose generation, transcription, image/audio/video storage, and paid services.
- **OUT OF SCOPE:** Publishing marketplace integration, print layout/typesetting, ISBN/e-book packaging, PDF generation, version history, merge restore, and guaranteed permanent preservation.
- **OUT OF SCOPE:** Clinical, therapeutic, legal, or historical-fact verification advice.

