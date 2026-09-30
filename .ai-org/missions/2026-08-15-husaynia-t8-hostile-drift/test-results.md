# T8 hostile-drift developer test results

Date: 2026-08-15

## Environment

- PostgreSQL server and psql: `18.6`
- .NET SDK/target: repository-pinned .NET 10
- Node: `24.19.0`
- npm: `11.17.0`
- Docker/Compose: unavailable (`docker` command not found)
- Git commands: none run

The local portable PostgreSQL server was restarted on port 55432 after the first targeted attempt
reported the PostgreSQL-dependent test skipped because the server was not listening.

```text
pg_ctl start:
database system is ready to accept connections
server started

psql (PostgreSQL) 18.6
SHOW server_version -> 18.6
```

## T7 scope-clarification regression

The earlier full solution run reproduced the stale test failure before this scope clarification:

```text
PostgresIndependentGateTests.UnavailablePostgresEndpointFailsClosedInsteadOfUsingAnotherProvider
System.InvalidOperationException:
TABRUK_MIGRATIONS_CONNECTION requires exactly one Search Path schema.
PostgresIndependentGateTests.cs:133
```

The test-only unavailable connection now explicitly supplies matching
`Search Path=public` and `Options=-c tabruk.target_schema=public`. Production code and assertions
were not changed.

Command after rebuilding:

```powershell
$env:TABRUK_TEST_POSTGRES_CONNECTION = 'Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'
$env:TABRUK_TEST_PSQL_PATH = "$env:LOCALAPPDATA\PostgreSQL18Portable\pgsql\bin\psql.exe"
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PostgresIndependentGateTests.UnavailablePostgresEndpointFailsClosedInsteadOfUsingAnotherProvider" --logger "console;verbosity=normal"
```

```text
Test Run Successful.
Total tests: 1
Passed: 1
Total time: 5.5182 Seconds
```

## Restore and build

```powershell
dotnet restore .\HusayniaTabruk.sln
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror
```

```text
All projects are up-to-date for restore.
Build succeeded.
0 Warning(s)
0 Error(s)
Time Elapsed 00:00:03.27
```

## Focused migration/security gates

```powershell
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PostgresLeastPrivilegeCatalogTests|FullyQualifiedName~PostgresMigrationArtifactManifestTests" --logger "console;verbosity=normal"
```

```text
Test Run Successful.
Total tests: 4
Passed: 4
Skipped: 0
Total time: 1.2145 Seconds
```

```powershell
$env:TABRUK_TEST_POSTGRES_CONNECTION = 'Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'
$env:TABRUK_TEST_PSQL_PATH = "$env:LOCALAPPDATA\PostgreSQL18Portable\pgsql\bin\psql.exe"
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PostgresMigrationAndSchemaTests" --logger "console;verbosity=normal"
```

```text
Test Run Successful.
Total tests: 35
Passed: 35
Skipped: 0
Total time: 30.2283 Seconds
```

Coverage preserved factory validation; real EF Up/Down schema rejection; hostile non-unique,
invalid, and wrong-definition objects; object preservation; history-last failures; correct
validated/unvalidated object reuse; duplicate and chronology retry; PUBLIC table/sequence grants;
fresh, upgrade, Down, and re-Up; and owner first-run/rerun/schema/retry behavior.

## Full solution

```powershell
$env:TABRUK_TEST_POSTGRES_CONNECTION = 'Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'
$env:TABRUK_TEST_PSQL_PATH = "$env:LOCALAPPDATA\PostgreSQL18Portable\pgsql\bin\psql.exe"
dotnet test .\HusayniaTabruk.sln --no-build --logger "console;verbosity=minimal"
```

```text
HusayniaTabruk.Api.ContractTests: Passed 38, Failed 0, Skipped 0
HusayniaTabruk.Application.Tests: Passed 79, Failed 0, Skipped 0
HusayniaTabruk.Domain.Tests: Passed 395, Failed 0, Skipped 0
HusayniaTabruk.IntegrationTests: Passed 83, Failed 0, Skipped 0

Aggregate: Passed 595, Failed 0, Skipped 0, Total 595.
```

## Format

```powershell
dotnet format .\HusayniaTabruk.sln --verify-no-changes --no-restore
```

```text
Exit code 0; no output.
```

## Mobile

The installed Node distribution was invoked by absolute path because Node was not present on this
process's PATH.

```powershell
npm ci --prefix .\apps\mobile
npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
npm test --prefix .\apps\mobile -- --runInBand
```

```text
v24.19.0
npm 11.17.0
added 1028 packages in 1m
lint: exit code 0
typecheck: exit code 0
Test Suites: 1 passed, 1 total
Tests: 1 passed, 1 total
Snapshots: 0 total
```

