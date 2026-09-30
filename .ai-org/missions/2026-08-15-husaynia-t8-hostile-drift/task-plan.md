# T8 hostile-drift implementation plan

## Scope and immutable boundaries

- One developer executes T1-T7 in order. Do not fan out implementation: T1, T3, and T4
  intentionally serialize edits to `PostgresMigrationAndSchemaTests.cs`.
- Allowed production scope is only the T8 migration/catalog/factory artifacts listed below.
- Do not edit:
  - `src/HusayniaTabruk.Infrastructure/Migrations/20260815075156_InitialPostgresSchema.cs`
  - `src/HusayniaTabruk.Infrastructure/Migrations/20260815075156_InitialPostgresSchema.Designer.cs`
  - `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.Designer.cs`
  - `src/HusayniaTabruk.Infrastructure/Migrations/TabrukDbContextModelSnapshot.cs`
- Do not change application projects, application behavior, database model metadata, package
  versions, or Git state/history.
- Immutable initial hashes are:
  - source: `ad9bc814e73f8ac6b76c43f07b09f6a82ac0479501c657112525e2d485db2829`
  - designer: `2fd38406cedb8ca86a1153304d4b37bfbe1729b9c298813084abb6380d126bc1`

## Frozen interfaces

### Migration connection contract

- Keep the existing public signature:

  ```csharp
  public static TabrukDbContext Create(string connectionString)
  ```

- `Create` is the single construction path used by the design-time factory and PostgreSQL migration
  tests. Before creating a context it must parse with `NpgsqlConnectionStringBuilder`, validate the
  contract below, and configure:

  ```csharp
  npgsql.MigrationsAssembly(typeof(TabrukDbContext).Assembly.FullName);
  npgsql.MigrationsHistoryTable("__EFMigrationsHistory", expectedSchema);
  ```

- The accepted connection has exactly one `Search Path` value and exactly one
  `-c tabruk.target_schema=<schema>` option. The normalized values must be equal.
- `<schema>` matches `^[a-z_][a-z0-9_$]{0,62}$`. Reject whitespace, quotes, commas, `$user`,
  `pg_catalog`, `information_schema`, and names beginning `pg_`; allow `public`.
- Missing, blank, repeated, multi-valued, malformed, quoted, reserved, or mismatched inputs throw
  `InvalidOperationException` before a context is returned. Error messages use these fixed
  prefixes and never include the connection string:
  - `TABRUK_MIGRATIONS_CONNECTION requires exactly one Search Path schema.`
  - `TABRUK_MIGRATIONS_CONNECTION requires exactly one tabruk.target_schema option.`
  - `TABRUK_MIGRATIONS_CONNECTION schema must be one unquoted lowercase PostgreSQL identifier.`
  - `TABRUK_MIGRATIONS_CONNECTION Search Path and tabruk.target_schema must match.`
- Other Npgsql connection properties and unrelated PostgreSQL options remain unchanged.

### Test connection helper contract

- `PostgresTestDatabase.ConnectionString` is always a valid migration connection containing:

  ```text
  Search Path=<Schema>;Options=-c tabruk.target_schema=<Schema>
  ```

- Add this test-only helper to `PostgresTestDatabase`:

  ```csharp
  public string BuildMigrationConnectionString(string? searchPath, string? options)
  ```

  It clones the administrative connection, omits a null argument, and otherwise assigns the exact
  supplied `SearchPath`/`Options`. It performs no production validation, allowing tests to create
  missing, repeated, quoted, multi-schema, mismatched, and unresolved variants.

### SQL schema-preflight contract

- Corrective `Up`, corrective `Down`, and owner SQL begin with equivalent fixed SQL checks:
  exactly one server `search_path` entry; one valid `tabruk.target_schema`; equality after
  normalization; and `current_schema()` equality.
- Failure uses SQLSTATE `P0001` and the fixed prefix
  `T8 schema contract mismatch:` followed only by
  `expected_schema`, `search_path`, and `current_schema` values. Never expose credentials.
- SQL contains no dynamically quoted identifier and no user-supplied value is interpolated into
  DDL. All DDL identifiers remain fixed and quoted.

### Object verification and error contract

- Run both object-definition checks before duplicate-data scans or corrective DDL.
- Index verification checks expected namespace/table, index relation kind, `btree`, uniqueness,
  `indisvalid`, `indisready`, `indislive`, exactly three key and zero INCLUDE/expression
  attributes, ordered columns, and canonical predicate `(status = 2)`.
