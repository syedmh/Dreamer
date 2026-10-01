[CmdletBinding()]
param(
  [ValidatePattern('^[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*$')]
  [string]$Version = '1.0.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$distRoot = Join-Path $projectRoot 'dist'
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$nodeVersion = '22.23.3'
$nodeVersionTag = "v$nodeVersion"
$nodeDistBaseUrl = "https://nodejs.org/dist/$nodeVersionTag"
$runtimeCacheRoot = Join-Path $artifactsRoot "runtime-cache\$nodeVersionTag"
$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
  'TCFBuild-release-{0}' -f [System.Guid]::NewGuid().ToString('N')
)

$runtimeFiles = @(
  'server.mjs',
  'index.html',
  'styles.css',
  'control.html',
  'control.css',
  'Logo.png',
  'Boy.png',
  'Girl.png',
  'src/app.mjs',
  'src/config.mjs',
  'src/control.mjs',
  'src/currency.mjs',
  'src/model.mjs',
  'src/render.mjs',
  'src/scene.mjs'
)

$runtimePackages = @(
  [PSCustomObject]@{
    Platform = 'windows'
    RuntimeDirectory = 'win-x64'
    ArchiveName = "node-$nodeVersionTag-win-x64.zip"
    ArchiveType = 'zip'
    ExecutablePath = 'node.exe'
  },
  [PSCustomObject]@{
    Platform = 'windows'
    RuntimeDirectory = 'win-arm64'
    ArchiveName = "node-$nodeVersionTag-win-arm64.zip"
    ArchiveType = 'zip'
    ExecutablePath = 'node.exe'
  },
  [PSCustomObject]@{
    Platform = 'macos'
    RuntimeDirectory = 'darwin-arm64'
    ArchiveName = "node-$nodeVersionTag-darwin-arm64.tar.gz"
    ArchiveType = 'tar.gz'
    ExecutablePath = 'bin\node'
  },
  [PSCustomObject]@{
    Platform = 'macos'
    RuntimeDirectory = 'darwin-x64'
    ArchiveName = "node-$nodeVersionTag-darwin-x64.tar.gz"
    ArchiveType = 'tar.gz'
    ExecutablePath = 'bin\node'
  }
)

function Copy-RequiredFile {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Source,
    [Parameter(Mandatory = $true)]
    [string]$Destination
  )

  if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
    throw "Required file is missing: $Source"
  }

  $destinationDirectory = Split-Path -Parent $Destination
  if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
  }

  Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Get-Sha256 {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Path
  )

  $stream = [System.IO.File]::OpenRead($Path)
  $sha256 = [System.Security.Cryptography.SHA256]::Create()
  try {
    $hashBytes = $sha256.ComputeHash($stream)
    return (
      [System.BitConverter]::ToString($hashBytes).Replace('-', '').ToLowerInvariant()
    )
  }
  finally {
    $sha256.Dispose()
    $stream.Dispose()
  }
}

