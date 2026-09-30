# T8M Ultimate Independent Test Gate

## TEST RESULT

PostgreSQL:
`18.6`, UTF8, fresh trust-authenticated loopback cluster at `127.0.0.1:61693`;
`tabruk_app|false`.

Command:

```powershell
dotnet restore .\HusayniaTabruk.sln
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror

# Each filter was run separately, with an exact pg_default_acl fingerprint query before and after.
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build `
  --filter 'FullyQualifiedName~PostgresMigrationAndSchemaTests.ApprovedOwnerScriptsRejectUnsafePostgresGlobalDefaultsWithoutRewriting'
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build `
  --filter 'FullyQualifiedName~PostgresMigrationAndSchemaTests.ApprovedOwnerScriptsHardenHostileTargetDefaultAclRows'
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build `
  --filter 'FullyQualifiedName~PostgresMigrationAndSchemaTests.ApprovedOwnerScriptsRejectHostileGlobalDefaultAclRowsWithoutRewriting'

dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build `
  --filter 'FullyQualifiedName~PostgresMigrationAndSchemaTests|FullyQualifiedName~PostgresLeastPrivilegeCatalogTests|FullyQualifiedName~PostgresMigrationArtifactManifestTests' `
  --logger 'console;verbosity=minimal' -- xUnit.Seed=20260816

dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --no-build --filter 'Category=Persistence' --logger 'console;verbosity=minimal'
dotnet test .\HusayniaTabruk.sln --no-build -m:1 --logger 'console;verbosity=minimal'
dotnet format .\HusayniaTabruk.sln --no-restore --verify-no-changes --verbosity minimal

# PATH prefixed with the portable Node 24.19.0 directory.
npm ci --prefix .\apps\mobile
npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
npm test --prefix .\apps\mobile -- --runInBand

# Portable Docker CLI and DOCKER_CONFIG containing Compose v5.4.0.
docker compose -f .\docker-compose.yml config
```

Result:

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)

Standalone three ACL methods:
Passed 2/2, 2/2, and 2/2; Failed 0; Skipped 0.
Each exact fingerprint: GLOBAL_ACL_BEFORE=<none>, GLOBAL_ACL_AFTER=<none>, EQUAL=True.

Randomized hostile/catalog gate, xUnit.Seed=20260816:
Passed! - Failed: 0, Passed: 194, Skipped: 0, Total: 194, Duration: 2 m 40 s
RANDOMIZED_GLOBAL_ACL_BEFORE=<none>
RANDOMIZED_GLOBAL_ACL_AFTER=T:{postgres=U/postgres},f:{postgres=X/postgres}
RANDOMIZED_GLOBAL_ACL_EQUAL=False

Persistence:
Passed! - Failed: 0, Passed: 238, Skipped: 0, Total: 238, Duration: 3 m 29 s
PERSISTENCE_GLOBAL_ACL_BEFORE=<none>
PERSISTENCE_GLOBAL_ACL_AFTER=T:{postgres=U/postgres},f:{postgres=X/postgres}
PERSISTENCE_GLOBAL_ACL_EQUAL=False

Full .NET:
Api.ContractTests:  Passed 38,  Failed 0, Skipped 0
Application.Tests: Passed 79,  Failed 0, Skipped 0
Domain.Tests:      Passed 395, Failed 0, Skipped 0
IntegrationTests: Passed 238, Failed 0, Skipped 0
Total:             Passed 750, Failed 0, Skipped 0
FULL_GLOBAL_ACL_BEFORE=<none>
FULL_GLOBAL_ACL_AFTER=T:{postgres=U/postgres},f:{postgres=X/postgres}
FULL_GLOBAL_ACL_EQUAL=False

FORMAT_EXIT=0

Mobile, Node v24.19.0 / npm 11.17.0:
Test Suites: 1 passed, 1 total
Tests:       1 passed, 1 total
Snapshots:   0 total

Compose:
Docker Compose version v5.4.0
image: postgres:18.6-alpine

Hashes:
HASHES_MATCHED=7/7

Cleanup:
FINAL_GLOBAL_ACL=<none>
TEST_SCHEMAS=0
NON_TEMPLATE_DBS=postgres
PROBE_ROLES=0
PID_EXISTS=False
DATADIR_EXISTS=False
```

Passed:   1195 executed tests, including six additional isolation diagnostics
Failed:   3 gate invariants
Skipped:  0

Failures:

1. Randomized hostile/catalog global ACL isolation
   - Expected: exact `pg_default_acl` before and after remains `<none>`.
   - Actual: after is `T:{postgres=U/postgres},f:{postgres=X/postgres}`.
   - Root cause: the three specifically repaired hostile methods now restore exactly, but ordinary
     corrective-migration tests still execute global default-privilege hardening through
     `ApplySafeDisposableDefaultPrivilegesSql`
     (`src/HusayniaTabruk.Infrastructure/Migrations/PostgresLeastPrivilegeCatalog.cs:531`).
     `PostgresTestDatabase.DisposeAsync` only drops its schema and does not restore cluster-global
     ACL state (`tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresIntegrationSupport.cs:201`).

2. Persistence global ACL isolation
   - Expected: `<none>` before and after the 238-test persistence suite.
   - Actual: `<none>` before; hardened `T`/`f` rows after.
   - Root cause: same fixture-wide cleanup gap. The suite passes its assertions while leaking
     cluster-global state.

3. Full-solution global ACL isolation
   - Expected: `<none>` before and after the 750-test solution run.
   - Actual: `<none>` before; hardened `T`/`f` rows after.
   - Root cause: the integration project repeats the same persistence leak.

Coverage of acceptance criteria:

- Fresh PostgreSQL 18.6, UTF8, `tabruk_app NOLOGIN` -> PASS.
- Exact ACL before/after for each of the three repaired standalone methods -> PASS.
- Seeded randomized hostile/catalog 194/194 with zero skips -> assertions PASS; isolation FAIL.
- Persistence 238/238 with zero skips -> assertions PASS; isolation FAIL.
- Full .NET 750/750 with zero skips -> assertions PASS; isolation FAIL.
- Warning-free build and clean format -> PASS.
- Mobile with exact Node 24.19.0/npm 11.17.0 -> PASS.
- Portable Compose when absent from PATH -> PASS.
- Seven production hashes unchanged -> PASS, 7/7.
- Final schema/database/role/process/data-path cleanup -> PASS after explicit ACL reset.
- Zero global ACL leaks without manual remediation -> FAIL.

Conclusion: FAIL — T8M is not ready for T9.

## Standard Status

STATUS:          FAIL

SUMMARY:
All 194/238/750 claimed suites, build, format, pinned mobile, portable Compose, and seven hashes
passed. The repaired three standalone methods restore exact ACL state, but the randomized,
persistence, and full runs still leak PostgreSQL cluster-global default ACL rows.

WORK_COMPLETED:
Provisioned and removed a fresh PostgreSQL 18.6 cluster; executed exact standalone ACL probes,
seeded randomized hostile/catalog, persistence, full solution, build, format, pinned mobile,
portable Compose, immutable hashes, isolation diagnostics, and final cleanup.

EVIDENCE:
The TEST RESULT above.

ARTIFACTS:
- `.ai-org/missions/2026-08-16-husaynia-t8m-independent-test-gate/test-results.md`

FINDINGS:
- The narrow cleanup fix works for the three named methods.
- Fixture-wide cleanup remains incomplete for normal tests applying the corrective migration.
- npm emitted dependency deprecation and one unapproved-install-script warning; configured gates
  passed.

RISKS:
Order-dependent tests and shared PostgreSQL clusters inherit hardened global defaults, masking
behavior and mutating developer/CI infrastructure.

BLOCKERS:
- Exact `pg_default_acl` before=after fails for randomized, persistence, and full gates.

NEXT_ACTION:
Developer must snapshot and restore exact relevant cluster-global default ACL state at the
`PostgresTestDatabase` lifecycle boundary (including exceptional setup/disposal paths), then rerun
the complete 194/238/750 gate without any manual ACL reset between before/after measurements.

---

# T8M Fixture-Wide ACL Restoration Final Closure

## TEST RESULT

Command:

```powershell
dotnet restore .\HusayniaTabruk.sln
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror

dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --no-build --filter 'FullyQualifiedName~PostgresTestDatabaseLifecycleTests' `
  --logger 'console;verbosity=minimal'

dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --no-build `
  --filter 'FullyQualifiedName~PostgresMigrationAndSchemaTests|FullyQualifiedName~PostgresLeastPrivilegeCatalogTests|FullyQualifiedName~PostgresMigrationArtifactManifestTests' `
  --logger 'console;verbosity=minimal' -- xUnit.Seed=20260816

dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --no-build --filter 'Category=Persistence' --logger 'console;verbosity=minimal'

dotnet test .\HusayniaTabruk.sln --no-build -m:1 --logger 'console;verbosity=minimal'
dotnet format .\HusayniaTabruk.sln --no-restore --verify-no-changes --verbosity minimal

