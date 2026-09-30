# Threat model

- **Entry points:** application requests that reach EF Core/Npgsql persistence wrappers; migration-owner `dotnet ef database update` / `database update <target>` via `TABRUK_MIGRATIONS_CONNECTION` (`src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContextFactory.cs:14-16`); manifest verification via integration tests (`tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationArtifactManifestTests.cs:18-43`).
- **Trust boundaries:** untrusted request -> SQL session running as fixed `tabruk_app`; migration owner -> DDL/grant authority; repository source tree -> compiled migration behavior and pinned artifacts.
- **Assets:** `__EFMigrationsHistory`, audit/access-event append-only trails, runtime table/sequence privileges, signup uniqueness/chronology constraints, pinned migration artifacts.
- **Dangerous sinks:** `migrationBuilder.Sql(...)` in the corrective migration (`src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs:13-105`); transaction rollback/dispose cleanup (`src/HusayniaTabruk.Infrastructure/Persistence/Repositories/PostgresOwnedTransactionCleanup.cs:8-77`); raw/interpolated SQL in persistence wrappers (`src/HusayniaTabruk.Infrastructure/Persistence/Repositories/PostgresUnitOfWorkAndStores.cs:34-66`, `src/HusayniaTabruk.Infrastructure/Persistence/Repositories/PersistenceWriteSupport.cs:143-180`, `src/HusayniaTabruk.Infrastructure/Persistence/Repositories/PostgresAggregateRepositories.cs:47-74`, `:565-592`, `:1001-1029`).
- **Actors:** authenticated application caller, migration owner/operator, malicious contributor tampering with migration-related source.

SECURITY RESULT

Scope:           T8 privilege catalog, corrective migration, owned-transaction cleanup, manifest, safety doc, and PostgreSQL persistence tests
Critical: 0   High: 0   Medium: 0   Low: 0   Informational: 0

Blocking findings:
None

All findings:
None

Conclusion: PASS

STATUS:          PASS
SUMMARY:
No unresolved Critical/High flaws were found in the scoped T8 hardening. The corrective migration now uses fixed quoted identifiers from the shared catalog, keeps `tabruk_app` off `__EFMigrationsHistory`, preserves insert-only audit/access tables, stages PostgreSQL-safe concurrent index / constraint validation steps, and preserves the primary exception when rollback/dispose cleanup also fails.
WORK_COMPLETED:
- Reviewed mission requirements/architecture/plan in `.ai-org/missions/2026-08-15-husaynia-t8-final-remediation/`.
- Inspected the scoped implementation and documentation: `PostgresLeastPrivilegeCatalog.cs`, `20260815102612_T8CorrectivePostgresHardening.cs`, `PostgresOwnedTransactionCleanup.cs`, repository cleanup call sites, `TabrukDbContext.cs`, `TabrukDbContextFactory.cs`, `T8MigrationArtifacts.sha256`, and `InitialPostgresSchema.safety.md`.
- Inspected the scoped tests: `PostgresLeastPrivilegeCatalogTests.cs`, `PostgresMigrationArtifactManifestTests.cs`, `PostgresMigrationAndSchemaTests.cs`, `PostgresTransactionCleanupTests.cs`, `PostgresIndependentGateTests.cs`, and `PostgresIntegrationSupport.cs`.
- Executed the full PostgreSQL persistence suite against an isolated disposable PostgreSQL 18.6 cluster created locally for this review.
EVIDENCE:
- Reviewed least-privilege catalog and fixed identifier SQL generation: `src/HusayniaTabruk.Infrastructure/Migrations/PostgresLeastPrivilegeCatalog.cs:11-100`.
- Reviewed corrective migration privilege/DDL flow: `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs:13-105`.
- Reviewed immutable initial broad-grant baseline being corrected: `src/HusayniaTabruk.Infrastructure/Migrations/20260815075156_InitialPostgresSchema.cs:1048-1064`.
- Reviewed EF table/constraint definitions that the catalog must exactly cover: `src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContext.cs:304-332`, `:499-560`.
- Reviewed cleanup helper and all owned-transaction call sites: `src/HusayniaTabruk.Infrastructure/Persistence/Repositories/PostgresOwnedTransactionCleanup.cs:8-77`, `src/HusayniaTabruk.Infrastructure/Persistence/Repositories/PostgresUnitOfWorkAndStores.cs:34-66`, `src/HusayniaTabruk.Infrastructure/Persistence/Repositories/PersistenceWriteSupport.cs:143-180`, `src/HusayniaTabruk.Infrastructure/Persistence/Repositories/PostgresAggregateRepositories.cs:47-74`, `:565-592`, `:1001-1029`.
- Reviewed migration-owner connection sourcing / no-secret-in-source posture: `src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContextFactory.cs:8-30`.
- Reviewed safety/rollback/manifest operating guidance: `src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md:11-122`.
- Reviewed catalog/manifest/privilege/integrity regression coverage: `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresLeastPrivilegeCatalogTests.cs:12-61`, `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationArtifactManifestTests.cs:18-59`, `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:40-198`, `:782-922`, `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresTransactionCleanupTests.cs:16-86`, `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresIndependentGateTests.cs:194-361`, `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresIntegrationSupport.cs:92-145`.
- Command: `git --no-pager status --short --untracked-files=all`
  - Output: working tree is entirely untracked from the repository root, so direct working-tree inspection was treated as authoritative for this review.
