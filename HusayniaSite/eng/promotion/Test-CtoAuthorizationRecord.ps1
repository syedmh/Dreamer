[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ExpectedAppSha256,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ExpectedBundleSha256,
    [Parameter(Mandatory = $true)]
    [string]$ReleaseVerifiedProvenancePath,
    [Parameter(Mandatory = $true)]
    [string]$PreflightVerifiedProvenancePath,
    [Parameter(Mandatory = $true)]
    [string]$PreparedInputVerifiedProvenancePath,
    [Parameter(Mandatory = $true)]
    [string]$CtoAuthorizationVerifiedProvenancePath,
    [Parameter(Mandatory = $true)]
    [string]$StageTargetMetadataPath,
    [Parameter(Mandatory = $true)]
    [string]$AuthorizedTopLevelCallerWorkflowRef,
    [Parameter(Mandatory = $true)]
    [string]$AuthorizedProducerWorkflowRef,
    [Parameter(Mandatory = $true)]
    [string]$PreparedInputProducerWorkflowRef,
    [Parameter(Mandatory = $true)]
    [string]$CtoAuthorizedActorIdAllowlist,
    [Parameter(Mandatory = $true)]
    [string]$ApprovalReference,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$PolicyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
. (Join-Path $PSScriptRoot 'StageTarget.Common.ps1')

function Assert-CtoContextProperties {
    param($Value, [string[]]$Expected, [string]$Label)

    if ($null -eq $Value -or @(Compare-Object ($Expected | Sort-Object) @(
            $Value.PSObject.Properties.Name | Sort-Object)).Count -ne 0) {
        throw "$Label does not exactly match the CTO authorization v2 contract."
    }
}

function Test-CtoContextRunBinding {
    param($Binding, $Expected)

    Assert-CtoContextProperties -Value $Binding -Expected @(
        'repository', 'workflowPath', 'workflowRef', 'runId', 'runAttempt', 'ref', 'commitSha', 'status', 'conclusion'
    ) -Label 'CTO authorization run binding'
    return [string]$Binding.repository -ceq [string]$Expected.repository -and
        [string]$Binding.workflowPath -ceq [string]$Expected.workflowPath -and
        [string]$Binding.workflowRef -ceq [string]$Expected.workflowRef -and
        [string]$Binding.runId -ceq [string]$Expected.runId -and
        [int]$Binding.runAttempt -eq [int]$Expected.runAttempt -and
        [string]$Binding.ref -ceq [string]$Expected.ref -and
        [string]$Binding.commitSha -ceq [string]$Expected.commitSha -and
        [string]$Binding.status -ceq [string]$Expected.status -and
        [string]$Binding.conclusion -ceq [string]$Expected.conclusion
}

function Get-CtoContextTopLevelPath {
    param($Contract, [string]$WorkflowRef, [string]$Label)

    $prefix = "$([string]$Contract.repository)/"
    $suffix = "@$([string]$Contract.protectedRef)"
    if ([string]::IsNullOrWhiteSpace($WorkflowRef) -or
        -not $WorkflowRef.StartsWith($prefix, [StringComparison]::Ordinal) -or
        -not $WorkflowRef.EndsWith($suffix, [StringComparison]::Ordinal)) {
        throw "$Label is not the exact protected-main workflow ref."
    }
    $path = $WorkflowRef.Substring($prefix.Length, $WorkflowRef.Length - $prefix.Length - $suffix.Length)
    if ($path -notmatch '^\.github/workflows/[A-Za-z0-9_.-]+\.yml$') {
        throw "$Label workflow path is malformed."
    }
    return $path
}

function Get-CtoContextPinnedPath {
    param($Contract, [string]$WorkflowRef, [string]$Label)

    $prefix = "$([string]$Contract.repository)/"
    $at = if ([string]::IsNullOrWhiteSpace($WorkflowRef)) {
        -1
    }
    else {
        $WorkflowRef.LastIndexOf('@', [StringComparison]::Ordinal)
    }
    if ([string]::IsNullOrWhiteSpace($WorkflowRef) -or
        -not $WorkflowRef.StartsWith($prefix, [StringComparison]::Ordinal) -or
        $at -le $prefix.Length -or $at -eq $WorkflowRef.Length - 1) {
        throw "$Label is malformed."
    }
    $path = $WorkflowRef.Substring($prefix.Length, $at - $prefix.Length)
    $commit = $WorkflowRef.Substring($at + 1)
    if ($path -notmatch '^\.github/workflows/[A-Za-z0-9_.-]+\.yml$' -or
        $commit -notmatch '^[a-f0-9]{40}$' -or $commit -ceq ('0' * 40)) {
        throw "$Label is malformed or all-zero."
    }
    return $path
}

function Get-CtoContextArtifact {
    param(
        [string]$VerifiedProvenancePath,
        [ValidateSet('release-c6', 'trusted-preflight', 'stage-operation-inputs', 'cto-authorization')]
        [string]$ExpectedRole,
        $Policy
    )

    if ([string]::IsNullOrWhiteSpace($VerifiedProvenancePath) -or
        -not (Test-Path -LiteralPath $VerifiedProvenancePath -PathType Leaf)) {
        throw "CTO authorization context provenance is missing: $ExpectedRole."
    }
    $provenancePath = (Resolve-Path -LiteralPath $VerifiedProvenancePath).Path
    if ([IO.Path]::GetFileName($provenancePath) -cne 'verified-provenance.json') {
        throw "CTO authorization context requires resolver-created verified-provenance.json: $ExpectedRole."
    }
    $resolverRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $provenancePath)).Path
    $artifactRoot = Join-Path $resolverRoot 'artifact'
    if (-not (Test-Path -LiteralPath $artifactRoot -PathType Container)) {
        throw "CTO authorization context resolver artifact is missing: $ExpectedRole."
    }
    $artifactRoot = (Resolve-Path -LiteralPath $artifactRoot).Path
    if (-not (Test-PathWithinDirectory -BasePath $resolverRoot -Path $artifactRoot)) {
        throw "CTO authorization context resolver artifact escaped its output root: $ExpectedRole."
    }

    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json -DateKind String
    Assert-CtoContextProperties -Value $provenance -Expected @(
        'schemaVersion', 'authority', 'expectedRole', 'run', 'artifact', 'contentManifestSha256', 'attestation'
    ) -Label "CTO $ExpectedRole provenance"
    Assert-CtoContextProperties -Value $provenance.run -Expected @(
        'repository', 'workflowPath', 'workflowRef', 'runId', 'runAttempt', 'ref', 'commitSha', 'status', 'conclusion'
    ) -Label "CTO $ExpectedRole run"
    Assert-CtoContextProperties -Value $provenance.artifact -Expected @(
        'id', 'name', 'archiveSha256', 'sizeBytes', 'createdAtUtc', 'expiresAtUtc'
    ) -Label "CTO $ExpectedRole artifact"
    if ([string]$provenance.schemaVersion -cne '2.0.0' -or
        [string]$provenance.authority -cne 'github-actions-api-and-sigstore-v1' -or
        [string]$provenance.expectedRole -cne $ExpectedRole -or
        [string]$provenance.run.repository -cne [string]$Policy.t21ProvenanceContract.repository -or
        [string]$provenance.run.runId -notmatch '^[1-9][0-9]*$' -or
        [int]$provenance.run.runAttempt -lt 1 -or
        [string]$provenance.run.ref -cne [string]$Policy.t21ProvenanceContract.protectedRef -or
        [string]$provenance.run.commitSha -notmatch '^[a-f0-9]{40}$' -or
        [string]$provenance.run.status -cne 'completed' -or
        [string]$provenance.run.conclusion -cne 'success' -or
        [string]$provenance.artifact.id -notmatch '^[1-9][0-9]*$' -or
        [string]$provenance.artifact.archiveSha256 -notmatch '^[a-f0-9]{64}$' -or
        [int64]$provenance.artifact.sizeBytes -lt 1 -or
        [string]$provenance.contentManifestSha256 -notmatch '^[a-f0-9]{64}$') {
        throw "CTO authorization context provenance has malformed bindings: $ExpectedRole."
    }
    if ($ExpectedRole -eq 'release-c6') {
        if ([string]$provenance.run.workflowPath -cne [string]$Policy.provenance.releaseWorkflowPath -or
            [string]$provenance.run.workflowRef -cne
                "$([string]$Policy.t21ProvenanceContract.repository)/$([string]$Policy.provenance.releaseWorkflowPath)@$([string]$Policy.t21ProvenanceContract.protectedRef)" -or
            [string]$provenance.run.status -cne 'completed' -or
            [string]$provenance.run.conclusion -cne 'success' -or
            [string]$provenance.artifact.name -notmatch '^husaynia-site-[0-9A-Za-z][0-9A-Za-z._-]{0,127}$' -or
            $null -ne $provenance.attestation) {
            throw 'CTO authorization context release C6 provenance is not the approved API-only release role.'
        }
        $releaseRoots = @(
            Get-ChildItem -LiteralPath $artifactRoot -Directory -Force |
                Where-Object { $_.Name -ceq [string]$provenance.artifact.name }
        )
        if ($releaseRoots.Count -ne 1 -or
            @(Get-ChildItem -LiteralPath $artifactRoot -Force).Count -ne 1 -or
            -not (Test-PathWithinDirectory -BasePath $artifactRoot -Path $releaseRoots[0].FullName)) {
            throw 'CTO authorization context release C6 root is not uniquely API-bound.'
        }
        $artifactRoot = $releaseRoots[0].FullName
    }
    else {
        Assert-CtoContextProperties -Value $provenance.attestation -Expected @(
            'predicateType', 'signerWorkflow', 'signerDigest', 'sourceRef', 'sourceCommitSha', 'status'
        ) -Label "CTO $ExpectedRole attestation"
        if ([string]$provenance.attestation.predicateType -cne
                [string]$Policy.t21ProvenanceContract.attestation.predicateType -or
            [string]$provenance.attestation.sourceRef -cne
                [string]$Policy.t21ProvenanceContract.protectedRef -or
            [string]$provenance.attestation.sourceCommitSha -cne [string]$provenance.run.commitSha -or
            [string]$provenance.attestation.status -cne 'verified') {
            throw "CTO authorization context attestation is invalid: $ExpectedRole."
        }
    }
    return [pscustomobject]@{
        provenance = $provenance
        artifactRoot = (Resolve-Path -LiteralPath $artifactRoot).Path
    }
}

