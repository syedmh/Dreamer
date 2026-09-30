# T17 QA / E2E Result

## QA RESULT

Environment: Windows_NT; .NET SDK 10.0.400; PostgreSQL 18.6 x64 on
`127.0.0.1:5432`; ASP.NET Core API hosted on ephemeral loopback Kestrel ports.
Stateful scenarios used migrated isolated `tabruk_it_*` schemas, real
`POST /api/v1/auth/login`, bearer authentication, EF Core/Npgsql transactions,
and PostgreSQL state assertions. Synthetic `example.test` identities only.

Setup and primary command:

```powershell
$env:TABRUK_TEST_POSTGRES_CONNECTION =
  'Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=<local-dev>;Persist Security Info=true'

dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --configuration Release --no-restore --filter 'Category=T17' `
  --logger 'console;verbosity=minimal'
```

Actual:

```text
PostgreSQL 18.6 on x86_64-windows
Passed: 27, Failed: 0, Skipped: 0, Total: 27, Duration: 37 s
```

Scenario 1: Authorized date and need editing with ETags [PASS]
  Steps: Login as the managing Food Incharge; PATCH the date/need with a strong
  quoted `If-Match`; query PostgreSQL.
  Expected: 200 with the next ETag; requested fields and versions persist.
  Actual: Successful edits persisted; concurrent date editing preserved an
  independently committed need edit.
  Evidence: `EditDateWithStaleIfMatch...` first write and
  `DatePatchConcurrentNeedPatchPreservesTheNeedEdit` passed.

Scenario 2: Stale and malformed If-Match [PASS]
  Steps: Repeat an edit with the old ETag; race close/cancel against a version
  advance; send `If-Match: W/"0"` over live Kestrel HTTP.
  Expected: stale writes return `412 stale_version`; malformed header returns
  RFC problem `400 invalid_date_request`; no partial writes/effects.
  Actual: exact statuses and unchanged PostgreSQL state.
  Evidence:

```text
status=400 code=invalid_date_request
detail="A valid If-Match header is required."
fieldErrors.ifMatch="A valid If-Match header is required."
```

Scenario 3: Capacity reduction conflict is no-write [PASS]
  Steps: Approve two occupied slots; PATCH capacity to 1 with current need ETag.
  Expected: `409 capacity_unavailable`; instructions, capacity, status, versions,
  signup, audit, notification, outbox, and idempotency state remain unchanged.
  Actual: exact conflict and before/after state equality.
  Evidence: `NeedPatchReducingCapacityBelowApprovedParticipantsReturnsConflictWithoutWrites`.

Scenario 4: Close preserves history and locks only existing threads [PASS]
  Steps: Approve a signup; close its open date. Repeat from a seed with no thread.
  Expected: date/need closed; approved signup retained; existing thread locked;
  absent thread not created or backfilled.
  Actual: exact persisted states.
  Evidence: `ClosePreservesApprovedSignupHistoryAndLocksExistingThread` and
  `CloseWithoutAnExistingThreadDoesNotCreateOrBackfillThread`.

Scenario 5: Close blocks submission, including retry [PASS]
  Steps: Start an authenticated signup, pause before save, close canonical
  date/need, release, then retry the same key.
  Expected: both calls return `409 category_closed`; zero signup/effect writes.
  Actual:

```text
POST /auth/login = 200
POST /needs/{id}/signups = 409
retry POST /needs/{id}/signups = 409
needStatus=closed signupVersion=0
signups=0 audits=0 notifications=0 outbox=0 idempotency=0
```

Scenario 6: Cancellation cancels active signups and revokes access [PASS]
  Steps: Prepare approved, pending, and waitlisted signups; cancel an open or
  previously closed date; inspect date, needs, signups, thread, notifications,
  outbox, audit, and versions.
  Expected: active signups become cancelled, waitlist metadata clears, thread is
  locked, each distinct primary receives one cancellation notification, and a
  `thread.access_changed` invalidation is emitted without duplicate same-primary
  effects.
  Actual: three distinct primaries produced three notifications; duplicate
  signups for one primary produced one contact effect.
  Evidence: cancellation, deduplication, and cancel-after-close tests passed.

Scenario 7: Idempotent retry and fingerprint mismatch [PASS]
  Steps: Cancel with a new idempotency key; retry identical request/key and old
  ETag; retry the same key with a different ETag for close and cancel.
  Expected: identical retry returns current projection with no duplicate effects;
  mismatch returns `409 idempotency_mismatch`.
  Actual: exact behavior and stable effect counts.
  Evidence: retry test plus both close/cancel mismatch theory cases passed.

Scenario 8: Authority revocation/disable race [PASS]
  Steps: Authenticate as Food Incharge, pause after initial authorization, revoke
  the role or disable membership, then continue edit-date, edit-need, close, and
  cancel.
  Expected: all eight combinations return `403 forbidden`; no mutation commits.
  Actual: 8/8 combinations passed with before/after PostgreSQL equality.

Scenario 9: Effect failure rolls back the whole cancellation [PASS]
  Steps: Inject PostgreSQL failures on notification INSERT and idempotency UPDATE;
  cancel open and previously closed dates.
  Expected: `503 dependency_unavailable`; date, need, signup, thread, and effects
  remain at the exact pre-request state.
  Actual: all four failure combinations rolled back atomically.

Scenario 10: Need signup-version and concurrent-need CAS loss [PASS]
  Steps: Advance signup version or edit a need between load and write.
  Expected: `412 stale_version`; no lost update and no partial effects.
  Actual: canonical concurrent values were preserved; T17 writes did not commit.

Scenario 11: Remediated need-patch wire contract [PASS]
  Steps:
  - Run the focused nine-test contract suite.
  - Send live HTTP patches with omitted `capacity` and numeric `status: 0`.
  - Execute the production parser with `capacity: null`, `status: "open"`.
  Expected: omitted capacity and numeric status return 400; explicit null parses
  as unlimited capacity; OpenAPI requires `capacity` while allowing null.
  Actual:

```text
Contract tests: Passed 9, Failed 0, Total 9
omitted capacity: status=400 code=invalid_date_request
numeric status:   status=400 code=invalid_date_request
nullable probe:   isSuccess=True capacityIsNull=True status=Open
```

Scenario 12: PostgreSQL isolation and cleanup [PASS]
  Steps: After all scenarios, query for `tabruk_it_*` schemas.
  Expected: fixtures remove disposable state.
  Actual: schema count `0`; production and test source files were not modified.

Scenarios: 12 run, 12 passed, 0 failed

Conclusion: PASS

## Standard result block

STATUS: PASS

SUMMARY: T17 passed independent Food-Incharge/API-consumer validation. All
required edit, ETag, close, submit-blocking, cancellation, notification/access
invalidation, retry, race, rollback, and remediated need-patch contract journeys
passed.

WORK_COMPLETED:

- Installed and ran local PostgreSQL 18.6 and provisioned the required NOLOGIN
  `tabruk_app` role.
- Executed 27 PostgreSQL-backed T17 cases through real Kestrel HTTP, login, bearer
  authentication, EF Core, and Npgsql.
- Executed the close-during-submit HTTP race and nine focused API contract tests.
- Probed malformed `If-Match`, omitted capacity, and numeric status over live HTTP.
- Verified nullable capacity parsing and final disposable-schema cleanup.
- Did not modify production or test code.

EVIDENCE: The QA RESULT above.

ARTIFACTS:

- `.ai-org/missions/2026-08-14-husaynia-tabarruk-signup/qa-results.md`

FINDINGS:

- No T17 product defect was reproduced.
- Validation errors are RFC problem details with stable codes and useful details.
- Initial fixture cleanup failed when the supplied Npgsql connection omitted
  `Persist Security Info=true`; adding that local harness setting produced a clean
  27/27 run. This was environment setup, not a product failure.

RISKS:

- Deterministic authority/concurrency/effect-failure timing uses test-only
  repository hooks or PostgreSQL failure triggers; HTTP/auth/transactions and
  persisted writes remain real.
- Invalid-body HTTP probes use the contract host's synthetic authentication
  override because validation occurs before business persistence. All stateful
  authorization scenarios use the real login and bearer pipeline.

BLOCKERS: None.

NEXT_ACTION: Accept the T17 QA gate.
