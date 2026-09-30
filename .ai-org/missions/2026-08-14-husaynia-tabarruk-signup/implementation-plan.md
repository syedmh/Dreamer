# Tabruk MVP Implementation Plan

Date: 2026-08-14  
Status: READY FOR DISPATCH AFTER FOCUSED SECURITY RE-REVIEW  
Scope: MVP coordination only (R-1 through R-19, AC-1 through AC-18)

## 1. Delivery strategy

Build one thin, production-shaped vertical slice first:

> An invited/active member signs in, sees an open service date/help need, submits an individual signup, sees `Pending`, and the managing Food Incharge sees the same pending request in a privacy-safe roster.

The slice must use the real PostgreSQL transaction, bearer authentication, record authorization, OpenAPI-generated mobile client, idempotency key, audit row, in-app notification seam, and accessible mobile UI. It must not use an in-memory production repository or hand-written mobile transport.

After that slice, extend the same aggregates and contracts to approval/waitlist, cancellation/reassignment, threads, notifications, offline behavior, and acceptance hardening.

## 2. Frozen interfaces

These contracts are fixed before parallel implementation. A change requires stopping affected waves and updating this plan, OpenAPI compatibility tests, and all consumers together.

### 2.1 Component boundaries

- `Domain` references no framework, EF, HTTP, Identity, Expo, or provider package.
- `Application` references only `Domain`.
- `Infrastructure` implements `Application` ports and owns EF Core, PostgreSQL, Identity, token persistence, clock, local push, and outbox leasing.
- `Api` composes dependencies, owns HTTP/auth middleware and OpenAPI, and contains no workflow rules.
- Mobile routes call feature use-cases; feature use-cases call only the generated client and mobile core ports.
- Tests may reference production assemblies; production assemblies never reference tests.

### 2.2 Shared primitives and semantics

- IDs are opaque UUID strings on the wire and strongly typed IDs in the domain.
- Timestamps are ISO-8601 UTC on the wire; organization rules use an IANA time-zone identifier.
- JSON is camelCase. Lists return `{ items, nextCursor }`.
- Write retries use `Idempotency-Key`; message send also carries the same `clientMessageId`.
- Optimistic writes use `If-Match`; stale versions return `412 stale_version`.
- Record-specific concealed reads return `404`; known forbidden collection/actions return `403`.
- Errors are RFC 9457 problem details with `type`, `title`, `status`, `code`, `detail`, `traceId`, and optional `fieldErrors`.
- Stable conflict codes: `signup_duplicate`, `category_closed`, `capacity_unavailable`, `cancellation_deadline_passed`, `invalid_transition`, `signup_chronology_invalid`, `stale_version`.
- Stable signup validation codes additionally include `invalid_signup_aggregate_state` and `signup_not_owned`.
- Oversized input returns `413 payload_too_large`. Rate exhaustion returns `429 rate_limited` with integer `Retry-After`; rejected writes create no notification, audit transition, or outbox work.
- Cursor pages default to 50 and cap at 100. Payload and account/organization rate limits are exactly those in `architecture.md` section 6 and may not be relaxed without security review.

### 2.3 Domain contracts

Enums:

- `MembershipStatus`: `Invited | Active | Disabled`
- `OrganizationRole`: `Admin | FoodIncharge`
- `AdministratorBootstrapStatus`: `Unsealed | Sealed`
- `AdministratorRoleChangeAction`: `Grant | Revoke` (undefined numeric values are invalid)
- `PrivilegedPermission`: `PrivilegedThreadRead` (authorization policy derived from active Admin plus the privileged endpoint; not an ordinary thread entitlement)
- `ServiceDateStatus`: `Draft | Open | Closed | Cancelled | Completed`
- `HelpCategory`: `FoodPreparation | Serving | Cleanup`
- `HelpNeedStatus`: `Open | Closed`
- `SignupKind`: `Individual | Household | Team`
- `SignupStatus`: `Pending | Approved | Waitlisted | Declined | Withdrawn | Cancelled`
- `ThreadStatus`: `Open | Locked`
- `MessageVisibility`: `Visible | Hidden`

Required domain operations:

```text
ServiceDate.Create(...)
ServiceDate.AddNeed(...)
ServiceDate.Open(...)
ServiceDate.Close(...)
ServiceDate.Cancel(...)
HelpNeed.Change(...)
HelpNeedSignups.Rehydrate(...)
HelpNeedSignups.Submit(...)
HelpNeedSignups.Approve(...)
HelpNeedSignups.Decline(...)
HelpNeedSignups.Waitlist(...)
HelpNeedSignups.Withdraw(...)
HelpNeedSignups.Override(...)
HelpNeedSignups.Cancel(...)
HelpNeedSignups.Reassign(...)
HelpNeedSignups.OrderedWaitlist()
DateThread.Post(...)
DateThread.Hide(...)
DateThread.Lock(...)
OrganizationAccountGovernance.Rehydrate(...)
OrganizationAccountGovernance.BootstrapAdministrators(...)
OrganizationAccountGovernance.DisableMembership(...)
OrganizationAccountGovernance.AssignFoodIncharge(...)
OrganizationAccountGovernance.RevokeFoodIncharge(...)
OrganizationAccountGovernance.ProposeAdministratorRoleChange(...)
OrganizationAccountGovernance.ApproveAdministratorRoleChange(...)
```

All state-changing methods return domain events or a typed domain error; they do not call persistence or providers.

The T4 remediation contract is frozen exactly as follows:

```csharp
public sealed class OrganizationAccountGovernance
{
    public OrganizationId OrganizationId { get; }
    public long OriginalVersion { get; }
    public long Version { get; private set; }
    public AdministratorBootstrapStatus BootstrapStatus { get; }
    public DateTimeOffset? BootstrapSealedAt { get; }
    public IReadOnlyCollection<Membership> Memberships { get; }
    public IReadOnlyCollection<RoleChangeRequest> RoleChangeRequests { get; }

    public static Result<OrganizationAccountGovernance> Rehydrate(
        OrganizationId organizationId,
        long version,
        AdministratorBootstrapStatus bootstrapStatus,
        DateTimeOffset? bootstrapSealedAt,
        IReadOnlyCollection<Membership> memberships,
        IReadOnlyCollection<RoleChangeRequest> roleChangeRequests);

    public Result<AdministratorBootstrapCompleted> BootstrapAdministrators(
        IReadOnlyCollection<Membership> administrators,
        DateTimeOffset now);
    public Result<MembershipDisabled> DisableMembership(
        Membership actor, Membership target, DateTimeOffset now);
    public Result<FoodInchargeAssigned> AssignFoodIncharge(
        Membership actor, Membership target, DateTimeOffset now);
    public Result<FoodInchargeRevoked> RevokeFoodIncharge(
        Membership actor, Membership target, DateTimeOffset now);
    public Result<RoleChangeRequest> ProposeAdministratorRoleChange(
        RoleChangeRequestId requestId,
        AdministratorRoleChangeAction action,
        Membership proposer,
        Membership target,
        string reason,
        DateTimeOffset now);
    public Result<AdministratorRoleChangeApproved> ApproveAdministratorRoleChange(
        RoleChangeRequest request,
        Membership proposer,
        Membership approver,
        Membership target,
        DateTimeOffset now);
}

public sealed class RoleChangeRequest
{
    public static Result<RoleChangeRequest> Rehydrate(
        RoleChangeRequestId id,
        OrganizationId organizationId,
        MembershipId targetMembershipId,
        AdministratorRoleChangeAction action,
        MembershipId proposerMembershipId,
        MembershipId? approverMembershipId,
        string reason,
        DateTimeOffset proposedAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? approvedAt,
        RoleChangeRequestStatus status);
}
```

