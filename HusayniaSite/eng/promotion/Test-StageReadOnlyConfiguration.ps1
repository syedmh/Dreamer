[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Development', 'Staging')]
    [string]$Stage,
    [Parameter(Mandatory = $true)]
    [string]$StageTargetMetadataPath,
    [Parameter(Mandatory = $true)]
    [string]$DataIsolationKey,
    [Parameter(Mandatory = $true)]
    [string]$ProviderModesJson,
    [Parameter(Mandatory = $true)]
    [string]$ReadOnlyProbeEndpoint,
    [Parameter(Mandatory = $true)]
    [string]$ReadOnlyProbeHost,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$TokenEnvironmentVariable = 'T21_STAGE_READONLY_PROBE_TOKEN',
    [string]$LocalFixtureRoot,
    [string]$PolicyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'StageOperationInput.Common.ps1')

$repositoryRoot = Resolve-HusayniaRepositoryRoot
if ([string]::IsNullOrWhiteSpace($PolicyPath)) {
    $PolicyPath = Join-Path $repositoryRoot 'pipelines\config\promotion-policy.json'
}
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
$httpPolicy = Get-StageOperationHttpPolicy -Policy $policy
$stagePolicy = @($policy.stages | Where-Object { $_.name -eq $Stage })
if ($stagePolicy.Count -ne 1 -or $Stage -eq 'Production') {
    throw 'Read-only configuration validation is limited to one reviewed nonproduction stage.'
}
$stagePolicy = Assert-StageDeploymentEnabled -Policy $policy -Stage $Stage `
    -Operation 'Stage configuration evidence production'
$target = Read-StageTargetMetadata -Path $StageTargetMetadataPath -Stage $Stage -StagePolicy $stagePolicy
$providerModes = $ProviderModesJson | ConvertFrom-Json
Assert-StageTargetExactProperties -Object $providerModes -Label 'Protected provider modes' -Expected @(
    'payments',
    'messaging',
    'analytics',
    'contentMutation'
)
if ($DataIsolationKey -cne [string]$stagePolicy.dataIsolationKey -or
    $DataIsolationKey -cne [string]$target.dataIsolationKey) {
    throw 'Protected data isolation key does not match the immutable stage target.'
}
foreach ($providerName in @('payments', 'messaging', 'analytics', 'contentMutation')) {
    if ([string](Get-StageOperationInputProperty $providerModes $providerName) -cne
        [string](Get-StageOperationInputProperty $target.providerModes $providerName)) {
        throw "Protected provider mode does not match the immutable stage target: $providerName."
    }
}

$inventory = Invoke-StageOperationServiceGet `
    -Uri $ReadOnlyProbeEndpoint `
    -ExpectedHost $ReadOnlyProbeHost `
    -TokenEnvironmentVariable $TokenEnvironmentVariable `
    -FixtureRoot $LocalFixtureRoot `
    -FixtureFile 'resource-inventory.json' `
    -ApprovedPrivateEndpoints @($stagePolicy.approvedPrivateServiceProbeEndpoints) `
    -HttpPolicy $httpPolicy
$observedAt = ConvertFrom-StageOperationInputUtc `
    -Value ([string]$inventory.observedAtUtc) `
    -Label 'Read-only resource inventory' `
    -MaximumAgeHours ([int]$stagePolicy.maxEvidenceAgeHours)
if ([string]$inventory.schemaVersion -ne '1.0.0' -or
    [string]$inventory.stage -cne $Stage -or
    $inventory.readOnly -ne $true -or
    $inventory.mutationAttempted -ne $false -or
    [string]$inventory.dataIsolationKey -cne [string]$target.dataIsolationKey -or
    [string]$inventory.serviceConnectionSecretName -cne [string]$target.serviceConnectionSecretName -or
    [string]$inventory.resourceGroupName -cne [string]$target.immutableResourceGroupName -or
    [string]$inventory.sqlServerResourceId -cne [string]$target.sqlServerResourceId -or
    [string]$inventory.sqlDatabaseResourceId -cne [string]$target.sqlDatabaseResourceId -or
    [string]$inventory.webAppResourceId -cne [string]$target.webAppResourceId) {
    throw 'Read-only inventory did not prove the exact isolated nonproduction resource identity.'
}

Write-Utf8Json -Value ([ordered]@{
    schemaVersion = '1.0.0'
    stage = $Stage
    status = 'PASS'
    readOnly = $true
    mutationAttempted = $false
    stageTargetFingerprint = Get-StageTargetFingerprint -TargetMetadata $target
    dataIsolationKey = [string]$target.dataIsolationKey
    serviceConnectionSecretName = [string]$target.serviceConnectionSecretName
    providerModes = $target.providerModes
    resourceGroupName = [string]$target.immutableResourceGroupName
    sqlServerResourceId = [string]$target.sqlServerResourceId
    sqlDatabaseResourceId = [string]$target.sqlDatabaseResourceId
    webAppResourceId = [string]$target.webAppResourceId
    observedAtUtc = $observedAt.ToString('O')
}) -Path $OutputPath -Depth 20

Write-Output "STAGE-CONFIGURATION status=PASS stage=$Stage readOnly=true"
