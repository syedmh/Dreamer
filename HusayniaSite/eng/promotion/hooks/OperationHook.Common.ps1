Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\..\common\Release.Common.ps1')
. (Join-Path $PSScriptRoot '..\StageTarget.Common.ps1')

function Get-OperationHookProperty {
    param($Object, [string]$Name)

    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Test-OperationHookInteger {
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

function Assert-OperationHookTimestamp {
    param([string]$Value, [int]$MaximumAgeHours, [string]$Label)

    $parsed = [DateTimeOffset]::MinValue
    $now = [DateTimeOffset]::UtcNow
    if (-not [DateTimeOffset]::TryParse($Value, [ref]$parsed) -or
        $parsed -gt $now.AddMinutes(5) -or
        $parsed -lt $now.AddHours(-$MaximumAgeHours)) {
        throw "$Label is missing, stale, or in the future."
    }
    return $Value
}

function Assert-OperationHookWebUri {
    param([string]$Value, $TargetMetadata, [string]$RequiredPath = '')

    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne 'https' -or
        $uri.Host -cne "$($TargetMetadata.webAppName).azurewebsites.net" -or
        $uri.Port -ne 443 -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Query) -or
        -not [string]::IsNullOrEmpty($uri.Fragment) -or
        (-not [string]::IsNullOrWhiteSpace($RequiredPath) -and $uri.AbsolutePath -cne $RequiredPath)) {
        throw 'Protected operation endpoint is not the exact immutable stage web application target.'
    }
    return $uri
}

function Resolve-OperationHookSourcePath {
    param(
        [string]$EnvironmentVariable,
        [string]$FixtureRoot,
        [string]$FixtureFile
    )

    if (-not [string]::IsNullOrWhiteSpace($FixtureRoot)) {
        $root = (Resolve-Path -LiteralPath $FixtureRoot).Path
        $candidate = Join-Path $root $FixtureFile
        $resolved = (Resolve-Path -LiteralPath $candidate).Path
        $relative = [IO.Path]::GetRelativePath($root, $resolved)
        if ([IO.Path]::IsPathRooted($relative) -or $relative.StartsWith('..')) {
            throw 'Local dry-run fixture escaped its explicit fixture root.'
        }
        return $resolved
    }

    $configuredPath = [Environment]::GetEnvironmentVariable($EnvironmentVariable)
    if ([string]::IsNullOrWhiteSpace($configuredPath)) {
        throw "Protected operation input reference is missing: $EnvironmentVariable"
    }
    return (Resolve-Path -LiteralPath $configuredPath).Path
}