`npm ci` also reported existing dependency deprecation warnings and one pending `allowScripts`
entry for `unrs-resolver@1.12.2`; no package or lock-file change was made.

## Compose

```powershell
docker compose config
```

```text
docker: The term 'docker' is not recognized as a name of a cmdlet, function, script file, or executable program.
```

Compose remains environmentally unavailable. The PostgreSQL gate ran against the local portable
PostgreSQL 18.6 server with the required `tabruk_app NOLOGIN` role and PostgreSQL 18.6 psql.

## Hash verification

Manifest:

```text
20260815075156_InitialPostgresSchema.cs: ad9bc814e73f8ac6b76c43f07b09f6a82ac0479501c657112525e2d485db2829
20260815075156_InitialPostgresSchema.Designer.cs: 2fd38406cedb8ca86a1153304d4b37bfbe1729b9c298813084abb6380d126bc1
20260815102612_T8CorrectivePostgresHardening.cs: 4d39a82e1b221270fc1cf2d21303b544ee8f7fca474312879fd2b3fe76cb9c59
20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql: 5584f40c250dfaa53438fa5a0f591a4b5afa69f1124e42a6f255ddebd23c06ea
PostgresLeastPrivilegeCatalog.cs: 2b87f0531bb5a507b527807b0a57b832b81928e9dfe679d60069f550c04ad19d
```

Protected generated hashes:

```text
20260815102612_T8CorrectivePostgresHardening.Designer.cs: e71f719a271d3a78c3cc468b33f77658a77e25011a271e0ab8134c0b8bfdfd30
TabrukDbContextModelSnapshot.cs: f8143017431644746cdd085d48c53a98c5c2d19e43d32410ca3c45f7f06932b5
```

The immutable initial hashes match the frozen values. No initial migration/designer, corrective
designer, snapshot, application code, package file, or production contract was changed.

## Developer gate result

`PASS`: T7 is complete. The clarified stale T8 independent migration test now honors the explicit
schema contract; focused catalog/manifest passed 4/4, hostile PostgreSQL 18.6 passed 35/35 with zero
skips, the full solution passed 595/595 with zero skips, and build, format, mobile, and hash gates
passed. Compose alone remains unexecuted because the `docker` command is absent from the
environment.

---

# Independent test-engineer gate

Date: 2026-08-15

No Git command was run. No production, migration, test, package, or documentation source file was
changed by this gate.

## TEST RESULT

### PostgreSQL environment

Command:

```powershell
$pgroot=Join-Path $env:LOCALAPPDATA 'PostgreSQL18Portable'
$pgctl=Join-Path $pgroot 'pgsql\bin\pg_ctl.exe'
$data=Join-Path $pgroot 'data'
$log=Join-Path $pgroot 'postgres.log'
& $pgctl start -D $data -l $log -o '"-p" "55432"' -w

$psql=Join-Path $env:LOCALAPPDATA 'PostgreSQL18Portable\pgsql\bin\psql.exe'
& $psql -h 127.0.0.1 -p 55432 -U tabruk -d tabruk -v ON_ERROR_STOP=1 -Atc `
  "SHOW server_version; SELECT current_database() || '|' || current_user; SELECT rolname || '|' || rolcanlogin FROM pg_roles WHERE rolname='tabruk_app';"
