# Husaynia Tabruk Architecture Decision Records

# ADR-1: Use Expo React Native for the mobile application
Date: 2026-08-14     Status: Proposed

## Context
The product is iPhone-first but must support Android later from one codebase. It is a forms, lists, notifications, and coordination application without a native-only capability requirement.

## Decision
Use TypeScript, React Native, and Expo development builds with Expo Router. Keep mobile use-case/view logic outside route components and consume a generated OpenAPI client.

## Consequences
Most mobile code and tests are reused on Android. The team accepts React Native/Expo upgrade discipline and may need limited native configuration. Production does not depend on Expo Go or OTA updates.

## Alternatives considered
Flutter - capable, but adds Dart/custom-rendering ecosystem and accessibility overhead without a requirement advantage.  
Swift then Kotlin/Compose - strongest platform specialization, but duplicates implementation and testing and makes later Android materially more expensive.

# ADR-2: Use a .NET modular monolith with framework-independent domain logic
Date: 2026-08-14     Status: Proposed

## Context
The adjacent repository already uses ASP.NET Core, Identity, EF Core, and Core/Infrastructure/Web separation. MVP workflows are transactional and cohesive; independent service scaling is not required.

## Decision
Use .NET 10 with Domain, Application, Infrastructure, and Api projects. Run HTTP and the outbox worker in one deployable process, while enforcing dependency direction with project references and architecture tests.

## Consequences
Deployment and local development remain simple, and transactions span signup, audit, notifications, and outbox records. A process failure affects all API functions, and module boundaries require active enforcement.

## Alternatives considered
Extend the existing MVC site - rejected because its browser/Identity UI contract is not a mobile API and its domain does not model the approved workflow.  
Microservices/serverless functions - rejected because they add distributed transactions, operations, and observability without measured scaling need.  
Backend-as-a-service - rejected because record-level workflow authorization, transactional capacity, audit, and provider portability would become harder to control.

# ADR-3: Use PostgreSQL as the single authoritative datastore
Date: 2026-08-14     Status: Proposed; CTO approval required for managed production service

## Context
Duplicate prevention, capacity approval, cancellation, waitlist reassignment, in-app notification, audit, and outbox writes require relational constraints and transactions. No production database or shared SQL Server infrastructure was verified.

## Decision
Use PostgreSQL 16+ through EF Core/Npgsql. Store operational data, Identity data, notifications, audit, idempotency receipts, and outbox rows in one database.

## Consequences
Concurrency rules can be proven using unique indexes, row locks, and atomic commits. A managed production instance creates cost; the database is a shared failure domain and requires backups/restores.

## Alternatives considered
SQL Server - compatible with adjacent code, but no reusable production instance was verified and licensing/managed cost may be higher; reconsider if the CTO confirms an existing supported service.  
SQLite - rejected for production server concurrency and operational durability.  
NoSQL - rejected because cross-record invariants and state transitions would require more application/distributed coordination.

# ADR-4: Own member authentication and enforce authorization from database state
Date: 2026-08-14     Status: Proposed

## Context
Only invited/approved members may enter; administrator role revocation must take effect promptly; roster/thread authorization is record-specific. A managed identity vendor would reduce credential operations but adds an external dependency and cost.

## Decision
Use ASP.NET Core Identity, short-lived access JWTs, and rotating opaque refresh tokens. Treat tokens as identity assertions only; load active membership, roles, organization, and record relationship on every protected request.

## Consequences
The system controls invite, disable, revocation, and audit semantics and avoids an identity SaaS dependency. The team owns password reset, token security, abuse controls, and credential incident response.

## Alternatives considered
Auth0/Clerk/Cognito/Entra External ID - rejected for MVP because cost/vendor setup is not justified; can replace the authentication adapter later while preserving membership authorization.  
Long-lived role-bearing JWTs - rejected because revocation would be delayed.  
Cookie-only Identity UI - rejected because it is a poor mobile-client contract.

# ADR-5: Use a transactional outbox and Expo Push Service
Date: 2026-08-14     Status: Proposed; CTO approval required

## Context
The in-app record must remain authoritative when push is delayed or fails. Adding a message broker would increase operations, while direct APNs now plus FCM later duplicates provider logic.

## Decision
Commit in-app notifications and outbox rows in the business transaction. A hosted worker sends generic push messages through an `IPushGateway` implemented initially with Expo Push Service, with retry, token invalidation, and dead-letter monitoring.

## Consequences
Business state never depends on successful push and no external queue is needed. Push can be delayed by the API/provider, and Expo is an external dependency that requires CTO acceptance.

