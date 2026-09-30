# ADR-1: Owner scripts are the sole production T8 migration interface
Date: 2026-08-15     Status: Accepted

## Context
EF releases transaction locks before it reads history and splits concurrent DDL from history
mutation. The current factory cannot keep one trusted lock over that complete lifecycle.

## Decision
Production T8 upgrade and downgrade use checked-in owner `psql` scripts only. EF migrations require
an explicit disposable-database flag and are limited to fresh/empty local or CI databases.

## Consequences
The history TOCTOU is removed from the supported production path without replacing EF provider
internals. Operators lose production `dotnet ef database update` convenience.

## Alternatives considered
- Extend the factory callback — rejected because its transaction ends before EF history selection.
- Replace EF history/migrator services — rejected as provider-coupled and larger than owner
  orchestration.

# ADR-2: Hold a session advisory lock and provide compensating semantic atomicity
Date: 2026-08-15     Status: Accepted

## Context
`CREATE INDEX CONCURRENTLY` must run outside a transaction, but history must never claim an invalid
upgrade. Transaction advisory locks and table locks cannot span the required commits.

## Decision
Each owner script holds one database/schema-scoped session advisory lock from preflight through
final commit and disconnect. Upgrade records entry object state and compensates only objects it
created when validation, concurrent index creation, final attestation, or history insertion fails.
History is the final success marker; transaction atomicity across concurrent DDL is not claimed.

## Consequences
Concurrent owner workflows serialize, live index safety remains, and ordinary failures restore the
entry state. A failed compensation becomes an explicit manual-recovery state with history absent.

## Alternatives considered
- Non-concurrent index — rejected due to unacceptable large-table write blocking.
- Accept retry debris — rejected because it does not meet the required atomic semantic guarantee.

# ADR-3: Production Down is logical and non-destructive
Date: 2026-08-15     Status: Accepted

## Context
Dropping the constraint and index around `DROP INDEX CONCURRENTLY` cannot be made transaction-atomic
without a gap in enforcement. Runtime least privilege must also remain after Down.

## Decision
Production owner Down deletes only the corrective history row in the same transaction that
re-attests the fully hardened state. The index, validated constraint, schema/default ACL posture,
and runtime least privilege remain. Destructive Down is supported only for attested-empty
disposable databases through EF.

## Consequences
Production rollback is safe and reversible but is not a physical schema reversal. Re-upgrade is an
idempotent attestation plus final history insert.

## Alternatives considered
- Drop objects in production — rejected because concurrent drop creates an unenforced,
  non-atomic interval.
- Restore broad historical privileges — rejected because it violates least privilege.

# ADR-4: Attest by namespace/object identity and exact schema/default ACL posture
Date: 2026-08-15     Status: Accepted

## Context
Textual `search_path`, `current_schema()`, and unqualified `to_regclass(...)` checks can resolve
temporary or hostile same-name objects. Relation-only grants also do not prove schema ownership,
effective CREATE denial, or safe privileges for future objects.

## Decision
Capture the validated target namespace OID once, qualify every fixed application identifier, and
attest objects by namespace/object OID plus canonical definition and ownership. Reject temporary
shadows. Require exact target-schema and default-ACL posture: PUBLIC has neither USAGE nor CREATE;
`tabruk_app` has direct USAGE but no effective CREATE or grant option; conflicting global owner
defaults fail closed.

## Consequences
The supported path is independent of ambient schema resolution and covers both current and future
objects. The owner scripts become more explicit and operators must repair hostile global defaults
or inherited privileges rather than having the migration silently rewrite them.

# ADR-5: Pin the complete production migration interface
Date: 2026-08-15     Status: Accepted

## Context
The corrective designer proves model intent, and production Down is now a separate owner script.
Omitting either from the manifest permits an unreviewed production interface or model artifact.

## Decision
`T8MigrationArtifacts.sha256` pins exactly seven artifacts: the immutable initial source/designer,
corrective source/designer, owner upgrade script, owner downgrade script, and model snapshot.

## Consequences
Any legitimate change to the T8 production interface requires an explicit manifest update and
review. The immutable initial artifacts remain unchanged.
