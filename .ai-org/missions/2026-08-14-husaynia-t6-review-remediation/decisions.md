# T6 Signup Remediation ADRs

# ADR-T6R-1: Use one versioned signup aggregate per help need
Date: 2026-08-14     Status: Accepted

## Context
Capacity, active-primary uniqueness, and waitlist order are per help need. Current entity methods
accept caller-selected signup collections, so completeness and freshness cannot be enforced.

## Decision
Introduce `HelpNeedSignups`, rehydrated with the complete signup set and canonical date/need
context. It owns all signup mutations and a dedicated optimistic version; child mutators become
internal and commands select children by ID.

## Consequences
Omission-based overbooking and duplicate waitlist allocation are removed from the public command
surface. Persistence must load all signups for one need and compare-and-swap
`help_needs.signup_version`.

## Alternatives considered
Caller-supplied collections/counts - rejected because completeness cannot be proven.  
Public child mutators plus a service wrapper - rejected because callers can bypass the invariant.  
One aggregate per service date - rejected because it widens reads/contention without a cross-need
capacity invariant.

# ADR-T6R-2: Bind every signup to an immutable service-date identity and context
Date: 2026-08-14     Status: Proposed

## Context
`Signup` stores `HelpNeedId` but no `ServiceDateId`; withdrawal accepts a replaceable
`ServiceDate`, allowing a same-organization forged date/need graph to supply another deadline.

## Decision
Persist `ServiceDateId` on every signup. Validate the service-date/help-need pair only while
rehydrating the root, copy its canonical deadline/window/status/capacity, and remove date/need
parameters from commands.

## Consequences
Deadline and capacity context cannot be substituted during a transition. Each command unit of work
must rehydrate the root from current authoritative date/need rows.

## Alternatives considered
Keep `Withdraw(ServiceDate, now)` and strengthen containment checks - rejected because callers still
provide the authority-bearing snapshot.  
Store only the deadline on `Signup` - rejected because it does not prove which date supplied it and
would become stale when the date changes.

# ADR-T6R-3: Persist monotonic waitlist allocation and keep selected reassignment
Date: 2026-08-14     Status: Accepted

## Context
The approved product contract makes promotion Food-Incharge-selected, while current order
allocation can be duplicated by omitting entries.

## Decision
Persist a non-negative `WaitlistOrderHighWater` inside `HelpNeedSignups` and rehydrate it
explicitly. Every new waitlist assignment increments the high-water value and uses the resulting
positive order. Clearing an order never lowers the high-water value; gaps remain and removed orders
are never reused. Allocation at `long.MaxValue` fails atomically with `version_exhausted`.

Expose deterministic ordering by order then signup ID, but allow `Reassign(signupId, now)` to
select any waitlisted signup that fits current capacity. T8 stores
`help_needs.waitlist_order_high_water` and writes it with child rows under the same
`help_needs.signup_version` compare-and-swap.

## Consequences
Order is stable, auditable, and collision-free across removal and rehydration, while operational
discretion is preserved. The high-water value may exceed every current order and requires one
additional non-null help-need column.

## Alternatives considered
Automatic FIFO promotion - rejected because it changes the approved product behavior.  
Renumber after every removal - rejected because it creates unnecessary writes and concurrency.  
Derive from the current maximum - rejected because removing the maximum permits historical order
reuse after rehydration.  
Timestamp-only order - rejected because equal timestamps require another persisted or implicit
ordering rule and would be a larger migration.

# ADR-T6R-4: Enforce exact reachable child versions and monotonic transition time
Date: 2026-08-14     Status: Proposed

## Context
Current rehydration admits unreachable status/version pairs, and commands can backdate a transition
before submission or a prior decision.

## Decision
Validate the finite status/version ranges defined in the architecture and require every UTC command
time to be no earlier than `LastTransitionAt ?? SubmittedAt`. Equality is allowed.

## Consequences
Persisted state corresponds to a reachable transition history and timestamps never move backward.
The model still records only the latest transition instant; full history remains the audit/event
store's responsibility.

## Alternatives considered
Accept any positive version - rejected because corrupt/impossible state crosses persistence.  
Store full transition history in the aggregate - rejected because audit persistence is later work
and is not needed to enforce T6 invariants.