- Constraint verification checks expected namespace/table, `contype = 'c'`,
  `connoinherit = false`, and canonical expression
  `((last_transition_at IS NULL) OR (last_transition_at >= submitted_at))`.
- A correct `convalidated = false` constraint is accepted and subsequently validated.
- Mismatch uses SQLSTATE `P0001` and:

  ```text
  T8 object definition mismatch: object=<fixed object name>; category=<fixed category>.
  ```

  Fixed index categories are `namespace`, `relation-kind`, `table`, `access-method`, `state`,
  `key-count`, `key-order`, and `predicate`. Fixed constraint categories are `namespace`,
  `table`, `type`, `inheritance`, and `expression`.
- Absent objects may be created/skipped idempotently. Wrong or invalid same-name objects are never
  dropped, renamed, replaced, or repaired automatically.
- `Down` performs the same verification before dropping; absent is idempotent, hostile presence
  blocks and remains untouched.

### Privilege and ordering contract

- `PostgresLeastPrivilegeCatalog.ApplyRuntimeLeastPrivilegeSql` remains the only C# statement-set
  interface. It must emit, in order:
  1. revoke all table privileges from `tabruk_app` on every managed table;
  2. revoke all table privileges from `PUBLIC` on every managed table;
  3. revoke all sequence privileges from `tabruk_app` on every catalogued sequence;
  4. revoke all sequence privileges from `PUBLIC` on every catalogued sequence;
  5. grant CRUD on `CrudTables`;
  6. grant INSERT on `InsertOnlyTables`;
  7. grant USAGE and SELECT on `UsageSelectSequences`.
- The owner script contains that exact fixed seven-statement set verbatim.
- `Up`/owner order: schema preflight -> privilege hardening outside the EF transaction -> verify
  both objects -> duplicate scan -> concurrent index create if absent -> add check `NOT VALID` if
  absent -> validate if unvalidated -> history mutation last.
- `Down` order: schema preflight -> privilege hardening -> verify both objects -> verified
  constraint drop -> verified concurrent index drop -> privilege hardening again -> EF history
  deletion last.

### Hostile test contract

- Real PostgreSQL tests assert catalog definitions, not names alone.
- For every hostile-object failure, assert SQLSTATE/error prefix, corrective history absence or
  preservation, and byte-for-byte/catalog-equivalent survival of the hostile object.
- Seed PUBLIC grants on one CRUD table, both insert-only tables, migration history, and both
  sequences including sequence `UPDATE`; assert effective denial after successful Up, successful
  Down, failed Down, owner first run, and owner rerun.
- Cover factory validation without PostgreSQL and cover real EF Up/Down schema rejection against
  PostgreSQL 18.6.
- PostgreSQL/psql absence is a gate failure for this mission; the final focused run must report zero
  skipped tests.

### Manifest contract

`T8MigrationArtifacts.sha256` contains exactly these five entries after implementation:

1. `20260815075156_InitialPostgresSchema.cs`
2. `20260815075156_InitialPostgresSchema.Designer.cs`
3. `20260815102612_T8CorrectivePostgresHardening.cs`
4. `20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql`
5. `PostgresLeastPrivilegeCatalog.cs`

The first two hashes remain fixed. Compute the final three only after T1-T4 focused tests pass.
Tests, factory, documentation, corrective designer, and snapshot are not pinned.

## Tasks

T1  Enforce migration schema targeting
    owner:        developer
    objective:    Make every factory/test-created migration context reject ambiguous schema
                  configuration and use schema-qualified EF history; add focused factory and real
                  EF schema-matrix coverage.
    files:        src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContextFactory.cs
                  tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresIntegrationSupport.cs
                  tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs
    depends_on:   -
    parallel_ok:  no
    exit_criteria: Factory matrix covers valid public/custom schemas plus missing Search Path,
                  missing target option, repeated target option, multi-schema, quoted/invalid/
                  reserved schema, mismatch, and unresolved schema. Real EF Up and Down reject
                  missing/multi/mismatch/unresolved configurations with history unchanged.
                  Focused test project builds and the factory-only cases pass.
    status:       DONE
    evidence:     Factory/configuration cases and real EF Up/Down routing cases passed in the
                  zero-skip PostgreSQL 18.6 focused run.

