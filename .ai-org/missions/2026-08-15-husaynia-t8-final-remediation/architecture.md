# T8 remediation architecture

## Current state (FACTS)
- The API entry point is a thin minimal-hosting shell; persistence changes stay below the API layer (`src/HusayniaTabruk.Api/Program.cs:21-58`).
- Application code depends on an `IUnitOfWork` abstraction, and Infrastructure owns the implementation (`src/HusayniaTabruk.Application/Abstractions/Persistence/PersistencePorts.cs:7-11`, `src/HusayniaTabruk.Infrastructure/Persistence/Repositories/PostgresUnitOfWorkAndStores.cs:12-55`).
- Infrastructure is the only layer that carries EF Core/Npgsql dependencies, and the design-time factory takes its owner connection from `TABRUK_MIGRATIONS_CONNECTION` (`src/HusayniaTabruk.Infrastructure/HusayniaTabruk.Infrastructure.csproj:3-16`, `Directory.Packages.props:8-13`, `src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContextFactory.cs:10-33`).
- The EF model already declares the signup waitlist unique index and chronology check, and the current snapshot/designer already encode them (`src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContext.cs:302-334`, `src/HusayniaTabruk.Infrastructure/Migrations/TabrukDbContextModelSnapshot.cs:1045-1064`, `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.Designer.cs:1050-1067`).
- Audit and privileged-access events are EF-mapped tables and therefore belong in any privilege classification, not in ad-hoc SQL only (`src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContext.cs:538-575`).
- The initial migration grants `tabruk_app` on `ALL TABLES` and `ALL SEQUENCES` in the schema, then narrows only the audit tables; it does not explicitly protect `__EFMigrationsHistory` (`src/HusayniaTabruk.Infrastructure/Migrations/20260815075156_InitialPostgresSchema.cs:1041-1087`).
- The corrective migration currently uses EF `CreateIndex`/`AddCheckConstraint` inside the default migration transaction and still contains broad privilege SQL; its `Down` restores the original broad posture (`src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs:14-149`).
- Owned-transaction cleanup is duplicated and currently lets rollback/dispose failures replace the primary exception in write and repeatable-read wrappers (`src/HusayniaTabruk.Infrastructure/Persistence/Repositories/PostgresUnitOfWorkAndStores.cs:33-53`, `src/HusayniaTabruk.Infrastructure/Persistence/Repositories/PersistenceWriteSupport.cs:141-207`, `src/HusayniaTabruk.Infrastructure/Persistence/Repositories/PostgresAggregateRepositories.cs:47-64`, `:553-570`, `:977-994`).
- The real-PostgreSQL fixture creates isolated schemas, requires a DBA-preprovisioned `tabruk_app` NOLOGIN role, and migrates with `context.Database.MigrateAsync()` (`tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresIntegrationSupport.cs:71-137`).
- Existing persistence tests duplicate the runtime table allowlist locally, assert hardening after corrective `Up`, currently expect downgrade-to-initial to restore broad history/sentinel access, and already run a real cancellation rollback regression plus a PostgreSQL 18.6 environment check (`tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:15-44`, `:67-139`, `:828-896`; `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresIndependentGateTests.cs:83-120`, `:194-303`, `:350-363`).
- Safety documentation already records the migration-owner/runtime-role split for the initial migration only (`src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md:3-50`).
- Verified by repo search: no hash manifest exists yet for the migration source/designer/snapshot set (`rg 'sha256|SHA256|HashAlgorithm|manifest'` under `HusayniaTabruk` returned no matches).

## Desired state
Keep T8 as an Infrastructure-only remediation. The EF model stays as-is, but the corrective migration becomes PostgreSQL-safe and retryable, `tabruk_app` remains exact least privilege on both `Up` and corrective `Down`, and persistence transaction wrappers stop masking the original thrown exception when cleanup also fails.

