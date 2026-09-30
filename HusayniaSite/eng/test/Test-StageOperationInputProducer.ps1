[CmdletBinding()]
param([string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Resolve-HusayniaRepositoryRoot }
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$passed = 0
$failed = 0

function Assert-Contract([string]$Name, [bool]$Condition, [string]$Failure) {
    if ($Condition) { $script:passed++; Write-Output "PASS  $Name" }
    else { $script:failed++; Write-Output "FAIL  $Name :: $Failure" }
}

$workflowRoot = Join-Path $RepositoryRoot 'pipelines\github'
$producer = Get-Content -LiteralPath (Join-Path $workflowRoot 'stage-operation-inputs.yml') -Raw |
    ConvertFrom-Json -DateKind String
$manual = Get-Content -LiteralPath (Join-Path $workflowRoot 'operation-evidence-producer.yml') -Raw |
    ConvertFrom-Json -DateKind String
$production = Get-Content -LiteralPath (Join-Path $workflowRoot 'production-operation-evidence.yml') -Raw |
    ConvertFrom-Json -DateKind String
$release = Get-Content -LiteralPath (Join-Path $workflowRoot 'release-build-and-nonproduction.yml') -Raw |
    ConvertFrom-Json -DateKind String
$coordinator = Get-Content -LiteralPath (
    Join-Path $workflowRoot 'automatic-nonproduction-orchestration.yml') -Raw |
    ConvertFrom-Json -DateKind String
$coordinatorScript = Get-Content -LiteralPath (
    Join-Path $RepositoryRoot 'eng\promotion\Invoke-T21CompletedRunCoordinator.ps1') -Raw
$producerText = Get-Content -LiteralPath (Join-Path $workflowRoot 'stage-operation-inputs.yml') -Raw
$allWorkflowText = (Get-ChildItem -LiteralPath $workflowRoot -Filter *.yml |
    Get-Content -Raw) -join "`n"
$producerSteps = @($producer.jobs.prepare.steps)
$reportSteps = @($producerSteps | Where-Object {
        [string]$_.name -ceq 'Generate inert role-derived prepared input reports and bundle'
    })

Assert-Contract 'stage-input-producer-workflow-materialized' ($null -ne $producer.jobs.prepare) `
    'The reusable prepared-input producer is missing.'
Assert-Contract 'stage-input-producer-inputs-are-release-only-plus-production-request' (
    @(Compare-Object @('ctoApprovalReference','expectedAppSha256','sourceReleaseRunId','stage') @(
        $producer.on.workflow_call.inputs.PSObject.Properties.Name | Sort-Object)).Count -eq 0
) 'Prepared inputs retained preflight/CTO selectors or an unreviewed input.'
Assert-Contract 'stage-input-producer-is-disabled-and-least-privilege' (
    [string]$producer.jobs.prepare.if -ceq
        '${{ inputs.stage == ''Development'' || inputs.stage == ''Staging'' || inputs.stage == ''Production'' }}' -and
    (@($producer.jobs.prepare.permissions.PSObject.Properties.Name | Sort-Object) -join ',') -ceq
        'actions,attestations,contents,id-token' -and
    [string]$producer.jobs.prepare.permissions.actions -ceq 'read' -and
    [string]$producer.jobs.prepare.permissions.attestations -ceq 'write' -and
    [string]$producer.jobs.prepare.permissions.contents -ceq 'read' -and
    [string]$producer.jobs.prepare.permissions.'id-token' -ceq 'write'
) 'Prepared-input producer stage guard or permissions broadened; wrapper/policy disablement remains authoritative.'
Assert-Contract 'stage-input-producer-uses-stage-exact-protected-environment-and-step-secret' (
    [string]$producer.jobs.prepare.environment.name -ceq
        '${{ inputs.stage == ''Production'' && ''Husaynia-Production'' || format(''Husaynia-{0}-Operations'', inputs.stage) }}' -and
    $reportSteps.Count -eq 1 -and
    [string]$reportSteps[0].env.T21_STAGE_READONLY_PROBE_TOKEN -ceq
        '${{ secrets.T21_STAGE_READONLY_PROBE_TOKEN }}' -and
    @($producerSteps | Where-Object {
            $null -ne $_.PSObject.Properties['env'] -and
            $null -ne $_.env.PSObject.Properties['T21_STAGE_READONLY_PROBE_TOKEN']
        }).Count -eq 1
) 'Prepared inputs lack the exact stage environment or expose the read-only probe token beyond the report step.'
Assert-Contract 'stage-input-producer-uses-canonical-child-root-and-pwsh' (
    [string]$producer.jobs.prepare.env.HUSAYNIA_REPOSITORY_ROOT -ceq
        '${{ github.workspace }}/HusayniaSite' -and
    [string]$producer.jobs.prepare.defaults.run.shell -ceq 'pwsh' -and
    [string]$producer.jobs.prepare.defaults.run.'working-directory' -ceq 'HusayniaSite' -and
    $producerText.Contains('canonical Dreamer/HusayniaSite checkout root')
) 'Prepared-input checkout does not validate the canonical child root.'
Assert-Contract 'stage-input-producer-resolves-only-completed-release-with-consumer-id' (
    ([regex]::Matches($producerText, 'Resolve-GitHubArtifactProvenance\.ps1')).Count -eq 1 -and
    ([regex]::Matches($producerText, '-ConsumerRunId \$env:GITHUB_RUN_ID')).Count -eq 1 -and
    -not $producerText.Contains('preflightRunId') -and
    -not $producerText.Contains('ctoAuthorizationRunId')
) 'Prepared inputs select same-run or authorization artifacts.'
Assert-Contract 'stage-input-v21-request-has-no-change-record-placeholder' (
    $producerText.Contains('-CtoApprovalReference $env:T21_CTO_APPROVAL_REFERENCE') -and
    $producerText.Contains("'change-record'") -and
    $producerText.Contains("Where-Object { `$_ -notin") -and
    -not $producerText.Contains('ProductionAuthorizationContextPath') -and
    -not $producerText.Contains('source-change-record.json')
) 'Prepared inputs retained pre-CTO change-record ownership or a success placeholder.'
Assert-Contract 'stage-input-producer-pin-is-exact' (
    $producerText.Contains(
        'actions/attest-build-provenance@e8998f949152b193b063cb0ec769d69d929409be')
) 'Prepared-input attestation action is not the frozen existing commit.'

