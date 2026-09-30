# QA RESULT

Environment: Windows_NT; .NET SDK `10.0.400`; QA-only harness at
`C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-08-19-husaynia-t17\qa-harness\T17QaHarness.csproj`
compiled from the checked-in integration host/support sources; ASP.NET Core API hosted on real
loopback HTTP ports by `AuthApiHost`; temporary local PostgreSQL 18.6 cluster at
`127.0.0.1:55432` with `tabruk/tabruk` and `tabruk_app` NOLOGIN; each scenario used an isolated
migrated `tabruk_it_*` schema and real `/api/v1/auth/login` bearer-token auth.

Setup:

```powershell
dotnet restore 'C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-08-19-husaynia-t17\qa-harness\T17QaHarness.csproj' --verbosity minimal
dotnet build 'C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-08-19-husaynia-t17\qa-harness\T17QaHarness.csproj' --configuration Release --no-restore --verbosity minimal

$root = 'C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-08-19-husaynia-t17\.qa-pg18'
$zip = Join-Path $root 'postgresql-18.6-windows-x64-binaries.zip'
$extract = Join-Path $root 'pgsql'
$data = Join-Path $root 'data'
$log = Join-Path $root 'postgres.log'
New-Item -ItemType Directory -Path $root -Force | Out-Null
if (-not (Test-Path $zip)) { Invoke-WebRequest -Uri 'https://sbp.enterprisedb.com/getfile.jsp?fileid=1260435' -OutFile $zip }
if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
Expand-Archive -Path $zip -DestinationPath $extract
$bin = Join-Path $extract 'pgsql\bin'
if (Test-Path $data) { Remove-Item -Recurse -Force $data }
& (Join-Path $bin 'initdb.exe') -D $data -U tabruk --auth-local=trust --auth-host=trust --encoding=UTF8 --locale=C
& (Join-Path $bin 'pg_ctl.exe') -D $data -l $log -o '-p 55432' start -w
& (Join-Path $bin 'createdb.exe') -h 127.0.0.1 -p 55432 -U tabruk tabruk
& (Join-Path $bin 'psql.exe') -h 127.0.0.1 -p 55432 -U tabruk -d postgres -v ON_ERROR_STOP=1 -c 'DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = ''tabruk_app'') THEN CREATE ROLE tabruk_app NOLOGIN; END IF; END $$;'
& (Join-Path $bin 'psql.exe') --version
& (Join-Path $bin 'psql.exe') -h 127.0.0.1 -p 55432 -U tabruk -d tabruk -v ON_ERROR_STOP=1 -c "select current_setting('server_version') as server_version, current_database() as database_name, current_user as current_user;"
```

PostgreSQL probe result:

```text
psql (PostgreSQL) 18.6
 server_version | database_name | current_user
----------------+---------------+-------------
 18.6           | tabruk        | tabruk
```

Scenario 1: Authorized edit honors ETag and rejects stale/malformed If-Match [PASS]
  Steps:     `$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'; dotnet run --project 'C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-08-19-husaynia-t17\qa-harness\T17QaHarness.csproj' --configuration Release --no-build -- --scenario edit-ifmatch`
             login manager -> `PATCH /api/v1/dates/{dateId}` with `If-Match: "2"` -> repeat with stale `"2"` -> repeat with malformed `W/"3"`.
  Expected:  First edit returns `200` with `ETag: "3"` and persists the new date data; stale replay returns `412 stale_version`; malformed `If-Match` returns `400 invalid_date_request`; no later write changes persisted data.
  Actual:    `edit=200 etag="3" version=3 stale=412:stale_version malformed=400:invalid_date_request`
  Evidence:  `dateTitle=Edited title dbVersion=3 helpNeedInstructionsUnchanged=true`

Scenario 2: Need capacity reduction rejects over-allocation without writes [PASS]
  Steps:     `$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'; dotnet run --project 'C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-08-19-husaynia-t17\qa-harness\T17QaHarness.csproj' --configuration Release --no-build -- --scenario capacity-reduction`
             approve existing signup -> seed second approved signup -> `PATCH /api/v1/needs/{needId}` with `If-Match: "0"` and `capacity: 1`.
  Expected:  API returns `409 capacity_unavailable`; the help need row, signup-version CAS value, signups, and effect tables stay unchanged.
  Actual:    `patch=409:capacity_unavailable`
  Evidence:  `needCapacity=10 needVersion=0 signupVersion=3 approvedSignups=2 fullStateUnchanged=true`

Scenario 3: Close preserves approved signup history and locks the existing thread [PASS]
  Steps:     `$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'; dotnet run --project 'C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-08-19-husaynia-t17\qa-harness\T17QaHarness.csproj' --configuration Release --no-build -- --scenario close-preserves-thread`
             approve pending signup -> `POST /api/v1/dates/{dateId}/close` with `If-Match: "2"` and `Idempotency-Key`.
  Expected:  Close returns `200` with `ETag: "3"`; date/help need become `closed`; approved signup remains `approved`; existing thread becomes `locked`.
  Actual:    `close=200 etag="3" status=closed version=3`
  Evidence:  `dateStatus=closed needStatus=closed signupStatus=approved threadStatus=locked threadVersion=1`

