[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$ActionlintPath
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
    if ([string]::IsNullOrWhiteSpace($ActionlintPath)) {
        $temporaryTools = Join-Path ([IO.Path]::GetTempPath()) "husaynia-t21-actionlint-$([guid]::NewGuid().ToString('N'))"
        $installerOutput = @(Invoke-CheckedScript `
            -Path (Join-Path $RepositoryRoot 'eng\tools\Install-Actionlint.ps1') `
            -Parameters @{ DestinationDirectory = $temporaryTools } `
            -Label 'install pinned actionlint')
        $installerOutput | ForEach-Object { Write-Output $_ }
        $ActionlintPath = $installerOutput[-1]
    }

    $workflowFiles = Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'pipelines\github') -File -Filter '*.yml' |
        Sort-Object FullName
    if ($workflowFiles.Count -eq 0) {
        throw 'No workflow definitions were found.'
    }

    & $ActionlintPath @($workflowFiles.FullName)
    if ($LASTEXITCODE -ne 0) {
        throw "actionlint rejected one or more workflows with exit code $LASTEXITCODE."
    }
    Write-Output "ACTIONLINT status=PASS version=1.7.12 workflows=$($workflowFiles.Count)"
}
finally {
    if ($null -ne $temporaryTools -and (Test-Path -LiteralPath $temporaryTools)) {
        Remove-Item -LiteralPath $temporaryTools -Recurse -Force
    }
}
