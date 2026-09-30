# T19 implementation design decisions

1. **Authorization is live and operation-local.** Each thread command/query reloads the managed date, current actor account/membership state, managing Food Incharge assignment, and signup state under the repository's current authorization and transaction conventions. Cached claims, historical assignment, and generic administrator status are insufficient.
2. **Concealment precedes existence disclosure.** Ordinary endpoints return 404 for an ineligible actor, ordinary administrator, or absent thread; no T19 endpoint creates a missing thread or backfills historical threads.
3. **Separate ordinary and privileged paths.** The moderation historical endpoint is not a fallback ordinary-read path. It has a dedicated `PrivilegedThreadRead` permission and validates step-up/reason/purpose/case ID before any content is returned. It never confers ordinary participant eligibility.
4. **Privileged audit is a fail-closed transaction.** In one PostgreSQL transaction, validate a single-use unexpired purpose-bound step-up, lock/consume it, obtain the requested page, insert the immutable `privileged_access_events` record (cursor and stable page hash), and commit. Any insert or transaction failure returns no page.
5. **Thread state is not synthesized.** Existing one-per-service-date persistence is used only when already present. Thread list/read treats no row as concealed not-found. T17 close does not create a thread and T19 preserves that behavior.
6. **Mutation correctness is database-enforced.** Reuse established PostgreSQL locking, CAS/version, chronology, idempotency, rate-limit, duplicate-report, and transactional patterns. An API-level check alone is insufficient.
7. **Privacy and notifications.** DTOs exclude direct contacts. Hidden text becomes an intentional redacted representation. Message body never feeds log, outbox, or push fields; no T20 worker or push delivery is added.
8. **Limits are semantic boundaries.** Input validation counts Unicode scalars and UTF-8 bytes; `caseId` additionally requires ASCII and <=100 despite broader persistence. Request-byte guards return 413; successful throttle rejects emit 429 plus a computed `Retry-After`.
9. **No schema work by default.** Inspect mapped thread/audit/step-up/report/rate entities and the migration catalog before implementation. If durable storage required by T19 is already represented, use it. A new migration is permitted only when absence is proven, then its rationale and approval impact must be recorded before it is created.

## Planned task graph

| Task | Owner | Dependency | Exit evidence |
|---|---|---|---|
| T19-ARCH | architect/tech lead | requirements | Grounded implementation map, interface/file ownership, migration decision |
| T19-IMPLEMENT | backend developer | T19-ARCH | Test-first T19 code/tests, focused red then green evidence |
| T19-VALIDATE | test/security/code reviewers | implementation | Independent real-PG, security, and code review evidence |
| T19-QA | QA engineer | validation | User/API journey evidence |
| T19-JUDGE | engineering judge | all gates | Independent DoD verdict |

## Grounded architecture update — 2026-08-19

- Verified current state:
  - No `Application/Threads` or `Api/Endpoints/V1/Threads` slice exists yet; the current repo only has Domain/persistence thread building blocks.
  - Ordinary thread rules already live in `DateThread`: only current managing Food Incharge or a current approved primary contact may use the ordinary thread; ordinary denial is concealed `404`.
  - PostgreSQL already contains `date_threads`, `thread_messages`, `message_reports`, `thread_moderation_events`, and `privileged_access_events`; `case_id` persistence already allows 200 chars, so T19 stays application-limited at ASCII `<=100` and adds **no migration**.
  - T17 already proves close-without-thread does not create or backfill one and must remain unchanged.
- Binding design:
  - Ordinary routes stay **service-date scoped**: `GET/POST /dates/{dateId}/thread/messages`, `POST /dates/{dateId}/thread/messages/{messageId}/report`, `POST /dates/{dateId}/thread/messages/{messageId}/hide`, `POST /dates/{dateId}/thread/lock`.
  - Privileged historical read stays separate: `POST /admin/moderation/thread-reads` with `{ serviceDateId, reason, purpose, caseId, cursor?, pageSize? }` plus `X-Step-Up-Token`.
  - Ordinary reads return privacy-safe sender display + timestamp + redacted hidden body only; privileged reads return stored bodies and must atomically consume step-up + insert one `privileged_access_events` row + return the page or fail closed with no content.
  - Thread send/report/hide/lock add **no T19 push/outbox/notification**; only existing hide/lock and privileged-read audit requirements persist.
