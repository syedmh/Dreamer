TEST RESULT

Command:
1.
```powershell
$ErrorActionPreference='Stop';
$pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin';
$root='C:\Users\syedhu\AppData\Local\Temp';
$clusterId='tabruk-pg18-' + [guid]::NewGuid().ToString('N');
$dataDir=Join-Path $root $clusterId;
$logPath=Join-Path $dataDir 'postgres.log';
$listener=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse('127.0.0.1'),0);
$listener.Start();
$port=($listener.LocalEndpoint).Port;
$listener.Stop();
& (Join-Path $pgBin 'initdb.exe') -D $dataDir -U postgres --auth=trust --auth-host=trust --auth-local=trust -E UTF8 --locale-provider=icu --icu-locale=en-US;
@(
"listen_addresses = '127.0.0.1'",
"port = $port"
) | Add-Content -Path (Join-Path $dataDir 'postgresql.conf');
@(
'local   all             all                                     trust',
'host    all             all             127.0.0.1/32            trust',
'host    all             all             ::1/128                 trust'
) | Set-Content -Path (Join-Path $dataDir 'pg_hba.conf');
& (Join-Path $pgBin 'pg_ctl.exe') -D $dataDir -l $logPath -o "-h 127.0.0.1 -p $port" start -w;
$serverPid = [int](Get-Content (Join-Path $dataDir 'postmaster.pid') | Select-Object -First 1);
Write-Host ("DATADIR={0}" -f $dataDir);
Write-Host ("LOG={0}" -f $logPath);
Write-Host ("PORT={0}" -f $port);
Write-Host ("PID={0}" -f $serverPid);
```

2.
```powershell
$ErrorActionPreference='Stop';
$pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin';
& (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p 62389 -U postgres -d postgres -v ON_ERROR_STOP=1 -c 'CREATE ROLE tabruk_app NOLOGIN;';
& (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p 62389 -U postgres -d postgres -At -v ON_ERROR_STOP=1 -c "SHOW server_version; SELECT 'role=' || rolname || ';canlogin=' || rolcanlogin::text FROM pg_roles WHERE rolname='tabruk_app';";
```

3.
```powershell
$ErrorActionPreference='Stop'; Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk'; dotnet restore HusayniaTabruk.sln
```

4.
```powershell
$ErrorActionPreference='Stop'; Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk'; dotnet build HusayniaTabruk.sln --no-restore -warnaserror
```

5.
```powershell
$ErrorActionPreference='Stop';
$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=62389;Database=postgres;Username=postgres;Pooling=false;Include Error Detail=true';
$env:TABRUK_MIGRATIONS_CONNECTION='Host=127.0.0.1;Port=62389;Database=postgres;Username=postgres;Pooling=false;Include Error Detail=true';
Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk';
dotnet test tests/HusayniaTabruk.IntegrationTests/HusayniaTabruk.IntegrationTests.csproj --no-restore --filter "Category=Persistence"
```

6.
```powershell
$ErrorActionPreference='Stop';
$env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=62389;Database=postgres;Username=postgres;Pooling=false;Include Error Detail=true';
$env:TABRUK_MIGRATIONS_CONNECTION='Host=127.0.0.1;Port=62389;Database=postgres;Username=postgres;Pooling=false;Include Error Detail=true';
Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk';
dotnet test HusayniaTabruk.sln --no-restore
```

7.
```powershell
$ErrorActionPreference='Stop'; Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk'; dotnet format HusayniaTabruk.sln --no-restore --verify-no-changes
```

8.
```powershell
$ErrorActionPreference='Stop';
$nodeDir='C:\Users\syedhu\AppData\Local\Temp\tabruk-node-v24.19.0\node-v24.19.0-win-x64';
$env:PATH="$nodeDir;$env:PATH";
Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\apps\mobile';
& (Join-Path $nodeDir 'npm.cmd') ci
```

9.
```powershell
$ErrorActionPreference='Stop';
$nodeDir='C:\Users\syedhu\AppData\Local\Temp\tabruk-node-v24.19.0\node-v24.19.0-win-x64';
$env:PATH="$nodeDir;$env:PATH";
Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\apps\mobile';
& (Join-Path $nodeDir 'npm.cmd') run lint
```

10.
```powershell
$ErrorActionPreference='Stop';
$nodeDir='C:\Users\syedhu\AppData\Local\Temp\tabruk-node-v24.19.0\node-v24.19.0-win-x64';
$env:PATH="$nodeDir;$env:PATH";
Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\apps\mobile';
& (Join-Path $nodeDir 'npm.cmd') run typecheck
```

11.
```powershell
$ErrorActionPreference='Stop';
$nodeDir='C:\Users\syedhu\AppData\Local\Temp\tabruk-node-v24.19.0\node-v24.19.0-win-x64';
$env:PATH="$nodeDir;$env:PATH";
Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\apps\mobile';
& (Join-Path $nodeDir 'npm.cmd') test -- --runInBand
```

12.
```powershell
$ErrorActionPreference='Stop';
$docker='C:\Users\syedhu\AppData\Local\Temp\tabruk-docker-cli-29.7.2\docker\docker.exe';
Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk';
& $docker compose config
```

13.
```powershell
$ErrorActionPreference='Stop';
$pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin';
& (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p 62389 -U postgres -d postgres -At -v ON_ERROR_STOP=1 -c "SELECT 'schemas=' || COUNT(*) FROM pg_namespace WHERE nspname LIKE 'tabruk_it_%';";
```

14.
```powershell
$ErrorActionPreference='Stop';
$process=Get-Process -Id 46776;
Write-Host ("PID={0};ProcessName={1};StartTime={2:O}" -f $process.Id,$process.ProcessName,$process.StartTime);
```

15.
```powershell
$ErrorActionPreference='Stop';
$dataDir='C:\Users\syedhu\AppData\Local\Temp\tabruk-pg18-73cd9943a1274fe4865feb8779919516';
if(Get-Process -Id 46776 -ErrorAction SilentlyContinue){
  Stop-Process -Id 46776 -Force;
  Write-Host 'Stopped PID 46776';
} else {
  Write-Host 'PID 46776 already stopped';
}
Start-Sleep -Seconds 3;
$remaining = Get-CimInstance Win32_Process -Filter "Name = 'postgres.exe'" | Select-Object ProcessId,ParentProcessId,CommandLine;
if($remaining){
  Write-Host 'REMAINING_POSTGRES_PROCESSES';
  $remaining | ForEach-Object { Write-Host ("PID={0};PPID={1};CMD={2}" -f $_.ProcessId,$_.ParentProcessId,$_.CommandLine) }
} else {
  Write-Host 'REMAINING_POSTGRES_PROCESSES=0';
}
Remove-Item -LiteralPath $dataDir -Recurse -Force;
Write-Host ("REMOVED={0}" -f $dataDir);
Write-Host ("DATADIR_EXISTS={0}" -f (Test-Path $dataDir));
```

16.
```powershell
$ErrorActionPreference='Stop';
$needle='tabruk-pg18-73cd9943a1274fe4865feb8779919516';
$matches = Get-CimInstance Win32_Process -Filter "Name = 'postgres.exe'" | Where-Object { $_.CommandLine -like "*$needle*" } | Select-Object ProcessId,CommandLine;
if($matches){
  Write-Host 'MATCHES_FOUND';
  $matches | ForEach-Object { Write-Host ("PID={0};CMD={1}" -f $_.ProcessId,$_.CommandLine) }
} else {
  Write-Host 'MATCHES_FOUND=0';
}
```

Result:
1.
```text
waiting for server to start.... done
server started
DATADIR=C:\Users\syedhu\AppData\Local\Temp\tabruk-pg18-73cd9943a1274fe4865feb8779919516
LOG=C:\Users\syedhu\AppData\Local\Temp\tabruk-pg18-73cd9943a1274fe4865feb8779919516\postgres.log
PORT=62389
PID=46776
```

2.
```text
CREATE ROLE
18.6
role=tabruk_app;canlogin=false
```

3.
```text
Determining projects to restore...
  All projects are up-to-date for restore.
```

4.
```text
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:01.56
```

5.
```text
Passed!  - Failed:     0, Passed:    52, Skipped:     0, Total:    52, Duration: 55 s - HusayniaTabruk.IntegrationTests.dll (net10.0)
```

6.
```text
Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 1 s - HusayniaTabruk.Api.ContractTests.dll (net10.0)
Passed!  - Failed:     0, Passed:    79, Skipped:     0, Total:    79, Duration: 7 s - HusayniaTabruk.Application.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   395, Skipped:     0, Total:   395, Duration: 11 s - HusayniaTabruk.Domain.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    52, Skipped:     0, Total:    52, Duration: 54 s - HusayniaTabruk.IntegrationTests.dll (net10.0)
```