`Rehydrate` rejects negative versions, duplicate IDs, cross-organization children, inconsistent
bootstrap fields, and undefined enum/status values. `RoleChangeRequest.Rehydrate` additionally
rejects blank reason, non-UTC or inconsistent chronology, and state/approver/approval combinations
that do not match `Pending` or `Approved`. Operations accept only the exact object
instances owned by the aggregate. The root derives active-Admin quorum from `Memberships`,
revalidates the stored proposer at approval, and enforces UTC chronology
`ProposedAt <= now < ExpiresAt`. Every success increments `Version` once; every failure preserves
the root and all children. `Membership.Disable`, `AssignFoodIncharge`, `RevokeFoodIncharge`,
`GrantAdministrator`, and `RevokeAdministrator`, plus `RoleChangeRequest.Propose` and `Approve`,
are internal primitives only. The public `AdministratorBootstrap` class is deleted; only
`AdministratorBootstrapCompleted` and aggregate-owned seal state remain.

Add stable Account error codes `invalid_account_governance_state`,
`invalid_administrator_role_change_action`, and
`role_change_request_chronology_invalid`. Existing codes remain unchanged. Non-UTC `now` throws
`ArgumentException` before mutation; invalid state/action/chronology returns the typed error.

The T6R signup-authority contract is frozen exactly as follows:

```csharp
public sealed class HelpNeedSignups
{
    public OrganizationId OrganizationId { get; }
    public ServiceDateId ServiceDateId { get; }
    public HelpNeedId HelpNeedId { get; }
    public long OriginalVersion { get; }
    public long Version { get; private set; }
    public long WaitlistOrderHighWater { get; private set; }
    public IReadOnlyCollection<Signup> Signups { get; }

    public static Result<HelpNeedSignups> Rehydrate(
        ServiceDate serviceDate,
        HelpNeed helpNeed,
        long version,
        long waitlistOrderHighWater,
        IReadOnlyCollection<Signup> signups);

    public Result<SignupSubmitted> Submit(
        SignupId signupId,
        Membership primaryMembership,
        SignupKind kind,
        IReadOnlyCollection<Membership> memberParticipants,
        int unnamedParticipantCount,
        DateTimeOffset now);
    public Result<SignupTransitioned> Approve(SignupId signupId, DateTimeOffset now);
    public Result<SignupTransitioned> Decline(SignupId signupId, DateTimeOffset now);
    public Result<SignupTransitioned> Waitlist(SignupId signupId, DateTimeOffset now);
    public Result<SignupTransitioned> Withdraw(SignupId signupId, DateTimeOffset now);
    public Result<SignupTransitioned> Override(
        SignupId signupId, SignupStatus targetStatus, DateTimeOffset now);
    public Result<SignupTransitioned> Cancel(SignupId signupId, DateTimeOffset now);
    public Result<WaitlistedSignupReassigned> Reassign(
        SignupId signupId, DateTimeOffset now);
    public IReadOnlyCollection<Signup> OrderedWaitlist();
}

public sealed class Signup
{
    public ServiceDateId ServiceDateId { get; }
    public DateTimeOffset SubmittedAt { get; }
    public DateTimeOffset? LastTransitionAt { get; private set; }
    public long? WaitlistOrder { get; private set; }
    public long Version { get; private set; }

    public static Result<Signup> Rehydrate(
        SignupId id,
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        HelpNeedId helpNeedId,
        MembershipId primaryMembershipId,
        SignupKind kind,
        IReadOnlyCollection<MembershipId> memberParticipantIds,
        int unnamedParticipantCount,
        SignupStatus status,
        DateTimeOffset submittedAt,
        DateTimeOffset? lastTransitionAt,
        long? waitlistOrder,
        long version);
}
```

`HelpNeedSignups.Rehydrate` validates the exact service-date/help-need identity, status, capacity,
and version and owns a defensive deep copy of every child. All children must match the root's
organization/date/need and have unique IDs; the set must satisfy capacity, active-primary, waitlist,
metadata, and exact reachable-version invariants. Commands accept no signup collection, selected
`Signup`, `ServiceDate`, or `HelpNeed`. `Signup` mutation primitives and `DeepCopy` are internal.
Every successful root command increments `Version` once; child transitions increment once;
submission creates child version zero; failure mutates nothing; `OriginalVersion` is immutable.
`WaitlistOrderHighWater` is non-negative and monotonic. Zero means no order has ever been
allocated. Rehydration permits retained gaps and requires each current waitlist order to be
positive, unique, and no greater than the supplied high-water value. A successful new waitlist
assignment allocates and persists `WaitlistOrderHighWater + 1`; leaving the waitlist never lowers
it. At `long.MaxValue`, allocation returns `version_exhausted` with root, children, version, and
high-water value unchanged. UTC chronology is `now >= LastTransitionAt ?? SubmittedAt`.

T8 must provide one repository path that loads the complete signup set and current canonical
date/need context plus `help_needs.waitlist_order_high_water`, then compare-and-swaps
`help_needs.signup_version` while writing the high-water value and changed children atomically.
T12/T15/T16/T17 consume only that path. A partial set, omitted high-water value, or alternate child
write path violates the frozen contract.

### 2.4 Application ports

```csharp
IClock.UtcNow
ICurrentActor.UserId / MembershipId / OrganizationId
IUnitOfWork.ExecuteAsync(...)
IIdentityService
ITokenService
IMembershipRepository
IServiceDateRepository
ISignupRepository
IThreadRepository
INotificationRepository
IIdempotencyStore
IAuditWriter
IPrivilegedAccessWriter
IOutboxWriter
IPushGateway.SendAsync(...)
IStepUpVerifier.IssueAsync(...) / ConsumeAsync(...)
```

Every protected use-case receives an active database-backed actor context. Role claims in JWTs are never authoritative.
Governance commands accept identifiers and reason only, resolve the actor from
`ICurrentActor`, load one complete `OrganizationAccountGovernance`, select only aggregate-owned
entities, and pass `IClock.UtcNow`. They never accept `Membership`, role collections,
administrator counts, bootstrap state, aggregate version, or governance timestamps from an API
request.

### 2.5 Public API

The endpoint paths, request/response intent, privacy rules, and status semantics in `architecture.md` section 6 are frozen for v1. The first vertical slice implements:

```text
POST /api/v1/auth/login
POST /api/v1/auth/refresh
POST /api/v1/auth/logout
GET  /api/v1/me
GET  /api/v1/dates?scope=open
GET  /api/v1/dates/{dateId}
POST /api/v1/dates
POST /api/v1/dates/{dateId}/needs
POST /api/v1/dates/{dateId}/open
POST /api/v1/needs/{needId}/signups
GET  /api/v1/signups/mine
GET  /api/v1/dates/{dateId}/roster
```

Signup input accepts `memberParticipantIds` and `unnamedParticipantCount`, not participant display-name input. Each referenced membership must be active and `eligible_as_named_participant` under the organization's external adult-membership policy; the system stores no age/date of birth. Roster DTOs may resolve those member display names but expose non-members only as a count/non-identifying label—never non-member names, email, phone, login identifiers, tokens, or unrelated profile data.

