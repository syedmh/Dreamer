[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Preflight', 'StageOperations')]
    [string]$Operation,
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
    [string]$PreflightVerifiedProvenancePath,
    [string]$StageOperationInputVerifiedProvenancePath,
    [string]$MigrationAuthorizationVerifiedProvenancePath,
    [string]$CtoAuthorizationVerifiedProvenancePath,
    [Parameter(Mandatory = $true)]
    [string]$StageTargetMetadataPath,
    [string]$CtoApprovalReference,
    [Parameter(Mandatory = $true)]
    [string]$PolicyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
. (Join-Path $PSScriptRoot 'StageTarget.Common.ps1')

function Assert-T21ExactProperties {
    param($Value, [string[]]$Expected, [string]$Label)

    if ($null -eq $Value -or @(Compare-Object ($Expected | Sort-Object) @(
            $Value.PSObject.Properties.Name | Sort-Object)).Count -ne 0) {
        throw "$Label does not exactly match the T21 v2 contract."
    }
}

function Assert-T21PositiveInteger {
    param([string]$Value, [string]$Label)

    if ($Value -notmatch '^[1-9][0-9]*$') {
        throw "$Label is not a positive decimal selector."
    }
}

function Assert-T21Sha256 {
    param([string]$Value, [string]$Label)

    if ($Value -notmatch '^[a-f0-9]{64}$' -or $Value -ceq ('0' * 64)) {
        throw "$Label is not an exact nonzero SHA-256."
    }
}

function Assert-T21CommitSha {
    param([string]$Value, [string]$Label)

    if ($Value -notmatch '^[a-f0-9]{40}$' -or $Value -ceq ('0' * 40)) {
        throw "$Label is not an exact nonzero commit SHA."
    }
}

function Get-T21ArtifactName {
    param([string]$Role, [string]$StageName, [string]$AppSha, [string]$PreflightSha)

    $stageLower = $StageName.ToLowerInvariant()
    switch ($Role) {
        'trusted-preflight' { return "raw-$stageLower-preflight-$AppSha" }
        'stage-operation-inputs' { return "stage-operation-inputs-$stageLower-$AppSha" }
        'cto-authorization' { return "cto-authorization-$AppSha" }
        'migration-authorization' {
            Assert-T21Sha256 -Value $PreflightSha -Label 'Migration authorization preflight evidence SHA-256'
            return "migration-apply-authorization-$StageName-$AppSha-$PreflightSha"
        }
        default { throw "T21 resolver role has no canonical artifact name: $Role." }
    }
}

function Assert-T21RunBinding {
    param($Actual, $Expected, [string]$Label)

    Assert-T21ExactProperties -Value $Actual -Expected @(
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
        throw "$Label does not bind the exact resolver-verified API run."
    }
}

function Get-T21ResolvedArtifact {
    param(
        [string]$VerifiedProvenancePath,
        [string]$ExpectedRole,
        [string]$ExpectedPreflightEvidenceSha256 = ''
    )

    if ([string]::IsNullOrWhiteSpace($VerifiedProvenancePath) -or
        -not (Test-Path -LiteralPath $VerifiedProvenancePath -PathType Leaf)) {
        throw "Resolver-created verified provenance is missing: $ExpectedRole."
    }
    $provenancePath = (Resolve-Path -LiteralPath $VerifiedProvenancePath).Path
    if ([IO.Path]::GetFileName($provenancePath) -cne 'verified-provenance.json') {
        throw "Trusted input is not a resolver-created verified provenance path: $ExpectedRole."
    }
    $resolverRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $provenancePath)).Path
    $artifactContainer = Join-Path $resolverRoot 'artifact'
    if (-not (Test-Path -LiteralPath $artifactContainer -PathType Container) -or
        -not (Test-Path -LiteralPath (Join-Path $resolverRoot 'artifact.zip') -PathType Leaf)) {
        throw "Resolver-created artifact output is incomplete: $ExpectedRole."
    }
    $artifactContainer = (Resolve-Path -LiteralPath $artifactContainer).Path
    if (-not (Test-PathWithinDirectory -BasePath $resolverRoot -Path $artifactContainer)) {
        throw "Resolver-created artifact output escaped its canonical root: $ExpectedRole."
    }

    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json -DateKind String
    Assert-T21ExactProperties -Value $provenance -Expected @(
        'schemaVersion', 'authority', 'expectedRole', 'run', 'artifact', 'contentManifestSha256', 'attestation'
    ) -Label "$ExpectedRole verified provenance"
    Assert-T21ExactProperties -Value $provenance.run -Expected @(
        'repository', 'workflowPath', 'workflowRef', 'runId', 'runAttempt', 'ref', 'commitSha', 'status', 'conclusion'
    ) -Label "$ExpectedRole verified API run"
    Assert-T21ExactProperties -Value $provenance.artifact -Expected @(
        'id', 'name', 'archiveSha256', 'sizeBytes', 'createdAtUtc', 'expiresAtUtc'
    ) -Label "$ExpectedRole verified artifact"
    Assert-T21PositiveInteger -Value ([string]$provenance.run.runId) -Label "$ExpectedRole API run ID"
    Assert-T21PositiveInteger -Value ([string]$provenance.artifact.id) -Label "$ExpectedRole API artifact ID"
    Assert-T21CommitSha -Value ([string]$provenance.run.commitSha) -Label "$ExpectedRole API commit SHA"
    Assert-T21Sha256 -Value ([string]$provenance.artifact.archiveSha256) -Label "$ExpectedRole API artifact SHA-256"
    Assert-T21Sha256 -Value ([string]$provenance.contentManifestSha256) -Label "$ExpectedRole content-manifest SHA-256"
    if ([string]$provenance.schemaVersion -cne '2.0.0' -or
        [string]$provenance.authority -cne 'github-actions-api-and-sigstore-v1' -or
        [string]$provenance.expectedRole -cne $ExpectedRole -or
        [string]$provenance.run.repository -cne [string]$contract.repository -or
        [int]$provenance.run.runAttempt -lt 1 -or
        [string]$provenance.run.ref -cne [string]$contract.protectedRef -or
        [int64]$provenance.artifact.sizeBytes -lt 1) {
        throw "Resolver provenance has invalid v2 API bindings: $ExpectedRole."
    }

    $role = @($contract.producerRoles | Where-Object { [string]$_.role -ceq $ExpectedRole })
    if ($ExpectedRole -eq 'release-c6') {
        if ($role.Count -ne 1 -or
            [string]$provenance.run.workflowPath -cne [string]$policy.provenance.releaseWorkflowPath -or
            [string]$provenance.run.workflowRef -cne
                "$([string]$contract.repository)/$([string]$policy.provenance.releaseWorkflowPath)@$([string]$contract.protectedRef)" -or
            [string]$provenance.run.status -cne 'completed' -or
            [string]$provenance.run.conclusion -cne 'success' -or
            [string]$provenance.artifact.name -notmatch '^husaynia-site-[0-9A-Za-z][0-9A-Za-z._-]{0,127}$' -or
            $null -ne $provenance.attestation) {
            throw 'Release C6 provenance is not the canonical API-only release role.'
        }
        $roots = @(
            Get-ChildItem -LiteralPath $artifactContainer -Directory -Force |
                Where-Object { $_.Name -ceq [string]$provenance.artifact.name }
        )
        if ($roots.Count -ne 1 -or
            @(Get-ChildItem -LiteralPath $artifactContainer -Force).Count -ne 1 -or
            -not (Test-PathWithinDirectory -BasePath $artifactContainer -Path $roots[0].FullName)) {
            throw 'Release C6 resolver output is not a unique canonical artifact root.'
        }
        return [pscustomobject]@{
            provenance = $provenance
            provenancePath = $provenancePath
            artifactRoot = (Resolve-Path -LiteralPath $roots[0].FullName).Path
            manifest = $null
        }
    }

    if ($role.Count -ne 1 -or [bool]$role[0].forbidden -or
        [string]$provenance.run.workflowPath -notmatch '^\.github/workflows/[A-Za-z0-9_.-]+\.yml$' -or
        [string]$provenance.run.workflowRef -cne
            "$([string]$contract.repository)/$([string]$provenance.run.workflowPath)@$([string]$contract.protectedRef)") {
        throw "Resolver provenance has an invalid role or protected-main workflow binding: $ExpectedRole."
    }
    $expectedArtifactName = Get-T21ArtifactName -Role $ExpectedRole -StageName $Stage `
        -AppSha $ExpectedAppSha256.ToLowerInvariant() -PreflightSha $ExpectedPreflightEvidenceSha256
    if ([string]$provenance.artifact.name -cne $expectedArtifactName) {
        throw "Resolver provenance artifact name is not canonical: $ExpectedRole."
    }
    if ($ExpectedRole -in @('trusted-preflight', 'stage-operation-inputs')) {
        if (@($contract.callerMatrix | Where-Object {
                    [string]$_.stage -ceq $Stage -and
                    [string]$_.workflowRef -ceq [string]$provenance.run.workflowRef
                }).Count -ne 1) {
            throw "Resolver provenance caller is not in the exact stage caller matrix: $ExpectedRole."
        }
    }
    elseif ([string]$provenance.run.workflowPath -cne [string]$role[0].workflowPath) {
        throw "Resolver provenance direct producer is not its exact protected-main workflow: $ExpectedRole."
    }
    if (($ExpectedRole -eq 'cto-authorization' -and $Stage -ne 'Production') -or
        ([bool]$role[0].requiresCompletedSuccess -and
            ([string]$provenance.run.status -cne 'completed' -or
             [string]$provenance.run.conclusion -cne 'success'))) {
        throw "Resolver provenance producer completion or stage is invalid: $ExpectedRole."
    }

    Assert-T21ExactProperties -Value $provenance.attestation -Expected @(
        'predicateType', 'signerWorkflow', 'signerDigest', 'sourceRef', 'sourceCommitSha', 'status'
    ) -Label "$ExpectedRole verified attestation"
    $expectedSignerWorkflow = "$([string]$contract.repository)/$([string]$role[0].workflowPath)"
    Assert-T21CommitSha -Value ([string]$provenance.attestation.signerDigest) -Label "$ExpectedRole signer digest"
    if ([string]$provenance.attestation.predicateType -cne [string]$contract.attestation.predicateType -or
        [string]$provenance.attestation.signerWorkflow -cne $expectedSignerWorkflow -or
        [string]$provenance.attestation.sourceRef -cne [string]$contract.protectedRef -or
        [string]$provenance.attestation.sourceCommitSha -cne [string]$provenance.run.commitSha -or
        [string]$provenance.attestation.status -cne 'verified') {
        throw "Resolver provenance attestation is not bound to the exact role signer: $ExpectedRole."
    }
    $manifestPath = Join-Path $artifactContainer 't21-producer-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        (Get-Sha256Lower -Path $manifestPath) -cne [string]$provenance.contentManifestSha256) {
        throw "Resolver provenance producer manifest is absent or changed: $ExpectedRole."
    }
    return [pscustomobject]@{
        provenance = $provenance
        provenancePath = $provenancePath
        artifactRoot = $artifactContainer
        manifest = (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -DateKind String)
    }
}

function Assert-T21ProducerManifest {
    param($Artifact, [string]$Role, $Release)

    $manifest = $Artifact.manifest
    Assert-T21ExactProperties -Value $manifest -Expected @(
        'applicationSha256', 'bundleSha256', 'createdAtUtc', 'files', 'kind', 'producer', 'producerRole',
        'release', 'schemaVersion', 'stage', 'trustedExecution'
    ) -Label "$Role producer manifest"
    Assert-T21ExactProperties -Value $manifest.producer -Expected @(
        'commitSha', 'ref', 'repository', 'runAttempt', 'runId', 'workflowPath', 'workflowRef'
    ) -Label "$Role producer API binding"
    Assert-T21ExactProperties -Value $manifest.trustedExecution -Expected @(
        'bundlePath', 'bundleSha256', 'producerWorkflowRef', 'topLevelCallerWorkflowRef'
    ) -Label "$Role trusted execution"
    $producerRef = "$([string]$Artifact.provenance.attestation.signerWorkflow)@$([string]$Artifact.provenance.attestation.signerDigest)"
    if ([string]$manifest.schemaVersion -cne '2.0.0' -or
        [string]$manifest.kind -cne 't21-producer-manifest' -or
        [string]$manifest.producerRole -cne $Role -or
        [string]$manifest.stage -cne $Stage -or
        [string]$manifest.applicationSha256 -cne $ExpectedAppSha256.ToLowerInvariant() -or
        [string]$manifest.bundleSha256 -cne $ExpectedBundleSha256.ToLowerInvariant() -or
        [string]$manifest.producer.repository -cne [string]$Artifact.provenance.run.repository -or
        [string]$manifest.producer.workflowPath -cne [string]$Artifact.provenance.run.workflowPath -or
        [string]$manifest.producer.workflowRef -cne [string]$Artifact.provenance.run.workflowRef -or
        [string]$manifest.producer.runId -cne [string]$Artifact.provenance.run.runId -or
        [int]$manifest.producer.runAttempt -ne [int]$Artifact.provenance.run.runAttempt -or
        [string]$manifest.producer.commitSha -cne [string]$Artifact.provenance.run.commitSha -or
        [string]$manifest.producer.ref -cne [string]$Artifact.provenance.run.ref -or
        [string]$manifest.trustedExecution.topLevelCallerWorkflowRef -cne
            [string]$Artifact.provenance.run.workflowRef -or
        [string]$manifest.trustedExecution.producerWorkflowRef -cne $producerRef -or
        [string]$manifest.trustedExecution.bundlePath -cne 'operations/protected-execution-bundle.zip' -or
        [string]$manifest.trustedExecution.bundleSha256 -cne $ExpectedBundleSha256.ToLowerInvariant()) {
        throw "$Role producer manifest does not bind the exact API run and trusted execution pair."
    }
    & (Join-Path $PSScriptRoot 'New-T21ProducerManifest.ps1') -Mode Validate `
        -ArtifactRoot $Artifact.artifactRoot -ProducerRole $Role -Stage $Stage `
        -ApplicationSha256 $ExpectedAppSha256.ToLowerInvariant() -BundleSha256 $ExpectedBundleSha256.ToLowerInvariant() `
        -ReleaseManifestSha256 $Release.manifestSha256 -ReleaseCommitSha $Release.commitSha `
        -ReleaseRunId ([string]$Release.provenance.run.runId) -ReleaseRunAttempt ([int]$Release.provenance.run.runAttempt) `
        -ProducerRunId ([string]$Artifact.provenance.run.runId) -ProducerRunAttempt ([int]$Artifact.provenance.run.runAttempt) `
        -ProducerCommitSha ([string]$Artifact.provenance.run.commitSha) `
        -TopLevelCallerWorkflowRef ([string]$Artifact.provenance.run.workflowRef) `
        -ProducerWorkflowRef $producerRef -PolicyPath $PolicyPath | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "$Role producer manifest did not validate against the v2 role contract."
    }
    return [pscustomobject]@{
        topLevelCallerWorkflowRef = [string]$manifest.trustedExecution.topLevelCallerWorkflowRef
        producerWorkflowRef = [string]$manifest.trustedExecution.producerWorkflowRef
    }
}

function Get-T21Release {
    param($Artifact)

    $manifestPath = Join-Path $Artifact.artifactRoot 'release\release-manifest.json'
    $appPath = Join-Path $Artifact.artifactRoot 'app\Husaynia.Web.zip'
    $bundlePath = Join-Path $Artifact.artifactRoot 'operations\protected-execution-bundle.zip'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $appPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $bundlePath -PathType Leaf)) {
        throw 'Release C6 canonical manifest, application archive, or protected bundle is missing.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -DateKind String
    $manifestSha256 = Get-Sha256Lower -Path $manifestPath
    $commitSha = ([string]$manifest.commitSha).ToLowerInvariant()
    Assert-T21CommitSha -Value $commitSha -Label 'Release C6 manifest commit SHA'
    $bundleEntry = @($manifest.files | Where-Object {
            [string]$_.path -ceq 'operations/protected-execution-bundle.zip'
        })
    if ($manifestSha256 -cne [string]$Artifact.provenance.contentManifestSha256 -or
        $commitSha -cne [string]$Artifact.provenance.run.commitSha -or
        (Get-Sha256Lower -Path $appPath) -cne $ExpectedAppSha256.ToLowerInvariant() -or
        $bundleEntry.Count -ne 1 -or
        [string]$bundleEntry[0].sha256 -cne $ExpectedBundleSha256.ToLowerInvariant() -or
        (Get-Sha256Lower -Path $bundlePath) -cne $ExpectedBundleSha256.ToLowerInvariant()) {
        throw 'Release C6 does not bind the exact manifest, commit, application archive, and protected bundle.'
    }
    return [pscustomobject]@{
        provenance = $Artifact.provenance
        artifactRoot = $Artifact.artifactRoot
        manifest = $manifest
        manifestSha256 = $manifestSha256
        commitSha = $commitSha
    }
}

function Assert-T21Preflight {
    param($Artifact, $Release, $Target)

    $execution = Assert-T21ProducerManifest -Artifact $Artifact -Role 'trusted-preflight' -Release $Release
    $preflightPath = Join-Path $Artifact.artifactRoot 'migration-preflight.json'
    if (-not (Test-Path -LiteralPath $preflightPath -PathType Leaf)) {
        throw 'Resolver-created trusted preflight evidence is missing.'
    }
    $preflight = Get-Content -LiteralPath $preflightPath -Raw | ConvertFrom-Json -DateKind String
    $targetSha256 = Get-Sha256Lower -Path $StageTargetMetadataPath
    $targetFingerprint = Get-StageTargetFingerprint -TargetMetadata $Target
    if ([string]$preflight.schemaVersion -cne '1.0.0' -or
        [string]$preflight.evidenceType -cne 'migration-preflight' -or
        [string]$preflight.status -cne 'PASS' -or
        [string]$preflight.stage -cne $Stage -or
        [string]$preflight.artifactSha256 -cne $ExpectedAppSha256.ToLowerInvariant() -or
        [string]$preflight.releaseManifestSha256 -cne $Release.manifestSha256 -or
        [string]$preflight.releaseCommitSha -cne $Release.commitSha -or
        [string]$preflight.operation.bundlePath -cne 'operations/protected-execution-bundle.zip' -or
        [string]$preflight.operation.bundleSha256 -cne $ExpectedBundleSha256.ToLowerInvariant() -or
        [string]$preflight.operation.stageTargetMetadataSha256 -cne $targetSha256 -or
        [string]$preflight.operation.databaseTargetFingerprint -cne $targetFingerprint -or
        [string]$preflight.operation.sqlServerResourceId -cne [string]$Target.sqlServerResourceId -or
        [string]$preflight.operation.sqlDatabaseResourceId -cne [string]$Target.sqlDatabaseResourceId) {
        throw 'Trusted preflight evidence is not semantically bound to the exact release and stage target.'
    }
    return [pscustomobject]@{
        artifact = $Artifact
        execution = $execution
        evidencePath = $preflightPath
        evidenceSha256 = Get-Sha256Lower -Path $preflightPath
    }
}

function Assert-T21StageOperationInput {
    param($Artifact, $Release, $Target)

    $null = Assert-T21ProducerManifest -Artifact $Artifact -Role 'stage-operation-inputs' -Release $Release
    $inputPath = Join-Path $Artifact.artifactRoot 'stage-operation-inputs.json'
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) {
        throw 'Resolver-created stage-operation input record is missing.'
    }
    $input = Get-Content -LiteralPath $inputPath -Raw | ConvertFrom-Json -DateKind String
    Assert-T21ExactProperties -Value $input -Expected @(
        'schemaVersion', 'stage', 'artifactSha256', 'releaseManifestSha256', 'releaseVersion',
        'releaseCommitSha', 'targetFingerprint', 'sourceRelease', 'releaseBinding',
        'productionAuthorizationRequest', 'observedAtUtc', 'reports'
    ) -Label 'Stage-operation input record'
    Assert-T21ExactProperties -Value $input.sourceRelease -Expected @(
        'schemaVersion', 'authority', 'expectedRole', 'run', 'artifact', 'contentManifestSha256'
    ) -Label 'Stage-operation input source release'
    Assert-T21RunBinding -Actual $input.sourceRelease.run -Expected $Release.provenance.run `
        -Label 'Stage-operation input source release'
    if ([string]$input.schemaVersion -cne '2.1.0' -or
        [string]$input.stage -cne $Stage -or
        [string]$input.artifactSha256 -cne $ExpectedAppSha256.ToLowerInvariant() -or
        [string]$input.releaseManifestSha256 -cne $Release.manifestSha256 -or
        [string]$input.releaseCommitSha -cne $Release.commitSha -or
        [string]$input.targetFingerprint -cne (Get-StageTargetFingerprint -TargetMetadata $Target) -or
        [string]$input.sourceRelease.schemaVersion -cne '2.0.0' -or
        [string]$input.sourceRelease.authority -cne 'github-actions-api-and-sigstore-v1' -or
        [string]$input.sourceRelease.expectedRole -cne 'release-c6' -or
        [string]$input.sourceRelease.contentManifestSha256 -cne $Release.manifestSha256) {
        throw 'Stage-operation input record is not semantically bound to the resolver-verified C6 release.'
    }
    if ([string]$input.releaseBinding.runId -cne [string]$Release.provenance.run.runId -or
        [string]$input.releaseBinding.applicationSha256 -cne $ExpectedAppSha256.ToLowerInvariant() -or
        [string]$input.releaseBinding.bundleSha256 -cne $ExpectedBundleSha256.ToLowerInvariant() -or
        [string]$input.releaseBinding.manifestSha256 -cne $Release.manifestSha256 -or
        [string]$input.releaseBinding.commitSha -cne $Release.commitSha) {
        throw 'Stage-operation input release binding is not exact.'
    }
    if ($Stage -eq 'Production') {
        if ([string]$input.productionAuthorizationRequest.stage -cne 'Production' -or
            [string]$input.productionAuthorizationRequest.targetFingerprint -cne
                (Get-StageTargetFingerprint -TargetMetadata $Target) -or
            [string]$input.productionAuthorizationRequest.ctoApprovalReference -cne $CtoApprovalReference) {
            throw 'Production prepared-input authorization request is not exact.'
        }
    }
    elseif ($null -ne $input.productionAuthorizationRequest) {
        throw 'Non-Production prepared inputs contain Production authorization state.'
    }
    return [pscustomobject]@{
        artifact = $Artifact
        record = $input
        recordPath = $inputPath
    }
}

