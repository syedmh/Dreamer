# T04 Final Identity Hardening Architecture

Date: 2026-08-21  
Status: Accepted for implementation

## 1. Current state

- **FACT:** `Program.cs` validates configuration before composing modules, then maps module
  endpoints; there is no Identity-specific startup schema step
  (`src/Husaynia.Web/Program.cs:3-16`).
- **FACT:** the Identity Infrastructure module owns the SQL Server `HusayniaIdentityDbContext`,
  ASP.NET Core Identity stores, `IAuditWriter`, administration store/service, options, and
  configuration validation (`src/Husaynia.Infrastructure/Identity/IdentityModule.cs:13-78`,
  `81-181`).
- **FACT:** the Web module owns cookies, authorization policies, antiforgery, the authorization
  result handler, bootstrap hosted service, and endpoint mapping
  (`src/Husaynia.Web/Areas/Admin/Identity/IdentityAdminEndpointModule.cs:15-76`).
- **FACT:** anonymous login, invitation acceptance, MFA setup, and MFA enable are mapped under
  `/admin/identity`; only capability/audit/user-administration routes currently carry authorization
  metadata (`src/Husaynia.Web/Areas/Admin/Identity/IdentityAdminEndpoints.cs:38-87`).
- **FACT:** login performs account lookup, password verification, MFA verification, and lockout
  mutation in the endpoint; invitation acceptance similarly performs token validation and mutation
  there (`IdentityAdminEndpoints.cs:146-269`, `291-388`).
- **FACT:** MFA setup/enable manually authenticate after antiforgery and do not receive an audit
  writer (`IdentityAdminEndpoints.cs:417-554`).
- **FACT:** framework denials are audited in the authorization middleware result handler, while
  endpoint entry checks, antiforgery helpers, Application service, and Infrastructure store each
  write audits independently (`IdentityAuthorizationMiddlewareResultHandler.cs:22-86`;
  `IdentityAdminEndpoints.cs:897-1089`;
  `src/Husaynia.Application/Identity/IdentityAdministrationService.cs:235-296`;
  `src/Husaynia.Infrastructure/Identity/IdentityAdministrationStore.cs:122-134,591-609`).
- **FACT:** only selected Web denial/anonymous audits receive a shutdown-linked five-second token;
  Application and store audits receive the request token
  (`IdentityAdminEndpoints.cs:1052-1089`;
  `IdentityAdministrationService.cs:235-296`).
- **FACT:** EF rejects audit-event and bootstrap-seal update/delete, but database SQL protects only
  the audit table (`src/Husaynia.Infrastructure/Identity/HusayniaIdentityDbContext.cs:9-37`;
  `src/Husaynia.Infrastructure/Identity/IdentityAuditDatabaseInvariant.cs:3-27`).
- **FACT:** bootstrap uses a serializable transaction and `UPDLOCK,HOLDLOCK` on seal `Id = 1`, then
  persists user/role changes, two audit rows, and the seal before commit
  (`src/Husaynia.Web/Areas/Admin/Identity/IdentityBootstrapSeeder.cs:36-65,67-136`).
- **FACT:** Identity entity configurations are discovered from the Infrastructure assembly, so a
  new Identity entity configuration will participate in both migrations and `EnsureCreated`
  (`src/Husaynia.Infrastructure/Persistence/Core/HusayniaDbContext.cs:18-35`;
  `src/Husaynia.Infrastructure/Identity/HusayniaIdentityDbContext.cs:6-8`).
- **FACT:** the LocalDB fixture calls `EnsureCreated` and explicitly executes `InstallSql`; startup
  tests already reject trigger DDL (`tests/Husaynia.IntegrationTests/Identity/IdentityTestHost.cs:28-68`;
  `tests/Husaynia.IntegrationTests/Identity/IdentityBootstrapAndConfigurationTests.cs:159-207`).
- **FACT:** no `UseForwardedHeaders`, forwarded-header trust configuration, or DB-backed Identity
  limiter exists in the inspected scoped production code.
- **FACT:** Application depends only on Domain and configuration/DI abstractions; Infrastructure
  depends on Application/Domain and existing EF Core/Identity packages; Web already depends on
  Application and Infrastructure (`src/Husaynia.Application/Husaynia.Application.csproj:1-10`;
  `src/Husaynia.Infrastructure/Husaynia.Infrastructure.csproj:1-15`;
  `src/Husaynia.Web/Husaynia.Web.csproj:1-7`). No dependency change is needed.

