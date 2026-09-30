[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Development', 'Staging', 'Production')]
    [string]$Stage,
    [Parameter(Mandatory = $true)]
    [string]$ArtifactRoot,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ExpectedAppSha256,
    [Parameter(Mandatory = $true)]
    [string]$EvidenceRoot,
    [Parameter(Mandatory = $true)]
    [string]$ReleaseRunMetadataPath,
    [Parameter(Mandatory = $true)]
    [string]$EvidenceRunMetadataPath,
    [Parameter(Mandatory = $true)]
    [string]$ReceiptPath,
    [string]$DeploymentEvidencePath,
    [string]$PreviousStageReceipt,
    [string]$PreviousStageReceiptRunMetadataPath,
    [string]$ApprovalReference,
    [string]$CtoAuthorizationPath,
    [string]$CtoAuthorizationRunMetadataPath,
    [string]$CtoAuthorizedActorIdAllowlist,
    [string]$MigrationAuthorizedActorIdAllowlist,
    [Parameter(Mandatory = $true)]
    [string]$PromotionRepository,
    [Parameter(Mandatory = $true)]
    [string]$PromotionWorkflowPath,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[1-9][0-9]*$')]
    [string]$PromotionRunId,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$PromotionRunAttempt,
    [Parameter(Mandatory = $true)]
    [string]$PromotionRef,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{40}$')]
    [string]$PromotionCommitSha,
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
$null = Assert-DeploymentEvidence -Policy $policy -Stage $Stage
$stagePolicy = @($policy.stages | Where-Object { $_.name -eq $Stage })
if ($stagePolicy.Count -ne 1) {
    throw "Promotion policy must contain exactly one $Stage stage."
}
$stagePolicy = Assert-StageDeploymentEnabled -Policy $policy -Stage $Stage `
    -Operation 'Stage promotion gate'
$provenance = $policy.provenance

$expectedPromotionWorkflow = if ($Stage -eq 'Production') {
    [string]$provenance.productionPromotionWorkflowPath
}
else {
    [string]$provenance.releaseWorkflowPath
}
if ($PromotionRepository -ne [string]$provenance.repository -or
    $PromotionWorkflowPath -ne $expectedPromotionWorkflow -or
    $PromotionRef -ne [string]$provenance.protectedRef) {
    throw 'Promotion run identity does not match the approved repository, workflow, and protected ref.'
}

Invoke-CheckedScript -Path (Join-Path $PSScriptRoot '..\artifact\Test-ReleaseArtifact.ps1') `
    -Parameters @{
        ArtifactRoot = $ArtifactRoot
        ExpectedAppSha256 = $ExpectedAppSha256
        PolicyPath = $PolicyPath
    } `
    -Label 'verify immutable release artifact'

Invoke-CheckedScript -Path (Join-Path $PSScriptRoot 'Test-StageEvidenceBundle.ps1') `
    -Parameters @{
        Stage = $Stage
        ArtifactRoot = $ArtifactRoot
        ExpectedAppSha256 = $ExpectedAppSha256
        EvidenceRoot = $EvidenceRoot
        ReleaseRunMetadataPath = $ReleaseRunMetadataPath
        EvidenceRunMetadataPath = $EvidenceRunMetadataPath
        MigrationAuthorizedActorIdAllowlist = $MigrationAuthorizedActorIdAllowlist
        PolicyPath = $PolicyPath
    } `
    -Label "validate $Stage evidence bundle"

$artifact = (Resolve-Path -LiteralPath $ArtifactRoot).Path
$releaseManifestPath = Join-Path $artifact 'release\release-manifest.json'
$releaseManifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
$releaseManifestSha256 = Get-Sha256Lower -Path $releaseManifestPath
$actualAppSha256 = Get-Sha256Lower -Path (Join-Path $artifact 'app\Husaynia.Web.zip')
$releaseRun = Get-Content -LiteralPath $ReleaseRunMetadataPath -Raw | ConvertFrom-Json

$deploymentContract = $policy.deploymentContract
Assert-StageTargetExactProperties -Object $deploymentContract -Label 'Deployment contract' -Expected @(
    'schemaVersion',
    'packageAndConfigurationHookRequired',
    'runningVersionChecksumRequired',
    'postDeploymentEvidenceRequiresRunningVersion'
)
if ([string]$deploymentContract.schemaVersion -cne '1.0.0' -or
    $deploymentContract.packageAndConfigurationHookRequired -ne $true -or
    $deploymentContract.runningVersionChecksumRequired -ne $true -or
    $deploymentContract.postDeploymentEvidenceRequiresRunningVersion -ne $true -or
    [string]::IsNullOrWhiteSpace($DeploymentEvidencePath) -or
    -not (Test-Path -LiteralPath $DeploymentEvidencePath -PathType Leaf)) {
    throw 'Promotion requires package/configuration deployment and checksum-bound running-version evidence.'
}
$deploymentEvidence = Get-Content -LiteralPath $DeploymentEvidencePath -Raw | ConvertFrom-Json
Assert-StageTargetExactProperties -Object $deploymentEvidence -Label 'Deployment evidence' -Expected @(
    'schemaVersion',
    'evidenceType',
    'status',
    'stage',
    'artifactSha256',
    'releaseManifestSha256',
    'releaseCommitSha',
    'observedAtUtc',
    'operation',
    'trustedExecution'
)
Assert-StageTargetExactProperties -Object $deploymentEvidence.operation -Label 'Deployment operation evidence' -Expected @(
    'packageAndConfigurationApplied',
    'deploymentExecuted',
    'runningVersionChecksumVerified',
    'runningApplicationSha256',
    'healthVerified',
    'healthApplicationSha256',
    'completedAtUtc'
)
$deploymentObserved = [DateTimeOffset]::MinValue
$deploymentCompleted = [DateTimeOffset]::MinValue
if ([string]$deploymentEvidence.schemaVersion -cne '1.0.0' -or
    [string]$deploymentEvidence.evidenceType -cne 'deployment' -or
    [string]$deploymentEvidence.status -cne 'PASS' -or
    [string]$deploymentEvidence.stage -cne $Stage -or
    [string]$deploymentEvidence.artifactSha256 -cne $actualAppSha256 -or
    [string]$deploymentEvidence.releaseManifestSha256 -cne $releaseManifestSha256 -or
    [string]$deploymentEvidence.releaseCommitSha -cne ([string]$releaseManifest.commitSha).ToLowerInvariant() -or
    $deploymentEvidence.operation.packageAndConfigurationApplied -ne $true -or
    $deploymentEvidence.operation.deploymentExecuted -ne $true -or
    $deploymentEvidence.operation.runningVersionChecksumVerified -ne $true -or
    [string]$deploymentEvidence.operation.runningApplicationSha256 -cne $actualAppSha256 -or
    $deploymentEvidence.operation.healthVerified -ne $true -or
    [string]$deploymentEvidence.operation.healthApplicationSha256 -cne $actualAppSha256 -or
    -not [DateTimeOffset]::TryParse([string]$deploymentEvidence.observedAtUtc, [ref]$deploymentObserved) -or
    -not [DateTimeOffset]::TryParse([string]$deploymentEvidence.operation.completedAtUtc, [ref]$deploymentCompleted) -or
    $deploymentObserved -lt $deploymentCompleted) {
    throw 'Deployment evidence does not prove package/configuration application and running-version health for the immutable checksum.'
}
$null = Assert-TrustedExecutionEvidence `
    -TrustedExecution $deploymentEvidence.trustedExecution `
    -Policy $policy `
    -ReleaseManifest $releaseManifest
$deploymentEvidenceSha256 = Get-Sha256Lower -Path $DeploymentEvidencePath
$evidenceRun = Get-Content -LiteralPath $EvidenceRunMetadataPath -Raw | ConvertFrom-Json
$failures = [Collections.Generic.List[string]]::new()

function Add-GateFailure {
    param([string]$Message)
    $script:failures.Add($Message)
}

function Get-PropertyValue {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Test-ReceiptRunBinding {
    param($Binding, $Metadata)
    foreach ($name in @('repository', 'workflowPath', 'runId', 'runAttempt', 'ref', 'commitSha')) {
        if ([string](Get-PropertyValue $Binding $name) -ne [string](Get-PropertyValue $Metadata $name)) {
            return $false
        }
    }
    return $true
}

function Get-EvidenceObservationRecords {
    param(
        [string]$Root,
        $ObservationStagePolicy
    )

    $records = [Collections.Generic.List[object]]::new()
    $now = [DateTimeOffset]::UtcNow
    foreach ($requiredEvidence in @($ObservationStagePolicy.requiredEvidence)) {
        $path = Join-Path $Root ([string]$requiredEvidence)
        $evidence = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -DateKind String
        $evidenceType = [IO.Path]::GetFileNameWithoutExtension([string]$requiredEvidence)
        $observed = [DateTimeOffset]::MinValue
        if ([string]$evidence.evidenceType -cne $evidenceType -or
            -not [DateTimeOffset]::TryParse([string]$evidence.observedAtUtc, [ref]$observed) -or
            $observed -gt $now.AddMinutes([int]$provenance.maxClockSkewMinutes) -or
            $observed -lt $now.AddHours(-[int]$ObservationStagePolicy.maxEvidenceAgeHours)) {
            throw "Stage gate could not preserve a fresh original evidence observation: $requiredEvidence"
        }
        $records.Add([ordered]@{
            evidenceType = $evidenceType
            path = [string]$requiredEvidence
            sha256 = Get-Sha256Lower -Path $path
            observedAtUtc = [string]$evidence.observedAtUtc
        })
    }
    return @($records)
}

function Test-ReceiptEvidenceObservations {
    param(
        $Receipt,
        $ObservationStagePolicy
    )

    $observations = @(Get-PropertyValue $Receipt 'evidenceObservations')
    $requiredEvidence = @($ObservationStagePolicy.requiredEvidence)
    if ($observations.Count -ne $requiredEvidence.Count) {
        return $false
    }
    $now = [DateTimeOffset]::UtcNow
    foreach ($requiredPath in $requiredEvidence) {
        $evidenceType = [IO.Path]::GetFileNameWithoutExtension([string]$requiredPath)
        $matches = @($observations | Where-Object {
            [string](Get-PropertyValue $_ 'evidenceType') -ceq $evidenceType
        })
        if ($matches.Count -ne 1) {
            return $false
        }
        $observation = $matches[0]
        $observed = [DateTimeOffset]::MinValue
        if ([string](Get-PropertyValue $observation 'path') -cne [string]$requiredPath -or
            -not (Test-Sha256 ([string](Get-PropertyValue $observation 'sha256'))) -or
            -not [DateTimeOffset]::TryParse([string](Get-PropertyValue $observation 'observedAtUtc'), [ref]$observed) -or
            $observed -gt $now.AddMinutes([int]$provenance.maxClockSkewMinutes) -or
            $observed -lt $now.AddHours(-[int]$ObservationStagePolicy.maxEvidenceAgeHours)) {
            return $false
        }
    }
    return $true
}

$evidenceObservations = @(Get-EvidenceObservationRecords -Root $EvidenceRoot -ObservationStagePolicy $stagePolicy)

$requiredPreviousStage = switch ($Stage) {
    'Development' { $null }
    'Staging' { 'Development' }
    'Production' { 'Staging' }
}
if ($null -ne $requiredPreviousStage) {
    if ([string]::IsNullOrWhiteSpace($PreviousStageReceipt) -or
        -not (Test-Path -LiteralPath $PreviousStageReceipt -PathType Leaf) -or
        [string]::IsNullOrWhiteSpace($PreviousStageReceiptRunMetadataPath) -or
        -not (Test-Path -LiteralPath $PreviousStageReceiptRunMetadataPath -PathType Leaf)) {
        Add-GateFailure 'previous-stage-receipt-or-run-metadata-missing'
    }
    else {
        try {
            $previousReceipt = Get-Content -LiteralPath $PreviousStageReceipt -Raw | ConvertFrom-Json -DateKind String
            $previousRun = Get-Content -LiteralPath $PreviousStageReceiptRunMetadataPath -Raw | ConvertFrom-Json
            $previousStagePolicy = @($policy.stages | Where-Object { $_.name -eq $requiredPreviousStage })
            if ([string]$previousRun.repository -ne [string]$provenance.repository -or
                [string]$previousRun.workflowPath -ne [string]$provenance.releaseWorkflowPath -or
                [string]$previousRun.ref -ne [string]$provenance.protectedRef -or
                [string]$previousRun.status -ne 'completed' -or
                [string]$previousRun.conclusion -ne 'success' -or
                $previousReceipt.stage -ne $requiredPreviousStage -or
                $previousReceipt.status -ne 'PASS' -or
                $previousReceipt.promotionReady -ne $true -or
                $previousReceipt.appSha256 -ne $actualAppSha256 -or
                $previousReceipt.releaseManifestSha256 -ne $releaseManifestSha256 -or
                $previousReceipt.releaseCommitSha -ne ([string]$releaseManifest.commitSha).ToLowerInvariant() -or
                $previousReceipt.deploymentEnabled -ne $true -or
                $previousReceipt.deploymentExecuted -ne $true -or
                [string]$previousReceipt.runningApplicationSha256 -ne $actualAppSha256 -or
                -not (Test-Sha256 ([string]$previousReceipt.deploymentEvidenceSha256)) -or
                $previousStagePolicy.Count -ne 1 -or
                -not (Test-ReceiptEvidenceObservations -Receipt $previousReceipt -ObservationStagePolicy $previousStagePolicy[0]) -or
                -not (Test-ReceiptRunBinding -Binding $previousReceipt.promotionRun -Metadata $previousRun) -or
                -not (Test-ReceiptRunBinding -Binding $previousReceipt.sourceReleaseRun -Metadata $releaseRun)) {
                Add-GateFailure 'previous-stage-receipt-invalid-or-untrusted'
            }
        }
        catch {
            Add-GateFailure "previous-stage-receipt-invalid-json:$($_.Exception.Message)"
        }
    }
}

if ($Stage -eq 'Production') {
    if ([string]::IsNullOrWhiteSpace($ApprovalReference)) {
        Add-GateFailure 'production-environment-approval-reference-missing'
    }
    if ([string]::IsNullOrWhiteSpace($CtoAuthorizationPath) -or
        -not (Test-Path -LiteralPath $CtoAuthorizationPath -PathType Leaf) -or
        [string]::IsNullOrWhiteSpace($CtoAuthorizationRunMetadataPath) -or
        -not (Test-Path -LiteralPath $CtoAuthorizationRunMetadataPath -PathType Leaf) -or
        [string]::IsNullOrWhiteSpace($CtoAuthorizedActorIdAllowlist)) {
        Add-GateFailure 'cto-authorization-record-or-run-metadata-missing'
    }
    else {
        try {
            $authorization = Get-Content -LiteralPath $CtoAuthorizationPath -Raw | ConvertFrom-Json
            $authorizationRun = Get-Content -LiteralPath $CtoAuthorizationRunMetadataPath -Raw | ConvertFrom-Json
            $authorizedActorIds = ConvertTo-AuthorizedActorIdSet -Value $CtoAuthorizedActorIdAllowlist
            $issued = [DateTimeOffset]::MinValue
            $expires = [DateTimeOffset]::MinValue
            $now = [DateTimeOffset]::UtcNow
            if ([string]$authorizationRun.repository -ne [string]$provenance.repository -or
                [string]$authorizationRun.workflowPath -ne [string]$provenance.ctoAuthorizationWorkflowPath -or
                [string]$authorizationRun.ref -ne [string]$provenance.protectedRef -or
                [string]$authorizationRun.status -ne 'completed' -or
                [string]$authorizationRun.conclusion -ne 'success' -or
                [string]$authorization.schemaVersion -ne '1.0.0' -or
                [string]$authorization.decision -ne 'AUTHORIZE' -or
                [string]$authorization.environment -ne 'Husaynia-Production' -or
                [string]$authorization.artifactSha256 -ne $actualAppSha256 -or
                [string]$authorization.releaseManifestSha256 -ne $releaseManifestSha256 -or
                [string]$authorization.releaseCommitSha -ne ([string]$releaseManifest.commitSha).ToLowerInvariant() -or
                [string]$authorization.sourceReleaseRunId -ne [string]$releaseRun.runId -or
                [string]$authorization.changeReference -ne $ApprovalReference -or
                [string]$authorization.authorizedByActor -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$' -or
                -not $authorizedActorIds.Contains([string]$authorization.authorizedByActorId) -or
                [string]$authorization.authorizedByActor -ne [string]$authorizationRun.actor -or
                [string]$authorization.authorizedByActorId -ne [string]$authorizationRun.actorId -or
                [string]$authorization.authorizationRun.actor -ne [string]$authorizationRun.actor -or
                [string]$authorization.authorizationRun.actorId -ne [string]$authorizationRun.actorId -or
                -not [DateTimeOffset]::TryParse([string]$authorization.issuedAtUtc, [ref]$issued) -or
                -not [DateTimeOffset]::TryParse([string]$authorization.expiresAtUtc, [ref]$expires) -or
                $issued -gt $now.AddMinutes([int]$provenance.maxClockSkewMinutes) -or
                $expires -le $now -or
                $expires -le $issued -or
                $expires -gt $issued.AddHours(24)) {
                Add-GateFailure 'cto-authorization-record-invalid-or-untrusted'
            }
            Assert-GitHubRunBinding -Binding $authorization.authorizationRun -Metadata $authorizationRun
            Assert-GitHubRunBinding -Binding $authorization.sourceReleaseRun -Metadata $releaseRun
        }
        catch {
            Add-GateFailure "cto-authorization-invalid-json:$($_.Exception.Message)"
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output "FAIL  $_" }
    Write-Output "STAGE-GATE stage=$Stage status=FAIL appSha256=$actualAppSha256 deploymentExecuted=false"
    throw "Stage gate failed for $Stage with $($failures.Count) failure(s)."
}

$status = if ($Stage -eq 'Production' -and -not $stagePolicy.deploymentEnabled) {
    'BLOCKED'
}
else {
    'PASS'
}

$receipt = [ordered]@{
    schemaVersion = '2.0.0'
    stage = $Stage
    status = $status
    mode = 'immutable-deployment'
    appSha256 = $actualAppSha256
    releaseManifestSha256 = $releaseManifestSha256
    releaseCommitSha = ([string]$releaseManifest.commitSha).ToLowerInvariant()
    artifactRoot = (Split-Path -Leaf $artifact)
    sourceReleaseRun = $releaseRun
    evidenceRun = $evidenceRun
    promotionRun = [ordered]@{
        repository = $PromotionRepository
        workflowPath = $PromotionWorkflowPath
        runId = $PromotionRunId
        runAttempt = $PromotionRunAttempt
        ref = $PromotionRef
        commitSha = $PromotionCommitSha.ToLowerInvariant()
    }
    previousStage = $requiredPreviousStage
    promotionReady = $status -eq 'PASS'
    deploymentEnabled = [bool]$stagePolicy.deploymentEnabled
    deploymentExecuted = $true
    deploymentEvidenceSha256 = $deploymentEvidenceSha256
    runningApplicationSha256 = $actualAppSha256
    rebuildAllowed = [bool]$stagePolicy.rebuildAllowed
    environmentApprovalRequired = [bool]$stagePolicy.environmentApprovalRequired
    ctoAuthorizationRequired = [bool]$stagePolicy.ctoAuthorizationRequired
    evidenceRetentionDays = [int]$policy.evidenceRetentionDays
    evidenceObservations = @($evidenceObservations)
    evaluatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    failures = @()
}
Write-Utf8Json -Value $receipt -Path $ReceiptPath -Depth 40

Write-Output "STAGE-GATE stage=$Stage status=$status appSha256=$actualAppSha256 deploymentExecuted=true"
if ($status -eq 'BLOCKED') {
    throw 'Production execution is blocked by reviewed policy.'
}
