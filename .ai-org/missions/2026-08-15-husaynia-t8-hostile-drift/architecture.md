# T8 hostile-drift remediation architecture

## Status and scope

Design only. Scope is limited to the corrective migration and designer, owner SQL, least-privilege
catalog, design-time factory, snapshot/manifest when justified, PostgreSQL persistence tests, and
migration safety documentation. The initial migration source and designer are immutable.

## Current state — verified facts

- **FACT:** `TabrukDbContextFactory` requires `TABRUK_MIGRATIONS_CONNECTION` but passes it directly
  to Npgsql without validating or pinning a target schema
  (`src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContextFactory.cs:12-18,27-30`).
- **FACT:** corrective `Up` applies role grants first, but only rejects an existing index when it is
  invalid; `CREATE ... IF NOT EXISTS` can therefore accept a valid, wrong same-name index
  (`.../Migrations/20260815102612_T8CorrectivePostgresHardening.cs:12-15,39-60`).
- **FACT:** corrective `Up` skips a same-name constraint based only on table/name, without checking
  constraint type or expression (`...T8CorrectivePostgresHardening.cs:63-79`).
- **FACT:** corrective `Down` drops the named objects without verifying their definitions
  (`...T8CorrectivePostgresHardening.cs:91-105`).
- **FACT:** the least-privilege catalog revokes PUBLIC only from the two insert-only audit tables;
  it does not revoke PUBLIC from CRUD tables, migration history, or sequences
  (`.../Migrations/PostgresLeastPrivilegeCatalog.cs:86-94`).
- **FACT:** the owner script validates a target GUC and one search-path entry, but strips quotes and
  does not verify complete existing index/constraint definitions before skipping them
  (`...T8CorrectivePostgresHardening.owner-idempotent.sql:8-41,92-124`).
- **FACT:** the snapshot and corrective designer already describe the intended unique partial index
  and chronology check (`.../Migrations/TabrukDbContextModelSnapshot.cs:1045-1064`;
  `...T8CorrectivePostgresHardening.Designer.cs:1050,1067`).
- **FACT:** integration fixtures create isolated schemas and set `SearchPath`, but do not set an
  independent expected-schema marker (`tests/.../PostgresIntegrationSupport.cs:69-110`).
- **FACT:** privilege assertions use effective `has_*_privilege` checks, but current tests do not
  seed hostile PUBLIC grants (`tests/.../PostgresMigrationAndSchemaTests.cs:1062-1225`).
- **FACT:** the manifest currently pins six files, including the unchanged corrective designer and
  snapshot (`.../Migrations/T8MigrationArtifacts.sha256:1-6`;
  `tests/.../PostgresMigrationArtifactManifestTests.cs:8-48`).
- **FACT:** PostgreSQL tests may skip when the database or `psql` is unavailable; the real gate must
  supply both dependencies to achieve zero skips
  (`tests/.../PostgresIntegrationSupport.cs:29-52`;
  `tests/.../PostgresMigrationAndSchemaTests.cs:1290-1320`).
- **FACT:** the Compose database image is PostgreSQL 18.6
  (`docker-compose.yml:4-5`).
- **FACT:** current immutable hashes match the manifest: initial source
  `ad9bc814...db2829`, initial designer `2fd38406...26bc1`, corrective designer
  `e71f719a...dfd30`, snapshot `f8143017...06932b5`.
- **FACT:** repository lineage cannot be verified because this project is untracked
  (`.ai-org/missions/2026-08-15-husaynia-t8-hostile-drift/mission.md:8`).

## Desired state and frozen contracts

### 1. One explicit schema contract for every EF migration command

`TABRUK_MIGRATIONS_CONNECTION` must include both:

```text
Search Path=<schema>;Options=-c tabruk.target_schema=<schema>
```

The factory rejects before returning a context when either value is missing, blank, repeated,
multi-valued, or unequal. `<schema>` is one unquoted ASCII PostgreSQL ordinary identifier matching
`^[a-z_][a-z0-9_$]{0,62}$`; commas, whitespace, quotes, `$user`, `pg_catalog`,
`information_schema`, and `pg_*` schemas are rejected. `public` is explicitly allowed. Other
connection-string properties remain supported.