7.
```text
[no console output; command exited 0]
```

8.
```text
added 1028 packages in 1m
npm warn allow-scripts 1 package has install scripts not yet covered by allowScripts:
npm warn allow-scripts   unrs-resolver@1.12.2 (postinstall: node postinstall.js)
```

9.
```text
> tabruk-mobile@0.1.0 lint
> eslint --config node_modules/eslint-config-expo/flat.js app --max-warnings=0
```

10.
```text
> tabruk-mobile@0.1.0 typecheck
> tsc --noEmit
```

11.
```text
PASS tests/root-layout.test.tsx (50.857 s)
  RootLayout
    √ exports the Tabruk navigation shell with its expected title (2 ms)

Test Suites: 1 passed, 1 total
Tests:       1 passed, 1 total
Snapshots:   0 total
Time:        58.256 s
Ran all test suites.
```

12.
```text
name: tabruk
services:
  postgres:
    image: postgres:18.6-alpine
```

13.
```text
schemas=0
```

14.
```text
PID=46776;ProcessName=postgres;StartTime=2026-08-15T05:42:56.4082304-07:00
```

15.
```text
Stopped PID 46776
REMAINING_POSTGRES_PROCESSES
PID=43752;PPID=41476;CMD="C:/Users/syedhu/AppData/Local/HusayniaTabruk/PostgreSQL18Binary/pgsql/bin/postgres.exe"  -D "C:/Users/syedhu/AppData/Local/Temp/tabruk-priv-repro-ad59883df0294136ba1f0b7965bda989/data" -p 62323 -c listen_addresses=127.0.0.1 
PID=47148;PPID=43752;CMD="C:/Users/syedhu/AppData/Local/HusayniaTabruk/PostgreSQL18Binary/pgsql/bin/postgres.exe" --forkchild="io_worker" 5972
PID=46564;PPID=43752;CMD="C:/Users/syedhu/AppData/Local/HusayniaTabruk/PostgreSQL18Binary/pgsql/bin/postgres.exe" --forkchild="io_worker" 5964
PID=36528;PPID=43752;CMD="C:/Users/syedhu/AppData/Local/HusayniaTabruk/PostgreSQL18Binary/pgsql/bin/postgres.exe" --forkchild="io_worker" 5932
PID=10296;PPID=43752;CMD="C:/Users/syedhu/AppData/Local/HusayniaTabruk/PostgreSQL18Binary/pgsql/bin/postgres.exe" --forkchild="checkpointer" 5916
PID=47432;PPID=43752;CMD="C:/Users/syedhu/AppData/Local/HusayniaTabruk/PostgreSQL18Binary/pgsql/bin/postgres.exe" --forkchild="bgwriter" 5920
PID=15192;PPID=43752;CMD="C:/Users/syedhu/AppData/Local/HusayniaTabruk/PostgreSQL18Binary/pgsql/bin/postgres.exe" --forkchild="wal_writer" 5884
PID=34460;PPID=43752;CMD="C:/Users/syedhu/AppData/Local/HusayniaTabruk/PostgreSQL18Binary/pgsql/bin/postgres.exe" --forkchild="autovacuum launcher" 5876
PID=8172;PPID=43752;CMD="C:/Users/syedhu/AppData/Local/HusayniaTabruk/PostgreSQL18Binary/pgsql/bin/postgres.exe" --forkchild="bgworker" 5892
REMOVED=C:\Users\syedhu\AppData\Local\Temp\tabruk-pg18-73cd9943a1274fe4865feb8779919516
DATADIR_EXISTS=False
```

16.
```text
MATCHES_FOUND=0
```

Passed:   617 executed tests (52 persistence category + 564 full .NET suite + 1 mobile Jest; the full suite reran the 52 persistence tests)
Failed:   0
Skipped:  0

Failures:
- DoD-9 gate evidence - expected independent test, security, code review, and final judgment gates to pass; actual state remains unchecked/pending in `definition-of-done.md:11` and `task-plan.md:9-11`. Root cause: the working tree contains no completed security/code-review/judge artifacts, and an attempt to launch additional review agents in this session returned `Maximum sub-agent depth of 4 reached`.

Coverage of acceptance criteria:
- DoD-1 Original exceptions survive rollback/disposal cleanup failures; cancellation regression is executed -> `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresTransactionCleanupTests.cs:16`, `:43`, and `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresIndependentGateTests.cs:83` executed in command 5 -> PASS
- DoD-2 Corrective Down uses a centralized safe privilege classification and never grants migration-history CRUD -> `src/HusayniaTabruk.Infrastructure/Migrations/PostgresLeastPrivilegeCatalog.cs:9-86`, `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs:90-105`, `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresLeastPrivilegeCatalogTests.cs:35`, `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:40`, and `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresIndependentGateTests.cs:194` executed in command 5 -> PASS
- DoD-3 Corrective migration uses transactional boundaries valid for concurrent indexes and staged validated constraints, including idempotent upgrades -> `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs:11-105`, `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:40`, `:112`, `:162`, and `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresIndependentGateTests.cs:23` executed in command 5 -> PASS
- DoD-4 Classification covers each EF-mapped table exactly, with executable tests -> `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresLeastPrivilegeCatalogTests.cs:12` executed in command 5 -> PASS
- DoD-5 Immutable initial migration is unchanged; manifest verifies both migrations, designers, and snapshot -> `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationArtifactManifestTests.cs:18` executed in command 5 against `src/HusayniaTabruk.Infrastructure/Migrations/T8MigrationArtifacts.sha256` -> PASS (current working-tree pinning verified)
- DoD-6 Safety documentation describes owner/runtime roles and up/down procedure -> inspected `src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md:11`, `:29`, `:52`, `:70`, `:98`, and `:119` -> PASS
- DoD-7 Real isolated PostgreSQL 18.6 fresh up/down/up and initial-to-corrective up/down/re-up pass -> disposable loopback/trust PostgreSQL `18.6` cluster on `127.0.0.1:62389` PID `46776`; `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresIndependentGateTests.cs:23`, `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:40`, and `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresIndependentGateTests.cs:350` executed in command 5 -> PASS
- DoD-8 Full .NET build/tests/format, mobile lint/typecheck/tests, and Compose config pass with zero skipped persistence tests -> commands 4-12 executed; persistence command 5 reported `Skipped: 0` -> PASS
- DoD-9 Independent test, security, code review, and final judgment gates pass -> independent test gate completed here, but `definition-of-done.md:11` remains unchecked and `task-plan.md:9-11` still show V2/V3/J1 as `PENDING` with no working-tree artifacts proving pass -> GAP

Conclusion: FAIL

## Rework 1 independent result

Scope: validated only the approved owner idempotent PostgreSQL corrective script, the disposal-failure cleanup regressions, the owner-script manifest/doc surface, the full persistence category, and full `.NET` build/test/format regressions.

> Note: the first attempt to append follow-up verification SQL to the owner-script and EF-idempotent repro PowerShell blocks had shell-quoting errors after the product behavior under test had already completed. I reran those verification reads separately below; those shell errors did not modify repository code or change database state.

### Commands and outputs

1. Fresh disposable PostgreSQL 18.6 loopback/trust UTF-8 cluster

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin'; $clusterId='tabruk-r1-v1-' + [guid]::NewGuid().ToString('N'); $dataDir=Join-Path 'C:\Users\syedhu\AppData\Local\Temp' $clusterId; $logPath=Join-Path $dataDir 'postgres.log'; $listener=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse('127.0.0.1'),0); $listener.Start(); $port=($listener.LocalEndpoint).Port; $listener.Stop(); & (Join-Path $pgBin 'initdb.exe') -D $dataDir -U postgres --auth=trust --auth-host=trust --auth-local=trust -E UTF8 --locale-provider=icu --icu-locale=en-US; @("listen_addresses = '127.0.0.1'","port = $port") | Add-Content -Path (Join-Path $dataDir 'postgresql.conf'); @('local   all             all                                     trust','host    all             all             127.0.0.1/32            trust','host    all             all             ::1/128                 trust') | Set-Content -Path (Join-Path $dataDir 'pg_hba.conf'); & (Join-Path $pgBin 'pg_ctl.exe') -D $dataDir -l $logPath -o "-h 127.0.0.1 -p $port" start -w; $serverPid=[int](Get-Content (Join-Path $dataDir 'postmaster.pid') | Select-Object -First 1); Write-Host ("DATADIR={0}" -f $dataDir); Write-Host ("LOG={0}" -f $logPath); Write-Host ("PORT={0}" -f $port); Write-Host ("PID={0}" -f $serverPid);
```

```text
The files belonging to this database system will be owned by user "syedhu".
This user must also own the server process.