```

Result:

```text
server started
18.6
tabruk|tabruk
tabruk_app|false
```

This was a real local non-production PostgreSQL 18.6 server. The test connection did not use an
alternate provider.

### Restore and build

Command:

```powershell
dotnet restore .\HusayniaTabruk.sln
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror
```

Result:

```text
All projects are up-to-date for restore.
Build succeeded.
0 Warning(s)
0 Error(s)
Time Elapsed 00:00:03.31
```

### Catalog and manifest

Command:

```powershell
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PostgresLeastPrivilegeCatalogTests|FullyQualifiedName~PostgresMigrationArtifactManifestTests" --logger "console;verbosity=normal"
```

Result:

```text
Test Run Successful.
Total tests: 4
Passed: 4
Total time: 1.7617 Seconds
```

Passed: 4
Failed: 0
Skipped: 0

### Hostile PostgreSQL migration gate

Command:

```powershell
$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'
$env:TABRUK_TEST_PSQL_PATH=Join-Path $env:LOCALAPPDATA 'PostgreSQL18Portable\pgsql\bin\psql.exe'
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PostgresMigrationAndSchemaTests" --logger "console;verbosity=normal"
```

Result:

```text
Test Run Successful.
Total tests: 35
Passed: 35
Total time: 37.8806 Seconds
```

Passed: 35
Failed: 0
Skipped: 0

Flake check, exact repeat:

```powershell
$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'
$env:TABRUK_TEST_PSQL_PATH=Join-Path $env:LOCALAPPDATA 'PostgreSQL18Portable\pgsql\bin\psql.exe'
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PostgresMigrationAndSchemaTests" --logger "console;verbosity=minimal"
```

```text
Passed! - Failed: 0, Passed: 35, Skipped: 0, Total: 35, Duration: 25 s
```

Observed flake rate: 0/2 runs.

### Full solution

Command:

```powershell
$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'
$env:TABRUK_TEST_PSQL_PATH=Join-Path $env:LOCALAPPDATA 'PostgreSQL18Portable\pgsql\bin\psql.exe'
dotnet test .\HusayniaTabruk.sln --no-build --logger "console;verbosity=minimal"
```

Result:

```text
Api.ContractTests: Passed 38, Failed 0, Skipped 0
Application.Tests: Passed 79, Failed 0, Skipped 0
Domain.Tests: Passed 395, Failed 0, Skipped 0
IntegrationTests: Passed 83, Failed 0, Skipped 0
```

Passed: 595
Failed: 0
Skipped: 0

The complete persistence-bearing integration project reported 83 passed and zero skipped.

### Format

Command:

```powershell
dotnet format .\HusayniaTabruk.sln --verify-no-changes --no-restore
```

Result:

```text
Exit code 0; no output.
```

### Mobile

Command:

```powershell
$nodeDir="$env:LOCALAPPDATA\HusayniaTabruk\Node24\node-v24.19.0-win-x64"
$env:PATH="$nodeDir;$env:PATH"
$node=Join-Path $nodeDir 'node.exe'
$npm=Join-Path $nodeDir 'node_modules\npm\bin\npm-cli.js'
& $node --version
& $node $npm --version
& $node $npm ci --prefix .\apps\mobile
& $node $npm run lint --prefix .\apps\mobile
& $node $npm run typecheck --prefix .\apps\mobile
& $node $npm test --prefix .\apps\mobile -- --runInBand
```

Result:

```text
v24.19.0
11.17.0
added 1028 packages in 34s
lint: exit code 0
typecheck: exit code 0
Test Suites: 1 passed, 1 total
Tests: 1 passed, 1 total
Snapshots: 0 total
```

The first independent `npm ci` attempt used the absolute npm CLI without adding its Node directory
to `PATH`; the `unrs-resolver` child postinstall could not resolve `node`. The command above fixed
only the process environment and then passed without a source change. Existing deprecation and
pending `allowScripts` warnings remain.

### Compose

Command:

```powershell
docker compose config
```

Result:

```text
docker: The term 'docker' is not recognized as a name of a cmdlet, function, script file, or executable program.
```

Compose is environmentally unexecutable because no Docker CLI exists. This does not affect the
mandatory database gate, which executed against PostgreSQL 18.6 with zero skips.

### Manifest and immutable hashes

Command:

```powershell
$root='.\src\HusayniaTabruk.Infrastructure\Migrations'
Get-Content (Join-Path $root 'T8MigrationArtifacts.sha256') | ForEach-Object {
  $parts=$_ -split ': ',2
  $actual=(Get-FileHash -Algorithm SHA256 (Join-Path $root $parts[0])).Hash.ToLowerInvariant()
  "$($parts[0]) expected=$($parts[1]) actual=$actual match=$($actual -eq $parts[1])"
}
```

Result:

```text
20260815075156_InitialPostgresSchema.cs match=True
20260815075156_InitialPostgresSchema.Designer.cs match=True
20260815102612_T8CorrectivePostgresHardening.cs match=True
20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql match=True
PostgresLeastPrivilegeCatalog.cs match=True
all_manifest_hashes_match=True
```

Protected files independently calculated:

```text
20260815102612_T8CorrectivePostgresHardening.Designer.cs: e71f719a271d3a78c3cc468b33f77658a77e25011a271e0ab8134c0b8bfdfd30
TabrukDbContextModelSnapshot.cs: f8143017431644746cdd085d48c53a98c5c2d19e43d32410ca3c45f7f06932b5
```

## Failures

No functional test failed. Docker Compose was not executable due to the missing Docker CLI. The
first mobile dependency-install invocation had an environment-only Node `PATH` error; the corrected
reproducible command passed.

## Coverage of acceptance criteria

- Hostile non-unique index fails closed, history absent, object preserved ->
  `CorrectiveMigrationRejectsHostileSameNameIndexBeforeHistoryAndPreservesIt` and
  `ApprovedOwnerScriptRejectsHostileSameNameIndexAndPreservesIt`: PASS.
- Permissive `CHECK(true)` fails closed, history absent, object preserved ->
  `CorrectiveMigrationRejectsHostileSameNameConstraintBeforeHistoryAndPreservesIt` and
  `ApprovedOwnerScriptRejectsHostileSameNameConstraintAndPreservesIt`: PASS.
- Failure precedes corrective history insertion ->
  hostile-object tests, `CorrectiveMigrationRejectsDuplicatePreexistingWaitlistPositionsAtomically`,
  `CorrectiveMigrationRejectsPreexistingInvalidChronologyAtomically`,
  `ApprovedOwnerIdempotentScriptFailsWithoutInitialMigrationHistoryAndWritesNoCorrectiveHistory`,
  and chronology-retry test: PASS.
- Correct-object idempotency and unvalidated-check validation ->
  `CorrectiveMigrationReusesCorrectExistingObjectsAndValidatesRetryableConstraint` and
  `ApprovedOwnerScriptReusesCorrectUnvalidatedObjectsAndValidatesConstraint`: PASS.
- PUBLIC denied after Up, successful Down, failed Down, owner first run/rerun ->
  `CorrectiveMigrationUpgradeDownAndReapplyPreservesInitialDataAndPrivilegePosture`,
  `CorrectiveDownRejectsHostileObjectPreservesHistoryAndFailsClosedPrivileges`,
  `ApprovedOwnerIdempotentScriptAppliesAndReappliesOnInitialOnlySchema`, and catalog parity tests:
  PASS.
- EF Up and downgrade reject missing, multi-schema, mismatch, and unresolved targets with unchanged
  history -> `EfUpgradeSchemaMatrixRejectsAmbiguousOrUnresolvedTargetsWithoutHistoryMutation` and
  `EfDowngradeSchemaMatrixRejectsAmbiguousOrUnresolvedTargetsWithoutHistoryMutation`: PASS.
- Factory and owner require one explicit matching schema ->
  factory acceptance/rejection theories,
  `ApprovedOwnerIdempotentScriptRequiresMatchingExplicitSchemaAndDoesNotHardenAlternateSchema`, and
  `ApprovedOwnerScriptSchemaMatrixRejectsInvalidTargetsBeforeHistoryMutation`: PASS.
- Fresh, upgrade, Down, re-Up, duplicate/chronology retry, and interrupted invalid index ->
  migration lifecycle, retry, and invalid-index tests: PASS.
- Manifest exact five-file allowlist and immutable initial hashes ->
  `ManifestPinsExactlyTheExpectedMigrationArtifacts` plus independent SHA-256 calculation: PASS.
- Real PostgreSQL 18.6 persistence with zero skips -> server/role query, focused 35/35 twice, and
  full integration 83/83 with zero skips: PASS.
- Full build/tests/format/mobile -> PASS.
- Compose configuration -> ENVIRONMENTAL LIMITATION; Docker CLI absent.
- Independent security review, code review, and engineering-judge approvals -> not produced by this
  test-engineer gate; pending separate gates.
- Provenance limitation -> recorded in architecture/runbook and remains a residual process risk
  because the project is untracked and Git operations were prohibited: PASS as documented risk.

Conclusion: PASS for the independent test gate. Mandatory PostgreSQL 18.6 persistence evidence is
real and zero-skip. Compose remains an explicitly isolated environmental limitation; the separate
security-review, code-review, and judge gates remain pending.

---

# R1-R6 forged-history rework developer evidence

Date: 2026-08-15

## PostgreSQL environment

```text
psql (PostgreSQL) 18.6
SHOW server_version -> 18.6
tabruk_app|false
```

## Restore and build

```powershell
dotnet restore .\HusayniaTabruk.sln
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror
```

```text
All projects are up-to-date for restore.
Build succeeded.
0 Warning(s)
0 Error(s)
Time Elapsed 00:00:02.21
```

## Catalog and manifest

```powershell
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PostgresLeastPrivilegeCatalogTests|FullyQualifiedName~PostgresMigrationArtifactManifestTests" --logger "console;verbosity=minimal"
```

```text
Passed: 5
Failed: 0
Skipped: 0
Total: 5
```

## PostgreSQL 18.6 hostile migration gate

```powershell
$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'
$env:TABRUK_TEST_PSQL_PATH=Join-Path $env:LOCALAPPDATA 'PostgreSQL18Portable\pgsql\bin\psql.exe'
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PostgresMigrationAndSchemaTests" --logger "console;verbosity=minimal"
```

```text
Passed: 53
Failed: 0
Skipped: 0
Total: 53
Duration: 41 s
```

The gate includes forged T8 history before trust, separately committed revokes, owner reconstruction,
fully attested forged-row no-op, empty EF bootstrap, unknown history, exact retry debris,
interrupted downgrade mismatch, and parameterized index key-order, expression/key-count,
sort/null-option, and relation-option variants across Up, Down, and owner execution. All prior
schema, PUBLIC, lifecycle, hostile-object, chronology, and retry cases remain covered.

## Full solution

```powershell
dotnet test .\HusayniaTabruk.sln --no-build --logger "console;verbosity=minimal"
```

```text
Api.ContractTests: Passed 38, Failed 0, Skipped 0
Application.Tests: Passed 79, Failed 0, Skipped 0
Domain.Tests: Passed 395, Failed 0, Skipped 0
IntegrationTests: Passed 102, Failed 0, Skipped 0
Aggregate: Passed 614, Failed 0, Skipped 0
```

The first full-suite rework attempt exposed concurrent privilege-hardening catalog updates
(`XX000: tuple concurrently updated`) in two idempotency concurrency tests. The root cause was
concurrent owner connections executing the fixed ACL statements. A fixed transaction-scoped
advisory lock now serializes only the hardening transaction; the exact two failed tests passed 2/2,
and the full suite then passed 614/614.

## Format and mobile

```powershell
dotnet format .\HusayniaTabruk.sln --verify-no-changes --no-restore
```

```text
Exit code 0; no output.
```

```text
npm ci: added 1028 packages
lint: exit code 0
typecheck: exit code 0
Test Suites: 1 passed, 1 total
Tests: 1 passed, 1 total
Snapshots: 0 total
```

Existing npm deprecation and pending `allowScripts` warnings remain; package and lock files were not
changed.

## Hashes

```text
20260815075156_InitialPostgresSchema.cs:
  ad9bc814e73f8ac6b76c43f07b09f6a82ac0479501c657112525e2d485db2829