## Alternatives considered
Direct APNs/FCM - rejected initially because it creates two provider adapters/credential paths; retain as a future adapter option.  
External queue - rejected because PostgreSQL leasing is sufficient for expected MVP volume and materially simpler.  
Synchronous push - rejected because provider latency/failure would couple to user transactions.

# ADR-6: Support bounded offline operation, not offline administration
Date: 2026-08-14     Status: Proposed

## Context
Members may lose connectivity, but approvals, capacity, cancellation, and reassignment are concurrency-sensitive and must never show false success.

## Decision
Cache authorized reads and queue only idempotent member commands with a visible `Pending sync` state. Require connectivity for Food Incharge/admin mutations and purge local data on logout/revocation.

## Consequences
Core member journeys tolerate interruption without implementing conflict-heavy offline replication. Food Incharges cannot manage a date while offline, and queued member actions can later be rejected by server rules.

## Alternatives considered
Full offline-first replication - rejected because resolving capacity, role, deadline, and moderation conflicts is disproportionate to MVP need.  
Online-only - rejected because it fails the explicit offline-tolerance and connectivity-loss acceptance criteria.

# ADR-7: Keep one moderated thread per date in MVP
Date: 2026-08-14     Status: Proposed

## Context
Eligible contacts and the Food Incharge need date-specific communication, while direct contact exposure and unrestricted messaging are prohibited.

## Decision
Create one access-controlled date thread with text-only immutable messages, reporting, hide, and lock operations. Do not provide DMs, attachments, edits, reactions, or hard deletion.

## Consequences
Communication is auditable and bounded to the operational context. It is less feature-rich than consumer chat and requires a retention/moderation policy before production.

## Alternatives considered
One-way announcements - rejected because the approved contract requires eligible contacts to use threads.  
General group chat/DMs - rejected because it expands privacy, safeguarding, moderation, and notification scope.  
Third-party chat service - rejected because it adds cost, data processing, and authorization integration without MVP justification.

# ADR-8: Restrict ordinary thread access to approved primary contacts
Date: 2026-08-14     Status: Accepted

## Context
The prior negative rule allowed any non-declined signup contact to read a private thread, including pending, waitlisted, withdrawn, and cancelled contacts. Reversing leaked access after implementation would require contract, cache, and authorization changes.

## Decision
Use an explicit default-deny matrix. Only an active managing Food Incharge and the active primary contact of an `Approved` signup may use the ordinary thread; revoking transitions remove access on the next request and cause mobile cache purge. Administrators use only the separate audited moderation-read contract.

## Consequences
Private conversations align with approval state and revocation is testable at every transition. Pending and waitlisted contacts do not receive thread access; their communication remains limited to signup-state notifications.

## Alternatives considered
Any non-declined signup - rejected because it grants access before approval and after participation ends.  
Waitlisted read-only access - rejected because no approved product need justifies expanding the private audience.

# ADR-9: Minimize signup participant identity in MVP
Date: 2026-08-14     Status: Accepted

## Context
Free-text participant names can identify minors and create indefinite retention, consent, visibility, and deletion obligations before policy approval.

## Decision
Store the named primary contact and references to active adult member accounts only. Represent other household/team participants by count and an optional non-identifying group label; do not collect their names, ages, or minor status.

## Consequences
Capacity and household/team workflows remain supported with materially less sensitive data. Rosters provide less individual detail, and named non-member/minor participants require a future safeguarding and retention decision plus migration.

## Alternatives considered
Free-text names with a warning - rejected because a warning does not prevent collection.  
Encrypt free-text names - rejected because encryption does not establish purpose, consent, retention, or access legitimacy.

# ADR-10: Use dual-control administrator governance and reusable step-up verification
Date: 2026-08-14     Status: Accepted

## Context
The product authorizes administrators to manage Food Incharge access but does not justify unilateral administrator propagation. A compromised admin session must not create durable peers or silently inspect private threads.

## Decision
Separate Food Incharge management from administrator governance. Use one versioned
`OrganizationAccountGovernance` aggregate, rehydrated with the complete authoritative membership
and role-request set for one organization, its durable bootstrap status/sealed instant, and its
optimistic version. Governance mutations on `Membership` and `RoleChangeRequest` are internal and
may be invoked only by this root; callers may not supply an administrator count, role snapshot, or
look-alike actor.

Administrator grant/revoke requires a 24-hour request, two distinct active administrators, no
self-assignment, five-minute single-use/purpose-bound step-up verification for both actions, and a
minimum of two active administrators after normal changes derived from aggregate state. Approval
must revalidate the stored proposer as a current active Admin, reject undefined action enum values,
and enforce `ProposedAt <= now < ExpiresAt`. Domain accepts deterministic UTC `now`; Application
must supply `IClock.UtcNow` and must never map an API timestamp into a governance transition.

