# T6 Signup Remediation Architecture

Date: 2026-08-14  
Status: Implementation-ready; Domain only

## Evidence and current state

- **FACT:** `Signup` is the current mutable root. Approval, waitlisting, override, and reassignment accept caller-supplied signup collections (`HusayniaTabruk/src/HusayniaTabruk.Domain/Signups/Signup.cs:193-252,286-328`; `HelpNeedSignupExtensions.cs:9-42`).
- **FACT:** Capacity totals and the next waitlist order are derived only from those supplied collections, so omitting an approved or waitlisted signup can authorize overbooking or reuse an order (`SignupCapacity.cs:11-35`; `Signup.cs:238-251`).
- **FACT:** A signup persists organization and help-need IDs but not its service-date ID (`Signup.cs:13-52,129-190`). `Withdraw` therefore tries to authenticate a deadline from a supplied `ServiceDate` by organization and contained need ID (`Signup.cs:254-284`).
- **FACT:** Transition methods require UTC but do not require `now >= SubmittedAt/DecidedAt`; `TransitionTo` overwrites the timestamp directly (`Signup.cs:341-363`).
- **FACT:** Rehydration permits any positive version for a non-pending status, rather than versions reachable through the transition graph (`Signup.cs:158-174`). The focused test run failed the two unreachable-version cases and the counterfeit same-organization date case: 124 passed, 3 failed.
- **FACT:** Current tests encode participant composition, transition, capacity, waitlist, deadline, override, and reassignment behavior (`SignupTests.cs:13-620`; `T6IndependentSignupTests.cs:14-278`).
- **FACT:** T4R established the repository pattern: one aggregate owns the complete invariant set, deep-copies children, exposes `OriginalVersion`/`Version`, resolves owned entities, and increments once per successful command (`Accounts/OrganizationAccountGovernance.cs:7-37,39-103,167,204,228,252`).
- **FACT:** The main architecture already requires help-need locking/recomputed approved slots and selected—not automatic—waitlist promotion (`.ai-org/missions/2026-08-14-husaynia-tabarruk-signup/architecture.md:126-129`; `:25-27`).
- **FACT:** T8 persistence has not started and depends on T6, so this domain contract can change without a deployed data migration (`implementation-plan.md:287-313`).

## Decision

Introduce one `HelpNeedSignups` aggregate per help need. It owns the complete signup set for that
need plus immutable, canonical organization/date/need context for the unit of work. Capacity,
active-primary uniqueness, waitlist allocation, transition chronology, and reassignment are
calculated only from owned state.

`Signup` remains the child entity containing composition and transition metadata. Its mutators
become `internal`; callers select an owned child by `SignupId`. No command accepts a `Signup`
collection, selected `Signup`, `ServiceDate`, or `HelpNeed`.

## Public contract

```csharp
public sealed class HelpNeedSignups
{
    public OrganizationId OrganizationId { get; }
    public ServiceDateId ServiceDateId { get; }
    public HelpNeedId HelpNeedId { get; }
    public long OriginalVersion { get; }
    public long Version { get; private set; }
    public long WaitlistOrderHighWater { get; private set; }
    public IReadOnlyCollection<Signup> Signups { get; } // defensive deep copies

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
        SignupId signupId,
        SignupStatus targetStatus,
        DateTimeOffset now);
    public Result<SignupTransitioned> Cancel(SignupId signupId, DateTimeOffset now);
    public Result<WaitlistedSignupReassigned> Reassign(
        SignupId signupId,
        DateTimeOffset now);

    public IReadOnlyCollection<Signup> OrderedWaitlist();
}
```

Submission retains `Membership` inputs because eligibility is owned by the Accounts aggregate;
T6 must revalidate their current status, organization, eligibility, distinctness, and primary
exclusion. All signup/date/need selection uses IDs or aggregate-owned context—never caller-created
signup or deadline snapshots.

`Signup` adds immutable `ServiceDateId` and exposes:

```csharp
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
```

`Rehydrate` is a trusted persistence boundary, as in T4R. It must validate that the supplied need is
the exact need represented in the service date (ID, date ID, status, capacity, and version), then
copy scalar date/need context. Repository work in T8 must load the complete help-need signup set in
the same unit of work; a partial set violates the repository contract.

## Invariants and semantics

1. Every owned signup has the aggregate's organization, service-date, and help-need IDs; signup IDs
   are unique.
2. At most one active (`Pending`, `Approved`, `Waitlisted`) signup exists per primary membership.
3. Bounded capacity is participant slots. Rehydration rejects already-overallocated state.
   Approval/override/reassignment sum every owned approved signup and cannot exceed capacity.
4. `WaitlistOrderHighWater` is non-negative persisted aggregate state; zero means no order has ever
   been allocated. Every new waitlist assignment allocates `WaitlistOrderHighWater + 1` and advances
   it. Leaving the waitlist clears the child's order but never lowers the high-water value, so gaps
   remain and removed orders are never reused. Rehydration accepts a high-water value greater than
   the current maximum and rejects any current order that is non-positive, duplicated, or greater
   than it. At `long.MaxValue`, allocation fails atomically with `version_exhausted`. Presentation
   order remains `WaitlistOrder`, then canonical `SignupId` as a deterministic defensive tie-break.
5. Reassignment remains Food-Incharge-selected. It may skip earlier entries, but it cannot approve
   without available slots. Withdrawal/cancellation immediately releases capacity; promotion is
   never automatic.
6. The aggregate stores the canonical date ID, end, cancellation deadline, date status, need
   status, and capacity captured from the validated date/need pair. Commands never accept
   replacement context.
