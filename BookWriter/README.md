# Life Memoir Writer

A dependency-free, privacy-first browser application for gathering memory fragments, expanding them
with optional prompts, arranging them into chapters, and exporting a readable memoir.

## Open the app

1. Keep the `BookWriter` folder together.
2. Open `index.html` directly in a current desktop version of Chrome, Edge, or Firefox.
3. No server, installation, account, internet connection, build step, or command line is required.

All core application files are local. The app makes no network requests and uses no analytics,
telemetry, external fonts, cloud service, or AI API.

## Write and organize

- Create a memory and add free-form text, an approximate date, people, places, themes, and sensory
  details.
- Use any optional prompt; every prompt may be skipped.
- Write narrative prose directly, or explicitly create an editable draft from the source memory and
  non-empty prompt responses. That action never changes the source memory.
- Create chapters, rename or reorder them, and assign or reorder memories with keyboard-operable
  controls.
- Deleting a chapter keeps its memories and moves them to **Unassigned memories**.
- Book view uses narrative prose when present and otherwise uses the source memory.

Edits are kept on screen immediately and automatically saved to one browser `localStorage` snapshot
after 300 ms. The save indicator reports saved, failed, or conflicted states. If saving fails, do
not close the page until you have tried the emergency backup.

## Privacy and durability

The working copy is stored only in the browser profile on this device. It is not encrypted. Anyone
with access to that profile may be able to read it.

Browser-data clearing, private browsing, enterprise policy, storage limits, device loss, or browser
removal can destroy the working copy. “Lives forever” cannot be guaranteed. For durable ownership:

- Download backups regularly, especially after major edits.
- Keep more than one backup on separate trusted storage devices or locations.
- Also keep HTML and text exports that can be read without this application.
- Verify that important backups restore before relying on them.

Downloaded backup files are **not encrypted**. Protect them with the security of the device or
storage location where you keep them.

## Backup and restore

**Backup & data → Download backup** creates:

`life-memoir-backup-YYYY-MM-DD.json`

The versioned JSON backup contains the complete collection, timestamps, assignments, and ordering.
Backup creation, including emergency backup after a save failure, preserves the full locally
authored collection without applying restore limits or silently truncating content. It is the only
supported restore format.

To restore:

1. Choose the JSON backup.
2. Review its memory and chapter counts.
3. Confirm replacement.

Restore is replace-only, not merge. Imported files are untrusted, so invalid, unsupported,
wrong-type, or resource-excessive files are rejected before preview, rendering, or storage
mutation. Restore accepts files up to 10 MiB and applies defensive limits of 50 chapters, 500
memories, and about 600,000 characters across supported memoir text fields. A confirmed restore
replaces application state only after one successful local-storage write.

A locally authored collection may grow beyond those restore-defense limits and will still persist,
export, and produce a complete backup. That backup remains a full-fidelity archive, but this
version may reject it during restore. Split very large working collections into smaller backups
before relying on restore; this version does not automate splitting. Keep readable HTML/text
exports as an additional recovery format.

## Readable exports

- **Export HTML** creates a self-contained, scriptless reading document with escaped memoir text.
- **Export text** creates deterministic UTF-8 plain text.

Exports preserve chapter and memory order and use source memory text when narrative prose is empty.
They are durable reading copies, not restore files.

## Multiple tabs

The app is intended primarily for one tab. If another tab changes the stored collection, a stale tab
pauses saving and asks you to load the stored copy or explicitly overwrite it. There is no automatic
merge. If another tab erases the memoir key, open tabs immediately clear the in-memory copy and
return to first run instead of restoring stale data.

## Erase local data

**Backup & data → Erase all local data** requires confirmation and removes only
`dreamer.bookWriter.collection.v1`. It does not remove unrelated browser data, downloaded backups,
or exports.

## Tests

Open `tests/browser-tests.html` directly. It runs dependency-free browser tests for domain commands,
validation, persistence failures and conflicts, backup/restore, safe exports, UI text rendering, and
the supported performance dataset.

The reproducible interactive browser, offline, accessibility, failure, restore, and compatibility
procedures are in `tests/manual-checklist.md`.

## Scope

This MVP intentionally excludes hosting, accounts, authentication, collaboration, cloud sync,
generative AI, media uploads, PDF/EPUB generation, encrypted backups, and guaranteed permanent
preservation.
