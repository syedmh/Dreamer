# T19 test results

## T19 RED

### Integration

Command:

```powershell
$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres;Persist Security Info=true;Pooling=false'; dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --filter "FullyQualifiedName~HusayniaTabruk.IntegrationTests.Threads.ThreadIntegrationTests.ManagerCanListExistingThreadMessages" --nologo --logger "console;verbosity=minimal"
```

Result:

```text
Determining projects to restore...
  All projects are up-to-date for restore.
  HusayniaTabruk.Domain -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\src\HusayniaTabruk.Domain\bin\Debug\net10.0\HusayniaTabruk.Domain.dll
  HusayniaTabruk.Application -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\src\HusayniaTabruk.Application\bin\Debug\net10.0\HusayniaTabruk.Application.dll
  HusayniaTabruk.Infrastructure -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\src\HusayniaTabruk.Infrastructure\bin\Debug\net10.0\HusayniaTabruk.Infrastructure.dll
  HusayniaTabruk.Api -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\src\HusayniaTabruk.Api\bin\Debug\net10.0\HusayniaTabruk.Api.dll
  HusayniaTabruk.IntegrationTests -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.IntegrationTests\bin\Debug\net10.0\HusayniaTabruk.IntegrationTests.dll
Test run for C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.IntegrationTests\bin\Debug\net10.0\HusayniaTabruk.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
[xUnit.net 00:00:30.83]     HusayniaTabruk.IntegrationTests.Threads.ThreadIntegrationTests.ManagerCanListExistingThreadMessages [FAIL]
  Failed HusayniaTabruk.IntegrationTests.Threads.ThreadIntegrationTests.ManagerCanListExistingThreadMessages [15 s]
  Error Message:
   Expected OK, received NotFound: {"type":"https://httpstatuses.com/404","title":"Not found","status":404,"code":"not_found","detail":"Not found.","traceId":"3d7a2d8877aca48604d35df7bc35ec02"}
  Stack Trace:
     at HusayniaTabruk.IntegrationTests.Threads.ThreadIntegrationTestSupport.ReadRequiredAsync[T](HttpResponseMessage response, HttpStatusCode expectedStatus) in C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.IntegrationTests\Threads\ThreadIntegrationTestSupport.cs:line 80
   at HusayniaTabruk.IntegrationTests.Threads.ThreadIntegrationTests.ManagerCanListExistingThreadMessages() in C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.IntegrationTests\Threads\ThreadIntegrationTests.cs:line 34
   at HusayniaTabruk.IntegrationTests.Threads.ThreadIntegrationTests.ManagerCanListExistingThreadMessages() in C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.IntegrationTests\Threads\ThreadIntegrationTests.cs:line 45
   at HusayniaTabruk.IntegrationTests.Threads.ThreadIntegrationTests.ManagerCanListExistingThreadMessages() in C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.IntegrationTests\Threads\ThreadIntegrationTests.cs:line 45
--- End of stack trace from previous location ---

Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 15 s - HusayniaTabruk.IntegrationTests.dll (net10.0)
```

## Independent T19 test gate — 2026-08-19

### Environment

- PostgreSQL: isolated local PostgreSQL `18.6`, port `55432`, trust-authenticated,
  with a pre-provisioned `tabruk_app NOLOGIN` role.
- Source/test stability fingerprint before and after the final regression run:
  `418 E2F3F6BA3FF1372EA593EE0F87DBD24BB3F0D08B50DC7621331BB714F24DF989`.
- Git could not provide a line-level combined diff because both `HusayniaTabruk/`
  and this mission directory are untracked at the repository root.

### Executed results