7. Submit/managed decisions requiring an open signup window reject a non-open date/need or
   `now >= EndsAt`. Withdrawal requires `now < CancellationDeadlineAt`. Override and cancellation
   retain the approved transition matrix.
8. Every timestamp is UTC and every transition requires
   `now >= (LastTransitionAt ?? SubmittedAt)`. Equality is allowed for deterministic clocks.
9. Reachable child versions are exact:
   - `Pending`: `0`
   - `Waitlisted`: `1`
   - `Approved`: `1..2`
   - `Declined`: `1..2`
   - `Withdrawn`: `1..2`
   - `Cancelled`: `1..3`
   Metadata must match status: pending has no transition/order; waitlisted has both; every other
   non-pending status has a transition instant and no order.
10. Every successful aggregate command increments aggregate `Version` exactly once. A child
    transition increments that child exactly once; submission creates child version `0`. Failure
    mutates neither root nor children. `OriginalVersion` never changes. The high-water value changes
    only on a successful new waitlist assignment and participates in the same atomic mutation.

Use existing error codes where applicable. Add only:

- `invalid_signup_aggregate_state` — validation, malformed/incomplete rehydrated state.
- `signup_not_owned` — validation, an ID is not owned by this aggregate.
- `signup_chronology_invalid` — conflict, command time predates submission/prior transition.

Persistence later maps a compare-and-swap miss to existing `stale_version`; Domain does not retry.

## Delta and T6R boundaries

Production files:

- Add `Signups/HelpNeedSignups.cs`.
- Change `Signup.cs`: add `ServiceDateId`, rename persisted transition timestamp to
  `LastTransitionAt`, enforce reachable versions/chronology, add `DeepCopy`, and make all mutation
  primitives internal.
- Change `SignupErrorCodes.cs` only for the three codes above.
- Delete `SignupCapacity.cs` and `HelpNeedSignupExtensions.cs`; their logic moves behind the root.
- Do not add repository, EF, API, application, locking, clock, audit, notification, or outbox code.

Tests:

- Replace direct child-mutator tests with `HelpNeedSignups` command tests.
- Add omission attacks: approval/reassignment always sees an owned approved signup even when the
  caller supplies only an ID; no collection parameter exists.
- Add duplicate waitlist-order and mixed organization/date/need rehydration rejection.
- Add counterfeit same-organization date regression proving withdrawal has no date parameter.
- Add all reachable/unreachable status-version boundaries, including `Cancelled` v3.
- Add pre-submission and pre-prior-transition command-time failures with no mutation.
- Add exact capacity, release, selected reassignment, active-primary uniqueness, overflow,
  defensive-copy, root/child/high-water version, and failure-atomicity coverage.
- Add retained-gap and removed-maximum regression coverage that rehydrates before the next
  assignment, plus invalid negative/below-current-order high-water hydration and permanent
  `long.MaxValue` exhaustion.
- Add reflection/compile-time assertions that unsafe public child mutators, collection-based
  capacity APIs, and the help-need extension no longer exist.

## Concurrency, failure, security, and observability

T8 persists a dedicated `help_needs.signup_version` concurrency token (not `HelpNeed.Version`,
which remains date/need-edit state) and
`waitlist_order_high_water bigint NOT NULL DEFAULT 0 CHECK (waitlist_order_high_water >= 0)`. A
write compares `OriginalVersion`, writes changed signup rows and high-water, and increments
`signup_version` atomically. T15/T16 must lock/CAS this root and rehydrate the full set plus
high-water; zero rows updated means rollback and `stale_version`. Parallel approval, waitlist
allocation, withdrawal/reassignment, and duplicate submission require real PostgreSQL tests later.

Domain has no external failure or retry path. Invalid or stale input fails before mutation.
Participant membership state crosses the Accounts-to-Signups trust boundary only at submission;
no names or new sensitive fields are copied. Application audit should record root version,
signup/need/date IDs, transition, actor, and capacity conflict without participant details.

## Tradeoffs, risks, and migration

- **Chosen:** help-need boundary. Capacity, waitlist order, and active-primary uniqueness are all
  per need, so a service-date-wide aggregate loads unrelated categories without strengthening T6.
- **Cost:** every signup write loads all signups for one need. This is required to prove
  completeness and is bounded by the operational roster; pagination remains a read concern.
- **Rejected:** keep public `Signup` mutators behind a helper service—callers could bypass it.
- **Rejected:** caller-supplied counts/collections—freshness and completeness cannot be proven.
- **Rejected:** service-date aggregate—larger contention/read set with no cross-need capacity rule.
- **Risk:** T8 hydrates a partial set. Mitigate with one repository method, a dedicated CAS token,
  no public alternate write path, and integration tests that deliberately omit/concurrently add.
- **Risk:** reducing capacity below approved slots makes rehydration invalid. T17 must load this
  aggregate and reject such a need edit with `capacity_unavailable`; it must not persist invalid
  state.

No deployed schema or API exists. Forward migration is a compile-time replacement of unsafe Domain
contracts before T8. Rollback is reverting T6R before persistence work begins; no data rollback is
needed. This is intentionally source-breaking but needs no CTO decision because no runtime client
or database contract has shipped.

## Main artifact updates required

1. Main `architecture.md`: name `HelpNeedSignups`, add `service_date_id` to signup state,
   `help_needs.signup_version`, durable `help_needs.waitlist_order_high_water`, complete
   hydration/CAS, chronology, exact reachable versions, and capacity-edit coordination.
2. Main `decisions.md`: add the accepted per-help-need authoritative aggregate ADR.
3. `implementation-plan.md`/`task-plan.md`: add T6R after T6; make T8, T12, T15, T16, and T17 depend
   on T6R; add the test and deletion gates above.
