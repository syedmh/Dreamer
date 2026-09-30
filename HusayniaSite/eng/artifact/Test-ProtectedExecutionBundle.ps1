[CmdletBinding(DefaultParameterSetName = 'Archive')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Archive')]
    [string]$BundlePath,
    [Parameter(ParameterSetName = 'Archive')]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ExpectedSha256,
    [Parameter(ParameterSetName = 'Archive')]
    [string]$ExtractTo,
    [Parameter(Mandatory = $true, ParameterSetName = 'Extracted')]
    [string]$ExtractedRoot,
    [Parameter(ParameterSetName = 'Extracted')]
    [switch]$RequireReadOnly,
    [Parameter(Mandatory = $true, ParameterSetName = 'Archive')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Extracted')]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ExpectedApplicationSha256
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$textExtensions = @('.ps1', '.json', '.sql')
$requiredEntries = @(
    'bundle-manifest.json',
    'SHA256SUMS',
    'pipelines/config/protected-operation-policy.json',
    'contracts/pipeline/release-manifest.schema.json',
    'eng/common/Release.Common.ps1',
    'eng/common/Invoke-CheckedScriptChild.ps1',
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
)
$expectedSqlRuntimeAssemblyNames = @(
    'Azure.Core',
    'Azure.Identity',
    'Microsoft.Bcl.AsyncInterfaces',
    'Microsoft.Bcl.Cryptography',
    'Microsoft.Data.SqlClient',
    'Microsoft.Identity.Client',
    'Microsoft.Identity.Client.Extensions.Msal',
    'Microsoft.IdentityModel.Abstractions',
    'Microsoft.IdentityModel.JsonWebTokens',
    'Microsoft.IdentityModel.Logging',
    'Microsoft.IdentityModel.Protocols',
    'Microsoft.IdentityModel.Protocols.OpenIdConnect',
    'Microsoft.IdentityModel.Tokens',
    'Microsoft.SqlServer.Server',
    'System.ClientModel',
    'System.Configuration.ConfigurationManager',
    'System.IdentityModel.Tokens.Jwt',
    'System.Memory.Data',
    'System.Security.Cryptography.ProtectedData'
)

function Assert-SafeBundleEntryName {
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
    $segments = @($Name.Split('/'))
    if ([string]::IsNullOrWhiteSpace($Name) -or
        $Name -cne $normalizedUnicode -or
        $Name.Contains('\') -or
        $Name.Contains(':') -or
        $Name.StartsWith('/') -or
        $Name.EndsWith('/') -or
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
        throw "Protected-execution bundle contains an unsafe, duplicate, or aliased path: $Name"
    }
}

function Assert-CanonicalTextBytes {
    param([byte[]]$Bytes, [string]$RelativePath)

    if ([IO.Path]::GetExtension($RelativePath) -notin $textExtensions) {
        return
    }
    if ($Bytes -contains [byte]13) {
        throw "Protected-execution bundle text contains CR/CRLF bytes: $RelativePath"
    }
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $null = $utf8.GetString($Bytes)
}

function Get-BytesSha256Lower {
    param([byte[]]$Bytes)
    return ([Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($Bytes)
    )).ToLowerInvariant()
}

function Assert-BundleContent {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.Generic.Dictionary[string, byte[]]]$Entries
    )

    foreach ($required in $requiredEntries) {
        if (-not $Entries.ContainsKey($required) -or $Entries[$required].Length -eq 0) {
            throw "Protected-execution bundle is missing a required entry: $required"
        }
    }
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $checksumText = $utf8.GetString($Entries['SHA256SUMS'])
    if ($checksumText.Contains("`r")) {
        throw 'Protected-execution bundle SHA256SUMS must use LF line endings.'
    }
    $checksums = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($line in @($checksumText.Split("`n") | Where-Object { $_.Length -gt 0 })) {
        if ($line -notmatch '^([a-f0-9]{64})  ([A-Za-z0-9][A-Za-z0-9/_.-]*)$' -or
            -not $checksums.TryAdd($Matches[2], $Matches[1])) {
            throw "Protected-execution bundle contains a malformed or duplicate checksum line: $line"
        }
    }
    $expectedChecksumPaths = @($Entries.Keys | Where-Object { $_ -cne 'SHA256SUMS' } | Sort-Object)
    if (@(Compare-Object $expectedChecksumPaths @($checksums.Keys | Sort-Object)).Count -ne 0) {
        throw 'Protected-execution bundle SHA256SUMS does not exactly cover every entry except itself.'
    }
    foreach ($entry in $checksums.GetEnumerator()) {
        if ((Get-BytesSha256Lower -Bytes $Entries[$entry.Key]) -cne $entry.Value) {
            throw "Protected-execution bundle checksum mismatch: $($entry.Key)"
        }
    }

    $manifest = $utf8.GetString($Entries['bundle-manifest.json']) | ConvertFrom-Json
    if (@(Compare-Object @(
        'bundlePath',
        'entrypoints',
        'files',
        'policyPath',
        'schemaVersion',
        'sqlRuntime'
    ) @($manifest.PSObject.Properties.Name | Sort-Object)).Count -ne 0 -or
        [string]$manifest.schemaVersion -cne '1.1.0' -or
        [string]$manifest.bundlePath -cne 'operations/protected-execution-bundle.zip' -or
        [string]$manifest.policyPath -cne 'pipelines/config/protected-operation-policy.json') {
        throw 'Protected-execution bundle manifest root contract is invalid.'
    }
    $expectedEntrypoints = [ordered]@{
        operationEvidence = 'eng/promotion/Invoke-OperationEvidenceProducer.ps1'
        ctoAuthorization = 'eng/promotion/Test-CtoAuthorizationRecord.ps1'
        migrationAuthorization = 'eng/promotion/New-MigrationApplyAuthorization.ps1'
        bundleValidator = 'eng/artifact/Test-ProtectedExecutionBundle.ps1'
        externalTrustMarker = 'eng/promotion/Test-ExternalTrustMarker.ps1'
        producerManifest = 'eng/promotion/New-T21ProducerManifest.ps1'
        provenanceResolver = 'eng/promotion/Resolve-GitHubArtifactProvenance.ps1'
    }
    if (@(Compare-Object @($expectedEntrypoints.Keys | Sort-Object) @($manifest.entrypoints.PSObject.Properties.Name | Sort-Object)).Count -ne 0) {
        throw 'Protected-execution bundle entrypoint set is invalid.'
    }
    foreach ($name in $expectedEntrypoints.Keys) {
        if ([string]$manifest.entrypoints.$name -cne [string]$expectedEntrypoints[$name]) {
            throw "Protected-execution bundle entrypoint changed: $name"
        }
    }

    $sqlRuntime = $manifest.sqlRuntime
    $sqlRuntimeFields = @(
        'assemblies',
        'dependencyManifestSourcePath',
        'entryAssembly',
        'packageId',
        'packageVersion',
        'runtime',
        'schemaVersion',
        'sourceApplicationPath',
        'sourceApplicationSha256',
        'target'
    )
    if ($null -eq $sqlRuntime -or
        @(Compare-Object $sqlRuntimeFields @(
            $sqlRuntime.PSObject.Properties.Name | Sort-Object)).Count -ne 0 -or
        [string]$sqlRuntime.schemaVersion -cne '1.0.0' -or
        [string]$sqlRuntime.sourceApplicationPath -cne 'app/Husaynia.Web.zip' -or
        [string]$sqlRuntime.sourceApplicationSha256 -cne
            $ExpectedApplicationSha256.ToLowerInvariant() -or
        [string]$sqlRuntime.dependencyManifestSourcePath -cne 'Husaynia.Web.deps.json' -or
        [string]$sqlRuntime.packageId -cne 'Microsoft.Data.SqlClient' -or
        [string]$sqlRuntime.packageVersion -cne '6.1.1' -or
        [string]$sqlRuntime.target -cne '.NETCoreApp,Version=v10.0' -or
        [string]$sqlRuntime.runtime -cne 'unix' -or
        [string]$sqlRuntime.entryAssembly -cne
            'runtime/sqlclient/Microsoft.Data.SqlClient.dll') {
        throw 'Protected-execution bundle SQL runtime contract is invalid or not application-bound.'
    }
    $runtimeAssemblyPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $runtimeAssemblyNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $runtimeSourcePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $orderedBundlePaths = [Collections.Generic.List[string]]::new()
    $identityTemporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
        "husaynia-sql-runtime-identities-$([guid]::NewGuid().ToString('N'))")
    try {
        New-Item -ItemType Directory -Path $identityTemporaryRoot -Force | Out-Null
        foreach ($assembly in @($sqlRuntime.assemblies)) {
            $assemblyFields = @(
                'assemblyVersion',
                'bundlePath',
                'dependencyAssetPath',
                'fileVersion',
                'length',
                'managedIdentity',
                'packageKey',
                'runtimeIdentifier',
                'sha256',
                'simpleName',
                'sourcePath'
            )
            $sourcePath = [string]$assembly.sourcePath
            $dependencyAssetPath = [string]$assembly.dependencyAssetPath
            $bundlePath = [string]$assembly.bundlePath
            $simpleName = [string]$assembly.simpleName
            $packageKey = [string]$assembly.packageKey
            $runtimeIdentifier = [string]$assembly.runtimeIdentifier
            $sourceSegments = @($sourcePath.Split('/'))
            $dependencySegments = @($dependencyAssetPath.Split('/'))
            $sourceFileName = [IO.Path]::GetFileName($sourcePath)
            $dependencyFileName = [IO.Path]::GetFileName($dependencyAssetPath)
            $expectedSqlClientAsset =
                'runtimes/unix/lib/net9.0/Microsoft.Data.SqlClient.dll'
            if (@(Compare-Object $assemblyFields @(
                    $assembly.PSObject.Properties.Name | Sort-Object)).Count -ne 0 -or
                [string]::IsNullOrWhiteSpace($sourcePath) -or
                [string]::IsNullOrWhiteSpace($dependencyAssetPath) -or
                $sourcePath.Contains('\') -or
                $dependencyAssetPath.Contains('\') -or
                $sourcePath.Contains(':') -or
                $dependencyAssetPath.Contains(':') -or
                $sourcePath.StartsWith('/') -or
                $dependencyAssetPath.StartsWith('/') -or
                @($sourceSegments + $dependencySegments | Where-Object {
                    [string]::IsNullOrWhiteSpace($_) -or
                    $_ -in @('.', '..') -or
                    $_.EndsWith('.') -or
                    $_.EndsWith(' ')
                }).Count -ne 0 -or
                $sourceFileName -cne "$simpleName.dll" -or
                $dependencyFileName -cne $sourceFileName -or
                $packageKey -notmatch "^$([regex]::Escape($simpleName))/[0-9]+(?:\.[0-9]+){2}(?:[-+][0-9A-Za-z.-]+)?$" -or
                [string]$assembly.assemblyVersion -notmatch '^[0-9]+(?:\.[0-9]+){3}$' -or
                [string]$assembly.fileVersion -notmatch '^[0-9]+(?:\.[0-9]+){3}$' -or
                [string]::IsNullOrWhiteSpace([string]$assembly.managedIdentity) -or
                ($simpleName -ceq 'Microsoft.Data.SqlClient' -and (
                    $packageKey -cne 'Microsoft.Data.SqlClient/6.1.1' -or
                    $dependencyAssetPath -cne $expectedSqlClientAsset -or
                    $sourcePath -cne $expectedSqlClientAsset -or
                    $runtimeIdentifier -cne 'unix'
                )) -or
                ($simpleName -cne 'Microsoft.Data.SqlClient' -and (
                    -not [string]::IsNullOrEmpty($runtimeIdentifier) -or
                    $sourcePath -cne $sourceFileName -or
                    $dependencyAssetPath -notmatch "^lib/[A-Za-z0-9.]+/$([regex]::Escape($sourceFileName))$"
                )) -or
                $bundlePath -cne "runtime/sqlclient/$simpleName.dll" -or
                -not $runtimeSourcePaths.Add($sourcePath) -or
                -not $runtimeAssemblyPaths.Add($bundlePath) -or
                -not $runtimeAssemblyNames.Add($simpleName) -or
                -not $Entries.ContainsKey($bundlePath) -or
                [long]$assembly.length -ne $Entries[$bundlePath].Length -or
                [string]$assembly.sha256 -notmatch '^[a-f0-9]{64}$' -or
                [string]$assembly.sha256 -cne
                    (Get-BytesSha256Lower -Bytes $Entries[$bundlePath])) {
                throw "Protected-execution bundle SQL runtime assembly is invalid: $bundlePath"
            }
            $orderedBundlePaths.Add($bundlePath)
            $identityPath = Join-Path $identityTemporaryRoot $sourceFileName
            [IO.File]::WriteAllBytes($identityPath, $Entries[$bundlePath])
            try {
                $identity = [Reflection.AssemblyName]::GetAssemblyName($identityPath)
            }
            catch {
                throw "Protected-execution bundle SQL runtime entry is not a managed assembly: $bundlePath"
            }
            $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($identityPath).FileVersion
            if ([string]$identity.Name -cne $simpleName -or
                [string]$identity.Version -cne [string]$assembly.assemblyVersion -or
                [string]$fileVersion -cne [string]$assembly.fileVersion -or
                [string]$identity.FullName -cne [string]$assembly.managedIdentity) {
                throw "Protected-execution bundle SQL runtime assembly identity changed: $bundlePath"
            }
        }
    }
    finally {
        Remove-Item -LiteralPath $identityTemporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (@(Compare-Object $expectedSqlRuntimeAssemblyNames @(
            $runtimeAssemblyNames | Sort-Object)).Count -ne 0 -or
        (@($orderedBundlePaths) -join "`n") -cne
            (@($orderedBundlePaths | Sort-Object) -join "`n")) {
        throw 'Protected-execution bundle SQL runtime is not the exact ordinal managed closure.'
    }
    $exactEntries = @($requiredEntries) + @($runtimeAssemblyPaths)
    if (@(Compare-Object ($exactEntries | Sort-Object) @(
            $Entries.Keys | Sort-Object)).Count -ne 0) {
        throw 'Protected-execution bundle entry set is not the exact reviewed closure.'
    }

    $manifestPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in @($manifest.files)) {
        $path = [string]$file.path
        if (@(Compare-Object @('length', 'path', 'sha256') @($file.PSObject.Properties.Name | Sort-Object)).Count -ne 0 -or
            -not $manifestPaths.Add($path) -or
            -not $Entries.ContainsKey($path) -or
            [long]$file.length -ne $Entries[$path].Length -or
            [string]$file.sha256 -cne (Get-BytesSha256Lower -Bytes $Entries[$path])) {
            throw "Protected-execution bundle manifest entry is invalid: $path"
        }
    }
    $expectedManifestPaths = @($Entries.Keys |
        Where-Object { $_ -notin @('bundle-manifest.json', 'SHA256SUMS') } |
        Sort-Object)
    if (@(Compare-Object $expectedManifestPaths @($manifestPaths | Sort-Object)).Count -ne 0) {
        throw 'Protected-execution bundle manifest does not exactly cover its payload.'
    }

    $policy = $utf8.GetString($Entries['pipelines/config/protected-operation-policy.json']) |
        ConvertFrom-Json
    $t21Provenance = $policy.PSObject.Properties['t21ProvenanceContract']
    if ($null -ne $policy.PSObject.Properties['privilegedRepositoryFiles'] -or
        $null -eq $policy.PSObject.Properties['trustedExecutionContract'] -or
        [string]$policy.trustedExecutionContract.schemaVersion -cne '2.0.0' -or
        [string]$policy.trustedExecutionContract.bundlePath -cne
            'operations/protected-execution-bundle.zip' -or
        $null -eq $t21Provenance -or
        [string]$t21Provenance.Value.schemaVersion -cne '2.1.0' -or
        @($t21Provenance.Value.callerMatrix).Count -ne 3 -or
        @($t21Provenance.Value.producerRoles | Where-Object {
            [string]$_.role -ceq 'deployment-evidence' -and [bool]$_.forbidden
        }).Count -ne 1 -or
        @($policy.stages | Where-Object {
            $_.name -eq 'Production' -and $_.deploymentEnabled -eq $false
        }).Count -ne 1) {
        throw 'Protected-operation policy snapshot is not reduced, trust-bound, or Production-disabled.'
    }
}

function Get-ArchiveEntries {
    param([string]$Path)

    $entries = [Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::Ordinal)
    $seenOrdinal = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $seenAliases = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $totalLength = [long]0
        foreach ($entry in $archive.Entries) {
            Assert-SafeBundleEntryName -Name $entry.FullName `
                -SeenOrdinal $seenOrdinal -SeenAliases $seenAliases
            $unixMode = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if ($unixMode -eq 0xA000 -or $entry.Length -gt 16MB) {
                throw "Protected-execution bundle contains a link or oversized entry: $($entry.FullName)"
            }
            $totalLength += $entry.Length
            if ($totalLength -gt 64MB) {
                throw 'Protected-execution bundle uncompressed size exceeds the reviewed limit.'
            }
            $stream = $entry.Open()
            try {
                $memory = [IO.MemoryStream]::new()
                try {
                    $stream.CopyTo($memory)
                    $bytes = $memory.ToArray()
                }
                finally {
                    $memory.Dispose()
                }
            }
            finally {
                $stream.Dispose()
            }
            Assert-CanonicalTextBytes -Bytes $bytes -RelativePath $entry.FullName
            $entries.Add($entry.FullName, $bytes)
        }
    }
    finally {
        $archive.Dispose()
    }
    return ,$entries
}

function Get-ExtractedEntries {
    param([string]$Root, [switch]$CheckReadOnly)

    $resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
    $entries = [Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::Ordinal)
    $seenOrdinal = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $seenAliases = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in Get-ChildItem -LiteralPath $resolvedRoot -Recurse -File -Force) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Extracted protected-execution bundle contains a reparse point: $($file.FullName)"
        }
        $relative = Get-RelativeUnixPath -BasePath $resolvedRoot -Path $file.FullName
        Assert-SafeBundleEntryName -Name $relative `
            -SeenOrdinal $seenOrdinal -SeenAliases $seenAliases
        if ($CheckReadOnly -and -not $file.IsReadOnly) {
            throw "Extracted protected-execution bundle file is writable: $relative"
        }
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        Assert-CanonicalTextBytes -Bytes $bytes -RelativePath $relative
        $entries.Add($relative, $bytes)
    }
    return ,$entries
}

if ($PSCmdlet.ParameterSetName -eq 'Archive') {
    $resolvedBundle = (Resolve-Path -LiteralPath $BundlePath).Path
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and
        (Get-Sha256Lower -Path $resolvedBundle) -cne $ExpectedSha256.ToLowerInvariant()) {
        throw 'Protected-execution bundle does not match the expected immutable SHA-256.'
    }
    $archiveEntries = Get-ArchiveEntries -Path $resolvedBundle
    Assert-BundleContent -Entries $archiveEntries

    if (-not [string]::IsNullOrWhiteSpace($ExtractTo)) {
        if (Test-Path -LiteralPath $ExtractTo) {
            throw 'Protected-execution extraction target must not already exist.'
        }
        New-Item -ItemType Directory -Path $ExtractTo -Force | Out-Null
        $resolvedExtract = (Resolve-Path -LiteralPath $ExtractTo).Path
        try {
            foreach ($entry in $archiveEntries.GetEnumerator()) {
                $destination = Join-Path $resolvedExtract $entry.Key
                $parent = Split-Path -Parent $destination
                New-Item -ItemType Directory -Path $parent -Force | Out-Null
                $stream = [IO.File]::Open(
                    $destination,
                    [IO.FileMode]::CreateNew,
                    [IO.FileAccess]::Write,
                    [IO.FileShare]::None)
                try {
                    $stream.Write($entry.Value, 0, $entry.Value.Length)
                }
                finally {
                    $stream.Dispose()
                }
            }
            foreach ($file in Get-ChildItem -LiteralPath $resolvedExtract -Recurse -File -Force) {
                $file.IsReadOnly = $true
            }
            if (-not $IsWindows) {
                & chmod -R a-w -- $resolvedExtract
                if ($LASTEXITCODE -ne 0) {
                    throw 'Could not make the extracted protected-execution bundle read-only.'
                }
            }
            $extractedEntries = Get-ExtractedEntries -Root $resolvedExtract -CheckReadOnly
            Assert-BundleContent -Entries $extractedEntries
        }
        catch {
            Remove-Item -LiteralPath $resolvedExtract -Recurse -Force -ErrorAction SilentlyContinue
            throw
        }
    }
    Write-Output "PROTECTED-BUNDLE-VERIFY status=PASS sha256=$(Get-Sha256Lower -Path $resolvedBundle) entries=$($archiveEntries.Count)"
    return
}

$extractedEntries = Get-ExtractedEntries -Root $ExtractedRoot -CheckReadOnly:$RequireReadOnly
Assert-BundleContent -Entries $extractedEntries
Write-Output "PROTECTED-BUNDLE-VERIFY status=PASS extracted=$((Resolve-Path -LiteralPath $ExtractedRoot).Path) entries=$($extractedEntries.Count)"
