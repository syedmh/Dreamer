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
    [string]$InputRoot,
    [Parameter(Mandatory = $true)]
    [string]$DataIsolationKey,
    [Parameter(Mandatory = $true)]
    [string]$ProviderModesJson,
    [Parameter(Mandatory = $true)]
    [string]$HealthEndpoint,
    [string]$CtoApprovalReference,
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
$stagePolicy = @($policy.stages | Where-Object { $_.name -eq $Stage })
if ($stagePolicy.Count -ne 1) {
    throw "Promotion policy must contain exactly one $Stage stage."
}
$stagePolicy = Assert-StageDeploymentEnabled -Policy $policy -Stage $Stage `
    -Operation 'Stage-operation input validation'
$provenance = $policy.provenance
$inputContract = $policy.stageOperationInputContract
$context = Get-StageOperationInputContext -Stage $Stage `
    -ReleaseVerifiedProvenancePath $ReleaseVerifiedProvenancePath `
    -ExpectedAppSha256 $ExpectedAppSha256 -StageTargetMetadataPath $StageTargetMetadataPath `
    -PolicyPath $PolicyPath
Assert-StageTargetExactProperties -Object $inputContract -Label 'Stage-operation input contract' -Expected @(
    'schemaVersion',
    'artifactNameTemplate',
    'manifestPath',
    'checksumPath',
    'reports'
)
if ([string]$inputContract.schemaVersion -ne '2.1.0' -or
    [string]$inputContract.artifactNameTemplate -cne 'stage-operation-inputs-{stageLower}-{applicationSha256}' -or
    [string]$inputContract.manifestPath -cne 'stage-operation-inputs.json' -or
    [string]$inputContract.checksumPath -cne 'SHA256SUMS') {
    throw 'Stage-operation input contract identity or fixed artifact paths changed.'
}
$contractReports = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
foreach ($contractReport in @($inputContract.reports)) {
    Assert-StageTargetExactProperties -Object $contractReport -Label 'Stage-operation contract report' -Expected @(
        'evidenceType',
        'path'
    )
    $contractEvidenceType = [string]$contractReport.evidenceType
    $contractPath = [string]$contractReport.path
    if ($contractEvidenceType -notmatch '^[a-z][a-z-]{0,63}$' -or
        $contractPath -notmatch '^source-[a-z][a-z-]{0,63}\.json$' -or
        -not $contractReports.TryAdd($contractEvidenceType, $contractPath)) {
        throw 'Stage-operation input contract contains an unsafe or duplicate report mapping.'
    }
}
$target = $context.Target
$targetFingerprint = $context.TargetFingerprint
$artifact = $context.ArtifactRoot
$inputDirectory = (Resolve-Path -LiteralPath $InputRoot).Path
$releaseManifestPath = Join-Path $artifact 'release\release-manifest.json'
$appPath = Join-Path $artifact 'app\Husaynia.Web.zip'

function Test-StageOperationInteger {
    param($Value, [int64]$Minimum)

    if ($null -eq $Value -or $Value -is [bool] -or
        ($Value -isnot [sbyte] -and
         $Value -isnot [byte] -and
         $Value -isnot [int16] -and
         $Value -isnot [uint16] -and
         $Value -isnot [int32] -and
         $Value -isnot [uint32] -and
         $Value -isnot [int64])) {
        return $false
    }
    return [int64]$Value -ge $Minimum
}

function ConvertFrom-StageOperationUtc {
    param(
        [string]$Value,
        [string]$Label
    )

    $parsed = [DateTimeOffset]::MinValue
    if ([string]::IsNullOrWhiteSpace($Value) -or
        -not [DateTimeOffset]::TryParse(
            $Value,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None,
            [ref]$parsed) -or
        $parsed.Offset -ne [TimeSpan]::Zero) {
        throw "$Label must be a strict UTC timestamp."
    }
    return $parsed
}

function Assert-StageOperationFresh {
    param(
        [DateTimeOffset]$Value,
        [string]$Label
    )

    $now = [DateTimeOffset]::UtcNow
    if ($Value -gt $now.AddMinutes([int]$provenance.maxClockSkewMinutes) -or
        $Value -lt $now.AddHours(-[int]$stagePolicy.maxEvidenceAgeHours)) {
        throw "$Label is stale or in the future."
    }
}

function Assert-StageOperationHealthEndpoint {
    param([string]$Value)

    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne 'https' -or
        $uri.Host -cne "$($target.webAppName).azurewebsites.net" -or
        $uri.AbsolutePath -cne '/health' -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Query) -or
        -not [string]::IsNullOrEmpty($uri.Fragment)) {
        throw 'Protected health endpoint does not bind the exact immutable stage target.'
    }
}

