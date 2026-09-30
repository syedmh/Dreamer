# Husaynia Tabruk Architecture

Date: 2026-08-14  
Status: Proposed; security-review remediations incorporated; implementation-ready except for CTO-reserved vendor/cost decisions

## 1. Recommendation

Build an iPhone-first **Expo/React Native TypeScript app** that can later ship the same UI and feature code to Android. Back it with a **.NET 10 modular monolith**, PostgreSQL, and a database-backed outbox; keep domain/application logic independent of Expo, HTTP, EF Core, PostgreSQL, and push providers. Deploy one API process and one relational database initially—no microservices, external queue, cache, or payment integration.

## 2. Current state

### Verified FACTs

- `HusayniaTabruk/` contains no application, configuration, dependency, or test files (verified by recursive directory inspection on 2026-08-14).
- The approved contract requires invited/approved access, administrator-controlled Food Incharge authority, approval/waitlist workflows, household/team primary contacts, hidden direct contact details, date threads, and push attempts (`requirements.md:3-19`, `requirements.md:21-32`).
- Correctness under duplicate/concurrent submission, cancellation deadlines, waitlist reassignment, connectivity loss, and failed push is acceptance-critical (`requirements.md:47-64`, `requirements.md:68-71`).
- Later scope is planning/cost records, then quote requests; initial marketplace payments remain outside the app (`requirements.md:35-43`).
- The adjacent `Husaynia` application establishes repository familiarity with a Core/Infrastructure/Web dependency direction, ASP.NET Core, Identity, EF Core, and a hosted background service (`Husaynia/Husaynia.Web/Program.cs:10-49`; `Husaynia/Husaynia.Infrastructure/Husaynia.Infrastructure.csproj:3-19`).
- The adjacent application is not a reusable mobile/API implementation: it is MVC/Razor with SQL Server, and no test files were found. Its authentication also permits unconfirmed accounts, which does not satisfy this product's invite/approval contract (`Husaynia/Husaynia.Web/Program.cs:13-29`, `Husaynia/Husaynia.Web/Program.cs:85-89`).
- The pre-implementation security review found one High and four Medium issues in the proposed contracts: non-approved thread access, unaudited administrator thread reads, named-minor collection risk, unconstrained administrator propagation, and missing message/report abuse limits (`security-review.md:46-113`).

### Assumptions

- One Husaynia organization launches first, but every protected row is organization-scoped to make authorization explicit and avoid a later destructive tenant migration.
- A signup targets one date/help category. A primary contact may submit separate signups for multiple categories.
- Capacity is measured in participant slots; an unlimited category has `capacity = null`.
- Waitlist promotion is Food Incharge-selected, not automatic. The system prevents over-allocation and alerts the Food Incharge when capacity becomes available.
- Household/team members without an active adult membership are represented only by a count and optional non-identifying label. Names, ages, minor status, allergy/medical data, certifications, attachments, direct messages, and unrestricted chat are not collected in MVP.

## 3. Target components and dependency direction

```text
Mobile UI -> mobile use-cases -> generated API client
                                      |
                                      v
HTTP API -> Application use-cases -> Domain
             ^                    ^
             |                    |
       Infrastructure ------------+
       (EF, auth, push, clock)
```

### Components

1. **Mobile app**: Expo Router screens, accessible React Native components, server-state cache, limited offline command queue, SecureStore tokens, notification/deep-link handling.
2. **API host**: versioned JSON API, authentication, request validation, authorization policies, OpenAPI, rate limits, health endpoints, and the outbox worker.
3. **Application layer**: commands/queries, transaction boundaries, idempotency, authorization requirements, DTO mapping, and ports such as `IClock`, `IPushGateway`, and repositories.
4. **Domain layer**: date, need, signup, capacity, cancellation, role, thread-eligibility, and transition rules. It has no framework/storage references.
5. **Infrastructure**: EF Core/PostgreSQL mappings, Identity/token persistence, Expo push adapter, structured telemetry, and migrations.
6. **PostgreSQL**: the sole system of record, including in-app notifications, audit, idempotency receipts, and outbox jobs.

## 4. Exact proposed repository structure

```text
HusayniaTabruk/
  README.md
  HusayniaTabruk.sln
  Directory.Build.props
  docker-compose.yml                 # local PostgreSQL only
  docs/
    api/openapi.json                 # generated/verified contract artifact
    privacy-data-inventory.md
    operations.md
  apps/mobile/
    app.json
    eas.json
    package.json
    tsconfig.json
    app/                             # Expo Router routes only
    src/
      features/{auth,dates,signups,roster,threads,notifications,admin}/
      core/api/                      # generated client + transport
      core/domain/                   # pure TS view/use-case rules
      core/offline/                  # cache policy and command queue
      core/security/                 # SecureStore/token lifecycle
      core/ui/                       # accessible design primitives
      core/telemetry/
    tests/{unit,component,integration,e2e}/
  src/
    HusayniaTabruk.Domain/
      Accounts/ Dates/ Signups/ Threads/ Notifications/ Audit/
    HusayniaTabruk.Application/
      Abstractions/ Authorization/ Behaviors/
      Accounts/ Dates/ Signups/ Threads/ Notifications/ Admin/
    HusayniaTabruk.Infrastructure/
      Persistence/ Identity/ Push/ Observability/ Time/
    HusayniaTabruk.Api/
      Endpoints/V1/ Auth/ Middleware/ Configuration/
  tests/
    HusayniaTabruk.Domain.Tests/
    HusayniaTabruk.Application.Tests/
    HusayniaTabruk.IntegrationTests/
    HusayniaTabruk.Api.ContractTests/
```