20260815075156_InitialPostgresSchema.Designer.cs:
  2fd38406cedb8ca86a1153304d4b37bfbe1729b9c298813084abb6380d126bc1
20260815102612_T8CorrectivePostgresHardening.cs:
  80d5463f242f4c198e366cc0183b0ea95fc32b2b90091a8cff415a1f2d575b1a
20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql:
  f9fe6fc0dbacc9311ac5be922140f2159480c059b0b12456cc64293b59f440f5
PostgresLeastPrivilegeCatalog.cs:
  2c41f725e849ea64b86845d089198f2be0014796dc4d625d5d4b535a3b142123
```

All five manifest entries matched. Protected generated hashes remained:

```text
20260815102612_T8CorrectivePostgresHardening.Designer.cs:
  e71f719a271d3a78c3cc468b33f77658a77e25011a271e0ab8134c0b8bfdfd30
TabrukDbContextModelSnapshot.cs:
  f8143017431644746cdd085d48c53a98c5c2d19e43d32410ca3c45f7f06932b5
```

## Compose

```powershell
docker compose config
```

```text
docker: The term 'docker' is not recognized.
```

Compose remains environmentally unavailable. No Git command was run.

## Developer rework result

`PASS`: R1-R6 are complete and self-verified. Independent test, security, code-review, and
engineering-judge gates are invalidated by this rework and must be rerun.

---

# R1-R6 final independent test-engineer gate

Date: 2026-08-15

## TEST RESULT

### Commands

```powershell
# Disposable PostgreSQL 18.6 cluster: initdb --auth=trust --encoding=UTF8 --locale=C,
# pg_ctl start on 127.0.0.1:56599, then CREATE ROLE tabruk_app NOLOGIN.