Scenario 4: Close without an existing thread does not backfill and blocks new ordinary signup [PASS]
  Steps:     `$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'; dotnet run --project 'C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-08-19-husaynia-t17\qa-harness\T17QaHarness.csproj' --configuration Release --no-build -- --scenario close-no-thread-blocks-signup`
             delete date thread -> approve pending signup -> close the date -> login a different member -> `POST /api/v1/needs/{needId}/signups`.
  Expected:  Close succeeds without creating/backfilling a thread row; new ordinary signup is denied with `409 category_closed`; failed submit creates no signup or side effects.
  Actual:    `close=200 threadRows=0 signupSubmit=409:category_closed`
  Evidence:  `signupsPersisted=1 effectsUnchangedAfterFailedSubmit=true`

Scenario 5: Close then cancel preserves history until cancellation and emits distinct-contact effects [PASS]
  Steps:     `$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'; dotnet run --project 'C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-08-19-husaynia-t17\qa-harness\T17QaHarness.csproj' --configuration Release --no-build -- --scenario close-then-cancel`
             approve one signup -> add pending and waitlisted signups with different primary contacts -> close date -> cancel date.
  Expected:  Cancel after close returns `200`; help need stays `closed`; approved/pending/waitlisted signups become `cancelled`; one cancellation notification is recorded per affected distinct contact; one `thread.access_changed` outbox message is written.
  Actual:    `close=200 cancel=200 cancelledSignups=3`
  Evidence:  `dateStatus=cancelled needStatus=closed signupVersion=8 notificationsDelta=3 threadAccessChanged=1 waitlistCleared=true`

Scenario 6: Cancel same-key retry is idempotent and changed If-Match with same key is rejected [PASS]
  Steps:     `$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'; dotnet run --project 'C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-08-19-husaynia-t17\qa-harness\T17QaHarness.csproj' --configuration Release --no-build -- --scenario cancel-idempotency`
             approve signup -> `POST /api/v1/dates/{dateId}/cancel` with key `K` and `If-Match: "2"` -> retry same key/version -> retry same key with `If-Match: "3"`.
  Expected:  Same-key retry returns `200` with the current cancelled projection and does not duplicate effects; same key plus changed `If-Match` returns `409 idempotency_mismatch` and does not replay.
  Actual:    `first=200:cancelled/3 retry=200:cancelled/3 changedIfMatch=409:idempotency_mismatch`
  Evidence:  `stateAfterRetryEqualsFirst=true effectsUnchangedAfterMismatch=true`

Scenario 7: Food Incharge authority loss between authorization and write fails closed [PASS]
  Steps:     `$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'; dotnet run --project 'C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-08-19-husaynia-t17\qa-harness\T17QaHarness.csproj' --configuration Release --no-build -- --scenario authority-revocation-race`
             login manager -> revoke Food Incharge role during write-time revalidation -> `POST /api/v1/dates/{dateId}/close`.
  Expected:  Request returns `403 forbidden`; date/help need/signup/thread/effect tables remain unchanged.
  Actual:    `close=403:forbidden`
  Evidence:  `dateStatus=Open dateVersion=2 effectsUnchanged=true`

Scenario 8: Cancel rolls back atomically when notification persistence fails [PASS]
  Steps:     `$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=55432;Database=tabruk;Username=tabruk;Pooling=false'; dotnet run --project 'C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-08-19-husaynia-t17\qa-harness\T17QaHarness.csproj' --configuration Release --no-build -- --scenario cancel-rollback`
             approve signup -> close date -> inject PostgreSQL notification `INSERT` failure trigger -> `POST /api/v1/dates/{dateId}/cancel`.
  Expected:  API returns `503 dependency_unavailable`; already-closed date, approved signup, locked thread, and effect tables stay unchanged.
  Actual:    `cancel=503:dependency_unavailable`
  Evidence:  `dateStatus=Closed signupStatus=Approved threadStatus=Locked effectsUnchanged=true`

Post-run state check:

```powershell
& (Join-Path $bin 'psql.exe') -h 127.0.0.1 -p 55432 -U tabruk -d tabruk -v ON_ERROR_STOP=1 -c "SELECT count(*) AS residual_schemas FROM pg_namespace WHERE nspname LIKE 'tabruk_it_%';"
```

```text
 residual_schemas
------------------
                0
```

Scenarios: 8 run, 8 passed, 0 failed
Conclusion: PASS

STATUS:          PASS
SUMMARY:         Independent real-component QA of T17 passed: 8/8 API consumer/Food Incharge scenarios succeeded on live ASP.NET Core HTTP hosting backed by real PostgreSQL 18.6, covering ETag edits, stale/malformed If-Match, no-write capacity rejection, close/no-thread/signup denial, close-then-cancel cancellation effects, idempotent retry vs changed If-Match, authority revocation race, and rollback on notification failure.
WORK_COMPLETED:  Built and ran a QA-only harness outside the production repo using the checked-in integration host/support sources; provisioned a temporary local PostgreSQL 18.6 cluster; executed eight reproducible HTTP scenarios against real persistence; verified persisted state after every mutation/failure path; confirmed zero residual `tabruk_it_*` schemas; then stopped and removed the temporary PostgreSQL files.
EVIDENCE:        The QA RESULT above.
ARTIFACTS:       `.ai-org/missions/2026-08-19-husaynia-t17/qa-results.md`
FINDINGS:        No blocking T17 API or workflow defects were found in scope. Client-facing failure contracts remained actionable (`stale_version`, `invalid_date_request`, `capacity_unavailable`, `category_closed`, `idempotency_mismatch`, `dependency_unavailable`, `forbidden`).
RISKS:           Thread revocation was validated at the persisted `thread.access_changed` signal boundary; thread endpoint behavior itself remains intentionally out of scope under T19.
BLOCKERS:        None.
NEXT_ACTION:     Proceed to the engineering judge / release-readiness gate with this QA evidence.