### 2.6 Provider/configuration seams

- `IPushGateway` defaults to a local recording/no-op implementation. Expo Push is a later adapter and is not an MVP coordination blocker.
- PostgreSQL runs locally in Docker. A managed database is configuration/deployment scope, not application scope.
- API configuration is environment-based. No hosting-vendor SDK is permitted in Domain or Application.
- Invite URLs are displayed once for out-of-band delivery; no email provider is required.
- No payment package, model, endpoint, secret, or placeholder credential is introduced.
- iOS is the MVP target. Android-compatible code is maintained, but Android release work is deferred.

## 3. Task graph

### Phase A — Foundation and frozen contracts

T1  Scaffold repository and deterministic toolchain
    owner:        developer
    objective:    Create buildable .NET/mobile/test shells and local PostgreSQL without feature behavior.
    files:        `HusayniaTabruk/HusayniaTabruk.sln`; `Directory.Build.props`; `Directory.Packages.props`; `.editorconfig`; `.gitignore`; `global.json`; `docker-compose.yml`; all initial `src/*/*.csproj`; all initial `tests/*/*.csproj`; `apps/mobile/package.json`; `apps/mobile/package-lock.json`; `apps/mobile/app.json`; `apps/mobile/eas.json`; `apps/mobile/tsconfig.json`; `apps/mobile/jest.config.*`; `apps/mobile/app/_layout.tsx`; `README.md`
    depends_on:   -
    parallel_ok:  no
    exit_criteria: `dotnet restore HusayniaTabruk.sln`, `dotnet build HusayniaTabruk.sln --no-restore`, `npm ci --prefix apps/mobile`, and `npm run typecheck --prefix apps/mobile` pass; `docker compose config` passes.
    status:       PENDING

T2  Implement shared primitives, ports, and boundary tests
    owner:        backend-specialist
    objective:    Materialize the frozen IDs, enums, errors, application ports, and project dependency rules used by every feature task.
    files:        `src/HusayniaTabruk.Domain/Common/**`; `src/HusayniaTabruk.Application/Abstractions/**`; `tests/HusayniaTabruk.Domain.Tests/Architecture/**`; `tests/HusayniaTabruk.Application.Tests/Architecture/**`
    depends_on:   T1
    parallel_ok:  no
    exit_criteria: named architecture tests prove the dependency direction; primitive/error unit tests pass.
    status:       PENDING

T3  Establish API conventions and contract harness
    owner:        api-specialist
    objective:    Add `/api/v1`, problem details, request tracing, OpenAPI generation, auth placeholders, pagination schema, ETag/idempotency conventions, and contract snapshot verification.
    files:        `src/HusayniaTabruk.Api/Program.cs`; `src/HusayniaTabruk.Api/Configuration/**`; `src/HusayniaTabruk.Api/Middleware/**`; `src/HusayniaTabruk.Api/OpenApi/**`; `tests/HusayniaTabruk.Api.ContractTests/Conventions/**`; `docs/api/openapi.json`
    depends_on:   T2
    parallel_ok:  no
    exit_criteria: API starts without feature endpoints; OpenAPI is generated deterministically; convention tests prove problem details, camelCase, trace ID, version prefix, security scheme, bounded cursor semantics, endpoint body limits, and `413`/`429` with `Retry-After`.
    status:       PENDING

### Phase B — Parallel domain cores

T4  Implement membership and role domain
    owner:        backend-specialist
    objective:    Implement membership activation/disable, Food Incharge assignment/revocation, and dual-control administrator grant/revoke requests with no self-assignment.
    files:        `src/HusayniaTabruk.Domain/Accounts/**`; `tests/HusayniaTabruk.Domain.Tests/Accounts/**`
    depends_on:   T2
    parallel_ok:  yes
    exit_criteria: tests cover invited/active/disabled membership, named-participant eligibility, proposer/approver/target distinctness, 24-hour request expiry, bootstrap requiring at least two distinct administrators and sealing after use, duplicate/invalid role transitions, and immediate revocation.
    status:       FAILED — independent T4 gate reproduced stale-proposer privilege propagation; superseded by T4R

T4R  Remediate account-governance invariant boundary
    owner:        developer
    objective:    Replace caller-controlled T4 governance with the frozen versioned `OrganizationAccountGovernance` aggregate, authoritative owned actors, internal quorum, durable bootstrap seal, proposer revalidation, exhaustive action validation, and strict UTC chronology.
    files:        `src/HusayniaTabruk.Domain/Accounts/OrganizationAccountGovernance.cs` (new); `src/HusayniaTabruk.Domain/Accounts/AccountErrorCodes.cs`; `src/HusayniaTabruk.Domain/Accounts/Membership.cs`; `src/HusayniaTabruk.Domain/Accounts/RoleChangeRequest.cs`; `src/HusayniaTabruk.Domain/Accounts/AdministratorBootstrap.cs` (remove the public unsafe class, retain only approved status/event declarations); `tests/HusayniaTabruk.Domain.Tests/Accounts/OrganizationAccountGovernanceTests.cs` (new); `tests/HusayniaTabruk.Domain.Tests/Accounts/MembershipTests.cs`; `tests/HusayniaTabruk.Domain.Tests/Accounts/RoleChangeRequestTests.cs`; `tests/HusayniaTabruk.Domain.Tests/Accounts/T4IndependentTests.cs`; `tests/HusayniaTabruk.Domain.Tests/Accounts/AdministratorBootstrapTests.cs` (delete after migrating coverage)
    depends_on:   T2
    parallel_ok:  yes
    exit_criteria: Domain-only change; no repository/EF/API/clock/step-up/audit/notification code. Tests prove invalid aggregate/request rehydration; inactive/non-Admin/cross-organization/look-alike actor rejection for disable/assign/revoke; two-Admin disable/revoke denial and three-Admin success; no integer quorum API; revoked/disabled proposer denial; undefined action rejection at proposal and rehydration; approval before proposal and at/after expiry denial with equality/one-tick boundaries; non-UTC throws before mutation; bootstrap seal survives rehydration and cannot rerun; every successful mutation increments version once and every failure preserves root/children. Public caller-ID governance methods, public `RoleChangeRequest.Propose/Approve`, caller-count approval, and public resettable `AdministratorBootstrap` no longer compile or exist. `dotnet test .\tests\HusayniaTabruk.Domain.Tests --filter "FullyQualifiedName~Accounts"` and the full Domain gate pass.
    status:       PENDING

T5  Implement service date and help-need domain
    owner:        backend-specialist
    objective:    Implement date/need creation, validation, open/close/cancel transitions, deadline fields, and retained-history behavior.
    files:        `src/HusayniaTabruk.Domain/Dates/**`; `tests/HusayniaTabruk.Domain.Tests/Dates/**`
    depends_on:   T2
    parallel_ok:  yes
    exit_criteria: tests cover valid and invalid transitions, close/cancel semantics, time boundaries, nullable capacity, and no destructive removal.
    status:       PENDING

