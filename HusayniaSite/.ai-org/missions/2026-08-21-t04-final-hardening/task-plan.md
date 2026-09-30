# T04 Final Identity Hardening — Dependency-Ordered Task Plan

Date: 2026-08-21  
Status: COMPLETED — FINAL JUDGE APPROVED  
Source of truth: `requirements.md`, `architecture.md`, `definition-of-done.md`, `decisions.md`

## Scope guard

Implementation agents may modify only the exact files assigned to their task below. They must:

- preserve every unrelated existing change and never clean/reset the worktree;
- treat `HusayniaSite` as an untracked subtree at the parent repository level;
- inspect and stage changes by exact path only;
- add no migration or model snapshot;
- change no project/package file, configuration default, secret, deployment, pipeline,
  infrastructure, non-Identity production file, or non-Identity test file;
- execute no schema DDL from application startup or other runtime production code;
- not commit, push, or alter git history;
- stop if an unlisted file is required and return `BLOCKED` with the proposed file and reason.

T18 exclusively owns migration/snapshot authoring and migration application. T04 supplies only the
frozen model, deterministic SQL constants, executable fixture coverage, and handoff artifact.

## Frozen interfaces and behavior

These contracts are fixed before implementation fan-out. Consumer tasks must not rename, reshape,
or reinterpret them. A required change stops the fan-out and returns to the Tech Lead.

### Application contracts

File: `src/Husaynia.Application/Identity/IdentityAdministrationContracts.cs`

```csharp
public interface IIdentityAuditFinalizer
{
    Task FinalizeOnceAsync(
        IdentityAuditDescriptor descriptor,
        PrivilegedAttemptOutcome outcome,
        IReadOnlyDictionary<string, string?> details,
        Func<CancellationToken, Task>? completePersistenceAsync = null);
}

public interface IIdentityAnonymousRateLimiter
{
    Task<IdentityRateLimitDecision> AttemptAsync(
        string endpointFamily,
        ReadOnlyMemory<byte> clientFingerprint,
        CancellationToken cancellationToken);
}

public sealed record IdentityRateLimitDecision(
    bool IsAllowed,
    DateTimeOffset WindowEndsAtUtc,
    int RequestCount,
    int PermitLimit);

public sealed class IdentityAuditFinalizationException : Exception
{
    public IdentityAuditFinalizationException(string message, Exception? innerException = null);
}
```

`IdentityAuditFinalizationException` is the only typed signal used to distinguish audit
finalization failure from an operation/business failure. Its public message is constant and
sanitized; callers never expose its inner exception or retry finalization.

### Audit finalizer semantics

- Production type: `IdentityAuditFinalizer`.
- Lifetime: scoped.
- Exactly-once key: ordinal tuple `(descriptor.CorrelationId, descriptor.Action)`.
- Claim occurs atomically before writer invocation.
- A duplicate claim performs no write/callback and throws `IdentityAuditFinalizationException`.
- Token is linked only to `IHostApplicationLifetime.ApplicationStopping`; it is never linked to
  `HttpContext.RequestAborted`.
- Timeout is exactly `TimeSpan.FromSeconds(5)`.
- Order: `IAuditWriter.AppendAsync` -> `ThrowIfCancellationRequested` -> optional
  `completePersistenceAsync`, all using the same finalizer token.
- No retry.
- Timeout, cancellation, writer failure, and callback failure become a sanitized
  `IdentityAuditFinalizationException`.
- Transactional callers pass `token => transaction.CommitAsync(token)`.
- Rollback remains `CancellationToken.None`.

### Audit ownership

- Framework authentication/authorization denial:
  `IdentityAuthorizationMiddlewareResultHandler`.
- Privileged antiforgery or malformed transport input: Web endpoint/helper.
- Application capability denial or command validation failure:
  `IdentityAdministrationService`.
- Store conflict, dependency/business failure, success, or pre-finalization exception:
  `IdentityAdministrationStore`.
- MFA setup/enable after framework admission: MFA endpoint handler.
- Exception escaping before any owner finalizes: privileged endpoint exception wrapper.
- Bootstrap and anonymous failure audits remain direct `IAuditWriter` users and are not claimed by
  `IIdentityAuditFinalizer`.
- No catch block may translate `IdentityAuditFinalizationException` into a business/dependency
  result or request another audit.

### Anonymous admission contracts

