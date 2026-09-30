# T9 bounded limiter independent test gate

## TEST RESULT

Command:

```powershell
dotnet test .\tests\HusayniaTabruk.Api.ContractTests\HusayniaTabruk.Api.ContractTests.csproj --filter "FullyQualifiedName~ApiRateLimitStoreTests" --logger "console;verbosity=minimal"

$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=57985;Database=postgres;Username=postgres;Pooling=false'
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --filter "FullyQualifiedName~HusayniaTabruk.IntegrationTests.Auth.AuthIntegrationTests" --logger "console;verbosity=minimal"

# ConcurrentInvitationAcceptanceAllowsOneWinnerAndRollsBackTheLoser, repeated 20 times.

$env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin\psql.exe'
dotnet test .\HusayniaTabruk.sln --no-build -m:1 --logger "console;verbosity=minimal"

dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror
dotnet format .\HusayniaTabruk.sln --no-restore --verify-no-changes --verbosity minimal

# Portable Node 24.19.0/npm 11.17.0:
npm ci --prefix .\apps\mobile
npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
npm test --prefix .\apps\mobile -- --runInBand

docker-compose.exe -f .\docker-compose.yml config
```

Result:

```text
PostgreSQL: 18.6, UTF8, tabruk_app|false
Limiter: Passed 6, Failed 0, Skipped 0
Auth: Passed 26, Failed 0, Skipped 0
Invitation race: 20/20 passed; flake rate 0%
Full .NET: Passed 801, Failed 0, Skipped 0
  Api.ContractTests 48
  Application.Tests 90
  Domain.Tests 395
  IntegrationTests 268
Build succeeded: 0 warnings, 0 errors
Format verification: FAILED (ENDOFLINE errors in production auth files)
Mobile: lint/typecheck passed; Jest 1 passed, 0 failed, 0 skipped
Compose v5.4.0: config passed; postgres:18.6-alpine
Cleanup: schemas 0, pg_default_acl rows 0, process/data directory absent
```

Passed: 854 executed test cases (6 limiter + 26 auth + 20 repeated race + 801 full + 1 mobile)
Failed: 1 quality gate
Skipped: 0

Failures:

- `dotnet format --verify-no-changes` expected a clean formatting gate, but returned exit 2 with
  `ENDOFLINE` errors. Examples include
  `src/HusayniaTabruk.Api/Auth/AuthClaimTypes.cs:1`,
  `src/HusayniaTabruk.Api/Auth/AuthEndpointContracts.cs:1`, and
  `src/HusayniaTabruk.Infrastructure/Identity/Services/OpaqueTokenCrypto.cs:31`.
  Root cause: committed authentication production files use CRLF while repository formatting rules
  require LF.

Coverage of acceptance criteria:

- Unique rejected keys do not increase retained bucket count -> `RejectedUniqueKeysDoNotGrowRetainedBucketCount` PASS.
- Hard cap under concurrency -> `HardCapHoldsUnderConcurrentAcquisition` PASS.
- Expiry removes retained buckets -> `ExpiredBucketsAreRemovedOpportunistically` PASS.
- Multi-partition atomicity/no bypass -> `DeniedPartitionDoesNotUpdateOtherPartitions` PASS.
- Account/IP behavior -> limiter unit tests plus real-PG login rate-limit integration tests PASS.
- Invitation race 20 repetitions -> PASS, 20/20.
- Retained count bound -> explicit `RetainedBucketCount` and `MaximumRetainedBuckets` assertions PASS.
- No raw account/invitation/refresh key exposure -> privacy contract and response/log integration tests PASS.
- Real PostgreSQL 18.6, zero skips -> PASS.
- Full .NET, portable npm, Compose -> tests/npm/Compose PASS; formatting gate FAIL.

Conclusion: FAIL

## Standard status

STATUS: FAIL

SUMMARY:
The bounded limiter, real-PostgreSQL auth suite, 20 invitation races, all 801 .NET tests, mobile
gates, and portable Compose passed with zero skips. T10 readiness fails because the mandatory
format verification gate is red.

WORK_COMPLETED:
Executed adversarial limiter tests, real PostgreSQL 18.6 authentication integration tests, repeated
the invitation race 20 times, ran the complete solution, build, format, pinned mobile, Compose, and
verified database/process cleanup.

EVIDENCE:
The TEST RESULT above.

ARTIFACTS:
- `.ai-org/missions/2026-08-16-husaynia-t9-bounded-limiter-test-gate/test-results.md`

FINDINGS:
- Formatting gate reports widespread end-of-line violations in T9 authentication production files.
- npm reports deprecated transitive packages and one unapproved install script.

RISKS:
- CI/release gates that enforce `dotnet format --verify-no-changes` will reject the candidate.

BLOCKERS:
- Production authentication files do not satisfy repository LF formatting policy.

NEXT_ACTION:
Developer must normalize the reported production files to LF without changing behavior, then rerun
`dotnet format --verify-no-changes` and the full gate.