No project may reference `Api` or `Infrastructure` from `Domain`; `Application` references only `Domain`; `Infrastructure` and `Api` compose the ports.

## 5. Data model

All identifiers are UUIDv7/ULID-style opaque IDs; timestamps are UTC; organization-local rules use the organization's IANA time zone.

| Aggregate/table | Required fields and constraints |
|---|---|
| `organizations` | `id`, `name`, `time_zone`, `default_cancellation_lead_minutes`, `status`, account-governance `bootstrap_status: Unsealed/Sealed`, nullable `bootstrap_sealed_at`, and `version`; `version` is the optimistic concurrency token for the complete organization account-governance aggregate |
| `users` / Identity tables | login email (private), password hash, security stamp; no public contact exposure |
| `memberships` | `id`, `organization_id`, `user_id`, `display_name`, `status: Invited/Active/Disabled`, `eligible_as_named_participant`; unique `(organization_id,user_id)`. Eligibility is set from the organization's external adult-membership policy without storing age/date of birth |
| `invitations` | org, normalized email, hashed one-time token, expiry, issuer, accepted/revoked timestamps |
| `role_assignments` | membership, `Admin` or `FoodIncharge`, assigned/revoked by/at; unique active role |
| `role_change_requests` | organization, target membership, defined `Admin` action `Grant/Revoke`, proposer, approver, proposal/expiry/approval instants, state, reason, before/after metadata; proposer, approver, and target must be distinct and the proposer is revalidated as an active Admin at approval |
| `service_dates` | org, title, instructions, starts/ends, manager membership, status `Draft/Open/Closed/Cancelled/Completed`, cancellation deadline, version |
| `help_needs` | date, category `FoodPreparation/Serving/Cleanup`, instructions, nullable capacity, status `Open/Closed`, edit version, dedicated `signup_version`, and `waitlist_order_high_water bigint NOT NULL DEFAULT 0 CHECK (waitlist_order_high_water >= 0)`; unique `(date,category)`. `signup_version` is the optimistic concurrency token for the complete per-help-need signup aggregate and is not the date/need edit version. `waitlist_order_high_water` is aggregate state persisted and compared/written under the same signup-version CAS |
| `signups` | immutable service date and need IDs, primary membership, kind `Individual/Household/Team`, optional non-identifying label, total participant count, unnamed participant count, status `Pending/Approved/Waitlisted/Declined/Withdrawn/Cancelled`, submitted timestamp, nullable last-transition timestamp, nullable waitlist order, version |
| `signup_member_participants` | signup and active adult membership reference; display name is resolved from membership and is not copied into signup storage; primary membership is implicit |
| `date_threads` | exactly one per date, state `Open/Locked`, nullable authoritative `locked_at`, and `version bigint NOT NULL` optimistic concurrency token; `Locked` requires `locked_at` and `Open` forbids it |
| `thread_messages` | thread, author membership, client message ID, body, `Visible/Hidden`, created timestamp; no edit/delete in MVP |
| `message_reports` | message, reporter, reason code/comment, state, timestamps |
| `notifications` | recipient membership, type, resource ID, title/body-safe payload, read timestamp, created timestamp |
| `device_registrations` | membership, installation ID, provider token, platform, enabled, last seen; token encrypted at rest where provider supports it |
| `outbox_messages` | event type/payload, attempts, next attempt, processed/dead-letter timestamps |
| `idempotency_records` | organization, actor, key, operation, request hash, status/result reference, expiry; unique `(actor,key)` |
| `audit_events` | org, actor, action, target type/id, reason, purpose, case/correlation ID, before/after state metadata, timestamp; insert-only to the application database role |
| `privileged_access_events` | org, actor, target thread, reason, purpose `Support/Moderation/Safeguarding`, case ID, page cursor/hash, timestamp; insert-only and required before each moderation page is returned |

### Integrity and concurrency

- A partial unique index prevents more than one active (`Pending/Approved/Waitlisted`) signup for `(help_need_id, primary_membership_id)`.
- Every signup write rehydrates one `HelpNeedSignups` aggregate with the authoritative service-date/help-need context and the complete signup set for that need. Partial hydration is a repository contract violation.
- The aggregate owns capacity, active-primary uniqueness, waitlist allocation, chronology, and every child mutation. Commands select an owned child by `SignupId`; no public command accepts a signup collection, selected `Signup`, replacement `ServiceDate`, or replacement `HelpNeed`.
- Persistence compares `HelpNeedSignups.OriginalVersion` with `help_needs.signup_version`, writes all changed signup rows plus `help_needs.waitlist_order_high_water`, and increments `signup_version` atomically. The high-water value and child rows must never commit independently. A compare-and-swap miss rolls back the unit of work and returns `stale_version`; Domain does not retry.
- Thread persistence stores `DateThread.Version` in `date_threads.version`. Every post, report, hide,
  or first lock saves the thread row and all child/event/outbox effects in one transaction using an
  atomic compare-and-swap against the version loaded with the aggregate
  (`UPDATE date_threads ... WHERE id = @id AND version = @expectedVersion`). A zero-row update
  returns `stale_version` and rolls back every thread, message, report, moderation, notification,
  audit, and outbox write; persistence does not retry. An identical lock retry at the authoritative
  `locked_at` is a successful no-op because the Domain version is unchanged, and it must not append
  another lock event or outbox record. The reachable Domain version formula remains exactly
  `messages.Count + reports.Count + hidden-message count + (Locked ? 1 : 0)`.
