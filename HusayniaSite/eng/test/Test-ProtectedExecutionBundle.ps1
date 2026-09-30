[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [switch]$KeepTemporary
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Resolve-HusayniaRepositoryRoot }
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "t21-r10-bundle-$([guid]::NewGuid().ToString('N'))"
$passed = 0
$failed = 0
function Assert-Test([string]$Name,[bool]$Condition,[string]$Failure) {
    if ($Condition) { $script:passed++; Write-Output "PASS  $Name" }
    else { $script:failed++; Write-Output "FAIL  $Name :: $Failure" }
}
function Invoke-Body([scriptblock]$Body,[hashtable]$Environment) {
    $prior = @{}
    try {
        foreach ($name in $Environment.Keys) {
            $prior[$name] = [Environment]::GetEnvironmentVariable($name)
            [Environment]::SetEnvironmentVariable($name,[string]$Environment[$name])
        }
        [TimeZoneInfo]::ClearCachedData()
        $output = @(& $Body 2>&1)
        return [pscustomobject]@{ exit = 0; output = $output; error = '' }
    }
    catch {
        return [pscustomobject]@{
            exit = 1
            output = @($_ | Out-String)
            error = $_.Exception.Message
        }
    }
    finally {
        foreach ($name in $Environment.Keys) {
            [Environment]::SetEnvironmentVariable($name,$prior[$name])
        }
        [TimeZoneInfo]::ClearCachedData()
    }
}