New Web file:
`src/Husaynia.Web/Areas/Admin/Identity/IdentityAnonymousAdmission.cs`

```csharp
internal interface IIdentityClientFingerprintProvider
{
    ReadOnlyMemory<byte> Create(HttpContext context);
}

internal static class IdentityAnonymousEndpointFamilies
{
    internal const string Login = "login";
    internal const string InvitationAccept = "invitation-accept";
}
```

Production provider name: `HmacIdentityClientFingerprintProvider`.

- Input is only `HttpContext.Connection.RemoteIpAddress`.
- IPv4-mapped IPv6 is normalized to IPv4.
- Null address hashes fixed UTF-8 bytes for `unknown`.
- Output is exactly 32 bytes from HMAC-SHA-256 using the decoded configured key.
- `X-Forwarded-For`, `Forwarded`, and similar headers are ignored.
- No forwarded-header middleware/configuration is added.

Limiter configuration is required:

- `Identity:AnonymousRateLimit:PermitLimit`: integer `1..1000`.
- `Identity:AnonymousRateLimit:Window`: `TimeSpan > 0` and `<= 01:00:00`.
- `Identity:AnonymousRateLimit:Retention`: `>= Window` and `<= 30.00:00:00`.
- `Identity:AnonymousRateLimit:FingerprintKey`: valid base64 decoding to at least 32 bytes.

Production types:

- `IdentityAnonymousRateLimitOptions`
- `IdentityAnonymousRateLimit`
- `IdentityAnonymousRateLimitConfiguration`
- `SqlIdentityAnonymousRateLimiter`

SQL limiter behavior is fixed:

1. SQL Server `Serializable` transaction.
2. Read the endpoint/fingerprint partition with `WITH (UPDLOCK, HOLDLOCK)`.
3. Insert count `1` when absent; reset to `1` when `now >= WindowEndsAtUtc`; otherwise increment
   only when `RequestCount < PermitLimit`.
4. Deny without Identity-state mutation when exhausted.
5. Retry the whole transaction once, using the same captured `now`, only for SQL 2601, 2627, or
   1205.
6. Any remaining store failure propagates; Web maps it fail-closed to generic 503.
7. After decision commit, best-effort parameterized `DELETE TOP (32)` removes
   `RetainUntilUtc <= now`; cleanup failure is identifier-free and does not change the decision.

### HTTP behavior

- Limiter runs after antiforgery and before JSON parsing, account/token lookup, password/OTP
  verification, or lockout/access-failure mutation.
- Login password and anonymous MFA challenge share endpoint family `login`.
- Invitation acceptance uses `invitation-accept`.
- Authenticated MFA setup/enable do not use the anonymous limiter.
- Throttle response: HTTP 429, existing `FailureResponse`, code `rate_limited`, message
  `Too many attempts. Try again later.`, and integer `Retry-After` equal to
  `max(1, ceil(WindowEndsAtUtc - now))`.
- Limiter dependency failure: HTTP 503, constant code `identity_rate_limit_unavailable`, constant
  non-secret message.
- Audit finalization failure: HTTP 503, code `identity_audit_unavailable`, constant non-secret
  message.
- Existing below-threshold route, success, status, and body contracts remain unchanged.

### MFA policy and taxonomy

- Policy constant: `IdentityMfaEnrollment`.
- Requirement: authenticated user with any role in `RoleNames.All`; an MFA-satisfied claim is not
  required.
- Both POST `/mfa/setup` and POST `/mfa/enable` carry this policy and
  `IdentityPrivilegedEndpointMetadata`.
- Actions: `identity.mfa.setup`, `identity.mfa.enable`.
- Setup results/error codes:
  `denied/unauthenticated`, `denied/forbidden`, `denied/antiforgery_required`,
  `key_ready` with `keyCreated=true|false`, `conflict/mfa_already_enabled`,
  `failed/authenticator_key_generation_failed`, `exception/unexpected_failure`.
- Enable results/error codes:
  framework/antiforgery denial, `validation_failed/missing_two_factor_code`,
  `validation_failed/malformed_two_factor_code`, `invalid_code/invalid_two_factor_code`,
  `locked_out/locked_out`, `failed/mfa_enable_failed`, `enabled`,
  `exception/unexpected_failure`.
- Details are constants/state classifications only. No OTP, authenticator key, password, recovery
  material, invitation token, raw email, or raw IP may enter any audit field.
