# T2 review remediation architecture

Date: 2026-08-14

## Current state (FACT)

- The product is still a shell: the solution contains `Api`, `Application`, `Domain`, `Infrastructure`, and four test projects; `Api` has no `Program.cs`, and its project falls back to `Library` output when `Program.cs` is absent (`HusayniaTabruk.sln:7-24`, `src/HusayniaTabruk.Api/HusayniaTabruk.Api.csproj:1-9`).
- Global build rules are already frozen at `.NET 10`, nullable, deterministic, and warnings-as-errors (`Directory.Build.props:2-9`); central package management only defines test packages (`Directory.Packages.props:2-10`).
- The repository boundary is intentionally strict: `Domain` has no project references, `Application` references only `Domain`, `Infrastructure` references only `Application`, and `Api` composes `Application` + `Infrastructure` (`src/HusayniaTabruk.Domain/HusayniaTabruk.Domain.csproj:1-2`, `src/HusayniaTabruk.Application/HusayniaTabruk.Application.csproj:1-5`, `src/HusayniaTabruk.Infrastructure/HusayniaTabruk.Infrastructure.csproj:1-5`, `src/HusayniaTabruk.Api/HusayniaTabruk.Api.csproj:1-9`, `README.md:11-17`).
- `IIdempotencyStore` currently exposes only `FindAsync` and `AddAsync`; `IdempotencyReceipt` stores `Operation`, `RequestFingerprint`, `Status`, `ResultReference`, `CreatedAt`, and `ExpiresAt`; `Evaluate` compares only fingerprint + expiry and ignores `Operation` (`src/HusayniaTabruk.Application/Abstractions/Persistence/PersistencePorts.cs:23-31`, `src/HusayniaTabruk.Application/Abstractions/Persistence/PersistencePorts.cs:73-149`).
- Strong IDs are public `record struct`s with `New()`, `From(Guid)`, `Parse`, `TryParse`, `EnsureValid()`, `IsValid`, `ToString()`, and a public `Guid Value` getter that currently exposes `Guid.Empty` on a language-default instance (`src/HusayniaTabruk.Domain/Common/Identifiers/StronglyTypedIds.cs:5-19`, `src/HusayniaTabruk.Domain/Common/Identifiers/StronglyTypedIds.cs:22-34`, `src/HusayniaTabruk.Domain/Common/Identifiers/StronglyTypedIds.cs:202-243`).
- Application port DTOs already reject default IDs when they are passed into constructors (`src/HusayniaTabruk.Application/Abstractions/Persistence/PersistencePorts.cs:86-88`, `src/HusayniaTabruk.Application/Abstractions/Audit/AuditPorts.cs:39-41`, `src/HusayniaTabruk.Application/Abstractions/Messaging/MessagingPorts.cs:24-25`, `src/HusayniaTabruk.Application/Abstractions/Messaging/MessagingPorts.cs:55-56`).
- Current tests freeze the old shapes: `ApplicationPortTests` only checks receipt expiry/fingerprint mismatch and constructor validation, while `ApplicationDependencyTests` parses raw project XML and filters assembly references only to `HusayniaTabruk.*` (`tests/HusayniaTabruk.Application.Tests/Architecture/ApplicationPortTests.cs:63-90`, `tests/HusayniaTabruk.Application.Tests/Architecture/ApplicationDependencyTests.cs:8-63`).
- `DomainDependencyTests` already prove that `dotnet msbuild -getItem:ProjectReference -getItem:PackageReference -getItem:FrameworkReference` can be used in tests to inspect evaluated items, including implicit framework references (`tests/HusayniaTabruk.Domain.Tests/Architecture/DomainDependencyTests.cs:10-18`, `tests/HusayniaTabruk.Domain.Tests/Architecture/DomainDependencyTests.cs:64-97`).
- The signup architecture expects idempotency to live in the application layer, be stored in PostgreSQL with unique actor+key semantics, and surface `409`-class conflicts; application tests are expected to cover idempotency (`.ai-org/missions/2026-08-14-husaynia-tabarruk-signup/architecture.md:47-50`, `.ai-org/missions/2026-08-14-husaynia-tabarruk-signup/architecture.md:120`, `.ai-org/missions/2026-08-14-husaynia-tabarruk-signup/architecture.md:234`, `.ai-org/missions/2026-08-14-husaynia-tabarruk-signup/architecture.md:293`, `.ai-org/missions/2026-08-14-husaynia-tabarruk-signup/decisions.md:26`, `.ai-org/missions/2026-08-14-husaynia-tabarruk-signup/decisions.md:43`).
- This mission is intentionally limited to T2 contracts, shared primitives, and boundary tests; no storage adapter or T3 API behavior is allowed (`.ai-org/missions/2026-08-14-husaynia-t2-review-remediation/mission.md:3-7`, `.ai-org/missions/2026-08-14-husaynia-t2-review-remediation/definition-of-done.md:3-8`, `.ai-org/missions/2026-08-14-husaynia-t2-review-remediation/decisions.md:3-5`).

