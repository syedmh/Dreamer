[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Development', 'Staging', 'Production')]
    [string]$Stage,
    [Parameter(Mandatory = $true)]
    [ValidateSet(
        'backup',
        'change-record',
        'health',
        'smoke',
        'crawl',
        'restore',
        'performance',
        'accessibility',
        'visual',
        'sandbox-integrations',
        'rollback')]
    [string]$EvidenceType,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ExpectedAppSha256,
    [Parameter(Mandatory = $true)]
    [string]$ReleaseVerifiedProvenancePath,
    [Parameter(Mandatory = $true)]
    [string]$StageTargetMetadataPath,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$HealthEndpoint,
    [string]$ReadOnlyProbeHost,
    [string]$BackupRecordEndpoint,
    [string]$ChangeRecordEndpoint,
    [string]$ApprovalReference,
    [string]$RestoreValidationEndpoint,
    [string]$PerformanceEndpoint,
    [int]$PerformanceSampleCount,
    [int]$PerformanceMaxP95Milliseconds,
    [string]$RollbackReferenceEndpoint,
    [string]$SandboxEndpointsJson,
    [string]$AccessibilityProjectPath,
    [string]$VisualProjectPath,
    [string]$VisualBaselineManifestPath,
    [string]$BackupReportPath,
    [string]$TokenEnvironmentVariable = 'T21_STAGE_READONLY_PROBE_TOKEN',
    [string]$LocalFixtureRoot,
    [string]$ProductionAuthorizationContextPath,
    [switch]$CtoAuthorizationOwner,
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
    -ProductionAuthorizationContextPath $ProductionAuthorizationContextPath `
    -PolicyPath $PolicyPath
$target = $context.Target
$httpPolicy = Get-StageOperationHttpPolicy -Policy $context.Policy
$maximumAgeHours = [int]$context.StagePolicy.maxEvidenceAgeHours
$webHost = "$($target.webAppName).azurewebsites.net"
$baseUri = "https://$webHost/"

function Assert-RequiredValue {
    param([string]$Value, [string]$Label)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Label is required to generate $EvidenceType evidence."
    }
}

function Assert-ReadOnlyTestProject {
    param([string]$ProjectPath, [string]$Label)

    Assert-RequiredValue -Value $ProjectPath -Label $Label
    if (-not (Test-Path -LiteralPath $ProjectPath -PathType Leaf) -or
        [IO.Path]::GetExtension($ProjectPath) -cne '.csproj') {
        throw "$Label is missing."
    }
    $lockPath = Join-Path (Split-Path -Parent $ProjectPath) 'packages.lock.json'
    if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
        throw "$Label locked dependency file is missing."
    }
    $projectText = Get-Content -LiteralPath $ProjectPath -Raw
    if ($projectText -match '(?i)<ProjectReference\b') {
        throw "$Label may not reference or rebuild release projects."
    }
}

function Read-ExecutedToolResult {
    param(
        [string]$ProjectPath,
        [string]$FixtureFile,
        [string]$Label
    )

    Assert-ReadOnlyTestProject -ProjectPath $ProjectPath -Label $Label
    if (-not [string]::IsNullOrWhiteSpace($LocalFixtureRoot)) {
        return Read-StageOperationFixture -FixtureRoot $LocalFixtureRoot -FileName $FixtureFile
    }

    $resultRoot = Join-Path ([IO.Path]::GetTempPath()) "husaynia-stage-tool-$([guid]::NewGuid().ToString('N'))"
    $resultPath = Join-Path $resultRoot $FixtureFile
    New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
    $previousBaseUri = [Environment]::GetEnvironmentVariable('T21_STAGE_BASE_URI')
    $previousOutput = [Environment]::GetEnvironmentVariable('T21_STAGE_REPORT_OUTPUT_PATH')
    $sensitiveEnvironment = @(
        'T21_STAGE_READONLY_PROBE_TOKEN',
        'ACTIONS_ID_TOKEN_REQUEST_TOKEN',
        'ACTIONS_ID_TOKEN_REQUEST_URL',
        'HUSAYNIA_MIGRATION_CONNECTION'
    )
    $previousSensitiveEnvironment = @{}
    try {
        foreach ($name in $sensitiveEnvironment) {
            $previousSensitiveEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
            [Environment]::SetEnvironmentVariable($name, $null)
        }
        [Environment]::SetEnvironmentVariable('T21_STAGE_BASE_URI', $baseUri)
        [Environment]::SetEnvironmentVariable('T21_STAGE_REPORT_OUTPUT_PATH', $resultPath)
        Invoke-CheckedNative -FilePath 'dotnet' -Arguments @(
            'restore', $ProjectPath,
            '--locked-mode',
            '--nologo',
            '-p:NuGetAudit=true',
            '-p:NuGetAuditMode=all',
            '-warnaserror'
        ) -Label "restore and audit $Label"
        Invoke-CheckedNative -FilePath 'dotnet' -Arguments @(
            'test', $ProjectPath,
            '--configuration', 'Release',
            '--no-restore',
            '--nologo'
        ) -Label "execute $Label"
        if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
            throw "$Label did not emit its required JSON result."
        }
        return Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json -DateKind String
    }
    finally {
        foreach ($name in $sensitiveEnvironment) {
            [Environment]::SetEnvironmentVariable($name, $previousSensitiveEnvironment[$name])
        }
        [Environment]::SetEnvironmentVariable('T21_STAGE_BASE_URI', $previousBaseUri)
        [Environment]::SetEnvironmentVariable('T21_STAGE_REPORT_OUTPUT_PATH', $previousOutput)
        if (Test-Path -LiteralPath $resultRoot) {
            Remove-Item -LiteralPath $resultRoot -Recurse -Force
        }
    }
}

$report = switch ($EvidenceType) {
    'backup' {
        Assert-RequiredValue -Value $ReadOnlyProbeHost -Label 'Read-only probe host'
        Assert-RequiredValue -Value $BackupRecordEndpoint -Label 'Backup record endpoint'
        $record = Invoke-StageOperationServiceGet `
            -Uri $BackupRecordEndpoint `
            -ExpectedHost $ReadOnlyProbeHost `
            -TokenEnvironmentVariable $TokenEnvironmentVariable `
            -FixtureRoot $LocalFixtureRoot `
            -FixtureFile 'backup-record.json' `
            -ApprovedPrivateEndpoints @($context.StagePolicy.approvedPrivateServiceProbeEndpoints) `
            -HttpPolicy $httpPolicy
        $observedAt = ConvertFrom-StageOperationInputUtc `
            -Value ([string]$record.observedAtUtc) `
            -Label 'Backup record observation' `
            -MaximumAgeHours $maximumAgeHours
        $retentionUntil = [DateTimeOffset]::MinValue
        if ([string]$record.schemaVersion -ne '1.0.0' -or
            [string]$record.provider -cne 'AzureSql' -or
            [string]$record.stage -cne $Stage -or
            [string]$record.sqlServerResourceId -cne [string]$target.sqlServerResourceId -or
            [string]$record.sqlDatabaseResourceId -cne [string]$target.sqlDatabaseResourceId -or
            [string]$record.observedDatabaseName -cne [string]$target.sqlDatabaseName -or
            [string]::IsNullOrWhiteSpace([string]$record.backupId) -or
            $record.restorable -ne $true -or
            $record.integrityVerified -ne $true -or
            -not [DateTimeOffset]::TryParse([string]$record.retentionUntilUtc, [ref]$retentionUntil) -or
            $retentionUntil -le [DateTimeOffset]::UtcNow) {
            throw 'Existing backup record does not prove a restorable backup for the exact stage database.'
        }
        [ordered]@{
            schemaVersion = '1.0.0'
            provider = 'AzureSql'
            stage = $Stage
            sqlServerResourceId = [string]$target.sqlServerResourceId
            sqlDatabaseResourceId = [string]$target.sqlDatabaseResourceId
            observedDatabaseName = [string]$target.sqlDatabaseName
            backupId = [string]$record.backupId
            restorable = $true
            integrityVerified = $true
            retentionUntilUtc = $retentionUntil.ToUniversalTime().ToString('O')
            observedAtUtc = $observedAt.ToString('O')
        }
    }
    'change-record' {
        if ($Stage -ne 'Production' -or
            (-not $CtoAuthorizationOwner -and $null -eq $context.ProductionAuthorizationContext)) {
            throw 'Change-record evidence requires CTO authorization ownership or validated Production context.'
        }
        Assert-RequiredValue -Value $ReadOnlyProbeHost -Label 'Read-only probe host'
        Assert-RequiredValue -Value $ChangeRecordEndpoint -Label 'Change record endpoint'
        Assert-RequiredValue -Value $ApprovalReference -Label 'Approval reference'
        if (-not $CtoAuthorizationOwner -and
            $ApprovalReference -cne [string]$context.ProductionAuthorizationContext.approvalReference) {
            throw 'Change-record approval reference does not match the validated Production authorization context.'
        }
        $record = Invoke-StageOperationServiceGet `
            -Uri $ChangeRecordEndpoint `
            -ExpectedHost $ReadOnlyProbeHost `
            -TokenEnvironmentVariable $TokenEnvironmentVariable `
            -FixtureRoot $LocalFixtureRoot `
            -FixtureFile 'change-record.json' `
            -ApprovedPrivateEndpoints @($context.StagePolicy.approvedPrivateServiceProbeEndpoints) `
            -HttpPolicy $httpPolicy
        $observedAt = ConvertFrom-StageOperationInputUtc `
            -Value ([string]$record.observedAtUtc) `
            -Label 'Change record observation' `
            -MaximumAgeHours $maximumAgeHours
        $expiry = [DateTimeOffset]::MinValue
        if ([string]$record.schemaVersion -ne '1.0.0' -or
            [string]$record.stage -cne $Stage -or
            [string]$record.changeId -cne $ApprovalReference -or
            [string]$record.decision -cne 'APPROVED' -or
            [string]::IsNullOrWhiteSpace([string]$record.owner) -or
            [string]$record.artifactSha256 -cne $context.AppSha256 -or
            [string]$record.releaseManifestSha256 -cne $context.ManifestSha256 -or
            -not [DateTimeOffset]::TryParse([string]$record.expiresAtUtc, [ref]$expiry) -or
            $expiry -le [DateTimeOffset]::UtcNow) {
            throw 'Change record is not approved, fresh, and bound to the selected stage and immutable release.'
        }
        [ordered]@{
            schemaVersion = '1.0.0'
            stage = $Stage
            changeId = $ApprovalReference
            decision = 'APPROVED'
            owner = [string]$record.owner
            artifactSha256 = $context.AppSha256
            releaseManifestSha256 = $context.ManifestSha256
            expiresAtUtc = $expiry.ToUniversalTime().ToString('O')
            observedAtUtc = $observedAt.ToString('O')
        }
    }
    'health' {
        Assert-RequiredValue -Value $HealthEndpoint -Label 'Health endpoint'
        $null = Assert-StageOperationInputUri `
            -Value $HealthEndpoint `
            -ExpectedHost $webHost `
            -RequiredPath '/health'
        $samples = @(
            Invoke-StageOperationWebGet -Uri $HealthEndpoint -ExpectedHost $webHost `
                -FixtureRoot $LocalFixtureRoot `
                -ApprovedPrivateEndpoints @($context.StagePolicy.approvedPrivateServiceProbeEndpoints) `
                -HttpPolicy $httpPolicy
            Invoke-StageOperationWebGet -Uri $HealthEndpoint -ExpectedHost $webHost `
                -FixtureRoot $LocalFixtureRoot `
                -ApprovedPrivateEndpoints @($context.StagePolicy.approvedPrivateServiceProbeEndpoints) `
                -HttpPolicy $httpPolicy
        )
        if (@($samples | Where-Object {
            $_.StatusCode -ne 200 -or $_.Body -notmatch '(?i)\b(healthy|ok)\b'
        }).Count -ne 0) {
            throw 'Health probes did not return two healthy HTTP 200 responses.'
        }
        [ordered]@{
            schemaVersion = '1.0.0'
            stage = $Stage
            endpoint = $HealthEndpoint
            webAppResourceId = [string]$target.webAppResourceId
            healthy = $true
            statusCode = 200
            sampleCount = $samples.Count
            observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        }
    }
    'smoke' {
        Assert-RequiredValue -Value $HealthEndpoint -Label 'Health endpoint'
        $tests = @(
            [ordered]@{ name = 'home'; url = $baseUri },
            [ordered]@{ name = 'health'; url = $HealthEndpoint }
        )
        foreach ($test in $tests) {
            $result = Invoke-StageOperationWebGet `
                -Uri ([string]$test.url) `
                -ExpectedHost $webHost `
                -FixtureRoot $LocalFixtureRoot `
                -ApprovedPrivateEndpoints @($context.StagePolicy.approvedPrivateServiceProbeEndpoints) `
                -HttpPolicy $httpPolicy
            if ($result.StatusCode -ne 200) {
                throw "Smoke probe failed: $($test.name)."
            }
        }
        [ordered]@{
            schemaVersion = '1.0.0'
            stage = $Stage
            baseUri = $baseUri
            webAppResourceId = [string]$target.webAppResourceId
            passedTests = $tests.Count
            failedTests = 0
            tests = @($tests.name)
            observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        }
    }
    'crawl' {
        $routeManifestPath = Join-Path $context.ArtifactRoot 'contracts\route-manifest.json'
        if (-not (Test-Path -LiteralPath $routeManifestPath -PathType Leaf)) {
            throw 'Immutable release route manifest is missing.'
        }
        $routeManifest = Get-Content -LiteralPath $routeManifestPath -Raw | ConvertFrom-Json
        $routes = @($routeManifest.routes)
        if ($routes.Count -eq 0) {
            throw 'Immutable release route manifest contains no routes.'
        }
        $origin = [Uri]$baseUri
        $terminalRoutes = @{}
        foreach ($candidate in $routes) {
            $candidateCanonicalPath = [string](Get-StageOperationInputProperty $candidate 'canonicalPath')
            $null = Assert-StageOperationRoutePath -Value $candidateCanonicalPath -Label 'Canonical route path'
            $candidateStatus = Get-StageOperationInputProperty $candidate 'expectedStatus'
            if (-not (Test-StageOperationInputInteger -Value $candidateStatus -Minimum 100 -Maximum 599)) {
                throw "Route manifest contains an invalid expected status: $candidateCanonicalPath"
            }
            if ([int]$candidateStatus -notin @(301, 308)) {
                if ($terminalRoutes.ContainsKey($candidateCanonicalPath)) {
                    throw "Route manifest contains duplicate terminal canonical paths: $candidateCanonicalPath"
                }
                $terminalRoutes[$candidateCanonicalPath] = $candidate
            }
        }
        $urlsChecked = 0
        foreach ($route in $routes) {
            $legacyPath = [string](Get-StageOperationInputProperty $route 'legacyPath')
            $canonicalPath = [string](Get-StageOperationInputProperty $route 'canonicalPath')
            $null = Assert-StageOperationRoutePath -Value $legacyPath -Label 'Legacy route path'
            $null = Assert-StageOperationRoutePath -Value $canonicalPath -Label 'Canonical route path'
            $expectedStatusValue = Get-StageOperationInputProperty $route 'expectedStatus'
            if (-not (Test-StageOperationInputInteger -Value $expectedStatusValue -Minimum 100 -Maximum 599)) {
                throw "Route manifest contains an invalid expected status: $legacyPath"
            }
            $expectedStatus = [int]$expectedStatusValue
            if ($expectedStatus -in @(301, 308)) {
                $redirectTarget = [string](Get-StageOperationInputProperty $route 'redirectTarget')
                $null = Assert-StageOperationRoutePath -Value $redirectTarget -Label 'Redirect target'
                if (-not $terminalRoutes.ContainsKey($redirectTarget)) {
                    throw "Redirect target does not resolve to one terminal manifest route: $redirectTarget"
                }
                $legacyUri = New-StageOperationRouteUri -Origin $origin -Path $legacyPath -Label 'Legacy route path'
                $redirectResult = Invoke-StageOperationWebGet -Uri $legacyUri.AbsoluteUri `
                    -ExpectedHost $webHost -ExpectedPort $origin.Port -FixtureRoot $LocalFixtureRoot `
                    -ApprovedPrivateEndpoints @($context.StagePolicy.approvedPrivateServiceProbeEndpoints) `
                    -HttpPolicy $httpPolicy
                $urlsChecked++
                if ($redirectResult.StatusCode -ne $expectedStatus) {
                    throw "Legacy redirect returned $($redirectResult.StatusCode), expected $expectedStatus`: $legacyPath"
                }
                $null = Resolve-StageOperationRedirectLocation -RequestUri $legacyUri `
                    -Location ([string]$redirectResult.Location) -ApprovedOrigin $origin -ExpectedPath $redirectTarget

                $targetRoute = $terminalRoutes[$redirectTarget]
                $targetExpectedStatus = [int](Get-StageOperationInputProperty $targetRoute 'expectedStatus')
                $targetUri = New-StageOperationRouteUri -Origin $origin -Path $redirectTarget -Label 'Canonical redirect target'
                $targetResult = Invoke-StageOperationWebGet -Uri $targetUri.AbsoluteUri `
                    -ExpectedHost $webHost -ExpectedPort $origin.Port -FixtureRoot $LocalFixtureRoot `
                    -ApprovedPrivateEndpoints @($context.StagePolicy.approvedPrivateServiceProbeEndpoints) `
                    -HttpPolicy $httpPolicy
                $urlsChecked++
                if ($targetResult.StatusCode -ne $targetExpectedStatus -or
                    -not [string]::IsNullOrWhiteSpace([string]$targetResult.Location) -or
                    [Uri]$targetResult.FinalUri -ne $targetUri) {
                    throw "Redirect target did not return its exact terminal manifest response: $redirectTarget"
                }
                continue
            }

            $canonicalUri = New-StageOperationRouteUri -Origin $origin -Path $canonicalPath -Label 'Canonical route path'
            $result = Invoke-StageOperationWebGet -Uri $canonicalUri.AbsoluteUri `
                -ExpectedHost $webHost -ExpectedPort $origin.Port -FixtureRoot $LocalFixtureRoot `
                -ApprovedPrivateEndpoints @($context.StagePolicy.approvedPrivateServiceProbeEndpoints) `
                -HttpPolicy $httpPolicy
            $urlsChecked++
            if ($result.StatusCode -ne $expectedStatus -or
                -not [string]::IsNullOrWhiteSpace([string]$result.Location) -or
                [Uri]$result.FinalUri -ne $canonicalUri) {
                throw "Canonical route did not return its exact terminal manifest response: $canonicalPath"
            }
        }
        [ordered]@{
            schemaVersion = '1.0.0'
            stage = $Stage
            baseUri = $baseUri
            webAppResourceId = [string]$target.webAppResourceId
            urlsChecked = $urlsChecked
            brokenLinks = 0
            redirectMismatches = 0
            routeManifestSha256 = Get-Sha256Lower -Path $routeManifestPath
            observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        }
    }
    'restore' {
        Assert-RequiredValue -Value $ReadOnlyProbeHost -Label 'Read-only probe host'
        Assert-RequiredValue -Value $RestoreValidationEndpoint -Label 'Restore validation endpoint'
        Assert-RequiredValue -Value $BackupReportPath -Label 'Verified backup source report'
        if (-not (Test-Path -LiteralPath $BackupReportPath -PathType Leaf)) {
            throw 'Verified backup source report is missing.'
        }
        $backup = Get-Content -LiteralPath $BackupReportPath -Raw | ConvertFrom-Json
        if ([string]$backup.schemaVersion -ne '1.0.0' -or
            [string]$backup.stage -cne $Stage -or
            [string]::IsNullOrWhiteSpace([string]$backup.backupId)) {
            throw 'Restore validation requires the exact generated backup source report.'
        }
        $record = Invoke-StageOperationServiceGet `
            -Uri $RestoreValidationEndpoint `
            -ExpectedHost $ReadOnlyProbeHost `
            -TokenEnvironmentVariable $TokenEnvironmentVariable `
            -FixtureRoot $LocalFixtureRoot `
            -FixtureFile 'restore-validation.json' `
            -ApprovedPrivateEndpoints @($context.StagePolicy.approvedPrivateServiceProbeEndpoints) `
            -HttpPolicy $httpPolicy
        $observedAt = ConvertFrom-StageOperationInputUtc `
            -Value ([string]$record.observedAtUtc) `
            -Label 'Isolated restore validation observation' `
            -MaximumAgeHours $maximumAgeHours
        if ([string]$record.schemaVersion -ne '1.0.0' -or
            [string]$record.operation -cne 'isolated-restore-validation' -or
            [string]$record.stage -cne $Stage -or
            [string]$record.backupId -cne [string]$backup.backupId -or
            [string]$record.sourceSqlDatabaseResourceId -cne [string]$target.sqlDatabaseResourceId -or
            [string]$record.sourceTargetFingerprint -cne $context.TargetFingerprint -or
            [string]$record.artifactSha256 -cne $context.AppSha256 -or
            [string]$record.releaseManifestSha256 -cne $context.ManifestSha256 -or
            [string]::IsNullOrWhiteSpace([string]$record.isolatedRestoreTargetResourceId) -or
            [string]$record.isolatedRestoreTargetResourceId -ceq [string]$target.sqlDatabaseResourceId -or
            $record.restoreTestPassed -ne $true -or
            $record.dataIntegrityVerified -ne $true -or
            $record.sourceDatabaseUnchanged -ne $true -or
            $record.cleanupCompleted -ne $true) {
            throw 'Restore evidence does not prove a protected isolated restore validation bound to the backup, target, and release.'
        }
        [ordered]@{
            schemaVersion = '1.0.0'
            stage = $Stage
            operation = 'isolated-restore-validation'
            backupId = [string]$backup.backupId
            backupSourceReportSha256 = Get-Sha256Lower -Path $BackupReportPath
            sourceSqlDatabaseResourceId = [string]$target.sqlDatabaseResourceId
            sourceTargetFingerprint = $context.TargetFingerprint
            artifactSha256 = $context.AppSha256
            releaseManifestSha256 = $context.ManifestSha256
            isolatedRestoreTargetResourceId = [string]$record.isolatedRestoreTargetResourceId
            restoreTestPassed = $true
            dataIntegrityVerified = $true
            sourceDatabaseUnchanged = $true
            cleanupCompleted = $true
            observedAtUtc = $observedAt.ToString('O')
        }
    }
    'performance' {
        Assert-RequiredValue -Value $PerformanceEndpoint -Label 'Performance endpoint'
        if ($PerformanceSampleCount -lt 1 -or $PerformanceSampleCount -gt 1000 -or
            $PerformanceMaxP95Milliseconds -lt 1 -or $PerformanceMaxP95Milliseconds -gt 600000) {
            throw 'Performance sample count and p95 threshold are required and outside approved bounds.'
        }
        $performanceUri = Assert-StageOperationInputUri -Value $PerformanceEndpoint `
            -ExpectedHost $webHost -ExpectedPort ([Uri]$baseUri).Port -AllowPath
        $durations = [Collections.Generic.List[int64]]::new()
        $failedSamples = 0
        for ($index = 0; $index -lt $PerformanceSampleCount; $index++) {
            $sample = Invoke-StageOperationWebGet -Uri $performanceUri.AbsoluteUri `
                -ExpectedHost $webHost -ExpectedPort $performanceUri.Port -FixtureRoot $LocalFixtureRoot `
                -ApprovedPrivateEndpoints @($context.StagePolicy.approvedPrivateServiceProbeEndpoints) `
                -HttpPolicy $httpPolicy
            if ($sample.StatusCode -ne 200 -or
                -not [string]::IsNullOrWhiteSpace([string]$sample.Location)) {
                $failedSamples++
                continue
            }
            $durations.Add([int64]$sample.DurationMilliseconds)
        }
        if ($failedSamples -ne 0 -or $durations.Count -ne $PerformanceSampleCount) {
            throw 'Performance measurement contained failed or redirected HTTP samples.'
        }
        $orderedDurations = @($durations | Sort-Object)
        $p95Index = [Math]::Max(0, [Math]::Ceiling($orderedDurations.Count * 0.95) - 1)
        $observedP95 = [int64]$orderedDurations[$p95Index]
        if ($observedP95 -gt $PerformanceMaxP95Milliseconds) {
            throw "Performance p95 threshold exceeded: observed=$observedP95 threshold=$PerformanceMaxP95Milliseconds."
        }
        [ordered]@{
            schemaVersion = '1.0.0'
            stage = $Stage
            baseUri = $baseUri
            endpoint = $performanceUri.AbsoluteUri
            webAppResourceId = [string]$target.webAppResourceId
            sampleCount = $PerformanceSampleCount
            successfulSamples = $durations.Count
            failedSamples = 0
            observedP95Milliseconds = $observedP95
            maxP95Milliseconds = $PerformanceMaxP95Milliseconds
            thresholdsMet = $true
            readOnly = $true
            observedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        }
    }
    'accessibility' {
        $result = Read-ExecutedToolResult `
            -ProjectPath $AccessibilityProjectPath `
            -FixtureFile 'accessibility-result.json' `
            -Label 'required accessibility project'
        $observedAt = ConvertFrom-StageOperationInputUtc `
            -Value ([string]$result.observedAtUtc) `
            -Label 'Accessibility execution' `
            -MaximumAgeHours $maximumAgeHours
        if ([string]$result.schemaVersion -ne '1.0.0' -or
            [string]$result.stage -cne $Stage -or
            [string]$result.baseUri -cne $baseUri -or
            [string]::IsNullOrWhiteSpace([string]$result.tool) -or
            [string]::IsNullOrWhiteSpace([string]$result.toolVersion) -or
            -not (Test-StageOperationInputInteger -Value $result.testedPages -Minimum 1) -or
            -not (Test-StageOperationInputInteger -Value $result.criticalViolations -Minimum 0 -Maximum 0) -or
            -not (Test-StageOperationInputInteger -Value $result.seriousViolations -Minimum 0 -Maximum 0)) {
            throw 'Executed accessibility project did not emit a passing stage-bound result.'
        }
        [ordered]@{
            schemaVersion = '1.0.0'
            stage = $Stage
            baseUri = $baseUri
            webAppResourceId = [string]$target.webAppResourceId
            tool = [string]$result.tool
            toolVersion = [string]$result.toolVersion
            testedPages = [int]$result.testedPages
            criticalViolations = 0
            seriousViolations = 0
            observedAtUtc = $observedAt.ToString('O')
        }
    }
    'visual' {
        Assert-RequiredValue -Value $VisualBaselineManifestPath -Label 'Visual baseline manifest'
        if (-not (Test-Path -LiteralPath $VisualBaselineManifestPath -PathType Leaf)) {
            throw 'Required visual baseline manifest is missing.'
        }
        $baselineSha256 = Get-Sha256Lower -Path $VisualBaselineManifestPath
        $result = Read-ExecutedToolResult `
            -ProjectPath $VisualProjectPath `
            -FixtureFile 'visual-result.json' `
            -Label 'required visual comparison project'
        $observedAt = ConvertFrom-StageOperationInputUtc `
            -Value ([string]$result.observedAtUtc) `
            -Label 'Visual comparison execution' `
            -MaximumAgeHours $maximumAgeHours
        if ([string]$result.schemaVersion -ne '1.0.0' -or
            [string]$result.stage -cne $Stage -or
            [string]$result.baseUri -cne $baseUri -or
            [string]$result.baselineArtifactSha256 -cne $baselineSha256 -or
            -not (Test-StageOperationInputInteger -Value $result.comparedPages -Minimum 1) -or
            -not (Test-StageOperationInputInteger -Value $result.failedComparisons -Minimum 0 -Maximum 0) -or
            $result.thresholdApproved -ne $true) {
            throw 'Executed visual project did not validate the required immutable baseline.'
        }
        [ordered]@{
            schemaVersion = '1.0.0'
            stage = $Stage
            baseUri = $baseUri
            webAppResourceId = [string]$target.webAppResourceId
            baselineArtifactSha256 = $baselineSha256
            comparedPages = [int]$result.comparedPages
            failedComparisons = 0
            thresholdApproved = $true
            observedAtUtc = $observedAt.ToString('O')
        }
    }
    'sandbox-integrations' {
        Assert-RequiredValue -Value $ReadOnlyProbeHost -Label 'Read-only probe host'
        Assert-RequiredValue -Value $SandboxEndpointsJson -Label 'Sandbox endpoints JSON'
        if ([string]$target.providerModes.payments -notin @('disabled', 'sandbox') -or
            [string]$target.providerModes.messaging -notin @('disabled', 'capture-only')) {
            throw 'Stage target permits a live payment or messaging provider.'
        }
        $endpoints = @($SandboxEndpointsJson | ConvertFrom-Json)
        if ($endpoints.Count -eq 0) {
            throw 'At least one protected sandbox endpoint is required.'
        }
        $providers = [Collections.Generic.List[string]]::new()
        $seenProviders = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $observedAt = [DateTimeOffset]::MinValue
        foreach ($endpoint in $endpoints) {
            $provider = [string]$endpoint.provider
            if ($provider -notmatch '^[a-z][a-z0-9-]{0,31}$' -or -not $seenProviders.Add($provider)) {
                throw 'Sandbox endpoint provider is unsafe or duplicated.'
            }
            $providers.Add($provider)
            $probe = Invoke-StageOperationServiceGet `
                -Uri ([string]$endpoint.endpoint) `
                -ExpectedHost $ReadOnlyProbeHost `
                -TokenEnvironmentVariable $TokenEnvironmentVariable `
                -FixtureRoot $LocalFixtureRoot `
                -FixtureFile "sandbox-$provider.json" `
                -ApprovedPrivateEndpoints @($context.StagePolicy.approvedPrivateServiceProbeEndpoints) `
                -HttpPolicy $httpPolicy
            $probeObserved = ConvertFrom-StageOperationInputUtc `
                -Value ([string]$probe.observedAtUtc) `
                -Label "Sandbox $provider observation" `
                -MaximumAgeHours $maximumAgeHours
            if ([string]$probe.schemaVersion -ne '1.0.0' -or
                [string]$probe.stage -cne $Stage -or
                [string]$probe.provider -cne $provider -or
                [string]$probe.mode -notin @('sandbox', 'capture-only', 'test', 'disabled') -or
                $probe.readOnly -ne $true -or
                $probe.liveCredentialsUsed -ne $false -or
                $probe.passed -ne $true) {
                throw "Sandbox $provider probe failed or used a live/mutating integration."
            }
            if ($probeObserved -gt $observedAt) {
                $observedAt = $probeObserved
            }
        }
        [ordered]@{
            schemaVersion = '1.0.0'
            stage = $Stage
            providerMode = 'sandbox'
            liveCredentialsUsed = $false
            passedChecks = $providers.Count
            failedChecks = 0
            providers = @($providers)
            observedAtUtc = $observedAt.ToString('O')
        }
    }
    'rollback' {
        Assert-RequiredValue -Value $ReadOnlyProbeHost -Label 'Read-only probe host'
        Assert-RequiredValue -Value $RollbackReferenceEndpoint -Label 'Rollback reference endpoint'
        $reference = Invoke-StageOperationServiceGet `
            -Uri $RollbackReferenceEndpoint `
            -ExpectedHost $ReadOnlyProbeHost `
            -TokenEnvironmentVariable $TokenEnvironmentVariable `
            -FixtureRoot $LocalFixtureRoot `
            -FixtureFile 'rollback-reference.json' `
            -ApprovedPrivateEndpoints @($context.StagePolicy.approvedPrivateServiceProbeEndpoints) `
            -HttpPolicy $httpPolicy
        $observedAt = ConvertFrom-StageOperationInputUtc `
            -Value ([string]$reference.observedAtUtc) `
            -Label 'Rollback reference observation' `
            -MaximumAgeHours $maximumAgeHours
        if ([string]$reference.schemaVersion -ne '1.0.0' -or
            [string]$reference.stage -cne $Stage -or
            [string]$reference.decision -notin @('READY', 'NOT_REQUIRED') -or
            -not (Test-Sha256 -Value ([string]$reference.lastKnownGoodArtifactSha256)) -or
            -not (Test-Sha256 -Value ([string]$reference.releaseManifestSha256)) -or
            -not (Test-SafeVersion -Version ([string]$reference.releaseVersion)) -or
            [string]$reference.lastKnownGoodArtifactSha256 -ceq $context.AppSha256 -or
            [string]$reference.releaseVersion -ceq [string]$context.Manifest.version -or
            [string]::IsNullOrWhiteSpace([string]$reference.databaseRestorePointId) -or
            $reference.rehearsalPassed -ne $true -or
            $reference.restorePointVerified -ne $true) {
            throw 'Rollback reference is not bound to an immutable prior release and verified restore point.'
        }
        $backupRequired = @($context.StagePolicy.requiredEvidence) -contains 'backup.json'
        if ($backupRequired -and (
                [string]::IsNullOrWhiteSpace($BackupReportPath) -or
                -not (Test-Path -LiteralPath $BackupReportPath -PathType Leaf))) {
            throw 'Rollback evidence requires the generated backup source report for this stage.'
        }
        if (-not [string]::IsNullOrWhiteSpace($BackupReportPath)) {
            $backup = Get-Content -LiteralPath $BackupReportPath -Raw | ConvertFrom-Json
            if ([string]$backup.schemaVersion -ne '1.0.0' -or
                [string]$backup.stage -cne $Stage -or
                [string]::IsNullOrWhiteSpace([string]$backup.backupId) -or
                [string]$backup.backupId -cne [string]$reference.databaseRestorePointId) {
                throw 'Rollback restore point does not match the verified existing backup record.'
            }
        }
        [ordered]@{
            schemaVersion = '1.0.0'
            stage = $Stage
            decision = [string]$reference.decision
            lastKnownGoodArtifactSha256 = ([string]$reference.lastKnownGoodArtifactSha256).ToLowerInvariant()
            releaseManifestSha256 = ([string]$reference.releaseManifestSha256).ToLowerInvariant()
            releaseVersion = [string]$reference.releaseVersion
            databaseRestorePointId = [string]$reference.databaseRestorePointId
            rehearsalPassed = $true
            restorePointVerified = $true
            observedAtUtc = $observedAt.ToString('O')
        }
    }
}

Write-Utf8Json -Value $report -Path $OutputPath -Depth 30
Write-Output "STAGE-OPERATION-REPORT status=PASS stage=$Stage type=$EvidenceType path=$OutputPath"
