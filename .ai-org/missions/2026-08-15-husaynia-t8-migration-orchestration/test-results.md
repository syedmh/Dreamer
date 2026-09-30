# T8M Current Seven-Hash Certification — 2026-08-16 04:05 PDT

## TEST RESULT

Command:

```powershell
# Fresh PostgreSQL 18.6 clusters on ports 63648 and 63649
initdb.exe -D <isolated-data-root> -U postgres --auth=trust --auth-host=trust `
  --auth-local=trust -E UTF8 --locale-provider=icu --icu-locale=en-US
pg_ctl.exe -D <isolated-data-root> -l <log> -o "-h 127.0.0.1 -p <port>" start -w
psql.exe ... -c "CREATE ROLE tabruk_app NOLOGIN;"

dotnet restore .\HusayniaTabruk.sln
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror

dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --no-build `
  --filter "FullyQualifiedName~ApprovedOwnerConstraintCompensationPreservesReplacementAcrossCleanupBranches|FullyQualifiedName~ApprovedOwnerScriptsRejectUnsafePostgresGlobalDefaultsWithoutRewriting"

dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --no-build `
  --filter "FullyQualifiedName~PostgresMigrationAndSchemaTests|FullyQualifiedName~PostgresLeastPrivilegeCatalogTests|FullyQualifiedName~PostgresMigrationArtifactManifestTests"

dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --no-build `
  --filter "FullyQualifiedName~CorrectiveSurfacesSerializeConcurrentManagedTopologyBeforeTrust|FullyQualifiedName~DownAndOwnerRejectHostileIndexSemanticVariantAndPreserveIt|FullyQualifiedName~EffectivePrivilegeAttestationRejectsInheritedPostgres18TablePrivilege"

dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --no-build --filter "Category=Persistence"
dotnet test .\HusayniaTabruk.sln --no-build -m:1
dotnet format .\HusayniaTabruk.sln --no-restore --verify-no-changes --verbosity minimal

npm ci --prefix .\apps\mobile
npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
npm test --prefix .\apps\mobile -- --runInBand

docker compose version
docker compose config

# Get-FileHash recomputation for all seven manifest entries and the manifest itself
# PostgreSQL database/schema/application/event-trigger probes and resource cleanup
```

Result:

```text
PostgreSQL 18.6, UTF8; tabruk_app NOLOGIN/non-superuser

Build succeeded.
    0 Warning(s)
    0 Error(s)

New constraint-OID compensation/global-default-ACL regressions:
Passed 6, Failed 0, Skipped 0

Focused T8M:
Passed 174, Failed 4, Skipped 0, Total 178

Immediate rerun of all three affected methods:
Passed 30, Failed 0, Skipped 0

Persistence:
Passed 222, Failed 0, Skipped 0

Full .NET:
Api.ContractTests 38; Application.Tests 79; Domain.Tests 395; IntegrationTests 222
Passed 734, Failed 0, Skipped 0

Format exit: 0

Mobile:
Test Suites: 1 passed, 1 total
Tests: 1 passed, 1 total
Lint exit: 0
Typecheck exit: 0

Compose:
Docker Compose version v5.4.0
image: postgres:18.6-alpine

Manifest:
Entries: 7
Hash mismatches: 0

Final-cluster pre-stop probes:
NON_TEMPLATE_DBS=postgres
TEST_SCHEMAS=0
TEST_APPLICATIONS=0
TEST_EVENT_TRIGGERS=0

Final cluster stopped; port 63649 closed; data/log removed.
An earlier interrupted-cluster Windows postmaster left stale listener PID 19040 on port 63648
and a locked log after its data root was removed. Stop-Process reported that PID did not exist.
```

Passed:   1167
Failed:   4
Skipped:  0

Failures:

1. `CorrectiveSurfacesSerializeConcurrentManagedTopologyBeforeTrust(surface: "factory", topologyProbe: "descendant")`
   - Expected SQLSTATE `P0001`; actual `XX000`.
   - `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:2388`
   - PostgreSQL logged `tuple concurrently updated`. The raw PostgreSQL internal-error state escaped
     instead of the frozen fail-closed attestation state. The case passed on immediate rerun.
2. `CorrectiveSurfacesSerializeConcurrentManagedTopologyBeforeTrust(surface: "down", topologyProbe: "descendant")`
   - Expected SQLSTATE `P0001`; actual `XX000`.
   - `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:2407`
   - Same intermittent `tuple concurrently updated` behavior; passed on immediate rerun.
3. `DownAndOwnerRejectHostileIndexSemanticVariantAndPreserveIt(... category: "key-count", surface: "down")`
   - Expected SQLSTATE `P0001`; actual `XX000`.
   - `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:1984`
   - Same intermittent raw PostgreSQL internal-error state; passed on immediate rerun.
4. `EffectivePrivilegeAttestationRejectsInheritedPostgres18TablePrivilege(privilege: "TRUNCATE")`
   - Expected SQLSTATE `P0001`; actual `XX000`.
   - `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:1648`
   - Same intermittent raw PostgreSQL internal-error state; passed on immediate rerun.

Observed flake rate for the rerun set: 4 failures in 60 executions across the two runs (6.67%);
each named failing case failed once and passed once. A flaky test is a finding, not a pass.

Coverage of acceptance criteria:

- New constraint-OID compensation branches -> 4 theory cases: PASS.
- Fail-closed unsafe PostgreSQL global defaults, unchanged global ACL fingerprint -> 2 cases: PASS.
- Focused migration/catalog/manifest gate -> 178 executed, but 4 intermittent failures: FAIL.
- Complete persistence claim -> 222/222, zero skips: PASS.
- Complete full-suite claim -> 734/734, zero skips: PASS.
- Build/format -> warning-free build and format exit 0: PASS.
- Mobile/Compose -> install, lint, typecheck, Jest, Compose 18.6 image: PASS.
- Exact seven hashes -> all independently recomputed and matched: PASS.
- Fresh PG18.6 cleanup -> final cluster clean, but one earlier interrupted-cluster stale listener/log
  remained: FAIL.

### Exact current hashes

| Artifact | SHA-256 |
|---|---|
| `20260815075156_InitialPostgresSchema.cs` | `ad9bc814e73f8ac6b76c43f07b09f6a82ac0479501c657112525e2d485db2829` |
| `20260815075156_InitialPostgresSchema.Designer.cs` | `2fd38406cedb8ca86a1153304d4b37bfbe1729b9c298813084abb6380d126bc1` |
| `20260815102612_T8CorrectivePostgresHardening.cs` | `da3491f6a22d1f81dcef2a4e0e36c3ec13e5536a4110446db6e8ecef3c4125d2` |
| `20260815102612_T8CorrectivePostgresHardening.Designer.cs` | `e71f719a271d3a78c3cc468b33f77658a77e25011a271e0ab8134c0b8bfdfd30` |
| `20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql` | `1a7b61274a1fbe21ddc9f1eaedb27889719e62a5aaa8df8837b5d1ef6138ccca` |
| `20260815102612_T8CorrectivePostgresHardening.owner-downgrade.sql` | `a8bfbb0dfb419816656d47aaf6901aed9d9ef675cd6f6638920e0b46e88dd685` |
| `TabrukDbContextModelSnapshot.cs` | `f8143017431644746cdd085d48c53a98c5c2d19e43d32410ca3c45f7f06932b5` |

Manifest SHA-256:
`cb02e6c378b3a033a07827d7d9548ec4c3a873522e1a5ee1a505c1ce469e4e37`

Conclusion: FAIL — the exact current seven hashes are verified and the claimed 222 persistence /
734 full counts reproduce, but the focused gate is flaky and exact cleanup is incomplete. T9 is
not ready.