T2  Close PUBLIC privilege inheritance
    owner:        developer
    objective:    Expand the fixed least-privilege catalog to revoke PUBLIC from every managed
                  table and sequence and prove exact statement/order coverage.
    files:        src/HusayniaTabruk.Infrastructure/Migrations/PostgresLeastPrivilegeCatalog.cs
                  tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresLeastPrivilegeCatalogTests.cs
    depends_on:   -
    parallel_ok:  no
    exit_criteria: Catalog tests assert all seven exact fixed statements, all managed identifiers,
                  no broad ALL TABLES/SEQUENCES or dynamic SQL, and expected statement ordering.
                  Focused catalog tests pass.
    status:       DONE
    evidence:     Exact seven-statement catalog/order and fixed-identifier tests passed.

T3  Harden EF corrective migration
    owner:        developer
    objective:    Implement fixed schema and full catalog preflights, fail-closed Up/Down ordering,
                  verified idempotency, and hostile EF migration tests.
    files:        src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs
                  tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs
    depends_on:   T1, T2
    parallel_ok:  no
    exit_criteria: EF tests cover hostile non-unique same-name index, permissive same-name check,
                  invalid interrupted index, correct validated objects, correct unvalidated check,
                  duplicate and chronology retry, hostile failed Down, PUBLIC grants, fresh apply,
                  initial-to-corrective upgrade, Down, and re-Up. Every failure proves history
                  ordering and hostile-object preservation. Focused EF migration tests pass on
                  PostgreSQL 18.6 with zero skips.
    status:       DONE
    evidence:     Hostile index/check, invalid-index, retry, history ordering, PUBLIC, fresh,
                  upgrade, Down, and re-Up cases passed on PostgreSQL 18.6.

T4  Mirror owner-script guarantees
    owner:        developer
    objective:    Make the approved psql owner upgrade exactly mirror schema, privilege, object,
                  retry, and history-last contracts while preserving top-level concurrent DDL.
    files:        src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql
                  tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresLeastPrivilegeCatalogTests.cs
                  tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs
    depends_on:   T2, T3
    parallel_ok:  no
    exit_criteria: Owner tests cover hostile index/check rejection, correct-object idempotency,
                  unvalidated-check retry, schema matrix, history-last failure, PUBLIC grants on
                  first/rerun, and chronology retry. Static parity test proves the exact seven
                  catalog statements. `CREATE/DROP INDEX CONCURRENTLY` remains top-level through
                  `\gexec`; no transaction wrapper or EF idempotent guard is introduced. Focused
                  owner tests pass with psql and PostgreSQL 18.6, zero skips.
    status:       DONE
    evidence:     Owner hostile drift, schema matrix, retry, PUBLIC first/rerun, and exact catalog
                  parity cases passed with PostgreSQL 18.6 psql.

T5  Apply approved artifact allowlist
    owner:        developer
    objective:    Change manifest policy to the five approved artifacts while preserving immutable
                  inputs and rejecting accidental designer/snapshot pinning.
    files:        src/HusayniaTabruk.Infrastructure/Migrations/T8MigrationArtifacts.sha256
                  tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationArtifactManifestTests.cs
    depends_on:   T3, T4
    parallel_ok:  no
    exit_criteria: Manifest contains exactly the frozen five-file set; hashes match current bytes;
                  initial hashes equal the frozen values; test explicitly rejects corrective
                  designer/snapshot and any extra entry. Focused manifest tests pass.
    status:       DONE
    evidence:     Manifest test passed with exactly five entries and frozen initial hashes.

T6  Update migration safety runbook
    owner:        documentation-specialist
    objective:    Document the strict connection contract, fixed failures, hostile-object repair,
                  retry debris, privilege effects, PostgreSQL locking/scans/capacity/monitoring,
                  and approved rollback path.
    files:        src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md
    depends_on:   T3, T4
    parallel_ok:  no
    exit_criteria: Runbook includes valid/invalid connection examples; no manual history insertion;
                  no hostile-object auto-drop advice; duplicate/validation/concurrent-index scans;
                  temporary disk headroom; ACCESS EXCLUSIVE and SHARE UPDATE EXCLUSIVE locks;
                  long-transaction/low-DDL-window guidance; pg_stat_progress_create_index;
                  deployment-specific timeouts; failed nontransactional repair/retry; corrective
                  Down only after verification; downgrade-to-0 restriction; PUBLIC consumer
                  inventory; provenance residual risk.
    status:       DONE
    evidence:     Safety runbook now documents the frozen connection, object, capacity, locking,
                  retry, privilege, rollback, manifest, and provenance contracts.

