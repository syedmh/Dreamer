[CmdletBinding()]
param([string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\artifact\migrations\bundle\Migration.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "t21-migration-fence-$([guid]::NewGuid().ToString('N'))"
$statePath = Join-Path $temporaryRoot 'lease-state.json'
$mutationMarkerPath = Join-Path $temporaryRoot 'mutation-count.txt'
$previousStatePath = [Environment]::GetEnvironmentVariable('T21_MIGRATION_STAGE_LEASE_STATE_PATH')
$previousMutationId = [Environment]::GetEnvironmentVariable('T21_STAGE_LEASE_MUTATION_ID')
$passed = 0
$failed = 0
$mutationMarkerCount = 0
$oldMarkerCount = 0
$currentMarkerCount = 0
$failureMarkerCount = 0
$staleReleasePreserved = 0

function Assert-MigrationFenceTest {
    param([string]$Name, [bool]$Condition, [string]$Failure)

    if ($Condition) {
        $script:passed++
        Write-Output "PASS  $Name"
    }
    else {
        $script:failed++
        Write-Output "FAIL  $Name :: $Failure"
    }
}

function Set-FixtureLeaseExpired {
    param([Parameter(Mandatory = $true)][string]$Resource)

    $state = Read-MigrationStageLeaseState -Path $statePath
    $lease = @($state.leases | Where-Object { [string]$_.resource -ceq $Resource })
    if ($lease.Count -ne 1) {
        throw "Fixture lease is missing: $Resource"
    }
    $lease[0].expiresAtUtc = [DateTimeOffset]::UtcNow.AddSeconds(-1).ToString('O')
    Write-MigrationStageLeaseState -State $state -Path $statePath
}

function Get-FixtureLease {
    param([Parameter(Mandatory = $true)][string]$Resource)

    $state = Read-MigrationStageLeaseState -Path $statePath
    $lease = @($state.leases | Where-Object { [string]$_.resource -ceq $Resource })
    if ($lease.Count -ne 1) {
        throw "Fixture lease is missing: $Resource"
    }
    return $lease[0]
}

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    [Environment]::SetEnvironmentVariable('T21_MIGRATION_STAGE_LEASE_STATE_PATH', $statePath)
    $leasePolicy = [pscustomobject]@{
        provider = 'sqlserver-stage-lease-v1'
        resourcePrefix = 'husaynia-stage-lease'
        tableName = '__HusayniaStageLease'
        leaseDurationSeconds = 900
        renewAfterSeconds = 300
        staleAfterSeconds = 900
    }
    $target = 'a' * 64
    $resource = "husaynia-stage-lease-development-$target"
    $oldHolder = [ordered]@{ holderRunId = '101'; holderRunAttempt = 1 }
    $currentHolder = [ordered]@{ holderRunId = '102'; holderRunAttempt = 1 }
    $takeoverHolder = [ordered]@{ holderRunId = '103'; holderRunAttempt = 1 }
    $oldAuthorization = 'b' * 64
    $currentAuthorization = 'c' * 64
    $takeoverAuthorization = 'd' * 64

    $noSwitchFailure = ''
    try {
        Invoke-MigrationStageLeaseOperation `
            -Mode AcquireLease `
            -ServerName 'fixture.database.windows.net' `
            -DatabaseName 'husaynia-dev' `
            -LeasePolicy $leasePolicy `
            -Resource $resource `
            -Stage Development `
            -TargetFingerprint $target `
            -AuthorizationEvidenceSha256 $oldAuthorization `
            -Holder $oldHolder | Out-Null
    }
    catch {
        $noSwitchFailure = $_.Exception.Message
    }
    Assert-MigrationFenceTest 'migration-fence-local-state-requires-explicit-test-seam-switch' (
        $noSwitchFailure -ceq 'T21_SQL_RUNTIME_INVALID' -and
        -not (Test-Path -LiteralPath $statePath)
    ) 'The local JSON lease seam activated without EnableLocalTestSeams or touched its state file.'

    $previousGitHubActions = [Environment]::GetEnvironmentVariable('GITHUB_ACTIONS')
    $global:T21LeaseActionsSeamGuard = [ordered]@{ token = 0; sql = 0 }
    $actionsFailure = ''
    try {
        [Environment]::SetEnvironmentVariable('GITHUB_ACTIONS', 'true')
        Invoke-MigrationStageLeaseOperation `
            -Mode AcquireLease `
            -ServerName 'fixture.database.windows.net' `
            -DatabaseName 'husaynia-dev' `
            -LeasePolicy $leasePolicy `
            -Resource $resource `
            -Stage Development `
            -TargetFingerprint $target `
            -AuthorizationEvidenceSha256 $oldAuthorization `
            -Holder $oldHolder `
            -EnableLocalTestSeams `
            -LocalAccessTokenProvider {
                param($Request)
                $global:T21LeaseActionsSeamGuard.token++
                return [pscustomobject]@{
                    exitCode = 0
                    timedOut = $false
                    stdout = 'not-invoked-token'
                    stderr = ''
                }
            } `
            -LocalSqlExecutor {
                param($Request)
                $global:T21LeaseActionsSeamGuard.sql++
                return ,@(1..7)
            } | Out-Null
    }
    catch {
        $actionsFailure = $_.Exception.Message
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            'GITHUB_ACTIONS',
            $previousGitHubActions)
    }
    Assert-MigrationFenceTest 'migration-fence-actions-rejects-local-state-before-any-seam-or-file' (
        $actionsFailure -ceq 'T21_SQL_RUNTIME_INVALID' -and
        $global:T21LeaseActionsSeamGuard.token -eq 0 -and
        $global:T21LeaseActionsSeamGuard.sql -eq 0 -and
        -not (Test-Path -LiteralPath $statePath)
    ) 'GITHUB_ACTIONS=true invoked a local lease provider/executor or touched the JSON state file.'

    $oldLease = Invoke-MigrationStageLeaseOperation `
        -Mode AcquireLease -ServerName 'fixture.database.windows.net' -DatabaseName 'husaynia-dev' `
        -LeasePolicy $leasePolicy -Resource $resource -Stage Development -TargetFingerprint $target `
        -AuthorizationEvidenceSha256 $oldAuthorization -Holder $oldHolder `
        -EnableLocalTestSeams
    Set-FixtureLeaseExpired -Resource $resource
    $currentLease = Invoke-MigrationStageLeaseOperation `
        -Mode AcquireLease -ServerName 'fixture.database.windows.net' -DatabaseName 'husaynia-dev' `
        -LeasePolicy $leasePolicy -Resource $resource -Stage Development -TargetFingerprint $target `
        -AuthorizationEvidenceSha256 $currentAuthorization -Holder $currentHolder `
        -EnableLocalTestSeams

    Assert-MigrationFenceTest 'migration-fence-stale-holder-takeover-increments-token' (
        [int64]$oldLease.fenceToken -eq 1 -and
        [int64]$currentLease.fenceToken -eq 2 -and
        [string]$currentLease.holderRunId -ceq '102'
    ) 'A stale durable lease was not replaced by a new holder with a strictly higher fence token.'

    $staleReleaseRejected = $false
    try {
        Invoke-MigrationStageLeaseOperation `
            -Mode ReleaseLease `
            -ServerName 'fixture.database.windows.net' `
            -DatabaseName 'husaynia-dev' `
            -LeasePolicy $leasePolicy `
            -Resource $resource `
            -Stage Development `
            -TargetFingerprint $target `
            -AuthorizationEvidenceSha256 $oldAuthorization `
            -Holder $oldHolder `
            -ExpectedFenceToken ([int64]$oldLease.fenceToken) `
            -EnableLocalTestSeams |
            Out-Null
    }
    catch {
        $staleReleaseRejected = $_.Exception.Message.Contains(
            'cleanup could not release the current fence token')
    }
    $leaseAfterStaleRelease = Get-FixtureLease -Resource $resource
    $staleReleasePreserved = [int](
        $staleReleaseRejected -and
        [string]$leaseAfterStaleRelease.holderRunId -ceq
            [string]$currentHolder.holderRunId -and
        [int]$leaseAfterStaleRelease.holderRunAttempt -eq
            [int]$currentHolder.holderRunAttempt -and
        [int64]$leaseAfterStaleRelease.fenceToken -eq [int64]$currentLease.fenceToken -and
        [string]$leaseAfterStaleRelease.authorizationEvidenceSha256 -ceq
            $currentAuthorization -and
        -not [bool]$leaseAfterStaleRelease.released
    )
    Assert-MigrationFenceTest 'migration-fence-stale-release-preserves-current-holder' (
        $staleReleasePreserved -eq 1
    ) 'A stale holder release changed or released the current holder lease.'

    $oldHolderRejected = $false
    try {
        Invoke-MigrationFencedMutation `
            -ServerName 'fixture.database.windows.net' -DatabaseName 'husaynia-dev' `
            -LeasePolicy $leasePolicy -Resource $resource -Stage Development -TargetFingerprint $target `
            -AuthorizationEvidenceSha256 $oldAuthorization -Holder $oldHolder `
            -ExpectedFenceToken ([int64]$oldLease.fenceToken) `
            -EnableLocalTestSeams `
            -MutationExecutor {
                param($validatedLease)
                Add-Content -LiteralPath $mutationMarkerPath -Value "old:$($validatedLease.fenceToken)"
            } | Out-Null
    }
    catch {
        $oldHolderRejected = $_.Exception.Message.Contains('Migration mutation claim rejected')
    }
    $oldMarkers = @(
        if (Test-Path -LiteralPath $mutationMarkerPath) {
            Get-Content -LiteralPath $mutationMarkerPath | Where-Object { $_ -like 'old:*' }
        }
    )
    Assert-MigrationFenceTest 'migration-fence-old-holder-rejected-before-fake-mutation-executor' (
        $oldHolderRejected -and $oldMarkers.Count -eq 0
    ) 'An old lease holder invoked the fake mutation executor after takeover.'

    $takeoverProbe = [pscustomobject]@{
        rejected = $false
        message = ''
    }
    $currentMutation = Invoke-MigrationFencedMutation `
        -ServerName 'fixture.database.windows.net' -DatabaseName 'husaynia-dev' `
        -LeasePolicy $leasePolicy -Resource $resource -Stage Development -TargetFingerprint $target `
        -AuthorizationEvidenceSha256 $currentAuthorization -Holder $currentHolder `
        -ExpectedFenceToken ([int64]$currentLease.fenceToken) `
        -EnableLocalTestSeams `
        -MutationExecutor ({
            param($validatedLease)

            $executorState = Read-MigrationStageLeaseState -Path $statePath
            $executorLease = @(
                $executorState.leases |
                    Where-Object { [string]$_.resource -ceq $resource }
            )
            if ($executorLease.Count -ne 1) {
                throw "Fixture lease is missing inside mutation executor: $resource"
            }
            $executorLease[0].expiresAtUtc =
                [DateTimeOffset]::UtcNow.AddSeconds(-1).ToString('O')
            Write-MigrationStageLeaseState -State $executorState -Path $statePath
            try {
                Invoke-MigrationStageLeaseOperation `
                    -Mode AcquireLease `
                    -ServerName 'fixture.database.windows.net' `
                    -DatabaseName 'husaynia-dev' `
                    -LeasePolicy $leasePolicy `
                    -Resource $resource `
                    -Stage Development `
                    -TargetFingerprint $target `
                    -AuthorizationEvidenceSha256 $takeoverAuthorization `
                    -Holder $takeoverHolder `
                    -EnableLocalTestSeams | Out-Null
            }
            catch {
                $takeoverProbe.message = $_.Exception.Message
                $takeoverProbe.rejected = $_.Exception.Message.Contains(
                    'active mutation claim exists')
            }
            Add-Content -LiteralPath $mutationMarkerPath -Value "current:$($validatedLease.activeMutationId)"
            return [pscustomobject]@{
                mutationId = [string]$validatedLease.activeMutationId
                environmentMutationId =
                    [Environment]::GetEnvironmentVariable('T21_STAGE_LEASE_MUTATION_ID')
            }
        })

    $currentMarkers = @(
        Get-Content -LiteralPath $mutationMarkerPath |
            Where-Object { $_ -like 'current:*' }
    )
    Assert-MigrationFenceTest 'migration-fence-active-claim-blocks-takeover-after-expiry-inside-executor' (
        $takeoverProbe.rejected -and
        [string]$currentMutation.Result.mutationId -ceq
            [string]$currentMutation.Result.environmentMutationId
    ) "An expired lease was taken over while its durable mutation claim was active: $($takeoverProbe.message)"
    Assert-MigrationFenceTest 'migration-fence-current-holder-executor-writes-exactly-once' (
        $currentMarkers.Count -eq 1 -and
        [string]$currentMutation.Lease.activeMutationId -ceq
            [string]$currentMutation.Result.mutationId
    ) 'The current holder executor did not run exactly once with the bound mutation claim.'

    $completedRecord = Get-FixtureLease -Resource $resource
    Assert-MigrationFenceTest 'migration-fence-success-clears-active-mutation-claim-after-expiry' (
        [string]::IsNullOrWhiteSpace([string]$completedRecord.activeMutationId) -and
        [string]::IsNullOrWhiteSpace([string]$completedRecord.activeMutationStartedAtUtc) -and
        [DateTimeOffset]::Parse([string]$completedRecord.expiresAtUtc) -le [DateTimeOffset]::UtcNow
    ) 'Successful completion did not clear the durable claim after the nominal lease expired.'

    $failureResource = "husaynia-stage-lease-staging-$target"
    $failureHolder = [ordered]@{ holderRunId = '201'; holderRunAttempt = 1 }
    $failureAuthorization = 'e' * 64
    $failureLease = Invoke-MigrationStageLeaseOperation `
        -Mode AcquireLease -ServerName 'fixture.database.windows.net' -DatabaseName 'husaynia-stg' `
        -LeasePolicy $leasePolicy -Resource $failureResource -Stage Staging -TargetFingerprint $target `
        -AuthorizationEvidenceSha256 $failureAuthorization -Holder $failureHolder `
        -EnableLocalTestSeams
    $executorFailed = $false
    try {
        Invoke-MigrationFencedMutation `
            -ServerName 'fixture.database.windows.net' -DatabaseName 'husaynia-stg' `
            -LeasePolicy $leasePolicy -Resource $failureResource -Stage Staging -TargetFingerprint $target `
            -AuthorizationEvidenceSha256 $failureAuthorization -Holder $failureHolder `
            -ExpectedFenceToken ([int64]$failureLease.fenceToken) `
            -EnableLocalTestSeams `
            -MutationExecutor {
                param($validatedLease)
                Add-Content -LiteralPath $mutationMarkerPath -Value "failure:$($validatedLease.activeMutationId)"
                throw 'intentional mutation executor failure'
            } | Out-Null
    }
    catch {
        $executorFailed = $_.Exception.Message.Contains('intentional mutation executor failure')
    }
    $failureRecord = Get-FixtureLease -Resource $failureResource
    $failureMarkers = @(
        Get-Content -LiteralPath $mutationMarkerPath |
            Where-Object { $_ -like 'failure:*' }
    )
    Assert-MigrationFenceTest 'migration-fence-executor-failure-clears-claim-and-rethrows' (
        $executorFailed -and
        $failureMarkers.Count -eq 1 -and
        [string]::IsNullOrWhiteSpace([string]$failureRecord.activeMutationId) -and
        [string]::IsNullOrWhiteSpace([string]$failureRecord.activeMutationStartedAtUtc)
    ) 'Executor failure was swallowed or left the durable mutation claim active.'

    $mismatchResource = "husaynia-stage-lease-production-$target"
    $mismatchHolder = [ordered]@{ holderRunId = '301'; holderRunAttempt = 1 }
    $mismatchAuthorization = 'f' * 64
    $mismatchLease = Invoke-MigrationStageLeaseOperation `
        -Mode AcquireLease -ServerName 'fixture.database.windows.net' -DatabaseName 'husaynia-prd' `
        -LeasePolicy $leasePolicy -Resource $mismatchResource -Stage Production -TargetFingerprint $target `
        -AuthorizationEvidenceSha256 $mismatchAuthorization -Holder $mismatchHolder `
        -EnableLocalTestSeams
    $claimId = [guid]::NewGuid()
    Invoke-MigrationStageLeaseOperation `
        -Mode BeginMutation -ServerName 'fixture.database.windows.net' -DatabaseName 'husaynia-prd' `
        -LeasePolicy $leasePolicy -Resource $mismatchResource -Stage Production -TargetFingerprint $target `
        -AuthorizationEvidenceSha256 $mismatchAuthorization -Holder $mismatchHolder `
        -ExpectedFenceToken ([int64]$mismatchLease.fenceToken) -MutationId $claimId `
        -EnableLocalTestSeams | Out-Null
    Set-FixtureLeaseExpired -Resource $mismatchResource
    $completionMismatchRejected = $false
    try {
        Invoke-MigrationStageLeaseOperation `
            -Mode CompleteMutation -ServerName 'fixture.database.windows.net' -DatabaseName 'husaynia-prd' `
            -LeasePolicy $leasePolicy -Resource $mismatchResource -Stage Production -TargetFingerprint $target `
            -AuthorizationEvidenceSha256 $mismatchAuthorization -Holder $mismatchHolder `
            -ExpectedFenceToken ([int64]$mismatchLease.fenceToken) -MutationId ([guid]::NewGuid()) `
            -EnableLocalTestSeams |
            Out-Null
    }
    catch {
        $completionMismatchRejected = $_.Exception.Message.Contains(
            'mismatched claim or lease tuple')
    }
    $mismatchRecord = Get-FixtureLease -Resource $mismatchResource
    $claimPreservedAfterMismatch =
        [string]$mismatchRecord.activeMutationId -ceq $claimId.ToString('D')
    Invoke-MigrationStageLeaseOperation `
        -Mode CompleteMutation -ServerName 'fixture.database.windows.net' -DatabaseName 'husaynia-prd' `
        -LeasePolicy $leasePolicy -Resource $mismatchResource -Stage Production -TargetFingerprint $target `
        -AuthorizationEvidenceSha256 $mismatchAuthorization -Holder $mismatchHolder `
        -ExpectedFenceToken ([int64]$mismatchLease.fenceToken) -MutationId $claimId `
        -EnableLocalTestSeams | Out-Null
    $mismatchCompletedRecord = Get-FixtureLease -Resource $mismatchResource
    Assert-MigrationFenceTest 'migration-fence-completion-mismatch-fails-closed-and-exact-claim-completes-after-expiry' (
        $completionMismatchRejected -and
        $claimPreservedAfterMismatch -and
        [string]::IsNullOrWhiteSpace([string]$mismatchCompletedRecord.activeMutationId)
    ) 'Completion accepted a mismatched claim or rejected the exact claim after expiry.'

    $commonPath = Join-Path $RepositoryRoot 'eng\artifact\migrations\bundle\Migration.Common.ps1'
    $bundlePath = Join-Path $RepositoryRoot 'eng\artifact\migrations\bundle\Invoke-MigrationBundle.ps1'
    $commonText = Get-Content -LiteralPath $commonPath -Raw
    $bundleText = Get-Content -LiteralPath $bundlePath -Raw
    $sqlText = $commonText.Substring(
        $commonText.IndexOf('function Invoke-SqlMigrationStageLease'))
    $acquireSql = $sqlText.Substring(
        $sqlText.IndexOf("'AcquireLease' {"),
        $sqlText.IndexOf("'BeginMutation' {") - $sqlText.IndexOf("'AcquireLease' {"))
    $beginSql = $sqlText.Substring(
        $sqlText.IndexOf("'BeginMutation' {"),
        $sqlText.IndexOf("'CompleteMutation' {") - $sqlText.IndexOf("'BeginMutation' {"))
    $completeSql = $sqlText.Substring(
        $sqlText.IndexOf("'CompleteMutation' {"),
        $sqlText.IndexOf("'RenewLease' {") - $sqlText.IndexOf("'CompleteMutation' {"))
    $renewSql = $sqlText.Substring(
        $sqlText.IndexOf("'RenewLease' {"),
        $sqlText.IndexOf("'ReleaseLease' {") - $sqlText.IndexOf("'RenewLease' {"))
    $releaseSql = $sqlText.Substring(
        $sqlText.IndexOf("'ReleaseLease' {"),
        $sqlText.IndexOf('    $rows = Invoke-MigrationSqlQuery') -
            $sqlText.IndexOf("'ReleaseLease' {"))
    $guardThrows = @(
        [pscustomobject]@{
            segment = $acquireSql
            number = 51021
            message = 'Migration stage lease operation rejected while an active mutation claim exists.'
            following = 'IF @ExistingAuthorizationEvidenceSha256 IS NOT NULL AND'
        },
        [pscustomobject]@{
            segment = $acquireSql
            number = 51022
            message = 'Migration stage lease rejected authorization-hash replay.'
            following = 'IF @ExistingAuthorizationEvidenceSha256 IS NULL'
        },
        [pscustomobject]@{
            segment = $acquireSql
            number = 51023
            message = 'Migration stage lease is already held and not stale.'
            following = 'COMMIT TRANSACTION;'
        },
        [pscustomobject]@{
            segment = $beginSql
            number = 51024
            message = 'Migration mutation claim rejected stale lease holder, fence token, authorization hash, expiry, or active claim.'
            following = 'COMMIT TRANSACTION;'
        },
        [pscustomobject]@{
            segment = $completeSql
            number = 51025
            message = 'Migration mutation completion rejected a mismatched claim or lease tuple.'
            following = 'COMMIT TRANSACTION;'
        },
        [pscustomobject]@{
            segment = $renewSql
            number = 51026
            message = 'Migration stage lease row is missing.'
            following = 'IF @ObservedActiveMutationId IS NOT NULL OR'
        },
        [pscustomobject]@{
            segment = $renewSql
            number = 51027
            message = 'Migration stage lease operation rejected while an active mutation claim exists.'
            following = 'IF @ObservedAuthorizationEvidenceSha256 <> $authorizationSql OR'
        },
        [pscustomobject]@{
            segment = $renewSql
            number = 51028
            message = 'Migration stage lease fence token, holder, or authorization changed.'
            following = 'IF @ExpiresAtUtc <= @Now'
        },
        [pscustomobject]@{
            segment = $renewSql
            number = 51029
            message = 'Migration stage lease expired before renewal.'
            following = 'IF @ExpiresAtUtc <= DATEADD(SECOND, $renewAfterSeconds, @Now)'
        },
        [pscustomobject]@{
            segment = $releaseSql
            number = 51030
            message = 'Migration stage lease row is missing.'
            following = 'IF @ObservedActiveMutationId IS NOT NULL OR'
        },
        [pscustomobject]@{
            segment = $releaseSql
            number = 51031
            message = 'Migration stage lease operation rejected while an active mutation claim exists.'
            following = 'IF @ObservedAuthorizationEvidenceSha256 <> $authorizationSql OR'
        },
        [pscustomobject]@{
            segment = $releaseSql
            number = 51032
            message = 'Migration stage lease cleanup could not release the current fence token.'
            following = 'IF @ExpiresAtUtc <= @Now'
        },
        [pscustomobject]@{
            segment = $releaseSql
            number = 51033
            message = 'Migration stage lease expired before cleanup completed.'
            following = 'SET @ReleasedAtUtc = @Now;'
        }
    )
    $numberedThrows = @(
        [regex]::Matches(
            $sqlText,
            "(?m)^\s*;THROW (?<number>[0-9]+), '(?<message>[^']+)', 1;\s*$")
    )
    $expectedThrowNumbers = @($guardThrows | ForEach-Object { [int]$_.number })
    $actualThrowNumbers = @(
        $numberedThrows | ForEach-Object { [int]$_.Groups['number'].Value }
    )
    $allGuardThrowsPresent = @(
        $guardThrows | Where-Object {
            $needle = ";THROW $($_.number), '$($_.message)', 1;"
            -not ([string]$_.segment).Contains($needle)
        }
    ).Count -eq 0
    $allGuardThrowsPrecedeFollowingWork = @(
        $guardThrows | Where-Object {
            $needle = ";THROW $($_.number), '$($_.message)', 1;"
            $throwIndex = ([string]$_.segment).IndexOf(
                $needle,
                [StringComparison]::Ordinal)
            $followingIndex = ([string]$_.segment).IndexOf(
                [string]$_.following,
                [StringComparison]::Ordinal)
            $throwIndex -lt 0 -or $followingIndex -le $throwIndex
        }
    ).Count -eq 0

    Assert-MigrationFenceTest 'migration-fence-static-sql-idempotently-adds-durable-claim-columns' (
        $sqlText.Contains('[ActiveMutationId] uniqueidentifier NULL') -and
        $sqlText.Contains('[ActiveMutationStartedAtUtc] datetimeoffset(7) NULL') -and
        $sqlText.Contains("COL_LENGTH(N'dbo.`$tableName', N'ActiveMutationId') IS NULL") -and
        $sqlText.Contains("COL_LENGTH(N'dbo.`$tableName', N'ActiveMutationStartedAtUtc') IS NULL")
    ) 'SQL lease storage does not idempotently add both nullable durable claim columns.'

    Assert-MigrationFenceTest 'migration-fence-static-sql-begin-requires-exact-live-unclaimed-lease-tuple' (
        $beginSql.Contains('AND [Stage] = $stageSql') -and
        $beginSql.Contains('AND [DatabaseTargetFingerprint] = $targetFingerprintSql') -and
        $beginSql.Contains('AND [AuthorizationEvidenceSha256] = $authorizationSql') -and
        $beginSql.Contains('AND [HolderRunId] = $([string]$Holder.holderRunId)') -and
        $beginSql.Contains('AND [HolderRunAttempt] = $([int]$Holder.holderRunAttempt)') -and
        $beginSql.Contains('AND [FenceToken] = $ExpectedFenceToken') -and
        $beginSql.Contains('AND [Released] = 0') -and
        $beginSql.Contains('AND [ExpiresAtUtc] > @Now') -and
        $beginSql.Contains('AND [ActiveMutationId] IS NULL') -and
        $beginSql.Contains('[ActiveMutationId] = @MutationId')
    ) 'BeginMutation SQL is not one atomic exact-tuple claim of a live unclaimed lease.'

    Assert-MigrationFenceTest 'migration-fence-static-sql-complete-requires-exact-claim-and-allows-expiry' (
        $completeSql.Contains('AND [Stage] = $stageSql') -and
        $completeSql.Contains('AND [DatabaseTargetFingerprint] = $targetFingerprintSql') -and
        $completeSql.Contains('AND [AuthorizationEvidenceSha256] = $authorizationSql') -and
        $completeSql.Contains('AND [HolderRunId] = $([string]$Holder.holderRunId)') -and
        $completeSql.Contains('AND [HolderRunAttempt] = $([int]$Holder.holderRunAttempt)') -and
        $completeSql.Contains('AND [FenceToken] = $ExpectedFenceToken') -and
        $completeSql.Contains('AND [ActiveMutationId] = @MutationId') -and
        -not $completeSql.Contains('[ExpiresAtUtc] > @Now') -and
        -not $completeSql.Contains('[ExpiresAtUtc] <= @Now')
    ) 'CompleteMutation SQL is not exact-claim fail-closed or still requires an unexpired lease.'

    Assert-MigrationFenceTest 'migration-fence-static-sql-acquire-renew-release-reject-active-claim' (
        $acquireSql.Contains('@ExistingActiveMutationId IS NOT NULL') -and
        $renewSql.Contains('@ObservedActiveMutationId IS NOT NULL') -and
        $releaseSql.Contains('@ObservedActiveMutationId IS NOT NULL') -and
        $acquireSql.Contains('active mutation claim exists') -and
        $renewSql.Contains('active mutation claim exists') -and
        $releaseSql.Contains('active mutation claim exists')
    ) 'AcquireLease, RenewLease, and ReleaseLease SQL do not all fail closed on an active claim.'

    Assert-MigrationFenceTest 'migration-fence-static-sql-all-guards-use-unique-semicolon-safe-throw' (
        -not $sqlText.Contains('RAISERROR') -and
        $numberedThrows.Count -eq $guardThrows.Count -and
        @($actualThrowNumbers | Sort-Object -Unique).Count -eq $guardThrows.Count -and
        @(Compare-Object $expectedThrowNumbers $actualThrowNumbers).Count -eq 0 -and
        $allGuardThrowsPresent
    ) 'Lease SQL contains RAISERROR or lacks an exact uniquely numbered semicolon-safe THROW guard.'

    Assert-MigrationFenceTest 'migration-fence-static-sql-throws-before-subsequent-work' (
        $allGuardThrowsPrecedeFollowingWork
    ) 'A lease SQL guard can reach subsequent mutation or COMMIT work before its terminating THROW.'

    Assert-MigrationFenceTest 'migration-fence-static-removes-assert-fence-check-then-execute' (
        -not $commonText.Contains('AssertFence') -and
        -not $bundleText.Contains('AssertFence') -and
        $commonText.Contains('-Mode BeginMutation') -and
        $commonText.Contains('-Mode CompleteMutation') -and
        $commonText.IndexOf('-Mode BeginMutation') -lt
            $commonText.IndexOf('$mutationResult = & $MutationExecutor') -and
        $commonText.IndexOf('-Mode CompleteMutation') -gt
            $commonText.IndexOf('$mutationResult = & $MutationExecutor')
    ) 'The Apply wrapper still uses check-then-execute fencing instead of begin/complete mutation claims.'
    Assert-MigrationFenceTest 'migration-fence-static-uses-typed-sqlclient-adapter-without-retry' (
        $sqlText.Contains('$rows = Invoke-MigrationSqlQuery') -and
        $sqlText.Contains('-ExpectedColumnCount 7') -and
        $sqlText.Contains('-CommandTimeoutSeconds 60') -and
        -not $sqlText.Contains('sqlcmd') -and
        -not $sqlText.Contains('Start-Sleep') -and
        -not $sqlText.Contains('Retry')
    ) 'Lease SQL does not use the typed seven-column adapter exactly once without retry.'

    $allMarkers = @(Get-Content -LiteralPath $mutationMarkerPath)
    $mutationMarkerCount = $allMarkers.Count
    $oldMarkerCount = @($allMarkers | Where-Object { $_ -like 'old:*' }).Count
    $currentMarkerCount = @($allMarkers | Where-Object { $_ -like 'current:*' }).Count
    $failureMarkerCount = @($allMarkers | Where-Object { $_ -like 'failure:*' }).Count
}
finally {
    Remove-Variable -Scope Global -Name T21LeaseActionsSeamGuard `
        -ErrorAction SilentlyContinue
    [Environment]::SetEnvironmentVariable(
        'T21_MIGRATION_STAGE_LEASE_STATE_PATH',
        $previousStatePath)
    [Environment]::SetEnvironmentVariable('T21_STAGE_LEASE_MUTATION_ID', $previousMutationId)
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Output (
    "SUMMARY migration-stage-lease-fence total=$($passed + $failed) passed=$passed failed=$failed " +
    "mutationMarkers=$mutationMarkerCount oldMutationMarkers=$oldMarkerCount " +
    "currentMutationMarkers=$currentMarkerCount failureMutationMarkers=$failureMarkerCount " +
    "staleReleasePreserved=$staleReleasePreserved " +
    'auth=0 deployments=0 database=0 resources=0')
if ($failed -gt 0) { exit 1 }
