# QA RESULT

Environment: Windows_NT; .NET SDK 10.0.400; ASP.NET Core API hosted on real
ephemeral loopback HTTP ports; PostgreSQL 18.6 started as an isolated local
cluster; migrated per-test schemas; real login endpoint, bearer authentication,
EF Core/Npgsql persistence, audit, notification, outbox, roster, and
`/signups/mine` paths. All identities and data were synthetic `example.test`
fixtures.

Reproduction setup:

```powershell
$root = 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk'
$pgbin = 'C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin'
$cluster = & "$root\tests\HusayniaTabruk.IntegrationTests\Persistence\Postgres18TestCluster.ps1" -Action Start
& "$pgbin\psql.exe" -h 127.0.0.1 -p $cluster.Port -U postgres -d postgres -v ON_ERROR_STOP=1 -c 'CREATE ROLE tabruk_app NOLOGIN'
$env:TABRUK_TEST_POSTGRES_CONNECTION = $cluster.ConnectionString

dotnet test "$root\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj" `
  --filter "Category=T15" --logger "console;verbosity=minimal" --no-restore

dotnet test "$root\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj" `
  --filter "Category=T15QA" --logger "console;verbosity=normal" --no-restore

dotnet test "$root\tests\HusayniaTabruk.Api.ContractTests\HusayniaTabruk.Api.ContractTests.csproj" `
  --filter "FullyQualifiedName~SignupDecisionEndpointContractTests" `
  --logger "console;verbosity=minimal" --no-restore

& "$root\tests\HusayniaTabruk.IntegrationTests\Persistence\Postgres18TestCluster.ps1" `
  -Action Stop -ClusterId $cluster.ClusterId