- Command: environment check for `TABRUK_TEST_POSTGRES_CONNECTION` and `TABRUK_MIGRATIONS_CONNECTION`
  - Output: both were `missing`, so I did not use any preconfigured/shared database connection.
- Command: initialized a disposable local PostgreSQL 18.6 cluster from `C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin`, created `tabruk_app NOLOGIN`, set `TABRUK_TEST_POSTGRES_CONNECTION` only inside that ephemeral process, then ran:
  - `dotnet test tests/HusayniaTabruk.IntegrationTests/HusayniaTabruk.IntegrationTests.csproj --filter "Category=Persistence"`
  - Output: `Passed!  - Failed: 0, Passed: 52, Skipped: 0, Total: 52, Duration: 54 s - HusayniaTabruk.IntegrationTests.dll (net10.0)`
ARTIFACTS:
- `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-08-15-husaynia-t8-final-remediation\security-review.md`
FINDINGS:
- None.
RISKS:
- No unresolved code-level risk was identified inside the scoped application-role / migration-owner trust boundary.
- Residual operational safety still depends on continuing to run migrations with an owner connection and keeping the persistence suite + manifest test in CI, as documented in `InitialPostgresSchema.safety.md`.
BLOCKERS:
- None.
NEXT_ACTION:
- Mark V2 complete and hand this PASS result to V3 / J1.

## Rework 1

### Threat model

- **Entry points:** owner-supplied `psql --dbname ... -f 20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql`; ambient PostgreSQL `search_path`; runtime `tabruk_app` sessions against the hardened schema.
- **Trust boundaries:** operator shell/environment -> `psql` conninfo parsing and startup; migration owner -> DDL/grant authority; hardened schema -> runtime `tabruk_app`.
- **Assets:** `__EFMigrationsHistory` integrity, exact `tabruk_app` least privilege, insert-only `audit_events` / `privileged_access_events`, pinned migration artifacts.
- **Dangerous sinks:** `psql` metacommands (`\gset`, `\gexec`, `\quit`), unqualified SQL in the owner script, grant/revoke statements, history-row insertion, manifest hash verification.
- **Actors:** runtime application principal (`tabruk_app`), migration owner/operator, compromised operator execution environment influencing `search_path` or startup parameters.

SECURITY RESULT

Scope:           Rework 1 owner-run idempotent PostgreSQL script, manifest, focused tests, and safety documentation
Critical: 0   High: 0   Medium: 1   Low: 0   Informational: 0

Blocking findings:
None

All findings:

