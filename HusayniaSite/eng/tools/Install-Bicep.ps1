[CmdletBinding()]
param(
    [string]$DestinationDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'husaynia-t21-tools'),
    [string]$Version = '0.46.1'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Version -ne '0.46.1') {
    throw 'Only the reviewed Bicep version 0.46.1 is allowed.'
}

$platform = if ($IsWindows) {
    @{
        Asset = 'bicep-win-x64.exe'
        FileName = 'bicep.exe'
        Sha256 = '441d3d6094513acaa8a9be0b96b754d174bc0c19064c7be04e35b87e67b66250'
    }
}
elseif ($IsLinux) {
    @{
        Asset = 'bicep-linux-x64'
        FileName = 'bicep'
        Sha256 = '3e011d629ea4311b7a7dd8f0040ab2b1a072ea4ff5d02cb75e0e55a9a6703fb9'
    }
}
else {
    throw 'This installer currently supports only reviewed Windows x64 and Linux x64 runners.'
}

New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
$destination = Join-Path $DestinationDirectory $platform.FileName
$download = "$destination.download"
$uri = "https://github.com/Azure/bicep/releases/download/v$Version/$($platform.Asset)"

try {
    Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $download -MaximumRetryCount 3 -RetryIntervalSec 2
    $actualHash = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $platform.Sha256) {
        throw "Bicep checksum mismatch. Expected $($platform.Sha256), received $actualHash."
    }

    Move-Item -LiteralPath $download -Destination $destination -Force
    if (-not $IsWindows) {
        & chmod 700 $destination
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not set the Bicep executable permission.'
        }
    }

    $reportedVersion = @(& $destination --version 2>&1)
    if ($LASTEXITCODE -ne 0 -or ($reportedVersion -join "`n") -notmatch [regex]::Escape($Version)) {
        throw "Bicep version verification failed: $($reportedVersion -join ' ')"
    }

    Write-Output $destination
}
finally {
    if (Test-Path -LiteralPath $download) {
        Remove-Item -LiteralPath $download -Force
    }
}
