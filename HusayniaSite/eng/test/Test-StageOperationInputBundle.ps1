[CmdletBinding()]
param(
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
. (Join-Path $PSScriptRoot '..\promotion\StageTarget.Common.ps1')
. (Join-Path $PSScriptRoot '..\promotion\StageOperationInput.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Resolve-HusayniaRepositoryRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$pwsh = (Get-Process -Id $PID).Path
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "husaynia-stage-operation-inputs-$([guid]::NewGuid().ToString('N'))"
$passed = 0
$failed = 0

function Assert-Contract {
    param([string]$Name, [bool]$Condition, [string]$Failure)

    if ($Condition) {
        $script:passed++
        Write-Output "PASS  $Name"
    }
    else {
        $script:failed++
        Write-Output "FAIL  $Name :: $Failure"
    }
}

function Write-TestRunMetadata {
    param(
        [string]$Path,
        [string]$WorkflowPath,
        [string]$CommitSha,
        [string]$RunId = '8101',
        [string]$Conclusion = 'success'
    )

    Write-Utf8Json -Value ([ordered]@{
        schemaVersion = '1.0.0'
        repository = 'syedmh/Dreamer'
        workflowPath = $WorkflowPath
        workflowName = [IO.Path]::GetFileNameWithoutExtension($WorkflowPath)
        runId = $RunId
        runAttempt = 1
        ref = 'refs/heads/main'
        commitSha = $CommitSha
        event = 'workflow_dispatch'
        actor = 'test-actor'
        actorId = '9001'
        triggeringActor = 'test-actor'
        triggeringActorId = '9001'
        status = 'completed'
        conclusion = $Conclusion
        createdAtUtc = [DateTimeOffset]::UtcNow.AddMinutes(-5).ToString('O')
        updatedAtUtc = [DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('O')
    }) -Path $Path
}

function New-TestTargetMetadata {
    param(
        [string]$Path,
        $StagePolicy
    )

    $subscriptionId = '11111111-2222-3333-4444-555555555555'
    $stableToken = 'dev123abc4567'
    $resourceGroup = [string]$StagePolicy.immutableResourceGroupName
    $sqlServerName = "husaynia-sql-$($StagePolicy.stageCode)-$stableToken"
    $databaseName = "husaynia-$($StagePolicy.stageCode)"
    $webAppName = "husaynia-web-$($StagePolicy.stageCode)-$stableToken"
    $keyVaultName = "hsy-kv-$($StagePolicy.stageCode)-$stableToken"
    $keyVaultResourceId = "/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.KeyVault/vaults/$keyVaultName"
    $secretVersion = '11111111-2222-3333-4444-555555555555'
    $metadata = [ordered]@{
        schemaVersion = '1.0.0'
        source = 'T20_T21_PROTECTED_STAGE_CONFIGURATION'
        stage = 'Development'
        stageCode = [string]$StagePolicy.stageCode
        githubEnvironment = [string]$StagePolicy.githubEnvironment
        dataIsolationKey = [string]$StagePolicy.dataIsolationKey
        serviceConnectionSecretName = [string]$StagePolicy.serviceConnectionSecretName
        immutableResourceGroupName = $resourceGroup
        sqlServerName = $sqlServerName
        sqlServerFqdn = "$sqlServerName.database.windows.net"
        sqlDatabaseName = $databaseName
        sqlServerResourceId = "/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.Sql/servers/$sqlServerName"
        sqlDatabaseResourceId = "/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.Sql/servers/$sqlServerName/databases/$databaseName"
        webAppName = $webAppName
        webAppResourceId = "/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.Web/sites/$webAppName"
        keyVaultName = $keyVaultName
        secretReferences = @(
            [ordered]@{
                settingKey = 'ConnectionStrings__HusayniaDatabase'
                secretName = 'SqlConnectionString'
                provider = 'AzureKeyVault'
                referenceIdentifier = "@Microsoft.KeyVault(SecretUri=https://$keyVaultName.vault.azure.net/secrets/SqlConnectionString/$secretVersion)"
                resourceId = $keyVaultResourceId
                mode = 'reference'
                stage = 'Development'
            },
            [ordered]@{
                settingKey = 'APPLICATIONINSIGHTS_CONNECTION_STRING'
                secretName = 'ApplicationInsightsConnectionString'
                provider = 'AzureKeyVault'
                referenceIdentifier = "@Microsoft.KeyVault(SecretUri=https://$keyVaultName.vault.azure.net/secrets/ApplicationInsightsConnectionString/$secretVersion)"
                resourceId = $keyVaultResourceId
                mode = 'reference'
                stage = 'Development'
            }
        )
        providerModes = [ordered]@{
            payments = 'sandbox'
            messaging = 'capture-only'
            analytics = 'test'
            contentMutation = 'isolated'
        }
        operationHooks = @($StagePolicy.operationHooks)
    }
    Write-Utf8Json -Value $metadata -Path $Path -Depth 30
    return $metadata
}

function Write-InputChecksums {
    param([string]$InputRoot)

    $lines = Get-ChildItem -LiteralPath $InputRoot -File |
        Where-Object { $_.Name -ne 'SHA256SUMS' } |
        Sort-Object Name |
        ForEach-Object { "$(Get-Sha256Lower -Path $_.FullName)  $($_.Name)" }
    [IO.File]::WriteAllLines(
        (Join-Path $InputRoot 'SHA256SUMS'),
        $lines,
        [Text.UTF8Encoding]::new($false))
}

function New-InputBundle {
    param(
        [string]$InputRoot,
        $Target,
        $SourceRelease,
        [string]$AppSha256,
        [string]$ManifestSha256,
        [string]$BundleSha256,
        [string]$ReleaseVersion,
        [string]$CommitSha,
        [DateTimeOffset]$ObservedAt
    )

    New-Item -ItemType Directory -Path $InputRoot -Force | Out-Null
    $baseUri = "https://$($Target.webAppName).azurewebsites.net/"
    Write-Utf8Json -Value ([ordered]@{
        schemaVersion = '1.0.0'
        stage = 'Development'
        endpoint = "${baseUri}health"
        webAppResourceId = $Target.webAppResourceId
        healthy = $true
        statusCode = 200
        sampleCount = 2
        observedAtUtc = $ObservedAt.ToString('O')
    }) -Path (Join-Path $InputRoot 'source-health.json')
    Write-Utf8Json -Value ([ordered]@{
        schemaVersion = '1.0.0'
        stage = 'Development'
        baseUri = $baseUri
        webAppResourceId = $Target.webAppResourceId
        passedTests = 2
        failedTests = 0
        tests = @('home', 'health')
        observedAtUtc = $ObservedAt.ToString('O')
    }) -Path (Join-Path $InputRoot 'source-smoke.json')
    Write-Utf8Json -Value ([ordered]@{
        schemaVersion = '1.0.0'
        stage = 'Development'
        decision = 'READY'
        lastKnownGoodArtifactSha256 = $AppSha256
        releaseManifestSha256 = $ManifestSha256
        releaseVersion = $ReleaseVersion
        databaseRestorePointId = 'test-restore-point'
        rehearsalPassed = $true
        restorePointVerified = $true
        observedAtUtc = $ObservedAt.ToString('O')
    }) -Path (Join-Path $InputRoot 'source-rollback.json')

    $reports = @(
        @{ Type = 'health'; Path = 'source-health.json' },
        @{ Type = 'rollback'; Path = 'source-rollback.json' },
        @{ Type = 'smoke'; Path = 'source-smoke.json' }
    ) | ForEach-Object {
        [ordered]@{
            evidenceType = $_.Type
            path = $_.Path
            sha256 = Get-Sha256Lower -Path (Join-Path $InputRoot $_.Path)
            observedAtUtc = $ObservedAt.ToString('O')
        }
    }
    Write-Utf8Json -Value ([ordered]@{
        schemaVersion = '2.1.0'
        stage = 'Development'
        artifactSha256 = $AppSha256
        releaseManifestSha256 = $ManifestSha256
        releaseVersion = $ReleaseVersion
        releaseCommitSha = $CommitSha
        targetFingerprint = Get-StageTargetFingerprint -TargetMetadata $Target
        sourceRelease = $SourceRelease
        releaseBinding = [ordered]@{
            runId = [string]$SourceRelease.run.runId
            applicationSha256 = $AppSha256
            bundleSha256 = $BundleSha256
            manifestSha256 = $ManifestSha256
            commitSha = $CommitSha
        }
        productionAuthorizationRequest = $null
        observedAtUtc = $ObservedAt.ToString('O')
        reports = @($reports)
    }) -Path (Join-Path $InputRoot 'stage-operation-inputs.json') -Depth 30
    Write-InputChecksums -InputRoot $InputRoot
}

function Invoke-Validator {
    param(
        [string]$InputRoot,
        [string]$ReleaseVerifiedProvenancePath,
        [string]$AppSha256,
        [string]$TargetPath,
        [string]$PolicyPath,
        [string]$ProviderModesJson,
        [string]$HealthEndpoint
    )

    $script:lastValidatorOutput = @(& $pwsh -NoProfile -File (Join-Path $RepositoryRoot 'eng\promotion\Test-StageOperationInputBundle.ps1') `
        -Stage Development `
        -ExpectedAppSha256 $AppSha256 `
        -ReleaseVerifiedProvenancePath $ReleaseVerifiedProvenancePath `
        -StageTargetMetadataPath $TargetPath `
        -InputRoot $InputRoot `
        -DataIsolationKey 'husaynia-development' `
        -ProviderModesJson $ProviderModesJson `
        -HealthEndpoint $HealthEndpoint `
        -PolicyPath $PolicyPath 2>&1)
    return $LASTEXITCODE
}

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    $policyPath = Join-Path $RepositoryRoot 'pipelines\config\promotion-policy.json'
    $policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
    $enabledPolicy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
    @($enabledPolicy.stages | Where-Object { $_.name -eq 'Development' })[0].deploymentEnabled = $true
    $enabledPolicyPath = Join-Path $temporaryRoot 'enabled-development-policy.json'
    Write-Utf8Json -Value $enabledPolicy -Path $enabledPolicyPath -Depth 60
    $enabledPolicy = Get-Content -LiteralPath $enabledPolicyPath -Raw | ConvertFrom-Json
    $stagePolicy = @($enabledPolicy.stages | Where-Object { $_.name -eq 'Development' })[0]
    $commitSha = '0123456789abcdef0123456789abcdef01234567'
    $resolverOutputRoot = Join-Path $temporaryRoot 'resolved-release'
    $artifactRoot = Join-Path $resolverOutputRoot 'artifact\husaynia-site-contract-test'
    New-Item -ItemType Directory -Path (Join-Path $artifactRoot 'app'), (Join-Path $artifactRoot 'release'), (Join-Path $artifactRoot 'operations') -Force | Out-Null
    $appPath = Join-Path $artifactRoot 'app\Husaynia.Web.zip'
    [IO.File]::WriteAllText($appPath, 'immutable-test-application', [Text.UTF8Encoding]::new($false))
    $appSha256 = Get-Sha256Lower -Path $appPath
    $bundlePath = Join-Path $artifactRoot 'operations\protected-execution-bundle.zip'
    [IO.File]::WriteAllText($bundlePath, 'immutable-test-protected-bundle', [Text.UTF8Encoding]::new($false))
    $bundleSha256 = Get-Sha256Lower -Path $bundlePath
    $manifestPath = Join-Path $artifactRoot 'release\release-manifest.json'
    Write-Utf8Json -Value ([ordered]@{
        schemaVersion = '1.0.0'
        version = 'contract-test'
        commitSha = $commitSha
        files = @(
            [ordered]@{
                path = 'app/Husaynia.Web.zip'
                sha256 = $appSha256
                length = (Get-Item -LiteralPath $appPath).Length
            },
            [ordered]@{
                path = 'operations/protected-execution-bundle.zip'
                sha256 = $bundleSha256
                length = (Get-Item -LiteralPath $bundlePath).Length
            }
        )
    }) -Path $manifestPath
    $manifestSha256 = Get-Sha256Lower -Path $manifestPath
    $targetPath = Join-Path $temporaryRoot 'stage-target-metadata.json'
    $target = New-TestTargetMetadata -Path $targetPath -StagePolicy $stagePolicy
    $providerModesJson = $target.providerModes | ConvertTo-Json -Compress
    $healthEndpoint = "https://$($target.webAppName).azurewebsites.net/health"
    $releaseProvenancePath = Join-Path $resolverOutputRoot 'verified-provenance.json'
    $now = [DateTimeOffset]::UtcNow
    $releaseProvenance = [ordered]@{
        schemaVersion = '2.0.0'
        authority = 'github-actions-api-and-sigstore-v1'
        expectedRole = 'release-c6'
        run = [ordered]@{
            repository = 'syedmh/Dreamer'
            workflowPath = '.github/workflows/release-build-and-nonproduction.yml'
            workflowRef = 'syedmh/Dreamer/.github/workflows/release-build-and-nonproduction.yml@refs/heads/main'
            runId = '8101'
            runAttempt = 1
            ref = 'refs/heads/main'
            commitSha = $commitSha
            status = 'completed'
            conclusion = 'success'
        }
        artifact = [ordered]@{
            id = '8201'
            name = 'husaynia-site-contract-test'
            archiveSha256 = 'a' * 64
            sizeBytes = 1
            createdAtUtc = $now.AddMinutes(-2).ToString('O')
            expiresAtUtc = $now.AddDays(1).ToString('O')
        }
        contentManifestSha256 = $manifestSha256
        attestation = $null
    }
    Write-Utf8Json -Value $releaseProvenance -Path $releaseProvenancePath -Depth 20
    $sourceRelease = Get-StageOperationSourceRelease -VerifiedProvenance $releaseProvenance
    $sourceRunPath = Join-Path $temporaryRoot 'stage-operation-input-run.json'
    Write-TestRunMetadata -Path $sourceRunPath `
        -WorkflowPath '.github/workflows/stage-operation-inputs.yml' `
        -CommitSha $commitSha
    $validInputRoot = Join-Path $temporaryRoot 'valid-inputs'
    New-InputBundle -InputRoot $validInputRoot -Target $target -SourceRelease $sourceRelease `
        -AppSha256 $appSha256 -ManifestSha256 $manifestSha256 -BundleSha256 $bundleSha256 `
        -ReleaseVersion 'contract-test' -CommitSha $commitSha `
        -ObservedAt ([DateTimeOffset]::UtcNow.AddMinutes(-2))

    $validatorPath = Join-Path $RepositoryRoot 'eng\promotion\Test-StageOperationInputBundle.ps1'
    Assert-Contract 'stage-operation-input-validator-materialized' (Test-Path -LiteralPath $validatorPath -PathType Leaf) `
        'The trusted stage-operation input bundle validator is missing.'
    if (Test-Path -LiteralPath $validatorPath -PathType Leaf) {
        $validExit = Invoke-Validator -InputRoot $validInputRoot `
            -ReleaseVerifiedProvenancePath $releaseProvenancePath `
            -AppSha256 $appSha256 -TargetPath $targetPath `
            -PolicyPath $enabledPolicyPath -ProviderModesJson $providerModesJson -HealthEndpoint $healthEndpoint
        Assert-Contract 'trusted-stage-operation-input-bundle-accepted' ($validExit -eq 0) `
            "A valid provenance, target, release, checksum, report-set, and freshness-bound bundle was rejected: $($script:lastValidatorOutput -join ' | ')"

        $negativeCases = @(
            @{
                Name = 'missing'
                Mutate = {
                    param($root, $provenancePath)
                    Remove-Item -LiteralPath (Join-Path $root 'source-smoke.json') -Force
                }
            },
            @{
                Name = 'wrong-stage'
                Mutate = {
                    param($root, $provenancePath)
                    $manifest = Get-Content -LiteralPath (Join-Path $root 'stage-operation-inputs.json') -Raw | ConvertFrom-Json
                    $manifest.stage = 'Staging'
                    Write-Utf8Json -Value $manifest -Path (Join-Path $root 'stage-operation-inputs.json') -Depth 30
                    Write-InputChecksums -InputRoot $root
                }
            },
            @{
                Name = 'wrong-target'
                Mutate = {
                    param($root, $provenancePath)
                    $manifest = Get-Content -LiteralPath (Join-Path $root 'stage-operation-inputs.json') -Raw | ConvertFrom-Json
                    $manifest.targetFingerprint = 'f' * 64
                    Write-Utf8Json -Value $manifest -Path (Join-Path $root 'stage-operation-inputs.json') -Depth 30
                    Write-InputChecksums -InputRoot $root
                }
            },
            @{
                Name = 'wrong-source-release'
                Mutate = {
                    param($root, $provenancePath)
                    $manifest = Get-Content -LiteralPath (Join-Path $root 'stage-operation-inputs.json') -Raw | ConvertFrom-Json
                    $manifest.sourceRelease.run.runId = '9999'
                    Write-Utf8Json -Value $manifest -Path (Join-Path $root 'stage-operation-inputs.json') -Depth 30
                    Write-InputChecksums -InputRoot $root
                }
            },
            @{
                Name = 'untrusted-release-provenance'
                Mutate = {
                    param($root, $provenancePath)
                    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
                    $provenance.run.workflowPath = '.github/workflows/untrusted.yml'
                    Write-Utf8Json -Value $provenance -Path $provenancePath
                }
            },
            @{
                Name = 'failed-release-provenance'
                Mutate = {
                    param($root, $provenancePath)
                    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
                    $provenance.run.conclusion = 'failure'
                    Write-Utf8Json -Value $provenance -Path $provenancePath
                }
            },
            @{
                Name = 'v1-provenance'
                Mutate = {
                    param($root, $provenancePath)
                    Write-Utf8Json -Value ([ordered]@{
                        schemaVersion = '1.0.0'
                        runId = '8101'
                    }) -Path $provenancePath
                }
            },
            @{
                Name = 'stale'
                Mutate = {
                    param($root, $provenancePath)
                    $stale = [DateTimeOffset]::UtcNow.AddHours(-25).ToString('O')
                    foreach ($name in @('source-health.json', 'source-smoke.json', 'source-rollback.json')) {
                        $report = Get-Content -LiteralPath (Join-Path $root $name) -Raw | ConvertFrom-Json
                        $report.observedAtUtc = $stale
                        Write-Utf8Json -Value $report -Path (Join-Path $root $name) -Depth 20
                    }
                    $manifest = Get-Content -LiteralPath (Join-Path $root 'stage-operation-inputs.json') -Raw | ConvertFrom-Json
                    $manifest.observedAtUtc = $stale
                    foreach ($report in $manifest.reports) {
                        $report.observedAtUtc = $stale
                        $report.sha256 = Get-Sha256Lower -Path (Join-Path $root $report.path)
                    }
                    Write-Utf8Json -Value $manifest -Path (Join-Path $root 'stage-operation-inputs.json') -Depth 30
                    Write-InputChecksums -InputRoot $root
                }
            },
            @{
                Name = 'tampered-report'
                Mutate = {
                    param($root, $provenancePath)
                    $report = Get-Content -LiteralPath (Join-Path $root 'source-smoke.json') -Raw | ConvertFrom-Json
                    $report.failedTests = 1
                    Write-Utf8Json -Value $report -Path (Join-Path $root 'source-smoke.json')
                }
            }
        )
        foreach ($case in $negativeCases) {
            $caseRoot = Join-Path $temporaryRoot "case-$($case.Name)"
            Copy-Item -LiteralPath $validInputRoot -Destination $caseRoot -Recurse
            $caseResolverOutput = Join-Path $temporaryRoot "case-$($case.Name)-resolved-release"
            Copy-Item -LiteralPath $resolverOutputRoot -Destination $caseResolverOutput -Recurse
            $caseProvenancePath = Join-Path $caseResolverOutput 'verified-provenance.json'
            & $case.Mutate $caseRoot $caseProvenancePath
            $exitCode = Invoke-Validator -InputRoot $caseRoot `
                -ReleaseVerifiedProvenancePath $caseProvenancePath `
                -AppSha256 $appSha256 -TargetPath $targetPath `
                -PolicyPath $enabledPolicyPath -ProviderModesJson $providerModesJson -HealthEndpoint $healthEndpoint
            Assert-Contract "stage-operation-input-$($case.Name)-rejected" ($exitCode -ne 0) `
                "The $($case.Name) stage-operation input bundle was accepted."
        }
    }

    $operationRunPath = Join-Path $temporaryRoot 'operation-run.json'
    Write-TestRunMetadata -Path $operationRunPath `
        -WorkflowPath '.github/workflows/operation-evidence-producer.yml' `
        -CommitSha $commitSha -RunId '8201'
    $nearExpiryRoot = Join-Path $temporaryRoot 'near-expiry-hook'
    New-Item -ItemType Directory -Path $nearExpiryRoot -Force | Out-Null
    $nearExpiry = [DateTimeOffset]::UtcNow.AddHours(-24).AddMinutes(10)
    $health = Get-Content -LiteralPath (Join-Path $validInputRoot 'source-health.json') -Raw | ConvertFrom-Json
    $health.observedAtUtc = $nearExpiry.ToString('O')
    Write-Utf8Json -Value $health -Path (Join-Path $nearExpiryRoot 'source-health.json')
    $nearExpiryOutput = Join-Path $temporaryRoot 'near-expiry-health.json'
    $nearExpiryHookOutput = @(& $pwsh -NoProfile -File (Join-Path $RepositoryRoot 'eng\promotion\Invoke-ProtectedOperationHook.ps1') `
        -EvidenceType health -Stage Development -ArtifactRoot $artifactRoot `
        -StageTargetMetadataPath $targetPath -SourceRunMetadataPath $operationRunPath `
        -OutputPath $nearExpiryOutput -LocalDryRunFixtureRoot $nearExpiryRoot `
        -PolicyPath $enabledPolicyPath 2>&1)
    $nearExpiryExitCode = $LASTEXITCODE
    $normalizedObserved = if ($nearExpiryExitCode -eq 0 -and (Test-Path -LiteralPath $nearExpiryOutput)) {
        $normalizedEvidence = Get-Content -LiteralPath $nearExpiryOutput -Raw | ConvertFrom-Json
        [DateTimeOffset]$normalizedEvidence.observedAtUtc
    }
    else {
        [DateTimeOffset]::MinValue
    }
    Assert-Contract 'normalization-preserves-near-expiry-observation' (
        $nearExpiryExitCode -eq 0 -and [DateTimeOffset]::Compare($normalizedObserved, $nearExpiry) -eq 0
    ) "Normalization replaced the immutable source observation timestamp and extended evidence freshness: exit=$nearExpiryExitCode compare=$([DateTimeOffset]::Compare($normalizedObserved, $nearExpiry)) source=$nearExpiry normalized=$normalizedObserved output=$($nearExpiryHookOutput -join ' | ')"

    $staleRoot = Join-Path $temporaryRoot 'stale-hook'
    New-Item -ItemType Directory -Path $staleRoot -Force | Out-Null
    $health.observedAtUtc = [DateTimeOffset]::UtcNow.AddHours(-24).AddMinutes(-1).ToString('O')
    Write-Utf8Json -Value $health -Path (Join-Path $staleRoot 'source-health.json')
    $staleOutput = Join-Path $temporaryRoot 'stale-health.json'
    $null = @(& $pwsh -NoProfile -File (Join-Path $RepositoryRoot 'eng\promotion\Invoke-ProtectedOperationHook.ps1') `
        -EvidenceType health -Stage Development -ArtifactRoot $artifactRoot `
        -StageTargetMetadataPath $targetPath -SourceRunMetadataPath $operationRunPath `
        -OutputPath $staleOutput -LocalDryRunFixtureRoot $staleRoot `
        -PolicyPath $enabledPolicyPath 2>&1)
    Assert-Contract 'stale-source-observation-rejected-before-normalization' (
        $LASTEXITCODE -ne 0 -and -not (Test-Path -LiteralPath $staleOutput)
    ) 'A stale source report was accepted or normalized into fresh evidence.'

    Write-Output "SUMMARY total=$($passed + $failed) passed=$passed failed=$failed"
    if ($failed -gt 0) {
        throw "Stage-operation input contract validation failed with $failed assertion(s)."
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