function Assert-CtoProducerManifest {
    param(
        $Artifact,
        [string]$Role,
        [string]$AppSha,
        [string]$BundleSha,
        [string]$ReleaseManifestSha,
        [string]$ReleaseCommitSha,
        [string]$TopLevelRef,
        [string]$ProducerRef,
        [string]$PolicyFile
    )

    $manifestPath = Join-Path $Artifact.artifactRoot 't21-producer-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        (Get-Sha256Lower -Path $manifestPath) -cne [string]$Artifact.provenance.contentManifestSha256) {
        throw "CTO authorization context producer manifest is missing or changed: $Role."
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -DateKind String
    & (Join-Path $PSScriptRoot 'New-T21ProducerManifest.ps1') -Mode Validate `
        -ArtifactRoot $Artifact.artifactRoot -ProducerRole $Role -Stage Production `
        -ApplicationSha256 $AppSha -BundleSha256 $BundleSha -ReleaseManifestSha256 $ReleaseManifestSha `
        -ReleaseCommitSha $ReleaseCommitSha -ReleaseRunId ([string]$manifest.release.runId) `
        -ReleaseRunAttempt ([int]$manifest.release.runAttempt) `
        -ProducerRunId ([string]$Artifact.provenance.run.runId) `
        -ProducerRunAttempt ([int]$Artifact.provenance.run.runAttempt) `
        -ProducerCommitSha ([string]$Artifact.provenance.run.commitSha) `
        -TopLevelCallerWorkflowRef $TopLevelRef -ProducerWorkflowRef $ProducerRef -PolicyPath $PolicyFile | Out-Null
    if (-not $?) {
        throw "CTO authorization context producer manifest validation failed: $Role."
    }
    return $manifest
}

