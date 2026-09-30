# T19 Durable Operations Rework — Independent Test Gate

Verdict: **APPROVED**

## TEST RESULT

### Commands

```text
dotnet build .\tests\Husaynia.Application.Tests\Husaynia.Application.Tests.csproj --no-restore --configuration Release -warnaserror --nologo
dotnet build .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj --no-restore --configuration Release -warnaserror --nologo
dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj --no-build --no-restore --configuration Release --filter "FullyQualifiedName~ExpiredFinalAttemptIsDeadLetteredWithoutExecutingAgain|FullyQualifiedName~ExhaustedDeadLetterRequeueGetsFreshBudgetAndCompletes|FullyQualifiedName~DeadLetterCancellationIsRejectedWithoutPersistingRequest|FullyQualifiedName~FinalAttemptShutdownTerminalizesWithoutRestart|FullyQualifiedName~RepeatedShutdownRecoveryNeverAcquiresMaximumAttemptsPlusOne|FullyQualifiedName~ExpiredFinalAttemptWithCancellationIsCancelled|FullyQualifiedName~ReducedRetryBudgetTerminalizesBeforeAnotherAcquisition|FullyQualifiedName~PartialApplyIsReclaimedWithDurableProgress|FullyQualifiedName~StaleMarkAppliedIsFencedAfterLeaseReclaim|FullyQualifiedName~StaleCompleteIsFencedAfterLeaseReclaim|FullyQualifiedName~CompletedRunRejectsStaleLeaseAndAcceptsCompletingLeaseIdempotently|FullyQualifiedName~HoldPlacementIsBlockedByExpiredApplyingItem|FullyQualifiedName~DetachedOldPermitCannotDeleteAfterReclaimAndHoldAcceptance|FullyQualifiedName~CompletedRunTargetMutatesDatabase|FullyQualifiedName~ActiveApplyPermitPreservesIdempotentAtLeastOnceMutation|FullyQualifiedName~ConcurrentHoldAndApplyAreSerialized" --logger "console;verbosity=minimal"
dotnet test .\tests\Husaynia.Application.Tests\Husaynia.Application.Tests.csproj --no-build --no-restore --configuration Release --filter "FullyQualifiedName~Husaynia.Application.Tests.Operations" --logger "console;verbosity=minimal"
dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj --no-build --no-restore --configuration Release --filter "FullyQualifiedName~Husaynia.IntegrationTests.Operations" --logger "console;verbosity=minimal"
1..3 | ForEach-Object {
  dotnet test .\tests\Husaynia.Application.Tests\Husaynia.Application.Tests.csproj --no-build --no-restore --configuration Release --filter "FullyQualifiedName~LongRunningHandlerRenewsLeaseBeforeCompleting|FullyQualifiedName~RenewalLossCancelsHandlerWithoutMutatingLostLease|FullyQualifiedName~HostStopReleasesLeaseForRecoveryAndPropagatesCancellation|FullyQualifiedName~HostStopDoesNotWaitForCancellationIgnoringHandlerOrAllowLateCompletion|FullyQualifiedName~RenewalLossDuringHandlerExceptionDoesNotMutateLostLease" --logger "console;verbosity=minimal"
  dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj --no-build --no-restore --configuration Release --filter "FullyQualifiedName~ExpiredFinalAttemptIsDeadLetteredWithoutExecutingAgain|FullyQualifiedName~ExhaustedDeadLetterRequeueGetsFreshBudgetAndCompletes|FullyQualifiedName~DeadLetterCancellationIsRejectedWithoutPersistingRequest|FullyQualifiedName~FinalAttemptShutdownTerminalizesWithoutRestart|FullyQualifiedName~RepeatedShutdownRecoveryNeverAcquiresMaximumAttemptsPlusOne|FullyQualifiedName~ExpiredFinalAttemptWithCancellationIsCancelled|FullyQualifiedName~ReducedRetryBudgetTerminalizesBeforeAnotherAcquisition|FullyQualifiedName~PartialApplyIsReclaimedWithDurableProgress|FullyQualifiedName~StaleMarkAppliedIsFencedAfterLeaseReclaim|FullyQualifiedName~StaleCompleteIsFencedAfterLeaseReclaim|FullyQualifiedName~CompletedRunRejectsStaleLeaseAndAcceptsCompletingLeaseIdempotently|FullyQualifiedName~HoldPlacementIsBlockedByExpiredApplyingItem|FullyQualifiedName~DetachedOldPermitCannotDeleteAfterReclaimAndHoldAcceptance|FullyQualifiedName~CompletedRunTargetMutatesDatabase|FullyQualifiedName~ActiveApplyPermitPreservesIdempotentAtLeastOnceMutation|FullyQualifiedName~ConcurrentHoldAndApplyAreSerialized" --logger "console;verbosity=minimal"
  sqlcmd -S "(localdb)\MSSQLLocalDB" -E -d master -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name LIKE 'HusayniaT03[_]%';"
}
dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj --no-build --no-restore --configuration Release --logger "console;verbosity=minimal"
```

