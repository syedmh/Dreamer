[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[1-9][0-9]*$')]
    [string]$RunId,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$TokenEnvironmentVariable = 'GITHUB_TOKEN'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

$token = [Environment]::GetEnvironmentVariable($TokenEnvironmentVariable)
if ([string]::IsNullOrWhiteSpace($token)) {
    throw "GitHub API token environment variable is empty: $TokenEnvironmentVariable"
}

$headers = @{
    Accept = 'application/vnd.github+json'
    Authorization = "Bearer $token"
    'X-GitHub-Api-Version' = '2022-11-28'
}
$uri = "https://api.github.com/repos/$Repository/actions/runs/$RunId"
$run = Invoke-RestMethod -Method Get -Uri $uri -Headers $headers

if ([string]$run.repository.full_name -ne $Repository -or [string]$run.id -ne $RunId) {
    throw 'GitHub run metadata response did not match the requested repository and run ID.'
}

$ref = if ([string]::IsNullOrWhiteSpace([string]$run.head_branch)) {
    ''
}
elseif ([string]$run.head_branch -match '^refs/') {
    [string]$run.head_branch
}
else {
    "refs/heads/$($run.head_branch)"
}

$metadata = [ordered]@{
    schemaVersion = '1.0.0'
    repository = [string]$run.repository.full_name
    workflowPath = [string]$run.path
    workflowName = [string]$run.name
    runId = [string]$run.id
    runAttempt = [int]$run.run_attempt
    ref = $ref
    commitSha = ([string]$run.head_sha).ToLowerInvariant()
    event = [string]$run.event
    actor = [string]$run.actor.login
    actorId = [string]$run.actor.id
    triggeringActor = [string]$run.triggering_actor.login
    triggeringActorId = [string]$run.triggering_actor.id
    status = [string]$run.status
    conclusion = [string]$run.conclusion
    createdAtUtc = ([DateTimeOffset]::Parse([string]$run.created_at)).ToUniversalTime().ToString('O')
    updatedAtUtc = ([DateTimeOffset]::Parse([string]$run.updated_at)).ToUniversalTime().ToString('O')
}
Write-Utf8Json -Value $metadata -Path $OutputPath
Write-Output "RUN-METADATA repository=$Repository runId=$RunId workflow=$($metadata.workflowPath) conclusion=$($metadata.conclusion)"