- Setup key creation and audit are one transaction.
- Enable mutation, audit, and commit are one transaction; cookie issuance occurs after commit.

### SQL object names and deterministic handoff

- Table: `dbo.IdentityAnonymousRateLimits`.
- PK: `PK_IdentityAnonymousRateLimits`.
- Unique constraint:
  `UQ_IdentityAnonymousRateLimits_EndpointFamily_ClientFingerprint`.
- Expiry index: `IX_IdentityAnonymousRateLimits_RetainUntilUtc`.
- Checks: `CK_IdentityAnonymousRateLimits_RequestCount`,
  `CK_IdentityAnonymousRateLimits_Window`,
  `CK_IdentityAnonymousRateLimits_Retention`.
- Audit trigger: `dbo.TR_IdentityAuditEvents_AppendOnly`; SQL error 51004.
- Seal trigger: `dbo.TR_IdentityBootstrapState_PermanentSeal`; SQL error 51005.
- `IdentityAuditDatabaseInvariant.InstallSql` and `.DownSql` must exactly implement architecture
  section 7, including `SET XACT_ABORT ON`, idempotent table/index creation, `CREATE OR ALTER`
  triggers through dynamic SQL, and Down order seal trigger -> audit trigger -> limiter table.

Entity columns are fixed:

`Id bigint IDENTITY(1,1)`, `EndpointFamily nvarchar(32)`,
`ClientFingerprint binary(32)`, `WindowStartedAtUtc datetimeoffset(7)`,
`WindowEndsAtUtc datetimeoffset(7)`, `RequestCount int`,
`RetainUntilUtc datetimeoffset(7)`.

## Tasks

T1  Add frozen Application contracts
    owner:        developer
    objective:    Add the audit-finalizer, anonymous-limiter, decision, and sanitized exception
                  contracts exactly as frozen above, without changing existing public method shapes.
    files:        src/Husaynia.Application/Identity/IdentityAdministrationContracts.cs
                  tests/Husaynia.Application.Tests/Identity/IdentityHardeningContractTests.cs (new)
    depends_on:   -
    parallel_ok:  no
    red_tests:    Contract reflection tests fail because the frozen types/signatures do not exist.
    green_tests:  Contract reflection/constructor tests prove exact names, parameter order, optional
                  callback default, immutable decision values, and sanitized exception construction.
    commands:     dotnet build src/Husaynia.Application/Husaynia.Application.csproj -c Release -warnaserror
                  dotnet test tests/Husaynia.Application.Tests/Husaynia.Application.Tests.csproj -c Release
                  --filter FullyQualifiedName~IdentityHardeningContractTests
    exit_criteria: Frozen contracts compile exactly; focused tests pass with zero failed/skipped;
                  scoped diff contains only the two allowed files.
    handoff:      Publish the compiled signatures to T2/T3/T4/T6; no consumer work starts before T1.
    status:       PENDING

T2  Implement scoped audit finalizer
    owner:        developer
    objective:    Implement exactly-once claim, shutdown-linked five-second cancellation, single
                  writer attempt, optional commit callback, and sanitized failure semantics.
    files:        src/Husaynia.Web/Areas/Admin/Identity/IdentityAuditFinalizer.cs (new)
                  tests/Husaynia.IntegrationTests/Identity/IdentityAuditFinalizerTests.cs (new)
    depends_on:   T1
    parallel_ok:  yes
    red_tests:    Tests fail for disconnect survival, shutdown cancellation, five-second timeout,
                  duplicate claim, writer throw, non-completing writer, callback ordering, and no retry.
    green_tests:  Deterministic fake-writer/lifetime tests assert one attempt; request cancellation is
                  ignored; shutdown and timeout cancel; callback receives the identical token and runs
                  after append; duplicate claim and failures are sanitized.
    commands:     dotnet build src/Husaynia.Web/Husaynia.Web.csproj -c Release -warnaserror
                  dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj -c Release
                  --filter FullyQualifiedName~IdentityAuditFinalizerTests
    exit_criteria: AC-05, finalizer portions of AC-07/AC-08 are proven in isolation with zero
                  failed/skipped tests; no DI or caller files are changed.
    handoff:      Provide constructor dependencies and passing test names to T8.
    status:       PENDING

