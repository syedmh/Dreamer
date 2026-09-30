[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(
        'configuration',
        'backup',
        'health',
        'smoke',
        'crawl',
        'performance',
        'accessibility',
        'visual',
        'sandbox-integrations',
        'rollback',
        'restore',
        'change-record'
    )]
    [string]$EvidenceType,
    [Parameter(Mandatory = $true)]
    [ValidateSet('Development', 'Staging', 'Production')]
    [string]$Stage,
    [Parameter(Mandatory = $true)]
    [string]$ArtifactRoot,
    [Parameter(Mandatory = $true)]
    [string]$StageTargetMetadataPath,
    [Parameter(Mandatory = $true)]
    [string]$SourceRunMetadataPath,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$PreflightEvidencePath,
    [string]$BackupEvidencePath,
    [string]$MigrationApplyEvidencePath,
    [string]$ProductionAuthorizationContextPath,
    [string]$LocalDryRunFixtureRoot,
    [string]$PolicyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
. (Join-Path $PSScriptRoot 'StageTarget.Common.ps1')

$repositoryRoot = Resolve-HusayniaRepositoryRoot
if ([string]::IsNullOrWhiteSpace($PolicyPath)) {
    $PolicyPath = Join-Path $repositoryRoot 'pipelines\config\promotion-policy.json'
}
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
$stagePolicy = @($policy.stages | Where-Object { $_.name -eq $Stage })
if ($stagePolicy.Count -ne 1) {
    throw "Promotion policy must contain exactly one $Stage stage."
}
$stagePolicy = Assert-StageDeploymentEnabled -Policy $policy -Stage $Stage `
    -Operation 'Protected operation hook'
if ($Stage -eq 'Production' -and -not [bool]$stagePolicy.deploymentEnabled) {
    throw 'Production operation hooks are unreachable while Production deploymentEnabled is false.'
}
if ($Stage -eq 'Production' -and
    ([string]::IsNullOrWhiteSpace($ProductionAuthorizationContextPath) -or
     -not (Test-Path -LiteralPath $ProductionAuthorizationContextPath -PathType Leaf))) {
    throw 'Production operation hooks require the dedicated protected authorization context.'
}
$metadata = Read-StageTargetMetadata -Path $StageTargetMetadataPath -Stage $Stage -StagePolicy $stagePolicy
$hook = @($metadata.operationHooks | Where-Object { $_.evidenceType -eq $EvidenceType })
if ($hook.Count -ne 1) {
    throw "Protected stage metadata must bind exactly one $EvidenceType hook."
}

if (-not [string]::IsNullOrWhiteSpace($LocalDryRunFixtureRoot) -and
    [Environment]::GetEnvironmentVariable('GITHUB_ACTIONS') -eq 'true') {
    throw 'Local dry-run hook fixtures are prohibited in GitHub Actions.'
}
$hookRoot = Assert-ReviewedProtectedHookRoot -RepositoryRoot $repositoryRoot -Policy $policy
$hookPath = Join-Path $hookRoot ([string]$hook[0].relativePath)
$resolvedHookPath = (Resolve-Path -LiteralPath $hookPath).Path
$relativeHookPath = [IO.Path]::GetRelativePath($hookRoot, $resolvedHookPath)
if ($relativeHookPath.StartsWith('..') -or
    [IO.Path]::IsPathRooted($relativeHookPath) -or
    (Get-CanonicalTextSha256Lower -Path $resolvedHookPath) -ne [string]$hook[0].sha256) {
    throw "Protected $EvidenceType hook is outside the reviewed root or its checksum changed."
}

$parameters = @{
    Stage = $Stage
    ArtifactRoot = (Resolve-Path -LiteralPath $ArtifactRoot).Path
    StageTargetMetadataPath = (Resolve-Path -LiteralPath $StageTargetMetadataPath).Path
    SourceRunMetadataPath = (Resolve-Path -LiteralPath $SourceRunMetadataPath).Path
    OutputPath = $OutputPath
    PolicyPath = (Resolve-Path -LiteralPath $PolicyPath).Path
}
if (-not [string]::IsNullOrWhiteSpace($PreflightEvidencePath)) {
    $parameters.PreflightEvidencePath = (Resolve-Path -LiteralPath $PreflightEvidencePath).Path
}
if (-not [string]::IsNullOrWhiteSpace($BackupEvidencePath)) {
    $parameters.BackupEvidencePath = (Resolve-Path -LiteralPath $BackupEvidencePath).Path
}
if (-not [string]::IsNullOrWhiteSpace($MigrationApplyEvidencePath)) {
    $parameters.MigrationApplyEvidencePath = (Resolve-Path -LiteralPath $MigrationApplyEvidencePath).Path
}
if (-not [string]::IsNullOrWhiteSpace($ProductionAuthorizationContextPath)) {
    $parameters.ProductionAuthorizationContextPath =
        (Resolve-Path -LiteralPath $ProductionAuthorizationContextPath).Path
}
if (-not [string]::IsNullOrWhiteSpace($LocalDryRunFixtureRoot)) {
    $parameters.LocalDryRunFixtureRoot = (Resolve-Path -LiteralPath $LocalDryRunFixtureRoot).Path
}

Invoke-CheckedScript -Path $resolvedHookPath -Parameters $parameters -Label "execute protected $Stage $EvidenceType operation hook"
if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf) -or
    (Get-Item -LiteralPath $OutputPath).Length -eq 0) {
    throw "Protected $EvidenceType hook did not produce evidence."
}
$evidence = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json
if ([string]$evidence.schemaVersion -ne '1.0.0' -or
    [string]$evidence.evidenceType -ne $EvidenceType -or
    [string]$evidence.status -ne 'PASS' -or
    [string]$evidence.stage -ne $Stage -or
    $null -eq $evidence.operation) {
    throw "Protected $EvidenceType hook produced invalid or success-shaped stub evidence."
}
Write-Output "OPERATION-HOOK stage=$Stage evidenceType=$EvidenceType status=PASS sha256=$(Get-Sha256Lower -Path $OutputPath)"
