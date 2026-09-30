# T9 token lifecycle independent test gate

## TEST RESULT

Command:

```powershell
# Fresh PostgreSQL 18.6 clusters were provisioned with Postgres18TestCluster.ps1.
# Each cluster was given the required DBA-owned role:
psql -h 127.0.0.1 -p <port> -U postgres -d postgres -c "CREATE ROLE tabruk_app NOLOGIN;"

dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --filter "FullyQualifiedName~HusayniaTabruk.IntegrationTests.Auth.AuthIntegrationTests" `
  --logger "console;verbosity=minimal"

dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --no-build `
  --filter "FullyQualifiedName~RefreshRateLimitFollowsTheFamilyAcrossRotationsAndKeepsFamiliesIndependent|FullyQualifiedName~LoginPurgesOnlyRefreshLifecycleRowsPastRetention|FullyQualifiedName~RefreshPurgesExpiredFamilyAndConsumedHistoryTransactionally|FullyQualifiedName~StepUpIssuanceAndVerificationPurgeOnlyAuditRetentionExpiredGrants" `
  --logger "console;verbosity=minimal"

dotnet test .\tests\HusayniaTabruk.Api.ContractTests\HusayniaTabruk.Api.ContractTests.csproj `
  --no-build --filter "FullyQualifiedName~AuthRateLimitKeyPrivacyTests" `
  --logger "console;verbosity=minimal"

# Exact T8M method, invoked in a PowerShell loop 60 times:
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --no-restore `
  --filter "FullyQualifiedName=HusayniaTabruk.IntegrationTests.Auth.AuthIntegrationTests.ConcurrentInvitationAcceptanceAllowsOneWinnerAndRollsBackTheLoser" `
  --logger "console;verbosity=quiet"

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
AuthIntegration: Passed 30, Failed 0, Skipped 0
Exact lifecycle quartet: Passed 4, Failed 0, Skipped 0
Rate-key privacy: Passed 6, Failed 0, Skipped 0
T8M repetition: Passed 60/60, Failed 0, flake rate 0%

Full .NET:
  Api.ContractTests:  Passed 50,  Failed 0, Skipped 0
  Application.Tests: Passed 90,  Failed 0, Skipped 0
  Domain.Tests:      Passed 395, Failed 0, Skipped 0
  IntegrationTests: Passed 272, Failed 0, Skipped 0
  Total:             Passed 807, Failed 0, Skipped 0

Build: 0 warnings, 0 errors
Format verification: exit 0
Mobile: lint/typecheck passed; Jest 1 passed, 0 failed, 0 skipped
Compose: config passed; postgres:18.6-alpine
Cleanup: registry absent, test PostgreSQL processes 0, test data directories 0
```

Passed:   908 executed test cases, including deliberate subset/repetition runs
Failed:   0
Skipped:  0

Failures:

None in executed tests. Readiness nevertheless fails because explicit acceptance criteria lack
tests that prove them.

Coverage of acceptance criteria:

- Chained rotations remain one stable family key and throttle after the quota -> PASS,
  `RefreshRateLimitFollowsTheFamilyAcrossRotationsAndKeepsFamiliesIndependent`.
- Independent refresh families remain independent -> PASS, same test.
- Malformed refresh tokens use a bounded address fallback -> PASS,
  `MalformedRefreshTokensShareTheAddressFallbackInsteadOfCreatingUnboundedBuckets`.
- Cleanup runs on login, refresh, step-up issue, and step-up consume -> PASS, lifecycle quartet.
- Rows immediately older/newer than the 30-day cutoff -> PASS, lifecycle cleanup tests.
- Exact equality at the cleanup cutoff -> GAP; no test inserts a row exactly at the cutoff.
- Cleanup batch boundary at 100 and overflow at 101+ rows -> GAP; lifecycle tests insert only a
  small number of rows.
- Reuse-detection consumed records are retained before their cutoff -> PASS,
  `LoginPurgesOnlyRefreshLifecycleRowsPastRetention`.
- Active refresh families and step-up grants are not deleted -> PASS, retained-row assertions.
- Concurrent cleanup is safe and bounded -> GAP; no lifecycle cleanup concurrency test exists.
- T8M deadlock regression -> PASS, 60/60, 0% flake rate.
- Full build/tests/format/mobile/npm/Compose -> PASS.
- Fresh PostgreSQL and final process/data cleanup -> PASS.

Conclusion: FAIL

## Standard status

STATUS:          FAIL

SUMMARY:
All executed product and quality gates are green, including PostgreSQL 18.6 AuthIntegration,
the exact lifecycle quartet, rate-key contracts, 60 T8M repetitions, 807 full .NET tests, format,
mobile, and Compose. T10 readiness is not proven because exact-cutoff, 100/101 batch-boundary, and
concurrent-cleanup acceptance criteria have no tests.

WORK_COMPLETED:
Executed all requested suites and gates on fresh PostgreSQL 18.6 clusters and verified cleanup.

EVIDENCE:
The TEST RESULT above.

ARTIFACTS:
- `.ai-org/missions/2026-08-16-husaynia-t9-token-lifecycle-test-gate/test-results.md`

FINDINGS:
- Exact cleanup cutoff equality is untested.
- Cleanup batch limit and overflow behavior are untested.
- Concurrent lifecycle cleanup is untested.
- npm reports deprecated transitive packages and one unapproved install script.

RISKS:
Off-by-one retention defects, incomplete 100-row batching, and cleanup races could ship despite the
green suite.

BLOCKERS:
Missing executed tests for three explicit lifecycle acceptance criteria.

NEXT_ACTION:
Developer must add real-PostgreSQL tests for exact cutoff equality, 100/101+ batching across repeated
cleanup triggers, and concurrent cleanup preserving active/reuse-detection rows; then rerun this gate.
