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
    [string]$OperationRunMetadataPath,
    [string]$EvidenceRunMetadataPath,
    [string]$ValidatedOutputRoot,
    [string]$IntakeRepository,
    [string]$IntakeWorkflowPath,
    [string]$IntakeRunId,
    [int]$IntakeRunAttempt,
    [string]$IntakeRef,
    [string]$IntakeCommitSha,
    [string]$MigrationAuthorizedActorIdAllowlist,
    [string]$PolicyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
. (Join-Path $PSScriptRoot 'StageTarget.Common.ps1')
. (Join-Path $PSScriptRoot '..\artifact\migrations\bundle\Migration.Common.ps1')

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
    -Operation 'Stage evidence validation'
$provenance = $policy.provenance
$migrationBundlePolicy = $policy.migration.applicationBundle
$protectedBundleRelativePath = [string]$policy.trustedExecutionContract.bundlePath
$artifact = (Resolve-Path -LiteralPath $ArtifactRoot).Path
$evidenceDirectory = (Resolve-Path -LiteralPath $EvidenceRoot).Path
$releaseManifestPath = Join-Path $artifact 'release\release-manifest.json'
$releaseManifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
$releaseManifestSha256 = Get-Sha256Lower -Path $releaseManifestPath
$protectedBundleEntry = @($releaseManifest.files | Where-Object {
        [string]$_.path -ceq $protectedBundleRelativePath
    })
$protectedBundleSha256 = if ($protectedBundleEntry.Count -eq 1) {
    [string]$protectedBundleEntry[0].sha256
}
else {
    ''
}
$appPath = Join-Path $artifact 'app\Husaynia.Web.zip'
$actualAppSha256 = Get-Sha256Lower -Path $appPath
$failures = [Collections.Generic.List[string]]::new()
$preparing = -not [string]::IsNullOrWhiteSpace($ValidatedOutputRoot)
$producerValidation = -not [string]::IsNullOrWhiteSpace($OperationRunMetadataPath)
$trustedExecution = $null

function Add-EvidenceFailure {
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

function Test-RunMetadata {
    param(
        $Metadata,
        [string[]]$AllowedWorkflowPaths,
        [string]$ExpectedCommitSha,
        [switch]$RequireSuccess
    )

    if ([string](Get-PropertyValue $Metadata 'schemaVersion') -ne '1.0.0') {
        Add-EvidenceFailure 'run-metadata-schema'
    }
    if ([string](Get-PropertyValue $Metadata 'repository') -ne [string]$provenance.repository) {
        Add-EvidenceFailure 'run-metadata-repository'
    }
    if ([string](Get-PropertyValue $Metadata 'ref') -ne [string]$provenance.protectedRef) {
        Add-EvidenceFailure 'run-metadata-ref'
    }
    if ([string](Get-PropertyValue $Metadata 'workflowPath') -notin $AllowedWorkflowPaths) {
        Add-EvidenceFailure 'run-metadata-workflow'
    }
    if ([string](Get-PropertyValue $Metadata 'runId') -notmatch '^[1-9][0-9]*$' -or
        -not (Test-PositiveInteger (Get-PropertyValue $Metadata 'runAttempt')) -or
        [string](Get-PropertyValue $Metadata 'actor') -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$' -or
        [string](Get-PropertyValue $Metadata 'actorId') -notmatch '^[1-9][0-9]*$') {
        Add-EvidenceFailure 'run-metadata-run-identity'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedCommitSha) -and
        [string](Get-PropertyValue $Metadata 'commitSha') -ne $ExpectedCommitSha.ToLowerInvariant()) {
        Add-EvidenceFailure 'run-metadata-commit'
    }
    if ($RequireSuccess -and
        ([string](Get-PropertyValue $Metadata 'status') -ne 'completed' -or
         [string](Get-PropertyValue $Metadata 'conclusion') -ne 'success')) {
        Add-EvidenceFailure 'run-metadata-not-successful'
    }
}

function Test-RunBinding {
    param($Binding, $Metadata, [string]$Prefix, [switch]$IgnoreConclusion)

    if (-not (Test-PositiveInteger (Get-PropertyValue $Binding 'runAttempt'))) {
        Add-EvidenceFailure "$Prefix-runAttempt-type"
    }
    foreach ($name in @('repository', 'workflowPath', 'runId', 'runAttempt', 'ref', 'commitSha')) {
        if ([string](Get-PropertyValue $Binding $name) -ne [string](Get-PropertyValue $Metadata $name)) {
            Add-EvidenceFailure "$Prefix-$name"
        }
    }
    if (-not $IgnoreConclusion -and
        [string](Get-PropertyValue $Binding 'conclusion') -ne [string](Get-PropertyValue $Metadata 'conclusion')) {
        Add-EvidenceFailure "$Prefix-conclusion"
    }
}

function Test-JsonInteger {
    param($Value, [int64]$Minimum, [int64]$Maximum = [int64]::MaxValue)

    if ($null -eq $Value -or $Value -is [bool]) {
        return $false
    }
    if ($Value -isnot [sbyte] -and
        $Value -isnot [byte] -and
        $Value -isnot [int16] -and
        $Value -isnot [uint16] -and
        $Value -isnot [int32] -and
        $Value -isnot [uint32] -and
        $Value -isnot [int64]) {
        return $false
    }
    $number = [int64]$Value
    return $number -ge $Minimum -and $number -le $Maximum
}

function Test-PositiveInteger {
    param($Value)
    return Test-JsonInteger -Value $Value -Minimum 1
}

function Test-ZeroInteger {
    param($Value)
    return Test-JsonInteger -Value $Value -Minimum 0 -Maximum 0
}

