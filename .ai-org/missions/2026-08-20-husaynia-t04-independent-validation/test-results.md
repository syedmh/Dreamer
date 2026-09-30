# T04 Identity/Audit Independent Test Gate

Date: 2026-08-21  
Verdict: **APPROVED**

## TEST RESULT

### Commands

```text
dotnet build .\tests\Husaynia.Application.Tests\Husaynia.Application.Tests.csproj --no-restore --configuration Release -warnaserror --nologo
dotnet build .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj --no-restore --configuration Release -warnaserror --nologo
dotnet test .\tests\Husaynia.Application.Tests\Husaynia.Application.Tests.csproj --no-build --configuration Release --filter "FullyQualifiedName~Identity" --logger "console;verbosity=minimal"
dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj --no-build --configuration Release --filter "FullyQualifiedName~Identity" --logger "console;verbosity=minimal"
$filter='FullyQualifiedName~ConcurrentCreateRoleAndDisableRequestsDoNotDuplicateOrLoseAccountState|FullyQualifiedName~AntiforgeryDeniedPrivilegedMutationIsAudited|FullyQualifiedName~DatabaseRejectsAuditUpdateAndDeleteOutsideTheEfChangeTracker|FullyQualifiedName~MfaSetupRejectsGetAndMissingAntiforgeryWithoutResettingKey'; 1..3 | ForEach-Object { dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj --no-build --configuration Release --filter $filter --logger "console;verbosity=minimal" }
dotnet build .\HusayniaSite.sln --no-restore --configuration Release -warnaserror --nologo
dotnet test .\HusayniaSite.sln --no-build --configuration Release --logger "console;verbosity=minimal"
```

### Result

```text
T04 Application strict build:
Build succeeded. 0 Warning(s), 0 Error(s).

T04 Integration strict build:
Build succeeded. 0 Warning(s), 0 Error(s).

T04 Application:
Passed! - Failed: 0, Passed: 15, Skipped: 0, Total: 15.

T04 Integration:
Passed! - Failed: 0, Passed: 17, Skipped: 0, Total: 17.

High-risk repeat 1:
Passed! - Failed: 0, Passed: 4, Skipped: 0, Total: 4.
High-risk repeat 2:
Passed! - Failed: 0, Passed: 4, Skipped: 0, Total: 4.
High-risk repeat 3:
Passed! - Failed: 0, Passed: 4, Skipped: 0, Total: 4.

Compiled full solution:
Passed: 273, Failed: 13, Skipped: 0, Total: 286.
All 13 failures are Operations/Retention or Operations/Jobs tests, outside T04.

Full strict build was invalidated by concurrent unrelated Operations drift:
first run: 0 warnings, 2 CS0246 errors in RetentionWorkflowTests.cs;
rerun after that file changed: 0 warnings, 3 CS0136/CA2250 errors in DurableJobProcessor.cs.
```

Focused T04 Passed: **32**  
Focused T04 Failed: **0**  
Focused T04 Skipped: **0**

Repeated high-risk executions Passed: **12**  
Repeated high-risk executions Failed: **0**

### Failures

None in T04.

Unrelated full-solution failures:

- Six Application failures in `Operations/Retention` and `Operations/Jobs`.
- Seven Integration failures in `Operations/Retention`.
- Concurrent source edits also made the full strict build non-reproducible. The first build saw
  missing helper types; the same file later contained those types, and the rerun failed in
  `src/Husaynia.Application/Operations/Jobs/DurableJobProcessor.cs`.
- These failures do not execute Identity code and are not attributable to T04.

### Coverage of acceptance criteria

- Role/capability matrix for anonymous, ordinary, and all six privileged roles:
  `CapabilityProbeEnforcesFrozenMatrixForAnonymousOrdinaryAndAllPrivilegedRoles` — PASS.
- Named policy metadata and endpoint enforcement:
  `PrivilegedEndpointsApplyTheFrozenAuthorizationPolicies`,
  `OrdinaryUserCannotReachAdministrationAndExactRoleValidationRejectsUnknownRoles`,
  capability matrix — PASS.
- MFA enrollment/challenge, POST-only setup, rejected GET/missing antiforgery, and key preservation:
  four `IdentityMfaAndLockoutTests` paths — PASS.
- Registration absent: `LockoutTriggersAfterRepeatedPasswordFailuresAndRegistrationRouteRemainsAbsent`
  — PASS.
- Password and MFA lockout: two lockout regressions — PASS.
- Antiforgery denial audit, exactly one event, bounded identifiers, correlation, and no body secrets:
  three antiforgery/denial audit tests — PASS.
- Direct SQL INSERT accepted and UPDATE/DELETE rejected:
  `DatabaseRejectsAuditUpdateAndDeleteOutsideTheEfChangeTracker` — PASS.
- Trigger installation/replay idempotency: `CREATE OR ALTER` is exercised by repeated host startup in
  `BootstrapSeedingIsEnvironmentControlledAndIdempotent`; direct SQL rejection proves it is active
  — PASS.
- Invite stress: five concurrent same-email pairs produce one success plus one typed conflict per
  pair, one account, and ten audit events; repeated three times with zero failures — PASS.
- Role/disable optimistic concurrency and no lost account state:
  workflow and concurrent mutation test — PASS.
- Security-stamp invalidation after role change:
  `BrowserMutationsRequireAntiforgeryAndRoleChangesInvalidateExistingSessions` — PASS.
- Bootstrap validation, environment gating, idempotency, and no default credential:
  `IdentityBootstrapAndConfigurationTests` — PASS.
- T18 handoff: `IdentityAuditDatabaseInvariant.InstallSql` is the single public SQL artifact.
  T18 is still PENDING and owns migrations; its future migration must consume this constant rather
  than copy the trigger SQL. No T18 migration exists yet to validate.

Conclusion: **PASS — APPROVED for T04**

STATUS: **PASS**

SUMMARY: All 32 focused T04 tests pass under strict owned builds. The four prior blockers are
closed, and 12 repeated executions of the highest-risk concurrency, antiforgery, SQL invariant,
and MFA setup regressions produced no flake.

WORK_COMPLETED: Executed strict T04 builds, focused Application and LocalDB/WebApplicationFactory
tests, repeated high-risk regressions three times, and attempted full-solution build/test.

EVIDENCE: The TEST RESULT above.

ARTIFACTS:
- `tests/Husaynia.IntegrationTests/Identity/IdentitySecurityAndAuditTests.cs`
- `tests/Husaynia.IntegrationTests/Identity/IdentityAdministrationWorkflowTests.cs`
- `.ai-org/missions/2026-08-20-husaynia-t04-independent-validation/test-results.md`

FINDINGS: No T04 finding. T18 remains responsible for consuming
`IdentityAuditDatabaseInvariant.InstallSql` in the initial migration.

RISKS: The repository is under concurrent unrelated Operations modification, so a stable full
strict build cannot currently be certified. This does not change the green T04-owned evidence.

BLOCKERS: None for T04. Unrelated Operations drift blocks a repository-wide green gate.

NEXT_ACTION: Accept T04. Preserve the SQL artifact contract for T18 and rerun the full solution
after Operations work stabilizes.