dotnet restore .\HusayniaTabruk.sln
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror

dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PostgresLeastPrivilegeCatalogTests|FullyQualifiedName~PostgresMigrationArtifactManifestTests" --logger "console;verbosity=normal"

$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=56599;Database=postgres;Username=postgres;Pooling=false;Timeout=5;Command Timeout=30'
$env:TABRUK_TEST_PSQL_PATH="$env:LOCALAPPDATA\PostgreSQL18Portable\pgsql\bin\psql.exe"
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PostgresMigrationAndSchemaTests" --logger "console;verbosity=normal"
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PostgresMigrationAndSchemaTests" --logger "console;verbosity=minimal"
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "Category=Persistence" --logger "console;verbosity=minimal"
dotnet test .\HusayniaTabruk.sln --no-build --logger "console;verbosity=minimal"

dotnet format .\HusayniaTabruk.sln --verify-no-changes --no-restore

# Node 24.19.0 / npm 11.17.0 portable installation:
npm ci --prefix .\apps\mobile
npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
npm test --prefix .\apps\mobile -- --runInBand

docker compose config
```

### Real summary output

```text
PostgreSQL:
18.6
tabruk_app | f

Build succeeded.
    0 Warning(s)
    0 Error(s)

Catalog/manifest:
Total tests: 5
Passed: 5

