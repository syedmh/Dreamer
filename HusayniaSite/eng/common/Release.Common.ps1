Set-StrictMode -Version Latest

function Resolve-HusayniaRepositoryRoot {
    param([string]$StartPath = $PSScriptRoot)

    $trustedBundleRoot = [Environment]::GetEnvironmentVariable('T21_TRUSTED_BUNDLE_ROOT')
    if (-not [string]::IsNullOrWhiteSpace($trustedBundleRoot)) {
        $resolvedBundleRoot = (Resolve-Path -LiteralPath $trustedBundleRoot).Path
        if (-not (Test-Path -LiteralPath (Join-Path $resolvedBundleRoot 'bundle-manifest.json') -PathType Leaf) -or
            -not (Test-Path -LiteralPath (
                Join-Path $resolvedBundleRoot 'pipelines\config\protected-operation-policy.json'
            ) -PathType Leaf)) {
            throw 'T21_TRUSTED_BUNDLE_ROOT is not a validated protected-execution bundle.'
        }
        return $resolvedBundleRoot
    }

    $explicitRepositoryRoot = [Environment]::GetEnvironmentVariable('HUSAYNIA_REPOSITORY_ROOT')
    if (-not [string]::IsNullOrWhiteSpace($explicitRepositoryRoot)) {
        $workspaceRoot = [Environment]::GetEnvironmentVariable('GITHUB_WORKSPACE')
        if ([string]::IsNullOrWhiteSpace($workspaceRoot)) {
            throw 'HUSAYNIA_REPOSITORY_ROOT requires GITHUB_WORKSPACE.'
        }
        $resolvedWorkspaceRoot = (Resolve-Path -LiteralPath $workspaceRoot).Path
        $resolvedRepositoryRoot = (Resolve-Path -LiteralPath $explicitRepositoryRoot).Path
        $expectedRepositoryRoot = (Join-Path $resolvedWorkspaceRoot 'HusayniaSite')
        $comparison = if ($IsWindows) {
            [StringComparison]::OrdinalIgnoreCase
        }
        else {
            [StringComparison]::Ordinal
        }
        if (-not $resolvedRepositoryRoot.Equals($expectedRepositoryRoot, $comparison) -or
            -not (Test-PathWithinDirectory -BasePath $resolvedWorkspaceRoot -Path $resolvedRepositoryRoot) -or
            -not (Test-Path -LiteralPath (Join-Path $resolvedRepositoryRoot 'HusayniaSite.sln') -PathType Leaf) -or
            -not (Test-Path -LiteralPath (Join-Path $resolvedRepositoryRoot 'eng') -PathType Container) -or
            -not (Test-Path -LiteralPath (Join-Path $resolvedRepositoryRoot 'pipelines') -PathType Container)) {
            throw 'HUSAYNIA_REPOSITORY_ROOT is not the exact validated Dreamer/HusayniaSite checkout child.'
        }
        return $resolvedRepositoryRoot
    }

    $current = [IO.DirectoryInfo](Resolve-Path -LiteralPath $StartPath).Path
    while ($null -ne $current -and -not (Test-Path -LiteralPath (Join-Path $current.FullName 'HusayniaSite.sln'))) {
        $current = $current.Parent
    }

    if ($null -eq $current) {
        throw 'Could not locate HusayniaSite.sln.'
    }

    return $current.FullName
}

function New-CleanDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    return (Resolve-Path -LiteralPath $Path).Path
}

function Write-Utf8Json {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$Depth = 20
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    $json = $Value | ConvertTo-Json -Depth $Depth
    [IO.File]::WriteAllText($Path, "$json`n", [Text.UTF8Encoding]::new($false))
}

function Get-Sha256Lower {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-CanonicalTextSha256Lower {
    param([Parameter(Mandatory = $true)][string]$Path)

    $bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path).Path)
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $text = $utf8.GetString($bytes).Replace("`r`n", "`n").Replace("`r", "`n")
    $canonicalBytes = $utf8.GetBytes($text)
    return ([Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($canonicalBytes)
    )).ToLowerInvariant()
}