Using language tag "en-US" for ICU locale "en-US".
The database cluster will be initialized with this locale configuration:
  locale provider:   icu
  default collation: en-US
  LC_COLLATE:  English_United States.1252
  LC_CTYPE:    English_United States.1252
  LC_MESSAGES: English_United States.1252
  LC_MONETARY: English_United States.1252
  LC_NUMERIC:  English_United States.1252
  LC_TIME:     English_United States.1252
The default text search configuration will be set to "english".

Data page checksums are enabled.

creating directory C:/Users/syedhu/AppData/Local/Temp/tabruk-r1-v1-2c64962c9e3548ce909b481746cb45c0 ... ok
creating subdirectories ... ok
selecting dynamic shared memory implementation ... windows
selecting default "max_connections" ... 100
selecting default "shared_buffers" ... 128MB
selecting default time zone ... America/Los_Angeles
creating configuration files ... ok
running bootstrap script ... ok
performing post-bootstrap initialization ... ok
syncing data to disk ... ok

Success. You can now start the database server using:

    ^"C^:^\Users^\syedhu^\AppData^\Local^\HusayniaTabruk^\PostgreSQL18Binary^\pgsql^\bin^\pg^_ctl^" -D ^"C^:^\Users^\syedhu^\AppData^\Local^\Temp^\tabruk^-r1^-v1^-2c64962c9e3548ce909b481746cb45c0^" -l logfile start

waiting for server to start.... done
server started
DATADIR=C:\Users\syedhu\AppData\Local\Temp\tabruk-r1-v1-2c64962c9e3548ce909b481746cb45c0
LOG=C:\Users\syedhu\AppData\Local\Temp\tabruk-r1-v1-2c64962c9e3548ce909b481746cb45c0\postgres.log
PORT=53123
PID=19516
```

2. Pre-provision the required `tabruk_app NOLOGIN` role and confirm PostgreSQL 18.6 / UTF-8

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin'; & (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p 53123 -U postgres -d postgres -v ON_ERROR_STOP=1 -c 'CREATE ROLE tabruk_app NOLOGIN;'; & (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p 53123 -U postgres -d postgres -At -v ON_ERROR_STOP=1 -c "SHOW server_version; SHOW server_encoding; SELECT 'role=' || rolname || ';canlogin=' || rolcanlogin::text FROM pg_roles WHERE rolname='tabruk_app';";
```

```text
CREATE ROLE
18.6
UTF8
role=tabruk_app;canlogin=false
```

3. Full .NET build regression

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk'; dotnet build HusayniaTabruk.sln -warnaserror
```

```text
Determining projects to restore...
  All projects are up-to-date for restore.
  HusayniaTabruk.Domain -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\src\HusayniaTabruk.Domain\bin\Debug\net10.0\HusayniaTabruk.Domain.dll
  HusayniaTabruk.Application -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\src\HusayniaTabruk.Application\bin\Debug\net10.0\HusayniaTabruk.Application.dll
  HusayniaTabruk.Domain.Tests -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.Domain.Tests\bin\Debug\net10.0\HusayniaTabruk.Domain.Tests.dll
  HusayniaTabruk.Infrastructure -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\src\HusayniaTabruk.Infrastructure\bin\Debug\net10.0\HusayniaTabruk.Infrastructure.dll
  HusayniaTabruk.Application.Tests -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.Application.Tests\bin\Debug\net10.0\HusayniaTabruk.Application.Tests.dll
  HusayniaTabruk.Api -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\src\HusayniaTabruk.Api\bin\Debug\net10.0\HusayniaTabruk.Api.dll
  HusayniaTabruk.Api.ContractTests -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.Api.ContractTests\bin\Debug\net10.0\HusayniaTabruk.Api.ContractTests.dll
  HusayniaTabruk.IntegrationTests -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.IntegrationTests\bin\Debug\net10.0\HusayniaTabruk.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:01.96
```

4. Apply only the initial migration on a fresh database intended for the approved owner-script path

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $port=53123; $db='tabruk_rework_ok_2c64962c'; $pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin'; & (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p $port -U postgres -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE $db;"; $env:TABRUK_MIGRATIONS_CONNECTION="Host=127.0.0.1;Port=$port;Database=$db;Username=postgres;Pooling=false;Include Error Detail=true"; Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk'; dotnet ef --version; dotnet ef database update 20260815075156_InitialPostgresSchema --project src/HusayniaTabruk.Infrastructure/HusayniaTabruk.Infrastructure.csproj --startup-project src/HusayniaTabruk.Infrastructure/HusayniaTabruk.Infrastructure.csproj --no-build
```

```text
CREATE DATABASE
Entity Framework Core .NET Command-line Tools
10.0.11
Failed executing DbCommand (15ms) [Parameters=[], CommandType='Text', CommandTimeout='30']
SELECT "MigrationId", "ProductVersion"
FROM "__EFMigrationsHistory"
ORDER BY "MigrationId";
Acquiring an exclusive lock for migration application. See https://aka.ms/efcore-docs-migrations-lock for more information if this takes too long.
Applying migration '20260815075156_InitialPostgresSchema'.
Done.
```

5. Run the approved owner idempotent script twice under psql autocommit (`-X -v ON_ERROR_STOP=1 -f`, no `-1`)

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $port=53123; $db='tabruk_rework_ok_2c64962c'; $pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin'; $script='C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\src\HusayniaTabruk.Infrastructure\Migrations\20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql'; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1 port=$port dbname=$db user=postgres" -X -v ON_ERROR_STOP=1 -f $script; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1 port=$port dbname=$db user=postgres" -X -v ON_ERROR_STOP=1 -f $script; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1 port=$port dbname=$db user=postgres" -X -At -v ON_ERROR_STOP=1 -c "SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"='20260815102612_T8CorrectivePostgresHardening'; SELECT to_regclass('public.\"ux_signups_waitlisted_order_per_help_need\"') IS NOT NULL; SELECT indisvalid FROM pg_index WHERE indexrelid='\"ux_signups_waitlisted_order_per_help_need\"'::regclass; SELECT convalidated FROM pg_constraint WHERE conrelid='signups'::regclass AND conname='ck_signups_transition_chronology'; SELECT has_table_privilege('tabruk_app','public.notifications','SELECT')::text || '|' || has_table_privilege('tabruk_app','public.notifications','INSERT')::text || '|' || has_table_privilege('tabruk_app','public.notifications','UPDATE')::text || '|' || has_table_privilege('tabruk_app','public.notifications','DELETE')::text; SELECT has_table_privilege('tabruk_app','public.audit_events','INSERT')::text || '|' || has_table_privilege('tabruk_app','public.audit_events','SELECT')::text || '|' || has_table_privilege('tabruk_app','public.audit_events','UPDATE')::text || '|' || has_table_privilege('tabruk_app','public.audit_events','DELETE')::text; SELECT has_table_privilege('tabruk_app','public.\"__EFMigrationsHistory\"','SELECT')::text || '|' || has_table_privilege('tabruk_app','public.\"__EFMigrationsHistory\"','INSERT')::text || '|' || has_table_privilege('tabruk_app','public.\"__EFMigrationsHistory\"','UPDATE')::text || '|' || has_table_privilege('tabruk_app','public.\"__EFMigrationsHistory\"','DELETE')::text; SELECT has_sequence_privilege('tabruk_app','public.\"identity_user_claims_Id_seq\"','USAGE')::text || '|' || has_sequence_privilege('tabruk_app','public.\"identity_user_claims_Id_seq\"','SELECT')::text || '|' || has_sequence_privilege('tabruk_app','public.\"identity_user_claims_Id_seq\"','UPDATE')::text;";
```

```text
REVOKE
REVOKE
REVOKE
GRANT
GRANT
GRANT
CREATE INDEX
ALTER TABLE
ALTER TABLE
INSERT 0 1
REVOKE
REVOKE
REVOKE
GRANT
GRANT
GRANT
INSERT 0 0
ERROR:  syntax error at or near "\"
LINE 1: SELECT COUNT(*) FROM \
                             ^
NativeCommandExitException: 
Line |
   2 |  … -f $script; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1  …
     |                ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
     | Program "psql.exe" ended with non-zero exit code: 1 (0x00000001).