T7  Perform developer self-verification
    owner:        developer
    objective:    Run the focused hostile PostgreSQL gate and repository-wide non-destructive
                  quality commands, recording exact counts and skips for independent validators.
    files:        -
    depends_on:   T5, T6
    parallel_ok:  no
    exit_criteria: Commands below complete without modifying immutable/generated files; focused
                  PostgreSQL result reports zero skipped tests; immutable hashes are rechecked;
                  no out-of-scope file changed. This is developer evidence only and does not replace
                  independent test, security, review, or judgment gates.
    status:       DONE
    evidence:     Scope clarification updated the stale T8 independent unavailable-endpoint test
                  connection with matching public Search Path/target schema. Focused PostgreSQL
                  18.6 passed 35/35 with zero skips; catalog/manifest passed 4/4; full solution
                  passed 595/595 with zero skips; build, format, mobile, and immutable hashes
                  passed. Docker remains absent, so Compose config could not run.

## Execution waves

`wave 1: T1 -> T2 -> wave 2: T3 -> wave 3: T4 -> wave 4: T5 -> T6 -> wave 5: T7`

T1 and T2 are logically independent but remain serialized because the mission is explicitly sized
for one developer context. T6 starts only after SQL/error behavior is final so the runbook does not
document a moving contract.

After T7, the VP dispatches independent gates in parallel:

`validation wave: test-engineer + security-engineer + code-reviewer -> engineering-judge`

## Verification commands

Run from `C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk`. Supply a reachable PostgreSQL 18.6
connection, pre-provisioned `tabruk_app NOLOGIN`, and `TABRUK_TEST_PSQL_PATH` (or psql on PATH).

```powershell
docker compose config
docker compose up -d --wait postgres

$env:TABRUK_TEST_POSTGRES_CONNECTION = 'Host=127.0.0.1;Port=5432;Database=tabruk;Username=tabruk;Password=<development password from docker-compose.yml>;Pooling=false'
$env:TABRUK_TEST_PSQL_PATH = '<absolute path to PostgreSQL 18.6 psql.exe>'

dotnet restore .\HusayniaTabruk.sln
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror

dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PostgresLeastPrivilegeCatalogTests|FullyQualifiedName~PostgresMigrationArtifactManifestTests"
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PostgresMigrationAndSchemaTests" --logger "console;verbosity=normal"

dotnet test .\HusayniaTabruk.sln --no-build
dotnet format .\HusayniaTabruk.sln --verify-no-changes --no-restore

npm ci --prefix .\apps\mobile
npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
npm test --prefix .\apps\mobile -- --runInBand
```

Hash verification:

```powershell
Get-FileHash .\src\HusayniaTabruk.Infrastructure\Migrations\20260815075156_InitialPostgresSchema.cs -Algorithm SHA256
Get-FileHash .\src\HusayniaTabruk.Infrastructure\Migrations\20260815075156_InitialPostgresSchema.Designer.cs -Algorithm SHA256
```

Do not run Git commands. Do not claim Compose/PostgreSQL success unless the commands were executed.

## Rework boundaries

- Factory parsing/history qualification failure: reopen T1 only, then rerun T1, T3, T4, T7 and all
  independent gates because migration routing evidence is invalidated.
- Privilege/catalog/parity failure: reopen T2 and T4, then rerun T3/T4 hostile PUBLIC cases, T5 if
  catalog bytes change, T7, security, tests, review, and judgment.
- EF object/order/retry failure: reopen T3; rerun T4 for parity, then T5, T7, and all gates.
- Owner-only behavior/parity failure: reopen T4; rerun T5, T7, and all gates.
- Manifest/hash failure: reopen T5 only unless an immutable hash changed. Any immutable initial hash
  change blocks the mission and requires CTO escalation; do not repair or regenerate it.
- Documentation-only finding: reopen T6 and rerun documentation review/judgment; code gates remain
  valid unless prose exposed a contract mismatch.
- Corrective designer or snapshot appears changed: stop. Revert that implementation attempt without
  Git operations and determine why model metadata changed. Escalate to the Architect if a model
  change is actually required.
- Any proposal to auto-drop hostile objects, alter an initial migration, interpolate schema names
  into DDL, weaken PUBLIC revocation, move history mutation earlier, or broaden application scope is
  an architecture violation; stop implementation and return to the Architect/VP.

## Mission coordination status