The factory configures EF's migrations-history table as
`MigrationsHistoryTable("__EFMigrationsHistory", expectedSchema)`. This makes upgrade and downgrade
history discovery target the declared schema rather than ambient `search_path`.

Both corrective `Up` and `Down` begin with fixed SQL that requires:

1. exactly one server `search_path` entry;
2. `tabruk.target_schema` to be present and valid under the same identifier grammar;
3. normalized `search_path = tabruk.target_schema`; and
4. `current_schema() = tabruk.target_schema`.

Failure raises a fixed `P0001` exception containing expected/actual schema values, never the
connection string. The owner script uses the identical rules. These checks contain no dynamic DDL
and interpolate no user value into an identifier.

### 2. Existing-object verification contract

All validations occur before duplicate-data scans or corrective DDL. Absence permits creation;
presence permits skipping only when every required attribute matches.

**Index `ux_signups_waitlisted_order_per_help_need`:**

- namespace is the expected/current schema;
- relation kind is index and indexed table is that schema's `signups`;
- access method is `btree`;
- `indisunique`, `indisvalid`, `indisready`, and `indislive` are true;
- exactly three key attributes and no INCLUDE/expression attributes;
- ordered keys are `organization_id`, `help_need_id`, `waitlist_order`;
- `pg_get_expr(indpred, indrelid)` is PostgreSQL 18.6's canonical `(status = 2)`.

**Constraint `ck_signups_transition_chronology`:**

- namespace/table is the expected schema's `signups`;
- `contype = 'c'`, `connoinherit = false`;
- `pg_get_expr(conbin, conrelid)` is PostgreSQL 18.6's canonical
  `((last_transition_at IS NULL) OR (last_transition_at >= submitted_at))`.

`convalidated = false` is a retryable correct state and proceeds to validation. Any other mismatch
raises `P0001` naming the object and mismatched category. The migration and owner script never drop,
rename, replace, or repair a hostile object automatically.

`Down` applies the same verification before dropping either object. An absent object is idempotent;
a wrong same-name object blocks downgrade and remains untouched.

### 3. Failure ordering and retry behavior

**Up / owner upgrade order:**

1. validate schema contract;
2. revoke/regrant the complete privilege catalog outside the EF transaction;
3. validate both existing named objects;
4. reject duplicate waitlist positions;
5. create the unique index concurrently only when absent;
6. add the check `NOT VALID` only when absent;
7. validate the check when unvalidated;
8. let EF insert, or owner SQL insert, corrective migration history last.

Thus hostile drift and data errors never produce a corrective history row. A chronology failure may
leave only the verified index and an unvalidated correct constraint; an interrupted concurrent
index may leave an invalid index. Both states are documented owner-repair/retry states. Invalid or
wrong-definition objects are never auto-dropped.

**Down order:**

1. validate schema contract;
2. apply complete privilege hardening;
3. verify both objects if present;
4. drop the verified constraint;
5. drop the verified index concurrently;
6. reapply complete privilege hardening;
7. let EF delete corrective history only after successful `Down`.

The early hardening makes a failed downgrade fail closed; the final hardening establishes the
successful post-Down contract.

### 4. Privilege contract

Generate only fixed, quoted identifiers from `PostgresLeastPrivilegeCatalog`:

- revoke ALL table privileges from `tabruk_app` **and PUBLIC** on every CRUD table, insert-only
  table, and `__EFMigrationsHistory`;
- revoke ALL sequence privileges from `tabruk_app` **and PUBLIC** on both catalogued sequences;
- grant `tabruk_app` CRUD only on `CrudTables`;
- grant `tabruk_app` INSERT only on `InsertOnlyTables`;
- grant `tabruk_app` USAGE and SELECT only on `UsageSelectSequences`;
- grant nothing on migration history.

Apply the same generated statement set in `Up`, twice in `Down` as ordered above, and verbatim in
the owner script. This closes PUBLIC inheritance without changing schema ownership or role
membership policy.

### 5. File-level delta

- `20260815102612_T8CorrectivePostgresHardening.cs`: add shared fixed schema/object preflights,
  reorder `Up`, and make `Down` verify-before-drop and harden before/after.
