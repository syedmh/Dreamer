# SECURITY RESULT

Scope: T04 revalidation delta covering the bounded audit-finalizer cancellation-observation grace,
the deterministic Integration-test limiter configuration/key, and the approved singleton
`IdentityBootstrapState.Id = 1` seal predicate. Reviewed the changed production/test code, frozen
requirements, architecture/ADR amendment, prior security gate, and focused LocalDB tests.

Critical: 0   High: 0   Medium: 0   Low: 0   Informational: 0

## Threat-model delta

- **Entry points:** privileged Identity requests after an outcome is selected; application shutdown;
  the internal five-second finalization timeout; direct SQL attempts against the bootstrap seal;
  Integration-test host configuration.
- **Trust boundaries:** HTTP request lifetime to the scoped audit finalizer; finalizer cancellation
  to the audit writer and transactional commit callback; test assembly configuration to the
  in-process test host; T18-delivered trigger SQL to SQL Server.
- **Assets:** exactly-once audit integrity, transactional mutation/audit coupling, bounded request
  and shutdown behavior, the permanent bootstrap seal, and the production limiter HMAC secret.
- **Actors/failures considered:** disconnected client, shutting-down host, non-cooperative or
  cancellation-delayed writer, duplicate layer invocation, direct SQL caller, parallel test
  execution, and accidental promotion of fixture configuration into production.

## Blocking findings

None.

## All findings

None.

## Delta analysis

- The finalizer still claims `(correlationId, action)` before invoking the writer and invokes
  `AppendAsync` only once. Duplicate claims fail before either the writer or commit callback runs
  (`src/Husaynia.Web/Areas/Admin/Identity/IdentityAuditFinalizer.cs:35-48`).
- The token remains linked only to `ApplicationStopping`, not `RequestAborted`, and is cancelled
  after five seconds (`IdentityAuditFinalizer.cs:40-43`). The new cancellation-observation path
  waits with `Task.WhenAny` against a fixed one-second delay; it therefore cannot add an unbounded
  request/shutdown wait (`IdentityAuditFinalizer.cs:10-11,54-59,77-85`).
- If cancellation wins, persistence completion is not started. If the writer completes during the
  grace period, the finalizer observes its fault/success and then the cancelled-token check prevents
  the commit callback. A writer that ignores cancellation can continue its own task, but the
  finalizer returns after the bounded grace and performs no retry or duplicate write.
- The finalizer is registered scoped, so its claim dictionary is request-scoped rather than a
  process-lifetime unbounded key store
  (`src/Husaynia.Web/Areas/Admin/Identity/IdentityAdminEndpointModule.cs:72-74`).
- The deterministic fingerprint value is defined only in
  `tests/Husaynia.IntegrationTests/Identity/IdentityTestHost.cs:128-158`, is supplied through the
  test factory's in-memory/effective configuration, and is not a production credential. Searches
  found no production, infrastructure, pipeline, or deployment reference to the test factory or
  fixture key. Production configuration continues to require externally supplied valid base64
  containing at least 32 bytes
  (`src/Husaynia.Infrastructure/Identity/IdentityModule.cs:262-279`).
- The seal trigger checks the pre-change `deleted` rows for authoritative key `Id = 1`, which blocks
  UPDATE, key reassignment, DELETE, and mixed-row statements containing that row
  (`src/Husaynia.Infrastructure/Identity/IdentityAuditDatabaseInvariant.cs:56-68`). The domain
  constant and bootstrap lookup both use the same singleton key
  (`IdentityPersistence.cs:205-224`;
  `src/Husaynia.Web/Areas/Admin/Identity/IdentityBootstrapSeeder.cs:57-61`). This matches AC-01 and
  the explicit architecture/ADR approval; statements affecting only non-authoritative rows do not
  bypass protection of the singleton.
- The reviewed delta adds no package reference, package source, production default, logging of the
  fixture key, raw secret material, dynamic SQL, command execution, or new externally reachable
  input.

## Verification evidence

- Focused build/test command:
  `dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj -c Release
  --no-restore --nologo --filter
  "FullyQualifiedName~IdentityAuditFinalizerTests|FullyQualifiedName~IdentitySecurityAndAuditTests.DeniedAuditIsBoundedIndependentlyOfRequestAborted|FullyQualifiedName~IdentityBootstrapAndConfigurationTests.BootstrapSeedingIsEnvironmentControlledAndPermanentlySealed|FullyQualifiedName~IdentityBootstrapAndConfigurationTests.InvariantSqlIsExactIdempotentAndReinstallable"`
  — **12 passed, 0 failed, 0 skipped**.
- Three consecutive repetitions of the four timeout/shutdown tests
  (`TimeoutWaitsForWriterCancellationPathWithinBoundedGrace`,
  `NonCompletingWriterTimesOutAfterFiveSecondsWithoutRetry`,
  `ApplicationShutdownCancelsWriterWithoutRetry`, and
  `DeniedAuditIsBoundedIndependentlyOfRequestAborted`) — each run **4 passed, 0 failed, 0 skipped**;
  aggregate **12/12 passed**.
- Source searches found no production/deployment references to `IdentityWebApplicationFactory` or
  `AnonymousRateLimitFingerprintKey`, and no production execution of
  `IdentityAuditDatabaseInvariant.InstallSql`/`DownSql`.
- Package manifests were reviewed; no dependency or package-source change is part of this delta.
  NU1900 was not reassessed, per scope.
- Repository caveat remains: the parent Git worktree reports `HusayniaSite` as an untracked subtree,
  so an authoritative historical scoped diff is unavailable. The specified changed files and their
  connected trust boundaries were reviewed directly.

Conclusion: **PASS**

---

STATUS:          PASS

SUMMARY:         Zero unresolved Critical or High findings. The timeout-observation rework remains
                 bounded, preserves shutdown-only cancellation and exactly-once behavior, does not
                 start a commit after timeout, and introduces no production secret/configuration
                 leakage. The singleton `Id = 1` trigger predicate protects the authoritative seal
                 as approved.

WORK_COMPLETED:  Threat-modeled the rework delta; reviewed finalizer, registration, fixture,
                 trigger/domain/bootstrap code, tests, requirements, architecture, decisions,
                 package manifests, and the prior security gate; executed focused and repeated
                 regression tests.

EVIDENCE:        Files and line ranges cited above. Focused security run: 12/12 passed. Repeated
                 timeout/shutdown subset: 4/4 passed in each of three runs (12/12 aggregate).
                 Production-isolation and secret/reference searches returned no fixture-key or
                 test-host references.

ARTIFACTS:       `.ai-org/missions/2026-08-21-t04-final-hardening/security-review.md`

FINDINGS:        None.

RISKS:           A deliberately non-cooperative `IAuditWriter` can continue its own task after the
                 finalizer's bounded grace, but cannot cause an unbounded request wait or a retry
                 from this finalizer. The repository's untracked-subtree state prevents an
                 authoritative Git diff. Neither creates an identified Critical/High attack path.

BLOCKERS:        None.

NEXT_ACTION:     Proceed to the remaining independent gate. No security remediation is required
                 for this revalidation.