T6  Implement signup, capacity, and waitlist domain
    owner:        backend-specialist
    objective:    Implement minimized participant composition, signup transitions, deadline rules, capacity accounting, overrides, and selected waitlist reassignment.
    files:        `src/HusayniaTabruk.Domain/Signups/**`; `tests/HusayniaTabruk.Domain.Tests/Signups/**`
    depends_on:   T2
    parallel_ok:  yes
    exit_criteria: table-driven tests cover every allowed/denied transition, deadline edge, formula `1 + referenced + unnamed`, 1-25 total boundary, at most 20 eligible member references/20 unnamed participants, inactive/ineligible reference rejection, rejection of display-name fields, capacity boundary, and no-overallocation reassignment.
    status:       PENDING

T6R Remediate signup authority with per-help-need aggregate
    owner:        developer
    objective:    Replace caller-controlled signup collections and date context with the frozen `HelpNeedSignups` root while preserving approved T6 behavior.
    files:        `src/HusayniaTabruk.Domain/Signups/HelpNeedSignups.cs` (add); `src/HusayniaTabruk.Domain/Signups/Signup.cs`; `src/HusayniaTabruk.Domain/Signups/SignupErrorCodes.cs`; `src/HusayniaTabruk.Domain/Signups/SignupCapacity.cs` (delete); `src/HusayniaTabruk.Domain/Signups/HelpNeedSignupExtensions.cs` (delete); `tests/HusayniaTabruk.Domain.Tests/Signups/SignupTests.cs`; `tests/HusayniaTabruk.Domain.Tests/Signups/T6IndependentSignupTests.cs`
    depends_on:   T6
    parallel_ok:  yes, but never with another task editing `Domain/Signups/**` or its domain tests
    exit_criteria: Domain-only change; no repository/EF/API/application/clock/audit/notification/outbox code. Exact public contract includes `WaitlistOrderHighWater` and the five-argument `Rehydrate(serviceDate, helpNeed, version, waitlistOrderHighWater, signups)`. Tests prove full-set rehydration; non-negative high-water; zero initial value; current orders positive/unique/at-or-below high-water; retained gaps; permanent non-reuse after removing the maximum across rehydration; atomic `long.MaxValue` exhaustion; omission-resistant approval/reassignment with no collection parameter; mixed organization/date/need and duplicate-ID/order rejection; counterfeit same-organization date rejection with no date command parameter; exact reachable/unreachable status-version boundaries including `Cancelled` v3; pre-submission and pre-prior-transition chronology rejection; exact capacity/release and Food-Incharge-selected reassignment; active-primary uniqueness; version overflow; defensive copies; root/child/high-water increments and failure atomicity. Reflection/compile-time assertions prove public child mutators, collection-based capacity APIs, `SignupCapacity`, and `HelpNeedSignupExtensions` are absent. `dotnet test .\tests\HusayniaTabruk.Domain.Tests --filter "FullyQualifiedName~Signups"` and the full Domain gate pass.
    status:       FAILED

T6R-HW Complete durable waitlist high-water remediation
    owner:        developer
    objective:    Amend the implemented T6R aggregate to retain monotonic waitlist allocation history across removal and rehydration.
    files:        `src/HusayniaTabruk.Domain/Signups/HelpNeedSignups.cs`; `tests/HusayniaTabruk.Domain.Tests/Signups/SignupTests.cs`; `tests/HusayniaTabruk.Domain.Tests/Signups/T6IndependentSignupTests.cs`
    depends_on:   -
    parallel_ok:  no
    exit_criteria: exact property/rehydration signatures above compile; focused tests prove gaps retained, removed maximum never reused after rehydration, invalid high-water state rejected, deterministic ordering unchanged, Food-Incharge-selected reassignment unchanged, and `long.MaxValue` exhaustion is atomic; focused Signups and full Domain gates pass.
    status:       PENDING

T7  Implement thread and notification domain
    owner:        backend-specialist
    objective:    Implement one immutable text thread per date, the explicit approved-contact eligibility matrix, report/hide/lock rules, bounded content, notification records, and provider-neutral push intent events.
    files:        `src/HusayniaTabruk.Domain/Threads/**`; `src/HusayniaTabruk.Domain/Notifications/**`; `tests/HusayniaTabruk.Domain.Tests/Threads/**`; `tests/HusayniaTabruk.Domain.Tests/Notifications/**`
    depends_on:   T2
    parallel_ok:  yes
    exit_criteria: tests prove read/post denial for every non-approved signup state and ordinary admins, grant for approved contacts/managing Food Incharge, revocation inputs, immutable messages, payload bounds, duplicate reports, lock/hide/report rules, generic push intent data, and notification authority independent of delivery. Repeating `Lock` as the currently authorized managing Food Incharge with a timestamp exactly equal to authoritative `LockedAt` succeeds as `WasDuplicate` with unchanged state/version; a different timestamp returns `invalid_transition`, and both retry paths preserve authorization checks and failure atomicity.
    status:       PENDING

### Phase C — Persistence, identity, and first vertical slice

T8  Implement PostgreSQL model and initial migration
    owner:        database-specialist
    objective:    Map the approved minimized participant model, role-change requests, constraints, indexes, concurrency tokens including `date_threads.version`, Identity tables, idempotency, insert-only audit/privileged access, notification, and outbox storage.
    files:        `src/HusayniaTabruk.Infrastructure/Persistence/**`; `src/HusayniaTabruk.Infrastructure/Identity/Entities/**`; `src/HusayniaTabruk.Infrastructure/Migrations/**`; `tests/HusayniaTabruk.IntegrationTests/Persistence/**`
    depends_on:   T4R, T5, T6R-HW, T7
    parallel_ok:  no
    exit_criteria: migration applies to a clean PostgreSQL database; schema tests prove organization scoping, active-signup uniqueness, immutable signup `service_date_id`, dedicated `help_needs.signup_version`, and `help_needs.waitlist_order_high_water bigint NOT NULL DEFAULT 0 CHECK (waitlist_order_high_water >= 0)`, plus one thread/date, nullable authoritative `date_threads.locked_at` with a status/timestamp consistency check, `date_threads.version bigint NOT NULL` configured as the thread concurrency token, active-role uniqueness, role-request constraints, no free-text participant-name column, FK integrity, concurrency columns, and application-role denial of audit/access-event update/delete. Organization storage includes bootstrap status/sealed-at and the governance version. Thread repository tests hydrate `LockedAt` and `Version`, preserve the exact Domain formula `messages.Count + reports.Count + hidden-message count + (Locked ? 1 : 0)`, and reject messages, reports, or hides after the authoritative lock and reports after a message's `hidden_at`. Each post, report, hide, and first-lock save uses the loaded `DateThread.Version` as the expected value in an atomic `date_threads` compare-and-swap and commits the new thread version with every child, moderation, notification, audit, and outbox effect in the same transaction. Permanent PostgreSQL integration tests use two stale aggregate instances for each of post/report/hide/lock, prove the loser returns `stale_version`, and prove its thread row, child rows, visibility/lock state, notifications, audit records, and outbox records are all rolled back. An identical lock retry with the authoritative `locked_at` is a successful no-write path with unchanged version and no duplicate lock event/outbox record; a different timestamp returns `invalid_transition` and writes nothing. Repository tests also prove complete same-organization membership/request hydration and complete per-help-need signup hydration from canonical date/need context including high-water values greater than the current maximum. Saving compares `OriginalVersion`, writes changed children and high-water together, and increments `signup_version` in one transaction; stale CAS, invalid hydration, or any write failure rolls all of them back. Governance and signup aggregates reject stale original versions by compare-and-swap with atomic rollback; thread persistence applies the explicit `DateThread.Version` CAS contract above.
    status:       DONE — existing T8 baseline; production orchestration superseded by T8M