STATUS:          FAIL
SUMMARY:         Seven hashes match; new 6/6, persistence 222/222, full 734/734, build/format/mobile/Compose pass; focused run had four intermittent XX000 failures.
WORK_COMPLETED:  Executed all requested fresh-PG18.6, focused, persistence, full, build, format, mobile, Compose, manifest, and cleanup gates.
EVIDENCE:        The TEST RESULT above.
ARTIFACTS:       `.ai-org/missions/2026-08-15-husaynia-t8-migration-orchestration/test-results.md`
FINDINGS:        Four contract tests intermittently expose PostgreSQL `XX000 tuple concurrently updated`; immediate rerun passed 30/30.
RISKS:           Passing aggregate persistence/full reruns can mask the focused concurrency flake.
BLOCKERS:        Focused gate is not deterministic; stale interrupted-cluster listener PID 19040/port 63648 and locked log remain.
NEXT_ACTION:     Developer must eliminate or deterministically translate the concurrent catalog-update race to the frozen P0001 contract, add a repeated regression, then rerun all gates from a clean host/session and prove complete cleanup.

---

# CURRENT T8M Artifact and Behavior Certification — 2026-08-16

## TEST RESULT

Command:

```powershell
# Fresh PostgreSQL 18.6 cluster
initdb.exe -D C:\Users\syedhu\AppData\Local\Temp\tabruk-t8m-cert-6bec4780bb2b4d23bd5389e159550c72 -U postgres --auth=trust --auth-host=trust --auth-local=trust -E UTF8 --locale-provider=icu --icu-locale=en-US
pg_ctl.exe -D <data-dir> -l <postgres.log> -o "-h 127.0.0.1 -p 63647" start -w
psql.exe -h 127.0.0.1 -p 63647 -U postgres -d postgres -c "CREATE ROLE tabruk_app NOLOGIN;"

dotnet restore .\HusayniaTabruk.sln
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror

dotnet test tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~ApprovedOwnerCompensationPreservesReplacementAfterCapturedOidAndRecovers|FullyQualifiedName~ApprovedOwnerCompensationLocksOutReplacementBetweenVerificationAndDrop|FullyQualifiedName~ApprovedOwnerScriptsHardenAbsentEffectiveDefaultAcls|FullyQualifiedName~ApprovedOwnerScriptsHardenHostileDefaultAclRows"

dotnet test tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PostgresMigrationAndSchemaTests|FullyQualifiedName~PostgresLeastPrivilegeCatalogTests|FullyQualifiedName~PostgresMigrationArtifactManifestTests"

dotnet test tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "Category=Persistence"
dotnet test .\HusayniaTabruk.sln --no-build -m:1

# Two additional repetitions of both new compensation race tests
dotnet test tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~ApprovedOwnerCompensationPreservesReplacementAfterCapturedOidAndRecovers|FullyQualifiedName~ApprovedOwnerCompensationLocksOutReplacementBetweenVerificationAndDrop"

dotnet format .\HusayniaTabruk.sln --no-restore --verify-no-changes --verbosity minimal

npm ci --prefix .\apps\mobile
npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
npm test --prefix .\apps\mobile -- --runInBand

docker compose version
docker compose config

# Independent SHA-256 recomputation with Get-FileHash
# PostgreSQL database/schema/application/event-trigger probes
pg_ctl.exe -D <data-dir> stop -m fast -w
Remove-Item <data-dir> -Recurse -Force
```

Result:

```text
PostgreSQL: 18.6
Encoding: UTF8
tabruk_app:false

Build succeeded.
    0 Warning(s)
    0 Error(s)

New atomic-compensation race and effective-default-ACL tests:
Passed 6, Failed 0, Skipped 0

Focused T8M:
Passed 172, Failed 0, Skipped 0

Persistence:
Passed 216, Failed 0, Skipped 0

Full .NET:
Api.ContractTests:  Passed 38,  Failed 0, Skipped 0
Application.Tests: Passed 79,  Failed 0, Skipped 0
Domain.Tests:      Passed 395, Failed 0, Skipped 0
IntegrationTests: Passed 216, Failed 0, Skipped 0
Total:             Passed 728, Failed 0, Skipped 0

Additional compensation-race repetitions:
Run 1: Passed 2, Failed 0, Skipped 0
Run 2: Passed 2, Failed 0, Skipped 0
Both new race tests: 12/12 executions across all certification commands, 0% observed flake rate

Format: FORMAT_EXIT=0

Mobile:
Node v24.19.0; npm 11.17.0
Test Suites: 1 passed, 1 total
Tests:       1 passed, 1 total
Lint exit: 0
Typecheck exit: 0

Compose:
Docker Compose version v5.4.0
image: postgres:18.6-alpine

Manifest:
Entries: 7
Hash mismatches: 0

Cleanup:
NON_TEMPLATE_DBS=postgres
TEST_SCHEMAS=0
TEST_APPLICATIONS=0
TEST_EVENT_TRIGGERS=0
PID_EXISTS=False
MATCHING_PROCESSES=0
DATADIR_EXISTS=False
ROOT_EXISTS=False
```

Passed:   1127 executed tests including reruns (728 distinct .NET + 1 mobile configured tests)
Failed:   0
Skipped:  0

Failures:

None.

Coverage of acceptance criteria:

- Current new atomic-compensation behavior -> `ApprovedOwnerCompensationPreservesReplacementAfterCapturedOidAndRecovers` and `ApprovedOwnerCompensationLocksOutReplacementBetweenVerificationAndDrop`: PASS, 12/12 combined executions across certification commands, 0% observed flake rate.
- Current effective default ACL behavior -> `ApprovedOwnerScriptsHardenAbsentEffectiveDefaultAcls` and `ApprovedOwnerScriptsHardenHostileDefaultAclRows`, both Up/Down surfaces: PASS.
- Focused migration/catalog/manifest gate -> 172/172, zero skips: PASS.
- Complete persistence claim -> 216/216, zero skips: PASS.
- Complete .NET claim -> 728/728, zero skips: PASS.
- Warning-free build and format -> 0 warnings, 0 errors; format exit 0: PASS.
- Mobile install/lint/typecheck/Jest and Compose configuration -> all exit 0; PostgreSQL image exactly `18.6-alpine`: PASS.
- Current seven-entry manifest and independent hashes -> every manifest value exactly matched recomputation: PASS.
- Fresh isolated PG18.6 and exact cleanup -> UTF8/NOLOGIN role verified; no test databases, schemas, applications, event triggers, processes, PID, or data root remained: PASS.

### Exact current artifact hashes and timestamps

All timestamps are UTC `LastWriteTimeUtc`.

| Artifact | SHA-256 | Timestamp | Bytes |
|---|---|---:|---:|
| `T8MigrationArtifacts.sha256` | `6411769a699d3592c8c11ddd230d62f0e2beb7a00cc8b8798341add4be28d607` | `2026-08-16T09:58:27.0055241Z` | 819 |
| `20260815075156_InitialPostgresSchema.cs` | `ad9bc814e73f8ac6b76c43f07b09f6a82ac0479501c657112525e2d485db2829` | `2026-08-15T08:58:48.5677490Z` | 67252 |
| `20260815075156_InitialPostgresSchema.Designer.cs` | `2fd38406cedb8ca86a1153304d4b37bfbe1729b9c298813084abb6380d126bc1` | `2026-08-15T08:58:48.5800544Z` | 72961 |
| `20260815102612_T8CorrectivePostgresHardening.cs` | `d6c5fc3ebab13264a6f4bf5ee0ac441d3ba2adb588fe24015e668c755a39438a` | `2026-08-16T09:49:57.5710901Z` | 5998 |
| `20260815102612_T8CorrectivePostgresHardening.Designer.cs` | `e71f719a271d3a78c3cc468b33f77658a77e25011a271e0ab8134c0b8bfdfd30` | `2026-08-15T10:26:12.3499435Z` | 75012 |
| `20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql` | `f462c41249e5d025691e13907fd720b894b99bff08956defdf4e6da2178f19a6` | `2026-08-16T09:53:47.9814087Z` | 70797 |
| `20260815102612_T8CorrectivePostgresHardening.owner-downgrade.sql` | `18eb64f995c1b4650ec8bc7ec698e157aaa784732a7ea6335920c3797445d53f` | `2026-08-16T09:50:17.1340059Z` | 26738 |
| `TabrukDbContextModelSnapshot.cs` | `f8143017431644746cdd085d48c53a98c5c2d19e43d32410ca3c45f7f06932b5` | `2026-08-15T10:26:12.3499435Z` | 74879 |

