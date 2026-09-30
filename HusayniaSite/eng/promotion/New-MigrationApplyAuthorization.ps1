[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Development', 'Staging', 'Production')]
    [string]$Stage,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ExpectedAppSha256,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ExpectedBundleSha256,
    [Parameter(Mandatory = $true)]
    [string]$ReleaseVerifiedProvenancePath,
    [Parameter(Mandatory = $true)]
    [string]$StageTargetMetadataPath,
    [Parameter(Mandatory = $true)]
    [string]$PreflightVerifiedProvenancePath,
    [Parameter(Mandatory = $true)]
    [string]$PreparedInputVerifiedProvenancePath,
    [Parameter(Mandatory = $true)]
    [string]$AuthorizedTopLevelCallerWorkflowRef,
    [Parameter(Mandatory = $true)]
    [string]$AuthorizedProducerWorkflowRef,
    [Parameter(Mandatory = $true)]
    [string]$PreparedInputProducerWorkflowRef,
    [Parameter(Mandatory = $true)]
    [ValidateSet('AUTHORIZE', 'DENY')]
    [string]$Decision,
    [Parameter(Mandatory = $true)]
    [string]$ExpiresAtUtc,
    [Parameter(Mandatory = $true)]
    [string]$AuthorizedByActor,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[1-9][0-9]*$')]
    [string]$AuthorizedByActorId,
    [Parameter(Mandatory = $true)]
    [string]$AuthorizedActorIdAllowlist,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [ValidatePattern('^[1-9][0-9]*$')]
    [string]$CtoAuthorizationRunId,
    [string]$CtoApprovalReference,
    [string]$CtoAuthorizationVerifiedProvenancePath,
    [string]$CtoAuthorizationContextPath,
    [string]$CtoAuthorizationProducerWorkflowRef,
    [string]$PolicyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
. (Join-Path $PSScriptRoot 'StageTarget.Common.ps1')

function Assert-MigrationExactProperties {
    param($Value, [string[]]$Expected, [string]$Label)

    if ($null -eq $Value -or @(Compare-Object ($Expected | Sort-Object) @(
            $Value.PSObject.Properties.Name | Sort-Object)).Count -ne 0) {
        throw "$Label does not exactly match the migration authorization v2 contract."
    }
}

function Get-MigrationTopLevelWorkflowPath {
    param($Contract, [string]$WorkflowRef, [string]$Label)

    $prefix = "$([string]$Contract.repository)/"
    $suffix = "@$([string]$Contract.protectedRef)"
    if ([string]::IsNullOrWhiteSpace($WorkflowRef) -or
        -not $WorkflowRef.StartsWith($prefix, [StringComparison]::Ordinal) -or
        -not $WorkflowRef.EndsWith($suffix, [StringComparison]::Ordinal)) {
        throw "$Label is not the exact protected-main workflow ref."
    }
    $workflowPath = $WorkflowRef.Substring($prefix.Length, $WorkflowRef.Length - $prefix.Length - $suffix.Length)
    if ($workflowPath -notmatch '^\.github/workflows/[A-Za-z0-9_.-]+\.yml$') {
        throw "$Label workflow path is malformed."
    }
    return $workflowPath
}

function Get-MigrationPinnedWorkflowPath {
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
    $workflowPath = $WorkflowRef.Substring($prefix.Length, $at - $prefix.Length)
    $digest = $WorkflowRef.Substring($at + 1)
    if ($workflowPath -notmatch '^\.github/workflows/[A-Za-z0-9_.-]+\.yml$' -or
        $digest -notmatch '^[a-f0-9]{40}$' -or $digest -ceq ('0' * 40)) {
        throw "$Label is malformed or all-zero."
    }
    return $workflowPath
}

function Assert-MigrationRunBinding {
    param($Actual, $Expected, [string]$Label)

    Assert-MigrationExactProperties -Value $Actual -Expected @(
        'repository', 'workflowPath', 'workflowRef', 'runId', 'runAttempt', 'ref', 'commitSha', 'status', 'conclusion'
    ) -Label $Label
    if ([string]$Actual.repository -cne [string]$Expected.repository -or
        [string]$Actual.workflowPath -cne [string]$Expected.workflowPath -or
        [string]$Actual.workflowRef -cne [string]$Expected.workflowRef -or
        [string]$Actual.runId -cne [string]$Expected.runId -or
        [int]$Actual.runAttempt -ne [int]$Expected.runAttempt -or
        [string]$Actual.ref -cne [string]$Expected.ref -or
        [string]$Actual.commitSha -cne [string]$Expected.commitSha -or
        [string]$Actual.status -cne [string]$Expected.status -or
        [string]$Actual.conclusion -cne [string]$Expected.conclusion) {
        throw "$Label is not bound to the resolver-verified producer run."
    }
}

function Get-MigrationVerifiedArtifact {
    param(
        [string]$VerifiedProvenancePath,
        [ValidateSet('release-c6', 'trusted-preflight', 'stage-operation-inputs', 'cto-authorization')]
        [string]$ExpectedRole,
        [string]$ExpectedArtifactName,
        [string]$ExpectedReleaseManifestSha256,
        [string]$ExpectedReleaseCommitSha,
        [string]$ExpectedTopLevelCallerWorkflowRef,
        [string]$ExpectedProducerWorkflowRef
    )

    if ([string]::IsNullOrWhiteSpace($VerifiedProvenancePath) -or
        -not (Test-Path -LiteralPath $VerifiedProvenancePath -PathType Leaf)) {
        throw "Migration authorization verified provenance is missing before authorization: $ExpectedRole."
    }
    $provenancePath = (Resolve-Path -LiteralPath $VerifiedProvenancePath).Path
    if ([IO.Path]::GetFileName($provenancePath) -cne 'verified-provenance.json') {
        throw "Migration authorization requires consumer-created verified-provenance.json: $ExpectedRole."
    }
    $resolverRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $provenancePath)).Path
    $artifactRoot = Join-Path $resolverRoot 'artifact'
    if (-not (Test-Path -LiteralPath $artifactRoot -PathType Container)) {
        throw "Migration authorization resolver artifact is missing: $ExpectedRole."
    }
    $artifactRoot = (Resolve-Path -LiteralPath $artifactRoot).Path
    if (-not (Test-PathWithinDirectory -BasePath $resolverRoot -Path $artifactRoot)) {
        throw "Migration authorization resolver artifact escaped its consumer-created root: $ExpectedRole."
    }

    $verified = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json -DateKind String
    Assert-MigrationExactProperties -Value $verified -Expected @(
        'schemaVersion', 'authority', 'expectedRole', 'run', 'artifact', 'contentManifestSha256', 'attestation'
    ) -Label "Migration $ExpectedRole verified provenance"
    Assert-MigrationExactProperties -Value $verified.run -Expected @(
        'repository', 'workflowPath', 'workflowRef', 'runId', 'runAttempt', 'ref', 'commitSha', 'status', 'conclusion'
    ) -Label "Migration $ExpectedRole verified run"
    Assert-MigrationExactProperties -Value $verified.artifact -Expected @(
        'id', 'name', 'archiveSha256', 'sizeBytes', 'createdAtUtc', 'expiresAtUtc'
    ) -Label "Migration $ExpectedRole verified artifact"
    if ([string]$verified.schemaVersion -cne '2.0.0' -or
        [string]$verified.authority -cne 'github-actions-api-and-sigstore-v1' -or
        [string]$verified.expectedRole -cne $ExpectedRole -or
        [string]$verified.run.repository -cne [string]$policy.t21ProvenanceContract.repository -or
        [string]$verified.run.workflowPath -notmatch '^\.github/workflows/[A-Za-z0-9_.-]+\.yml$' -or
        [string]$verified.run.workflowRef -cne
            "$([string]$verified.run.repository)/$([string]$verified.run.workflowPath)@$([string]$policy.t21ProvenanceContract.protectedRef)" -or
        [string]$verified.run.runId -notmatch '^[1-9][0-9]*$' -or
        [int]$verified.run.runAttempt -lt 1 -or
        [string]$verified.run.ref -cne [string]$policy.t21ProvenanceContract.protectedRef -or
        [string]$verified.run.commitSha -notmatch '^[a-f0-9]{40}$' -or
        [string]$verified.run.status -cne 'completed' -or
        [string]$verified.run.conclusion -cne 'success' -or
        [string]$verified.artifact.id -notmatch '^[1-9][0-9]*$' -or
        [string]$verified.artifact.archiveSha256 -notmatch '^[a-f0-9]{64}$' -or
        [int64]$verified.artifact.sizeBytes -lt 1 -or
        [string]$verified.contentManifestSha256 -notmatch '^[a-f0-9]{64}$') {
        throw "Migration authorization verified provenance has invalid API/artifact bindings: $ExpectedRole."
    }

    if ($ExpectedRole -eq 'release-c6') {
        if ([string]$verified.run.workflowPath -cne [string]$policy.provenance.releaseWorkflowPath -or
            [string]$verified.artifact.name -notmatch '^husaynia-site-[0-9A-Za-z][0-9A-Za-z._-]{0,127}$' -or
            $null -ne $verified.attestation) {
            throw 'Migration authorization release C6 provenance is not the approved API-only release role.'
        }
        $releaseRoots = @(
            Get-ChildItem -LiteralPath $artifactRoot -Directory -Force |
                Where-Object { $_.Name -ceq [string]$verified.artifact.name }
        )
        if ($releaseRoots.Count -ne 1 -or @(Get-ChildItem -LiteralPath $artifactRoot -Force).Count -ne 1) {
            throw 'Migration authorization release C6 root is not uniquely API-bound.'
        }
        return [pscustomobject]@{
            provenance = $verified
            artifactRoot = $releaseRoots[0].FullName
        }
    }

    Assert-MigrationExactProperties -Value $verified.attestation -Expected @(
        'predicateType', 'signerWorkflow', 'signerDigest', 'sourceRef', 'sourceCommitSha', 'status'
    ) -Label "Migration $ExpectedRole attestation"
    $role = @($policy.t21ProvenanceContract.producerRoles | Where-Object {
        [string]$_.role -ceq $ExpectedRole
    })
    $topLevelPath = Get-MigrationTopLevelWorkflowPath -Contract $policy.t21ProvenanceContract `
        -WorkflowRef $ExpectedTopLevelCallerWorkflowRef -Label 'Expected top-level caller workflow ref'
    $producerPath = Get-MigrationPinnedWorkflowPath -Contract $policy.t21ProvenanceContract `
        -WorkflowRef $ExpectedProducerWorkflowRef -Label 'Expected producer workflow ref'
    if ($role.Count -ne 1 -or [string]$role[0].workflowPath -cne $producerPath -or
        [string]$verified.artifact.name -cne $ExpectedArtifactName -or
        [string]$verified.run.workflowPath -cne $topLevelPath -or
        [string]$verified.run.workflowRef -cne $ExpectedTopLevelCallerWorkflowRef -or
        [string]$verified.attestation.predicateType -cne
            [string]$policy.t21ProvenanceContract.attestation.predicateType -or
        [string]$verified.attestation.signerWorkflow -cne $ExpectedProducerWorkflowRef.Split('@')[0] -or
        [string]$verified.attestation.signerDigest -cne $ExpectedProducerWorkflowRef.Split('@')[1] -or
        [string]$verified.attestation.sourceRef -cne [string]$policy.t21ProvenanceContract.protectedRef -or
        [string]$verified.attestation.sourceCommitSha -cne [string]$verified.run.commitSha -or
        [string]$verified.attestation.status -cne 'verified') {
        throw "Migration authorization $ExpectedRole provenance is not bound to the exact caller and pinned producer."
    }
    if ($ExpectedRole -in @('trusted-preflight', 'stage-operation-inputs') -and
        @($policy.t21ProvenanceContract.callerMatrix | Where-Object {
            [string]$_.stage -ceq $Stage -and
            [string]$_.workflowRef -ceq $ExpectedTopLevelCallerWorkflowRef
        }).Count -ne 1) {
        throw 'Migration authorization preflight caller is not in the exact stage caller matrix.'
    }
    if ($ExpectedRole -eq 'cto-authorization' -and $Stage -ne 'Production') {
        throw 'Migration authorization CTO provenance is restricted to Production.'
    }
    $manifestPath = Join-Path $artifactRoot 't21-producer-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        (Get-Sha256Lower -Path $manifestPath) -cne [string]$verified.contentManifestSha256) {
        throw "Migration authorization $ExpectedRole producer manifest is missing or changed."
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -DateKind String
    & (Join-Path $PSScriptRoot 'New-T21ProducerManifest.ps1') -Mode Validate `
        -ArtifactRoot $artifactRoot -ProducerRole $ExpectedRole -Stage $Stage `
        -ApplicationSha256 $expectedAppSha -BundleSha256 $expectedBundleSha `
        -ReleaseManifestSha256 $ExpectedReleaseManifestSha256 -ReleaseCommitSha $ExpectedReleaseCommitSha `
        -ReleaseRunId ([string]$manifest.release.runId) -ReleaseRunAttempt ([int]$manifest.release.runAttempt) `
        -ProducerRunId ([string]$verified.run.runId) -ProducerRunAttempt ([int]$verified.run.runAttempt) `
        -ProducerCommitSha ([string]$verified.run.commitSha) `
        -TopLevelCallerWorkflowRef $ExpectedTopLevelCallerWorkflowRef `
        -ProducerWorkflowRef $ExpectedProducerWorkflowRef -PolicyPath $PolicyPath | Out-Null
    if (-not $?) {
        throw "Migration authorization $ExpectedRole producer manifest validation failed."
    }
    return [pscustomobject]@{
        provenance = $verified
        artifactRoot = $artifactRoot
    }
}