- Required file plan:
  - In-scope new files: `src/HusayniaTabruk.Application/Threads/ThreadService.cs`, `ThreadContracts.cs`, `ThreadStepUpPurposes.cs`, `ThreadMembershipLookupPorts.cs`; `src/HusayniaTabruk.Api/Endpoints/V1/Threads/ThreadEndpoints.cs`, `ThreadEndpointSupport.cs`; new `Tests/**/Threads/**`; `docs/api/openapi.json`.
  - Minimal scope expansion still needed: a read-only sender-display lookup implementation in `src/HusayniaTabruk.Infrastructure/Identity/Services/PostgresAuthenticationMembershipRepository.Threads.cs`. Without it, T19 cannot return required sender display names or hash the exact privileged page response.

## Implementation-ready plan — 2026-08-19 (frozen for T19)

### Frozen implementation decisions and surfaced interface conflicts

1. **No new DI registration file changes.**
   - `ThreadService` is **not** added to the container; `ThreadEndpoints` must construct it per-handler exactly like `DateManagementEndpoints` and `SignupSubmitEndpoints`.
   - `ThreadEndpoints` is auto-discovered by `IApiEndpoint`; no route-registration edit outside `Api/Endpoints/V1/Threads/**`.
   - `IMembershipRepository -> PostgresAuthenticationMembershipRepository`, `IThreadRepository -> PostgresThreadRepository`, `IUnitOfWork -> PostgresUnitOfWork`, `IStepUpVerifier -> IdentityStepUpVerifier`, and `IPrivilegedAccessWriter -> PostgresPrivilegedAccessWriter` already exist and remain the binding points.

2. **Hidden interface conflict: `PrivilegedThreadRead` has vocabulary but no separate permission surface.**
   - The codebase currently exposes only role claims (`Admin`, `FoodIncharge`) through the bearer handler; there is no permission claim, permission table, or permission mapper in scope for T19.
   - T19 therefore freezes the narrow equivalent required by current conventions: `POST /api/v1/admin/moderation/thread-reads` stays under the existing admin route policy, revalidates a live active admin inside `ThreadService`, and additionally requires the one-use `thread.privileged.read` step-up token.
   - This is a **documented adjustment**, not a route change. A separately persisted/claim-backed `PrivilegedThreadRead` permission remains post-T19 scope.

3. **Hidden interface conflict: ordinary thread concurrency lives in `ETag`, but the mobile generated client currently discards successful response headers.**
   - Do **not** add a `version` field to the architected ordinary thread bodies.
   - Freeze the T19 client work as additive runtime/generator work only: the generated client and `auth-api.ts` wrappers must return `{ data, etag, version }` envelopes for ordinary thread `GET/POST/report/hide/lock` calls, while leaving existing T18 method signatures untouched.

4. **OpenAPI scope remains route/header/schema drift only.**
   - `ApiOpenApiDocument.ApplyKnownContractConstraints(...)` currently special-cases only `SubmitSignupRequest`; T19 will not expand that file in this mission.
   - T19 OpenAPI work is therefore limited to discovered routes, request/response schemas, header references, and snapshot regeneration. Byte/scalar/ASCII limits stay enforced by runtime parsing/body-limit middleware and are proven by contract/integration tests.

### Frozen file inventory

- **Application**
  - `src/HusayniaTabruk.Application/Threads/ThreadContracts.cs`
  - `src/HusayniaTabruk.Application/Threads/ThreadMembershipLookupPorts.cs`
  - `src/HusayniaTabruk.Application/Threads/ThreadStepUpPurposes.cs`
  - `src/HusayniaTabruk.Application/Threads/ThreadService.cs`
- **API**
  - `src/HusayniaTabruk.Api/Endpoints/V1/Threads/ThreadEndpointSupport.cs`
  - `src/HusayniaTabruk.Api/Endpoints/V1/Threads/ThreadEndpoints.cs`
- **Approved scope expansion**
  - `src/HusayniaTabruk.Infrastructure/Identity/Services/PostgresAuthenticationMembershipRepository.Threads.cs`