function Read-OperationHookSource {
    param(
        [string]$EnvironmentVariable,
        [string]$FixtureRoot,
        [string]$FixtureFile
    )

    $path = Resolve-OperationHookSourcePath `
        -EnvironmentVariable $EnvironmentVariable `
        -FixtureRoot $FixtureRoot `
        -FixtureFile $FixtureFile
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-Item -LiteralPath $path).Length -eq 0) {
        throw 'Protected operation evidence input is missing or empty.'
    }
    return [pscustomobject]@{
        Path = $path
        Value = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -DateKind String
        Sha256 = Get-Sha256Lower -Path $path
    }
}

function New-OperationHookContext {
    param([hashtable]$Parameters)

    $repositoryRoot = Resolve-HusayniaRepositoryRoot
    $policyPath = if ($Parameters.ContainsKey('PolicyPath') -and
        -not [string]::IsNullOrWhiteSpace([string]$Parameters.PolicyPath)) {
        [string]$Parameters.PolicyPath
    }
    else {
        Join-Path $repositoryRoot 'pipelines\config\promotion-policy.json'
    }
    $policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
    $stage = [string]$Parameters.Stage
    $stagePolicy = @($policy.stages | Where-Object { $_.name -eq $stage })
    if ($stagePolicy.Count -ne 1) {
        throw "Promotion policy must contain exactly one $stage stage."
    }
    $stagePolicy = $stagePolicy[0]
    if ($stage -eq 'Production' -and -not [bool]$stagePolicy.deploymentEnabled) {
        throw 'Production operation hooks are unreachable while Production deploymentEnabled is false.'
    }
    $targetMetadata = Read-StageTargetMetadata `
        -Path ([string]$Parameters.StageTargetMetadataPath) `
        -Stage $stage `
        -StagePolicy $stagePolicy
    $authorizationContext = $null
    if ($stage -eq 'Production') {
        $authorizationContextPath = [string]$Parameters.ProductionAuthorizationContextPath
        if ([string]::IsNullOrWhiteSpace($authorizationContextPath) -or
            -not (Test-Path -LiteralPath $authorizationContextPath -PathType Leaf)) {
            throw 'Production operation hook authorization context is missing.'
        }
        $authorizationContext = Get-Content -LiteralPath $authorizationContextPath -Raw | ConvertFrom-Json
        $authorizedAt = [DateTimeOffset]::MinValue
        $now = [DateTimeOffset]::UtcNow
        if ([string]$authorizationContext.schemaVersion -ne '1.0.0' -or
            [string]$authorizationContext.status -ne 'PASS' -or
            [string]$authorizationContext.stage -ne 'Production' -or
            [string]$authorizationContext.environment -ne [string]$stagePolicy.githubEnvironment -or
            [string]$authorizationContext.targetFingerprint -ne
                (Get-StageTargetFingerprint -TargetMetadata $targetMetadata) -or
            [string]::IsNullOrWhiteSpace([string]$authorizationContext.approvalReference) -or
            -not [DateTimeOffset]::TryParse([string]$authorizationContext.authorizedAtUtc, [ref]$authorizedAt) -or
            $authorizedAt -gt $now.AddMinutes(5) -or
            $authorizedAt -lt $now.AddHours(-[int]$stagePolicy.maxEvidenceAgeHours)) {
            throw 'Production operation hook authorization context is invalid.'
        }
    }
    $artifact = (Resolve-Path -LiteralPath ([string]$Parameters.ArtifactRoot)).Path
    $manifestPath = Join-Path $artifact 'release\release-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $appEntry = @($manifest.files | Where-Object { $_.path -eq 'app/Husaynia.Web.zip' })
    if ($appEntry.Count -ne 1) {
        throw 'Protected operation hook could not bind the immutable application archive.'
    }
    if ($stage -eq 'Production' -and
        ([string]$authorizationContext.artifactSha256 -ne [string]$appEntry[0].sha256 -or
         [string]$authorizationContext.releaseManifestSha256 -ne (Get-Sha256Lower -Path $manifestPath) -or
         [string]$authorizationContext.releaseCommitSha -ne ([string]$manifest.commitSha).ToLowerInvariant() -or
         -not (Test-Sha256 ([string]$authorizationContext.ctoAuthorizationSha256)) -or
         -not (Test-Sha256 ([string]$authorizationContext.ctoAuthorizationRunSha256)))) {
        throw 'Production operation hook authorization context is not bound to the immutable release and CTO evidence.'
    }
    $sourceRun = Get-Content -LiteralPath ([string]$Parameters.SourceRunMetadataPath) -Raw | ConvertFrom-Json

    return [pscustomobject]@{
        RepositoryRoot = $repositoryRoot
        Policy = $policy
        StagePolicy = $stagePolicy
        Stage = $stage
        ArtifactRoot = $artifact
        Manifest = $manifest
        ManifestPath = $manifestPath
        ArtifactSha256 = [string]$appEntry[0].sha256
        ManifestSha256 = Get-Sha256Lower -Path $manifestPath
        TargetMetadata = $targetMetadata
        TargetMetadataSha256 = Get-Sha256Lower -Path ([string]$Parameters.StageTargetMetadataPath)
        TargetFingerprint = Get-StageTargetFingerprint -TargetMetadata $targetMetadata
        SourceRun = $sourceRun
        OutputPath = [string]$Parameters.OutputPath
        FixtureRoot = if ($Parameters.ContainsKey('LocalDryRunFixtureRoot')) {
            [string]$Parameters.LocalDryRunFixtureRoot
        }
        else {
            ''
        }
    }
}

function Write-OperationHookEvidence {
    param(
        $Context,
        [string]$EvidenceType,
        [string]$ObservedAtUtc,
        $Operation
    )

    Write-Utf8Json -Value ([ordered]@{
        schemaVersion = '1.0.0'
        evidenceType = $EvidenceType
        status = 'PASS'
        stage = $Context.Stage
        artifactSha256 = $Context.ArtifactSha256
        releaseManifestSha256 = $Context.ManifestSha256
        releaseVersion = [string]$Context.Manifest.version
        releaseCommitSha = ([string]$Context.Manifest.commitSha).ToLowerInvariant()
        observedAtUtc = $ObservedAtUtc
        sourceRun = $Context.SourceRun
        operation = $Operation
    }) -Path $Context.OutputPath -Depth 40
}