$repositoryRoot = Resolve-HusayniaRepositoryRoot
if ([string]::IsNullOrWhiteSpace($PolicyPath)) {
    $PolicyPath = Join-Path $repositoryRoot 'pipelines\config\promotion-policy.json'
}
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json -DateKind String
$stagePolicy = @($policy.stages | Where-Object { [string]$_.name -ceq 'Production' })
if ($stagePolicy.Count -ne 1) {
    throw 'Promotion policy must contain exactly one Production stage.'
}
$stagePolicy = Assert-StageDeploymentEnabled -Policy $policy -Stage Production `
    -Operation 'CTO authorization context materialization'
$contract = $policy.t21ProvenanceContract
Assert-CtoContextProperties -Value $contract -Expected @(
    'attestation', 'callerMatrix', 'producerRoles', 'protectedRef', 'repository', 'schemaVersion'
) -Label 'T21 provenance policy'
if ([string]$contract.schemaVersion -cne '2.1.0' -or
    [string]$contract.repository -cne [string]$policy.provenance.repository -or
    [string]$contract.protectedRef -cne [string]$policy.provenance.protectedRef) {
    throw 'T21 provenance policy identity is invalid.'
}
$callerPath = Get-CtoContextTopLevelPath -Contract $contract `
    -WorkflowRef $AuthorizedTopLevelCallerWorkflowRef -Label 'Authorized top-level caller workflow ref'
