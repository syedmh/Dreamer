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

$workflowPath = Join-Path $RepositoryRoot 'pipelines\github\migration-apply-authorization.yml'
$workflowText = Get-Content -LiteralPath $workflowPath -Raw
$workflow = $workflowText | ConvertFrom-Json -DateKind String
$writerPath = Join-Path $RepositoryRoot 'eng\promotion\New-MigrationApplyAuthorization.ps1'
$writer = Get-Content -LiteralPath $writerPath -Raw
$trusted = Get-Content -LiteralPath (
    Join-Path $RepositoryRoot 'eng\promotion\Test-TrustedProtectedOperationInputs.ps1') -Raw
$resolver = Get-Content -LiteralPath (
    Join-Path $RepositoryRoot 'eng\promotion\Resolve-GitHubArtifactProvenance.ps1') -Raw
$coordinator = Get-Content -LiteralPath (
    Join-Path $RepositoryRoot 'eng\promotion\Invoke-T21CompletedRunCoordinator.ps1') -Raw

Assert-Test 'migration-workflow-is-protected-disabled-manual' (
    $null -ne $workflow.on.workflow_dispatch -and
    [string]$workflow.jobs.'authorize-apply'.if -match 'T21_MIGRATION_AUTHORIZATION_ENABLED' -and
    [string]$workflow.jobs.'authorize-apply'.environment.name -ceq
        'Husaynia-${{ inputs.stage }}-Migration-Authorization'
) 'Migration authorization is not protected and disabled.'
Assert-Test 'migration-workflow-inputs-bind-completed-rpi-and-production-c' (
    @($workflow.on.workflow_dispatch.inputs.PSObject.Properties.Name) -contains
        'stageOperationInputsRunId' -and
    @($workflow.on.workflow_dispatch.inputs.PSObject.Properties.Name) -contains
        'ctoAuthorizationRunId' -and
    $workflowText.Contains('preflightRunId') -and
    $workflowText.Contains('sourceReleaseRunId')
) 'Migration authorization omitted a frozen R/P/I/C selector.'
Assert-Test 'migration-run-name-carries-nonauthoritative-correlation-only' (
    [string]$workflow.'run-name' -match '^T21 R10 M ' -and
    [string]$workflow.'run-name' -like '*r${{ inputs.sourceReleaseRunId }}*' -and
    [string]$workflow.'run-name' -like '*a${{ inputs.expectedAppSha256 }}*' -and
    [string]$workflow.'run-name' -like '*p${{ inputs.preflightRunId }}*' -and
    [string]$workflow.'run-name' -like '*i${{ inputs.stageOperationInputsRunId }}*'
) 'Migration coordinator correlation does not carry the exact opaque selectors.'
Assert-Test 'coordinator-discovers-attested-migration-authorization-without-title-bindings' (
    $resolver.Contains('[switch]$DiscoverMigrationAuthorizationIdentity') -and
    $resolver.Contains(
        '^migration-apply-authorization-(Development|Staging)-([a-f0-9]{64})-([a-f0-9]{64})$') -and
    $resolver.Contains('discovered-migration-authorization-identity.json') -and
    $coordinator.Contains('-DiscoverMigrationAuthorizationIdentity') -and
    -not $coordinator.Contains('^T21 R10 M ')
) 'Coordinator still derives authorization bindings from the predecessor title or unverified outputs.'
Assert-Test 'coordinator-validates-canonical-apply-and-all-bindings-before-dispatch' (
    $trusted.Contains("[string]`$record.decision -cne 'AUTHORIZE'") -and
    $trusted.Contains("[string]`$record.mode -cne 'Apply'") -and
    $trusted.Contains('Assert-T21RunBinding -Actual $record.sourceRelease') -and
    $trusted.Contains('producerBindings.preflight.runId') -and
    $trusted.Contains('producerBindings.preparedInputs.runId') -and
    $trusted.Contains('authorizedExecution.topLevelCallerWorkflowRef') -and
    $trusted.Contains('authorizedExecution.producerWorkflowRef') -and
    $coordinator.IndexOf(
        'Test-TrustedProtectedOperationInputs.ps1',
        [StringComparison]::Ordinal) -lt
        $coordinator.IndexOf('-Mode StageOperations', [StringComparison]::Ordinal)
) 'Canonical APPLY authorization or exact release/preflight/prepared/stage/caller/reusable validation does not precede dispatch.'
Assert-Test 'migration-workflow-resolves-rpi-with-current-consumer' (
    ([regex]::Matches($workflowText, '-ExpectedRole release-c6')).Count -eq 1 -and
    ([regex]::Matches($workflowText, '-ExpectedRole trusted-preflight')).Count -eq 1 -and
    ([regex]::Matches($workflowText, '-ExpectedRole stage-operation-inputs')).Count -eq 1 -and
    ([regex]::Matches($workflowText, '-ConsumerRunId \$env:GITHUB_RUN_ID')).Count -ge 3
) 'Migration authorization does not resolve completed R/P/I before record creation.'
Assert-Test 'production-migration-resolves-cto-before-record' (
    $workflowText.Contains('-ExpectedRole cto-authorization') -and
    $workflowText.IndexOf('-ExpectedRole cto-authorization',[StringComparison]::Ordinal) -lt
        $workflowText.IndexOf('New-MigrationApplyAuthorization.ps1',[StringComparison]::Ordinal)
) 'Production migration does not resolve completed CTO authorization first.'
Assert-Test 'migration-workflow-canonical-root-and-pwsh' (
    [string]$workflow.jobs.'authorize-apply'.env.HUSAYNIA_REPOSITORY_ROOT -ceq
        '${{ github.workspace }}/HusayniaSite' -and
    [string]$workflow.jobs.'authorize-apply'.defaults.run.shell -ceq 'pwsh' -and
    [string]$workflow.jobs.'authorize-apply'.defaults.run.'working-directory' -ceq 'HusayniaSite'
) 'Migration workflow does not use the canonical checked-out child root.'
Assert-Test 'migration-attestation-pin-is-exact' (
    ([regex]::Matches($workflowText,
        'actions/attest-build-provenance@e8998f949152b193b063cb0ec769d69d929409be')).Count -eq 1
) 'Migration attestation pin is not exact.'
foreach ($parameter in @(
    'PreparedInputVerifiedProvenancePath','PreparedInputProducerWorkflowRef',
    'CtoAuthorizationProducerWorkflowRef'
)) {
    Assert-Test "migration-writer-parameter-$parameter" (
        $writer.Contains("[string]`$$parameter")
    ) "Migration writer is missing $parameter."
}
Assert-Test 'migration-writer-emits-v21-rpi-bindings' (
    $writer.Contains("schemaVersion = '2.1.0'") -and
    $writer.Contains('releaseBinding = [ordered]@{') -and
    $writer.Contains('producerBindings = [ordered]@{') -and
    $writer.Contains('preparedInputs = [ordered]@{')
) 'Migration record does not bind exact completed R/P/I.'
Assert-Test 'production-migration-binds-cto-change-record' (
    $writer.Contains('sourceChangeRecordSha256') -and
    $writer.Contains('authorizationProvenanceSha256') -and
    $writer.Contains('productionCtoAuthorization = $productionCtoAuthorization') -and
    $trusted.Contains('productionCtoAuthorization.sourceChangeRecordSha256')
) 'Production migration/StageOperations does not bind CTO-owned checked change evidence.'
Assert-Test 'nonproduction-migration-rejects-cto-context' (
    $writer.Contains('Non-Production migration authorization must not accept CTO authorization selectors or context.')
) 'Nonproduction migration can accept Production CTO state.'
Assert-Test 'migration-producer-commit-is-separated-from-release-commit' (
    -not $writer.Contains('verified.run.commitSha -cne $ExpectedReleaseCommitSha') -and
    $writer.Contains('verified.attestation.sourceCommitSha -cne [string]$verified.run.commitSha')
) 'Migration producer identity is still conflated with immutable release identity.'
Assert-Test 'durable-fence-contract-remains-present' (
    (Get-Content -LiteralPath (
        Join-Path $RepositoryRoot 'eng\artifact\migrations\bundle\Migration.Common.ps1') -Raw).
        Contains('ActiveMutationId') -and
    (Get-Content -LiteralPath (
        Join-Path $RepositoryRoot 'eng\artifact\migrations\bundle\Migration.Common.ps1') -Raw).
        Contains('UPDLOCK, HOLDLOCK')
) 'Durable SQL-backed fenced apply lease was weakened.'

Write-Output "SUMMARY migration-authorization-provenance total=$($passed + $failed) passed=$passed failed=$failed auth=0 dispatches=0 deployments=0 database=0 resources=0"
if ($failed -gt 0) { exit 1 }