- **Tests**
  - `tests/HusayniaTabruk.Application.Tests/Threads/ThreadServiceTests.cs`
  - `tests/HusayniaTabruk.Api.ContractTests/Threads/ThreadEndpointContractTests.cs`
  - `tests/HusayniaTabruk.IntegrationTests/Threads/ThreadIntegrationTests.cs`
  - `tests/HusayniaTabruk.IntegrationTests/Threads/ThreadIntegrationTestSupport.cs`
- **Snapshot / generated client**
  - `docs/api/openapi.json`
  - `apps/mobile/src/core/api/generate-api-client.mjs`
  - `apps/mobile/src/core/api/generated/api-contract-client.ts`
  - `apps/mobile/src/core/api/auth-api.ts`
  - `apps/mobile/tests/unit/api/generated-api-client.test.ts`

### Frozen application contracts

```csharp
// src/HusayniaTabruk.Application/Threads/ThreadStepUpPurposes.cs
public static class ThreadStepUpPurposes
{
    public static StepUpPurpose PrivilegedRead { get; } = new("thread.privileged.read");
}

// src/HusayniaTabruk.Application/Threads/ThreadMembershipLookupPorts.cs
public partial interface IMembershipRepository
{
    ValueTask<Result<IReadOnlyDictionary<MembershipId, string>>> GetThreadSenderDisplaysAsync(
        OrganizationId organizationId,
        IReadOnlyCollection<MembershipId> membershipIds,
        CancellationToken cancellationToken = default);
}

// src/HusayniaTabruk.Application/Threads/ThreadContracts.cs
public sealed record PostThreadMessageCommand(
    ServiceDateId ServiceDateId,
    string Body,
    IdempotencyKey IdempotencyKey);

public sealed record ReportThreadMessageCommand(
    ServiceDateId ServiceDateId,
    MessageId MessageId,
    MessageReportReason Reason,
    string? Comment);

public sealed record HideThreadMessageCommand(
    ServiceDateId ServiceDateId,
    MessageId MessageId,
    string Reason);

public sealed record LockThreadCommand(
    ServiceDateId ServiceDateId,
    string Reason);

public sealed record ReadPrivilegedThreadPageCommand(
    ServiceDateId ServiceDateId,
    string Reason,
    PrivilegedAccessPurpose Purpose,
    string CaseId,
    string? Cursor,
    int? PageSize);

public sealed record ThreadMessageSummary(
    MessageId Id,
    string SenderDisplayName,
    string Body,
    MessageVisibility Visibility,
    DateTimeOffset CreatedAt);

public sealed record ThreadMessagePage(
    ServiceDateId ServiceDateId,
    ThreadStatus Status,
    DateTimeOffset? LockedAt,
    long Version,
    IReadOnlyList<ThreadMessageSummary> Items,
    string? NextCursor);

public sealed record VersionedThreadMessage(
    ThreadMessageSummary Message,
    long Version);

public sealed record VersionedThreadReport(
    long Version,
    bool WasDuplicate);

public sealed record VersionedThreadState(
    ServiceDateId ServiceDateId,
    ThreadStatus Status,
    DateTimeOffset? LockedAt,
    long Version);

public sealed record PrivilegedThreadMessageSummary(
    MessageId Id,
    string SenderDisplayName,
    string Body,
    MessageVisibility Visibility,
    DateTimeOffset CreatedAt,
    DateTimeOffset? HiddenAt);

public sealed record PrivilegedThreadMessagePage(
    ThreadId ThreadId,
    ServiceDateId ServiceDateId,
    ThreadStatus Status,
    DateTimeOffset? LockedAt,
    IReadOnlyList<PrivilegedThreadMessageSummary> Items,
    string? NextCursor);

public static class ThreadApplicationErrorCodes
{
    public const string InvalidThreadRequest = "invalid_thread_request";
    public const string DuplicateReportMismatch = "duplicate_report_mismatch";
    public const string ThreadSenderDisplayUnavailable = "thread_sender_display_unavailable";
}

// src/HusayniaTabruk.Application/Threads/ThreadService.cs
public sealed class ThreadService(
    IUnitOfWork unitOfWork,
    IServiceDateRepository serviceDateRepository,
    ISignupRepository signupRepository,
    IThreadRepository threadRepository,
    IMembershipRepository membershipRepository,
    IPrivilegedAccessWriter privilegedAccessWriter,
    IStepUpVerifier stepUpVerifier,
    ICurrentActor currentActor,
    IClock clock)
{
    public ValueTask<Result<ThreadMessagePage>> ListAsync(
        ServiceDateId serviceDateId,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken = default);

    public ValueTask<Result<VersionedThreadMessage>> PostAsync(
        PostThreadMessageCommand command,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    public ValueTask<Result<VersionedThreadReport>> ReportAsync(
        ReportThreadMessageCommand command,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    public ValueTask<Result<VersionedThreadMessage>> HideAsync(
        HideThreadMessageCommand command,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    public ValueTask<Result<VersionedThreadState>> LockAsync(
        LockThreadCommand command,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    public ValueTask<Result<PrivilegedThreadMessagePage>> ReadPrivilegedAsync(
        ReadPrivilegedThreadPageCommand command,
        StepUpToken stepUpToken,
        CancellationToken cancellationToken = default);
}
```

