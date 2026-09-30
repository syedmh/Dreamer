# T04 Independent Test Evidence

Date: 2026-08-21

## Environment

- SDK/build target: .NET 10, Release.
- The four limiter environment variables were explicitly removed before every evidentiary build and
  test command:
  `Identity__AnonymousRateLimit__PermitLimit`,
  `Identity__AnonymousRateLimit__Window`,
  `Identity__AnonymousRateLimit__Retention`, and
  `Identity__AnonymousRateLimit__FingerprintKey`.
- Exact PowerShell prefix used in the same process before each command:
  `$names = @('Identity__AnonymousRateLimit__PermitLimit','Identity__AnonymousRateLimit__Window','Identity__AnonymousRateLimit__Retention','Identity__AnonymousRateLimit__FingerprintKey'); foreach ($name in $names) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue };`
- `IdentityWebApplicationFactory` supplies deterministic in-memory limiter options and a test-only
  fingerprint key. The 100-test Identity suite therefore passed without limiter environment input.
- A strict `--no-restore` solution build initially surfaced cached `NU1900` vulnerability-service
  errors. To isolate that known environmental condition, locked assets were regenerated with:
  `dotnet restore HusayniaSite.sln --locked-mode --ignore-failed-sources --force-evaluate --nologo -p:NuGetAudit=false`.
  All final evidentiary builds/tests then used `--no-restore`.

## TEST RESULT

### Strict owned builds

Command:

`dotnet build src/Husaynia.Application/Husaynia.Application.csproj -c Release --no-restore --nologo -warnaserror`

`dotnet build src/Husaynia.Infrastructure/Husaynia.Infrastructure.csproj -c Release --no-restore --nologo -warnaserror`

`dotnet build src/Husaynia.Web/Husaynia.Web.csproj -c Release --no-restore --nologo -warnaserror`

`dotnet build tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj -c Release --no-restore --nologo -warnaserror`

Result:

Each build reported:

`Build succeeded.`

`0 Warning(s)`

`0 Error(s)`

Passed: 4
Failed: 0
Skipped: 0

### Strict solution build

Command:

`dotnet build HusayniaSite.sln -c Release --no-restore --nologo -warnaserror`

Result before isolated asset regeneration:

`Build FAILED.` with seven `NU1900: Warning As Error` failures because
`https://api.nuget.org/v3/index.json` was unavailable.

Isolation command:

`dotnet restore HusayniaSite.sln --locked-mode --ignore-failed-sources --force-evaluate --nologo -p:NuGetAudit=false`

Final result from the same strict build command:

`Build succeeded.`

`0 Warning(s)`

`0 Error(s)`

Passed: 1
Failed: 0
Skipped: 0

Classification: NU1900 is an environmental audit lookup condition, not a compiler failure.

### Focused Application Identity suite

Command:

`dotnet test tests/Husaynia.Application.Tests/Husaynia.Application.Tests.csproj -c Release --no-build --no-restore --nologo --filter FullyQualifiedName~Identity --logger "console;verbosity=minimal"`

Result:

`Passed! - Failed: 0, Passed: 25, Skipped: 0, Total: 25`

Passed: 25
Failed: 0
Skipped: 0

### Complete Integration Identity suite

Command:

`dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj -c Release --no-build --no-restore --nologo --filter FullyQualifiedName~Identity --logger "console;verbosity=minimal"`

Result:

`Passed! - Failed: 0, Passed: 100, Skipped: 0, Total: 100, Duration: 1 m 32 s`

Passed: 100
Failed: 0
Skipped: 0

### Causal audit/direct-SQL/MFA/multi-host-limiter subset, three runs

Command, executed three consecutive times:

`dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj -c Release --no-build --no-restore --nologo --filter "FullyQualifiedName~IdentityAuditFinalizerTests|FullyQualifiedName~IdentityPrivilegedAuditOutcomeMatrixTests|FullyQualifiedName~IdentityMfaAuditTests|FullyQualifiedName~IdentityAnonymousRateLimitConfigurationTests|FullyQualifiedName~IdentityAnonymousRateLimiterStoreTests|FullyQualifiedName~IdentityAnonymousRateLimitingTests|FullyQualifiedName~IdentityClientFingerprintTests|FullyQualifiedName~IdentityBootstrapAndConfigurationTests|FullyQualifiedName~IdentitySecurityAndAuditTests|FullyQualifiedName~IdentityAnonymousEnumerationTests|FullyQualifiedName~IdentityMfaAndLockoutTests" --logger "console;verbosity=minimal"`