T3  Move Application audit ownership
    owner:        developer
    objective:    Replace direct privileged Application `IAuditWriter` calls with
                  `IIdentityAuditFinalizer`, retaining Application ownership only for capability
                  denial and command validation outcomes.
    files:        src/Husaynia.Application/Identity/IdentityAdministrationService.cs
                  tests/Husaynia.Application.Tests/Identity/IdentityAdministrationServiceTests.cs
    depends_on:   T1
    parallel_ok:  yes
    red_tests:    Existing service tests are converted to a spy finalizer and initially fail when
                  request tokens reach audit calls, outcomes duplicate, or validation reaches store.
    green_tests:  Every denial/validation/probe result invokes finalizer once with the frozen action,
                  outcome, sanitized details, and no commit callback; successful store-bound paths
                  leave audit ownership to the store.
    commands:     dotnet build src/Husaynia.Application/Husaynia.Application.csproj -c Release -warnaserror
                  dotnet test tests/Husaynia.Application.Tests/Husaynia.Application.Tests.csproj -c Release
                  --filter FullyQualifiedName~IdentityAdministrationServiceTests
    exit_criteria: Application tests cover denial, invalid email/role/id/stamp/reason/take, capability
                  success, and store delegation with exactly one finalizer claim per selected outcome.
    handoff:      T8 may rely on Application returning `IdentityAuditFinalizationException` unchanged.
    status:       PENDING

T4  Move store audit and commit ownership
    owner:        backend-specialist
    objective:    Use the finalizer for store-selected conflict/failure/success/exception outcomes and
                  commit transactional mutation only through the finalizer callback.
    files:        src/Husaynia.Infrastructure/Identity/IdentityAdministrationStore.cs
                  tests/Husaynia.IntegrationTests/Identity/IdentityAdministrationStoreAuditFinalizationTests.cs (new)
    depends_on:   T1
    parallel_ok:  yes
    red_tests:    Store tests fail when request cancellation suppresses audit, commit precedes audit,
                  audit/callback failure leaves mutation committed, or a catch path audits twice.
    green_tests:  Invite/disable/role/read success and conflict/dependency paths finalize once;
                  transactional success passes `CommitAsync` callback; audit failure rolls back and
                  propagates; `IdentityAuditFinalizationException` is never reclassified/re-audited.
    commands:     dotnet build src/Husaynia.Infrastructure/Husaynia.Infrastructure.csproj -c Release -warnaserror
                  dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj -c Release
                  --filter FullyQualifiedName~IdentityAdministrationStoreAuditFinalizationTests
    exit_criteria: AC-06/AC-08 store ownership and mutation-plus-audit atomicity are proven with
                  zero failed/skipped tests and no edits outside the two assigned files.
    handoff:      Report the exact transactional paths covered to T8 and T12.
    status:       PENDING

T5  Install deterministic schema model and fixture
    owner:        database-specialist
    objective:    Add the limiter EF entity/configuration, exact deterministic invariant SQL, direct
                  seal protection tests, and explicit fixture install/down/reinstall helpers.
    files:        src/Husaynia.Infrastructure/Identity/IdentityPersistence.cs
                  src/Husaynia.Infrastructure/Identity/IdentityAuditDatabaseInvariant.cs
                  tests/Husaynia.IntegrationTests/Identity/IdentityTestHost.cs
                  tests/Husaynia.IntegrationTests/Identity/IdentityBootstrapAndConfigurationTests.cs
    depends_on:   T1
    parallel_ok:  yes
    red_tests:    Tests fail because seal UPDATE/DELETE succeeds, limiter objects are absent, Down/
                  reinstall helpers do not exist, and runtime-DDL interception recognizes only one trigger.
    green_tests:  Exact SQL text assertions and LocalDB install/install/down/down/reinstall pass;
                  direct seal UPDATE and DELETE each throw SQL 51005 with byte-for-byte unchanged row;
                  restart does not recreate roles/admin/seal/audits; ordinary and bootstrap-enabled
                  runtime startup issue no owned CREATE/ALTER/DROP command.
    commands:     dotnet build src/Husaynia.Infrastructure/Husaynia.Infrastructure.csproj -c Release -warnaserror
                  dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj -c Release
                  --filter FullyQualifiedName~IdentityBootstrapAndConfigurationTests
    exit_criteria: AC-01..AC-04 and SQL portion of AC-21 pass; fixture alone executes InstallSql;
                  no production startup path references InstallSql/DownSql.
    handoff:      Freeze fixture helper signatures and publish exact SQL/object names to T6/T11.
    status:       PENDING

