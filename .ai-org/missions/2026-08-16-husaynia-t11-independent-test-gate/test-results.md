# T11 Independent Test Gate

Date: 2026-08-16

## Result

**PASS** — AC-3 and the T11 exit criteria are proven by executed tests against PostgreSQL 18.6.

Database:

`Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false`

Server evidence:

`PostgreSQL 18.6 on x86_64-windows, compiled by msvc-19.44.35228, 64-bit`

## Executed suites

| Scope | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Original T11 date integration baseline | 3 | 0 | 0 |
| Expanded T11 date integration/attack suite | 5 | 0 | 0 |
| Date domain boundaries | 132 | 0 | 0 |
| API contract/security regressions | 57 | 0 | 0 |
| PostgreSQL idempotency concurrency | 4 | 0 | 0 |
| PostgreSQL independent security gates | 10 | 0 | 0 |
| Same-key concurrent-open stability reruns | 3 | 0 | 0 |
| **Total test executions** | **214** | **0** | **0** |

The concurrent-open test passed 3/3 repeated runs (observed flake rate 0%).

## Independent tests added

- `DateIntegrationTests.MemberCannotDiscoverDraftOrMutateNeedAndOpenSurfaces`
- `DateIntegrationTests.ConcurrentOpenWithSameKeyHasOneDurableEffectAndNoServerError`

No production code was changed.

## Acceptance mapping

- AC-3 valid Food Incharge date/need/open flow and eligible-member list/detail:
  `FoodInchargeCanCreateNeedAndOpenDateForMemberSafeQueries`
- Instructions, category, capacity/availability, and cancellation deadline:
  `FoodInchargeCanCreateNeedAndOpenDateForMemberSafeQueries`
- No private roster/contact fields in member query payload:
  `FoodInchargeCanCreateNeedAndOpenDateForMemberSafeQueries`
- Non-manager mutation denial across create, need-create, and open:
  `NonManagerCannotMutateDatesAndAnonymousCannotReadThem`;
  `MemberCannotDiscoverDraftOrMutateNeedAndOpenSurfaces`
- Draft concealment:
  `MemberCannotDiscoverDraftOrMutateNeedAndOpenSurfaces`
- Retry/concurrent idempotency and one durable audit/outbox effect:
  `OpenRetryWithSameIdempotencyKeyDoesNotDuplicateEffects`;
  `ConcurrentOpenWithSameKeyHasOneDurableEffectAndNoServerError`;
  `PostgresIdempotencyStoreTests`
- OpenAPI snapshot, auth fail-closed, body/rate-limit and sanitized-error contracts:
  `HusayniaTabruk.Api.ContractTests` (57 passing)

## Remaining non-blocking gaps

1. No HTTP attack for two different idempotency keys opening the same date concurrently.
2. No T11-specific cross-organization date query/mutation test.
3. No second active Food Incharge attack against a date managed by another Food Incharge.
4. No date-specific disabled/revoked-actor test; only broader auth regressions cover revocation.
5. No multi-page ordering/cursor boundary test for open-date queries.
6. No dependency-unavailable test specifically on date list/detail/mutation endpoints.
7. No future-state test proving `availability` subtracts approved commitments once signup persistence is integrated.
8. Existing privacy assertions are substring-based rather than a strict allow-listed JSON schema assertion.

