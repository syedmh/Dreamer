# T14 Independent Test Gate Re-run

Date: 2026-08-17

## TEST RESULT

### Command

Secrets are redacted below; all non-secret command arguments are exact.

```powershell
$env:PGPASSWORD='<redacted>'
& 'C:\Users\syedhu\AppData\Local\PostgreSQL18Portable\pgsql\bin\psql.exe' `
  --host=127.0.0.1 --port=55432 --username=tabruk --dbname=tabruk `
  --no-password --tuples-only --no-align `
  --command "SHOW server_version; SELECT version();"

dotnet restore .\HusayniaTabruk.sln --verbosity minimal
dotnet build .\HusayniaTabruk.sln --configuration Release --no-restore -warnaserror --verbosity minimal
dotnet format .\HusayniaTabruk.sln --verify-no-changes --no-restore --verbosity minimal

$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Password=<redacted>;Pooling=false'
$env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\AppData\Local\PostgreSQL18Portable\pgsql\bin\psql.exe'
dotnet test .\tests\HusayniaTabruk.Application.Tests\HusayniaTabruk.Application.Tests.csproj `
  --configuration Release --no-build --no-restore `
  --filter "FullyQualifiedName~EligibleParticipant" --verbosity minimal

$env:TABRUK_TEST_POSTGRES_CONNECTION='<same provided connection; password redacted>'
$env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\AppData\Local\PostgreSQL18Portable\pgsql\bin\psql.exe'
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --configuration Release --no-build --no-restore `
  --filter "FullyQualifiedName~EligibleParticipant|Category=FirstSlice|Category=Concurrency" `
  --verbosity minimal

$env:TABRUK_TEST_POSTGRES_CONNECTION='<same provided connection; password redacted>'
$env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\AppData\Local\PostgreSQL18Portable\pgsql\bin\psql.exe'
dotnet test .\tests\HusayniaTabruk.Api.ContractTests\HusayniaTabruk.Api.ContractTests.csproj `
  --configuration Release --no-build --no-restore `
  --filter "FullyQualifiedName~EligibleParticipant|Category=Privacy|Category=OpenApi" `
  --verbosity minimal

$env:TABRUK_TEST_POSTGRES_CONNECTION='<same provided connection; password redacted>'
$env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\AppData\Local\PostgreSQL18Portable\pgsql\bin\psql.exe'
dotnet test .\HusayniaTabruk.sln --configuration Release --no-build --no-restore --verbosity minimal

# Failed-test reproduction
$env:TABRUK_TEST_POSTGRES_CONNECTION='<same provided connection; password redacted>'
$env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\AppData\Local\PostgreSQL18Portable\pgsql\bin\psql.exe'
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --configuration Release --no-build --no-restore `
  --filter "FullyQualifiedName~InvitationAcceptanceActivatesMembershipIssuesTokensAndReturnsMe|FullyQualifiedName~MemberCannotDiscoverDraftOrMutateNeedAndOpenSurfaces|FullyQualifiedName~AuthoritativeLockAndHiddenMessagePreventAllSubsequentForbiddenThreadWrites|FullyQualifiedName~AlternateGrantorGrantOptionFailsClosedAcrossCorrectiveSurfaces|FullyQualifiedName~CorrectiveMigrationRejectsHostileIndexSemanticVariantAndPreservesIt" `
  --verbosity minimal

# Further isolation of the repeatedly failing theory
$env:TABRUK_TEST_POSTGRES_CONNECTION='<same provided connection; password redacted>'
$env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\AppData\Local\PostgreSQL18Portable\pgsql\bin\psql.exe'
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --configuration Release --no-build --no-restore `
  --filter "FullyQualifiedName~AlternateGrantorGrantOptionFailsClosedAcrossCorrectiveSurfaces" `
  --verbosity minimal

# The command calculated a deterministic combined SHA-256 over
# docs/api/openapi.json and apps/mobile/src/core/api before and after:
npm run generate:api --prefix .\apps\mobile
npm run check:generated-client --prefix .\apps\mobile

npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
npm test --prefix .\apps\mobile -- --runInBand --testPathPattern="auth|first-slice|dates|signups"
npm test --prefix .\apps\mobile -- --runInBand
npm audit --prefix .\apps\mobile --offline --audit-level=high
npm audit --prefix .\apps\mobile --audit-level=high
dotnet list .\HusayniaTabruk.sln package --vulnerable --include-transitive

# Read-only line-ending probe over 37 T14 and rework source/test files:
# inspect file bytes for CRLF while .editorconfig requires end_of_line = lf.
```