Bootstrap is a one-time deployment command that initializes at least two distinct active non-Admin
memberships and atomically persists a sealed status/sealed instant. Every successful aggregate
operation increments the organization governance version exactly once. Persistence compares and
swaps the original organization version and rolls back membership/request/bootstrap, audit,
notification, and outbox writes on `stale_version`. Define `IStepUpVerifier`, initially backed by
password re-verification, for both role governance and privileged moderation reads.

## Consequences
Privilege persistence requires two compromised current administrators or deployment access, and
step-up can evolve without changing use-cases. Caller-controlled actor substitution, quorum,
bootstrap state, and timestamps are excluded from the contract. Governance writes must load the
complete organization state and serialize through one organization version, which costs a larger
low-frequency administrative read. Small organizations need two administrators after bootstrap,
and role changes take an extra action.

## Alternatives considered
Any admin may assign Admin - rejected because one compromised session creates durable organization-wide access.  
Public entity mutators or caller-supplied administrator counts - rejected because completeness,
freshness, and actor authority cannot be proven and callers can bypass the invariant boundary.  
Separate bootstrap/request aggregates - rejected because the same invariants would require
cross-aggregate locking and transactions.  
Introduce an external identity/governance service - rejected because the same control can be enforced inside the existing monolith without a new vendor or cost.

# ADR-11: Enforce bounded messaging and immutable privileged-read audit
Date: 2026-08-14     Status: Accepted

## Context
Immutable messages, reports, notifications, and outbox work amplify oversized or high-rate writes. Ordinary administrator thread reads also bypassed the stated purpose-limited moderation boundary.

## Decision
Enforce the concrete payload, pagination, account, and organization limits in `architecture.md` section 6. Privileged thread reads use a separate POST contract requiring purpose-scoped permission, recent step-up, reason/purpose/case ID, and an insert-only access event before every page is returned; audit failure denies the read.

## Consequences
Abuse has bounded storage/notification impact and privileged browsing becomes attributable and alertable. Legitimate burst activity may receive `429` and must retry after the supplied interval; database owners remain a higher trust boundary until segregated/tamper-evident export is approved.

## Alternatives considered
Only global web-server limits - rejected because they do not control per-account or per-organization harassment.  
Log privileged reads asynchronously - rejected because a failed log path would permit unaccountable disclosure.

# ADR-12: Make the complete per-help-need signup set authoritative
Date: 2026-08-14     Status: Accepted

## Context
Capacity, active-primary uniqueness, and waitlist order are per help need. The original T6 entity
surface accepted caller-selected signup collections and replacement date context, so omission or
counterfeit-context attacks could authorize overbooking, duplicate waitlist order, or an invalid
withdrawal deadline.

## Decision
Use one versioned `HelpNeedSignups` aggregate per help need. Rehydrate it with the authoritative
service date/help need and the complete signup set. It owns all signup mutation, capacity,
waitlist, chronology, and active-primary invariants; commands select children only by ID.
`Signup` stores immutable `ServiceDateId`, child mutators are internal, and persistence later uses
the dedicated `help_needs.signup_version` compare-and-swap token.

Persist a non-negative `HelpNeedSignups.WaitlistOrderHighWater` as
`help_needs.waitlist_order_high_water`. Rehydration receives it explicitly. Every successful new
waitlist assignment increments it and assigns that value; leaving the waitlist clears only the
child order. The high-water value never decreases, gaps are retained, and removed orders are never
reused. Allocation at `long.MaxValue` fails atomically with `version_exhausted`. T8 loads and writes
the high-water value with all changed signup rows under the same `signup_version` CAS.

Delete `SignupCapacity` and `HelpNeedSignupExtensions`; no collection-based capacity API,
replacement date/need command parameter, or public child mutation path remains.

## Consequences
Omission and context-substitution attacks are removed from the public domain contract. Every signup
write must hydrate the full per-need set, and T17 capacity edits must validate against the same
aggregate. Historical allocation state adds one help-need column and permits the high-water value
to exceed the maximum current order; this is required for auditable non-reuse. This is an
intentional source-breaking domain replacement before persistence exists.

## Alternatives considered
Caller-supplied collections or counts - rejected because completeness and freshness cannot be
proven.  
Public child mutators behind a service - rejected because callers can bypass the invariant root.  
One aggregate per service date - rejected because it widens reads and contention without adding a
cross-need invariant.
Derive the next order from the current maximum - rejected because removing the maximum permits
reuse after rehydration.  
Renumber or compact gaps - rejected because it rewrites history and increases write contention.