function Assert-CanonicalScriptIntegrityPolicy {
    param([Parameter(Mandatory = $true)]$Policy)

    $integrity = $Policy.PSObject.Properties['scriptIntegrity']
    if ($null -eq $integrity -or
        [string]$integrity.Value.algorithm -cne 'sha256-utf8-lf-v1' -or
        [string]$integrity.Value.encoding -cne 'utf-8' -or
        [string]$integrity.Value.lineEndings -cne 'lf') {
        throw 'Script integrity policy must use the reviewed UTF-8/LF canonical SHA-256 contract.'
    }
}

function Test-PathWithinDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $base = (Resolve-Path -LiteralPath $BasePath).Path
    $candidate = (Resolve-Path -LiteralPath $Path).Path
    $comparison = if ($IsWindows) {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    $basePrefix = $base.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return $candidate.Equals($base, $comparison) -or
        $candidate.StartsWith($basePrefix, $comparison)
}

function Add-ReviewedRepositoryFileEntry {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.Generic.Dictionary[string, string]]$Map,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Sha256,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $normalizedRelativePath = $RelativePath.Replace('\', '/').Trim()
    if ([string]::IsNullOrWhiteSpace($normalizedRelativePath) -or
        [IO.Path]::IsPathRooted($normalizedRelativePath) -or
        $normalizedRelativePath.StartsWith('./') -or
        $normalizedRelativePath.StartsWith('../') -or
        $normalizedRelativePath.Contains('/../') -or
        $normalizedRelativePath.Contains('\') -or
        $normalizedRelativePath -notmatch '^[A-Za-z0-9][A-Za-z0-9/_.-]{0,255}\.ps1$' -or
        $Sha256 -notmatch '^[a-f0-9]{64}$') {
        throw "$Label is unsafe, malformed, or not canonically hash-bound."
    }
    if ($Map.ContainsKey($normalizedRelativePath)) {
        if ($Map[$normalizedRelativePath] -cne $Sha256) {
            throw "Reviewed repository file policy contains conflicting checksums for $normalizedRelativePath."
        }
        return
    }
    $Map.Add($normalizedRelativePath, $Sha256)
}

function Get-ReviewedRepositoryFileMap {
    param(
        [Parameter(Mandatory = $true)]$Policy,
        [switch]$IncludePrivilegedFiles,
        [switch]$IncludeProtectedHooks,
        [switch]$IncludeMigrationOrchestrationFiles
    )

    Assert-CanonicalScriptIntegrityPolicy -Policy $Policy
    $reviewedFiles = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)

    if ($IncludePrivilegedFiles) {
        foreach ($entry in @($Policy.privilegedRepositoryFiles)) {
            if ($null -eq $entry -or
                @(Compare-Object @('relativePath', 'sha256') @($entry.PSObject.Properties.Name)).Count -ne 0) {
                throw 'Privileged repository file policy fields do not exactly match the reviewed contract.'
            }
            Add-ReviewedRepositoryFileEntry -Map $reviewedFiles `
                -RelativePath ([string]$entry.relativePath) `
                -Sha256 ([string]$entry.sha256) `
                -Label 'Privileged repository file policy entry'
        }
    }

    if ($IncludeProtectedHooks) {
        $hookRootRelativePath = [string]$Policy.protectedHookRootRelativePath
        if ($hookRootRelativePath -cne 'eng/promotion/hooks' -or
            [IO.Path]::IsPathRooted($hookRootRelativePath) -or
            $hookRootRelativePath.Contains('..')) {
            throw 'Protected hook root is not the reviewed repository-owned path.'
        }
        foreach ($entry in @($Policy.protectedHookSupportFiles)) {
            if ($null -eq $entry -or
                @(Compare-Object @('relativePath', 'sha256') @($entry.PSObject.Properties.Name)).Count -ne 0) {
                throw 'Protected hook support-file policy fields do not exactly match the reviewed contract.'
            }
            Add-ReviewedRepositoryFileEntry -Map $reviewedFiles `
                -RelativePath "$hookRootRelativePath/$([string]$entry.relativePath)" `
                -Sha256 ([string]$entry.sha256) `
                -Label 'Protected hook support-file policy entry'
        }
        foreach ($stage in @($Policy.stages)) {
            foreach ($entry in @($stage.operationHooks)) {
                if ($null -eq $entry -or
                    @(Compare-Object @('evidenceType', 'relativePath', 'sha256') @($entry.PSObject.Properties.Name)).Count -ne 0) {
                    throw 'Protected operation hook policy fields do not exactly match the reviewed contract.'
                }
                Add-ReviewedRepositoryFileEntry -Map $reviewedFiles `
                    -RelativePath "$hookRootRelativePath/$([string]$entry.relativePath)" `
                    -Sha256 ([string]$entry.sha256) `
                    -Label 'Protected operation hook policy entry'
            }
        }
    }

    if ($IncludeMigrationOrchestrationFiles) {
        foreach ($entry in @($Policy.migration.orchestrationFiles)) {
            if ($null -eq $entry -or
                @(Compare-Object @('relativePath', 'sha256') @($entry.PSObject.Properties.Name)).Count -ne 0) {
                throw 'Migration orchestration-file policy fields do not exactly match the reviewed contract.'
            }
            Add-ReviewedRepositoryFileEntry -Map $reviewedFiles `
                -RelativePath "eng/artifact/$([string]$entry.relativePath)" `
                -Sha256 ([string]$entry.sha256) `
                -Label 'Migration orchestration-file policy entry'
        }
    }

    return ,$reviewedFiles
}

function Assert-ReviewedRepositoryFileClosure {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]$Policy,
        [switch]$IncludeProtectedHooks,
        [switch]$IncludeMigrationOrchestrationFiles
    )

    $repository = (Resolve-Path -LiteralPath $RepositoryRoot).Path
    $reviewedFiles = Get-ReviewedRepositoryFileMap -Policy $Policy `
        -IncludePrivilegedFiles `
        -IncludeProtectedHooks:$IncludeProtectedHooks `
        -IncludeMigrationOrchestrationFiles:$IncludeMigrationOrchestrationFiles
    if ($reviewedFiles.Count -eq 0) {
        throw 'Reviewed repository file closure is empty.'
    }

    foreach ($relativePath in @($reviewedFiles.Keys | Sort-Object)) {
        $candidate = Join-Path $repository ($relativePath.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "Reviewed repository file is missing: $relativePath"
        }
        $resolved = (Resolve-Path -LiteralPath $candidate).Path
        if (-not (Test-PathWithinDirectory -BasePath $repository -Path $resolved)) {
            throw "Reviewed repository file escaped the checkout root: $relativePath"
        }
        if ((Get-CanonicalTextSha256Lower -Path $resolved) -cne $reviewedFiles[$relativePath]) {
            throw "Reviewed repository file checksum changed: $relativePath"
        }
    }

    return ,$reviewedFiles
}

function Assert-ReviewedRepositoryScriptPath {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]$Policy,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $repository = (Resolve-Path -LiteralPath $RepositoryRoot).Path
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if (-not (Test-PathWithinDirectory -BasePath $repository -Path $resolved)) {
        throw 'Reviewed repository script path escaped the checkout root.'
    }

    $reviewedFiles = Get-ReviewedRepositoryFileMap -Policy $Policy `
        -IncludePrivilegedFiles `
        -IncludeProtectedHooks `
        -IncludeMigrationOrchestrationFiles
    $relativePath = Get-RelativeUnixPath -BasePath $repository -Path $resolved
    if (-not $reviewedFiles.ContainsKey($relativePath)) {
        throw "Repository script is not in the reviewed post-login execution closure: $relativePath"
    }
    if ((Get-CanonicalTextSha256Lower -Path $resolved) -cne $reviewedFiles[$relativePath]) {
        throw "Repository script checksum changed after closure validation: $relativePath"
    }
    return $relativePath
}