[MEDIUM] Public-schema invocation can harden the wrong schema when `search_path` is left ambient
Location:     src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md:46-52; src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql:7-104; src/HusayniaTabruk.Infrastructure/Migrations/20260815075156_InitialPostgresSchema.cs:1048-1064
Issue:        The safety doc tells operators targeting `public` to omit an explicit `search_path`, but the owner script uses only unqualified relation names for its guards, grants, DDL, and `__EFMigrationsHistory` write. `-X` disables `.psqlrc`, yet it does not pin schema resolution. Any role/database `search_path` setting or injected `PGOPTIONS` that resolves another schema before `public` makes the script operate there instead.
Attack path:  Operator follows the documented public-schema invocation -> PostgreSQL resolves `__EFMigrationsHistory` / `signups` in another schema first -> the owner script's guards, revokes/grants, and final history insert all succeed against that schema -> the real application schema keeps the initial broad grant posture (`GRANT ... ON ALL TABLES IN SCHEMA %I TO tabruk_app`) -> a runtime session using `tabruk_app` still has unintended access to the real schema's migration history and can tamper with migration state.
Impact:       Least-privilege hardening can be silently skipped for the intended schema, leaving `tabruk_app` overprivileged on `__EFMigrationsHistory` and weakening migration-state integrity until an owner notices and reapplies the fix against the correct schema.
Fix:          Require an explicit single-schema `search_path` even for `public` (for example `options='-c search_path=public'`) or schema-qualify / assert the target schema inside the owner script before any grant or history operation.
Confidence:   High

Conclusion: PASS

STATUS:          PASS
SUMMARY:
No Critical or High flaw was found in Rework 1. The owner-run script keeps its DDL text fixed, records migration history only after the corrective hardening completes, and the six-file manifest currently matches on disk. One non-blocking medium issue remains: the public-schema invocation guidance leaves `search_path` ambient, so the script can be pointed at the wrong schema.
WORK_COMPLETED:
- Re-reviewed the mission inputs (`code-review.md`, `security-review.md`, `definition-of-done.md`, `architecture.md`, `task-plan.md`) for the Rework 1 scope.
- Inspected the full owner idempotent script, least-privilege catalog, corrective migration source, manifest, safety doc, and focused persistence tests covering `psql` execution and no-early-history-write behavior.
- Verified the six pinned SHA-256 artifact hashes directly from the working tree.
EVIDENCE:        `rg "owner-idempotent|psql|gexec|ON_ERROR_STOP|search_path"` across scoped files; numbered file review of `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql:1-104`, `src/HusayniaTabruk.Infrastructure/Migrations/PostgresLeastPrivilegeCatalog.cs:6-101`, `src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md:35-80`, `src/HusayniaTabruk.Infrastructure/Migrations/20260815075156_InitialPostgresSchema.cs:1048-1064`, `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:224-359`, `:1121-1231`, `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresLeastPrivilegeCatalogTests.cs:14-91`, `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationArtifactManifestTests.cs:18-58`; PowerShell manifest verification output: `MATCH 20260815075156_InitialPostgresSchema.cs`, `MATCH 20260815075156_InitialPostgresSchema.Designer.cs`, `MATCH 20260815102612_T8CorrectivePostgresHardening.cs`, `MATCH 20260815102612_T8CorrectivePostgresHardening.Designer.cs`, `MATCH 20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql`, `MATCH TabrukDbContextModelSnapshot.cs`.
ARTIFACTS:       `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-08-15-husaynia-t8-final-remediation\security-review.md`
FINDINGS:
- Medium: public-schema invocation leaves `search_path` ambient and can mis-target hardening.
RISKS:
- If operators run the documented public-schema command without pinning `search_path`, database or environment-level path settings can leave the real application schema un-hardened even though the owner script exits successfully.
BLOCKERS:
- None.
NEXT_ACTION:
- Before J1 sign-off, tighten the safety doc (and preferably the script) so owner execution always pins the intended schema, including `public`.

## Rework 2

### Threat model

- **Entry points:** owner-run `psql --dbname ... options='-c tabruk.target_schema=<schema> -c search_path=<schema>' -X -v ON_ERROR_STOP=1 -f 20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql`; ambient `PGOPTIONS` / role or database `search_path` defaults that could previously influence startup.
- **Trust boundaries:** operator shell/conninfo -> libpq startup parameters; owner session -> privileged `REVOKE` / `GRANT` / `ALTER TABLE` / `CREATE INDEX CONCURRENTLY`; runtime `tabruk_app` -> hardened application schema.
- **Assets:** correct target-schema selection, `__EFMigrationsHistory` integrity, exact `tabruk_app` least privilege, and the absence of shell/SQL injection in the owner-run upgrade path.
- **Dangerous sinks:** `current_setting(...)` preflight guards, unqualified object references resolved through `search_path`, `psql \gexec`, and the final `__EFMigrationsHistory` insert.
- **Actors:** migration owner/operator, compromised local environment attempting ambient startup-option influence, and runtime `tabruk_app`.