### Result

```text
PostgreSQL:
18.6
PostgreSQL 18.6 on x86_64-windows, compiled by msvc-19.44.35228, 64-bit

Restore:
All projects are up-to-date for restore.

Build:
Build succeeded.
    0 Warning(s)
    0 Error(s)

dotnet format:
Exit code 0; no output.

Application focused:
Passed!  - Failed: 0, Passed: 6, Skipped: 0, Total: 6

PostgreSQL focused:
Passed!  - Failed: 0, Passed: 21, Skipped: 0, Total: 21

API contract focused:
Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2

Full .NET solution:
Api.ContractTests:  Passed 62,  Failed 0, Skipped 0
Application.Tests: Passed 142, Failed 0, Skipped 0
Domain.Tests:      Passed 395, Failed 0, Skipped 0
IntegrationTests: Passed 319, Failed 5, Skipped 0
Total:             Passed 918, Failed 5, Skipped 0, Total 923

Failed-test reproduction:
Failed!  - Failed: 1, Passed: 29, Skipped: 0, Total: 30

Isolated default-ACL theory:
Failed!  - Failed: 2, Passed: 18, Skipped: 0, Total: 20

Generated client:
ClientFileCountBefore=6
ClientHashBefore=77b3ea6f8a65baee6481bbb466e82332ca7010e43c813ae201a0b4d9961d08f4
Wrote src\core\api\generated\api-contract-client.ts
Generated API client is up to date.
ClientFileCountAfter=6
ClientHashAfter=77b3ea6f8a65baee6481bbb466e82332ca7010e43c813ae201a0b4d9961d08f4
ClientHashMatch=True

Lint:
eslint --config node_modules/eslint-config-expo/flat.js app src tests --max-warnings=0
Exit code 0.

Typecheck:
tsc --noEmit
Exit code 0.

Focused mobile:
Test Suites: 16 passed, 16 total
Tests:       107 passed, 107 total
Snapshots:   0 total

Full mobile:
Test Suites: 18 passed, 18 total
Tests:       114 passed, 114 total
Snapshots:   0 total

Offline npm audit:
found 0 vulnerabilities

Online npm audit:
npm warn audit request to https://registry.npmjs.org/-/npm/v1/security/advisories/bulk failed,
reason: SSL/TLS alert handshake failure, alert number 40
npm error audit endpoint returned an error
Exit code 1.

.NET vulnerable-package audit:
All eight solution projects reported no vulnerable packages from the configured sources.

Line endings:
ScannedFiles=37
CRLFFiles=31
```

Authoritative full-suite counts:

Passed:   **918 .NET + 114 mobile**
Failed:   **5 .NET**
Skipped:  **0**

Focused and reproduction runs overlap the full-suite counts and are not added to those totals.

### Failures

1. `AuthIntegrationTests.InvitationAcceptanceActivatesMembershipIssuesTokensAndReturnsMe`
   - Expected: successful test completion and isolated database cleanup.
   - Actual: `System.InvalidOperationException: Connection is not open` during
     `PostgresDefaultAclRegistry.ReleaseAsync`.
   - Location: `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresIntegrationSupport.cs:621`;
     surfaced from `AuthIntegrationTests.cs:123`.

2. `DateIntegrationTests.MemberCannotDiscoverDraftOrMutateNeedAndOpenSurfaces`
   - Expected: successful first-slice date authorization behavior and cleanup.
   - Actual: `System.InvalidOperationException: Connection is not open` during default-ACL cleanup.
   - Location: `PostgresIntegrationSupport.cs:621`; surfaced from `DateIntegrationTests.cs:306`.