Conclusion: PASS — current T8M artifacts and behavior are independently certified; T9 is ready.

## Standard Status

STATUS:          PASS
SUMMARY:         Fresh PG18.6 certification reproduced 172 focused, 216 persistence, and 728 full .NET with zero failures/skips; all seven manifest hashes match.
WORK_COMPLETED:  Executed new race/ACL regressions, repeated races, complete gates, independent hash/timestamp capture, and exact cleanup.
EVIDENCE:        The TEST RESULT above.
ARTIFACTS:       `.ai-org/missions/2026-08-15-husaynia-t8-migration-orchestration/test-results.md`
FINDINGS:        Both new compensation races passed 12/12 combined executions (0% observed flake). npm reported deprecated transitive packages and one unapproved install script.
RISKS:           Mobile dependency/install-script warnings remain maintenance concerns; no T8M behavioral or artifact-integrity defect was found.
BLOCKERS:        None.
NEXT_ACTION:     Proceed to T9 final judgment.

---

# T8M Final Independent Certification — 2026-08-16

## TEST RESULT

Command:

```powershell
# Fresh PostgreSQL 18.6, trust auth, UTF-8, ICU en-US, port 53718
initdb.exe -D <fresh-data> --auth=trust --username=syedhu --encoding=UTF8 --locale-provider=icu --icu-locale=en-US
pg_ctl.exe -D <fresh-data> -l <log> -o "-p 53718 -h 127.0.0.1" start
psql.exe -h 127.0.0.1 -p 53718 -U syedhu -d postgres -c "CREATE ROLE tabruk_app NOLOGIN;"

dotnet restore .\HusayniaTabruk.sln
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror

$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=53718;Database=postgres;Username=syedhu;Pooling=false;Timeout=5;Command Timeout=30;Include Error Detail=true'
$env:TABRUK_MIGRATIONS_CONNECTION=$env:TABRUK_TEST_POSTGRES_CONNECTION
$env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin\psql.exe'

dotnet test tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~ApprovedOwnerScriptsRejectInheritedSchemaUsageWithoutDirectGrant|FullyQualifiedName~ApprovedOwnerScriptsRejectCatalogNamedOperatorClassOutsidePgCatalog|FullyQualifiedName~ApprovedOwnerCompensationNeverDropsExternalCanonicalIndexCreatedAfterEntryCapture"
dotnet test tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PostgresMigrationAndSchemaTests|FullyQualifiedName~PostgresLeastPrivilegeCatalogTests|FullyQualifiedName~PostgresMigrationArtifactManifestTests"
dotnet test tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "Category=Persistence"
dotnet test .\HusayniaTabruk.sln --no-build -m:1

# Foreign-index race repeated twice more
dotnet test tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~ApprovedOwnerCompensationNeverDropsExternalCanonicalIndexCreatedAfterEntryCapture"
dotnet test tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~ApprovedOwnerCompensationNeverDropsExternalCanonicalIndexCreatedAfterEntryCapture"

dotnet format .\HusayniaTabruk.sln --no-restore --verify-no-changes --verbosity minimal

npm ci --prefix .\apps\mobile
npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
npm test --prefix .\apps\mobile -- --runInBand

docker compose version
docker compose config

# Independent SHA-256 recomputation of T8MigrationArtifacts.sha256
# Exact database/schema/application/event-trigger checks, pg_ctl stop, PID/data/root removal
```