T8M Remediate production T8 migration orchestration
    owner:        developer
    objective:    Make owner `psql` scripts the sole production T8 Up/Down interface with one
                  session advisory lock, schema-qualified/OID attestation, exact schema/default ACL
                  controls, compensation plus history-last Up, logical non-destructive Down,
                  disposable-only EF execution, and a seven-artifact manifest.
    files:        `src/HusayniaTabruk.Infrastructure/Migrations/PostgresLeastPrivilegeCatalog.cs`;
                  `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql`;
                  `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-downgrade.sql` (new);
                  `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs`;
                  `src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContextFactory.cs`;
                  `src/HusayniaTabruk.Infrastructure/Migrations/T8MigrationArtifacts.sha256`;
                  `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationArtifactManifestTests.cs`;
                  `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs`;
                  `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresLeastPrivilegeCatalogTests.cs`;
                  `src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md`
    depends_on:   T8
    parallel_ok:  no
    exit_criteria: the complete frozen interface, failure semantics, test matrix, and operator
                  documentation in
                  `.ai-org/missions/2026-08-15-husaynia-t8-migration-orchestration/implementation-plan.md`
                  pass on real PostgreSQL 18.6; production Down deletes only T8 history and
                  preserves all safety objects and least privilege; seven manifest hashes verify;
                  independent test/security/review gates pass and Engineering Judge approves.
    status:       PENDING

T9  Implement authentication, invitations, and actor resolution
    owner:        backend-specialist
    objective:    Implement invite acceptance, login, refresh rotation/reuse detection, logout, `/me`, active-membership loading, disable/revocation behavior, auth rate limits, and the five-minute single-purpose step-up adapter.
    files:        `src/HusayniaTabruk.Application/Accounts/**`; `src/HusayniaTabruk.Application/Admin/Auth/**`; `src/HusayniaTabruk.Infrastructure/Identity/Services/**`; `src/HusayniaTabruk.Api/Auth/**`; `src/HusayniaTabruk.Api/Endpoints/V1/Auth/**`; `src/HusayniaTabruk.Api/Endpoints/V1/Me/**`; `tests/HusayniaTabruk.Application.Tests/Accounts/**`; `tests/HusayniaTabruk.IntegrationTests/Auth/**`
    depends_on:   T8M
    parallel_ok:  no
    exit_criteria: named tests prove invited/approved access, expired invite denial, disabled-member denial, rotating refresh tokens, reuse revocation, prompt role revocation, no role authority from stale JWT claims, password re-verification, opaque `X-Step-Up-Token`, purpose binding, five-minute expiry, single use, replay denial, and no mobile persistence/logging.
    status:       PENDING

T10  Implement administrator membership and role API
    owner:        backend-specialist
    objective:    Implement invitation issuance, no-self-target Food Incharge assignment/revocation, dual-control administrator requests/approval, sealed bootstrap command, step-up authorization, administrator notifications, member disable, and atomic audit.
    files:        `src/HusayniaTabruk.Application/Admin/Members/**`; `src/HusayniaTabruk.Api/Endpoints/V1/Admin/**`; `tests/HusayniaTabruk.Application.Tests/Admin/**`; `tests/HusayniaTabruk.IntegrationTests/Admin/**`
    depends_on:   T9, T4R
    parallel_ok:  no
    exit_criteria: AC-1, AC-2, and relevant AC-17 API tests pass; no general role endpoint exists; commands accept IDs/reason only, resolve the actor from `ICurrentActor`, load the complete governance aggregate, use only aggregate-owned entities, and pass `IClock.UtcNow`; no caller-supplied role/count/bootstrap/version/timestamp state exists. Proposer/approver/target are distinct and proposer authority is revalidated; bootstrap requires at least two initial administrators and cannot rerun after rehydration; normal changes cannot leave fewer than two active administrators; expired/missing/replayed step-up is denied; reason/body bounds pass; version compare-and-swap makes membership/request/bootstrap, before/after audit, admin notification, and outbox writes atomic and rolls all back as `412 stale_version`; revocation is immediately effective; invite URL is returned once; private identifiers never appear in member-list DTOs.
    status:       PENDING

T11  Implement date publish and open-date query API
    owner:        backend-specialist
    objective:    Implement authorized date/need creation and opening plus member-safe date list/detail queries.
    files:        `src/HusayniaTabruk.Application/Dates/**`; `src/HusayniaTabruk.Api/Endpoints/V1/Dates/**`; `src/HusayniaTabruk.Api/Endpoints/V1/Needs/Management/**`; `tests/HusayniaTabruk.Application.Tests/Dates/**`; `tests/HusayniaTabruk.IntegrationTests/Dates/**`
    depends_on:   T8, T10
    parallel_ok:  no
    exit_criteria: AC-3 tests pass; non-manager mutations fail; open-date DTO contains instructions, availability, and deadline but no private roster data.
    status:       PENDING

T12  Implement pending signup and privacy-safe roster API
    owner:        backend-specialist
    objective:    Complete the first vertical slice with minimized/idempotent signup submission, mine query, managing roster, audit, notification/outbox writes, abuse limits, and concealed record authorization.
    files:        `src/HusayniaTabruk.Application/Signups/Submit/**`; `src/HusayniaTabruk.Application/Signups/Queries/**`; `src/HusayniaTabruk.Application/Roster/**`; `src/HusayniaTabruk.Api/Endpoints/V1/Signups/Submit/**`; `src/HusayniaTabruk.Api/Endpoints/V1/Signups/Queries/**`; `src/HusayniaTabruk.Api/Endpoints/V1/Roster/**`; `tests/HusayniaTabruk.Application.Tests/Signups/Submit/**`; `tests/HusayniaTabruk.IntegrationTests/Signups/Submit/**`; `tests/HusayniaTabruk.Api.ContractTests/Privacy/SignupRosterSnapshots.*`
    depends_on:   T11, T6R-HW
    parallel_ok:  no
    exit_criteria: AC-4, AC-5, AC-7, AC-13, and submission portions of AC-16/17 pass against PostgreSQL using only complete `HelpNeedSignups` hydration/CAS, including rejection of participant-name fields, participant/label/body bounds, account/org submission quotas, `413`/`429`, concurrent duplicate submission, partial-set repository attack, stale signup-version retry response, and close-during-submit.
    status:       PENDING

T13  Generate mobile client and implement secure auth shell
    owner:        frontend-specialist
    objective:    Generate the TypeScript client from verified OpenAPI; implement SecureStore token lifecycle, login, session restoration, refresh, logout purge, and authenticated routing.
    files:        `apps/mobile/src/core/api/**`; `apps/mobile/src/core/security/**`; `apps/mobile/src/features/auth/**`; `apps/mobile/app/(auth)/**`; `apps/mobile/app/(member)/_layout.tsx`; `apps/mobile/tests/unit/auth/**`; `apps/mobile/tests/integration/auth/**`
    depends_on:   T9, T3
    parallel_ok:  yes
    exit_criteria: generated-client drift check passes; tests prove no token enters AsyncStorage/logs, refresh rotation, failed refresh logout/purge, and accessible login errors.
    status:       PENDING

