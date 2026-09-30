# T4 Account-Governance Remediation Design

Date: 2026-08-14  
Status: Implementation-ready; Domain only

## Verified current state

- **FACT:** Domain is framework/storage independent (`HusayniaTabruk/src/HusayniaTabruk.Domain/HusayniaTabruk.Domain.csproj:1-2`; mission architecture `architecture.md:48`).
- **FACT:** `Membership.Disable`, `AssignFoodIncharge`, and `RevokeFoodIncharge` accept only an actor ID; they validate target state/self-targeting but not the actor's current organization, status, or Admin role (`Membership.cs:73-76,93-100,117-124,144-159`).
- **FACT:** `RoleChangeRequest.Approve` accepts a caller-supplied administrator count, does not receive/revalidate the proposer, and treats every non-`Grant` enum value as `Revoke` (`RoleChangeRequest.cs:118-132,165-180,193-207`).
- **FACT:** Approval checks only the expiry upper bound; it does not reject an approval instant before proposal (`RoleChangeRequest.cs:143-150`).
- **FACT:** `AdministratorBootstrap` is publicly constructible with `IsSealed` held only in that instance (`AdministratorBootstrap.cs:7-17,73`).
- **FACT:** The independent T4 test reproduced approval of a revoked proposer's pending request and blocks T5 readiness (`test-results.md:95-107,125-130,160-164`).
- **FACT:** The approved architecture requires authoritative membership state, immediate revocation, two-person governance, a minimum of two active administrators, and a sealed one-time bootstrap (`architecture.md:242-245`).

## Decision and boundary

Add one Domain aggregate root, `OrganizationAccountGovernance`, as the consistency boundary for
membership disable, Food Incharge changes, administrator bootstrap, and administrator role
requests. It is rehydrated with the complete authoritative membership/request set for one
organization and owns the durable bootstrap seal plus an optimistic concurrency version.

`Membership` remains the entity holding membership status/roles. Its governance mutators become
`internal`; callers cannot bypass aggregate authorization or quorum checks. `RoleChangeRequest`
remains an entity, but creation/approval is public only through the aggregate.

## Concrete public contract

```csharp
public enum AdministratorBootstrapStatus { Unsealed, Sealed }

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
        Membership actor,
        Membership target,
        DateTimeOffset now);

    public Result<FoodInchargeAssigned> AssignFoodIncharge(
        Membership actor,
        Membership target,
        DateTimeOffset now);

    public Result<FoodInchargeRevoked> RevokeFoodIncharge(
        Membership actor,
        Membership target,
        DateTimeOffset now);

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
```

`Rehydrate` validates nonnegative version, unique IDs, same-organization membership/request
ownership, defined enum/status values, and consistent seal fields. Each operation rejects supplied
entities unless the exact entity instance is owned by this aggregate; this prevents look-alike or
stale actor substitution. T5 must load the complete organization governance state in one unit of
work.

`RoleChangeRequest` adds a rehydration factory for T5, but its `Propose`/`Approve` methods become
`internal`. Existing event record shapes remain unchanged.

## Invariants and error semantics

1. Actor/proposer/approver/target must be authoritative aggregate members in the same organization.
2. Disable/Food Incharge actor must be active and have `Admin`; actor and target are distinct.
3. Food Incharge target must be active; duplicate grant/revoke remains a conflict.
4. Disabling an active Admin or approving Admin revocation must leave at least two active Admins,
   derived from `Memberships`; no integer count is accepted.
5. Proposal and approval require a defined `AdministratorRoleChangeAction`. Use an exhaustive
   `switch`; undefined values return new validation code `invalid_administrator_role_change_action`
   and never mutate.
6. Approval revalidates the stored proposer against the supplied current proposer: matching ID,
   active, same organization, and still Admin. Revoked/disabled proposers cannot authorize.
7. `now` must be UTC. Approval requires `now >= ProposedAt && now < ExpiresAt`; otherwise return
   `role_change_request_chronology_invalid` or existing `role_change_request_expired`.
