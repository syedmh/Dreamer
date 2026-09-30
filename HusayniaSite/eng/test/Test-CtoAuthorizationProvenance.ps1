[CmdletBinding()]
param([string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Resolve-HusayniaRepositoryRoot }
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$passed = 0
$failed = 0
function Assert-Test([string]$Name,[bool]$Condition,[string]$Failure) {
    if ($Condition) { $script:passed++; Write-Output "PASS  $Name" }
    else { $script:failed++; Write-Output "FAIL  $Name :: $Failure" }
}

$workflowPath = Join-Path $RepositoryRoot 'pipelines\github\cto-authorization-record.yml'
$workflowText = Get-Content -LiteralPath $workflowPath -Raw
$workflow = $workflowText | ConvertFrom-Json -DateKind String
$writerPath = Join-Path $RepositoryRoot 'eng\promotion\New-CtoAuthorizationRecord.ps1'
$validatorPath = Join-Path $RepositoryRoot 'eng\promotion\Test-CtoAuthorizationRecord.ps1'
$writer = Get-Content -LiteralPath $writerPath -Raw
$validator = Get-Content -LiteralPath $validatorPath -Raw
$ctoSteps = @($workflow.jobs.'record-authorization'.steps)
$changeRecordSteps = @($ctoSteps | Where-Object {
        [string]$_.name -ceq 'Create checked Production change record and CTO authorization'
    })

Assert-Test 'cto-workflow-is-protected-disabled-manual' (
    $null -ne $workflow.on.workflow_dispatch -and
    [string]$workflow.jobs.'record-authorization'.if -ceq
        '${{ vars.T21_CTO_AUTHORIZATION_RECORD_ENABLED == ''true'' && vars.T21_PRODUCTION_DEPLOYMENT_ENABLED == ''true'' }}' -and
    [string]$workflow.jobs.'record-authorization'.environment -ceq 'Husaynia-CTO-Authorization'
) 'CTO authorization is not a protected disabled manual workflow.'
Assert-Test 'cto-workflow-maps-readonly-probe-secret-only-to-change-record-step' (
    $changeRecordSteps.Count -eq 1 -and
    [string]$changeRecordSteps[0].env.T21_STAGE_READONLY_PROBE_TOKEN -ceq
        '${{ secrets.T21_STAGE_READONLY_PROBE_TOKEN }}' -and
    @($ctoSteps | Where-Object {
            $null -ne $_.PSObject.Properties['env'] -and
            $null -ne $_.env.PSObject.Properties['T21_STAGE_READONLY_PROBE_TOKEN']
        }).Count -eq 1
) 'CTO authorization omits the protected read-only token or exposes it outside the change-record step.'
Assert-Test 'cto-workflow-inputs-are-exact-rpi-approval-contract' (
    @(Compare-Object @(
        'ctoApprovalReference','decision','expectedAppSha256','expiresAtUtc','preflightRunId',
        'sourceReleaseRunId','stageOperationInputsRunId'
    ) @($workflow.on.workflow_dispatch.inputs.PSObject.Properties.Name | Sort-Object)).Count -eq 0 -and
    -not $workflowText.Contains('"changeReference"')
) 'CTO workflow retained changeReference or omitted the completed prepared-input selector.'
Assert-Test 'cto-workflow-resolves-rpi-with-current-consumer' (
    ([regex]::Matches($workflowText, '-ExpectedRole release-c6')).Count -eq 1 -and
    ([regex]::Matches($workflowText, '-ExpectedRole trusted-preflight')).Count -eq 1 -and
    ([regex]::Matches($workflowText, '-ExpectedRole stage-operation-inputs')).Count -eq 1 -and
    ([regex]::Matches($workflowText, '-ConsumerRunId \$env:GITHUB_RUN_ID')).Count -eq 3
) 'CTO authorization does not resolve completed R/P/I before record creation.'
Assert-Test 'cto-workflow-owns-checked-change-record' (
    $workflowText.Contains('-EvidenceType change-record') -and
    $workflowText.Contains('-CtoAuthorizationOwner') -and
    $workflowText.Contains("source-change-record.json") -and
    $workflowText.IndexOf('-EvidenceType change-record',[StringComparison]::Ordinal) -lt
        $workflowText.IndexOf('New-CtoAuthorizationRecord.ps1',[StringComparison]::Ordinal)
) 'Production change-record ownership was not moved to CTO authorization.'
Assert-Test 'cto-workflow-canonical-root-and-pwsh' (
    [string]$workflow.jobs.'record-authorization'.env.HUSAYNIA_REPOSITORY_ROOT -ceq
        '${{ github.workspace }}/HusayniaSite' -and
    [string]$workflow.jobs.'record-authorization'.defaults.run.shell -ceq 'pwsh' -and
    [string]$workflow.jobs.'record-authorization'.defaults.run.'working-directory' -ceq 'HusayniaSite'
) 'CTO workflow does not use the canonical checked-out Husaynia child root.'
Assert-Test 'cto-workflow-permissions-remain-minimal' (
    (@($workflow.permissions.PSObject.Properties.Name | Sort-Object) -join ',') -ceq
        'actions,attestations,contents,id-token' -and
    [string]$workflow.permissions.actions -ceq 'read' -and
    [string]$workflow.permissions.attestations -ceq 'write' -and
    [string]$workflow.permissions.contents -ceq 'read' -and
    [string]$workflow.permissions.'id-token' -ceq 'write'
) 'CTO workflow permissions broadened.'
Assert-Test 'cto-attestation-pin-is-exact' (
    ([regex]::Matches($workflowText,
        'actions/attest-build-provenance@e8998f949152b193b063cb0ec769d69d929409be')).Count -eq 1
) 'CTO producer attestation pin is not the frozen existing commit.'

foreach ($parameter in @(
    'PreparedInputVerifiedProvenancePath','PreparedInputProducerWorkflowRef',
    'CtoApprovalReference','SourceChangeRecordPath'
)) {
    Assert-Test "cto-writer-parameter-$parameter" (
        $writer.Contains("[string]`$$parameter")
    ) "CTO writer is missing $parameter."
}
Assert-Test 'cto-writer-emits-v21-rpi-bindings' (
    $writer.Contains("schemaVersion = '2.1.0'") -and
    $writer.Contains('producerBindings = [ordered]@{') -and
    $writer.Contains('preparedInputs = [ordered]@{') -and
    $writer.Contains('releaseBinding = [ordered]@{') -and
    $writer.Contains('sourceChangeRecordSha256 = Get-Sha256Lower')
) 'CTO record is not the exact v2.1 R/P/I/change-record binding.'
Assert-Test 'cto-writer-rejects-placeholder-and-legacy-change-reference' (
    -not $writer.Contains('[string]$ChangeReference') -and
    -not $writer.Contains('successPlaceholder') -and
    $writer.Contains('checked source change record is not approved')
) 'CTO writer retained a legacy alias or success placeholder.'
Assert-Test 'cto-validator-requires-two-payloads-and-prepared-binding' (
    $validator.Contains('$ctoManifest.files.Count -ne 2') -and
    $validator.Contains("'source-change-record.json'") -and
    $validator.Contains('producerBindings.preparedInputs') -and
    $validator.Contains("schemaVersion = '2.1.0'")
) 'CTO validator does not bind both canonical payloads and completed prepared inputs.'
Assert-Test 'cto-producer-commit-is-not-conflated-with-release-commit' (
    -not $writer.Contains('AuthorizationCommitSha.ToLowerInvariant() -cne $releaseCommit') -and
    -not $validator.Contains('cto.provenance.run.commitSha -cne $releaseCommit')
) 'Separately dispatched CTO producer commit is still forced to equal the release commit.'

Write-Output "SUMMARY cto-authorization-provenance total=$($passed + $failed) passed=$passed failed=$failed auth=0 dispatches=0 deployments=0 database=0 resources=0"
if ($failed -gt 0) { exit 1 }
