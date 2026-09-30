# T04 Acceptance Evidence Matrix

Date: 2026-08-21

| AC | Named executed evidence | Result |
|---|---|---|
| AC-01 | `IdentityBootstrapAndConfigurationTests.BootstrapSeedingIsEnvironmentControlledAndPermanentlySealed` | PASS |
| AC-02 | `IdentityBootstrapAndConfigurationTests.BootstrapSeedingIsEnvironmentControlledAndPermanentlySealed`; `ConcurrentBootstrapInstancesCreateOneAdministratorAndOneSeal` | PASS |
| AC-03 | `IdentityBootstrapAndConfigurationTests.InvariantSqlIsExactIdempotentAndReinstallable` | PASS |
| AC-04 | `IdentityBootstrapAndConfigurationTests.OrdinaryAndBootstrapStartupNeverExecuteOwnedDdl`; production source search found no `InstallSql`/`DownSql` execution | PASS |
| AC-05 | `IdentityAuditFinalizerTests.ClientDisconnectDoesNotCancelAuditFinalization`; `ApplicationShutdownCancelsWriterWithoutRetry`; `NonCompletingWriterTimesOutAfterFiveSecondsWithoutRetry`; new `TimeoutWaitsForWriterCancellationPathWithinBoundedGrace`; `IdentitySecurityAndAuditTests.DeniedAuditIsBoundedIndependentlyOfRequestAborted` | PASS — 87-test causal subset passed 3/3 |
| AC-06 | `IdentityPrivilegedAuditOutcomeMatrixTests.PrivilegedRouteOutcomesEmitExactlyOneAuditPerActionAndCorrelation`; `IdentityAdministrationStoreAuditFinalizationTests.*`; focused Application Identity tests | PASS |
| AC-07 | `IdentityPrivilegedAuditOutcomeMatrixTests.ValidationConflictAndSuccessAuditsSurviveRequestAbort`; `EscapingExceptionAuditSurvivesRequestAbort`; `IdentitySecurityAndAuditTests.DeniedAuditsSurviveClientDisconnectAndAreNotDuplicated`; bounded timeout tests | PASS |
| AC-08 | `IdentityPrivilegedAuditOutcomeMatrixTests.ThrowingWriterReturnsSanitized503WithoutRetryOrTransactionalSuccess`; `IdentityAuditFinalizerTests.WriterFailureIsSanitizedAndNeverRetried`; store rollback tests | PASS |
| AC-09 | `IdentityMfaAuditTests.SetupFrameworkAndAntiforgeryDenialsAreAuditedExactlyOnce`; `SetupAndEnableForbiddenFrameworkDenialsAreAuditedExactlyOnce`; `SetupSuccessAuditIsSanitizedAndIdentifiesTheAuthenticatedUser`; `SetupConflictAndExistingKeySuccessPreserveKeyAndAuditState`; `SetupGenerationFailureAndUnexpectedExceptionAreAudited` | PASS |
| AC-10 | `IdentityMfaAuditTests.EnableDenialsAndValidationFailuresAreAuditedExactlyOnce`; `InvalidEnableCodesAuditEveryAttemptAndLockTheAccount`; `EnableSuccessAuditsOnceResetsFailuresAndIssuesSatisfiedSessionAfterCommit`; `EnableFrameworkFailureAndUnexpectedExceptionAreAuditedWithoutMutation` | PASS |
| AC-11 | MFA audit tests' `AssertSanitized` checks across setup/enable success and failure outcomes | PASS |
| AC-12 | `IdentityMfaAndLockoutTests.MfaSetupRejectsGetAndMissingAntiforgeryWithoutResettingKey`; `EnabledMfaSetupNeverRevealsEstablishedKeyOrDowngradesSatisfiedSession`; `EnabledMfaSetupRejectsUnsatisfiedAdministrativeSession` | PASS |
| AC-13 | `IdentityAnonymousRateLimiterStoreTests.TwoProvidersAtomicallyEnforceNAndNPlusOneAcrossAnAbsentRow`; `IdentityAnonymousRateLimitingTests.TwoHostsEnforceOneGlobalEndpointThresholdWithoutRaceOvershoot` | PASS |
| AC-14 | `IdentityAnonymousRateLimitingTests.AntiforgeryPrecedesAdmissionAndMfaEnrollmentRoutesRemainUnthrottled`; `LoginAndMfaChallengeShareUniformThrottleBeforeJsonOrLockoutMutation`; `InvitationHasASeparatePartitionAndThrottlePreservesInvitationState` | PASS |
| AC-15 | `IdentityAnonymousRateLimiterStoreTests.EndpointAndFingerprintPartitionsAreIndependent`; `IdentityClientFingerprintTests.FingerprintIsDeterministicFixedLengthHmacAndNormalizesMappedIpv4`; database-row assertions | PASS |
| AC-16 | `IdentityAnonymousRateLimiterStoreTests.ExpiredWindowResetsAndRetentionCleanupDeletesStaleRows` | PASS |
| AC-17 | `IdentityAnonymousRateLimitConfigurationTests.InvalidConfigurationIsRejected`; `ValidConfigurationRegistersObservableOptionsAndSqlLimiter`; complete Identity suite passed with all limiter environment variables absent | PASS |
| AC-18 | `IdentityAnonymousRateLimitingTests.LoginAndMfaChallengeShareUniformThrottleBeforeJsonOrLockoutMutation`; `InvitationHasASeparatePartitionAndThrottlePreservesInvitationState` | PASS |
| AC-19 | `IdentityClientFingerprintTests.SpoofedForwardingHeadersDoNotChangeTheServerAddressFingerprint`; production source search found no forwarded-header configuration | PASS |
| AC-20 | `IdentityAnonymousRateLimitingTests.LoginAndMfaChallengeShareUniformThrottleBeforeJsonOrLockoutMutation`; `TwoHostsEnforceOneGlobalEndpointThresholdWithoutRaceOvershoot` | PASS |
| AC-21 | `IdentityBootstrapAndConfigurationTests.InvariantSqlIsExactIdempotentAndReinstallable`; mechanical comparison: `InstallSql MATCH` and `DownSql MATCH`; architecture/decision/handoff use singleton `deleted.[Id] = 1`; T18 ownership explicit | PASS |
| AC-22 | Strict owned builds 4/4; strict solution build 0 warnings/errors; Application Identity 25/25; Integration Identity 100/100; causal subset 87/87 ×3; full solution 468/468; zero skips | PASS |

## Gate conclusion

AC-01 through AC-22 are satisfied. Overall result: **PASS**.
