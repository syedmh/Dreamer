[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [Parameter(Mandatory = $true)]
    [string]$ApplicationArchivePath,
    [Parameter(Mandatory = $true)]
    [string]$DestinationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Resolve-HusayniaRepositoryRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) "husaynia-protected-bundle-$([guid]::NewGuid().ToString('N'))"

function Assert-SafeBundleRelativePath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $normalized = $RelativePath.Replace('\', '/')
    $segments = @($normalized.Split('/'))
    if ([string]::IsNullOrWhiteSpace($normalized) -or
        $normalized -cne $RelativePath -or
        [IO.Path]::IsPathRooted($normalized) -or
        $normalized.Contains(':') -or
        $segments.Count -lt 2 -or
        @($segments | Where-Object {
            [string]::IsNullOrWhiteSpace($_) -or
            $_ -in @('.', '..') -or
            $_.EndsWith('.') -or
            $_.EndsWith(' ')
        }).Count -ne 0) {
        throw "Unsafe protected-execution bundle path: $RelativePath"
    }
}

function Copy-CanonicalBundleFile {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    Assert-SafeBundleRelativePath -RelativePath $RelativePath
    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
        throw "Protected-execution dependency is missing: $SourcePath"
    }
    $destination = Join-Path $stagingRoot $RelativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    if ([IO.Path]::GetExtension($RelativePath) -in @('.ps1', '.json', '.sql')) {
        $utf8 = [Text.UTF8Encoding]::new($false, $true)
        $text = $utf8.GetString([IO.File]::ReadAllBytes($SourcePath))
        $canonical = $text.Replace("`r`n", "`n").Replace("`r", "`n")
        [IO.File]::WriteAllText($destination, $canonical, [Text.UTF8Encoding]::new($false))
    }
    else {
        Copy-Item -LiteralPath $SourcePath -Destination $destination
    }
}

function Write-CanonicalBundleJson {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$Depth = 80
    )

    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    $json = ($Value | ConvertTo-Json -Depth $Depth).
        Replace("`r`n", "`n").
        Replace("`r", "`n")
    [IO.File]::WriteAllText($Path, "$json`n", [Text.UTF8Encoding]::new($false))
}

function Assert-SafeApplicationEntryName {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [Collections.Generic.HashSet[string]]$SeenOrdinal,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [Collections.Generic.HashSet[string]]$SeenAliases
    )

    $normalizedUnicode = $Name.Normalize([Text.NormalizationForm]::FormC)
    $segments = @($Name.TrimEnd('/').Split('/'))
    if ([string]::IsNullOrWhiteSpace($Name) -or
        $Name -cne $normalizedUnicode -or
        $Name.Contains('\') -or
        $Name.Contains(':') -or
        $Name.StartsWith('/') -or
        [IO.Path]::IsPathRooted($Name) -or
        @($segments | Where-Object {
            [string]::IsNullOrWhiteSpace($_) -or
            $_ -in @('.', '..') -or
            $_.EndsWith('.') -or
            $_.EndsWith(' ') -or
            $_ -match '^(?i:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)'
        }).Count -ne 0 -or
        -not $SeenOrdinal.Add($Name) -or
        -not $SeenAliases.Add($normalizedUnicode.ToUpperInvariant())) {
        throw "Application archive contains an unsafe, duplicate, or aliased path: $Name"
    }
}

function Assert-SafeDependencyAssetPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $segments = @($Path.Split('/'))
    if ([string]::IsNullOrWhiteSpace($Path) -or
        $Path.Contains('\') -or
        $Path.Contains(':') -or
        $Path.StartsWith('/') -or
        $Path.Normalize([Text.NormalizationForm]::FormC) -cne $Path -or
        @($segments | Where-Object {
            [string]::IsNullOrWhiteSpace($_) -or
            $_ -in @('.', '..') -or
            $_.EndsWith('.') -or
            $_.EndsWith(' ')
        }).Count -ne 0) {
        throw "SQL runtime dependency manifest contains an unsafe asset path: $Path"
    }
}

