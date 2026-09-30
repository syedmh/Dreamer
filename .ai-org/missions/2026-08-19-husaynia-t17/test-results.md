# Test gate — PASS

TEST RESULT

Command:
```powershell
# Temporary local PostgreSQL 18.6 provisioning because no running server/Docker daemon was available
$ErrorActionPreference = 'Stop'
$root = 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18'
New-Item -ItemType Directory -Path $root -Force | Out-Null
$zip = Join-Path $root 'postgresql-18.6-windows-x64-binaries.zip'
Invoke-WebRequest -Uri 'https://sbp.enterprisedb.com/getfile.jsp?fileid=1260435' -OutFile $zip
$extract = Join-Path $root 'pgsql'
if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
Expand-Archive -Path $zip -DestinationPath $extract
& 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18\pgsql\pgsql\bin\initdb.exe' -D 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18\data' -U tabruk --auth-local=trust --auth-host=trust --encoding=UTF8 --locale=C
& 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18\pgsql\pgsql\bin\pg_ctl.exe' -D 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18\data' -l 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18\postgres.log' -o '\"-p 55432\"' start -w
& 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18\pgsql\pgsql\bin\createdb.exe' -h 127.0.0.1 -p 55432 -U tabruk tabruk
& 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18\pgsql\pgsql\bin\createdb.exe' -h 127.0.0.1 -p 55432 -U tabruk tabruk_factory_probe
& 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18\pgsql\pgsql\bin\psql.exe' -h 127.0.0.1 -p 55432 -U tabruk -d postgres -v ON_ERROR_STOP=1 -c "CREATE ROLE tabruk_app NOLOGIN;"
& 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18\pgsql\pgsql\bin\psql.exe' -h 127.0.0.1 -p 55432 -U tabruk -d tabruk -v ON_ERROR_STOP=1 -c "SHOW server_version; SELECT version();"

dotnet restore .\HusayniaTabruk.sln --verbosity minimal
dotnet build .\HusayniaTabruk.sln --configuration Release --no-restore -warnaserror --verbosity minimal

dotnet test .\tests\HusayniaTabruk.Application.Tests\HusayniaTabruk.Application.Tests.csproj --configuration Release --no-build --no-restore --filter "FullyQualifiedName~T17DateManagementContractTests" --verbosity minimal

dotnet test .\tests\HusayniaTabruk.Api.ContractTests\HusayniaTabruk.Api.ContractTests.csproj --configuration Release --no-build --no-restore --filter "FullyQualifiedName~DateManagementEndpointContractTests" --verbosity minimal

$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'; $env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18\pgsql\pgsql\bin\psql.exe'; dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --configuration Release --no-build --no-restore --filter "FullyQualifiedName~DateManagementIntegrationTests" --verbosity minimal

$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'; $env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18\pgsql\pgsql\bin\psql.exe'; dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --configuration Release --no-build --no-restore --filter "FullyQualifiedName~CategoryCloseRaceReturns409AndRollsBackAtomically|FullyQualifiedName~SubmissionSaveRejectsPartialSetStaleWriterAndCloseWithAtomicRollback|FullyQualifiedName~CategoryCloseCommittedAfterLoadBeforeSaveReturnsCategoryClosedAndRollsBack" --verbosity minimal

dotnet test .\tests\HusayniaTabruk.Api.ContractTests\HusayniaTabruk.Api.ContractTests.csproj --configuration Release --no-build --no-restore --filter "FullyQualifiedName~EditDateRejectsNonUtcTimestampsWithValidationProblemDetails" --verbosity minimal

$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'; $env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18\pgsql\pgsql\bin\psql.exe'; dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --configuration Release --no-build --no-restore --filter "FullyQualifiedName~AuthorityLostBetweenInitialAuthorizationAndWriteDoesNotCommit|FullyQualifiedName~DatePatchConcurrentNeedPatchPreservesTheNeedEdit|FullyQualifiedName~NeedPatchSignupVersionCasLossReturnsPreconditionFailedWithoutNeedWriteOrEffects|FullyQualifiedName~SameIdempotencyKeyWithDifferentIfMatchDoesNotReplayCompletedMutation|FullyQualifiedName~CloseWithoutAnExistingThreadDoesNotCreateOrBackfillThread|FullyQualifiedName~CloseOrCancelStaleIfMatchRaceReturnsPreconditionFailedWithoutPartialWrites|FullyQualifiedName~CloseOrCancelConcurrentNeedPatchReturnsStaleVersionWithoutPartialWrites" --verbosity minimal

$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'; $env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18\pgsql\pgsql\bin\psql.exe'; dotnet test .\HusayniaTabruk.sln --configuration Release --no-build --no-restore --verbosity minimal

npm ci --prefix .\apps\mobile
npm run check:generated-client --prefix .\apps\mobile
npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
npm test --prefix .\apps\mobile -- --runInBand

& 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18\pgsql\pgsql\bin\psql.exe' -h 127.0.0.1 -p 55432 -U tabruk -d tabruk -v ON_ERROR_STOP=1 -c "SELECT count(*) AS residual_schemas FROM pg_namespace WHERE nspname LIKE 'tabruk_it_%';"
& 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18\pgsql\pgsql\bin\pg_ctl.exe' -D 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18\data' stop -m fast -w
if (Test-Path 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18') { Remove-Item -Recurse -Force 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\.pg18' }
```