Result:

```text
PostgreSQL: 18.6
Encoding: UTF8
tabruk_app:false

Build succeeded.
    0 Warning(s)
    0 Error(s)

New hostile ACL/opclass and foreign-index race:
Passed 5, Failed 0, Skipped 0

Focused T8M:
Passed 166, Failed 0, Skipped 0

Persistence:
Passed 210, Failed 0, Skipped 0

Full .NET:
Api.ContractTests:  Passed 38,  Failed 0, Skipped 0
Application.Tests: Passed 79,  Failed 0, Skipped 0
Domain.Tests:      Passed 395, Failed 0, Skipped 0
IntegrationTests: Passed 210, Failed 0, Skipped 0
Total:             Passed 722, Failed 0, Skipped 0

Foreign-index race additional repetitions:
Passed 2, Failed 0, Skipped 0
Race total across certification commands: 6/6, flake rate 0%

Format exit: 0

Mobile:
Test Suites: 1 passed, 1 total
Tests:       1 passed, 1 total
Lint exit: 0
Typecheck exit: 0

Compose:
Docker Compose version v5.4.0
image: postgres:18.6-alpine

Manifest:
ENTRIES=7
HASH_FAILURES=0

Cleanup:
NON_TEMPLATE_DBS=postgres
TEST_SCHEMAS=0
TEST_APPLICATIONS=0
TEST_EVENT_TRIGGERS=0
PID_EXISTS=False
DATADIR_EXISTS=False
ROOT_EXISTS=False
```

Passed:   1106 executed tests across reported successful commands (723 distinct configured tests)
Failed:   0
Skipped:  0

Failures:

None. One initial persistence-suite process ended after test discovery without a result; the same
command was immediately rerun and completed 210/210. Initial PATH-based `npm` and `docker` probes
could not locate the installed tools; explicit local Node 24.19.0/npm 11.17.0 and Docker Compose
v5.4.0 paths executed all gates successfully.

Coverage of acceptance criteria:

- Sole owner production Up/Down, lock lifetime, OID/canonical/owner checks -> focused 166 PASS.
- Direct schema ACL required; inherited USAGE rejected on Up and Down -> new two-case theory PASS.
- Exact type/opclass identity; schema-local `uuid_ops` rejected on Up and Down -> new two-case theory PASS.
- Invocation-OID compensation preserves a canonical foreign index created after entry capture -> new race PASS, 6/6 total executions.
- Compensation/history/serialization/logical Down/re-up/disposable EF matrix -> persistence 210 PASS.
- Exactly seven immutable artifacts -> manifest test plus independent hashes PASS.
- PostgreSQL 18.6 and zero persistence skips -> PASS.
- Build, full .NET 722, format, mobile lint/typecheck/test, Compose config -> PASS.
- Exact database/schema/application/event-trigger/PID/data-root cleanup -> PASS.

Conclusion: PASS

## Standard Status

STATUS:          PASS
SUMMARY:         Fresh PG18.6 certification passed 166 focused, 210 persistence, 722 full .NET, mobile and all gates with zero skips.
WORK_COMPLETED:  Executed new hostile ACL/opclass and foreign-index race regressions, full gates, independent hashes, and exact cleanup.
EVIDENCE:        The TEST RESULT above.
ARTIFACTS:       `.ai-org/missions/2026-08-15-husaynia-t8-migration-orchestration/test-results.md`
FINDINGS:        Foreign-index race passed 6/6 (0% flake). npm reported deprecated transitive packages and one unapproved install script.
RISKS:           Mobile transitive dependency/install-script warnings remain maintenance concerns; no T8M certification failure.
BLOCKERS:        None for the T8M independent test gate.
NEXT_ACTION:     T8M is test-certified and ready for final T9-unblocking judgment.