## Desired state

Keep the modular-monolith boundary and test-only scope, but harden three T2 contracts:

1. `IIdempotencyStore` expresses atomic create / complete / fail operations with typed conflict outcomes.
2. Strong IDs still round-trip canonical UUID strings and UUIDv7 generation, but every public raw-GUID conversion rejects the language-default sentinel.
3. Application dependency tests evaluate resolved MSBuild items, including imported items, and prove each forbidden dependency category is detected.

## Delta

### 1) Idempotency lifecycle contract

Replace the write side of `IIdempotencyStore` with explicit lifecycle methods and keep `FindAsync` as the read-only lookup:

```csharp
public interface IIdempotencyStore
{
    ValueTask<IdempotencyReceipt?> FindAsync(
        OrganizationId organizationId,
        MembershipId membershipId,
        IdempotencyKey key,
        CancellationToken cancellationToken = default);

    ValueTask<IdempotencyCreateResult> TryCreateProcessingAsync(
        IdempotencyCreateRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<IdempotencyTransitionResult> TryCompleteAsync(
        IdempotencyRequest request,
        string resultReference,
        CancellationToken cancellationToken = default);

    ValueTask<IdempotencyTransitionResult> TryFailAsync(
        IdempotencyRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record IdempotencyCreateRequest(
    OrganizationId OrganizationId,
    MembershipId MembershipId,
    IdempotencyKey Key,
    string Operation,
    RequestFingerprint RequestFingerprint,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record IdempotencyRequest(
    OrganizationId OrganizationId,
    MembershipId MembershipId,
    IdempotencyKey Key,
    string Operation,
    RequestFingerprint RequestFingerprint);

public enum IdempotencyCreateOutcome
{
    Created = 0,
    ExistingProcessing = 1,
    ExistingCompleted = 2,
    ExistingFailed = 3,
    RequestMismatch = 4,
    Expired = 5,
}

public sealed record IdempotencyCreateResult(
    IdempotencyCreateOutcome Outcome,
    IdempotencyReceipt Receipt);

public enum IdempotencyTransitionOutcome
{
    Completed = 0,
    Failed = 1,
    Missing = 2,
    RequestMismatch = 3,
    ExpectedStatusMismatch = 4,
}

public sealed record IdempotencyTransitionResult(
    IdempotencyTransitionOutcome Outcome,
    IdempotencyReceipt? Receipt);
```

Required semantics:

- `TryCreateProcessingAsync` is the only create path. It must create only a `Processing` receipt and must never overwrite an existing actor+key row.
- `TryCompleteAsync` and `TryFailAsync` are compare-and-set transitions from **expected status = `Processing`** to the named terminal state.
- Duplicate and race conditions are normal business outcomes, not exceptions:
  - second create on same actor+key + same operation/fingerprint returns `ExistingProcessing`, `ExistingCompleted`, or `ExistingFailed` with the stored receipt;
  - same actor+key + different operation or fingerprint returns `RequestMismatch` with the stored receipt;
  - late terminal writes after another terminal write return `ExpectedStatusMismatch` with the winner’s stored receipt;
  - missing receipts return `Missing` with `Receipt = null`.
- Exceptions remain only for invalid arguments/default IDs/cancellation.

Update `IdempotencyReceipt.Evaluate` so request identity includes both operation and fingerprint:

```csharp
public IdempotencyReceiptDisposition Evaluate(
    string operation,
    RequestFingerprint requestFingerprint,
    DateTimeOffset evaluatedAt)
```

`RequestMismatch` must mean **operation mismatch OR fingerprint mismatch**. Keep expiry semantics and current status-based outcomes.

No storage adapter, schema, or API mapping is added in T2.

### 2) Strong-ID raw GUID access

Do **not** introduce a separate `ToGuid()` API in T2. Keep the public accessor name `Value`, but change its semantics:

- each ID gets a private backing field;
- `public Guid Value` becomes a validated getter that throws `InvalidOperationException` on the language-default instance;
- `IsValid` stays side-effect free;
- `From(Guid)`, `Parse`, `TryParse`, `EnsureValid()`, `ToString()`, and `New()` keep their current UUIDv7/canonical-string behavior.

This is the smallest change that closes the leak because the only verified `Value` callers are tests (`tests/HusayniaTabruk.Domain.Tests/Architecture/SharedPrimitiveTests.cs:11-27`, `tests/HusayniaTabruk.Domain.Tests/Architecture/IndependentT2BoundaryTests.cs:33-67`).

### 3) Application dependency guard

Replace raw XML inspection in `ApplicationDependencyTests` with the same evaluated-MSBuild strategy already used by `DomainDependencyTests`:

- allowed `ProjectReference` set for `Application`: `HusayniaTabruk.Domain` only;
- allowed `PackageReference` set: empty;
- allowed `FrameworkReference` set: `Microsoft.NETCore.App` only.