- State changes use a domain transition table; invalid transitions return `409`.
- Date/category closure is checked inside the signup transaction, not only before it.
- Every material state change, in-app notification, audit event, and push outbox record commits atomically.
- The normal application database role may insert but may not update/delete `audit_events` or `privileged_access_events`; migrations use a separate owner role. A privileged read fails closed if its access-event insert fails.
- Deletion is not an MVP workflow. Disable, revoke, withdraw, cancel, lock, or hide preserves operational/audit history pending an approved retention policy.

### Signup authority and reachable state

`HelpNeedSignups` is the sole public mutation boundary for one help need. It exposes immutable
`OrganizationId`, `ServiceDateId`, `HelpNeedId`, `OriginalVersion`, `Version`,
`WaitlistOrderHighWater`, and defensive deep copies of its children. Rehydration accepts
`(ServiceDate serviceDate, HelpNeed helpNeed, long version, long waitlistOrderHighWater,
IReadOnlyCollection<Signup> signups)`, validates the exact service-date/help-need pair, copies the
canonical date end, cancellation deadline, date/need statuses and capacity, rejects mixed or
duplicate children, and rejects already-overallocated or invalid high-water state.

Capacity is participant slots across every owned approved signup. The aggregate persists a
non-negative `WaitlistOrderHighWater`; zero means no order has ever been allocated. Every new
waitlist assignment allocates `WaitlistOrderHighWater + 1` and advances the high-water value.
Leaving the waitlist clears the child's order but never decreases the high-water value, so gaps are
retained and removed orders are never reused. Existing positive waitlist orders must be unique and
no greater than the high-water value; the high-water value may exceed the current maximum.
Allocation when the high-water value is `long.MaxValue` returns `version_exhausted` with the root,
children, version, and high-water value unchanged. Presentation remains deterministic by
`WaitlistOrder`, then canonical `SignupId`. Reassignment remains Food-Incharge-selected and never
auto-promotes. At most one active (`Pending`, `Approved`, `Waitlisted`) signup may exist per primary
membership.

Every signup stores immutable `ServiceDateId`. Every UTC transition time must be no earlier than
`LastTransitionAt ?? SubmittedAt`; equality is allowed. Reachable child versions are exact:
`Pending=0`, `Waitlisted=1`, `Approved=1..2`, `Declined=1..2`, `Withdrawn=1..2`, and
`Cancelled=1..3`. Pending has no transition/order metadata; waitlisted has both; every other
non-pending status has a transition instant and no waitlist order. Each successful aggregate
command increments the root once; a child transition increments that child once; submission
creates child version zero; failures preserve root and children.

Stable signup-domain errors additionally include `invalid_signup_aggregate_state`,
`signup_not_owned`, and `signup_chronology_invalid`.

## 6. API contract

Base path `/api/v1`; JSON uses camelCase and ISO-8601 UTC timestamps. OpenAPI is the source for a generated TypeScript client. Lists use opaque cursor pagination (`items`, `nextCursor`).

### Authentication and administration

```text
POST /auth/invitations/accept        { token, displayName, password } -> TokenSet
POST /auth/login                     { email, password } -> TokenSet
POST /auth/refresh                   { refreshToken } -> TokenSet
POST /auth/logout                    { refreshToken } -> 204
POST /auth/step-up                   { password, purpose } -> { stepUpToken, expiresAt }
GET  /me                             -> membership, roles, organization

POST /admin/invitations              { email, expiresInHours } -> one-time invite URL
PUT  /admin/members/{id}/food-incharge    { reason } + X-Step-Up-Token -> 204
DELETE /admin/members/{id}/food-incharge  { reason } + X-Step-Up-Token -> 204
POST /admin/admin-role-requests      { targetMembershipId, action, reason } + X-Step-Up-Token -> RoleChangeRequest
POST /admin/admin-role-requests/{id}/approve { reason } + X-Step-Up-Token -> RoleChangeRequest
POST /admin/members/{id}/disable     { reason } -> Member
```

Invite URLs are displayed once for secure out-of-band delivery in MVP; adding transactional email is a separate external-service decision.

### Dates, needs, and signups

```text
GET  /dates?scope=open|mine|managed&cursor=
GET  /dates/{dateId}
POST /dates                          DateCreate -> Date
PATCH /dates/{dateId}                DatePatch + If-Match -> Date
POST /dates/{dateId}/open|close|cancel  { reason? } + Idempotency-Key -> Date
POST /dates/{dateId}/needs           NeedCreate -> Need
PATCH /needs/{needId}                 NeedPatch + If-Match -> Need

POST /needs/{needId}/signups
  Idempotency-Key: <UUID>
  { kind, label?, memberParticipantIds:[], unnamedParticipantCount }
  -> 201 Signup(status="pending")

GET  /signups/mine
GET  /dates/{dateId}/roster
POST /signups/{id}/approve           { reason? } -> Signup
POST /signups/{id}/decline           { reason } -> Signup
POST /signups/{id}/waitlist          { reason? } -> Signup
POST /signups/{id}/withdraw          {} + Idempotency-Key -> Signup
POST /signups/{id}/override          { targetState, reason } -> Signup
POST /needs/{needId}/reassign        { signupId, reason? } -> Signup
```