## 2. Desired state and boundaries

Keep the existing three-layer shape and add only Identity-owned components:

1. **Web admission:** validate antiforgery, derive a trusted client fingerprint, invoke the
   anonymous limiter before body/account/token/password/OTP processing, and map 429.
2. **Web privileged finalization:** one scoped `IIdentityAuditFinalizer` creates the independent
   audit token, enforces one attempt per `(correlationId, action)`, and calls the existing writer.
3. **Application/store ownership:** existing authorization/validation/business boundaries continue
   to select outcomes, but call the finalizer rather than `IAuditWriter` directly.
4. **Infrastructure persistence:** one SQL Server rate-limit store serializes each partition, and
   EF configuration describes the T18-owned table.
5. **Database invariants:** deterministic `InstallSql`/`DownSql` own both triggers and the limiter
   table; only T18 or explicit integration-fixture setup executes them.

No new package, service, cache, queue, database, migration, runtime DDL, route, or response schema is
introduced.

## 3. Contracts

Add compatible internal Identity contracts in
`src/Husaynia.Application/Identity/IdentityAdministrationContracts.cs`:

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
```

`FinalizeOnceAsync` deliberately has no request cancellation argument. Its implementation:

- is **scoped**;
- atomically claims `(descriptor.CorrelationId, descriptor.Action)` before calling the writer;
- creates a token linked only to `IHostApplicationLifetime.ApplicationStopping`;
- calls `CancelAfter(TimeSpan.FromSeconds(5))`;
- calls `IAuditWriter.AppendAsync`, then `ThrowIfCancellationRequested`, then the optional
  `completePersistenceAsync` with the same token;
- never retries;
- throws a sanitized `IdentityAuditFinalizationException` on timeout/writer failure;
- treats a second claim as an implementation error and performs no second write.

The optional callback is for transaction commit after the audit row has been saved in the same
DbContext transaction. This preserves the existing mutation-plus-audit atomicity. If audit
finalization fails, the transaction is disposed/rolled back and a success is never returned.

`IAuditWriter` remains the persistence primitive and remains source-compatible. Production
privileged callers stop invoking it directly; bootstrap and anonymous failure auditing are outside
the privileged finalizer policy and may continue using the writer.

### HTTP compatibility and errors

- Existing successful and below-threshold route/status/body contracts remain unchanged.
- Throttled login/invitation requests return status `429`, existing `FailureResponse` JSON with
  code `rate_limited`, message `Too many attempts. Try again later.`, and an integer
  `Retry-After` header equal to `max(1, ceil(WindowEndsAtUtc-now))`.
- The 429 result is selected before JSON parsing or Identity lookup. Variations in email, account
  state, token, password, or OTP therefore cannot alter status, body shape, or headers.
- An audit finalization failure maps to `503` with code `identity_audit_unavailable` and a constant
  message. Writer exceptions, SQL text, secrets, and inner exception messages are never returned.
- Existing `PrivilegedAttemptOutcome.Allowed/Denied` values are retained for compatibility.
  Stable result/error taxonomy is carried in sanitized `DetailJson`.

## 4. Exactly-once audit ownership

The owner is the first layer that conclusively selects the request outcome:

| Outcome | Sole audit owner |
|---|---|
| Framework authentication/authorization denial | `IdentityAuthorizationMiddlewareResultHandler` |
| Privileged antiforgery or malformed transport input | endpoint helper/handler |
| Application capability denial or command validation failure | `IdentityAdministrationService` |
| Store conflict, dependency/business failure, success, or pre-finalization exception | `IdentityAdministrationStore` |
| MFA setup/enable after framework admission | MFA endpoint handler |
| Exception escaping before any owner finalizes | privileged endpoint exception filter/wrapper |

Remove the redundant successful endpoint capability pre-check, or make it non-auditing; the
Application service remains the authoritative second authorization check required by ADR-004.
Framework-denied requests never enter the endpoint. The scoped finalizer gate is a backstop, not the
normal control flow.

For store mutations, stage mutation and audit in the existing transaction and pass
`token => transaction.CommitAsync(token)` as `completePersistenceAsync`. Rollback uses
`CancellationToken.None`, as it does today. Reads and validation paths omit the callback.
Catch blocks must not catch `IdentityAuditFinalizationException` as a dependency/business failure,
because doing so would request a second audit.

For post-commit HTTP failures (for example, cookie serialization after successful MFA enable), the
already-persisted audit remains the single authoritative Identity-state outcome; no second
“exception” audit is emitted.

## 5. MFA design

Add an internal `IdentityMfaEnrollment` authorization policy requiring authentication and one of
the six privileged roles, but not an already-satisfied MFA claim. Attach it and privileged audit
metadata to both POST MFA routes. This gives framework 401/403 auditing while still permitting a
privileged user to enroll MFA.

MFA handlers use `IIdentityAuditFinalizer` and constant-only metadata:

| Action | Authorization outcome | `details.result` | `details.errorCode` when applicable |
|---|---|---|---|
| `identity.mfa.setup` | denied | `denied` | `unauthenticated`, `forbidden`, `antiforgery_required` |
| setup | allowed | `key_ready` | none; add `keyCreated=true|false` |
| setup | allowed | `conflict` | `mfa_already_enabled` |
| setup | allowed | `failed` | `authenticator_key_generation_failed` |
| setup | allowed | `exception` | `unexpected_failure` |
| `identity.mfa.enable` | denied | `denied` | framework/antiforgery codes |
| enable | allowed | `validation_failed` | `missing_two_factor_code` or `malformed_two_factor_code` |
| enable | allowed | `invalid_code` | `invalid_two_factor_code` |
| enable | allowed | `locked_out` | `locked_out` |
| enable | allowed | `failed` | `mfa_enable_failed` |
| enable | allowed | `enabled` | none |
| enable | allowed | `exception` | `unexpected_failure` |

Actor and target are the authenticated user GUID; correlation ID and roles use the existing
descriptor. Details must never contain request values. The shared key may appear only in the
successful setup response; OTP, key, password, recovery material, invitation token, raw email, and
raw IP never enter descriptors, details, logs, or exceptions.

Setup key creation and its audit use one DB transaction. Enable mutation, success audit, and commit
use one DB transaction; the MFA-satisfied cookie is issued only after commit. Invalid enable codes
use the existing Identity access-failure/lockout behavior used by login, and successful enable
resets the access-failure count. Already-enabled setup remains 409, never reads/returns/resets the
established key, and never downgrades an MFA-satisfied session. Antiforgery failure occurs before key
access.

## 6. Anonymous limiter and trusted fingerprint

### Configuration

Extend `IdentityModuleOptions` with required `AnonymousRateLimit` values:

- `Identity:AnonymousRateLimit:PermitLimit`: integer `1..1000`;
- `Identity:AnonymousRateLimit:Window`: TimeSpan `> 0` and `<= 01:00:00`;
- `Identity:AnonymousRateLimit:Retention`: TimeSpan `>= Window` and `<= 30.00:00:00`;
- `Identity:AnonymousRateLimit:FingerprintKey`: base64 for at least 32 random bytes.

Missing, malformed, zero, negative, overflow, or out-of-policy values fail existing startup
configuration validation. T04 changes no default/secret file: production supplies these through its
existing external configuration mechanism; tests supply in-memory values.

### Fingerprint

Web reads only `HttpContext.Connection.RemoteIpAddress`. IPv4-mapped IPv6 is normalized to IPv4,
then address bytes are HMAC-SHA-256 hashed with `FingerprintKey`. A null address uses a fixed
`unknown` input, still HMACed. The resulting 32 bytes are the only client identifier passed to or
stored by Infrastructure.

Do not read `X-Forwarded-For`, `Forwarded`, or similar headers and do not add
`UseForwardedHeaders`. This is correct for the repository's current trust boundary. A later proxy
trust change must be a separate reviewed architecture change.

Endpoint families are constant values `login` and `invitation-accept`; family and fingerprint form
the partition. Login's password and MFA challenge share the `login` allowance. Authenticated MFA
setup/enable do not invoke the anonymous limiter.

### Atomic algorithm

`SqlIdentityAnonymousRateLimiter` uses the existing scoped DbContext and `TimeProvider`:

1. Begin a SQL Server `Serializable` transaction with the request token.
2. Select the partition through the unique endpoint/fingerprint index using
   `WITH (UPDLOCK, HOLDLOCK)`.
3. If absent, insert count `1`, window `[now, now+W)`, and
   `RetainUntilUtc = WindowEndsAtUtc + Retention`; allow.
4. If `now >= WindowEndsAtUtc`, reset count to `1` and the timestamps; allow.
5. If `RequestCount < N`, increment once; allow.
6. Otherwise do not mutate Identity state; deny and return the stored window end.
7. Commit. On SQL 2601/2627 or deadlock 1205, roll back and retry the whole transaction once with
   the same `now`; any further failure propagates as a generic dependency failure.

The serializable key-range lock makes absent-row creation and increments global across hosts, so at
most `N` calls can pass. The limiter is invoked after antiforgery but before body parsing,
user/token lookup, password/OTP verification, or access-failure mutation. A denied decision returns
immediately, preventing lockout amplification.

Expired windows are non-counting immediately at `WindowEndsAtUtc`. After a decision commits, a
best-effort parameterized `DELETE TOP (32)` removes rows with `RetainUntilUtc <= now`, using the
expiry index. Cleanup failure is logged without identifiers and cannot make an expired row count
again; subsequent requests retry cleanup. No background service or runtime DDL is added.

## 7. Data model and exact T18 handoff

Owned names:

- table: `dbo.IdentityAnonymousRateLimits`;
- primary key: `PK_IdentityAnonymousRateLimits`;
- unique partition constraint:
  `UQ_IdentityAnonymousRateLimits_EndpointFamily_ClientFingerprint`;
- expiry index: `IX_IdentityAnonymousRateLimits_RetainUntilUtc`;
- checks: `CK_IdentityAnonymousRateLimits_RequestCount`,
  `CK_IdentityAnonymousRateLimits_Window`, `CK_IdentityAnonymousRateLimits_Retention`;
- audit trigger: `dbo.TR_IdentityAuditEvents_AppendOnly` (existing);
- seal trigger: `dbo.TR_IdentityBootstrapState_PermanentSeal`;
- SQL errors: 51004 for audit mutation, 51005 for seal mutation.

Entity columns:

| Column | SQL type | Rule |
|---|---|---|
| `Id` | `bigint IDENTITY(1,1)` | clustered PK |
| `EndpointFamily` | `nvarchar(32)` | required |
| `ClientFingerprint` | `binary(32)` | required |
| `WindowStartedAtUtc` | `datetimeoffset(7)` | required |
| `WindowEndsAtUtc` | `datetimeoffset(7)` | required, greater than start |
| `RequestCount` | `int` | required, greater than zero |
| `RetainUntilUtc` | `datetimeoffset(7)` | required, not before window end |

`IdentityAuditDatabaseInvariant.InstallSql` is extended to the following idempotent handoff SQL
(dynamic execution keeps trigger creation valid in one executable command):

```sql
SET XACT_ABORT ON;

