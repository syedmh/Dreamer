# T4 Account-Governance Remediation ADRs

# ADR-T4-1: Use one versioned organization account-governance aggregate
Date: 2026-08-14     Status: Proposed

## Context
Membership entity methods accept actor IDs, administrator quorum is caller-supplied, and bootstrap
sealing exists only in a resettable object instance. These controls require current organization
membership state and one concurrency boundary.

## Decision
Introduce `OrganizationAccountGovernance`, rehydrated with the complete organization membership and
role-request set, durable bootstrap state, and optimistic version. Make governance mutations on
`Membership` and `RoleChangeRequest` internal and expose them only through this root.

## Consequences
Authorization, quorum, request approval, bootstrap, and concurrency share one testable invariant
boundary. Governance writes load the organization's membership set and T5 must compare-and-swap the
organization version.

## Alternatives considered
Caller-supplied count/snapshot - rejected because completeness and freshness are not enforceable.  
Stateless domain service over public entity methods - rejected because callers can bypass it.  
Separate bootstrap and role-request aggregates - rejected because cross-aggregate transactions and
locking would be required for the same invariants.

# ADR-T4-2: Treat application clock time as trusted input and enforce chronology in Domain
Date: 2026-08-14     Status: Proposed

## Context
Current proposal/approval signatures accept timestamps without proving their source, and approval
does not reject an instant before proposal.

## Decision
Keep Domain independent of `IClock`, but name command parameters `now`, require the application to
supply `IClock.UtcNow`, and reject non-UTC, pre-proposal, and expired approval instants. API request
timestamps are never accepted for governance transitions.

## Consequences
Domain remains deterministic and unit-testable while the application owns the clock trust
boundary. A compromised application process remains trusted, as it already is for all Domain calls.

## Alternatives considered
Inject `IClock` into Domain - rejected because it adds an application/infrastructure concern to
pure domain logic.  
Accept request DTO timestamps - rejected because clients can backdate or future-date authority.
