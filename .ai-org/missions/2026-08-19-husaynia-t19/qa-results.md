# QA RESULT

Environment: Windows_NT; .NET SDK 10.0.400; Node 24.19.0; npm 11.17.0. The API was exercised through the real ASP.NET test host over HTTP against an isolated local PostgreSQL 18.6 server at `127.0.0.1:55432`. The cluster used local trust authentication and contained the required `tabruk_app NOLOGIN` role. No production code was modified.

Common PostgreSQL setup:

```powershell
& 'C:\Users\syedhu\AppData\Local\Temp\pg18_6\pgsql\pgsql\bin\pg_ctl.exe' `
  -D 'C:\Users\syedhu\AppData\Local\Temp\HusayniaTabruk-t19-pg18' `
  -l 'C:\Users\syedhu\AppData\Local\Temp\HusayniaTabruk-t19-pg18.log' `
  -o '-p 55432' -w start

$env:TABRUK_TEST_POSTGRES_CONNECTION =
  'Host=127.0.0.1;Port=55432;Database=postgres;Username=postgres;Pooling=false;Include Error Detail=true'
$env:TABRUK_TEST_PSQL_PATH =
  'C:\Users\syedhu\AppData\Local\Temp\pg18_6\pgsql\pgsql\bin\psql.exe'
```

PostgreSQL verification:

```powershell
& $env:TABRUK_TEST_PSQL_PATH -h 127.0.0.1 -p 55432 -U postgres -d postgres -Atc `
  "select version(); select rolname||':'||rolcanlogin from pg_roles where rolname in ('postgres','tabruk_app') order by rolname;"
```

Actual:

```text
PostgreSQL 18.6 on x86_64-windows
postgres:true
tabruk_app:false
```

## Scenario 1: Active manager and approved primary-contact ordinary journeys [PASS]

Steps:

```powershell
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --filter "FullyQualifiedName~HusayniaTabruk.IntegrationTests.Threads.ThreadIntegrationTests" `
  --nologo --logger "console;verbosity=normal"
```

Expected: The current manager can list/post. An approved primary contact can access and post. Responses preserve managed-date association, privacy-safe sender display, `createdAt`, and ETag/version behavior.

Actual: `ManagerCanListExistingThreadMessages`, `ParticipantAccessTracksLiveApprovalAndWithdrawalState`, `OrdinaryPostUsesCasAllowsOnlyExactReplayAndCreatesNoOutbox`, and the approved-signup concurrency journey passed.

Evidence: Manager list returned `200`, date/status matched, sender display was `Manager`, the message was visible, and ETag was `"1"`. Post returned `201` with ETag `"1"`. The generated/API schemas include `createdAt` and exclude direct contact identifiers.

## Scenario 2: Live eligibility transitions and concealed ordinary access [PASS]

Steps: Run the Scenario 1 command.

Expected: Pending, waitlisted, declined, cancelled, withdrawn, reassigned, role-revoked, disabled, date-cancelled, unrelated ordinary admin, and missing-thread states return concealed `404`; no losing race may mutate the thread.

Actual: Four non-approved signup cases, approval-to-withdrawal, five revocation-first race cases, ordinary administrator concealment, and missing-thread concealment all passed.

Evidence: `NonApprovedSignupStatesAreConcealed` passed for all four states; `RevocationThatStartsBeforeAuthorizationWinsWithoutThreadMutation` passed for withdrawal, manager reassignment, role revocation, membership disable, and date cancellation; persisted `thread_messages` remained empty in losing races.

## Scenario 3: Send idempotency, CAS, privacy, and no notification expansion [PASS]

Steps: Run the Scenario 1 command.

Expected: Exact retry succeeds without duplication; a stale different request returns `412`; only one message is stored; body is absent from logs/outbox.

Actual: Initial post and exact replay both returned `201` with the same ETag. A stale different post returned `412`.

Evidence: Exactly one `thread_messages` row, zero outbox rows, and no matching body in collected API logs.

## Scenario 4: Report, hide, redaction, privileged recovery, and lock [PASS]

Steps: Run the Scenario 1 command.

Expected: Report returns `202`; exact duplicate is idempotent; conflicting duplicate is `409`; manager hide redacts ordinary content while preserving the stored body; privileged read recovers it; lock advances ETag and prevents later post.

Actual: The complete `ReportHideRedactPrivilegedReadAndLockPreserveBodiesAndVersions` journey passed.

Evidence: ETags advanced `"1"` through `"4"`; duplicate report retained `"2"`; mismatch returned `409`; ordinary body was exactly `This message was hidden by a moderator.`; privileged body matched the original; post after lock returned `409`; exactly one report and zero outbox rows persisted.

## Scenario 5: Privileged page step-up, reason/purpose/case, cursor/hash audit [PASS]

Steps: Run the Scenario 1 command.

Expected: Active admin plus `thread.privileged.read` step-up can read. Each page creates exactly one immutable audit with resource, case, cursor, and exact response hash.

Actual: Two one-item pages returned `200`, each with a separate step-up token.

Evidence: Two `privileged_access_events` rows persisted. The second row recorded the returned first-page cursor, `resourceType=thread`, the thread ID, `CASE-123`, and a page hash equal to SHA-256 of the exact returned JSON.

## Scenario 6: Privileged replay, expiry, wrong purpose, and audit failure recovery [PASS]

Steps: Run the Scenario 1 command.

Expected: Token replay, wrong purpose, and exact five-minute expiry are denied without audit. Forced audit insertion failure returns no content, rolls back token consumption, and permits recovery after dependency restoration.

Actual: Replay/wrong-purpose/expiry returned `401`. Forced audit failure returned `503` without the secret body; the same token succeeded after the trigger was removed.

Evidence: No audit rows were written for wrong-purpose/expiry or failed audit; one row persisted after recovery.

## Scenario 7: Exact page/reason/case and payload boundaries [PASS]

Steps: Run the Scenario 1 command, then:

```powershell
dotnet test .\tests\HusayniaTabruk.Api.ContractTests\HusayniaTabruk.Api.ContractTests.csproj `
  --filter "FullyQualifiedName~HusayniaTabruk.Api.ContractTests.Threads.ThreadEndpointContractTests" `
  --nologo --logger "console;verbosity=normal"