T6  Implement atomic SQL limiter
    owner:        backend-specialist
    objective:    Implement validated options, DI registration, SQL Server fixed-window store,
                  bounded retry, expiry, retention cleanup, and fail-closed propagation.
    files:        src/Husaynia.Infrastructure/Identity/IdentityModule.cs
                  src/Husaynia.Infrastructure/Identity/IdentityAnonymousRateLimit.cs (new)
                  src/Husaynia.Infrastructure/Identity/SqlIdentityAnonymousRateLimiter.cs (new)
                  tests/Husaynia.IntegrationTests/Identity/IdentityAnonymousRateLimitConfigurationTests.cs (new)
                  tests/Husaynia.IntegrationTests/Identity/IdentityAnonymousRateLimiterStoreTests.cs (new)
    depends_on:   T1, T5
    parallel_ok:  yes
    red_tests:    Configuration matrix and barrier-synchronized two-provider LocalDB tests fail for
                  missing/invalid options, absent-row races, N+1 overshoot, expiry, partitions, cleanup,
                  retry, and dependency failure.
    green_tests:  All invalid/missing values fail validation; valid limits are observable; two
                  independent service providers sharing one DB allow at most N; endpoint/fingerprint
                  partitions isolate; expiry resets; stale rows become non-counting and are cleaned;
                  2601/2627/1205 retry once with fixed `now`; further errors propagate.
    commands:     dotnet build src/Husaynia.Infrastructure/Husaynia.Infrastructure.csproj -c Release -warnaserror
                  dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj -c Release
                  --filter "FullyQualifiedName~IdentityAnonymousRateLimitConfigurationTests|FullyQualifiedName~IdentityAnonymousRateLimiterStoreTests"
    exit_criteria: AC-13, AC-15..AC-17 store/configuration portions pass with zero failed/skipped;
                  SQL uses Serializable plus UPDLOCK/HOLDLOCK and stores only family + 32-byte hash.
    handoff:      Publish registered service and deterministic test configuration to T7.
    status:       PENDING

T7  Enforce anonymous Web admission
    owner:        frontend-specialist
    objective:    Add trusted HMAC fingerprinting and enforce limiter before login/invitation body
                  or verifier work, with uniform 429 and fail-closed 503 behavior.
    files:        src/Husaynia.Web/Areas/Admin/Identity/IdentityAnonymousAdmission.cs (new)
                  src/Husaynia.Web/Areas/Admin/Identity/IdentityAdminEndpointModule.cs
                  src/Husaynia.Web/Areas/Admin/Identity/IdentityAdminEndpoints.cs
                  tests/Husaynia.IntegrationTests/Identity/IdentityClientFingerprintTests.cs (new)
                  tests/Husaynia.IntegrationTests/Identity/IdentityAnonymousRateLimitingTests.cs (new)
    depends_on:   T5, T6
    parallel_ok:  no
    red_tests:    Tests fail for raw/forwarded IP handling, HMAC shape, uniform 429, Retry-After,
                  verifier short-circuiting, invitation short-circuiting, MFA-login allowance sharing,
                  partition isolation, store failure, and lockout amplification.
    green_tests:  Provider ignores spoofed headers and emits deterministic 32-byte HMAC; login and
                  invitation admission occurs after antiforgery but before JSON/state/verifier work;
                  exhausted partitions return equivalent 429 responses and valid Retry-After;
                  limiter failure returns generic 503; throttled calls do not change access-failure,
                  lockout, invitation, password, or OTP state.
    commands:     dotnet build src/Husaynia.Web/Husaynia.Web.csproj -c Release -warnaserror
                  dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj -c Release
                  --filter "FullyQualifiedName~IdentityClientFingerprintTests|FullyQualifiedName~IdentityAnonymousRateLimitingTests"
    exit_criteria: AC-14, AC-18..AC-20 and Web portions of AC-13/AC-15 pass; no forwarded-header
                  configuration exists; below-threshold contracts remain unchanged.
    handoff:      T8/T9 must preserve the admission ordering and must not move MFA setup/enable behind it.
    status:       PENDING

