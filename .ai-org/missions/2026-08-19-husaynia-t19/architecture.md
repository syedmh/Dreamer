# T19 architecture

Date: 2026-08-19  
Status: NEEDS_DECISION

## Current state (FACT)

- No T19 Application/API/test slices exist yet: `glob src/HusayniaTabruk.Application/Threads/**`, `src/HusayniaTabruk.Api/Endpoints/V1/Threads/**`, `tests/HusayniaTabruk.Application.Tests/Threads/**`, and `tests/HusayniaTabruk.IntegrationTests/Threads/**` all returned no matches.
- Thread domain behavior already exists. Ordinary access is concealed unless the actor is the current managing Food Incharge or the primary contact of an `Approved` signup, and only the current manager may hide/lock: `src/HusayniaTabruk.Domain/Threads/DateThread.cs:137-147,163-202,235-323,376-449`; concealed ordinary denial is `NotFound`: `src/HusayniaTabruk.Domain/Threads/ThreadErrorCodes.cs:21-23`.
- Thread persistence already exists. `date_threads`, `thread_messages`, `message_reports`, and `privileged_access_events` are mapped now, and `privileged_access_events.case_id` already permits 200 chars: `src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContext.cs:372-433,560-570`. The initial migration created the same tables: `src/HusayniaTabruk.Infrastructure/Migrations/20260815075156_InitialPostgresSchema.cs:382-402,536-738`.
- The thread repository already supports `GetAsync`, `GetByServiceDateAsync`, and CAS `SaveAsync` with atomic `UPDATE date_threads ... WHERE ... version = @expectedVersion`: `src/HusayniaTabruk.Application/Abstractions/Persistence/ThreadPersistencePorts.cs:10-12,22-29,42-51`; `src/HusayniaTabruk.Infrastructure/Persistence/Repositories/PostgresAggregateRepositories.cs:2090-2215,2278-2469`.
- T17 already preserves “close without thread does not create/backfill thread”: `tests/HusayniaTabruk.IntegrationTests/Dates/CloseCancelConcurrency/DateManagementIntegrationTests.cs:74-108`.
- Step-up already exists as a single-use, purpose-bound token stored in `identity_user_tokens`, consumed under `FOR UPDATE`, with purpose mismatch and replay checks: `src/HusayniaTabruk.Infrastructure/Identity/Services/IdentityStepUpVerifier.cs:22-25,78-142,167`; lifetime is fixed at five minutes: `src/HusayniaTabruk.Domain/Common/ApplicationLimits.cs:25-28`; `src/HusayniaTabruk.Infrastructure/Identity/Services/TabrukAuthOptions.cs:5-24`.
- Privileged access audit types and insert-only DB permissions already exist: `src/HusayniaTabruk.Application/Abstractions/Audit/AuditPorts.cs:16-20,73-116`; `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:4863-4886`; `src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md:88-90`.
- API conventions already provide endpoint auto-discovery, body-byte limits, and `429 Retry-After`: `src/HusayniaTabruk.Api/Configuration/ApiEndpointExtensions.cs:5-42`; `src/HusayniaTabruk.Api/Program.cs:50,63-88`; `src/HusayniaTabruk.Api/Middleware/RequestBodyLimitMiddleware.cs:5-56`; `src/HusayniaTabruk.Api/Middleware/ApiRateLimitMiddleware.cs:433-447`.
- The checked-in OpenAPI snapshot currently has no thread routes (`rg "thread/messages|thread/lock|moderation/thread-reads" docs/api/openapi.json` returned no matches), and snapshot drift is already guarded by contract test: `tests/HusayniaTabruk.Api.ContractTests/Conventions/ApiConventionTests.cs:16-23`.

## Desired state

Add a T19 thread slice that reuses the existing Domain thread aggregate and current PostgreSQL schema, adds no migration, keeps ordinary operations service-date scoped, and adds one separate privileged historical read with atomic step-up consumption plus access-event audit.

## Delta / file map

### In scope

- `src/HusayniaTabruk.Application/Threads/ThreadService.cs`
- `src/HusayniaTabruk.Application/Threads/ThreadContracts.cs`
- `src/HusayniaTabruk.Application/Threads/ThreadStepUpPurposes.cs`
- `src/HusayniaTabruk.Application/Threads/ThreadMembershipLookupPorts.cs`  
  Partial `IMembershipRepository` addition for read-only sender-display lookup.