```

Expected: Defaults/maxima succeed; max+1 and malformed cursor return `400`; exactly 100 printable ASCII case characters succeed while 101 fail without consuming the token; oversized message/report/admin bodies return `413 payload_too_large`.

Actual: All frozen page/reason/case cases passed. Runtime requests returned `413` for message, report, hide, lock, and privileged-read oversized bodies.

Evidence: HTTP logs include four ordinary `413 application/problem+json` responses; PostgreSQL journey also proved privileged `413` with zero audit rows.

## Scenario 8: Rate limiting and Retry-After [PASS]

Steps: Run the Scenario 7 contract command.

Expected: Frozen account/organization partitions are attached; exhausted post/report limits return `429 rate_limited` with positive integer `Retry-After`.

Actual: Contract-host HTTP journeys exhausted both shortest active limits and returned `429`.

Evidence: `ThreadMutationRoutesEmitRetryAfterWhenRateLimited` passed; HTTP logs show post and report `429 application/problem+json`; both responses contained a positive integer `Retry-After`.

## Scenario 9: T17 no-backfill regression [PASS]

Steps:

```powershell
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj `
  --filter "FullyQualifiedName~CloseWithoutAnExistingThreadDoesNotCreateOrBackfillThread" `
  --nologo --logger "console;verbosity=minimal"
```

Expected: Closing a date without an existing thread does not provision or backfill one.

Actual: `1 passed, 0 failed, 0 skipped`.

Evidence: Real PostgreSQL regression completed in 5 seconds.

## Scenario 10: Generated mobile client ETag/version and privileged header behavior [PASS]

Steps:

```powershell
npm test --prefix .\apps\mobile -- --runInBand --runTestsByPath tests/unit/api/generated-api-client.test.ts
npm --prefix .\apps\mobile run check:generated-client
```

Expected: Five ordinary methods expose `{ data, etag, version }`, reject missing/invalid successful ETags, preserve body schemas without `version`, propagate `If-Match`, parse RFC 9457/`Retry-After`, and send `X-Step-Up-Token` for privileged reads. Generated code has no drift.

Actual: `10 passed, 0 failed`; drift check reported `Generated API client is up to date.`

Evidence: ETags `"4"` through `"8"` mapped to versions `4` through `8`; a success without ETag was rejected as malformed; privileged header assertion passed.

## Execution totals

- PostgreSQL T19 integration cases: 25 passed, 0 failed, 0 skipped.
- Explicit T17 PostgreSQL regression: 1 passed, 0 failed, 0 skipped.
- API contract-host cases: 8 passed, 0 failed, 0 skipped.
- Generated mobile client cases: 10 passed, 0 failed.
- Generated-client drift gate: 1 passed.

Scenarios: 10 run, 10 passed, 0 failed.

Conclusion: PASS

---

STATUS:          PASS

SUMMARY: Independent API-consumer QA passed all requested T19 ordinary, moderation, failure/recovery, boundary, rate-limit, PostgreSQL persistence, and generated-client journeys.

WORK_COMPLETED: Read authoritative AC-12/T19 requirements and frozen design; started and verified isolated PostgreSQL 18.6; exercised the real HTTP test host; verified persisted state, rollback, audit, privacy, CAS/idempotency, exact boundaries, Retry-After, no-backfill, and generated client behavior.

EVIDENCE: The QA RESULT above. Aggregate executed checks: 45 passed, 0 failed, 0 skipped.

ARTIFACTS:       `.ai-org/missions/2026-08-19-husaynia-t19/qa-results.md`

FINDINGS: Docker Desktop was unavailable, and the repository's compose credentials did not authenticate to the already-running unrelated local server on port 5432. QA recovered by using the existing disposable trust-authenticated PostgreSQL 18.6 cluster on port 55432. No product usability defect was observed.

RISKS: Generated-client transport behavior is intentionally exercised with a controlled fetch double; the real API/DB behavior is independently covered by the ASP.NET/PostgreSQL journeys. Long-window organization/hour/day rate limits are verified as endpoint metadata; runtime exhaustion covered the shortest post/report limits to avoid destructive/high-volume execution.

BLOCKERS: None.

NEXT_ACTION: Proceed to the independent engineering-judge gate.
