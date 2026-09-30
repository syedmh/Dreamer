[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{40}$')]
    [string]$CommitSha,
    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,
    [Parameter(Mandatory = $true)]
    [string]$ReportsRoot,
    [string]$BuiltAtUtc = ([DateTimeOffset]::UtcNow.ToString('O')),
    [string]$PublishedAppPath,
    [switch]$Restore,
    [switch]$AuthoringValidation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Resolve-HusayniaRepositoryRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path

if (-not (Test-SafeVersion -Version $Version)) {
    throw "Unsafe artifact version: $Version"
}

$requiredReportDirectories = @('test-results', 'security', 'accessibility')
foreach ($reportDirectory in $requiredReportDirectories) {
    $path = Join-Path $ReportsRoot $reportDirectory
    if (-not (Test-Path -LiteralPath $path -PathType Container) -or
        @(Get-ChildItem -LiteralPath $path -Recurse -File).Count -eq 0) {
        throw "Required real report directory is missing or empty: $path"
    }
}

if ($AuthoringValidation) {
    $accessibilityWiringReport = Join-Path $ReportsRoot 'accessibility\t21-gate-wiring-validation.json'
    if (-not (Test-Path -LiteralPath $accessibilityWiringReport -PathType Leaf)) {
        throw 'Authoring artifact creation requires the real T21 accessibility gate-wiring validation report.'
    }
    $wiring = Get-Content -LiteralPath $accessibilityWiringReport -Raw | ConvertFrom-Json
    if ($wiring.status -ne 'PASS' -or $wiring.scope -ne 'pipeline-wiring-only') {
        throw 'The accessibility authoring report must explicitly prove pipeline wiring only.'
    }
}
else {
    $accessibilityEvidence = Get-ChildItem -LiteralPath (Join-Path $ReportsRoot 'accessibility') -Recurse -File |
        Where-Object { $_.Name -match '^(axe|accessibility).*\.(json|xml|trx)$' }
    if (@($accessibilityEvidence).Count -eq 0) {
        throw 'Release artifact creation requires real accessibility execution output; authoring wiring reports are not release evidence.'
    }
}

$artifactRoot = Join-Path $OutputRoot "husaynia-site-$Version"
New-CleanDirectory -Path $artifactRoot | Out-Null
$ownsTemporaryPublish = [string]::IsNullOrWhiteSpace($PublishedAppPath)
$temporaryPublish = if ($ownsTemporaryPublish) {
    Join-Path ([IO.Path]::GetTempPath()) "husaynia-publish-$([guid]::NewGuid().ToString('N'))"
}
else {
    if (-not $AuthoringValidation) {
        throw 'A prepublished application path is allowed only for explicit authoring validation.'
    }
    (Resolve-Path -LiteralPath $PublishedAppPath).Path
}

try {
    if ($ownsTemporaryPublish) {
        New-Item -ItemType Directory -Path $temporaryPublish -Force | Out-Null
        $publishArguments = @(
            'publish',
            (Join-Path $RepositoryRoot 'src\Husaynia.Web\Husaynia.Web.csproj'),
            '--configuration', 'Release',
            '--output', $temporaryPublish,
            '--nologo',
            '-p:ContinuousIntegrationBuild=true',
            '-p:Deterministic=true',
            "-p:PathMap=$RepositoryRoot=/_/",
            '-p:UseAppHost=false',
            '-p:DebugSymbols=false',
            '-p:DebugType=None',
            "-p:Version=$Version"
        )
        if (-not $Restore) {
            $publishArguments += '--no-restore'
        }
        Invoke-CheckedNative -FilePath 'dotnet' -Arguments $publishArguments -Label 'publish Husaynia.Web'
    }

    $appDirectory = Join-Path $artifactRoot 'app'
    New-Item -ItemType Directory -Path $appDirectory -Force | Out-Null
    New-DeterministicZip -SourceDirectory $temporaryPublish -DestinationPath (Join-Path $appDirectory 'Husaynia.Web.zip')

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'migrations') -Destination $artifactRoot -Recurse
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
    $operationsDirectory = Join-Path $artifactRoot 'operations'
    New-Item -ItemType Directory -Path $operationsDirectory -Force | Out-Null
    Invoke-CheckedScript -Path (Join-Path $PSScriptRoot 'New-ProtectedExecutionBundle.ps1') `
        -Parameters @{
            RepositoryRoot = $RepositoryRoot
            ApplicationArchivePath = (Join-Path $appDirectory 'Husaynia.Web.zip')
            DestinationPath = (Join-Path $operationsDirectory 'protected-execution-bundle.zip')
        } `
        -Label 'create immutable protected-execution bundle'

    $contractsDirectory = Join-Path $artifactRoot 'contracts'
    New-Item -ItemType Directory -Path $contractsDirectory -Force | Out-Null
    $routeManifest = Join-Path $RepositoryRoot 'evidence\baseline\route-manifest.json'
    if (-not (Test-Path -LiteralPath $routeManifest -PathType Leaf)) {
        throw "Required route manifest is missing: $routeManifest"
    }
    Copy-Item -LiteralPath $routeManifest -Destination (Join-Path $contractsDirectory 'route-manifest.json')
    Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'contracts\migration\import-manifest.schema.json') `
        -Destination (Join-Path $contractsDirectory 'import-manifest.schema.json')

    foreach ($reportDirectory in $requiredReportDirectories) {
        $destination = Join-Path $artifactRoot "reports\$reportDirectory"
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -Path (Join-Path $ReportsRoot "$reportDirectory\*") -Destination $destination -Recurse
    }

    $sbomDirectory = Join-Path $artifactRoot 'sbom'
    New-Item -ItemType Directory -Path $sbomDirectory -Force | Out-Null
    Invoke-CheckedScript -Path (Join-Path $PSScriptRoot 'New-SpdxSbom.ps1') `
        -Parameters @{
            RepositoryRoot = $RepositoryRoot
            Version = $Version
            CommitSha = $CommitSha
            OutputPath = (Join-Path $sbomDirectory 'sbom.spdx.json')
            CreatedAtUtc = $BuiltAtUtc
        } `
        -Label 'create SPDX SBOM'

    $releaseDirectory = Join-Path $artifactRoot 'release'
    New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null

    $payloadFiles = Get-ChildItem -LiteralPath $artifactRoot -Recurse -File |
        Where-Object { (Get-RelativeUnixPath -BasePath $artifactRoot -Path $_.FullName) -notlike 'release/*' } |
        Sort-Object { Get-RelativeUnixPath -BasePath $artifactRoot -Path $_.FullName }

    $manifestFiles = foreach ($file in $payloadFiles) {
        [ordered]@{
            path = Get-RelativeUnixPath -BasePath $artifactRoot -Path $file.FullName
            sha256 = Get-Sha256Lower -Path $file.FullName
            length = $file.Length
        }
    }

    $sdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -ne '10.0.400') {
        throw "Expected .NET SDK 10.0.400, received '$sdkVersion'."
    }

    $releaseManifest = [ordered]@{
        schemaVersion = '1.0.0'
        version = $Version
        commitSha = $CommitSha.ToLowerInvariant()
        builtAtUtc = ([DateTimeOffset]::Parse($BuiltAtUtc)).ToUniversalTime().ToString('O')
        dotnetSdk = $sdkVersion
        files = @($manifestFiles)
        databaseCompatibility = 'expand-compatible; explicit-preflight-and-apply; no-runtime-startup-migrations'
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
    }

    $manifestPath = Join-Path $releaseDirectory 'release-manifest.json'
    Write-Utf8Json -Value $releaseManifest -Path $manifestPath -Depth 30

    $checksumFiles = Get-ChildItem -LiteralPath $artifactRoot -Recurse -File |
        Where-Object { (Get-RelativeUnixPath -BasePath $artifactRoot -Path $_.FullName) -ne 'release/SHA256SUMS' } |
        Sort-Object { Get-RelativeUnixPath -BasePath $artifactRoot -Path $_.FullName }
    $checksumLines = foreach ($file in $checksumFiles) {
        "$(Get-Sha256Lower -Path $file.FullName)  $(Get-RelativeUnixPath -BasePath $artifactRoot -Path $file.FullName)"
    }
    [IO.File]::WriteAllLines(
        (Join-Path $releaseDirectory 'SHA256SUMS'),
        $checksumLines,
        [Text.UTF8Encoding]::new($false))

    Invoke-CheckedScript -Path (Join-Path $PSScriptRoot 'Test-ReleaseArtifact.ps1') `
        -Parameters @{ ArtifactRoot = $artifactRoot } `
        -Label 'verify newly created release artifact'

    $appHash = Get-Sha256Lower -Path (Join-Path $appDirectory 'Husaynia.Web.zip')
    $protectedBundleHash = Get-Sha256Lower -Path (
        Join-Path $operationsDirectory 'protected-execution-bundle.zip')
    Write-Output (
        "ARTIFACT path=$artifactRoot appSha256=$appHash " +
        "protectedBundleSha256=$protectedBundleHash files=$($checksumFiles.Count)")
}
finally {
    if ($ownsTemporaryPublish -and (Test-Path -LiteralPath $temporaryPublish)) {
        Remove-Item -LiteralPath $temporaryPublish -Recurse -Force
    }
}