function Test-StageLeaseFields {
    param($Lease)

    if ($null -eq $Lease -or
        @(Compare-Object @(
            'acquiredAtUtc',
            'authorizationEvidenceSha256',
            'expiresAtUtc',
            'fenceToken',
            'holderRunAttempt',
            'holderRunId',
            'provider',
            'released',
            'releasedAtUtc',
            'resource'
        ) @($Lease.PSObject.Properties.Name | Sort-Object)).Count -ne 0 -or
        [string](Get-PropertyValue $Lease 'provider') -ne 'sqlserver-stage-lease-v1' -or
        [string](Get-PropertyValue $Lease 'resource') -ne $script:expectedStageLeaseResource -or
        -not (Test-Sha256 ([string](Get-PropertyValue $Lease 'authorizationEvidenceSha256'))) -or
        [string](Get-PropertyValue $Lease 'holderRunId') -notmatch '^[1-9][0-9]*$' -or
        -not (Test-PositiveInteger (Get-PropertyValue $Lease 'holderRunAttempt')) -or
        -not (Test-PositiveInteger (Get-PropertyValue $Lease 'fenceToken')) -or
        (Get-PropertyValue $Lease 'released') -ne $true) {
        return $false
    }

    $acquiredAt = [DateTimeOffset]::MinValue
    $expiresAt = [DateTimeOffset]::MinValue
    $releasedAt = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse([string](Get-PropertyValue $Lease 'acquiredAtUtc'), [ref]$acquiredAt) -or
        -not [DateTimeOffset]::TryParse([string](Get-PropertyValue $Lease 'expiresAtUtc'), [ref]$expiresAt) -or
        -not [DateTimeOffset]::TryParse([string](Get-PropertyValue $Lease 'releasedAtUtc'), [ref]$releasedAt) -or
        $acquiredAt.Offset -ne [TimeSpan]::Zero -or
        $expiresAt.Offset -ne [TimeSpan]::Zero -or
        $releasedAt.Offset -ne [TimeSpan]::Zero -or
        $expiresAt -le $acquiredAt -or
        $releasedAt -lt $acquiredAt) {
        return $false
    }

    return $true
}

