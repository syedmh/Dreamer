# Decisions

- 2026-08-15: Treat as a large security-sensitive database remediation. Requirements are explicit in the CTO directive, so no product-analysis phase is needed.
- 2026-08-15: Use only an isolated loopback PostgreSQL 18.6 cluster and remove its process/data directory after validation.

# ADR-1: Make the corrective migration the durable least-privilege and PostgreSQL-safe contract
Date: 2026-08-15     Status: Accepted

## Context
`20260815075156_InitialPostgresSchema` is immutable, but it grants `tabruk_app` on `ALL TABLES` and
`ALL SEQUENCES` before narrowing only the audit tables
(`src/HusayniaTabruk.Infrastructure/Migrations/20260815075156_InitialPostgresSchema.cs:1041-1087`).
The corrective migration currently uses transactional EF DDL for a concurrent-index problem and
duplicates the privilege table classification in tests
(`src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs:14-149`,
`tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:18-44`).

## Decision
We will keep the EF model unchanged, but we will:
- introduce one shared fixed-identifier privilege catalog consumed by both the corrective migration
  and persistence tests;
- rewrite the corrective migration source to use exact grant/revoke SQL only, a raw concurrent
  unique-index step with `suppressTransaction`, and a staged `NOT VALID` / `VALIDATE` chronology
  constraint;
- keep least privilege in corrective `Down` instead of restoring historical broad grants; and
- pin both migrations, both designers, and the snapshot with an external SHA-256 manifest rather
  than modifying the immutable initial migration.

## Consequences
Least privilege becomes the durable runtime contract even after downgrading to the initial migration
target. Retry behavior is explicit and PostgreSQL-correct, but the migration source becomes more
manual than EF-generated DDL. The manifest adds an upkeep step whenever pinned migration artifacts
legitimately change.

## Alternatives considered
- **Edit the initial migration** - rejected because the CTO explicitly forbade changing the initial
  migration source/designer.
- **Restore broad grants in corrective `Down`** - rejected because it would knowingly reintroduce
  history CRUD and violate the T8 least-privilege objective.
- **Derive tables/sequences dynamically from EF metadata or PostgreSQL catalog state at migration
  time** - rejected because T8 requires fixed SQL identifiers and shared exact classification.

# ADR-2: Attest migration state before EF reads history
Date: 2026-08-15     Status: Accepted

## Context
The immutable initial migration grants the application role broad table DML, including EF history.
A compromised `tabruk_app` can therefore insert the T8 migration ID on an initial-only database;
EF then skips T8 because current hardening and verification execute only inside that migration.
Current managed-index checks are also insufficient to authorize destructive downgrade because they
omit ownership and PostgreSQL semantic attributes.

## Decision
Extend the existing migration connection-open callback into an owner-only pre-history gate.
Classify pristine/bootstrap/initialized schemas from catalogs; for initialized schemas commit exact
least-privilege hardening before reading history; then require history, object, and effective
privilege state to agree. A T8 history row is accepted only with a fully attested T8 state.

Use one shared fixed-identifier attestation contract from the factory and corrective migration, with
the standalone owner script mirroring it. Before any downgrade drop, verify the complete normalized
index definition: owner, AM/state/flags, keys/types, operator classes, collations, per-key options,
relation options, null-distinct behavior, predicate, and absence of constraint attachment.

## Consequences
Forged history cannot bypass corrective enforcement, and destructive rollback is limited to the
managed object definition. Broad grants remain revoked even when later attestation fails.
Ambiguous partial-initial or interrupted-downgrade states now require explicit owner recovery; the
supported recovery is to reconstruct and attest T8 with the owner script, then retry.

## Alternatives considered
- **Keep checks only in T8** - rejected because EF skips the migration when history is forged.
- **Replace EF/Npgsql `IHistoryRepository` internals** - rejected as a larger, provider-version-
  coupled change when the existing connection-open seam runs earlier.
- **Automatically delete forged/suspicious history** - rejected because the tooling cannot prove
  provenance and must not make destructive guesses.
- **Treat name/key/predicate as sufficient before drop** - rejected because PostgreSQL ownership,
  opclass, collation, option, and null-distinct changes can produce a different same-name object.
