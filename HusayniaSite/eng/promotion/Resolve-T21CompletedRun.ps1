[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [Parameter(Mandatory = $true)][string]$RunId,
    [Parameter(Mandatory = $true)][string]$ExpectedRepository,
    [Parameter(Mandatory = $true)][string]$ExpectedRepositoryId,
    [Parameter(Mandatory = $true)][string]$ExpectedHeadSha,
    [string]$ExpectedMode = '',
    [string]$ExpectedStage = '',
    [string]$ExpectedSourceReleaseRunId = '',
    [string]$ExpectedAppSha256 = '',
    [string]$ExpectedPreflightRunId = '',
    [string]$ExpectedStageOperationInputsRunId = '',
    [string]$ExpectedMigrationAuthorizationRunId = '',
    [string]$ExpectedCtoAuthorizationRunId = '',
    [string]$ExpectedCorrelationId = '',
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [string]$FixturePath,
    [string]$TokenEnvironmentVariable = 'GH_TOKEN',
    [scriptblock]$RequestInvoker
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

function Assert-ExactProperties {
    param($Value, [string[]]$Expected, [string]$Label)

    if ($null -eq $Value -or @(Compare-Object ($Expected | Sort-Object) @(
                $Value.PSObject.Properties.Name | Sort-Object
            )).Count -ne 0) {
        throw "$Label does not exactly match the T21 completed-run contract."
    }
}

function Get-StrictUtc {
    param([string]$Value, [string]$Label)

    $parsed = [DateTimeOffset]::MinValue
    if ([string]::IsNullOrWhiteSpace($Value) -or
        $Value -notmatch
            '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?Z$' -or
        -not [DateTimeOffset]::TryParse(
            $Value,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal -bor
                [Globalization.DateTimeStyles]::AdjustToUniversal,
            [ref]$parsed)) {
        throw "$Label is not an exact GitHub API UTC timestamp."
    }
    return $parsed.ToUniversalTime()
}

function Get-CanonicalDispatchInputsSha256 {
    param([Parameter(Mandatory = $true)]$Inputs)

    $normalized = [ordered]@{
        mode = [string]$Inputs.mode
        stage = [string]$Inputs.stage
        sourceReleaseRunId = [string]$Inputs.sourceReleaseRunId
        expectedAppSha256 = [string]$Inputs.expectedAppSha256
        preflightRunId = [string]$Inputs.preflightRunId
        stageOperationInputsRunId = [string]$Inputs.stageOperationInputsRunId
        migrationAuthorizationRunId = [string]$Inputs.migrationAuthorizationRunId
        ctoAuthorizationRunId = [string]$Inputs.ctoAuthorizationRunId
        orchestrationCorrelationId = [string]$Inputs.orchestrationCorrelationId
    }
    $json = $normalized | ConvertTo-Json -Depth 10 -Compress
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($json)
    return ([Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($bytes)
    )).ToLowerInvariant()
}

function Invoke-T21ApiRequest {
    param(
        [ValidateSet('Get')][string]$Method,
        [string]$Uri,
        [hashtable]$Headers,
        [string]$OutFile
    )

    if ($null -ne $RequestInvoker) {
        return & $RequestInvoker -Method $Method -Uri $Uri -Headers $Headers -OutFile $OutFile
    }
    if ([string]::IsNullOrWhiteSpace($OutFile)) {
        return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers
    }
    Invoke-WebRequest -Method $Method -Uri $Uri -Headers $Headers -OutFile $OutFile
}

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Resolve-HusayniaRepositoryRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
if ($RunId -notmatch '^[1-9][0-9]*$' -or
    $ExpectedRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
    $ExpectedRepositoryId -notmatch '^[1-9][0-9]*$' -or
    $ExpectedHeadSha -notmatch '^[a-f0-9]{40}$' -or
    $ExpectedHeadSha -ceq ('0' * 40)) {
    throw 'T21 completed-run resolver expected API identity is malformed.'
}

$fixture = $null
if (-not [string]::IsNullOrWhiteSpace($FixturePath)) {
    if (-not (Test-Path -LiteralPath $FixturePath -PathType Leaf)) {
        throw 'T21 completed-run API fixture is missing.'
    }
    $fixture = Get-Content -LiteralPath $FixturePath -Raw |
        ConvertFrom-Json -DateKind String
    Assert-ExactProperties -Value $fixture -Expected @(
        'artifacts', 'attestation', 'jobs', 'run'
    ) -Label 'T21 completed-run API fixture'
    $run = $fixture.run
    $jobs = @($fixture.jobs)
    $artifacts = @($fixture.artifacts)
}
else {
    $token = [Environment]::GetEnvironmentVariable($TokenEnvironmentVariable)
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw 'GitHub Actions API token is absent for completed-run verification.'
    }
    $headers = @{
        Accept = 'application/vnd.github+json'
        Authorization = "Bearer $token"
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    $apiRoot = "https://api.github.com/repos/$ExpectedRepository/actions/runs/$RunId"
    $run = Invoke-T21ApiRequest -Method Get -Uri $apiRoot -Headers $headers
    $jobsResponse = Invoke-T21ApiRequest -Method Get `
        -Uri "$apiRoot/jobs?filter=latest&per_page=100" -Headers $headers
    $artifactResponse = Invoke-T21ApiRequest -Method Get `
        -Uri "$apiRoot/artifacts?per_page=100" -Headers $headers
    $jobs = @($jobsResponse.jobs)
    $artifacts = @($artifactResponse.artifacts)
    if ([int]$jobsResponse.total_count -ne $jobs.Count -or
        [int]$artifactResponse.total_count -ne $artifacts.Count) {
        throw 'T21 completed-run API metadata was truncated and cannot be authoritative.'
    }
}

$runRef = if ([string]$run.head_branch -match '^refs/') {
    [string]$run.head_branch
}
else {
    "refs/heads/$([string]$run.head_branch)"
}
$createdAt = Get-StrictUtc -Value ([string]$run.created_at) -Label 'Completed run creation'
$updatedAt = Get-StrictUtc -Value ([string]$run.updated_at) -Label 'Completed run update'
if ([string]$run.id -cne $RunId -or
    [string]$run.path -cne '.github/workflows/operation-evidence-producer.yml' -or
    [string]$run.event -cne 'workflow_dispatch' -or
    $runRef -cne 'refs/heads/main' -or
    [string]$run.head_sha -cne $ExpectedHeadSha -or
    [string]$run.status -cne 'completed' -or
    [string]$run.conclusion -cne 'success' -or
    [int]$run.run_attempt -lt 1 -or
    [string]$run.repository.full_name -cne $ExpectedRepository -or
    [string]$run.repository.id -cne $ExpectedRepositoryId -or
    [string]$run.head_repository.full_name -cne $ExpectedRepository -or
    [string]$run.head_repository.id -cne $ExpectedRepositoryId -or
    $updatedAt -lt $createdAt -or
    $updatedAt -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
    throw 'T21 completed-run API metadata is not the exact protected workflow_dispatch success.'
}

$completionJobs = @($jobs | Where-Object {
        [string]$_.name -ceq 'T21 canonical completed-run attestation'
    })
if ($completionJobs.Count -ne 1 -or
    [string]$completionJobs[0].run_id -cne $RunId -or
    [string]$completionJobs[0].head_sha -cne $ExpectedHeadSha -or
    [string]$completionJobs[0].status -cne 'completed' -or
    [string]$completionJobs[0].conclusion -cne 'success') {
    throw 'T21 completed-run API jobs do not contain one successful canonical attestation job.'
}

$artifactName = "t21-completed-run-$RunId-$([int]$run.run_attempt)"
$artifactMatches = @($artifacts | Where-Object {
        [string]$_.name -ceq $artifactName -and
        [string]$_.workflow_run.id -ceq $RunId -and
        [string]$_.workflow_run.repository_id -ceq $ExpectedRepositoryId -and
        [string]$_.workflow_run.head_repository_id -ceq $ExpectedRepositoryId -and
        [string]$_.workflow_run.head_branch -ceq 'main' -and
        [string]$_.workflow_run.head_sha -ceq $ExpectedHeadSha
    })
if ($artifactMatches.Count -ne 1 -or
    [string]$artifactMatches[0].id -notmatch '^[1-9][0-9]*$' -or
    [bool]$artifactMatches[0].expired -or
    [int64]$artifactMatches[0].size_in_bytes -lt 1 -or
    [string]$artifactMatches[0].digest -notmatch '^sha256:[a-f0-9]{64}$') {
    throw 'T21 completed-run artifact is missing, duplicate, expired, or lacks an immutable digest.'
}
$artifactCreatedAt = Get-StrictUtc -Value ([string]$artifactMatches[0].created_at) `
    -Label 'Completed-run artifact creation'
if ($artifactCreatedAt -lt $createdAt -or
    $artifactCreatedAt -gt $updatedAt.AddMinutes(5) -or
    $artifactCreatedAt -gt [DateTimeOffset]::UtcNow.AddMinutes(5) -or
    (-not [string]::IsNullOrWhiteSpace([string]$artifactMatches[0].expires_at) -and
        (Get-StrictUtc -Value ([string]$artifactMatches[0].expires_at) `
            -Label 'Completed-run artifact expiry') -le [DateTimeOffset]::UtcNow)) {
    throw 'T21 completed-run artifact timestamps are stale or expired.'
}