8. The application will supply `now` from `IClock.UtcNow`; no API timestamp is mapped to these
   parameters. Domain chronology remains independently enforced.
9. Bootstrap succeeds only while persisted state is `Unsealed`, with at least two distinct,
   authoritative, active, same-organization non-Admin memberships. Success grants all roles and
   changes the seal to `Sealed` atomically.
10. Every successful aggregate operation increments `Version` exactly once. Failure changes no
    entity, request, seal, or version.

Use existing error codes where applicable. Add only:
`invalid_account_governance_state`, `invalid_administrator_role_change_action`, and
`role_change_request_chronology_invalid`.

## Concurrency, failure, and T5 handoff

T5 maps `Version` to the organization governance concurrency token. A transaction writes the
organization version, affected membership/request rows, audit, notification, and outbox records
atomically using `OriginalVersion`; zero updated organization rows means `stale_version`, rollback,
reload, and re-evaluate. This prevents two concurrent revocations/disables or bootstrap attempts
from both committing. No retry exists in Domain.

A request whose proposer later loses authority may remain stored as `Pending`, but it is inert:
every approval revalidates the proposer. Eager request invalidation is rejected as unnecessary
cascade complexity.

## Migration from current T4 classes

- Add `OrganizationAccountGovernance.cs`.
- Replace `AdministratorBootstrap` with aggregate-owned seal state; retain
  `AdministratorBootstrapCompleted`.
- Make `Membership.Disable`, `AssignFoodIncharge`, `RevokeFoodIncharge`,
  `GrantAdministrator`, and `RevokeAdministrator` internal primitives invoked only by the root.
- Move public proposal/approval orchestration from `RoleChangeRequest` to the root; add exhaustive
  action validation and rehydration.
- Update current Account tests to construct one rehydrated root and call its methods. Do not add
  repository, EF, API, clock, step-up, audit, or notification code in T4.

This intentionally changes Domain public signatures. No shipped API exists, so no runtime client
compatibility window or data migration is required.

## Required tests

- Substituted inactive, non-Admin, cross-organization, and look-alike actor fails for disable,
  assign, and revoke with no mutation/version increment.
- Direct Admin disable at two Admins fails; at three succeeds and leaves two.
- Caller cannot supply a fake quorum count because no integer contract exists.
- Revoked and disabled proposers cannot approve an existing pending request.
- Undefined action fails at proposal and rehydration and never follows revoke behavior.
- Approval before proposal and at/after expiry fails; equal-to-proposal and one tick before expiry
  succeeds; non-UTC throws before mutation.
- Bootstrap seal survives rehydration; a second bootstrap fails. Two roots at the same version are
  documented for T5 stale-version integration coverage.
- Every success increments version once; every failure preserves aggregate and child state.

## Tradeoffs and risks

- **Chosen:** one organization governance boundary. It is the smallest boundary that can derive
  quorum and atomically own the seal/request/member mutations.
- **Rejected:** pass an integer count or a caller-created “AdminCount” value; neither proves
  completeness or freshness.
- **Rejected:** keep public entity mutators plus a service wrapper; callers could bypass controls.
- **Cost:** T5 must load the organization's governance membership set for writes. This is acceptable
  for low-frequency administration and avoids a second aggregate/service or lock protocol.
- **Risk:** incomplete hydration produces an incorrect quorum. Mitigation: strict repository
  contract, organization-version compare-and-swap, and T8/T10 real-database concurrency tests.

## Upstream document updates required

- Amend main architecture data model to persist bootstrap status/sealed-at and treat
  `organizations.version` as the account-governance concurrency token.
- Amend ADR-10 to name `OrganizationAccountGovernance`, proposer revalidation, trusted application
  time, and versioned sealed bootstrap.
- Amend implementation-plan T4 exit criteria with the tests above; amend T8/T10 to require complete
  aggregate hydration and stale-version rollback tests.