function Test-OperationFields {
    param($Evidence, [string]$EvidenceType, [string]$RelativePath)

    $operation = Get-PropertyValue $Evidence 'operation'
    if ($null -eq $operation) {
        Add-EvidenceFailure "operation-missing:$RelativePath"
        return
    }
    if ([string](Get-PropertyValue $operation 'stageTargetMetadataSha256') -ne $script:targetMetadataSha256) {
        Add-EvidenceFailure "operation-stage-target-binding:$RelativePath"
    }

    switch ($EvidenceType) {
        'configuration' {
            if ([string](Get-PropertyValue $operation 'dataIsolationKey') -ne [string]$stagePolicy.dataIsolationKey) {
                Add-EvidenceFailure "configuration-isolation:$RelativePath"
            }
            if ([string](Get-PropertyValue $operation 'serviceConnectionSecretName') -ne [string]$stagePolicy.serviceConnectionSecretName) {
                Add-EvidenceFailure "configuration-service-connection:$RelativePath"
            }
            $secretReferences = @(Get-PropertyValue $operation 'secretReferences')
            $approvedSecretReferences = @($script:targetMetadata.secretReferences)
            if ($secretReferences.Count -ne $approvedSecretReferences.Count) {
                Add-EvidenceFailure "configuration-secret-references:$RelativePath"
            }
            foreach ($approvedReference in $approvedSecretReferences) {
                $match = @($secretReferences | Where-Object {
                    [string](Get-PropertyValue $_ 'settingKey') -eq [string]$approvedReference.settingKey
                })
                if ($match.Count -ne 1 -or
                    [string](Get-PropertyValue $match[0] 'secretName') -ne [string]$approvedReference.secretName -or
                    [string](Get-PropertyValue $match[0] 'provider') -ne [string]$approvedReference.provider -or
                    [string](Get-PropertyValue $match[0] 'referenceIdentifier') -ne [string]$approvedReference.referenceIdentifier -or
                    [string](Get-PropertyValue $match[0] 'resourceId') -ne [string]$approvedReference.resourceId -or
                    [string](Get-PropertyValue $match[0] 'mode') -ne [string]$approvedReference.mode -or
                    [string](Get-PropertyValue $match[0] 'stage') -ne $Stage) {
                    Add-EvidenceFailure "configuration-secret-reference-binding:$RelativePath"
                }
            }
            foreach ($providerName in @('payments', 'messaging', 'analytics', 'contentMutation')) {
                if ([string](Get-PropertyValue (Get-PropertyValue $operation 'providerModes') $providerName) -ne
                    [string](Get-PropertyValue $script:targetMetadata.providerModes $providerName)) {
                    Add-EvidenceFailure "configuration-provider-mode:$providerName"
                }
            }
        }
        'migration-preflight' {
            if ([string](Get-PropertyValue $operation 'mode') -ne 'Preflight' -or
                -not (Test-JsonInteger -Value (Get-PropertyValue $operation 'operationOrder') -Minimum 10 -Maximum 10) -or
                (Get-PropertyValue $operation 'nonMutating') -ne $true -or
                (Get-PropertyValue $operation 'executed') -ne $true -or
                [string](Get-PropertyValue $operation 'identityMode') -ne 'readonly' -or
                (Get-PropertyValue $operation 'readOnlyPrincipalVerified') -ne $true -or
                (Get-PropertyValue $operation 'mutationPermissionsDenied') -ne $true -or
                (Get-PropertyValue $operation 'repositorySqlExecuted') -ne $false -or
                [string](Get-PropertyValue $operation 'queryContract') -ne 'fixed-read-only-metadata-v1' -or
                [string](Get-PropertyValue $operation 'databaseStatus') -ne 'ONLINE' -or
                [string](Get-PropertyValue $operation 'databaseTargetFingerprint') -ne $script:targetFingerprint -or
                -not (Test-Sha256 ([string](Get-PropertyValue $operation 'preflightSqlSha256'))) -or
                -not (Test-Sha256 ([string](Get-PropertyValue $operation 'bundleSha256'))) -or
                [string](Get-PropertyValue $operation 'bundlePath') -cne $protectedBundleRelativePath -or
                [string](Get-PropertyValue $operation 'bundleSha256') -cne $protectedBundleSha256 -or
                [string](Get-PropertyValue $operation 'bundleFormat') -cne
                    'protected-execution-bundle-v1.1.0' -or
                [string](Get-PropertyValue $operation 'sqlServerName') -ne [string]$script:targetMetadata.sqlServerName -or
                [string](Get-PropertyValue $operation 'sqlServerFqdn') -ne [string]$script:targetMetadata.sqlServerFqdn -or
                [string](Get-PropertyValue $operation 'sqlDatabaseName') -ne [string]$script:targetMetadata.sqlDatabaseName -or
                [string](Get-PropertyValue $operation 'sqlServerResourceId') -ne [string]$script:targetMetadata.sqlServerResourceId -or
                [string](Get-PropertyValue $operation 'sqlDatabaseResourceId') -ne [string]$script:targetMetadata.sqlDatabaseResourceId -or
                @(Get-PropertyValue $operation 'checks').Count -eq 0) {
                Add-EvidenceFailure "migration-preflight-fields:$RelativePath"
            }
        }
        'backup' {
            $retention = [DateTimeOffset]::MinValue
            if ([string]::IsNullOrWhiteSpace([string](Get-PropertyValue $operation 'backupId')) -or
                [string](Get-PropertyValue $operation 'databaseTargetFingerprint') -ne $script:targetFingerprint -or
                (Get-PropertyValue $operation 'restorable') -ne $true -or
                (Get-PropertyValue $operation 'integrityVerified') -ne $true -or
                -not [DateTimeOffset]::TryParse([string](Get-PropertyValue $operation 'retentionUntilUtc'), [ref]$retention) -or
                $retention -le [DateTimeOffset]::UtcNow) {
                Add-EvidenceFailure "backup-fields:$RelativePath"
            }
        }
        'migration-apply' {
            $stageLease = Get-PropertyValue $operation 'stageLease'
            if ([string](Get-PropertyValue $operation 'mode') -ne 'Apply' -or
                -not (Test-JsonInteger -Value (Get-PropertyValue $operation 'operationOrder') -Minimum 30 -Maximum 30) -or
                [string](Get-PropertyValue $operation 'operationId') -notmatch
                    "^migration-apply/$Stage/[1-9][0-9]*/[1-9][0-9]*$" -or
                (Get-PropertyValue $operation 'explicitlyAuthorized') -ne $true -or
                (Get-PropertyValue $operation 'applied') -ne $true -or
                -not (Test-ZeroInteger (Get-PropertyValue $operation 'exitCode')) -or
                [string](Get-PropertyValue $operation 'databaseTargetFingerprint') -ne $script:targetFingerprint -or
                -not (Test-Sha256 ([string](Get-PropertyValue $operation 'backupEvidenceSha256'))) -or
                -not (Test-Sha256 ([string](Get-PropertyValue $operation 'preflightEvidenceSha256'))) -or
                -not (Test-Sha256 ([string](Get-PropertyValue $operation 'applyAuthorizationSha256'))) -or
                -not (Test-Sha256 ([string](Get-PropertyValue $operation 'bundleSha256'))) -or
                [string](Get-PropertyValue $operation 'bundlePath') -cne [string]$migrationBundlePolicy.relativePath -or
                [string](Get-PropertyValue $operation 'bundleSha256') -cne [string]$migrationBundlePolicy.sha256 -or
                [string](Get-PropertyValue $operation 'bundleFormat') -cne [string]$migrationBundlePolicy.format -or
                [string](Get-PropertyValue $operation 'sqlServerName') -ne [string]$script:targetMetadata.sqlServerName -or
                [string](Get-PropertyValue $operation 'sqlServerFqdn') -ne [string]$script:targetMetadata.sqlServerFqdn -or
                [string](Get-PropertyValue $operation 'sqlDatabaseName') -ne [string]$script:targetMetadata.sqlDatabaseName -or
                [string](Get-PropertyValue $operation 'sqlServerResourceId') -ne [string]$script:targetMetadata.sqlServerResourceId -or
                [string](Get-PropertyValue $operation 'sqlDatabaseResourceId') -ne [string]$script:targetMetadata.sqlDatabaseResourceId -or
                [string]::IsNullOrWhiteSpace([string](Get-PropertyValue $operation 'migrationStateBefore')) -or
                [string]::IsNullOrWhiteSpace([string](Get-PropertyValue $operation 'migrationStateAfter')) -or
                [string]::IsNullOrWhiteSpace([string](Get-PropertyValue $operation 'targetMigration')) -or
                [string](Get-PropertyValue $operation 'currentMigration') -ne [string](Get-PropertyValue $operation 'targetMigration') -or
                -not (Test-JsonInteger -Value (Get-PropertyValue $operation 'appliedMigrationCount') -Minimum 0) -or
                (Get-PropertyValue $operation 'migrationHistoryTableVerified') -ne $true -or
                -not (Test-StageLeaseFields $stageLease) -or
                [string](Get-PropertyValue $stageLease 'authorizationEvidenceSha256') -ne
                    [string](Get-PropertyValue $operation 'applyAuthorizationSha256') -or
                [string](Get-PropertyValue $stageLease 'holderRunId') -ne
                    [string](Get-PropertyValue (Get-PropertyValue $Evidence 'sourceRun') 'runId') -or
                [int](Get-PropertyValue $stageLease 'holderRunAttempt') -ne
                    [int](Get-PropertyValue (Get-PropertyValue $Evidence 'sourceRun') 'runAttempt')) {
                Add-EvidenceFailure "migration-apply-fields:$RelativePath"
            }
        }
        'health' {
            if ((Get-PropertyValue $operation 'healthy') -ne $true -or
                -not (Test-JsonInteger -Value (Get-PropertyValue $operation 'statusCode') -Minimum 200 -Maximum 200) -or
                -not (Test-PositiveInteger (Get-PropertyValue $operation 'sampleCount'))) {
                Add-EvidenceFailure "health-fields:$RelativePath"
            }
        }
        'smoke' {
            if (-not (Test-PositiveInteger (Get-PropertyValue $operation 'passedTests')) -or
                -not (Test-ZeroInteger (Get-PropertyValue $operation 'failedTests'))) {
                Add-EvidenceFailure "smoke-fields:$RelativePath"
            }
        }
        'crawl' {
            if (-not (Test-PositiveInteger (Get-PropertyValue $operation 'urlsChecked')) -or
                -not (Test-ZeroInteger (Get-PropertyValue $operation 'brokenLinks')) -or
                -not (Test-ZeroInteger (Get-PropertyValue $operation 'redirectMismatches'))) {
                Add-EvidenceFailure "crawl-fields:$RelativePath"
            }
        }
        'performance' {
            if (-not (Test-PositiveInteger (Get-PropertyValue $operation 'sampleCount')) -or
                -not (Test-PositiveInteger (Get-PropertyValue $operation 'successfulSamples')) -or
                -not (Test-ZeroInteger (Get-PropertyValue $operation 'failedSamples')) -or
                -not (Test-JsonInteger -Value (Get-PropertyValue $operation 'observedP95Milliseconds') -Minimum 0) -or
                -not (Test-PositiveInteger (Get-PropertyValue $operation 'maxP95Milliseconds')) -or
                [int64](Get-PropertyValue $operation 'successfulSamples') -ne [int64](Get-PropertyValue $operation 'sampleCount') -or
                [int64](Get-PropertyValue $operation 'observedP95Milliseconds') -gt [int64](Get-PropertyValue $operation 'maxP95Milliseconds') -or
                (Get-PropertyValue $operation 'thresholdsMet') -ne $true -or
                (Get-PropertyValue $operation 'readOnly') -ne $true) {
                Add-EvidenceFailure "performance-fields:$RelativePath"
            }
        }
        'accessibility' {
            if (-not (Test-PositiveInteger (Get-PropertyValue $operation 'testedPages')) -or
                -not (Test-ZeroInteger (Get-PropertyValue $operation 'criticalViolations')) -or
                -not (Test-ZeroInteger (Get-PropertyValue $operation 'seriousViolations'))) {
                Add-EvidenceFailure "accessibility-fields:$RelativePath"
            }
        }
        'visual' {
            if (-not (Test-PositiveInteger (Get-PropertyValue $operation 'comparedPages')) -or
                -not (Test-ZeroInteger (Get-PropertyValue $operation 'failedComparisons')) -or
                (Get-PropertyValue $operation 'thresholdApproved') -ne $true) {
                Add-EvidenceFailure "visual-fields:$RelativePath"
            }
        }
        'sandbox-integrations' {
            if ([string](Get-PropertyValue $operation 'providerMode') -ne 'sandbox' -or
                (Get-PropertyValue $operation 'liveCredentialsUsed') -ne $false -or
                -not (Test-PositiveInteger (Get-PropertyValue $operation 'passedChecks')) -or
                -not (Test-ZeroInteger (Get-PropertyValue $operation 'failedChecks'))) {
                Add-EvidenceFailure "sandbox-fields:$RelativePath"
            }
        }
        'rollback' {
            if ([string](Get-PropertyValue $operation 'decision') -notin @('READY', 'NOT_REQUIRED') -or
                -not (Test-Sha256 ([string](Get-PropertyValue $operation 'lastKnownGoodArtifactSha256'))) -or
                (Get-PropertyValue $operation 'rehearsalPassed') -ne $true -or
                (Get-PropertyValue $operation 'restorePointVerified') -ne $true) {
                Add-EvidenceFailure "rollback-fields:$RelativePath"
            }
        }
        'restore' {
            if (-not (Test-Sha256 ([string](Get-PropertyValue $operation 'backupEvidenceSha256'))) -or
                [string](Get-PropertyValue $operation 'restoredTargetFingerprint') -ne $script:targetFingerprint -or
                [string]::IsNullOrWhiteSpace([string](Get-PropertyValue $operation 'isolatedRestoreTargetResourceId')) -or
                (Get-PropertyValue $operation 'restoreTestPassed') -ne $true -or
                (Get-PropertyValue $operation 'dataIntegrityVerified') -ne $true -or
                (Get-PropertyValue $operation 'sourceDatabaseUnchanged') -ne $true -or
                (Get-PropertyValue $operation 'cleanupCompleted') -ne $true) {
                Add-EvidenceFailure "restore-fields:$RelativePath"
            }
        }
        'change-record' {
            $expiry = [DateTimeOffset]::MinValue
            if ([string]::IsNullOrWhiteSpace([string](Get-PropertyValue $operation 'changeId')) -or
                [string](Get-PropertyValue $operation 'decision') -ne 'APPROVED' -or
                [string]::IsNullOrWhiteSpace([string](Get-PropertyValue $operation 'owner')) -or
                -not [DateTimeOffset]::TryParse([string](Get-PropertyValue $operation 'expiresAtUtc'), [ref]$expiry) -or
                $expiry -le [DateTimeOffset]::UtcNow) {
                Add-EvidenceFailure "change-record-fields:$RelativePath"
            }
        }
        default {
            Add-EvidenceFailure "unsupported-evidence-type:$EvidenceType"
        }
    }
}