```

## Scenario 1: Food Incharge approval reaches member, roster, and durable effects [PASS]

- Steps: login as Food Incharge and member; approve the member's pending signup;
  call member `GET /api/v1/signups/mine`; call operator roster; query notification,
  outbox, audit, and idempotency rows.
- Expected: `200`, ETag, approved in both projections, one notification and one
  push intent, exactly one atomic effect set.
- Actual: all outcomes matched.
- Evidence:

```text
PRIMARY decision=OK member=approved roster=approved notification=Signup approved
outbox=notification.push_requested etag="2" version=2
```

## Scenario 2: Approve, decline, and waitlist remain consistent [PASS]

- Steps: execute each decision through HTTP, then read roster and PostgreSQL.
- Expected: response, ETag, roster, and stored status agree.
- Actual: exact agreement for Approved, Declined, and Waitlisted.
- Evidence: `ApproveDeclineAndWaitlistAgreeAcrossResponseMineRosterAndDatabaseRows`
  passed against PostgreSQL.

## Scenario 3: Unauthorized and cross-organization attacks are concealed [PASS]

- Steps: attempt decisions anonymously, with a disabled account, revoked role,
  unrelated same-org manager, cross-org manager, and unknown signup ID.
- Expected: anonymous/disabled `401`; record-specific denials `404
  signup_not_found`; no writes or identifier disclosure.
- Actual: expected statuses/codes, zero effects, and identical concealed behavior.
- Evidence: `AnonymousDisabledRevokedUnrelatedManagerAndCrossTenantAttacksWriteNothing`
  and `RecordSpecificDenialsConcealTargetExistence` passed.

## Scenario 4: Capacity conflict rolls back and same-key retry recovers [PASS]

- Steps: fill capacity; approve with a new idempotency key; verify rollback;
  correct capacity; retry the identical request with the same key.
- Expected: first `409 capacity_unavailable` and zero effects; retry `200
  approved` and one effect set.
- Actual:

```text
CAPACITY first=Conflict:capacity_unavailable effects=0
retry=OK:approved effects=1 sameKey=true
```

## Scenario 5: Concurrent approvals never overbook [PASS]

- Steps: send two simultaneous approvals against capacity one and the same root
  version.
- Expected: one `200`, one `412 stale_version`, one approved participant, and
  winner-only effects.
- Actual: both concurrency tests passed with no excess slot.
- Evidence: `SimultaneousCapacityOneApprovalsFromTheSameRootYieldOneWinnerAndOneStaleVersion`
  and `SimultaneousCapacityOneApprovalsNeverOverbookAndPersistWinnerOnlyEffects`.

## Scenario 6: Retry, mismatch, stale-write recovery, and atomicity [PASS]

- Steps: repeat completed keys; reuse a key with changed action/reason/target/version;
  retry stale and temporarily unauthorized attempts; force CAS loss and effect
  insertion failures.
- Expected: completed retry returns authoritative state without duplicate effects;
  mismatches return `409`; failed attempts leave no receipt and recover safely;
  partial effects never commit.
- Actual: all expected results and counts matched.
- Evidence: six relevant T15 integration tests passed.

## Scenario 7: Private reason and protected outputs remain concealed [PASS]

- Steps: decline with a private operational reason; inspect HTTP response,
  signup row, notification, outbox payload, and audit row.
- Expected: reason exists only in insert-only audit storage.
- Actual: no reason appeared outside the audit event.
- Evidence: `ReasonAppearsOnlyInTheInsertOnlyAuditRow` passed.

## Scenario 8: Decision state controls thread eligibility [PASS]

- Steps: evaluate thread access while pending, waitlisted, declined, and after a
  persisted approval/reassignment.
- Expected: only persisted approved state grants access; invalid waitlisted
  approval is rejected.
- Actual: exact matrix matched.
- Evidence: both T15 thread-eligibility integration tests passed.

## Scenario 9: Operator input errors are stable and actionable [PASS]

- Steps: exercise missing/invalid `If-Match`, missing decline reason, unknown and
  duplicate JSON properties, body limits, declared failure codes, and required
  idempotency/ETag contracts.
- Expected: stable ProblemDetails codes, field errors for `ifMatch`, frozen
  response statuses, and documented required headers.
- Actual: 10 contract tests passed.
- Evidence:

```text
Passed: 10, Failed: 0, Skipped: 0
```

## Execution totals

```text
T15 PostgreSQL/API integration: 19 passed, 0 failed, 0 skipped
T15 operator/member QA harness:   2 passed, 0 failed
Decision API contracts:          10 passed, 0 failed
```

Scenarios: 9 run, 9 passed, 0 failed

Conclusion: PASS

T16 readiness: READY. T15 decision state, full-root CAS, capacity safety,
idempotency recovery, tenant concealment, durable notification intent, and
thread eligibility are verified through real API and PostgreSQL.

## Findings

- The member can observe the updated signup state through `/signups/mine`.
- T15 persists the in-app notification and push outbox intent, but this slice has
  no member notification-read endpoint. Actual notification inbox/push delivery
  cannot be observed from the member API and remains a downstream integration risk.
- A fresh local PostgreSQL cluster must be provisioned with `tabruk_app NOLOGIN`;
  otherwise all persistence tests stop with a precise fixture prerequisite error.

## Standard status block

STATUS: PASS

SUMMARY: Nine complete operator/member scenarios passed; 31 executed tests passed.

WORK_COMPLETED: Exercised real HTTP authentication and decisions, PostgreSQL
persistence, roster/member projections, authorization isolation, capacity
conflict/recovery, concurrency, idempotency, privacy, thread eligibility, and
operator error contracts.

EVIDENCE: This QA RESULT.

ARTIFACTS:

- `.ai-org/missions/2026-08-18-husaynia-t15-e2e-validation/qa-results.md`
- `HusayniaTabruk/tests/HusayniaTabruk.IntegrationTests/Signups/Decisions/T15OperatorMemberQaTests.cs`

FINDINGS: Notification persistence is verified, but member-side notification
retrieval/delivery is not exposed by this backend slice.

RISKS: Push dispatch and member notification inbox behavior require validation
when their consumer/API is implemented.

BLOCKERS: None for T16.

NEXT_ACTION: Proceed to T16; retain the capacity-recovery and operator/member
journeys as regression gates.