# ADR-13: Use owner psql orchestration as the sole production T8 path
Date: 2026-08-15     Status: Accepted

## Context
T8 contains concurrent DDL and multiple transaction boundaries. EF cannot retain one trusted
session-level serialization boundary through migration selection, suppressed transactions, final
attestation, and history mutation. Ambient name resolution and relation-only grants also do not
prove temp safety, object identity, schema CREATE denial, or future-object default privileges.

## Decision
Production T8 Up and Down use checked-in owner `psql` scripts only. Both hold the same
database/schema-scoped session advisory lock until disconnect, qualify fixed identifiers, attest by
namespace/object OID and canonical definition, and enforce exact schema/default ACL posture.
Upgrade compensates invocation-created concurrent-DDL objects and writes history last. Production
Down is logical: delete only T8 history while preserving the index, validated constraint, and
least privilege. EF Up/Down requires an explicit disposable/local flag; destructive EF Down also
requires every application table to be empty. The manifest pins seven artifacts including the
corrective designer and owner Down script.

## Consequences
Production loses `dotnet ef database update` and destructive rollback convenience. In exchange,
owner workflows serialize across all phases, temp/hostile drift fails closed, and Down never opens
an integrity or privilege gap. T9 remains blocked until the bounded T8M remediation and its
independent gates pass.

## Alternatives considered
Extend the EF factory lock across provider internals - rejected because no stable session boundary
covers history selection and suppressed transactions.  
Use non-concurrent index creation - rejected because it weakens live-table safety.  
Physically drop T8 objects in production Down - rejected because it creates a non-atomic
enforcement gap and violates least privilege.

# ADR-14: Require dual-control countersign before a redeemable invite URL is issued
Date: 2026-08-16     Status: Proposed; CTO approval required because this changes the frozen T10 admin invitation contract

## Context
T9 keeps one global email login and resolves exactly one active membership for that user
(`HusayniaTabruk/src/HusayniaTabruk.Application/Accounts/LoginService.cs:33-59`;
`HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Identity/Services/PostgresAuthenticationMembershipRepository.cs:247-257`).
T10 currently lets one admin obtain a raw invite URL after a single step-up and persists a
placeholder user/membership immediately
(`HusayniaTabruk/src/HusayniaTabruk.Application/Admin/Members/AdministratorMembershipService.cs:224-307`;
`HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Identity/Services/PostgresAuthenticationMembershipRepository.Admin.cs:45-123`).
Acceptance then sets the password and binds the global login email
(`HusayniaTabruk/src/HusayniaTabruk.Application/Accounts/AcceptInvitationService.cs:36-72`;
`HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Identity/Services/PostgresAuthenticationMembershipRepository.cs:149-245`).

With out-of-band admin delivery and no system-owned email verification, a single issuer who sees
the raw invite URL can always redeem an unused email. Existing claimed emails also fail only at
acceptance time, creating a tenant-existence oracle.

## Decision
Keep the current `/auth/invitations/accept`, `/auth/login`, placeholder membership model, and
one-time invite URL behavior. Change only invitation issuance so that the raw invite URL is not
returned unless two distinct active same-organization admins both approve the exact request.

`POST /admin/invitations` will require:
- issuer body: `email`, `expiresInHours`, `approvingMembershipId`;
- issuer header: `X-Step-Up-Token` for `membership.invite`;
- approver header: `X-Invite-Approval-Token` for
  `membership.invite.approve.v1:<request-hash>`.

The repository must reject issuance with stable `invitation_conflict` when the normalized email is
already claimed globally or is already reserved by another active issued invitation anywhere.
Expired/revoked invitations remain reissuable, and same-org reissue must reuse the existing invited
placeholder membership instead of creating duplicate pending members.

## Consequences
One compromised admin can no longer self-redeem an unused victim email. T9 global email login and
single-membership resolution stay unchanged, so T10 still does not support cross-organization
account linking. Invitation issuance becomes more deliberate and the frozen admin request contract
changes by one required body field and one required header.

## Alternatives considered
Keep the current single-admin issue flow - rejected because the issuer still knows the only secret
needed to redeem a brand-new email principal.  
Add transactional email / email proof-of-control now - rejected because it adds a deferred external
service and is explicitly out of scope for T10.  
Broaden T9 to support a non-email or multi-organization login identity - rejected because it is a
larger auth-model change than this mission needs.  
Add a separate invitation-request resource with propose/approve endpoints - rejected because it
adds more API surface and state than needed when a second purpose-bound step-up can countersign the
existing issuance endpoint.
