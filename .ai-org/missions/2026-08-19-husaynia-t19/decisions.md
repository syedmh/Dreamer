# ADR-1: Keep T19 service-date scoped and reuse the existing thread aggregate/schema
Date: 2026-08-19     Status: Accepted

## Context
The repository already contains the `DateThread` aggregate, `IThreadRepository` load/save ports, PostgreSQL thread tables, and the T17 no-backfill close behavior. The authorized T19 slices do not contain any current Threads Application/API implementation.

## Decision
Implement T19 as new `Application/Threads/**` and `Api/Endpoints/V1/Threads/**` slices that call the existing Domain thread aggregate and existing thread persistence APIs. Keep ordinary routes service-date scoped so send/read/report/hide/lock can reuse `GetByServiceDateAsync(...)` and avoid a schema change or a new thread/message lookup contract.

## Consequences
This keeps T19 incremental and preserves the existing PostgreSQL CAS/chronology rules. It also means ordinary report/hide routes are nested under the service date instead of using root `/messages/{id}` paths.

## Alternatives considered
Root message-scoped routes - rejected because they force new persistence lookup APIs or broader scope expansion.  
New schema or migration - rejected because the current schema already contains the required thread/audit tables.

# ADR-2: Do not emit thread-message notifications or push/outbox work in T19
Date: 2026-08-19     Status: Accepted

## Context
The product already has outbox/push plumbing for signup/date events, but no production code currently emits thread-message notifications. T19 explicitly excludes T20 notification delivery, push, and worker scope, and message bodies must not appear in logs, outbox payloads, or push payloads.

## Decision
T19 thread send/report/hide/lock and privileged read create no new notification, push, or generic thread-message outbox records. T19 persists only thread rows, reports, moderation events, and the required hide/lock/privileged-read audit entries.

## Consequences
The feature meets the current contract without leaking message bodies into downstream payloads or pulling in T20 work. If product later wants participant notifications for thread activity, that must be a separate contract with its own privacy review.

## Alternatives considered
Emit `notification.push_requested` for thread posts - rejected because T19 excludes push/worker ownership and production code does not already own a thread-message notification contract.  
Emit a generic `thread.updated` intent now - rejected because it still expands ownership beyond the stated T19 contract.
# T19 scope decisions

## D-001 — minimal sender-display lookup expansion approved

**Status:** accepted  
**Date:** 2026-08-19  
**Decision:** T19 may add the smallest read-only sender-display lookup implementation in `src/HusayniaTabruk.Infrastructure/Identity/Services/PostgresAuthenticationMembershipRepository.Threads.cs`.

**Why:** The ordinary and privileged page contracts require privacy-safe sender display. The existing thread aggregate persists sender identity, but T19 has no authorized application/API source from which a display can be safely resolved. The architecture review found an existing identity-membership repository to extend; a dedicated read-only partial prevents a new cross-cutting persistence abstraction or schema change.

**Guardrails:** No direct contact fields; no mutation; no notifications/outbox/push; no schema migration. The implementation must use only the live, same-organization membership/account data required to render an existing message, and it must be covered by focused real-PostgreSQL tests.

## D-002 — migration not authorized

**Status:** accepted  
**Date:** 2026-08-19  
**Decision:** Do not create a migration. Existing schema/mapping already provides date threads, messages, reports, and insert-only privileged access event storage including `case_id`.
