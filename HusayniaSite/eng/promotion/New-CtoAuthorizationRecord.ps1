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
    [string]$StageTargetMetadataPath,
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
    [string]$CtoApprovalReference,
    [Parameter(Mandatory = $true)]
    [string]$SourceChangeRecordPath,
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
    [string]$AuthorizationRepository,
    [Parameter(Mandatory = $true)]
    [string]$AuthorizationWorkflowPath,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[1-9][0-9]*$')]
    [string]$AuthorizationRunId,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$AuthorizationRunAttempt,
    [Parameter(Mandatory = $true)]
    [string]$AuthorizationRef,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{40}$')]
    [string]$AuthorizationCommitSha,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$PolicyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
. (Join-Path $PSScriptRoot 'StageTarget.Common.ps1')

function Assert-CtoExactProperties {
    param($Value, [string[]]$Expected, [string]$Label)

    if ($null -eq $Value -or @(Compare-Object ($Expected | Sort-Object) @(
            $Value.PSObject.Properties.Name | Sort-Object)).Count -ne 0) {
        throw "$Label does not exactly match the T21 v2 contract."
    }
}

function Get-CtoTopLevelWorkflowPath {
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

function Get-CtoPinnedWorkflowPath {
    param($Contract, [string]$WorkflowRef, [string]$Label)

    $prefix = "$([string]$Contract.repository)/"
    $at = if ([string]::IsNullOrWhiteSpace($WorkflowRef)) {
        -1
    }
    else {
        $WorkflowRef.LastIndexOf('@', [StringComparison]::Ordinal)
    }
    if (-not $WorkflowRef.StartsWith($prefix, [StringComparison]::Ordinal) -or
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

function Get-CtoResolverArtifact {
    param(
        [string]$VerifiedProvenancePath,
        [ValidateSet('release-c6', 'trusted-preflight', 'stage-operation-inputs')]
        [string]$ExpectedRole,
        $Policy,
        [string]$ExpectedAppSha,
        [string]$ExpectedBundleSha,
        [string]$ExpectedReleaseManifestSha,
        [string]$ExpectedReleaseCommitSha,
        [string]$ExpectedTopLevelCallerRef,
        [string]$ExpectedProducerRef
    )

    if ([string]::IsNullOrWhiteSpace($VerifiedProvenancePath) -or
        -not (Test-Path -LiteralPath $VerifiedProvenancePath -PathType Leaf)) {
        throw "CTO authorization verified provenance is missing before authorization: $ExpectedRole."
    }
    $provenancePath = (Resolve-Path -LiteralPath $VerifiedProvenancePath).Path
    if ([IO.Path]::GetFileName($provenancePath) -cne 'verified-provenance.json') {
        throw "CTO authorization requires resolver-created verified-provenance.json: $ExpectedRole."
    }
    $resolverRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $provenancePath)).Path
    $artifactRoot = Join-Path $resolverRoot 'artifact'
    if (-not (Test-Path -LiteralPath $artifactRoot -PathType Container)) {
        throw "CTO authorization resolver artifact is missing: $ExpectedRole."
    }
    $artifactRoot = (Resolve-Path -LiteralPath $artifactRoot).Path
    if (-not (Test-PathWithinDirectory -BasePath $resolverRoot -Path $artifactRoot)) {
        throw "CTO authorization resolver artifact escaped its consumer-created root: $ExpectedRole."
    }

    $verified = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json -DateKind String
    Assert-CtoExactProperties -Value $verified -Expected @(
        'schemaVersion', 'authority', 'expectedRole', 'run', 'artifact', 'contentManifestSha256', 'attestation'
    ) -Label "CTO $ExpectedRole verified provenance"
    Assert-CtoExactProperties -Value $verified.run -Expected @(
        'repository', 'workflowPath', 'workflowRef', 'runId', 'runAttempt', 'ref', 'commitSha', 'status', 'conclusion'
    ) -Label "CTO $ExpectedRole verified run"
    Assert-CtoExactProperties -Value $verified.artifact -Expected @(
        'id', 'name', 'archiveSha256', 'sizeBytes', 'createdAtUtc', 'expiresAtUtc'
    ) -Label "CTO $ExpectedRole verified artifact"
    if ([string]$verified.schemaVersion -cne '2.0.0' -or
        [string]$verified.authority -cne 'github-actions-api-and-sigstore-v1' -or
        [string]$verified.expectedRole -cne $ExpectedRole -or
        [string]$verified.run.repository -cne [string]$Policy.t21ProvenanceContract.repository -or
        [string]$verified.run.runId -notmatch '^[1-9][0-9]*$' -or
        [int]$verified.run.runAttempt -lt 1 -or
        [string]$verified.run.ref -cne [string]$Policy.t21ProvenanceContract.protectedRef -or
        [string]$verified.run.commitSha -notmatch '^[a-f0-9]{40}$' -or
        [string]$verified.artifact.id -notmatch '^[1-9][0-9]*$' -or
        [string]$verified.artifact.archiveSha256 -notmatch '^[a-f0-9]{64}$' -or
        [int64]$verified.artifact.sizeBytes -lt 1 -or
        [string]$verified.contentManifestSha256 -notmatch '^[a-f0-9]{64}$') {
        throw "CTO authorization verified provenance has malformed bindings: $ExpectedRole."
    }

    if ($ExpectedRole -eq 'release-c6') {
        if ([string]$verified.run.workflowPath -cne [string]$Policy.provenance.releaseWorkflowPath -or
            [string]$verified.run.workflowRef -cne
                "$([string]$Policy.t21ProvenanceContract.repository)/$([string]$Policy.provenance.releaseWorkflowPath)@$([string]$Policy.t21ProvenanceContract.protectedRef)" -or
            [string]$verified.run.status -cne 'completed' -or
            [string]$verified.run.conclusion -cne 'success' -or
            [string]$verified.artifact.name -notmatch '^husaynia-site-[0-9A-Za-z][0-9A-Za-z._-]{0,127}$' -or
            $null -ne $verified.attestation) {
            throw 'CTO authorization release C6 provenance is not the approved API-only release role.'
        }
        $releaseRoots = @(
            Get-ChildItem -LiteralPath $artifactRoot -Directory -Force |
                Where-Object { $_.Name -ceq [string]$verified.artifact.name }
        )
        if ($releaseRoots.Count -ne 1 -or
            @(Get-ChildItem -LiteralPath $artifactRoot -Force).Count -ne 1 -or
            -not (Test-PathWithinDirectory -BasePath $artifactRoot -Path $releaseRoots[0].FullName)) {
            throw 'CTO authorization release C6 root is not uniquely API-bound.'
        }
        return [pscustomobject]@{
            provenance = $verified
            artifactRoot = $releaseRoots[0].FullName
        }
    }

    $topLevelPath = Get-CtoTopLevelWorkflowPath -Contract $Policy.t21ProvenanceContract `
        -WorkflowRef $ExpectedTopLevelCallerRef -Label 'Authorized top-level caller workflow ref'
    $producerPath = Get-CtoPinnedWorkflowPath -Contract $Policy.t21ProvenanceContract `
        -WorkflowRef $ExpectedProducerRef -Label 'Authorized producer workflow ref'
    $role = @($Policy.t21ProvenanceContract.producerRoles | Where-Object {
        [string]$_.role -ceq $ExpectedRole
    })
    $caller = @($Policy.t21ProvenanceContract.callerMatrix | Where-Object {
        [string]$_.stage -ceq 'Production' -and
        [string]$_.workflowRef -ceq $ExpectedTopLevelCallerRef
    })
    $expectedArtifactName = if ($ExpectedRole -eq 'trusted-preflight') {
        "raw-production-preflight-$ExpectedAppSha"
    }
    else {
        "stage-operation-inputs-production-$ExpectedAppSha"
    }
    Assert-CtoExactProperties -Value $verified.attestation -Expected @(
        'predicateType', 'signerWorkflow', 'signerDigest', 'sourceRef', 'sourceCommitSha', 'status'
    ) -Label 'CTO trusted-preflight attestation'
    if ($role.Count -ne 1 -or [string]$role[0].workflowPath -cne $producerPath -or
        $caller.Count -ne 1 -or [string]$verified.run.workflowPath -cne $topLevelPath -or
        [string]$verified.run.workflowRef -cne $ExpectedTopLevelCallerRef -or
        [string]$verified.run.status -cne 'completed' -or
        [string]$verified.run.conclusion -cne 'success' -or
        [string]$verified.artifact.name -cne $expectedArtifactName -or
        [string]$verified.attestation.predicateType -cne
            [string]$Policy.t21ProvenanceContract.attestation.predicateType -or
        [string]$verified.attestation.signerWorkflow -cne $ExpectedProducerRef.Split('@')[0] -or
        [string]$verified.attestation.signerDigest -cne $ExpectedProducerRef.Split('@')[1] -or
        [string]$verified.attestation.sourceRef -cne [string]$Policy.t21ProvenanceContract.protectedRef -or
        [string]$verified.attestation.sourceCommitSha -cne [string]$verified.run.commitSha -or
        [string]$verified.attestation.status -cne 'verified') {
        throw 'CTO authorization preflight provenance is not bound to the exact authorized caller and pinned producer.'
    }
    $manifestPath = Join-Path $artifactRoot 't21-producer-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        (Get-Sha256Lower -Path $manifestPath) -cne [string]$verified.contentManifestSha256) {
        throw 'CTO authorization preflight producer manifest is missing or changed.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -DateKind String
    & (Join-Path $PSScriptRoot 'New-T21ProducerManifest.ps1') -Mode Validate `
        -ArtifactRoot $artifactRoot -ProducerRole $ExpectedRole -Stage Production `
        -ApplicationSha256 $ExpectedAppSha -BundleSha256 $ExpectedBundleSha `
        -ReleaseManifestSha256 $ExpectedReleaseManifestSha -ReleaseCommitSha $ExpectedReleaseCommitSha `
        -ReleaseRunId ([string]$manifest.release.runId) -ReleaseRunAttempt ([int]$manifest.release.runAttempt) `
        -ProducerRunId ([string]$verified.run.runId) -ProducerRunAttempt ([int]$verified.run.runAttempt) `
        -ProducerCommitSha ([string]$verified.run.commitSha) `
        -TopLevelCallerWorkflowRef $ExpectedTopLevelCallerRef -ProducerWorkflowRef $ExpectedProducerRef `
        -PolicyPath $PolicyPath | Out-Null
    if (-not $?) {
        throw 'CTO authorization preflight producer manifest validation failed.'
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
$stagePolicy = @($policy.stages | Where-Object { [string]$_.name -ceq 'Production' })
if ($stagePolicy.Count -ne 1) {
    throw 'Promotion policy must contain exactly one Production stage.'
}
$stagePolicy = Assert-StageDeploymentEnabled -Policy $policy -Stage Production `
    -Operation 'CTO deployment authorization recording'
$contract = $policy.t21ProvenanceContract
Assert-CtoExactProperties -Value $contract -Expected @(
    'attestation', 'callerMatrix', 'producerRoles', 'protectedRef', 'repository', 'schemaVersion'
) -Label 'T21 provenance policy'
if ([string]$contract.schemaVersion -cne '2.1.0' -or
    [string]$contract.repository -cne [string]$policy.provenance.repository -or
    [string]$contract.protectedRef -cne [string]$policy.provenance.protectedRef) {
    throw 'T21 provenance policy identity is invalid.'
}

$actorIds = ConvertTo-AuthorizedActorIdSet -Value $AuthorizedActorIdAllowlist
if ($AuthorizationRepository -cne [string]$contract.repository -or
    $AuthorizationWorkflowPath -cne [string]$policy.provenance.ctoAuthorizationWorkflowPath -or
    $AuthorizationRef -cne [string]$contract.protectedRef -or
    $AuthorizedByActor -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$' -or
    -not $actorIds.Contains($AuthorizedByActorId) -or
    $CtoApprovalReference -notmatch '^[A-Za-z0-9][A-Za-z0-9._:/#-]{0,127}$') {
    throw 'CTO authorization identity, immutable actor allowlist, or approval reference is invalid.'
}
$issued = [DateTimeOffset]::UtcNow
$expiry = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse($ExpiresAtUtc, [ref]$expiry) -or
    $expiry.Offset -ne [TimeSpan]::Zero -or
    $expiry -le $issued -or $expiry -gt $issued.AddHours(24)) {
    throw 'CTO authorization expiry must be a future UTC time no more than 24 hours from issuance.'
}

$release = Get-CtoResolverArtifact -VerifiedProvenancePath $ReleaseVerifiedProvenancePath `
    -ExpectedRole release-c6 -Policy $policy -ExpectedAppSha $ExpectedAppSha256.ToLowerInvariant() `
    -ExpectedBundleSha $ExpectedBundleSha256.ToLowerInvariant()
$releaseRoot = (Resolve-Path -LiteralPath $release.artifactRoot).Path
Invoke-CheckedScript -Path (Join-Path $repositoryRoot 'eng\artifact\Test-ReleaseArtifact.ps1') `
    -Parameters @{
        ArtifactRoot = $releaseRoot
        ExpectedAppSha256 = $ExpectedAppSha256
        PolicyPath = $PolicyPath
    } -Label 'verify CTO-authorized release artifact'
$releaseManifestPath = Join-Path $releaseRoot 'release\release-manifest.json'
$releaseManifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json -DateKind String
$releaseManifestSha = Get-Sha256Lower -Path $releaseManifestPath
$releaseCommit = ([string]$releaseManifest.commitSha).ToLowerInvariant()
$bundlePath = 'operations/protected-execution-bundle.zip'
$releaseBundle = Join-Path $releaseRoot $bundlePath
$bundleEntry = @($releaseManifest.files | Where-Object { [string]$_.path -ceq $bundlePath })
if ([string]$release.provenance.contentManifestSha256 -cne $releaseManifestSha -or
    [string]$release.provenance.run.commitSha -cne $releaseCommit -or
    (Get-Sha256Lower -Path (Join-Path $releaseRoot 'app\Husaynia.Web.zip')) -cne
        $ExpectedAppSha256.ToLowerInvariant() -or
    $bundleEntry.Count -ne 1 -or
    [string]$bundleEntry[0].sha256 -cne $ExpectedBundleSha256.ToLowerInvariant() -or
    -not (Test-Path -LiteralPath $releaseBundle -PathType Leaf) -or
    (Get-Sha256Lower -Path $releaseBundle) -cne $ExpectedBundleSha256.ToLowerInvariant()) {
    throw 'CTO authorization release C6 provenance does not bind the verified app, manifest, commit, and bundle.'
}
$target = Read-StageTargetMetadata -Path $StageTargetMetadataPath -Stage Production -StagePolicy $stagePolicy
$targetFingerprint = Get-StageTargetFingerprint -TargetMetadata $target
$targetMetadataSha = Get-Sha256Lower -Path $StageTargetMetadataPath
$preflight = Get-CtoResolverArtifact -VerifiedProvenancePath $PreflightVerifiedProvenancePath `
    -ExpectedRole trusted-preflight -Policy $policy -ExpectedAppSha $ExpectedAppSha256.ToLowerInvariant() `
    -ExpectedBundleSha $ExpectedBundleSha256.ToLowerInvariant() -ExpectedReleaseManifestSha $releaseManifestSha `
    -ExpectedReleaseCommitSha $releaseCommit -ExpectedTopLevelCallerRef $AuthorizedTopLevelCallerWorkflowRef `
    -ExpectedProducerRef $AuthorizedProducerWorkflowRef
$preflightPath = Join-Path $preflight.artifactRoot 'migration-preflight.json'
if (-not (Test-Path -LiteralPath $preflightPath -PathType Leaf)) {
    throw 'CTO authorization verified preflight evidence is missing.'
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
    [string]$preflightEvidence.operation.stageTargetMetadataSha256 -cne $targetMetadataSha -or
    [string]$preflightEvidence.operation.databaseTargetFingerprint -cne $targetFingerprint -or
    [string]$preflightEvidence.operation.sqlServerResourceId -cne [string]$target.sqlServerResourceId -or
    [string]$preflightEvidence.operation.sqlDatabaseResourceId -cne [string]$target.sqlDatabaseResourceId -or
    -not [DateTimeOffset]::TryParse([string]$preflightEvidence.observedAtUtc, [ref]$observed) -or
    $observed.Offset -ne [TimeSpan]::Zero -or
    $observed -gt $issued.AddMinutes([int]$policy.provenance.maxClockSkewMinutes) -or
    $observed -lt $issued.AddMinutes(-[int]$stagePolicy.maxPreflightAgeMinutes)) {
    throw 'CTO authorization requires a fresh trusted successful preflight bound to the exact Production target.'
}

$preparedInput = Get-CtoResolverArtifact -VerifiedProvenancePath $PreparedInputVerifiedProvenancePath `
    -ExpectedRole stage-operation-inputs -Policy $policy `
    -ExpectedAppSha $ExpectedAppSha256.ToLowerInvariant() `
    -ExpectedBundleSha $ExpectedBundleSha256.ToLowerInvariant() `
    -ExpectedReleaseManifestSha $releaseManifestSha -ExpectedReleaseCommitSha $releaseCommit `
    -ExpectedTopLevelCallerRef $AuthorizedTopLevelCallerWorkflowRef `
    -ExpectedProducerRef $PreparedInputProducerWorkflowRef
$preparedInputPath = Join-Path $preparedInput.artifactRoot 'stage-operation-inputs.json'
if (-not (Test-Path -LiteralPath $preparedInputPath -PathType Leaf)) {
    throw 'CTO authorization verified prepared-input manifest is missing.'
}
$preparedInputRecord = Get-Content -LiteralPath $preparedInputPath -Raw | ConvertFrom-Json -DateKind String
Assert-CtoExactProperties -Value $preparedInputRecord -Expected @(
    'schemaVersion', 'stage', 'artifactSha256', 'releaseManifestSha256', 'releaseVersion',
    'releaseCommitSha', 'targetFingerprint', 'sourceRelease', 'releaseBinding',
    'productionAuthorizationRequest', 'observedAtUtc', 'reports'
) -Label 'Production prepared-input record'
Assert-CtoExactProperties -Value $preparedInputRecord.releaseBinding -Expected @(
    'runId', 'applicationSha256', 'bundleSha256', 'manifestSha256', 'commitSha'
) -Label 'Production prepared-input release binding'
Assert-CtoExactProperties -Value $preparedInputRecord.productionAuthorizationRequest -Expected @(
    'stage', 'releaseBinding', 'targetFingerprint', 'ctoApprovalReference'
) -Label 'Production authorization request'
if ([string]$preparedInputRecord.schemaVersion -cne '2.1.0' -or
    [string]$preparedInputRecord.stage -cne 'Production' -or
    [string]$preparedInputRecord.artifactSha256 -cne $ExpectedAppSha256.ToLowerInvariant() -or
    [string]$preparedInputRecord.releaseManifestSha256 -cne $releaseManifestSha -or
    [string]$preparedInputRecord.releaseCommitSha -cne $releaseCommit -or
    [string]$preparedInputRecord.targetFingerprint -cne $targetFingerprint -or
    [string]$preparedInputRecord.releaseBinding.runId -cne [string]$release.provenance.run.runId -or
    [string]$preparedInputRecord.releaseBinding.applicationSha256 -cne $ExpectedAppSha256.ToLowerInvariant() -or
    [string]$preparedInputRecord.releaseBinding.bundleSha256 -cne $ExpectedBundleSha256.ToLowerInvariant() -or
    [string]$preparedInputRecord.releaseBinding.manifestSha256 -cne $releaseManifestSha -or
    [string]$preparedInputRecord.releaseBinding.commitSha -cne $releaseCommit -or
    [string]$preparedInputRecord.productionAuthorizationRequest.stage -cne 'Production' -or
    [string]$preparedInputRecord.productionAuthorizationRequest.targetFingerprint -cne $targetFingerprint -or
    [string]$preparedInputRecord.productionAuthorizationRequest.ctoApprovalReference -cne $CtoApprovalReference -or
    (@($preparedInputRecord.productionAuthorizationRequest.releaseBinding | ConvertTo-Json -Compress) -join '') -cne
        (@($preparedInputRecord.releaseBinding | ConvertTo-Json -Compress) -join '')) {
    throw 'CTO authorization prepared-input request is not bound to completed R/I and the exact target/reference.'
}
if (-not (Test-Path -LiteralPath $SourceChangeRecordPath -PathType Leaf)) {
    throw 'CTO authorization checked source change record is missing.'
}
$sourceChangeRecord = Get-Content -LiteralPath $SourceChangeRecordPath -Raw | ConvertFrom-Json -DateKind String
$changeExpiry = [DateTimeOffset]::MinValue
if ([string]$sourceChangeRecord.schemaVersion -cne '1.0.0' -or
    [string]$sourceChangeRecord.stage -cne 'Production' -or
    [string]$sourceChangeRecord.changeId -cne $CtoApprovalReference -or
    [string]$sourceChangeRecord.decision -cne 'APPROVED' -or
    [string]::IsNullOrWhiteSpace([string]$sourceChangeRecord.owner) -or
    [string]$sourceChangeRecord.artifactSha256 -cne $ExpectedAppSha256.ToLowerInvariant() -or
    [string]$sourceChangeRecord.releaseManifestSha256 -cne $releaseManifestSha -or
    -not [DateTimeOffset]::TryParse([string]$sourceChangeRecord.expiresAtUtc, [ref]$changeExpiry) -or
    $changeExpiry.Offset -ne [TimeSpan]::Zero -or $changeExpiry -le $issued) {
    throw 'CTO authorization checked source change record is not approved, fresh, and release-bound.'
}

$record = [ordered]@{
    schemaVersion = '2.1.0'
    decision = $Decision
    stage = 'Production'
    environment = [string]$stagePolicy.githubEnvironment
    applicationSha256 = $ExpectedAppSha256.ToLowerInvariant()
    releaseManifestSha256 = $releaseManifestSha
    releaseCommitSha = $releaseCommit
    sourceRelease = $release.provenance.run
    releaseBinding = [ordered]@{
        runId = [string]$release.provenance.run.runId
        applicationSha256 = $ExpectedAppSha256.ToLowerInvariant()
        bundleSha256 = $ExpectedBundleSha256.ToLowerInvariant()
        manifestSha256 = $releaseManifestSha
        commitSha = $releaseCommit
    }
    bundlePath = $bundlePath
    bundleSha256 = $ExpectedBundleSha256.ToLowerInvariant()
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
    ctoApprovalReference = $CtoApprovalReference
    sourceChangeRecordSha256 = Get-Sha256Lower -Path $SourceChangeRecordPath
    authorizedByActor = $AuthorizedByActor
    authorizedByActorId = $AuthorizedByActorId
    issuedAtUtc = $issued.ToString('O')
    expiresAtUtc = $expiry.ToUniversalTime().ToString('O')
    authorizationRun = [ordered]@{
        repository = $AuthorizationRepository
        workflowPath = $AuthorizationWorkflowPath
        runId = $AuthorizationRunId
        runAttempt = $AuthorizationRunAttempt
        ref = $AuthorizationRef
        commitSha = $AuthorizationCommitSha.ToLowerInvariant()
        actor = $AuthorizedByActor
        actorId = $AuthorizedByActorId
    }
}
Write-Utf8Json -Value $record -Path $OutputPath -Depth 30
Write-Output "CTO-AUTHORIZATION decision=$Decision stage=Production releaseManifestSha256=$releaseManifestSha"