3. `PostgresThreadConcurrencyTests.AuthoritativeLockAndHiddenMessagePreventAllSubsequentForbiddenThreadWrites`
   - Expected: successful concurrency assertions and cleanup.
   - Actual: `System.InvalidOperationException: Connection is not open` during default-ACL cleanup.
   - Location: `PostgresIntegrationSupport.cs:621`; surfaced from
     `PostgresThreadConcurrencyTests.cs:318`.

4. `PostgresMigrationAndSchemaTests.AlternateGrantorGrantOptionFailsClosedAcrossCorrectiveSurfaces`
   - Expected: hostile alternate-grantor privilege is rejected.
   - Actual first run: PostgreSQL `23505 duplicate key value violates unique constraint
     "pg_default_acl_role_nsp_obj_index"` while changing global default ACLs.
   - Reproduction run: a different data row failed with PostgreSQL `XX000 tuple concurrently updated`.
   - Isolated theory run: two different rows failed, one with `XX000 tuple concurrently updated` and
     one with `Connection is not open` during cleanup.
   - Location: `PostgresIntegrationSupport.cs:239`; test helper call at
     `PostgresMigrationAndSchemaTests.cs:5232-5234`.

5. `PostgresMigrationAndSchemaTests.CorrectiveMigrationRejectsHostileIndexSemanticVariantAndPreservesIt`
   - Expected: hostile index variant is rejected while preserved.
   - Actual: PostgreSQL `XX000 tuple concurrently updated` while changing global default ACLs.
   - Location: `PostgresIntegrationSupport.cs:239`; test at
     `PostgresMigrationAndSchemaTests.cs:1917`.

Root-cause analysis:

- `PostgresTestDatabase.CreateAsync` acquires a registry lease, but then calls
  `EnsureSafeGlobalDefaultPrivilegesAsync` outside the advisory-lock critical section
  (`PostgresIntegrationSupport.cs:136-143`).
- That method performs cluster/database-global `ALTER DEFAULT PRIVILEGES` operations
  (`PostgresIntegrationSupport.cs:201-239`) while parallel test cases can perform the same global
  catalog writes.
- The resulting races produce duplicate `pg_default_acl` keys and concurrent tuple updates.
  Cleanup then sometimes reports `Connection is not open`, masking the earlier catalog failure.
- The failure is repeatable, not cleared by rerun: full run 5/324 integration failures, selected
  rerun 1/30, isolated theory 2/20. Different theory rows fail between runs, demonstrating
  nondeterministic fixture contention.
- Failed cleanup left two schemas on the supplied server:
  `tabruk_it_00e691f35a2d42bab44d6df7e43c37a7` and
  `tabruk_it_550200f8e1934bd38c1e47a5d6cfcc1a`.

### Coverage of acceptance criteria

- T14 all three signup compositions -> `signup-composer.test.tsx` and
  `signup-composition.test.ts`: **PASS**.
- No arbitrary/non-member name input -> `submits the individual composition and exposes no
  arbitrary text input` plus PostgreSQL
  `SubmissionRejectsParticipantNamesBoundsAndOversizedContentWithoutEffects`: **PASS**.
- Adult-member eligibility, same-organization filtering, stable pagination, disabled/anonymous
  denial, and sanitized dependency failure -> 6 Application tests plus 3 real PostgreSQL
  `EligibleParticipantQueryIntegrationTests`: **PASS**.
- Bounded member count, unnamed count, total count, and controlled label -> mobile boundary tests
  plus PostgreSQL exact-boundary tests: **PASS**.
- Pending rather than Approved and Pending versus Pending sync -> signup composer and PostgreSQL
  first-slice submission tests: **PASS**.
- Duplicate/concurrent submission and final-state reconciliation -> PostgreSQL
  `SameKeyConcurrentSubmissionHasOneEffectAndRetryShowsFinalState`,
  `DifferentKeyConcurrentDuplicateCreatesOneSignupAndMineShowsFinalState`, and mobile sync tests:
  **PASS**.
