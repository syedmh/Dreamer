[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Preflight', 'PreparedInputs', 'StageOperations')]
    [string]$Mode,
    [Parameter(Mandatory = $true)]
    [ValidateSet('Development', 'Staging')]
    [string]$Stage,
    [Parameter(Mandatory = $true)][string]$SourceReleaseRunId,
    [Parameter(Mandatory = $true)][string]$ExpectedAppSha256,
    [string]$PreflightRunId = '',
    [string]$StageOperationInputsRunId = '',
    [string]$MigrationAuthorizationRunId = '',
    [string]$CtoAuthorizationRunId = '',
    [string]$OrchestrationCorrelationId = '',
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$RepositoryId,
    [Parameter(Mandatory = $true)][string]$WorkflowPath,
    [Parameter(Mandatory = $true)][string]$EventName,
    [Parameter(Mandatory = $true)][string]$HeadRef,
    [Parameter(Mandatory = $true)][string]$HeadSha,
    [Parameter(Mandatory = $true)][string]$RunId,
    [Parameter(Mandatory = $true)][int]$RunAttempt,
    [Parameter(Mandatory = $true)][string]$ValidateInputShapeResult,
    [Parameter(Mandatory = $true)][string]$PreflightResult,
    [Parameter(Mandatory = $true)][string]$PreparedInputsResult,
    [Parameter(Mandatory = $true)][string]$StageOperationsResult,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

function Get-CanonicalDispatchInputsSha256 {
    param([Parameter(Mandatory = $true)]$Inputs)

    $json = $Inputs | ConvertTo-Json -Depth 10 -Compress
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($json)
    return ([Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($bytes)
    )).ToLowerInvariant()
}

if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
    $RepositoryId -notmatch '^[1-9][0-9]*$' -or
    $WorkflowPath -cne '.github/workflows/operation-evidence-producer.yml' -or
    $EventName -cne 'workflow_dispatch' -or
    $HeadRef -cne 'refs/heads/main' -or
    $HeadSha -notmatch '^[a-f0-9]{40}$' -or
    $HeadSha -ceq ('0' * 40) -or
    $RunId -notmatch '^[1-9][0-9]*$' -or
    $RunAttempt -lt 1 -or
    $SourceReleaseRunId -notmatch '^[1-9][0-9]*$' -or
    $ExpectedAppSha256 -notmatch '^[a-f0-9]{64}$' -or
    $ExpectedAppSha256 -ceq ('0' * 64) -or
    (-not [string]::IsNullOrWhiteSpace($OrchestrationCorrelationId) -and
        $OrchestrationCorrelationId -notmatch
            '^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$')) {
    throw 'T21 completed-run manifest identity or base dispatch input is malformed.'
}

$producerGraph = [ordered]@{
    validateInputShape = $ValidateInputShapeResult
    preflight = $PreflightResult
    preparedInputs = $PreparedInputsResult
    stageOperations = $StageOperationsResult
}
$expectedGraph = switch ($Mode) {
    'Preflight' {
        [ordered]@{
            validateInputShape = 'success'
            preflight = 'success'
            preparedInputs = 'skipped'
            stageOperations = 'skipped'
        }
    }
    'PreparedInputs' {
        [ordered]@{
            validateInputShape = 'success'
            preflight = 'skipped'
            preparedInputs = 'success'
            stageOperations = 'skipped'
        }
    }
    'StageOperations' {
        [ordered]@{
            validateInputShape = 'success'
            preflight = 'skipped'
            preparedInputs = 'skipped'
            stageOperations = 'success'
        }
    }
}
if (@(Compare-Object @(
            $expectedGraph.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }
        ) @(
            $producerGraph.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }
        )).Count -ne 0) {
    throw 'T21 completed-run producer graph is not the exact successful selected-mode graph.'
}

$emptySelectors = @(
    $PreflightRunId,
    $StageOperationInputsRunId,
    $MigrationAuthorizationRunId,
    $CtoAuthorizationRunId
)
if ($Mode -cin @('Preflight', 'PreparedInputs')) {
    if (@($emptySelectors | Where-Object {
                -not [string]::IsNullOrWhiteSpace([string]$_)
            }).Count -ne 0) {
        throw 'T21 completed-run P/I modes must not carry downstream selectors.'
    }
}
else {
    $stageSelectors = @(
        $SourceReleaseRunId,
        $PreflightRunId,
        $StageOperationInputsRunId,
        $MigrationAuthorizationRunId
    )
    if (@($stageSelectors | Where-Object {
                [string]$_ -notmatch '^[1-9][0-9]*$'
            }).Count -ne 0 -or
        @($stageSelectors | Select-Object -Unique).Count -ne $stageSelectors.Count -or
        -not [string]::IsNullOrWhiteSpace($CtoAuthorizationRunId)) {
        throw 'T21 completed-run StageOperations selectors are not exact nonproduction R/P/I/M with empty C.'
    }
}

$dispatchInputs = [ordered]@{
    mode = $Mode
    stage = $Stage
    sourceReleaseRunId = $SourceReleaseRunId
    expectedAppSha256 = $ExpectedAppSha256
    preflightRunId = $PreflightRunId
    stageOperationInputsRunId = $StageOperationInputsRunId
    migrationAuthorizationRunId = $MigrationAuthorizationRunId
    ctoAuthorizationRunId = $CtoAuthorizationRunId
    orchestrationCorrelationId = $OrchestrationCorrelationId
}
$manifest = [ordered]@{
    schemaVersion = '1.0.0'
    authority = 'github-actions-api-and-sigstore-dispatch-inputs-v1'
    repository = [ordered]@{
        fullName = $Repository
        id = $RepositoryId
    }
    workflow = [ordered]@{
        path = $WorkflowPath
        event = $EventName
        headRef = $HeadRef
        headSha = $HeadSha
    }
    run = [ordered]@{
        id = $RunId
        attempt = $RunAttempt
        status = 'completed'
        conclusion = 'success'
    }
    dispatchInputs = $dispatchInputs
    dispatchInputsSha256 = Get-CanonicalDispatchInputsSha256 -Inputs $dispatchInputs
    producerGraph = $producerGraph
}
Write-Utf8Json -Value $manifest -Path $OutputPath -Depth 20
Write-Output (
    "T21-COMPLETED-RUN-MANIFEST status=PASS runId=$RunId attempt=$RunAttempt " +
    "mode=$Mode stage=$Stage dispatchInputsSha256=$($manifest.dispatchInputsSha256)"
)
