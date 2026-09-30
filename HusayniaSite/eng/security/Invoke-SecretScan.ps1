[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string[]]$ScanPaths,
    [string]$ConfigurationPath,
    [Parameter(Mandatory = $true)]
    [string]$ReportPath,
    [switch]$IncludeUntracked
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Resolve-HusayniaRepositoryRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path

if ([string]::IsNullOrWhiteSpace($ConfigurationPath)) {
    $ConfigurationPath = Join-Path $RepositoryRoot 'pipelines\config\secret-scan.json'
}

$configuration = Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
if ($configuration.schemaVersion -ne '1.0.0' -or $configuration.rules.Count -eq 0) {
    throw 'Secret scan configuration is invalid or empty.'
}
$configurationRepositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $ConfigurationPath).Path) '..\..'))

function Get-ShannonEntropy {
    param([string]$Value)

    if ([string]::IsNullOrEmpty($Value)) { return 0.0 }
    $counts = @{}
    foreach ($character in $Value.ToCharArray()) {
        $key = [string]$character
        $counts[$key] = 1 + [int]$counts[$key]
    }
    $entropy = 0.0
    foreach ($count in $counts.Values) {
        $probability = [double]$count / [double]$Value.Length
        $entropy -= $probability * [Math]::Log($probability, 2)
    }
    return $entropy
}

function Add-SecretFinding {
    param([string]$RuleId, [string]$Path, [int]$Line)
    $script:findings.Add([ordered]@{
        ruleId = $RuleId
        path = $Path
        line = $Line
    })
}

$candidateFiles = [Collections.Generic.List[string]]::new()
if ($null -ne $ScanPaths -and $ScanPaths.Count -gt 0) {
    foreach ($scanPath in $ScanPaths) {
        $resolved = Join-Path $RepositoryRoot $scanPath
        if (-not (Test-Path -LiteralPath $resolved)) {
            throw "Secret scan path does not exist: $scanPath"
        }

        if ((Get-Item -LiteralPath $resolved).PSIsContainer) {
            Get-ChildItem -LiteralPath $resolved -Recurse -File | ForEach-Object {
                $candidateFiles.Add($_.FullName)
            }
        }
        else {
            $candidateFiles.Add((Resolve-Path -LiteralPath $resolved).Path)
        }
    }
}
elseif (Get-Command git -ErrorAction SilentlyContinue) {
    $gitArguments = @('-C', $RepositoryRoot, 'ls-files', '--cached')
    if ($IncludeUntracked) {
        $gitArguments += @('--others', '--exclude-standard')
    }
    $gitFiles = @(& git @gitArguments)
    if ($LASTEXITCODE -ne 0) {
        throw 'git ls-files failed while enumerating secret scan inputs.'
    }

    foreach ($gitFile in $gitFiles) {
        $fullPath = Join-Path $RepositoryRoot $gitFile
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            $candidateFiles.Add((Resolve-Path -LiteralPath $fullPath).Path)
        }
    }
}
else {
    throw 'Git is required unless explicit -ScanPaths are supplied.'
}

$findings = [Collections.Generic.List[object]]::new()
$scanned = 0
$scannedBytes = [long]0
$excludedFiles = 0
$knownBinaryFiles = 0
$hashAllowlistedFiles = 0
$binaryExtensions = @($configuration.knownBinaryExtensions | ForEach-Object { ([string]$_).ToLowerInvariant() })
$archiveExtensions = @($configuration.sourceArchiveExtensions | ForEach-Object { ([string]$_).ToLowerInvariant() })
$entropyMinimumLength = [int]$configuration.entropy.minimumLength
$entropyThreshold = [double]$configuration.entropy.threshold
$allowlistedFiles = @{}