$producerPath = Get-CtoContextPinnedPath -Contract $contract `
    -WorkflowRef $AuthorizedProducerWorkflowRef -Label 'Authorized producer workflow ref'
$caller = @($contract.callerMatrix | Where-Object {
    [string]$_.stage -ceq 'Production' -and
    [string]$_.workflowRef -ceq $AuthorizedTopLevelCallerWorkflowRef
})
$preflightRole = @($contract.producerRoles | Where-Object { [string]$_.role -ceq 'trusted-preflight' })
if ($caller.Count -ne 1 -or $preflightRole.Count -ne 1 -or
    [string]$preflightRole[0].workflowPath -cne $producerPath) {
    throw 'CTO authorization context caller or reusable workflow is not exactly authorized for Production.'
}

$release = Get-CtoContextArtifact -VerifiedProvenancePath $ReleaseVerifiedProvenancePath `
    -ExpectedRole release-c6 -Policy $policy
Invoke-CheckedScript -Path (Join-Path $repositoryRoot 'eng\artifact\Test-ReleaseArtifact.ps1') `
    -Parameters @{
        ArtifactRoot = $release.artifactRoot
        ExpectedAppSha256 = $ExpectedAppSha256
        PolicyPath = $PolicyPath
    } -Label 'verify CTO context release artifact'
$releaseManifestPath = Join-Path $release.artifactRoot 'release\release-manifest.json'
$releaseManifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json -DateKind String
$releaseManifestSha = Get-Sha256Lower -Path $releaseManifestPath
$releaseCommit = ([string]$releaseManifest.commitSha).ToLowerInvariant()
$bundlePath = 'operations/protected-execution-bundle.zip'
$bundleEntry = @($releaseManifest.files | Where-Object { [string]$_.path -ceq $bundlePath })
if ([string]$release.provenance.contentManifestSha256 -cne $releaseManifestSha -or
    [string]$release.provenance.run.commitSha -cne $releaseCommit -or
    (Get-Sha256Lower -Path (Join-Path $release.artifactRoot 'app\Husaynia.Web.zip')) -cne
        $ExpectedAppSha256.ToLowerInvariant() -or
    $bundleEntry.Count -ne 1 -or
    [string]$bundleEntry[0].sha256 -cne $ExpectedBundleSha256.ToLowerInvariant() -or
    (Get-Sha256Lower -Path (Join-Path $release.artifactRoot $bundlePath)) -cne
        $ExpectedBundleSha256.ToLowerInvariant()) {
    throw 'CTO authorization context release C6 does not bind the exact app, manifest, commit, and bundle.'
}

$target = Read-StageTargetMetadata -Path $StageTargetMetadataPath -Stage Production -StagePolicy $stagePolicy
$targetFingerprint = Get-StageTargetFingerprint -TargetMetadata $target
$targetSha = Get-Sha256Lower -Path $StageTargetMetadataPath
$preflight = Get-CtoContextArtifact -VerifiedProvenancePath $PreflightVerifiedProvenancePath `
    -ExpectedRole trusted-preflight -Policy $policy