function Get-RequiredStageOperationReports {
    $required = [Collections.Generic.List[object]]::new()
    foreach ($evidenceName in @($stagePolicy.requiredEvidence)) {
        $evidenceType = [IO.Path]::GetFileNameWithoutExtension([string]$evidenceName)
        if ($evidenceType -in @('configuration', 'migration-preflight', 'migration-apply', 'change-record')) {
            continue
        }
        if (-not $contractReports.ContainsKey($evidenceType)) {
            throw "No trusted stage-operation source report contract exists for $evidenceType."
        }
        $required.Add([ordered]@{
            evidenceType = $evidenceType
            path = [string]$contractReports[$evidenceType]
        })
    }
    return @($required | Sort-Object evidenceType)
}

if (-not (Test-Path -LiteralPath $releaseManifestPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $appPath -PathType Leaf)) {
    throw 'The immutable release manifest or application archive is missing.'
}
$releaseManifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
$releaseManifestSha256 = Get-Sha256Lower -Path $releaseManifestPath
$actualAppSha256 = Get-Sha256Lower -Path $appPath
$appEntry = @($releaseManifest.files | Where-Object { $_.path -ceq 'app/Husaynia.Web.zip' })
if ($actualAppSha256 -cne $ExpectedAppSha256.ToLowerInvariant() -or
    $appEntry.Count -ne 1 -or
    [string]$appEntry[0].sha256 -cne $actualAppSha256 -or
    -not (Test-CommitSha ([string]$releaseManifest.commitSha)) -or
    -not (Test-SafeVersion ([string]$releaseManifest.version))) {
    throw 'Stage-operation inputs do not have a valid immutable release binding.'
}

if ($DataIsolationKey -cne [string]$stagePolicy.dataIsolationKey -or
    $DataIsolationKey -cne [string]$target.dataIsolationKey) {
    throw 'Protected data isolation input does not match the immutable stage policy and target.'
}
$providerModes = $ProviderModesJson | ConvertFrom-Json
Assert-StageTargetExactProperties -Object $providerModes -Label 'Protected provider modes input' -Expected @(
    'payments',
    'messaging',
    'analytics',
    'contentMutation'
)
foreach ($providerName in @('payments', 'messaging', 'analytics', 'contentMutation')) {
    if ([string](Get-StageTargetProperty $providerModes $providerName) -cne
        [string](Get-StageTargetProperty $target.providerModes $providerName)) {
        throw "Protected provider mode does not match stage target metadata: $providerName."
    }
}
Assert-StageOperationHealthEndpoint -Value $HealthEndpoint

$manifestPath = Join-Path $inputDirectory ([string]$inputContract.manifestPath)
$checksumsPath = Join-Path $inputDirectory ([string]$inputContract.checksumPath)
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $checksumsPath -PathType Leaf)) {
    throw 'Stage-operation input manifest or SHA256SUMS is missing.'
}
$requiredReports = @(Get-RequiredStageOperationReports)
$expectedPaths = @(
    [string]$inputContract.checksumPath,
    [string]$inputContract.manifestPath
) + @($requiredReports.path)
$actualPaths = @(Get-ChildItem -LiteralPath $inputDirectory -Recurse -File | ForEach-Object {
    if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Stage-operation input bundle contains a reparse point.'
    }
    Get-RelativeUnixPath -BasePath $inputDirectory -Path $_.FullName
})
if (@(Get-ChildItem -LiteralPath $inputDirectory -Recurse -Directory).Count -gt 0 -or
    @(Compare-Object ($expectedPaths | Sort-Object) ($actualPaths | Sort-Object)).Count -ne 0) {
    throw 'Stage-operation input bundle does not contain the exact required report set.'
}

$checksumEntries = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
foreach ($line in Get-Content -LiteralPath $checksumsPath) {
    if ($line -notmatch '^([a-f0-9]{64})  ([A-Za-z0-9][A-Za-z0-9_.-]{0,127})$') {
        throw "Malformed stage-operation SHA256SUMS line: $line"
    }
    $relativePath = $Matches[2]
    if ($relativePath -eq [string]$inputContract.checksumPath -or
        -not $checksumEntries.TryAdd($relativePath, $Matches[1])) {
        throw 'Stage-operation SHA256SUMS contains an unsafe or duplicate path.'
    }
    $path = Join-Path $inputDirectory $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-Sha256Lower -Path $path) -cne $Matches[1]) {
        throw "Stage-operation SHA256SUMS mismatch: $relativePath"
    }
}
$expectedChecksumPaths = @($expectedPaths | Where-Object { $_ -ne [string]$inputContract.checksumPath })
if (@(Compare-Object ($expectedChecksumPaths | Sort-Object) (@($checksumEntries.Keys) | Sort-Object)).Count -ne 0) {
    throw 'Stage-operation SHA256SUMS does not exactly cover the trusted bundle.'
}

