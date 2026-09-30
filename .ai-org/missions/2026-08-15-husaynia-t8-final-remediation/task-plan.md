# Task Plan

| ID | Owner | Status | Exit evidence |
|---|---|---|---|
| A1 | architect | DONE | `architecture.md`; shared catalog, safe EF boundary plan |
| P1 | tech-lead | DONE | File ownership / implementation plan |
| D1 | developer | DONE | T8 changes and self-verification |
| V1 | test-engineer | DONE | `test-results.md`; disposable PostgreSQL 18.6 validation, quality-command, and cleanup evidence |
| V2 | security-engineer | DONE | PASS security review; 52/52 isolated PostgreSQL 18.6 persistence tests |
| V3 | code-reviewer | DONE | Rework 2 final review: APPROVED |
| J1 | engineering-judge | DONE | `final-verdict.md`: APPROVED |

## Log
- 2026-08-15: Created; assessment in progress.
- 2026-08-15: Architecture completed. Build currently succeeds with 0 warnings/errors. PostgreSQL 18.6 portable binaries are available at `C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin`.
- 2026-08-15: Tech-lead plan completed; serialized implementation delegated to a single developer because catalog/migration/tests share frozen interfaces.
- 2026-08-15: Implementation completed with developer evidence of an isolated PostgreSQL 18.6 52-test persistence run, a warning-free build, and clean format. Independent validation, security, and code-review gates started.
- 2026-08-15: Rework 1: security passed; validation and review exposed a high finding. The EF-generated idempotent corrective script nests concurrent index creation inside a `DO` guard, which PostgreSQL rejects. Add an executable owner idempotent path and disposal-failure regression before repeating V1/V3.
- 2026-08-15: Rework 1 implementation added a six-artifact manifest including an owner-run `psql` idempotent script and disposal-failure coverage. V1/V2/V3 restarted; their prior green claims are not reused for changed script surface.
- 2026-08-15: Rework 2: V1 proved owner-script happy path but V3 proved `\quit 3` guards return zero. V2 also identified ambient public-schema invocation as a Medium risk. Replace guards with actual SQL failure, require/assert one explicit schema, and add negative-path tests.
- 2026-08-15: Rework 2 implementation replaced client quit guards with real SQL errors, added a matching explicit schema contract, and added PostgreSQL negative-path coverage. V1/V2/V3 restarted.
- 2026-08-15: Rework 2 V1 PASS (real PostgreSQL 18.6, 59 persistence and 571 full .NET tests, zero skips); V2 PASS (zero Critical/High); V3 APPROVED. J1 started.
- 2026-08-15: Tech-lead appended the dependency-ordered T8 implementation graph. Developer work is intentionally serialized to keep the shared catalog/migration/tests/doc contract in one compile-time lane.
- 2026-08-15: Developer completed T1-T4, then re-verified the persistence suite against a disposable loopback PostgreSQL 18.6 UTF-8 cluster.
- 2026-08-15: Security review completed with PASS. Reviewed the shared privilege catalog, corrective migration SQL, cleanup helper, manifest, and safety doc; then ran the full `Category=Persistence` integration suite against an isolated disposable PostgreSQL 18.6 cluster with 52 passed, 0 failed, 0 skipped.
- 2026-08-15: V1 completed. Independent validation used a fresh disposable loopback/trust PostgreSQL 18.6 cluster at `127.0.0.1:62389` (PID `46776`) with only `tabruk_app NOLOGIN` pre-provisioned, executed `dotnet build`, `dotnet test` persistence (52 passed, 0 skipped), full `dotnet test`, `dotnet format --verify-no-changes`, portable mobile `npm ci`/lint/typecheck/test, and portable `docker compose config`, verified `tabruk_it_*` schema cleanup (`schemas=0`), then stopped PID `46776` and removed `C:\Users\syedhu\AppData\Local\Temp\tabruk-pg18-73cd9943a1274fe4865feb8779919516`. See `test-results.md`. Remaining mission gap: DoD item 9 still lacks completed V3/J1 artifacts in the working tree.
- 2026-08-15: Rework 1 developer remediation completed. Added the approved owner-run idempotent corrective psql script, pinned it in the migration manifest, added direct owner-script and disposal-failure regression coverage, and reran focused script/cleanup tests (`7` passed, `0` skipped), `Category=Persistence` (`57` passed, `0` skipped), `dotnet build` (`0` warnings, `0` errors), full `.NET` tests (`569` passed), and `dotnet format --verify-no-changes`. Manual owner execution also succeeded twice against fresh schema `tabruk_manual_2a1422f5c50d` on disposable PostgreSQL 18.6 at `127.0.0.1:58360` (PID `39696`) with validated history/index/constraint/privilege checks. Pending refreshed independent V1/V3/J1 artifacts.
- 2026-08-15: Rework 1 independent security review completed with PASS for blocking severity (0 Critical, 0 High). Verified all six manifest hashes match on disk and confirmed the owner script writes `__EFMigrationsHistory` only after corrective hardening succeeds. Recorded one non-blocking Medium finding: the public-schema `psql` guidance leaves `search_path` ambient, so owner execution can harden the wrong schema if database or environment path settings resolve another schema before `public`.