T14  Implement first mobile coordination slice
    owner:        frontend-specialist
    objective:    Implement open dates, date detail, individual/household/team adult-member selection plus unnamed count/non-identifying label, pending-sync-aware submit, personal signup state, and Food Incharge pending roster.
    files:        `apps/mobile/src/features/dates/**`; `apps/mobile/src/features/signups/**`; `apps/mobile/src/features/roster/**`; `apps/mobile/app/(member)/dates/**`; `apps/mobile/app/(member)/signups/**`; `apps/mobile/app/(incharge)/dates/**`; `apps/mobile/tests/unit/dates/**`; `apps/mobile/tests/unit/signups/**`; `apps/mobile/tests/component/first-slice/**`
    depends_on:   T12, T13
    parallel_ok:  no
    exit_criteria: component/integration tests prove all three signup compositions, no non-member name input, bounded counts/label, Pending rather than Approved, consistent roster state, privacy-safe rendering, empty states, and non-color status labels.
    status:       PENDING

### Phase D — Coordination workflow completion

T15  Implement approval, decline, and waitlist
    owner:        backend-specialist
    objective:    Add transactional decision commands with authorization, consistent roster/member state, audit, in-app notification, and outbox intent.
    files:        `src/HusayniaTabruk.Application/Signups/Decisions/**`; `src/HusayniaTabruk.Api/Endpoints/V1/Signups/Decisions/**`; `tests/HusayniaTabruk.Application.Tests/Signups/Decisions/**`; `tests/HusayniaTabruk.IntegrationTests/Signups/Decisions/**`
    depends_on:   T12, T6R-HW
    parallel_ok:  yes
    exit_criteria: AC-6 and real PostgreSQL simultaneous-approval/omitted-approved-signup attacks pass through complete root hydration/CAS with no excess slots; waitlisted-to-approved grants thread access; pending/declined/waitlisted remain denied; invalid transitions return `409 invalid_transition`; stale root writes return `412 stale_version`.
    status:       PENDING

T16  Implement withdrawal, deadline override, and reassignment
    owner:        backend-specialist
    objective:    Add idempotent pre-deadline withdrawal, post-deadline denial, authorized override, and capacity-safe selected waitlist reassignment.
    files:        `src/HusayniaTabruk.Application/Signups/Cancellation/**`; `src/HusayniaTabruk.Application/Signups/Reassignment/**`; `src/HusayniaTabruk.Api/Endpoints/V1/Signups/Cancellation/**`; `src/HusayniaTabruk.Api/Endpoints/V1/Needs/Reassignment/**`; `tests/HusayniaTabruk.Application.Tests/Signups/Cancellation/**`; `tests/HusayniaTabruk.IntegrationTests/Signups/CancellationReassignment/**`
    depends_on:   T12, T6R-HW
    parallel_ok:  yes
    exit_criteria: AC-8 through AC-11 pass using aggregate-owned date/deadline/high-water context and full-set CAS, including counterfeit-date absence from the application contract, immediate approved-to-withdrawn/cancelled thread denial, queued-post replay denial, retry, Food-Incharge-selected reassignment that may skip earlier entries, omitted-approved-signup attack, removed-maximum then rehydrate then waitlist non-reuse, and concurrent reassignment/waitlisting with no excess approved slots, duplicate order, reused order, or high-water/child split commit.
    status:       PENDING

T17  Implement close, cancel, and versioned date editing
    owner:        backend-specialist
    objective:    Add ETag-protected date/need edits, closure, and date cancellation with atomic affected-signup updates and notifications.
    files:        `src/HusayniaTabruk.Application/Dates/Management/**`; `src/HusayniaTabruk.Api/Endpoints/V1/Dates/Management/**`; `src/HusayniaTabruk.Api/Endpoints/V1/Needs/Editing/**`; `tests/HusayniaTabruk.Application.Tests/Dates/Management/**`; `tests/HusayniaTabruk.IntegrationTests/Dates/CloseCancelConcurrency/**`
    depends_on:   T12, T6R-HW
    parallel_ok:  yes
    exit_criteria: AC-7, AC-15, closure preservation, stale `If-Match`, cancellation atomicity, thread lock, and immediate contact thread revocation/cache-invalidation signal tests pass. Capacity reduction hydrates the complete `HelpNeedSignups` root and fails with `capacity_unavailable` without persisting when approved slots exceed the proposed capacity.
    status:       PENDING

T18  Implement mobile coordination management
    owner:        frontend-specialist
    objective:    Add publish/edit/close/cancel, decision, waitlist, cancellation deadline, override, reassignment, and status refresh UI.
    files:        `apps/mobile/src/features/admin/**`; `apps/mobile/src/features/roster/management/**`; `apps/mobile/src/features/signups/management/**`; `apps/mobile/app/(incharge)/manage/**`; `apps/mobile/tests/component/coordination-management/**`
    depends_on:   T15, T16, T17, T14
    parallel_ok:  no
    exit_criteria: component tests cover publish, approve/decline/waitlist, before/after-deadline cancellation, override, reassignment, close, cancel, and stale/conflict recovery without false success.
    status:       PENDING

### Phase E — Communication, notifications, and bounded offline operation

T19  Implement threads and moderation API
    owner:        backend-specialist
    objective:    Add approved-contact/managing-Food-Incharge ordinary thread list/send, bounded report/hide/lock, and separate step-up privileged moderation read with immutable history/access audit.
    files:        `src/HusayniaTabruk.Application/Threads/**`; `src/HusayniaTabruk.Api/Endpoints/V1/Threads/**`; `tests/HusayniaTabruk.Application.Tests/Threads/**`; `tests/HusayniaTabruk.IntegrationTests/Threads/**`
    depends_on:   T10, T15, T16, T17
    parallel_ok:  yes
    exit_criteria: AC-12 and the complete transition-level authorization matrix pass; ordinary admins receive concealed `404`; moderation requires `PrivilegedThreadRead`, unexpired purpose-bound step-up, reason/purpose/case ID, and a successful insert-only event per page; audit failure denies access; exact payload/page/rate limits, duplicate-report control, notification suppression, `413`, and `429 Retry-After` pass; message body is absent from logs and push payloads.
    status:       PENDING

T20  Implement in-app notifications, local push gateway, and outbox worker
    owner:        backend-specialist
    objective:    Add notification list/read/device APIs, PostgreSQL outbox leasing/retry/dead-letter behavior, and a local recording/no-op `IPushGateway`.
    files:        `src/HusayniaTabruk.Application/Notifications/**`; `src/HusayniaTabruk.Infrastructure/Push/**`; `src/HusayniaTabruk.Infrastructure/Outbox/**`; `src/HusayniaTabruk.Api/Endpoints/V1/Notifications/**`; `src/HusayniaTabruk.Api/Endpoints/V1/Devices/**`; `src/HusayniaTabruk.Api/Configuration/OutboxConfiguration.cs`; `tests/HusayniaTabruk.Application.Tests/Notifications/**`; `tests/HusayniaTabruk.IntegrationTests/Outbox/**`
    depends_on:   T8, T15, T16, T17, T19
    parallel_ok:  no
    exit_criteria: AC-14 tests prove atomic in-app notification/outbox creation, provider failure independence, lease recovery, idempotent handler behavior, retry, token invalidation, and dead-letter recording.
    status:       PENDING