## Delta
1. **Shared exact privilege catalog**  
   Add one Infrastructure-owned catalog (for example `Migrations/PostgresLeastPrivilegeCatalog.cs`) that enumerates:
   - `CrudTables` = exact EF-mapped runtime tables
   - `InsertOnlyTables` = `audit_events`, `privileged_access_events`
   - `DeniedTables` = `__EFMigrationsHistory`
   - `UsageSelectSequences` = exact Identity sequences  
   Migration code and tests must both consume this catalog; no duplicated table arrays remain in tests.

2. **Corrective migration source only**  
   Keep `TabrukDbContext`, the initial migration, and the initial designer unchanged. Rewrite only `20260815102612_T8CorrectivePostgresHardening.cs` so that:
   - privilege hardening uses only exact identifiers from the shared catalog;
   - the waitlist index is created with raw `CREATE UNIQUE INDEX CONCURRENTLY ...` in `migrationBuilder.Sql(..., suppressTransaction: true)`;
   - the chronology constraint is added with exact `ALTER TABLE ... ADD CONSTRAINT ... NOT VALID` and then `VALIDATE CONSTRAINT` in separate transactional steps;
   - `Down` drops the index with `DROP INDEX CONCURRENTLY` and re-applies the same exact least-privilege posture instead of restoring `ALL TABLES/ALL SEQUENCES`.

3. **Shared transaction-cleanup semantics**  
   Centralize owned-transaction cleanup in one Infrastructure helper and route `PostgresUnitOfWork`, `PersistenceWriteSupport`, and the repeatable-read wrappers through it. Contract: if the operation body throws/cancels and rollback/dispose also fails, rethrow the original exception and attach cleanup failures to `Exception.Data`; do not change `Result.Failure` contracts.

4. **Integrity manifest**  
   Add an external SHA-256 manifest in `src/HusayniaTabruk.Infrastructure/Migrations/` covering exactly:
   - `20260815075156_InitialPostgresSchema.cs`
   - `20260815075156_InitialPostgresSchema.Designer.cs`
   - `20260815102612_T8CorrectivePostgresHardening.cs`
   - `20260815102612_T8CorrectivePostgresHardening.Designer.cs`
   - `TabrukDbContextModelSnapshot.cs`  
   This verifies immutable artifacts without editing the initial migration.

5. **Documentation**  
   Extend `InitialPostgresSchema.safety.md` to describe the post-T8 operating contract: migration owner runs `Up`/`Down`; runtime `tabruk_app` never receives history CRUD; downgrade to the initial migration target removes only the corrective index/constraint and keeps hardened privileges; include the manifest update/check procedure.

## Contracts
- **No API/domain contract change:** no handler, route, DTO, or domain behavior changes.
- **Privilege contract:** `tabruk_app` has CRUD only on `CrudTables`, INSERT-only on `InsertOnlyTables`, USAGE+SELECT only on `UsageSelectSequences`, and no rights on `__EFMigrationsHistory`.
- **Coverage contract:** `CrudTables ∪ InsertOnlyTables` must equal the set of EF-mapped table names exactly once; tests derive the EF set from the model and fail on drift.
- **Migration failure contract:**
  - duplicate waitlist data fails before index creation with a `23505`/`ux_signups_waitlisted_order_per_help_need` style failure;
  - invalid chronology may leave the exact check constraint present but not validated; the migration remains unapplied and retry after data repair is supported;
  - corrective `Down` is backward compatible for runtime code but intentionally stricter than the historical initial grant posture.

## Safe migration / rollback plan
- **Forward (`Initial` -> `Corrective`):**
  1. apply exact privilege hardening from the shared catalog;
  2. preflight duplicate waitlist positions with exact SQL;
  3. create the unique waitlist index concurrently outside the EF transaction;
  4. add the chronology constraint as `NOT VALID`;
  5. validate the chronology constraint.
- **Retry after failed `Up`:**
  - duplicates: fix data, rerun migration; no contract/model drift is needed;
  - chronology: fix data, rerun; the existing named constraint may already exist and must be reused/validated, not duplicated.