## Frozen implementation interfaces

1. **Least-privilege identifier contract**  
   `src/HusayniaTabruk.Infrastructure/Migrations/PostgresLeastPrivilegeCatalog.cs` is the only
   source of fixed SQL object names for T8. It owns the exact CRUD-table set, insert-only table
   set, denied table set, and USAGE+SELECT sequence set. The corrective migration source and all
   persistence tests must consume this catalog; no duplicated allowlists remain elsewhere.
2. **Owned-transaction cleanup contract**  
   Infrastructure repository wrappers must route owned rollback/change-tracker-clear/dispose
   through one helper. If operation code throws or cancels and cleanup also fails, the original
   exception/cancellation is rethrown and cleanup failures are attached to `Exception.Data`.
   `Result.Failure(...)` shapes do not change.
3. **Pinned artifact contract**  
   The hash manifest covers exactly these six files and nothing else:
   - `src/HusayniaTabruk.Infrastructure/Migrations/20260815075156_InitialPostgresSchema.cs`
   - `src/HusayniaTabruk.Infrastructure/Migrations/20260815075156_InitialPostgresSchema.Designer.cs`
   - `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs`
   - `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql`
   - `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.Designer.cs`
   - `src/HusayniaTabruk.Infrastructure/Migrations/TabrukDbContextModelSnapshot.cs`
   Initial migration source/designer stay immutable inputs throughout implementation.

## T8 implementation task graph

T1  Freeze shared identifier catalog and corrective migration source
    owner:        developer
    objective:    Add the shared exact-identifier catalog and rewrite only the corrective migration
                  source so PostgreSQL-safe DDL, exact grants, and least-privilege Down behavior
                  are implemented without touching immutable migration artifacts.
    files:        src/HusayniaTabruk.Infrastructure/Migrations/PostgresLeastPrivilegeCatalog.cs (new)
                  src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs
    depends_on:   -
    parallel_ok:  no
    exit_criteria: `dotnet build HusayniaTabruk.sln` passes; corrective `Up` uses exact identifier
                  SQL plus `migrationBuilder.Sql(..., suppressTransaction: true)` for
                  `CREATE UNIQUE INDEX CONCURRENTLY`; chronology constraint uses staged
                  `NOT VALID` then `VALIDATE CONSTRAINT`; corrective `Down` uses
                  `DROP INDEX CONCURRENTLY` and re-applies exact least privilege; no edits land in
                  `20260815075156_InitialPostgresSchema.cs`,
                  `20260815075156_InitialPostgresSchema.Designer.cs`,
                  `20260815102612_T8CorrectivePostgresHardening.Designer.cs`, or
                  `TabrukDbContextModelSnapshot.cs`.
    status:       DONE

T2  Centralize owned-transaction cleanup semantics
    owner:        developer
    objective:    Move owned rollback/dispose/change-tracker cleanup behind one Infrastructure
                  helper and route write plus repeatable-read wrappers through it so the original
                  exception always wins.
    files:        src/HusayniaTabruk.Infrastructure/Persistence/Repositories/PostgresOwnedTransactionCleanup.cs (new)
                  src/HusayniaTabruk.Infrastructure/Persistence/Repositories/PostgresUnitOfWorkAndStores.cs
                  src/HusayniaTabruk.Infrastructure/Persistence/Repositories/PersistenceWriteSupport.cs
                  src/HusayniaTabruk.Infrastructure/Persistence/Repositories/PostgresAggregateRepositories.cs
                  tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresTransactionCleanupTests.cs (new)
    depends_on:   T1
    parallel_ok:  no
    exit_criteria: helper is the only owned-transaction cleanup path in these repositories;
                  cancellation and ordinary exceptions preserve the original thrown exception while
                  recording cleanup failures in `Exception.Data`; existing `Result` contracts are
                  unchanged; focused cleanup regression tests pass.
    status:       DONE

T3  Add manifest and align migration assertions to the frozen catalog
    owner:        developer
    objective:    Remove duplicated table classification from tests, pin immutable migration
                  artifacts with one SHA-256 manifest, and prove the corrective migration’s retry
                  and downgrade posture against the shared catalog.
    files:        src/HusayniaTabruk.Infrastructure/Migrations/T8MigrationArtifacts.sha256 (new)
                  tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs
                  tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresLeastPrivilegeCatalogTests.cs (new)
                  tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationArtifactManifestTests.cs (new)
    depends_on:   T2
    parallel_ok:  no
    exit_criteria: no duplicated runtime allowlist remains in tests; EF-mapped table coverage test
                  proves catalog equality exactly once; manifest test recomputes SHA-256 for the
                  five pinned artifacts; real-Postgres migration tests assert hardened corrective
                  `Down`, duplicate waitlist retry behavior, and chronology `NOT VALID`/validate
                  failure behavior.
    status:       DONE