T8  Integrate privileged audit ownership
    owner:        developer
    objective:    Register the finalizer, replace Web/middleware privileged writer calls, remove
                  overlapping endpoint pre-check audit ownership, add exception finalization and
                  sanitized 503 mapping, and prove the route outcome matrix.
    files:        src/Husaynia.Web/Areas/Admin/Identity/IdentityAdminEndpointModule.cs
                  src/Husaynia.Web/Areas/Admin/Identity/IdentityAdminEndpoints.cs
                  src/Husaynia.Web/Areas/Admin/Identity/IdentityAuthorizationMiddlewareResultHandler.cs
                  tests/Husaynia.IntegrationTests/Identity/IdentitySecurityAndAuditTests.cs
                  tests/Husaynia.IntegrationTests/Identity/IdentityPrivilegedAuditOutcomeMatrixTests.cs (new)
    depends_on:   T2, T3, T4, T7
    parallel_ok:  no
    red_tests:    Matrix fails for framework denial, antiforgery, malformed transport, Application
                  validation, store conflict/failure, escaping exception, and success when attempts are
                  zero/multiple, request cancellation wins, or writer details leak.
    green_tests:  Every privileged route/outcome has one finalizer attempt for action/correlation;
                  delayed denial/validation/conflict/exception/success survives disconnect; shutdown/
                  timeout cancel; throwing/non-completing writer makes no second attempt and yields
                  constant 503 without falsely reporting transactional success.
    commands:     dotnet build src/Husaynia.Web/Husaynia.Web.csproj -c Release -warnaserror
                  dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj -c Release
                  --filter "FullyQualifiedName~IdentitySecurityAndAuditTests|FullyQualifiedName~IdentityPrivilegedAuditOutcomeMatrixTests"
    exit_criteria: AC-05..AC-08 pass for middleware, endpoint, Application, and store paths; direct
                  privileged `IAuditWriter` calls remain only in explicitly exempt bootstrap/anonymous code.
    handoff:      Publish route/action ownership matrix and unresolved MFA rows to T9.
    status:       PENDING

T9  Complete MFA audit lifecycle
    owner:        developer
    objective:    Apply `IdentityMfaEnrollment`, make MFA setup/enable transactional where required,
                  finalize every frozen outcome exactly once, preserve secrecy/session behavior, and
                  implement invalid-code lockout behavior.
    files:        src/Husaynia.Web/Areas/Admin/Identity/IdentityAdminEndpoints.cs
                  tests/Husaynia.IntegrationTests/Identity/IdentityMfaAndLockoutTests.cs
                  tests/Husaynia.IntegrationTests/Identity/IdentityMfaAuditTests.cs (new)
    depends_on:   T8
    parallel_ok:  no
    red_tests:    Named taxonomy tests fail for framework/antiforgery denial, validation, invalid code,
                  lockout, key-generation/enable failure, conflict, success, unexpected exception,
                  transaction rollback, session ordering, and sentinel-secret leakage.
    green_tests:  AC-09/AC-10 outcome matrices each persist exactly one record; all fields exclude
                  sentinel OTP/key/password/recovery/token/email/IP values; setup/enable mutations and
                  audits are atomic; cookie follows commit; existing conflict, key, and MFA-satisfied
                  session behavior is preserved.
    commands:     dotnet build src/Husaynia.Web/Husaynia.Web.csproj -c Release -warnaserror
                  dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj -c Release
                  --filter "FullyQualifiedName~IdentityMfaAndLockoutTests|FullyQualifiedName~IdentityMfaAuditTests"
    exit_criteria: AC-09..AC-12 pass with zero failed/skipped; setup/enable never use anonymous limiter;
                  no secret-bearing value appears in audit/log/exception response assertions.
    handoff:      Publish named MFA tests and taxonomy coverage to T12/T13.
    status:       PENDING

T10 Prove compatibility regressions
    owner:        developer
    objective:    Preserve enumeration equivalence, workflows, capability behavior, existing route
                  contracts, and below-threshold behavior after all slices integrate.
    files:        tests/Husaynia.IntegrationTests/Identity/IdentityAnonymousEnumerationTests.cs
                  tests/Husaynia.IntegrationTests/Identity/IdentityAdministrationWorkflowTests.cs
                  tests/Husaynia.IntegrationTests/Identity/IdentityCapabilityMatrixTests.cs
                  tests/Husaynia.IntegrationTests/Identity/IdentityCompatibilityRegressionTests.cs (new)
    depends_on:   T7, T8, T9
    parallel_ok:  no
    red_tests:    Regression tests expose any changed below-threshold status/body, authorization,
                  antiforgery, enumeration, workflow, audit-redaction, or capability result.
    green_tests:  Existing and new compatibility tests pass without weakening assertions; any audit
                  count change is explained by a newly required named action, not relaxed matching.
    commands:     dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj -c Release
                  --filter "FullyQualifiedName~IdentityAnonymousEnumerationTests|FullyQualifiedName~IdentityAdministrationWorkflowTests|FullyQualifiedName~IdentityCapabilityMatrixTests|FullyQualifiedName~IdentityCompatibilityRegressionTests"
    exit_criteria: AC-22 compatibility portion passes with zero failed/skipped and scoped diff is tests only.
    handoff:      Send compatibility test list and counts to T12.
    status:       PENDING