foreach ($allowlistManifest in @($configuration.hashBoundAllowlistManifests)) {
    $manifestPath = Join-Path $configurationRepositoryRoot ([string]$allowlistManifest.manifestPath)
    $allowlistRoot = Join-Path $configurationRepositoryRoot ([string]$allowlistManifest.rootPath)
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $allowlistRoot -PathType Container) -or
        [string]$allowlistManifest.manifestSha256 -notmatch '^[a-f0-9]{64}$' -or
        (Get-Sha256Lower -Path $manifestPath) -ne [string]$allowlistManifest.manifestSha256) {
        throw "Secret scan hash-bound allowlist manifest is missing or changed: $($allowlistManifest.id)"
    }
    foreach ($manifestLine in Get-Content -LiteralPath $manifestPath) {
        $line = $manifestLine.TrimStart([char]0xFEFF)
        if ($line -notmatch '^([a-f0-9]{64})  (.+)$') {
            throw "Secret scan hash-bound allowlist contains a malformed checksum line: $($allowlistManifest.id)"
        }
        $listedSha256 = $Matches[1]
        $listedRelative = $Matches[2].Replace('\', '/')
        if ([IO.Path]::IsPathRooted($listedRelative) -or $listedRelative.Contains('..')) {
            throw "Secret scan hash-bound allowlist contains an unsafe path: $($allowlistManifest.id)"
        }
        $repositoryRelative = (([string]$allowlistManifest.rootPath).TrimEnd('/', '\') + '/' + $listedRelative).Replace('\', '/')
        $allowedByPattern = @($allowlistManifest.allowedPathPatterns |
            Where-Object { $repositoryRelative -match [string]$_ }).Count -gt 0
        if (-not $allowedByPattern) {
            continue
        }
        if ($allowlistedFiles.ContainsKey($repositoryRelative)) {
            throw "Secret scan hash-bound allowlist path is duplicated: $repositoryRelative"
        }
        $allowlistedFiles[$repositoryRelative] = [ordered]@{
            sha256 = $listedSha256
            categories = @($allowlistManifest.allowedCategories)
        }
    }
}

function Test-HashBoundAllowlist {
    param([string]$RelativePath, [string]$Category, [string]$FullPath)

    if (-not $script:allowlistedFiles.ContainsKey($RelativePath)) {
        return $false
    }
    $entry = $script:allowlistedFiles[$RelativePath]
    return $Category -in @($entry.categories) -and
        (Get-Sha256Lower -Path $FullPath) -eq [string]$entry.sha256
}

foreach ($file in ($candidateFiles | Sort-Object -Unique)) {
    $relative = Get-RelativeUnixPath -BasePath $RepositoryRoot -Path $file
    $excluded = $false
    foreach ($excludedPattern in $configuration.excludedPathPatterns) {
        if ($relative -match $excludedPattern) {
            $excluded = $true
            break
        }
    }
    if ($excluded) {
        $excludedFiles++
        if (Test-HashBoundAllowlist -RelativePath $relative -Category 'excluded-path' -FullPath $file) {
            $hashAllowlistedFiles++
            continue
        }
        Add-SecretFinding -RuleId 'excluded-source-path' -Path $relative -Line 0
        continue
    }

    $extension = [IO.Path]::GetExtension($file).ToLowerInvariant()
    if ($extension -in $binaryExtensions) {
        $knownBinaryFiles++
        if (Test-HashBoundAllowlist -RelativePath $relative -Category 'known-binary' -FullPath $file) {
            $hashAllowlistedFiles++
            continue
        }
        $ruleId = if ($extension -in $archiveExtensions) {
            'source-archive-requires-hash-allowlist'
        }
        else {
            'known-binary-requires-hash-allowlist'
        }
        Add-SecretFinding -RuleId $ruleId -Path $relative -Line 0
        continue
    }

    $fileInfo = Get-Item -LiteralPath $file
    $scannedBytes += $fileInfo.Length
    $stream = [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $sampleLength = [Math]::Min(8192, [int][Math]::Min($stream.Length, [int]::MaxValue))
        $sample = [byte[]]::new($sampleLength)
        $read = $stream.Read($sample, 0, $sampleLength)
        $containsNull = $false
        for ($index = 0; $index -lt $read; $index++) {
            if ($sample[$index] -eq 0) {
                $containsNull = $true
                break
            }
        }
        if ($containsNull) {
            Add-SecretFinding -RuleId 'unscannable-binary-content' -Path $relative -Line 0
            continue
        }
        $stream.Position = 0
        $encoding = [Text.UTF8Encoding]::new($false, $true)
        $reader = [IO.StreamReader]::new($stream, $encoding, $true, 4096, $true)
        try {
            $scanned++
            $lineNumber = 0
            while (-not $reader.EndOfStream) {
                $line = $reader.ReadLine()
                $lineNumber++
                foreach ($rule in $configuration.rules) {
                    if ([regex]::IsMatch($line, [string]$rule.pattern)) {
                        Add-SecretFinding -RuleId ([string]$rule.id) -Path $relative -Line $lineNumber
                    }
                }

                foreach ($match in [regex]::Matches($line, "[A-Za-z0-9+/=_-]{$entropyMinimumLength,}")) {
                    $token = $match.Value
                    if ($token -match '^[a-fA-F0-9]{40}$' -or
                        $token -match '^[a-fA-F0-9]{64}$' -or
                        $token -match '^(UNAPPROVED|PLACEHOLDER|EXAMPLE|REDACTED)') {
                        continue
                    }
                    $classes = 0
                    if ($token -match '[a-z]') { $classes++ }
                    if ($token -match '[A-Z]') { $classes++ }
                    if ($token -match '[0-9]') { $classes++ }
                    if ($token -match '[/+_=-]') { $classes++ }
                    if ($classes -ge 3 -and (Get-ShannonEntropy -Value $token) -ge $entropyThreshold) {
                        Add-SecretFinding -RuleId 'high-entropy-token' -Path $relative -Line $lineNumber
                    }
                }
            }
        }
        catch [Text.DecoderFallbackException] {
            Add-SecretFinding -RuleId 'unscannable-non-utf8-content' -Path $relative -Line 0
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

$report = [ordered]@{
    schemaVersion = '2.0.0'
    scanner = 'Husaynia fail-closed repository secret scanner'
    status = if ($findings.Count -eq 0) { 'PASS' } else { 'FAIL' }
    scannedFiles = $scanned
    scannedBytes = $scannedBytes
    excludedFiles = $excludedFiles
    knownBinaryFiles = $knownBinaryFiles
    hashAllowlistedFiles = $hashAllowlistedFiles
    silentlySkippedFiles = 0
    findingCount = $findings.Count
    findings = @($findings)
    redacted = $true
}
Write-Utf8Json -Value $report -Path $ReportPath

Write-Output "SECRET-SCAN status=$($report.status) scanned=$scanned bytes=$scannedBytes findings=$($findings.Count) report=$ReportPath"
if ($scanned -eq 0) {
    throw 'Secret scan examined zero scannable files; refusing a success-shaped empty result.'
}
if ($findings.Count -gt 0) {
    foreach ($finding in $findings) {
        Write-Output "FAIL  secret:$($finding.ruleId) $($finding.path):$($finding.line) value=[REDACTED]"
    }
    throw "Secret scan found $($findings.Count) finding(s)."
}