function New-SyntheticApplicationArchive {
    param([Parameter(Mandatory = $true)][string]$DestinationPath)

    $publishedRoot = Join-Path $RepositoryRoot 'src\Husaynia.Web\bin\Release\net10.0'
    $fixtureRoot = Join-Path $temporaryRoot "app-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $publishedRoot 'Husaynia.Web.deps.json') `
        -Destination $fixtureRoot
    Copy-Item -Path (Join-Path $publishedRoot '*.dll') -Destination $fixtureRoot
    foreach ($relativePath in @(
        'runtimes/unix/lib/net9.0/Microsoft.Data.SqlClient.dll',
        'runtimes/win/lib/net9.0/Microsoft.Data.SqlClient.dll'
    )) {
        $destination = Join-Path $fixtureRoot $relativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force |
            Out-Null
        Copy-Item -LiteralPath (Join-Path $publishedRoot $relativePath) `
            -Destination $destination
    }
    New-DeterministicZip -SourceDirectory $fixtureRoot -DestinationPath $DestinationPath
}

function New-MutatedApplicationArchive {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Mutation
    )

    $publishedRoot = Join-Path $RepositoryRoot 'src\Husaynia.Web\bin\Release\net10.0'
    $fixtureRoot = Join-Path $temporaryRoot "mutated-$Name"
    New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $publishedRoot 'Husaynia.Web.deps.json') `
        -Destination $fixtureRoot
    Copy-Item -Path (Join-Path $publishedRoot '*.dll') -Destination $fixtureRoot
    foreach ($relativePath in @(
        'runtimes/unix/lib/net9.0/Microsoft.Data.SqlClient.dll',
        'runtimes/win/lib/net9.0/Microsoft.Data.SqlClient.dll'
    )) {
        $destination = Join-Path $fixtureRoot $relativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force |
            Out-Null
        Copy-Item -LiteralPath (Join-Path $publishedRoot $relativePath) `
            -Destination $destination
    }
    & $Mutation $fixtureRoot
    $archivePath = Join-Path $temporaryRoot "$Name.zip"
    New-DeterministicZip -SourceDirectory $fixtureRoot -DestinationPath $archivePath
    return $archivePath
}

function Get-BundleBuildFailure {
    param(
        [Parameter(Mandatory = $true)][string]$ApplicationArchivePath,
        [Parameter(Mandatory = $true)][string]$Name
    )

    try {
        & $builder -RepositoryRoot $RepositoryRoot `
            -ApplicationArchivePath $ApplicationArchivePath `
            -DestinationPath (Join-Path $temporaryRoot "$Name-bundle.zip") | Out-Null
        return ''
    }
    catch {
        return [string]$_.Exception.Message
    }
}

function Update-ExtractedBundleManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][scriptblock]$Mutation
    )

    $manifestPath = Join-Path $Root 'bundle-manifest.json'
    $manifestItem = Get-Item -LiteralPath $manifestPath -Force
    $manifestItem.IsReadOnly = $false
    $manifest = Get-Content -LiteralPath $manifestPath -Raw |
        ConvertFrom-Json -DateKind String
    & $Mutation $manifest
    $manifestJson = ($manifest | ConvertTo-Json -Depth 100).
        Replace("`r`n", "`n").
        Replace("`r", "`n")
    [IO.File]::WriteAllText(
        $manifestPath,
        "$manifestJson`n",
        [Text.UTF8Encoding]::new($false))
    $checksumsPath = Join-Path $Root 'SHA256SUMS'
    $checksumsItem = Get-Item -LiteralPath $checksumsPath -Force
    $checksumsItem.IsReadOnly = $false
    $lines = @(Get-Content -LiteralPath $checksumsPath | ForEach-Object {
        if ($_ -match '^[a-f0-9]{64}  bundle-manifest\.json$') {
            "$(Get-Sha256Lower -Path $manifestPath)  bundle-manifest.json"
        }
        else {
            $_
        }
    })
    [IO.File]::WriteAllText(
        $checksumsPath,
        (($lines -join "`n") + "`n"),
        [Text.UTF8Encoding]::new($false))
}

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    $builder = Join-Path $RepositoryRoot 'eng\artifact\New-ProtectedExecutionBundle.ps1'
    $validator = Join-Path $RepositoryRoot 'eng\artifact\Test-ProtectedExecutionBundle.ps1'
    $workflowPath = Join-Path $RepositoryRoot 'pipelines\github\trusted-protected-operations.yml'
    $policyPath = Join-Path $RepositoryRoot 'pipelines\config\promotion-policy.json'
    Assert-Test 'protected-bundle-tools-present' (
        (Test-Path -LiteralPath $builder -PathType Leaf) -and
        (Test-Path -LiteralPath $validator -PathType Leaf)
    ) 'Protected bundle builder or validator is missing.'

    $policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
    $closureError = ''
    try {
        $null = Assert-ReviewedRepositoryFileClosure -RepositoryRoot $RepositoryRoot -Policy $policy `
            -IncludeProtectedHooks -IncludeMigrationOrchestrationFiles
    }
    catch { $closureError = $_.Exception.Message }
    Assert-Test 'protected-policy-hash-closure-is-current' (
        [string]::IsNullOrWhiteSpace($closureError)
    ) "Protected hash closure is stale: $closureError"

    $externalMarker = Join-Path $RepositoryRoot 'eng\promotion\Test-ExternalTrustMarker.ps1'
    $externalMarkerText = Get-Content -LiteralPath $externalMarker -Raw
    $requiredClaimsMatch = [regex]::Match(
        $externalMarkerText,
        '(?s)\$requiredClaims\s*=\s*@\((?<body>.*?)\)\s*foreach\s*\(\$required')
    $requiredClaimNames = if ($requiredClaimsMatch.Success) {
        @([regex]::Matches(
                $requiredClaimsMatch.Groups['body'].Value,
                "'(?<name>[^']+)'"
            ) | ForEach-Object { $_.Groups['name'].Value })
    }
    else {
        @()
    }
    $expectedRequiredClaims = @(
        'iss', 'aud', 'sub', 'repository', 'repository_id', 'repository_owner_id', 'ref',
        'environment', 'workflow_ref', 'workflow_sha', 'job_workflow_ref', 'job_workflow_sha'
    )
    Assert-Test 'external-marker-uses-environment-as-context-without-requesting-context-claim' (
        $requiredClaimsMatch.Success -and
        @(Compare-Object ($expectedRequiredClaims | Sort-Object) (
                $requiredClaimNames | Sort-Object)).Count -eq 0 -and
        $externalMarkerText.Contains(
            "`$context = Get-Claim -Claims `$claims -Name 'environment'") -and
        $externalMarkerText.Contains('[string]$_.context -ceq $context') -and
        -not $externalMarkerText.Contains(
            "Get-Claim -Claims `$claims -Name 'context'") -and
        -not $externalMarkerText.Contains("'environment-context'")
    ) 'External trust validation does not authoritatively derive context only from environment.'

    $reusableSha = 'c' * 40
    $reusableRef =
        "syedmh/Dreamer/.github/workflows/trusted-protected-operations.yml@$reusableSha"
    $callerRef =
        'syedmh/Dreamer/.github/workflows/operation-evidence-producer.yml@refs/heads/main'
    $protectedContext = 'Husaynia-Development-Operations'
    $developmentSubject = 'repo:syedmh@42/Dreamer@123' +
        ":environment:$protectedContext" +
        ":workflow_ref:$callerRef" +
        ":job_workflow_ref:$reusableRef"
    $externalSubjects = @(
        $developmentSubject,
        ('repo:syedmh@42/Dreamer@123:environment:Husaynia-Staging-Operations' +
            ':workflow_ref:syedmh/Dreamer/.github/workflows/' +
            "operation-evidence-producer.yml@refs/heads/main:job_workflow_ref:$reusableRef"),
        ('repo:syedmh@42/Dreamer@123:environment:Husaynia-Production' +
            ':workflow_ref:syedmh/Dreamer/.github/workflows/' +
            "production-operation-evidence.yml@refs/heads/main:job_workflow_ref:$reusableRef")
    )
    $externalSubjectsJson = ConvertTo-Json -InputObject $externalSubjects -Compress
    $bundleMarkerSha = 'd' * 64

    function New-ExternalTrustClaims {
        return [ordered]@{
            iss = 'https://token.actions.githubusercontent.com'
            aud = 'api://AzureADTokenExchange'
            sub = $developmentSubject
            repository = 'syedmh/Dreamer'
            repository_id = '123'
            repository_owner_id = '42'
            ref = 'refs/heads/main'
            environment = $protectedContext
            workflow_ref = $callerRef
            workflow_sha = 'a' * 40
            job_workflow_ref = $reusableRef
            job_workflow_sha = $reusableSha
        }
    }

    function Invoke-ExternalTrustClaims {
        param(
            [Parameter(Mandatory = $true)]$Claims,
            [Parameter(Mandatory = $true)][string]$Name,
            [string]$SubjectsJson = $externalSubjectsJson
        )

        $claimsPath = Join-Path $temporaryRoot "$Name-oidc-claims.json"
        $headerJson = '{"alg":"RS256","typ":"JWT"}'
        $claimsJson = ConvertTo-Json -InputObject $Claims -Depth 12 -Compress
        $toBase64Url = {
            param([string]$Value)
            return [Convert]::ToBase64String(
                [Text.Encoding]::UTF8.GetBytes($Value)
            ).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        }
        $jwt = "$(& $toBase64Url $headerJson).$(& $toBase64Url $claimsJson).fixture-signature"
        $payload = $jwt.Split('.')[1].Replace('-', '+').Replace('_', '/')
        while ($payload.Length % 4) {
            $payload += '='
        }
        [IO.File]::WriteAllBytes(
            $claimsPath,
            [Convert]::FromBase64String($payload))
        try {
            $output = @(& $externalMarker `
                    -ClaimsPath $claimsPath `
                    -Stage Development `
                    -ExpectedWorkflowRef $reusableRef `
                    -ExpectedSubjectsJson $SubjectsJson `
                    -ExpectedBundleSha256 $bundleMarkerSha `
                    -ActualBundleSha256 $bundleMarkerSha `
                    -PolicyPath $policyPath 2>&1)
            return [pscustomobject]@{
                passed = $true
                output = $output
                error = ''
            }
        }
        catch {
            return [pscustomobject]@{
                passed = $false
                output = @($_ | Out-String)
                error = $_.Exception.Message
            }
        }
    }

    $validJwtClaims = New-ExternalTrustClaims
    $validJwt = Invoke-ExternalTrustClaims -Claims $validJwtClaims -Name 'valid'
    Assert-Test 'external-marker-exact-approved-set-and-valid-jwt-pass' (
        -not $validJwtClaims.Contains('context') -and
        $validJwt.passed -and
        ($validJwt.output -join "`n").Contains(
            'EXTERNAL-TRUST-MARKER status=PASS stage=Development')
    ) "The exact three-subject marker or valid GitHub JWT payload was rejected: $($validJwt.error)"

    $currentAndJunk = @($developmentSubject, 'junk-1', 'junk-2')
    $currentAndJunkResult = Invoke-ExternalTrustClaims `
        -Claims (New-ExternalTrustClaims) `
        -Name 'current-and-junk-marker' `
        -SubjectsJson (ConvertTo-Json -InputObject $currentAndJunk -Compress)
    Assert-Test 'external-marker-current-subject-plus-junk-fails' (
        -not $currentAndJunkResult.passed -and
        $currentAndJunkResult.error.Contains('external-subject-marker')
    ) "Current+junk marker was not rejected: $($currentAndJunkResult.output -join ' | ')"

    $missingApproved = @($externalSubjects[0], $externalSubjects[1])
    $missingApprovedResult = Invoke-ExternalTrustClaims `
        -Claims (New-ExternalTrustClaims) `
        -Name 'missing-approved-marker' `
        -SubjectsJson (ConvertTo-Json -InputObject $missingApproved -Compress)
    Assert-Test 'external-marker-missing-approved-subject-fails' (
        -not $missingApprovedResult.passed -and
        $missingApprovedResult.error.Contains('external-subject-marker')
    ) "Missing approved marker was not rejected: $($missingApprovedResult.output -join ' | ')"

    $extraSubject = @($externalSubjects) + @('junk-extra')
    $extraSubjectResult = Invoke-ExternalTrustClaims `
        -Claims (New-ExternalTrustClaims) `
        -Name 'extra-subject-marker' `
        -SubjectsJson (ConvertTo-Json -InputObject $extraSubject -Compress)
    Assert-Test 'external-marker-extra-subject-fails' (
        -not $extraSubjectResult.passed -and
        $extraSubjectResult.error.Contains('external-subject-marker')
    ) "Extra subject marker was not rejected: $($extraSubjectResult.output -join ' | ')"

    $duplicateSubject = @($externalSubjects[0], $externalSubjects[0], $externalSubjects[2])
    $duplicateSubjectResult = Invoke-ExternalTrustClaims `
        -Claims (New-ExternalTrustClaims) `
        -Name 'duplicate-subject-marker' `
        -SubjectsJson (ConvertTo-Json -InputObject $duplicateSubject -Compress)
    Assert-Test 'external-marker-duplicate-subject-fails' (
        -not $duplicateSubjectResult.passed -and
        $duplicateSubjectResult.error.Contains('external-subject-marker')
    ) "Duplicate subject marker was not rejected: $($duplicateSubjectResult.output -join ' | ')"

    $caseChangedSubjects = @($externalSubjects)
    $caseChangedSubjects[1] = $caseChangedSubjects[1].Replace(
        'Husaynia-Staging-Operations',
        'husaynia-Staging-Operations')
    $caseChangedResult = Invoke-ExternalTrustClaims `
        -Claims (New-ExternalTrustClaims) `
        -Name 'case-changed-marker' `
        -SubjectsJson (ConvertTo-Json -InputObject $caseChangedSubjects -Compress)
    Assert-Test 'external-marker-case-changed-subject-fails' (
        -not $caseChangedResult.passed -and
        $caseChangedResult.error.Contains('external-subject-marker')
    ) "Case-changed subject marker was not rejected: $($caseChangedResult.output -join ' | ')"

    $wrongMarkerReusableSubjects = @($externalSubjects)
    $wrongMarkerReusableSubjects[1] = $wrongMarkerReusableSubjects[1].Replace(
        $reusableRef,
        "syedmh/Dreamer/.github/workflows/trusted-protected-operations.yml@$([string]('e' * 40))")
    $wrongMarkerReusableResult = Invoke-ExternalTrustClaims `
        -Claims (New-ExternalTrustClaims) `
        -Name 'wrong-marker-reusable' `
        -SubjectsJson (ConvertTo-Json -InputObject $wrongMarkerReusableSubjects -Compress)
    Assert-Test 'external-marker-wrong-reusable-workflow-subject-fails' (
        -not $wrongMarkerReusableResult.passed -and
        $wrongMarkerReusableResult.error.Contains('external-subject-marker')
    ) "Wrong reusable marker subject was not rejected: $($wrongMarkerReusableResult.output -join ' | ')"

    $wrongMarkerCallerSubjects = @($externalSubjects)
    $wrongMarkerCallerSubjects[1] = $wrongMarkerCallerSubjects[1].Replace(
        'operation-evidence-producer.yml@refs/heads/main',
        'production-operation-evidence.yml@refs/heads/main')
    $wrongMarkerCallerResult = Invoke-ExternalTrustClaims `
        -Claims (New-ExternalTrustClaims) `
        -Name 'wrong-marker-caller' `
        -SubjectsJson (ConvertTo-Json -InputObject $wrongMarkerCallerSubjects -Compress)
    Assert-Test 'external-marker-wrong-caller-workflow-subject-fails' (
        -not $wrongMarkerCallerResult.passed -and
        $wrongMarkerCallerResult.error.Contains('external-subject-marker')
    ) "Wrong caller marker subject was not rejected: $($wrongMarkerCallerResult.output -join ' | ')"

    $wrongMarkerEnvironmentSubjects = @($externalSubjects)
    $wrongMarkerEnvironmentSubjects[1] = $wrongMarkerEnvironmentSubjects[1].Replace(
        'Husaynia-Staging-Operations',
        'Husaynia-Unknown-Operations')
    $wrongMarkerEnvironmentResult = Invoke-ExternalTrustClaims `
        -Claims (New-ExternalTrustClaims) `
        -Name 'wrong-marker-environment' `
        -SubjectsJson (ConvertTo-Json -InputObject $wrongMarkerEnvironmentSubjects -Compress)
    Assert-Test 'external-marker-wrong-environment-subject-fails' (
        -not $wrongMarkerEnvironmentResult.passed -and
        $wrongMarkerEnvironmentResult.error.Contains('external-subject-marker')
    ) "Wrong environment marker subject was not rejected: $($wrongMarkerEnvironmentResult.output -join ' | ')"

    $missingEnvironmentClaims = New-ExternalTrustClaims
    $missingEnvironmentClaims.Remove('environment')
    $missingEnvironment = Invoke-ExternalTrustClaims `
        -Claims $missingEnvironmentClaims -Name 'missing-environment'
    Assert-Test 'external-marker-missing-environment-fails' (
        -not $missingEnvironment.passed -and
        $missingEnvironment.error.Contains('GitHub OIDC claim is missing: environment')
    ) "Missing environment was not rejected: $($missingEnvironment.output -join ' | ')"

    $wrongEnvironmentClaims = New-ExternalTrustClaims
    $wrongEnvironmentClaims.environment = 'Husaynia-Staging-Operations'
    $wrongEnvironment = Invoke-ExternalTrustClaims `
        -Claims $wrongEnvironmentClaims -Name 'wrong-environment'
    Assert-Test 'external-marker-wrong-environment-fails-caller-matrix' (
        -not $wrongEnvironment.passed -and
        $wrongEnvironment.error.Contains('caller workflow/context')
    ) "Wrong environment was not rejected by the caller matrix: $($wrongEnvironment.output -join ' | ')"

    $wrongSubjectClaims = New-ExternalTrustClaims
    $wrongSubjectClaims.sub = "$developmentSubject-tampered"
    $wrongSubject = Invoke-ExternalTrustClaims -Claims $wrongSubjectClaims -Name 'wrong-subject'
    Assert-Test 'external-marker-wrong-rendered-sub-fails' (
        -not $wrongSubject.passed -and
        $wrongSubject.error.Contains('rendered-subject')
    ) "Wrong rendered subject was not rejected: $($wrongSubject.output -join ' | ')"

    $wrongMarkerSubjects = @($externalSubjects)
    $wrongMarkerSubjects[0] = "$developmentSubject-not-approved"
    $wrongMarker = Invoke-ExternalTrustClaims `
        -Claims (New-ExternalTrustClaims) `
        -Name 'wrong-external-marker' `
        -SubjectsJson (ConvertTo-Json -InputObject $wrongMarkerSubjects -Compress)
    Assert-Test 'external-marker-wrong-external-subject-marker-fails' (
        -not $wrongMarker.passed -and
        $wrongMarker.error.Contains('external-subject-marker')
    ) "Wrong external subject marker was not rejected: $($wrongMarker.output -join ' | ')"

    $wrongCallerClaims = New-ExternalTrustClaims
    $wrongCallerClaims.workflow_ref =
        'syedmh/Dreamer/.github/workflows/production-operation-evidence.yml@refs/heads/main'
    $wrongCaller = Invoke-ExternalTrustClaims -Claims $wrongCallerClaims -Name 'wrong-caller'
    Assert-Test 'external-marker-wrong-caller-fails' (
        -not $wrongCaller.passed -and
        $wrongCaller.error.Contains('caller workflow/context')
    ) "Wrong caller was not rejected: $($wrongCaller.output -join ' | ')"

    $wrongReusableClaims = New-ExternalTrustClaims
    $wrongReusableClaims.job_workflow_ref =
        "syedmh/Dreamer/.github/workflows/stage-operation-inputs.yml@$reusableSha"
    $wrongReusable = Invoke-ExternalTrustClaims `
        -Claims $wrongReusableClaims -Name 'wrong-reusable'
    Assert-Test 'external-marker-wrong-reusable-ref-fails' (
        -not $wrongReusable.passed -and
        $wrongReusable.error.Contains('reusable-ref')
    ) "Wrong reusable workflow was not rejected: $($wrongReusable.output -join ' | ')"

    $wrongReusableShaClaims = New-ExternalTrustClaims
    $wrongReusableShaClaims.job_workflow_sha = 'e' * 40
    $wrongReusableSha = Invoke-ExternalTrustClaims `
        -Claims $wrongReusableShaClaims -Name 'wrong-reusable-sha'
    Assert-Test 'external-marker-wrong-reusable-sha-fails' (
        -not $wrongReusableSha.passed -and
        $wrongReusableSha.error.Contains('reusable-sha')
    ) "Wrong reusable workflow SHA was not rejected: $($wrongReusableSha.output -join ' | ')"

    $wrongCallerShaClaims = New-ExternalTrustClaims
    $wrongCallerShaClaims.workflow_sha = 'not-a-sha'
    $wrongCallerSha = Invoke-ExternalTrustClaims `
        -Claims $wrongCallerShaClaims -Name 'wrong-caller-sha'
    Assert-Test 'external-marker-malformed-caller-sha-fails' (
        -not $wrongCallerSha.passed -and
        $wrongCallerSha.error.Contains('caller-sha')
    ) "Malformed caller workflow SHA was not rejected: $($wrongCallerSha.output -join ' | ')"

    $applicationArchive = Join-Path $temporaryRoot 'Husaynia.Web.zip'
    New-SyntheticApplicationArchive -DestinationPath $applicationArchive
    $applicationSha = Get-Sha256Lower -Path $applicationArchive
    $bundleA = Join-Path $temporaryRoot 'bundle-a.zip'
    $bundleB = Join-Path $temporaryRoot 'bundle-b.zip'
    & $builder -RepositoryRoot $RepositoryRoot -ApplicationArchivePath $applicationArchive `
        -DestinationPath $bundleA | Out-Null
    & $builder -RepositoryRoot $RepositoryRoot -ApplicationArchivePath $applicationArchive `
        -DestinationPath $bundleB | Out-Null
    $bundleSha = Get-Sha256Lower -Path $bundleA
    Assert-Test 'protected-bundle-is-deterministic' (
        $bundleSha -ceq (Get-Sha256Lower -Path $bundleB)
    ) 'Two temporary protected bundles differ.'

    $extracted = Join-Path $temporaryRoot 'extracted'
    & $validator -BundlePath $bundleA -ExpectedSha256 $bundleSha `
        -ExpectedApplicationSha256 $applicationSha -ExtractTo $extracted | Out-Null
    & $validator -ExtractedRoot $extracted -ExpectedApplicationSha256 $applicationSha `
        -RequireReadOnly | Out-Null
    Assert-Test 'protected-bundle-validates-read-only' $true `
        'Valid protected bundle was rejected.'
    Assert-Test 'protected-bundle-contains-final-v21-closure' (
        (Test-Path -LiteralPath (
            Join-Path $extracted 'eng\promotion\New-CtoAuthorizationRecord.ps1') -PathType Leaf) -and
        (Test-Path -LiteralPath (
            Join-Path $extracted 'eng\promotion\Resolve-GitHubArtifactProvenance.ps1') -PathType Leaf) -and
        [string](Get-Content -LiteralPath (
            Join-Path $extracted 'pipelines\config\protected-operation-policy.json') -Raw |
            ConvertFrom-Json).t21ProvenanceContract.schemaVersion -ceq '2.1.0'
    ) 'Final CTO/resolver/policy closure is not in the protected bundle.'
    $bundleManifest = Get-Content -LiteralPath (
        Join-Path $extracted 'bundle-manifest.json') -Raw | ConvertFrom-Json -DateKind String
    Assert-Test 'protected-bundle-contains-exact-app-bound-sqlclient-runtime' (
        [string]$bundleManifest.schemaVersion -ceq '1.1.0' -and
        [string]$bundleManifest.sqlRuntime.sourceApplicationSha256 -ceq $applicationSha -and
        [string]$bundleManifest.sqlRuntime.packageId -ceq 'Microsoft.Data.SqlClient' -and
        [string]$bundleManifest.sqlRuntime.packageVersion -ceq '6.1.1' -and
        [string]$bundleManifest.sqlRuntime.target -ceq '.NETCoreApp,Version=v10.0' -and
        [string]$bundleManifest.sqlRuntime.runtime -ceq 'unix' -and
        @($bundleManifest.sqlRuntime.assemblies).Count -eq 19 -and
        @($bundleManifest.sqlRuntime.assemblies | Where-Object {
            [string]$_.simpleName -ceq 'Microsoft.Data.SqlClient' -and
            [string]$_.packageKey -ceq 'Microsoft.Data.SqlClient/6.1.1' -and
            [string]$_.dependencyAssetPath -ceq
                'runtimes/unix/lib/net9.0/Microsoft.Data.SqlClient.dll' -and
            [string]$_.sourcePath -ceq
                'runtimes/unix/lib/net9.0/Microsoft.Data.SqlClient.dll' -and
            [string]$_.runtimeIdentifier -ceq 'unix'
        }).Count -eq 1
    ) 'Protected bundle omitted or broadened the exact application-bound SqlClient closure.'
    $selectedSqlClient = @($bundleManifest.sqlRuntime.assemblies | Where-Object {
            [string]$_.simpleName -ceq 'Microsoft.Data.SqlClient'
        })[0]
    $unixSqlClientHash = Get-Sha256Lower -Path (
        Join-Path $RepositoryRoot (
            'src\Husaynia.Web\bin\Release\net10.0\' +
            'runtimes\unix\lib\net9.0\Microsoft.Data.SqlClient.dll'))
    $rootSqlClientHash = Get-Sha256Lower -Path (
        Join-Path $RepositoryRoot (
            'src\Husaynia.Web\bin\Release\net10.0\Microsoft.Data.SqlClient.dll'))
    $winSqlClientHash = Get-Sha256Lower -Path (
        Join-Path $RepositoryRoot (
            'src\Husaynia.Web\bin\Release\net10.0\' +
            'runtimes\win\lib\net9.0\Microsoft.Data.SqlClient.dll'))
    Assert-Test 'protected-bundle-copies-the-exact-selected-unix-sqlclient-entry' (
        [string]$selectedSqlClient.sha256 -ceq $unixSqlClientHash -and
        $unixSqlClientHash -cne $rootSqlClientHash -and
        $unixSqlClientHash -cne $winSqlClientHash -and
        (Get-Sha256Lower -Path (
                Join-Path $extracted 'runtime\sqlclient\Microsoft.Data.SqlClient.dll'
            )) -ceq $unixSqlClientHash
    ) 'The builder copied a root/basename or Windows SqlClient substitute instead of the selected Unix asset.'

    $wrongApplicationRejected = $false
    try {
        & $validator -BundlePath $bundleA -ExpectedSha256 $bundleSha `
            -ExpectedApplicationSha256 ('f' * 64) | Out-Null
    }
    catch { $wrongApplicationRejected = $_.Exception.Message.Contains('application-bound') }
    Assert-Test 'protected-bundle-rejects-runtime-application-hash-mismatch' (
        $wrongApplicationRejected
    ) 'Protected runtime accepted a different source application hash.'

    $readOnlyDriftRoot = Join-Path $temporaryRoot 'readonly-drift'
    Copy-Item -LiteralPath $extracted -Destination $readOnlyDriftRoot -Recurse
    $readOnlyDriftFile = Join-Path $readOnlyDriftRoot `
        'runtime\sqlclient\Microsoft.Data.SqlClient.dll'
    (Get-Item -LiteralPath $readOnlyDriftFile -Force).IsReadOnly = $false
    $readOnlyDriftRejected = $false
    try {
        & $validator -ExtractedRoot $readOnlyDriftRoot `
            -ExpectedApplicationSha256 $applicationSha -RequireReadOnly | Out-Null
    }
    catch { $readOnlyDriftRejected = $_.Exception.Message.Contains('writable') }
    Assert-Test 'protected-bundle-rejects-runtime-readonly-drift' $readOnlyDriftRejected `
        'A writable SqlClient runtime assembly was accepted.'

    $extraEntryRoot = Join-Path $temporaryRoot 'extra-entry'
    Copy-Item -LiteralPath $extracted -Destination $extraEntryRoot -Recurse
    [IO.File]::WriteAllText(
        (Join-Path $extraEntryRoot 'runtime\sqlclient\Unexpected.dll'),
        'unexpected',
        [Text.UTF8Encoding]::new($false))
    $extraEntryRejected = $false
    try {
        & $validator -ExtractedRoot $extraEntryRoot `
            -ExpectedApplicationSha256 $applicationSha | Out-Null
    }
    catch { $extraEntryRejected = $true }
    Assert-Test 'protected-bundle-rejects-extra-runtime-entry' $extraEntryRejected `
        'An extra runtime assembly outside the manifest closure was accepted.'

    $runtimeTamperRoot = Join-Path $temporaryRoot 'runtime-tamper'
    Copy-Item -LiteralPath $extracted -Destination $runtimeTamperRoot -Recurse
    $runtimeTamperPath = Join-Path $runtimeTamperRoot `
        'runtime\sqlclient\Microsoft.Data.SqlClient.dll'
    (Get-Item -LiteralPath $runtimeTamperPath -Force).IsReadOnly = $false
    $runtimeStream = [IO.File]::Open(
        $runtimeTamperPath,
        [IO.FileMode]::Append,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try { $runtimeStream.WriteByte(0) } finally { $runtimeStream.Dispose() }
    $runtimeTamperRejected = $false
    try {
        & $validator -ExtractedRoot $runtimeTamperRoot `
            -ExpectedApplicationSha256 $applicationSha | Out-Null
    }
    catch {
        $runtimeTamperRejected = $_.Exception.Message.Contains('checksum mismatch') -or
            $_.Exception.Message.Contains('assembly is invalid')
    }
    Assert-Test 'protected-bundle-rejects-runtime-tamper' $runtimeTamperRejected `
        'A modified SqlClient runtime assembly was accepted.'

    foreach ($manifestMutation in @(
        [pscustomobject]@{
            name = 'source-path'
            mutate = {
                param($value)
                @($value.sqlRuntime.assemblies | Where-Object {
                        [string]$_.simpleName -ceq 'Microsoft.Data.SqlClient'
                    })[0].sourcePath = 'Microsoft.Data.SqlClient.dll'
            }
        },
        [pscustomobject]@{
            name = 'runtime-identifier'
            mutate = {
                param($value)
                @($value.sqlRuntime.assemblies | Where-Object {
                        [string]$_.simpleName -ceq 'Microsoft.Data.SqlClient'
                    })[0].runtimeIdentifier = 'win'
            }
        },
        [pscustomobject]@{
            name = 'package-key'
            mutate = {
                param($value)
                @($value.sqlRuntime.assemblies | Where-Object {
                        [string]$_.simpleName -ceq 'Microsoft.Data.SqlClient'
                    })[0].packageKey = 'Microsoft.Data.SqlClient/6.1.0'
            }
        },
        [pscustomobject]@{
            name = 'managed-identity'
            mutate = {
                param($value)
                @($value.sqlRuntime.assemblies | Where-Object {
                        [string]$_.simpleName -ceq 'Microsoft.Data.SqlClient'
                    })[0].managedIdentity =
                    'Microsoft.Data.SqlClient, Version=99.0.0.0, Culture=neutral, PublicKeyToken=23ec7fc2d6eaa4a5'
            }
        }
    )) {
        $mutationRoot = Join-Path $temporaryRoot "manifest-$($manifestMutation.name)"
        Copy-Item -LiteralPath $extracted -Destination $mutationRoot -Recurse
        Update-ExtractedBundleManifest `
            -Root $mutationRoot `
            -Mutation $manifestMutation.mutate
        $mutationRejected = $false
        $mutationFailure = ''
        try {
            & $validator -ExtractedRoot $mutationRoot `
                -ExpectedApplicationSha256 $applicationSha | Out-Null
        }
        catch {
            $mutationFailure = $_.Exception.Message
            $mutationRejected = $_.Exception.Message.Contains(
                'SQL runtime assembly is invalid') -or
                $_.Exception.Message.Contains(
                    'SQL runtime assembly identity changed')
        }
        Assert-Test "protected-bundle-validator-rejects-sqlclient-$(
            $manifestMutation.name)-falsification" (
            $mutationRejected
        ) (
            "Protected manifest accepted falsified SqlClient $($manifestMutation.name): " +
            $mutationFailure
        )
    }

    $missingDepsArchive = New-MutatedApplicationArchive -Name 'missing-deps' -Mutation {
        param($root)
        Remove-Item -LiteralPath (Join-Path $root 'Husaynia.Web.deps.json') -Force
    }
    Assert-Test 'protected-bundle-builder-rejects-missing-dependency-manifest' (
        (Get-BundleBuildFailure -ApplicationArchivePath $missingDepsArchive `
            -Name 'missing-deps').Contains('exactly one root Husaynia.Web.deps.json')
    ) 'An application archive without its dependency manifest was accepted.'

    $wrongVersionArchive = New-MutatedApplicationArchive -Name 'wrong-version' -Mutation {
        param($root)
        $path = Join-Path $root 'Husaynia.Web.deps.json'
        $text = Get-Content -LiteralPath $path -Raw
        [IO.File]::WriteAllText(
            $path,
            $text.Replace('Microsoft.Data.SqlClient/6.1.1', 'Microsoft.Data.SqlClient/6.1.0'),
            [Text.UTF8Encoding]::new($false))
    }
    Assert-Test 'protected-bundle-builder-rejects-sqlclient-version-drift' (
        (Get-BundleBuildFailure -ApplicationArchivePath $wrongVersionArchive `
            -Name 'wrong-version').Contains('Microsoft.Data.SqlClient 6.1.1')
    ) 'A dependency manifest with SqlClient version drift was accepted.'

    $wrongPackageArchive = New-MutatedApplicationArchive -Name 'wrong-package' -Mutation {
        param($root)
        $path = Join-Path $root 'Husaynia.Web.deps.json'
        $text = Get-Content -LiteralPath $path -Raw
        [IO.File]::WriteAllText(
            $path,
            $text.Replace('Microsoft.Data.SqlClient/6.1.1', 'Contoso.Data.SqlClient/6.1.1'),
            [Text.UTF8Encoding]::new($false))
    }
    Assert-Test 'protected-bundle-builder-rejects-sqlclient-package-drift' (
        (Get-BundleBuildFailure -ApplicationArchivePath $wrongPackageArchive `
            -Name 'wrong-package').Contains('Microsoft.Data.SqlClient 6.1.1')
    ) 'A dependency manifest with the wrong SQL client package was accepted.'

    $wrongTargetArchive = New-MutatedApplicationArchive -Name 'wrong-target' -Mutation {
        param($root)
        $path = Join-Path $root 'Husaynia.Web.deps.json'
        $text = Get-Content -LiteralPath $path -Raw
        [IO.File]::WriteAllText(
            $path,
            $text.Replace('.NETCoreApp,Version=v10.0', '.NETCoreApp,Version=v9.0'),
            [Text.UTF8Encoding]::new($false))
    }
    Assert-Test 'protected-bundle-builder-rejects-runtime-target-drift' (
        (Get-BundleBuildFailure -ApplicationArchivePath $wrongTargetArchive `
            -Name 'wrong-target').Contains('exact .NET 10 runtime target')
    ) 'A dependency manifest with the wrong target was accepted.'

    $projectDependencyArchive = New-MutatedApplicationArchive `
        -Name 'project-dependency' -Mutation {
            param($root)
            $path = Join-Path $root 'Husaynia.Web.deps.json'
            $deps = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -DateKind String
            $deps.libraries.'Azure.Core/1.47.1'.type = 'project'
            Write-Utf8Json -Value $deps -Path $path -Depth 100
        }
    Assert-Test 'protected-bundle-builder-rejects-project-dependency' (
        (Get-BundleBuildFailure -ApplicationArchivePath $projectDependencyArchive `
            -Name 'project-dependency').Contains('project library')
    ) 'A project assembly entered the SQL runtime dependency closure.'

    $missingAssemblyArchive = New-MutatedApplicationArchive -Name 'missing-assembly' -Mutation {
        param($root)
        Remove-Item -LiteralPath (Join-Path $root 'Azure.Core.dll') -Force
    }
    Assert-Test 'protected-bundle-builder-rejects-missing-selected-assembly' (
        (Get-BundleBuildFailure -ApplicationArchivePath $missingAssemblyArchive `
            -Name 'missing-assembly').Contains('missing a SQL runtime assembly')
    ) 'A dependency-selected managed assembly was absent but accepted.'

    $nativeUnixArchive = New-MutatedApplicationArchive -Name 'native-unix' -Mutation {
        param($root)
        $path = Join-Path $root 'Husaynia.Web.deps.json'
        $deps = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -DateKind String
        $runtimeTargets = $deps.targets.'.NETCoreApp,Version=v10.0'.
            'Microsoft.Data.SqlClient.SNI.runtime/6.0.2'.runtimeTargets
        $runtimeTargets | Add-Member -NotePropertyName `
            'runtimes/unix/native/libMicrosoft.Data.SqlClient.SNI.so' `
            -NotePropertyValue ([pscustomobject]@{ rid = 'unix'; assetType = 'native' })
        Write-Utf8Json -Value $deps -Path $path -Depth 100
    }
    Assert-Test 'protected-bundle-builder-rejects-native-unix-asset' (
        (Get-BundleBuildFailure -ApplicationArchivePath $nativeUnixArchive `
            -Name 'native-unix').Contains('native Unix asset')
    ) 'A native Unix asset entered the managed SQL runtime closure.'

    $windowsRuntimeArchive = New-MutatedApplicationArchive -Name 'windows-runtime' -Mutation {
        param($root)
        $path = Join-Path $root 'Husaynia.Web.deps.json'
        $deps = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -DateKind String
        $sqlClientTargets = $deps.targets.'.NETCoreApp,Version=v10.0'.
            'Microsoft.Data.SqlClient/6.1.1'.runtimeTargets
        $sqlClientTargets.'runtimes/unix/lib/net9.0/Microsoft.Data.SqlClient.dll'.rid = 'win'
        Write-Utf8Json -Value $deps -Path $path -Depth 100
    }
    Assert-Test 'protected-bundle-builder-rejects-windows-rid-substitution' (
        (Get-BundleBuildFailure -ApplicationArchivePath $windowsRuntimeArchive `
            -Name 'windows-runtime').Contains('exactly one managed Unix runtime asset')
    ) 'A Windows/root fallback was accepted after the Unix runtime target was removed.'

    foreach ($versionField in @('assemblyVersion', 'fileVersion')) {
        $identityArchive = New-MutatedApplicationArchive `
            -Name "identity-$versionField" -Mutation {
                param($root)
                $path = Join-Path $root 'Husaynia.Web.deps.json'
                $deps = Get-Content -LiteralPath $path -Raw |
                    ConvertFrom-Json -DateKind String
                $asset = $deps.targets.'.NETCoreApp,Version=v10.0'.
                    'Microsoft.Data.SqlClient/6.1.1'.runtimeTargets.
                    'runtimes/unix/lib/net9.0/Microsoft.Data.SqlClient.dll'
                $asset.$versionField = '99.0.0.0'
                Write-Utf8Json -Value $deps -Path $path -Depth 100
            }.GetNewClosure()
        Assert-Test "protected-bundle-builder-rejects-selected-$versionField-falsification" (
            (Get-BundleBuildFailure -ApplicationArchivePath $identityArchive `
                -Name "identity-$versionField").Contains(
                'assembly version does not match its dependency manifest')
        ) "The selected Unix assembly accepted falsified $versionField metadata."
    }

    $duplicateAssemblyArchive = New-MutatedApplicationArchive `
        -Name 'duplicate-assembly' -Mutation {
            param($root)
            $path = Join-Path $root 'Husaynia.Web.deps.json'
            $deps = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -DateKind String
            $node = $deps.targets.'.NETCoreApp,Version=v10.0'.'System.ClientModel/1.5.1'
            $node.PSObject.Properties.Remove('runtime')
            $node | Add-Member -NotePropertyName runtime -NotePropertyValue ([pscustomobject]@{
                'lib/net8.0/Azure.Core.dll' = [pscustomobject]@{
                    assemblyVersion = '1.47.1.0'
                    fileVersion = '1.4700.125.36505'
                }
            })
            Write-Utf8Json -Value $deps -Path $path -Depth 100
        }
    Assert-Test 'protected-bundle-builder-rejects-duplicate-assembly-selection' (
        (Get-BundleBuildFailure -ApplicationArchivePath $duplicateAssemblyArchive `
            -Name 'duplicate-assembly').Contains('duplicate assembly file name')
    ) 'Two dependency nodes selected the same managed assembly.'

    $unsafeArchive = Join-Path $temporaryRoot 'unsafe-entry.zip'
    Copy-Item -LiteralPath $applicationArchive -Destination $unsafeArchive
    $unsafeZip = [IO.Compression.ZipFile]::Open(
        $unsafeArchive,
        [IO.Compression.ZipArchiveMode]::Update)
    try {
        $unsafeEntry = $unsafeZip.CreateEntry('../unsafe.dll')
        $unsafeStream = $unsafeEntry.Open()
        try { $unsafeStream.WriteByte(0) } finally { $unsafeStream.Dispose() }
    }
    finally { $unsafeZip.Dispose() }
    Assert-Test 'protected-bundle-builder-rejects-unsafe-application-entry' (
        (Get-BundleBuildFailure -ApplicationArchivePath $unsafeArchive `
            -Name 'unsafe-entry').Contains('unsafe, duplicate, or aliased path')
    ) 'An unsafe application ZIP entry was accepted.'

    $aliasArchive = Join-Path $temporaryRoot 'alias-entry.zip'
    Copy-Item -LiteralPath $applicationArchive -Destination $aliasArchive
    $aliasZip = [IO.Compression.ZipFile]::Open(
        $aliasArchive,
        [IO.Compression.ZipArchiveMode]::Update)
    try {
        $aliasEntry = $aliasZip.CreateEntry('azure.core.dll')
        $aliasStream = $aliasEntry.Open()
        try { $aliasStream.WriteByte(0) } finally { $aliasStream.Dispose() }
    }
    finally { $aliasZip.Dispose() }
    Assert-Test 'protected-bundle-builder-rejects-case-alias-application-entry' (
        (Get-BundleBuildFailure -ApplicationArchivePath $aliasArchive `
            -Name 'alias-entry').Contains('unsafe, duplicate, or aliased path')
    ) 'A case-aliased application ZIP entry was accepted.'

    $tampered = Join-Path $temporaryRoot 'tampered.zip'
    Copy-Item -LiteralPath $bundleA -Destination $tampered
    $stream = [IO.File]::Open($tampered,[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.WriteByte(0) } finally { $stream.Dispose() }
    $tamperRejected = $false
    try {
        & $validator -BundlePath $tampered -ExpectedSha256 $bundleSha `
            -ExpectedApplicationSha256 $applicationSha | Out-Null
    }
    catch { $tamperRejected = $_.Exception.Message.Contains('expected immutable SHA-256') }
    Assert-Test 'protected-bundle-outer-tamper-rejected' $tamperRejected `
        'Protected bundle outer hash tampering was accepted.'

    $workflow = Get-Content -LiteralPath $workflowPath -Raw | ConvertFrom-Json -DateKind String
    $downloadSteps = @($workflow.jobs.'protected-operation'.steps | Where-Object {
        [string]$_.name -ceq 'Resolve canonical C6 and compare protected archive before extraction'
    })
    Assert-Test 'trusted-workflow-has-one-exact-binary-body' (
        $downloadSteps.Count -eq 1 -and
        ([string]$downloadSteps[0].run).Contains(
            "& gh api ('repos/{0}/actions/artifacts/{1}/zip' -f `$env:GITHUB_REPOSITORY, `$artifact.id) > `$carrier") -and
        -not ([string]$downloadSteps[0].run).Contains('--output')
    ) 'Trusted workflow does not contain the exact supported binary gh body.'

    $installedGh = Get-Command gh -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $ghHelpOutput = @()
    $ghHelpExit = -1
    if ($null -ne $installedGh) {
        $ghHelpOutput = @(& $installedGh.Source api --help 2>&1)
        $ghHelpExit = $LASTEXITCODE
    }
    $ghHelpText = $ghHelpOutput -join "`n"
    Assert-Test 'installed-gh-api-parser-supports-stdout-contract-without-output-flag' (
        $null -ne $installedGh -and
        $ghHelpExit -eq 0 -and
        $ghHelpText.Contains('gh api <endpoint>') -and
        $ghHelpText -notmatch '(?m)^\s*--output(?:[=,\s]|$)'
    ) (
        "The real installed gh api parser/help contract is unavailable or advertises --output. " +
        "exit=$ghHelpExit output=$ghHelpText"
    )
    $downloadBody = [scriptblock]::Create([string]$downloadSteps[0].run)

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $carrierRoot = Join-Path $temporaryRoot 'carrier-root'
    New-Item -ItemType Directory -Path (Join-Path $carrierRoot 'operations') -Force | Out-Null
    Copy-Item -LiteralPath $bundleA -Destination (
        Join-Path $carrierRoot 'operations\protected-execution-bundle.zip')
    [IO.File]::WriteAllBytes(
        (Join-Path $carrierRoot 'arbitrary-binary.bin'),
        [byte[]](0,13,10,255,128,65,0,66))
    $carrierFixture = Join-Path $temporaryRoot 'carrier-fixture.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($carrierRoot,$carrierFixture)
    $carrierSha = Get-Sha256Lower -Path $carrierFixture

    $fakeRoot = Join-Path $temporaryRoot 'fake-gh'
    New-Item -ItemType Directory -Path $fakeRoot -Force | Out-Null
    $pythonPath = Join-Path $fakeRoot 'fake_gh.py'
    $python = @"
import json, os, pathlib, sys
args=sys.argv[1:]
if '--output' in args:
    sys.exit(97)
uri=next((a for a in args if a.startswith('repos/')), '')
mode=os.environ.get('FAKE_GH_MODE','valid')
if uri.endswith('/zip'):
    if mode=='cli-failure':
        sys.exit(19)
    if mode=='empty':
        sys.exit(0)
    sys.stdout.buffer.write(pathlib.Path(os.environ['FAKE_GH_CARRIER']).read_bytes())
    sys.exit(0)
now='2026-08-27 18:00:00 +00:00'
if '/artifacts' in uri:
    digest=os.environ['FAKE_GH_CARRIER_SHA']
    if mode=='digest-mismatch':
        digest='f'*64
    print(json.dumps({'artifacts':[{'id':701,'name':'husaynia-site-fixture','expired':False,'digest':'sha256:'+digest,'workflow_run':{'id':101}}]}))
elif uri.endswith('/999'):
    print(json.dumps({'repository':{'full_name':'syedmh/Dreamer'},'id':999,'path':'.github/workflows/consumer.yml','head_branch':'main','status':'in_progress','conclusion':None,'created_at':now,'updated_at':now}))
else:
    print(json.dumps({'repository':{'full_name':'syedmh/Dreamer'},'id':101,'path':'.github/workflows/release-build-and-nonproduction.yml','head_branch':'main','status':'completed','conclusion':'success','created_at':'2026-08-27 17:00:00 +00:00','updated_at':'2026-08-27 17:30:00 +00:00'}))
"@
    [IO.File]::WriteAllText($pythonPath,$python,[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $fakeRoot 'gh.cmd'),
        "@echo off`r`npython `"$pythonPath`" %*`r`n",
        [Text.ASCIIEncoding]::new())

    $baseEnvironment = @{
        PATH = "$fakeRoot$([IO.Path]::PathSeparator)$env:PATH"
        TZ = 'UTC'
        FAKE_GH_CARRIER = $carrierFixture
        FAKE_GH_CARRIER_SHA = $carrierSha
        FAKE_GH_MODE = 'valid'
        GITHUB_REPOSITORY = 'syedmh/Dreamer'
        GITHUB_RUN_ID = '999'
        T21_RELEASE_RUN_ID = '101'
        T21_EXPECTED_APP_SHA256 = 'a' * 64
        T21_TRUSTED_BUNDLE_SHA256 = $bundleSha
        T21_STAGE_TARGET_METADATA_JSON = '{"stage":"Development"}'
    }
    $validRunner = Join-Path $temporaryRoot 'runner-valid'
    New-Item -ItemType Directory -Path $validRunner -Force | Out-Null
    $validEnvironment = @{} + $baseEnvironment
    $validEnvironment.RUNNER_TEMP = $validRunner
    $validEnvironment.GITHUB_ENV = Join-Path $validRunner 'github-env'
    $valid = Invoke-Body -Body $downloadBody -Environment $validEnvironment
    $fixedBundle = Join-Path $validRunner 't21-trusted-protected-execution-bundle.zip'
    Assert-Test 'exact-binary-body-preserves-carrier-and-streams-fixed-bundle' (
        $valid.exit -eq 0 -and
        (Get-Sha256Lower -Path (Join-Path $validRunner 'c6-carrier.zip')) -ceq $carrierSha -and
        (Get-Sha256Lower -Path $fixedBundle) -ceq $bundleSha -and
        (Get-Content -LiteralPath $validEnvironment.GITHUB_ENV -Raw).Contains(
            'T21_TRUSTED_BUNDLE_ARCHIVE=')
    ) "Exact checked-in binary body failed valid arbitrary bytes: $($valid.error)"

    foreach ($case in @(
        @{ name = 'cli-failure'; mode = 'cli-failure'; message = 'binary download failed' },
        @{ name = 'empty'; mode = 'empty'; message = 'size, count, or decompression limits' },
        @{ name = 'digest-mismatch'; mode = 'digest-mismatch'; message = 'digest changed' }
    )) {
        $runner = Join-Path $temporaryRoot "runner-$($case.name)"
        New-Item -ItemType Directory -Path $runner -Force | Out-Null
        $environment = @{} + $baseEnvironment
        $environment.RUNNER_TEMP = $runner
        $environment.GITHUB_ENV = Join-Path $runner 'github-env'
        $environment.FAKE_GH_MODE = $case.mode
        $result = Invoke-Body -Body $downloadBody -Environment $environment
        Assert-Test "binary-$($case.name)-fails-before-side-effects" (
            $result.exit -ne 0 -and
            $result.error.Contains([string]$case.message) -and
            -not (Test-Path -LiteralPath $environment.GITHUB_ENV) -and
            -not (Test-Path -LiteralPath (
                Join-Path $runner 't21-trusted-protected-execution-bundle.zip'))
        ) "Binary $($case.name) reached trusted environment or fixed-bundle effects: $($result.error)"
    }

    $trustedText = Get-Content -LiteralPath $workflowPath -Raw
    Assert-Test 'binary-static-missing-and-oversize-guards-remain' (
        $trustedText.Contains('-not (Test-Path -LiteralPath $carrier -PathType Leaf)') -and
        $trustedText.Contains('$carrierLength -lt 1') -and
        $trustedText.Contains('$carrierLength -gt 2GB') -and
        $trustedText.Contains('[IO.File]::Delete($carrier)')
    ) 'Missing/empty/oversize carrier guards were weakened.'
    Assert-Test 'binary-r9-entry-canonicalization-remains' (
        $trustedText.Contains('StringComparer]::OrdinalIgnoreCase') -and
        $trustedText.Contains("`$_ -in @('.','..')") -and
        $trustedText.Contains('IncrementalHash') -and
        $trustedText.Contains('$copied -ne $bundleEntry.Length')
    ) 'Carrier path, alias, size, or streaming controls were weakened.'
    Assert-Test 'trusted-post-login-remains-bundle-only' (
        -not $trustedText.Contains('$env:GITHUB_WORKSPACE') -and
        $trustedText.Contains('T21_TRUSTED_BUNDLE_ROOT/eng/artifact/Test-ProtectedExecutionBundle.ps1')
    ) 'Trusted workflow uses checkout content after login.'
    Assert-Test 'external-marker-contract-is-three-caller-v21' (
        @($policy.t21ProvenanceContract.callerMatrix).Count -eq 3 -and
        (Get-Content -LiteralPath (
            Join-Path $RepositoryRoot 'eng\promotion\Test-ExternalTrustMarker.ps1') -Raw).
            Contains('Test-ExactOrdinalStringSet -Actual $subjects -Expected $approvedSubjects')
    ) 'External OIDC subject validation retained five release-inclusive rows.'
}
finally {
    if ($KeepTemporary) { Write-Output "TEMP $temporaryRoot" }
    else { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Output "SUMMARY protected-bundle total=$($passed + $failed) passed=$passed failed=$failed binaryCases=4 ghParserCases=1 auth=0 dispatches=0 deployments=0 database=0 resources=0"
if ($failed -gt 0) { exit 1 }