```

6. Rerun the verification query separately after the shell-quoting mistake above

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin'; $port=53123; $db='tabruk_rework_ok_2c64962c'; $sql=@'
SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId"='20260815102612_T8CorrectivePostgresHardening';
SELECT to_regclass('public."ux_signups_waitlisted_order_per_help_need"') IS NOT NULL;
SELECT indisvalid FROM pg_index WHERE indexrelid='"ux_signups_waitlisted_order_per_help_need"'::regclass;
SELECT convalidated FROM pg_constraint WHERE conrelid='signups'::regclass AND conname='ck_signups_transition_chronology';
SELECT has_table_privilege('tabruk_app','public.notifications','SELECT')::text || '|' || has_table_privilege('tabruk_app','public.notifications','INSERT')::text || '|' || has_table_privilege('tabruk_app','public.notifications','UPDATE')::text || '|' || has_table_privilege('tabruk_app','public.notifications','DELETE')::text;
SELECT has_table_privilege('tabruk_app','public.audit_events','INSERT')::text || '|' || has_table_privilege('tabruk_app','public.audit_events','SELECT')::text || '|' || has_table_privilege('tabruk_app','public.audit_events','UPDATE')::text || '|' || has_table_privilege('tabruk_app','public.audit_events','DELETE')::text;
SELECT has_table_privilege('tabruk_app','public."__EFMigrationsHistory"','SELECT')::text || '|' || has_table_privilege('tabruk_app','public."__EFMigrationsHistory"','INSERT')::text || '|' || has_table_privilege('tabruk_app','public."__EFMigrationsHistory"','UPDATE')::text || '|' || has_table_privilege('tabruk_app','public."__EFMigrationsHistory"','DELETE')::text;
SELECT has_sequence_privilege('tabruk_app','public."identity_user_claims_Id_seq"','USAGE')::text || '|' || has_sequence_privilege('tabruk_app','public."identity_user_claims_Id_seq"','SELECT')::text || '|' || has_sequence_privilege('tabruk_app','public."identity_user_claims_Id_seq"','UPDATE')::text;
'@; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1 port=$port dbname=$db user=postgres" -X -At -v ON_ERROR_STOP=1 -c $sql;
```

```text
1
t
t
t
true|true|true|true
true|false|false|false
false|false|false|false
true|true|false
```

7. Reproduce that the standard EF-generated idempotent script is rejected on PostgreSQL 18.6 instead of being silently used

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $port=53123; $db='tabruk_ef_bad_2c64962c'; $pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin'; & (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p $port -U postgres -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE $db;"; $env:TABRUK_MIGRATIONS_CONNECTION="Host=127.0.0.1;Port=$port;Database=$db;Username=postgres;Pooling=false;Include Error Detail=true"; Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk'; dotnet ef database update 20260815075156_InitialPostgresSchema --project src/HusayniaTabruk.Infrastructure/HusayniaTabruk.Infrastructure.csproj --startup-project src/HusayniaTabruk.Infrastructure/HusayniaTabruk.Infrastructure.csproj --no-build; $script = dotnet ef migrations script 20260815075156_InitialPostgresSchema 20260815102612_T8CorrectivePostgresHardening --idempotent --project src/HusayniaTabruk.Infrastructure/HusayniaTabruk.Infrastructure.csproj --startup-project src/HusayniaTabruk.Infrastructure/HusayniaTabruk.Infrastructure.csproj --no-build; Write-Host ('DO_EF_COUNT=' + (($script | Select-String 'DO \$EF\$').Count)); Write-Host ('CONCURRENT_INDEX_COUNT=' + (($script | Select-String 'CREATE UNIQUE INDEX CONCURRENTLY').Count)); $PSNativeCommandUseErrorActionPreference=$false; $script | & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1 port=$port dbname=$db user=postgres" -X -v ON_ERROR_STOP=1; $pipeExit=$LASTEXITCODE; $PSNativeCommandUseErrorActionPreference=$true; Write-Host ('PIPE_EXIT=' + $pipeExit); if($pipeExit -eq 0){ throw 'Expected EF generated idempotent script to fail on PostgreSQL, but it succeeded.' }; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1 port=$port dbname=$db user=postgres" -X -At -v ON_ERROR_STOP=1 -c "SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"='20260815102612_T8CorrectivePostgresHardening';";
```

```text
CREATE DATABASE
Failed executing DbCommand (17ms) [Parameters=[], CommandType='Text', CommandTimeout='30']
SELECT "MigrationId", "ProductVersion"
FROM "__EFMigrationsHistory"
ORDER BY "MigrationId";
Acquiring an exclusive lock for migration application. See https://aka.ms/efcore-docs-migrations-lock for more information if this takes too long.
Applying migration '20260815075156_InitialPostgresSchema'.
Done.
DO_EF_COUNT=7
CONCURRENT_INDEX_COUNT=1
DO
START TRANSACTION
DO
DO
COMMIT
ERROR:  CREATE INDEX CONCURRENTLY cannot be executed from a function
CONTEXT:  SQL statement "CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS "ux_signups_waitlisted_order_per_help_need"
        ON "signups" ("organization_id", "help_need_id", "waitlist_order")
        WHERE "status" = 2"
PL/pgSQL function inline_code_block line 4 at SQL statement
PIPE_EXIT=3
ERROR:  syntax error at or near "\"
LINE 1: SELECT COUNT(*) FROM \
                             ^
NativeCommandExitException: 
Line |
   2 |  … ceeded.' }; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1  …
     |                ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
     | Program "psql.exe" ended with non-zero exit code: 1 (0x00000001).
```

8. Rerun the corrective-history verification separately after the shell-quoting mistake above

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin'; $port=53123; $db='tabruk_ef_bad_2c64962c'; $sql=@'
SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId"='20260815102612_T8CorrectivePostgresHardening';
'@; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1 port=$port dbname=$db user=postgres" -X -At -v ON_ERROR_STOP=1 -c $sql;
```

```text
0
```

9. Targeted manifest + owner-script + cleanup regressions

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=53123;Database=postgres;Username=postgres;Pooling=false;Include Error Detail=true'; $env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin\psql.exe'; Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk'; dotnet test tests/HusayniaTabruk.IntegrationTests/HusayniaTabruk.IntegrationTests.csproj --no-build --filter 'FullyQualifiedName~PostgresMigrationArtifactManifestTests|FullyQualifiedName~PostgresLeastPrivilegeCatalogTests|FullyQualifiedName~ApprovedOwnerIdempotentScript|FullyQualifiedName~PostgresTransactionCleanupTests'
```

```text
Test run for C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.IntegrationTests\bin\Debug\net10.0\HusayniaTabruk.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 5 s - HusayniaTabruk.IntegrationTests.dll (net10.0)
```

10. Whole PostgreSQL persistence category

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=53123;Database=postgres;Username=postgres;Pooling=false;Include Error Detail=true'; $env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin\psql.exe'; Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk'; dotnet test tests/HusayniaTabruk.IntegrationTests/HusayniaTabruk.IntegrationTests.csproj --no-build --filter 'Category=Persistence'
```

```text
Test run for C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.IntegrationTests\bin\Debug\net10.0\HusayniaTabruk.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    57, Skipped:     0, Total:    57, Duration: 54 s - HusayniaTabruk.IntegrationTests.dll (net10.0)
```

11. Full .NET suite regression

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=53123;Database=postgres;Username=postgres;Pooling=false;Include Error Detail=true'; $env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin\psql.exe'; Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk'; dotnet test HusayniaTabruk.sln --no-build
```

```text
Test run for C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.Domain.Tests\bin\Debug\net10.0\HusayniaTabruk.Domain.Tests.dll (.NETCoreApp,Version=v10.0)
Test run for C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.Api.ContractTests\bin\Debug\net10.0\HusayniaTabruk.Api.ContractTests.dll (.NETCoreApp,Version=v10.0)
Test run for C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.Application.Tests\bin\Debug\net10.0\HusayniaTabruk.Application.Tests.dll (.NETCoreApp,Version=v10.0)
Test run for C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.IntegrationTests\bin\Debug\net10.0\HusayniaTabruk.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.A total of 1 test files matched the specified pattern.

A total of 1 test files matched the specified pattern.A total of 1 test files matched the specified pattern.


Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 1 s - HusayniaTabruk.Api.ContractTests.dll (net10.0)

Passed!  - Failed:     0, Passed:    79, Skipped:     0, Total:    79, Duration: 5 s - HusayniaTabruk.Application.Tests.dll (net10.0)

Passed!  - Failed:     0, Passed:   395, Skipped:     0, Total:   395, Duration: 9 s - HusayniaTabruk.Domain.Tests.dll (net10.0)