function Assert-T21CtoAuthorization {
    param($Artifact, $Release, $Preflight, $PreparedInput, $Target)

    $null = Assert-T21ProducerManifest -Artifact $Artifact -Role 'cto-authorization' -Release $Release
    $recordPath = Join-Path $Artifact.artifactRoot 'cto-authorization.json'
    if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf)) {
        throw 'Resolver-created CTO authorization record is missing.'
    }
    $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json -DateKind String
    Assert-T21ExactProperties -Value $record -Expected @(
        'schemaVersion', 'decision', 'stage', 'environment', 'applicationSha256', 'releaseManifestSha256',
        'releaseCommitSha', 'sourceRelease', 'releaseBinding', 'bundlePath', 'bundleSha256', 'targetFingerprint',
        'preflightEvidenceSha256', 'preflightProducerRun', 'producerBindings', 'authorizedExecution',
        'ctoApprovalReference', 'sourceChangeRecordSha256',
        'authorizedByActor', 'authorizedByActorId', 'issuedAtUtc', 'expiresAtUtc', 'authorizationRun'
    ) -Label 'CTO authorization record'
    Assert-T21ExactProperties -Value $record.authorizedExecution -Expected @(
        'topLevelCallerWorkflowRef', 'producerWorkflowRef'
    ) -Label 'CTO authorization execution'
    Assert-T21ExactProperties -Value $record.authorizationRun -Expected @(
        'repository', 'workflowPath', 'runId', 'runAttempt', 'ref', 'commitSha', 'actor', 'actorId'
    ) -Label 'CTO authorization producer run'
    Assert-T21RunBinding -Actual $record.sourceRelease -Expected $Release.provenance.run `
        -Label 'CTO authorization source release'
    Assert-T21RunBinding -Actual $record.preflightProducerRun -Expected $Preflight.artifact.provenance.run `
        -Label 'CTO authorization preflight producer'
    $expiry = [DateTimeOffset]::MinValue
    if ([string]$record.schemaVersion -cne '2.1.0' -or
        [string]$record.decision -cne 'AUTHORIZE' -or
        [string]$record.stage -cne 'Production' -or
        [string]$record.environment -cne [string]$stagePolicy.githubEnvironment -or
        [string]$record.applicationSha256 -cne $ExpectedAppSha256.ToLowerInvariant() -or
        [string]$record.releaseManifestSha256 -cne $Release.manifestSha256 -or
        [string]$record.releaseCommitSha -cne $Release.commitSha -or
        [string]$record.bundlePath -cne 'operations/protected-execution-bundle.zip' -or
        [string]$record.bundleSha256 -cne $ExpectedBundleSha256.ToLowerInvariant() -or
        [string]$record.targetFingerprint -cne (Get-StageTargetFingerprint -TargetMetadata $Target) -or
        [string]$record.preflightEvidenceSha256 -cne $Preflight.evidenceSha256 -or
        [string]$record.authorizedExecution.topLevelCallerWorkflowRef -cne
            $Preflight.execution.topLevelCallerWorkflowRef -or
        [string]$record.authorizedExecution.producerWorkflowRef -cne $Preflight.execution.producerWorkflowRef -or
        [string]$record.ctoApprovalReference -cne $CtoApprovalReference -or
        [string]$record.producerBindings.preflight.runId -cne
            [string]$Preflight.artifact.provenance.run.runId -or
        [string]$record.producerBindings.preparedInputs.runId -cne
            [string]$PreparedInput.artifact.provenance.run.runId -or
        [string]$record.authorizedByActor -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$' -or
        [string]$record.authorizedByActorId -notmatch '^[1-9][0-9]*$' -or
        [string]$record.authorizationRun.repository -cne [string]$Artifact.provenance.run.repository -or
        [string]$record.authorizationRun.workflowPath -cne [string]$Artifact.provenance.run.workflowPath -or
        [string]$record.authorizationRun.runId -cne [string]$Artifact.provenance.run.runId -or
        [int]$record.authorizationRun.runAttempt -ne [int]$Artifact.provenance.run.runAttempt -or
        [string]$record.authorizationRun.ref -cne [string]$Artifact.provenance.run.ref -or
        [string]$record.authorizationRun.commitSha -cne [string]$Artifact.provenance.run.commitSha -or
        [string]$record.authorizationRun.actor -cne [string]$record.authorizedByActor -or
        [string]$record.authorizationRun.actorId -cne [string]$record.authorizedByActorId -or
        -not [DateTimeOffset]::TryParse([string]$record.expiresAtUtc, [ref]$expiry) -or
        $expiry.Offset -ne [TimeSpan]::Zero -or
        $expiry -le [DateTimeOffset]::UtcNow) {
        throw 'CTO authorization is invalid, expired, or not cross-bound to the Production approval context.'
    }
    $changeRecordPath = Join-Path $Artifact.artifactRoot 'source-change-record.json'
    if (-not (Test-Path -LiteralPath $changeRecordPath -PathType Leaf) -or
        [string]$record.sourceChangeRecordSha256 -cne (Get-Sha256Lower -Path $changeRecordPath)) {
        throw 'CTO authorization checked change record is missing or not hash-bound.'
    }
    return [pscustomobject]@{
        artifact = $Artifact
        record = $record
        recordPath = $recordPath
        changeRecordPath = $changeRecordPath
    }
}