T21  Implement mobile threads and notification center
    owner:        frontend-specialist
    objective:    Add eligible thread UI, send/report/moderation controls, notification center/read state, generic push deep-link handling, and sensitive-content warning.
    files:        `apps/mobile/src/features/threads/**`; `apps/mobile/src/features/notifications/**`; `apps/mobile/app/(member)/threads/**`; `apps/mobile/app/(member)/notifications/**`; `apps/mobile/tests/component/threads-notifications/**`
    depends_on:   T19, T20, T13
    parallel_ok:  yes
    exit_criteria: tests prove sender/timestamp display, no thread UI before approval, immediate removal/purge after revocation response, queued-post permanent rejection UX, immutable messages, `413`/`429` handling using `Retry-After`, generic deep links, no sensitive lock-screen content, and authoritative in-app state when push fails.
    status:       PENDING

T22  Implement bounded mobile cache and command queue
    owner:        frontend-specialist
    objective:    Cache authorized reads with TTL and queue only allowed idempotent member commands with visible `Pending sync`, ordered replay, backoff, terminal-error refetch, and purge.
    files:        `apps/mobile/src/core/offline/**`; `apps/mobile/src/core/api/retryPolicy.ts`; `apps/mobile/tests/unit/offline/**`; `apps/mobile/tests/integration/offline/**`
    depends_on:   T14, T18, T21
    parallel_ok:  no
    exit_criteria: AC-16 tests cover offline submit/withdraw/message/read/device actions, no offline Food Incharge mutation, unknown outcomes, duplicate-safe replay, permanent eligibility/rate rejection, `Retry-After` scheduling, and logout/disable/organization-change/thread-revocation purge.
    status:       PENDING

### Phase F — Hardening, traceability, and independent gates

T23  Complete accessibility and terminology hardening
    owner:        frontend-specialist
    objective:    Apply shared accessible primitives, focus management, dynamic type, screen-reader labels, non-color states, respectful copy, and distinct empty states across all MVP journeys.
    files:        `apps/mobile/src/core/ui/**`; `apps/mobile/src/core/domain/terminology.ts`; `apps/mobile/tests/component/accessibility/**`; `apps/mobile/tests/component/empty-states/**`
    depends_on:   T18, T21, T22
    parallel_ok:  no
    exit_criteria: automated accessibility/component checks cover AC-18 journeys and a repository copy scan finds no unapproved public “Tabarruk”.
    status:       PENDING

T24  Add observability, health, and safe logging
    owner:        devops-sre-specialist
    objective:    Add health endpoints, OpenTelemetry-compatible logging/metrics/traces, redaction, database readiness, outbox metrics, privileged-read/role-change/rate-limit metrics and alerts, and environment configuration validation.
    files:        `src/HusayniaTabruk.Infrastructure/Observability/**`; `src/HusayniaTabruk.Api/Endpoints/Health/**`; `src/HusayniaTabruk.Api/Configuration/ObservabilityConfiguration.cs`; `tests/HusayniaTabruk.IntegrationTests/Operations/**`
    depends_on:   T20
    parallel_ok:  yes
    exit_criteria: tests prove `/health/live`, database-sensitive `/health/ready`, push-independent readiness, required metrics, privileged-read thresholds (>10 distinct threads/hour/actor or >50/day/org), role-change alerts, non-relaxable production limits, and redaction of identifiers, member display names, messages, tokens, invite URLs, and push tokens.
    status:       PENDING

T25  Add CI quality gates and deterministic validation scripts
    owner:        devops-sre-specialist
    objective:    Run restore/build/format/analyzers, all .NET test tiers, PostgreSQL integration tests, OpenAPI drift/client generation, mobile lint/typecheck/tests, dependency audit, and terminology scan.
    files:        `.github/workflows/tabruk-ci.yml`; `HusayniaTabruk/scripts/**`
    depends_on:   T23, T24
    parallel_ok:  no
    exit_criteria: the workflow or equivalent local script completes from a clean checkout and archives test/OpenAPI evidence.
    status:       PENDING

T26  Add independent acceptance and concurrency test suite
    owner:        test-engineer
    objective:    Add black-box tests mapping AC-1 through AC-17 plus security-remediation and reliability failures with named traceability; do not duplicate implementation-owned unit tests.
    files:        `tests/HusayniaTabruk.AcceptanceTests/**`; `.ai-org/missions/2026-08-14-husaynia-tabarruk-signup/acceptance-traceability.md`
    depends_on:   T25
    parallel_ok:  yes
    exit_criteria: every AC-1 through AC-17 has a named executable test; the complete signup-state/transition thread matrix, ordinary-admin denial, audited moderation, dual-control/step-up/no-self-assignment roles, participant minimization, payload/rate limits, and parallel-client duplicate/closure/approval/reassignment/notification/retry/date-cancellation cases execute.
    status:       PENDING

T27  Add user, admin, privacy, and local-operations documentation
    owner:        documentation-specialist
    objective:    Document setup, local containers, member eligibility, dual-control role governance/step-up, minimized participant data, workflow states, deadlines/override, approved-only thread access, audited moderation, payload/rate limits, hidden contacts, push/offline behavior, retention prerequisite, and off-platform payment boundary.
    files:        `HusayniaTabruk/README.md`; `HusayniaTabruk/docs/privacy-data-inventory.md`; `HusayniaTabruk/docs/operations.md`; `HusayniaTabruk/docs/user-guide.md`; `HusayniaTabruk/docs/admin-guide.md`
    depends_on:   T25
    parallel_ok:  yes
    exit_criteria: documentation gate checklist passes and all public copy uses “Tabruk”.
    status:       PENDING

T28  Execute independent security gate
    owner:        security-engineer
    objective:    First perform a focused re-review of the architecture remediation, then threat-model and test auth, every thread state/transition, moderation audit/step-up, role governance, participant minimization, privacy DTOs, token storage/rotation, payload/idempotency/rate abuse, logging, dependencies, device tokens, and local data purge.
    files:        `.ai-org/missions/2026-08-14-husaynia-tabarruk-signup/security-review.md`
    depends_on:   T25
    parallel_ok:  yes
    exit_criteria: all prior High/Medium findings are explicitly closed; zero unresolved Critical/High implementation findings; executed evidence is recorded.
    status:       PENDING

T29  Execute independent code review
    owner:        code-reviewer
    objective:    Review correctness, architecture boundaries, concurrency, error semantics, compatibility, maintainability, and test quality.
    files:        `.ai-org/missions/2026-08-14-husaynia-tabarruk-signup/code-review.md`
    depends_on:   T25
    parallel_ok:  yes
    exit_criteria: verdict is `APPROVED`; otherwise remediation tasks are inserted before T30.
    status:       PENDING

T30  Execute end-to-end iOS MVP validation
    owner:        qa-engineer
    objective:    Exercise AC-1 through AC-18 as real member, Food Incharge, and admin journeys against API/PostgreSQL with push failure and connectivity-loss scenarios.
    files:        `apps/mobile/tests/e2e/**`; `.ai-org/missions/2026-08-14-husaynia-tabarruk-signup/qa-results.md`
    depends_on:   T26, T27, T28, T29
    parallel_ok:  no
    exit_criteria: all primary journeys pass on the agreed iOS simulator/device matrix; accessibility evidence includes screen reader, focus, non-color status, and 200% text.
    status:       PENDING

T31  Judge Definition of Done
    owner:        engineering-judge
    objective:    Independently verify every Definition of Done item from executed artifacts and reject unsupported claims.
    files:        `.ai-org/missions/2026-08-14-husaynia-tabarruk-signup/final-judgment.md`
    depends_on:   T30
    parallel_ok:  no
    exit_criteria: verdict is `APPROVED`.
    status:       PENDING

