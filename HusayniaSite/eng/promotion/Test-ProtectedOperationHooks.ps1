[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$PolicyPath,
    [ValidateSet('Development', 'Staging', 'Production')]
    [string]$Stage,
    [string]$StageTargetMetadataPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
. (Join-Path $PSScriptRoot 'StageTarget.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Resolve-HusayniaRepositoryRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
if ([string]::IsNullOrWhiteSpace($PolicyPath)) {
    $PolicyPath = Join-Path $RepositoryRoot 'pipelines\config\promotion-policy.json'
}
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
$hookRoot = Assert-ReviewedProtectedHookRoot -RepositoryRoot $RepositoryRoot -Policy $policy
$reviewedRepositoryFiles = Assert-ReviewedRepositoryFileClosure `
    -RepositoryRoot $RepositoryRoot `
    -Policy $policy `
    -IncludeMigrationOrchestrationFiles

if (-not [string]::IsNullOrWhiteSpace($Stage)) {
    if ([string]::IsNullOrWhiteSpace($StageTargetMetadataPath)) {
        throw 'Stage target metadata is required when validating a stage hook mapping.'
    }
    $stagePolicy = @($policy.stages | Where-Object { $_.name -eq $Stage })
    if ($stagePolicy.Count -ne 1) {
        throw "Promotion policy must contain exactly one $Stage stage."
    }
    $null = Read-StageTargetMetadata -Path $StageTargetMetadataPath -Stage $Stage -StagePolicy $stagePolicy[0]
}

$count = @(Get-ChildItem -LiteralPath $hookRoot -File -Filter '*.ps1').Count
Write-Output "PROTECTED-HOOKS status=PASS hooks=$count reviewedRepositoryFiles=$($reviewedRepositoryFiles.Count) root=$hookRoot"