function Assert-T21MigrationAuthorization {
    param($Artifact, $Release, $Preflight, $PreparedInput, $Target, $Cto)

    $null = Assert-T21ProducerManifest -Artifact $Artifact -Role 'migration-authorization' -Release $Release
    $recordPath = Join-Path $Artifact.artifactRoot 'migration-apply-authorization.json'
    if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf)) {
        throw 'Resolver-created migration authorization record is missing.'
    }
    $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json -DateKind String
    Assert-T21ExactProperties -Value $record -Expected @(
        'schemaVersion', 'decision', 'mode', 'operationOrder', 'stage', 'environment', 'applicationSha256',
        'releaseManifestSha256', 'releaseCommitSha', 'sourceRelease', 'releaseBinding',
        'bundlePath', 'bundleSha256', 'targetFingerprint', 'preflightEvidenceSha256',
        'preflightProducerRun', 'producerBindings', 'authorizedExecution',
        'authorizedByActor', 'authorizedByActorId', 'issuedAtUtc', 'expiresAtUtc', 'productionCtoAuthorization'
    ) -Label 'Migration authorization record'
    Assert-T21ExactProperties -Value $record.authorizedExecution -Expected @(
        'topLevelCallerWorkflowRef', 'producerWorkflowRef'
    ) -Label 'Migration authorization execution'
    Assert-T21RunBinding -Actual $record.sourceRelease -Expected $Release.provenance.run `
        -Label 'Migration authorization source release'
    Assert-T21RunBinding -Actual $record.preflightProducerRun -Expected $Preflight.artifact.provenance.run `
        -Label 'Migration authorization preflight producer'
    $expiry = [DateTimeOffset]::MinValue
    if ([string]$record.schemaVersion -cne '2.1.0' -or
        [string]$record.decision -cne 'AUTHORIZE' -or
        [string]$record.mode -cne 'Apply' -or
        [int]$record.operationOrder -ne 20 -or
        [string]$record.stage -cne $Stage -or
        [string]$record.environment -cne [string]$stagePolicy.githubEnvironment -or
        [string]$record.applicationSha256 -cne $ExpectedAppSha256.ToLowerInvariant() -or
        [string]$record.releaseManifestSha256 -cne $Release.manifestSha256 -or
        [string]$record.releaseCommitSha -cne $Release.commitSha -or
        [string]$record.bundlePath -cne 'operations/protected-execution-bundle.zip' -or
        [string]$record.bundleSha256 -cne $ExpectedBundleSha256.ToLowerInvariant() -or
        [string]$record.targetFingerprint -cne (Get-StageTargetFingerprint -TargetMetadata $Target) -or
        [string]$record.preflightEvidenceSha256 -cne $Preflight.evidenceSha256 -or
        [string]$record.authorizedExecution.topLevelCallerWorkflowRef -cne
            $Preflight.execution.topLevelCallerWorkflowRef -or
        [string]$record.authorizedExecution.producerWorkflowRef -cne $Preflight.execution.producerWorkflowRef -or
        [string]$record.producerBindings.preflight.runId -cne
            [string]$Preflight.artifact.provenance.run.runId -or
        [string]$record.producerBindings.preparedInputs.runId -cne
            [string]$PreparedInput.artifact.provenance.run.runId -or
        [string]$record.authorizedByActor -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$' -or
        [string]$record.authorizedByActorId -notmatch '^[1-9][0-9]*$' -or
        -not [DateTimeOffset]::TryParse([string]$record.expiresAtUtc, [ref]$expiry) -or
        $expiry.Offset -ne [TimeSpan]::Zero -or
        $expiry -le [DateTimeOffset]::UtcNow) {
        throw 'Migration authorization is invalid, expired, or not cross-bound to the exact trusted preflight.'
    }
    if ($Stage -ne 'Production') {
        if ($null -ne $record.productionCtoAuthorization) {
            throw 'Non-Production migration authorization must not contain a Production CTO authorization context.'
        }
        return
    }

    Assert-T21ExactProperties -Value $record.productionCtoAuthorization -Expected @(
        'runId', 'approvalReference', 'contextSha256', 'authorizationSha256',
        'authorizationProvenanceSha256', 'sourceChangeRecordSha256', 'expiresAtUtc'
    ) -Label 'Production migration CTO authorization context'
    $ctoExpiry = [DateTimeOffset]::MinValue
    if ($null -eq $Cto -or
        [string]$record.productionCtoAuthorization.runId -cne [string]$Cto.artifact.provenance.run.runId -or
        [string]$record.productionCtoAuthorization.approvalReference -cne $CtoApprovalReference -or
        [string]$record.productionCtoAuthorization.contextSha256 -notmatch '^[a-f0-9]{64}$' -or
        [string]$record.productionCtoAuthorization.authorizationSha256 -cne
            (Get-Sha256Lower -Path $Cto.recordPath) -or
        [string]$record.productionCtoAuthorization.authorizationProvenanceSha256 -cne
            (Get-Sha256Lower -Path $Cto.artifact.provenancePath) -or
        [string]$record.productionCtoAuthorization.sourceChangeRecordSha256 -cne
            (Get-Sha256Lower -Path $Cto.changeRecordPath) -or
        -not [DateTimeOffset]::TryParse([string]$record.productionCtoAuthorization.expiresAtUtc, [ref]$ctoExpiry) -or
        $ctoExpiry.Offset -ne [TimeSpan]::Zero -or
        $ctoExpiry -lt $expiry -or
        [string]$record.productionCtoAuthorization.expiresAtUtc -cne [string]$Cto.record.expiresAtUtc) {
        throw 'Production migration authorization CTO approval context is not exactly cross-bound to the resolved CTO record.'
    }
}