## 4. Execution waves

```text
wave 1:  T1
wave 2:  T2
wave 3:  T3
wave 4:  T4, T5, T6, T7 (parallel; disjoint domain folders)
wave 5:  T4R, T6R (parallel remediation; Accounts and Signups ownership are disjoint)
wave 5a: T6R-HW (serialized Signups rework after T6R)
wave 6:  T8
wave 7:  T9
wave 8:  T10
wave 9:  T11, T13 (parallel; backend dates vs mobile auth)
wave 10: T12
wave 11: T14
          FIRST VERTICAL SLICE GATE
wave 12: T15, T16, T17 (parallel; disjoint coordination use-cases)
wave 13: T18, T19 (parallel; mobile management vs backend threads)
wave 14: T20
wave 15: T21, T24 (parallel; mobile communications vs backend operations)
wave 16: T22
wave 17: T23
wave 18: T25
wave 19: T26, T27, T28, T29 (parallel independent gates/artifacts)
wave 20: T30
wave 21: T31
```

No two tasks in a wave own the same file. Shared project/package files are frozen by T1. Shared primitives and ports are frozen by T2. `Program.cs`, API conventions, and the OpenAPI generation mechanism are frozen by T3; later endpoint tasks add files under their assigned folders and use centralized endpoint discovery rather than editing `Program.cs`.

## 5. Quality gates and exact commands

Run from `C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk` in PowerShell.

### Foundation gate — after T3

```powershell
docker compose config
dotnet restore .\HusayniaTabruk.sln
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror
dotnet test .\tests\HusayniaTabruk.Domain.Tests --no-build
dotnet test .\tests\HusayniaTabruk.Application.Tests --no-build
npm ci --prefix .\apps\mobile
npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
```

### Domain gate — after T4R and T6R

```powershell
dotnet test .\tests\HusayniaTabruk.Domain.Tests --filter "Category!=Slow" --logger "console;verbosity=normal"
dotnet test .\tests\HusayniaTabruk.Application.Tests --filter "FullyQualifiedName~Architecture"
```

### PostgreSQL/identity gate — after T10

```powershell
docker compose up -d postgres
dotnet ef database update --project .\src\HusayniaTabruk.Infrastructure --startup-project .\src\HusayniaTabruk.Api
dotnet test .\tests\HusayniaTabruk.IntegrationTests --filter "Category=Persistence|Category=Auth|Category=Admin"
docker compose down -v
```

### First vertical slice gate — after T14

```powershell
docker compose up -d postgres
dotnet test .\tests\HusayniaTabruk.IntegrationTests --filter "Category=FirstSlice|Category=Concurrency"
dotnet test .\tests\HusayniaTabruk.Api.ContractTests --filter "Category=Privacy|Category=OpenApi"
npm run generate:api --prefix .\apps\mobile
git diff --exit-code -- .\docs\api\openapi.json .\apps\mobile\src\core\api
npm test --prefix .\apps\mobile -- --runInBand --testPathPattern="auth|first-slice"
docker compose down -v
```

### Full implementation gate — after T25

```powershell
.\scripts\validate.ps1
```

`validate.ps1` must perform, at minimum:

```powershell
dotnet format .\HusayniaTabruk.sln --verify-no-changes
dotnet build .\HusayniaTabruk.sln --configuration Release -warnaserror
dotnet test .\HusayniaTabruk.sln --configuration Release --no-build --logger "trx"
npm ci --prefix .\apps\mobile
npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
npm test --prefix .\apps\mobile -- --runInBand
npm audit --prefix .\apps\mobile --audit-level=high
dotnet list .\HusayniaTabruk.sln package --vulnerable --include-transitive
npm run generate:api --prefix .\apps\mobile
git diff --exit-code -- .\docs\api\openapi.json .\apps\mobile\src\core\api
```

### Independent release gates

```powershell
dotnet test .\tests\HusayniaTabruk.AcceptanceTests --configuration Release --logger "trx"
npm run test:e2e:ios --prefix .\apps\mobile
```

Security review, code review, QA, and Engineering Judge must use the reports produced by T28-T31. A failed gate creates a new remediation task and reruns every invalidated downstream gate.

## 6. Acceptance traceability allocation

| Acceptance criteria | Primary tasks |
|---|---|
| AC-1, AC-2, AC-17 | T9, T10, T13, T26 |
| AC-3 | T11, T14, T26 |
| AC-4, AC-5, AC-7 | T12, T14, T26 |
| AC-6 | T15, T18, T26 |
| AC-8 to AC-11 | T16, T18, T26 |
| AC-12, AC-13 | T12, T19, T21, T26 |
| AC-14 | T20, T21, T26 |
| AC-15 | T17, T18, T26 |
| AC-16 | T12, T18, T20, T22, T26 |
| AC-18 | T23, T30 |

## 7. Explicit deferrals

The following are not blockers and must not enter MVP coordination tasks:

- Production hosting selection or deployment.
- Managed PostgreSQL purchase/provider integration.
- Expo Push Service credentials or production push provider; only `IPushGateway` and local test implementation are required.
- Transactional email provider.
- EAS subscription or OTA update dependency.
- Android release packaging/certification.
- In-app payments, payment credentials, marketplace payments, quotes, costs, cooking timelines, or prior named-participant reuse.

## 8. Risks and controls

- **Contract drift:** controlled by generated OpenAPI snapshot/client drift checks and serialized T3.
- **Shared-file collisions:** root manifests are T1-only; shared ports are T2-only; `Program.cs` is T3-only; feature tasks own disjoint folders.
- **PostgreSQL migration orchestration:** T8M serializes owner Up/Down across concurrent DDL and
  history finalization; T9 and all downstream work remain blocked until independent T8M gates pass.
- **Partial signup hydration:** T8 owns one full-set repository path and dedicated `signup_version`; T12/T15/T16/T17 may not write child signups directly.
- **Capacity-edit race:** T17 hydrates and compare-and-swaps the signup root before lowering capacity; invalid overallocated proposals fail atomically.
- **Auth scope expansion:** invitation delivery remains out-of-band; production email is deferred.
- **Push/vendor delay:** local gateway and transactional outbox prove behavior without provider approval.
- **Mobile false success:** every queued mutation shows `Pending sync`; administrative mutations require connectivity.
- **Privacy leakage:** DTO snapshots, concealed-record tests, logging redaction tests, security gate, and memory-only roster cache.
- **Authorization regression:** T7/T15-T19 freeze and execute the explicit thread state/transition matrix; mobile purges on authorization loss.
- **Privilege persistence:** T4/T9/T10 require two-person admin governance, no self-assignment, short step-up, immediate revocation, audit, and notification.
- **Messaging abuse:** T3/T7/T12/T19/T24 enforce payload, page, account, and organization bounds before notification/outbox creation.
- **Accessibility left late:** feature tasks include component accessibility tests; T23 is hardening, not first coverage.
- **Greenfield package churn:** T1 pins SDK/package/tool versions; later tasks may not edit manifests without Tech Lead replan.

## 9. Next dispatch

Dispatch **T8M only** to one developer against the frozen contract and exact file list in the T8M
mission implementation plan. Do not dispatch T9 or any transitive dependant until real PostgreSQL
tests, independent security/review gates, and Engineering Judge approval pass. No concurrent task
may edit a T8M-owned file.