function Assert-StageDeploymentEnabled {
    param(
        [Parameter(Mandatory = $true)]$Policy,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Development', 'Staging', 'Production')]
        [string]$Stage,
        [string]$Operation = 'Protected stage operation'
    )

    $stagePolicy = @($Policy.stages | Where-Object { $_.name -ceq $Stage })
    if ($stagePolicy.Count -ne 1) {
        throw "Promotion policy must contain exactly one $Stage stage."
    }
    if ([bool]$stagePolicy[0].deploymentEnabled -ne $true) {
        throw "$Operation denied before authentication, authorization, mutation, evidence, or receipt: $Stage deploymentEnabled=false."
    }
    return $stagePolicy[0]
}

function Assert-DeploymentEvidence {
    param(
        [Parameter(Mandatory = $true)]$Policy,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Development', 'Staging', 'Production')]
        [string]$Stage
    )

    # T21-R4 deliberately has no authenticated deployment-evidence producer.
    # This boundary is intentionally before any candidate path is inspected.
    throw 'T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN'

    $contract = $Policy.deploymentContract
    $contractProperties = @($contract.PSObject.Properties.Name | Sort-Object)
    if (@(Compare-Object $contractProperties @(
        'packageAndConfigurationHookRequired',
        'postDeploymentEvidenceRequiresRunningVersion',
        'runningVersionChecksumRequired',
        'schemaVersion'
    )).Count -ne 0 -or
        [string]$contract.schemaVersion -cne '1.0.0' -or
        $contract.packageAndConfigurationHookRequired -ne $true -or
        $contract.runningVersionChecksumRequired -ne $true -or
        $contract.postDeploymentEvidenceRequiresRunningVersion -ne $true -or
        -not (Test-Path -LiteralPath $DeploymentEvidencePath -PathType Leaf)) {
        throw 'Deployment evidence contract is missing or not the reviewed package/configuration and running-version contract.'
    }

    $manifest = Get-Content -LiteralPath $ReleaseManifestPath -Raw | ConvertFrom-Json
    $manifestSha256 = Get-Sha256Lower -Path $ReleaseManifestPath
    $evidence = Get-Content -LiteralPath $DeploymentEvidencePath -Raw | ConvertFrom-Json
    $expectedEvidenceProperties = @(
        'artifactSha256',
        'evidenceType',
        'observedAtUtc',
        'operation',
        'releaseCommitSha',
        'releaseManifestSha256',
        'schemaVersion',
        'stage',
        'status',
        'trustedExecution'
    )
    $expectedOperationProperties = @(
        'completedAtUtc',
        'deploymentExecuted',
        'healthApplicationSha256',
        'healthVerified',
        'packageAndConfigurationApplied',
        'runningApplicationSha256',
        'runningVersionChecksumVerified'
    )
    if (@(Compare-Object @($evidence.PSObject.Properties.Name | Sort-Object) $expectedEvidenceProperties).Count -ne 0 -or
        @(Compare-Object @($evidence.operation.PSObject.Properties.Name | Sort-Object) $expectedOperationProperties).Count -ne 0) {
        throw 'Deployment evidence fields do not exactly match the reviewed contract.'
    }
    $observed = [DateTimeOffset]::MinValue
    $completed = [DateTimeOffset]::MinValue
    if ([string]$evidence.schemaVersion -cne '1.0.0' -or
        [string]$evidence.evidenceType -cne 'deployment' -or
        [string]$evidence.status -cne 'PASS' -or
        [string]$evidence.stage -cne $Stage -or
        [string]$evidence.artifactSha256 -cne $ExpectedAppSha256 -or
        [string]$evidence.releaseManifestSha256 -cne $manifestSha256 -or
        [string]$evidence.releaseCommitSha -cne ([string]$manifest.commitSha).ToLowerInvariant() -or
        $evidence.operation.packageAndConfigurationApplied -ne $true -or
        $evidence.operation.deploymentExecuted -ne $true -or
        $evidence.operation.runningVersionChecksumVerified -ne $true -or
        [string]$evidence.operation.runningApplicationSha256 -cne $ExpectedAppSha256 -or
        $evidence.operation.healthVerified -ne $true -or
        [string]$evidence.operation.healthApplicationSha256 -cne $ExpectedAppSha256 -or
        -not [DateTimeOffset]::TryParse([string]$evidence.observedAtUtc, [ref]$observed) -or
        -not [DateTimeOffset]::TryParse([string]$evidence.operation.completedAtUtc, [ref]$completed) -or
        $observed -lt $completed) {
        throw 'Deployment evidence does not prove package/configuration application and checksum-bound running health.'
    }
    $null = Assert-TrustedExecutionEvidence `
        -TrustedExecution $evidence.trustedExecution `
        -Policy $Policy `
        -ReleaseManifest $manifest
    return $evidence
}

function Get-RelativeUnixPath {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$Path
    )

    return [IO.Path]::GetRelativePath($BasePath, $Path).Replace('\', '/')
}

function Test-SafeVersion {
    param([Parameter(Mandatory = $true)][string]$Version)

    return $Version -match '^[0-9A-Za-z][0-9A-Za-z._-]{0,127}$'
}

function Resolve-TrustedReleaseArtifactRoot {
    param(
        [Parameter(Mandatory = $true)][string]$DownloadRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactLeaf,
        [Parameter(Mandatory = $true)][string]$ReleaseArtifactName,
        [Parameter(Mandatory = $true)][string]$ExpectedAppSha256,
        [Parameter(Mandatory = $true)][string]$ReleaseRunMetadataPath,
        [string]$PolicyPath
    )

    if ([IO.Path]::IsPathRooted($ArtifactLeaf) -or
        $ArtifactLeaf.Contains('..') -or
        $ArtifactLeaf.Contains('/') -or
        $ArtifactLeaf.Contains('\') -or
        $ArtifactLeaf -notmatch '^husaynia-site-([0-9A-Za-z][0-9A-Za-z._-]{0,127})$') {
        throw 'Artifact root input must be one safe husaynia-site-{version} leaf.'
    }
    $version = $Matches[1]
    if (-not (Test-SafeVersion -Version $version) -or $ReleaseArtifactName -cne $ArtifactLeaf) {
        throw 'Release artifact name, leaf, and safe version must match exactly.'
    }
    if (-not (Test-Sha256 -Value $ExpectedAppSha256)) {
        throw 'Expected application SHA-256 is invalid.'
    }

    $download = (Resolve-Path -LiteralPath $DownloadRoot).Path
    $downloadItem = Get-Item -LiteralPath $download
    if (-not $downloadItem.PSIsContainer -or
        ($downloadItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Release download root must be a real directory, not a reparse point.'
    }

    $candidate = Join-Path $download $ArtifactLeaf
    $artifact = (Resolve-Path -LiteralPath $candidate).Path
    $artifactItem = Get-Item -LiteralPath $artifact
    $comparison = if ($IsWindows) {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    $downloadPrefix = $download.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $artifactItem.PSIsContainer -or
        ($artifactItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not $artifact.StartsWith($downloadPrefix, $comparison) -or
        (Split-Path -Parent $artifact) -cne $download -or
        (Split-Path -Leaf $artifact) -cne $ArtifactLeaf) {
        throw 'Resolved release artifact must remain the selected direct child of the download root.'
    }
    $downloadDirectories = @(Get-ChildItem -LiteralPath $download -Directory -Force)
    if ($downloadDirectories.Count -ne 1 -or $downloadDirectories[0].Name -cne $ArtifactLeaf) {
        throw 'Release download must contain exactly the selected artifact directory.'
    }

    $manifestPath = Join-Path $artifact 'release\release-manifest.json'
    $appPath = Join-Path $artifact 'app\Husaynia.Web.zip'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $appPath -PathType Leaf)) {
        throw 'Release manifest or immutable application archive is missing.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $actualAppSha256 = Get-Sha256Lower -Path $appPath
    $appEntry = @($manifest.files | Where-Object { $_.path -ceq 'app/Husaynia.Web.zip' })
    if ([string]$manifest.version -cne $version -or
        -not (Test-CommitSha -Value ([string]$manifest.commitSha)) -or
        $actualAppSha256 -cne $ExpectedAppSha256.ToLowerInvariant() -or
        $appEntry.Count -ne 1 -or
        [string]$appEntry[0].sha256 -cne $actualAppSha256) {
        throw 'Artifact leaf, release manifest, and immutable application identity do not match.'
    }

    $repositoryRoot = Resolve-HusayniaRepositoryRoot
    if ([string]::IsNullOrWhiteSpace($PolicyPath)) {
        $PolicyPath = Join-Path $repositoryRoot 'pipelines\config\promotion-policy.json'
    }
    $policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
    $runMetadata = Get-Content -LiteralPath $ReleaseRunMetadataPath -Raw | ConvertFrom-Json -DateKind String
    Assert-TrustedGitHubRunMetadata -Metadata $runMetadata -Provenance $policy.provenance `
        -AllowedWorkflowPaths @([string]$policy.provenance.releaseWorkflowPath) `
        -ExpectedCommitSha ([string]$manifest.commitSha) -RequireSuccess

    return $artifact
}

function New-DeterministicZip {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $source = (Resolve-Path -LiteralPath $SourceDirectory).Path
    $destinationParent = Split-Path -Parent $DestinationPath
    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }

    $stream = [IO.File]::Open($DestinationPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            $files = Get-ChildItem -LiteralPath $source -Recurse -File | Sort-Object {
                Get-RelativeUnixPath -BasePath $source -Path $_.FullName
            }

            foreach ($file in $files) {
                if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Refusing to archive reparse point: $($file.FullName)"
                }

                $entryName = Get-RelativeUnixPath -BasePath $source -Path $file.FullName
                $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::Parse('2000-01-01T00:00:00Z')
                $entryStream = $entry.Open()
                try {
                    $inputStream = [IO.File]::OpenRead($file.FullName)
                    try {
                        $inputStream.CopyTo($entryStream)
                    }
                    finally {
                        $inputStream.Dispose()
                    }
                }
                finally {
                    $entryStream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string]$Label = $FilePath
    )

    Write-Output "RUN   $Label"
    & $FilePath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$Label failed with exit code $exitCode."
    }
}

function Invoke-CheckedScript {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [hashtable]$Parameters = @{},
        [int[]]$SuccessExitCodes = @(0),
        [string]$Label = $Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Script is missing: $Path"
    }

    Write-Output "RUN   $Label"
    $parameterPath = Join-Path ([IO.Path]::GetTempPath()) "husaynia-script-parameters-$([guid]::NewGuid().ToString('N')).clixml"
    try {
        $Parameters | Export-Clixml -LiteralPath $parameterPath -Depth 20
        $pwsh = (Get-Process -Id $PID).Path
        $childRunner = Join-Path $PSScriptRoot 'Invoke-CheckedScriptChild.ps1'
        if ([Environment]::GetEnvironmentVariable('T21_PRIVILEGED_REPOSITORY_EXECUTION_REQUIRED') -ceq 'true') {
            $policyPath = [Environment]::GetEnvironmentVariable('T21_POLICY_PATH')
            if ([string]::IsNullOrWhiteSpace($policyPath) -or
                -not (Test-Path -LiteralPath $policyPath -PathType Leaf)) {
                throw 'Post-login repository script execution requires a reviewed policy path in T21_POLICY_PATH.'
            }
            $policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
            $repositoryRoot = Resolve-HusayniaRepositoryRoot
            foreach ($reviewedPath in @($Path, $childRunner)) {
                if (Test-PathWithinDirectory -BasePath $repositoryRoot -Path $reviewedPath) {
                    $null = Assert-ReviewedRepositoryScriptPath `
                        -RepositoryRoot $repositoryRoot `
                        -Policy $policy `
                        -Path $reviewedPath
                }
            }
        }
        & $pwsh -NoLogo -NoProfile -NonInteractive -File $childRunner `
            -TargetPath $Path `
            -ParametersPath $parameterPath
        $exitCode = $LASTEXITCODE
        if ($exitCode -notin $SuccessExitCodes) {
            throw "$Label failed with exit code $exitCode."
        }
    }
    finally {
        if (Test-Path -LiteralPath $parameterPath) {
            Remove-Item -LiteralPath $parameterPath -Force
        }
    }
}