`DateCreate` includes `title`, `instructions`, `startsAt`, `endsAt`, `cancellationDeadlineAt`, and `managerMembershipId`. `NeedCreate` includes `category`, `instructions`, and nullable `capacity`.

### Threads, moderation, and notifications

```text
GET  /dates/{dateId}/thread/messages?cursor=
POST /dates/{dateId}/thread/messages
  Idempotency-Key: <clientMessageId>
  { body, clientMessageId } -> 201 Message
POST /messages/{id}/report            { reasonCode, comment? } -> 202
POST /messages/{id}/hide              { reason } -> Message       # managing FI; moderator also supplies X-Step-Up-Token
POST /dates/{dateId}/thread/lock      { reason } -> Thread        # managing FI; moderator also supplies X-Step-Up-Token
POST /admin/moderation/thread-reads
  { threadId, reason, purpose, caseId, cursor? } + X-Step-Up-Token -> MessagePage

GET  /notifications?cursor=
POST /notifications/{id}/read         -> 204
PUT  /devices/{installationId}        { platform, pushToken, enabled } -> 204
DELETE /devices/{installationId}      -> 204
```

Ordinary thread eligibility is server-computed and default-deny:

| Actor/current state | Read | Post |
|---|---:|---:|
| Active managing Food Incharge with current assignment; date not cancelled | yes | yes while thread `Open` |
| Active primary contact with signup `Approved`; date not cancelled | yes | yes while thread `Open` |
| `Pending`, `Waitlisted`, `Declined`, `Withdrawn`, or `Cancelled` primary contact | no | no |
| Organization administrator without the managing Food Incharge relationship | no | no |
| Disabled member, revoked Food Incharge, cross-organization actor, or any contact after date cancellation | no | no |

`Waitlisted -> Approved` grants contact access. Any transition from `Approved` to `Withdrawn`, `Cancelled`, or `Declined`, plus date cancellation, membership disable, organization change, and Food Incharge revocation, removes access on the next request and invalidates/purges cached thread data. A queued post is reauthorized at replay time and permanently rejected if eligibility changed. Administrators never use the ordinary GET: moderation reads use the separate POST operation, require the `PrivilegedThreadRead` authorization policy (active Admin plus the moderation endpoint), a purpose-bound step-up grant issued within five minutes, non-empty reason/purpose/case ID, and an immutable access event for every returned page. Unrelated or ineligible record-specific requests receive concealed `404`.

Input and abuse limits are enforced server-side before notification/outbox creation:

- Message: at most 2,000 Unicode scalar values and 8 KiB UTF-8; endpoint request body at most 12 KiB.
- Report comment: at most 500 Unicode scalar values and 2 KiB UTF-8; reason is a fixed enum; endpoint request body at most 4 KiB.
- Signup label: at most 80 Unicode scalar values/320 UTF-8 bytes; at most 20 referenced memberships with `eligible_as_named_participant = true` and 20 unnamed participants; total participant count is `1 + referenced + unnamed` and must be 1-25; endpoint request body at most 8 KiB.
- Administrative/moderation reason: 500 Unicode scalar values/2 KiB; case ID: 100 ASCII characters; administrative endpoint request bodies at most 4 KiB.
- Cursor pages default to 50 and are capped at 100 items.
- Thread posts: 3 per 10 seconds, 10 per minute, and 60 per hour per account; 300 per hour per organization.
- Reports: 5 per hour and 20 per day per account; 100 per day per organization; unique active `(message, reporter)` makes retries idempotent.
- Signup submissions: 10 per minute per account and 100 per hour per organization.
- Food Incharge/admin notification-generating mutations: 60 per minute per account and 500 per hour per organization.

Limits use a server-side sliding-window/token-bucket policy keyed by authenticated membership plus organization. Oversized requests return `413 payload_too_large`; exhausted quotas return `429 rate_limited` with `Retry-After` seconds and do not create messages, reports, notifications, audit transitions, or outbox rows.

### Errors and compatibility

- RFC 9457 problem details: `type`, `title`, `status`, `code`, `detail`, `traceId`, optional `fieldErrors`.
- `400` malformed/validation; `401` invalid authentication; `403` known but forbidden collection/action; `404` absent or concealed record; `409` duplicate/state/capacity/deadline conflict; `412` stale `If-Match`; `413` payload limit; `429` rate limit with `Retry-After`; `503` transient dependency failure.
- Stable machine codes include `signup_duplicate`, `category_closed`, `capacity_unavailable`, `cancellation_deadline_passed`, `invalid_transition`, and `stale_version`.
- Additive fields/endpoints are backward compatible within v1. Removing/renaming fields or changing enum meaning requires `/v2` and a supported-client migration window.

## 7. Authentication, authorization, and privacy

