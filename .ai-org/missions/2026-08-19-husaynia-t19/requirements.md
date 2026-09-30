# T19 requirements — threads and moderation API

## Objective and scope

Implement only T19: the managed-date ordinary thread and the separate privileged historical moderation read.  T20 notification delivery, outbox workers, push, T21 mobile UI, deployment, migrations unless proven necessary, and all later tasks are out of scope.

## Authoritative requirements

- AC-12 is the product acceptance criterion.  The T19 implementation-plan entry (lines 542–548) is the implementation scope and exit criterion.
- An ordinary thread is available only to an actor who is **currently** either (a) the live, active managing Food Incharge for the managed date or (b) the live, active primary contact of an approved signup for it.
- Every ordinary operation (list, page/read, send, report, hide, lock where applicable) authorizes from current actor and live database state. Pending, waitlisted, declined, withdrawn, cancelled, disabled, revoked, cross-organization, and unrelated actors are denied immediately.
- Ordinary administrators are not ordinary participants: conceal their access with 404. A missing ordinary thread is likewise concealed with 404. T19 must not provision or backfill a thread.
- Ordinary results show only privacy-safe sender display, timestamp, and managed-date association; direct contact information is never included. Hidden message content is redacted.
- One persisted thread may exist per service date. Existing architecture governs manager hide/lock authority.
- Ordinary sending, reporting, paging, and mutation behavior must use PostgreSQL compare-and-swap/chronology/idempotency conventions already present in the product. Duplicate reports are controlled.
- The privileged historical moderation read is a distinct endpoint/page. It requires `PrivilegedThreadRead`, a single-use, unexpired, purpose-bound step-up, a reason, purpose, and ASCII `caseId` of at most 100 characters. General admin privilege never makes an actor ordinarily eligible.
- On privileged page read, consume the step-up atomically with a successful insert-only `privileged_access_events` audit event and the page read. If the audit insert cannot succeed, deny with no content. Record cursor and page hash.
- Frozen limits in `Domain/Common/ApplicationLimits.cs` are binding: default page 50/max 100; message 2,000 Unicode scalars/8 KiB UTF-8/request 12 KiB; report 500 scalars/2 KiB/request 4 KiB; reason 500 scalars/2 KiB; ASCII case ID 100; administrative request 4 KiB; account post limits 3/10 seconds, 10/minute, 60/hour; organization post 300/hour; account reports 5/hour and 20/day; organization reports 100/day; step-up lifetime 5 minutes. Persistence permits `case_id` 200, but application/domain input must enforce 100 ASCII.
- Oversized payloads return 413. Throttled requests return 429 and `Retry-After`.
- Message bodies must not enter logs, outbox, or push payloads. Do not implement push. Create only a generic T20 notification intent if the established T19 contract explicitly owns its creation; otherwise create none.

## Acceptance evidence

Test first and retain focused red evidence before green. Use real portable PostgreSQL 18.6 with zero skipped focused Application/API tests. Cover the authorization-transition matrix, concealed missing/administrator 404s, moderation audit failure, step-up replay/expiry/purpose/case bounds, rate/payload/page/idempotency/CAS/chronology, duplicate reporting, hidden redaction, and absence of message body in logs/outbox/push. Explicitly prove T17 close-without-thread did not create one.

When API shape changes, verify OpenAPI snapshot, generated client drift, and mobile regression. Before final judgment run focused suites, full solution test/build/format, generated drift, and mobile regression. Independent test, security, code-review, QA, and engineering-judge gates are mandatory.

## Scope constraints

Authorized production/test areas are `src/HusayniaTabruk.Application/Threads/**`, `src/HusayniaTabruk.Api/Endpoints/V1/Threads/**`, `tests/HusayniaTabruk.Application.Tests/Threads/**`, and `tests/HusayniaTabruk.IntegrationTests/Threads/**`. Generated OpenAPI/client artifacts and required registration/configuration may change only when API work makes that necessary, with rationale recorded. No migration absent repository proof and a documented rationale.