$repositoryRoot = Resolve-HusayniaRepositoryRoot
if ([string]::IsNullOrWhiteSpace($PolicyPath)) {
    $PolicyPath = Join-Path $repositoryRoot 'pipelines\config\promotion-policy.json'
}
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json -DateKind String
$stagePolicy = @($policy.stages | Where-Object { [string]$_.name -ceq $Stage })
if ($stagePolicy.Count -ne 1) {
    throw "Promotion policy must contain exactly one $Stage stage."
}
$stagePolicy = Assert-StageDeploymentEnabled -Policy $policy -Stage $Stage `
    -Operation 'Migration Apply authorization'
$contract = $policy.t21ProvenanceContract
Assert-MigrationExactProperties -Value $contract -Expected @(
    'attestation', 'callerMatrix', 'producerRoles', 'protectedRef', 'repository', 'schemaVersion'
) -Label 'T21 provenance policy'
if ([string]$contract.schemaVersion -cne '2.1.0' -or
    [string]$contract.repository -cne [string]$policy.provenance.repository -or
    [string]$contract.protectedRef -cne [string]$policy.provenance.protectedRef) {
    throw 'T21 provenance policy identity is invalid.'
}

$expectedAppSha = $ExpectedAppSha256.ToLowerInvariant()
$expectedBundleSha = $ExpectedBundleSha256.ToLowerInvariant()
$callerPath = Get-MigrationTopLevelWorkflowPath -Contract $contract `
    -WorkflowRef $AuthorizedTopLevelCallerWorkflowRef -Label 'Authorized caller workflow ref'