$expectedModes = @('Preflight','PreparedInputs','StageOperations')
foreach ($wrapper in @($manual, $production)) {
    $expectedJobs = if ([string]$wrapper.name -ceq
        'T21 disabled nonproduction operation wrapper') {
        'preflight,prepared-inputs,record-completion,stage-operations,validate-input-shape'
    }
    else {
        'preflight,prepared-inputs,stage-operations,validate-input-shape'
    }
    Assert-Contract "wrapper-$([string]$wrapper.name)-has-three-exclusive-modes" (
        @(Compare-Object $expectedModes @($wrapper.on.workflow_dispatch.inputs.mode.options)).Count -eq 0 -and
        @($wrapper.jobs.PSObject.Properties.Name | Sort-Object) -join ',' -ceq
            $expectedJobs
    ) 'A wrapper does not execute exactly one frozen producer mode.'
}
Assert-Contract 'nonproduction-wrapper-stageoperations-selects-distinct-prior-runs' (
    [string]$manual.jobs.'stage-operations'.with.preflightRunId -ceq '${{ inputs.preflightRunId }}' -and
    [string]$manual.jobs.'stage-operations'.with.stageOperationInputsRunId -ceq
        '${{ inputs.stageOperationInputsRunId }}' -and
    [string]$manual.jobs.'stage-operations'.with.migrationAuthorizationRunId -ceq
        '${{ inputs.migrationAuthorizationRunId }}' -and
    -not (($manual.jobs.'stage-operations' | ConvertTo-Json -Depth 40).
            Contains('${{ github.run_id }}'))
) 'Nonproduction StageOperations still selects its current run.'
Assert-Contract 'production-wrapper-binds-completed-rpicm' (
    [string]$production.jobs.'prepared-inputs'.with.ctoApprovalReference -ceq
        '${{ inputs.ctoApprovalReference }}' -and
    [string]$production.jobs.'stage-operations'.with.ctoAuthorizationRunId -ceq
        '${{ inputs.ctoAuthorizationRunId }}' -and
    [string]$production.jobs.'stage-operations'.with.migrationAuthorizationRunId -ceq
        '${{ inputs.migrationAuthorizationRunId }}' -and
    -not (($production | ConvertTo-Json -Depth 40).Contains('${{ github.run_id }}'))
) 'Production wrapper does not preserve the distinct completed R/P/I/C/M contract.'
Assert-Contract 'release-is-sole-c6-builder' (
    @($release.jobs.PSObject.Properties.Name).Count -eq 1 -and
    $null -ne $release.jobs.build_release -and
    ([regex]::Matches($allWorkflowText, 'Invoke-PrValidation\.ps1')).Count -eq 2
) 'A downstream workflow rebuilds C6 or release still embeds producers.'
Assert-Contract 'coordinator-is-nonprivileged-cycle-free-and-nonproduction-only' (
    (@($coordinator.permissions.PSObject.Properties.Name | Sort-Object) -join ',') -ceq
        'actions,attestations,contents' -and
    [string]$coordinator.permissions.actions -ceq 'write' -and
    [string]$coordinator.permissions.attestations -ceq 'read' -and
    [string]$coordinator.permissions.contents -ceq 'read' -and
    -not (($coordinator | ConvertTo-Json -Depth 40).Contains('production-operation-evidence.yml')) -and
    -not (@($coordinator.on.workflow_run.workflows) -contains
        'T21 automatic nonproduction completed-run coordinator')
) 'Coordinator gained privilege, Production automation, or an event cycle.'
Assert-Contract 'coordinator-is-idempotent-bounded-and-duplicate-fail-closed' (
    $coordinatorScript.Contains('duplicate canonical child runs') -and
    $coordinatorScript.Contains('$attempt -lt 60') -and
    $coordinatorScript.Contains('Resolve-T21CompletedRun.ps1') -and
    -not $coordinatorScript.Contains('display_title') -and
    $coordinatorScript.Contains('T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN')
) 'Coordinator can duplicate, wait forever, or bypass the forbidden Staging receipt boundary.'

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "t21-root-$([guid]::NewGuid().ToString('N'))"
try {
    $workspace = Join-Path $fixtureRoot 'Dreamer'
    $child = Join-Path $workspace 'HusayniaSite'
    New-Item -ItemType Directory -Path (Join-Path $child 'eng'), (Join-Path $child 'pipelines') -Force |
        Out-Null
    [IO.File]::WriteAllText((Join-Path $child 'HusayniaSite.sln'), 'fixture')
    $oldWorkspace = $env:GITHUB_WORKSPACE
    $oldRoot = $env:HUSAYNIA_REPOSITORY_ROOT
    $env:GITHUB_WORKSPACE = $workspace
    $env:HUSAYNIA_REPOSITORY_ROOT = $child
    Assert-Contract 'dreamer-husaynia-child-root-resolves' (
        (Resolve-HusayniaRepositoryRoot) -ceq (Resolve-Path -LiteralPath $child).Path
    ) 'The canonical Dreamer/HusayniaSite fixture did not resolve.'
    $env:HUSAYNIA_REPOSITORY_ROOT = $workspace
    $wrongRejected = $false
    try { $null = Resolve-HusayniaRepositoryRoot } catch { $wrongRejected = $true }
    Assert-Contract 'workspace-root-is-rejected-before-producer-effects' $wrongRejected `
        'The Dreamer workspace root was accepted as the Husaynia project root.'
}
finally {
    $env:GITHUB_WORKSPACE = $oldWorkspace
    $env:HUSAYNIA_REPOSITORY_ROOT = $oldRoot
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output "SUMMARY stage-operation-input-producer total=$($passed + $failed) passed=$passed failed=$failed auth=0 dispatches=0 deployments=0 database=0 resources=0"
if ($failed -gt 0) { exit 1 }