if ($actualAppSha256 -ne $ExpectedAppSha256.ToLowerInvariant()) {
    Add-EvidenceFailure 'artifact-application-checksum'
}
if ([string]$releaseManifest.commitSha -notmatch '^[a-fA-F0-9]{40}$') {
    Add-EvidenceFailure 'release-manifest-commit'
}

$releaseRun = Get-Content -LiteralPath $ReleaseRunMetadataPath -Raw | ConvertFrom-Json
Test-RunMetadata -Metadata $releaseRun `
    -AllowedWorkflowPaths @([string]$provenance.releaseWorkflowPath) `
    -ExpectedCommitSha ([string]$releaseManifest.commitSha) `
    -RequireSuccess

$operationRun = $null
$evidenceRun = $null
$bundleManifest = $null
$allowedOperationWorkflowPaths = @(
    Get-MigrationRunIssuerWorkflowPaths -Provenance $provenance -Stage $Stage
)
$allowedIntakeWorkflowPaths = @([string]$provenance.evidenceIntakeWorkflowPath)
if ($Stage -in @('Development', 'Staging')) {
    $allowedIntakeWorkflowPaths += [string]$provenance.releaseWorkflowPath
}
if ($producerValidation) {
    $operationRun = Get-Content -LiteralPath $OperationRunMetadataPath -Raw | ConvertFrom-Json
    Test-RunMetadata -Metadata $operationRun `
        -AllowedWorkflowPaths $allowedOperationWorkflowPaths `
        -ExpectedCommitSha ([string]$releaseManifest.commitSha) `
        -RequireSuccess

    if ($preparing) {
        if ([string]$IntakeRepository -ne [string]$provenance.repository -or
            [string]$IntakeWorkflowPath -notin $allowedIntakeWorkflowPaths -or
            [string]$IntakeRunId -notmatch '^[1-9][0-9]*$' -or
            $IntakeRunAttempt -lt 1 -or
            [string]$IntakeRef -ne [string]$provenance.protectedRef -or
            -not (Test-CommitSha $IntakeCommitSha)) {
            Add-EvidenceFailure 'intake-run-identity'
        }
    }
}
else {
    if ([string]::IsNullOrWhiteSpace($EvidenceRunMetadataPath)) {
        throw 'Consuming a validated evidence bundle requires evidence intake run metadata.'
    }
    $evidenceRun = Get-Content -LiteralPath $EvidenceRunMetadataPath -Raw | ConvertFrom-Json
    Test-RunMetadata -Metadata $evidenceRun `
        -AllowedWorkflowPaths $allowedIntakeWorkflowPaths `
        -ExpectedCommitSha '' `
        -RequireSuccess

    $bundleManifestPath = Join-Path $evidenceDirectory 'evidence-bundle-manifest.json'
    if (-not (Test-Path -LiteralPath $bundleManifestPath -PathType Leaf)) {
        throw 'Validated evidence bundle manifest is missing.'
    }
    $bundleManifest = Get-Content -LiteralPath $bundleManifestPath -Raw | ConvertFrom-Json -DateKind String
    if ([string]$bundleManifest.schemaVersion -ne '1.0.0' -or
        [string]$bundleManifest.stage -ne $Stage -or
        [string]$bundleManifest.artifactSha256 -ne $actualAppSha256 -or
        [string]$bundleManifest.releaseManifestSha256 -ne $releaseManifestSha256) {
        Add-EvidenceFailure 'bundle-manifest-release-binding'
    }
    try {
        $trustedExecution = Assert-TrustedExecutionEvidence `
            -TrustedExecution $bundleManifest.trustedExecution `
            -Policy $policy `
            -ReleaseManifest $releaseManifest
    }
    catch {
        Add-EvidenceFailure "bundle-manifest-trusted-execution:$($_.Exception.Message)"
    }
    Test-RunBinding -Binding $bundleManifest.sourceReleaseRun -Metadata $releaseRun -Prefix 'bundle-release-run'
    Test-RunBinding -Binding $bundleManifest.intakeRun -Metadata $evidenceRun -Prefix 'bundle-intake-run' -IgnoreConclusion
    $operationRun = $bundleManifest.sourceOperationRun
    Test-RunMetadata -Metadata $operationRun `
        -AllowedWorkflowPaths $allowedOperationWorkflowPaths `
        -ExpectedCommitSha ([string]$releaseManifest.commitSha) `
        -RequireSuccess
}

$validatedFiles = [Collections.Generic.List[object]]::new()
$targetMetadataPath = Join-Path $evidenceDirectory 'stage-target-metadata.json'
try {
    $targetMetadata = Read-StageTargetMetadata -Path $targetMetadataPath -Stage $Stage -StagePolicy $stagePolicy
    $targetMetadataSha256 = Get-Sha256Lower -Path $targetMetadataPath
    $targetFingerprint = Get-StageTargetFingerprint -TargetMetadata $targetMetadata
    $validatedFiles.Add([ordered]@{
        path = 'stage-target-metadata.json'
        evidenceType = 'stage-target-metadata'
        sha256 = $targetMetadataSha256
        observedAtUtc = ''
    })
}
catch {
    $targetMetadata = $null
    $targetMetadataSha256 = ''
    $targetFingerprint = ''
    Add-EvidenceFailure "stage-target-metadata:$($_.Exception.Message)"
}
$script:targetMetadata = $targetMetadata
$script:targetMetadataSha256 = $targetMetadataSha256
$script:targetFingerprint = $targetFingerprint
$script:expectedStageLeaseResource = "husaynia-stage-lease-$($Stage.ToLowerInvariant())-$targetFingerprint"

$preflightRunPath = Join-Path $evidenceDirectory 'migration-preflight-run.json'
$preflightRun = $null
if (-not (Test-Path -LiteralPath $preflightRunPath -PathType Leaf)) {
    Add-EvidenceFailure 'migration-preflight-run-missing'
}
else {
    try {
        $preflightRun = Get-Content -LiteralPath $preflightRunPath -Raw | ConvertFrom-Json
        Test-RunMetadata -Metadata $preflightRun `
            -AllowedWorkflowPaths $allowedOperationWorkflowPaths `
            -ExpectedCommitSha ([string]$releaseManifest.commitSha) `
            -RequireSuccess
        $validatedFiles.Add([ordered]@{
            path = 'migration-preflight-run.json'
            evidenceType = 'migration-preflight-run'
            sha256 = Get-Sha256Lower -Path $preflightRunPath
            observedAtUtc = [string]$preflightRun.updatedAtUtc
        })
    }
    catch {
        Add-EvidenceFailure "migration-preflight-run-invalid:$($_.Exception.Message)"
    }
}