| Phase | Owner | Status | Exit evidence | Dependencies |
|---|---|---|---|---|
| A1 Approved fail-closed architecture | architect | DONE | `architecture.md`, `decisions.md` | none |
| P1 Frozen implementation plan | tech-lead | DONE | this file | A1 |
| D1 Execute T1-T7 | developer/documentation-specialist | DONE | T1-T7 complete; exact zero-skip evidence in `test-results.md`; Docker absence recorded | P1 |
| V1 Independent PostgreSQL/regression validation | test-engineer | PENDING | exact commands/counts, zero skips | D1 |
| V2 Independent security gate | security-engineer | PENDING | zero unresolved Critical/High | D1 |
| V3 Independent code review | code-reviewer | PENDING | APPROVED | D1 |
| J1 Definition-of-Done judgment | engineering-judge | PENDING | APPROVED | V1, V2, V3 |

## Log

- 2026-08-15 07:45 - Mission opened after hostile-drift findings invalidated the prior T8 judgment.
- 2026-08-15 07:48 - Approved architecture converted into frozen, dependency-ordered implementation
  work. No production code or Git state changed.
- 2026-08-15 08:20 - Rework 1: focused PostgreSQL passed 35/35 with zero skips, but the full suite
  exposed `PostgresIndependentGateTests.cs` as a T8 migration test still constructing an ambient
  connection. Scope is clarified to include that T8 test; production interfaces remain frozen.
- 2026-08-15 09:15 - Rework 2: independent tests and code review passed. Security blocked release:
  an initial-only runtime role can forge the corrective history row before EF evaluates pending
  migrations, and managed-index verification is incomplete for safe downgrade. Architecture is
  reopened for a pre-history attestation contract; prior security evidence is invalidated.
- 2026-08-15 10:55 - Rework 3: independent database tests passed, but security found a new
  `ef-bootstrap` hostile-history grant race. Code review also found incomplete effective privilege
  attestation and runtime semantic-variant coverage. Reopen only bootstrap hardening, privilege
  matrix attestation, hostile semantic tests, manifest/docs, and invalidated gates.
- 2026-08-15 12:45 - Rework 4: independent tests and security passed. Code review reproduced a
  malformed empty `__EFMigrationsHistory` (no primary key) being accepted as EF bootstrap and then
  populated. Add exact provider history relation/column/type/nullability/length/PK attestation and
  hostile bootstrap structural regressions; rerun all invalidated gates.
- 2026-08-15 14:10 - Rework 5: history-shape tests and code review passed, but security identified
  a pre-executed uncommitted history INSERT race. The hardening transaction must acquire a
  conflicting history-table lock before revocation and hold it through commit, forcing outstanding
  writers to finish before attestation. Add the exact concurrency regression and rerun all gates.
- 2026-08-15 08:14 - T1-T6 completed. Focused PostgreSQL 18.6 migration/security tests passed with
  zero skips; exact five-file manifest and immutable hashes verified.
- 2026-08-15 08:20 - T7 repository-wide build, format, and mobile gates passed. Full solution tests
  passed 594/595; the sole failure is an existing out-of-scope unavailable-endpoint test that calls
  the now-strict migration context factory without the mandatory schema contract. Docker/Compose
  was unavailable, and the required PostgreSQL gate ran against a local portable 18.6 server.
- 2026-08-15 08:43 - Scope clarification applied to the stale T8 independent migration test only.
  Focused catalog/manifest passed 4/4, hostile PostgreSQL 18.6 passed 35/35 with zero skips, and the
  full solution passed 595/595 with zero skips. Build, format, mobile, and immutable hashes passed;
  Docker/Compose remains environmentally unavailable because the `docker` command is absent.

---

# Binding forged-history rework plan

## Rework boundary

This section supersedes the prior validation-wave dispatch. T1-T7 remain useful baseline evidence,
but security gate 1 invalidated the pre-history trust model and the existing index attestation.
Implementation remains limited to the T8 migration/catalog/factory/tests/docs artifacts. The
initial migration source/designer, corrective designer, model snapshot, and application code remain
immutable. Do not interpolate user values, delete suspicious history, repair/drop hostile objects,
or perform Git operations.

One developer executes R1-R3 serially because they freeze and consume one shared attestation
contract and R2/R3 both edit `PostgresMigrationAndSchemaTests.cs`. R4 and R5 have disjoint files and
may run in parallel after behavior is final.

## Frozen rework interfaces

### Pre-history connection-open gate

- Preserve `public static TabrukDbContext Create(string connectionString)`.
- Preserve the strict schema parsing, fixed messages, and schema-qualified EF history configuration
  already implemented by `TabrukDbContextOptions`.