Hostile migration run 1:
Total tests: 53
Passed: 53

Hostile migration run 2:
Failed: 0, Passed: 53, Skipped: 0, Total: 53, Duration: 49 s

Persistence category:
Failed: 0, Passed: 102, Skipped: 0, Total: 102, Duration: 1 m 31 s

Full .NET:
Api.ContractTests:  Failed 0, Passed 38,  Skipped 0
Application.Tests: Failed 0, Passed 79,  Skipped 0
Domain.Tests:      Failed 0, Passed 395, Skipped 0
IntegrationTests: Failed 0, Passed 102, Skipped 0
Aggregate:         Failed 0, Passed 614, Skipped 0

Format:
FORMAT_EXIT=0

Mobile:
Test Suites: 1 passed, 1 total
Tests:       1 passed, 1 total
Snapshots:   0 total

Compose:
docker: The term 'docker' is not recognized as a name of a cmdlet, function, script file, or executable program.

Cleanup:
tabruk_it_* schemas=0
PID_STILL_RUNNING=False
DATADIR_EXISTS_AFTER_REMOVE=False
```

Passed:   614 full .NET + 1 mobile
Failed:   0
Skipped:  0

Focused reruns also passed: catalog/manifest 5/5, hostile PostgreSQL 53/53 twice, and persistence
102/102. Hostile-suite observed flake rate: 0/2.

### Failures

None. Docker Compose was not executed because the Docker CLI is absent; Compose was optional only
when Docker exists. npm emitted existing dependency deprecation and pending `allowScripts`
warnings, but all configured mobile gates passed.

### Coverage of acceptance criteria

- Forged T8 history fails before EF trust and committed privilege hardening survives ->
  `ForgedCorrectiveHistoryFailsBeforeTrustAndCommittedHardeningSurvives`: PASS.
- Owner recovery after forged history, then EF no-op ->
  `OwnerScriptReconstructsForgedHistoryAndEfThenAcceptsAttestedNoOp`: PASS.
- Exact fully attested forged state is accepted as no-op ->
  `FullyCorrectForgedHistoryIsAcceptedAsAlreadyApplied`: PASS.
- Exact retry debris is reused/validated and history is recorded last ->
  `CorrectiveMigrationReusesCorrectExistingObjectsAndValidatesRetryableConstraint` and
  `ApprovedOwnerScriptReusesCorrectUnvalidatedObjectsAndValidatesConstraint`: PASS.
- Interrupted downgrade mismatch fails closed ->
  `InterruptedDowngradeHistoryObjectMismatchFailsClosed`: PASS.
- Hostile index semantics remain untouched across Up, Down, and owner surfaces ->
  parameterized `CorrectiveMigrationRejectsHostileIndexSemanticVariantAndPreservesIt` and
  `DownAndOwnerRejectHostileIndexSemanticVariantAndPreserveIt`: PASS.
- Hostile same-name index/constraint and history-last ordering ->
  corrective and owner hostile-object tests: PASS.
- PUBLIC grants, exact role privilege matrix, and `tabruk_app NOLOGIN` ->
  lifecycle/failed-Down/owner tests plus real role query: PASS.
- Missing, multi-schema, mismatched, and unresolved schema matrix ->
  factory, EF upgrade/downgrade, and owner schema-matrix tests: PASS.
- Fresh, upgrade, Down, re-Up lifecycle and retry behavior -> migration lifecycle tests: PASS.
- Immutable initial files and exact five-file manifest -> manifest test plus SHA-256 recomputation:
  PASS; all five entries matched, corrective designer and snapshot hashes remained frozen.
- Real PostgreSQL 18.6 and PostgreSQL 18.6 psql with zero persistence skips -> PASS.
- Full build/tests/format/mobile -> PASS.
- Compose if Docker exists -> NOT APPLICABLE; Docker CLI absent.
- Independent security, code-review, and judge gates -> GAP; separate gates remain required.
- Provenance limitation -> documented residual process risk: PASS.

Conclusion: PASS

## Standard status

STATUS:          PASS
SUMMARY:
R1-R6 independently passed on a fresh disposable PostgreSQL 18.6 cluster. The hostile suite passed
53/53 twice, the persistence category passed 102/102, the full .NET suite passed 614/614, and build,
format, mobile, manifest, immutable-hash, cleanup, and zero-skip gates passed.
WORK_COMPLETED:
Created and removed an isolated loopback PostgreSQL cluster; executed forged-history, committed
revocation, owner recovery, exact no-op, retry debris, interrupted downgrade, hostile semantic
object, PUBLIC privilege, schema matrix, lifecycle, manifest, full regression, format, and mobile
gates. No production or test source was changed.
EVIDENCE:
The TEST RESULT above.
ARTIFACTS:
- `.ai-org/missions/2026-08-15-husaynia-t8-hostile-drift/test-results.md`
FINDINGS:
- No functional failures or persistence skips.
- Hostile-suite flake signal: 0 failures in 2 identical runs.
- Docker CLI absent; Compose was therefore not applicable under the mission command contract.
- Existing npm deprecation and `allowScripts` warnings remain.
RISKS:
- Repository provenance remains unverifiable under the no-Git constraint.
- Dependency warnings should be reviewed during package maintenance.
BLOCKERS:
None for the independent test gate.
NEXT_ACTION:
Run the independent security-review, code-review, and engineering-judge gates.

---

# Final independent gate after ef-bootstrap and exact-privilege rework

Date: 2026-08-15

## TEST RESULT

### Commands

```powershell
# Fresh PostgreSQL 18.6 cluster
initdb.exe -D $dataDir -U postgres --auth=trust --auth-host=trust --auth-local=trust -E UTF8 --locale=C
pg_ctl.exe -D $dataDir -l $logPath -o "-h 127.0.0.1 -p 62418" start -w
psql.exe -h 127.0.0.1 -p 62418 -U postgres -d postgres -v ON_ERROR_STOP=1 -c 'CREATE ROLE tabruk_app NOLOGIN;'