IF OBJECT_ID(N'[dbo].[IdentityAnonymousRateLimits]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[IdentityAnonymousRateLimits]
    (
        [Id] bigint IDENTITY(1,1) NOT NULL,
        [EndpointFamily] nvarchar(32) NOT NULL,
        [ClientFingerprint] binary(32) NOT NULL,
        [WindowStartedAtUtc] datetimeoffset(7) NOT NULL,
        [WindowEndsAtUtc] datetimeoffset(7) NOT NULL,
        [RequestCount] int NOT NULL,
        [RetainUntilUtc] datetimeoffset(7) NOT NULL,
        CONSTRAINT [PK_IdentityAnonymousRateLimits]
            PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [UQ_IdentityAnonymousRateLimits_EndpointFamily_ClientFingerprint]
            UNIQUE ([EndpointFamily], [ClientFingerprint]),
        CONSTRAINT [CK_IdentityAnonymousRateLimits_RequestCount]
            CHECK ([RequestCount] > 0),
        CONSTRAINT [CK_IdentityAnonymousRateLimits_Window]
            CHECK ([WindowEndsAtUtc] > [WindowStartedAtUtc]),
        CONSTRAINT [CK_IdentityAnonymousRateLimits_Retention]
            CHECK ([RetainUntilUtc] >= [WindowEndsAtUtc])
    );
END;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_IdentityAnonymousRateLimits_RetainUntilUtc'
      AND [object_id] = OBJECT_ID(N'[dbo].[IdentityAnonymousRateLimits]')
)
BEGIN
    CREATE INDEX [IX_IdentityAnonymousRateLimits_RetainUntilUtc]
        ON [dbo].[IdentityAnonymousRateLimits] ([RetainUntilUtc]);