SECURITY RESULT

Scope:           Rework 2 owner idempotent PostgreSQL script, safety doc, manifest, corrective migration/catalog, and focused regression tests
Critical: 0   High: 0   Medium: 0   Low: 0   Informational: 0

Blocking findings:
None

All findings:
None

Conclusion: PASS

STATUS:          PASS
SUMMARY:
No unresolved Critical or High migration privilege/injection issue remains in the Rework 2 scope. The owner script now fails closed with real SQL errors under `ON_ERROR_STOP`, requires an explicit single-schema `tabruk.target_schema` + matching `search_path`, keeps all privileged SQL on fixed quoted identifiers, and still writes the corrective history row only after the hardening/index/constraint steps succeed.
WORK_COMPLETED:
- Re-reviewed the mission inputs (`security-review.md`, `code-review.md`, `definition-of-done.md`, `task-plan.md`) for the Rework 2 scope.
- Inspected the full owner idempotent script, safety doc, corrective migration source, least-privilege catalog, manifest, and focused persistence tests.
- Verified all six pinned migration artifact hashes against the working tree.
- Reproduced the relevant `psql` / PostgreSQL 18.6 semantics on a disposable local cluster, then ran the focused owner-script regression suite against that cluster.
EVIDENCE:
- File review: `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql:10-40` (explicit `tabruk.target_schema` / single-schema `search_path` / `current_schema()` guard), `:47-64` (history-table and initial-migration guards), `:69-74` (fixed least-privilege `REVOKE` / `GRANT` statements), `:105-128` (top-level `\gexec` DDL and history row written last).
- File review: `src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md:43-60` (owner-run `psql` command now requires both `tabruk.target_schema` and `search_path`, including `public`), `:66-75` (documented rejection of EF idempotent wrapper for concurrent index DDL).
- File review: `src/HusayniaTabruk.Infrastructure/Migrations/PostgresLeastPrivilegeCatalog.cs:86-100` (fixed quoted identifier SQL only); `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs:13-105` (runtime migration applies the same least-privilege catalog and PostgreSQL-safe staged DDL); `src/HusayniaTabruk.Infrastructure/Migrations/20260815075156_InitialPostgresSchema.cs:1048-1064` (historical broad `ALL TABLES` / `ALL SEQUENCES` posture being corrected).
- File review: `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresLeastPrivilegeCatalogTests.cs:59-86`, `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationArtifactManifestTests.cs:19-44`, `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:223-499`, `:1262-1382`.
- Command: SHA-256 verification of `src/HusayniaTabruk.Infrastructure/Migrations/T8MigrationArtifacts.sha256`
  - Output: `MATCH 20260815075156_InitialPostgresSchema.cs`, `MATCH 20260815075156_InitialPostgresSchema.Designer.cs`, `MATCH 20260815102612_T8CorrectivePostgresHardening.cs`, `MATCH 20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql`, `MATCH 20260815102612_T8CorrectivePostgresHardening.Designer.cs`, `MATCH TabrukDbContextModelSnapshot.cs`.
- Command: disposable PostgreSQL 18.6 probe with ambient `PGOPTIONS='-c search_path=pg_catalog'` plus pinned conninfo `options='-c tabruk.target_schema=public -c search_path=public'`
  - Output: `PINNED_SETTINGS_EXIT=0` and `public|public|public`, confirming the explicit conninfo pin controls both custom GUC and effective schema resolution.
- Command: manual owner-script execution against an empty schema pinned with `tabruk.target_schema=manual_no_history` / `search_path=manual_no_history`
  - Output: `MANUAL_GUARD_EXIT=3` and `ERROR: __EFMigrationsHistory is missing in schema manual_no_history. Apply 20260815075156_InitialPostgresSchema first.`, confirming the guard now exits nonzero under `ON_ERROR_STOP`.