### Frozen ordinary authorization algorithm

Every ordinary operation (`GET/POST /dates/{dateId}/thread/messages`, report, hide, lock) follows this order inside `IUnitOfWork.ExecuteAsync(...)`:

1. `ResolveActiveActorAsync(currentActor.UserId, currentActor.MembershipId, currentActor.OrganizationId, ...)`.
   - Any inactive/missing/cross-org actor fails immediately.
2. Load the live service date by `serviceDateId`.
   - If the date lookup is missing, map it to concealed thread `404`, not a date-specific disclosure.
3. Load the existing persisted thread by `serviceDateId`.
   - If absent, return concealed `404`; do **not** create/backfill a thread.
4. Rehydrate a live `Membership` from the resolved actor.
5. Resolve the actor’s current participant path by iterating the loaded service date’s current `HelpNeeds` in deterministic order (`OrderBy(need => need.Id.Value)`), calling `signupRepository.GetAsync(...)` for each help need until a current `Approved` signup whose `PrimaryMembershipId == actor.Id` is found.
6. Call the existing domain method on `DateThread`:
   - `AuthorizeRead(...)` for the `GET` page.
   - `Post(...)` for send.
   - `Report(...)` for report.
   - `Hide(...)` for hide.
   - `Lock(...)` for lock.
7. Persist only through existing CAS `threadRepository.SaveAsync(...)`; never retry after a stale-version failure.

This preserves every required live-state transition:

- manager role granted/revoked or manager reassigned on the service date
- actor membership disabled
- service date cancelled
- signup moved from `Approved` to `Withdrawn`, `Cancelled`, `Declined`, or any non-approved state
- ordinary admin with no current participant/manager eligibility
- cross-organization or unrelated actor
- missing thread row

All of the above are concealed `404` for ordinary access.

### Frozen stale-version and duplicate handling

- **List**
  - No `If-Match`; write `ETag` from `ThreadMessagePage.Version`.
- **Send**
  - If `expectedVersion == loaded.LoadedVersion`, call `DateThread.Post(...)`.
  - If `expectedVersion != loaded.LoadedVersion`, only succeed when the current persisted thread already contains the same `IdempotencyKey`, same author membership, and same body; otherwise return `412`.
  - Duplicate same-key different-body at the current version remains `409 duplicate_client_message` via the domain.
- **Report**
  - If `expectedVersion == loaded.LoadedVersion`, first pre-check for an existing `(messageId, reporterMembershipId)` report.
    - same `reason` + normalized `comment` => duplicate success
    - different `reason/comment` => `409 duplicate_report_mismatch`
  - If `expectedVersion != loaded.LoadedVersion`, only that exact same persisted report replay succeeds; otherwise `412`.
  - Successful response is `202` with the current `ETag`.
- **Hide / Lock**
  - No duplicate replay path beyond the domain’s current behavior.
  - If `expectedVersion != loaded.LoadedVersion`, return `412` before calling the domain.

### Frozen ordinary page rendering

