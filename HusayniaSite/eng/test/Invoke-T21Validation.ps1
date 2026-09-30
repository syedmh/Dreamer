[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [switch]$ContractOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Resolve-HusayniaRepositoryRoot }
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$passed = 0
$failed = 0
$blocked = 0
function Invoke-T21Check {
    param([string]$Name, [string]$Path)
    try {
        $output = @(& (Get-Process -Id $PID).Path -NoProfile -File $Path -RepositoryRoot $RepositoryRoot 2>&1)
        if ($LASTEXITCODE -ne 0) { throw ($output -join ' | ') }
        $script:passed++
        Write-Output "PASS  $Name :: $($output | Select-Object -Last 1)"
    }
    catch {
        $script:failed++
        Write-Output "FAIL  $Name :: $($_.Exception.Message)"
    }
}
Invoke-T21Check -Name 'pipeline-definition-contracts' -Path (Join-Path $RepositoryRoot 'eng\ci\Test-PipelineDefinitions.ps1')
Invoke-T21Check -Name 'github-action-pin-contracts' -Path (Join-Path $RepositoryRoot 'eng\ci\Test-GitHubActionPins.ps1')
Invoke-T21Check -Name 'protected-bundle-contracts' -Path (Join-Path $RepositoryRoot 'eng\test\Test-ProtectedExecutionBundle.ps1')
Invoke-T21Check -Name 'github-provenance-contracts' -Path (Join-Path $RepositoryRoot 'eng\test\Test-GitHubArtifactProvenance.ps1')
Invoke-T21Check -Name 'cto-authorization-provenance-contracts' -Path (Join-Path $RepositoryRoot 'eng\test\Test-CtoAuthorizationProvenance.ps1')
Invoke-T21Check -Name 'preflight-policy-contracts' -Path (Join-Path $RepositoryRoot 'eng\test\Test-PreflightPolicyBoundary.ps1')
Invoke-T21Check -Name 'r10-enabled-path-regressions' -Path (Join-Path $RepositoryRoot 'eng\test\Test-T21R10EnabledPathRegressions.ps1')
Invoke-T21Check -Name 'completed-run-lifecycle-state-machine' -Path (Join-Path $RepositoryRoot 'eng\test\Test-T21LifecycleStateMachine.ps1')
Invoke-T21Check -Name 'r12-completed-run-reuse-contracts' -Path (Join-Path $RepositoryRoot 'eng\test\Test-T21R12CompletedRunReuse.ps1')
Invoke-T21Check -Name 'stage-operation-input-producer-contracts' -Path (Join-Path $RepositoryRoot 'eng\test\Test-StageOperationInputProducer.ps1')
Invoke-T21Check -Name 'stage-operation-input-bundle-contracts' -Path (Join-Path $RepositoryRoot 'eng\test\Test-StageOperationInputBundle.ps1')
Invoke-T21Check -Name 'migration-authorization-provenance-contracts' -Path (Join-Path $RepositoryRoot 'eng\test\Test-MigrationAuthorizationProvenance.ps1')
Invoke-T21Check -Name 'trusted-stage-operations-semantics-contracts' -Path (Join-Path $RepositoryRoot 'eng\test\Test-TrustedStageOperationsSemantics.ps1')
Invoke-T21Check -Name 'migration-stage-lease-fence-contracts' -Path (Join-Path $RepositoryRoot 'eng\test\Test-MigrationStageLeaseFence.ps1')
Invoke-T21Check -Name 'migration-sqlclient-execution-contracts' -Path (Join-Path $RepositoryRoot 'eng\test\Test-MigrationSqlClientExecution.ps1')
Invoke-T21Check -Name 'stage-operation-http-client-contracts' -Path (Join-Path $RepositoryRoot 'eng\test\Test-StageOperationHttpClient.ps1')

$secretReport = Join-Path ([IO.Path]::GetTempPath()) "t21-r4-secret-$([guid]::NewGuid().ToString('N')).json"
try {
    $secretOutput = @(& (Join-Path $RepositoryRoot 'eng\security\Invoke-SecretScan.ps1') -RepositoryRoot $RepositoryRoot `
        -ScanPaths @('pipelines', 'eng', '.config') -ReportPath $secretReport 2>&1)
    if (-not $?) { throw ($secretOutput -join ' | ') }
    $passed++
    Write-Output "PASS  t21-owned-secret-scan :: $($secretOutput | Select-Object -Last 1)"
}
catch {
    $failed++
    Write-Output "FAIL  t21-owned-secret-scan :: $($_.Exception.Message)"
}
finally {
    Remove-Item -LiteralPath $secretReport -Force -ErrorAction SilentlyContinue
}
Write-Output "SUMMARY total=$($passed + $failed) passed=$passed failed=$failed blocked=$blocked authentication=0 workflowDispatches=0 deployments=0 successReceipts=0 databaseCallsOrMutations=0 cloudResourceOrSecretMutations=0 installs=0 packageRestores=0 actualC6Builds=0"
if ($failed -gt 0) { exit 1 }
if ($blocked -gt 0 -and -not $ContractOnly) { exit 2 }