$authorization = $null
$authorizationRun = $null
if ('migration-apply.json' -in @($stagePolicy.requiredEvidence)) {
    $authorizationPath = Join-Path $evidenceDirectory 'migration-apply-authorization.json'
    $authorizationRunPath = Join-Path $evidenceDirectory 'migration-apply-authorization-run.json'
    if ([string]::IsNullOrWhiteSpace($MigrationAuthorizedActorIdAllowlist) -or
        -not (Test-Path -LiteralPath $authorizationPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $authorizationRunPath -PathType Leaf)) {
        Add-EvidenceFailure 'migration-apply-authorization-artifacts-missing'
    }
    else {
        try {
            $authorization = Get-Content -LiteralPath $authorizationPath -Raw | ConvertFrom-Json
            $authorizationRun = Get-Content -LiteralPath $authorizationRunPath -Raw | ConvertFrom-Json
            $authorizationIssuer = Get-MigrationAuthorizationIssuer -Provenance $provenance -Stage $Stage
            Test-RunMetadata -Metadata $authorizationRun `
                -AllowedWorkflowPaths @([string]$authorizationIssuer.workflowPath) `
                -ExpectedCommitSha ([string]$releaseManifest.commitSha) `
                -RequireSuccess
            if ([string]$authorizationRun.repository -cne [string]$authorizationIssuer.repository -or
                [string]$authorizationRun.ref -cne [string]$authorizationIssuer.ref) {
                Add-EvidenceFailure 'migration-apply-authorization-issuer'
            }
            $authorizedActorIds = ConvertTo-AuthorizedActorIdSet -Value $MigrationAuthorizedActorIdAllowlist
            if (-not $authorizedActorIds.Contains([string]$authorization.authorizedByActorId) -or
                [string]$authorization.authorizedByActor -ne [string]$authorizationRun.actor -or
                [string]$authorization.authorizedByActorId -ne [string]$authorizationRun.actorId -or
                [string]$authorization.authorizationRun.actor -ne [string]$authorizationRun.actor -or
                [string]$authorization.authorizationRun.actorId -ne [string]$authorizationRun.actorId) {
                Add-EvidenceFailure 'migration-apply-authorization-actor'
            }
            Test-RunBinding -Binding $authorization.authorizationRun -Metadata $authorizationRun -Prefix 'migration-authorization-run'
            if ($null -ne $preflightRun) {
                Test-RunBinding -Binding $authorization.sourcePreflightRun -Metadata $preflightRun -Prefix 'migration-authorization-preflight-run'
            }
            Test-RunBinding -Binding $authorization.sourceReleaseRun -Metadata $releaseRun -Prefix 'migration-authorization-release-run'
            foreach ($auxiliary in @(
                @{ Path = $authorizationPath; Relative = 'migration-apply-authorization.json'; Type = 'migration-apply-authorization' },
                @{ Path = $authorizationRunPath; Relative = 'migration-apply-authorization-run.json'; Type = 'migration-apply-authorization-run' }
            )) {
                $validatedFiles.Add([ordered]@{
                    path = $auxiliary.Relative
                    evidenceType = $auxiliary.Type
                    sha256 = Get-Sha256Lower -Path $auxiliary.Path
                    observedAtUtc = ''
                })
            }
        }
        catch {
            Add-EvidenceFailure "migration-apply-authorization-invalid:$($_.Exception.Message)"
        }
    }
}

$evidenceByType = @{}
foreach ($requiredEvidence in $stagePolicy.requiredEvidence) {
    $evidencePath = Join-Path $evidenceDirectory $requiredEvidence
    if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf) -or
        (Get-Item -LiteralPath $evidencePath).Length -eq 0) {
        Add-EvidenceFailure "missing-or-empty:$requiredEvidence"
        continue
    }

    try {
        $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json -DateKind String
        $evidenceType = [IO.Path]::GetFileNameWithoutExtension($requiredEvidence)
        if ([string](Get-PropertyValue $evidence 'schemaVersion') -ne '1.0.0' -or
            [string](Get-PropertyValue $evidence 'evidenceType') -ne $evidenceType -or
            [string](Get-PropertyValue $evidence 'status') -ne 'PASS' -or
            [string](Get-PropertyValue $evidence 'stage') -ne $Stage -or
            [string](Get-PropertyValue $evidence 'artifactSha256') -ne $actualAppSha256 -or
            [string](Get-PropertyValue $evidence 'releaseManifestSha256') -ne $releaseManifestSha256 -or
            [string](Get-PropertyValue $evidence 'releaseVersion') -ne [string]$releaseManifest.version -or
            [string](Get-PropertyValue $evidence 'releaseCommitSha') -ne ([string]$releaseManifest.commitSha).ToLowerInvariant()) {
            Add-EvidenceFailure "evidence-release-binding:$requiredEvidence"
        }
        try {
            $evidenceTrustedExecution = Assert-TrustedExecutionEvidence `
                -TrustedExecution (Get-PropertyValue $evidence 'trustedExecution') `
                -Policy $policy `
                -ReleaseManifest $releaseManifest
            if ($null -eq $trustedExecution) {
                $trustedExecution = $evidenceTrustedExecution
            }
            elseif ([string]$trustedExecution.workflowRef -cne
                    [string]$evidenceTrustedExecution.workflowRef -or
                [string]$trustedExecution.bundlePath -cne
                    [string]$evidenceTrustedExecution.bundlePath -or
                [string]$trustedExecution.bundleSha256 -cne
                    [string]$evidenceTrustedExecution.bundleSha256) {
                Add-EvidenceFailure "evidence-trusted-execution-mismatch:$requiredEvidence"
            }
        }
        catch {
            Add-EvidenceFailure "evidence-trusted-execution:${requiredEvidence}:$($_.Exception.Message)"
        }

        if ($evidenceType -eq 'migration-preflight') {
            if ($null -eq $preflightRun) {
                Add-EvidenceFailure "evidence-source-missing:$requiredEvidence"
            }
            else {
                Test-RunBinding -Binding (Get-PropertyValue $evidence 'sourceRun') -Metadata $preflightRun -Prefix "evidence-source:$requiredEvidence"
            }
        }
        else {
            Test-RunBinding -Binding (Get-PropertyValue $evidence 'sourceRun') -Metadata $operationRun -Prefix "evidence-source:$requiredEvidence"
        }

        $observed = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse([string](Get-PropertyValue $evidence 'observedAtUtc'), [ref]$observed)) {
            Add-EvidenceFailure "evidence-timestamp:$requiredEvidence"
        }
        else {
            $now = [DateTimeOffset]::UtcNow
            if ($observed -gt $now.AddMinutes([int]$provenance.maxClockSkewMinutes) -or
                $observed -lt $now.AddHours(-[int]$stagePolicy.maxEvidenceAgeHours)) {
                Add-EvidenceFailure "evidence-stale-or-future:$requiredEvidence"
            }
        }

        Test-OperationFields -Evidence $evidence -EvidenceType $evidenceType -RelativePath $requiredEvidence
        $evidenceByType[$evidenceType] = $evidence
        $validatedFiles.Add([ordered]@{
            path = $requiredEvidence
            evidenceType = $evidenceType
            sha256 = Get-Sha256Lower -Path $evidencePath
            observedAtUtc = [string](Get-PropertyValue $evidence 'observedAtUtc')
        })
    }
    catch {
        Add-EvidenceFailure "invalid-evidence:${requiredEvidence}:$($_.Exception.Message)"
    }
}