- Closed-during-submit and stale write rollback -> PostgreSQL
  `SubmissionSaveRejectsPartialSetStaleWriterAndCloseWithAtomicRollback`: **PASS**.
- Personal signup/roster consistency, privacy-safe rendering, and no contact data -> roster,
  My Signups, API privacy, and PostgreSQL privacy tests: **PASS**.
- Empty states -> dates, My Signups, and pending-roster component tests: **PASS**.
- Non-color status labels and enlarged-text behavior -> first-slice accessibility tests: **PASS**.
- Five rework regressions:
  - `does not bypass an unexpired Retry-After on manual retry`
  - `aborts and suppresses a signup completion after route parameters change`
  - `purges the stored session even when the logout endpoint fails`
  - `attempts remote revocation when local session deletion fails`
  - `waits for bounded remote logout before reporting durable sign out`
  All executed in the 107-test focused run and the 114-test full mobile run: **PASS**.
- Client generation/drift, lint, typecheck, focused mobile, and full mobile with zero skips:
  **PASS**.
- Real PostgreSQL 18.6 focused first-slice/concurrency execution with zero skips: **PASS**.
- Full .NET/PostgreSQL solution with zero failures/skips: **FAIL** — 5 failures.
- Repository LF contract -> **FAIL/GAP**. `.editorconfig` requires LF, but a direct byte probe found
  CRLF in 31 of 37 sampled T14/rework files. `dotnet format` passed because it does not validate the
  mobile TypeScript/TSX scope, and the current ESLint command has no line-ending rule.
- Online npm advisory lookup -> environmental residual allowed by the requested gate: offline audit
  reports 0 vulnerabilities, .NET audit reports none, T14 did not own dependency manifests, and the
  online endpoint consistently fails during TLS negotiation.

### Conclusion: FAIL

The focused T14 behavior is green, including the five rework regressions, but the mandatory full
.NET/PostgreSQL gate is repeatably red and leaves database residue. The repository LF contract is
also not satisfied across the sampled T14 mobile scope. PASS exit criteria are therefore not met.

---

STATUS:          FAIL
SUMMARY:         Focused T14 Application, PostgreSQL, API, client, lint, typecheck, and mobile gates pass with zero skips; the full .NET solution fails 5 PostgreSQL integration cases due repeatable global default-ACL fixture races, and 31/37 sampled T14 files still contain CRLF.
WORK_COMPLETED:  Read the mission DoD, architecture, task plan, superseded test results, current implementation and tests; verified PostgreSQL 18.6; ran every requested gate; reproduced and isolated the .NET failures; checked generated-client hashes, dependency audits, and T14 line endings.
EVIDENCE:        The TEST RESULT above. Full .NET: 918 passed, 5 failed, 0 skipped. Full mobile: 114 passed, 0 failed, 0 skipped. Focused PostgreSQL: 21 passed, 0 failed, 0 skipped.
ARTIFACTS:       `.ai-org/missions/2026-08-14-husaynia-tabarruk-signup/test-results.md`; no code or test files were edited.
FINDINGS:        The five requested mobile regressions pass. The PostgreSQL fixture has a repeatable global-default-ACL concurrency defect. Two `tabruk_it_*` schemas remain after failed cleanup. Thirty-one of 37 sampled T14/rework files contain CRLF despite the LF editor contract. Online npm audit is blocked by the known TLS handshake failure; offline npm and .NET audits pass.
RISKS:           Full-suite results are nondeterministic while global default ACLs are mutated outside one lifetime-wide lock. The pre-existing portable server now contains test residue. Offline npm audit cannot prove access to advisories absent from the local cache.
BLOCKERS:        Mandatory full .NET/PostgreSQL zero-failure gate not met; LF repository contract not met across T14 mobile files.
NEXT_ACTION:     Developer must serialize/correct global default-ACL setup, hostile mutation, restoration, and cleanup for the full test-database lifetime without weakening filters; preserve the original exception when cleanup also fails; clean the two residual test schemas through the approved fixture/DB-owner procedure; normalize T14 mobile files to LF and add an executable LF check; then rerun this exact gate on the same PostgreSQL 18.6 server.
