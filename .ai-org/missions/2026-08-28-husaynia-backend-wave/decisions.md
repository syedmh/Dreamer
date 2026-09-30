# Husaynia backend wave decisions

Date: 2026-08-28  
Status: Accepted for implementation; subordinate to the master requirements/architecture/ADRs.

## D-01 — Preserve the completed foundation and compose only through discovery

**Decision:** T05/T06/T07/T08/T11/T12 add public parameterless Infrastructure modules,
configuration validators, and Web endpoint modules. They do not edit `Program.cs`, project files,
shared contexts, frozen contracts, Identity, Operations, Social, or migrations.

**Reason:** Current deterministic module/config/entity discovery already supplies every required
composition seam and keeps six branches disjoint.

## D-02 — Keep C2/C4 byte-compatible and place additions inside module namespaces

**Decision:** Existing `Husaynia.Application.Contracts` interfaces and DTOs remain unchanged.
Downstream catalog/admin/lifecycle interfaces are additive under `Husaynia.Application.<Feature>`.
Provider SDK types never cross Application ports.

**Rejected:** expanding frozen shared interfaces or allowing controllers to use EF/SDK clients.

## D-03 — Perform explicit endpoint policy checks without editing T04 middleware

**Decision:** Each admin endpoint explicitly invokes the existing named policy through
`IAuthorizationService`, derives the stable denial through `AdministrativeCapabilityAuthorizer`,
and audits denial before returning. The Application use case independently checks the frozen
capability/role. MFA and antiforgery remain mandatory.

**Reason:** T04's privileged endpoint metadata is internal to Identity. Manual endpoint invocation
of the same policy preserves endpoint enforcement/auditing without a shared-file collision.

## D-04 — Make audit ownership outcome-based and mutations atomic with their audit

**Decision:** Web owns policy/transport denials, Application owns capability/validation outcomes,
and Infrastructure stores own all authorized store outcomes. Store mutations and `AuditEvent` are
committed in one `HusayniaDbContext` transaction. Audit failure rolls back success. No layer retries
or duplicates finalization.

**Rejected:** best-effort post-commit audits, cross-context pseudo-atomic commits, or modifying T04.

## D-05 — Standardize feature persistence on discovered configurations and `Set<T>()`

**Decision:** Mutable roots use `MutableEntityConfiguration<T>`; immutable records use explicit EF
configuration. No feature adds a DbSet or modifies a context. Uniqueness/check/concurrency rules are
named and database-enforced.

## D-06 — Reserve every migration and snapshot change for T18

**Decision:** Features own configurations, custom invariant SQL constants, tests, and exact
`t18-schema-handoff.md` packets. T18 alone authors/applies migrations/snapshot and consumes the exact
reserved trigger names/error numbers.

**Rejected:** runtime DDL, feature migrations, or T18 independently renaming module objects.

## D-07 — Keep external I/O outside SQL and use explicit replay fences

**Decision:** Prayer/provider, Blob, outbound message, and Stripe calls never run under SQL locks.
SQL phases use rowversion, unique keys, canonical SHA-256 request hashes, serializable boundaries
where required, and compensating/reconciliation state for cross-resource workflows.

**Rejected:** distributed transactions, automatic retries around provider writes, and
last-write-wins.

## D-08 — Use T19 as the only durable job runtime

**Decision:** Prayer refresh, Blob cleanup, form delivery, and donation reconciliation register
strict module handlers/definitions through T19. Payloads carry identifiers only; recurring
idempotency derives from persisted/due generations. No module adds a worker, queue, or scheduler.

## D-09 — Hold production defaults behind the T01 evidence gate

**Decision:** Existing blocked baseline artifacts are observations, not approved defaults. Tests use
synthetic safe fixtures. Production form definitions, donation categories/modes/amounts, prayer
profile/tolerance, recurrence inventory, provider enablement, and media allowlist remain empty or
disabled until T01/authorized export reconciliation.

**Rejected:** inferring hidden widget/payment/provider behavior from markup or plugin assets.

## D-10 — Use immutable revision pointers for Content and Calendar

**Decision:** Content and Calendar roots are mutable rowversioned identities with immutable draft and
published revisions. Publish changes a pointer atomically; rollback copies a prior revision into a
new revision. Public reads follow only the published pointer.

**Reason:** This applies ADR-003 consistently and makes migration conflict/checksum behavior stable.

## D-11 — Materialize Calendar occurrences synchronously with explicit DST ambiguity

**Decision:** Calendar stores local wall time plus `America/Los_Angeles`, rejects invalid times,
requires explicit earlier/later resolution for ambiguous times, supports a bounded RFC recurrence
subset, and materializes occurrences in the publish transaction. Unsupported live rules fail
explicitly instead of approximation.

## D-12 — Make Prayer public reads snapshot-only

**Decision:** Profiles are immutable and hashed; generated/provider values are persisted per date;
overrides apply last; public month reads never resolve/call a provider. Stale complete data is
labeled, missing/incomplete data is unavailable. The deterministic local calculator is the default
port implementation; no Snohomish values are defaulted while T01 is blocked.

## D-13 — Treat media versions as immutable cross-resource state

**Decision:** Upload validates and transforms before storage, stages an immutable pending Blob,
commits SQL metadata/current pointer/audit, then clears pending state. Exact key+ETag compensation
and a DB-reference-aware cleanup job handle faults. Aliases remain stable across replacement.

**Rejected:** filesystem upload, overwrite-in-place, extension-only validation, SVG/active content,
and deletion based only on an old pending marker.

## D-14 — Persist Forms submission and delivery job exactly once

**Decision:** Versioned generic definitions drive validation. Antiforgery/honeypot/rate limit precede
persistence. A keyed privacy-safe fingerprint plus deterministic duplicate window and unique
constraint return the prior receipt. One transaction writes submission/values/job. Delivery is a
T19 handler and provider failures never lose the submission.

## D-15 — Make verified Stripe webhooks the sole completion authority

**Decision:** Checkout uses two idempotent local SQL phases around one same-key Stripe call. Browser
success/cancel is read-only. A verified webhook event, donation transition, and one campaign ledger
row commit serializably with unique provider IDs. Raw payment body/signature/card data is never
stored or exposed.

## D-16 — Add a serialized package-owner prerequisite instead of violating ownership

**Decision:** PX-01, owned only by the T02 foundation/package owner, supplies exact audited
non-preview dependencies for sanitizer, independent iCal parser, image transcode + Azure Blob/
identity, and Stripe. It changes references/locks only; feature owners add no package/project edits.

**Consequence:** T07/T11 and most domain/application work can start immediately. T05/T06/T08/T12
cannot pass their named production-adapter/final test gates until PX-01 is available. NU1900 remains
an external fail-closed audit blocker, not a waiver.

## D-17 — Freeze downstream access through published/status ports

**Decision:** T10/T13/T14/T15/T16/T17 consume Content/Calendar/Prayer/Media/Forms/Donations
Application ports. They do not query another module's tables or depend on Infrastructure entities.
T18 consumes model configurations/handoffs only.

## D-18 — Require independent gates before schema consolidation

**Decision:** Each feature retains real Domain/Application/Integration/Web/contract/architecture
results, strict Release build, no-live/no-runtime-DDL evidence, and independent test/code review.
T05/T08/T11/T12 security-sensitive boundaries require independent security review before T18.

## Consequences and residual risk

The wave is parallel-safe and migration-safe, but it intentionally does not claim live fidelity.
PX-01 and T01 are visible dependencies. No deployment, migration, package selection, production
secret, real form/payment action, commit, or push is authorized here.