# PATH prefixed with portable Node 24.19.0.
npm ci --prefix .\apps\mobile
npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
npm test --prefix .\apps\mobile -- --runInBand

# Portable Docker CLI and DOCKER_CONFIG containing Compose v5.4.0.
docker compose -f .\docker-compose.yml config
```

Result:

```text
PostgreSQL 18.6, UTF8, fresh trust-authenticated loopback cluster
tabruk_app|false

Build succeeded.
    0 Warning(s)
    0 Error(s)

Lifecycle:
Passed! - Failed: 0, Passed: 4, Skipped: 0, Total: 4
LIFECYCLE_ACL_BEFORE=<none>
LIFECYCLE_ACL_AFTER=<none>
LIFECYCLE_ACL_EQUAL=True

Randomized hostile/catalog, xUnit.Seed=20260816:
Passed! - Failed: 0, Passed: 194, Skipped: 0, Total: 194
RANDOMIZED_ACL_BEFORE=<none>
RANDOMIZED_ACL_AFTER=<none>
RANDOMIZED_ACL_EQUAL=True

Persistence:
Passed! - Failed: 0, Passed: 238, Skipped: 0, Total: 238
PERSISTENCE_ACL_BEFORE=<none>
PERSISTENCE_ACL_AFTER=<none>
PERSISTENCE_ACL_EQUAL=True

Full solution:
Api.ContractTests:  Passed 38,  Failed 0, Skipped 0
Application.Tests: Passed 79,  Failed 0, Skipped 0
Domain.Tests:      Passed 395, Failed 0, Skipped 0
IntegrationTests: Passed 242, Failed 0, Skipped 0
Total:             Passed 754, Failed 0, Skipped 0
FULL_ACL_BEFORE=<none>
FULL_ACL_AFTER=<none>
FULL_ACL_EQUAL=True

FORMAT_EXIT=0
HASHES_MATCHED=7/7

Mobile, Node v24.19.0 / npm 11.17.0:
Test Suites: 1 passed, 1 total
Tests:       1 passed, 1 total
Snapshots:   0 total
Lint exit 0; typecheck exit 0.

Docker 29.7.2; Docker Compose v5.4.0
image: postgres:18.6-alpine
COMPOSE_CONFIG_EXIT=0

FINAL_GLOBAL_ACL=<none>
TEST_SCHEMAS=0
NON_TEMPLATE_DBS=postgres
PROBE_ROLES=0
PID_EXISTS=False
DATADIR_EXISTS=False
REGISTRY_EXISTS=False
```

Passed:   1191 executed tests across the requested gates
Failed:   0
Skipped:  0

Failures:
None.

Coverage of acceptance criteria:

- Fresh PostgreSQL 18.6, UTF8, `tabruk_app NOLOGIN` -> PASS.
- Lifecycle restoration including exceptional path and concurrent fixtures, 4/4 -> PASS.
- Exact ACL equality around lifecycle, randomized, persistence, and full gates -> PASS.
- Seeded randomized hostile/catalog, 194/194, zero skips -> PASS.
- Persistence, 238/238, zero skips -> PASS.
- Full solution, 754/754, zero skips -> PASS.
- Warning-free build and clean format -> PASS.
- Pinned mobile install/lint/typecheck/test -> PASS.
- Portable Compose config and PostgreSQL 18.6 image -> PASS.
- Seven immutable migration hashes unchanged -> PASS, 7/7.
- Final ACL/schema/database/role/process/data-directory/registry cleanup -> PASS.

Conclusion: PASS — T9 ready.

## Standard Status

STATUS:          PASS

SUMMARY:
Fixture-wide ACL restoration is independently closed. Every requested suite passed with exact
`pg_default_acl` before/after equality and zero skipped tests.

WORK_COMPLETED:
Provisioned a fresh PostgreSQL 18.6 cluster; ran lifecycle/concurrent-fixture, seeded randomized,
persistence, full solution, build, format, pinned mobile, portable Compose, immutable-hash, and
cleanup gates.

EVIDENCE:
The TEST RESULT above.

ARTIFACTS:
- `.ai-org/missions/2026-08-16-husaynia-t8m-independent-test-gate/test-results.md`

FINDINGS:
- npm reported deprecated transitive packages and one unapproved install script; all configured
  mobile gates passed.
- The first cleanup wrapper used PowerShell's reserved `$PID` variable and exited after all product
  gates. Immediate independent cleanup then passed with PID, data directory, and registry absent.

RISKS:
- Non-blocking mobile dependency maintenance debt remains.

BLOCKERS:
None.

NEXT_ACTION:
Proceed to T9.