$producerPath = Get-MigrationPinnedWorkflowPath -Contract $contract `
    -WorkflowRef $AuthorizedProducerWorkflowRef -Label 'Authorized producer workflow ref'
$preflightRole = @($contract.producerRoles | Where-Object { [string]$_.role -ceq 'trusted-preflight' })
$caller = @($contract.callerMatrix | Where-Object {
    [string]$_.stage -ceq $Stage -and
    [string]$_.workflowRef -ceq $AuthorizedTopLevelCallerWorkflowRef
})
if ($caller.Count -ne 1 -or $preflightRole.Count -ne 1 -or
    [string]$preflightRole[0].workflowPath -cne $producerPath) {
    throw 'Migration authorization caller or reusable workflow is not exactly authorized for the selected stage.'
}
$actorIds = ConvertTo-AuthorizedActorIdSet -Value $AuthorizedActorIdAllowlist
if ([string]$AuthorizedByActor -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$' -or
    -not $actorIds.Contains($AuthorizedByActorId)) {
    throw 'Migration authorization actor is not in the protected immutable actor ID allowlist.'
}
$issued = [DateTimeOffset]::UtcNow
$expiry = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse($ExpiresAtUtc, [ref]$expiry) -or
    $expiry.Offset -ne [TimeSpan]::Zero -or
    $expiry -le $issued -or
    $expiry -gt $issued.AddHours(4)) {
    throw 'Migration authorization expiry must be a future UTC time no more than four hours from issuance.'
}
if ($Stage -eq 'Production') {
    if ($CtoAuthorizationRunId -notmatch '^[1-9][0-9]*$' -or
        [string]::IsNullOrWhiteSpace($CtoApprovalReference) -or
        $CtoApprovalReference -notmatch '^[A-Za-z0-9][A-Za-z0-9._:/#-]{0,127}$' -or
        [string]::IsNullOrWhiteSpace($CtoAuthorizationVerifiedProvenancePath) -or
        [string]::IsNullOrWhiteSpace($CtoAuthorizationContextPath) -or
        [string]::IsNullOrWhiteSpace($CtoAuthorizationProducerWorkflowRef)) {
        throw 'Production migration authorization requires a valid CTO run selector, approval reference, and resolved CTO context.'
    }
}
elseif (-not [string]::IsNullOrWhiteSpace($CtoAuthorizationRunId) -or
        -not [string]::IsNullOrWhiteSpace($CtoApprovalReference) -or
        -not [string]::IsNullOrWhiteSpace($CtoAuthorizationVerifiedProvenancePath) -or
        -not [string]::IsNullOrWhiteSpace($CtoAuthorizationContextPath) -or
        -not [string]::IsNullOrWhiteSpace($CtoAuthorizationProducerWorkflowRef)) {
    throw 'Non-Production migration authorization must not accept CTO authorization selectors or context.'
}

$release = Get-MigrationVerifiedArtifact -VerifiedProvenancePath $ReleaseVerifiedProvenancePath `
    -ExpectedRole release-c6