- Message order is chronological ascending, then `MessageId` ascending for tie-break stability.
- Cursor is opaque base64-encoded zero-based offset string, matching the existing signup/roster pagination style.
- Default `pageSize` = `50`; max = `100`.
- Ordinary hidden body text is frozen to **`"This message was hidden by a moderator."`**
- Ordinary responses expose only:
  - `id`
  - `senderDisplayName`
  - `body`
  - `visibility`
  - `createdAt`
- No ordinary response may expose `membershipId`, `userId`, email, phone, or hidden original body.

### Frozen privileged read transaction and audit algorithm

`ThreadService.ReadPrivilegedAsync(...)` runs inside a single `IUnitOfWork.ExecuteAsync(...)` transaction:

1. Validate request shape:
   - `serviceDateId` required/valid
   - `reason` required; `<= 500` Unicode scalars and `<= 2 KiB` UTF-8
   - `purpose` required; must map to `support|moderation|safeguarding`
   - `caseId` required; trimmed printable ASCII only; `<= 100`
   - `cursor` optional opaque base64 offset
   - `pageSize` optional `1..100`, default `50`
2. Resolve the live actor and require a current active admin.
3. Consume `X-Step-Up-Token` with `ThreadStepUpPurposes.PrivilegedRead`.
4. Load the existing thread by `serviceDateId`.
   - If missing, return `404`; the ambient transaction rolls back the step-up consume.
5. Resolve sender display names for the page’s distinct author membership IDs through `IMembershipRepository.GetThreadSenderDisplaysAsync(...)`.
6. Build the **exact privileged response DTO** in-memory:
   - `threadId`
   - `serviceDateId`
   - `status`
   - `lockedAt`
   - `items[]` with full stored bodies and `hiddenAt`
   - `nextCursor`
7. Serialize that DTO with camelCase + string-enum JSON options matching the API response contract, hash the exact UTF-8 payload with SHA-256, and create one `PrivilegedAccessEntry`:
   - `resourceType = "thread"`
   - `resourceId = thread.Id.ToString()`
   - `reason = command.Reason`
   - `purpose = command.Purpose`
   - `caseId = command.CaseId`
   - `pageCursor = command.Cursor`
   - `pageHash = RequestFingerprint.FromSha256(...)`
8. `privilegedAccessWriter.WriteAsync(...)`.
9. Commit and return the page.

If any failure occurs after step-up consume but before commit (including audit insert failure), the transaction rolls back, no content page is returned, and the step-up remains unconsumed.

### Frozen API surface

#### Ordinary routes

- `GET /api/v1/dates/{dateId}/thread/messages?cursor=&pageSize=`
  - `200 OK`
  - response header: `ETag`
  - response body: `ThreadMessagePageResponse`
  - errors: `400/401/404/503`
- `POST /api/v1/dates/{dateId}/thread/messages`
  - headers: `Idempotency-Key`, `If-Match`
  - request body limit: `ApplicationLimits.MaximumMessageRequestBytes`
  - rate keys: `membership_id` for 3/10s, 10/min, 60/hour; `organization_id` for 300/hour
  - `201 Created`
  - response header: `ETag`
  - response body: `ThreadMessageResponse`
  - errors: `400/401/404/409/412/413/429/503`
- `POST /api/v1/dates/{dateId}/thread/messages/{messageId}/report`
  - header: `If-Match`
  - request body limit: `ApplicationLimits.MaximumReportRequestBytes`
  - rate keys: `membership_id` for 5/hour and 20/day; `organization_id` for 100/day
  - `202 Accepted`
  - response header: `ETag`
  - no response body
  - errors: `400/401/404/409/412/413/429/503`
- `POST /api/v1/dates/{dateId}/thread/messages/{messageId}/hide`
  - header: `If-Match`
  - request body limit: `ApplicationLimits.MaximumAdministrativeRequestBytes`
  - rate keys: existing administrative `membership_id` 60/min and `organization_id` 500/hour
  - `200 OK`
  - response header: `ETag`
  - response body: `ThreadMessageResponse`
  - errors: `400/401/404/412/413/429/503`
- `POST /api/v1/dates/{dateId}/thread/lock`
  - header: `If-Match`
  - request body limit: `ApplicationLimits.MaximumAdministrativeRequestBytes`
  - rate keys: existing administrative `membership_id` 60/min and `organization_id` 500/hour
  - `200 OK`
  - response header: `ETag`
  - response body: `ThreadStateResponse`
  - errors: `400/401/404/409/412/413/429/503`

