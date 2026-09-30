[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Development', 'Staging', 'Production')]
    [string]$Stage,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ExpectedAppSha256,
    [Parameter(Mandatory = $true)]
    [string]$ReleaseVerifiedProvenancePath,
    [Parameter(Mandatory = $true)]
    [string]$StageTargetMetadataPath,
    [Parameter(Mandatory = $true)]
    [string]$DataIsolationKey,
    [Parameter(Mandatory = $true)]
    [string]$ProviderModesJson,
    [Parameter(Mandatory = $true)]
    [string]$HealthEndpoint,
    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,
    [string]$CtoApprovalReference,
    [string]$PolicyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'StageOperationInput.Common.ps1')

$context = Get-StageOperationInputContext `
    -Stage $Stage `
    -ReleaseVerifiedProvenancePath $ReleaseVerifiedProvenancePath `
    -ExpectedAppSha256 $ExpectedAppSha256 `
    -StageTargetMetadataPath $StageTargetMetadataPath `
    -PolicyPath $PolicyPath
if ($Stage -eq 'Production') {
    if ($CtoApprovalReference -notmatch '^[A-Za-z0-9][A-Za-z0-9._:/#-]{0,127}$') {
        throw 'Production prepared inputs require the approved CTO reference request.'
    }
}
elseif (-not [string]::IsNullOrWhiteSpace($CtoApprovalReference)) {
    throw 'Non-Production prepared inputs must not carry a CTO approval request.'
}
$contract = $context.Policy.stageOperationInputContract
$contractReports = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
foreach ($entry in @($contract.reports)) {
    if (-not $contractReports.TryAdd([string]$entry.evidenceType, [string]$entry.path)) {
        throw 'Stage-operation input contract contains duplicate report types.'
    }
}
$requiredReports = [Collections.Generic.List[object]]::new()
foreach ($requiredEvidence in @($context.StagePolicy.requiredEvidence)) {
    $evidenceType = [IO.Path]::GetFileNameWithoutExtension([string]$requiredEvidence)
    if ($evidenceType -in @('configuration', 'migration-preflight', 'migration-apply', 'change-record')) {
        continue
    }
    if (-not $contractReports.ContainsKey($evidenceType)) {
        throw "Stage-operation input contract is missing required report type: $evidenceType"
    }
    $requiredReports.Add([ordered]@{
        evidenceType = $evidenceType
        path = [string]$contractReports[$evidenceType]
    })
}
$requiredReports = @($requiredReports | Sort-Object evidenceType)

$output = (Resolve-Path -LiteralPath $OutputRoot).Path
if (@(Get-ChildItem -LiteralPath $output -Directory -Force).Count -ne 0) {
    throw 'Stage-operation input output may not contain subdirectories.'
}
$expectedReportPaths = @($requiredReports.path | Sort-Object)
$actualReportPaths = @(Get-ChildItem -LiteralPath $output -File -Force | ForEach-Object { $_.Name } | Sort-Object)
if (@(Compare-Object $expectedReportPaths $actualReportPaths).Count -ne 0) {
    throw 'Generated stage-operation reports do not exactly match the selected stage policy.'
}

$manifestReports = [Collections.Generic.List[object]]::new()
$latestObserved = [DateTimeOffset]::MinValue
foreach ($requiredReport in $requiredReports) {
    $path = Join-Path $output ([string]$requiredReport.path)
    $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -DateKind String
    $observedAt = ConvertFrom-StageOperationInputUtc `
        -Value ([string](Get-StageOperationInputProperty $report 'observedAtUtc')) `
        -Label "Generated $($requiredReport.evidenceType) report" `
        -MaximumAgeHours ([int]$context.StagePolicy.maxEvidenceAgeHours)
    if ([string](Get-StageOperationInputProperty $report 'schemaVersion') -ne '1.0.0' -or
        [string](Get-StageOperationInputProperty $report 'stage') -cne $Stage) {
        throw "Generated report is not bound to $Stage`: $($requiredReport.path)"
    }
    if ($observedAt -gt $latestObserved) {
        $latestObserved = $observedAt
    }
    $manifestReports.Add([ordered]@{
        evidenceType = [string]$requiredReport.evidenceType
        path = [string]$requiredReport.path
        sha256 = Get-Sha256Lower -Path $path
        observedAtUtc = $observedAt.ToString('O')
    })
}

$manifestPath = Join-Path $output ([string]$contract.manifestPath)
Write-Utf8Json -Value ([ordered]@{
    schemaVersion = '2.1.0'
    stage = $Stage
    artifactSha256 = $context.AppSha256
    releaseManifestSha256 = $context.ManifestSha256
    releaseVersion = [string]$context.Manifest.version
    releaseCommitSha = ([string]$context.Manifest.commitSha).ToLowerInvariant()
    targetFingerprint = $context.TargetFingerprint
    sourceRelease = $context.SourceRelease
    releaseBinding = [ordered]@{
        runId = [string]$context.SourceRelease.run.runId
        applicationSha256 = $context.AppSha256
        bundleSha256 = [string](@($context.Manifest.files | Where-Object {
            [string]$_.path -ceq 'operations/protected-execution-bundle.zip'
        })[0].sha256)
        manifestSha256 = $context.ManifestSha256
        commitSha = ([string]$context.Manifest.commitSha).ToLowerInvariant()
    }
    productionAuthorizationRequest = if ($Stage -eq 'Production') {
        [ordered]@{
            stage = 'Production'
            releaseBinding = [ordered]@{
                runId = [string]$context.SourceRelease.run.runId
                applicationSha256 = $context.AppSha256
                bundleSha256 = [string](@($context.Manifest.files | Where-Object {
                    [string]$_.path -ceq 'operations/protected-execution-bundle.zip'
                })[0].sha256)
                manifestSha256 = $context.ManifestSha256
                commitSha = ([string]$context.Manifest.commitSha).ToLowerInvariant()
            }
            targetFingerprint = $context.TargetFingerprint
            ctoApprovalReference = $CtoApprovalReference
        }
    }
    else {
        $null
    }
    observedAtUtc = $latestObserved.ToString('O')
    reports = @($manifestReports)
}) -Path $manifestPath -Depth 40

$checksumLines = Get-ChildItem -LiteralPath $output -File |
    Where-Object { $_.Name -cne [string]$contract.checksumPath } |
    Sort-Object Name |
    ForEach-Object { "$(Get-Sha256Lower -Path $_.FullName)  $($_.Name)" }
[IO.File]::WriteAllLines(
    (Join-Path $output ([string]$contract.checksumPath)),
    $checksumLines,
    [Text.UTF8Encoding]::new($false))

Invoke-CheckedScript `
    -Path (Join-Path $PSScriptRoot 'Test-StageOperationInputBundle.ps1') `
    -Parameters @{
        Stage = $Stage
        ExpectedAppSha256 = $context.AppSha256
        ReleaseVerifiedProvenancePath = $ReleaseVerifiedProvenancePath
        StageTargetMetadataPath = $StageTargetMetadataPath
        InputRoot = $output
        DataIsolationKey = $DataIsolationKey
        ProviderModesJson = $ProviderModesJson
        HealthEndpoint = $HealthEndpoint
        CtoApprovalReference = $CtoApprovalReference
        PolicyPath = $PolicyPath
    } `
    -Label 'validate generated trusted stage-operation input bundle'

Write-Output "STAGE-OPERATION-INPUT-BUNDLE status=PASS stage=$Stage reports=$($requiredReports.Count) root=$output"
