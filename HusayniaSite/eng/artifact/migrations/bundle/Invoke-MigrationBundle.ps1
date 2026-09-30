[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Preflight', 'AcquireLease', 'Apply', 'RenewLease', 'ReleaseLease')]
    [string]$Mode,
    [Parameter(Mandatory = $true)]
    [ValidateSet('Development', 'Staging', 'Production')]
    [string]$Stage,
    [Parameter(Mandatory = $true)]
    [string]$ReleaseVerifiedProvenancePath,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ExpectedProtectedBundleSha256,
    [object]$ProducerRunBinding,
    [Parameter(Mandatory = $true)]
    [string]$ServerName,
    [Parameter(Mandatory = $true)]
    [string]$DatabaseName,
    [Parameter(Mandatory = $true)]
    [string]$StageTargetMetadataPath,
    [Parameter(Mandatory = $true)]
    [string]$PolicyPath,
    [Parameter(Mandatory = $true)]
    [string]$EvidencePath,
    [string]$LeaseContextPath,
    [string]$BackupEvidencePath,
    [string]$PreflightVerifiedProvenancePath,
    [string]$MigrationAuthorizationVerifiedProvenancePath,
    [string]$AuthorizedActorIdAllowlist,
    [string]$ConnectionStringEnvironmentVariable = 'HUSAYNIA_MIGRATION_CONNECTION',
    [string]$IdentityModeEnvironmentVariable = 'T21_MIGRATION_IDENTITY_MODE',
    [switch]$AllowApply,
    [switch]$DryRun,
    [switch]$EnableLocalTestSeams,
    [scriptblock]$LocalAccessTokenProvider,
    [scriptblock]$LocalSqlExecutor
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Migration.Common.ps1')
Assert-MigrationLocalTestSeams -EnableLocalTestSeams:$EnableLocalTestSeams `
    -LocalAccessTokenProvider $LocalAccessTokenProvider `
    -LocalSqlExecutor $LocalSqlExecutor

if ($ServerName -notmatch '^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.database\.windows\.net$' -or
    $DatabaseName -notmatch '^husaynia-(dev|stg|prd)$') {
    throw 'Migration target names contain prohibited characters, SQLCMD expressions, or unsupported target syntax.'
}
if (-not (Test-Path -LiteralPath $PolicyPath -PathType Leaf)) {
    throw 'Reviewed promotion policy is missing.'
}

$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
$stagePolicy = @($policy.stages | Where-Object { $_.name -eq $Stage })
if ($stagePolicy.Count -ne 1) {
    throw "Promotion policy must contain exactly one $Stage stage."
}
$stagePolicy = $stagePolicy[0]
if ($Stage -eq 'Production' -and [bool]$stagePolicy.deploymentEnabled -ne $true) {
    throw 'Production migration operations are unreachable while Production deploymentEnabled is false.'
}
if ([bool]$stagePolicy.deploymentEnabled -ne $true) {
    throw "Migration $Mode denied before database authentication or mutation: $Stage deploymentEnabled=false."
}
if ($IdentityModeEnvironmentVariable -notmatch '^[A-Z][A-Z0-9_]{2,127}$') {
    throw 'Migration identity-mode environment variable name is unsafe.'
}
$expectedIdentityMode = if ($Mode -eq 'Preflight') { 'readonly' } else { 'apply' }
if ([Environment]::GetEnvironmentVariable($IdentityModeEnvironmentVariable) -cne $expectedIdentityMode) {
    throw "Migration $Mode requires the distinct $expectedIdentityMode identity mode."
}
if ($Mode -in @('Preflight', 'AcquireLease', 'Apply') -and
    (Test-Path -LiteralPath $EvidencePath)) {
    Remove-Item -LiteralPath $EvidencePath -Force
}
$provenance = $policy.provenance
$preflightPolicy = $policy.migration.preflight
Assert-MigrationExactProperties -Object $preflightPolicy -Label 'Migration preflight policy' -Expected @(
    'queryContract',
    'repositorySqlExecutionAllowed',
    'requiredIdentityMode',
    'requiredDatabasePermission',
    'prohibitedDatabasePermissions'
)
if ([string]$preflightPolicy.queryContract -cne 'fixed-read-only-metadata-v1' -or
    $preflightPolicy.repositorySqlExecutionAllowed -ne $false -or
    [string]$preflightPolicy.requiredIdentityMode -cne 'readonly' -or
    [string]$preflightPolicy.requiredDatabasePermission -cne 'SELECT' -or
    (@($preflightPolicy.prohibitedDatabasePermissions) -join ',') -cne
        'ALTER,CONTROL,CREATE TABLE,DELETE,INSERT,UPDATE') {
    throw 'Migration preflight policy does not enforce the fixed read-only metadata query contract.'
}
$lockPolicy = $policy.migration.lock
Assert-MigrationExactProperties -Object $lockPolicy -Label 'Migration lock policy' -Expected @(
    'provider',
    'mode',
    'owner',
    'timeoutMilliseconds',
    'resourcePrefix'
)
if ([string]$lockPolicy.provider -cne 'sqlserver-sp-getapplock-session-v1' -or
    [string]$lockPolicy.mode -cne 'Exclusive' -or
    [string]$lockPolicy.owner -cne 'Session' -or
    [int]$lockPolicy.timeoutMilliseconds -ne 0 -or
    [string]$lockPolicy.resourcePrefix -cne 'husaynia-migration') {
    throw 'Migration lock policy does not require the reviewed database-side exclusive session lock.'
}
$leasePolicy = Get-MigrationApplyLeasePolicy -Policy $policy
$targetMetadata = Read-MigrationTargetMetadata -Path $StageTargetMetadataPath -Stage $Stage -StagePolicy $stagePolicy
if ($ServerName -cne [string]$targetMetadata.sqlServerFqdn -or
    $DatabaseName -cne [string]$targetMetadata.sqlDatabaseName) {
    throw 'Migration target does not exactly match the immutable protected stage target metadata.'
}

$releaseVerified = Assert-MigrationVerifiedArtifact `
    -VerifiedProvenancePath $ReleaseVerifiedProvenancePath `
    -ExpectedRole release-c6 `
    -Stage $Stage `
    -Policy $policy
$artifact = $releaseVerified.artifactRoot
$manifestPath = Join-Path $artifact 'release\release-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'Verified release manifest is missing.'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$bundlePolicy = Get-ApplicationMigrationBundlePolicy -Policy $policy `
    -RequireMaterialized:($Mode -ne 'Preflight')
$bundleRelativePath = [string]$bundlePolicy.relativePath
$bundleFormat = [string]$bundlePolicy.format
$bundlePolicySha256 = [string]$bundlePolicy.sha256
$bundleMaterialized = Test-MigrationSha256 $bundlePolicySha256
$protectedBundleRelativePath = [string]$policy.trustedExecutionContract.bundlePath
$protectedBundleSha256 = $ExpectedProtectedBundleSha256.ToLowerInvariant()
if ($protectedBundleRelativePath -cne 'operations/protected-execution-bundle.zip') {
    throw 'Trusted execution policy does not bind the exact protected-execution bundle path.'
}
Assert-MigrationOrchestrationFiles -ArtifactRoot $artifact -Policy $policy -Manifest $manifest
$appEntry = @($manifest.files | Where-Object { $_.path -eq 'app/Husaynia.Web.zip' })
$preflightEntry = @($manifest.files | Where-Object { $_.path -eq 'migrations/sql/000-preflight.sql' })
$bundleEntry = @($manifest.files | Where-Object { $_.path -ceq $bundleRelativePath })
$protectedBundleEntry = @($manifest.files | Where-Object {
        $_.path -ceq $protectedBundleRelativePath
    })
if ($appEntry.Count -ne 1 -or $preflightEntry.Count -ne 1 -or
    $protectedBundleEntry.Count -ne 1 -or
    [string]$protectedBundleEntry[0].sha256 -cne $protectedBundleSha256) {
    throw 'Release manifest does not bind the application, preflight SQL, and protected bundle exactly once.'
}
if ($bundleMaterialized -and
    ($bundleEntry.Count -ne 1 -or
     [string]$bundleEntry[0].sha256 -cne $bundlePolicySha256)) {
    throw 'Release manifest migration bundle checksum does not match immutable policy.'
}

$appPath = Join-Path $artifact 'app\Husaynia.Web.zip'
$bundlePath = Join-Path $artifact $bundleRelativePath
$protectedBundlePath = Join-Path $artifact $protectedBundleRelativePath
if (-not $bundleMaterialized -and
    ($bundleEntry.Count -ne 0 -or (Test-Path -LiteralPath $bundlePath))) {
    throw 'An application migration bundle exists without a reviewed immutable policy checksum.'
}
$trustedBundleRoot = [Environment]::GetEnvironmentVariable('T21_TRUSTED_BUNDLE_ROOT')
$sqlRuntimeBundleRoot = ''
if ([string]::IsNullOrWhiteSpace($trustedBundleRoot)) {
    $preflightPath = Join-Path $artifact 'migrations\sql\000-preflight.sql'
}
else {
    $resolvedTrustedBundleRoot = (Resolve-Path -LiteralPath $trustedBundleRoot).Path
    $sqlRuntimeBundleRoot = $resolvedTrustedBundleRoot
    $preflightPath = Join-Path $resolvedTrustedBundleRoot 'eng\artifact\migrations\sql\000-preflight.sql'
}
foreach ($binding in @(
    @{ Path = $appPath; Entry = $appEntry[0]; Name = 'application' },
    @{ Path = $preflightPath; Entry = $preflightEntry[0]; Name = 'preflight SQL' },
    @{
        Path = $protectedBundlePath
        Entry = $protectedBundleEntry[0]
        Name = 'protected execution bundle'
    }
)) {
    if (-not (Test-Path -LiteralPath $binding.Path -PathType Leaf) -or
        (Get-MigrationSha256 -Path $binding.Path) -ne [string]$binding.Entry.sha256) {
        throw "Release-manifest checksum validation failed for $($binding.Name)."
    }
}
if ($bundleMaterialized -and
    (-not (Test-Path -LiteralPath $bundlePath -PathType Leaf) -or
     (Get-MigrationSha256 -Path $bundlePath) -ne [string]$bundleEntry[0].sha256)) {
    throw 'Release-manifest checksum validation failed for migration bundle.'
}

$releaseCommitSha = ([string]$manifest.commitSha).ToLowerInvariant()
$artifactSha256 = [string]$appEntry[0].sha256
$releaseManifestSha256 = Get-MigrationSha256 -Path $manifestPath
$bundleSha256 = if ($bundleMaterialized) {
    [string]$bundleEntry[0].sha256
}
else {
    $null
}
if ([string]$releaseVerified.provenance.run.commitSha -cne $releaseCommitSha -or
    [string]$releaseVerified.provenance.contentManifestSha256 -cne $releaseManifestSha256) {
    throw 'Migration release verified provenance does not bind the release manifest and immutable commit.'
}
$targetMetadataSha256 = Get-MigrationSha256 -Path $StageTargetMetadataPath
$targetFingerprint = Get-MigrationTargetFingerprint -TargetMetadata $targetMetadata
$lockResource = "$([string]$lockPolicy.resourcePrefix)-$($Stage.ToLowerInvariant())-$targetFingerprint"
$operationRun = $null
if ($Mode -eq 'Preflight') {
    if ($null -eq $ProducerRunBinding) {
        throw 'Migration Preflight requires the signed in-memory producer run binding.'
    }
    $operationRun = Assert-MigrationProducerRunBinding `
        -Binding $ProducerRunBinding `
        -Stage $Stage `
        -Policy $policy
}
else {
    if ([string]::IsNullOrWhiteSpace($PreflightVerifiedProvenancePath) -or
        [string]::IsNullOrWhiteSpace($MigrationAuthorizationVerifiedProvenancePath)) {
        throw "Migration $Mode requires resolver-created preflight and migration authorization verified provenance."
    }
    $preflightVerified = Assert-MigrationVerifiedArtifact `
        -VerifiedProvenancePath $PreflightVerifiedProvenancePath `
        -ExpectedRole trusted-preflight `
        -Stage $Stage `
        -ApplicationSha256 $artifactSha256 `
        -BundleSha256 $protectedBundleSha256 `
        -Policy $policy
    $preflightEvidencePath = Join-Path $preflightVerified.artifactRoot 'migration-preflight.json'
    if (-not (Test-Path -LiteralPath $preflightEvidencePath -PathType Leaf) -or
        -not (Test-MigrationPathWithinDirectory -BasePath $preflightVerified.artifactRoot `
                -Path $preflightEvidencePath)) {
        throw 'Migration resolver-owned preflight evidence record is missing or escaped its verified artifact root.'
    }
    $preflight = Get-Content -LiteralPath $preflightEvidencePath -Raw | ConvertFrom-Json -DateKind String
    $operationRun = Assert-MigrationProducerRunBinding `
        -Binding $preflight.sourceRun `
        -Stage $Stage `
        -Policy $policy `
        -ExpectedTopLevelCallerWorkflowRef ([string]$preflightVerified.provenance.run.workflowRef) `
        -ExpectedProducerWorkflowRef ([string]$preflightVerified.producerRunBinding.producerWorkflowRef)
    if ([string]$operationRun.runId -cne [string]$preflightVerified.provenance.run.runId -or
        [int]$operationRun.runAttempt -ne [int]$preflightVerified.provenance.run.runAttempt -or
        [string]$operationRun.commitSha -cne [string]$preflightVerified.provenance.run.commitSha) {
        throw 'Migration preflight evidence producer run binding does not match resolver-verified provenance.'
    }
    $preflightEvidenceSha256 = Get-MigrationSha256 -Path $preflightEvidencePath
    $authorizationVerified = Assert-MigrationVerifiedArtifact `
        -VerifiedProvenancePath $MigrationAuthorizationVerifiedProvenancePath `
        -ExpectedRole migration-authorization `
        -Stage $Stage `
        -ApplicationSha256 $artifactSha256 `
        -BundleSha256 $protectedBundleSha256 `
        -PreflightEvidenceSha256 $preflightEvidenceSha256 `
        -Policy $policy
    $applyAuthorizationPath = Join-Path $authorizationVerified.artifactRoot 'migration-apply-authorization.json'
    if (-not (Test-Path -LiteralPath $applyAuthorizationPath -PathType Leaf) -or
        -not (Test-MigrationPathWithinDirectory -BasePath $authorizationVerified.artifactRoot `
                -Path $applyAuthorizationPath)) {
        throw 'Migration resolver-owned authorization record is missing or escaped its verified artifact root.'
    }
    $authorization = Get-Content -LiteralPath $applyAuthorizationPath -Raw | ConvertFrom-Json -DateKind String
    $authorizationEvidenceSha256 = Get-MigrationSha256 -Path $applyAuthorizationPath
}
$leaseHolder = Get-MigrationStageLeaseHolder -ProducerRunBinding $operationRun
$leaseResource = Get-MigrationStageLeaseResource -LeasePolicy $leasePolicy -Stage $Stage -TargetFingerprint $targetFingerprint
$observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
$evidenceBundlePath = if ($Mode -eq 'Preflight') {
    $protectedBundleRelativePath
}
else {
    $bundleRelativePath
}
$evidenceBundleSha256 = if ($Mode -eq 'Preflight') {
    $protectedBundleSha256
}
else {
    $bundleSha256
}
$evidenceBundleFormat = if ($Mode -eq 'Preflight') {
    'protected-execution-bundle-v1.1.0'
}
else {
    $bundleFormat
}

if ($DryRun) {
    Write-MigrationJson -Value ([ordered]@{
        schemaVersion = '1.0.0'
        evidenceType = if ($Mode -eq 'Preflight') { 'migration-preflight' } else { 'migration-apply' }
        status = 'DRY_RUN'
        stage = $Stage
        artifactSha256 = $artifactSha256
        releaseManifestSha256 = $releaseManifestSha256
        releaseVersion = [string]$manifest.version
        releaseCommitSha = $releaseCommitSha
        observedAtUtc = $observedAtUtc
        sourceRun = $operationRun
        operation = [ordered]@{
            mode = $Mode
            operationOrder = if ($Mode -eq 'Preflight') { 10 } else { 30 }
            bundlePath = $evidenceBundlePath
            bundleSha256 = $evidenceBundleSha256
            bundleFormat = $evidenceBundleFormat
            preflightSqlSha256 = [string]$preflightEntry[0].sha256
            stageTargetMetadataSha256 = $targetMetadataSha256
            databaseTargetFingerprint = $targetFingerprint
            sqlServerName = [string]$targetMetadata.sqlServerName
            sqlServerFqdn = [string]$targetMetadata.sqlServerFqdn
            sqlDatabaseName = [string]$targetMetadata.sqlDatabaseName
            sqlServerResourceId = [string]$targetMetadata.sqlServerResourceId
            sqlDatabaseResourceId = [string]$targetMetadata.sqlDatabaseResourceId
            databaseInvocationExecuted = $false
            identityMode = $expectedIdentityMode
            repositorySqlExecuted = $false
            migrationLock = [ordered]@{
                provider = [string]$lockPolicy.provider
                resource = $lockResource
                acquired = $false
            }
        }
    }) -Path $EvidencePath
    Write-Output "MIGRATION-BUNDLE mode=$Mode status=DRY_RUN stage=$Stage target=$targetFingerprint databaseInvocationExecuted=false"
    return
}

if ($Mode -eq 'Preflight') {
    $preflightQuery = @'
SET NOCOUNT ON;
SELECT
    DB_NAME(),
    CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')),
    CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),
    CONVERT(nvarchar(60), DATABASEPROPERTYEX(DB_NAME(), 'Status')),
    HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'SELECT'),
    HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER'),
    HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CONTROL'),
    HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE'),
    HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'DELETE'),
    HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'INSERT'),
    HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'UPDATE');
'@
    Write-Output "MIGRATION-BUNDLE mode=Preflight stage=$Stage target=$targetFingerprint sqlSha256=$($preflightEntry[0].sha256)"
    $rows = Invoke-MigrationSqlQuery `
        -ServerName $ServerName `
        -DatabaseName $DatabaseName `
        -Query $preflightQuery `
        -ExpectedColumnCount 11 `
        -CommandTimeoutSeconds 60 `
        -BundleRoot $sqlRuntimeBundleRoot `
        -ExpectedApplicationSha256 $artifactSha256 `
        -EnableLocalTestSeams:$EnableLocalTestSeams `
        -LocalAccessTokenProvider $LocalAccessTokenProvider `
        -LocalSqlExecutor $LocalSqlExecutor
    $identity = @($rows[0] | ForEach-Object {
        if ($null -eq $_) { '' } else { ([string]$_).Trim() }
    })
    $validatedIdentity = Assert-MigrationPreflightSqlRow `
        -Row $identity `
        -TargetMetadata $targetMetadata
    $observedProductVersion = [string]$validatedIdentity.ProductVersion
    $observedDatabaseStatus = [string]$validatedIdentity.DatabaseStatus

    Write-MigrationJson -Value ([ordered]@{
        schemaVersion = '1.0.0'
        evidenceType = 'migration-preflight'
        status = 'PASS'
        stage = $Stage
        artifactSha256 = $artifactSha256
        releaseManifestSha256 = $releaseManifestSha256
        releaseVersion = [string]$manifest.version
        releaseCommitSha = $releaseCommitSha
        observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        sourceRun = $operationRun
        operation = [ordered]@{
            mode = 'Preflight'
            operationOrder = 10
            nonMutating = $true
            executed = $true
            identityMode = 'readonly'
            readOnlyPrincipalVerified = $true
            mutationPermissionsDenied = $true
            repositorySqlExecuted = $false
            queryContract = [string]$preflightPolicy.queryContract
            bundlePath = $protectedBundleRelativePath
            bundleSha256 = $protectedBundleSha256
            bundleFormat = 'protected-execution-bundle-v1.1.0'
            preflightSqlSha256 = [string]$preflightEntry[0].sha256
            stageTargetMetadataSha256 = $targetMetadataSha256
            databaseTargetFingerprint = $targetFingerprint
            sqlServerName = [string]$targetMetadata.sqlServerName
            sqlServerFqdn = [string]$targetMetadata.sqlServerFqdn
            sqlDatabaseName = [string]$targetMetadata.sqlDatabaseName
            sqlServerResourceId = [string]$targetMetadata.sqlServerResourceId
            sqlDatabaseResourceId = [string]$targetMetadata.sqlDatabaseResourceId
            databaseStatus = $observedDatabaseStatus
            serverProductVersion = $observedProductVersion
            checks = @(
                'actual-database-identity',
                'actual-server-identity',
                'immutable-stage-target',
                'database-online',
                'server-product-version',
                'select-permission',
                'mutation-permissions-denied',
                'fixed-query-no-repository-sql'
            )
            databaseInvocationExecuted = $true
        }
    }) -Path $EvidencePath
    return
}

if ($Mode -in @('AcquireLease', 'Apply', 'RenewLease', 'ReleaseLease') -and -not $AllowApply) {
    throw "Migration $Mode is denied without the explicit -AllowApply switch."
}

$backup = $null
$leaseContext = $null
$authorizedActorIds = $null

if ($Mode -in @('AcquireLease', 'Apply')) {
    if ([string]::IsNullOrWhiteSpace($AuthorizedActorIdAllowlist)) {
        throw "Migration $Mode requires the protected immutable authorizer actor ID allowlist."
    }

    if ($Mode -eq 'Apply') {
        if ([string]::IsNullOrWhiteSpace($BackupEvidencePath) -or
            -not (Test-Path -LiteralPath $BackupEvidencePath -PathType Leaf)) {
            throw 'Migration Apply requires the reviewed backup evidence.'
        }
        $backup = Get-Content -LiteralPath $BackupEvidencePath -Raw | ConvertFrom-Json
        $backupSourceRun = Assert-MigrationProducerRunBinding `
            -Binding $backup.sourceRun `
            -Stage $Stage `
            -Policy $policy `
            -ExpectedTopLevelCallerWorkflowRef ([string]$operationRun.topLevelCallerWorkflowRef) `
            -ExpectedProducerWorkflowRef ([string]$operationRun.producerWorkflowRef)
        if ([string]$backupSourceRun.runId -cne [string]$operationRun.runId -or
            [int]$backupSourceRun.runAttempt -ne [int]$operationRun.runAttempt -or
            [string]$backupSourceRun.commitSha -cne [string]$operationRun.commitSha) {
            throw 'Backup evidence producer run binding does not match resolver-verified preflight provenance.'
        }
    }

    $preflightObserved = [DateTimeOffset]::MinValue
    $now = [DateTimeOffset]::UtcNow
    if ([string]$preflight.schemaVersion -ne '1.0.0' -or
        [string]$preflight.evidenceType -ne 'migration-preflight' -or
        [string]$preflight.status -ne 'PASS' -or
        [string]$preflight.stage -ne $Stage -or
        [string]$preflight.artifactSha256 -ne $artifactSha256 -or
        [string]$preflight.releaseManifestSha256 -ne $releaseManifestSha256 -or
        [string]$preflight.releaseCommitSha -ne $releaseCommitSha -or
        [string]$preflight.operation.mode -ne 'Preflight' -or
        -not (Test-MigrationJsonInteger -Value $preflight.operation.operationOrder -Minimum 10 -Maximum 10) -or
        $preflight.operation.nonMutating -ne $true -or
        $preflight.operation.executed -ne $true -or
        [string]$preflight.operation.identityMode -ne 'readonly' -or
        $preflight.operation.readOnlyPrincipalVerified -ne $true -or
        $preflight.operation.mutationPermissionsDenied -ne $true -or
        $preflight.operation.repositorySqlExecuted -ne $false -or
        [string]$preflight.operation.queryContract -ne [string]$preflightPolicy.queryContract -or
        [string]$preflight.operation.bundlePath -ne $protectedBundleRelativePath -or
        [string]$preflight.operation.bundleSha256 -ne $protectedBundleSha256 -or
        [string]$preflight.operation.bundleFormat -ne 'protected-execution-bundle-v1.1.0' -or
        [string]$preflight.operation.preflightSqlSha256 -ne [string]$preflightEntry[0].sha256 -or
        [string]$preflight.operation.stageTargetMetadataSha256 -ne $targetMetadataSha256 -or
        [string]$preflight.operation.databaseTargetFingerprint -ne $targetFingerprint -or
        [string]$preflight.operation.sqlServerFqdn -ne [string]$targetMetadata.sqlServerFqdn -or
        [string]$preflight.operation.sqlDatabaseName -ne [string]$targetMetadata.sqlDatabaseName -or
        [string]$preflight.operation.sqlServerResourceId -ne [string]$targetMetadata.sqlServerResourceId -or
        [string]$preflight.operation.sqlDatabaseResourceId -ne [string]$targetMetadata.sqlDatabaseResourceId -or
        [string]$preflight.operation.databaseStatus -ne 'ONLINE' -or
        -not [DateTimeOffset]::TryParse([string]$preflight.observedAtUtc, [ref]$preflightObserved) -or
        $preflightObserved -gt $now.AddMinutes([int]$provenance.maxClockSkewMinutes) -or
        $preflightObserved -lt $now.AddMinutes(-[int]$stagePolicy.maxPreflightAgeMinutes)) {
        throw "Migration $Mode requires a fresh successful preflight bound to this release, bundle, exact target, stage, and trusted source run."
    }

    if ($Mode -eq 'Apply' -and
        ([string]$backup.schemaVersion -ne '1.0.0' -or
         [string]$backup.evidenceType -ne 'backup' -or
         [string]$backup.status -ne 'PASS' -or
         [string]$backup.stage -ne $Stage -or
         [string]$backup.artifactSha256 -ne $artifactSha256 -or
         [string]$backup.releaseManifestSha256 -ne $releaseManifestSha256 -or
         [string]$backup.releaseCommitSha -ne $releaseCommitSha -or
         [string]$backup.operation.stageTargetMetadataSha256 -ne $targetMetadataSha256 -or
         [string]$backup.operation.databaseTargetFingerprint -ne $targetFingerprint -or
         $backup.operation.restorable -ne $true -or
         $backup.operation.integrityVerified -ne $true)) {
        throw 'Backup evidence is not bound to this release, stage metadata, and exact database target.'
    }

    Assert-MigrationExactProperties -Object $authorization `
        -Label 'Migration authorization record' -Expected @(
            'schemaVersion', 'decision', 'mode', 'operationOrder', 'stage', 'environment',
            'applicationSha256', 'releaseManifestSha256', 'releaseCommitSha',
            'sourceRelease', 'releaseBinding', 'bundlePath', 'bundleSha256',
            'targetFingerprint', 'preflightEvidenceSha256', 'preflightProducerRun',
            'producerBindings', 'authorizedExecution', 'authorizedByActor',
            'authorizedByActorId', 'issuedAtUtc', 'expiresAtUtc',
            'productionCtoAuthorization'
        )
    Assert-MigrationExactProperties -Object $authorization.releaseBinding `
        -Label 'Migration authorization release binding' -Expected @(
            'runId', 'applicationSha256', 'bundleSha256', 'manifestSha256', 'commitSha'
        )
    Assert-MigrationExactProperties -Object $authorization.authorizedExecution `
        -Label 'Migration authorization execution' -Expected @(
            'topLevelCallerWorkflowRef', 'producerWorkflowRef'
        )
    Assert-MigrationExactProperties -Object $authorization.producerBindings `
        -Label 'Migration authorization producer bindings' -Expected @(
            'preflight', 'preparedInputs'
        )
    Assert-MigrationExactProperties -Object $authorization.producerBindings.preflight `
        -Label 'Migration authorization preflight producer binding' -Expected @(
            'runId', 'artifactSha256', 'contentManifestSha256'
        )
    Assert-MigrationExactProperties -Object $authorization.producerBindings.preparedInputs `
        -Label 'Migration authorization prepared-input producer binding' -Expected @(
            'runId', 'artifactSha256', 'contentManifestSha256'
        )
    Assert-MigrationRecordRunProvenanceBinding `
        -Binding $authorization.sourceRelease `
        -VerifiedProvenance $releaseVerified.provenance `
        -Label 'authorization source release run'
    Assert-MigrationRecordRunProvenanceBinding `
        -Binding $authorization.preflightProducerRun `
        -VerifiedProvenance $preflightVerified.provenance `
        -Label 'authorization source preflight run'

    $authorizationIssued = [DateTimeOffset]::MinValue
    $authorizationExpiry = [DateTimeOffset]::MinValue
    $authorizedActorIds = ConvertTo-MigrationActorIdSet -Value $AuthorizedActorIdAllowlist
    if ([string]$authorization.schemaVersion -ne '2.1.0' -or
        [string]$authorization.decision -ne 'AUTHORIZE' -or
        [string]$authorization.mode -ne 'Apply' -or
        -not (Test-MigrationJsonInteger -Value $authorization.operationOrder -Minimum 20 -Maximum 20) -or
        [string]$authorization.stage -ne $Stage -or
        [string]$authorization.environment -ne [string]$stagePolicy.githubEnvironment -or
        [string]$authorization.artifactSha256 -ne $artifactSha256 -or
        [string]$authorization.releaseManifestSha256 -ne $releaseManifestSha256 -or
        [string]$authorization.releaseCommitSha -ne $releaseCommitSha -or
        [string]$authorization.releaseBinding.runId -ne
            [string]$releaseVerified.provenance.run.runId -or
        [string]$authorization.releaseBinding.applicationSha256 -ne $artifactSha256 -or
        [string]$authorization.releaseBinding.bundleSha256 -ne $protectedBundleSha256 -or
        [string]$authorization.releaseBinding.manifestSha256 -ne $releaseManifestSha256 -or
        [string]$authorization.releaseBinding.commitSha -ne $releaseCommitSha -or
        [string]$authorization.bundlePath -ne $protectedBundleRelativePath -or
        [string]$authorization.bundleSha256 -ne $protectedBundleSha256 -or
        [string]$authorization.targetFingerprint -ne $targetFingerprint -or
        [string]$authorization.preflightEvidenceSha256 -ne $preflightEvidenceSha256 -or
        [string]$authorization.authorizedExecution.topLevelCallerWorkflowRef -ne
            [string]$operationRun.topLevelCallerWorkflowRef -or
        [string]$authorization.authorizedExecution.producerWorkflowRef -ne
            [string]$operationRun.producerWorkflowRef -or
        [string]$authorization.producerBindings.preflight.runId -ne
            [string]$preflightVerified.provenance.run.runId -or
        -not $authorizedActorIds.Contains([string]$authorization.authorizedByActorId) -or
        [string]$authorization.authorizedByActor -notmatch
            '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$' -or
        -not [DateTimeOffset]::TryParse([string]$authorization.issuedAtUtc, [ref]$authorizationIssued) -or
        -not [DateTimeOffset]::TryParse([string]$authorization.expiresAtUtc, [ref]$authorizationExpiry) -or
        $authorizationIssued -lt $preflightObserved -or
        $authorizationIssued -gt $now.AddMinutes([int]$provenance.maxClockSkewMinutes) -or
        $authorizationExpiry -le $now -or
        $authorizationExpiry -le $authorizationIssued -or
        ($Stage -eq 'Production' -and $null -eq $authorization.productionCtoAuthorization) -or
        ($Stage -ne 'Production' -and $null -ne $authorization.productionCtoAuthorization)) {
        throw "Migration $Mode authorization is invalid, expired, locally forgeable, or not bound to the release, target, preflight, and authorized immutable actor."
    }
}