- Command: manual owner-script execution with mismatched `tabruk.target_schema=public` and `search_path=manual_no_history`
  - Output: `MISMATCH_EXIT=3` and `ERROR: Owner script expected search_path public but current setting is manual_no_history.`, confirming the script fails before any privileged DDL when schema targeting is inconsistent.
- Command: `dotnet test tests/HusayniaTabruk.IntegrationTests/HusayniaTabruk.IntegrationTests.csproj --filter '(FullyQualifiedName~PostgresMigrationArtifactManifestTests)|(FullyQualifiedName~PostgresLeastPrivilegeCatalogTests.ApprovedOwnerIdempotentScriptUsesFixedLeastPrivilegeSqlAndAvoidsEfGuard)|(FullyQualifiedName~PostgresMigrationAndSchemaTests.ApprovedOwnerIdempotentScriptAppliesAndReappliesOnInitialOnlySchema)|(FullyQualifiedName~PostgresMigrationAndSchemaTests.ApprovedOwnerIdempotentScriptFailsWithoutInitialMigrationHistoryAndWritesNoCorrectiveHistory)|(FullyQualifiedName~PostgresMigrationAndSchemaTests.ApprovedOwnerIdempotentScriptRequiresMatchingExplicitSchemaAndDoesNotHardenAlternateSchema)|(FullyQualifiedName~PostgresMigrationAndSchemaTests.ApprovedOwnerIdempotentScriptRetriesAfterChronologyRepairWithoutWritingHistoryEarly)' --nologo --verbosity minimal`
  - Output: `Passed!  - Failed: 0, Passed: 6, Skipped: 0, Total: 6, Duration: 5 s - HusayniaTabruk.IntegrationTests.dll (net10.0)`.
ARTIFACTS:
- `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-08-15-husaynia-t8-final-remediation\security-review.md`
- `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-08-15-husaynia-t8-final-remediation\task-plan.md`
FINDINGS:
- None.
RISKS:
- No unresolved Critical/High privilege-escalation, schema-selection, or injection path was demonstrated in the scoped owner-run migration flow.
- Residual operational safety still depends on using an owner connection; the script now enforces the schema-pinning contract and fails closed when operators violate it.
BLOCKERS:
- None.
NEXT_ACTION:
- Hand this PASS result to V3 / J1 as the Rework 2 security gate result.

## Final independent gate

### Threat model

- **Actors:** compromised `tabruk_app`, hostile database/schema owner, and malicious or mistaken migration operator.
- **Trust boundaries:** runtime role to owner-run migration; connection options to schema selection; catalog state to migration skip/drop decisions.
- **Assets:** migration-history integrity, runtime least privilege, application data integrity, and the corrective index and constraint.
- **Dangerous sinks:** `__EFMigrationsHistory`, `GRANT`/`REVOKE`, concurrent index creation/deletion, and constraint creation/deletion.
- **Principal attack paths:** forged migration history, hostile same-name objects, inherited PUBLIC privileges, schema-routing manipulation, and unsafe downgrade.

SECURITY RESULT

Scope:           Final T8 migration, owner script, catalog, factory, migration tests, safety documentation, manifest, and current PostgreSQL migration behavior
Critical: 0   High: 1   Medium: 1   Low: 0   Informational: 0

Blocking findings:
- Runtime role can forge corrective history and bypass the entire remediation.

All findings:

[HIGH] Runtime role can forge corrective history and bypass the entire remediation
Location:     src/HusayniaTabruk.Infrastructure/Migrations/20260815075156_InitialPostgresSchema.cs:1050; src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContextFactory.cs:109-124; src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md:63-67
Issue:        The immutable initial migration grants `tabruk_app` CRUD on all tables, including `__EFMigrationsHistory`. The corrective migration revokes that access only inside its `Up`. Before `Up` executes, a compromised runtime role can insert the corrective migration ID. EF then considers T8 applied and never executes its schema preflight, privilege hardening, object verification, index creation, or constraint creation. Connection-open schema validation does not attest that a recorded corrective migration corresponds to the required catalog state.
Attack path:  Compromise application SQL privileges or obtain `SET ROLE tabruk_app` on an initial-only database -> insert `20260815102612_T8CorrectivePostgresHardening` into `__EFMigrationsHistory` -> operator runs the documented EF database update -> EF sees no pending corrective migration and skips `Up` -> broad history/PUBLIC access and missing corrective objects persist.
Impact:       An attacker can permanently bypass T8 hardening, preserve excessive privileges, forge later migration state, and leave required data-integrity controls absent while the database reports the migration as applied.
Fix:          Add an owner-only bootstrap/preflight before EF determines pending migrations. It must revoke runtime/PUBLIC history privileges, detect a recorded corrective ID, verify the complete required T8 catalog and privilege state, and fail closed when history and physical state differ. Alternatively, make an attesting owner script the exclusive supported initial-to-T8 path and reject direct EF application as unsafe.
Confidence:   High

[MEDIUM] Partial index verification can accept and later drop a noncanonical same-name object
Location:     src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs:55-169; src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs:325-344; src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql:72-181
Issue:        Index preflight checks namespace, relation type, table, access method, validity, uniqueness, key names/count, expressions, and predicate, but not the complete PostgreSQL definition. Unchecked attributes include `indclass`, `indcollation`, `indoption`, `indnullsnotdistinct`, and ownership, with no equivalent normalized full `pg_get_indexdef` comparison.
Attack path:  A hostile or stale same-name index matches the checked attributes but differs in an unchecked catalog attribute -> preflight accepts it as managed -> owner execution can record successful T8 history -> a later EF downgrade reaches `DROP INDEX CONCURRENTLY` and removes the noncanonical object.
Impact:       T8 can falsely attest to a noncanonical object and later destructively remove an object it did not create.
Fix:          Verify every security-relevant catalog attribute or compare a normalized complete definition, including ownership and EF/owner-script parity. `Down` must drop only after complete verification succeeds.
Confidence:   High

Conclusion: FAIL

STATUS:          FAIL
SUMMARY:
The final T8 remediation is blocked by a realistic migration-history forgery path. On an initial-only database, `tabruk_app` can record the corrective migration before its revocation executes, causing EF to skip all T8 hardening. A separate medium-severity gap remains in complete same-name index verification.
WORK_COMPLETED:
- Reviewed the mission definition of done, architecture, decisions, task plan, and test results.
- Reviewed the final T8 migration, owner script, catalog, factory, tests, safety documentation, manifest, and relevant PostgreSQL behavior.
- Threat-modeled hostile same-name objects, PUBLIC inheritance, schema-path mismatch, migration-history forgery, destructive repair/downgrade, and SQL/option injection.
- Checked reported focused tests and manifest verification evidence.
EVIDENCE:
- Initial broad table grant: `src/HusayniaTabruk.Infrastructure/Migrations/20260815075156_InitialPostgresSchema.cs:1050`.
- Factory behavior: `src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContextFactory.cs:109-124`.
- Upgrade guidance: `src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md:63-67`.
- Index verification and downgrade sinks: `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs:55-169`, `:325-344`.
- Owner-script parity: `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql:72-181`.
- Focused local test evidence: `Passed: 17, Failed: 0, Skipped: 0`.
- SHA-256 manifest recomputation: `5/5 listed artifacts matched`.
- PostgreSQL 18.6 mission evidence: 59 persistence tests passed with zero skipped; schema mismatch and missing-history guards exited `3`; initial-only schemas returned true for `tabruk_app` UPDATE privilege on `__EFMigrationsHistory`.
ARTIFACTS:
- `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-08-15-husaynia-t8-final-remediation\security-review.md`
FINDINGS:
- High: migration-history forgery lets the runtime role bypass the complete T8 remediation.
- Medium: incomplete catalog verification can accept and later drop a noncanonical same-name index.
RISKS:
- Migration history can claim T8 while the required privilege and integrity state is absent.
- Downgrade may destructively remove an object not proven to be the exact managed object.
BLOCKERS:
- Unresolved HIGH migration-history forgery bypass.
NEXT_ACTION:
- Remediate the history-attestation/bootstrap design and rerun the independent security gate. Do not ship or accept an exception through this gate.