### Result

```text
Application owned strict build:
Build succeeded. 0 Warning(s), 0 Error(s).

Integration owned strict build:
Build succeeded. 0 Warning(s), 0 Error(s).

Focused recovery/fencing regression set:
Passed! - Failed: 0, Passed: 16, Skipped: 0, Total: 16.

All Application Operations:
Passed! - Failed: 0, Passed: 56, Skipped: 0, Total: 56.

All Integration Operations:
Passed! - Failed: 0, Passed: 62, Skipped: 0, Total: 62.

Causal stress:
Three runs, each 5 Application + 16 Integration.
Passed: 63, Failed: 0, Skipped: 0. Flake rate: 0/63.

Full Integration project, executed only to isolate non-T19 drift:
Failed: 24, Passed: 253, Skipped: 0, Total: 277.
All 24 failures are under Husaynia.IntegrationTests.Identity and have the same
configuration-validation root cause: missing Identity:AnonymousRateLimit
PermitLimit, Window, Retention, and FingerprintKey.

Final disposable LocalDB inventory:
0 databases matching HusayniaT03_%.
```

Passed: **118 distinct Operations tests; 63 repeated causal executions**  
Failed: **0 Operations tests**  
Skipped: **0 Operations tests**

### Failures

No T19 failure.

Unrelated T04 Identity failures: **24**. Expected host startup; actual
`HusayniaConfigurationException` from `IdentityConfigurationValidator` because the affected
Identity test hosts do not provide the newly required anonymous-rate-limit configuration. These
fail before their Identity scenarios execute and are outside Operations production/test ownership.

### Coverage

- Maximum attempts never exceeded:
  `FinalAttemptShutdownTerminalizesWithoutRestart`,
  `RepeatedShutdownRecoveryNeverAcquiresMaximumAttemptsPlusOne`,
  `ReducedRetryBudgetTerminalizesBeforeAnotherAcquisition`, and
  `ExpiredFinalAttemptIsDeadLetteredWithoutExecutingAgain` — PASS.
- Requeue budget reset and completion:
  `ExhaustedDeadLetterRequeueGetsFreshBudgetAndCompletes` — PASS.
- Cancellation precedence and terminal-state protection:
  `ExpiredFinalAttemptWithCancellationIsCancelled`,
  `DeadLetterCancellationIsRejectedWithoutPersistingRequest`, and
  `CancellationStopsPendingAndRunningJobs` — PASS.
- Stale retention lease fencing:
  `StaleMarkAppliedIsFencedAfterLeaseReclaim`,
  `StaleCompleteIsFencedAfterLeaseReclaim`, and
  `CompletedRunRejectsStaleLeaseAndAcceptsCompletingLeaseIdempotently` — PASS.
- Recovery reconciliation:
  `PartialApplyIsReclaimedWithDurableProgress` — PASS.
- Hold/apply serialization and stale permit refusal:
  `HoldPlacementIsBlockedByExpiredApplyingItem`,
  `DetachedOldPermitCannotDeleteAfterReclaimAndHoldAcceptance`, and
  `ConcurrentHoldAndApplyAreSerialized` — PASS.
- Real mutation and at-least-once idempotency:
  `CompletedRunTargetMutatesDatabase` and
  `ActiveApplyPermitPreservesIdempotentAtLeastOnceMutation` — PASS.
- Processor renewal/loss/shutdown causal paths — PASS in three repeated runs.
- Completed validation leak inventory — PASS, zero.

Conclusion: **PASS**

## Standard block

STATUS: **PASS**

SUMMARY: T19 rework is independently approved. Owned strict builds, all 118 Operations tests, the
16-test recovery/fencing set, and 63 causal stress executions passed with no skips or flakes.

WORK_COMPLETED: Executed focused job/retention recovery and fencing regressions, all Operations
Application/Integration tests, strict owned builds, repeated stress, full Integration isolation,
and LocalDB inventory.

EVIDENCE: The TEST RESULT above.

ARTIFACTS:
- `.ai-org/missions/2026-08-21-t19-rework-independent-validation/test-results.md`

FINDINGS: No T19 finding. The 24 full-Integration failures are isolated T04 Identity test-host
configuration drift.

RISKS: Shared concurrent test processes can leave disposable databases when externally terminated;
completed T19 runs cleaned normally, interrupted-run databases were verified sessionless and
removed, and final inventory is zero.

BLOCKERS: None for T19.

NEXT_ACTION: Approve T19; remediate the T04 Identity test-host AnonymousRateLimit configuration
separately.
