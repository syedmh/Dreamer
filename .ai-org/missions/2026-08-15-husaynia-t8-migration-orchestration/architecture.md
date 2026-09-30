# T8 production migration orchestration

## Recommended decision

Freeze the checked-in owner `psql` orchestration as the **only supported production T8 upgrade
and downgrade interface**. EF migration execution remains available only for explicitly marked
disposable databases; it is not a production upgrade, rollback, or recovery mechanism.

Status: **APPROVED and binding for T8M implementation.**

This is the smallest design that closes the history TOCTOU while retaining
`CREATE/DROP INDEX CONCURRENTLY`: one PostgreSQL session can hold a session advisory lock across
all transaction boundaries and can compensate nontransactional work before releasing that lock.

## Current state — verified facts

- **FACT:** T8 `Up` and `Down` contain multiple `suppressTransaction: true` phases, including
  concurrent index create/drop (`src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs:12-19,47-51,103-131`).
- **FACT:** the owner upgrade is four committed transactions separated by top-level concurrent
  index creation; history is inserted in the last transaction
  (`...T8CorrectivePostgresHardening.owner-idempotent.sql:8,647,649,1039,1041-1045,1974-1976,2889-2907`).
- **FACT:** the factory commits hardening at line 201, starts a new attestation transaction at
  line 204, commits it at line 272, and then releases all transaction advisory/table locks before
  EF reads history (`src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContextFactory.cs:128-204,272-275`).
- **FACT:** the catalog uses a transaction advisory lock and many unqualified `to_regclass(...)`
  resolutions; textual `search_path`/`current_schema()` checks therefore do not exclude implicit
  `pg_temp` resolution (`.../Migrations/PostgresLeastPrivilegeCatalog.cs:23,64,471-501,764-789,1099-1131`).
- **FACT:** fixed table locks are schema-qualified only in selected factory helpers; migration and
  owner SQL still issue unqualified fixed relation DDL/locks
  (`.../PostgresLeastPrivilegeCatalog.cs:210-213`;
  `...T8CorrectivePostgresHardening.owner-idempotent.sql:1041`).
- **FACT:** the catalog has no `has_schema_privilege` or `pg_default_acl` attestation; current
  checks cover relation/column ACLs, not schema ownership, effective CREATE, or future-object
  defaults (`.../PostgresLeastPrivilegeCatalog.cs:510-704`).
- **FACT:** the manifest test requires five artifacts and explicitly excludes the corrective
  designer (`tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationArtifactManifestTests.cs:8-16,44-52`).
- **FACT:** operations documentation currently supports EF production apply and destructive EF
  corrective rollback, and explicitly accepts retry debris after nontransactional failure
  (`.../Migrations/InitialPostgresSchema.safety.md:174-188,301-315`).
- **FACT:** the corrective designer contains the intended partial unique index and chronology
  constraint (`.../Migrations/20260815102612_T8CorrectivePostgresHardening.Designer.cs:1045-1072`).
- **FACT:** Infrastructure already depends on EF/Npgsql; no new runtime package is required
  (`src/HusayniaTabruk.Infrastructure/HusayniaTabruk.Infrastructure.csproj:1-18`;
  `Directory.Packages.props:8-13`).

## Target contracts

### Supported interfaces

| Environment | Upgrade | Downgrade |
|---|---|---|
| Production/shared | `psql ... -v target_schema=<schema> -f ...owner-idempotent.sql` | `psql ... -v target_schema=<schema> -f ...owner-downgrade.sql` |
| Disposable/local/CI | EF `database update` with `tabruk.disposable_ef=on` | EF destructive `Down`, only after empty-database attestation |

`TabrukDbContextFactory` must reject EF execution unless the connection has exactly one validated
target schema **and** `Options=-c tabruk.disposable_ef=on`. T8 EF `Up`/`Down` repeat that guard in
SQL so bypassing the factory does not make EF a supported production path. Documentation must not
publish an EF production command.

### Identifier and temp safety

- Owner scripts receive `target_schema` as a `psql` variable and use `:"target_schema"."name"` for
  every fixed table, sequence, index, constraint, and history reference.
- Catalog queries bind the target namespace OID once, then compare namespace OIDs and object OIDs;
  no trust decision uses unqualified `to_regclass`, `current_schema`, or name-only lookup.
- Preflight rejects any fixed T8 identifier found in `pg_my_temp_schema()` and any target-schema
  same-name relation of the wrong kind/owner/definition.
- `search_path` becomes non-authoritative. Set it to `pg_catalog` after target-schema capture; all
  application objects remain explicitly schema-qualified.

### Owner and ACL attestation

Before mutation, require:

1. `current_user` owns the target schema, all 27 managed tables, both sequences, history, and any
   existing T8 index/constraint;
2. `tabruk_app` exists as `NOLOGIN`, is not superuser/createdb/createrole/replication/bypass-RLS,
   and is not the migration owner;
3. PUBLIC has no target-schema USAGE or CREATE;
4. `tabruk_app` has direct USAGE, no effective CREATE (including inherited roles), and no schema
   grant option;
5. target-schema default ACLs for the owner grant nothing to PUBLIC or `tabruk_app`/its inherited
   roles for tables, sequences, functions, or types; conflicting global owner defaults fail closed;
6. existing relation, sequence, column, PUBLIC, inherited, and grant-option checks remain exact.

The owner script may normalize target-schema ACL/default ACL entries, but must fail rather than
silently alter global defaults or role membership.

## Production upgrade sequence

```text
Operator -> psql session: connect -X, ON_ERROR_STOP, target_schema
psql -> PostgreSQL: pg_advisory_lock(hash(database, schema, T8))
psql -> PostgreSQL: BEGIN; schema/temp/owner/ACL/history/object attestation
psql -> PostgreSQL: lock history + managed relations; apply least privilege; COMMIT
psql -> PostgreSQL: record exact entry state (index/constraint present?)
psql -> PostgreSQL: add chronology CHECK NOT VALID if absent
psql -> PostgreSQL: VALIDATE CHECK
alt validation fails
  psql -> PostgreSQL: rollback; drop only constraint created by this run
  psql -> Operator: nonzero, history absent
end
psql -> PostgreSQL: CREATE UNIQUE INDEX CONCURRENTLY if absent
alt concurrent create fails
  psql -> PostgreSQL: DROP INDEX CONCURRENTLY only if created/debris from this run
  psql -> PostgreSQL: drop only constraint created by this run
  psql -> Operator: nonzero, history absent
end
psql -> PostgreSQL: BEGIN; re-lock and fully re-attest
psql -> PostgreSQL: INSERT corrective history last; COMMIT
psql -> PostgreSQL: release session advisory lock / disconnect
```

Use `psql` error-state variables with a temporarily disabled `ON_ERROR_STOP` only around the
validation/concurrent statements so cleanup runs in the same locked session. Re-enable
`ON_ERROR_STOP` immediately. Compensation may remove only objects absent at entry and created by
this invocation after re-verifying their canonical identity. If compensation itself fails, return
`T8_COMPENSATION_FAILED`, keep history absent, retain the session lock until disconnect, and require
the documented owner repair procedure.

**Atomic semantic guarantee:** success is represented only by the final history commit. Failure
returns the database to its attested entry object state when compensation succeeds; otherwise it
leaves an explicit, history-unapplied recovery state. PostgreSQL transaction atomicity is not
claimed across concurrent DDL.

## Production downgrade sequence

Production Down is a **logical, non-destructive rollback marker**:

```text
Operator -> psql session: connect and take the same session advisory lock
psql -> PostgreSQL: BEGIN; lock, harden, and attest fully applied T8
psql -> PostgreSQL: DELETE only the corrective history row
psql -> PostgreSQL: re-attest index, validated constraint, schema/default ACLs, least privilege
psql -> PostgreSQL: COMMIT; disconnect
```

The unique index, validated chronology constraint, and runtime least privilege remain. This makes
production Down transaction-atomic and safe for older application versions because the objects
only reject states already defined as invalid. Re-upgrade reuses the exact objects and restores the
history row last. Destructive T8 object removal remains an EF-only disposable-database operation;
it must attest every application table is empty before proceeding.

## File/interface delta

- `PostgresLeastPrivilegeCatalog.cs`
  - accept/emit a validated schema for every SQL fragment;
  - add namespace-OID, temp-shadow, schema owner/ACL/default-ACL, owner-role, and disposable-EF
    guards;
  - add one stable session-lock key derivation contract.
- `20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql`
  - become the sole production upgrade; add session lock, fully qualified identifiers, entry-state
    capture, compensation, final history commit, and stage/result messages.
- `20260815102612_T8CorrectivePostgresHardening.owner-downgrade.sql` (new)
  - implement logical production Down; no object drop.
- `20260815102612_T8CorrectivePostgresHardening.cs`
  - retain model fidelity and disposable EF testing; require disposable mode, schema-qualify all
    SQL, and allow destructive Down only after empty-table attestation.
- `TabrukDbContextFactory.cs`
  - remove the production pre-history attestation claim; validate disposable mode and clearly
    reject production EF use before context creation.
- `T8MigrationArtifacts.sha256` and manifest test
  - pin exactly: current five entries, corrective designer, and new owner downgrade script
    (seven total); immutable initial hashes remain frozen.
