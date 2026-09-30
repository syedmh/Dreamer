[CmdletBinding()]
param(
    [string]$DevelopmentReceiptPath,
    [string]$ExpectedArtifactRoot,
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ExpectedAppSha256,
    [string]$PolicyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

$repositoryRoot = Resolve-HusayniaRepositoryRoot
if ([string]::IsNullOrWhiteSpace($PolicyPath)) {
    $PolicyPath = Join-Path $repositoryRoot 'pipelines\config\promotion-policy.json'
}

if ([Environment]::GetEnvironmentVariable('GITHUB_ACTIONS') -ne 'true' -or
    [Environment]::GetEnvironmentVariable('GITHUB_EVENT_NAME') -ne 'push') {
    throw 'Automatic promotion requires the protected push workflow context.'
}

$requiredEnvironment = @(
    'GITHUB_EVENT_PATH',
    'GITHUB_REPOSITORY',
    'GITHUB_WORKFLOW_REF',
    'GITHUB_WORKFLOW',
    'GITHUB_RUN_ID',
    'GITHUB_RUN_ATTEMPT',
    'GITHUB_REF',
    'GITHUB_SHA',
    'GITHUB_ACTOR',
    'GITHUB_ACTOR_ID',
    'GITHUB_TRIGGERING_ACTOR'
)
foreach ($name in $requiredEnvironment) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "Automatic promotion GitHub context is empty: $name"
    }
}

$runAttempt = [Environment]::GetEnvironmentVariable('GITHUB_RUN_ATTEMPT')
if ($runAttempt -cne '1') {
    throw 'Automatic promotion run attempt must be exactly 1; reruns are denied.'
}