#### Privileged route

- `POST /api/v1/admin/moderation/thread-reads`
  - header: `X-Step-Up-Token`
  - request body limit: `ApplicationLimits.MaximumAdministrativeRequestBytes`
  - rate keys: existing administrative `membership_id` 60/min and `organization_id` 500/hour
  - `200 OK`
  - response body: `PrivilegedThreadMessagePageResponse`
  - errors: `400/401/403/404/413/429/503`

#### Frozen endpoint DTOs

- `PostThreadMessageRequest { string Body }`
- `ReportThreadMessageRequest { MessageReportReason Reason; string? Comment }`
- `ReasonRequest { string Reason }` for hide/lock
- `PrivilegedThreadReadRequest { Guid ServiceDateId; string Reason; PrivilegedAccessPurpose Purpose; string CaseId; string? Cursor; int? PageSize }`
- `ThreadMessageResponse { string Id; string SenderDisplayName; string Body; MessageVisibility Visibility; DateTimeOffset CreatedAt }`
- `ThreadMessagePageResponse { string ServiceDateId; ThreadStatus Status; DateTimeOffset? LockedAt; IReadOnlyCollection<ThreadMessageResponse> Items; string? NextCursor }`
- `ThreadStateResponse { string ServiceDateId; ThreadStatus Status; DateTimeOffset? LockedAt }`
- `PrivilegedThreadMessageResponse { string Id; string SenderDisplayName; string Body; MessageVisibility Visibility; DateTimeOffset CreatedAt; DateTimeOffset? HiddenAt }`
- `PrivilegedThreadMessagePageResponse { string ThreadId; string ServiceDateId; ThreadStatus Status; DateTimeOffset? LockedAt; IReadOnlyCollection<PrivilegedThreadMessageResponse> Items; string? NextCursor }`

### Frozen mobile client/OpenAPI work

- `ThreadEndpoints` must use the following operation names:
  - `ListThreadMessages`
  - `PostThreadMessage`
  - `ReportThreadMessage`
  - `HideThreadMessage`
  - `LockThread`
  - `ReadPrivilegedThreadMessages`
- `apps/mobile/src/core/api/generate-api-client.mjs` must add six T19 operations and generator support for success-header envelopes on the five ordinary operations.
- `apps/mobile/src/core/api/auth-api.ts` must:
  - leave existing `AuthApi`, `T14Api`, and `T18Api` signatures untouched
  - add additive `T19Api extends T18Api`
  - expose thread methods only on `T19Api` / `GeneratedAuthApi`
  - keep `getMobileApi()` as the only registration point
- `apps/mobile/tests/unit/api/generated-api-client.test.ts` must add:
  - method list assertions for the six T19 methods
  - `ETag` envelope assertions for ordinary thread methods
  - `X-Step-Up-Token` header assertion for privileged thread reads

### Frozen test-first sequence and fixtures

1. **Contract red first**
   - Create `tests/HusayniaTabruk.Api.ContractTests/Threads/ThreadEndpointContractTests.cs`.
   - First red assertions:
     - route table/openapi now expect the six T19 operations
     - ordinary response DTOs expose only privacy-safe fields
     - ordinary route metadata exposes body limits/rate limits/`Retry-After`
     - privileged route requires the step-up header and admin path
2. **Application red second**
   - Create `tests/HusayniaTabruk.Application.Tests/Threads/ThreadServiceTests.cs`.
   - Reuse the current unit-test pattern: fake repositories + fixed clock + recording writers.
   - First red assertions:
     - live manager/signup authorization transitions immediately change access
     - stale `If-Match` allows only exact send/report replays
     - privileged read rolls back on audit failure
     - duplicate report mismatch returns conflict
3. **Real PostgreSQL red third**
   - Create `tests/HusayniaTabruk.IntegrationTests/Threads/ThreadIntegrationTests.cs` plus `ThreadIntegrationTestSupport.cs`.
   - Reuse:
     - `PostgresTestDatabase` / `RequiresPostgresFact`
     - `PersistenceSeed`
     - `SignupDecisionTestSupport.CreateSeedAsync(...)`
     - `AuthApiHost`
     - step-up/login helpers from `AdminIntegrationTests`
     - failure-trigger pattern already used by T16/T17 tests to force transactional rollback
   - First red assertions:
     - missing routes or incorrect concealment
     - no thread backfill
     - privileged read cannot yet consume step-up + audit atomically