function Test-Sha256 {
    param([AllowNull()][string]$Value)

    return -not [string]::IsNullOrWhiteSpace($Value) -and $Value -match '^[a-fA-F0-9]{64}$'
}

function Test-CommitSha {
    param([AllowNull()][string]$Value)

    return -not [string]::IsNullOrWhiteSpace($Value) -and $Value -match '^[a-fA-F0-9]{40}$'
}

function Assert-TrustedExecutionEvidence {
    param(
        [Parameter(Mandatory = $true)]$TrustedExecution,
        [Parameter(Mandatory = $true)]$Policy,
        [Parameter(Mandatory = $true)]$ReleaseManifest
    )

    $contract = $Policy.trustedExecutionContract
    if ($null -eq $contract -or
        @(Compare-Object @(
            'bundlePath',
            'externalBundleSha256VariableName',
            'externalSubjectsVariableName',
            'externalWorkflowRefVariableName',
            'schemaVersion',
            'workflowPath'
        ) @($contract.PSObject.Properties.Name | Sort-Object)).Count -ne 0 -or
        [string]$contract.schemaVersion -cne '2.0.0' -or
        [string]$contract.workflowPath -cne '.github/workflows/trusted-protected-operations.yml' -or
        [string]$contract.bundlePath -cne 'operations/protected-execution-bundle.zip') {
        throw 'Trusted execution policy contract is missing or changed.'
    }
    if ($null -eq $TrustedExecution -or
        @(Compare-Object @(
            'bundlePath',
            'bundleSha256',
            'callerWorkflowRef',
            'reusableWorkflowRef'
        ) @($TrustedExecution.PSObject.Properties.Name | Sort-Object)).Count -ne 0 -or
        [string]$TrustedExecution.callerWorkflowRef -notmatch
            '^syedmh/Dreamer/\.github/workflows/[A-Za-z0-9_.-]+\.yml@refs/heads/main$' -or
        [string]$TrustedExecution.reusableWorkflowRef -notmatch
            '^syedmh/Dreamer/\.github/workflows/trusted-protected-operations\.yml@[a-f0-9]{40}$' -or
        [string]$TrustedExecution.reusableWorkflowRef.EndsWith(('@' + ('0' * 40))) -or
        [string]$TrustedExecution.bundlePath -cne [string]$contract.bundlePath -or
        -not (Test-Sha256 ([string]$TrustedExecution.bundleSha256)) -or
        [string]$TrustedExecution.bundleSha256 -cne
            ([string]$TrustedExecution.bundleSha256).ToLowerInvariant()) {
        throw 'Trusted execution evidence does not match the exact reusable-workflow and bundle contract.'
    }
    $bundleEntries = @($ReleaseManifest.files | Where-Object {
        [string]$_.path -ceq [string]$contract.bundlePath
    })
    if ($bundleEntries.Count -ne 1 -or
        [string]$bundleEntries[0].sha256 -cne [string]$TrustedExecution.bundleSha256) {
        throw 'Trusted execution bundle SHA-256 is not bound by the C6 release manifest.'
    }
    return $TrustedExecution
}