$expectedPreflightName = "raw-production-preflight-$($ExpectedAppSha256.ToLowerInvariant())"
if ([string]$preflight.provenance.run.workflowPath -cne $callerPath -or
[string]$preflight.provenance.run.workflowRef -cne $AuthorizedTopLevelCallerWorkflowRef -or
[string]$preflight.provenance.artifact.name -cne $expectedPreflightName -or
    [string]$preflight.provenance.attestation.signerWorkflow -cne $AuthorizedProducerWorkflowRef.Split('@')[0] -or
    [string]$preflight.provenance.attestation.signerDigest -cne $AuthorizedProducerWorkflowRef.Split('@')[1]) {
    throw 'CTO authorization context preflight provenance is not bound to the authorized caller and reusable workflow.'
}
$preflightManifest = Assert-CtoProducerManifest -Artifact $preflight -Role trusted-preflight `
    -AppSha $ExpectedAppSha256.ToLowerInvariant() -BundleSha $ExpectedBundleSha256.ToLowerInvariant() `
    -ReleaseManifestSha $releaseManifestSha -ReleaseCommitSha $releaseCommit `
    -TopLevelRef $AuthorizedTopLevelCallerWorkflowRef -ProducerRef $AuthorizedProducerWorkflowRef `
    -PolicyFile $PolicyPath
$preflightPath = Join-Path $preflight.artifactRoot 'migration-preflight.json'
if (-not (Test-Path -LiteralPath $preflightPath -PathType Leaf)) {
    throw 'CTO authorization context verified preflight evidence is missing.'
}
$preflightEvidence = Get-Content -LiteralPath $preflightPath -Raw | ConvertFrom-Json -DateKind String
$observed = [DateTimeOffset]::MinValue
if ([string]$preflightEvidence.schemaVersion -cne '1.0.0' -or
    [string]$preflightEvidence.evidenceType -cne 'migration-preflight' -or
    [string]$preflightEvidence.status -cne 'PASS' -or
    [string]$preflightEvidence.stage -cne 'Production' -or
    [string]$preflightEvidence.artifactSha256 -cne $ExpectedAppSha256.ToLowerInvariant() -or
    [string]$preflightEvidence.releaseManifestSha256 -cne $releaseManifestSha -or
    [string]$preflightEvidence.releaseCommitSha -cne $releaseCommit -or
    [string]$preflightEvidence.operation.bundlePath -cne $bundlePath -or
    [string]$preflightEvidence.operation.bundleSha256 -cne $ExpectedBundleSha256.ToLowerInvariant() -or
    [string]$preflightEvidence.operation.stageTargetMetadataSha256 -cne $targetSha -or
    [string]$preflightEvidence.operation.databaseTargetFingerprint -cne $targetFingerprint -or
    [string]$preflightEvidence.operation.sqlServerResourceId -cne [string]$target.sqlServerResourceId -or
    [string]$preflightEvidence.operation.sqlDatabaseResourceId -cne [string]$target.sqlDatabaseResourceId -or
    -not [DateTimeOffset]::TryParse([string]$preflightEvidence.observedAtUtc, [ref]$observed) -or
    $observed.Offset -ne [TimeSpan]::Zero -or
    $observed -gt [DateTimeOffset]::UtcNow.AddMinutes([int]$policy.provenance.maxClockSkewMinutes) -or
    $observed -lt [DateTimeOffset]::UtcNow.AddMinutes(-[int]$stagePolicy.maxPreflightAgeMinutes)) {
    throw 'CTO authorization context preflight is not fresh and exactly bound to the Production target.'
}

