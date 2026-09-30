# T04 Final Hardening — Independent Test Gate

Verdict: **APPROVED**

## TEST RESULT

### Commands

```text
dotnet build .\HusayniaSite.sln -c Release --no-restore --nologo -warnaserror
dotnet test .\tests\Husaynia.Application.Tests\Husaynia.Application.Tests.csproj -c Release --no-build --no-restore --nologo --filter FullyQualifiedName~Identity --logger "console;verbosity=minimal"
dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj -c Release --no-build --no-restore --nologo --filter FullyQualifiedName~Identity --logger "console;verbosity=minimal"
dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj -c Release --no-build --no-restore --nologo --filter "<87-test T04 causal filter>" --logger "console;verbosity=minimal"
# The preceding causal command was executed three consecutive times.
dotnet test .\HusayniaSite.sln -c Release --no-build --no-restore --nologo --logger "console;verbosity=minimal"
```

The four `Identity__AnonymousRateLimit__*` environment variables were removed in the same
PowerShell process before every command.

### Result

```text
Strict solution build:
Build succeeded.
0 Warning(s)
0 Error(s)

Application Identity:
Passed! - Failed: 0, Passed: 25, Skipped: 0, Total: 25

Integration Identity:
Passed! - Failed: 0, Passed: 100, Skipped: 0, Total: 100

Causal repeat 1:
Passed! - Failed: 0, Passed: 87, Skipped: 0, Total: 87
Causal repeat 2:
Passed! - Failed: 0, Passed: 87, Skipped: 0, Total: 87
Causal repeat 3:
Passed! - Failed: 0, Passed: 87, Skipped: 0, Total: 87

Full solution:
Domain 1, SystemValidation 1, E2E 1, Contract 21, Architecture 10,
Application 147, Integration 287.
Passed: 468, Failed: 0, Skipped: 0.
```

Passed: **468 full-suite tests; 125 focused Identity tests; 261 repeated causal executions**  
Failed: **0**  
Skipped: **0**

### Failures

None.

### Coverage of acceptance criteria

- Direct SQL audit UPDATE/DELETE rejection:
  `DatabaseRejectsAuditUpdateAndDeleteOutsideTheEfChangeTracker` — PASS.
- Direct SQL permanent-seal UPDATE/DELETE rejection and restart revocation:
  `BootstrapSeedingIsEnvironmentControlledAndPermanentlySealed` — PASS.
- Concurrent bootstrap exactly-once:
  `ConcurrentBootstrapInstancesCreateOneAdministratorAndOneSeal` — PASS.
- Deterministic/idempotent install, down, and reinstall:
  `InvariantSqlIsExactIdempotentAndReinstallable` — PASS.
- No runtime DDL:
  `OrdinaryAndBootstrapStartupNeverExecuteOwnedDdl` and production source search — PASS.
- Cancellation-safe, bounded, exactly-once audits:
  `IdentityAuditFinalizerTests`, `IdentityPrivilegedAuditOutcomeMatrixTests`,
  `IdentityAdministrationStoreAuditFinalizationTests`, and
  `IdentitySecurityAndAuditTests` — PASS.
- Complete sanitized MFA setup/enable outcome auditing:
  `IdentityMfaAuditTests` and `IdentityMfaAndLockoutTests` — PASS.
- Shared multi-instance DB limiter, N/N+1 atomicity and no race overshoot:
  `IdentityAnonymousRateLimiterStoreTests` and
  `IdentityAnonymousRateLimitingTests` — PASS.
- Limiter partitioning, boundaries, expiry, retention, uniform 429 and no lockout mutation:
  rate-limiter, configuration, fingerprint, and enumeration tests — PASS.
- T18 handoff:
  normalized mechanical comparison reported `InstallSql exact normalized: True`
  (`2032/2032`) and `DownSql exact normalized: True` (`214/214`); no migrations or model
  snapshot exist — PASS.
- ADR-004 deny-by-default policy and append-only audit contract:
  Identity authorization/audit regression tests — PASS.

Flake rate for the 87-test causal subset: **0/3 runs**.

Conclusion: **PASS**

---

STATUS: **PASS**

SUMMARY: Final T04 hardening independently passes every exit criterion, strict build, both focused
Identity suites, three causal repetitions, and all 468 solution tests.

WORK_COMPLETED: Read ADR-004 and the T18 handoff; inspected causal tests and source constraints;
executed strict build, focused suites, repeated LocalDB/concurrency/cancellation tests, and the full
solution suite; mechanically compared SQL constants with the handoff.

EVIDENCE: The TEST RESULT above.

ARTIFACTS:
- `.ai-org/missions/2026-08-21-t04-final-independent-validation/test-results.md`

FINDINGS: None. No T04 tests required modification.

RISKS: NU1900 remains separate and was not exercised because all commands used `--no-restore`.

BLOCKERS: None.

NEXT_ACTION: Approve final T04 hardening.