$targetFingerprints = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($type in @('migration-preflight', 'backup', 'migration-apply')) {
    if ($evidenceByType.ContainsKey($type)) {
        $fingerprint = [string](Get-PropertyValue $evidenceByType[$type].operation 'databaseTargetFingerprint')
        if (Test-Sha256 $fingerprint) {
            $null = $targetFingerprints.Add($fingerprint)
        }
    }
}
if ($evidenceByType.ContainsKey('restore')) {
    $restoredFingerprint = [string]$evidenceByType['restore'].operation.restoredTargetFingerprint
    if (Test-Sha256 $restoredFingerprint) {
        $null = $targetFingerprints.Add($restoredFingerprint)
    }
}
if ($targetFingerprints.Count -gt 1) {
    Add-EvidenceFailure 'database-target-fingerprint-mismatch'
}

foreach ($type in @('migration-preflight', 'migration-apply')) {
    if ($evidenceByType.ContainsKey($type)) {
        $operation = $evidenceByType[$type].operation
        $bundleRelativePath = [string](Get-PropertyValue $operation 'bundlePath')
        $bundleManifestEntry = @($releaseManifest.files | Where-Object { $_.path -eq $bundleRelativePath })
        if ($bundleManifestEntry.Count -ne 1 -or
            [string]$bundleManifestEntry[0].sha256 -ne [string](Get-PropertyValue $operation 'bundleSha256')) {
            Add-EvidenceFailure "$type-bundle-release-manifest-mismatch"
        }
    }
}
if ($evidenceByType.ContainsKey('migration-preflight')) {
    $preflightEntry = @($releaseManifest.files | Where-Object { $_.path -eq 'migrations/sql/000-preflight.sql' })
    if ($preflightEntry.Count -ne 1 -or
        [string]$preflightEntry[0].sha256 -ne [string]$evidenceByType['migration-preflight'].operation.preflightSqlSha256) {
        Add-EvidenceFailure 'migration-preflight-sql-release-manifest-mismatch'
    }
}
if ($evidenceByType.ContainsKey('backup')) {
    $backupSha256 = Get-Sha256Lower -Path (Join-Path $evidenceDirectory 'backup.json')
    if ($evidenceByType.ContainsKey('migration-apply') -and
        [string]$evidenceByType['migration-apply'].operation.backupEvidenceSha256 -ne $backupSha256) {
        Add-EvidenceFailure 'migration-apply-backup-evidence-mismatch'
    }
    if ($evidenceByType.ContainsKey('restore') -and
        [string]$evidenceByType['restore'].operation.backupEvidenceSha256 -ne $backupSha256) {
        Add-EvidenceFailure 'restore-backup-evidence-mismatch'
    }
}
if ($evidenceByType.ContainsKey('migration-preflight') -and $evidenceByType.ContainsKey('migration-apply')) {
    $preflightEvidenceSha256 = Get-Sha256Lower -Path (Join-Path $evidenceDirectory 'migration-preflight.json')
    if ([string]$evidenceByType['migration-apply'].operation.preflightEvidenceSha256 -ne
        $preflightEvidenceSha256) {
        Add-EvidenceFailure 'migration-apply-preflight-evidence-mismatch'
    }
}
if ($evidenceByType.ContainsKey('migration-apply') -and $null -ne $authorization -and $null -ne $targetMetadata) {
    $authorizationPath = Join-Path $evidenceDirectory 'migration-apply-authorization.json'
    $authorizationIssued = [DateTimeOffset]::MinValue
    $authorizationExpires = [DateTimeOffset]::MinValue
    $preflightObserved = [DateTimeOffset]::MinValue
    $now = [DateTimeOffset]::UtcNow
    $null = [DateTimeOffset]::TryParse([string]$evidenceByType['migration-preflight'].observedAtUtc, [ref]$preflightObserved)
    if ([string]$authorization.schemaVersion -ne '2.1.0' -or
        [string]$authorization.decision -ne 'AUTHORIZE' -or
        [string]$authorization.mode -ne 'Apply' -or
        -not (Test-JsonInteger -Value $authorization.operationOrder -Minimum 20 -Maximum 20) -or
        [string]$authorization.stage -ne $Stage -or
        [string]$authorization.environment -ne [string]$stagePolicy.githubEnvironment -or
        [string]$authorization.applicationSha256 -ne $actualAppSha256 -or
        [string]$authorization.releaseManifestSha256 -ne $releaseManifestSha256 -or
        [string]$authorization.releaseCommitSha -ne ([string]$releaseManifest.commitSha).ToLowerInvariant() -or
        [string]$authorization.releaseBinding.applicationSha256 -ne $actualAppSha256 -or
        [string]$authorization.releaseBinding.bundleSha256 -ne $protectedBundleSha256 -or
        [string]$authorization.releaseBinding.manifestSha256 -ne $releaseManifestSha256 -or
        [string]$authorization.releaseBinding.commitSha -ne
            ([string]$releaseManifest.commitSha).ToLowerInvariant() -or
        [string]$authorization.bundlePath -ne $protectedBundleRelativePath -or
        [string]$authorization.bundleSha256 -ne $protectedBundleSha256 -or
        [string]$authorization.targetFingerprint -ne
            [string]$evidenceByType['migration-apply'].operation.databaseTargetFingerprint -or
        [string]$authorization.preflightEvidenceSha256 -ne (Get-Sha256Lower -Path (Join-Path $evidenceDirectory 'migration-preflight.json')) -or
        ($Stage -eq 'Production' -and $null -eq $authorization.productionCtoAuthorization) -or
        ($Stage -ne 'Production' -and $null -ne $authorization.productionCtoAuthorization) -or
        [string]$evidenceByType['migration-apply'].operation.applyAuthorizationSha256 -ne (Get-Sha256Lower -Path $authorizationPath) -or
        -not [DateTimeOffset]::TryParse([string]$authorization.issuedAtUtc, [ref]$authorizationIssued) -or
        -not [DateTimeOffset]::TryParse([string]$authorization.expiresAtUtc, [ref]$authorizationExpires) -or
        $authorizationIssued -lt $preflightObserved -or
        $authorizationIssued -gt $now.AddMinutes([int]$provenance.maxClockSkewMinutes) -or
        $authorizationExpires -le $now -or
        $authorizationExpires -le $authorizationIssued) {
        Add-EvidenceFailure 'migration-apply-authorization-binding'
    }
}

