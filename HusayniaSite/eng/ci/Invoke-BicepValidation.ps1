[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$BicepPath,
    [switch]$InstallToTemporaryDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Resolve-HusayniaRepositoryRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path

$temporaryTools = $null
try {
    if ([string]::IsNullOrWhiteSpace($BicepPath)) {
        if (-not $InstallToTemporaryDirectory) {
            throw 'Supply -BicepPath or opt into -InstallToTemporaryDirectory.'
        }

        $temporaryTools = Join-Path ([IO.Path]::GetTempPath()) "husaynia-t21-bicep-$([guid]::NewGuid().ToString('N'))"
        $installerOutput = @(Invoke-CheckedScript `
            -Path (Join-Path $RepositoryRoot 'eng\tools\Install-Bicep.ps1') `
            -Parameters @{ DestinationDirectory = $temporaryTools } `
            -Label 'install pinned Bicep')
        $installerOutput | ForEach-Object { Write-Output $_ }
        $BicepPath = $installerOutput[-1]
    }

    $scripts = @(
        'validate-bicep.ps1',
        'validate-static-policy.ps1',
        'validate-negative-mutations.ps1',
        'summarize-static-plan.ps1'
    )
    $pwsh = (Get-Process -Id $PID).Path

    foreach ($scriptName in $scripts) {
        $scriptPath = Join-Path $RepositoryRoot "infra\scripts\$scriptName"
        Write-Output "RUN   infra/$scriptName"
        & $pwsh -NoProfile -File $scriptPath -InfraRoot (Join-Path $RepositoryRoot 'infra') -BicepPath $BicepPath
        if ($LASTEXITCODE -ne 0) {
            throw "Infrastructure validation failed: $scriptName"
        }
    }
}
finally {
    if ($null -ne $temporaryTools -and (Test-Path -LiteralPath $temporaryTools)) {
        Remove-Item -LiteralPath $temporaryTools -Recurse -Force
    }
}