- On the first successful `Open` of the owned `NpgsqlConnection`, execute exactly once:
  1. the existing fixed schema preflight;
  2. a read-only fixed-catalog classification as `pristine`, `ef-bootstrap`,
     `initial-or-retry`, `t8-applied`, or invalid;
  3. for every non-pristine state with the complete required initial inventory, execute
     `PostgresLeastPrivilegeCatalog.ApplyRuntimeLeastPrivilegeSql` in its own database transaction
     and `COMMIT`;
  4. only after that commit, read the two known history IDs and run full initial/T8 catalog and
     effective-privilege attestation;
  5. detach the open handler only after the gate succeeds, so one EF upgrade/downgrade operation is
     not re-gated between its nontransactional commands.
- A later attestation failure must not roll back the privilege hardening commit.
- `pristine` permits fresh EF bootstrap without querying/revoking absent managed relations.
  `ef-bootstrap` permits only the exact empty/bootstrap history state described by fixed catalog
  SQL. `initial-or-retry` requires the complete initial inventory and permits no T8 objects or only
  exact retryable T8 objects. `t8-applied` requires both known history rows, fully attested T8
  objects, and the exact effective privilege matrix.
- Unknown history IDs, T8 history without complete T8 state, initial history without complete
  initial inventory, partial initial inventory, missing T8 history with non-retryable objects, and
  interrupted-downgrade history/object disagreement fail closed with SQLSTATE `P0001`.
- The fixed prefix is `T8 pre-history attestation failed:` followed only by a fixed state/category;
  never include credentials, the connection string, or user-supplied SQL.
- Never insert, update, or delete migration history in the gate. A fully attested database with a
  forged-but-catalog-consistent T8 row is accepted as an already-applied no-op. Owner SQL is the
  only supported reconstruction path for a failed/mismatched state.

### Shared fixed-catalog contract

- `PostgresLeastPrivilegeCatalog.ApplyRuntimeLeastPrivilegeSql` remains the sole privilege statement
  set and retains the existing exact seven-statement order.
- `PostgresLeastPrivilegeCatalog.cs` owns the C# fixed SQL/constants used by both the connection-open
  gate and corrective migration for:
  - known initial/corrective migration IDs and history-table name;
  - complete initial managed table/sequence inventory;
  - effective `tabruk_app`/PUBLIC privilege attestation;
  - complete managed index and constraint attestation.
- The owner script mirrors the same fixed identifiers and predicates verbatim where SQL cannot be
  shared. Static parity tests must fail if migration/factory/owner predicates or privilege
  statements diverge.

### Complete T8 object attestation

- Preserve all currently required namespace/table/access-method/uniqueness/state/key/predicate and
  constraint type/expression checks.
- The index must additionally attest:
  - index owner equals `signups` table owner;
  - key types and order;
  - exact per-key operator classes, collations, and `indoption` values;
  - no INCLUDE or expression attributes;
  - empty/default relation options;
  - `indnullsnotdistinct = false`;
  - `indisprimary = false`, `indisexclusion = false`, `indimmediate = true`,
    `indisclustered = false`, and `indisreplident = false`;
  - no attached `pg_constraint`;
  - the existing PostgreSQL 18.6 canonical predicate and readiness/liveness/validity checks.
- The check constraint must additionally attest owner equality with `signups`, local/non-inherited
  status, no parent linkage, and validation state. `convalidated = false` remains retryable only for
  an otherwise exact constraint.
- Any mismatch raises `P0001` with the existing fixed object prefix and a fixed semantic category.
  Up, Down, the pre-history gate, and owner SQL neither alter nor drop the hostile object.
- Corrective Down verifies the complete definition before either destructive drop. The once-per-
  connection gate permits a legitimate fully attested downgrade to proceed; history deletion
  remains EF's final successful action.

### Required hostile PostgreSQL 18.6 evidence

- Forge the T8 history row on an initial-only database through `tabruk_app`; EF must reject it before
  trusting history, preserve the row and objects, and leave the separately committed privilege
  hardening effective.
- Run the owner script to reconstruct the forged-history state, then prove EF accepts the fully
  attested result.
- A forged T8 row with already-complete objects/privileges is an accepted no-op.
- Missing T8 history with exact retryable objects proceeds and records history last.
- Interrupted downgrade with T8 history but missing/mismatched objects fails closed.
- Parameterized hostile index variants cover owner, opclass, collation, sort/null options,
  relation options, `indnullsnotdistinct`, primary/exclusion/immediate/clustered/replica-identity
  flags where constructible, constraint attachment, key type/order, predicate, and readiness state.
  Each remains untouched.