- `20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql`: mirror all contracts and keep
  top-level `CREATE INDEX CONCURRENTLY` through `\gexec`; history insertion remains last.
- `PostgresLeastPrivilegeCatalog.cs`: expand PUBLIC revocation to all managed tables and sequences.
- `TabrukDbContextFactory.cs`: strict connection contract and schema-qualified EF history table.
- `PostgresIntegrationSupport.cs`: construct test migration connections with matching Search Path
  and target GUC; add helpers for deliberately hostile variants without changing production code.
- `PostgresMigrationAndSchemaTests.cs`: add EF and owner hostile-object, PUBLIC-grant, schema-matrix,
  history-order, retry, fresh/upgrade/down/re-up coverage.
- `PostgresLeastPrivilegeCatalogTests.cs`: assert complete PUBLIC statements and exact owner-script
  parity.
- `PostgresMigrationArtifactManifestTests.cs` and `T8MigrationArtifacts.sha256`: enforce the hash
  policy below.
- `InitialPostgresSchema.safety.md`: document strict connection examples, validation errors,
  large-table locks/scans, capacity/monitoring, failure debris, repair, retry, and rollback.
- Corrective designer and snapshot: do not modify unless EF model metadata actually changes; this
  remediation is SQL/configuration-only and currently requires no model change.

### 6. Hostile test matrix

Run against real PostgreSQL 18.6, with `TABRUK_TEST_POSTGRES_CONNECTION` and `psql` available so the
persistence result reports zero skipped tests:

- EF Up and owner script each reject a same-name non-unique index and a same-name permissive check;
  assert history absent and hostile object unchanged.
- Correct existing validated objects pass idempotently; a correct unvalidated check validates.
- Up failure and owner failure both occur before history insertion.
- Seed PUBLIC grants on a CRUD table, each audit table, migration history, and each sequence
  (including UPDATE); assert the effective role matrix after Up, successful Down, failed Down, owner
  first run, and owner rerun.
- Factory tests reject missing Search Path, missing target option, multiple schemas, invalid/quoted
  schema, and target mismatch.
- Real EF Up and downgrade each reject missing, multi-schema, mismatch, and unresolved target
  schema, with history unchanged.
- Fresh apply, initial-to-corrective upgrade, corrective Down, re-Up, chronology retry, interrupted
  invalid-index rejection, and owner re-execution pass.
- Catalog tests verify schema/table, uniqueness, ordered keys, predicate, constraint type/expression,
  and exact fixed SQL; no assertion relies only on object name.

### 7. Documentation and operations

The safety document must state that duplicate detection and constraint validation scan `signups`;
concurrent unique-index creation performs multiple scans and needs temporary disk/headroom; `ADD
CONSTRAINT NOT VALID` takes a brief `ACCESS EXCLUSIVE` lock; validation takes a
`SHARE UPDATE EXCLUSIVE` lock; concurrent create/drop waits on conflicting transactions and blocks
conflicting DDL, not ordinary reads/writes. Operators must measure table size, verify free disk,
inspect long transactions, schedule a low-DDL window, monitor `pg_stat_progress_create_index`, and
set deployment-specific `lock_timeout`/`statement_timeout` rather than embedding universal values.

Rollback is corrective Down only after definition verification. A failed nontransactional apply is
repaired and retried; operators must not manually insert migration history. Full downgrade to `0`
remains restricted to known-empty non-production databases.

## Manifest/hash policy

The manifest is an allowlist, not a directory snapshot:

- always pin the two immutable initial migration inputs at their currently verified hashes;
- pin each approved T8 runtime migration artifact modified by this remediation:
  corrective migration, owner SQL, and least-privilege catalog;
- pin the corrective designer and model snapshot only if their contents legitimately change;
- do not pin tests, factory, or prose documentation.

For this SQL/config-only design, the intended exact set is the two initial files plus the three
modified runtime migration artifacts. The unchanged corrective designer and snapshot are removed
from the manifest, remain unmodified, and are protected by model/snapshot tests rather than being
misrepresented as modified corrective artifacts. Hash updates occur only after implementation and
verification; any initial-file hash change blocks completion.