if ($Mode -eq 'AcquireLease') {
    $leaseContext = Invoke-MigrationStageLeaseOperation `
        -Mode AcquireLease `
        -ServerName $ServerName `
        -DatabaseName $DatabaseName `
        -LeasePolicy $leasePolicy `
        -Resource $leaseResource `
        -Stage $Stage `
        -TargetFingerprint $targetFingerprint `
        -AuthorizationEvidenceSha256 $authorizationEvidenceSha256 `
        -Holder $leaseHolder `
        -BundleRoot $sqlRuntimeBundleRoot `
        -ExpectedApplicationSha256 $artifactSha256 `
        -EnableLocalTestSeams:$EnableLocalTestSeams `
        -LocalAccessTokenProvider $LocalAccessTokenProvider `
        -LocalSqlExecutor $LocalSqlExecutor
    Write-MigrationStageLeaseContext -LeaseContext $leaseContext -Path $EvidencePath
    Write-Output "MIGRATION-BUNDLE mode=AcquireLease status=PASS stage=$Stage target=$targetFingerprint fenceToken=$($leaseContext.fenceToken)"
    return
}

if ($Mode -in @('Apply', 'RenewLease', 'ReleaseLease')) {
    if ([string]::IsNullOrWhiteSpace($LeaseContextPath) -or
        -not (Test-Path -LiteralPath $LeaseContextPath -PathType Leaf)) {
        throw "Migration $Mode requires the durable stage lease context from AcquireLease."
    }
}