- **Backward (`Corrective` -> `Initial` target):**
  1. drop the chronology constraint if present;
  2. drop the waitlist index concurrently;
  3. re-apply the exact least-privilege catalog.  
  This is schema-compatible and keeps runtime least privilege intact.

## Test plan
- **Fast, no-Postgres tests**
  - privilege-catalog parity test: EF-mapped tables == shared catalog exactly;
  - manifest test: recompute SHA-256 for the five pinned files and compare to the manifest;
  - cleanup helper tests: rollback failure and dispose failure preserve the original thrown exception/cancellation and expose cleanup failures in `Exception.Data`.
- **Real PostgreSQL tests**
  - update `PostgresMigrationAndSchemaTests` to consume the shared catalog instead of `RuntimeTables`;
  - change downgrade assertions so `__EFMigrationsHistory` remains denied after migrating back to `InitialMigration`;
  - assert duplicate-data failure leaves the migration unapplied and no concurrent index;
  - assert chronology-validation failure leaves the migration unapplied and the exact constraint present with `convalidated = false`;
  - keep the existing cancellation regression and fresh up/down/up + initial-to-corrective up/down/re-up flows on PostgreSQL 18.6.

## Security invariants
- `tabruk_app` is always the least-privilege runtime role; migration-owner credentials are the only DDL/grant authority.
- No broad `ALL TABLES`, `ALL SEQUENCES`, or discovery-driven `pg_get_serial_sequence` grants remain in the corrective path.
- Fixed identifier lists are the only source of SQL object names used for T8 grants/revokes.

## Tradeoffs
- **Chosen:** one explicit catalog and raw PostgreSQL DDL. This is more verbose than EF helpers but matches PostgreSQL transaction rules and removes drift between migration/tests.
- **Rejected:** editing the initial migration (forbidden/immutable); leaving downgrade broad grants in place (violates least privilege); deriving grant targets dynamically from EF metadata or catalog queries at migration time (breaks the fixed-identifier requirement).

## Risks
- A manually interrupted owner session during `CREATE INDEX CONCURRENTLY` can still leave an invalid index object; document the exact named `DROP INDEX CONCURRENTLY` repair path.
- The external hash manifest adds maintenance overhead whenever the corrective migration source/designer/snapshot legitimately change.
- Tightening downgrade privileges may break ad-hoc manual SQL that was relying on access the application role should never have had; this is intentional and should be called out in the safety doc.

---

# T8 forged-history replan (binding override)

This section supersedes the earlier migration-entry and object-attestation portions of this
document. The initial migration, EF model, API, domain, and runtime application contract remain
unchanged.

## Current state (verified FACTS)

- `TabrukDbContextOptions.Create(string)` owns the migration connection, registers an Npgsql
  `StateChange` callback, and configures the schema-qualified EF history table. The callback
  currently validates only schema routing, before EF migration commands execute
  (`src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContextFactory.cs:77-145`).
- The immutable initial migration grants `tabruk_app` CRUD on all current schema tables. Because EF
  creates `__EFMigrationsHistory` before applying migrations, an initial-only database can expose
  history mutation to the compromised application role
  (`src/HusayniaTabruk.Infrastructure/Migrations/20260815075156_InitialPostgresSchema.cs:1041-1066`).
- Corrective `Up` hardens privileges only inside T8. EF does not execute it when a forged T8
  `MigrationId` is already present (`src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs:247-316`).
- The owner script does not skip its DDL based on the T8 row and writes history last, but it reads
  initial history before privilege hardening and has no final, complete post-DDL attestation
  (`src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql:10-70`,
  `:279-307`).
- Existing index checks cover namespace, relation kind, table, access method, readiness, key count,
  key order, and predicate. They omit owner, operator classes, collations, per-key options,
  relation options, constraint linkage, and `indnullsnotdistinct`
  (`src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs:47-246`;
  `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:1640-1710`).