if (-not (Test-Path -LiteralPath $PolicyPath -PathType Leaf)) {
    throw 'Trusted protected operation policy is missing.'
}
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json -DateKind String
$contract = $policy.t21ProvenanceContract
Assert-T21ExactProperties -Value $contract -Expected @(
    'attestation', 'callerMatrix', 'producerRoles', 'protectedRef', 'repository', 'schemaVersion'
) -Label 'T21 provenance policy'
if ([string]$contract.schemaVersion -cne '2.1.0' -or
    [string]$contract.repository -cne 'syedmh/Dreamer' -or
    [string]$contract.protectedRef -cne 'refs/heads/main') {
    throw 'Trusted protected operation provenance policy identity is invalid.'
}
$stagePolicy = @($policy.stages | Where-Object { [string]$_.name -ceq $Stage })
if ($stagePolicy.Count -ne 1) {
    throw "Promotion policy must contain exactly one $Stage stage."
}
$stagePolicy = $stagePolicy[0]
Assert-T21Sha256 -Value $ExpectedAppSha256.ToLowerInvariant() -Label 'Expected application SHA-256'
Assert-T21Sha256 -Value $ExpectedBundleSha256.ToLowerInvariant() -Label 'Expected protected bundle SHA-256'

$releaseArtifact = Get-T21ResolvedArtifact -VerifiedProvenancePath $ReleaseVerifiedProvenancePath -ExpectedRole 'release-c6'
$release = Get-T21Release -Artifact $releaseArtifact
$target = Read-StageTargetMetadata -Path $StageTargetMetadataPath -Stage $Stage -StagePolicy $stagePolicy

