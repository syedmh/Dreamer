param(
    [ValidateSet('Start', 'Stop', 'Cleanup')]
    [string] $Action,
    [string] $PgBin = 'C:\Users\syedhu\AppData\Local\HusayniaTabruk\PostgreSQL18Binary\pgsql\bin',
    [string] $ClusterId
)

$ErrorActionPreference = 'Stop'
$registryPath = Join-Path $env:TEMP 'tabruk-pg18-test-clusters.json'

function Read-Registry {
    if (-not (Test-Path $registryPath)) {
        return @()
    }

    return @((Get-Content $registryPath -Raw | ConvertFrom-Json))
}

function Write-Registry([object[]] $entries) {
    if ($entries.Count -eq 0) {
        Remove-Item $registryPath -Force -ErrorAction SilentlyContinue
        return
    }

    ConvertTo-Json $entries | Set-Content $registryPath
}

function Stop-ExactProcessTree([int] $rootPid) {
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $processes = Get-CimInstance Win32_Process
        $descendants = [System.Collections.Generic.List[int]]::new()
        function Add-Descendants([int] $parentPid) {
            foreach ($child in $processes | Where-Object ParentProcessId -eq $parentPid) {
                Add-Descendants ([int] $child.ProcessId)
                $descendants.Add([int] $child.ProcessId)
            }
        }

        Add-Descendants $rootPid
        foreach ($processId in $descendants) {
            if (Get-Process -Id $processId -ErrorAction SilentlyContinue) {
                Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
            }
        }
        if (Get-Process -Id $rootPid -ErrorAction SilentlyContinue) {
            Stop-Process -Id $rootPid -Force -ErrorAction SilentlyContinue
        }
        if (-not (Get-Process -Id $rootPid -ErrorAction SilentlyContinue) -and
            -not (Get-CimInstance Win32_Process |
                Where-Object ParentProcessId -eq $rootPid)) {
            return
        }
        Start-Sleep -Milliseconds 250
    }
}

function Remove-Cluster($entry) {
    $dataDir = [string] $entry.DataDirectory
    $serverPid = [int] $entry.ServerPid
    $pidFile = Join-Path $dataDir 'postmaster.pid'
    if (Test-Path $pidFile) {
        $recordedPid = [int](Get-Content $pidFile -First 1)
        if ($recordedPid -ne $serverPid) {
            throw "Refusing cleanup: postmaster PID mismatch for $dataDir."
        }
    }

    Stop-ExactProcessTree $serverPid
    for ($attempt = 0; $attempt -lt 10 -and (Test-Path $dataDir); $attempt++) {
        Start-Sleep -Milliseconds 500
        Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path $dataDir) {
        throw "Could not remove PostgreSQL test data directory $dataDir."
    }
}

$entries = @(Read-Registry)
$activeEntries = [System.Collections.Generic.List[object]]::new()
foreach ($entry in $entries) {
    $ownerAlive = Get-Process -Id ([int] $entry.OwnerPid) -ErrorAction SilentlyContinue
    $selected = $Action -in @('Start', 'Cleanup') -and -not $ownerAlive
    $selected = $selected -or ($Action -eq 'Stop' -and $entry.ClusterId -eq $ClusterId)
    if ($selected) {
        Remove-Cluster $entry
    }
    else {
        $activeEntries.Add($entry)
    }
}
Write-Registry $activeEntries.ToArray()

if ($Action -ne 'Start') {
    return
}

$id = if ($ClusterId) { $ClusterId } else { [guid]::NewGuid().ToString('N') }
$dataDirectory = Join-Path $env:TEMP "tabruk-pg18-$id"
$listener = [System.Net.Sockets.TcpListener]::new(
    [System.Net.IPAddress]::Loopback,
    0)
$listener.Start()
$port = ([System.Net.IPEndPoint] $listener.LocalEndpoint).Port
$listener.Stop()

& (Join-Path $PgBin 'initdb.exe') -D $dataDirectory -U postgres `
    --auth=trust --auth-host=trust --auth-local=trust -E UTF8 `
    --locale-provider=icu --icu-locale=en-US
Add-Content (Join-Path $dataDirectory 'postgresql.conf') `
    "`nlisten_addresses = '127.0.0.1'`nport = $port"
$postgresOutput = Join-Path $dataDirectory 'postgres.log'
$postgresError = Join-Path $dataDirectory 'postgres.err.log'
$postgresProcess = Start-Process (Join-Path $PgBin 'postgres.exe') `
    -ArgumentList "-D `"$dataDirectory`" -h 127.0.0.1 -p $port" `
    -RedirectStandardOutput $postgresOutput `
    -RedirectStandardError $postgresError `
    -WindowStyle Hidden `
    -PassThru

$ready = $false
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    & (Join-Path $PgBin 'pg_isready.exe') -h 127.0.0.1 -p $port | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $ready = $true
        break
    }
    if ($postgresProcess.HasExited) {
        break
    }
    Start-Sleep -Milliseconds 250
}
if (-not $ready) {
    throw "PostgreSQL did not become ready: $(Get-Content $postgresError -Raw)"
}
$serverPid = [int](Get-Content (Join-Path $dataDirectory 'postmaster.pid') -First 1)

$entries = @(Read-Registry)
$entries += [pscustomobject]@{
    ClusterId = $id
    DataDirectory = $dataDirectory
    Port = $port
    ServerPid = $serverPid
    OwnerPid = $PID
}
Write-Registry $entries

[pscustomobject]@{
    ClusterId = $id
    DataDirectory = $dataDirectory
    Port = $port
    ServerPid = $serverPid
    ConnectionString = "Host=127.0.0.1;Port=$port;Database=postgres;Username=postgres;Pooling=false"
}