$output = New-CleanDirectory -Path $OutputRoot
$archivePath = Join-Path $output 'artifact.zip'
if ($null -ne $fixture) {
    if ([string]::IsNullOrWhiteSpace([string]$artifactMatches[0].archivePath) -or
        -not (Test-Path -LiteralPath ([string]$artifactMatches[0].archivePath) -PathType Leaf)) {
        throw 'T21 completed-run fixture archive is missing.'
    }
    Copy-Item -LiteralPath ([string]$artifactMatches[0].archivePath) `
        -Destination $archivePath
}
else {
    Invoke-T21ApiRequest -Method Get `
        -Uri ([string]$artifactMatches[0].archive_download_url) `
        -Headers $headers -OutFile $archivePath | Out-Null
}
$archiveSha = Get-Sha256Lower -Path $archivePath
if ($archiveSha -cne ([string]$artifactMatches[0].digest).Substring(7)) {
    throw 'T21 completed-run artifact digest changed before verification.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $entries = @($archive.Entries)
    if ($entries.Count -ne 1 -or
        [string]$entries[0].FullName -cne 't21-completed-run.json' -or
        [int64]$entries[0].Length -lt 2 -or
        [int64]$entries[0].Length -gt 1048576) {
        throw 'T21 completed-run archive does not contain exactly one bounded canonical manifest.'
    }
    $manifestPath = Join-Path $output 't21-completed-run.json'
    $inputStream = $entries[0].Open()
    try {
        $outputStream = [IO.File]::Create($manifestPath)
        try {
            $inputStream.CopyTo($outputStream)
        }
        finally {
            $outputStream.Dispose()
        }
    }
    finally {
        $inputStream.Dispose()
    }
}
finally {
    $archive.Dispose()
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw |
    ConvertFrom-Json -DateKind String
Assert-ExactProperties -Value $manifest -Expected @(
    'authority', 'dispatchInputs', 'dispatchInputsSha256', 'producerGraph',
    'repository', 'run', 'schemaVersion', 'workflow'
) -Label 'T21 completed-run manifest'
Assert-ExactProperties -Value $manifest.repository -Expected @('fullName', 'id') `
    -Label 'T21 completed-run repository'
Assert-ExactProperties -Value $manifest.workflow -Expected @(
    'event', 'headRef', 'headSha', 'path'
) -Label 'T21 completed-run workflow'
Assert-ExactProperties -Value $manifest.run -Expected @(
    'attempt', 'conclusion', 'id', 'status'
) -Label 'T21 completed-run run'
Assert-ExactProperties -Value $manifest.dispatchInputs -Expected @(
    'ctoAuthorizationRunId', 'expectedAppSha256', 'migrationAuthorizationRunId',
    'mode', 'orchestrationCorrelationId', 'preflightRunId', 'sourceReleaseRunId',
    'stage', 'stageOperationInputsRunId'
) -Label 'T21 completed-run dispatch inputs'
Assert-ExactProperties -Value $manifest.producerGraph -Expected @(
    'preflight', 'preparedInputs', 'stageOperations', 'validateInputShape'
) -Label 'T21 completed-run producer graph'

$inputs = $manifest.dispatchInputs
$mode = [string]$inputs.mode
$stage = [string]$inputs.stage
$selectors = @(
    [string]$inputs.sourceReleaseRunId,
    [string]$inputs.preflightRunId,
    [string]$inputs.stageOperationInputsRunId,
    [string]$inputs.migrationAuthorizationRunId
)
if ([string]$manifest.schemaVersion -cne '1.0.0' -or
    [string]$manifest.authority -cne
        'github-actions-api-and-sigstore-dispatch-inputs-v1' -or
    [string]$manifest.repository.fullName -cne $ExpectedRepository -or
    [string]$manifest.repository.id -cne $ExpectedRepositoryId -or
    [string]$manifest.workflow.path -cne [string]$run.path -or
    [string]$manifest.workflow.event -cne [string]$run.event -or
    [string]$manifest.workflow.headRef -cne $runRef -or
    [string]$manifest.workflow.headSha -cne [string]$run.head_sha -or
    [string]$manifest.run.id -cne $RunId -or
    [int]$manifest.run.attempt -ne [int]$run.run_attempt -or
    [string]$manifest.run.status -cne [string]$run.status -or
    [string]$manifest.run.conclusion -cne [string]$run.conclusion -or
    $mode -cnotin @('Preflight', 'PreparedInputs', 'StageOperations') -or
    $stage -cnotin @('Development', 'Staging') -or
    [string]$inputs.sourceReleaseRunId -notmatch '^[1-9][0-9]*$' -or
    [string]$inputs.expectedAppSha256 -notmatch '^[a-f0-9]{64}$' -or
    [string]$inputs.expectedAppSha256 -ceq ('0' * 64) -or
    (-not [string]::IsNullOrWhiteSpace(
            [string]$inputs.orchestrationCorrelationId) -and
        [string]$inputs.orchestrationCorrelationId -notmatch
            '^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$') -or
    [string]$manifest.dispatchInputsSha256 -cne
        (Get-CanonicalDispatchInputsSha256 -Inputs $inputs)) {
    throw 'T21 completed-run manifest is not bound to the exact API run and canonical input hash.'
}

$expectedGraph = switch ($mode) {
    'Preflight' { 'success,success,skipped,skipped' }
    'PreparedInputs' { 'success,skipped,success,skipped' }
    'StageOperations' { 'success,skipped,skipped,success' }
}
$actualGraph = @(
    [string]$manifest.producerGraph.validateInputShape,
    [string]$manifest.producerGraph.preflight,
    [string]$manifest.producerGraph.preparedInputs,
    [string]$manifest.producerGraph.stageOperations
) -join ','
if ($actualGraph -cne $expectedGraph) {
    throw 'T21 completed-run manifest does not attest the exact selected producer graph.'
}
if ($mode -cin @('Preflight', 'PreparedInputs')) {
    if (@($selectors[1..3] + @([string]$inputs.ctoAuthorizationRunId) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -ne 0) {
        throw 'T21 completed-run P/I manifest carries forbidden downstream selectors.'
    }
}
elseif (@($selectors | Where-Object {
            $_ -notmatch '^[1-9][0-9]*$'
        }).Count -ne 0 -or
    @($selectors | Select-Object -Unique).Count -ne $selectors.Count -or
    -not [string]::IsNullOrWhiteSpace([string]$inputs.ctoAuthorizationRunId)) {
    throw 'T21 completed-run StageOperations manifest is not exact nonproduction R/P/I/M with empty C.'
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedMode)) {
    $expectedValues = [ordered]@{
        mode = $ExpectedMode
        stage = $ExpectedStage
        sourceReleaseRunId = $ExpectedSourceReleaseRunId
        expectedAppSha256 = $ExpectedAppSha256
        preflightRunId = $ExpectedPreflightRunId
        stageOperationInputsRunId = $ExpectedStageOperationInputsRunId
        migrationAuthorizationRunId = $ExpectedMigrationAuthorizationRunId
        ctoAuthorizationRunId = $ExpectedCtoAuthorizationRunId
        orchestrationCorrelationId = $ExpectedCorrelationId
    }
    foreach ($property in $expectedValues.Keys) {
        if ([string]$inputs.$property -cne [string]$expectedValues[$property]) {
            throw "T21 completed-run immutable dispatch input mismatch: $property."
        }
    }
}

$signerWorkflow =
    "$ExpectedRepository/.github/workflows/operation-evidence-producer.yml"
$predicateType = 'https://slsa.dev/provenance/v1'
if ($null -ne $fixture) {
    $attestation = $fixture.attestation
    Assert-ExactProperties -Value $attestation -Expected @(
        'predicateType', 'signerDigest', 'signerWorkflow', 'sourceCommitSha',
        'sourceRef', 'status'
    ) -Label 'T21 completed-run attestation fixture'
    if ([string]$attestation.status -cne 'verified' -or
        [string]$attestation.predicateType -cne $predicateType -or
        [string]$attestation.signerWorkflow -cne $signerWorkflow -or
        [string]$attestation.signerDigest -cne $ExpectedHeadSha -or
        [string]$attestation.sourceRef -cne 'refs/heads/main' -or
        [string]$attestation.sourceCommitSha -cne $ExpectedHeadSha) {
        throw 'T21 completed-run attestation fixture is not bound to the exact protected workflow.'
    }
}
else {
    $null = & gh attestation verify $manifestPath --repo $ExpectedRepository `
        --signer-workflow $signerWorkflow --signer-digest $ExpectedHeadSha `
        --source-ref refs/heads/main --source-digest $ExpectedHeadSha `
        --predicate-type $predicateType --deny-self-hosted-runners 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw 'GitHub/Sigstore completed-run attestation verification failed.'
    }
    $attestation = [ordered]@{
        predicateType = $predicateType
        signerWorkflow = $signerWorkflow
        signerDigest = $ExpectedHeadSha
        sourceRef = 'refs/heads/main'
        sourceCommitSha = $ExpectedHeadSha
        status = 'verified'
    }
}

$verified = [ordered]@{
    schemaVersion = '1.0.0'
    authority = 'github-actions-api-artifact-digest-and-sigstore-v1'
    run = [ordered]@{
        repository = $ExpectedRepository
        repositoryId = $ExpectedRepositoryId
        workflowPath = [string]$run.path
        event = [string]$run.event
        ref = $runRef
        commitSha = [string]$run.head_sha
        runId = $RunId
        runAttempt = [int]$run.run_attempt
        status = [string]$run.status
        conclusion = [string]$run.conclusion
    }
    artifact = [ordered]@{
        id = [string]$artifactMatches[0].id
        name = [string]$artifactMatches[0].name
        archiveSha256 = $archiveSha
    }
    dispatchInputs = $inputs
    dispatchInputsSha256 = [string]$manifest.dispatchInputsSha256
    producerGraph = $manifest.producerGraph
    attestation = $attestation
}
Write-Utf8Json -Value $verified -Path (
    Join-Path $output 'verified-completed-run.json') -Depth 20
Write-Output (
    "VERIFIED-T21-COMPLETED-RUN status=PASS runId=$RunId " +
    "mode=$mode stage=$stage dispatchInputsSha256=$($manifest.dispatchInputsSha256)"
)