Result:

- Run 1: `Failed: 0, Passed: 87, Skipped: 0, Total: 87`
- Run 2: `Failed: 0, Passed: 87, Skipped: 0, Total: 87`
- Run 3: `Failed: 0, Passed: 87, Skipped: 0, Total: 87`

Passed: 261
Failed: 0
Skipped: 0

Flakes: 0/3 runs. The prior audit cancellation-observation race did not reproduce.
`IdentityAuditFinalizerTests.TimeoutWaitsForWriterCancellationPathWithinBoundedGrace` exercises the
new bounded observation grace, and
`IdentitySecurityAndAuditTests.DeniedAuditIsBoundedIndependentlyOfRequestAborted` passed in all
three loaded subset runs.

### Complete solution tests

Command:

`dotnet test HusayniaSite.sln -c Release --no-build --no-restore --nologo --logger "console;verbosity=minimal"`

Result:

- Domain: `Failed: 0, Passed: 1, Skipped: 0, Total: 1`
- SystemValidation: `Failed: 0, Passed: 1, Skipped: 0, Total: 1`
- E2E: `Failed: 0, Passed: 1, Skipped: 0, Total: 1`
- Contract: `Failed: 0, Passed: 21, Skipped: 0, Total: 21`
- Architecture: `Failed: 0, Passed: 10, Skipped: 0, Total: 10`
- Application: `Failed: 0, Passed: 147, Skipped: 0, Total: 147`
- Integration: `Failed: 0, Passed: 287, Skipped: 0, Total: 287`

Passed: 468
Failed: 0
Skipped: 0

### Operations DurableJobStore reproducibility probe

Command, executed three consecutive times:

`dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj -c Release --no-build --no-restore --nologo --filter FullyQualifiedName~DurableJobStoreTests --logger "console;verbosity=minimal"`

Result:

- Run 1: `Failed: 0, Passed: 17, Skipped: 0, Total: 17`
- Run 2: `Failed: 0, Passed: 17, Skipped: 0, Total: 17`
- Run 3: `Failed: 0, Passed: 17, Skipped: 0, Total: 17`

Passed: 51
Failed: 0
Skipped: 0

The previously reported three Operations failures were not reproducible in the current full
solution run or in three focused repetitions. They are outside the Identity-owned implementation
and causal subset. They do not block this execution because the required full solution suite is
green; if they recur in a required full run, AC-22 and the mission gate would be blocked regardless
of ownership.

## Artifact and source checks

- Mechanical source/handoff comparison:
  `InstallSql MATCH sourceLength=2032 docLength=2032`;
  `DownSql MATCH sourceLength=214 docLength=214`.
- The source, architecture, decision, and handoff all use the approved singleton predicate:
  `IF EXISTS (SELECT 1 FROM deleted WHERE [Id] = 1)`.
- Production source contains no execution reference to
  `IdentityAuditDatabaseInvariant.InstallSql` or `.DownSql`.
- Production source contains no forwarded-header middleware/configuration.
- No migration directory or model snapshot was found.

## Failures

None in the final required builds or executed tests.

## Coverage of acceptance criteria

See `acceptance-evidence-matrix.md`. AC-01 through AC-22 are covered and pass.

## Conclusion

Conclusion: **PASS**

---

STATUS: PASS

SUMMARY: T04 rework passes strict owned/solution builds, 25 Application Identity tests, 100
Integration Identity tests, the 87-test causal subset three consecutive times, and all 468 solution
tests with zero failures or skips.

WORK_COMPLETED: Independently executed all exit-criterion builds/tests; cleared limiter environment
variables; rechecked SQL/handoff equality and source constraints; probed Operations
`DurableJobStoreTests` three times.

EVIDENCE: The TEST RESULT above.

ARTIFACTS: `.ai-org/missions/2026-08-21-t04-final-hardening/test-evidence.md`;
`.ai-org/missions/2026-08-21-t04-final-hardening/acceptance-evidence-matrix.md`.

FINDINGS: Prior audit timeout race reproduced 0/3 after rework. Prior Operations failures reproduced
0/1 full runs and 0/3 focused runs.

RISKS: NU1900 remains an external vulnerability-service availability issue; audit was disabled only
for locked asset regeneration and is not remediated by T04.

BLOCKERS: None.

NEXT_ACTION: Advance T04 to the remaining independent gates.
