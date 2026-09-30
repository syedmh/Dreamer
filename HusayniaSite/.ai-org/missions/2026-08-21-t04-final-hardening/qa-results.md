# QA RESULT

Date: 2026-08-21

Environment: Windows local development host; .NET SDK 10.0.400 / runtime 10.0.11; Release build
artifacts; xUnit through `dotnet test`; real ASP.NET Core `WebApplicationFactory`/TestServer HTTP
pipeline; disposable SQL Server `(localdb)\MSSQLLocalDB` databases created as
`HusayniaT04_*`. The fixture ran `EnsureCreated` and then explicitly executed
`IdentityAuditDatabaseInvariant.InstallSql`. Every database was guarded by the T04 disposable-name
check and deleted after its test. No production/shared resource, real credential, or customer data
was used.

Before every command, the four limiter environment variables were removed in the same PowerShell
process:

```powershell
$names = @(
  'Identity__AnonymousRateLimit__PermitLimit',
  'Identity__AnonymousRateLimit__Window',
  'Identity__AnonymousRateLimit__Retention',
  'Identity__AnonymousRateLimit__FingerprintKey')
foreach ($name in $names) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
```

## Scenario 1: Initial bootstrap seal, direct mutation, and restart [PASS]

Steps:

```powershell
dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj `
  -c Release --no-build --no-restore --nologo `
  --filter "FullyQualifiedName~IdentityBootstrapAndConfigurationTests.BootstrapSeedingIsEnvironmentControlledAndPermanentlySealed" `
  --logger "console;verbosity=normal"
```

Expected: mismatched environment does not bootstrap; matching environment creates one confirmed
administrator, six roles, one permanent seal, and two bootstrap audits atomically. Direct SQL
UPDATE and DELETE of seal `Id=1` fail with SQL error 51005 and leave the serialized row unchanged.
After removing the administrator role and restarting a matching host, bootstrap does not recreate
or reassign anything and does not duplicate the seal or audits.

Actual: all HTTP startup probes and persisted-state assertions passed. The restarted host retained
one administrator, one seal, two bootstrap audits, and zero administrator-role links after the
deliberate role removal.

Evidence:

```text
Passed IdentityBootstrapAndConfigurationTests.BootstrapSeedingIsEnvironmentControlledAndPermanentlySealed [2 s]
Test Run Successful.
Total tests: 1
Passed: 1
```

Persisted checks executed by the scenario: roles `6`; matching administrator rows `1`; seal rows
`1`; bootstrap audits `2`; SQL UPDATE error `51005`; SQL DELETE error `51005`; pre/post seal bytes
equal; after restart user-role links `0`, seals `1`, audits `2`.

## Scenario 2: Privileged outcomes, disconnect, audit failure, and timeout recovery [PASS]

Steps:

```powershell
$filter = 'FullyQualifiedName~IdentityPrivilegedAuditOutcomeMatrixTests.PrivilegedRouteOutcomesEmitExactlyOneAuditPerActionAndCorrelation|' +
  'FullyQualifiedName~IdentityPrivilegedAuditOutcomeMatrixTests.ValidationConflictAndSuccessAuditsSurviveRequestAbort|' +
  'FullyQualifiedName~IdentityPrivilegedAuditOutcomeMatrixTests.EscapingExceptionAuditSurvivesRequestAbort|' +
  'FullyQualifiedName~IdentityPrivilegedAuditOutcomeMatrixTests.EscapingEndpointExceptionIsAuditedOnceAndReturnsSanitizedFailure|' +
  'FullyQualifiedName~IdentityPrivilegedAuditOutcomeMatrixTests.ThrowingWriterReturnsSanitized503WithoutRetryOrTransactionalSuccess|' +
  'FullyQualifiedName~IdentitySecurityAndAuditTests.DeniedAuditsSurviveClientDisconnectAndAreNotDuplicated|' +
  'FullyQualifiedName~IdentityAuditFinalizerTests.NonCompletingWriterTimesOutAfterFiveSecondsWithoutRetry|' +
  'FullyQualifiedName~IdentityAuditFinalizerTests.TimeoutWaitsForWriterCancellationPathWithinBoundedGrace'
dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj `
  -c Release --no-build --no-restore --nologo --filter $filter `
  --logger "console;verbosity=normal"
```

Expected: framework/authorization denial, malformed input, validation, conflict, exception, and
success return the appropriate observable status and persist exactly one audit for each
action/correlation. Client cancellation after outcome selection must not cancel the audit.
Throwing/non-completing writers must not retry, leak diagnostics, falsely report mutation success,
or wait without bound.

Actual: observed HTTP included `403` capability denial, `400` antiforgery/malformed/validation,
`409` conflict, and `200` successful disable. Exception and writer-failure tests verified sanitized
`500 unexpected_failure` and `503 identity_audit_unavailable`. Cancellation tests completed their
delayed audit after the HTTP client task was cancelled. Timeout tests completed in approximately
5-6 seconds and observed cancellation without a retry.

Evidence:

```text
Passed IdentityAuditFinalizerTests.TimeoutWaitsForWriterCancellationPathWithinBoundedGrace [5 s]
Passed IdentityAuditFinalizerTests.NonCompletingWriterTimesOutAfterFiveSecondsWithoutRetry [6 s]
Passed IdentitySecurityAndAuditTests.DeniedAuditsSurviveClientDisconnectAndAreNotDuplicated [5 s]
Passed IdentityPrivilegedAuditOutcomeMatrixTests.EscapingEndpointExceptionIsAuditedOnceAndReturnsSanitizedFailure
Passed IdentityPrivilegedAuditOutcomeMatrixTests.ValidationConflictAndSuccessAuditsSurviveRequestAbort
Passed IdentityPrivilegedAuditOutcomeMatrixTests.ThrowingWriterReturnsSanitized503WithoutRetryOrTransactionalSuccess
Passed IdentityPrivilegedAuditOutcomeMatrixTests.EscapingExceptionAuditSurvivesRequestAbort
Passed IdentityPrivilegedAuditOutcomeMatrixTests.PrivilegedRouteOutcomesEmitExactlyOneAuditPerActionAndCorrelation
Total tests: 8
Passed: 8
```

Persisted checks executed by the scenarios: exactly one matching audit per action/correlation;
successful disable persisted; throwing-writer disable remained `IsDisabled=false`; writer attempt
count remained `1`.

## Scenario 3: MFA setup/enable denial, invalid, lockout, conflict, and success [PASS]

Steps:

```powershell
$filter = 'FullyQualifiedName~IdentityMfaAuditTests.SetupFrameworkAndAntiforgeryDenialsAreAuditedExactlyOnce|' +
  'FullyQualifiedName~IdentityMfaAuditTests.SetupAndEnableForbiddenFrameworkDenialsAreAuditedExactlyOnce|' +
  'FullyQualifiedName~IdentityMfaAuditTests.SetupSuccessAuditIsSanitizedAndIdentifiesTheAuthenticatedUser|' +
  'FullyQualifiedName~IdentityMfaAuditTests.InvalidEnableCodesAuditEveryAttemptAndLockTheAccount|' +
  'FullyQualifiedName~IdentityMfaAuditTests.EnableDenialsAndValidationFailuresAreAuditedExactlyOnce|' +
  'FullyQualifiedName~IdentityMfaAuditTests.SetupConflictAndExistingKeySuccessPreserveKeyAndAuditState|' +
  'FullyQualifiedName~IdentityMfaAuditTests.EnableSuccessAuditsOnceResetsFailuresAndIssuesSatisfiedSessionAfterCommit|' +
  'FullyQualifiedName~IdentityMfaAndLockoutTests.EnabledMfaSetupNeverRevealsEstablishedKeyOrDowngradesSatisfiedSession'
dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj `
  -c Release --no-build --no-restore --nologo --filter $filter `
  --logger "console;verbosity=normal"