- `PostgresMigrationAndSchemaTests.cs`
  - move production orchestration assertions to owner scripts; retain EF only for disposable
    fresh/down cycles.
- `PostgresLeastPrivilegeCatalogTests.cs`
  - assert no unqualified fixed identifiers and exact upgrade/downgrade script parity.
- `InitialPostgresSchema.safety.md`
  - replace production EF instructions; document session lock lifetime, semantic atomicity,
    compensation, logical Down, recovery, ACL requirements, and exit stages.

No application API, EF model, deployment service, database, package, or infrastructure change is
introduced.

## Frozen T8M ownership

One developer owns the complete remediation lane; no concurrent task may edit these files:

- `src/HusayniaTabruk.Infrastructure/Migrations/PostgresLeastPrivilegeCatalog.cs`
- `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql`
- `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-downgrade.sql` (new)
- `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs`
- `src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContextFactory.cs`
- `src/HusayniaTabruk.Infrastructure/Migrations/T8MigrationArtifacts.sha256`
- `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationArtifactManifestTests.cs`
- `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs`
- `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresLeastPrivilegeCatalogTests.cs`
- `src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md`

The immutable initial migration source/designer, corrective designer, model snapshot, API, Domain,
Application, package manifests, deployment files, and unrelated persistence code are read-only.
The corrective designer and snapshot may be re-hashed but not edited.

## Error and observability contract

- `P0001` + `T8_ATTESTATION_FAILED:<category>`: schema/temp/owner/ACL/history/object mismatch.
- `23505`: duplicate waitlist data/index build conflict.
- `23514`: chronology validation failure.
- Script output emits `T8_STAGE=<preflight|chronology|index|finalize|compensation|down>` and
  `T8_RESULT=<success|failed|compensation_failed>`; never print connection strings or ACL contents.
- Retries are operator-driven after the stated repair; scripts do not retry locks or DDL internally.

## Test matrix

| Area | Required evidence |
|---|---|
| Temp shadowing | Create temp table/index names matching every fixed identifier on the executing connection; production scripts still target the declared schema or reject before mutation. |
| Qualification | Static test rejects unqualified fixed relation references in catalog and both owner scripts. |
| Owner serialization | Two owner upgrades and upgrade-vs-down overlap; second waits on the same session lock through every commit/concurrent phase; exactly one valid history outcome. |
| History forgery | Pre-opened app writer, queued writer, and competing owner history insert cannot cross hardening/finalization; incomplete forged history is never trusted. |
| ACLs | Wrong schema owner, PUBLIC USAGE/CREATE, direct/inherited app CREATE, schema grant option, target/global default ACL grants, and wrong object owner all fail closed. |
| Upgrade compensation | Chronology failure, concurrent-index failure, final-attestation failure, and forced history-insert failure restore entry objects when created by the run; history stays absent. |
| Retry | Exact pre-existing index/constraint are preserved; retry after data repair succeeds and writes one history row. |
| Production Down | Deletes only T8 history in one transaction; objects and least privilege remain; rerun is idempotent; re-up writes history last. |
| Disposable EF | Missing disposable flag rejects; nonempty destructive Down rejects; empty fresh apply/down/re-up passes. |
| Manifest | Seven exact hashes, including corrective designer and both owner scripts. |
| Live safety | Real PostgreSQL 18.6 proves concurrent index progress, NOT VALID/VALIDATE behavior, lock timeout failure, cleanup, and zero persistence skips. |

## Tradeoffs, risks, and migration

- **Optimized for:** production safety, one serialized owner workflow, live index construction,
  least privilege, and unambiguous recovery.
- **Given up:** destructive production rollback and production `dotnet ef database update`.
- **Rejected:** extending the factory lock across EF history reads; EF/Npgsql does not expose one
  stable session-level orchestration boundary across migration selection, suppressed transactions,
  and history mutation without replacing provider internals.
- **Rejected:** non-concurrent index creation; it weakens large-table live safety.
- **Rejected:** claiming transaction atomicity; concurrent DDL cannot share the final transaction.
- **Risk:** compensation can fail after external hostile DDL/data changes. Mitigation: the session
  owner lock, canonical identity checks, history absent, explicit failure stage, and manual repair.
- **Forward migration:** deploy scripts/catalog/tests/docs first; production then uses owner Up.
- **Rollback:** roll back application code independently; use logical owner Down only when the
  release process requires the T8 marker removed. Never remove the safety objects in production.
- **Compatibility:** application and database model are backward compatible. Operational EF
  production commands are intentionally unsupported, but no deployed application contract breaks.