Invoke-CheckedScript -Path (Join-Path $repositoryRoot 'eng\artifact\Test-ReleaseArtifact.ps1') `
    -Parameters @{
        ArtifactRoot = $release.artifactRoot
        ExpectedAppSha256 = $expectedAppSha
        PolicyPath = $PolicyPath
    } -Label 'verify migration-authorized release C6 artifact'
$releaseManifestPath = Join-Path $release.artifactRoot 'release\release-manifest.json'
$releaseManifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json -DateKind String
$releaseManifestSha = Get-Sha256Lower -Path $releaseManifestPath
$releaseCommitSha = ([string]$releaseManifest.commitSha).ToLowerInvariant()
$bundlePath = 'operations/protected-execution-bundle.zip'
$releaseBundle = @($releaseManifest.files | Where-Object { [string]$_.path -ceq $bundlePath })
if ([string]$release.provenance.contentManifestSha256 -cne $releaseManifestSha -or
    [string]$release.provenance.run.commitSha -cne $releaseCommitSha -or
    (Get-Sha256Lower -Path (Join-Path $release.artifactRoot 'app\Husaynia.Web.zip')) -cne $expectedAppSha -or
    $releaseBundle.Count -ne 1 -or
    [string]$releaseBundle[0].sha256 -cne $expectedBundleSha -or
    (Get-Sha256Lower -Path (Join-Path $release.artifactRoot $bundlePath)) -cne $expectedBundleSha) {
    throw 'Migration authorization release C6 does not bind the exact application, manifest, commit, and protected bundle.'
}

$target = Read-StageTargetMetadata -Path $StageTargetMetadataPath -Stage $Stage -StagePolicy $stagePolicy
$targetFingerprint = Get-StageTargetFingerprint -TargetMetadata $target
$targetMetadataSha = Get-Sha256Lower -Path $StageTargetMetadataPath
$preflight = Get-MigrationVerifiedArtifact -VerifiedProvenancePath $PreflightVerifiedProvenancePath `
    -ExpectedRole trusted-preflight -ExpectedArtifactName "raw-$($Stage.ToLowerInvariant())-preflight-$expectedAppSha" `
    -ExpectedReleaseManifestSha256 $releaseManifestSha -ExpectedReleaseCommitSha $releaseCommitSha `
    -ExpectedTopLevelCallerWorkflowRef $AuthorizedTopLevelCallerWorkflowRef `
    -ExpectedProducerWorkflowRef $AuthorizedProducerWorkflowRef
$preflightPath = Join-Path $preflight.artifactRoot 'migration-preflight.json'
if (-not (Test-Path -LiteralPath $preflightPath -PathType Leaf)) {
    throw 'Migration authorization verified preflight evidence is missing.'
}
$preflightEvidence = Get-Content -LiteralPath $preflightPath -Raw | ConvertFrom-Json -DateKind String
$observed = [DateTimeOffset]::MinValue
if ([string]$preflightEvidence.schemaVersion -cne '1.0.0' -or
    [string]$preflightEvidence.evidenceType -cne 'migration-preflight' -or
    [string]$preflightEvidence.status -cne 'PASS' -or
    [string]$preflightEvidence.stage -cne $Stage -or
    [string]$preflightEvidence.artifactSha256 -cne $expectedAppSha -or
    [string]$preflightEvidence.releaseManifestSha256 -cne $releaseManifestSha -or
    [string]$preflightEvidence.releaseCommitSha -cne $releaseCommitSha -or
    [string]$preflightEvidence.operation.bundlePath -cne $bundlePath -or
    [string]$preflightEvidence.operation.bundleSha256 -cne $expectedBundleSha -or
    [string]$preflightEvidence.operation.stageTargetMetadataSha256 -cne $targetMetadataSha -or
    [string]$preflightEvidence.operation.databaseTargetFingerprint -cne $targetFingerprint -or
    [string]$preflightEvidence.operation.sqlServerResourceId -cne [string]$target.sqlServerResourceId -or
    [string]$preflightEvidence.operation.sqlDatabaseResourceId -cne [string]$target.sqlDatabaseResourceId -or
    -not [DateTimeOffset]::TryParse([string]$preflightEvidence.observedAtUtc, [ref]$observed) -or
    $observed.Offset -ne [TimeSpan]::Zero -or
    $observed -gt $issued.AddMinutes([int]$policy.provenance.maxClockSkewMinutes) -or
    $observed -lt $issued.AddMinutes(-[int]$stagePolicy.maxPreflightAgeMinutes)) {
    throw 'Migration authorization requires a fresh trusted successful preflight bound to the exact release and target.'
}

$preparedInput = Get-MigrationVerifiedArtifact `
    -VerifiedProvenancePath $PreparedInputVerifiedProvenancePath `
    -ExpectedRole stage-operation-inputs `
    -ExpectedArtifactName "stage-operation-inputs-$($Stage.ToLowerInvariant())-$expectedAppSha" `
    -ExpectedReleaseManifestSha256 $releaseManifestSha `
    -ExpectedReleaseCommitSha $releaseCommitSha `
    -ExpectedTopLevelCallerWorkflowRef $AuthorizedTopLevelCallerWorkflowRef `
    -ExpectedProducerWorkflowRef $PreparedInputProducerWorkflowRef