function Get-OperationHookPostMigrationBinding {
    param(
        $Context,
        [hashtable]$Parameters,
        $Source,
        [string]$SourceObservedAtUtc,
        [string]$EvidenceType
    )

    if ($EvidenceType -notin @($Context.StagePolicy.postMigrationEvidence)) {
        throw "$EvidenceType is not an approved post-migration verification type for $($Context.Stage)."
    }
    $applyPath = if ($Parameters.ContainsKey('MigrationApplyEvidencePath')) {
        [string]$Parameters.MigrationApplyEvidencePath
    }
    else {
        ''
    }
    if ([string]::IsNullOrWhiteSpace($applyPath) -or
        -not (Test-Path -LiteralPath $applyPath -PathType Leaf)) {
        throw "Post-migration $EvidenceType evidence requires successful Apply evidence."
    }
    $applyEvidence = Get-Content -LiteralPath $applyPath -Raw | ConvertFrom-Json -DateKind String
    $applyCompleted = [DateTimeOffset]::MinValue
    $applyObserved = [DateTimeOffset]::MinValue
    $sourceObserved = [DateTimeOffset]::MinValue
    $operationId = [string](Get-OperationHookProperty $applyEvidence.operation 'operationId')
    if (-not [DateTimeOffset]::TryParse(
            [string](Get-OperationHookProperty $applyEvidence 'completedAtUtc'),
            [ref]$applyCompleted) -or
        -not [DateTimeOffset]::TryParse(
            [string](Get-OperationHookProperty $applyEvidence 'observedAtUtc'),
            [ref]$applyObserved) -or
        -not [DateTimeOffset]::TryParse($SourceObservedAtUtc, [ref]$sourceObserved) -or
        $applyCompleted.Offset -ne [TimeSpan]::Zero -or
        $applyObserved -ne $applyCompleted -or
        $sourceObserved -le $applyCompleted) {
        throw "Post-migration $EvidenceType evidence is not strictly ordered after successful Apply " +
            "(applyObserved=$($applyObserved.ToString('O')); " +
            "applyCompleted=$($applyCompleted.ToString('O')); " +
            "sourceObserved=$($sourceObserved.ToString('O')))."
    }
    if ([string](Get-OperationHookProperty $applyEvidence 'schemaVersion') -cne '1.0.0' -or
        [string](Get-OperationHookProperty $applyEvidence 'evidenceType') -cne 'migration-apply' -or
        [string](Get-OperationHookProperty $applyEvidence 'status') -cne 'PASS' -or
        [string](Get-OperationHookProperty $applyEvidence 'stage') -cne $Context.Stage -or
        [string](Get-OperationHookProperty $applyEvidence 'artifactSha256') -cne $Context.ArtifactSha256 -or
        [string](Get-OperationHookProperty $applyEvidence 'releaseManifestSha256') -cne $Context.ManifestSha256 -or
        [string](Get-OperationHookProperty $applyEvidence 'releaseCommitSha') -cne
            ([string]$Context.Manifest.commitSha).ToLowerInvariant() -or
        [string](Get-OperationHookProperty $applyEvidence.operation 'stageTargetMetadataSha256') -cne
            $Context.TargetMetadataSha256 -or
        [string](Get-OperationHookProperty $applyEvidence.operation 'databaseTargetFingerprint') -cne
            $Context.TargetFingerprint -or
        (Get-OperationHookProperty $applyEvidence.operation 'applied') -ne $true -or
        $operationId -notmatch "^migration-apply/$($Context.Stage)/[1-9][0-9]*/[1-9][0-9]*$") {
        throw "Post-migration $EvidenceType evidence is not bound to a successful Apply on the exact release and target."
    }
    Assert-GitHubRunBinding -Binding $applyEvidence.sourceRun -Metadata $Context.SourceRun
    if ([string](Get-OperationHookProperty $Source 'artifactSha256') -cne $Context.ArtifactSha256 -or
        [string](Get-OperationHookProperty $Source 'releaseManifestSha256') -cne $Context.ManifestSha256 -or
        [string](Get-OperationHookProperty $Source 'releaseCommitSha') -cne
            ([string]$Context.Manifest.commitSha).ToLowerInvariant() -or
        [string](Get-OperationHookProperty $Source 'stageTargetMetadataSha256') -cne
            $Context.TargetMetadataSha256 -or
        [string](Get-OperationHookProperty $Source 'targetFingerprint') -cne $Context.TargetFingerprint -or
        [string](Get-OperationHookProperty $Source 'reportPurpose') -cne 'post-migration-verification') {
        throw "Post-migration $EvidenceType source report is not bound to the immutable release and target."
    }
    Assert-GitHubRunBinding -Binding $Source.sourceRun -Metadata $Context.SourceRun
    $binding = Get-OperationHookProperty $Source 'postMigrationApply'
    Assert-StageTargetExactProperties -Object $binding -Label "Post-migration $EvidenceType Apply binding" -Expected @(
        'evidenceSha256',
        'operationId',
        'completedAtUtc',
        'sourceRun'
    )
    if ([string](Get-OperationHookProperty $binding 'evidenceSha256') -cne (Get-Sha256Lower -Path $applyPath) -or
        [string](Get-OperationHookProperty $binding 'operationId') -cne $operationId -or
        [string](Get-OperationHookProperty $binding 'completedAtUtc') -cne $applyCompleted.ToString('O')) {
        throw "Post-migration $EvidenceType source report does not bind the exact successful Apply evidence."
    }
    Assert-GitHubRunBinding -Binding $binding.sourceRun -Metadata $Context.SourceRun
    return [ordered]@{
        postMigrationApplyEvidenceSha256 = Get-Sha256Lower -Path $applyPath
        postMigrationApplyOperationId = $operationId
        postMigrationApplyCompletedAtUtc = $applyCompleted.ToString('O')
        postMigrationApplyRun = $Context.SourceRun
    }
}