Passed!  - Failed:     0, Passed:    57, Skipped:     0, Total:    57, Duration: 56 s - HusayniaTabruk.IntegrationTests.dll (net10.0)
```

12. Formatting regression

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk'; dotnet format HusayniaTabruk.sln --no-restore --verify-no-changes
```

```text
[no console output; command exited 0]
```

13. Confirm persistence-test schema cleanup before shutting down the cluster

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin'; & (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p 53123 -U postgres -d postgres -At -v ON_ERROR_STOP=1 -c "SELECT 'schemas=' || COUNT(*) FROM pg_namespace WHERE nspname LIKE 'tabruk_it_%';";
```

```text
schemas=0
```

14. Stop the exact PostgreSQL server PID and remove the exact disposable data directory

```powershell
$ErrorActionPreference='Stop'; $dataDir='C:\Users\syedhu\AppData\Local\Temp\tabruk-r1-v1-2c64962c9e3548ce909b481746cb45c0'; $needle='tabruk-r1-v1-2c64962c9e3548ce909b481746cb45c0'; if(Get-Process -Id 19516 -ErrorAction SilentlyContinue){ Stop-Process -Id 19516 -Force; Write-Host 'Stopped PID 19516'; } else { Write-Host 'PID 19516 already stopped'; }; Start-Sleep -Seconds 3; $matches = Get-CimInstance Win32_Process -Filter "Name = 'postgres.exe'" | Where-Object { $_.CommandLine -like "*$needle*" } | Select-Object ProcessId,ParentProcessId,CommandLine; if($matches){ foreach($match in $matches){ Stop-Process -Id $match.ProcessId -Force; Write-Host ("Stopped child PID {0}" -f $match.ProcessId); }; Start-Sleep -Seconds 2; }; $remaining = Get-CimInstance Win32_Process -Filter "Name = 'postgres.exe'" | Where-Object { $_.CommandLine -like "*$needle*" } | Select-Object ProcessId,ParentProcessId,CommandLine; if($remaining){ Write-Host 'REMAINING_MATCHES'; $remaining | ForEach-Object { Write-Host ("PID={0};PPID={1};CMD={2}" -f $_.ProcessId,$_.ParentProcessId,$_.CommandLine) }; } else { Write-Host 'REMAINING_MATCHES=0'; }; Remove-Item -LiteralPath $dataDir -Recurse -Force; Write-Host ("REMOVED={0}" -f $dataDir); Write-Host ("DATADIR_EXISTS={0}" -f (Test-Path $dataDir));
```

```text
Stopped PID 19516
REMAINING_MATCHES=0
REMOVED=C:\Users\syedhu\AppData\Local\Temp\tabruk-r1-v1-2c64962c9e3548ce909b481746cb45c0
DATADIR_EXISTS=False
```

### Counts

- Targeted rework tests: `Passed: 10, Failed: 0, Skipped: 0`
- Full persistence category: `Passed: 57, Failed: 0, Skipped: 0`
- Full .NET suite: `Passed: 569, Failed: 0, Skipped: 0`
- Aggregate executed across the three `dotnet test` commands above (reruns included): `Passed: 636, Failed: 0, Skipped: 0`

### Focused DoD map

- Approved owner idempotent upgrade path is the documented safe path, and EF generated idempotent is documented unsafe -> manual psql happy-path commands 4-6 succeeded; EF repro commands 7-8 failed with `ERROR:  CREATE INDEX CONCURRENTLY cannot be executed from a function`; docs inspected at `src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md:46`, `:57`, `:64`, `:68`; approved script header at `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql:4` -> PASS
- Owner script is idempotent on an initial-only schema under autocommit and leaves exactly one corrective history row, a present/valid waitlist index, a validated chronology constraint, hardened runtime CRUD, insert-only audit permissions, denied history CRUD, and sequence USAGE+SELECT without UPDATE -> manual owner-script commands 5-6 on `tabruk_rework_ok_2c64962c` returned `1`, `t`, `t`, `t`, `true|true|true|true`, `true|false|false|false`, `false|false|false|false`, `true|true|false` -> PASS
- Chronology dirty-data failure leaves corrective history absent and retry succeeds -> `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:267` (`ApprovedOwnerIdempotentScriptRetriesAfterChronologyRepairWithoutWritingHistoryEarly`) executed in command 9 and again in command 10 -> PASS
- Disposal failures preserve the original cancellation/domain exception while recording cleanup failure data -> `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresTransactionCleanupTests.cs:26` and `:53` executed in command 9; rollback companions at `:80` and `:107` also passed -> PASS
- Migration artifact manifest still pins the six approved artifacts, including the new owner script -> `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationArtifactManifestTests.cs:19` executed in command 9 against `src/HusayniaTabruk.Infrastructure/Migrations/T8MigrationArtifacts.sha256` -> PASS
- Fixed least-privilege script surface remains free of EF's `DO $EF$` guard and uses only exact identifiers -> `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresLeastPrivilegeCatalogTests.cs:59` and `:38` executed in command 9 -> PASS
- Whole persistence category and full .NET build/tests/format remain green after rework -> commands 3 and 9-12 passed; persistence command 10 reported `Skipped: 0` -> PASS
- Disposable environment was isolated and cleaned -> commands 1-2, 13, and 14 proved PostgreSQL `18.6`, `UTF8`, `tabruk_app` `canlogin=false`, `schemas=0`, exact PID `19516` stopped, and exact data directory removed -> PASS

Conclusion: PASS

## Final independent operability validation — 2026-08-15

TEST RESULT

Command:

```powershell
# Fresh isolated PostgreSQL 18.6, loopback-only, trust-auth, UTF-8
initdb.exe -D <fresh-guid-data-dir> -U postgres --auth=trust --auth-host=trust --auth-local=trust -E UTF8 --locale-provider=icu --icu-locale=en-US
pg_ctl.exe -D <fresh-guid-data-dir> -l <postgres.log> -o "-h 127.0.0.1 -p 57628" start -w
psql.exe -h 127.0.0.1 -p 57628 -U postgres -d postgres -c "CREATE ROLE tabruk_app NOLOGIN;"

dotnet restore .\HusayniaTabruk.sln
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror

dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter 'FullyQualifiedName~PostgresMigrationArtifactManifestTests|FullyQualifiedName~PostgresLeastPrivilegeCatalogTests|FullyQualifiedName~ApprovedOwnerIdempotentScript|FullyQualifiedName~PostgresTransactionCleanupTests'
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-build --filter 'Category=Persistence'
dotnet test .\HusayniaTabruk.sln --no-build
dotnet format .\HusayniaTabruk.sln --no-restore --verify-no-changes --verbosity minimal

npm ci
npm run lint
npm run typecheck
npm test -- --runInBand
docker compose config

psql.exe -h 127.0.0.1 -p 57628 -U postgres -d postgres -c "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name LIKE 'tabruk_it_%';"
pg_ctl.exe -D <fresh-guid-data-dir> stop -m fast -w
Remove-Item <fresh-guid-data-dir> -Recurse -Force
```

Result:

```text
PostgreSQL: 18.6, UTF8, tabruk_app|false
Build succeeded. 0 Warning(s), 0 Error(s)
Focused: Passed 12, Failed 0, Skipped 0
Persistence: Passed 59, Failed 0, Skipped 0
Full .NET: Passed 571, Failed 0, Skipped 0
Format: exit 0
Mobile lint: exit 0
Mobile typecheck: exit 0
Mobile Jest: 1 suite passed, 1 test passed
Compose config: postgres:18.6-alpine
Cleanup: test_schemas=0; non_template_databases=postgres;
         matching processes=0; data directory absent; PID absent
