[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GateName,
    [string[]]$RequiredPaths,
    [string]$RequiredPathsJson,
    [string]$RepositoryRoot,
    [Parameter(Mandatory = $true)]
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Resolve-HusayniaRepositoryRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path

if (-not [string]::IsNullOrWhiteSpace($RequiredPathsJson)) {
    $RequiredPaths = @($RequiredPathsJson | ConvertFrom-Json)
}
if ($null -eq $RequiredPaths -or $RequiredPaths.Count -eq 0) {
    throw 'At least one required path must be supplied.'
}

$results = foreach ($requiredPath in $RequiredPaths) {
    $fullPath = Join-Path $RepositoryRoot $requiredPath
    $exists = Test-Path -LiteralPath $fullPath -PathType Leaf
    $nonEmpty = $exists -and (Get-Item -LiteralPath $fullPath).Length -gt 0
    [ordered]@{
        path = $requiredPath.Replace('\', '/')
        exists = $exists
        nonEmpty = $nonEmpty
    }
}

$passed = @($results | Where-Object { -not $_.exists -or -not $_.nonEmpty }).Count -eq 0
$report = [ordered]@{
    schemaVersion = '1.0.0'
    gate = $GateName
    status = if ($passed) { 'PASS' } else { 'FAIL' }
    checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    files = @($results)
}
Write-Utf8Json -Value $report -Path $ReportPath

Write-Output "REQUIRED-FILE-GATE gate=$GateName status=$($report.status) files=$(@($results).Count)"
if (-not $passed) {
    $results | Where-Object { -not $_.exists -or -not $_.nonEmpty } | ForEach-Object {
        Write-Output "FAIL  missing-or-empty $($_.path)"
    }
    throw "Required-file gate '$GateName' failed."
}