function Invoke-ReviewedOperationHook {
    param(
        [Parameter(Mandatory = $true)]
        [string]$EvidenceType,
        [Parameter(Mandatory = $true)]
        [hashtable]$Parameters
    )

    $context = New-OperationHookContext -Parameters $Parameters
    $target = $context.TargetMetadata
    $maximumAgeHours = [int]$context.StagePolicy.maxEvidenceAgeHours
    $sourceObservedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $operation = switch ($EvidenceType) {
        'configuration' {
            if ([string]::IsNullOrWhiteSpace($context.FixtureRoot)) {
                $dataIsolationKey = [Environment]::GetEnvironmentVariable('T21_DATA_ISOLATION_KEY')
                $providerModesJson = [Environment]::GetEnvironmentVariable('T21_PROVIDER_MODES_JSON')
                if ($dataIsolationKey -cne [string]$context.StagePolicy.dataIsolationKey -or
                    $dataIsolationKey -cne [string]$target.dataIsolationKey -or
                    [string]::IsNullOrWhiteSpace($providerModesJson)) {
                    throw 'Protected configuration/isolation inputs do not match the immutable stage target.'
                }
                $providerModes = $providerModesJson | ConvertFrom-Json
                Assert-StageTargetExactProperties -Object $providerModes -Label 'Protected provider mode input' -Expected @(
                    'payments',
                    'messaging',
                    'analytics',
                    'contentMutation'
                )
                foreach ($providerName in @('payments', 'messaging', 'analytics', 'contentMutation')) {
                    if ([string](Get-OperationHookProperty $providerModes $providerName) -cne
                        [string](Get-OperationHookProperty $target.providerModes $providerName)) {
                        throw "Protected provider mode input does not match stage target metadata: $providerName."
                    }
                }
            }
            [ordered]@{
                stageTargetMetadataSha256 = $context.TargetMetadataSha256
                dataIsolationKey = [string]$target.dataIsolationKey
                serviceConnectionSecretName = [string]$target.serviceConnectionSecretName
                secretReferences = @($target.secretReferences)
                providerModes = $target.providerModes
                isolationValidated = $true
                nonProductionLiveProvidersRejected = $context.Stage -ne 'Production'
            }
        }
        'backup' {
            $source = Read-OperationHookSource -EnvironmentVariable 'T21_BACKUP_EVIDENCE_PATH' `
                -FixtureRoot $context.FixtureRoot -FixtureFile 'source-backup.json'
            $value = $source.Value
            $retention = [DateTimeOffset]::MinValue
            $sourceObservedAtUtc = Assert-OperationHookTimestamp -Value ([string]$value.observedAtUtc) -MaximumAgeHours $maximumAgeHours -Label 'Backup observation'
            if ([string]$value.schemaVersion -ne '1.0.0' -or
                [string]$value.provider -ne 'AzureSql' -or
                [string]$value.stage -ne $context.Stage -or
                [string]$value.sqlServerResourceId -ne [string]$target.sqlServerResourceId -or
                [string]$value.sqlDatabaseResourceId -ne [string]$target.sqlDatabaseResourceId -or
                [string]$value.observedDatabaseName -ne [string]$target.sqlDatabaseName -or
                [string]::IsNullOrWhiteSpace([string]$value.backupId) -or
                $value.restorable -ne $true -or
                $value.integrityVerified -ne $true -or
                -not [DateTimeOffset]::TryParse([string]$value.retentionUntilUtc, [ref]$retention) -or
                $retention -le [DateTimeOffset]::UtcNow) {
                throw 'Backup provider evidence does not prove a fresh restorable backup for the exact stage database.'
            }
            [ordered]@{
                stageTargetMetadataSha256 = $context.TargetMetadataSha256
                backupId = [string]$value.backupId
                databaseTargetFingerprint = $context.TargetFingerprint
                restorable = $true
                integrityVerified = $true
                retentionUntilUtc = $retention.ToUniversalTime().ToString('O')
                providerEvidenceSha256 = $source.Sha256
            }
        }
        'health' {
            $source = Read-OperationHookSource -EnvironmentVariable 'T21_HEALTH_REPORT_PATH' `
                -FixtureRoot $context.FixtureRoot -FixtureFile 'source-health.json'
            $value = $source.Value
            $endpoint = [string]$value.endpoint
            $null = Assert-OperationHookWebUri -Value $endpoint -TargetMetadata $target -RequiredPath '/health'
            if ([string]::IsNullOrWhiteSpace($context.FixtureRoot)) {
                $protectedEndpoint = [Environment]::GetEnvironmentVariable('T21_HEALTH_ENDPOINT')
                $null = Assert-OperationHookWebUri -Value $protectedEndpoint -TargetMetadata $target -RequiredPath '/health'
                if ($endpoint -cne $protectedEndpoint) {
                    throw 'Health report endpoint does not match the protected stage endpoint input.'
                }
            }
            $sourceObservedAtUtc = Assert-OperationHookTimestamp -Value ([string]$value.observedAtUtc) -MaximumAgeHours $maximumAgeHours -Label 'Health observation'
            if ([string]$value.schemaVersion -ne '1.0.0' -or
                [string]$value.stage -ne $context.Stage -or
                [string]$value.webAppResourceId -ne [string]$target.webAppResourceId -or
                -not (Test-OperationHookInteger -Value $value.statusCode -Minimum 100 -Maximum 599) -or
                -not (Test-OperationHookInteger -Value $value.sampleCount -Minimum 1) -or
                $value.healthy -ne $true) {
                throw 'Health report does not prove a successful probe of the exact stage endpoint.'
            }
            $statusCode = [int]$value.statusCode
            $healthy = $value.healthy
            $sampleCount = [int]$value.sampleCount
            $sourceSha256 = $source.Sha256
            if (-not $healthy -or $statusCode -ne 200) {
                throw 'Health probe did not return a healthy HTTP 200 result.'
            }
            [ordered]@{
                stageTargetMetadataSha256 = $context.TargetMetadataSha256
                endpoint = $endpoint
                webAppResourceId = [string]$target.webAppResourceId
                healthy = $true
                statusCode = $statusCode
                sampleCount = $sampleCount
                sourceEvidenceSha256 = $sourceSha256
            }
        }
        'smoke' {
            $source = Read-OperationHookSource -EnvironmentVariable 'T21_SMOKE_REPORT_PATH' `
                -FixtureRoot $context.FixtureRoot -FixtureFile 'source-smoke.json'
            $value = $source.Value
            $null = Assert-OperationHookWebUri -Value ([string]$value.baseUri) -TargetMetadata $target
            $sourceObservedAtUtc = Assert-OperationHookTimestamp -Value ([string]$value.observedAtUtc) -MaximumAgeHours $maximumAgeHours -Label 'Smoke report'
            if ([string]$value.schemaVersion -ne '1.0.0' -or
                [string]$value.stage -ne $context.Stage -or
                [string]$value.webAppResourceId -ne [string]$target.webAppResourceId -or
                -not (Test-OperationHookInteger -Value $value.passedTests -Minimum 1) -or
                -not (Test-OperationHookInteger -Value $value.failedTests -Minimum 0 -Maximum 0) -or
                @($value.tests).Count -lt [int]$value.passedTests) {
                throw 'Smoke report is missing executed passing tests for the exact stage web application.'
            }
            [ordered]@{
                stageTargetMetadataSha256 = $context.TargetMetadataSha256
                passedTests = [int]$value.passedTests
                failedTests = 0
                reportSha256 = $source.Sha256
            }
        }
        'crawl' {
            $source = Read-OperationHookSource -EnvironmentVariable 'T21_CRAWL_REPORT_PATH' `
                -FixtureRoot $context.FixtureRoot -FixtureFile 'source-crawl.json'
            $value = $source.Value
            $null = Assert-OperationHookWebUri -Value ([string]$value.baseUri) -TargetMetadata $target
            $sourceObservedAtUtc = Assert-OperationHookTimestamp -Value ([string]$value.observedAtUtc) -MaximumAgeHours $maximumAgeHours -Label 'Crawl report'
            if ([string]$value.schemaVersion -ne '1.0.0' -or
                [string]$value.stage -ne $context.Stage -or
                [string]$value.webAppResourceId -ne [string]$target.webAppResourceId -or
                -not (Test-OperationHookInteger -Value $value.urlsChecked -Minimum 1) -or
                -not (Test-OperationHookInteger -Value $value.brokenLinks -Minimum 0 -Maximum 0) -or
                -not (Test-OperationHookInteger -Value $value.redirectMismatches -Minimum 0 -Maximum 0)) {
                throw 'Crawl report does not prove a clean crawl and redirect check for the exact stage.'
            }
            [ordered]@{
                stageTargetMetadataSha256 = $context.TargetMetadataSha256
                urlsChecked = [int]$value.urlsChecked
                brokenLinks = 0
                redirectMismatches = 0
                reportSha256 = $source.Sha256
            }
        }
        'performance' {
            $source = Read-OperationHookSource -EnvironmentVariable 'T21_PERFORMANCE_REPORT_PATH' `
                -FixtureRoot $context.FixtureRoot -FixtureFile 'source-performance.json'
            $value = $source.Value
            $null = Assert-OperationHookWebUri -Value ([string]$value.baseUri) -TargetMetadata $target
            $null = Assert-OperationHookWebUri -Value ([string]$value.endpoint) -TargetMetadata $target
            $sourceObservedAtUtc = Assert-OperationHookTimestamp -Value ([string]$value.observedAtUtc) `
                -MaximumAgeHours $maximumAgeHours -Label 'Performance report'
            if ([string]$value.schemaVersion -ne '1.0.0' -or
                [string]$value.stage -ne $context.Stage -or
                [string]$value.webAppResourceId -ne [string]$target.webAppResourceId -or
                -not (Test-OperationHookInteger -Value $value.sampleCount -Minimum 1) -or
                -not (Test-OperationHookInteger -Value $value.successfulSamples -Minimum 1) -or
                -not (Test-OperationHookInteger -Value $value.failedSamples -Minimum 0 -Maximum 0) -or
                -not (Test-OperationHookInteger -Value $value.observedP95Milliseconds -Minimum 0) -or
                -not (Test-OperationHookInteger -Value $value.maxP95Milliseconds -Minimum 1) -or
                [int64]$value.successfulSamples -ne [int64]$value.sampleCount -or
                [int64]$value.observedP95Milliseconds -gt [int64]$value.maxP95Milliseconds -or
                $value.thresholdsMet -ne $true -or
                $value.readOnly -ne $true) {
                throw 'Performance report does not prove real read-only measurements within the approved threshold.'
            }
            [ordered]@{
                stageTargetMetadataSha256 = $context.TargetMetadataSha256
                endpoint = [string]$value.endpoint
                sampleCount = [int]$value.sampleCount
                successfulSamples = [int]$value.successfulSamples
                failedSamples = 0
                observedP95Milliseconds = [int64]$value.observedP95Milliseconds
                maxP95Milliseconds = [int64]$value.maxP95Milliseconds
                thresholdsMet = $true
                readOnly = $true
                reportSha256 = $source.Sha256
            }
        }
        'accessibility' {
            $source = Read-OperationHookSource -EnvironmentVariable 'T21_ACCESSIBILITY_REPORT_PATH' `
                -FixtureRoot $context.FixtureRoot -FixtureFile 'source-accessibility.json'
            $value = $source.Value
            $null = Assert-OperationHookWebUri -Value ([string]$value.baseUri) -TargetMetadata $target
            $sourceObservedAtUtc = Assert-OperationHookTimestamp -Value ([string]$value.observedAtUtc) -MaximumAgeHours $maximumAgeHours -Label 'Accessibility report'
            if ([string]$value.schemaVersion -ne '1.0.0' -or
                [string]$value.stage -ne $context.Stage -or
                [string]$value.webAppResourceId -ne [string]$target.webAppResourceId -or
                [string]::IsNullOrWhiteSpace([string]$value.tool) -or
                [string]::IsNullOrWhiteSpace([string]$value.toolVersion) -or
                -not (Test-OperationHookInteger -Value $value.testedPages -Minimum 1) -or
                -not (Test-OperationHookInteger -Value $value.criticalViolations -Minimum 0 -Maximum 0) -or
                -not (Test-OperationHookInteger -Value $value.seriousViolations -Minimum 0 -Maximum 0)) {
                throw 'Accessibility report is absent, unbound, or contains critical/serious violations.'
            }
            [ordered]@{
                stageTargetMetadataSha256 = $context.TargetMetadataSha256
                testedPages = [int]$value.testedPages
                criticalViolations = 0
                seriousViolations = 0
                tool = [string]$value.tool
                toolVersion = [string]$value.toolVersion
                reportSha256 = $source.Sha256
            }
        }
        'visual' {
            $source = Read-OperationHookSource -EnvironmentVariable 'T21_VISUAL_REPORT_PATH' `
                -FixtureRoot $context.FixtureRoot -FixtureFile 'source-visual.json'
            $value = $source.Value
            $null = Assert-OperationHookWebUri -Value ([string]$value.baseUri) -TargetMetadata $target
            $sourceObservedAtUtc = Assert-OperationHookTimestamp -Value ([string]$value.observedAtUtc) -MaximumAgeHours $maximumAgeHours -Label 'Visual report'
            if ([string]$value.schemaVersion -ne '1.0.0' -or
                [string]$value.stage -ne $context.Stage -or
                [string]$value.webAppResourceId -ne [string]$target.webAppResourceId -or
                -not (Test-Sha256 ([string]$value.baselineArtifactSha256)) -or
                -not (Test-OperationHookInteger -Value $value.comparedPages -Minimum 1) -or
                -not (Test-OperationHookInteger -Value $value.failedComparisons -Minimum 0 -Maximum 0) -or
                $value.thresholdApproved -ne $true) {
                throw 'Visual report is absent, unbound, or contains failed comparisons.'
            }
            [ordered]@{
                stageTargetMetadataSha256 = $context.TargetMetadataSha256
                comparedPages = [int]$value.comparedPages
                failedComparisons = 0
                thresholdApproved = $true
                baselineArtifactSha256 = [string]$value.baselineArtifactSha256
                reportSha256 = $source.Sha256
            }
        }
        'sandbox-integrations' {
            $source = Read-OperationHookSource -EnvironmentVariable 'T21_SANDBOX_REPORT_PATH' `
                -FixtureRoot $context.FixtureRoot -FixtureFile 'source-sandbox-integrations.json'
            $value = $source.Value
            $sourceObservedAtUtc = Assert-OperationHookTimestamp -Value ([string]$value.observedAtUtc) -MaximumAgeHours $maximumAgeHours -Label 'Sandbox integration report'
            if ([string]$value.schemaVersion -ne '1.0.0' -or
                [string]$value.stage -ne $context.Stage -or
                [string]$value.providerMode -ne 'sandbox' -or
                $value.liveCredentialsUsed -ne $false -or
                -not (Test-OperationHookInteger -Value $value.passedChecks -Minimum 1) -or
                -not (Test-OperationHookInteger -Value $value.failedChecks -Minimum 0 -Maximum 0) -or
                @($value.providers).Count -eq 0) {
                throw 'Sandbox integration report is absent, failed, or indicates live credentials.'
            }
            [ordered]@{
                stageTargetMetadataSha256 = $context.TargetMetadataSha256
                providerMode = 'sandbox'
                liveCredentialsUsed = $false
                passedChecks = [int]$value.passedChecks
                failedChecks = 0
                providers = @($value.providers)
                reportSha256 = $source.Sha256
            }
        }
        'rollback' {
            $source = Read-OperationHookSource -EnvironmentVariable 'T21_ROLLBACK_REFERENCE_PATH' `
                -FixtureRoot $context.FixtureRoot -FixtureFile 'source-rollback.json'
            $value = $source.Value
            $sourceObservedAtUtc = Assert-OperationHookTimestamp -Value ([string]$value.observedAtUtc) -MaximumAgeHours $maximumAgeHours -Label 'Rollback reference'
            if ([string]$value.schemaVersion -ne '1.0.0' -or
                [string]$value.stage -ne $context.Stage -or
                [string]$value.decision -notin @('READY', 'NOT_REQUIRED') -or
            -not (Test-Sha256 ([string]$value.lastKnownGoodArtifactSha256)) -or
            -not (Test-Sha256 ([string]$value.releaseManifestSha256)) -or
                    [string]::IsNullOrWhiteSpace([string]$value.releaseVersion) -or
                    [string]::IsNullOrWhiteSpace([string]$value.databaseRestorePointId) -or
                    $value.rehearsalPassed -ne $true -or
                    $value.restorePointVerified -ne $true) {
                throw 'Rollback reference is absent or does not bind a rehearsed release and database restore point.'
            }
            [ordered]@{
                stageTargetMetadataSha256 = $context.TargetMetadataSha256
                decision = [string]$value.decision
                lastKnownGoodArtifactSha256 = [string]$value.lastKnownGoodArtifactSha256
                releaseManifestSha256 = [string]$value.releaseManifestSha256
                releaseVersion = [string]$value.releaseVersion
                databaseRestorePointId = [string]$value.databaseRestorePointId
                rehearsalPassed = $true
                restorePointVerified = $true
                referenceSha256 = $source.Sha256
            }
        }
        'restore' {
            if (-not $Parameters.ContainsKey('BackupEvidencePath') -or
                [string]::IsNullOrWhiteSpace([string]$Parameters.BackupEvidencePath) -or
                -not (Test-Path -LiteralPath ([string]$Parameters.BackupEvidencePath) -PathType Leaf)) {
                throw 'Restore validation requires the normalized backup evidence produced in this run.'
            }
            $source = Read-OperationHookSource -EnvironmentVariable 'T21_RESTORE_EVIDENCE_PATH' `
                -FixtureRoot $context.FixtureRoot -FixtureFile 'source-restore.json'
            $value = $source.Value
            $sourceObservedAtUtc = Assert-OperationHookTimestamp -Value ([string]$value.observedAtUtc) -MaximumAgeHours $maximumAgeHours -Label 'Restore report'
            $backupSha256 = Get-Sha256Lower -Path ([string]$Parameters.BackupEvidencePath)
            $backupEvidence = Get-Content -LiteralPath ([string]$Parameters.BackupEvidencePath) -Raw | ConvertFrom-Json
            if ([string]$value.schemaVersion -ne '1.0.0' -or
                [string]$value.stage -ne $context.Stage -or
                [string]$value.operation -ne 'isolated-restore-validation' -or
                [string]$value.backupId -ne [string]$backupEvidence.operation.backupId -or
                [string]$value.backupSourceReportSha256 -ne [string]$backupEvidence.operation.providerEvidenceSha256 -or
                [string]$value.sourceTargetFingerprint -ne $context.TargetFingerprint -or
                [string]$value.sourceSqlDatabaseResourceId -ne [string]$target.sqlDatabaseResourceId -or
                [string]$value.artifactSha256 -ne $context.ArtifactSha256 -or
                [string]$value.releaseManifestSha256 -ne $context.ManifestSha256 -or
                [string]::IsNullOrWhiteSpace([string]$value.isolatedRestoreTargetResourceId) -or
                [string]$value.isolatedRestoreTargetResourceId -eq [string]$target.sqlDatabaseResourceId -or
                $value.restoreTestPassed -ne $true -or
                $value.dataIntegrityVerified -ne $true -or
                $value.sourceDatabaseUnchanged -ne $true -or
                $value.cleanupCompleted -ne $true) {
                throw 'Restore report does not prove a protected isolated restore bound to the exact backup, target, and release.'
            }
            [ordered]@{
                stageTargetMetadataSha256 = $context.TargetMetadataSha256
                backupEvidenceSha256 = $backupSha256
                restoredTargetFingerprint = $context.TargetFingerprint
                isolatedRestoreTargetResourceId = [string]$value.isolatedRestoreTargetResourceId
                restoreTestPassed = $true
                dataIntegrityVerified = $true
                sourceDatabaseUnchanged = $true
                cleanupCompleted = $true
                reportSha256 = $source.Sha256
            }
        }
        'change-record' {
            $source = Read-OperationHookSource -EnvironmentVariable 'T21_CHANGE_RECORD_PATH' `
                -FixtureRoot $context.FixtureRoot -FixtureFile 'source-change-record.json'
            $value = $source.Value
            $expiry = [DateTimeOffset]::MinValue
            $sourceObservedAtUtc = Assert-OperationHookTimestamp -Value ([string]$value.observedAtUtc) -MaximumAgeHours $maximumAgeHours -Label 'Change record'
            if ([string]$value.schemaVersion -ne '1.0.0' -or
                [string]$value.stage -ne $context.Stage -or
                [string]$value.decision -ne 'APPROVED' -or
                [string]::IsNullOrWhiteSpace([string]$value.changeId) -or
                [string]::IsNullOrWhiteSpace([string]$value.owner) -or
                -not [DateTimeOffset]::TryParse([string]$value.expiresAtUtc, [ref]$expiry) -or
                $expiry -le [DateTimeOffset]::UtcNow) {
                throw 'Change record is absent, expired, or not approved for the selected stage.'
            }
            [ordered]@{
                stageTargetMetadataSha256 = $context.TargetMetadataSha256
                changeId = [string]$value.changeId
                decision = 'APPROVED'
                owner = [string]$value.owner
                expiresAtUtc = $expiry.ToUniversalTime().ToString('O')
                recordSha256 = $source.Sha256
            }
        }
        default {
            throw "Unsupported reviewed operation hook: $EvidenceType"
        }
    }

    if ($EvidenceType -in @($context.StagePolicy.postMigrationEvidence)) {
        $postMigrationBinding = Get-OperationHookPostMigrationBinding `
            -Context $context `
            -Parameters $Parameters `
            -Source $source.Value `
            -SourceObservedAtUtc $sourceObservedAtUtc `
            -EvidenceType $EvidenceType
        foreach ($name in $postMigrationBinding.Keys) {
            $operation[$name] = $postMigrationBinding[$name]
        }
    }

    Write-OperationHookEvidence -Context $context -EvidenceType $EvidenceType `
        -ObservedAtUtc $sourceObservedAtUtc -Operation $operation
}