```

Expected: setup/enable framework, authorization, antiforgery, missing/malformed-code, invalid-code,
lockout, conflict, and success paths each audit once. Audit fields must not contain the shared key,
OTP, password, or email. Existing keys and MFA-satisfied sessions remain intact. Successful enable
persists MFA, resets failures, and issues an MFA-satisfied session only after commit.

Actual: observed setup `200`; invalid enable responses `400`; fifth invalid enable returned `423`;
successful enable returned `200`. All audit uniqueness/redaction assertions passed. Existing keys
were unchanged, enabled setup did not expose the key or downgrade the session, and successful
enable persisted `TwoFactorEnabled=true`, `AccessFailedCount=0`, and `Session.MfaSatisfied=true`.

Evidence:

```text
Passed IdentityMfaAndLockoutTests.EnabledMfaSetupNeverRevealsEstablishedKeyOrDowngradesSatisfiedSession
Passed IdentityMfaAuditTests.EnableDenialsAndValidationFailuresAreAuditedExactlyOnce
Passed IdentityMfaAuditTests.SetupConflictAndExistingKeySuccessPreserveKeyAndAuditState
Passed IdentityMfaAuditTests.EnableSuccessAuditsOnceResetsFailuresAndIssuesSatisfiedSessionAfterCommit
Passed IdentityMfaAuditTests.SetupAndEnableForbiddenFrameworkDenialsAreAuditedExactlyOnce
Passed IdentityMfaAuditTests.SetupFrameworkAndAntiforgeryDenialsAreAuditedExactlyOnce
Passed IdentityMfaAuditTests.SetupSuccessAuditIsSanitizedAndIdentifiesTheAuthenticatedUser
Passed IdentityMfaAuditTests.InvalidEnableCodesAuditEveryAttemptAndLockTheAccount
Total tests: 8
Passed: 8
```

## Scenario 4: Shared anonymous limiter, uniform 429, expiry, and isolation [PASS]

Steps:

```powershell
$filter = 'FullyQualifiedName~IdentityAnonymousRateLimitingTests.TwoHostsEnforceOneGlobalEndpointThresholdWithoutRaceOvershoot|' +
  'FullyQualifiedName~IdentityAnonymousRateLimitingTests.LoginAndMfaChallengeShareUniformThrottleBeforeJsonOrLockoutMutation|' +
  'FullyQualifiedName~IdentityAnonymousRateLimitingTests.InvitationHasASeparatePartitionAndThrottlePreservesInvitationState|' +
  'FullyQualifiedName~IdentityAnonymousRateLimiterStoreTests.EndpointAndFingerprintPartitionsAreIndependent|' +
  'FullyQualifiedName~IdentityAnonymousRateLimiterStoreTests.ExpiredWindowResetsAndRetentionCleanupDeletesStaleRows|' +
  'FullyQualifiedName~IdentityClientFingerprintTests.SpoofedForwardingHeadersDoNotChangeTheServerAddressFingerprint'
dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj `
  -c Release --no-build --no-restore --nologo --filter $filter `
  --logger "console;verbosity=normal"
```

Expected: two independent hosts sharing LocalDB admit exactly `N` login-family requests and reject
`N+1`; login/MFA variants receive an identical enumeration-safe 429 body and `Retry-After`;
invitation uses an independent endpoint partition and throttling does not consume its token or set
a password; endpoint/fingerprint partitions remain independent; expiry resets allowance and
retention removes stale rows; spoofed forwarding headers do not create a new partition; rejected
requests do not amplify account lockout.

Actual: the two-host burst produced four `401` admissions and one `429`, with one persisted
`login` row at `RequestCount=4` and a 32-byte non-raw fingerprint. Login/MFA variants, including
malformed JSON, returned uniform `429` with body code `rate_limited`, message
`Too many attempts. Try again later.`, and `Retry-After: 300`. Access-failure count, lockout end,
and password-verifier count did not change after throttling. Invitation acceptance remained
independent; its throttled user retained no password and the same invitation-token hash. Expiry,
retention cleanup, partition isolation, and forwarded-header spoof resistance passed.

Evidence:

```text
Passed IdentityClientFingerprintTests.SpoofedForwardingHeadersDoNotChangeTheServerAddressFingerprint
Passed IdentityAnonymousRateLimitingTests.InvitationHasASeparatePartitionAndThrottlePreservesInvitationState
Passed IdentityAnonymousRateLimitingTests.TwoHostsEnforceOneGlobalEndpointThresholdWithoutRaceOvershoot
Passed IdentityAnonymousRateLimitingTests.LoginAndMfaChallengeShareUniformThrottleBeforeJsonOrLockoutMutation
Passed IdentityAnonymousRateLimiterStoreTests.ExpiredWindowResetsAndRetentionCleanupDeletesStaleRows
Passed IdentityAnonymousRateLimiterStoreTests.EndpointAndFingerprintPartitionsAreIndependent
Total tests: 6
Passed: 6
```

## Scenario 5: No runtime DDL and deterministic T18 SQL fixture lifecycle [PASS]

Steps:

```powershell
$filter = 'FullyQualifiedName~IdentityBootstrapAndConfigurationTests.InvariantSqlIsExactIdempotentAndReinstallable|' +
  'FullyQualifiedName~IdentityBootstrapAndConfigurationTests.OrdinaryAndBootstrapStartupNeverExecuteOwnedDdl'
dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj `
  -c Release --no-build --no-restore --nologo --filter $filter `
  --logger "console;verbosity=normal"
```

Expected: production `InstallSql`/`DownSql` exactly match the frozen fixture contract; repeated
install, down, and reinstall work against LocalDB with owned-object counts `9 -> 0 -> 9`; ordinary
and bootstrap-enabled host startup execute no CREATE/ALTER/DROP against owned objects.

Actual: exact SQL equality and the full LocalDB lifecycle passed. The command interceptor observed
no owned DDL during either startup mode.

Evidence:

```text
Passed IdentityBootstrapAndConfigurationTests.InvariantSqlIsExactIdempotentAndReinstallable [1 s]
Passed IdentityBootstrapAndConfigurationTests.OrdinaryAndBootstrapStartupNeverExecuteOwnedDdl [1 s]
Total tests: 2
Passed: 2
```

## Summary

Scenarios: 5 run, 5 passed, 0 failed.

Executed tests: 25 passed, 0 failed, 0 skipped.

Conclusion: **PASS**

Usability findings:

- The 429 contract is consistent and actionable (`rate_limited`, a plain-language retry message,
  and numeric `Retry-After`).
- Audit/limiter dependency failures return stable generic 503 contracts rather than infrastructure
  details.
- Passing xUnit output does not print audit rows or response bodies; replay requires the named
  scenarios' persisted-state assertions. This is an evidence ergonomics limitation, not a product
  failure.

Risks:

- The direct two-host HTTP burst is exercised for the shared login/MFA endpoint family. Invitation
  HTTP throttling and endpoint partitioning are exercised causally against the same DB-backed store,
  while cross-provider atomicity is separately covered by the store tests; there is no distinct
  two-host invitation HTTP burst scenario in the current suite.
- Runtime intentionally does not install schema. Deployment remains dependent on T18 applying the
  exact handoff before the hardened runtime starts.

---

STATUS:          PASS

SUMMARY:         All five requested T04 causal QA scenarios passed using real
                 WebApplicationFactory HTTP hosts and disposable SQL Server LocalDB state.

WORK_COMPLETED:  Executed bootstrap/restart and direct-SQL seal checks; privileged outcome,
                 cancellation, failure, and timeout checks; MFA denial/invalid/lockout/conflict/
                 success checks; shared anonymous limiter, 429, expiry, partition, spoofing, and
                 no-lockout-amplification checks; and deterministic SQL/no-runtime-DDL checks.

EVIDENCE:        The QA RESULT above. Aggregate: 25 passed, 0 failed, 0 skipped.

ARTIFACTS:       `.ai-org/missions/2026-08-21-t04-final-hardening/qa-results.md`

FINDINGS:        No functional QA defect found. Response contracts were stable and sanitized.
                 Evidence ergonomics and the absence of a dedicated two-host invitation HTTP burst
                 are recorded above.

RISKS:           T18 migration sequencing is operationally mandatory. Invitation cross-host
                 behavior is inferred through the common DB store plus separate endpoint and
                 cross-provider causal tests rather than one dedicated HTTP burst test.

BLOCKERS:        None.

NEXT_ACTION:     Advance to the Engineering Judge/release-readiness gate.
