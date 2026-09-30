[CmdletBinding()]
param([string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}
$workflow = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'pipelines\github\pr-validation.yml') -Raw |
    ConvertFrom-Json -DateKind String
$prCommand = [string]@($workflow.jobs.validate.steps | Where-Object {
    [string]$_.name -ceq 'Run strict T21 harness with environmental blockers fail-closed'
})[0].run
$output = @(& (Get-Process -Id $PID).Path -NoProfile -File (
    Join-Path $RepositoryRoot 'eng\test\Invoke-T21Validation.ps1') -RepositoryRoot $RepositoryRoot 2>&1)
if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'SUMMARY total=17 passed=17 failed=0 blocked=0') {
    throw "T21 PR-shaped validation fixture does not return the required full success result: $($output -join ' | ')"
}
if ($prCommand -cne './eng/test/Invoke-T21Validation.ps1') {
    throw "T21 PR workflow command shape drifted from the checked-in full fixture: $prCommand"
}
Write-Output 'SUMMARY t21-validation-exit-codes total=2 passed=2 failed=0'