T4  Update migration safety documentation to the post-T8 operating contract
    owner:        developer
    objective:    Bring the migration safety document in sync with the hardened owner/runtime role
                  contract, downgrade semantics, manifest upkeep, and named concurrent-index repair
                  procedure.
    files:        src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md
    depends_on:   T3
    parallel_ok:  no
    exit_criteria: document explains owner vs `tabruk_app` responsibilities, forward and backward
                  procedure, hardened corrective `Down`, manifest update/check steps, and the exact
                  `DROP INDEX CONCURRENTLY` repair path for an interrupted corrective `Up`.
    status:       DONE

## Execution waves

wave 1: T1 -> wave 2: T2 -> wave 3: T3 -> wave 4: T4 -> wave 5: V1, V2, V3 (parallel) -> wave 6: J1

## Coordination notes and risks

- **Single-developer cohesion rule:** keep T1-T4 with one developer in one branch/worktree. The
  shared catalog, corrective migration SQL, manifest hashes, and persistence assertions are one
  compile-time contract; parallel developers would immediately collide on that interface.
- **Highest sequencing risk:** T1 must land first because it freezes the catalog consumed by both
  later tests and the safety doc. Any mid-stream catalog renegotiation invalidates T3/T4 evidence.
- **Migration integrity risk:** manifest hashes in T3 are generated only after T1 code is final;
  otherwise every later change churns both the manifest and its verifier.
- **Validation risk:** V1 must run only after T4 because the real PostgreSQL evidence and doc
  commands need the final migration names, privilege posture, and manifest file path.
- 2026-08-15: Rework 1 independent V1 PASS. Fresh disposable loopback/trust PostgreSQL 18.6 cluster `127.0.0.1:53123` (PID `19516`) with pre-provisioned `tabruk_app NOLOGIN`; manually applied `20260815075156_InitialPostgresSchema`, proved the approved owner script is idempotent and leaves one corrective history row plus hardened privileges, reproduced EF generated idempotent failure (`CREATE INDEX CONCURRENTLY cannot be executed from a function`) with corrective history still absent, ran targeted rework tests (`10` passed), full `Category=Persistence` (`57` passed, `0` skipped), full `.NET` suite (`569` passed), `dotnet build -warnaserror`, and `dotnet format --verify-no-changes`, then removed `C:\Users\syedhu\AppData\Local\Temp\tabruk-r1-v1-2c64962c9e3548ce909b481746cb45c0`. See `test-results.md#rework-1-independent-result`.
- 2026-08-15: Rework 2 developer remediation completed. Replaced unsupported `\quit 3` guards with SQL exceptions, required explicit `tabruk.target_schema` + single-schema `search_path` pinning for the owner script, added real PostgreSQL regression coverage for missing-initial guard failure and alternate-schema rejection, updated the safety doc and manifest, then reverified focused manifest/catalog/owner tests (`8` passed), full `Category=Persistence` (`59` passed, `0` skipped), `dotnet build HusayniaTabruk.sln --no-restore -warnaserror`, and `dotnet format HusayniaTabruk.sln --verify-no-changes --no-restore` against disposable PostgreSQL 18.6 at `127.0.0.1:53227` (PID `47280`) with `tabruk_it_*` schema count returning to `0`.
- 2026-08-15: Rework 2 independent security review completed with PASS (0 Critical, 0 High). Verified all six migration-artifact hashes, proved pinned conninfo overrides ambient `PGOPTIONS` (`public|public|public`), reproduced nonzero owner-script guard exits (`MANUAL_GUARD_EXIT=3`, `MISMATCH_EXIT=3`) on disposable PostgreSQL 18.6, and ran focused manifest/owner-script regressions (`6` passed, `0` skipped) against the same review cluster before teardown.
- 2026-08-15: Rework 2 independent V1 PASS. Fresh disposable loopback/trust PostgreSQL 18.6 cluster `127.0.0.1:50626` (PID `44604`) with only `tabruk_app NOLOGIN` pre-provisioned; manually proved ambient alternate-schema, mismatched explicit-schema, and missing-initial-history owner-script guards all exited nonzero (`3`) without writing corrective history or hardening the wrong schema; then proved explicit `public/public` execution is idempotent, writes exactly one corrective history row, validates the waitlist index and chronology constraint, hardens only `public`, ran targeted rework tests (`12` passed), full `Category=Persistence` (`59` passed, `0` skipped), full `.NET` suite (`571` passed), `dotnet build HusayniaTabruk.sln --no-restore -warnaserror`, and `dotnet format HusayniaTabruk.sln --no-restore --verify-no-changes`, confirmed `tabruk_it_*` schema count `0`, then stopped PID `44604` and removed `C:\Users\syedhu\AppData\Local\Temp\tabruk-r2-v1-ce0018a9715a49dea55004ad2d57bd8b`. See `test-results.md#rework-2-independent-result`.
- 2026-08-15: J1 independently reran PostgreSQL 18.6, .NET, mobile, Compose, format, and manifest checks; FINAL APPROVED.