T11 Publish exact T18 schema handoff
    owner:        documentation-specialist
    objective:    Produce the reviewed T18 handoff by copying exact owned names/SQL from the landed
                  invariant and stating migration Up/Down, deployment, rollback, and ownership rules.
    files:        .ai-org/missions/2026-08-21-t04-final-hardening/t18-schema-handoff.md (new)
    depends_on:   T5
    parallel_ok:  yes
    red_tests:    Artifact review fails if any object/column/constraint/index/error/SQL statement differs
                  from `IdentityAuditDatabaseInvariant` or if it implies T04 authors/applies a migration.
    green_tests:  Artifact names exact InstallSql/DownSql, T18-only migration/snapshot/application,
                  removal of duplicate generated CreateTable/CreateIndex/DropTable operations, deploy
                  schema-before-runtime and rollback-runtime-before-Down.
    commands:     git -C .. --no-pager diff --no-index -- /dev/null HusayniaSite/.ai-org/missions/2026-08-21-t04-final-hardening/t18-schema-handoff.md
    exit_criteria: AC-21 artifact is exact, reviewed against landed constants, and contains no migration.
    handoff:      T18 receives only this artifact plus the invariant file and executable fixture tests.
    status:       PENDING

T12 Execute independent verification gate
    owner:        test-engineer
    objective:    Independently map AC-01..AC-22 to named tests/artifacts, execute strict focused/full
                  builds and tests, and run causal suites three consecutive times.
    files:        .ai-org/missions/2026-08-21-t04-final-hardening/test-evidence.md (new)
                  .ai-org/missions/2026-08-21-t04-final-hardening/acceptance-evidence-matrix.md (new)
    depends_on:   T6, T9, T10, T11
    parallel_ok:  yes
    commands:     dotnet build src/Husaynia.Application/Husaynia.Application.csproj -c Release -warnaserror
                  dotnet build src/Husaynia.Infrastructure/Husaynia.Infrastructure.csproj -c Release -warnaserror
                  dotnet build src/Husaynia.Web/Husaynia.Web.csproj -c Release -warnaserror
                  dotnet test tests/Husaynia.Application.Tests/Husaynia.Application.Tests.csproj -c Release
                  --filter FullyQualifiedName~Identity
                  dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj -c Release
                  --filter FullyQualifiedName~Identity
                  dotnet build HusayniaSite.sln -c Release -warnaserror
                  dotnet test HusayniaSite.sln -c Release --no-build
    exit_criteria: Every AC has named evidence; all commands record real pass/fail/skip counts; zero
                  failures/skips; direct-SQL, restart, delayed-writer, timeout, expiry, concurrency,
                  and multi-host causal filters pass three consecutive executions.
    handoff:      Any failure creates a bounded rework task assigned to the owning implementation task;
                  only a clean rerun can mark T12 DONE.
    status:       PENDING

T13 Perform independent security gate
    owner:        security-engineer
    objective:    Threat-model and review the final scoped diff for audit bypass/duplication, secret
                  exposure, spoofing, limiter races/fail-open behavior, SQL safety, and configuration risk.
    files:        .ai-org/missions/2026-08-21-t04-final-hardening/security-review.md (new)
    depends_on:   T9, T10, T11
    parallel_ok:  yes
    exit_criteria: APPROVED with zero unresolved Critical/High findings; exact evidence covers
                  AC-01..AC-21 security/negative requirements and confirms no forbidden file changes.
    handoff:      Findings return to the exact owning task; security gate reruns after remediation.
    status:       PENDING

