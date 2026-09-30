# Decisions

# ADR-1: Pin one explicit migration target schema
Date: 2026-08-15     Status: Accepted

## Context
EF currently accepts an ambient search path. Upgrade and downgrade must fail closed before changing
migration history when the configured and effective schemas differ.

## Decision
Require matching `Search Path=<schema>` and
`Options=-c tabruk.target_schema=<schema>` in `TABRUK_MIGRATIONS_CONNECTION`, using one unquoted
lowercase ordinary identifier. Configure EF migration history in that schema and repeat the
schema/current-schema validation in corrective `Up`, `Down`, and owner SQL.

## Consequences
Migration targeting is deterministic and downgrade cannot discover history through an ambient
schema. Exotic, quoted, multi-schema, or implicit configurations are intentionally unsupported.

## Alternatives considered
Ambient `search_path` - rejected because it can target the wrong schema.
SQL checks only - rejected because EF reads migration history before invoking `Down`.
A custom EF interceptor/history service - rejected as a larger change than schema-qualifying the
existing history table.

# ADR-2: Verify named objects before skip or drop
Date: 2026-08-15     Status: Accepted

## Context
PostgreSQL `IF NOT EXISTS` checks names, not intended definitions. Wrong same-name indexes and
constraints can otherwise be accepted or destroyed.

## Decision
Before any corrective DDL, verify the full required index and constraint catalog attributes.
Correct objects are reused; absent objects are created; mismatched objects raise a fixed error and
are never automatically dropped. Apply the same rule to downgrade.

## Consequences
Hostile drift fails before migration-history insertion and requires explicit owner repair. Catalog
checks are PostgreSQL 18.6-specific and add deliberate duplication between EF and owner SQL.

## Alternatives considered
Rely on `IF NOT EXISTS` - rejected because it silently accepts drift.
Auto-drop/recreate - rejected because it is destructive and increases outage risk.

# ADR-3: Treat PUBLIC as an untrusted privilege source
Date: 2026-08-15     Status: Accepted

## Context
Revoking privileges only from `tabruk_app` does not remove effective access inherited through
PUBLIC.

## Decision
Revoke all table privileges from PUBLIC on every catalogued table and all sequence privileges from
PUBLIC on every catalogued sequence before granting the exact `tabruk_app` matrix. Apply this in
Up, before and after Down, and in owner SQL.

## Consequences
The effective runtime role is fail-closed even after hostile PUBLIC grants. Other consumers that
incorrectly depended on PUBLIC grants must receive explicit DBA-managed privileges.

## Alternatives considered
Revoke PUBLIC only on audit/history objects - rejected because runtime tables and sequences remain
an uncontrolled privilege source.

# ADR-4: Make the T8 hash manifest an approved-artifact allowlist
Date: 2026-08-15     Status: Accepted

## Context
The manifest currently pins unchanged generated files alongside modified artifacts, obscuring what
the remediation actually authorizes while the initial migration must remain immutable.

## Decision
Always pin the two immutable initial inputs. Pin the corrective migration, owner SQL, and
least-privilege catalog when modified. Pin the corrective designer or model snapshot only when
their content legitimately changes; do not modify or pin them for this SQL/config-only remediation.

## Consequences
The manifest represents approved corrective change and preserves initial hashes. Factory, tests,
and documentation remain reviewable but outside the migration-runtime hash allowlist.

## Alternatives considered
Pin every touched file - rejected because tests and prose make the manifest self-referential and
blur runtime provenance.
Keep pinning unchanged generated files - rejected because it contradicts the approved-modification
policy.

# Process constraints

- Requirements phase skipped because the CTO supplied explicit, testable findings and exit criteria.
- E2E is real PostgreSQL 18.6 fresh/upgrade/down/re-up and hostile-drift coverage.
- Initial migration files are immutable.
- No git staging, commit, push, reset, or history operations.

# ADR-5: Attest migration state before EF history evaluation
Date: 2026-08-15     Status: Accepted

## Context
The immutable initial migration can grant the runtime role migration-history DML. A forged T8 row
can therefore cause EF to skip every control implemented inside T8.

## Decision
Use the existing migration connection-open seam to commit least-privilege hardening and attest the
fixed initial/T8 catalog state before EF reads history. A history row is accepted only when the
actual objects and effective privileges agree. Fully attest managed index/constraint ownership and
PostgreSQL semantics before any downgrade drop.

## Consequences
Forged history and interrupted downgrade states fail closed while revokes remain committed.
Recovery is explicit through the owner script; suspicious history is never auto-deleted.

## Alternatives considered
Checks only inside T8, deleting suspicious history, or replacing provider history internals were
rejected as bypassable, destructive, or unnecessarily provider-coupled.