- Use ASP.NET Core Identity for password hashing/account lifecycle, but expose mobile bearer auth: 10-minute signed access JWT plus rotating, opaque, per-installation refresh tokens stored hashed server-side for 30 days.
- Store tokens only in iOS Keychain/Android Keystore through Expo SecureStore. Never put secrets in AsyncStorage, SQLite, logs, URLs, or push payloads.
- JWT role claims are not authoritative. Every protected request loads active membership and role assignment; record policies also verify `organization_id`, managed date, primary-contact ownership, and thread eligibility. Revocation/disable therefore takes effect immediately.
- Apply login/invite/refresh plus endpoint-specific write limits, breached-password/password-strength controls, refresh-token reuse detection, and audit of privileged actions.
- `OrganizationAccountGovernance` is the authoritative, versioned Domain aggregate for membership disable, Food Incharge changes, administrator requests, and bootstrap. It is rehydrated with the complete membership/request set for one organization plus the durable bootstrap seal and organization version. Its owned `Membership` objects are the only accepted actors/targets; entity governance mutators are internal, administrator quorum is derived internally, and no caller-supplied role snapshot or count is accepted.
- Food Incharge assignment/revocation is separate from administrator governance and cannot target the actor. Administrator grants/revocations require a pending request approved by a second distinct active administrator; proposer, approver, and target are distinct and authoritative aggregate members. Approval revalidates that the stored proposer is still active and Admin, rejects undefined actions, and enforces `ProposedAt <= now < ExpiresAt`. The application supplies `now` only from `IClock.UtcNow`; API timestamps are never accepted for governance transitions.
- Normal governance may not reduce the organization below two active administrators, derived from the complete aggregate membership set. A one-time deployment bootstrap command must initialize at least two distinct active non-Admin memberships, persist `Sealed` plus `bootstrap_sealed_at`, increment the aggregate version once, and emit audit records. Every successful governance mutation increments the version once; failure mutates nothing. Persistence must compare-and-swap `organizations.version` using the rehydrated original version and atomically roll back membership/request/bootstrap, audit, notification, and outbox writes on `stale_version`. Emergency administrator revocation is a deployment-operator procedure, not a public API.
- `IStepUpVerifier` is an Application port. The initial adapter re-verifies the current password and issues an opaque, single-use, purpose-bound `X-Step-Up-Token` backed by a server-side grant expiring after five minutes; it is never logged or persisted on the mobile client beyond the active operation. A future WebAuthn/identity-provider adapter may replace it without changing role or moderation use-cases.
- The mobile cache may persist open dates, the user's own signups, thread messages they may access, and notifications. Food Incharge roster/participant data is memory-only in MVP. Purge all local data on logout, membership disable, organization switch, refresh revocation, or any thread authorization loss response.
- Direct email/phone is identity/private profile data and is never returned in roster, participant, thread, or notification DTOs.
- Push lock-screen text is generic (“New Tabruk update”) and carries only an opaque deep-link resource ID; the authenticated app fetches details.
- Logs and traces exclude message bodies, member display names, login identifiers, tokens, invite URLs, and push tokens.

## 8. Offline and failure model

- Persist query results with explicit TTLs and show “last updated”/offline state.
- Queue only signup submission, pre-deadline withdrawal, thread send, notification read, and device registration. Each queued command has a durable client ID/idempotency key and shows **Pending sync**, never success.
- Food Incharge approvals, capacity edits, date cancellation, overrides, and reassignment require connectivity because they are concurrency-sensitive.
- On reconnect, replay in creation order with bounded exponential backoff; stop on permanent `4xx`, surface the server state, and refetch affected resources after every terminal response.
- API timeout target: 10 seconds. Retry GETs and idempotent commands only; never blindly retry a non-idempotent request.
- PostgreSQL unavailable: fail closed with `503`; mobile retains confirmed cache/pending commands.
- Push provider unavailable: the committed in-app notification remains authoritative. The outbox retries with jitter for 24 hours, then dead-letters and alerts operators.
- Worker crash: unprocessed outbox rows are reclaimed using a lease/`SKIP LOCKED`; handlers are idempotent.
- Unknown command outcome: retry with the same idempotency key or query the resource; do not manufacture success.

## 9. Threads, moderation, and audit

- MVP has one date thread, no DMs, attachments, reactions, edits, or physical deletion.
- Only approved primary contacts and the active managing Food Incharge may use the ordinary thread. Administrators are excluded unless they also independently satisfy the managing Food Incharge rule.
- Moderation reads require the `PrivilegedThreadRead` policy, recent purpose-bound step-up authentication, reason, purpose, and case ID. The API inserts an immutable privileged-access event before returning each page; audit-write failure denies the read.
- Users can report messages. The managing Food Incharge may hide a message and lock a thread; an administrator must use the privileged moderation policy, step-up, reason, and case ID for those actions. The original remains restricted to authorized moderation/audit access.
- Every role proposal/approval/change, invite action, date/need mutation, signup transition, override, reassignment, message hide/lock, privileged read, member disable, and export/deletion operation is audited.
- The UI warns users not to post phone numbers, email, medical/allergy information, or sensitive information. Automated content scanning is not introduced in MVP.

## 10. Observability and operations

- OpenTelemetry-compatible structured logs, traces, and metrics with `traceId`, actor/org IDs (opaque), endpoint, result code, latency, idempotency outcome, and outbox attempt.
- Minimum dashboards/alerts: API error/latency, auth failures/rate limits, DB connectivity/pool, signup conflicts, stale writes, outbox age/dead letters, push rejection rate, active app version, administrator role proposals/approvals, and privileged-thread reads. Alert when one actor reads more than 10 distinct threads/hour or an organization exceeds 50 privileged reads/day.
- Health endpoints: `/health/live` (process) and `/health/ready` (database/migrations); push failure does not make the API unready.
- One API deployment runs HTTP and the hosted outbox worker. Database migrations run as a separate
  release step. For T8, checked-in owner `psql` Up/Down scripts are the only supported production
  interface and hold one database/schema-scoped session advisory lock through concurrent DDL and
  history-last finalization. EF migration execution is disposable/local-only. Daily encrypted
  backups and restore drills are mandatory before production personal data.