$preparedInput = Get-CtoContextArtifact -VerifiedProvenancePath $PreparedInputVerifiedProvenancePath `
    -ExpectedRole stage-operation-inputs -Policy $policy
if ([string]$preparedInput.provenance.run.workflowPath -cne $callerPath -or
    [string]$preparedInput.provenance.run.workflowRef -cne $AuthorizedTopLevelCallerWorkflowRef -or
    [string]$preparedInput.provenance.artifact.name -cne
        "stage-operation-inputs-production-$($ExpectedAppSha256.ToLowerInvariant())" -or
    [string]$preparedInput.provenance.attestation.signerWorkflow -cne
        $PreparedInputProducerWorkflowRef.Split('@')[0] -or
    [string]$preparedInput.provenance.attestation.signerDigest -cne
        $PreparedInputProducerWorkflowRef.Split('@')[1]) {
    throw 'CTO authorization context prepared-input provenance is not bound to the authorized caller and producer.'
}
$preparedManifest = Assert-CtoProducerManifest -Artifact $preparedInput -Role stage-operation-inputs `
    -AppSha $ExpectedAppSha256.ToLowerInvariant() -BundleSha $ExpectedBundleSha256.ToLowerInvariant() `
    -ReleaseManifestSha $releaseManifestSha -ReleaseCommitSha $releaseCommit `
    -TopLevelRef $AuthorizedTopLevelCallerWorkflowRef -ProducerRef $PreparedInputProducerWorkflowRef `
    -PolicyFile $PolicyPath
$preparedInputPath = Join-Path $preparedInput.artifactRoot 'stage-operation-inputs.json'
if (-not (Test-Path -LiteralPath $preparedInputPath -PathType Leaf)) {
    throw 'CTO authorization context prepared-input record is missing.'
}
$preparedInputRecord = Get-Content -LiteralPath $preparedInputPath -Raw | ConvertFrom-Json -DateKind String
Assert-CtoContextProperties -Value $preparedInputRecord -Expected @(
    'schemaVersion', 'stage', 'artifactSha256', 'releaseManifestSha256', 'releaseVersion',
    'releaseCommitSha', 'targetFingerprint', 'sourceRelease', 'releaseBinding',
    'productionAuthorizationRequest', 'observedAtUtc', 'reports'
) -Label 'CTO prepared-input record'
if ([string]$preparedInputRecord.schemaVersion -cne '2.1.0' -or
    [string]$preparedInputRecord.stage -cne 'Production' -or
    [string]$preparedInputRecord.artifactSha256 -cne $ExpectedAppSha256.ToLowerInvariant() -or
    [string]$preparedInputRecord.releaseManifestSha256 -cne $releaseManifestSha -or
    [string]$preparedInputRecord.releaseCommitSha -cne $releaseCommit -or
    [string]$preparedInputRecord.targetFingerprint -cne $targetFingerprint -or
    [string]$preparedInputRecord.productionAuthorizationRequest.ctoApprovalReference -cne $ApprovalReference) {
    throw 'CTO authorization context prepared-input request is not bound to the exact release, target, and approval.'
}

$cto = Get-CtoContextArtifact -VerifiedProvenancePath $CtoAuthorizationVerifiedProvenancePath `
    -ExpectedRole cto-authorization -Policy $policy