- Constraint variants cover owner, locality/inheritance, parent linkage, expression, type, and
  validation semantics and remain untouched.
- Retain all existing fresh/upgrade/down/re-up, retry, schema-contract, PUBLIC-grant, owner-script,
  manifest, and immutable-hash coverage with zero PostgreSQL skips.

## Rework tasks

R1  Freeze complete catalog attestation
    owner:        developer
    objective:    Centralize the fixed initial/T8 inventory, effective-privilege checks, complete
                  PostgreSQL 18.6 index/constraint semantics, fixed categories, and owner-script
                  parity contract without changing migration behavior yet.
    files:        src/HusayniaTabruk.Infrastructure/Migrations/PostgresLeastPrivilegeCatalog.cs
                  tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresLeastPrivilegeCatalogTests.cs
    depends_on:   -
    parallel_ok:  no
    exit_criteria: Catalog tests prove the exact seven privilege statements are unchanged; all
                  known history IDs and initial inventory identifiers are fixed/quoted; every
                  frozen index/constraint attribute has a fixed predicate/category; no dynamic
                  identifier or user interpolation exists; factory/migration/owner parity hooks
                  are testable. Focused catalog tests pass.
    status:       DONE

R2  Enforce the pre-history trust gate
    owner:        developer
    objective:    Extend the existing connection-open seam to classify state, commit hardening
                  separately, and attest catalog/history before EF evaluates migration history.
    files:        src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContextFactory.cs
                  tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresIntegrationSupport.cs
                  tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs
    depends_on:   R1
    parallel_ok:  no
    exit_criteria: Tests prove pristine bootstrap, initial-only upgrade, exact retry debris,
                  complete already-applied state, and legitimate downgrade are allowed; forged
                  incomplete T8 history, unknown history, partial initial inventory, and
                  interrupted-downgrade mismatch raise fixed `P0001`; privilege revokes remain
                  committed after attestation failure; history and hostile objects remain
                  unchanged; a fully attested forged row is a no-op. Existing schema factory and
                  EF routing tests remain green.
    status:       DONE

R3  Align migration and owner recovery
    owner:        developer
    objective:    Consume/mirror the frozen complete object attestation in corrective Up/Down and
                  owner SQL, preserving verify-before-change, concurrent DDL, and history-last
                  behavior.
    files:        src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs
                  src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql
                  tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresLeastPrivilegeCatalogTests.cs
                  tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs
    depends_on:   R2
    parallel_ok:  no
    exit_criteria: Parameterized PostgreSQL 18.6 tests reject and preserve every frozen hostile
                  index/constraint semantic variant during Up, Down, and owner execution; owner
                  recovery repairs only absent/exact retry states and inserts history last; forged
                  incomplete history recovers through owner SQL; missing history with exact
                  retryable objects succeeds; complete forged history is idempotent; interrupted
                  downgrade fails closed. Static parity and all prior hostile/retry/PUBLIC tests
                  pass with zero skips.
    status:       DONE

R4  Refresh the runtime artifact allowlist
    owner:        developer
    objective:    Recompute only approved changed runtime hashes after R1-R3 pass and retain the
                  exact five-file allowlist and immutable initial hashes.
    files:        src/HusayniaTabruk.Infrastructure/Migrations/T8MigrationArtifacts.sha256
                  tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationArtifactManifestTests.cs
    depends_on:   R3
    parallel_ok:  yes
    exit_criteria: Manifest still contains exactly the two immutable initial inputs plus corrective
                  migration, owner SQL, and least-privilege catalog; hashes match current bytes;
                  initial hashes are unchanged; factory/tests/docs/designer/snapshot remain
                  excluded. Focused manifest tests pass.
    status:       DONE

R5  Document forged-history recovery
    owner:        documentation-specialist
    objective:    Add operator-visible pre-history failure, committed-hardening, attestation, and
                  owner-reconstruction guidance without weakening existing safety rules.
    files:        src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md
    depends_on:   R3
    parallel_ok:  yes
    exit_criteria: Runbook explains fixed `P0001` states, that hardening survives later failure,
                  that suspicious history is never auto-deleted, how owner SQL reconstructs exact
                  state before retry, fully attested no-op behavior, interrupted-downgrade
                  handling, complete index ownership/semantic verification, and the existing
                  locking/capacity/rollback constraints. It gives no manual history-insertion or
                  hostile-object auto-repair advice.
    status:       DONE