if ($Operation -eq 'Preflight') {
    if (-not [string]::IsNullOrWhiteSpace($PreflightVerifiedProvenancePath) -or
        -not [string]::IsNullOrWhiteSpace($StageOperationInputVerifiedProvenancePath) -or
        -not [string]::IsNullOrWhiteSpace($MigrationAuthorizationVerifiedProvenancePath) -or
        -not [string]::IsNullOrWhiteSpace($CtoAuthorizationVerifiedProvenancePath) -or
        -not [string]::IsNullOrWhiteSpace($CtoApprovalReference)) {
        throw 'Preflight does not accept StageOperations authorization provenance or approval context.'
    }
    Write-Output "TRUSTED-OPERATION-INPUTS status=PASS operation=Preflight stage=$Stage"
    return
}

if ([string]::IsNullOrWhiteSpace($PreflightVerifiedProvenancePath) -or
    [string]::IsNullOrWhiteSpace($StageOperationInputVerifiedProvenancePath) -or
    [string]::IsNullOrWhiteSpace($MigrationAuthorizationVerifiedProvenancePath)) {
    throw 'StageOperations requires resolver-created preflight, prepared-input, and migration authorization provenance.'
}
if ($Stage -eq 'Production') {
    if ([string]::IsNullOrWhiteSpace($CtoAuthorizationVerifiedProvenancePath) -or
        $CtoApprovalReference -notmatch '^[A-Za-z0-9][A-Za-z0-9._:/#-]{0,127}$') {
        throw 'Production StageOperations requires resolver-created CTO authorization provenance and an approval reference.'
    }
}
elseif (-not [string]::IsNullOrWhiteSpace($CtoAuthorizationVerifiedProvenancePath) -or
        -not [string]::IsNullOrWhiteSpace($CtoApprovalReference)) {
    throw 'Non-Production StageOperations must not accept CTO authorization provenance or approval context.'
}