Also widen the assembly-level backstop so `Application` explicitly rejects forbidden runtime dependencies, not just non-domain product references:

- forbid `Microsoft.AspNetCore*`
- forbid `Microsoft.EntityFrameworkCore*`
- forbid `Microsoft.Extensions.Identity*`
- forbid `Npgsql*`
- forbid `System.Net.Http*`
- forbid `HusayniaTabruk.Infrastructure` and `HusayniaTabruk.Api`

Use the evaluated item metadata in failure messages, especially `DefiningProjectFullPath`, so imported `.props`/`.targets` sources are visible when a test fails.

## File-level implementation plan

- `src/HusayniaTabruk.Application/Abstractions/Persistence/PersistencePorts.cs`
  - replace `AddAsync` with explicit create/complete/fail methods;
  - add `IdempotencyCreateRequest`, `IdempotencyRequest`, `IdempotencyCreateOutcome`, `IdempotencyCreateResult`, `IdempotencyTransitionOutcome`, `IdempotencyTransitionResult`;
  - update `IdempotencyReceipt.Evaluate` to include `operation`.
- `src/HusayniaTabruk.Domain/Common/Identifiers/StronglyTypedIds.cs`
  - switch every ID to a private `Guid` backing field plus validated `Value` getter;
  - keep UUIDv7, canonical parsing, `IsValid`, and `EnsureValid`.
- `tests/HusayniaTabruk.Application.Tests/Architecture/ApplicationPortTests.cs`
  - update receipt evaluation assertions for operation + fingerprint mismatch;
  - assert new lifecycle DTO invariants and constructor rules.
- `tests/HusayniaTabruk.Application.Tests/Architecture/IndependentT2PortShapeTests.cs`
  - freeze the new `IIdempotencyStore` method names, parameter types, and result types.
- `tests/HusayniaTabruk.Application.Tests/Architecture/IdempotencyStoreContractTests.cs` (new)
  - add a private in-test probe implementation only for contract verification;
  - cover duplicate create, request mismatch, terminal race, missing transition, and invalid terminal transition outcomes.
- `tests/HusayniaTabruk.Application.Tests/Architecture/ApplicationDependencyTests.cs`
  - replace XML parsing with evaluated MSBuild item inspection;
  - switch production project-reference checks to the same evaluated reader;
  - add three mutation probes using temporary projects + imported props/targets for forbidden `ProjectReference`, `PackageReference`, and `FrameworkReference`.
- `tests/HusayniaTabruk.Domain.Tests/Architecture/SharedPrimitiveTests.cs`
  - add `default(TId).Value` rejection for at least one representative ID while preserving existing UUIDv7/canonical round-trip checks.
- `tests/HusayniaTabruk.Domain.Tests/Architecture/IndependentT2BoundaryTests.cs`
  - assert every strong ID throws from public raw-GUID access on the default instance.

## Test and mutation strategy

- **Idempotency contract tests**
  - create same actor+key twice with same operation/fingerprint: `Created`, then `ExistingProcessing`;
  - complete the receipt, then create same actor+key again: `ExistingCompleted`;
  - create same actor+key with different operation or fingerprint: `RequestMismatch`;
  - race `TryCompleteAsync` vs `TryFailAsync` on one processing receipt: exactly one terminal success, one `ExpectedStatusMismatch`;
  - call a terminal transition again after terminal state: `ExpectedStatusMismatch`;
  - call a terminal transition before create: `Missing`.
- **Strong-ID tests**
  - keep UUIDv7 version checks and canonical string parsing;
  - add default-instance rejection on `Value`, `EnsureValid()`, and `ToString()` for every strong ID.
- **Dependency mutation probes**
  - temp project with imported forbidden `ProjectReference` must fail;
  - temp project with imported forbidden `PackageReference` must fail;
  - temp project with imported forbidden `FrameworkReference` (for example `Microsoft.AspNetCore.App`) must fail.

## Risks and mitigations

- **Source compatibility risk inside the solution**: `IIdempotencyStore` changes are breaking for any future adapter/caller. Mitigation: no production adapter exists in-repo today, so change the contract now before T3.
- **Behavior drift between the test probe and a future real store**: keep the probe tiny and outcome-focused; when Infrastructure is built, its tests must reuse the same cases.
- **MSBuild test fragility**: these tests already work in `DomainDependencyTests`; reuse the same `dotnet msbuild -getItem` pattern and keep the temp projects minimal.

## Migration / rollback

- No data migration, schema migration, deployment change, or configuration change is part of T2.
- Forward path: land the contract/test changes, then let T3 adapters implement against the hardened contracts.
- Rollback: revert the T2 contract/test changes before any storage implementation is introduced.

## Compatibility note

These are **code-level** contract changes, not wire-contract changes. The only verified in-repo `Value` callers are tests, and no production `IIdempotencyStore` implementation exists yet, so the blast radius is limited to the current solution.
