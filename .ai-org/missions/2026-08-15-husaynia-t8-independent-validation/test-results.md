# T8 Independent PostgreSQL Validation

## TEST RESULT

PostgreSQL:
`PostgreSQL 18.6 on x86_64-windows, compiled by msvc-19.44.35228, 64-bit`

Isolated clusters:

- `C:\Users\syedhu\AppData\Local\Temp\tabruk-t8-independent-43660-20260815044945`,
  port `52410`, PID `13368`
- `C:\Users\syedhu\AppData\Local\Temp\tabruk-t8-claim-19384-20260815050132`,
  port `62904`, PID `38736`

Both were trust-authenticated, loopback-only test clusters with a pre-provisioned
`tabruk_app NOLOGIN` role. Before each shutdown, zero `tabruk_it_%` schemas remained. Both server
PIDs exited and both data paths were removed.

Command:

```powershell
$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=52410;Database=postgres;Username=postgres;Pooling=false;Timeout=5;Command Timeout=30'
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-restore --filter Category=Persistence --logger "console;verbosity=minimal"
```

Result:

```text
Passed!  - Failed:     0, Passed:    45, Skipped:     0, Total:    45, Duration: 48 s - HusayniaTabruk.IntegrationTests.dll (net10.0)
```

This independently reproduces the claimed 45-test persistence baseline before adding two retained
gap tests.

Command:

```powershell
$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=62904;Database=postgres;Username=postgres;Pooling=false;Timeout=5;Command Timeout=30'
dotnet test .\HusayniaTabruk.sln --no-restore --filter "FullyQualifiedName!~RealPostgresCommandTimeoutReturnsSanitizedServiceUnavailableContract&FullyQualifiedName!~ApplicationRoleCanCrudRuntimeAndOutboxButCannotReadHistoryOrMutateAudit" --logger "console;verbosity=minimal"
```

Result:

```text
Api.ContractTests:  Passed 38, Failed 0, Skipped 0
Application.Tests: Passed 79, Failed 0, Skipped 0
Domain.Tests:      Passed 395, Failed 0, Skipped 0
IntegrationTests: Passed 45, Failed 0, Skipped 0
Total:             Passed 557, Failed 0, Skipped 0
```

This independently reproduces the claimed 557-test full baseline.

Command:

```powershell
$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=52410;Database=postgres;Username=postgres;Pooling=false;Timeout=5;Command Timeout=30'
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-restore --filter Category=Persistence --logger "console;verbosity=minimal"
dotnet test .\HusayniaTabruk.sln --no-restore --logger "console;verbosity=minimal"
```

Result:

```text
Persistence:
Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 47 s

Full .NET:
Api.ContractTests:  Passed 38, Failed 0, Skipped 0
Application.Tests: Passed 79, Failed 0, Skipped 0
Domain.Tests:      Passed 395, Failed 0, Skipped 0
IntegrationTests: Passed 47, Failed 0, Skipped 0
Total:             Passed 559, Failed 0, Skipped 0
```

Command:

```powershell
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror
dotnet format .\HusayniaTabruk.sln --no-restore --verify-no-changes --verbosity minimal
```

Result:

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)

dotnet format exit code: 0
```

Command:

```powershell
npm ci --prefix .\apps\mobile
npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
npm test --prefix .\apps\mobile -- --runInBand
```

Result:

```text
npm ci: added 1028 packages
lint: exit code 0
typecheck: exit code 0
Test Suites: 1 passed, 1 total
Tests:       1 passed, 1 total
Snapshots:   0 total
```

Passed:   560
Failed:   0
Skipped:  0

Failures:
None. During independent test authoring, the first timeout-test compile failed because the test was
missing `Microsoft.AspNetCore.Http`; the test-only import was added and the test then executed
successfully. No production code was changed.

Coverage of acceptance criteria:

- Real isolated PostgreSQL 18.6 and safe PID/path cleanup -> version query,
  `PersistenceEnvironmentIsPostgres186WithNoLoginApplicationRole`, and both cleanup probes — PASS
- Fresh up/down/up -> `InitialMigrationAppliesAndRollsBackAnIsolatedSchema`,
  `InitialMigrationCanApplyDownAndApplyAgainOnTheSameFreshSchema` — PASS
- Initial-to-corrective upgrade/down/re-up -> 
  `CorrectiveMigrationUpgradeDownAndReapplyPreservesInitialDataAndPrivilegePosture` — PASS
- Concurrent governance coherent snapshot -> 
  `GovernanceGetAsyncUsesOneSnapshotWhenConcurrentAuthorityChangeCommitsAfterRootRead`,
  `GovernanceGetAsyncRetriesToCoherentVersionWhenAmbientUnitOfWorkIsReadCommitted` — PASS
- Recipient IDOR denial -> `NotificationReadRequiresTheExactRecipientWithinTheOrganization` — PASS
- Transient connection/timeout to sanitized 503 -> 
  `UnavailablePostgresEndpointFailsClosedInsteadOfUsingAnotherProvider`,
  `RealPostgresCommandTimeoutReturnsSanitizedServiceUnavailableContract`,
  `DependencyUnavailableExceptionReturnsSanitizedServiceUnavailableAndDedicatedLog` — PASS
- Duplicate waitlist and backdated transition constraints -> 
  `CorrectiveSignupConstraintsEnforceWaitlistUniquenessAndTransitionChronology`,
  both corrective dirty-upgrade rejection tests — PASS
- Application-role migration-history denial and normal CRUD/outbox/audit permissions ->
  `ApplicationRoleCanCrudRuntimeAndOutboxButCannotReadHistoryOrMutateAudit`,
  `SchemaEnforcesOperationalConstraintsIndexesAndAuditPermissions` — PASS
- Claimed 45 persistence / 557 full tests -> reproduced exactly with zero skips — PASS
- Full regression/build/format/mobile -> 559 .NET tests plus one mobile test, warning-free build,
  format clean, lint/typecheck clean — PASS

Conclusion: PASS

## Standard Status

STATUS:          PASS

SUMMARY:
All T8 remediation findings passed against real isolated PostgreSQL 18.6. The claimed 45/557
baseline was reproduced exactly. Two retained independent tests raised the final gate to 47
persistence tests and 559 full .NET tests; mobile adds one passing test.

WORK_COMPLETED:
Started and safely removed two isolated PostgreSQL clusters; executed fresh and corrective migration
cycles, concurrency/IDOR/503/constraint/role probes, full regression, build, format, and mobile gates.

EVIDENCE:
The TEST RESULT above.

ARTIFACTS:
- `HusayniaTabruk/tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresIndependentGateTests.cs`
- `.ai-org/missions/2026-08-15-husaynia-t8-independent-validation/test-results.md`

FINDINGS:
- No product defects or skipped persistence tests.
- `npm ci` emitted deprecation notices and one `allow-scripts` review notice; these did not fail the
  configured mobile gates.

RISKS:
Dependency deprecation/install-script warnings should be reviewed during dependency maintenance.

BLOCKERS:
None.

NEXT_ACTION:
T8 is ready for T9.