$bundle = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -DateKind String
Assert-StageTargetExactProperties -Object $bundle -Label 'Stage-operation input manifest' -Expected @(
    'schemaVersion',
    'stage',
    'artifactSha256',
    'releaseManifestSha256',
    'releaseVersion',
    'releaseCommitSha',
    'targetFingerprint',
    'sourceRelease',
    'releaseBinding',
    'productionAuthorizationRequest',
    'observedAtUtc',
    'reports'
)
if ([string]$bundle.schemaVersion -ne '2.1.0' -or
    [string]$bundle.stage -cne $Stage -or
    [string]$bundle.artifactSha256 -cne $actualAppSha256 -or
    [string]$bundle.releaseManifestSha256 -cne $releaseManifestSha256 -or
    [string]$bundle.releaseVersion -cne [string]$releaseManifest.version -or
    [string]$bundle.releaseCommitSha -cne ([string]$releaseManifest.commitSha).ToLowerInvariant() -or
    [string]$bundle.targetFingerprint -cne $targetFingerprint) {
    throw 'Stage-operation input manifest does not bind the selected stage, target, and immutable release.'
}
Assert-StageTargetExactProperties -Object $bundle.releaseBinding -Label 'Stage-operation release binding' -Expected @(
    'runId',
    'applicationSha256',
    'bundleSha256',
    'manifestSha256',
    'commitSha'
)
$protectedBundle = @($releaseManifest.files | Where-Object {
    [string]$_.path -ceq 'operations/protected-execution-bundle.zip'
})
if ($protectedBundle.Count -ne 1 -or
    [string]$bundle.releaseBinding.runId -cne [string]$context.SourceRelease.run.runId -or
    [string]$bundle.releaseBinding.applicationSha256 -cne $actualAppSha256 -or
    [string]$bundle.releaseBinding.bundleSha256 -cne [string]$protectedBundle[0].sha256 -or
    [string]$bundle.releaseBinding.manifestSha256 -cne $releaseManifestSha256 -or
    [string]$bundle.releaseBinding.commitSha -cne ([string]$releaseManifest.commitSha).ToLowerInvariant()) {
    throw 'Stage-operation input release binding is not exact.'
}
if ($Stage -eq 'Production') {
    Assert-StageTargetExactProperties -Object $bundle.productionAuthorizationRequest `
        -Label 'Production authorization request' -Expected @(
            'stage',
            'releaseBinding',
            'targetFingerprint',
            'ctoApprovalReference'
        )
    if ([string]$bundle.productionAuthorizationRequest.stage -cne 'Production' -or
        [string]$bundle.productionAuthorizationRequest.targetFingerprint -cne $targetFingerprint -or
        [string]$bundle.productionAuthorizationRequest.ctoApprovalReference -cne $CtoApprovalReference -or
        $CtoApprovalReference -notmatch '^[A-Za-z0-9][A-Za-z0-9._:/#-]{0,127}$' -or
        (@($bundle.productionAuthorizationRequest.releaseBinding | ConvertTo-Json -Compress) -join '') -cne
            (@($bundle.releaseBinding | ConvertTo-Json -Compress) -join '')) {
        throw 'Production authorization request is not inert and exactly release/target/reference bound.'
    }
}
elseif ($null -ne $bundle.productionAuthorizationRequest -or
        -not [string]::IsNullOrWhiteSpace($CtoApprovalReference)) {
    throw 'Non-Production prepared inputs contain a Production authorization request.'
}
Assert-StageTargetExactProperties -Object $bundle.sourceRelease -Label 'Stage-operation manifest source release' -Expected @(
    'schemaVersion',
    'authority',
    'expectedRole',
    'run',
    'artifact',
    'contentManifestSha256'
)
Assert-StageTargetExactProperties -Object $bundle.sourceRelease.run `
    -Label 'Stage-operation manifest source release run' -Expected @(
    'repository',
    'workflowPath',
    'workflowRef',
    'runId',
    'runAttempt',
    'ref',
    'commitSha',
    'status',
    'conclusion'
)
Assert-StageTargetExactProperties -Object $bundle.sourceRelease.artifact `
    -Label 'Stage-operation manifest source release artifact' -Expected @(
    'id',
    'name',
    'archiveSha256',
    'sizeBytes',
    'createdAtUtc',
    'expiresAtUtc'
)
if ((@($bundle.sourceRelease | ConvertTo-Json -Depth 20 -Compress) -join '') -cne
    (@($context.SourceRelease | ConvertTo-Json -Depth 20 -Compress) -join '')) {
    throw 'Stage-operation input manifest source release is not the exact normalized validated release provenance.'
}
if ([string]$bundle.sourceRelease.schemaVersion -ne '2.0.0' -or
    [string]$bundle.sourceRelease.authority -ne 'github-actions-api-and-sigstore-v1' -or
    [string]$bundle.sourceRelease.expectedRole -ne 'release-c6' -or
    [string]$bundle.sourceRelease.contentManifestSha256 -ne $releaseManifestSha256) {
    throw 'Stage-operation input manifest source release does not bind the validated C6 provenance.'
}
if (-not (Test-StageOperationInteger -Value $bundle.sourceRelease.run.runAttempt -Minimum 1) -or
    -not (Test-StageOperationInteger -Value $bundle.sourceRelease.artifact.sizeBytes -Minimum 1)) {
    throw 'Stage-operation input manifest source release contains invalid numeric bindings.'
}
if ([string]$bundle.sourceRelease.run.commitSha -cne
    ([string]$releaseManifest.commitSha).ToLowerInvariant()) {
    throw 'Stage-operation input manifest source release commit does not bind the immutable release manifest.'
}

$reportEntries = @($bundle.reports)
if ($reportEntries.Count -ne $requiredReports.Count) {
    throw 'Stage-operation input manifest does not contain the exact required report count.'
}
$latestObserved = [DateTimeOffset]::MinValue
$seenEvidenceTypes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($requiredReport in $requiredReports) {
    $matches = @($reportEntries | Where-Object {
        [string](Get-StageTargetProperty $_ 'evidenceType') -ceq [string]$requiredReport.evidenceType
    })
    if ($matches.Count -ne 1) {
        throw "Stage-operation report entry is missing or duplicated: $($requiredReport.evidenceType)."
    }
    $entry = $matches[0]
    Assert-StageTargetExactProperties -Object $entry -Label 'Stage-operation report entry' -Expected @(
        'evidenceType',
        'path',
        'sha256',
        'observedAtUtc'
    )
    if (-not $seenEvidenceTypes.Add([string]$entry.evidenceType) -or
        [string]$entry.path -cne [string]$requiredReport.path -or
        -not (Test-Sha256 ([string]$entry.sha256))) {
        throw "Stage-operation report entry is unsafe or does not match its fixed path: $($requiredReport.evidenceType)."
    }
    $reportPath = Join-Path $inputDirectory ([string]$entry.path)
    if ([string]$entry.sha256 -cne (Get-Sha256Lower -Path $reportPath)) {
        throw "Stage-operation report checksum mismatch: $($entry.path)."
    }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json -DateKind String
    if ([string](Get-StageTargetProperty $report 'schemaVersion') -ne '1.0.0' -or
        [string](Get-StageTargetProperty $report 'stage') -cne $Stage) {
        throw "Stage-operation source report is not bound to $Stage`: $($entry.path)."
    }
    $entryObserved = ConvertFrom-StageOperationUtc -Value ([string]$entry.observedAtUtc) `
        -Label "Manifest observation for $($entry.evidenceType)"
    $reportObserved = ConvertFrom-StageOperationUtc `
        -Value ([string](Get-StageTargetProperty $report 'observedAtUtc')) `
        -Label "Source observation for $($entry.evidenceType)"
    Assert-StageOperationFresh -Value $reportObserved -Label "Source report $($entry.evidenceType)"
    if ($entryObserved -ne $reportObserved) {
        throw "Stage-operation manifest changed the source observation time: $($entry.evidenceType)."
    }
    if ($reportObserved -gt $latestObserved) {
        $latestObserved = $reportObserved
    }
    if ([string]$entry.evidenceType -eq 'health' -and
        [string](Get-StageTargetProperty $report 'endpoint') -cne $HealthEndpoint) {
        throw 'Health report endpoint does not match the protected stage endpoint input.'
    }
}
$bundleObserved = ConvertFrom-StageOperationUtc -Value ([string]$bundle.observedAtUtc) `
    -Label 'Stage-operation bundle observation'
Assert-StageOperationFresh -Value $bundleObserved -Label 'Stage-operation bundle'
if ($bundleObserved -ne $latestObserved) {
    throw 'Stage-operation bundle observation must preserve the latest immutable source report observation.'
}

Write-Output "STAGE-OPERATION-INPUTS status=PASS stage=$Stage reports=$($requiredReports.Count) root=$inputDirectory"