$actor = [Environment]::GetEnvironmentVariable('GITHUB_ACTOR')
$triggeringActor = [Environment]::GetEnvironmentVariable('GITHUB_TRIGGERING_ACTOR')
if (-not [string]::Equals($actor, $triggeringActor, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Automatic promotion triggering actor does not match the original actor.'
}

$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
$provenance = $policy.provenance
$repository = [Environment]::GetEnvironmentVariable('GITHUB_REPOSITORY')
$workflowRef = [Environment]::GetEnvironmentVariable('GITHUB_WORKFLOW_REF')
$runId = [Environment]::GetEnvironmentVariable('GITHUB_RUN_ID')
$ref = [Environment]::GetEnvironmentVariable('GITHUB_REF')
$commitSha = [Environment]::GetEnvironmentVariable('GITHUB_SHA').ToLowerInvariant()
$actorId = [Environment]::GetEnvironmentVariable('GITHUB_ACTOR_ID')
$workflowPrefix = "$repository/"
$workflowSeparator = $workflowRef.LastIndexOf('@')
if (-not $workflowRef.StartsWith($workflowPrefix, [StringComparison]::Ordinal) -or
    $workflowSeparator -le $workflowPrefix.Length) {
    throw 'Automatic promotion workflow_ref is malformed.'
}
$workflowPath = $workflowRef.Substring(
    $workflowPrefix.Length,
    $workflowSeparator - $workflowPrefix.Length)
$workflowRevision = $workflowRef.Substring($workflowSeparator + 1)

if ($repository -cne [string]$provenance.repository -or
    $workflowPath -cne [string]$provenance.releaseWorkflowPath -or
    $workflowRevision -cne [string]$provenance.protectedRef -or
    $ref -cne [string]$provenance.protectedRef -or
    $runId -notmatch '^[1-9][0-9]*$' -or
    $commitSha -notmatch '^[a-f0-9]{40}$' -or
    $commitSha -match '^0{40}$' -or
    $actorId -notmatch '^[1-9][0-9]*$') {
    throw 'Automatic promotion context is not the approved repository, workflow, protected ref, or immutable run identity.'
}

$eventPath = [Environment]::GetEnvironmentVariable('GITHUB_EVENT_PATH')
if (-not (Test-Path -LiteralPath $eventPath -PathType Leaf)) {
    throw 'Automatic promotion push event payload is missing.'
}
try {
    $event = Get-Content -LiteralPath $eventPath -Raw | ConvertFrom-Json
}
catch {
    throw "Automatic promotion push event payload is invalid JSON: $($_.Exception.Message)"
}

$eventDeleted = Get-PropertyValue $event 'deleted'
if ([string](Get-PropertyValue $event 'ref') -cne $ref -or
    [string](Get-PropertyValue (Get-PropertyValue $event 'repository') 'full_name') -cne $repository -or
    $null -eq $eventDeleted -or
    [bool]$eventDeleted) {
    throw 'Automatic promotion context does not match the protected push event repository and ref.'
}
$eventCommitSha = [string](Get-PropertyValue $event 'after')
if ($eventCommitSha -notmatch '^[a-fA-F0-9]{40}$' -or
    $eventCommitSha.ToLowerInvariant() -cne $commitSha) {
    throw 'Automatic promotion source SHA does not match the protected push event SHA.'
}
$eventSender = Get-PropertyValue $event 'sender'
if (-not [string]::Equals(
        [string](Get-PropertyValue $eventSender 'login'),
        $actor,
        [StringComparison]::OrdinalIgnoreCase) -or
    [string](Get-PropertyValue $eventSender 'id') -cne $actorId) {
    throw 'Automatic promotion actor does not match the protected push event sender.'
}

$receiptValidationRequested =
    -not [string]::IsNullOrWhiteSpace($DevelopmentReceiptPath) -or
    -not [string]::IsNullOrWhiteSpace($ExpectedArtifactRoot) -or
    -not [string]::IsNullOrWhiteSpace($ExpectedAppSha256)
if ($receiptValidationRequested) {
    if ([string]::IsNullOrWhiteSpace($DevelopmentReceiptPath) -or
        [string]::IsNullOrWhiteSpace($ExpectedArtifactRoot) -or
        [string]::IsNullOrWhiteSpace($ExpectedAppSha256) -or
        -not (Test-Path -LiteralPath $DevelopmentReceiptPath -PathType Leaf)) {
        throw 'Automatic Development receipt validation inputs are incomplete.'
    }

    try {
        $receipt = Get-Content -LiteralPath $DevelopmentReceiptPath -Raw |
            ConvertFrom-Json -DateKind String
    }
    catch {
        throw "Automatic Development receipt is invalid JSON: $($_.Exception.Message)"
    }

    function Test-CurrentRunBinding {
        param($Binding)

        return $null -ne $Binding -and
            [string](Get-PropertyValue $Binding 'repository') -ceq $repository -and
            [string](Get-PropertyValue $Binding 'workflowPath') -ceq $workflowPath -and
            [string](Get-PropertyValue $Binding 'runId') -ceq $runId -and
            [string](Get-PropertyValue $Binding 'runAttempt') -ceq '1' -and
            [string](Get-PropertyValue $Binding 'ref') -ceq $ref -and
            [string](Get-PropertyValue $Binding 'commitSha') -ceq $commitSha
    }

    $sourceReleaseRun = Get-PropertyValue $receipt 'sourceReleaseRun'
    $evidenceRun = Get-PropertyValue $receipt 'evidenceRun'
    $promotionRun = Get-PropertyValue $receipt 'promotionRun'
    $deploymentExecutedProperty = $receipt.PSObject.Properties['deploymentExecuted']
    if ([string](Get-PropertyValue $receipt 'schemaVersion') -cne '2.0.0' -or
        [string](Get-PropertyValue $receipt 'stage') -cne 'Development' -or
        [string](Get-PropertyValue $receipt 'status') -cne 'PASS' -or
        [bool](Get-PropertyValue $receipt 'promotionReady') -ne $true -or
        $null -eq $deploymentExecutedProperty -or
        [bool](Get-PropertyValue $receipt 'deploymentExecuted') -ne $false -or
        [string](Get-PropertyValue $receipt 'artifactRoot') -cne $ExpectedArtifactRoot -or
        [string](Get-PropertyValue $receipt 'appSha256') -cne $ExpectedAppSha256.ToLowerInvariant() -or
        [string](Get-PropertyValue $receipt 'releaseCommitSha') -cne $commitSha -or
        -not (Test-CurrentRunBinding -Binding $sourceReleaseRun) -or
        -not (Test-CurrentRunBinding -Binding $evidenceRun) -or
        -not (Test-CurrentRunBinding -Binding $promotionRun) -or
        [string](Get-PropertyValue $sourceReleaseRun 'event') -cne 'push' -or
        [string](Get-PropertyValue $sourceReleaseRun 'actorId') -cne $actorId -or
        -not [string]::Equals(
            [string](Get-PropertyValue $sourceReleaseRun 'triggeringActor'),
            $triggeringActor,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Automatic Development receipt is not bound to the current protected push run.'
    }
}

Write-Output "AUTOMATIC-PROMOTION-CONTEXT status=PASS runId=$runId runAttempt=1 ref=$ref commitSha=$commitSha"