function Set-TrustedExecutionEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]        [string]$CallerWorkflowRef,
        [Parameter(Mandatory = $true)]
        [string]$ReusableWorkflowRef,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[a-fA-F0-9]{64}$')]
        [string]$BundleSha256,
        [string]$BundlePath = 'operations/protected-execution-bundle.zip'
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Trusted execution evidence file is missing: $Path"
    }
    if ($CallerWorkflowRef -notmatch
        '^syedmh/Dreamer/\.github/workflows/[A-Za-z0-9_.-]+\.yml@refs/heads/main$' -or
        $ReusableWorkflowRef -notmatch
        '^syedmh/Dreamer/\.github/workflows/trusted-protected-operations\.yml@[a-f0-9]{40}$' -or
        $ReusableWorkflowRef.EndsWith(('@' + ('0' * 40))) -or
        $BundlePath -cne 'operations/protected-execution-bundle.zip') {
        throw 'Trusted execution identity is not the exact protected reusable-workflow contract.'
    }
    $evidence = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -DateKind String
    if ($null -ne $evidence.PSObject.Properties['trustedExecution']) {
        $evidence.PSObject.Properties.Remove('trustedExecution')
    }
    $evidence | Add-Member -NotePropertyName trustedExecution -NotePropertyValue ([ordered]@{
        callerWorkflowRef = $CallerWorkflowRef
        reusableWorkflowRef = $ReusableWorkflowRef
        bundlePath = $BundlePath
        bundleSha256 = $BundleSha256.ToLowerInvariant()
    })
    Write-Utf8Json -Value $evidence -Path $Path -Depth 80
}