- Real PostgreSQL tests already exercise normal up/down/re-up, retry debris, wrong same-name
  objects, owner-script idempotency, schema routing, and fail-closed privileges
  (`tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:145-607`,
  `:752-1000`).
- The independent security gate is failed by one High forged-history bypass and one Medium
  incomplete-index-attestation finding
  (`.ai-org/missions/2026-08-15-husaynia-t8-final-remediation/security-review.md:1-20`).

## Desired state and EF integration

Keep the public signature `TabrukDbContextOptions.Create(string)` unchanged. Extend its existing
connection-open hook into an owner-only **pre-history gate**; do not replace EF internals or add a
dependency.

On every migration connection transition to `Open`, before EF can issue its first history query:

1. run the existing exact schema preflight;
2. inspect only PostgreSQL catalogs to classify the target as:
   - **pristine**: no history relation, initial managed objects, or T8 objects;
   - **EF bootstrap**: an empty history relation exists, but no initial managed objects exist; or
   - **initialized**: the complete fixed initial table/sequence inventory exists;
   every mixed/partial initial state fails closed;
3. for pristine, return; for EF bootstrap, revoke all privileges on the history table from
   `tabruk_app` and `PUBLIC`, then return;
4. for initialized state, execute `ApplyRuntimeLeastPrivilegeSql` as a separate autocommit command
   so a later attestation exception cannot roll back the revokes;
5. only after step 4, read known history rows and attest catalog state:
   - initial history must exist with the expected product version;
   - if T8 history is absent, accept only absent or exact retryable T8 objects (the exact index must
     be valid; the exact constraint may be absent, unvalidated, or validated);
   - if T8 history is present, require the complete final T8 index, validated constraint, and exact
     effective privilege matrix;
   - any unknown same-name object or applied/history-to-catalog mismatch raises SQLSTATE `P0001`
     with fixed prefix `T8 migration state attestation failed:` and no connection string or data.

The hook is the ordering boundary: privilege revocation commits before EF determines pending or
applied migrations. A forged T8 row therefore causes EF migration startup to fail unless the real
corrective state fully attests.

## Exact implementation delta

1. **Catalog/attestation**
   - Extend the existing migration catalog (or add one adjacent internal attestation class) with
     fixed initial inventory, history IDs/product version, shared schema-state SQL, retryable object
     preflight SQL, final object SQL, and privilege-attestation SQL.
   - Preserve fixed identifiers; no caller/user text is interpolated into SQL.
   - Final index attestation must verify: schema/name, target table/schema, `relkind = 'i'`,
     index owner equals table owner, btree AM, unique/not primary/not exclusion, immediate,
     valid/ready/live, not clustered/replident, three keys and no INCLUDE/expression columns,
     key names/types/order, `pg_catalog.uuid_ops`, `pg_catalog.uuid_ops`,
     `pg_catalog.int8_ops`, zero collations, zero `indoption` values (ASC/NULLS LAST), empty
     `reloptions`, `indnullsnotdistinct = false`, exact predicate, and no `pg_constraint.conindid`
     attachment.
   - Final constraint attestation must verify the existing table/schema/type/expression checks plus
     table ownership, local/non-inherited status, no parent constraint, and `convalidated = true`.
   - Effective privileges for `tabruk_app` and `PUBLIC` must equal the catalog contract, including
     absence of TRUNCATE/REFERENCES/TRIGGER and all history rights; sequences must have only the
     expected USAGE/SELECT rights. `tabruk_app` must still exist as `NOLOGIN`.

2. **Factory/options**
   - Replace `ValidateSchemaOnOpen` with the ordered pre-history gate above, retaining the existing
     `StateChange` integration and unchanged `Create(string)` contract.
   - Use separate commands for classification, hardening, and attestation. Never combine hardening
     and a potentially failing attestation in one transaction.

3. **Corrective migration**
   - Consume the shared retryable preflight in `Up`.
   - Consume the shared **final** attestation after constraint validation and before EF records T8.
   - In `Down`, harden first, require final attestation before either drop, keep wrong same-name
     objects untouched, then harden again. The schema contract remains index/constraint absent at
     the initial target and present/validated at the T8 target.

