# T10 cleanup ordering independent test gate

## TEST RESULT

Command:

```powershell
# Fresh PostgreSQL 18.6 cluster, with TABRUK_TEST_POSTGRES_CONNECTION and
# TABRUK_TEST_PSQL_PATH set to the portable cluster:
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --filter "FullyQualifiedName~LifecycleCleanupDeletesTheInclusiveCutoffAndRetainsTheNextInstant|FullyQualifiedName~LifecycleCleanupDeletesExactlyTheHundredOldestEligibleRowsPerInvocation|FullyQualifiedName~ConcurrentLifecycleCleanupAcrossRequestContextsIsSafe" `
  --logger "console;verbosity=minimal"

# Exact concurrent lifecycle test in a PowerShell loop, 20 repetitions:
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --no-restore `
  --filter "FullyQualifiedName=HusayniaTabruk.IntegrationTests.Auth.AuthIntegrationTests.ConcurrentLifecycleCleanupAcrossRequestContextsIsSafe" `
  --logger "console;verbosity=quiet"

dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --no-restore --filter "Category=Auth" --logger "console;verbosity=minimal"

# A second fresh PostgreSQL 18.6 cluster was used for the full solution:
dotnet restore .\HusayniaTabruk.sln
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror
dotnet test .\HusayniaTabruk.sln --no-build -m:1 --logger "console;verbosity=minimal"
dotnet format .\HusayniaTabruk.sln --no-restore --verify-no-changes --verbosity minimal

# PATH prefixed with portable Node 24.19.0/npm 11.17.0:
npm ci --prefix .\apps\mobile
npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
npm test --prefix .\apps\mobile -- --runInBand

# Portable Docker 29.7.2 / Compose v5.4.0:
docker compose -f .\docker-compose.yml config
```

Result:

```text
PostgreSQL: 18.6, UTF8, tabruk_app|false
Lifecycle tests: Passed 3, Failed 0, Skipped 0
Concurrent lifecycle repetition: Passed 20/20, Failed 0, flake rate 0%
Category=Auth: Passed 33, Failed 0, Skipped 0

Full .NET:
  Api.ContractTests:  Passed 50,  Failed 0, Skipped 0
  Application.Tests: Passed 90,  Failed 0, Skipped 0
  Domain.Tests:      Passed 395, Failed 0, Skipped 0
  IntegrationTests: Passed 275, Failed 0, Skipped 0
  Total:             Passed 810, Failed 0, Skipped 0

Build: 0 warnings, 0 errors
Format verification: exit 0
Mobile: lint/typecheck passed; Jest 1 passed, 0 failed, 0 skipped
Compose: config passed; postgres:18.6-alpine
Cleanup: registry absent, T10 test PostgreSQL processes 0, T10 data directories 0
```

Passed:   867 executed test cases, including deliberate subset/repetition runs
Failed:   0
Skipped:  0

Failures:

None.

Coverage of acceptance criteria:

- Fresh PostgreSQL 18.6 -> PASS; server reported PostgreSQL 18.6 and UTF8.
- Three lifecycle tests -> PASS, 3/3.
- Concurrent lifecycle test for 20 repetitions -> PASS, 20/20, 0% flake rate.
- Category=Auth -> PASS, 33/33.
- SQL uses lifecycle timestamp oldest-first with name tie-breaker -> PASS; source inspection and
  `LifecycleCleanupDeletesExactlyTheHundredOldestEligibleRowsPerInvocation`.
- Exactly 100 oldest eligible rows are deleted on the first invocation -> PASS; the test seeds 101
  families and asserts only age-100 family/consumed rows remain.
- Second invocation deletes the remaining eligible rows -> PASS; the same test invokes login twice
  and asserts no marked eligible row remains.
- Inclusive cutoff and next instant -> PASS,
  `LifecycleCleanupDeletesTheInclusiveCutoffAndRetainsTheNextInstant`.
- Concurrent cleanup uses `FOR UPDATE SKIP LOCKED`, completes without deadlock, and preserves active
  family, reuse-history, and active step-up rows -> PASS,
  `ConcurrentLifecycleCleanupAcrossRequestContextsIsSafe`, 20/20.
- Full solution has zero skips -> PASS, 810 passed, 0 skipped.
- Build, format, npm ci, mobile lint/typecheck/test, portable Compose -> PASS.
- Final portable PostgreSQL cleanup -> PASS.

Conclusion: PASS

## Standard status

STATUS:          PASS

SUMMARY:
The T9 cleanup ordering fix and all requested T10 gates passed independently on fresh PostgreSQL
18.6. Oldest-first ordering, the exact 100-row boundary, second invocation, inclusive cutoff,
concurrent safety, active-row retention, and deadlock freedom are covered by executed tests.

WORK_COMPLETED:
Executed all requested database, solution, mobile, format, build, and Compose gates and verified
portable PostgreSQL cleanup.

EVIDENCE:
The TEST RESULT above.

ARTIFACTS:
- `.ai-org/missions/2026-08-16-husaynia-t10-cleanup-ordering-test-gate/test-results.md`

FINDINGS:
- Concurrent lifecycle test flake rate was 0% across 20 repetitions.
- npm reports deprecated transitive packages and one unapproved install script
  (`unrs-resolver@1.12.2`).

RISKS:
- Dependency deprecation/install-script warnings remain a supply-chain maintenance concern but did
  not fail the requested gates.

BLOCKERS:
None.

NEXT_ACTION:
T10 is ready for the next quality gate.