function Get-PropertyValue {
    param($Object, [string]$Name)

    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Assert-TrustedGitHubRunMetadata {
    param(
        [Parameter(Mandatory = $true)]$Metadata,
        [Parameter(Mandatory = $true)]$Provenance,
        [Parameter(Mandatory = $true)][string[]]$AllowedWorkflowPaths,
        [string]$ExpectedCommitSha,
        [switch]$RequireSuccess
    )

    if ([string](Get-PropertyValue $Metadata 'schemaVersion') -ne '1.0.0' -or
        [string](Get-PropertyValue $Metadata 'repository') -ne [string]$Provenance.repository -or
        [string](Get-PropertyValue $Metadata 'ref') -ne [string]$Provenance.protectedRef -or
        [string](Get-PropertyValue $Metadata 'workflowPath') -notin $AllowedWorkflowPaths -or
        [string](Get-PropertyValue $Metadata 'runId') -notmatch '^[1-9][0-9]*$' -or
        [int](Get-PropertyValue $Metadata 'runAttempt') -lt 1 -or
        [string](Get-PropertyValue $Metadata 'actor') -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$' -or
        [string](Get-PropertyValue $Metadata 'actorId') -notmatch '^[1-9][0-9]*$' -or
        -not (Test-CommitSha ([string](Get-PropertyValue $Metadata 'commitSha')))) {
        throw 'GitHub run metadata is not from the approved repository, workflow, ref, or immutable run identity.'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedCommitSha) -and
        [string](Get-PropertyValue $Metadata 'commitSha') -ne $ExpectedCommitSha.ToLowerInvariant()) {
        throw 'GitHub run metadata is not bound to the expected release commit.'
    }
    if ($RequireSuccess -and
        ([string](Get-PropertyValue $Metadata 'status') -ne 'completed' -or
         [string](Get-PropertyValue $Metadata 'conclusion') -ne 'success')) {
        throw 'GitHub run metadata does not prove a completed successful run.'
    }
}

function Assert-GitHubRunBinding {
    param(
        [Parameter(Mandatory = $true)]$Binding,
        [Parameter(Mandatory = $true)]$Metadata,
        [switch]$IgnoreConclusion
    )

    foreach ($name in @('repository', 'workflowPath', 'runId', 'runAttempt', 'ref', 'commitSha')) {
        if ([string](Get-PropertyValue $Binding $name) -ne [string](Get-PropertyValue $Metadata $name)) {
            throw "GitHub run binding does not match trusted metadata: $name."
        }
    }
    if (-not $IgnoreConclusion -and
        [string](Get-PropertyValue $Binding 'conclusion') -ne [string](Get-PropertyValue $Metadata 'conclusion')) {
        throw 'GitHub run binding conclusion does not match trusted metadata.'
    }
}
