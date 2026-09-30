# T8 Final Independent PostgreSQL Validation

## TEST RESULT

PostgreSQL:
`18.6`, UTF-8, loopback-only, fresh trust-authenticated cluster; `tabruk_app` was pre-provisioned
`NOLOGIN`.

Commands:

```powershell
dotnet build HusayniaTabruk.sln --no-restore -warnaserror
dotnet format HusayniaTabruk.sln --no-restore --verify-no-changes --verbosity minimal

$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=49248;Database=postgres;Username=postgres;Pooling=false;Timeout=5;Command Timeout=30;Include Error Detail=true'
$env:TABRUK_MIGRATIONS_CONNECTION=$env:TABRUK_TEST_POSTGRES_CONNECTION
$env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin\psql.exe'
dotnet test tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter 'Category=Persistence' --logger 'console;verbosity=minimal'
dotnet test HusayniaTabruk.sln --no-build -m:1 --logger 'console;verbosity=minimal'

npm ci
npm run lint
npm run typecheck
npm test -- --runInBand
docker compose config
```

Result:

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)

FORMAT_EXIT=0

Persistence:
Passed!  - Failed: 0, Passed: 204, Skipped: 0, Total: 204, Duration: 4 m 32 s

Full .NET:
Api.ContractTests:  Passed 38,  Failed 0, Skipped 0
Application.Tests: Passed 79,  Failed 0, Skipped 0
Domain.Tests:      Passed 395, Failed 0, Skipped 0
IntegrationTests: Passed 204, Failed 0, Skipped 0
Total:             Passed 716, Failed 0, Skipped 0

Mobile:
Test Suites: 1 passed, 1 total
Tests:       1 passed, 1 total

Compose:
image: postgres:18.6-alpine

Cleanup:
test_schemas=0
non_template_dbs=postgres
MATCHING_PROCESSES=0
PID_EXISTS=False
DATADIR_EXISTS=False
```

Passed:   921 executed tests including the 204-test persistence rerun, plus all build/format/mobile/Compose gates
Failed:   0
Skipped:  0

Failures:
None.

Coverage of acceptance criteria:

- Exact 204 persistence / 716 full tests, zero skips -> executed results above: PASS
- Exact catalog attestation and PUBLIC revocation -> persistence catalog and hostile privilege tests: PASS
- Same-name wrong index/constraint rejection with history absent -> `CorrectiveMigrationRejectsHostileSameNameIndexBeforeHistoryAndPreservesIt`, `CorrectiveMigrationRejectsHostileSameNameConstraintBeforeHistoryAndPreservesIt`, and owner-script counterparts: PASS
- One-schema EF up/down pinning; missing/multiple/mismatched targets fail without history mutation -> `EfUpgradeSchemaMatrixRejectsAmbiguousOrUnresolvedTargetsWithoutHistoryMutation`, `EfDowngradeSchemaMatrixRejectsAmbiguousOrUnresolvedTargetsWithoutHistoryMutation`, `ApprovedOwnerScriptSchemaMatrixRejectsInvalidTargetsBeforeHistoryMutation`: PASS
- Ownership/topology locks and hostile managed descendants -> `CorrectiveSurfacesRejectHostileOwnedManagedDescendantBeforeMutation`, `ApprovedOwnerScriptRejectsHostileManagedObjectOwnerAndPreservesIt`: PASS
- Concurrent-boundary re-attestation and queued topology/canonical replacement -> `CorrectiveSurfacesSerializeConcurrentManagedTopologyBeforeTrust`, `CorrectiveSurfacesRejectQueuedHistoryTopologyAfterConcurrentPhase`, `CorrectiveSurfacesRejectQueuedCanonicalConstraintReplacementAfterConcurrentPhase`: PASS
- Correct owner script idempotency and no early history -> `ApprovedOwnerIdempotentScriptAppliesAndReappliesOnInitialOnlySchema`, missing/malformed-history and chronology-retry tests: PASS
- Fresh/up/down/re-up and upgrade/down/re-up -> `InitialMigrationAppliesAndRollsBackAnIsolatedSchema`, `InitialMigrationCanApplyDownAndApplyAgainOnTheSameFreshSchema`, `CorrectiveMigrationUpgradeDownAndReapplyPreservesInitialDataAndPrivilegePosture`: PASS
- Full gates and isolated data/process cleanup -> build, format, mobile, Compose, schema/database/process/path probes: PASS

Conclusion: PASS — T8 is ready for T9.

## Standard Status

STATUS:          PASS

SUMMARY:
The final T8 hostile-drift, PUBLIC-grant, and schema-pinning remediation passed independently on a
fresh isolated PostgreSQL 18.6 cluster. The claimed 204 persistence and 716 full .NET counts were
reproduced with zero failures and zero skips.

WORK_COMPLETED:
Executed the hostile migration matrix, complete persistence and .NET suites, warning-free build,
format, mobile, Compose, and exact process/data cleanup.

EVIDENCE:
The TEST RESULT above.

ARTIFACTS:
- `.ai-org/missions/2026-08-15-husaynia-t8-final-independent-validation/test-results.md`

FINDINGS:
- A first parallel solution-test attempt was stopped by the command runner before the integration
  project completed and left one disposable test schema. The complete suite was rerun serially and
  passed; the interrupted-run schema was explicitly removed and final cleanup was verified.
- `npm ci` reported dependency deprecation and unapproved-install-script warnings, but all configured
  mobile gates passed.

RISKS:
Dependency warnings remain a maintenance concern; no T8 functional or migration defect was found.

BLOCKERS:
None.

NEXT_ACTION:
Proceed to T9.