$ctoRole = @($contract.producerRoles | Where-Object { [string]$_.role -ceq 'cto-authorization' })
$ctoTopLevelRef = "$([string]$contract.repository)/$([string]$policy.provenance.ctoAuthorizationWorkflowPath)@$([string]$contract.protectedRef)"
$ctoProducerRef = "$([string]$contract.repository)/$([string]$policy.provenance.ctoAuthorizationWorkflowPath)@$([string]$cto.provenance.run.commitSha)"
if ($ctoRole.Count -ne 1 -or [string]$cto.provenance.run.workflowPath -cne
        [string]$policy.provenance.ctoAuthorizationWorkflowPath -or
    [string]$cto.provenance.run.workflowRef -cne $ctoTopLevelRef -or
    [string]$cto.provenance.run.status -cne 'completed' -or
    [string]$cto.provenance.run.conclusion -cne 'success' -or
    [string]$cto.provenance.artifact.name -cne "cto-authorization-$($ExpectedAppSha256.ToLowerInvariant())" -or
    [string]$cto.provenance.attestation.signerWorkflow -cne $ctoProducerRef.Split('@')[0] -or
    [string]$cto.provenance.attestation.signerDigest -cne $ctoProducerRef.Split('@')[1]) {
    throw 'CTO authorization context CTO producer provenance is not bound to the completed trusted authorization run.'
}
$ctoManifest = Assert-CtoProducerManifest -Artifact $cto -Role cto-authorization `
    -AppSha $ExpectedAppSha256.ToLowerInvariant() -BundleSha $ExpectedBundleSha256.ToLowerInvariant() `
    -ReleaseManifestSha $releaseManifestSha -ReleaseCommitSha $releaseCommit `
    -TopLevelRef $ctoTopLevelRef -ProducerRef $ctoProducerRef -PolicyFile $PolicyPath
if ($ctoManifest.files.Count -ne 2 -or
    @($ctoManifest.files | Where-Object { [string]$_.path -ceq 'cto-authorization.json' }).Count -ne 1 -or
    @($ctoManifest.files | Where-Object { [string]$_.path -ceq 'source-change-record.json' }).Count -ne 1) {
    throw 'CTO authorization context producer manifest does not bind the canonical record and checked change record.'
}
$recordPath = Join-Path $cto.artifactRoot 'cto-authorization.json'
if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf)) {
    throw 'CTO authorization context canonical record payload is missing.'
}
$record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json -DateKind String
Assert-CtoContextProperties -Value $record -Expected @(
    'schemaVersion', 'decision', 'stage', 'environment', 'applicationSha256', 'releaseManifestSha256',
    'releaseCommitSha', 'sourceRelease', 'releaseBinding', 'bundlePath', 'bundleSha256', 'targetFingerprint',
    'preflightEvidenceSha256', 'preflightProducerRun', 'producerBindings', 'authorizedExecution',
    'ctoApprovalReference', 'sourceChangeRecordSha256',
    'authorizedByActor', 'authorizedByActorId', 'issuedAtUtc', 'expiresAtUtc', 'authorizationRun'
) -Label 'CTO authorization record'
Assert-CtoContextProperties -Value $record.authorizedExecution -Expected @(
    'topLevelCallerWorkflowRef', 'producerWorkflowRef'
) -Label 'CTO authorization authorized execution'
Assert-CtoContextProperties -Value $record.authorizationRun -Expected @(
    'repository', 'workflowPath', 'runId', 'runAttempt', 'ref', 'commitSha', 'actor', 'actorId'
) -Label 'CTO authorization run'
Assert-CtoContextProperties -Value $record.sourceRelease -Expected @(
    'repository', 'workflowPath', 'workflowRef', 'runId', 'runAttempt', 'ref', 'commitSha', 'status', 'conclusion'
) -Label 'CTO authorization source release'
Assert-CtoContextProperties -Value $record.preflightProducerRun -Expected @(
    'repository', 'workflowPath', 'workflowRef', 'runId', 'runAttempt', 'ref', 'commitSha', 'status', 'conclusion'
) -Label 'CTO authorization preflight producer run'
$actors = ConvertTo-AuthorizedActorIdSet -Value $CtoAuthorizedActorIdAllowlist
$recordIssued = [DateTimeOffset]::MinValue
$recordExpiry = [DateTimeOffset]::MinValue
if ([string]$record.schemaVersion -cne '2.1.0' -or
    [string]$record.decision -cne 'AUTHORIZE' -or
    [string]$record.stage -cne 'Production' -or
    [string]$record.environment -cne [string]$stagePolicy.githubEnvironment -or
    [string]$record.applicationSha256 -cne $ExpectedAppSha256.ToLowerInvariant() -or
    [string]$record.releaseManifestSha256 -cne $releaseManifestSha -or
    [string]$record.releaseCommitSha -cne $releaseCommit -or
    -not (Test-CtoContextRunBinding -Binding $record.sourceRelease -Expected $release.provenance.run) -or
    [string]$record.bundlePath -cne $bundlePath -or
    [string]$record.bundleSha256 -cne $ExpectedBundleSha256.ToLowerInvariant() -or
    [string]$record.targetFingerprint -cne $targetFingerprint -or
    [string]$record.preflightEvidenceSha256 -cne (Get-Sha256Lower -Path $preflightPath) -or
    -not (Test-CtoContextRunBinding -Binding $record.preflightProducerRun -Expected $preflight.provenance.run) -or
    [string]$record.authorizedExecution.topLevelCallerWorkflowRef -cne $AuthorizedTopLevelCallerWorkflowRef -or
    [string]$record.authorizedExecution.producerWorkflowRef -cne $AuthorizedProducerWorkflowRef -or
    [string]$record.ctoApprovalReference -cne $ApprovalReference -or
    [string]$record.sourceChangeRecordSha256 -cne
        (Get-Sha256Lower -Path (Join-Path $cto.artifactRoot 'source-change-record.json')) -or
    [string]$record.producerBindings.preflight.runId -cne [string]$preflight.provenance.run.runId -or
    [string]$record.producerBindings.preflight.artifactSha256 -cne
        [string]$preflight.provenance.artifact.archiveSha256 -or
    [string]$record.producerBindings.preflight.contentManifestSha256 -cne
        [string]$preflight.provenance.contentManifestSha256 -or
    [string]$record.producerBindings.preparedInputs.runId -cne
        [string]$preparedInput.provenance.run.runId -or
    [string]$record.producerBindings.preparedInputs.artifactSha256 -cne
        [string]$preparedInput.provenance.artifact.archiveSha256 -or
    [string]$record.producerBindings.preparedInputs.contentManifestSha256 -cne
        [string]$preparedInput.provenance.contentManifestSha256 -or
    [string]$record.authorizedByActor -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$' -or
    -not $actors.Contains([string]$record.authorizedByActorId) -or
    [string]$record.authorizationRun.repository -cne [string]$cto.provenance.run.repository -or
    [string]$record.authorizationRun.workflowPath -cne [string]$cto.provenance.run.workflowPath -or
    [string]$record.authorizationRun.runId -cne [string]$cto.provenance.run.runId -or
    [int]$record.authorizationRun.runAttempt -ne [int]$cto.provenance.run.runAttempt -or
    [string]$record.authorizationRun.ref -cne [string]$cto.provenance.run.ref -or
    [string]$record.authorizationRun.commitSha -cne [string]$cto.provenance.run.commitSha -or
    [string]$record.authorizationRun.actor -cne [string]$record.authorizedByActor -or
    [string]$record.authorizationRun.actorId -cne [string]$record.authorizedByActorId -or
    -not [DateTimeOffset]::TryParse([string]$record.issuedAtUtc, [ref]$recordIssued) -or
    $recordIssued.Offset -ne [TimeSpan]::Zero -or
    $recordIssued -gt [DateTimeOffset]::UtcNow.AddMinutes([int]$policy.provenance.maxClockSkewMinutes) -or
    -not [DateTimeOffset]::TryParse([string]$record.expiresAtUtc, [ref]$recordExpiry) -or
    $recordExpiry.Offset -ne [TimeSpan]::Zero -or
    $recordExpiry -le [DateTimeOffset]::UtcNow -or
    $recordExpiry -le $recordIssued -or
    $recordExpiry -gt $recordIssued.AddHours(24)) {
    throw 'CTO authorization record is invalid, expired, or not bound to the exact Production authorization context.'
}

$context = [ordered]@{
    schemaVersion = '2.1.0'
    status = 'PASS'
    stage = 'Production'
    environment = [string]$stagePolicy.githubEnvironment
    applicationSha256 = $ExpectedAppSha256.ToLowerInvariant()
    releaseManifestSha256 = $releaseManifestSha
    releaseCommitSha = $releaseCommit
    sourceRelease = $release.provenance.run
    bundlePath = $bundlePath
    bundleSha256 = $ExpectedBundleSha256.ToLowerInvariant()
    targetFingerprint = $targetFingerprint
    preflightEvidenceSha256 = Get-Sha256Lower -Path $preflightPath
    preflightProducerRun = $preflight.provenance.run
    preparedInputProducerRun = $preparedInput.provenance.run
    producerBindings = $record.producerBindings
    authorizedExecution = $record.authorizedExecution
    approvalReference = $ApprovalReference
    authorizedByActor = [string]$record.authorizedByActor
    authorizedByActorId = [string]$record.authorizedByActorId
    expiresAtUtc = $recordExpiry.ToUniversalTime().ToString('O')
    ctoAuthorizationSha256 = Get-Sha256Lower -Path $recordPath
    ctoAuthorizationProvenanceSha256 = Get-Sha256Lower -Path $CtoAuthorizationVerifiedProvenancePath
    sourceChangeRecordSha256 = [string]$record.sourceChangeRecordSha256
    materializedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
Write-Utf8Json -Value $context -Path $OutputPath -Depth 30
Write-Output "CTO-AUTHORIZATION-CONTEXT status=PASS stage=Production authorizationSha256=$($context.ctoAuthorizationSha256)"