- Configuration is environment-based: `DATABASE_URL`, signing-key reference, allowed origins/hosts, token lifetimes, invite TTL, step-up TTL fixed at five minutes, endpoint payload/rate limits, telemetry endpoint, Expo push credential/token, and feature flags. Production may lower limits but may not raise them without security review. Secrets use the selected host's secret store.

## 11. Mobile technology comparison

| Option | Fit | Rejected cost/tradeoff |
|---|---|---|
| **React Native + Expo (recommended)** | One TypeScript app for iOS now/Android later; strong iteration, push/deep-link/update tooling, accessible native controls, and straightforward API typing | Requires Expo/React Native lifecycle discipline and occasional native development builds; Expo push/EAS are external dependencies |
| Flutter | Also one codebase and strong rendering consistency | Introduces Dart and a parallel UI ecosystem with no verified repository precedent; custom rendering increases the accessibility/testing burden for this forms/workflow-heavy app without a compensating requirement |
| Native Swift, later Kotlin/Compose | Best direct access to iOS APIs and platform-specific polish | Creates two UI/use-case implementations, duplicated testing, and later contract drift; the product does not require native-only capabilities that justify this cost |

Use Expo development builds, not Expo Go, and avoid unnecessary native modules. Do not make EAS Update a release requirement; App Store releases remain the authoritative production channel.

## 12. Test architecture

- **Domain unit tests**: every state transition, exact reachable status/version boundary, monotonic chronology, deadline boundary, capacity calculation, waitlist reassignment, aggregate/child failure atomicity, role-governance rule, thread eligibility state, and time-zone edge. Signup authority tests include omission attacks, mixed organization/date/need hydration, duplicate waitlist order, invalid high-water hydration, retained gaps, removed-maximum non-reuse across rehydration, permanent high-water exhaustion, counterfeit same-organization date, active-primary uniqueness, overflow, defensive copies, and reflection/compile-time absence of unsafe public mutation APIs.
- **Application tests**: use-case authorization, idempotency, notification/audit creation, privileged-read fail-closed behavior, step-up expiry, and provider failure with fakes.
- **PostgreSQL integration tests**: real transactions and parallel clients for duplicate signup, close-during-submit, simultaneous approval/reassignment, refresh rotation, and outbox leasing.
- **API contract tests**: OpenAPI compatibility, problem details, concealed `404`, DTO privacy snapshots, `413`/`429` plus `Retry-After`, bounded pagination, and ETag semantics.
- **Mobile tests**: reducers/use-cases, offline replay, “pending sync” UX, accessibility labels/focus/dynamic type, and push deep links.
- **End-to-end tests**: invited member, dual-control administrator governance, Food Incharge assignment/revocation, date publish, all signup states, cutoff/override, waitlist reassignment, ordinary versus moderated thread reads, disabled account, offline recovery, and push failure.
- **Required transition-level authorization matrix**: assert read and post for every `SignupStatus`; assert grant on `Waitlisted -> Approved`; assert immediate denial and mobile purge on `Approved -> Withdrawn`, `Approved -> Cancelled`, date cancellation, membership disable, organization change, and Food Incharge revocation; assert queued-post replay denial after each revoking transition; assert administrators cannot call ordinary thread GET; assert moderation read requires permission, unexpired step-up, reason, purpose, case ID, and successful immutable audit insert.

## 13. Phased delivery

1. **Foundation**: solution/app shells, domain boundaries, PostgreSQL migrations, auth/invites, organization/roles, OpenAPI client, CI quality gates.
2. **MVP coordination core**: dates, help needs, minimized participant composition, approval/waitlist/capacity, cancellation/override/reassignment, dual-control role governance, roster privacy, audit.
3. **MVP communication/reliability**: threads/moderation, in-app notifications, push outbox, offline queue/cache, accessibility and full acceptance tests.
4. **Phase 2**: operational templates/history with named historical participation excluded by default; deterministic cooking timeline as a pure versioned rules module; cost ledger with currency and append-only change audit.
5. **Phase 3**: caterer accounts, date-linked quote requests/responses and ratings only after policy approval; payment remains off-platform.

## 14. Migration and rollback

This is a greenfield system, so there is no initial data migration. T6R is a source-breaking,
domain-only replacement before T8: add the authoritative root, `ServiceDateId`, and durable
`WaitlistOrderHighWater`, then delete the unsafe collection-based helpers and public child mutation
surface. T8 creates the non-null high-water column in the initial migration.

The binding T8 production contract is:

- owner `psql` scripts are the sole production Up/Down path;
- target objects are schema-qualified and attested by namespace/object OID;
- schema ownership, PUBLIC/app schema privileges, inherited CREATE, grant options, relation/column
  privileges, and target/global default ACLs fail closed;
- Up retains concurrent index construction, compensates only invocation-created objects, and writes
  corrective history last while holding one session advisory lock;
- production Down is logical and non-destructive: it removes only corrective history and preserves
  the unique index, validated chronology constraint, schema/default ACL controls, and least
  privilege;
- EF Up/Down is supported only for explicitly marked empty disposable/local databases.

No production destructive rollback is supported. Application rollback leaves the hardened additive
schema in place. Later schema changes use expand/migrate/contract; destructive migrations require
separate CTO approval and a verified backup/restore plan.

Mobile API compatibility must cover at least the current and immediately previous production app versions. Feature flags disable unfinished endpoints/UI without changing stored states.

