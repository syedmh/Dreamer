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

$workflowPath = Join-Path $RepositoryRoot 'pipelines\github\trusted-protected-operations.yml'
$workflowText = Get-Content -LiteralPath $workflowPath -Raw
$workflow = $workflowText | ConvertFrom-Json -DateKind String
$inputGatePath = Join-Path $RepositoryRoot 'eng\promotion\Test-TrustedProtectedOperationInputs.ps1'
$inputGate = Get-Content -LiteralPath $inputGatePath -Raw
$common = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'eng\common\Release.Common.ps1') -Raw
$policy = Get-Content -LiteralPath (
    Join-Path $RepositoryRoot 'pipelines\config\promotion-policy.json') -Raw | ConvertFrom-Json
$steps = @($workflow.jobs.'protected-operation'.steps)

Assert-Test 'trusted-workflow-is-workflow-call-only' (
    $null -ne $workflow.on.workflow_call -and
    @($workflow.on.PSObject.Properties.Name).Count -eq 1
) 'Trusted protected operations gained a direct trigger.'
Assert-Test 'trusted-job-has-no-checkout-or-contents-permission' (
    @($steps | Where-Object {
        $null -ne $_.PSObject.Properties['uses'] -and
        [string]$_.uses -match '^actions/checkout@'
    }).Count -eq 0 -and
    $null -eq $workflow.jobs.'protected-operation'.permissions.PSObject.Properties['contents']
) 'Trusted execution can read repository checkout content.'
Assert-Test 'trusted-job-uses-pwsh-without-checkout-root' (
    [string]$workflow.jobs.'protected-operation'.defaults.run.shell -ceq 'pwsh' -and
    ($null -eq $workflow.jobs.'protected-operation'.PSObject.Properties['env'] -or
        $null -eq $workflow.jobs.'protected-operation'.env.PSObject.Properties[
            'HUSAYNIA_REPOSITORY_ROOT'])
) 'No-checkout trusted job defines a repository root or inherits another shell.'
Assert-Test 'trusted-stageoperations-inputs-are-completed-prior-selectors' (
    @($workflow.on.workflow_call.inputs.PSObject.Properties.Name) -contains 'preflightRunId' -and
    @($workflow.on.workflow_call.inputs.PSObject.Properties.Name) -contains
        'stageOperationInputsRunId' -and
    @($workflow.on.workflow_call.inputs.PSObject.Properties.Name) -contains
        'migrationAuthorizationRunId' -and
    $workflowText.Contains('$env:T21_PREFLIGHT_RUN_ID -cne $env:T21_STAGE_INPUT_RUN_ID') -and
    $workflowText.Contains('-cne $env:GITHUB_RUN_ID')
) 'StageOperations can select current or identical P/I runs.'
Assert-Test 'every-trusted-resolver-call-passes-consumer-run-id' (
    ([regex]::Matches($workflowText, '-ExpectedRole (?:release-c6|trusted-preflight|stage-operation-inputs|cto-authorization|migration-authorization)')).Count -eq
        ([regex]::Matches($workflowText, '-ConsumerRunId \$env:GITHUB_RUN_ID')).Count
) 'A trusted resolver call omits the current consumer temporal boundary.'
Assert-Test 'trusted-resolves-rpicm-before-semantic-gate' (
    $workflowText.IndexOf('-ExpectedRole release-c6',[StringComparison]::Ordinal) -lt
        $workflowText.IndexOf('Test-TrustedProtectedOperationInputs.ps1',[StringComparison]::Ordinal) -and
    $workflowText.IndexOf('-ExpectedRole migration-authorization',[StringComparison]::Ordinal) -lt
        $workflowText.IndexOf('Test-TrustedProtectedOperationInputs.ps1',[StringComparison]::Ordinal)
) 'Trusted semantic validation runs before authoritative provenance resolution.'
Assert-Test 'semantic-gate-requires-v21-prepared-and-authorization-bindings' (
    $inputGate.Contains("schemaVersion -cne '2.1.0'") -and
    $inputGate.Contains('producerBindings.preflight.runId') -and
    $inputGate.Contains('producerBindings.preparedInputs.runId') -and
    $inputGate.Contains('productionCtoAuthorization.sourceChangeRecordSha256')
) 'StageOperations does not enforce exact v2.1 R/P/I/C/M cross-bindings.'
Assert-Test 'stable-forbidden-boundary-is-single-and-pre-oidc' (
    ([regex]::Matches($inputGate, "throw 'T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN'")).Count -eq 1 -and
    $workflowText.IndexOf('Test-TrustedProtectedOperationInputs.ps1',[StringComparison]::Ordinal) -lt
        $workflowText.IndexOf('Mint OIDC only after verified provenance',[StringComparison]::Ordinal) -and
    $common.Contains("throw 'T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN'")
) 'The stable deployment-evidence-forbidden stop moved after OIDC or was duplicated.'
Assert-Test 'deployment-evidence-role-remains-forbidden' (
    @($policy.t21ProvenanceContract.producerRoles | Where-Object {
        [string]$_.role -ceq 'deployment-evidence' -and [bool]$_.forbidden
    }).Count -eq 1
) 'Deployment evidence became selectable.'
Assert-Test 'all-stage-policies-remain-disabled-no-rebuild' (
    @($policy.stages | Where-Object { [bool]$_.deploymentEnabled -or [bool]$_.rebuildAllowed }).Count -eq 0
) 'A stage was enabled or allowed to rebuild.'
Assert-Test 'trusted-post-login-execution-is-bundle-only' (
    @($steps | Where-Object {
        [string]$_.name -ceq 'Login with externally bound stage identity'
    }).Count -eq 1 -and
    @($steps | Where-Object {
        [string]$_.name -ceq 'Recheck immutable extraction' -and
        [string]$_.run -match 'T21_TRUSTED_BUNDLE_ROOT'
    }).Count -eq 1 -and
    -not $workflowText.Contains('$env:GITHUB_WORKSPACE')
) 'Post-login execution can return to repository content.'
Assert-Test 'login-and-attestation-pins-are-exact' (
    $workflowText.Contains('azure/login@7184910d9eb2b1c5e48f7073824a90609bb9b6d6') -and
    $workflowText.Contains(
        'actions/attest-build-provenance@e8998f949152b193b063cb0ec769d69d929409be')
) 'Trusted action pins are not the authoritative commits.'
Assert-Test 'caller-matrix-is-exact-three-rows' (
    @($policy.t21ProvenanceContract.callerMatrix).Count -eq 3 -and
    @($policy.t21ProvenanceContract.callerMatrix | Where-Object {
        [string]$_.workflowRef -match 'release-build-and-nonproduction'
    }).Count -eq 0
) 'Release remains an authorized protected-operation caller.'
Assert-Test 'all-selectable-producer-roles-require-completed-success' (
    @($policy.t21ProvenanceContract.producerRoles | Where-Object {
        -not [bool]$_.forbidden -and -not [bool]$_.requiresCompletedSuccess
    }).Count -eq 0
) 'A selectable role still permits an in-progress producer.'

Write-Output "SUMMARY trusted-stage-operations total=$($passed + $failed) passed=$passed failed=$failed auth=0 dispatches=0 deployments=0 receipts=0 database=0 resources=0"
if ($failed -gt 0) { exit 1 }