$preflightArtifact = Get-T21ResolvedArtifact -VerifiedProvenancePath $PreflightVerifiedProvenancePath `
    -ExpectedRole 'trusted-preflight'
$preflight = Assert-T21Preflight -Artifact $preflightArtifact -Release $release -Target $target
$stageOperationInputArtifact = Get-T21ResolvedArtifact `
    -VerifiedProvenancePath $StageOperationInputVerifiedProvenancePath -ExpectedRole 'stage-operation-inputs'
$preparedInput = Assert-T21StageOperationInput -Artifact $stageOperationInputArtifact -Release $release -Target $target
$cto = $null
if ($Stage -eq 'Production') {
    $ctoArtifact = Get-T21ResolvedArtifact `
        -VerifiedProvenancePath $CtoAuthorizationVerifiedProvenancePath -ExpectedRole 'cto-authorization'
    $cto = Assert-T21CtoAuthorization -Artifact $ctoArtifact -Release $release -Preflight $preflight `
        -PreparedInput $preparedInput -Target $target
}
$migrationAuthorizationArtifact = Get-T21ResolvedArtifact `
    -VerifiedProvenancePath $MigrationAuthorizationVerifiedProvenancePath -ExpectedRole 'migration-authorization' `
    -ExpectedPreflightEvidenceSha256 $preflight.evidenceSha256
Assert-T21MigrationAuthorization -Artifact $migrationAuthorizationArtifact -Release $release `
    -Preflight $preflight -PreparedInput $preparedInput -Target $target -Cto $cto

$null = Assert-DeploymentEvidence -Policy $policy -Stage $Stage
throw 'T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN'