if ($Mode -in @('Apply', 'RenewLease', 'ReleaseLease')) {
    $expectedAuthorizationEvidenceSha256 = if ($Mode -eq 'Apply') {
        $authorizationEvidenceSha256
    }
    else {
        ''
    }
    $leaseContext = Read-MigrationStageLeaseContext `
        -Path $LeaseContextPath `
        -ExpectedStage $Stage `
        -ExpectedTargetFingerprint $targetFingerprint `
        -ExpectedAuthorizationEvidenceSha256 $expectedAuthorizationEvidenceSha256 `
        -ExpectedResource $leaseResource
}

if ($Mode -eq 'RenewLease') {
    if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) {
        throw 'Migration RenewLease requires existing migration-apply evidence to update the durable lease receipt.'
    }
    $renewedLeaseContext = Invoke-MigrationStageLeaseOperation `
        -Mode RenewLease `
        -ServerName $ServerName `
        -DatabaseName $DatabaseName `
        -LeasePolicy $leasePolicy `
        -Resource $leaseResource `
        -Stage $Stage `
        -TargetFingerprint $targetFingerprint `
        -AuthorizationEvidenceSha256 ([string]$leaseContext.authorizationEvidenceSha256) `
        -Holder ([ordered]@{
            holderRunId = [string]$leaseContext.holderRunId
            holderRunAttempt = [int]$leaseContext.holderRunAttempt
        }) `
        -ExpectedFenceToken ([int64]$leaseContext.fenceToken) `
        -BundleRoot $sqlRuntimeBundleRoot `
        -ExpectedApplicationSha256 $artifactSha256 `
        -EnableLocalTestSeams:$EnableLocalTestSeams `
        -LocalAccessTokenProvider $LocalAccessTokenProvider `
        -LocalSqlExecutor $LocalSqlExecutor
    Write-MigrationStageLeaseContext -LeaseContext $renewedLeaseContext -Path $LeaseContextPath
    Update-MigrationApplyEvidenceStageLease -EvidencePath $EvidencePath -LeaseContext $renewedLeaseContext
    Write-Output "MIGRATION-BUNDLE mode=RenewLease status=PASS stage=$Stage target=$targetFingerprint fenceToken=$($renewedLeaseContext.fenceToken)"
    return
}