$preparedInputPath = Join-Path $preparedInput.artifactRoot 'stage-operation-inputs.json'
if (-not (Test-Path -LiteralPath $preparedInputPath -PathType Leaf)) {
    throw 'Migration authorization verified prepared-input manifest is missing.'
}
$preparedInputRecord = Get-Content -LiteralPath $preparedInputPath -Raw | ConvertFrom-Json -DateKind String
Assert-MigrationExactProperties -Value $preparedInputRecord -Expected @(
    'schemaVersion', 'stage', 'artifactSha256', 'releaseManifestSha256', 'releaseVersion',
    'releaseCommitSha', 'targetFingerprint', 'sourceRelease', 'releaseBinding',
    'productionAuthorizationRequest', 'observedAtUtc', 'reports'
) -Label 'Migration prepared-input record'
if ([string]$preparedInputRecord.schemaVersion -cne '2.1.0' -or
    [string]$preparedInputRecord.stage -cne $Stage -or
    [string]$preparedInputRecord.artifactSha256 -cne $expectedAppSha -or
    [string]$preparedInputRecord.releaseManifestSha256 -cne $releaseManifestSha -or
    [string]$preparedInputRecord.releaseCommitSha -cne $releaseCommitSha -or
    [string]$preparedInputRecord.targetFingerprint -cne $targetFingerprint -or
    [string]$preparedInputRecord.releaseBinding.runId -cne [string]$release.provenance.run.runId -or
    [string]$preparedInputRecord.releaseBinding.applicationSha256 -cne $expectedAppSha -or
    [string]$preparedInputRecord.releaseBinding.bundleSha256 -cne $expectedBundleSha -or
    [string]$preparedInputRecord.releaseBinding.manifestSha256 -cne $releaseManifestSha -or
    [string]$preparedInputRecord.releaseBinding.commitSha -cne $releaseCommitSha) {
    throw 'Migration authorization prepared inputs are not bound to the exact completed release and target.'
}
if ($Stage -eq 'Production') {
    if ([string]$preparedInputRecord.productionAuthorizationRequest.ctoApprovalReference -cne
        $CtoApprovalReference) {
        throw 'Production migration authorization prepared-input approval reference is inconsistent.'
    }
}
elseif ($null -ne $preparedInputRecord.productionAuthorizationRequest) {
    throw 'Non-Production migration authorization received a Production authorization request.'
}

