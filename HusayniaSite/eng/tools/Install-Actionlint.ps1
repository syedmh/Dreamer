[CmdletBinding()]
param(
    [string]$DestinationDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'husaynia-t21-tools'),
    [string]$Version = '1.7.12'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Version -ne '1.7.12') {
    throw 'Only the reviewed actionlint version 1.7.12 is allowed.'
}

$platform = if ($IsWindows) {
    @{
        Asset = 'actionlint_1.7.12_windows_amd64.zip'
        Sha256 = '6e7241b51e6817ea6a047693d8e6fed13b31819c9a0dd6c5a726e1592d22f6e9'
        Executable = 'actionlint.exe'
        ArchiveType = 'zip'
    }
}
elseif ($IsLinux) {
    @{
        Asset = 'actionlint_1.7.12_linux_amd64.tar.gz'
        Sha256 = '8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8'
        Executable = 'actionlint'
        ArchiveType = 'tar'
    }
}
else {
    throw 'This installer currently supports only reviewed Windows x64 and Linux x64 runners.'
}

New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
$archive = Join-Path $DestinationDirectory $platform.Asset
$executable = Join-Path $DestinationDirectory $platform.Executable
$uri = "https://github.com/rhysd/actionlint/releases/download/v$Version/$($platform.Asset)"

try {
    Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $archive -MaximumRetryCount 3 -RetryIntervalSec 2
    $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $platform.Sha256) {
        throw "actionlint checksum mismatch. Expected $($platform.Sha256), received $actualHash."
    }

    if ($platform.ArchiveType -eq 'zip') {
        Expand-Archive -LiteralPath $archive -DestinationPath $DestinationDirectory -Force
    }
    else {
        & tar -xzf $archive -C $DestinationDirectory actionlint
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not extract actionlint.'
        }
        & chmod 700 $executable
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not set the actionlint executable permission.'
        }
    }

    $reportedVersion = @(& $executable -version 2>&1)
    if ($LASTEXITCODE -ne 0 -or ($reportedVersion -join "`n") -notmatch [regex]::Escape($Version)) {
        throw "actionlint version verification failed: $($reportedVersion -join ' ')"
    }

    Write-Output $executable
}
finally {
    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive -Force
    }
}