END;

EXEC(N'
CREATE OR ALTER TRIGGER [dbo].[TR_IdentityAuditEvents_AppendOnly]
ON [dbo].[IdentityAuditEvents]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51004, ''Identity audit events are append-only and cannot be updated or deleted.'', 1;
END');

EXEC(N'
CREATE OR ALTER TRIGGER [dbo].[TR_IdentityBootstrapState_PermanentSeal]
ON [dbo].[IdentityBootstrapState]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted WHERE [Id] = 1)
    BEGIN
        THROW 51005, ''The identity bootstrap seal is permanent and cannot be updated or deleted.'', 1;
    END;
END');
```

The seal trigger is intentionally restricted to rows present in `deleted` with `[Id] = 1`.
SQL Server places the pre-update row in `deleted`, so this rejects changing any column of the
permanent seal, changing its key away from `1`, or deleting it. Statements affecting only other
rows are not rejected. This is the least-privilege database invariant for the domain singleton
defined by `IdentityBootstrapState.PermanentSealId`; a table-wide rejection would exceed AC-01's
known-row scope without adding protection to the authoritative seal.

`DownSql` is:

```sql
SET XACT_ABORT ON;
DROP TRIGGER IF EXISTS [dbo].[TR_IdentityBootstrapState_PermanentSeal];
DROP TRIGGER IF EXISTS [dbo].[TR_IdentityAuditEvents_AppendOnly];
DROP TABLE IF EXISTS [dbo].[IdentityAnonymousRateLimits];
```

T04 owns these constants, entity/configuration, and executable fixture coverage. **T18 exclusively
owns creating the EF migration/snapshot and applying the SQL in migration Up/Down.** Application
startup must not reference or execute either SQL constant. The limiter table is ephemeral security
state, so Down resets allowances but destroys no account/audit/business record.

### Migration and rollback sequence

T04 adds no migration or snapshot. T18 must generate the model snapshot from the supplied entity
configuration, then make the migration use the constants as the sole operations for these owned
objects:

1. Migration Up executes the full `InstallSql`; remove/suppress any separately generated
   `CreateTable/CreateIndex` operations for `IdentityAnonymousRateLimits`.
2. Migration Down executes the full `DownSql`; remove/suppress any separately generated
   `DropTable` operation for that table.
3. Deploy schema before deploying T04 runtime code. Old runtime is compatible with the added
   table/trigger. New runtime intentionally fails closed if the table is absent.
4. Rollback runtime before migration Down. Down removes the limiter table and triggers; account,
   seal, and audit rows remain. Reinstall is safe and starts limiter counts from zero.

This is an additive, backward-compatible forward migration. The only reset on rollback is
ephemeral rate-limit allowance state.

## 8. Failure, concurrency, observability, and security

- **Database unavailable:** limiter fails closed with generic 503; credential/token/OTP verification
  does not run. Privileged audit failure also returns generic 503; transactional mutations roll
  back.
- **Client disconnect:** ordinary operation work may cancel until an outcome is selected. Audit
  finalization then ignores `RequestAborted` and is bounded by shutdown/five seconds.
- **Application shutdown:** the finalizer token cancels; no retry occurs.
- **Duplicate audit call:** the scoped claim blocks the second writer call and surfaces an
  implementation error.
- **Limiter contention/deadlock:** one bounded retry; no optimistic increment and no fail-open path.
- **Cleanup failure:** rows remain storage-only but are non-counting after window expiry.
- **Logs:** finalizer/limiter may log action/family, correlation ID, exception type/category, and
  elapsed time. Never log request bodies, raw IP/email, hashes/fingerprints, keys, OTPs, passwords,
  tokens, or writer exception messages.
- **Trust boundaries:** Web owns untrusted HTTP/header/input handling; Application owns capability
  and command rules; Infrastructure owns SQL atomicity; T18 owns schema deployment.

## 9. Testing seams and required evidence

- Override `IIdentityAuditFinalizer`, `IAuditWriter`, `IIdentityAnonymousRateLimiter`,
  `TimeProvider`, and Web fingerprint provider in the existing factory.
- Extend `IdentitySqlServerTestDatabase.CreateAsync` to execute the full `InstallSql`; add explicit
  install/down/reinstall helpers. Never execute it from application startup.
- Add causal LocalDB tests for direct seal UPDATE/DELETE with before/after row comparison, restart,
  and unchanged bootstrap audit counts.
- Add an outcome matrix covering every privileged route and framework denial, antiforgery,
  transport validation, Application validation, store conflict/failure, exception, and success.
  Delayed/throwing/non-completing writers assert one attempt and token behavior.
- Add MFA setup/enable taxonomy and sentinel-secret assertions across every persisted field.
- Add barrier-synchronized two-factory limiter tests at `N`/`N+1`, expiry, partition isolation,
  spoofed headers, uniform 429, and unchanged access-failure/lockout state.
- Configuration tests supply all valid in-memory values and independently reject each invalid or
  missing value.
- Expand the no-runtime-DDL interceptor to reject CREATE/ALTER/DROP of either trigger, the limiter
  table, constraints, or index during ordinary and bootstrap-enabled startup.
- Preserve existing enumeration, MFA secrecy, capability, bootstrap concurrency, and workflow
  tests. LocalDB transport failures are not accepted as product success; required suites must pass
  with zero failures/skips and the causal concurrency suite three consecutive times.

## 10. File impact

Production changes are restricted to:

- `src/Husaynia.Application/Identity/IdentityAdministrationContracts.cs`
- `src/Husaynia.Application/Identity/IdentityAdministrationService.cs`
- `src/Husaynia.Infrastructure/Identity/IdentityModule.cs`
- `src/Husaynia.Infrastructure/Identity/IdentityPersistence.cs`
- `src/Husaynia.Infrastructure/Identity/IdentityAuditDatabaseInvariant.cs`
- `src/Husaynia.Infrastructure/Identity/IdentityAdministrationStore.cs`
- new Identity-only Infrastructure limiter file(s)
- `src/Husaynia.Web/Areas/Admin/Identity/IdentityAdminEndpointModule.cs`
- `src/Husaynia.Web/Areas/Admin/Identity/IdentityAdminEndpoints.cs`
- `src/Husaynia.Web/Areas/Admin/Identity/IdentityAuthorizationMiddlewareResultHandler.cs`
- new Identity-only Web audit-finalizer/fingerprint/filter file(s)

Tests are restricted to existing/new files under
`tests/Husaynia.Application.Tests/Identity` and
`tests/Husaynia.IntegrationTests/Identity`. No migration, snapshot, project/package, default,
secret, deployment, infrastructure, or non-Identity file is changed.

## 11. Tradeoffs and risks

- A SQL fixed window can produce boundary bursts; it is chosen because it is deterministic,
  atomic, dependency-free, and directly satisfies the frozen requirement. Sliding-window logs and
  token buckets add rows/complexity without an acceptance benefit.
- Per-partition serializable locking trades throughput for strict global `N`; the protected
  anonymous admin surface and small configured limits make this the correct priority.
- A keyed HMAC requires an externally supplied shared secret; unkeyed IP hashing was rejected
  because IPv4 values are cheaply reversible, and ASP.NET Data Protection was rejected because a
  shared key ring is not verified in this repository.
- Opportunistic cleanup avoids a new hosted worker. Storage may outlive the nominal window when
  traffic stops, but rows become non-counting immediately and contain only keyed fingerprints.
- The scoped exactly-once gate is process/request local, not a database uniqueness constraint.
  That is intentional: duplicate HTTP submissions are distinct attempts; duplicate layer writes
  within one request are the defect being prevented.

No CTO decision is required: there is no breaking public API/schema contract, destructive business
data migration, new external service, material infrastructure cost, or accepted security/reliability
tradeoff. T18's migration remains the already-established schema delivery boundary.