Result:
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

Focused application contract:
Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1

Focused API contract:
Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5

Focused T17 PostgreSQL:
Passed!  - Failed:     0, Passed:    25, Skipped:     0, Total:    25

Original AC-7 PostgreSQL regressions:
Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3

Explicit non-UTC contract regression:
Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1

Rework-focused PostgreSQL regressions:
Passed!  - Failed:     0, Passed:    17, Skipped:     0, Total:    17

Full .NET solution:
Api.ContractTests:   Passed 79,  Failed 0, Skipped 0
Application.Tests:  Passed 159, Failed 0, Skipped 0
Domain.Tests:       Passed 383, Failed 0, Skipped 0
IntegrationTests:   Passed 378, Failed 0, Skipped 0
Total:              Passed 999, Failed 0, Skipped 0, Total 999

Mobile clean install:
added 1029 packages in 1m

Generated client drift:
Generated API client is up to date.

Lint:
eslint --config node_modules/eslint-config-expo/flat.js app src tests --max-warnings=0

Typecheck:
tsc --noEmit

Full mobile:
Test Suites: 18 passed, 18 total
Tests:       121 passed, 121 total
Snapshots:   0 total

Residual test schemas:
0
```

Passed:   1120 total full-regression tests (999 .NET + 121 mobile). Targeted focused runs overlap the full counts and are not added again.
Failed:   0
Skipped:  0

Failures:
None.

Coverage of acceptance criteria:
- Frozen T17 command/contract shape -> `T17DateManagementContractTests.CommandsAreIdentifierScalarOnlyAndVersionRemainsAnExplicitMethodParameter`; `DateManagementEndpointContractTests.ManagementRoutesExposeFrozenOperationsSchemasAndHeaders` PASS
- Strong `If-Match` on all four mutations; close/cancel require idempotency -> `DateManagementEndpointContractTests.ManagementRoutesExposeFrozenOperationsSchemasAndHeaders`; `StrongIfMatchAcceptsOnlyOneQuotedNonnegativeInt64`; `StrongIfMatchRejectsWeakWildcardListMalformedAndUnquotedValues` PASS
- Write-time manager revocation/disable fails closed -> `DateManagementIntegrationTests.AuthorityLostBetweenInitialAuthorizationAndWriteDoesNotCommit` (8 PostgreSQL cases) PASS
- Date-vs-need CAS preserves concurrent child edits and rejects stale child versions -> `DateManagementIntegrationTests.DatePatchConcurrentNeedPatchPreservesTheNeedEdit`; `NeedPatchSignupVersionCasLossReturnsPreconditionFailedWithoutNeedWriteOrEffects` PASS
- Same idempotency key + different `If-Match` does not replay -> `DateManagementIntegrationTests.SameIdempotencyKeyWithDifferentIfMatchDoesNotReplayCompletedMutation` (close/cancel) PASS
- Non-UTC timestamps fail with stable `400 invalid_date_request` -> `DateManagementEndpointContractTests.EditDateRejectsNonUtcTimestampsWithValidationProblemDetails` PASS
- Close preserves history and only locks an existing thread -> `DateManagementIntegrationTests.ClosePreservesApprovedSignupHistoryAndLocksExistingThread`; `CloseWithoutAnExistingThreadDoesNotCreateOrBackfillThread` PASS
- Stale/concurrent close or cancel returns no-partial-write failure -> `DateManagementIntegrationTests.CloseOrCancelStaleIfMatchRaceReturnsPreconditionFailedWithoutPartialWrites` (close/cancel); `CloseOrCancelConcurrentNeedPatchReturnsStaleVersionWithoutPartialWrites` (close/cancel) PASS
- Capacity reduction uses canonical signup-root hydration and no-write rejection -> `DateManagementIntegrationTests.NeedPatchReducingCapacityBelowApprovedParticipantsReturnsConflictWithoutWrites`; `NeedPatchSignupVersionCasLossReturnsPreconditionFailedWithoutNeedWriteOrEffects` PASS
- AC-7 category closes before request completes -> `T12ApiConsumerQaTests.CategoryCloseRaceReturns409AndRollsBackAtomically`; `SignupSubmissionIntegrationTests.SubmissionSaveRejectsPartialSetStaleWriterAndCloseWithAtomicRollback`; `SignupDecisionConcurrencyTests.CategoryCloseCommittedAfterLoadBeforeSaveReturnsCategoryClosedAndRollsBack`; `DateManagementIntegrationTests.CloseCommittedAfterSubmissionLoadStillRejectsSignupSaveWithoutWrites` PASS
- AC-15 date cancellation cancels affected signups, deduplicates per-primary effects, records updates, and revokes thread access -> `DateManagementIntegrationTests.CancelCancelsActiveSignupsCreatesDistinctContactEffectsAndRevokesApprovedThreadAccess`; `CancelDeduplicatesEffectsForTheSamePrimaryAcrossMultipleNeeds`; `CancelRetryWithSameKeyReturnsCurrentProjectionAndDoesNotDuplicateEffects`; `CancelRollbackOnNotificationOrIdempotencyUpdateFailureLeavesDateNeedSignupAndThreadUnchanged` PASS
- OpenAPI/generated client drift -> `DateManagementEndpointContractTests.ManagementRoutesExposeFrozenOperationsSchemasAndHeaders`; `npm run check:generated-client --prefix .\apps\mobile` PASS
- Full .NET regression with zero skips -> `dotnet test .\HusayniaTabruk.sln --configuration Release --no-build --no-restore --verbosity minimal` PASS
- Clean-install mobile regression with zero skips -> `npm ci --prefix .\apps\mobile`; `npm test --prefix .\apps\mobile -- --runInBand` PASS
- Other independent mission gates (security, code review, QA, judge) -> not assessed in this Test Engineer report

Conclusion: PASS

STATUS:          PASS
SUMMARY:         Independent T17 revalidation passed. Real PostgreSQL 18.6 focused regressions are green, prior close/thread and stale/concurrent coverage gaps are now executed, full .NET is 999/999 with zero skips, generated-client drift passes, and clean-install mobile Jest is 121/121.
WORK_COMPLETED:  Provisioned a temporary local PostgreSQL 18.6 instance from official Windows binaries; verified server version; restored and built the solution; ran focused T17 application/API/PostgreSQL regressions; reran original AC-7 PostgreSQL regressions; reran explicit non-UTC and rework-focused regressions; executed the full .NET solution; ran clean-install mobile generated-client, lint, typecheck, and full Jest; verified zero residual `tabruk_it_%` schemas; removed the temporary PostgreSQL files after validation.
EVIDENCE:        The TEST RESULT above.
ARTIFACTS:       Updated `.ai-org/missions/2026-08-19-husaynia-t17/test-results.md`; no production code or test files edited.
FINDINGS:        No blocking defects found in the test-owned T17 scope. Previous gaps recorded in this mission (`close with no existing thread`; `stale/concurrent close or cancel mutation`) are now covered by executed PostgreSQL regressions and passed. `npm ci` emitted non-blocking dependency deprecation/allow-scripts warnings only.
RISKS:           This PASS is the independent Test Engineer gate only; security, code-review, QA, and judge approvals remain separate mission gates.
BLOCKERS:        None for the Test Engineer gate.
NEXT_ACTION:     Proceed to the remaining independent mission gates and judgment.