T14 Perform independent code review
    owner:        code-reviewer
    objective:    Review the exact scoped diff for contract adherence, ownership correctness,
                  transaction/cancellation control flow, SQL/model drift, races, and maintainability.
    files:        .ai-org/missions/2026-08-21-t04-final-hardening/code-review.md (new)
    depends_on:   T9, T10, T11
    parallel_ok:  yes
    exit_criteria: APPROVED with exact file:line evidence and no unresolved correctness or compatibility defect.
    handoff:      Findings return to the exact owning task; review reruns after remediation.
    status:       PENDING

T15 Validate causal user journeys
    owner:        qa-engineer
    objective:    Exercise bootstrap/restart, privileged outcomes, MFA lifecycle, and anonymous
                  throttling as real HTTP/database journeys after independent technical gates pass.
    files:        .ai-org/missions/2026-08-21-t04-final-hardening/qa-evidence.md (new)
    depends_on:   T12, T13, T14
    parallel_ok:  no
    exit_criteria: APPROVED with reproducible commands and observed responses/database state for the
                  four required journeys, including failure/recovery and no secret disclosure.
    handoff:      Any defect returns to the owning implementation task and invalidates affected gates.
    status:       PENDING

T16 Judge Definition of Done
    owner:        engineering-judge
    objective:    Verify every Definition-of-Done item against executed evidence and the final exact diff.
    files:        .ai-org/missions/2026-08-21-t04-final-hardening/final-judgment.md (new)
    depends_on:   T12, T13, T14, T15
    parallel_ok:  no
    exit_criteria: APPROVED only if AC-01..AC-22, scope guard, three-run causal stability, all
                  independent gates, and T18 handoff are proven; otherwise REJECTED with remediation.
    handoff:      VP may declare implementation complete only after APPROVED.
    status:       PENDING

## Execution waves

`wave 1: T1`

`wave 2: T2, T3, T4, T5 (parallel; disjoint files)`

`wave 3: T6, T11 (parallel; T6 consumes schema/fixture, T11 publishes the landed SQL)`

`wave 4: T7`

`wave 5: T8`

`wave 6: T9`

`wave 7: T10`

`wave 8: T12, T13, T14 (parallel independent gates)`

`wave 9: T15`

`wave 10: T16`

Implementation is therefore **partially parallel**, not fully serial. Only wave 2 and the specified
gate/handoff waves fan out. Web integration is deliberately serialized because
`IdentityAdminEndpoints.cs` and `IdentityAdminEndpointModule.cs` are shared collision points and
because anonymous admission ordering must be stable before privileged/MFA audit rewrites.

## Scope validation commands

Run before each task, after each task, and before every gate:

```powershell
git -C .. --no-pager status --short -- `
  HusayniaSite/.ai-org/missions/2026-08-21-t04-final-hardening `
  HusayniaSite/src/Husaynia.Application/Identity `
  HusayniaSite/src/Husaynia.Infrastructure/Identity `
  HusayniaSite/src/Husaynia.Web/Areas/Admin/Identity `
  HusayniaSite/tests/Husaynia.Application.Tests/Identity `
  HusayniaSite/tests/Husaynia.IntegrationTests/Identity
```

Because the subtree is untracked, also compare the actual modified-file list captured by the
orchestrator against each task's allowlist. Presence of any migration, snapshot, project/package,
default/secret, deployment, infrastructure, pipeline, or non-Identity file is a blocking failure.

## Replanning and collision rules

- No two active tasks may edit the same path.
- T7, T8, and T9 are serialized because they all edit `IdentityAdminEndpoints.cs`.
- T7 and T8 are serialized because they both edit `IdentityAdminEndpointModule.cs`.
- T5 lands fixture helper signatures before T6; T6 may consume but not edit `IdentityTestHost.cs`.
- T11 copies landed T5 SQL; it must not draft names independently.
- Validation agents modify evidence artifacts only. A test/code/security failure creates a new
  remediation task with the failing evidence and the original owner's exact file allowlist.
- A frozen interface change blocks dependent waves and requires central plan revision.
- Production limiter thresholds remain intentionally undecided and must not be added to repository defaults.

## Completion

- Implementation T1-T11: DONE.
- Independent tests: PASS (`test-evidence.md`, `acceptance-evidence-matrix.md`).
- Security review: PASS (`security-review.md`).
- Code review: APPROVED (`code-review.md`).
- QA: PASS (`qa-results.md`).
- Final judgment: APPROVED (`final-verdict.md`).
- Rework count: 1 (audit timeout observation race and test-fixture limiter configuration).