function Read-ZipEntryBytes {
    param(
        [Parameter(Mandatory = $true)][IO.Compression.ZipArchiveEntry]$Entry,
        [long]$MaximumLength = 16MB
    )

    if ($Entry.Length -lt 1 -or $Entry.Length -gt $MaximumLength) {
        throw "Application archive SQL runtime entry has an invalid length: $($Entry.FullName)"
    }
    $stream = $Entry.Open()
    try {
        $memory = [IO.MemoryStream]::new()
        try {
            $stream.CopyTo($memory)
            return ,$memory.ToArray()
        }
        finally {
            $memory.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Copy-SqlClientRuntimeClosure {
    param([Parameter(Mandatory = $true)][string]$ArchivePath)

    $resolvedArchive = (Resolve-Path -LiteralPath $ArchivePath).Path
    $applicationSha256 = Get-Sha256Lower -Path $resolvedArchive
    $archive = [IO.Compression.ZipFile]::OpenRead($resolvedArchive)
    try {
        if ($archive.Entries.Count -lt 1 -or $archive.Entries.Count -gt 4096) {
            throw 'Application archive exceeds the reviewed entry-count limit.'
        }
        $entries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new(
            [StringComparer]::Ordinal)
        $seenOrdinal = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $seenAliases = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $totalLength = [long]0
        foreach ($entry in $archive.Entries) {
            Assert-SafeApplicationEntryName -Name $entry.FullName `
                -SeenOrdinal $seenOrdinal -SeenAliases $seenAliases
            $unixMode = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if ($unixMode -eq 0xA000 -or $entry.Length -lt 0 -or $entry.Length -gt 1GB) {
                throw "Application archive contains a link or oversized entry: $($entry.FullName)"
            }
            $totalLength += $entry.Length
            if ($totalLength -gt 2GB) {
                throw 'Application archive exceeds the reviewed uncompressed-size limit.'
            }
            if (-not $entry.FullName.EndsWith('/')) {
                $entries.Add($entry.FullName, $entry)
            }
        }

        $dependencyManifestMatches = @($entries.Keys | Where-Object {
            [IO.Path]::GetFileName($_) -ceq 'Husaynia.Web.deps.json'
        })
        if ($dependencyManifestMatches.Count -ne 1 -or
            $dependencyManifestMatches[0] -cne 'Husaynia.Web.deps.json') {
            throw 'Application archive must contain exactly one root Husaynia.Web.deps.json.'
        }
        $utf8 = [Text.UTF8Encoding]::new($false, $true)
        $dependencyManifestBytes = Read-ZipEntryBytes -Entry $entries['Husaynia.Web.deps.json'] -MaximumLength 4MB
        $dependencyManifest = $utf8.GetString($dependencyManifestBytes) |
            ConvertFrom-Json -DateKind String
        $targetName = '.NETCoreApp,Version=v10.0'
        $targetProperty = $dependencyManifest.targets.PSObject.Properties[$targetName]
        if ([string]$dependencyManifest.runtimeTarget.name -cne $targetName -or
            $null -eq $targetProperty) {
            throw 'Application dependency manifest does not contain the exact .NET 10 runtime target.'
        }
        $target = $targetProperty.Value
        $rootPackageKey = 'Microsoft.Data.SqlClient/6.1.1'
        if ($null -eq $target.PSObject.Properties[$rootPackageKey] -or
            $null -eq $dependencyManifest.libraries.PSObject.Properties[$rootPackageKey]) {
            throw 'Application dependency manifest does not contain Microsoft.Data.SqlClient 6.1.1.'
        }

        $queue = [Collections.Generic.Queue[string]]::new()
        $queue.Enqueue($rootPackageKey)
        $packages = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $selectedAssets = [Collections.Generic.List[object]]::new()
        while ($queue.Count -gt 0) {
            $packageKey = $queue.Dequeue()
            if (-not $packages.Add($packageKey)) {
                continue
            }
            $packageProperty = $target.PSObject.Properties[$packageKey]
            $libraryProperty = $dependencyManifest.libraries.PSObject.Properties[$packageKey]
            if ($null -eq $packageProperty -or $null -eq $libraryProperty -or
                [string]$libraryProperty.Value.type -cne 'package') {
                throw "SQL runtime dependency closure contains a missing or project library: $packageKey"
            }
            $package = $packageProperty.Value
            $dependenciesProperty = $package.PSObject.Properties['dependencies']
            if ($null -ne $dependenciesProperty) {
                foreach ($dependency in $dependenciesProperty.Value.PSObject.Properties) {
                    $queue.Enqueue("$($dependency.Name)/$([string]$dependency.Value)")
                }
            }

            $runtimeTargetsProperty = $package.PSObject.Properties['runtimeTargets']
            $unixRuntimeAssets = @()
            if ($null -ne $runtimeTargetsProperty) {
                $nativeUnixAssets = @($runtimeTargetsProperty.Value.PSObject.Properties | Where-Object {
                    [string]$_.Value.assetType -ceq 'native' -and
                    [string]$_.Value.rid -match '^(?:unix|linux)(?:-|$)'
                })
                if ($nativeUnixAssets.Count -ne 0) {
                    throw "SQL runtime dependency closure contains a native Unix asset: $packageKey"
                }
                $unixRuntimeAssets = @($runtimeTargetsProperty.Value.PSObject.Properties | Where-Object {
                    [string]$_.Value.assetType -ceq 'runtime' -and
                    [string]$_.Value.rid -ceq 'unix'
                })
            }
            if ($packageKey -ceq $rootPackageKey -and $unixRuntimeAssets.Count -ne 1) {
                throw 'Microsoft.Data.SqlClient must select exactly one managed Unix runtime asset.'
            }
            $runtimeAssets = if ($unixRuntimeAssets.Count -gt 0) {
                $unixRuntimeAssets
            }
            else {
                $runtimeProperty = $package.PSObject.Properties['runtime']
                if ($null -eq $runtimeProperty) {
                    @()
                }
                else {
                    @($runtimeProperty.Value.PSObject.Properties)
                }
            }
            foreach ($asset in $runtimeAssets) {
                $assetPath = [string]$asset.Name
                Assert-SafeDependencyAssetPath -Path $assetPath
                if ([IO.Path]::GetExtension($assetPath) -cne '.dll') {
                    throw "SQL runtime dependency closure contains a non-managed runtime asset: $assetPath"
                }
                $runtimeIdentifier = if ($unixRuntimeAssets.Count -gt 0) {
                    [string]$asset.Value.rid
                }
                else {
                    ''
                }
                $expectedAssetFields = if ($runtimeIdentifier -ceq 'unix') {
                    @('assemblyVersion', 'assetType', 'fileVersion', 'rid')
                }
                else {
                    @('assemblyVersion', 'fileVersion')
                }
                if (@(Compare-Object $expectedAssetFields @(
                        $asset.Value.PSObject.Properties.Name | Sort-Object)).Count -ne 0 -or
                    [string]$asset.Value.assemblyVersion -notmatch '^[0-9]+(?:\.[0-9]+){3}$' -or
                    [string]$asset.Value.fileVersion -notmatch '^[0-9]+(?:\.[0-9]+){3}$') {
                    throw "SQL runtime dependency manifest has invalid managed identity metadata: $assetPath"
                }
                $sourcePath = if ($runtimeIdentifier -ceq 'unix') {
                    $assetPath
                }
                else {
                    [IO.Path]::GetFileName($assetPath)
                }
                $selectedAssets.Add([pscustomobject]@{
                    packageKey = $packageKey
                    dependencyAssetPath = $assetPath
                    sourcePath = $sourcePath
                    runtimeIdentifier = $runtimeIdentifier
                    assemblyVersion = [string]$asset.Value.assemblyVersion
                    fileVersion = [string]$asset.Value.fileVersion
                })
            }
        }

        $assemblyNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $bundlePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $assemblies = [Collections.Generic.List[object]]::new()
        foreach ($asset in @($selectedAssets | Sort-Object sourcePath)) {
            $sourcePath = [string]$asset.sourcePath
            $sourceFileName = [IO.Path]::GetFileName($sourcePath)
            if (-not $entries.ContainsKey($sourcePath)) {
                throw "Application archive is missing a SQL runtime assembly selected by the dependency manifest: $sourcePath"
            }
            $bundlePath = "runtime/sqlclient/$sourceFileName"
            if (-not $bundlePaths.Add($bundlePath)) {
                throw "SQL runtime dependency closure contains a duplicate assembly file name: $sourceFileName"
            }
            $destination = Join-Path $stagingRoot $bundlePath
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            $bytes = Read-ZipEntryBytes -Entry $entries[$sourcePath]
            [IO.File]::WriteAllBytes($destination, $bytes)
            try {
                $assemblyName = [Reflection.AssemblyName]::GetAssemblyName($destination)
            }
            catch {
                throw "SQL runtime dependency closure contains an invalid managed assembly: $sourceFileName"
            }
            $simpleName = [string]$assemblyName.Name
            if ([string]::IsNullOrWhiteSpace($simpleName) -or
                $simpleName -cne [IO.Path]::GetFileNameWithoutExtension($sourceFileName) -or
                -not $assemblyNames.Add($simpleName)) {
                throw "SQL runtime dependency closure contains a duplicate or mismatched assembly identity: $sourceFileName"
            }
            $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($destination).FileVersion
            if ([string]$assemblyName.Version -cne [string]$asset.assemblyVersion -or
                [string]$fileVersion -cne [string]$asset.fileVersion) {
                throw "SQL runtime dependency assembly version does not match its dependency manifest: $sourcePath"
            }
            $assemblies.Add([ordered]@{
                packageKey = [string]$asset.packageKey
                dependencyAssetPath = [string]$asset.dependencyAssetPath
                sourcePath = $sourcePath
                runtimeIdentifier = [string]$asset.runtimeIdentifier
                bundlePath = $bundlePath
                simpleName = $simpleName
                assemblyVersion = [string]$assemblyName.Version
                fileVersion = [string]$fileVersion
                managedIdentity = [string]$assemblyName.FullName
                sha256 = Get-Sha256Lower -Path $destination
                length = [long]$bytes.Length
            })
        }
        if (@($assemblies | Where-Object {
            [string]$_.simpleName -ceq 'Microsoft.Data.SqlClient'
        }).Count -ne 1) {
            throw 'SQL runtime dependency closure does not contain exactly one Microsoft.Data.SqlClient entry assembly.'
        }
        return [ordered]@{
            schemaVersion = '1.0.0'
            sourceApplicationPath = 'app/Husaynia.Web.zip'
            sourceApplicationSha256 = $applicationSha256
            dependencyManifestSourcePath = 'Husaynia.Web.deps.json'
            packageId = 'Microsoft.Data.SqlClient'
            packageVersion = '6.1.1'
            target = $targetName
            runtime = 'unix'
            entryAssembly = 'runtime/sqlclient/Microsoft.Data.SqlClient.dll'
            assemblies = @($assemblies | Sort-Object { [string]$_.bundlePath })
        }
    }
    finally {
        $archive.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    $sourceFiles = [Collections.Generic.List[string]]::new()
    foreach ($relativePath in @(
        'eng/common/Invoke-CheckedScriptChild.ps1',
        'eng/common/Release.Common.ps1',
        'eng/artifact/Resolve-ReleaseArtifactRoot.ps1',
        'eng/artifact/Test-ProtectedExecutionBundle.ps1',
        'eng/artifact/Test-ReleaseArtifact.ps1',
        'eng/promotion/Get-GitHubRunMetadata.ps1',
        'eng/promotion/Invoke-AutomaticStagingPromotion.ps1',
        'eng/promotion/Invoke-OperationEvidenceProducer.ps1',
        'eng/promotion/Invoke-ProtectedOperationHook.ps1',
        'eng/promotion/New-CtoAuthorizationRecord.ps1',
        'eng/promotion/New-MigrationApplyAuthorization.ps1',
        'eng/promotion/New-T21ProducerManifest.ps1',
        'eng/promotion/Resolve-GitHubArtifactProvenance.ps1',
        'eng/promotion/New-StageOperationInputBundle.ps1',
        'eng/promotion/New-StageOperationReport.ps1',
        'eng/promotion/StageOperationInput.Common.ps1',
        'eng/promotion/StageTarget.Common.ps1',
        'eng/promotion/Test-ExternalTrustMarker.ps1',
        'eng/promotion/Test-CtoAuthorizationRecord.ps1',
        'eng/promotion/Test-StageEvidenceBundle.ps1',
        'eng/promotion/Test-StageOperationInputBundle.ps1',
        'eng/promotion/Test-StageReadOnlyConfiguration.ps1',
        'eng/promotion/Test-TrustedProtectedOperationInputs.ps1',
        'contracts/pipeline/release-manifest.schema.json',
        'eng/artifact/migrations/bundle/Invoke-MigrationBundle.ps1',
        'eng/artifact/migrations/bundle/Migration.Common.ps1',
        'eng/artifact/migrations/sql/000-preflight.sql',
        'eng/promotion/hooks/Invoke-Accessibility.ps1',
        'eng/promotion/hooks/Invoke-Backup.ps1',
        'eng/promotion/hooks/Invoke-ChangeRecord.ps1',
        'eng/promotion/hooks/Invoke-Configuration.ps1',
        'eng/promotion/hooks/Invoke-Crawl.ps1',
        'eng/promotion/hooks/Invoke-Health.ps1',
        'eng/promotion/hooks/Invoke-Performance.ps1',
        'eng/promotion/hooks/Invoke-Restore.ps1',
        'eng/promotion/hooks/Invoke-Rollback.ps1',
        'eng/promotion/hooks/Invoke-SandboxIntegrations.ps1',
        'eng/promotion/hooks/Invoke-Smoke.ps1',
        'eng/promotion/hooks/Invoke-Visual.ps1',
        'eng/promotion/hooks/OperationHook.Common.ps1'
    )) {
        $sourceFiles.Add($relativePath)
    }

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($relativePath in @($sourceFiles | Sort-Object -Unique)) {
        if (-not $seen.Add($relativePath.Normalize([Text.NormalizationForm]::FormC))) {
            throw "Protected-execution dependency has an archive alias: $relativePath"
        }
        Copy-CanonicalBundleFile `
            -SourcePath (Join-Path $RepositoryRoot $relativePath) `
            -RelativePath $relativePath
    }

    $policyPath = Join-Path $RepositoryRoot 'pipelines\config\promotion-policy.json'
    $protectedPolicy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
    $protectedPolicy.PSObject.Properties.Remove('privilegedRepositoryFiles')
    $protectedPolicyPath = Join-Path $stagingRoot 'pipelines/config/protected-operation-policy.json'
    Write-CanonicalBundleJson -Value $protectedPolicy -Path $protectedPolicyPath -Depth 80

    $sqlRuntime = Copy-SqlClientRuntimeClosure -ArchivePath $ApplicationArchivePath
    $payloadFiles = Get-ChildItem -LiteralPath $stagingRoot -Recurse -File |
        Sort-Object { Get-RelativeUnixPath -BasePath $stagingRoot -Path $_.FullName }
    $manifestFiles = foreach ($file in $payloadFiles) {
        [ordered]@{
            path = Get-RelativeUnixPath -BasePath $stagingRoot -Path $file.FullName
            sha256 = Get-Sha256Lower -Path $file.FullName
            length = $file.Length
        }
    }
    $manifest = [ordered]@{
        schemaVersion = '1.1.0'
        bundlePath = 'operations/protected-execution-bundle.zip'
        policyPath = 'pipelines/config/protected-operation-policy.json'
        entrypoints = [ordered]@{
            operationEvidence = 'eng/promotion/Invoke-OperationEvidenceProducer.ps1'
            ctoAuthorization = 'eng/promotion/Test-CtoAuthorizationRecord.ps1'
            migrationAuthorization = 'eng/promotion/New-MigrationApplyAuthorization.ps1'
            bundleValidator = 'eng/artifact/Test-ProtectedExecutionBundle.ps1'
            externalTrustMarker = 'eng/promotion/Test-ExternalTrustMarker.ps1'
            producerManifest = 'eng/promotion/New-T21ProducerManifest.ps1'
            provenanceResolver = 'eng/promotion/Resolve-GitHubArtifactProvenance.ps1'
        }
        sqlRuntime = $sqlRuntime
        files = @($manifestFiles)
    }
    $manifestPath = Join-Path $stagingRoot 'bundle-manifest.json'
    Write-CanonicalBundleJson -Value $manifest -Path $manifestPath -Depth 40

    $checksumFiles = Get-ChildItem -LiteralPath $stagingRoot -Recurse -File |
        Sort-Object { Get-RelativeUnixPath -BasePath $stagingRoot -Path $_.FullName }
    $checksumLines = foreach ($file in $checksumFiles) {
        "$(Get-Sha256Lower -Path $file.FullName)  $(Get-RelativeUnixPath -BasePath $stagingRoot -Path $file.FullName)"
    }
    [IO.File]::WriteAllText(
        (Join-Path $stagingRoot 'SHA256SUMS'),
        (($checksumLines -join "`n") + "`n"),
        [Text.UTF8Encoding]::new($false))

    New-DeterministicZip -SourceDirectory $stagingRoot -DestinationPath $DestinationPath
    $bundleSha256 = Get-Sha256Lower -Path $DestinationPath
    Invoke-CheckedScript -Path (Join-Path $PSScriptRoot 'Test-ProtectedExecutionBundle.ps1') `
        -Parameters @{
            BundlePath = $DestinationPath
            ExpectedSha256 = $bundleSha256
            ExpectedApplicationSha256 = [string]$sqlRuntime.sourceApplicationSha256
        } `
        -Label 'verify newly created protected-execution bundle'
    Write-Output "PROTECTED-BUNDLE path=$DestinationPath sha256=$bundleSha256 files=$($checksumFiles.Count + 1)"
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