```

Passed: 642 .NET executions including reruns, plus 1 mobile test (643 total)
Failed: 0
Skipped: 0

Failures:
- None.

Coverage of acceptance criteria:
- Fresh up/down/up -> `InitialMigrationCanApplyDownAndApplyAgainOnTheSameFreshSchema` executed in the 59-test persistence run: PASS.
- Initial-to-corrective upgrade/down/re-up, index/constraint catalog validity, preserved data, and runtime/history privileges before and after Down -> `CorrectiveMigrationUpgradeDownAndReapplyPreservesInitialDataAndPrivilegePosture`: PASS.
- Classification exhaustive and exclusive -> `CatalogClassifiesEveryEfMappedTableExactlyOnce`: PASS.
- Migration hash manifest -> `ManifestPinsExactlyTheExpectedMigrationArtifacts`: PASS.
- Cancellation plus rollback/disposal cleanup failure preserves `OperationCanceledException` -> four `PostgresTransactionCleanupTests`: PASS.
- Full build/test/format/mobile/Compose gates with zero skips: PASS.
- Exact isolated PostgreSQL process and data cleanup: PASS.

Conclusion: PASS. T8 is ready for T9.

## Rework 2 independent result

Scope: validated only the Rework 2 owner-script guard behavior, explicit schema-selection contract, focused owner/manifest/catalog/cleanup regressions, the full PostgreSQL persistence category, full `.NET` build/test/format gates, and disposable PostgreSQL cleanup.

> Note: one discarded database-setup attempt misquoted `CREATE SCHEMA` and failed before schema creation. I dropped that disposable database and reran the setup cleanly below; no repository files or accepted database evidence came from the discarded attempt.

### Commands and outputs

1. Fresh disposable PostgreSQL 18.6 loopback/trust UTF-8 cluster

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin'; $clusterId='tabruk-r2-v1-' + [guid]::NewGuid().ToString('N'); $dataDir=Join-Path 'C:\Users\syedhu\AppData\Local\Temp' $clusterId; $logPath=Join-Path $dataDir 'postgres.log'; $listener=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse('127.0.0.1'),0); $listener.Start(); $port=($listener.LocalEndpoint).Port; $listener.Stop(); & (Join-Path $pgBin 'initdb.exe') -D $dataDir -U postgres --auth=trust --auth-host=trust --auth-local=trust -E UTF8 --locale-provider=icu --icu-locale=en-US; @("listen_addresses = '127.0.0.1'","port = $port") | Add-Content -Path (Join-Path $dataDir 'postgresql.conf'); @('local   all             all                                     trust','host    all             all             127.0.0.1/32            trust','host    all             all             ::1/128                 trust') | Set-Content -Path (Join-Path $dataDir 'pg_hba.conf'); & (Join-Path $pgBin 'pg_ctl.exe') -D $dataDir -l $logPath -o "-h 127.0.0.1 -p $port" start -w; $serverPid=[int](Get-Content (Join-Path $dataDir 'postmaster.pid') | Select-Object -First 1); Write-Host ("DATADIR={0}" -f $dataDir); Write-Host ("LOG={0}" -f $logPath); Write-Host ("PORT={0}" -f $port); Write-Host ("PID={0}" -f $serverPid);
```

```text
The files belonging to this database system will be owned by user "syedhu".
This user must also own the server process.

Using language tag "en-US" for ICU locale "en-US".
The database cluster will be initialized with this locale configuration:
  locale provider:   icu
  default collation: en-US
  LC_COLLATE:  English_United States.1252
  LC_CTYPE:    English_United States.1252
  LC_MESSAGES: English_United States.1252
  LC_MONETARY: English_United States.1252
  LC_NUMERIC:  English_United States.1252
  LC_TIME:     English_United States.1252
The default text search configuration will be set to "english".

Data page checksums are enabled.

creating directory C:/Users/syedhu/AppData/Local/Temp/tabruk-r2-v1-ce0018a9715a49dea55004ad2d57bd8b ... ok
creating subdirectories ... ok
selecting dynamic shared memory implementation ... windows
selecting default "max_connections" ... 100
selecting default "shared_buffers" ... 128MB
selecting default time zone ... America/Los_Angeles
creating configuration files ... ok
running bootstrap script ... ok
performing post-bootstrap initialization ... ok
syncing data to disk ... ok

Success. You can now start the database server using:

    ^"C^:^\Users^\syedhu^\AppData^\Local^\HusayniaTabruk^\PostgreSQL18Binary^\pgsql^\bin^\pg^_ctl^" -D ^"C^:^\Users^\syedhu^\AppData^\Local^\Temp^\tabruk^-r2^-v1^-ce0018a9715a49dea55004ad2d57bd8b^" -l logfile start

waiting for server to start.... done
server started
DATADIR=C:\Users\syedhu\AppData\Local\Temp\tabruk-r2-v1-ce0018a9715a49dea55004ad2d57bd8b
LOG=C:\Users\syedhu\AppData\Local\Temp\tabruk-r2-v1-ce0018a9715a49dea55004ad2d57bd8b\postgres.log
PORT=50626
PID=44604
```

2. Pre-provision the required `tabruk_app NOLOGIN` role and confirm PostgreSQL 18.6 / UTF-8

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin'; & (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p 50626 -U postgres -d postgres -v ON_ERROR_STOP=1 -c 'CREATE ROLE tabruk_app NOLOGIN;'; & (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p 50626 -U postgres -d postgres -At -v ON_ERROR_STOP=1 -c "SHOW server_version; SHOW server_encoding; SELECT 'role=' || rolname || ';canlogin=' || rolcanlogin::text FROM pg_roles WHERE rolname='tabruk_app';";
```

```text
CREATE ROLE
18.6
UTF8
role=tabruk_app;canlogin=false
```

3. Warning-as-error build regression

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk'; dotnet build HusayniaTabruk.sln --no-restore -warnaserror
```

```text
  HusayniaTabruk.Domain -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\src\HusayniaTabruk.Domain\bin\Debug\net10.0\HusayniaTabruk.Domain.dll
  HusayniaTabruk.Application -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\src\HusayniaTabruk.Application\bin\Debug\net10.0\HusayniaTabruk.Application.dll
  HusayniaTabruk.Domain.Tests -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.Domain.Tests\bin\Debug\net10.0\HusayniaTabruk.Domain.Tests.dll
  HusayniaTabruk.Infrastructure -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\src\HusayniaTabruk.Infrastructure\bin\Debug\net10.0\HusayniaTabruk.Infrastructure.dll
  HusayniaTabruk.Application.Tests -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.Application.Tests\bin\Debug\net10.0\HusayniaTabruk.Application.Tests.dll
  HusayniaTabruk.Api -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\src\HusayniaTabruk.Api\bin\Debug\net10.0\HusayniaTabruk.Api.dll
  HusayniaTabruk.Api.ContractTests -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.Api.ContractTests\bin\Debug\net10.0\HusayniaTabruk.Api.ContractTests.dll
  HusayniaTabruk.IntegrationTests -> C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.IntegrationTests\bin\Debug\net10.0\HusayniaTabruk.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:01.58
```

4. Recreate a disposable database, apply only the initial migration to both `public` and an alternate schema, and verify both schemas start with the initial broad history privilege

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $port=50626; $db='tabruk_r2_schema_ce0018a9'; $alt='tabruk_alt_ce0018a9'; $pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin'; & (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p $port -U postgres -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS $db WITH (FORCE);"; & (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p $port -U postgres -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE $db;"; & (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p $port -U postgres -d $db -v ON_ERROR_STOP=1 -c "CREATE SCHEMA $alt;"; Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk'; $env:TABRUK_MIGRATIONS_CONNECTION="Host=127.0.0.1;Port=$port;Database=$db;Username=postgres;Pooling=false;Include Error Detail=true"; dotnet ef database update 20260815075156_InitialPostgresSchema --project src/HusayniaTabruk.Infrastructure/HusayniaTabruk.Infrastructure.csproj --startup-project src/HusayniaTabruk.Infrastructure/HusayniaTabruk.Infrastructure.csproj --no-build; $env:TABRUK_MIGRATIONS_CONNECTION="Host=127.0.0.1;Port=$port;Database=$db;Username=postgres;SearchPath=$alt;Pooling=false;Include Error Detail=true"; dotnet ef database update 20260815075156_InitialPostgresSchema --project src/HusayniaTabruk.Infrastructure/HusayniaTabruk.Infrastructure.csproj --startup-project src/HusayniaTabruk.Infrastructure/HusayniaTabruk.Infrastructure.csproj --no-build; $sql=@"
SELECT COUNT(*) FROM public."__EFMigrationsHistory" WHERE "MigrationId"='20260815075156_InitialPostgresSchema';
SELECT COUNT(*) FROM "$alt"."__EFMigrationsHistory" WHERE "MigrationId"='20260815075156_InitialPostgresSchema';
SELECT has_table_privilege('tabruk_app','public."__EFMigrationsHistory"','UPDATE');
SELECT has_table_privilege('tabruk_app','"$alt"."__EFMigrationsHistory"','UPDATE');
"@; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1 port=$port dbname=$db user=postgres" -X -At -v ON_ERROR_STOP=1 -c $sql;
```

```text
DROP DATABASE
CREATE DATABASE
CREATE SCHEMA
Failed executing DbCommand (15ms) [Parameters=[], CommandType='Text', CommandTimeout='30']
SELECT "MigrationId", "ProductVersion"
FROM "__EFMigrationsHistory"
ORDER BY "MigrationId";
Acquiring an exclusive lock for migration application. See https://aka.ms/efcore-docs-migrations-lock for more information if this takes too long.
Applying migration '20260815075156_InitialPostgresSchema'.
Done.
Failed executing DbCommand (14ms) [Parameters=[], CommandType='Text', CommandTimeout='30']
SELECT "MigrationId", "ProductVersion"
FROM "__EFMigrationsHistory"
ORDER BY "MigrationId";
Acquiring an exclusive lock for migration application. See https://aka.ms/efcore-docs-migrations-lock for more information if this takes too long.
Applying migration '20260815075156_InitialPostgresSchema'.
Done.
1
1
t
t
```

5. Ambient alternate schema without explicit `tabruk.target_schema` must fail nonzero and leave both schemas uncorrected

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $port=50626; $db='tabruk_r2_schema_ce0018a9'; $alt='tabruk_alt_ce0018a9'; $pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin'; $script='C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\src\HusayniaTabruk.Infrastructure\Migrations\20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql'; & (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p $port -U postgres -d postgres -v ON_ERROR_STOP=1 -c "ALTER DATABASE $db SET search_path TO $alt;"; $PSNativeCommandUseErrorActionPreference=$false; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1 port=$port dbname=$db user=postgres" -X -v ON_ERROR_STOP=1 -f $script; $ambientExit=$LASTEXITCODE; $PSNativeCommandUseErrorActionPreference=$true; Write-Host ("AMBIENT_EXIT=$ambientExit"); if($ambientExit -eq 0){ throw 'Expected ambient alternate-schema owner-script run to fail, but it succeeded.' }; $sql=@"
SHOW search_path;
SELECT COUNT(*) FROM public."__EFMigrationsHistory" WHERE "MigrationId"='20260815102612_T8CorrectivePostgresHardening';
SELECT COUNT(*) FROM "$alt"."__EFMigrationsHistory" WHERE "MigrationId"='20260815102612_T8CorrectivePostgresHardening';
SELECT has_table_privilege('tabruk_app','public."__EFMigrationsHistory"','UPDATE');
SELECT has_table_privilege('tabruk_app','"$alt"."__EFMigrationsHistory"','UPDATE');
"@; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1 port=$port dbname=$db user=postgres" -X -At -v ON_ERROR_STOP=1 -c $sql;
```