- `src/HusayniaTabruk.Api/Endpoints/V1/Threads/ThreadEndpoints.cs`
- `src/HusayniaTabruk.Api/Endpoints/V1/Threads/ThreadEndpointSupport.cs`
- `tests/HusayniaTabruk.Application.Tests/Threads/**`
- `tests/HusayniaTabruk.Api.ContractTests/Threads/**`
- `tests/HusayniaTabruk.IntegrationTests/Threads/**`
- `docs/api/openapi.json` (mandatory snapshot drift only)

### Mandatory scope expansion beyond the current allowlist

- `src/HusayniaTabruk.Infrastructure/Identity/Services/PostgresAuthenticationMembershipRepository.Threads.cs`  
  Needed to implement the new read-only membership-display lookup. Without one DB-backed lookup for arbitrary author membership IDs, T19 cannot produce the required sender display for ordinary participants or the exact privileged page hash.

## Contracts

### Ordinary routes

- `GET /api/v1/dates/{dateId}/thread/messages?cursor=&pageSize=`  
  `200` + `ETag`; body `ThreadMessagePageResponse { serviceDateId, status, lockedAt, items[], nextCursor }`.  
  `items[]` = `{ id, senderDisplayName, body, visibility, createdAt }`. Hidden content maps to a fixed redaction string; no phone/email/user/membership IDs are returned.  
  `404` for ineligible actor, ordinary admin, missing/cross-org thread, or missing date.

- `POST /api/v1/dates/{dateId}/thread/messages`  
  Headers: `Idempotency-Key`, `If-Match`. Body: `{ body }`.  
  `201` + `ETag`; response `ThreadMessageResponse`.  
  Send rate limits: `ApplicationLimits.ThreadPostsPerTenSecondsPerAccount`, `...PerMinutePerAccount`, `...PerHourPerAccount`, `...PerHourPerOrganization` (`src/HusayniaTabruk.Domain/Common/ApplicationLimits.cs:30-33`).  
  If `If-Match` is stale, only an exact existing replay of the same client key/author/body succeeds; otherwise `412`.

- `POST /api/v1/dates/{dateId}/thread/messages/{messageId}/report`  
  Header: `If-Match`. Body: `{ reason, comment? }`.  
  `202` no body. Duplicate `(message, reporter)` reports remain successful no-ops.  
  Report limits: `ApplicationLimits.ReportsPerHourPerAccount`, `...PerDayPerAccount`, `...PerDayPerOrganization` (`src/HusayniaTabruk.Domain/Common/ApplicationLimits.cs:35-37`).

- `POST /api/v1/dates/{dateId}/thread/messages/{messageId}/hide`  
  Header: `If-Match`. Body: `{ reason }`.  
  `200` + `ETag`; response is the now-redacted `ThreadMessageResponse`.  
  Only the current managing Food Incharge succeeds; everyone else gets concealed `404`.

- `POST /api/v1/dates/{dateId}/thread/lock`  
  Header: `If-Match`. Body: `{ reason }`.  
  `200` + `ETag`; body `ThreadStateResponse { serviceDateId, status, lockedAt }`.  
  Only the current managing Food Incharge succeeds; everyone else gets concealed `404`.

### Privileged route

- `POST /api/v1/admin/moderation/thread-reads`  
  Header: `X-Step-Up-Token`. Body: `{ serviceDateId, reason, purpose, caseId, cursor?, pageSize? }`.  
  `200`; body `PrivilegedThreadMessagePageResponse { threadId, serviceDateId, status, lockedAt, items[], nextCursor }`.  
  `items[]` = `{ id, senderDisplayName, body, visibility, createdAt, hiddenAt }`; privileged reads return stored bodies, including hidden originals.  
  `purpose` uses existing `PrivilegedAccessPurpose` values (`support|moderation|safeguarding`): `src/HusayniaTabruk.Application/Abstractions/Audit/AuditPorts.cs:16-20`.  
  `caseId` is trimmed printable ASCII only, required, and `<= 100` even though persistence permits 200.

## Authorization / transaction sequence

### Ordinary read and mutations

