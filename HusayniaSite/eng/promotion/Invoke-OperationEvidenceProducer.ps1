[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Preflight', 'StageOperations')]
    [string]$Mode,
    [Parameter(Mandatory = $true)]
    [ValidateSet('Development', 'Staging', 'Production')]
    [string]$Stage,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ExpectedAppSha256,
    [Parameter(Mandatory = $true)]
    [string]$ReleaseVerifiedProvenancePath,
    [Parameter(Mandatory = $true)]
    [string]$TopLevelCallerWorkflowRef,
    [Parameter(Mandatory = $true)]
    [string]$ProducerWorkflowRef,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[1-9][0-9]*$')]
    [string]$ProducerRunId,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$ProducerRunAttempt,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{40}$')]
    [string]$ProducerCommitSha,
    [Parameter(Mandatory = $true)]
    [string]$StageTargetMetadataPath,
    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$TrustedBundleSha256,
    [string]$ConnectionStringEnvironmentVariable = 'HUSAYNIA_MIGRATION_CONNECTION',
    [switch]$AllowProtectedOperations,
    [switch]$EnableLocalTestSeams,
    [scriptblock]$LocalAccessTokenProvider,
    [scriptblock]$LocalSqlExecutor,
    [string]$PolicyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
. (Join-Path $PSScriptRoot 'StageTarget.Common.ps1')

$trustedBundleRoot = [Environment]::GetEnvironmentVariable('T21_TRUSTED_BUNDLE_ROOT')
if ([string]::IsNullOrWhiteSpace($trustedBundleRoot)) {
    $repositoryRoot = Resolve-HusayniaRepositoryRoot
}
else {
    $repositoryRoot = (Resolve-Path -LiteralPath $trustedBundleRoot).Path
    $expectedScriptRoot = (Resolve-Path -LiteralPath (Join-Path $repositoryRoot 'eng\promotion')).Path
    if ($PSScriptRoot -cne $expectedScriptRoot) {
        throw 'Protected operation evidence must execute only from the validated immutable bundle.'
    }
}
. (Join-Path $repositoryRoot 'eng\artifact\migrations\bundle\Migration.Common.ps1')
Assert-MigrationLocalTestSeams -EnableLocalTestSeams:$EnableLocalTestSeams `
    -LocalAccessTokenProvider $LocalAccessTokenProvider `
    -LocalSqlExecutor $LocalSqlExecutor
if (($null -eq $LocalAccessTokenProvider) -xor ($null -eq $LocalSqlExecutor)) {
    throw 'T21_SQL_RUNTIME_INVALID'
}
if ($EnableLocalTestSeams -and
    ($null -eq $LocalAccessTokenProvider -or $null -eq $LocalSqlExecutor)) {
    throw 'T21_SQL_RUNTIME_INVALID'
}
if ([string]::IsNullOrWhiteSpace($PolicyPath)) {
    $PolicyPath = Join-Path $repositoryRoot 'pipelines\config\promotion-policy.json'
}
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json -DateKind String
if ($Stage -eq 'Production') {
    $null = Assert-StageDeploymentEnabled -Policy $policy -Stage $Stage `
        -Operation 'Operation evidence production'
}

if (-not $AllowProtectedOperations -or (
        [Environment]::GetEnvironmentVariable('GITHUB_ACTIONS') -ne 'true' -and
        -not $EnableLocalTestSeams
    )) {
    throw 'Protected operation evidence execution is default-disabled and may run only in the separately installed protected GitHub workflow.'
}

# There is no v2 deployment-evidence producer. Reject every StageOperations request before
# consuming candidate evidence, metadata, prepared input, receipt, token, or mutation.
if ($Mode -eq 'StageOperations') {
    $null = Assert-DeploymentEvidence -Policy $policy -Stage $Stage
    throw 'T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN'
}

if ([string]::IsNullOrWhiteSpace($trustedBundleRoot) -or
    $ProducerWorkflowRef -notmatch
        '^syedmh/Dreamer/\.github/workflows/trusted-protected-operations\.yml@[a-f0-9]{40}$') {
    throw 'Protected operation evidence requires the validated immutable bundle and exact reusable-workflow identity.'
}
$stagePolicy = @($policy.stages | Where-Object { $_.name -eq $Stage })
if ($stagePolicy.Count -ne 1) {
    throw "Promotion policy must contain exactly one $Stage stage."
}
$stagePolicy = Assert-StageDeploymentEnabled -Policy $policy -Stage $Stage `
    -Operation 'Operation evidence production'
$targetMetadata = Read-StageTargetMetadata -Path $StageTargetMetadataPath -Stage $Stage -StagePolicy $stagePolicy
$null = Assert-ReviewedProtectedHookRoot -RepositoryRoot $repositoryRoot -Policy $policy

$releaseVerified = Assert-MigrationVerifiedArtifact `
    -VerifiedProvenancePath $ReleaseVerifiedProvenancePath `
    -ExpectedRole release-c6 `
    -Stage $Stage `
    -BundleSha256 $TrustedBundleSha256.ToLowerInvariant() `
    -Policy $policy
$artifact = $releaseVerified.artifactRoot
$producerRunBinding = Assert-MigrationProducerRunBinding `
    -Binding ([pscustomobject][ordered]@{
        schemaVersion = '2.0.0'
        topLevelCallerWorkflowRef = $TopLevelCallerWorkflowRef
        producerWorkflowRef = $ProducerWorkflowRef
        runId = $ProducerRunId
        runAttempt = $ProducerRunAttempt
        commitSha = $ProducerCommitSha.ToLowerInvariant()
    }) `
    -Stage $Stage `
    -Policy $policy `
    -ExpectedTopLevelCallerWorkflowRef $TopLevelCallerWorkflowRef `
    -ExpectedProducerWorkflowRef $ProducerWorkflowRef

Invoke-CheckedScript -Path (Join-Path $repositoryRoot 'eng\artifact\Test-ReleaseArtifact.ps1') `
    -Parameters @{ ArtifactRoot = $artifact; ExpectedAppSha256 = $ExpectedAppSha256; PolicyPath = $PolicyPath } `
    -Label 'verify operation-evidence release artifact'

$output = New-CleanDirectory -Path $OutputRoot
Copy-Item -LiteralPath $StageTargetMetadataPath -Destination (Join-Path $output 'stage-target-metadata.json')
$migrationHook = Join-Path $repositoryRoot 'eng\artifact\migrations\bundle\Invoke-MigrationBundle.ps1'
$preflightOutput = Join-Path $output 'migration-preflight.json'
$migrationParameters = @{
    Mode = 'Preflight'
    Stage = $Stage
    ReleaseVerifiedProvenancePath = $ReleaseVerifiedProvenancePath
    ExpectedProtectedBundleSha256 = $TrustedBundleSha256
    ProducerRunBinding = [pscustomobject]$producerRunBinding
    ServerName = [string]$targetMetadata.sqlServerFqdn
    DatabaseName = [string]$targetMetadata.sqlDatabaseName
    StageTargetMetadataPath = $StageTargetMetadataPath
    PolicyPath = $PolicyPath
    EvidencePath = $preflightOutput
}
if ($EnableLocalTestSeams) {
    $migrationParameters.EnableLocalTestSeams = $true
    $migrationParameters.LocalAccessTokenProvider = $LocalAccessTokenProvider
    $migrationParameters.LocalSqlExecutor = $LocalSqlExecutor
    & $migrationHook @migrationParameters
}
else {
    Invoke-CheckedScript -Path $migrationHook -Parameters $migrationParameters `
        -Label "execute immutable $Stage migration preflight"
}
Set-TrustedExecutionEvidence -Path $preflightOutput `
    -CallerWorkflowRef $TopLevelCallerWorkflowRef `
    -ReusableWorkflowRef $ProducerWorkflowRef `
    -BundleSha256 $TrustedBundleSha256
$releaseManifestPath = Join-Path $artifact 'release\release-manifest.json'
& (Join-Path $PSScriptRoot 'New-T21ProducerManifest.ps1') `
    -ArtifactRoot $output -ProducerRole trusted-preflight -Stage $Stage `
    -ApplicationSha256 $ExpectedAppSha256.ToLowerInvariant() `
    -BundleSha256 $TrustedBundleSha256.ToLowerInvariant() `
    -ReleaseManifestSha256 (Get-Sha256Lower -Path $releaseManifestPath) `
    -ReleaseCommitSha ([string]$releaseVerified.provenance.run.commitSha).ToLowerInvariant() `
    -ReleaseRunId ([string]$releaseVerified.provenance.run.runId) `
    -ReleaseRunAttempt ([int]$releaseVerified.provenance.run.runAttempt) `
    -ProducerRunId ([string]$producerRunBinding.runId) `
    -ProducerRunAttempt ([int]$producerRunBinding.runAttempt) `
    -ProducerCommitSha ([string]$producerRunBinding.commitSha) `
    -TopLevelCallerWorkflowRef ([string]$producerRunBinding.topLevelCallerWorkflowRef) `
    -ProducerWorkflowRef ([string]$producerRunBinding.producerWorkflowRef) `
    -PolicyPath $PolicyPath
if (-not $?) { throw 'Could not create the canonical preflight producer manifest.' }
Write-Output "OPERATION-EVIDENCE mode=Preflight stage=$Stage status=PASS output=$output"