dotnet restore .\HusayniaTabruk.sln
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror

# Focused alternate-grantor PUBLIC ACL and grant-option suite
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PreHistoryGateAllowsExactEmptyEfBootstrapState|FullyQualifiedName~EmptyEfBootstrapCommitsHistoryRevocationAgainstPreOpenedAttacker|FullyQualifiedName~EmptyEfBootstrapRejectsInheritedHistoryColumnPrivilegeAfterCommittedRevokes|FullyQualifiedName~PreOpenedInheritedHistoryColumnGrantCannotMakePostGateForgeryTrusted|FullyQualifiedName~AlternateGrantorPublicColumnAclFailsClosedAcrossCorrectiveSurfaces|FullyQualifiedName~AlternateGrantorGrantOptionFailsClosedAcrossCorrectiveSurfaces"

# Inherited ACL, PostgreSQL 18 conenforced, and complete semantic variants
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PreHistoryGateAllowsExactEmptyEfBootstrapState|FullyQualifiedName~EmptyEfBootstrapCommitsHistoryRevocationAgainstPreOpenedAttacker|FullyQualifiedName~EmptyEfBootstrapRejectsInheritedHistoryColumnPrivilegeAfterCommittedRevokes|FullyQualifiedName~PreOpenedInheritedHistoryColumnGrantCannotMakePostGateForgeryTrusted|FullyQualifiedName~EffectiveColumnPrivilegeAttestationRejectsInheritedDeniedPrivilege|FullyQualifiedName~InheritedHistoryColumnPrivilegeFailsClosedAcrossCorrectiveSurfaces|FullyQualifiedName~EffectivePrivilegeAttestationRejectsInheritedPostgres18TablePrivilege|FullyQualifiedName~InheritedMaintainPrivilegeFailsClosedAcrossCorrectiveSurfaces|FullyQualifiedName~SemanticVariant|FullyQualifiedName~PostgresLeastPrivilegeCatalogTests"

dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~PostgresMigrationAndSchemaTests|FullyQualifiedName~PostgresLeastPrivilegeCatalogTests|FullyQualifiedName~PostgresMigrationArtifactManifestTests"
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter "Category=Persistence"
dotnet test .\HusayniaTabruk.sln --no-build

dotnet format .\HusayniaTabruk.sln --verify-no-changes --no-restore
dotnet ef migrations has-pending-model-changes --project .\src\HusayniaTabruk.Infrastructure\HusayniaTabruk.Infrastructure.csproj --startup-project .\src\HusayniaTabruk.Infrastructure\HusayniaTabruk.Infrastructure.csproj --no-build

npm ci
npm run lint
npm run typecheck
npm test -- --runInBand

docker compose config
```

### Result

```text
PostgreSQL client/server: 18.6
tabruk_app: NOLOGIN, non-superuser

Build succeeded.
0 Warning(s)
0 Error(s)

Focused ACL run 1: Failed 0, Passed 24, Skipped 0, Total 24
Focused ACL run 2: Failed 0, Passed 24, Skipped 0, Total 24