$productionCtoAuthorization = $null
if ($Stage -eq 'Production') {
    $ctoTopLevelRef = "$([string]$contract.repository)/$([string]$policy.provenance.ctoAuthorizationWorkflowPath)@$([string]$contract.protectedRef)"
    $cto = Get-MigrationVerifiedArtifact -VerifiedProvenancePath $CtoAuthorizationVerifiedProvenancePath `
        -ExpectedRole cto-authorization -ExpectedArtifactName "cto-authorization-$expectedAppSha" `
        -ExpectedReleaseManifestSha256 $releaseManifestSha -ExpectedReleaseCommitSha $releaseCommitSha `
        -ExpectedTopLevelCallerWorkflowRef $ctoTopLevelRef `
        -ExpectedProducerWorkflowRef $CtoAuthorizationProducerWorkflowRef
    if ([string]$cto.provenance.run.runId -cne $CtoAuthorizationRunId) {
        throw 'Production migration authorization CTO provenance does not bind the selected run.'
    }
    if (-not (Test-Path -LiteralPath $CtoAuthorizationContextPath -PathType Leaf)) {
        throw 'Production migration authorization CTO context is missing.'
    }
    $ctoRecordPath = Join-Path $cto.artifactRoot 'cto-authorization.json'
    if (-not (Test-Path -LiteralPath $ctoRecordPath -PathType Leaf)) {
        throw 'Production migration authorization resolved CTO record is missing.'
    }
    $ctoRecord = Get-Content -LiteralPath $ctoRecordPath -Raw | ConvertFrom-Json -DateKind String
    Assert-MigrationExactProperties -Value $ctoRecord -Expected @(
        'schemaVersion', 'decision', 'stage', 'environment', 'applicationSha256', 'releaseManifestSha256',
        'releaseCommitSha', 'sourceRelease', 'releaseBinding', 'bundlePath', 'bundleSha256', 'targetFingerprint',
        'preflightEvidenceSha256', 'preflightProducerRun', 'producerBindings', 'authorizedExecution',
        'ctoApprovalReference', 'sourceChangeRecordSha256',
        'authorizedByActor', 'authorizedByActorId', 'issuedAtUtc', 'expiresAtUtc', 'authorizationRun'
    ) -Label 'Production CTO authorization record'
    Assert-MigrationExactProperties -Value $ctoRecord.authorizedExecution -Expected @(
        'topLevelCallerWorkflowRef', 'producerWorkflowRef'
    ) -Label 'Production CTO authorization execution'
    Assert-MigrationExactProperties -Value $ctoRecord.authorizationRun -Expected @(
        'repository', 'workflowPath', 'runId', 'runAttempt', 'ref', 'commitSha', 'actor', 'actorId'
    ) -Label 'Production CTO authorization run'
    Assert-MigrationRunBinding -Actual $ctoRecord.sourceRelease -Expected $release.provenance.run `
        -Label 'Production CTO record source release'
    Assert-MigrationRunBinding -Actual $ctoRecord.preflightProducerRun -Expected $preflight.provenance.run `
        -Label 'Production CTO record preflight producer'
    $ctoRecordExpiry = [DateTimeOffset]::MinValue
    if ([string]$ctoRecord.schemaVersion -cne '2.1.0' -or
        [string]$ctoRecord.decision -cne 'AUTHORIZE' -or
        [string]$ctoRecord.stage -cne 'Production' -or
        [string]$ctoRecord.environment -cne [string]$stagePolicy.githubEnvironment -or
        [string]$ctoRecord.applicationSha256 -cne $expectedAppSha -or
        [string]$ctoRecord.releaseManifestSha256 -cne $releaseManifestSha -or
        [string]$ctoRecord.releaseCommitSha -cne $releaseCommitSha -or
        [string]$ctoRecord.bundlePath -cne $bundlePath -or
        [string]$ctoRecord.bundleSha256 -cne $expectedBundleSha -or
        [string]$ctoRecord.targetFingerprint -cne $targetFingerprint -or
        [string]$ctoRecord.preflightEvidenceSha256 -cne (Get-Sha256Lower -Path $preflightPath) -or
        [string]$ctoRecord.authorizedExecution.topLevelCallerWorkflowRef -cne $AuthorizedTopLevelCallerWorkflowRef -or
        [string]$ctoRecord.authorizedExecution.producerWorkflowRef -cne $AuthorizedProducerWorkflowRef -or
        [string]$ctoRecord.ctoApprovalReference -cne $CtoApprovalReference -or
        [string]$ctoRecord.producerBindings.preparedInputs.runId -cne
            [string]$preparedInput.provenance.run.runId -or
        [string]$ctoRecord.authorizedByActor -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$' -or
        [string]$ctoRecord.authorizedByActorId -notmatch '^[1-9][0-9]*$' -or
        [string]$ctoRecord.authorizationRun.repository -cne [string]$cto.provenance.run.repository -or
        [string]$ctoRecord.authorizationRun.workflowPath -cne [string]$cto.provenance.run.workflowPath -or
        [string]$ctoRecord.authorizationRun.runId -cne [string]$cto.provenance.run.runId -or
        [int]$ctoRecord.authorizationRun.runAttempt -ne [int]$cto.provenance.run.runAttempt -or
        [string]$ctoRecord.authorizationRun.ref -cne [string]$cto.provenance.run.ref -or
        [string]$ctoRecord.authorizationRun.commitSha -cne [string]$cto.provenance.run.commitSha -or
        [string]$ctoRecord.authorizationRun.actor -cne [string]$ctoRecord.authorizedByActor -or
        [string]$ctoRecord.authorizationRun.actorId -cne [string]$ctoRecord.authorizedByActorId -or
        -not [DateTimeOffset]::TryParse([string]$ctoRecord.expiresAtUtc, [ref]$ctoRecordExpiry) -or
        $ctoRecordExpiry.Offset -ne [TimeSpan]::Zero -or
        $ctoRecordExpiry -lt $expiry) {
        throw 'Production migration authorization CTO record is invalid or not bound to the exact request.'
    }
    $context = Get-Content -LiteralPath $CtoAuthorizationContextPath -Raw | ConvertFrom-Json -DateKind String
    Assert-MigrationExactProperties -Value $context -Expected @(
        'schemaVersion', 'status', 'stage', 'environment', 'applicationSha256', 'releaseManifestSha256',
        'releaseCommitSha', 'sourceRelease', 'bundlePath', 'bundleSha256', 'targetFingerprint',
        'preflightEvidenceSha256', 'preflightProducerRun', 'preparedInputProducerRun',
        'producerBindings', 'authorizedExecution', 'approvalReference',
        'authorizedByActor', 'authorizedByActorId', 'expiresAtUtc', 'ctoAuthorizationSha256',
        'ctoAuthorizationProvenanceSha256', 'sourceChangeRecordSha256', 'materializedAtUtc'
    ) -Label 'Production CTO authorization context'
    Assert-MigrationExactProperties -Value $context.authorizedExecution -Expected @(
        'topLevelCallerWorkflowRef', 'producerWorkflowRef'
    ) -Label 'Production CTO authorization execution'
    Assert-MigrationRunBinding -Actual $context.sourceRelease -Expected $release.provenance.run `
        -Label 'Production CTO context source release'
    Assert-MigrationRunBinding -Actual $context.preflightProducerRun -Expected $preflight.provenance.run `
        -Label 'Production CTO context preflight producer'
    $contextExpiry = [DateTimeOffset]::MinValue
    if ([string]$context.schemaVersion -cne '2.1.0' -or
        [string]$context.status -cne 'PASS' -or
        [string]$context.stage -cne 'Production' -or
        [string]$context.environment -cne [string]$stagePolicy.githubEnvironment -or
        [string]$context.applicationSha256 -cne $expectedAppSha -or
        [string]$context.releaseManifestSha256 -cne $releaseManifestSha -or
        [string]$context.releaseCommitSha -cne $releaseCommitSha -or
        [string]$context.bundlePath -cne $bundlePath -or
        [string]$context.bundleSha256 -cne $expectedBundleSha -or
        [string]$context.targetFingerprint -cne $targetFingerprint -or
        [string]$context.preflightEvidenceSha256 -cne (Get-Sha256Lower -Path $preflightPath) -or
        [string]$context.producerBindings.preparedInputs.runId -cne
            [string]$preparedInput.provenance.run.runId -or
        [string]$context.authorizedExecution.topLevelCallerWorkflowRef -cne $AuthorizedTopLevelCallerWorkflowRef -or
        [string]$context.authorizedExecution.producerWorkflowRef -cne $AuthorizedProducerWorkflowRef -or
        [string]$context.approvalReference -cne $CtoApprovalReference -or
        [string]$context.ctoAuthorizationProvenanceSha256 -cne
            (Get-Sha256Lower -Path $CtoAuthorizationVerifiedProvenancePath) -or
        [string]$context.ctoAuthorizationSha256 -cne (Get-Sha256Lower -Path $ctoRecordPath) -or
        -not [DateTimeOffset]::TryParse([string]$context.expiresAtUtc, [ref]$contextExpiry) -or
        $contextExpiry.Offset -ne [TimeSpan]::Zero -or
        $contextExpiry -lt $expiry) {
        throw 'Production migration authorization CTO context is invalid or not bound to the exact request.'
    }
    $productionCtoAuthorization = [ordered]@{
        runId = $CtoAuthorizationRunId
        approvalReference = $CtoApprovalReference
        contextSha256 = Get-Sha256Lower -Path $CtoAuthorizationContextPath
        authorizationSha256 = [string]$context.ctoAuthorizationSha256
        authorizationProvenanceSha256 = [string]$context.ctoAuthorizationProvenanceSha256
        sourceChangeRecordSha256 = [string]$context.sourceChangeRecordSha256
        expiresAtUtc = $contextExpiry.ToUniversalTime().ToString('O')
    }
}