## Cross-cutting concerns

- **Idempotency:** only absent verified objects are created; correct existing objects are reused;
  privilege statements and history insertion are repeatable.
- **Concurrency:** index create/drop remains top-level and concurrent. No new transaction wrapper,
  service, dependency, queue, or lock coordinator is introduced.
- **Security boundary:** migration-owner/DBA connections are trusted to perform DDL; `tabruk_app`
  remains NOLOGIN and receives only catalogued privileges. PUBLIC is untrusted.
- **Observability:** fixed PostgreSQL exceptions expose stage/object/schema, not credentials.
  Existing command/test output is sufficient; no application telemetry change is in scope.
- **Compatibility:** database model and application APIs are unchanged. Migration commands that
  relied on ambient, quoted, multi-schema, or mismatched paths intentionally become invalid.

## Tradeoffs and risks

- Strict simple schema names reject valid exotic PostgreSQL identifiers, optimizing auditability
  and injection resistance over configurability.
- Schema-qualified history plus SQL preflight duplicates validation, but covers EF history lookup
  and direct migration execution without a new EF service/interceptor.
- Canonical-expression comparisons are PostgreSQL 18.6-specific; the version is already a hard gate.
- Nontransactional concurrent DDL can leave documented retry debris; removing concurrency would
  increase large-table write outage and is rejected.
- Revoking PUBLIC may affect unknown non-`tabruk_app` consumers. This is intentional least
  privilege; deployment must inventory such consumers before rollout.
- The untracked-project provenance limitation remains a residual process risk and cannot be fixed
  within the no-git constraint.

## Alternatives rejected

- `CREATE ... IF NOT EXISTS` without definition checks: silently accepts hostile drift.
- Auto-drop/recreate wrong objects: destructive, increases outage, and can erase operator-owned
  objects.
- Trusting factory validation alone: direct contexts/owner SQL could bypass it.
- Trusting migration SQL alone: EF downgrade may inspect the wrong history schema before `Down`.
- New migration/interceptor/service: larger surface than correcting the existing T8 artifacts.
- Editing the initial migration: violates immutability and upgrade compatibility.

---

# Binding rework override: attest state before EF reads history

The independent security gate proved that the immutable initial migration can leave
`tabruk_app` able to forge the corrective migration-history row. Checks inside T8 are therefore
too late: EF may decide T8 is already applied and never execute them.

Extend the existing Npgsql connection-open hook used by `TabrukDbContextOptions.Create` into an
owner-only pre-history gate:

1. validate the fixed schema contract;
2. classify the target as pristine, EF bootstrap, complete initial inventory, or invalid partial
   state using fixed catalog SQL;
3. for bootstrap/initialized states, commit the exact PUBLIC/`tabruk_app` revokes before any
   history read;
4. read known history only after hardening;
5. trust a T8 history row only when the complete T8 object and effective-privilege state attests;
6. fail fixed-message/SQLSTATE `P0001` on any history/catalog mismatch without deleting history or
   altering hostile objects.

The gate must allow fresh bootstrap, initial-only upgrade, exact retry debris, fully correct
already-applied state, and legitimate downgrade. A forged row with incomplete T8 state and an
interrupted downgrade with history/object mismatch fail closed. The owner script is the supported
reconstruction path before retry.

Before accepting or dropping the managed index, additionally attest owner equals table owner,
operator classes, collations, per-key options, relation options, `indnullsnotdistinct`, primary /
exclusion / immediate / clustered / replica-identity flags, absence of constraint attachment, key
types/order, and all existing namespace/table/predicate/state checks. The constraint attestation
also covers owner, locality/inheritance, parent linkage, and validation state.

Factory, corrective migration, and owner script share or exactly mirror this fixed-identifier
attestation contract. Hardening commits separately before any potentially failing attestation.
History remains last for forward apply, and downgrade still verifies before destructive drops.

Required new PostgreSQL 18.6 evidence includes forged-history failure before EF trust, privilege
hardening surviving that failure, owner-script recovery, fully attested forged-row no-op, missing
history with retryable exact objects, interrupted-downgrade mismatch, and hostile index semantic
variants that remain untouched.
