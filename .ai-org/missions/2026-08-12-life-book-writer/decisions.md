# Architecture Decision Records

# ADR-1: Use a static classic-script application
Date: 2026-08-12     Status: Accepted

## Context
The target is empty, no build/runtime tools are available, and the application must open directly
from `index.html` while offline.

## Decision
Use plain HTML/CSS and ordered classic JavaScript files under one `window.BookWriter` namespace.
Use no ES modules, package manager, bundler, server, service worker, or external asset.

## Consequences
Direct-file startup is simple and inspectable. Encapsulation is weaker than modules, so components
must expose narrow namespace APIs and keep an explicit script order.

## Alternatives considered
- ES modules - rejected because cross-origin restrictions for module loading from `file://` are not
  consistently compatible with the direct-open contract.
- One monolithic HTML file - rejected because it makes behavior and security testing unnecessarily
  difficult.
- Local web server/build step - rejected because it violates the delivery constraint.

# ADR-2: Persist one versioned collection snapshot in localStorage
Date: 2026-08-12     Status: Accepted

## Context
The supported content volume is modest, writes must be atomic from the application's perspective,
and direct-file operation must work in Chrome, Edge, and Firefox without a server.

## Decision
Store one validated JSON collection at `dreamer.bookWriter.collection.v1`. Keep edits in memory,
debounce full-snapshot writes, and treat `setItem` as the commit boundary. Detect unavailable or
quota-limited storage and retain/export the draft on failure.

## Consequences
Persistence, backup, restore, and rollback stay simple. Storage capacity and `file://` behavior are
browser-controlled, and full serialization must be performance-tested against NFR-8.

## Alternatives considered
- IndexedDB - rejected for MVP because it adds asynchronous schema/transaction complexity and has
  less predictable direct-file availability without a demonstrated capacity need.
- Per-record localStorage keys - rejected because multi-key changes create partial-write and restore
  complexity.
- Dual localStorage/IndexedDB fallback - rejected because two persistence implementations increase
  inconsistency and test burden.

# ADR-3: Normalize ordering through ID lists and dictionaries
Date: 2026-08-12     Status: Accepted

## Context
Chapter and memory ordering must survive restart/backup, chapter deletion must preserve memories,
and restore must detect dangling or duplicate references.

## Decision
Store ordered chapters, a memory dictionary, ordered memory ID lists per chapter, and one ordered
unassigned list. Require every memory ID to appear exactly once.

## Consequences
Reordering and chapter deletion are deterministic and backup validation is rigorous. All mutation
must go through domain commands that preserve the invariant.

## Alternatives considered
- Numeric order fields on every entity - rejected because renumbering and duplicate positions
  create multiple representations of order.
- Fully nested memory objects inside chapters - rejected because moving/deleting chapters risks
  coupling memory ownership to chapter lifetime.

# ADR-4: Make backup v1 a strict portable contract
Date: 2026-08-12     Status: Accepted

## Context
Local browser storage is not durable enough by itself. Invalid or newer backups must not corrupt or
replace current data, and readable exports must not be confused with restore data.

## Decision
Use a UTF-8 JSON wrapper identified by `dreamer.life-memoir.backup`, `formatVersion: 1`, metadata,
and the complete collection. Parse and validate a detached candidate, preview counts, and replace
storage only after explicit confirmation and a successful single write. Reject unsupported newer
versions and inputs over 10 MiB.

## Consequences
Backup v1 becomes a compatibility commitment and future schema changes need explicit migration
readers. Restore is replace-only, not merge.

## Alternatives considered
- Unversioned JSON - rejected because compatibility and validation would be ambiguous.
- HTML as backup - rejected because it cannot reliably preserve all structured fields.
- Merge restore - rejected as conflict-prone and outside scope.
- Encrypted backup - deferred because key management would add loss modes and is explicitly outside
  the MVP Definition of Done.

# ADR-5: Prevent stale writes with optimistic revision checks
Date: 2026-08-12     Status: Accepted

## Context
The MVP is primarily single-tab but must warn before a stale tab overwrites newer persisted content.

## Decision
Persist a monotonically increasing revision, listen for `storage` events, and compare the tab's
expected revision immediately before every save. On conflict, require either reload from storage or
an explicitly confirmed force overwrite; do not merge.

## Consequences
Silent multi-tab loss is prevented with little machinery. Concurrent edits require a user choice
and one side's changes may still be discarded after explicit confirmation.

## Alternatives considered
- Last writer wins - rejected because it violates FR-17.
- Automatic field-level merge - rejected because conflict semantics and testing are disproportionate
  to the single-author MVP.

# ADR-6: Treat memoir content as text at every boundary
Date: 2026-08-12     Status: Accepted

## Context
Authored and imported content is sensitive and untrusted; script-shaped content must never execute
in the application or exported HTML.

## Decision
Render with text properties/DOM nodes only, centralize HTML escaping for export, prohibit dynamic
code and user-derived HTML sinks, and apply restrictive CSPs to the app and HTML export.

## Consequences
XSS risk and accidental outbound loading are materially reduced. Rich user formatting is not
supported in MVP.

## Alternatives considered
- Sanitized rich HTML - rejected because a sanitizer would require a dependency or a risky custom
  parser and rich text is not required.
- Markdown rendering - rejected for the same dependency/security reasons and because plain text
  meets the product contract.