| Command | Passed | Failed | Skipped | Result |
|---|---:|---:|---:|---|
| `dotnet test tests/HusayniaTabruk.Api.ContractTests/HusayniaTabruk.Api.ContractTests.csproj --no-build --filter "FullyQualifiedName~Threads"` | 8 | 0 | 0 | PASS |
| `dotnet test tests/HusayniaTabruk.Application.Tests/HusayniaTabruk.Application.Tests.csproj --no-build --filter "FullyQualifiedName~Threads"` | 6 | 0 | 0 | PASS |
| `dotnet test tests/HusayniaTabruk.Domain.Tests/HusayniaTabruk.Domain.Tests.csproj --no-build --filter "FullyQualifiedName~Threads"` | 43 | 0 | 0 | PASS |
| `dotnet test tests/HusayniaTabruk.IntegrationTests/HusayniaTabruk.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~Threads"` | 11 | 0 | 0 | PASS |
| `dotnet test tests/HusayniaTabruk.IntegrationTests/HusayniaTabruk.IntegrationTests.csproj --filter "FullyQualifiedName~CloseWithoutAnExistingThreadDoesNotCreateOrBackfillThread"` | 1 | 0 | 0 | PASS |
| `dotnet test tests/HusayniaTabruk.IntegrationTests/HusayniaTabruk.IntegrationTests.csproj --filter "FullyQualifiedName~StepUpIsPurposeBoundSingleUseAndExpiresExactlyAfterFiveMinutes"` | 1 | 0 | 0 | PASS |
| Full current .NET projects (`Application`, `API Contract`, `Domain`, `Integration`) | 1031 | 0 | 0 | PASS |
| `dotnet build HusayniaTabruk.sln -warnaserror` | — | — | — | PASS, 0 warnings / 0 errors |
| `dotnet format HusayniaTabruk.sln --verify-no-changes --no-restore` | — | 44 diagnostics | — | **FAIL** |
| `npm run lint --prefix apps/mobile` | — | — | — | PASS |
| `npm run typecheck --prefix apps/mobile` | — | — | — | PASS |
| `npm test --prefix apps/mobile -- --runInBand` | 143 | 0 | 0 | PASS, 25 suites |
| `npm --prefix apps/mobile run check:generated-client` | — | — | — | PASS |

### Blocking failure

`dotnet format --verify-no-changes` reports `ENDOFLINE` on every line (44
diagnostics) in:

`src/HusayniaTabruk.Application/Threads/ThreadAuthorizationPersistencePorts.cs`

The repository requires LF via `.editorconfig`; the file is CRLF.

### Coverage gaps

- No focused/API PostgreSQL proof for ordinary administrator, disabled actor,
  cancelled date, cross-organization actor, unrelated actor, or each
  pending/waitlisted/declined/cancelled signup transition. Domain tests cover
  most rules, but not the new transaction-held PostgreSQL authorization path.
- No focused proof of ordinary hidden-body redaction or privileged recovery of
  the stored hidden body.
- No focused endpoint/integration proof for report, hide, and lock success,
  stale CAS, rollback, or persisted moderation effects.
- No exact T19 boundary proof for default page 50, max page 100, max+1,
  malformed cursor, reason 500/max+1 and 2 KiB/max+1, or exactly 100 ASCII
  `caseId`. The case test uses 101 and then a short valid identifier.
- Runtime 413 proof exists only for message/report, not hide/lock/privileged
  administrative bodies. Runtime 429 proof exercises only the shortest account
  post/report limits; longer account and organization windows are metadata-only.
- Log/outbox body-leak proof covers ordinary post only, not report/hide/lock or
  successful privileged read.
- Frozen-plan drift is not documented: implementation added
  `ThreadAuthorizationPersistencePorts.cs` and
  `PostgresThreadAuthorizationRepository.cs`, outside the frozen inventory and
  approved single infrastructure expansion.

### Conclusion

**FAIL** — focused and full executable tests are green with zero skips on real
PostgreSQL 18.6, but the mandatory format gate fails and acceptance evidence
has the gaps listed above.

## Independent remediation coverage — awaiting notification

Added test-only coverage in:

- `tests/HusayniaTabruk.IntegrationTests/Threads/ThreadIntegrationTests.cs`
- `tests/HusayniaTabruk.IntegrationTests/Threads/ThreadIntegrationTestSupport.cs`
- `tests/HusayniaTabruk.Api.ContractTests/Threads/ThreadEndpointContractTests.cs`

Executed on isolated PostgreSQL 18.6:

| Coverage | Result |
|---|---|
| Revocation-first deterministic schedules: withdrawal, manager reassignment, Food Incharge role revocation, membership disable, date cancellation | 5 passed, 0 failed, 0 skipped |
| Existing mutation-first manager reassignment and withdrawal schedules | retained and previously passed |
| Ordinary administrator concealed from an existing thread | passed |
| Pending, waitlisted, declined, and cancelled signup concealment | 4 passed |
| Report success, exact duplicate replay, mismatch conflict, hide, ordinary redaction, privileged original-body read, lock, post-after-lock rejection, CAS/ETags, no outbox/log body | passed |
| Thread-specific wrong-purpose and exact-expiry step-up denial with no audit | passed |
| Default page 50, max page 100, max+1, malformed cursor, reason max/max+1, exact 100 ASCII case ID, rejected-input token reuse | passed |
| Runtime 413 for authenticated hide, lock, and privileged administrative reads | passed |
| Expanded focused API contract suite | 8 passed, 0 failed, 0 skipped |

Test-construction failures were corrected before evidence was accepted:

- Role-revocation setup initially violated
  `ck_role_assignments_revocation` because `revoked_by_membership_id` was absent.
- A 500-emoji JSON reason exceeded the 4 KiB request limit due serializer
  escaping; the exact 500-scalar semantic test now uses 500 ASCII characters.
- Pending/waitlisted fixtures were corrected to satisfy
  `ck_signups_state_metadata`.

The production workspace has not changed since the review-gate notification:

- `ThreadService.cs`: `2026-08-19T21:00:17Z`
- `PostgresThreadAuthorizationRepository.cs`: `2026-08-19T21:16:23Z`
- `ThreadAuthorizationPersistencePorts.cs`: `2026-08-19T20:59:50Z`

The mandatory format gate still fails with 44 `ENDOFLINE` diagnostics in
`ThreadAuthorizationPersistencePorts.cs`.

**Status remains FAIL / awaiting remediation notification.** No final full
regression rerun or certification was issued.

## Final independent rerun — remediation landed

Environment:

- Isolated PostgreSQL `18.6`, port `55432`
- `tabruk_app NOLOGIN` present
- Source/test fingerprint remained stable before and after execution:
  `418 B38A88216EAE0B6C87FE0FE6ECA9293781DF7E9ED7208D3AAC88455439653364`

Results:

| Command | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Focused API thread contracts | 8 | 0 | 0 |
| Focused Application thread tests | 6 | 0 | 0 |
| Focused Domain thread tests | 43 | 0 | 0 |
| Focused PostgreSQL thread tests | 25 | 0 | 0 |
| Explicit T17 no-backfill + exact step-up lifecycle | 2 | 0 | 0 |
| Full Application suite | 166 | 0 | 0 |
| Full API contract suite | 91 | 0 | 0 |
| Full Domain suite | 383 | 0 | 0 |
| Full PostgreSQL integration suite | 405 | 0 | 0 |
| Mobile Jest | 143 | 0 | 0 |

Additional gates:

- `dotnet restore`: PASS
- `dotnet build --no-restore -warnaserror`: PASS, 0 warnings / 0 errors
- `dotnet format --verify-no-changes --no-restore`: PASS
- mobile lint: PASS
- mobile typecheck: PASS
- generated-client drift: PASS

Focused T19 total: **82 passed, 0 failed, 0 skipped**.

Full independent regression total: **1,188 passed, 0 failed, 0 skipped**
(1,045 .NET + 143 mobile).

### Final conclusion

**PASS** — remediation and independent acceptance coverage execute successfully
on PostgreSQL 18.6 with zero focused skips. The prior concurrency and formatting
blockers are closed.