## 15. Tradeoffs and risks

- **Optimized for** correctness, privacy, low operations, cross-platform reuse, testability, and incremental delivery.
- **Given up** microservice isolation, automatic waitlist optimization, offline Food Incharge administration, rich chat, and fully managed identity.
- PostgreSQL row locks serialize approval for a single need; this is intentional and appropriate until measured scale proves otherwise.
- Complete per-help-need hydration increases each signup write's read set, but it is required to prove capacity, waitlist, and active-primary invariants. T8 must expose one repository write path only.
- A capacity edit that would place the owned signup set over capacity is invalid. T17 must hydrate `HelpNeedSignups` and reject the edit with `capacity_unavailable`.
- A modular monolith can become coupled; enforce project references and architecture tests.
- Local mobile data may remain on a lost device; minimize it, use OS data protection, SecureStore tokens, remote refresh revocation, and purge rules.
- Thread content can expose self-posted sensitive data; mitigate with no DMs/attachments, warnings, reporting/hiding, generic push, and retention policy.
- Same-database audit is not independently tamper-proof against a database owner; mitigate with insert-only application grants, privileged-read alerts, protected exports, and require segregated/tamper-evident export before production.
- Count-only unnamed participants reduce coordination detail; the primary contact remains responsible for the group. Named minors or non-member names require a separately approved safeguarding and retention design.
- Expo/provider outage can delay push but cannot lose authoritative notifications because of the transactional outbox.

## 16. CTO-reserved decisions

1. **Approve Expo Push Service and Apple Developer Program use** (recommended) versus direct APNs now and FCM later. This is an external-service/platform commitment; push implementation must remain behind `IPushGateway`.
2. **Approve production hosting and managed PostgreSQL spend**. Recommended: one regional managed PostgreSQL instance plus one container/app service with backups; provider choice must not leak into domain/application code.
3. **Approve EAS Build subscription only if free/local CI capacity is inadequate**. EAS Update is optional and not required.
4. **Approve any transactional email provider**. MVP can use one-time invite/reset URLs delivered out-of-band, avoiding this dependency initially.
5. Before production data: approve retention/export/deletion, minors/safeguarding, supported iOS versions, expected scale, and incident/support ownership (`requirements.md:73-78`).

## 17. T10 invitation security addendum (2026-08-16)

This addendum supersedes the earlier single-admin invitation assumption in section 6 for T10. It is
the smallest change that closes the blocking High without adding transactional email or changing T9
global email login.

### Verified current state

- FACT: `/api/v1` is auth-protected by default and exposes admin and auth endpoints from one ASP.NET
  Core host (`../HusayniaTabruk/src/HusayniaTabruk.Api/Program.cs:21-58`).
- FACT: The codebase is still a modular monolith with `Application -> Domain`,
  `Infrastructure -> Application`, and `Api -> Application + Infrastructure`
  (`../HusayniaTabruk/src/HusayniaTabruk.Application/HusayniaTabruk.Application.csproj:1-5`,
  `../HusayniaTabruk/src/HusayniaTabruk.Infrastructure/HusayniaTabruk.Infrastructure.csproj:1-18`,
  `../HusayniaTabruk/src/HusayniaTabruk.Api/HusayniaTabruk.Api.csproj:1-15`).
- FACT: T9 login verifies a global email/username and then succeeds only when that user resolves to
  exactly one active membership (`../HusayniaTabruk/src/HusayniaTabruk.Application/Accounts/LoginService.cs:33-59`,
  `../HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Identity/Services/AspNetIdentityService.cs:44-68`,
  `../HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Identity/Services/PostgresAuthenticationMembershipRepository.cs:247-257`).
- FACT: T10 currently returns an invite URL after one admin step-up and immediately persists a
  placeholder user, placeholder membership, and invitation row (`../HusayniaTabruk/src/HusayniaTabruk.Api/Endpoints/V1/Admin/AdminEndpoints.cs:49-55,196-227`,
  `../HusayniaTabruk/src/HusayniaTabruk.Application/Admin/Members/AdministratorMembershipService.cs:224-307`,
  `../HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Identity/Services/PostgresAuthenticationMembershipRepository.Admin.cs:45-123`).
- FACT: Invitation acceptance currently sets a password on the placeholder user, activates the
  membership, and then binds the login email; it rejects if another user already owns that global
  login (`../HusayniaTabruk/src/HusayniaTabruk.Application/Accounts/AcceptInvitationService.cs:36-72`,
  `../HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Identity/Services/PostgresAuthenticationMembershipRepository.cs:149-245`).
- FACT: Invitation conflict detection is organization-scoped and treats any unaccepted,
  unrevoked invite as conflicting even when it is already expired (`../HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Identity/Services/PostgresAuthenticationMembershipRepository.Admin.cs:126-152`).
- FACT: `InvitationBaseUrl` currently validates only `absolute + no query + no fragment`, and the
  runtime already writes `ETag` headers directly while OpenAPI has no reusable `ETag` response
  header metadata (`../HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Identity/Services/TabrukAuthOptions.cs:42-52`,
  `../HusayniaTabruk/src/HusayniaTabruk.Api/Endpoints/V1/Admin/AdminEndpointSupport.cs:78-100`,
  `../HusayniaTabruk/src/HusayniaTabruk.Api/OpenApi/ApiOpenApiDocument.cs:406-449,562-660`).

### Why the frozen single-admin design cannot be made safe

