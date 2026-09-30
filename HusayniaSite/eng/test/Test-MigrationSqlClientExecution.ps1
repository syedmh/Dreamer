[CmdletBinding()]
param([string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
. (Join-Path $PSScriptRoot '..\artifact\migrations\bundle\Migration.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Resolve-HusayniaRepositoryRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "t21-sqlclient-$([guid]::NewGuid().ToString('N'))")
$passed = 0
$failed = 0
$tokenOccurrences = 0
$seamActionsCalls = 0
$sqlCalls = 0

function Assert-SqlClientTest {
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

function Get-StableFailure {
    param([scriptblock]$Body)

    try {
        & $Body | Out-Null
        return ''
    }
    catch {
        return [string]$_.Exception.Message
    }
}

function New-SyntheticSqlClientApplicationArchive {
    param([Parameter(Mandatory = $true)][string]$DestinationPath)

    $publishedRoot = Join-Path $RepositoryRoot 'src\Husaynia.Web\bin\Release\net10.0'
    $fixtureRoot = Join-Path $temporaryRoot "application-$([guid]::NewGuid().ToString('N'))"
    if (-not (Test-Path -LiteralPath (
                Join-Path $publishedRoot 'Husaynia.Web.deps.json') -PathType Leaf)) {
        throw 'The existing locked local Husaynia.Web publish output is unavailable.'
    }
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

function New-DevelopmentTargetMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$StagePolicy
    )

    $subscriptionId = '11111111-2222-3333-4444-555555555555'
    $stableToken = 'dev123abc4567'
    $resourceGroup = [string]$StagePolicy.immutableResourceGroupName
    $sqlServerName = "husaynia-sql-$($StagePolicy.stageCode)-$stableToken"
    $databaseName = "husaynia-$($StagePolicy.stageCode)"
    $webAppName = "husaynia-web-$($StagePolicy.stageCode)-$stableToken"
    $keyVaultName = "hsy-kv-$($StagePolicy.stageCode)-$stableToken"
    $keyVaultResourceId =
        "/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.KeyVault/vaults/$keyVaultName"
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
        sqlServerResourceId =
            "/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.Sql/servers/$sqlServerName"
        sqlDatabaseResourceId =
            "/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.Sql/servers/$sqlServerName/databases/$databaseName"
        webAppName = $webAppName
        webAppResourceId =
            "/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.Web/sites/$webAppName"
        keyVaultName = $keyVaultName
        secretReferences = @(
            [ordered]@{
                settingKey = 'ConnectionStrings__HusayniaDatabase'
                secretName = 'SqlConnectionString'
                provider = 'AzureKeyVault'
                referenceIdentifier =
                    "@Microsoft.KeyVault(SecretUri=https://$keyVaultName.vault.azure.net/secrets/SqlConnectionString/$secretVersion)"
                resourceId = $keyVaultResourceId
                mode = 'reference'
                stage = 'Development'
            },
            [ordered]@{
                settingKey = 'APPLICATIONINSIGHTS_CONNECTION_STRING'
                secretName = 'ApplicationInsightsConnectionString'
                provider = 'AzureKeyVault'
                referenceIdentifier =
                    "@Microsoft.KeyVault(SecretUri=https://$keyVaultName.vault.azure.net/secrets/ApplicationInsightsConnectionString/$secretVersion)"
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

function New-EnabledProtectedBundleFixture {
    param(
        [Parameter(Mandatory = $true)][string]$ApplicationArchivePath
    )

    $fixtureRepository = Join-Path $temporaryRoot 'enabled-producer-repository'
    New-Item -ItemType Directory -Path $fixtureRepository -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'eng') `
        -Destination $fixtureRepository -Recurse
    New-Item -ItemType Directory -Path (
        Join-Path $fixtureRepository 'contracts\pipeline'), (
        Join-Path $fixtureRepository 'pipelines\config') -Force | Out-Null
    Copy-Item -LiteralPath (
        Join-Path $RepositoryRoot 'contracts\pipeline\release-manifest.schema.json'
    ) -Destination (Join-Path $fixtureRepository 'contracts\pipeline')
    $policy = Get-Content -LiteralPath (
        Join-Path $RepositoryRoot 'pipelines\config\promotion-policy.json') -Raw |
        ConvertFrom-Json -DateKind String
    @($policy.stages | Where-Object {
            [string]$_.name -ceq 'Development'
        })[0].deploymentEnabled = $true
    $policyPath = Join-Path $fixtureRepository 'pipelines\config\promotion-policy.json'
    Write-Utf8Json -Value $policy -Path $policyPath -Depth 100

    $bundlePath = Join-Path $temporaryRoot 'enabled-protected-execution-bundle.zip'
    & (Join-Path $RepositoryRoot 'eng\artifact\New-ProtectedExecutionBundle.ps1') `
        -RepositoryRoot $fixtureRepository `
        -ApplicationArchivePath $ApplicationArchivePath `
        -DestinationPath $bundlePath | Out-Null
    $bundleSha256 = Get-Sha256Lower -Path $bundlePath
    $bundleRoot = Join-Path $temporaryRoot 'enabled-protected-bundle'
    & (Join-Path $RepositoryRoot 'eng\artifact\Test-ProtectedExecutionBundle.ps1') `
        -BundlePath $bundlePath `
        -ExpectedSha256 $bundleSha256 `
        -ExpectedApplicationSha256 (Get-Sha256Lower -Path $ApplicationArchivePath) `
        -ExtractTo $bundleRoot | Out-Null
    Get-ChildItem -LiteralPath $bundleRoot -Recurse -File -Force | ForEach-Object {
        $_.IsReadOnly = $true
    }
    return [pscustomobject]@{
        RepositoryRoot = $fixtureRepository
        PolicyPath = Join-Path $bundleRoot 'pipelines\config\protected-operation-policy.json'
        BundlePath = $bundlePath
        BundleRoot = (Resolve-Path -LiteralPath $bundleRoot).Path
        BundleSha256 = $bundleSha256
    }
}

function New-ReleaseProvenanceFixture {
    param(
        [Parameter(Mandatory = $true)][string]$ApplicationArchivePath,
        [Parameter(Mandatory = $true)]$ProtectedBundle,
        [Parameter(Mandatory = $true)][string]$ReleaseCommitSha
    )

    $resolverRoot = Join-Path $temporaryRoot 'enabled-release'
    $artifactName = 'husaynia-site-enabled-preflight'
    $artifactRoot = Join-Path $resolverRoot "artifact\$artifactName"
    foreach ($relativeDirectory in @(
        'app',
        'contracts',
        'migrations\bundle',
        'migrations\sql',
        'operations',
        'release',
        'reports\accessibility',
        'reports\security',
        'reports\test-results',
        'sbom'
    )) {
        New-Item -ItemType Directory -Path (Join-Path $artifactRoot $relativeDirectory) `
            -Force | Out-Null
    }
    Copy-Item -LiteralPath $ApplicationArchivePath `
        -Destination (Join-Path $artifactRoot 'app\Husaynia.Web.zip')
    Copy-Item -LiteralPath $ProtectedBundle.BundlePath `
        -Destination (Join-Path $artifactRoot 'operations\protected-execution-bundle.zip')
    Copy-Item -LiteralPath (
        Join-Path $ProtectedBundle.RepositoryRoot (
            'eng\artifact\migrations\bundle\Invoke-MigrationBundle.ps1')
    ) -Destination (Join-Path $artifactRoot 'migrations\bundle')
    Copy-Item -LiteralPath (
        Join-Path $ProtectedBundle.RepositoryRoot (
            'eng\artifact\migrations\bundle\Migration.Common.ps1')
    ) -Destination (Join-Path $artifactRoot 'migrations\bundle')
    Copy-Item -LiteralPath (
        Join-Path $ProtectedBundle.RepositoryRoot 'eng\artifact\migrations\sql\000-preflight.sql'
    ) -Destination (Join-Path $artifactRoot 'migrations\sql')
    foreach ($migrationTextFile in Get-ChildItem -LiteralPath (
            Join-Path $artifactRoot 'migrations') -Recurse -File |
        Where-Object { $_.Extension -in @('.ps1', '.sql') }) {
        $utf8 = [Text.UTF8Encoding]::new($false, $true)
        $text = $utf8.GetString([IO.File]::ReadAllBytes($migrationTextFile.FullName))
        [IO.File]::WriteAllText(
            $migrationTextFile.FullName,
            $text.Replace("`r`n", "`n").Replace("`r", "`n"),
            [Text.UTF8Encoding]::new($false))
    }
    Copy-Item -LiteralPath (
        Join-Path $RepositoryRoot 'evidence\baseline\route-manifest.json'
    ) -Destination (Join-Path $artifactRoot 'contracts\route-manifest.json')
    Copy-Item -LiteralPath (
        Join-Path $RepositoryRoot 'contracts\migration\import-manifest.schema.json'
    ) -Destination (Join-Path $artifactRoot 'contracts\import-manifest.schema.json')
    foreach ($relativePath in @(
        'reports\accessibility\accessibility.json',
        'reports\security\security.json',
        'reports\test-results\tests.trx',
        'sbom\sbom.spdx.json'
    )) {
        [IO.File]::WriteAllText(
            (Join-Path $artifactRoot $relativePath),
            '{}',
            [Text.UTF8Encoding]::new($false))
    }

    $payloadFiles = Get-ChildItem -LiteralPath $artifactRoot -Recurse -File |
        Where-Object {
            (Get-RelativeUnixPath -BasePath $artifactRoot -Path $_.FullName) -notlike
                'release/*'
        } |
        Sort-Object {
            Get-RelativeUnixPath -BasePath $artifactRoot -Path $_.FullName
        }
    $manifestFiles = @($payloadFiles | ForEach-Object {
        [ordered]@{
            path = Get-RelativeUnixPath -BasePath $artifactRoot -Path $_.FullName
            sha256 = Get-Sha256Lower -Path $_.FullName
            length = [long]$_.Length
        }
    })
    $manifestPath = Join-Path $artifactRoot 'release\release-manifest.json'
    Write-Utf8Json -Value ([ordered]@{
        schemaVersion = '1.0.0'
        version = 'enabled-preflight'
        commitSha = $ReleaseCommitSha
        builtAtUtc = [DateTimeOffset]::UtcNow.AddMinutes(-10).ToString('O')
        dotnetSdk = '10.0.400'
        files = $manifestFiles
        databaseCompatibility =
            'expand-compatible; explicit-preflight-and-apply; no-runtime-startup-migrations'
        requiredConfigurationKeys = @(
            'WEBSITE_RUN_FROM_PACKAGE',
            'ConnectionStrings__HusayniaDatabase',
            'APPLICATIONINSIGHTS_CONNECTION_STRING',
            'AppConfiguration__Endpoint',
            'Storage__AccountName',
            'KeyVault__Uri'
        )
        prohibitedLiveConfigurationInNonProduction = @(
            'Stripe live credentials',
            'public messaging delivery',
            'production content mutation',
            'production analytics identifiers',
            'cross-stage data or service connections'
        )
    }) -Path $manifestPath -Depth 40
    $checksumFiles = Get-ChildItem -LiteralPath $artifactRoot -Recurse -File |
        Where-Object {
            (Get-RelativeUnixPath -BasePath $artifactRoot -Path $_.FullName) -cne
                'release/SHA256SUMS'
        } |
        Sort-Object {
            Get-RelativeUnixPath -BasePath $artifactRoot -Path $_.FullName
        }
    $checksumLines = @($checksumFiles | ForEach-Object {
        "$(Get-Sha256Lower -Path $_.FullName)  $(
            Get-RelativeUnixPath -BasePath $artifactRoot -Path $_.FullName)"
    })
    [IO.File]::WriteAllLines(
        (Join-Path $artifactRoot 'release\SHA256SUMS'),
        $checksumLines,
        [Text.UTF8Encoding]::new($false))

    $provenancePath = Join-Path $resolverRoot 'verified-provenance.json'
    $now = [DateTimeOffset]::UtcNow
    Write-Utf8Json -Value ([ordered]@{
        schemaVersion = '2.0.0'
        authority = 'github-actions-api-and-sigstore-v1'
        expectedRole = 'release-c6'
        run = [ordered]@{
            repository = 'syedmh/Dreamer'
            workflowPath = '.github/workflows/release-build-and-nonproduction.yml'
            workflowRef =
                'syedmh/Dreamer/.github/workflows/release-build-and-nonproduction.yml@refs/heads/main'
            runId = '8101'
            runAttempt = 1
            ref = 'refs/heads/main'
            commitSha = $ReleaseCommitSha
            status = 'completed'
            conclusion = 'success'
        }
        artifact = [ordered]@{
            id = '8201'
            name = $artifactName
            archiveSha256 = 'a' * 64
            sizeBytes = 1
            createdAtUtc = $now.AddMinutes(-5).ToString('O')
            expiresAtUtc = $now.AddDays(1).ToString('O')
        }
        contentManifestSha256 = Get-Sha256Lower -Path $manifestPath
        attestation = $null
    }) -Path $provenancePath -Depth 30
    return [pscustomobject]@{
        ArtifactRoot = $artifactRoot
        ProvenancePath = $provenancePath
        ManifestPath = $manifestPath
    }
}

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    $applicationArchive = Join-Path $temporaryRoot 'Husaynia.Web.zip'
    New-SyntheticSqlClientApplicationArchive -DestinationPath $applicationArchive
    $applicationSha256 = Get-Sha256Lower -Path $applicationArchive
    $bundlePath = Join-Path $temporaryRoot 'protected-execution-bundle.zip'
    $builderPath = Join-Path $RepositoryRoot 'eng\artifact\New-ProtectedExecutionBundle.ps1'
    $validatorPath = Join-Path $RepositoryRoot 'eng\artifact\Test-ProtectedExecutionBundle.ps1'
    & $builderPath -RepositoryRoot $RepositoryRoot `
        -ApplicationArchivePath $applicationArchive `
        -DestinationPath $bundlePath | Out-Null
    $bundleSha256 = Get-Sha256Lower -Path $bundlePath
    $bundleRoot = Join-Path $temporaryRoot 'bundle'
    & $validatorPath -BundlePath $bundlePath -ExpectedSha256 $bundleSha256 `
        -ExpectedApplicationSha256 $applicationSha256 -ExtractTo $bundleRoot | Out-Null

    $manifest = Get-Content -LiteralPath (Join-Path $bundleRoot 'bundle-manifest.json') -Raw |
        ConvertFrom-Json -DateKind String
    Assert-SqlClientTest 'sqlclient-runtime-manifest-is-v11-app-bound-exact-managed-closure' (
        [string]$manifest.schemaVersion -ceq '1.1.0' -and
        [string]$manifest.sqlRuntime.sourceApplicationSha256 -ceq $applicationSha256 -and
        [string]$manifest.sqlRuntime.packageVersion -ceq '6.1.1' -and
        [string]$manifest.sqlRuntime.runtime -ceq 'unix' -and
        @($manifest.sqlRuntime.assemblies).Count -eq 19 -and
        @($manifest.sqlRuntime.assemblies | Where-Object {
            [string]$_.bundlePath -notmatch '^runtime/sqlclient/[A-Za-z0-9.]+\.dll$' -or
            [string]::IsNullOrWhiteSpace([string]$_.packageKey) -or
            [string]::IsNullOrWhiteSpace([string]$_.dependencyAssetPath) -or
            [string]::IsNullOrWhiteSpace([string]$_.assemblyVersion) -or
            [string]::IsNullOrWhiteSpace([string]$_.fileVersion) -or
            [string]::IsNullOrWhiteSpace([string]$_.managedIdentity)
        }).Count -eq 0 -and
        @($manifest.sqlRuntime.assemblies | Where-Object {
            [string]$_.simpleName -ceq 'Microsoft.Data.SqlClient' -and
            [string]$_.sourcePath -ceq
                'runtimes/unix/lib/net9.0/Microsoft.Data.SqlClient.dll' -and
            [string]$_.runtimeIdentifier -ceq 'unix'
        }).Count -eq 1
    ) 'Protected manifest did not contain the exact 19-assembly SqlClient 6.1.1 Unix closure.'

    $wrongApplicationFailure = Get-StableFailure {
        & $validatorPath -BundlePath $bundlePath -ExpectedSha256 $bundleSha256 `
            -ExpectedApplicationSha256 ('f' * 64)
    }
    Assert-SqlClientTest 'sqlclient-runtime-validator-rejects-application-hash-mismatch' (
        $wrongApplicationFailure.Contains('application-bound')
    ) "Wrong application hash was accepted: $wrongApplicationFailure"

    $goodFacts = [pscustomobject]@{
        isLinux = $true
        architecture = 'X64'
        powerShellVersion = '7.6.5'
        dotNetVersion = '10.0.0'
    }
    $descriptor = Assert-MigrationSqlRuntimeCompatibility `
        -BundleRoot $bundleRoot `
        -ExpectedApplicationSha256 $applicationSha256 `
        -EnableLocalTestSeams `
        -LocalPlatformFacts $goodFacts
    Assert-SqlClientTest 'sqlclient-runtime-compatibility-loads-managed-entry-in-collectible-context' (
        [string]$descriptor.Runtime.entryAssembly -ceq
            'runtime/sqlclient/Microsoft.Data.SqlClient.dll' -and
        (Get-Content -LiteralPath (
                Join-Path $RepositoryRoot 'eng\artifact\migrations\bundle\Migration.Common.ps1'
            ) -Raw).Contains('RestrictedAssemblyLoadContext') -and
        (Get-Content -LiteralPath (
                Join-Path $RepositoryRoot 'eng\artifact\migrations\bundle\Migration.Common.ps1'
            ) -Raw).Contains(': base(name, true)') -and
        (Get-Content -LiteralPath (
                Join-Path $RepositoryRoot 'eng\artifact\migrations\bundle\Migration.Common.ps1'
            ) -Raw).Contains('$loaded.Context.Unload()')
    ) 'Compatibility did not load the manifest-selected entry assembly in a collectible context.'
    $unknownDependencyRejected = $false
    $restrictedContext = New-MigrationSqlRuntimeLoadContext -Descriptor $descriptor
    try {
        try {
            $null = $restrictedContext.Context.LoadFromAssemblyName(
                [Reflection.AssemblyName]::new('Contoso.Unlisted.Dependency'))
        }
        catch {
            $unknownDependencyRejected =
                $_.Exception.ToString().Contains('T21_SQL_RUNTIME_INVALID')
        }
    }
    finally {
        $restrictedContext.EntryAssembly = $null
        $restrictedContext.Context.Unload()
    }
    Assert-SqlClientTest 'sqlclient-runtime-rejects-unlisted-assembly-resolution' (
        $unknownDependencyRejected
    ) 'The collectible runtime context resolved an assembly outside the manifest closure.'
    $wrongManagedIdentityRejected = $false
    $restrictedContext = New-MigrationSqlRuntimeLoadContext -Descriptor $descriptor
    try {
        try {
            $null = $restrictedContext.Context.LoadFromAssemblyName(
                [Reflection.AssemblyName]::new(
                    'Microsoft.Data.SqlClient, Version=99.0.0.0, Culture=neutral, PublicKeyToken=23ec7fc2d6eaa4a5'))
        }
        catch {
            $wrongManagedIdentityRejected =
                $_.Exception.ToString().Contains('T21_SQL_RUNTIME_INVALID')
        }
    }
    finally {
        $restrictedContext.EntryAssembly = $null
        $restrictedContext.Context.Unload()
    }
    Assert-SqlClientTest 'sqlclient-runtime-rejects-listed-simple-name-with-wrong-managed-identity' (
        $wrongManagedIdentityRejected
    ) 'The collectible runtime context resolved a listed basename with a different full managed identity.'

    foreach ($badFacts in @(
        [pscustomobject]@{ isLinux = $false; architecture = 'X64'; powerShellVersion = '7.6.5'; dotNetVersion = '10.0.0' },
        [pscustomobject]@{ isLinux = $true; architecture = 'Arm64'; powerShellVersion = '7.6.5'; dotNetVersion = '10.0.0' },
        [pscustomobject]@{ isLinux = $true; architecture = 'X64'; powerShellVersion = '7.5.9'; dotNetVersion = '10.0.0' },
        [pscustomobject]@{ isLinux = $true; architecture = 'X64'; powerShellVersion = '7.6.5'; dotNetVersion = '9.0.0' }
    )) {
        $compatibilityFailure = Get-StableFailure {
            Assert-MigrationSqlRuntimeCompatibility `
                -BundleRoot $bundleRoot `
                -ExpectedApplicationSha256 $applicationSha256 `
                -EnableLocalTestSeams `
                -LocalPlatformFacts $badFacts
        }
        Assert-SqlClientTest "sqlclient-runtime-rejects-platform-$(
            [string]$badFacts.isLinux)-$([string]$badFacts.architecture)-$(
            [string]$badFacts.powerShellVersion)-$([string]$badFacts.dotNetVersion)" (
            $compatibilityFailure -ceq 'T21_SQL_RUNTIME_INVALID'
        ) "Unsupported real runtime facts were accepted: $compatibilityFailure"
    }

    $global:T21SqlClientTokenState = [ordered]@{ calls = 0; request = $null }
    $validToken = 'eyJhbGciOiJSUzI1NiJ9.fixture.signature'
    $tokenProvider = {
        param($Request)
        $global:T21SqlClientTokenState.calls++
        $global:T21SqlClientTokenState.request = $Request
        return [pscustomobject]@{
            exitCode = 0
            timedOut = $false
            stdout = $validToken
            stderr = ''
        }
    }.GetNewClosure()
    $tokenNoSwitchFailure = Get-StableFailure {
        Get-MigrationAzureSqlAccessToken `
            -LocalAccessTokenProvider $tokenProvider
    }
    Assert-SqlClientTest 'azure-sql-token-provider-requires-explicit-local-test-seam-switch' (
        $tokenNoSwitchFailure -ceq 'T21_SQL_RUNTIME_INVALID' -and
        $global:T21SqlClientTokenState.calls -eq 0
    ) 'The local Azure CLI process seam ran without EnableLocalTestSeams.'
    $token = Get-MigrationAzureSqlAccessToken `
        -EnableLocalTestSeams `
        -LocalAccessTokenProvider $tokenProvider
    $expectedTokenArguments = @(
        'account',
        'get-access-token',
        '--resource',
        'https://database.windows.net/',
        '--query',
        'accessToken',
        '--output',
        'tsv',
        '--only-show-errors'
    )
    Assert-SqlClientTest 'azure-sql-token-provider-uses-exact-bounded-direct-cli-contract' (
        $token -ceq $validToken -and
        $global:T21SqlClientTokenState.calls -eq 1 -and
        [string]$global:T21SqlClientTokenState.request.filePath -ceq 'az' -and
        [int]$global:T21SqlClientTokenState.request.timeoutSeconds -eq 30 -and
        (@($global:T21SqlClientTokenState.request.arguments) -join "`n") -ceq
            ($expectedTokenArguments -join "`n")
    ) 'Token provider did not receive the exact direct Azure CLI vector and 30-second bound.'
    $token = $null

    $leakyToken = 'leaky-token-value'
    $malformedTokenFailure = Get-StableFailure {
        Get-MigrationAzureSqlAccessToken -EnableLocalTestSeams -LocalAccessTokenProvider {
            param($Request)
            return [pscustomobject]@{
                exitCode = 0
                timedOut = $false
                stdout = "$leakyToken malformed"
                stderr = ''
            }
        }.GetNewClosure()
    }
    $tokenOccurrences = @(
        $malformedTokenFailure,
        [Environment]::GetEnvironmentVariable('T21_SQL_ACCESS_TOKEN'),
        [Environment]::GetEnvironmentVariable('AZURE_SQL_ACCESS_TOKEN')
    ) | Where-Object { [string]$_ -like "*$leakyToken*" } | Measure-Object |
        Select-Object -ExpandProperty Count
    Assert-SqlClientTest 'azure-sql-token-failure-is-stable-and-redacted' (
        $malformedTokenFailure -ceq 'T21_AZURE_SQL_TOKEN_UNAVAILABLE' -and
        $tokenOccurrences -eq 0 -and
        @((Get-ChildItem -LiteralPath $temporaryRoot -Recurse -File) | Where-Object {
            (Get-Content -LiteralPath $_.FullName -Raw -ErrorAction SilentlyContinue) -like
                "*$leakyToken*"
        }).Count -eq 0
    ) 'Malformed token content reached an error, environment variable, or file.'

    foreach ($tokenFailureCase in @(
        [pscustomobject]@{
            name = 'timeout'
            result = [pscustomobject]@{
                exitCode = 0
                timedOut = $true
                stdout = 'timeout-token-material'
                stderr = 'timeout-sensitive-stderr'
            }
            sensitive = @('timeout-token-material', 'timeout-sensitive-stderr')
        },
        [pscustomobject]@{
            name = 'nonzero'
            result = [pscustomobject]@{
                exitCode = 17
                timedOut = $false
                stdout = 'nonzero-token-material'
                stderr = 'nonzero-sensitive-stderr'
            }
            sensitive = @('nonzero-token-material', 'nonzero-sensitive-stderr')
        }
    )) {
        $global:T21SqlClientTokenFailureState = [ordered]@{
            calls = 0
            request = $null
        }
        $failureResult = $tokenFailureCase.result
        $failure = Get-StableFailure {
            Get-MigrationAzureSqlAccessToken `
                -EnableLocalTestSeams `
                -LocalAccessTokenProvider {
                    param($Request)
                    $global:T21SqlClientTokenFailureState.calls++
                    $global:T21SqlClientTokenFailureState.request = $Request
                    return $failureResult
                }.GetNewClosure()
        }
        $sensitiveValues = @($tokenFailureCase.sensitive)
        $leakCount = @($sensitiveValues | ForEach-Object {
            $sensitive = [string]$_
            @(
                $failure,
                [Environment]::GetEnvironmentVariable('T21_SQL_ACCESS_TOKEN'),
                [Environment]::GetEnvironmentVariable('AZURE_SQL_ACCESS_TOKEN')
            ) | Where-Object { [string]$_ -like "*$sensitive*" }
        }).Count
        $fileLeakCount = @((Get-ChildItem -LiteralPath $temporaryRoot -Recurse -File) |
            Where-Object {
                $content = Get-Content -LiteralPath $_.FullName -Raw `
                    -ErrorAction SilentlyContinue
                @($sensitiveValues | Where-Object {
                    [string]$content -like "*$([string]$_)*"
                }).Count -ne 0
            }).Count
        Assert-SqlClientTest "azure-sql-token-$($tokenFailureCase.name)-fails-once-redacted" (
            $failure -ceq 'T21_AZURE_SQL_TOKEN_UNAVAILABLE' -and
            $global:T21SqlClientTokenFailureState.calls -eq 1 -and
            [int]$global:T21SqlClientTokenFailureState.request.timeoutSeconds -eq 30 -and
            $leakCount -eq 0 -and
            $fileLeakCount -eq 0
        ) "Azure CLI $($tokenFailureCase.name) was retried or leaked process output: $failure"
    }

    $pwshPath = (Get-Process -Id $PID).Path
    $timeoutStopwatch = [Diagnostics.Stopwatch]::StartNew()
    $actualTimeoutResult = Invoke-MigrationBoundedProcess `
        -FilePath $pwshPath `
        -Arguments @(
            '-NoProfile',
            '-NonInteractive',
            '-Command',
            "Start-Sleep -Seconds 5; [Console]::Out.Write('late-token-material')"
        ) `
        -TimeoutMilliseconds 100
    $timeoutStopwatch.Stop()
    $actualTimeoutFailure = Get-StableFailure {
        Get-MigrationAzureSqlAccessToken `
            -EnableLocalTestSeams `
            -LocalAccessTokenProvider {
                param($Request)
                return $actualTimeoutResult
            }.GetNewClosure()
    }
    Assert-SqlClientTest 'azure-sql-token-real-process-timeout-kills-and-waits' (
        $actualTimeoutResult.timedOut -eq $true -and
        $timeoutStopwatch.Elapsed.TotalSeconds -lt 5 -and
        $actualTimeoutFailure -ceq 'T21_AZURE_SQL_TOKEN_UNAVAILABLE' -and
        [string]$actualTimeoutFailure -notlike '*late-token-material*'
    ) (
        'The real bounded process timeout did not terminate and wait before returning a stable ' +
        "redacted failure: elapsed=$($timeoutStopwatch.Elapsed.TotalSeconds) " +
        "failure=$actualTimeoutFailure"
    )

    $actualNonzeroResult = Invoke-MigrationBoundedProcess `
        -FilePath $pwshPath `
        -Arguments @(
            '-NoProfile',
            '-NonInteractive',
            '-Command',
            "[Console]::Error.Write('nonzero-process-sensitive'); exit 17"
        ) `
        -TimeoutMilliseconds 5000
    $actualNonzeroFailure = Get-StableFailure {
        Get-MigrationAzureSqlAccessToken `
            -EnableLocalTestSeams `
            -LocalAccessTokenProvider {
                param($Request)
                return $actualNonzeroResult
            }.GetNewClosure()
    }
    Assert-SqlClientTest 'azure-sql-token-real-process-nonzero-is-stable-and-redacted' (
        $actualNonzeroResult.timedOut -eq $false -and
        [int]$actualNonzeroResult.exitCode -eq 17 -and
        $actualNonzeroFailure -ceq 'T21_AZURE_SQL_TOKEN_UNAVAILABLE' -and
        [string]$actualNonzeroFailure -notlike '*nonzero-process-sensitive*'
    ) "The real bounded process nonzero path was not preserved and redacted: $actualNonzeroFailure"

    $global:T21SqlClientExecutionState = [ordered]@{
        calls = 0
        request = $null
        mode = 'valid'
    }
    $sqlExecutor = {
        param($Request)
        $global:T21SqlClientExecutionState.calls++
        $global:T21SqlClientExecutionState.request = $Request
        switch ($global:T21SqlClientExecutionState.mode) {
            'valid' {
                return [pscustomobject]@{
                    resultSetCount = 1
                    rows = ,@(
                        'husaynia-dev',
                        'fixture',
                        '16.0',
                        'ONLINE',
                        '1',
                        '0',
                        '0',
                        '0',
                        '0',
                        '0',
                        '0'
                    )
                }
            }
            'extra-result' {
                return [pscustomobject]@{ resultSetCount = 2; rows = ,@(1..11) }
            }
            'extra-row' {
                return [pscustomobject]@{
                    resultSetCount = 1
                    rows = @(@(1..11), @(1..11))
                }
            }
            'wrong-columns' {
                return [pscustomobject]@{ resultSetCount = 1; rows = ,@(1..10) }
            }
            'throw' { throw 'raw database transport failure with secret material' }
        }
    }
    $fixedPreflightQuery = @'
SET NOCOUNT ON;
SELECT
    DB_NAME(),
    CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')),
    CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),
    CONVERT(nvarchar(60), DATABASEPROPERTYEX(DB_NAME(), 'Status')),
    HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'SELECT'),
    HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER'),
    HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CONTROL'),
    HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE'),
    HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'DELETE'),
    HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'INSERT'),
    HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'UPDATE');
'@
    $rows = Invoke-MigrationSqlQuery `
        -ServerName 'fixture.database.windows.net' `
        -DatabaseName 'husaynia-dev' `
        -Query $fixedPreflightQuery `
        -ExpectedColumnCount 11 `
        -CommandTimeoutSeconds 60 `
        -BundleRoot $bundleRoot `
        -ExpectedApplicationSha256 $applicationSha256 `
        -EnableLocalTestSeams `
        -LocalAccessTokenProvider $tokenProvider `
        -LocalSqlExecutor $sqlExecutor
    $preflightIdentity = Assert-MigrationPreflightSqlRow `
        -Row $rows[0] `
        -TargetMetadata ([pscustomobject]@{
            sqlDatabaseName = 'husaynia-dev'
            sqlServerName = 'fixture'
            sqlServerFqdn = 'fixture.database.windows.net'
        })
    $requestText = $global:T21SqlClientExecutionState.request |
        ConvertTo-Json -Depth 10 -Compress
    Assert-SqlClientTest 'typed-sql-adapter-enforces-fixed-connection-shape-without-token-in-seam' (
        [string]$preflightIdentity.DatabaseStatus -ceq 'ONLINE' -and
        $global:T21SqlClientExecutionState.calls -eq 1 -and
        [string]$global:T21SqlClientExecutionState.request.dataSource -ceq
            'tcp:fixture.database.windows.net,1433' -and
        $global:T21SqlClientExecutionState.request.encrypt -eq $true -and
        $global:T21SqlClientExecutionState.request.trustServerCertificate -eq $false -and
        $global:T21SqlClientExecutionState.request.pooling -eq $false -and
        [int]$global:T21SqlClientExecutionState.request.connectionTimeoutSeconds -eq 15 -and
        [int]$global:T21SqlClientExecutionState.request.commandTimeoutSeconds -eq 60 -and
        [string]$global:T21SqlClientExecutionState.request.applicationName -ceq
            'Husaynia-T21-Migration' -and
        $requestText -notlike "*$validToken*" -and
        -not $requestText.Contains('accessToken')
    ) 'The local SQL seam received a token or a connection contract different from the frozen design.'

    foreach ($case in @(
        @{ mode = 'extra-result'; error = 'T21_SQL_RESULT_INVALID' },
        @{ mode = 'extra-row'; error = 'T21_SQL_RESULT_INVALID' },
        @{ mode = 'wrong-columns'; error = 'T21_SQL_RESULT_INVALID' },
        @{ mode = 'throw'; error = 'T21_SQL_EXECUTION_FAILED' }
    )) {
        $global:T21SqlClientExecutionState.mode = $case.mode
        $global:T21SqlClientExecutionState.calls = 0
        $failure = Get-StableFailure {
            Invoke-MigrationSqlQuery `
                -ServerName 'fixture.database.windows.net' `
                -DatabaseName 'husaynia-dev' `
                -Query $fixedPreflightQuery `
                -ExpectedColumnCount 11 `
                -BundleRoot $bundleRoot `
                -ExpectedApplicationSha256 $applicationSha256 `
                -EnableLocalTestSeams `
                -LocalAccessTokenProvider $tokenProvider `
                -LocalSqlExecutor $sqlExecutor
        }
        Assert-SqlClientTest "typed-sql-adapter-$($case.mode)-fails-once-with-stable-error" (
            $failure -ceq [string]$case.error -and
            $global:T21SqlClientExecutionState.calls -eq 1 -and
            -not $failure.Contains('secret material')
        ) "SQL $($case.mode) failure was retried or leaked raw details: $failure"
    }

    foreach ($badRow in @(
        @('wrong-db', 'fixture', '16.0', 'ONLINE', '1', '0', '0', '0', '0', '0', '0'),
        @('husaynia-dev', 'wrong-server', '16.0', 'ONLINE', '1', '0', '0', '0', '0', '0', '0'),
        @('husaynia-dev', 'fixture', '16.0', 'OFFLINE', '1', '0', '0', '0', '0', '0', '0'),
        @('husaynia-dev', 'fixture', '16.0', 'ONLINE', '0', '0', '0', '0', '0', '0', '0'),
        @('husaynia-dev', 'fixture', '16.0', 'ONLINE', '1', '1', '0', '0', '0', '0', '0')
    )) {
        $preflightFailure = Get-StableFailure {
            Assert-MigrationPreflightSqlRow -Row $badRow -TargetMetadata ([pscustomobject]@{
                sqlDatabaseName = 'husaynia-dev'
                sqlServerName = 'fixture'
                sqlServerFqdn = 'fixture.database.windows.net'
            })
        }
        Assert-SqlClientTest "typed-preflight-rejects-invalid-value-$($badRow[0])-$($badRow[3])-$($badRow[4])-$($badRow[5])" (
            $preflightFailure -ceq 'T21_SQL_RESULT_INVALID'
        ) "Invalid typed Preflight row was accepted: $preflightFailure"
    }

    $global:T21SqlClientLeaseState = [ordered]@{
        calls = 0
        fail = $false
        badValue = $false
    }
    $leaseExecutor = {
        param($Request)
        $global:T21SqlClientLeaseState.calls++
        if ($global:T21SqlClientLeaseState.fail) {
            throw 'ambiguous lease transport failure'
        }
        if ($global:T21SqlClientLeaseState.badValue) {
            return [pscustomobject]@{
                resultSetCount = 1
                rows = ,@(
                    'not-a-fence-token',
                    '2026-08-27T20:00:00+00:00',
                    '2026-08-27T20:15:00+00:00',
                    '',
                    '0',
                    '',
                    ''
                )
            }
        }
        return [pscustomobject]@{
            resultSetCount = 1
            rows = ,@(
                '1',
                '2026-08-27T20:00:00+00:00',
                '2026-08-27T20:15:00+00:00',
                '',
                '0',
                '',
                ''
            )
        }
    }
    $lease = Invoke-SqlMigrationStageLease `
        -Mode AcquireLease `
        -ServerName 'fixture.database.windows.net' `
        -DatabaseName 'husaynia-dev' `
        -LeasePolicy ([pscustomobject]@{
            provider = 'sqlserver-stage-lease-v1'
            tableName = '__HusayniaStageLease'
            leaseDurationSeconds = 900
            renewAfterSeconds = 300
        }) `
        -Resource ('husaynia-stage-lease-development-' + ('a' * 64)) `
        -Stage Development `
        -TargetFingerprint ('a' * 64) `
        -AuthorizationEvidenceSha256 ('b' * 64) `
        -Holder ([ordered]@{ holderRunId = '101'; holderRunAttempt = 1 }) `
        -BundleRoot $bundleRoot `
        -ExpectedApplicationSha256 $applicationSha256 `
        -EnableLocalTestSeams `
        -LocalAccessTokenProvider $tokenProvider `
        -LocalSqlExecutor $leaseExecutor
    Assert-SqlClientTest 'durable-lease-uses-typed-seven-column-adapter-once' (
        [int64]$lease.fenceToken -eq 1 -and
        $lease.released -eq $false -and
        $global:T21SqlClientLeaseState.calls -eq 1
    ) 'Lease SQL did not use the typed seven-column adapter exactly once.'
    $global:T21SqlClientLeaseState.calls = 0
    $global:T21SqlClientLeaseState.badValue = $true
    $leaseValueFailure = Get-StableFailure {
        Invoke-SqlMigrationStageLease `
            -Mode AcquireLease `
            -ServerName 'fixture.database.windows.net' `
            -DatabaseName 'husaynia-dev' `
            -LeasePolicy ([pscustomobject]@{
                provider = 'sqlserver-stage-lease-v1'
                tableName = '__HusayniaStageLease'
                leaseDurationSeconds = 900
                renewAfterSeconds = 300
            }) `
            -Resource ('husaynia-stage-lease-development-' + ('a' * 64)) `
            -Stage Development `
            -TargetFingerprint ('a' * 64) `
            -AuthorizationEvidenceSha256 ('b' * 64) `
            -Holder ([ordered]@{ holderRunId = '101'; holderRunAttempt = 1 }) `
            -BundleRoot $bundleRoot `
            -ExpectedApplicationSha256 $applicationSha256 `
            -EnableLocalTestSeams `
            -LocalAccessTokenProvider $tokenProvider `
            -LocalSqlExecutor $leaseExecutor
    }
    Assert-SqlClientTest 'durable-lease-invalid-seven-column-value-is-sanitized' (
        $leaseValueFailure -ceq 'T21_SQL_RESULT_INVALID' -and
        $global:T21SqlClientLeaseState.calls -eq 1 -and
        -not $leaseValueFailure.Contains('not-a-fence-token')
    ) "Invalid lease values leaked or were accepted: $leaseValueFailure"
    $global:T21SqlClientLeaseState.calls = 0
    $global:T21SqlClientLeaseState.badValue = $false
    $global:T21SqlClientLeaseState.fail = $true
    $leaseFailure = Get-StableFailure {
        Invoke-SqlMigrationStageLease `
            -Mode AcquireLease `
            -ServerName 'fixture.database.windows.net' `
            -DatabaseName 'husaynia-dev' `
            -LeasePolicy ([pscustomobject]@{
                provider = 'sqlserver-stage-lease-v1'
                tableName = '__HusayniaStageLease'
                leaseDurationSeconds = 900
                renewAfterSeconds = 300
            }) `
            -Resource ('husaynia-stage-lease-development-' + ('a' * 64)) `
            -Stage Development `
            -TargetFingerprint ('a' * 64) `
            -AuthorizationEvidenceSha256 ('b' * 64) `
            -Holder ([ordered]@{ holderRunId = '101'; holderRunAttempt = 1 }) `
            -BundleRoot $bundleRoot `
            -ExpectedApplicationSha256 $applicationSha256 `
            -EnableLocalTestSeams `
            -LocalAccessTokenProvider $tokenProvider `
            -LocalSqlExecutor $leaseExecutor
    }
    Assert-SqlClientTest 'durable-lease-ambiguous-failure-is-not-retried' (
        $leaseFailure -ceq 'T21_SQL_EXECUTION_FAILED' -and
        $global:T21SqlClientLeaseState.calls -eq 1
    ) "Ambiguous lease failure was retried or unsanitized: $leaseFailure"

    $priorGitHubActions = [Environment]::GetEnvironmentVariable('GITHUB_ACTIONS')
    try {
        [Environment]::SetEnvironmentVariable('GITHUB_ACTIONS', 'true')
        $global:T21SqlClientActionsSeamState = [ordered]@{ token = 0; sql = 0 }
        $actionsFailure = Get-StableFailure {
            Invoke-MigrationSqlQuery `
                -ServerName 'fixture.database.windows.net' `
                -DatabaseName 'husaynia-dev' `
                -Query $fixedPreflightQuery `
                -ExpectedColumnCount 11 `
                -BundleRoot $bundleRoot `
                -ExpectedApplicationSha256 $applicationSha256 `
                -EnableLocalTestSeams `
                -LocalAccessTokenProvider {
                    param($Request)
                    $global:T21SqlClientActionsSeamState.token++
                    return [pscustomobject]@{
                        exitCode = 0
                        timedOut = $false
                        stdout = $validToken
                        stderr = ''
                    }
                }.GetNewClosure() `
                -LocalSqlExecutor {
                    param($Request)
                    $global:T21SqlClientActionsSeamState.sql++
                    return ,@(1..11)
                }
        }
        $seamActionsCalls = [int]$global:T21SqlClientActionsSeamState.token +
            [int]$global:T21SqlClientActionsSeamState.sql
        Assert-SqlClientTest 'github-actions-rejects-local-seams-before-provider-or-executor' (
            $actionsFailure -ceq 'T21_SQL_RUNTIME_INVALID' -and
            $seamActionsCalls -eq 0
        ) "GITHUB_ACTIONS=true invoked a local seam: $actionsFailure calls=$seamActionsCalls"
    }
    finally {
        [Environment]::SetEnvironmentVariable('GITHUB_ACTIONS', $priorGitHubActions)
        Remove-Variable -Scope Global -Name T21SqlClientActionsSeamState `
            -ErrorAction SilentlyContinue
    }

    $enabledProtectedBundle = New-EnabledProtectedBundleFixture `
        -ApplicationArchivePath $applicationArchive
    $releaseCommit = 'e' * 40
    $producerCommit = 'd' * 40
    $releaseFixture = New-ReleaseProvenanceFixture `
        -ApplicationArchivePath $applicationArchive `
        -ProtectedBundle $enabledProtectedBundle `
        -ReleaseCommitSha $releaseCommit
    $enabledPolicy = Get-Content -LiteralPath $enabledProtectedBundle.PolicyPath -Raw |
        ConvertFrom-Json -DateKind String
    $enabledStagePolicy = @($enabledPolicy.stages | Where-Object {
            [string]$_.name -ceq 'Development'
        })[0]
    $targetPath = Join-Path $temporaryRoot 'enabled-stage-target.json'
    $targetMetadata = New-DevelopmentTargetMetadata `
        -Path $targetPath `
        -StagePolicy $enabledStagePolicy
    $global:T21RealProducerSqlState = [ordered]@{
        tokenCalls = 0
        sqlCalls = 0
    }
    $realProducerTokenProvider = {
        param($Request)
        $global:T21RealProducerSqlState.tokenCalls++
        return [pscustomobject]@{
            exitCode = 0
            timedOut = $false
            stdout = 'real-producer-local-token'
            stderr = ''
        }
    }
    $realProducerSqlExecutor = {
        param($Request)
        $global:T21RealProducerSqlState.sqlCalls++
        return [pscustomobject]@{
            resultSetCount = 1
            rows = ,@(
                [string]$targetMetadata.sqlDatabaseName,
                [string]$targetMetadata.sqlServerName,
                '16.0',
                'ONLINE',
                '1',
                '0',
                '0',
                '0',
                '0',
                '0',
                '0'
            )
        }
    }.GetNewClosure()
    $producerOutputRoot = Join-Path $temporaryRoot 'real-producer-output'
    $priorTrustedBundleRoot =
        [Environment]::GetEnvironmentVariable('T21_TRUSTED_BUNDLE_ROOT')
    $priorIdentityMode =
        [Environment]::GetEnvironmentVariable('T21_MIGRATION_IDENTITY_MODE')
    $priorProducerGitHubActions =
        [Environment]::GetEnvironmentVariable('GITHUB_ACTIONS')
    $realProducerFailure = ''
    $realProducerOutput = @()
    try {
        [Environment]::SetEnvironmentVariable(
            'T21_TRUSTED_BUNDLE_ROOT',
            $enabledProtectedBundle.BundleRoot)
        [Environment]::SetEnvironmentVariable(
            'T21_MIGRATION_IDENTITY_MODE',
            'readonly')
        [Environment]::SetEnvironmentVariable('GITHUB_ACTIONS', '')
        $realProducerOutput = @(& (Join-Path $enabledProtectedBundle.BundleRoot (
            'eng\promotion\Invoke-OperationEvidenceProducer.ps1')) `
            -Mode Preflight `
            -Stage Development `
            -ExpectedAppSha256 $applicationSha256 `
            -ReleaseVerifiedProvenancePath $releaseFixture.ProvenancePath `
            -TopLevelCallerWorkflowRef (
                'syedmh/Dreamer/.github/workflows/operation-evidence-producer.yml@refs/heads/main') `
            -ProducerWorkflowRef (
                "syedmh/Dreamer/.github/workflows/trusted-protected-operations.yml@$('c' * 40)") `
            -ProducerRunId '9101' `
            -ProducerRunAttempt 1 `
            -ProducerCommitSha $producerCommit `
            -StageTargetMetadataPath $targetPath `
            -OutputRoot $producerOutputRoot `
            -TrustedBundleSha256 $enabledProtectedBundle.BundleSha256 `
            -PolicyPath $enabledProtectedBundle.PolicyPath `
            -AllowProtectedOperations `
            -EnableLocalTestSeams `
            -LocalAccessTokenProvider $realProducerTokenProvider `
            -LocalSqlExecutor $realProducerSqlExecutor 2>&1)
    }
    catch {
        $realProducerFailure =
            "$($_.Exception.Message) at $($_.InvocationInfo.ScriptName):$($_.InvocationInfo.ScriptLineNumber) $($_.ScriptStackTrace)"
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            'T21_TRUSTED_BUNDLE_ROOT',
            $priorTrustedBundleRoot)
        [Environment]::SetEnvironmentVariable(
            'T21_MIGRATION_IDENTITY_MODE',
            $priorIdentityMode)
        [Environment]::SetEnvironmentVariable(
            'GITHUB_ACTIONS',
            $priorProducerGitHubActions)
    }
    $realPreflightPath = Join-Path $producerOutputRoot 'migration-preflight.json'
    $realProducerManifestPath = Join-Path $producerOutputRoot 't21-producer-manifest.json'
    $realPreflight = if (Test-Path -LiteralPath $realPreflightPath -PathType Leaf) {
        Get-Content -LiteralPath $realPreflightPath -Raw |
            ConvertFrom-Json -DateKind String
    }
    else {
        $null
    }
    Assert-SqlClientTest 'enabled-v21-real-producer-wrapper-needs-no-apply-dll-and-keeps-distinct-commits' (
        [string]::IsNullOrWhiteSpace($realProducerFailure) -and
        [string]$enabledPolicy.t21ProvenanceContract.schemaVersion -ceq '2.1.0' -and
        $null -eq $enabledPolicy.migration.applicationBundle.sha256 -and
        $null -ne $realPreflight -and
        [string]$realPreflight.status -ceq 'PASS' -and
        [string]$realPreflight.operation.bundlePath -ceq
            'operations/protected-execution-bundle.zip' -and
        [string]$realPreflight.operation.bundleSha256 -ceq
            $enabledProtectedBundle.BundleSha256 -and
        [string]$realPreflight.operation.bundleFormat -ceq
            'protected-execution-bundle-v1.1.0' -and
        [string]$realPreflight.releaseCommitSha -ceq $releaseCommit -and
        [string]$realPreflight.sourceRun.commitSha -ceq $producerCommit -and
        $producerCommit -cne $releaseCommit -and
        $global:T21RealProducerSqlState.tokenCalls -eq 1 -and
        $global:T21RealProducerSqlState.sqlCalls -eq 1 -and
        (Test-Path -LiteralPath $realProducerManifestPath -PathType Leaf) -and
        -not (Test-Path -LiteralPath (
                Join-Path $releaseFixture.ArtifactRoot (
                    'migrations\bundle\Husaynia.Database.Migrations.dll')
            )) -and
        -not (Test-Path -LiteralPath (
                Join-Path $enabledProtectedBundle.BundleRoot (
                    'eng\artifact\migrations\bundle\Husaynia.Database.Migrations.dll')
            ))
    ) (
        'The current v2.1 enabled Preflight failed before or during the real producer/wrapper ' +
        "path without an Apply DLL, or conflated commits: " +
        "$realProducerFailure output=$($realProducerOutput -join ' | ')"
    )
    $global:T21UnmaterializedApplyState = [ordered]@{ tokenCalls = 0; sqlCalls = 0 }
    $priorApplyIdentityMode =
        [Environment]::GetEnvironmentVariable('T21_MIGRATION_IDENTITY_MODE')
    try {
        [Environment]::SetEnvironmentVariable('T21_MIGRATION_IDENTITY_MODE', 'apply')
        $unmaterializedApplyFailure = Get-StableFailure {
            & (Join-Path $enabledProtectedBundle.BundleRoot (
                'eng\artifact\migrations\bundle\Invoke-MigrationBundle.ps1')) `
                -Mode AcquireLease `
                -Stage Development `
                -ReleaseVerifiedProvenancePath $releaseFixture.ProvenancePath `
                -ExpectedProtectedBundleSha256 $enabledProtectedBundle.BundleSha256 `
                -ServerName ([string]$targetMetadata.sqlServerFqdn) `
                -DatabaseName ([string]$targetMetadata.sqlDatabaseName) `
                -StageTargetMetadataPath $targetPath `
                -PolicyPath $enabledProtectedBundle.PolicyPath `
                -EvidencePath (Join-Path $temporaryRoot 'unmaterialized-lease.json') `
                -AllowApply `
                -EnableLocalTestSeams `
                -LocalAccessTokenProvider {
                    param($Request)
                    $global:T21UnmaterializedApplyState.tokenCalls++
                    throw 'should-not-run'
                } `
                -LocalSqlExecutor {
                    param($Request)
                    $global:T21UnmaterializedApplyState.sqlCalls++
                    throw 'should-not-run'
                }
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            'T21_MIGRATION_IDENTITY_MODE',
            $priorApplyIdentityMode)
    }
    Assert-SqlClientTest 'unmaterialized-apply-bundle-still-blocks-stage-operations-before-seams' (
        $unmaterializedApplyFailure -ceq
            'The dedicated T18 application migration bundle is not materialized and hash-bound in policy.' -and
        $global:T21UnmaterializedApplyState.tokenCalls -eq 0 -and
        $global:T21UnmaterializedApplyState.sqlCalls -eq 0
    ) "Unmaterialized StageOperations did not fail closed before local seams: $unmaterializedApplyFailure"
    $stalePolicy = Get-Content -LiteralPath $enabledProtectedBundle.PolicyPath -Raw |
        ConvertFrom-Json -DateKind String
    $stalePolicy.t21ProvenanceContract.schemaVersion = '2.0.0'
    $stalePolicyFailure = Get-StableFailure {
        Assert-MigrationVerifiedArtifact `
            -VerifiedProvenancePath $releaseFixture.ProvenancePath `
            -ExpectedRole release-c6 `
            -Stage Development `
            -Policy $stalePolicy
    }
    Assert-SqlClientTest 'migration-provenance-rejects-stale-v20-policy-after-v21-baseline-passes' (
        $stalePolicyFailure.Contains(
            'invalid role, API run, or artifact bindings')
    ) 'Migration provenance still accepts the stale v2.0 policy contract.'

    $policy = Get-Content -LiteralPath (
        Join-Path $RepositoryRoot 'pipelines\config\promotion-policy.json') -Raw |
        ConvertFrom-Json -DateKind String
    $producerBinding = Assert-MigrationProducerRunBinding `
        -Binding ([pscustomobject][ordered]@{
            schemaVersion = '2.0.0'
            topLevelCallerWorkflowRef =
                'syedmh/Dreamer/.github/workflows/operation-evidence-producer.yml@refs/heads/main'
            producerWorkflowRef =
                "syedmh/Dreamer/.github/workflows/trusted-protected-operations.yml@$('c' * 40)"
            runId = '501'
            runAttempt = 1
            commitSha = $producerCommit
        }) `
        -Stage Development `
        -Policy $policy
    $migrationBundleText = Get-Content -LiteralPath (
        Join-Path $RepositoryRoot 'eng\artifact\migrations\bundle\Invoke-MigrationBundle.ps1'
    ) -Raw
    Assert-SqlClientTest 'producer-and-release-commits-remain-distinct-and-independently-bound' (
        [string]$producerBinding.commitSha -ceq $producerCommit -and
        $producerCommit -cne $releaseCommit -and
        $migrationBundleText.Contains(
            '[string]$releaseVerified.provenance.run.commitSha -cne $releaseCommitSha') -and
        -not $migrationBundleText.Contains(
            '[string]$operationRun.commitSha -cne $releaseCommitSha')
    ) 'The producer commit is still forced to equal the immutable release commit or release binding was removed.'

    $commonText = Get-Content -LiteralPath (
        Join-Path $RepositoryRoot 'eng\artifact\migrations\bundle\Migration.Common.ps1') -Raw
    $workflowText = Get-Content -LiteralPath (
        Join-Path $RepositoryRoot 'pipelines\github\trusted-protected-operations.yml') -Raw
    $workflow = $workflowText | ConvertFrom-Json -DateKind String
    $protectedValidationSegments = @(
        $workflow.jobs.'protected-operation'.steps |
            Where-Object { $null -ne $_.PSObject.Properties['run'] } |
            ForEach-Object { ([string]$_.run).Split(';') } |
            Where-Object { $_.Contains('Test-ProtectedExecutionBundle.ps1') }
    )
    $runtimeText = "$commonText`n$migrationBundleText`n$workflowText"
    Assert-SqlClientTest 'runtime-and-workflow-have-no-external-sql-tool-or-install-path' (
        -not $runtimeText.Contains('sqlcmd') -and
        -not $runtimeText.Contains('apt-get') -and
        -not $runtimeText.Contains('dotnet tool') -and
        -not $runtimeText.Contains('dotnet restore') -and
        -not $runtimeText.Contains('docker ') -and
        $workflowText.Contains(
            'Assert-MigrationSqlRuntimeCompatibility -BundleRoot $env:T21_TRUSTED_BUNDLE_ROOT') -and
        $protectedValidationSegments.Count -eq 3 -and
        @($protectedValidationSegments | Where-Object {
            -not $_.Contains('-ExpectedApplicationSha256')
        }).Count -eq 0
    ) 'A legacy SQL client/install path remains or a trusted validation omits the application hash.'

    $sqlCalls = [int]$global:T21SqlClientExecutionState.calls +
        [int]$global:T21SqlClientLeaseState.calls
}
finally {
    Remove-Variable -Scope Global -Name T21SqlClientTokenState -ErrorAction SilentlyContinue
    Remove-Variable -Scope Global -Name T21SqlClientTokenFailureState `
        -ErrorAction SilentlyContinue
    Remove-Variable -Scope Global -Name T21SqlClientExecutionState -ErrorAction SilentlyContinue
    Remove-Variable -Scope Global -Name T21SqlClientLeaseState -ErrorAction SilentlyContinue
    Remove-Variable -Scope Global -Name T21RealProducerSqlState -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Output (
    "SUMMARY migration-sqlclient total=$($passed + $failed) passed=$passed failed=$failed " +
    "tokenOccurrences=$tokenOccurrences seamActionsCalls=$seamActionsCalls sqlCalls=$sqlCalls " +
    'auth=0 deployments=0 database=0 resources=0 installs=0 restores=0')
if ($failed -gt 0) { exit 1 }