R6  Verify rework without production drift
    owner:        developer
    objective:    Execute focused and repository-wide non-destructive gates and record exact
                  pass/fail/skip evidence for independent validation.
    files:        -
    depends_on:   R4, R5
    parallel_ok:  no
    exit_criteria: PostgreSQL 18.6 and psql are available; focused catalog/manifest and migration
                  tests pass with zero skips; full solution build/tests/format and mobile gates
                  pass; immutable hashes match; corrective designer, snapshot, initial migration,
                  and app code are unchanged. No Git command is run.
    status:       DONE

## Rework execution waves

`wave R1: R1 -> wave R2: R2 -> wave R3: R3 -> wave R4: R4, R5 (parallel) -> wave R5: R6`

The risky bypass is addressed before owner/manifest/docs work. R1 lands the shared fixed contract
before either consumer changes. R2 precedes R3 so forged-history trust is closed before expanding
the recovery and downgrade paths. R4/R5 fan out only after SQL behavior and fixed messages stop
moving.

After R6, invalidate and rerun all independent gates:

`validation wave: test-engineer + security-engineer + code-reviewer -> engineering-judge`

## Rework verification commands

Run from `C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk`. Use a reachable PostgreSQL 18.6
instance with pre-provisioned `tabruk_app NOLOGIN` and PostgreSQL 18.6 `psql`; zero skips are
mandatory.

```powershell
$env:TABRUK_TEST_POSTGRES_CONNECTION = '<owner connection to PostgreSQL 18.6;Pooling=false>'
$env:TABRUK_TEST_PSQL_PATH = '<absolute path to PostgreSQL 18.6 psql.exe>'

dotnet restore .\HusayniaTabruk.sln
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror

dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PostgresLeastPrivilegeCatalogTests|FullyQualifiedName~PostgresMigrationArtifactManifestTests" --logger "console;verbosity=normal"
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PostgresMigrationAndSchemaTests" --logger "console;verbosity=normal"

dotnet test .\HusayniaTabruk.sln --no-build --logger "console;verbosity=normal"
dotnet format .\HusayniaTabruk.sln --verify-no-changes --no-restore

npm ci --prefix .\apps\mobile
npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
npm test --prefix .\apps\mobile -- --runInBand

Get-FileHash .\src\HusayniaTabruk.Infrastructure\Migrations\20260815075156_InitialPostgresSchema.cs -Algorithm SHA256
Get-FileHash .\src\HusayniaTabruk.Infrastructure\Migrations\20260815075156_InitialPostgresSchema.Designer.cs -Algorithm SHA256
```

Optional only when Docker is installed; absence is recorded as an environment limitation and does
not replace the mandatory real PostgreSQL 18.6 run:

```powershell
docker compose config
docker compose up -d --wait postgres
```

## Rework stop conditions

- Any immutable initial hash change, corrective designer/snapshot change, application-code change,
  dynamic identifier interpolation, suspicious-history deletion, or hostile-object auto-repair
  blocks the mission.
- If the connection-open gate cannot distinguish legitimate downgrade from mismatch without
  provider-internal replacement or a new public contract, stop and return to the Architect.
- If PostgreSQL 18.6 cannot construct a listed semantic variant directly, the test must prove the
  corresponding catalog predicate with the nearest safe catalog-producing DDL and record the
  limitation; never update system catalogs directly.
- All previous independent test, security, review, and judgment results are stale after R1 begins.

## Rework log

- 2026-08-15 09:02 - Binding forged-history architecture converted into R1-R6. No production,
  immutable/generated, application, or Git state was changed.
- 2026-08-15 - R1-R6 completed. The connection-open gate now serializes and commits fixed privilege
  hardening before history attestation, complete PostgreSQL 18.6 object semantics are shared by the
  factory/migration and mirrored by owner SQL, forged and interrupted states fail closed, the
  five-file runtime allowlist was refreshed, and recovery guidance was updated.
- 2026-08-15 - Final developer evidence: catalog/manifest 5/5; PostgreSQL migration/security 53/53
  with zero skips; full solution 614/614 with zero skips; build 0 warnings/errors; format, mobile,
  manifest, immutable, corrective-designer, and snapshot hashes passed. Docker Compose remained
  unavailable because the Docker CLI is not installed.