$record = [ordered]@{
    schemaVersion = '2.1.0'
    decision = $Decision
    mode = 'Apply'
    operationOrder = 20
    stage = $Stage
    environment = [string]$stagePolicy.githubEnvironment
    applicationSha256 = $expectedAppSha
    releaseManifestSha256 = $releaseManifestSha
    releaseCommitSha = $releaseCommitSha
    sourceRelease = $release.provenance.run
    releaseBinding = [ordered]@{
        runId = [string]$release.provenance.run.runId
        applicationSha256 = $expectedAppSha
        bundleSha256 = $expectedBundleSha
        manifestSha256 = $releaseManifestSha
        commitSha = $releaseCommitSha
    }
    bundlePath = $bundlePath
    bundleSha256 = $expectedBundleSha
    targetFingerprint = $targetFingerprint
    preflightEvidenceSha256 = Get-Sha256Lower -Path $preflightPath
    preflightProducerRun = $preflight.provenance.run
    producerBindings = [ordered]@{
        preflight = [ordered]@{
            runId = [string]$preflight.provenance.run.runId
            artifactSha256 = [string]$preflight.provenance.artifact.archiveSha256
            contentManifestSha256 = [string]$preflight.provenance.contentManifestSha256
        }
        preparedInputs = [ordered]@{
            runId = [string]$preparedInput.provenance.run.runId
            artifactSha256 = [string]$preparedInput.provenance.artifact.archiveSha256
            contentManifestSha256 = [string]$preparedInput.provenance.contentManifestSha256
        }
    }
    authorizedExecution = [ordered]@{
        topLevelCallerWorkflowRef = $AuthorizedTopLevelCallerWorkflowRef
        producerWorkflowRef = $AuthorizedProducerWorkflowRef
    }
    authorizedByActor = $AuthorizedByActor
    authorizedByActorId = $AuthorizedByActorId
    issuedAtUtc = $issued.ToString('O')
    expiresAtUtc = $expiry.ToUniversalTime().ToString('O')
    productionCtoAuthorization = $productionCtoAuthorization
}
Write-Utf8Json -Value $record -Path $OutputPath -Depth 30
Write-Output "MIGRATION-AUTHORIZATION decision=$Decision stage=$Stage preflightSha256=$($record.preflightEvidenceSha256)"