4. **Owner script**
   - Order: schema preflight; initial-inventory classification; privilege hardening; initial-history
     verification; retryable object preflight; DDL/validation; final object and privilege
     attestation; idempotent T8 history insert last.
   - Ignore a pre-existing T8 row as proof. The script must repair an otherwise legitimate
     initial/retry state, attest it, and only then leave or insert the row.

5. **Tests/docs/manifest**
   - Update the artifact manifest for every changed scoped artifact.
   - Extend safety documentation with forged-history failure and recovery, interrupted-downgrade
     recovery, and the pre-history ordering guarantee.

## Contracts and state transitions

- **Fresh `0 -> latest`:** pristine/bootstrap states are allowed; initial then T8 execute normally.
- **Initial-only:** opening the owner EF context commits least privilege before EF reads history;
  T8 then runs.
- **Forged T8 + incomplete state:** owner EF fails before pending/applied evaluation; privileges
  remain hardened and objects/history remain untouched.
- **Forged T8 + fully correct state:** the state, not the row, is trusted; EF may no-op.
- **T8 history absent + exact retry debris:** EF may rerun T8 idempotently and record history.
- **Legitimate T8 applied:** full attestation passes; normal no-op or downgrade proceeds.
- **Interrupted downgrade with T8 history but missing objects:** fail closed. Run the owner script to
  reconstruct/attest T8, then retry downgrade.
- **Wrong same-name object:** both EF and owner script fail without drop/replace. DBA repair remains
  explicit.

## Required PostgreSQL 18.6 tests

Add focused hostile tests for:

1. initial-only database where `SET ROLE tabruk_app` inserts T8 history; owner EF must fail before
   EF trusts it, harden history privileges, preserve the row, and create/drop no T8 object;
2. the same forged state recovered by the owner script, followed by successful EF no-op;
3. forged T8 with exact full objects but hostile grants; the gate must harden, fully attest, and
   permit no-op;
4. T8 history absent with exact validated and unvalidated retry states; EF reuses/validates and
   records history;
5. applied T8 with an absent/partial object (interrupted downgrade simulation); EF downgrade and
   upgrade both fail until owner-script reconstruction;
6. downgrade hostile variants for wrong owner/definition where constructible, `DESC`/NULL ordering,
   `NULLS NOT DISTINCT`, non-empty index options, expression/include keys, wrong predicate, and
   constraint attachment; every variant remains untouched and preserves T8 history;
7. positive catalog assertions for exact owner, operator classes, collations, options,
   null-distinct semantics, and constraint ownership/validation;
8. existing fresh, initial-to-T8, up/down/up, duplicate, chronology, schema-routing, privilege, and
   owner-script idempotency tests remain green.

## Failure, recovery, concurrency, and observability

- Revokes are committed before any later failure; no automatic rollback restores broad grants.
- Attestation is idempotent and read-only after hardening. It performs no automatic hostile-object
  repair.
- Existing EF migration locking remains authoritative after the gate. Deployments remain
  single-writer; concurrent owner script and EF migration execution is unsupported.
- Fixed SQLSTATE/message categories identify schema, inventory, history, privilege, index, and
  constraint failures without sensitive values.

## Tradeoffs and risks

- **Chosen:** extend the existing connection-open seam. It is the smallest mechanism guaranteed to
  run before EF history access and avoids fragile replacement of Npgsql/EF internal
  `IHistoryRepository` services.
- **Rejected:** checking only inside T8, because forged history skips T8; deleting suspicious
  history automatically, because provenance is ambiguous; trusting the owner script alone, because
  supported EF migration must also fail closed.
- The gate intentionally blocks ambiguous partial-initial and interrupted-downgrade states.
  Recovery is explicit owner action, prioritizing security over unattended repair.
- No breaking application API/schema change, destructive migration, new service/dependency, or
  accepted security exception is introduced; CTO escalation is not required.