if ($Mode -eq 'ReleaseLease') {
    $releasedLeaseContext = Invoke-MigrationStageLeaseOperation `
        -Mode ReleaseLease `
        -ServerName $ServerName `
        -DatabaseName $DatabaseName `
        -LeasePolicy $leasePolicy `
        -Resource $leaseResource `
        -Stage $Stage `
        -TargetFingerprint $targetFingerprint `
        -AuthorizationEvidenceSha256 ([string]$leaseContext.authorizationEvidenceSha256) `
        -Holder ([ordered]@{
            holderRunId = [string]$leaseContext.holderRunId
            holderRunAttempt = [int]$leaseContext.holderRunAttempt
        }) `
        -ExpectedFenceToken ([int64]$leaseContext.fenceToken) `
        -BundleRoot $sqlRuntimeBundleRoot `
        -ExpectedApplicationSha256 $artifactSha256 `
        -EnableLocalTestSeams:$EnableLocalTestSeams `
        -LocalAccessTokenProvider $LocalAccessTokenProvider `
        -LocalSqlExecutor $LocalSqlExecutor
    Write-MigrationStageLeaseContext -LeaseContext $releasedLeaseContext -Path $LeaseContextPath
    if (Test-Path -LiteralPath $EvidencePath -PathType Leaf) {
        Update-MigrationApplyEvidenceStageLease -EvidencePath $EvidencePath -LeaseContext $releasedLeaseContext
    }
    Write-Output "MIGRATION-BUNDLE mode=ReleaseLease status=PASS stage=$Stage target=$targetFingerprint fenceToken=$($releasedLeaseContext.fenceToken)"
    return
}

if ($ConnectionStringEnvironmentVariable -notmatch '^[A-Z][A-Z0-9_]{2,127}$') {
    throw 'Migration connection environment variable name is unsafe.'
}
$connectionString = [Environment]::GetEnvironmentVariable($ConnectionStringEnvironmentVariable)
if ([string]::IsNullOrWhiteSpace($connectionString)) {
    throw "Migration connection environment variable is empty: $ConnectionStringEnvironmentVariable"
}
Assert-MigrationCanonicalConnection `
    -ConnectionString $connectionString `
    -ExpectedServerFqdn ([string]$targetMetadata.sqlServerFqdn) `
    -ExpectedDatabaseName ([string]$targetMetadata.sqlDatabaseName)

$leaseExpiresAt = [DateTimeOffset]::Parse([string]$leaseContext.expiresAtUtc)
if ($leaseContext.released -or $leaseExpiresAt -le [DateTimeOffset]::UtcNow) {
    throw 'Migration apply requires an active durable stage lease held by the current authorized run.'
}
$bundleResultPath = Join-Path ([IO.Path]::GetTempPath()) "husaynia-migration-result-$([guid]::NewGuid().ToString('N')).json"
try {
    $fencedMutation = Invoke-MigrationFencedMutation `
        -ServerName $ServerName `
        -DatabaseName $DatabaseName `
        -LeasePolicy $leasePolicy `
        -Resource $leaseResource `
        -Stage $Stage `
        -TargetFingerprint $targetFingerprint `
        -AuthorizationEvidenceSha256 $authorizationEvidenceSha256 `
        -Holder ([ordered]@{
            holderRunId = [string]$leaseContext.holderRunId
            holderRunAttempt = [int]$leaseContext.holderRunAttempt
        }) `
        -ExpectedFenceToken ([int64]$leaseContext.fenceToken) `
        -BundleRoot $sqlRuntimeBundleRoot `
        -ExpectedApplicationSha256 $artifactSha256 `
        -EnableLocalTestSeams:$EnableLocalTestSeams `
        -LocalAccessTokenProvider $LocalAccessTokenProvider `
        -LocalSqlExecutor $LocalSqlExecutor `
        -MutationExecutor ({
            param($validatedLease)

            $dotnet = Get-Command 'dotnet' -ErrorAction Stop
            $bundleArguments = @(
                $bundlePath,
                '--connection-env', $ConnectionStringEnvironmentVariable,
                '--result-file', $bundleResultPath,
                '--expected-server-fqdn', [string]$targetMetadata.sqlServerFqdn,
                '--expected-database', [string]$targetMetadata.sqlDatabaseName,
                '--expected-server-resource-id', [string]$targetMetadata.sqlServerResourceId,
                '--expected-database-resource-id', [string]$targetMetadata.sqlDatabaseResourceId,
                '--expected-target-fingerprint', $targetFingerprint,
                '--release-version', [string]$manifest.version,
                '--release-manifest-sha256', $releaseManifestSha256,
                '--application-sha256', $artifactSha256,
                '--bundle-sha256', $bundleSha256,
                '--lock-provider', [string]$lockPolicy.provider,
                '--lock-resource', $lockResource,
                '--lock-mode', [string]$lockPolicy.mode,
                '--lock-owner', [string]$lockPolicy.owner,
                '--lock-timeout-milliseconds', [string]$lockPolicy.timeoutMilliseconds,
                '--stage-lease-resource', [string]$validatedLease.resource,
                '--stage-lease-fence-token', [string]$validatedLease.fenceToken,
                '--stage-lease-holder-run-id', [string]$validatedLease.holderRunId,
                '--stage-lease-holder-run-attempt', [string]$validatedLease.holderRunAttempt,
                '--stage-lease-authorization-evidence-sha256',
                    [string]$validatedLease.authorizationEvidenceSha256,
                '--stage-lease-expires-at-utc', [string]$validatedLease.expiresAtUtc
            )
            $bundleOutput = @(& $dotnet.Source @bundleArguments 2>&1)
            if ($LASTEXITCODE -ne 0) {
                throw "Migration bundle Apply failed with exit code $LASTEXITCODE."
            }
            if (-not (Test-Path -LiteralPath $bundleResultPath -PathType Leaf) -or
                (Get-Item -LiteralPath $bundleResultPath).Length -eq 0) {
                throw 'Migration bundle did not produce the required structured result.'
            }
            return Get-Content -LiteralPath $bundleResultPath -Raw | ConvertFrom-Json
        }.GetNewClosure())
    $mutationLease = $fencedMutation.Lease
    $bundleResult = $fencedMutation.Result
    Write-Output "MIGRATION-BUNDLE mode=Apply stage=$Stage target=$targetFingerprint bundleSha256=$bundleSha256"
    Assert-MigrationExactProperties -Object $bundleResult -Label 'Migration bundle result' -Expected @(
        'schemaVersion',
        'status',
        'executed',
        'applied',
        'databaseInvocationExecuted',
        'observedServerFqdn',
        'observedDatabaseName',
        'sqlServerResourceId',
        'sqlDatabaseResourceId',
        'databaseTargetFingerprint',
        'releaseVersion',
        'releaseManifestSha256',
        'applicationSha256',
        'bundleSha256',
        'migrationLock',
        'migrationState'
    )
    Assert-MigrationExactProperties -Object $bundleResult.migrationLock -Label 'Migration bundle database lock result' -Expected @(
        'provider',
        'resource',
        'mode',
        'owner',
        'timeoutMilliseconds',
        'acquired',
        'heldThroughApply',
        'released'
    )
    Assert-MigrationExactProperties -Object $bundleResult.migrationState -Label 'Migration bundle applied state' -Expected @(
        'before',
        'after',
        'targetMigration',
        'currentMigration',
        'appliedMigrationCount',
        'historyTableVerified'
    )
    if ([string]$bundleResult.schemaVersion -ne [string]$policy.migration.resultSchemaVersion -or
        [string]$bundleResult.status -ne 'PASS' -or
        $bundleResult.executed -ne $true -or
        $bundleResult.applied -ne $true -or
        $bundleResult.databaseInvocationExecuted -ne $true -or
        ([string]$bundleResult.observedServerFqdn).ToLowerInvariant() -cne ([string]$targetMetadata.sqlServerFqdn).ToLowerInvariant() -or
        [string]$bundleResult.observedDatabaseName -cne [string]$targetMetadata.sqlDatabaseName -or
        [string]$bundleResult.sqlServerResourceId -cne [string]$targetMetadata.sqlServerResourceId -or
        [string]$bundleResult.sqlDatabaseResourceId -cne [string]$targetMetadata.sqlDatabaseResourceId -or
        [string]$bundleResult.databaseTargetFingerprint -cne $targetFingerprint -or
        [string]$bundleResult.releaseVersion -cne [string]$manifest.version -or
        [string]$bundleResult.releaseManifestSha256 -cne $releaseManifestSha256 -or
        [string]$bundleResult.applicationSha256 -cne $artifactSha256 -or
        [string]$bundleResult.bundleSha256 -cne $bundleSha256 -or
        [string]$bundleResult.migrationLock.provider -cne [string]$lockPolicy.provider -or
        [string]$bundleResult.migrationLock.resource -cne $lockResource -or
        [string]$bundleResult.migrationLock.mode -cne [string]$lockPolicy.mode -or
        [string]$bundleResult.migrationLock.owner -cne [string]$lockPolicy.owner -or
        -not (Test-MigrationJsonInteger -Value $bundleResult.migrationLock.timeoutMilliseconds -Minimum 0 -Maximum 0) -or
        $bundleResult.migrationLock.acquired -ne $true -or
        $bundleResult.migrationLock.heldThroughApply -ne $true -or
        $bundleResult.migrationLock.released -ne $true -or
        [string]::IsNullOrWhiteSpace([string]$bundleResult.migrationState.before) -or
        [string]::IsNullOrWhiteSpace([string]$bundleResult.migrationState.after) -or
        [string]::IsNullOrWhiteSpace([string]$bundleResult.migrationState.targetMigration) -or
        [string]$bundleResult.migrationState.currentMigration -cne [string]$bundleResult.migrationState.targetMigration -or
        -not (Test-MigrationJsonInteger -Value $bundleResult.migrationState.appliedMigrationCount -Minimum 0) -or
        $bundleResult.migrationState.historyTableVerified -ne $true) {
        throw 'Migration bundle structured result does not prove execution and applied state on the exact protected target.'
    }
}
finally {
    if (Test-Path -LiteralPath $bundleResultPath) {
        Remove-Item -LiteralPath $bundleResultPath -Force
    }
}

$applyCompletedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
$applyOperationId = Get-MigrationApplyOperationId -Stage $Stage -OperationRun $operationRun
Write-MigrationJson -Value ([ordered]@{
    schemaVersion = '1.0.0'
    evidenceType = 'migration-apply'
    status = 'PASS'
    stage = $Stage
    artifactSha256 = $artifactSha256
    releaseManifestSha256 = $releaseManifestSha256
    releaseVersion = [string]$manifest.version
    releaseCommitSha = $releaseCommitSha
    observedAtUtc = $applyCompletedAtUtc
    completedAtUtc = $applyCompletedAtUtc
    sourceRun = $operationRun
    operation = [ordered]@{
        mode = 'Apply'
        operationOrder = 30
        operationId = $applyOperationId
        explicitlyAuthorized = $true
        applied = $true
        exitCode = 0
        bundlePath = $bundleRelativePath
        bundleSha256 = $bundleSha256
        bundleFormat = $bundleFormat
        stageTargetMetadataSha256 = $targetMetadataSha256
        databaseTargetFingerprint = $targetFingerprint
        sqlServerName = [string]$targetMetadata.sqlServerName
        sqlServerFqdn = [string]$targetMetadata.sqlServerFqdn
        sqlDatabaseName = [string]$targetMetadata.sqlDatabaseName
        sqlServerResourceId = [string]$targetMetadata.sqlServerResourceId
        sqlDatabaseResourceId = [string]$targetMetadata.sqlDatabaseResourceId
        preflightEvidenceSha256 = $preflightEvidenceSha256
        applyAuthorizationSha256 = $authorizationEvidenceSha256
        backupEvidenceSha256 = Get-MigrationSha256 -Path $BackupEvidencePath
        databaseInvocationExecuted = $true
        stageLease = Get-MigrationStageLeaseEvidence -LeaseContext $leaseContext
        migrationStateBefore = [string]$bundleResult.migrationState.before
        migrationStateAfter = [string]$bundleResult.migrationState.after
        targetMigration = [string]$bundleResult.migrationState.targetMigration
        currentMigration = [string]$bundleResult.migrationState.currentMigration
        appliedMigrationCount = [int64]$bundleResult.migrationState.appliedMigrationCount
        migrationHistoryTableVerified = $true
        migrationLock = [ordered]@{
            provider = [string]$bundleResult.migrationLock.provider
            resource = [string]$bundleResult.migrationLock.resource
            mode = [string]$bundleResult.migrationLock.mode
            owner = [string]$bundleResult.migrationLock.owner
            timeoutMilliseconds = [int]$bundleResult.migrationLock.timeoutMilliseconds
            acquired = $true
            heldThroughApply = $true
            released = $true
        }
    }
}) -Path $EvidencePath