```text
ALTER DATABASE
psql:C:/Users/syedhu/source/repos/Dreamer/HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql:43: ERROR:  Owner script requires options='-c tabruk.target_schema=<schema> -c search_path=<schema>'.
CONTEXT:  PL/pgSQL function inline_code_block line 10 at RAISE
AMBIENT_EXIT=3
tabruk_alt_ce0018a9
0
0
t
t
```

6. Mismatched explicit `target_schema=public` / `search_path=tabruk_alt_ce0018a9` must fail nonzero and leave both schemas uncorrected

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $port=50626; $db='tabruk_r2_schema_ce0018a9'; $alt='tabruk_alt_ce0018a9'; $pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin'; $script='C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\src\HusayniaTabruk.Infrastructure\Migrations\20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql'; $PSNativeCommandUseErrorActionPreference=$false; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1 port=$port dbname=$db user=postgres options='-c tabruk.target_schema=public -c search_path=$alt'" -X -v ON_ERROR_STOP=1 -f $script; $mismatchExit=$LASTEXITCODE; $PSNativeCommandUseErrorActionPreference=$true; Write-Host ("MISMATCH_EXIT=$mismatchExit"); if($mismatchExit -eq 0){ throw 'Expected mismatched explicit schema owner-script run to fail, but it succeeded.' }; $sql=@"
SELECT COUNT(*) FROM public."__EFMigrationsHistory" WHERE "MigrationId"='20260815102612_T8CorrectivePostgresHardening';
SELECT COUNT(*) FROM "$alt"."__EFMigrationsHistory" WHERE "MigrationId"='20260815102612_T8CorrectivePostgresHardening';
SELECT has_table_privilege('tabruk_app','public."__EFMigrationsHistory"','UPDATE');
SELECT has_table_privilege('tabruk_app','"$alt"."__EFMigrationsHistory"','UPDATE');
"@; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1 port=$port dbname=$db user=postgres" -X -At -v ON_ERROR_STOP=1 -c $sql;
```

```text
psql:C:/Users/syedhu/source/repos/Dreamer/HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql:43: ERROR:  Owner script expected search_path public but current setting is tabruk_alt_ce0018a9.
CONTEXT:  PL/pgSQL function inline_code_block line 23 at RAISE
MISMATCH_EXIT=3
0
0
t
t
```

7. Explicit `public/public` selection must succeed twice and harden only the intended schema

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $port=50626; $db='tabruk_r2_schema_ce0018a9'; $alt='tabruk_alt_ce0018a9'; $pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin'; $script='C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\src\HusayniaTabruk.Infrastructure\Migrations\20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql'; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1 port=$port dbname=$db user=postgres options='-c tabruk.target_schema=public -c search_path=public'" -X -v ON_ERROR_STOP=1 -f $script; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1 port=$port dbname=$db user=postgres options='-c tabruk.target_schema=public -c search_path=public'" -X -v ON_ERROR_STOP=1 -f $script; $sql=@"
SELECT COUNT(*) FROM public."__EFMigrationsHistory" WHERE "MigrationId"='20260815102612_T8CorrectivePostgresHardening';
SELECT COUNT(*) FROM "$alt"."__EFMigrationsHistory" WHERE "MigrationId"='20260815102612_T8CorrectivePostgresHardening';
SELECT to_regclass('public."ux_signups_waitlisted_order_per_help_need"') IS NOT NULL;
SELECT indisvalid FROM pg_index WHERE indexrelid='public."ux_signups_waitlisted_order_per_help_need"'::regclass;
SELECT convalidated FROM pg_constraint WHERE conrelid='public.signups'::regclass AND conname='ck_signups_transition_chronology';
SELECT has_table_privilege('tabruk_app','public.notifications','SELECT')::text || '|' || has_table_privilege('tabruk_app','public.notifications','INSERT')::text || '|' || has_table_privilege('tabruk_app','public.notifications','UPDATE')::text || '|' || has_table_privilege('tabruk_app','public.notifications','DELETE')::text;
SELECT has_table_privilege('tabruk_app','public.audit_events','INSERT')::text || '|' || has_table_privilege('tabruk_app','public.audit_events','SELECT')::text || '|' || has_table_privilege('tabruk_app','public.audit_events','UPDATE')::text || '|' || has_table_privilege('tabruk_app','public.audit_events','DELETE')::text;
SELECT has_table_privilege('tabruk_app','public."__EFMigrationsHistory"','SELECT')::text || '|' || has_table_privilege('tabruk_app','public."__EFMigrationsHistory"','INSERT')::text || '|' || has_table_privilege('tabruk_app','public."__EFMigrationsHistory"','UPDATE')::text || '|' || has_table_privilege('tabruk_app','public."__EFMigrationsHistory"','DELETE')::text;
SELECT has_sequence_privilege('tabruk_app','public."identity_user_claims_Id_seq"','USAGE')::text || '|' || has_sequence_privilege('tabruk_app','public."identity_user_claims_Id_seq"','SELECT')::text || '|' || has_sequence_privilege('tabruk_app','public."identity_user_claims_Id_seq"','UPDATE')::text;
SELECT has_table_privilege('tabruk_app','"$alt"."__EFMigrationsHistory"','UPDATE');
"@; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1 port=$port dbname=$db user=postgres options='-c tabruk.target_schema=public -c search_path=public'" -X -At -v ON_ERROR_STOP=1 -c $sql;
```

```text
DO
DO
DO
REVOKE
REVOKE
REVOKE
GRANT
GRANT
GRANT
DO
DO
CREATE INDEX
ALTER TABLE
ALTER TABLE
INSERT 0 1
DO
DO
DO
REVOKE
REVOKE
REVOKE
GRANT
GRANT
GRANT
DO
DO
INSERT 0 0
1
0
t
t
t
true|true|true|true
true|false|false|false
false|false|false|false
true|true|false
t
```

