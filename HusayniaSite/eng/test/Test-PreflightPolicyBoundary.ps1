[CmdletBinding()]
param([string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Resolve-HusayniaRepositoryRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "t21-preflight-policy-$([guid]::NewGuid().ToString('N'))"
$passed = 0
$failed = 0

function Assert-PreflightPolicyTest {
    param([string]$Name, [bool]$Condition, [string]$Failure)

    if ($Condition) {
        $script:passed++
        Write-Output "PASS  $Name"
    }
    else {
        $script:failed++
        Write-Output "FAIL  $Name :: $Failure"
    }
}

function Copy-EnvironmentValues {
    param([hashtable]$Values)

    $copy = @{}
    foreach ($name in $Values.Keys) {
        $copy[$name] = $Values[$name]
    }
    return $copy
}

function Invoke-WithScopedEnvironment {
    param(
        [hashtable]$Values,
        [scriptblock]$Body
    )

    $prior = @{}
    foreach ($name in $Values.Keys) {
        $prior[$name] = [Environment]::GetEnvironmentVariable(
            $name,
            [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable(
            $name,
            [string]$Values[$name],
            [EnvironmentVariableTarget]::Process)
    }
    try {
        return & $Body
    }
    finally {
        foreach ($name in $Values.Keys) {
            [Environment]::SetEnvironmentVariable(
                $name,
                $prior[$name],
                [EnvironmentVariableTarget]::Process)
        }
    }
}

function Install-PreflightSideEffectGuards {
    param(
        [hashtable]$Counters,
        [hashtable]$PriorFunctions
    )

    $definitions = [ordered]@{
        'Invoke-RestMethod' = @('oidc')
        'Invoke-WebRequest' = @('oidc')
        'Connect-AzAccount' = @('login')
        'az' = @('login')
        'Invoke-Sqlcmd' = @('database', 'mutation')
        'sqlcmd' = @('database', 'mutation')
        'Add-Content' = @('evidence', 'receipt')
    }
    $global:T21PreflightPolicyCounters = $Counters
    foreach ($name in $definitions.Keys) {
        $functionPath = "Function:\global:$name"
        $existing = Get-Item -LiteralPath $functionPath -ErrorAction SilentlyContinue
        $priorFunctions[$name] = if ($null -eq $existing) { $null } else { $existing.ScriptBlock }
        $increments = @($definitions[$name] | ForEach-Object {
                "`$global:T21PreflightPolicyCounters['$_']++"
            }) -join '; '
        Set-Item -LiteralPath $functionPath -Value ([scriptblock]::Create(
                "$increments; throw 'T21 test side-effect guard blocked $name.'"
            )) -Force
    }
}

function Restore-PreflightSideEffectGuards {
    param(
        [hashtable]$PriorFunctions,
        [bool]$HadPriorCounters,
        $PriorCounters
    )

    foreach ($name in $PriorFunctions.Keys) {
        $functionPath = "Function:\global:$name"
        if ($null -eq $PriorFunctions[$name]) {
            Remove-Item -LiteralPath $functionPath -Force -ErrorAction SilentlyContinue
        }
        else {
            Set-Item -LiteralPath $functionPath -Value $PriorFunctions[$name] -Force
        }
    }
    if ($HadPriorCounters) {
        $global:T21PreflightPolicyCounters = $PriorCounters
    }
    else {
        Remove-Variable -Scope Global -Name T21PreflightPolicyCounters -ErrorAction SilentlyContinue
    }
}

function Invoke-CheckedWorkflowRunBody {
    param(
        [scriptblock]$RunBody,
        [hashtable]$Environment
    )

    $counters = [ordered]@{
        oidc = 0
        login = 0
        database = 0
        mutation = 0
        evidence = 0
        receipt = 0
    }
    $hadPriorCounters = $null -ne (Get-Variable -Scope Global -Name T21PreflightPolicyCounters `
            -ErrorAction SilentlyContinue)
    $priorCounters = if ($hadPriorCounters) {
        (Get-Variable -Scope Global -Name T21PreflightPolicyCounters).Value
    }
    else {
        $null
    }
    $priorFunctions = @{}
    $output = [Collections.Generic.List[string]]::new()
    $executionState = @{ exceptionMessage = '' }
    try {
        Install-PreflightSideEffectGuards -Counters $counters -PriorFunctions $priorFunctions
        Invoke-WithScopedEnvironment -Values $Environment -Body {
            try {
                & $RunBody 2>&1 | ForEach-Object {
                    [void]$output.Add($_.ToString())
                }
            }
            catch {
                $executionState.exceptionMessage = $_.Exception.Message
                [void]$output.Add($executionState.exceptionMessage)
            }
        } | Out-Null
    }
    finally {
        Restore-PreflightSideEffectGuards -PriorFunctions $priorFunctions `
            -HadPriorCounters $hadPriorCounters -PriorCounters $priorCounters
    }
    return [pscustomobject]@{
        exceptionMessage = [string]$executionState.exceptionMessage
        output = @($output)
        counters = $counters
    }
}

function Test-ZeroPreflightSideEffects {
    param($Result)

    return @($Result.counters.Values | Where-Object { [int]$_ -ne 0 }).Count -eq 0
}

function New-ValidatedProtectedBundleRoot {
    param([string]$Root)

    $applicationFixtureRoot = Join-Path $Root 'application'
    $applicationArchivePath = Join-Path $Root 'Husaynia.Web.zip'
    $publishedRoot = Join-Path $RepositoryRoot 'src\Husaynia.Web\bin\Release\net10.0'
    New-Item -ItemType Directory -Path $applicationFixtureRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $publishedRoot 'Husaynia.Web.deps.json') `
        -Destination $applicationFixtureRoot
    Copy-Item -Path (Join-Path $publishedRoot '*.dll') -Destination $applicationFixtureRoot
    foreach ($relativePath in @(
        'runtimes/unix/lib/net9.0/Microsoft.Data.SqlClient.dll',
        'runtimes/win/lib/net9.0/Microsoft.Data.SqlClient.dll'
    )) {
        $destination = Join-Path $applicationFixtureRoot $relativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force |
            Out-Null
        Copy-Item -LiteralPath (Join-Path $publishedRoot $relativePath) `
            -Destination $destination
    }
    New-DeterministicZip -SourceDirectory $applicationFixtureRoot `
        -DestinationPath $applicationArchivePath
    $applicationSha256 = Get-Sha256Lower -Path $applicationArchivePath
    $bundlePath = Join-Path $Root 'protected-execution-bundle.zip'
    $bundleRoot = Join-Path $Root 'protected-execution-bundle'
    $builderPath = Join-Path $RepositoryRoot 'eng\artifact\New-ProtectedExecutionBundle.ps1'
    $validatorPath = Join-Path $RepositoryRoot 'eng\artifact\Test-ProtectedExecutionBundle.ps1'
    & $builderPath -RepositoryRoot $RepositoryRoot `
        -ApplicationArchivePath $applicationArchivePath -DestinationPath $bundlePath | Out-Null
    $bundleSha256 = Get-Sha256Lower -Path $bundlePath
    & $validatorPath -BundlePath $bundlePath -ExpectedSha256 $bundleSha256 `
        -ExpectedApplicationSha256 $applicationSha256 -ExtractTo $bundleRoot | Out-Null
    if (-not (Test-Path -LiteralPath (
                Join-Path $bundleRoot 'pipelines\config\protected-operation-policy.json') -PathType Leaf)) {
        throw 'The locally validated protected bundle did not contain its immutable policy.'
    }
    return [pscustomobject]@{
        root = (Resolve-Path -LiteralPath $bundleRoot).Path
        sha256 = $bundleSha256
        applicationSha256 = $applicationSha256
    }
}

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null

    $workflowPath = Join-Path $RepositoryRoot 'pipelines\github\trusted-protected-operations.yml'
    $workflow = Get-Content -LiteralPath $workflowPath -Raw | ConvertFrom-Json -DateKind String
    $steps = @($workflow.jobs.'protected-operation'.steps)
    $producerSteps = @($steps | Where-Object {
            [string]$_.name -ceq 'Produce canonical attested raw preflight after read-only login'
        })
    $enablementSteps = @($steps | Where-Object {
            [string]$_.name -ceq 'Enforce immutable stage enablement before token request'
        })
    $callerValidationSteps = @($steps | Where-Object {
            [string]$_.name -ceq 'Validate caller selectors and external subject marker before token request'
        })
    $tokenStepIndex = [array]::FindIndex([object[]]$steps, [Predicate[object]]{
            param($step)
            [string]$step.name -ceq 'Mint OIDC only after verified provenance'
        })
    $loginStepIndex = [array]::FindIndex([object[]]$steps, [Predicate[object]]{
            param($step)
            [string]$step.name -ceq 'Login with externally bound stage identity'
        })
    $producerStepIndex = [array]::FindIndex([object[]]$steps, [Predicate[object]]{
            param($step)
            [string]$step.name -ceq 'Produce canonical attested raw preflight after read-only login'
        })
    $enablementStepIndex = [array]::FindIndex([object[]]$steps, [Predicate[object]]{
            param($step)
            [string]$step.name -ceq 'Enforce immutable stage enablement before token request'
        })
    $producerRun = if ($producerSteps.Count -eq 1) { [string]$producerSteps[0].run } else { '' }
    $callerValidationRun = if ($callerValidationSteps.Count -eq 1) {
        [string]$callerValidationSteps[0].run
    }
    else {
        ''
    }
    $enablementRun = if ($enablementSteps.Count -eq 1) {
        [string]$enablementSteps[0].run
    }
    else {
        ''
    }
    $producerContinueOnError = if ($producerSteps.Count -eq 1 -and
        $null -ne $producerSteps[0].PSObject.Properties['continue-on-error']) {
        [string]$producerSteps[0].PSObject.Properties['continue-on-error'].Value
    }
    else {
        ''
    }
    Assert-PreflightPolicyTest 'trusted-workflow-has-pretoken-enablement-and-postlogin-readonly-producer' (
        $producerSteps.Count -eq 1 -and
        $enablementSteps.Count -eq 1 -and
        $callerValidationSteps.Count -eq 1 -and
        $producerStepIndex -gt 0 -and
        $enablementStepIndex -gt 0 -and
        $tokenStepIndex -gt $enablementStepIndex -and
        $loginStepIndex -gt $tokenStepIndex -and
        $producerStepIndex -gt $loginStepIndex -and
        [string]$producerSteps[0].env.T21_MIGRATION_IDENTITY_MODE -ceq 'readonly' -and
        $producerContinueOnError -ne 'true' -and
        -not [string]::IsNullOrWhiteSpace($producerRun) -and
        -not [string]::IsNullOrWhiteSpace($enablementRun)
    ) 'The immutable enablement check or post-login readonly producer is absent, non-unique, allowed to continue, or incorrectly ordered.'

    if ([string]::IsNullOrWhiteSpace($producerRun) -or
        [string]::IsNullOrWhiteSpace($enablementRun) -or
        [string]::IsNullOrWhiteSpace($callerValidationRun)) {
        throw 'Cannot execute a missing checked-in trusted workflow run body.'
    }
    $producerRunBody = [scriptblock]::Create($producerRun)
    $enablementRunBody = [scriptblock]::Create($enablementRun)
    $callerValidationRunBody = [scriptblock]::Create($callerValidationRun)
    $bundle = New-ValidatedProtectedBundleRoot -Root (Join-Path $temporaryRoot 'bundle')
    $sourceProducerPath = Join-Path $RepositoryRoot 'eng\promotion\Invoke-OperationEvidenceProducer.ps1'
    $bundleProducerPath = Join-Path $bundle.root 'eng\promotion\Invoke-OperationEvidenceProducer.ps1'
    Assert-PreflightPolicyTest 'workflow-run-body-executes-the-checked-in-producer-in-a-validated-bundle' (
        (Test-Path -LiteralPath $sourceProducerPath -PathType Leaf) -and
        (Test-Path -LiteralPath $bundleProducerPath -PathType Leaf) -and
        (Get-CanonicalTextSha256Lower -Path $sourceProducerPath) -ceq
            (Get-CanonicalTextSha256Lower -Path $bundleProducerPath)
    ) 'The workflow run body would not execute the real checked-in operation evidence producer from its validated bundle.'

    $policy = Get-Content -LiteralPath (
        Join-Path $RepositoryRoot 'pipelines\config\promotion-policy.json') -Raw |
        ConvertFrom-Json -DateKind String
    $callerRows = @($policy.t21ProvenanceContract.callerMatrix)
    Assert-PreflightPolicyTest 'policy-has-the-exact-three-caller-matrix-rows-for-causal-execution' (
        $callerRows.Count -eq 3 -and
        @($callerRows | Where-Object { $_.stage -eq 'Development' }).Count -eq 1 -and
        @($callerRows | Where-Object { $_.stage -eq 'Staging' }).Count -eq 1 -and
        @($callerRows | Where-Object { $_.stage -eq 'Production' }).Count -eq 1
    ) 'The causal execution matrix is not the approved three-row caller contract.'

    $trustedProducerRef = "syedmh/Dreamer/.github/workflows/trusted-protected-operations.yml@$('1' * 40)"
    foreach ($row in $callerRows) {
        $rowRoot = Join-Path $temporaryRoot (
            "workflow-$([string]$row.stage)-$([guid]::NewGuid().ToString('N'))")
        $runnerTemp = Join-Path $rowRoot 'runner-temp'
        New-Item -ItemType Directory -Path $runnerTemp -Force | Out-Null
        $githubOutput = Join-Path $runnerTemp 'github-output.txt'
        $rawEvidenceRoot = Join-Path $runnerTemp 'raw-evidence'
        $environment = @{
            T21_TRUSTED_BUNDLE_ROOT = $bundle.root
            RUNNER_TEMP = $runnerTemp
            GITHUB_ACTIONS = 'true'
            GITHUB_OUTPUT = $githubOutput
            GITHUB_RUN_ID = '501'
            GITHUB_RUN_ATTEMPT = '1'
            GITHUB_SHA = 'd' * 40
            T21_STAGE = [string]$row.stage
            T21_EXPECTED_APP_SHA256 = $bundle.applicationSha256
            T21_TRUSTED_BUNDLE_SHA256 = $bundle.sha256
            T21_TOP_LEVEL_CALLER_WORKFLOW_REF = [string]$row.workflowRef
            T21_PRODUCER_WORKFLOW_REF = $trustedProducerRef
            T21_PRODUCER_RUN_ID = '101'
            T21_PRODUCER_RUN_ATTEMPT = '1'
            T21_PRODUCER_COMMIT_SHA = 'd' * 40
            T21_RELEASE_VERIFIED_PROVENANCE_PATH = (Join-Path $runnerTemp 'missing-release\verified-provenance.json')
        }
        $result = Invoke-CheckedWorkflowRunBody -RunBody $enablementRunBody -Environment $environment
        $callerWorkflowName = (([string]$row.workflowRef).Split('@')[0]).Split('/')[-1]
        Assert-PreflightPolicyTest (
            "checked-in-pretoken-enablement-$([string]$row.stage)-$callerWorkflowName-stops-at-disabled-policy"
        ) (
            $result.exceptionMessage.Contains("$([string]$row.stage) deploymentEnabled=false.") -and
            -not (Test-Path -LiteralPath $rawEvidenceRoot) -and
            -not (Test-Path -LiteralPath (Join-Path $rawEvidenceRoot 't21-producer-manifest.json')) -and
            -not (Test-Path -LiteralPath $githubOutput) -and
            -not (Test-Path -LiteralPath (Join-Path $runnerTemp 'stage-target.json')) -and
            (Test-ZeroPreflightSideEffects -Result $result)
        ) (
            "The exact checked-in pre-token enablement body for $($row.stage)/$($row.workflowRef) did not fail at the immutable disabled policy " +
            "before raw evidence, manifest, output, OIDC, login, database, mutation, or receipt work: " +
            "$($result.output -join ' | ')"
        )
    }

    $baseValidationEnvironment = @{
        T21_STAGE = 'Development'
        T21_OPERATION = 'Preflight'
        T21_CALLER_WORKFLOW_REF =
            'syedmh/Dreamer/.github/workflows/operation-evidence-producer.yml@refs/heads/main'
        T21_RELEASE_RUN_ID = '101'
        T21_EXPECTED_APP_SHA256 = $bundle.applicationSha256
        T21_PREFLIGHT_RUN_ID = ''
        T21_STAGE_INPUT_RUN_ID = ''
        T21_MIGRATION_AUTHORIZATION_RUN_ID = ''
        T21_CTO_AUTHORIZATION_RUN_ID = ''
        T21_CTO_APPROVAL_REFERENCE = ''
        T21_TRUSTED_WORKFLOW_REF = $trustedProducerRef
        T21_STAGE_INPUTS_WORKFLOW_REF =
            "syedmh/Dreamer/.github/workflows/stage-operation-inputs.yml@$('2' * 40)"
        T21_CTO_AUTHORIZATION_WORKFLOW_REF =
            "syedmh/Dreamer/.github/workflows/cto-authorization-record.yml@$('3' * 40)"
        T21_MIGRATION_AUTHORIZATION_WORKFLOW_REF =
            "syedmh/Dreamer/.github/workflows/migration-apply-authorization.yml@$('4' * 40)"
        T21_TRUSTED_BUNDLE_SHA256 = $bundle.sha256
        T21_STAGE_TARGET_METADATA_JSON = '{"stage":"Development"}'
        T21_TRUSTED_OIDC_SUBJECTS_JSON = '["subject-one","subject-two","subject-three"]'
        GITHUB_RUN_ID = '501'
    }
    $swappedEnvironment = Copy-EnvironmentValues -Values $baseValidationEnvironment
    $swappedEnvironment.T21_CALLER_WORKFLOW_REF =
        'syedmh/Dreamer/.github/workflows/production-operation-evidence.yml@refs/heads/main'
    $swappedResult = Invoke-CheckedWorkflowRunBody -RunBody $callerValidationRunBody `
        -Environment $swappedEnvironment
    Assert-PreflightPolicyTest 'checked-in-caller-validation-run-body-rejects-swapped-stage-and-caller-before-token' (
        $swappedResult.exceptionMessage.Contains(
            'T21 completed prior-run selectors, exact caller, pins, or Production-only authorization inputs are invalid.'
        ) -and
        (Test-ZeroPreflightSideEffects -Result $swappedResult)
    ) "The exact checked-in caller-validation run body accepted a swapped Development/Production caller: $($swappedResult.output -join ' | ')"

    $wrongPinEnvironment = Copy-EnvironmentValues -Values $baseValidationEnvironment
    $wrongPinEnvironment.T21_TRUSTED_WORKFLOW_REF =
        "syedmh/Dreamer/.github/workflows/trusted-protected-operations.yml@$('0' * 40)"
    $wrongPinResult = Invoke-CheckedWorkflowRunBody -RunBody $callerValidationRunBody `
        -Environment $wrongPinEnvironment
    Assert-PreflightPolicyTest 'checked-in-caller-validation-run-body-rejects-all-zero-producer-pin-before-token' (
        $wrongPinResult.exceptionMessage.Contains(
            'T21 completed prior-run selectors, exact caller, pins, or Production-only authorization inputs are invalid.'
        ) -and
        (Test-ZeroPreflightSideEffects -Result $wrongPinResult)
    ) "The exact checked-in caller-validation run body accepted an all-zero trusted producer pin: $($wrongPinResult.output -join ' | ')"
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Output "SUMMARY preflight-policy total=$($passed + $failed) passed=$passed failed=$failed auth=0 deployments=0 database=0 resources=0"
if ($failed -gt 0) { exit 1 }