Inherited ACL/conenforced/semantic run 1:
Failed 0, Passed 54, Skipped 0, Total 54
Inherited ACL/conenforced/semantic run 2:
Failed 0, Passed 54, Skipped 0, Total 54

Lifecycle/catalog/manifest:
Failed 0, Passed 115, Skipped 0, Total 115

Persistence:
Failed 0, Passed 159, Skipped 0, Total 159

Full solution:
API contract 38, Application 79, Domain 395, Integration 159
Aggregate: Failed 0, Passed 671, Skipped 0, Total 671

dotnet format: exit 0, no output
EF pending model changes: No changes have been made to the model since the last migration.

Mobile lint: exit 0
Mobile typecheck: exit 0
Test Suites: 1 passed, 1 total
Tests: 1 passed, 1 total

docker compose config: exit 0; postgres image resolved to postgres:18.6-alpine

Final psql verification:
server_version=18.6
tabruk_app=false;super=false
schemas=0
probe_roles=0
```

Passed:   671 full .NET + 1 mobile
Failed:   0
Skipped:  0

### Failures

None.

One initial full-solution invocation was externally terminated before the integration summary was
returned, leaving two disposable test schemas. The complete full-solution command was rerun and
passed 671/671; the two schemas from the interrupted process were removed, and final psql
verification proved zero residual schemas and probe roles. This was harness interruption residue,
not a test failure.

### Coverage of acceptance criteria

- Fresh PostgreSQL 18.6 and PostgreSQL 18.6 `psql` -> PASS.
- Hostile ef-bootstrap with pre-opened attacker connection -> focused ACL tests, PASS twice.
- Inherited role/table/column privileges and PostgreSQL 18 `MAINTAIN` -> 54-test suite, PASS twice.
- Alternate-grantor PUBLIC column ACLs and table/column/sequence grant options -> 24-test suite,
  PASS twice.
- Complete constructible hostile index/constraint variants across EF Up, EF Down, and owner script
  -> semantic matrix within 54-test suite, PASS twice.
- Forged history, owner recovery, schemas, lifecycle, Down/re-Up, catalog and manifest ->
  lifecycle/catalog/manifest 115/115, PASS.
- Complete PostgreSQL persistence regression -> 159/159, zero skips.
- Full .NET regression -> 671/671, zero skips.
- Immutable migration/generated hashes -> manifest test passed; direct SHA-256 values match
  `T8MigrationArtifacts.sha256`, corrective designer, and model snapshot pins.
- Build, format, EF model, mobile, and Compose configuration -> PASS.
- Flake signal -> critical 24-test and 54-test suites each passed 2/2 runs (0% observed flake rate).
- Cleanup -> zero residual `tabruk_it_*` schemas and zero probe roles.

Conclusion: PASS

## Standard status

STATUS: PASS

SUMMARY: The final independent gate reproduced the claimed 24/24, 54/54, 115/115, 159/159, and
671/671 results with zero failures and zero skips on a fresh PostgreSQL 18.6 cluster. Critical
focused suites passed twice.

WORK_COMPLETED: Fresh database provisioning, psql verification, restore/build, focused hostile ACL,
inherited privilege/conenforced/semantic, lifecycle/catalog/manifest, persistence, full solution,
format, EF model, mobile, Compose configuration, direct hash, and cleanup gates.

EVIDENCE: The TEST RESULT above.

ARTIFACTS: `.ai-org/missions/2026-08-15-husaynia-t8-hostile-drift/test-results.md`

FINDINGS: Existing npm dependency deprecation warnings and the pending `allowScripts` entry for
`unrs-resolver@1.12.2` remain. No test failure or flake was observed.

RISKS: Docker Compose configuration was validated, but a Docker daemon was not running, so no
container was started. PostgreSQL behavior was exercised against a real local PostgreSQL 18.6
server instead.

BLOCKERS: None.

NEXT_ACTION: Proceed to the remaining independent release/judgment gates.

---

# Final history-writer serialization gate

- PostgreSQL 18.6 critical race: 2/2 passed twice, zero skips.
- Migration/catalog/manifest: 130/130 passed.
- Persistence: 174/174 passed.
- Full .NET: 686/686 passed, zero skips.
- Build: 0 warnings, 0 errors.
- Format, EF model, mobile lint/typecheck/Jest, hashes, and cleanup: passed.
- Compose configuration was independently validated successfully earlier in this mission after the
  Compose file reached its unchanged final state. The final runner lacked the Docker CLI; a direct
  retry confirmed that environmental limitation. No later task modified `docker-compose.yml`.

The race regression proves an INSERT executed before hardening blocks the gate; after the attacker
commits, hardening revokes privileges and attestation rejects the preserved forged row before EF
trust. Observed critical-suite flake rate: 0 failures in two runs.