1. Start `IUnitOfWork.ExecuteAsync(...)` so thread/date/signup reads share one DB transaction.
2. Re-resolve the actor from live DB through `IMembershipRepository.ResolveActiveActorAsync(...)`: `src/HusayniaTabruk.Infrastructure/Identity/Services/PostgresAuthenticationMembershipRepository.cs:49-55,260-304`.
3. Load the service date and existing persisted thread by `serviceDateId`.
4. Build the actor’s current eligibility from live state:
   - manager path = active actor currently has `FoodIncharge` and still owns `service_dates.manager_membership_id`;
   - participant path = scan each help-need aggregate on the date with existing `ISignupRepository.GetAsync(...)` until a current `Approved` primary-contact signup is found.
5. Rehydrate a Domain `Membership`, pass the live `ServiceDate` and the found `Signup?` into the existing Domain thread method (`AuthorizeRead/Post/Report/Hide/Lock`).
6. For mutations, honor `If-Match` first, except exact duplicate send/report retries may still succeed from the current persisted state; all other stale versions return `412`.
7. Persist via the existing thread repository CAS save. No retries after `stale_version`.

### Privileged read

1. Start `IUnitOfWork.ExecuteAsync(...)`.
2. Re-resolve the actor from live DB and require active Admin.
3. Consume the step-up using a new purpose constant (recommended: `thread.privileged.read`) through the existing `IStepUpVerifier`.
4. Load the thread by `serviceDateId`; if absent, return `404` and let the ambient transaction roll back the step-up consume.
5. Build the exact response page, resolve sender display names, hash the exact response payload, write one `PrivilegedAccessEntry`, then commit.
6. Any failure after step-up consume but before commit returns no content and rolls the token back with the transaction.

## Data / migration

- **No migration.** The thread, report, moderation-event, and privileged-access tables already exist, and `case_id` already stores up to 200 chars while T19 only needs stricter application validation: `src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContext.cs:372-433,560-570`.

## Observability / privacy / security

- Do not add any T19 push, outbox, or notification write. Production code currently emits thread-access-change outbox only from signup/date transitions, and `NotificationType.ThreadMessagePosted` is defined but unused in production code: `src/HusayniaTabruk.Application/Dates/Management/DateManagementService.cs:695-712`; `src/HusayniaTabruk.Application/Signups/Cancellation/SignupCancellationService.cs:415-432`; `src/HusayniaTabruk.Application/Signups/Reassignment/SignupReassignmentService.cs:286-299`; `src/HusayniaTabruk.Domain/Notifications/Notification.cs:9,24,99`.
- Ordinary hide/lock should append existing audit + moderation-event effects through `ThreadPersistenceEffects`; ordinary send/report add no push/outbox and should not log body text.
- Reuse the existing test log collector pattern for “body nowhere in logs” assertions: `tests/HusayniaTabruk.IntegrationTests/Auth/AuthApiHost.cs:41-50,97-101`; `tests/HusayniaTabruk.IntegrationTests/Auth/AuthIntegrationTests.cs:1620-1624`.

## Tests to add

- **Application** `tests/HusayniaTabruk.Application.Tests/Threads/ThreadServiceTests.cs`
  - ordinary 404 matrix: pending/waitlisted/declined/withdrawn/cancelled/admin/no-thread/cross-org
  - immediate revocation after `Approved -> Withdrawn/Cancelled`, date cancel, manager revocation
  - stale `If-Match` handling, duplicate send replay, duplicate report replay
  - privileged read: step-up expiry/replay/purpose mismatch, ASCII/length checks, audit fail closes
- **API contract** `tests/HusayniaTabruk.Api.ContractTests/Threads/*`
  - route table + OpenAPI snapshot + required headers/statuses
  - DTO privacy: no email/phone/userId/membershipId in ordinary responses
  - `413`/`429`/`Retry-After` on send/report
- **Integration** `tests/HusayniaTabruk.IntegrationTests/Threads/*`
  - real PG authorization-transition matrix
  - hidden-message redaction on ordinary reads and full body on privileged reads
  - no thread creation/backfill on missing thread, plus explicit rerun of existing T17 regression `tests/HusayniaTabruk.IntegrationTests/Dates/CloseCancelConcurrency/DateManagementIntegrationTests.cs:74-108`
  - privileged audit rollback denies page and preserves step-up when insert cannot commit
  - outbox/log assertions: no message body in logs; no new push or thread-message outbox rows

## Decision needed

Approve one minimal infrastructure scope expansion for sender-display lookup (`src/HusayniaTabruk.Infrastructure/Identity/Services/PostgresAuthenticationMembershipRepository.Threads.cs`). Staying strictly inside the current allowlist would leave T19 unable to return required sender display names or compute an exact privileged page hash over the real response.