$postMigrationEvidenceTypes = @($stagePolicy.postMigrationEvidence)
if ($postMigrationEvidenceTypes.Count -gt 0) {
    if ('migration-apply.json' -notin @($stagePolicy.requiredEvidence)) {
        Add-EvidenceFailure 'post-migration-policy-requires-apply'
    }
    elseif (-not $evidenceByType.ContainsKey('migration-apply')) {
        Add-EvidenceFailure 'post-migration-apply-missing'
    }
    else {
        $applyEvidence = $evidenceByType['migration-apply']
        $applyObserved = [DateTimeOffset]::MinValue
        $applyCompleted = [DateTimeOffset]::MinValue
        $expectedApplyOperationId = ''
        try {
            $expectedApplyOperationId = Get-MigrationApplyOperationId `
                -Stage $Stage `
                -OperationRun $applyEvidence.sourceRun
        }
        catch {
            Add-EvidenceFailure "post-migration-apply-operation-id:$($_.Exception.Message)"
        }
        if (-not [DateTimeOffset]::TryParse([string]$applyEvidence.observedAtUtc, [ref]$applyObserved) -or
            -not [DateTimeOffset]::TryParse([string](Get-PropertyValue $applyEvidence 'completedAtUtc'), [ref]$applyCompleted) -or
            $applyCompleted.Offset -ne [TimeSpan]::Zero -or
            $applyObserved -ne $applyCompleted -or
            [string]$applyEvidence.operation.operationId -cne $expectedApplyOperationId) {
            Add-EvidenceFailure 'post-migration-apply-completion-binding'
        }
        $applyEvidenceSha256 = Get-Sha256Lower -Path (Join-Path $evidenceDirectory 'migration-apply.json')
        foreach ($postMigrationEvidenceType in $postMigrationEvidenceTypes) {
            $requiredPath = "$postMigrationEvidenceType.json"
            if ($requiredPath -notin @($stagePolicy.requiredEvidence) -or
                -not $evidenceByType.ContainsKey($postMigrationEvidenceType)) {
                Add-EvidenceFailure "post-migration-evidence-missing:$postMigrationEvidenceType"
                continue
            }
            $postEvidence = $evidenceByType[$postMigrationEvidenceType]
            $postObserved = [DateTimeOffset]::MinValue
            $binding = $postEvidence.operation
            if (-not [DateTimeOffset]::TryParse([string]$postEvidence.observedAtUtc, [ref]$postObserved) -or
                $postObserved.Offset -ne [TimeSpan]::Zero -or
                $postObserved -le $applyCompleted -or
                [string](Get-PropertyValue $binding 'postMigrationApplyEvidenceSha256') -cne $applyEvidenceSha256 -or
                [string](Get-PropertyValue $binding 'postMigrationApplyOperationId') -cne $expectedApplyOperationId -or
                [string](Get-PropertyValue $binding 'postMigrationApplyCompletedAtUtc') -cne
                    $applyCompleted.ToString('O') -or
                $null -eq (Get-PropertyValue $binding 'postMigrationApplyRun')) {
                Add-EvidenceFailure "post-migration-order-or-binding:$postMigrationEvidenceType"
                continue
            }
            Test-RunBinding `
                -Binding (Get-PropertyValue $binding 'postMigrationApplyRun') `
                -Metadata $operationRun `
                -Prefix "post-migration-apply-run:$postMigrationEvidenceType"
        }
    }
}

if (-not $preparing -and $null -ne $bundleManifest) {
    $manifestFiles = @($bundleManifest.files)
    if ($manifestFiles.Count -ne $validatedFiles.Count) {
        Add-EvidenceFailure 'bundle-manifest-file-count'
    }
    foreach ($file in $validatedFiles) {
        $match = @($manifestFiles | Where-Object { $_.path -eq $file.path })
        if ($match.Count -ne 1 -or
            [string]$match[0].sha256 -ne [string]$file.sha256 -or
            [string]$match[0].observedAtUtc -ne [string]$file.observedAtUtc) {
            Add-EvidenceFailure "bundle-manifest-file-checksum:$($file.path)"
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output "FAIL  evidence:$_" }
    throw "Stage evidence validation failed with $($failures.Count) failure(s)."
}

if ($preparing) {
    $output = New-CleanDirectory -Path $ValidatedOutputRoot
    foreach ($file in $validatedFiles) {
        Copy-Item -LiteralPath (Join-Path $evidenceDirectory $file.path) -Destination (Join-Path $output $file.path)
    }
    $bundle = [ordered]@{
        schemaVersion = '1.0.0'
        stage = $Stage
        artifactSha256 = $actualAppSha256
        releaseManifestSha256 = $releaseManifestSha256
        releaseVersion = [string]$releaseManifest.version
        releaseCommitSha = ([string]$releaseManifest.commitSha).ToLowerInvariant()
        trustedExecution = $trustedExecution
        sourceReleaseRun = $releaseRun
        sourceOperationRun = $operationRun
        intakeRun = [ordered]@{
            schemaVersion = '1.0.0'
            repository = $IntakeRepository
            workflowPath = $IntakeWorkflowPath
            runId = $IntakeRunId
            runAttempt = $IntakeRunAttempt
            ref = $IntakeRef
            commitSha = $IntakeCommitSha.ToLowerInvariant()
            status = 'in_progress'
            conclusion = ''
        }
        validatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        files = @($validatedFiles)
    }
    Write-Utf8Json -Value $bundle -Path (Join-Path $output 'evidence-bundle-manifest.json') -Depth 30
    Write-Output "EVIDENCE-BUNDLE status=PASS stage=$Stage files=$($validatedFiles.Count) output=$output"
}
else {
    Write-Output "EVIDENCE-BUNDLE status=PASS stage=$Stage files=$($validatedFiles.Count)"
}