### Frozen real-PostgreSQL seeding strategy

- Base seed: `SignupDecisionTestSupport.CreateSeedAsync(...)` to get manager/admin/member users, service date, help need, existing thread row, and seeded passwords.
- Ordinary participant path:
  - add a pending signup, approve it, then mutate live state between assertions (withdraw/cancel/decline, manager-role revoke/restore, date cancel, cross-org actor).
- Existing thread content:
  - use `PersistenceSeed.AddVisibleThreadMessageAsync(...)` for an initial visible message, then add more rows directly when multi-page scenarios are needed.
- Privileged read:
  - log in as admin, issue `X-Step-Up-Token` via `/api/v1/auth/step-up`, call `/api/v1/admin/moderation/thread-reads`.
- Audit failure:
  - add a PostgreSQL `BEFORE INSERT` failure trigger on `privileged_access_events`, assert `503`/no content payload leak, verify the step-up token is still usable afterward because the transaction rolled back.
- Privacy:
  - assert no thread body text appears in `AuthApiHost.Logs`.
  - assert no new `notification.push_requested`, `thread.updated`, or other thread-message outbox rows were written by T19 send/report/hide/lock/privileged-read flows.
- T17 protection:
  - rerun the existing close-without-thread regression in addition to the new thread-focused PG suite.

### Frozen exact commands

#### Preconditions

```powershell
docker compose up -d postgres
$env:TABRUK_TEST_POSTGRES_CONNECTION = "Host=127.0.0.1;Port=5432;Database=tabruk;Username=tabruk;Password=tabruk_local_development;Pooling=false"
```

> If the local cluster does not already contain the required `tabruk_app` `NOLOGIN` role, the existing PostgreSQL fixture blocks exactly as designed; T19 does not create that role.

#### Focused red-first / green suites

```powershell
dotnet test .\tests\HusayniaTabruk.Api.ContractTests\HusayniaTabruk.Api.ContractTests.csproj --filter "FullyQualifiedName~Threads"
dotnet test .\tests\HusayniaTabruk.Application.Tests\HusayniaTabruk.Application.Tests.csproj --filter "FullyQualifiedName~Threads"
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --filter "FullyQualifiedName~Threads"
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --filter "FullyQualifiedName~CloseWithoutAnExistingThreadDoesNotCreateOrBackfillThread"
```

#### OpenAPI snapshot and generated client

```powershell
dotnet run --project .\src\HusayniaTabruk.Api\HusayniaTabruk.Api.csproj --urls http://127.0.0.1:5077
Invoke-WebRequest http://127.0.0.1:5077/openapi/v1.json -OutFile .\docs\api\openapi.json
npm --prefix .\apps\mobile run generate:api
npm --prefix .\apps\mobile run check:generated-client
```

#### Full regression before independent gates

```powershell
dotnet restore .\HusayniaTabruk.sln
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror
dotnet test .\HusayniaTabruk.sln --no-build
dotnet format .\HusayniaTabruk.sln --verify-no-changes
npm ci --prefix .\apps\mobile
npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
npm test --prefix .\apps\mobile -- --runInBand
npm --prefix .\apps\mobile run check:generated-client
```

### Frozen file ownership and execution order

- **Wave 1 (parallel, red-first)**  
  `T19-IMPLEMENT-APP`, `T19-IMPLEMENT-API`, `T19-IMPLEMENT-INTEGRATION`
- **Wave 2**  
  `T19-IMPLEMENT-CLIENT`
- **Wave 3 (parallel, independent gates)**  
  `T19-VALIDATE-TEST`, `T19-VALIDATE-SECURITY`, `T19-VALIDATE-REVIEW`
- **Wave 4**  
  `T19-QA`
- **Wave 5**  
  `T19-JUDGE`

No two parallel implementation tasks may edit the same file. The frozen file boundaries above are the collision contract for T19.