8. Missing initial migration history must fail nonzero and write no corrective history

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $port=50626; $db='tabruk_r2_missing_ce0018a9'; $pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin'; $script='C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\src\HusayniaTabruk.Infrastructure\Migrations\20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql'; & (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p $port -U postgres -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS $db WITH (FORCE);"; & (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p $port -U postgres -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE $db;"; $historySql=@'
CREATE TABLE "__EFMigrationsHistory"
(
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);
'@; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1 port=$port dbname=$db user=postgres" -X -v ON_ERROR_STOP=1 -c $historySql; $PSNativeCommandUseErrorActionPreference=$false; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1 port=$port dbname=$db user=postgres options='-c tabruk.target_schema=public -c search_path=public'" -X -v ON_ERROR_STOP=1 -f $script; $missingExit=$LASTEXITCODE; $PSNativeCommandUseErrorActionPreference=$true; Write-Host ("MISSING_HISTORY_EXIT=$missingExit"); if($missingExit -eq 0){ throw 'Expected missing-initial-history owner-script run to fail, but it succeeded.' }; $verifySql=@'
SELECT COUNT(*) FROM "__EFMigrationsHistory";
SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId"='20260815102612_T8CorrectivePostgresHardening';
'@; & (Join-Path $pgBin 'psql.exe') --dbname "host=127.0.0.1 port=$port dbname=$db user=postgres options='-c tabruk.target_schema=public -c search_path=public'" -X -At -v ON_ERROR_STOP=1 -c $verifySql;
```

```text
NOTICE:  database "tabruk_r2_missing_ce0018a9" does not exist, skipping
DROP DATABASE
CREATE DATABASE
CREATE TABLE
DO
DO
psql:C:/Users/syedhu/source/repos/Dreamer/HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql:67: ERROR:  20260815075156_InitialPostgresSchema has not been applied in schema public.
CONTEXT:  PL/pgSQL function inline_code_block line 8 at RAISE
MISSING_HISTORY_EXIT=3
0
0
```

9. Focused owner-script + manifest + catalog + cleanup regressions

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=50626;Database=postgres;Username=postgres;Pooling=false;Include Error Detail=true'; $env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin\psql.exe'; Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk'; dotnet test tests/HusayniaTabruk.IntegrationTests/HusayniaTabruk.IntegrationTests.csproj --no-build --filter 'FullyQualifiedName~PostgresMigrationArtifactManifestTests|FullyQualifiedName~PostgresLeastPrivilegeCatalogTests|FullyQualifiedName~ApprovedOwnerIdempotentScript|FullyQualifiedName~PostgresTransactionCleanupTests'
```

```text
Test run for C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.IntegrationTests\bin\Debug\net10.0\HusayniaTabruk.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 7 s - HusayniaTabruk.IntegrationTests.dll (net10.0)
```

10. Whole PostgreSQL persistence category

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=50626;Database=postgres;Username=postgres;Pooling=false;Include Error Detail=true'; $env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin\psql.exe'; Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk'; dotnet test tests/HusayniaTabruk.IntegrationTests/HusayniaTabruk.IntegrationTests.csproj --no-build --filter 'Category=Persistence'
```

```text
Test run for C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.IntegrationTests\bin\Debug\net10.0\HusayniaTabruk.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    59, Skipped:     0, Total:    59, Duration: 1 m - HusayniaTabruk.IntegrationTests.dll (net10.0)
```

11. Full `.NET` suite regression

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $env:TABRUK_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=50626;Database=postgres;Username=postgres;Pooling=false;Include Error Detail=true'; $env:TABRUK_TEST_PSQL_PATH='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin\psql.exe'; Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk'; dotnet test HusayniaTabruk.sln --no-build
```

```text
Test run for C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.Api.ContractTests\bin\Debug\net10.0\HusayniaTabruk.Api.ContractTests.dll (.NETCoreApp,Version=v10.0)
Test run for C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.Application.Tests\bin\Debug\net10.0\HusayniaTabruk.Application.Tests.dll (.NETCoreApp,Version=v10.0)
Test run for C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.Domain.Tests\bin\Debug\net10.0\HusayniaTabruk.Domain.Tests.dll (.NETCoreApp,Version=v10.0)
Test run for C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.IntegrationTests\bin\Debug\net10.0\HusayniaTabruk.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 2 s - HusayniaTabruk.Api.ContractTests.dll (net10.0)

Passed!  - Failed:     0, Passed:    79, Skipped:     0, Total:    79, Duration: 7 s - HusayniaTabruk.Application.Tests.dll (net10.0)

Passed!  - Failed:     0, Passed:   395, Skipped:     0, Total:   395, Duration: 13 s - HusayniaTabruk.Domain.Tests.dll (net10.0)

Passed!  - Failed:     0, Passed:    59, Skipped:     0, Total:    59, Duration: 59 s - HusayniaTabruk.IntegrationTests.dll (net10.0)
```

12. `dotnet format` verification

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; Set-Location 'C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk'; dotnet format HusayniaTabruk.sln --no-restore --verify-no-changes; Write-Host 'FORMAT_EXIT=0';
```

```text
FORMAT_EXIT=0
```

13. Prove the persistence suite cleaned up its disposable `tabruk_it_*` schemas

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $pgBin='C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin'; & (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p 50626 -U postgres -d postgres -X -At -v ON_ERROR_STOP=1 -c "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name LIKE 'tabruk_it_%';";
```

```text
0
```

14. Stop the exact PostgreSQL PID and remove the exact disposable data directory

```powershell
$ErrorActionPreference='Stop'; $PSNativeCommandUseErrorActionPreference=$true; $dataDir='C:\Users\syedhu\AppData\Local\Temp\tabruk-r2-v1-ce0018a9715a49dea55004ad2d57bd8b'; Stop-Process -Id 44604 -Force; Start-Sleep -Seconds 2; $running = $null -ne (Get-Process -Id 44604 -ErrorAction SilentlyContinue); Write-Host ("PID_STILL_RUNNING={0}" -f $running); Remove-Item -LiteralPath $dataDir -Recurse -Force; Write-Host ("DATADIR_EXISTS_AFTER_REMOVE={0}" -f (Test-Path -LiteralPath $dataDir));
```

```text
PID_STILL_RUNNING=False
DATADIR_EXISTS_AFTER_REMOVE=False
```

### Rework 2 summary counts

- Build: `0 Warning(s)`, `0 Error(s)`
- Focused rework tests: `Passed: 12, Failed: 0, Skipped: 0`
- Full persistence category: `Passed: 59, Failed: 0, Skipped: 0`
- Full `.NET` suite: `Passed: 571, Failed: 0, Skipped: 0`
- Aggregate executed across the three `dotnet test` commands above (reruns included): `Passed: 642, Failed: 0, Skipped: 0`
- Format: `FORMAT_EXIT=0`

### Focused Rework 2 map

- Guard errors now exit nonzero under real `psql` automation -> manual commands 5, 6, and 8 returned `AMBIENT_EXIT=3`, `MISMATCH_EXIT=3`, and `MISSING_HISTORY_EXIT=3`; matching regression coverage passed in `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:269` and `:307` -> PASS
- Ambient or mismatched alternate-schema execution does not harden the wrong schema -> manual commands 5-7 showed corrective history stayed `0` in `tabruk_alt_ce0018a9` through both failing runs and remained `0` after the successful `public/public` run, while alternate-schema `__EFMigrationsHistory` `UPDATE` stayed `t` -> PASS
- Explicit single-schema selection is now mandatory and documented even for `public` -> script guards inspected at `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql:10`, `:21`, `:36`; safety guidance inspected at `src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md:46`, `:55`, `:58` -> PASS
- Valid explicit owner execution is idempotent and hardens only the intended schema -> manual command 7 returned public corrective history `1`, alternate corrective history `0`, waitlist index present/valid `t`/`t`, chronology constraint validated `t`, CRUD on `public.notifications`, insert-only on `public.audit_events`, denied history CRUD on `public."__EFMigrationsHistory"`, and sequence rights `true|true|false`; matching regression coverage passed at `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:224` -> PASS
- Missing initial history fails before corrective history is written -> manual command 8 returned history row count `0` and corrective history row count `0`; matching regression coverage passed at `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:269` -> PASS
- Focused manifest/catalog/cleanup surface stayed green -> command 9 passed `12/12`; targeted tests included `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationArtifactManifestTests.cs:19`, `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresLeastPrivilegeCatalogTests.cs:59`, and `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresTransactionCleanupTests.cs:26`, `:53`, `:80`, `:107` -> PASS
- Full PostgreSQL persistence, full `.NET`, build, and format gates remained green on disposable PostgreSQL 18.6 -> commands 3 and 9-12 passed -> PASS
- Disposable environment was isolated and cleaned -> commands 1-2 and 13-14 proved PostgreSQL `18.6`, `UTF8`, `tabruk_app` `canlogin=false`, `tabruk_it_*` schema count `0`, exact PID `44604` stopped, and exact data directory removed -> PASS
- Mobile and Compose gates were not rerun because Rework 2 touched only the owner SQL/script docs/manifest and `.NET` persistence tests, matching the objective's scope restriction -> PASS

Conclusion: PASS