If one admin receives the only redeemable invite URL and T9 still uses global email login, that
admin always knows the sole factor needed to create a brand-new email principal. No storage-only
change can stop a single issuer from redeeming an unused email. Without direct system delivery to
the invited email address, the smallest correct control is second-admin countersign before the raw
URL is returned.

### Decision

Keep `POST /auth/invitations/accept` and `POST /auth/login` as they are. Keep out-of-band delivery
and the “invite URL returned once” behavior. Change only invitation issuance:

```text
POST /admin/invitations
  body:    { email, expiresInHours, approvingMembershipId }
  headers: X-Step-Up-Token, X-Invite-Approval-Token
  result:  200 { inviteUrl, expiresAt } + ETag
```

- `X-Step-Up-Token` remains the issuer’s single-use step-up for purpose `membership.invite`.
- `X-Invite-Approval-Token` is a second admin’s single-use step-up whose purpose is bound to the
  exact request challenge:
  `membership.invite.approve.v1:<base64url(sha256(orgId|issuerMembershipId|approvingMembershipId|normalizedEmail|expiresInHours))>`.
- The issuer and approver must be distinct active Admin memberships in the same organization. Both
  tokens are consumed in the same transaction that issues the invitation.
- Keep invitation issuance outside `OrganizationAccountGovernance`. Reuse the existing application
  service + repository transaction shape instead of widening the governance aggregate with a new
  request type.

### Issuance, storage, acceptance, and reissue rules

1. **Issuance preconditions**
   - Resolve the issuer from `ICurrentActor` exactly as today.
   - Resolve `approvingMembershipId` from the database; it must be active, Admin, same
     organization, and different from the issuer.
   - Consume the issuer step-up for `membership.invite`.
   - Consume the approver step-up for the exact request-bound challenge purpose.

2. **Global conflict check at issuance**
   - Reject with stable `409 invitation_conflict` when either of the following already exists for
     the normalized email:
     - any confirmed/claimed global login (`users.normalized_user_name` or
       `users.normalized_email`);
     - any active issued invitation anywhere (`accepted_at IS NULL`, `revoked_at IS NULL`,
       `expires_at > now`).
   - This intentionally preserves T9’s current “one global email principal, one active login
     resolution” boundary instead of introducing multi-organization account linking in T10.

3. **Placeholder materialization**
   - On first issue for a new email, keep the current placeholder membership/user pattern so the
     admin member list still shows the invited row and its collection ETag still advances.
   - On reissue, if the same organization already has an `Invited` placeholder membership for that
     email and every prior invitation row is expired or revoked, reuse that placeholder
     membership/user and insert a new invitation row with a new token hash and expiry instead of
     creating duplicate invited members.

4. **Acceptance**
   - Keep `POST /auth/invitations/accept { token, displayName, password } -> TokenSet`.
   - Keep the current transaction shape: load invitation by token hash, set password on the
     placeholder user, activate the invited membership, bind the global login email, and issue
     tokens.
   - Because issuance already enforced global exclusivity, acceptance no longer has to discover a
     cross-tenant login conflict at redemption time. Any invalid/expired/revoked/used token still
     returns the existing stable `401 invitation_invalid`.

5. **Stable conflict/reissue semantics**
   - Accepted memberships remain non-reissuable in T10.
   - Disabled memberships remain non-reissuable in T10; re-enable/reactivate is a later, separate
     contract.
   - Expired or revoked invitations are reissuable.
   - A second request against an already active invitation remains `409 invitation_conflict`.

### Why the attack no longer works

- A single malicious admin cannot obtain a redeemable invite URL alone; a second distinct current
  Admin must countersign the exact email and TTL.
- The system now blocks already-claimed emails and other still-active invitations before the raw
  URL is returned, so the accept endpoint no longer becomes a tenant-existence oracle.
- Temporary reservation of an unused email can still occur, but only after dual control has
  deliberately issued a live invite. The blocked High was unilateral self-redemption, and that
  path is removed.

### Config, OpenAPI, and testing deltas

- Validate `TabrukAuth:InvitationBaseUrl` on startup with `ValidateOnStart`. Allow only:
  - `https://...` in normal environments;
  - `http://localhost/...` or `http://127.0.0.1/...` only in Development/Testing.
  Reject query strings, fragments, and user-info in all environments.
- Return `ETag` on the invitation-issue response using the new organization version, and add a
  reusable OpenAPI response-header component for `ETag` on every admin response that writes it.
- Extend OpenAPI contract tests to assert `required` arrays for `IssueInvitationResponse`,
  `AdminMemberResponse`, `AdminRoleChangeRequestResponse`, and cursor pages, not only property
  types.
- Required tests:
  - issuer and approver must be distinct active same-org admins;
  - wrong-purpose, replayed, expired, or missing approval token is denied;
  - an existing claimed global email is rejected at issuance with `invitation_conflict`;
  - an active issued invite in another org is rejected at issuance with the same stable conflict;
  - expired invite reissue succeeds and reuses the same invited member row;
  - acceptance still allows one winner under concurrency and never leaks the raw token;
  - issuance response carries `ETag`, and OpenAPI advertises both `ETag` and required fields.

### Tradeoff

This keeps the current login model, keeps out-of-band delivery, avoids a new email provider, and
avoids widening the governance aggregate or adding a second invite resource. The cost is one
unavoidable T10 admin contract change: invitation issuance becomes dual-step-up instead of
single-step-up.