function Invoke-OfficialDownload {
  param(
    [Parameter(Mandatory = $true)]
    [string]$FileName,
    [Parameter(Mandatory = $true)]
    [string]$Destination
  )

  $temporaryPath = "$Destination.download"
  if (Test-Path -LiteralPath $temporaryPath) {
    Remove-Item -LiteralPath $temporaryPath -Force
  }

  try {
    Invoke-WebRequest `
      -Uri "$nodeDistBaseUrl/$FileName" `
      -OutFile $temporaryPath `
      -UseBasicParsing
    Move-Item -LiteralPath $temporaryPath -Destination $Destination -Force
  }
  finally {
    if (Test-Path -LiteralPath $temporaryPath) {
      Remove-Item -LiteralPath $temporaryPath -Force
    }
  }
}

function Get-OfficialChecksums {
  $checksumsPath = Join-Path $runtimeCacheRoot 'SHASUMS256.txt'
  if (-not (Test-Path -LiteralPath $checksumsPath -PathType Leaf)) {
    Write-Host "Downloading official Node.js checksums: $nodeVersionTag"
    Invoke-OfficialDownload -FileName 'SHASUMS256.txt' -Destination $checksumsPath
  }

  $checksums = @{}
  foreach ($line in [System.IO.File]::ReadAllLines($checksumsPath)) {
    if ($line -match '^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<name>.+)$') {
      $checksums[$Matches.name] = $Matches.hash.ToLowerInvariant()
    }
  }

  foreach ($runtimePackage in $runtimePackages) {
    if (-not $checksums.ContainsKey($runtimePackage.ArchiveName)) {
      throw "Official checksum is missing for $($runtimePackage.ArchiveName)."
    }
  }

  return $checksums
}

function Get-VerifiedRuntimeArchive {
  param(
    [Parameter(Mandatory = $true)]
    [PSCustomObject]$RuntimePackage,
    [Parameter(Mandatory = $true)]
    [hashtable]$Checksums
  )

  $archivePath = Join-Path $runtimeCacheRoot $RuntimePackage.ArchiveName
  $expectedHash = $Checksums[$RuntimePackage.ArchiveName]

  if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
    $cachedHash = Get-Sha256 -Path $archivePath
    if ($cachedHash -ne $expectedHash) {
      Write-Warning "Removing cached file with invalid checksum: $archivePath"
      Remove-Item -LiteralPath $archivePath -Force
    }
  }

  if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
    Write-Host "Downloading official Node.js runtime: $($RuntimePackage.ArchiveName)"
    Invoke-OfficialDownload `
      -FileName $RuntimePackage.ArchiveName `
      -Destination $archivePath
  }

  $actualHash = Get-Sha256 -Path $archivePath
  if ($actualHash -ne $expectedHash) {
    Remove-Item -LiteralPath $archivePath -Force
    throw (
      "Checksum verification failed for {0}. Expected {1}, found {2}." -f
        $RuntimePackage.ArchiveName,
        $expectedHash,
        $actualHash
    )
  }

  Write-Host (
    "Verified SHA256: {0}  {1}" -f
      $actualHash,
      $RuntimePackage.ArchiveName
  )
  return $archivePath
}

function Copy-EmbeddedRuntime {
  param(
    [Parameter(Mandatory = $true)]
    [PSCustomObject]$RuntimePackage,
    [Parameter(Mandatory = $true)]
    [string]$ArchivePath,
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot
  )

  $extractRoot = Join-Path $stagingRoot (
    'extract-{0}' -f $RuntimePackage.RuntimeDirectory
  )
  New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null

  if ($RuntimePackage.ArchiveType -eq 'zip') {
    $sourceRootName = $RuntimePackage.ArchiveName.Substring(
      0,
      $RuntimePackage.ArchiveName.Length - '.zip'.Length
    )
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $extractRoot -Force
  }
  else {
    $sourceRootName = $RuntimePackage.ArchiveName.Substring(
      0,
      $RuntimePackage.ArchiveName.Length - '.tar.gz'.Length
    )
    & tar.exe `
      -xzf $ArchivePath `
      -C $extractRoot `
      "$sourceRootName/bin/node" `
      "$sourceRootName/LICENSE"
    if ($LASTEXITCODE -ne 0) {
      throw (
        "tar.exe failed to extract {0} with exit code {1}." -f
          $RuntimePackage.ArchiveName,
          $LASTEXITCODE
      )
    }
  }

  $sourceRoot = Join-Path $extractRoot $sourceRootName
  $runtimeRoot = Join-Path (
    Join-Path $PackageRoot 'runtime'
  ) $RuntimePackage.RuntimeDirectory
  $executableName = Split-Path -Leaf $RuntimePackage.ExecutablePath

  Copy-RequiredFile `
    -Source (Join-Path $sourceRoot $RuntimePackage.ExecutablePath) `
    -Destination (Join-Path $runtimeRoot $executableName)
  Copy-RequiredFile `
    -Source (Join-Path $sourceRoot 'LICENSE') `
    -Destination (Join-Path $runtimeRoot 'LICENSE')
}

function Assert-LfOnly {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Path
  )

  $bytes = [System.IO.File]::ReadAllBytes($Path)
  if ($bytes -contains 13) {
    throw "macOS launcher must use LF line endings only: $Path"
  }
}

function Convert-ToLf {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Path
  )

  $content = [System.IO.File]::ReadAllText($Path)
  $content = $content.Replace("`r`n", "`n").Replace("`r", "`n")
  $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
  [System.IO.File]::WriteAllText($Path, $content, $utf8WithoutBom)
}

function Assert-ZipManifest {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ZipPath,
    [Parameter(Mandatory = $true)]
    [string[]]$ExpectedFiles
  )

  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
  try {
    $actualFiles = @(
      $archive.Entries |
        Where-Object { -not [string]::IsNullOrEmpty($_.Name) } |
        ForEach-Object { $_.FullName.Replace('\', '/') } |
        Sort-Object
    )
  }
  finally {
    $archive.Dispose()
  }

  $expected = @($ExpectedFiles | Sort-Object)
  $difference = @(Compare-Object -ReferenceObject $expected -DifferenceObject $actualFiles)
  if ($difference.Count -ne 0) {
    $details = $difference |
      ForEach-Object { '{0} {1}' -f $_.SideIndicator, $_.InputObject } |
      Out-String
    throw "Archive manifest mismatch for $ZipPath`n$details"
  }
}

function Get-PackagedBaseCommit {
  $commit = (
    & git.exe -C $projectRoot log -1 --format=%H -- .
  ).Trim()
  if ($LASTEXITCODE -ne 0) {
    throw "Could not determine the latest commit affecting TCFBuild."
  }
  if ($commit -notmatch '^[0-9a-f]{40}$') {
    throw "The latest TCFBuild commit is not a valid full Git SHA."
  }
  return $commit
}

function Write-UpdatePolicy {
  param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,
    [Parameter(Mandatory = $true)]
    [string]$PackagedBaseCommit
  )

  $policy = [ordered]@{
    schemaVersion = 1
    repository = [ordered]@{
      owner = 'syedmh'
      name = 'Dreamer'
      branch = 'main'
      subdirectory = 'TCFBuild'
    }
    packagedBaseCommit = $PackagedBaseCommit
    packagedContent = 'working-tree-snapshot'
    runtimeFiles = $runtimeFiles
  }
  $json = $policy | ConvertTo-Json -Depth 4
  $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
  [System.IO.File]::WriteAllText(
    (Join-Path $PackageRoot 'update-policy.json'),
    "$json`n",
    $utf8WithoutBom
  )
}

function New-PlatformPackage {
  param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('windows', 'macos')]
    [string]$Platform,
    [Parameter(Mandatory = $true)]
    [string]$LauncherName,
    [Parameter(Mandatory = $true)]
    [hashtable]$Checksums,
    [Parameter(Mandatory = $true)]
    [string]$PackagedBaseCommit
  )

  $packageName = "TCFBuild-v$Version-$Platform"
  $platformStagingRoot = Join-Path $stagingRoot $Platform
  $packageRoot = Join-Path $platformStagingRoot $packageName
  New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

  foreach ($relativePath in $runtimeFiles) {
    Copy-RequiredFile `
      -Source (Join-Path $distRoot $relativePath) `
      -Destination (
        Join-Path (
          Join-Path $packageRoot 'app\generations\packaged'
        ) $relativePath
      )
  }

  Copy-RequiredFile `
    -Source (Join-Path $projectRoot 'release\updater.mjs') `
    -Destination (Join-Path $packageRoot 'updater.mjs')
  Write-UpdatePolicy `
    -PackageRoot $packageRoot `
    -PackagedBaseCommit $PackagedBaseCommit

  $releaseRoot = Join-Path (Join-Path $projectRoot 'release') $Platform
  Copy-RequiredFile `
    -Source (Join-Path $releaseRoot $LauncherName) `
    -Destination (Join-Path $packageRoot $LauncherName)
  Copy-RequiredFile `
    -Source (Join-Path $releaseRoot 'README.txt') `
    -Destination (Join-Path $packageRoot 'README.txt')

  $platformRuntimes = @(
    $runtimePackages | Where-Object { $_.Platform -eq $Platform }
  )
  foreach ($runtimePackage in $platformRuntimes) {
    $archivePath = Get-VerifiedRuntimeArchive `
      -RuntimePackage $runtimePackage `
      -Checksums $Checksums
    Copy-EmbeddedRuntime `
      -RuntimePackage $runtimePackage `
      -ArchivePath $archivePath `
      -PackageRoot $packageRoot
  }

  if ($Platform -eq 'macos') {
    Convert-ToLf -Path (Join-Path $packageRoot $LauncherName)
    Assert-LfOnly -Path (Join-Path $packageRoot $LauncherName)
  }

  $zipPath = Join-Path $artifactsRoot "$packageName.zip"
  if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
  }

  Compress-Archive -Path $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal

  $expectedFiles = @(
    $runtimeFiles | ForEach-Object {
      '{0}/app/generations/packaged/{1}' -f $packageName, $_
    }
  )
  $expectedFiles += "$packageName/updater.mjs"
  $expectedFiles += "$packageName/update-policy.json"
  $expectedFiles += "$packageName/$LauncherName"
  $expectedFiles += "$packageName/README.txt"
  foreach ($runtimePackage in $platformRuntimes) {
    $runtimeExecutable = if ($Platform -eq 'windows') {
      'node.exe'
    }
    else {
      'node'
    }
    $expectedFiles += (
      "$packageName/runtime/{0}/{1}" -f
        $runtimePackage.RuntimeDirectory,
        $runtimeExecutable
    )
    $expectedFiles += (
      "$packageName/runtime/{0}/LICENSE" -f
        $runtimePackage.RuntimeDirectory
    )
  }
  Assert-ZipManifest -ZipPath $zipPath -ExpectedFiles $expectedFiles

  Write-Host "Created and validated: $zipPath"
}

try {
  & (Join-Path $projectRoot 'build.bat')
  if ($LASTEXITCODE -ne 0) {
    throw "build.bat failed with exit code $LASTEXITCODE."
  }

  New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
  New-Item -ItemType Directory -Path $runtimeCacheRoot -Force | Out-Null
  New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

  $checksums = Get-OfficialChecksums
  $packagedBaseCommit = Get-PackagedBaseCommit
  Write-Host "Packaged TCFBuild base commit: $packagedBaseCommit"

  New-PlatformPackage `
    -Platform 'windows' `
    -LauncherName 'setup-tcfbuild.bat' `
    -Checksums $checksums `
    -PackagedBaseCommit $packagedBaseCommit
  New-PlatformPackage `
    -Platform 'macos' `
    -LauncherName 'setup-tcfbuild.command' `
    -Checksums $checksums `
    -PackagedBaseCommit $packagedBaseCommit
}
finally {
  if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
  }
}
