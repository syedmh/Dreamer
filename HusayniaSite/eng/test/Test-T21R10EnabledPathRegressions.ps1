[CmdletBinding()]
param([string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Resolve-HusayniaRepositoryRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "t21-r10-enabled-$([guid]::NewGuid().ToString('N'))"
$passed = 0
$failed = 0

function Assert-EnabledPath {
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

function Invoke-WithEnvironment {
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

function Invoke-RunBody {
    param(
        [scriptblock]$RunBody,
        [hashtable]$Environment
    )

    $output = [Collections.Generic.List[string]]::new()
    $state = [pscustomobject]@{ failure = '' }
    $hadLastExitCode = $null -ne (Get-Variable -Scope Global -Name LASTEXITCODE `
            -ErrorAction SilentlyContinue)
    $priorLastExitCode = if ($hadLastExitCode) { $global:LASTEXITCODE } else { $null }
    try {
        $global:LASTEXITCODE = 0
        Invoke-WithEnvironment -Values $Environment -Body {
            try {
                & $RunBody 2>&1 | ForEach-Object { [void]$output.Add($_.ToString()) }
            }
            catch {
                $state.failure = $_.Exception.Message
                [void]$output.Add($state.failure)
            }
        } | Out-Null
    }
    finally {
        if ($hadLastExitCode) {
            $global:LASTEXITCODE = $priorLastExitCode
        }
        else {
            Remove-Variable -Scope Global -Name LASTEXITCODE -ErrorAction SilentlyContinue
        }
    }
    return [pscustomobject]@{
        failure = [string]$state.failure
        output = @($output)
    }
}

function Get-WorkflowStep {
    param($Steps, [string]$Name)

    $matches = @($Steps | Where-Object { [string]$_.name -ceq $Name })
    if ($matches.Count -ne 1) {
        return $null
    }
    return $matches[0]
}

function Write-FixtureScript {
    param([string]$Path, [string]$Content)

    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    $workflowRoot = Join-Path $RepositoryRoot 'pipelines\github'

    # Finding 1: execute the exact coordinator run body with a fake gh implementation that
    # reproduces gh --field type conversion and captures the resulting request body.
    $coordinator = Get-Content -LiteralPath (
        Join-Path $workflowRoot 'automatic-nonproduction-orchestration.yml') -Raw |
        ConvertFrom-Json -DateKind String
    $coordinatorSteps = @($coordinator.jobs.coordinate.steps)
    $dispatchStep = Get-WorkflowStep -Steps $coordinatorSteps `
        -Name 'Dispatch only idempotent completed-run successors'
    $coordinatorScriptPath = Join-Path $RepositoryRoot (
        'eng\promotion\Invoke-T21CompletedRunCoordinator.ps1')
    $dispatchRun = if ($null -ne $dispatchStep -and
        [string]$dispatchStep.run -ceq
            "& (Join-Path `$env:HUSAYNIA_REPOSITORY_ROOT 'eng/promotion/Invoke-T21CompletedRunCoordinator.ps1')") {
        { & $coordinatorScriptPath }.GetNewClosure()
    }
    else {
        $null
    }
    $coordinatorFixtureRoot = Join-Path $temporaryRoot 'coordinator'
    $coordinatorPolicyPath = Join-Path $coordinatorFixtureRoot 'pipelines\config\promotion-policy.json'
    New-Item -ItemType Directory -Path (Split-Path -Parent $coordinatorPolicyPath) -Force | Out-Null
    $coordinatorPolicy = Get-Content -LiteralPath (
        Join-Path $RepositoryRoot 'pipelines\config\promotion-policy.json') -Raw |
        ConvertFrom-Json -DateKind String
    @($coordinatorPolicy.stages | Where-Object { [string]$_.name -ceq 'Development' })[0].
        deploymentEnabled = $true
    Write-Utf8Json -Value $coordinatorPolicy -Path $coordinatorPolicyPath -Depth 80
    New-Item -ItemType Directory -Path (Join-Path $coordinatorFixtureRoot 'runner') -Force |
        Out-Null
    Write-FixtureScript -Path (Join-Path $coordinatorFixtureRoot (
            'eng\promotion\Resolve-GitHubArtifactProvenance.ps1')) -Content @'
param(
    [string]$ExpectedRole,
    [string]$RunId,
    [string]$ConsumerRunId,
    [string]$Stage,
    [string]$ApplicationSha256,
    [string]$BundleSha256,
    [string]$ReleaseManifestSha256,
    [string]$ReleaseCommitSha,
    [string]$ExpectedTopLevelCallerWorkflowRef,
    [string]$ExpectedProducerWorkflowRef,
    [switch]$DiscoverMigrationAuthorizationIdentity,
    [string]$OutputRoot,
    [string]$PolicyPath
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path (Join-Path $OutputRoot 'artifact') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $OutputRoot 'artifact.zip'), 'fixture')
[IO.File]::WriteAllText((Join-Path $OutputRoot 'verified-provenance.json'), '{}')
if ($DiscoverMigrationAuthorizationIdentity) {
    $identity = [ordered]@{
        stage = 'Development'
        applicationSha256 = 'a' * 64
        preflightEvidenceSha256 = 'e' * 64
        releaseManifestSha256 = 'c' * 64
        releaseCommitSha = 'd' * 40
        releaseRunId = '101'
        releaseRunAttempt = 1
        artifactName = "migration-apply-authorization-Development-$('a' * 64)-$('e' * 64)"
    }
    [IO.File]::WriteAllText(
        (Join-Path $OutputRoot 'discovered-migration-authorization-identity.json'),
        ($identity | ConvertTo-Json -Depth 10))
    $record = [ordered]@{
        decision = 'AUTHORIZE'
        mode = 'Apply'
        stage = 'Development'
        applicationSha256 = 'a' * 64
        producerBindings = [ordered]@{
            preflight = [ordered]@{ runId = '201' }
            preparedInputs = [ordered]@{ runId = '301' }
        }
    }
    [IO.File]::WriteAllText(
        (Join-Path $OutputRoot 'artifact\migration-apply-authorization.json'),
        ($record | ConvertTo-Json -Depth 10))
}
'@
    Write-FixtureScript -Path (Join-Path $coordinatorFixtureRoot (
            'eng\promotion\Test-TrustedProtectedOperationInputs.ps1')) -Content @'
param(
    [string]$Operation,
    [string]$Stage,
    [string]$ExpectedAppSha256,
    [string]$ExpectedBundleSha256,
    [string]$ReleaseVerifiedProvenancePath,
    [string]$PreflightVerifiedProvenancePath,
    [string]$StageOperationInputVerifiedProvenancePath,
    [string]$MigrationAuthorizationVerifiedProvenancePath,
    [string]$StageTargetMetadataPath,
    [string]$PolicyPath
)
throw 'T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN'
'@
    Write-FixtureScript -Path (Join-Path $coordinatorFixtureRoot (
            'eng\promotion\Resolve-T21CompletedRun.ps1')) -Content @'
param(
    [string]$RepositoryRoot,
    [string]$RunId,
    [string]$ExpectedRepository,
    [string]$ExpectedRepositoryId,
    [string]$ExpectedHeadSha,
    [string]$ExpectedMode,
    [string]$ExpectedStage,
    [string]$ExpectedSourceReleaseRunId,
    [string]$ExpectedAppSha256,
    [string]$ExpectedPreflightRunId,
    [string]$ExpectedStageOperationInputsRunId,
    [string]$ExpectedMigrationAuthorizationRunId,
    [string]$ExpectedCtoAuthorizationRunId,
    [string]$ExpectedCorrelationId,
    [string]$OutputRoot,
    [string]$FixturePath
)
$inputs = $global:T21R10CoordinatorFixture.payload.inputs
if (-not $global:T21R10CoordinatorFixture.dispatched -or $RunId -cne '901') {
    throw 'R10 completed-run fixture is absent.'
}
$expected = [ordered]@{
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
foreach ($name in $expected.Keys) {
    if ([string]$inputs.$name -cne [string]$expected[$name]) {
        throw "R10 completed-run input mismatch: $name"
    }
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$verified = [ordered]@{
    run = [ordered]@{ runId = $RunId }
    dispatchInputs = $inputs
}
[IO.File]::WriteAllText(
    (Join-Path $OutputRoot 'verified-completed-run.json'),
    ($verified | ConvertTo-Json -Depth 20),
    [Text.UTF8Encoding]::new($false))
'@

    $priorGh = Get-Item -LiteralPath Function:\global:gh -ErrorAction SilentlyContinue
    $priorSleep = Get-Item -LiteralPath Function:\global:Start-Sleep -ErrorAction SilentlyContinue
    $global:T21R10CoordinatorFixture = [ordered]@{
        dispatched = $false
        body = ''
        arguments = @()
        listArguments = @()
        workflowListCalls = 0
        payload = $null
    }
    try {
        Set-Item -LiteralPath Function:\global:gh -Force -Value {
            param([Parameter(ValueFromRemainingArguments = $true)][object[]]$Arguments)

            $values = @($Arguments | ForEach-Object { [string]$_ })
            if ($values.Count -lt 2 -or $values[0] -cne 'api') {
                throw 'Unexpected fake gh invocation.'
            }
            $endpoint = @($values | Where-Object { $_ -like 'repos/*' }) | Select-Object -First 1
            if ($values -contains '-X') {
                $methodIndex = [array]::IndexOf($values, '-X')
                if ($methodIndex -lt 0 -or $values[$methodIndex + 1] -cne 'POST' -or
                    $endpoint -notlike 'repos/*/dispatches') {
                    throw 'Unexpected fake gh mutation request.'
                }
                $payload = [ordered]@{
                    ref = $null
                    inputs = [ordered]@{}
                }
                for ($index = 0; $index -lt $values.Count; $index++) {
                    $option = $values[$index]
                    if ($option -cnotin @('-f', '--raw-field', '-F', '--field')) {
                        continue
                    }
                    $index++
                    $pair = $values[$index]
                    $separator = $pair.IndexOf('=')
                    if ($separator -le 0) {
                        throw 'Malformed fake gh field.'
                    }
                    $name = $pair.Substring(0, $separator)
                    $value = $pair.Substring($separator + 1)
                    $converted = $value
                    if ($option -cin @('-F', '--field')) {
                        if ($value -match '^-?[0-9]+$') {
                            $converted = [int64]$value
                        }
                        elseif ($value -ceq 'true') {
                            $converted = $true
                        }
                        elseif ($value -ceq 'false') {
                            $converted = $false
                        }
                        elseif ($value -ceq 'null') {
                            $converted = $null
                        }
                    }
                    if ($name -ceq 'ref') {
                        $payload.ref = $converted
                    }
                    elseif ($name -match '^inputs\[(.+)\]$') {
                        $payload.inputs[$Matches[1]] = $converted
                    }
                    else {
                        throw "Unexpected fake gh field: $name"
                    }
                }
                $global:T21R10CoordinatorFixture.arguments = $values
                $global:T21R10CoordinatorFixture.body = $payload | ConvertTo-Json -Depth 10 -Compress
                $global:T21R10CoordinatorFixture.payload = [pscustomobject]$payload
                $global:T21R10CoordinatorFixture.dispatched = $true
                $global:LASTEXITCODE = 0
                return
            }
            if ($endpoint -like 'repos/*/actions/workflows/operation-evidence-producer.yml/runs*') {
                $global:T21R10CoordinatorFixture.workflowListCalls++
                $global:T21R10CoordinatorFixture.listArguments = $values
                if ($global:T21R10CoordinatorFixture.dispatched) {
                    $global:LASTEXITCODE = 0
                    return '[{"workflow_runs":[{"id":901}]}]'
                }
                $global:LASTEXITCODE = 0
                return '[{"workflow_runs":[]}]'
            }
            if ($endpoint -like 'repos/*/actions/runs/*') {
                $global:LASTEXITCODE = 0
                return ([ordered]@{
                        id = 901
                        status = 'completed'
                        conclusion = 'success'
                    } | ConvertTo-Json -Compress)
            }
            throw "Unexpected fake gh endpoint: $endpoint"
        }
        Set-Item -LiteralPath Function:\global:Start-Sleep -Force -Value {
            param([int]$Seconds)
        }
        $dispatchExecution = if ($null -eq $dispatchRun) {
            [pscustomobject]@{ failure = 'Missing coordinator dispatch run body.'; output = @() }
        }
        else {
            Invoke-RunBody -RunBody $dispatchRun -Environment @{
                HUSAYNIA_REPOSITORY_ROOT = $coordinatorFixtureRoot
                GITHUB_REPOSITORY = 'syedmh/Dreamer'
                GITHUB_RUN_ID = '700'
                RUNNER_TEMP = (Join-Path $coordinatorFixtureRoot 'runner')
                GITHUB_STEP_SUMMARY = (Join-Path $coordinatorFixtureRoot 'summary.md')
                T21_PREDECESSOR_ID = '401'
                T21_PREDECESSOR_NAME = 'T21 protected migration Apply authorization'
                T21_PREDECESSOR_PATH = '.github/workflows/migration-apply-authorization.yml'
                T21_REPOSITORY_ID = '42'
                T21_PROTECTED_HEAD_SHA = '9' * 40
                T21_COMPLETED_RUN_FIXTURE_ROOT = (
                    Join-Path $coordinatorFixtureRoot 'completed-run-fixtures')
                T21_TRUSTED_BUNDLE_SHA256 = 'b' * 64
                T21_TRUSTED_WORKFLOW_REF =
                    "syedmh/Dreamer/.github/workflows/trusted-protected-operations.yml@$('1' * 40)"
                T21_STAGE_INPUTS_WORKFLOW_REF =
                    "syedmh/Dreamer/.github/workflows/stage-operation-inputs.yml@$('2' * 40)"
                T21_MIGRATION_AUTHORIZATION_WORKFLOW_REF =
                    "syedmh/Dreamer/.github/workflows/migration-apply-authorization.yml@$('4' * 40)"
                T21_STAGE_TARGET_METADATA_JSON = '{"schemaVersion":"1.0.0"}'
                T21_DEVELOPMENT_ENABLED = 'true'
                T21_STAGING_ENABLED = 'false'
            }
        }
    }
    finally {
        if ($null -eq $priorGh) {
            Remove-Item -LiteralPath Function:\global:gh -Force -ErrorAction SilentlyContinue
        }
        else {
            Set-Item -LiteralPath Function:\global:gh -Force -Value $priorGh.ScriptBlock
        }
        if ($null -eq $priorSleep) {
            Remove-Item -LiteralPath Function:\global:Start-Sleep -Force -ErrorAction SilentlyContinue
        }
        else {
            Set-Item -LiteralPath Function:\global:Start-Sleep -Force -Value $priorSleep.ScriptBlock
        }
    }
    $expectedDispatchBody = ([ordered]@{
            ref = 'main'
            inputs = [ordered]@{
                mode = 'StageOperations'
                stage = 'Development'
                sourceReleaseRunId = '101'
                expectedAppSha256 = 'a' * 64
                preflightRunId = '201'
                stageOperationInputsRunId = '301'
                migrationAuthorizationRunId = '401'
                ctoAuthorizationRunId = ''
                orchestrationCorrelationId = 'r10-401-development-stageoperations'
            }
        } | ConvertTo-Json -Depth 10 -Compress)
    $capturedDispatch = if ([string]::IsNullOrWhiteSpace(
            [string]$global:T21R10CoordinatorFixture.body)) {
        $null
    }
    else {
        $global:T21R10CoordinatorFixture.body | ConvertFrom-Json -DateKind String
    }
    Assert-EnabledPath 'coordinator-dispatch-body-preserves-every-input-as-a-json-string' (
        [string]::IsNullOrWhiteSpace([string]$dispatchExecution.failure) -and
        [string]$global:T21R10CoordinatorFixture.body -ceq $expectedDispatchBody -and
        $null -ne $capturedDispatch -and
        @($capturedDispatch.inputs.PSObject.Properties | Where-Object {
                $_.Value -isnot [string]
            }).Count -eq 0 -and
        @($global:T21R10CoordinatorFixture.arguments | Where-Object {
                $_ -cin @('-F', '--field')
            }).Count -eq 0 -and
        @($global:T21R10CoordinatorFixture.arguments | Where-Object {
                $_ -cin @('--paginate', '--slurp')
            }).Count -eq 0 -and
        @($global:T21R10CoordinatorFixture.listArguments | Where-Object {
                $_ -cin @('--paginate', '--slurp')
            }).Count -eq 2
    ) (
        "The exact fake API body was not string-preserving. failure=$($dispatchExecution.failure) " +
        "expected=$expectedDispatchBody actual=$([string]$global:T21R10CoordinatorFixture.body) " +
        "calls=$($global:T21R10CoordinatorFixture.workflowListCalls) " +
        "arguments=$($global:T21R10CoordinatorFixture.arguments -join ' ') " +
        "output=$($dispatchExecution.output -join ' | ')"
    )

    # Finding 2: model the enabled Preflight step order while executing the exact checked-in
    # producer run body against an immutable-bundle-shaped fake and fake in-process SQL adapter.
    $trusted = Get-Content -LiteralPath (
        Join-Path $workflowRoot 'trusted-protected-operations.yml') -Raw |
        ConvertFrom-Json -DateKind String
    $trustedSteps = @($trusted.jobs.'protected-operation'.steps)
    $tokenIndex = [array]::FindIndex([object[]]$trustedSteps, [Predicate[object]]{
            param($step)
            [string]$step.name -ceq 'Mint OIDC only after verified provenance'
        })
    $loginIndex = [array]::FindIndex([object[]]$trustedSteps, [Predicate[object]]{
            param($step)
            [string]$step.name -ceq 'Login with externally bound stage identity'
        })
    $recheckIndex = [array]::FindIndex([object[]]$trustedSteps, [Predicate[object]]{
            param($step)
            [string]$step.name -ceq 'Recheck immutable extraction'
        })
    $producerIndex = [array]::FindIndex([object[]]$trustedSteps, [Predicate[object]]{
            param($step)
            $null -ne $step.PSObject.Properties['run'] -and
            ([string]$step.run).Contains('Invoke-OperationEvidenceProducer.ps1')
        })
    $enablementIndex = [array]::FindIndex([object[]]$trustedSteps, [Predicate[object]]{
            param($step)
            [string]$step.name -ceq 'Enforce immutable stage enablement before token request'
        })
    $producerStep = if ($producerIndex -ge 0) { $trustedSteps[$producerIndex] } else { $null }
    $producerIdentityMode = if ($null -ne $producerStep -and
        $null -ne $producerStep.PSObject.Properties['env'] -and
        $null -ne $producerStep.env.PSObject.Properties['T21_MIGRATION_IDENTITY_MODE']) {
        [string]$producerStep.env.T21_MIGRATION_IDENTITY_MODE
    }
    else {
        ''
    }
    $identityModeSteps = @($trustedSteps | Where-Object {
            $null -ne $_.PSObject.Properties['env'] -and
            $null -ne $_.env.PSObject.Properties['T21_MIGRATION_IDENTITY_MODE']
        })
    Assert-EnabledPath 'enabled-preflight-producer-is-after-oidc-login-and-readonly-recheck' (
        $enablementIndex -ge 0 -and
        $tokenIndex -gt $enablementIndex -and
        $loginIndex -gt $tokenIndex -and
        $recheckIndex -gt $loginIndex -and
        $producerIndex -gt $recheckIndex -and
        $producerIdentityMode -ceq 'readonly' -and
        $identityModeSteps.Count -eq 1 -and
        [string]$trustedSteps[$tokenIndex].if -ceq '${{ inputs.operation == ''Preflight'' }}' -and
        [string]$trustedSteps[$loginIndex].if -ceq '${{ inputs.operation == ''Preflight'' }}'
    ) 'Enabled Preflight is not ordered after exact OIDC/login/recheck with one step-scoped readonly identity mode.'

    $preflightFixtureRoot = Join-Path $temporaryRoot 'preflight'
    $fakeBundleRoot = Join-Path $preflightFixtureRoot 'protected-bundle'
    $fakeProducerPath = Join-Path $fakeBundleRoot 'eng\promotion\Invoke-OperationEvidenceProducer.ps1'
    $fakeCommonPath = Join-Path $fakeBundleRoot 'eng\common\Release.Common.ps1'
    $fakeMigrationCommonPath = Join-Path $fakeBundleRoot `
        'eng\artifact\migrations\bundle\Migration.Common.ps1'
    $fakePolicyPath = Join-Path $fakeBundleRoot 'pipelines\config\protected-operation-policy.json'
    Write-FixtureScript -Path $fakeCommonPath -Content @'
function Assert-StageDeploymentEnabled {
    param($Policy, [string]$Stage, [string]$Operation)
    $row = @($Policy.stages | Where-Object { [string]$_.name -ceq $Stage })
    if ($row.Count -ne 1 -or -not [bool]$row[0].deploymentEnabled) {
        throw "$Operation denied before token: $Stage deploymentEnabled=false."
    }
    return $row[0]
}
'@
    Write-FixtureScript -Path $fakeMigrationCommonPath -Content @'
function Assert-MigrationSqlRuntimeCompatibility {
    param([string]$BundleRoot, [string]$ExpectedApplicationSha256)
    if ($ExpectedApplicationSha256 -notmatch '^[a-f0-9]{64}$') {
        throw 'T21_SQL_RUNTIME_INVALID'
    }
    return [pscustomobject]@{ BundleRoot = $BundleRoot }
}
'@
    Write-FixtureScript -Path $fakeProducerPath -Content @'
param(
    [string]$Mode,
    [string]$Stage,
    [string]$ExpectedAppSha256,
    [string]$ReleaseVerifiedProvenancePath,
    [string]$TopLevelCallerWorkflowRef,
    [string]$ProducerWorkflowRef,
    [string]$ProducerRunId,
    [int]$ProducerRunAttempt,
    [string]$ProducerCommitSha,
    [string]$StageTargetMetadataPath,
    [string]$OutputRoot,
    [string]$TrustedBundleSha256,
    [string]$PolicyPath,
    [switch]$AllowProtectedOperations
)
if ([Environment]::GetEnvironmentVariable('T21_FAKE_AZURE_LOGIN_ESTABLISHED') -cne 'true') {
    throw 'Fake immutable producer requires the simulated Azure login first.'
}
if ([Environment]::GetEnvironmentVariable('T21_MIGRATION_IDENTITY_MODE') -cne 'readonly') {
    throw 'Fake immutable producer requires readonly migration identity mode.'
}
if ($null -eq $global:T21R10SqlClientFixture) {
    throw 'Fake in-process SQL adapter state is missing.'
}
$global:T21R10SqlClientFixture.calls++
$global:T21R10SqlClientFixture.authenticated =
    [Environment]::GetEnvironmentVariable('T21_FAKE_AZURE_LOGIN_ESTABLISHED')
$global:T21R10SqlClientFixture.identityMode =
    [Environment]::GetEnvironmentVariable('T21_MIGRATION_IDENTITY_MODE')
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
[IO.File]::WriteAllText(
    (Join-Path $OutputRoot 'migration-preflight.json'),
    '{"schemaVersion":"1.0.0","status":"PASS","identityMode":"readonly"}',
    [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText(
    (Join-Path $OutputRoot 't21-producer-manifest.json'),
    '{"schemaVersion":"2.0.0","kind":"t21-producer-manifest"}',
    [Text.UTF8Encoding]::new($false))
'@
    New-Item -ItemType Directory -Path (Split-Path -Parent $fakePolicyPath) -Force | Out-Null
    Write-Utf8Json -Value ([ordered]@{
            stages = @([ordered]@{ name = 'Development'; deploymentEnabled = $false })
        }) -Path $fakePolicyPath

    $enablementStep = if ($enablementIndex -ge 0) { $trustedSteps[$enablementIndex] } else { $null }
    $enablementRun = if ($null -ne $enablementStep) { [string]$enablementStep.run } else { '' }
    $disabledEnablement = if ([string]::IsNullOrWhiteSpace($enablementRun)) {
        [pscustomobject]@{ failure = 'Missing immutable enablement run body.'; output = @() }
    }
    else {
        Invoke-RunBody -RunBody ([scriptblock]::Create($enablementRun)) -Environment @{
            T21_TRUSTED_BUNDLE_ROOT = $fakeBundleRoot
            T21_STAGE = 'Development'
            T21_EXPECTED_APP_SHA256 = 'a' * 64
        }
    }
    Assert-EnabledPath 'disabled-stage-is-rejected-by-immutable-bundle-before-token' (
        $disabledEnablement.failure.Contains('deploymentEnabled=false')
    ) "The pre-token immutable disabled-stage check did not fail closed: $($disabledEnablement.output -join ' | ')"

    $enabledPolicy = Get-Content -LiteralPath $fakePolicyPath -Raw | ConvertFrom-Json
    $enabledPolicy.stages[0].deploymentEnabled = $true
    Write-Utf8Json -Value $enabledPolicy -Path $fakePolicyPath
    $enabledEnablement = if ([string]::IsNullOrWhiteSpace($enablementRun)) {
        [pscustomobject]@{ failure = 'Missing immutable enablement run body.'; output = @() }
    }
    else {
        Invoke-RunBody -RunBody ([scriptblock]::Create($enablementRun)) -Environment @{
            T21_TRUSTED_BUNDLE_ROOT = $fakeBundleRoot
            T21_STAGE = 'Development'
            T21_EXPECTED_APP_SHA256 = 'a' * 64
        }
    }

    $global:T21R10SqlClientFixture = [ordered]@{
        calls = 0
        authenticated = ''
        identityMode = ''
    }
    try {
        $producerRunBody = if ($null -ne $producerStep -and
            -not [string]::IsNullOrWhiteSpace([string]$producerStep.run)) {
            [scriptblock]::Create([string]$producerStep.run)
        }
        else {
            $null
        }
        $producerBaseEnvironment = @{
            T21_TRUSTED_BUNDLE_ROOT = $fakeBundleRoot
            T21_FAKE_AZURE_LOGIN_ESTABLISHED = 'true'
            RUNNER_TEMP = $preflightFixtureRoot
            GITHUB_OUTPUT = (Join-Path $preflightFixtureRoot 'github-output.txt')
            GITHUB_RUN_ID = '501'
            GITHUB_RUN_ATTEMPT = '1'
            GITHUB_SHA = 'd' * 40
            T21_STAGE = 'Development'
            T21_EXPECTED_APP_SHA256 = 'a' * 64
            T21_TRUSTED_BUNDLE_SHA256 = 'b' * 64
            T21_TOP_LEVEL_CALLER_WORKFLOW_REF =
                'syedmh/Dreamer/.github/workflows/operation-evidence-producer.yml@refs/heads/main'
            T21_PRODUCER_WORKFLOW_REF =
                "syedmh/Dreamer/.github/workflows/trusted-protected-operations.yml@$('c' * 40)"
            T21_RELEASE_VERIFIED_PROVENANCE_PATH =
                (Join-Path $preflightFixtureRoot 'release\verified-provenance.json')
        }
        if (-not [string]::IsNullOrWhiteSpace($producerIdentityMode)) {
            $producerBaseEnvironment.T21_MIGRATION_IDENTITY_MODE = $producerIdentityMode
        }
        $validProducer = if ($null -eq $producerRunBody) {
            [pscustomobject]@{ failure = 'Missing producer run body.'; output = @() }
        }
        else {
            Invoke-RunBody -RunBody $producerRunBody -Environment $producerBaseEnvironment
        }
        $rawEvidenceRoot = Join-Path $preflightFixtureRoot 'raw-evidence'
        Assert-EnabledPath 'enabled-preflight-exact-body-runs-fake-in-process-adapter-after-login-and-creates-artifact' (
            [string]::IsNullOrWhiteSpace([string]$enabledEnablement.failure) -and
            [string]::IsNullOrWhiteSpace([string]$validProducer.failure) -and
            $global:T21R10SqlClientFixture.calls -eq 1 -and
            [string]$global:T21R10SqlClientFixture.authenticated -ceq 'true' -and
            [string]$global:T21R10SqlClientFixture.identityMode -ceq 'readonly' -and
            (Test-Path -LiteralPath (
                    Join-Path $rawEvidenceRoot 'migration-preflight.json') -PathType Leaf) -and
            (Test-Path -LiteralPath (
                    Join-Path $rawEvidenceRoot 't21-producer-manifest.json') -PathType Leaf)
        ) (
            "Enabled fake Preflight did not prove login/readonly/in-process-SQL/artifact ordering. " +
            "enablement=$($enabledEnablement.failure) producer=$($validProducer.failure)"
        )

        foreach ($invalidMode in @('', 'apply')) {
            Remove-Item -LiteralPath $rawEvidenceRoot -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $producerBaseEnvironment.GITHUB_OUTPUT -Force `
                -ErrorAction SilentlyContinue
            $global:T21R10SqlClientFixture.calls = 0
            $invalidEnvironment = @{}
            foreach ($name in $producerBaseEnvironment.Keys) {
                $invalidEnvironment[$name] = $producerBaseEnvironment[$name]
            }
            $invalidEnvironment.T21_MIGRATION_IDENTITY_MODE = $invalidMode
            $invalidProducer = if ($null -eq $producerRunBody) {
                [pscustomobject]@{ failure = 'Missing producer run body.'; output = @() }
            }
            else {
                Invoke-RunBody -RunBody $producerRunBody -Environment $invalidEnvironment
            }
            Assert-EnabledPath "preflight-identity-mode-$(
                if ([string]::IsNullOrEmpty($invalidMode)) { 'missing' } else { 'wrong' })-fails-before-sql-adapter-or-artifact" (
                $invalidProducer.failure.Contains('requires readonly migration identity mode') -and
                $global:T21R10SqlClientFixture.calls -eq 0 -and
                -not (Test-Path -LiteralPath $rawEvidenceRoot) -and
                -not (Test-Path -LiteralPath $producerBaseEnvironment.GITHUB_OUTPUT)
            ) (
                "Invalid identity mode reached fake SQL adapter or artifact publication: " +
                "$($invalidProducer.output -join ' | ') calls=$($global:T21R10SqlClientFixture.calls) " +
                "rawExists=$(Test-Path -LiteralPath $rawEvidenceRoot) " +
                "outputExists=$(Test-Path -LiteralPath $producerBaseEnvironment.GITHUB_OUTPUT)"
            )
        }
    }
    finally {
        Remove-Variable -Scope Global -Name T21R10SqlClientFixture -ErrorAction SilentlyContinue
    }

    # Finding 3: execute the exact I and C report run bodies against a fake protected service
    # client. The workflow secret mapping is the only source of the fake bearer token.
    $stageInputs = Get-Content -LiteralPath (
        Join-Path $workflowRoot 'stage-operation-inputs.yml') -Raw |
        ConvertFrom-Json -DateKind String
    $stageSteps = @($stageInputs.jobs.prepare.steps)
    $stageReportStep = Get-WorkflowStep -Steps $stageSteps `
        -Name 'Generate inert role-derived prepared input reports and bundle'
    $cto = Get-Content -LiteralPath (
        Join-Path $workflowRoot 'cto-authorization-record.yml') -Raw |
        ConvertFrom-Json -DateKind String
    $ctoSteps = @($cto.jobs.'record-authorization'.steps)
    $ctoReportStep = Get-WorkflowStep -Steps $ctoSteps `
        -Name 'Create checked Production change record and CTO authorization'
    $secretExpression = '${{ secrets.T21_STAGE_READONLY_PROBE_TOKEN }}'
    $stageSecretMapping = if ($null -ne $stageReportStep -and
        $null -ne $stageReportStep.PSObject.Properties['env'] -and
        $null -ne $stageReportStep.env.PSObject.Properties['T21_STAGE_READONLY_PROBE_TOKEN']) {
        [string]$stageReportStep.env.T21_STAGE_READONLY_PROBE_TOKEN
    }
    else {
        ''
    }
    $ctoSecretMapping = if ($null -ne $ctoReportStep -and
        $null -ne $ctoReportStep.PSObject.Properties['env'] -and
        $null -ne $ctoReportStep.env.PSObject.Properties['T21_STAGE_READONLY_PROBE_TOKEN']) {
        [string]$ctoReportStep.env.T21_STAGE_READONLY_PROBE_TOKEN
    }
    else {
        ''
    }
    $expectedStageEnvironment =
        '${{ inputs.stage == ''Production'' && ''Husaynia-Production'' || format(''Husaynia-{0}-Operations'', inputs.stage) }}'
    $stageEnvironmentName = if ($null -ne
        $stageInputs.jobs.prepare.PSObject.Properties['environment']) {
        [string]$stageInputs.jobs.prepare.environment.name
    }
    else {
        ''
    }
    Assert-EnabledPath 'stage-inputs-use-stage-exact-protected-environment-and-step-only-probe-secret' (
        $stageEnvironmentName -ceq $expectedStageEnvironment -and
        $stageSecretMapping -ceq $secretExpression -and
        @($stageSteps | Where-Object {
                $null -ne $_.PSObject.Properties['env'] -and
                $null -ne $_.env.PSObject.Properties['T21_STAGE_READONLY_PROBE_TOKEN']
            }).Count -eq 1
    ) 'Prepared inputs lack the exact stage environment or expose the probe secret beyond the report-producing step.'
    Assert-EnabledPath 'cto-keeps-dedicated-environment-and-step-only-probe-secret' (
        [string]$cto.jobs.'record-authorization'.environment -ceq 'Husaynia-CTO-Authorization' -and
        $ctoSecretMapping -ceq $secretExpression -and
        @($ctoSteps | Where-Object {
                $null -ne $_.PSObject.Properties['env'] -and
                $null -ne $_.env.PSObject.Properties['T21_STAGE_READONLY_PROBE_TOKEN']
            }).Count -eq 1
    ) 'CTO authorization lost its dedicated environment or exposes the probe secret beyond its report-producing step.'

    $reportFixtureRoot = Join-Path $temporaryRoot 'reports'
    $fakeRepositoryRoot = Join-Path $reportFixtureRoot 'repo'
    $fakeReportPath = Join-Path $fakeRepositoryRoot 'eng\promotion\New-StageOperationReport.ps1'
    $fakeInputBundlePath = Join-Path $fakeRepositoryRoot 'eng\promotion\New-StageOperationInputBundle.ps1'
    $fakeCtoPath = Join-Path $fakeRepositoryRoot 'eng\promotion\New-CtoAuthorizationRecord.ps1'
    Write-FixtureScript -Path $fakeReportPath -Content @'
param(
    [string]$Stage,
    [string]$EvidenceType,
    [string]$ExpectedAppSha256,
    [string]$ReleaseVerifiedProvenancePath,
    [string]$StageTargetMetadataPath,
    [string]$HealthEndpoint,
    [string]$ReadOnlyProbeHost,
    [string]$BackupRecordEndpoint,
    [string]$RestoreValidationEndpoint,
    [string]$PerformanceEndpoint,
    [int]$PerformanceSampleCount,
    [int]$PerformanceMaxP95Milliseconds,
    [string]$RollbackReferenceEndpoint,
    [string]$SandboxEndpointsJson,
    [string]$AccessibilityProjectPath,
    [string]$VisualProjectPath,
    [string]$VisualBaselineManifestPath,
    [string]$BackupReportPath,
    [string]$ChangeRecordEndpoint,
    [string]$ApprovalReference,
    [switch]$CtoAuthorizationOwner,
    [string]$OutputPath,
    [string]$PolicyPath
)
$token = [Environment]::GetEnvironmentVariable('T21_STAGE_READONLY_PROBE_TOKEN')
if ([string]::IsNullOrWhiteSpace($token)) {
    throw 'Read-only stage probe token is missing: T21_STAGE_READONLY_PROBE_TOKEN'
}
$uri = if ($EvidenceType -ceq 'change-record') { $ChangeRecordEndpoint } else { $BackupRecordEndpoint }
$response = & Invoke-T21FakeHttpClient -Uri $uri -BearerToken $token
if ([string]$response.status -cne 'PASS') {
    throw 'Fake protected report service failed.'
}
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
[IO.File]::WriteAllText(
    $OutputPath,
    ('{"schemaVersion":"1.0.0","stage":"' + $Stage + '","evidenceType":"' +
        $EvidenceType + '","status":"PASS"}'),
    [Text.UTF8Encoding]::new($false))
'@
    Write-FixtureScript -Path $fakeInputBundlePath -Content @'
param(
    [string]$Stage,
    [string]$ExpectedAppSha256,
    [string]$ReleaseVerifiedProvenancePath,
    [string]$StageTargetMetadataPath,
    [string]$DataIsolationKey,
    [string]$ProviderModesJson,
    [string]$HealthEndpoint,
    [string]$CtoApprovalReference,
    [string]$OutputRoot,
    [string]$PolicyPath
)
[IO.File]::WriteAllText(
    (Join-Path $OutputRoot 'stage-operation-inputs.json'),
    '{"schemaVersion":"2.1.0","status":"PASS"}',
    [Text.UTF8Encoding]::new($false))
'@
    Write-FixtureScript -Path $fakeCtoPath -Content @'
param(
    [string]$ExpectedAppSha256,
    [string]$ExpectedBundleSha256,
    [string]$ReleaseVerifiedProvenancePath,
    [string]$PreflightVerifiedProvenancePath,
    [string]$PreparedInputVerifiedProvenancePath,
    [string]$StageTargetMetadataPath,
    [string]$AuthorizedTopLevelCallerWorkflowRef,
    [string]$AuthorizedProducerWorkflowRef,
    [string]$PreparedInputProducerWorkflowRef,
    [string]$Decision,
    [string]$CtoApprovalReference,
    [string]$SourceChangeRecordPath,
    [string]$ExpiresAtUtc,
    [string]$AuthorizedByActor,
    [string]$AuthorizedByActorId,
    [string]$AuthorizedActorIdAllowlist,
    [string]$AuthorizationRepository,
    [string]$AuthorizationWorkflowPath,
    [string]$AuthorizationRunId,
    [string]$AuthorizationRunAttempt,
    [string]$AuthorizationRef,
    [string]$AuthorizationCommitSha,
    [string]$OutputPath,
    [string]$PolicyPath
)
if (-not (Test-Path -LiteralPath $SourceChangeRecordPath -PathType Leaf)) {
    throw 'Fake CTO writer requires the checked change record.'
}
[IO.File]::WriteAllText(
    $OutputPath,
    '{"schemaVersion":"2.1.0","decision":"AUTHORIZE"}',
    [Text.UTF8Encoding]::new($false))
'@
    $fakePolicy = [ordered]@{
        stages = @([ordered]@{
                name = 'Development'
                requiredEvidence = @('backup.json')
            })
    }
    $fakePolicyFile = Join-Path $fakeRepositoryRoot 'pipelines\config\promotion-policy.json'
    New-Item -ItemType Directory -Path (Split-Path -Parent $fakePolicyFile) -Force | Out-Null
    Write-Utf8Json -Value $fakePolicy -Path $fakePolicyFile

    $priorFakeHttp = Get-Item -LiteralPath Function:\global:Invoke-T21FakeHttpClient `
        -ErrorAction SilentlyContinue
    $global:T21R10HttpFixture = [ordered]@{
        calls = 0
        bearerToken = ''
        uris = [Collections.Generic.List[string]]::new()
    }
    try {
        Set-Item -LiteralPath Function:\global:Invoke-T21FakeHttpClient -Force -Value {
            param([string]$Uri, [string]$BearerToken)

            $global:T21R10HttpFixture.calls++
            $global:T21R10HttpFixture.bearerToken = $BearerToken
            [void]$global:T21R10HttpFixture.uris.Add($Uri)
            return [pscustomobject]@{ status = 'PASS' }
        }

        $fakeToken = 'fixture-readonly-bearer-token'
        $stageRunnerTemp = Join-Path $reportFixtureRoot 'stage-runner'
        New-Item -ItemType Directory -Path $stageRunnerTemp -Force | Out-Null
        Write-Utf8Json -Value ([ordered]@{
                dataIsolationKey = 'development'
                providerModes = [ordered]@{
                    payments = 'sandbox'
                    messaging = 'sandbox'
                    analytics = 'sandbox'
                    contentMutation = 'sandbox'
                }
            }) -Path (Join-Path $stageRunnerTemp 'stage-target.json')
        $stageEnvironment = @{
            HUSAYNIA_REPOSITORY_ROOT = $fakeRepositoryRoot
            RUNNER_TEMP = $stageRunnerTemp
            T21_STAGE = 'Development'
            T21_EXPECTED_APP_SHA256 = 'a' * 64
            T21_CTO_APPROVAL_REFERENCE = ''
            T21_HEALTH_ENDPOINT = 'https://dev.example.test/health'
            T21_READONLY_PROBE_HOST = 'probe.example.test'
            T21_BACKUP_RECORD_ENDPOINT = 'https://probe.example.test/backup'
            T21_RESTORE_VALIDATION_ENDPOINT = 'https://probe.example.test/restore'
            T21_ROLLBACK_REFERENCE_ENDPOINT = 'https://probe.example.test/rollback'
            T21_PERFORMANCE_ENDPOINT = 'https://probe.example.test/performance'
            T21_PERFORMANCE_SAMPLE_COUNT = '1'
            T21_PERFORMANCE_MAX_P95_MILLISECONDS = '1000'
            T21_SANDBOX_ENDPOINTS_JSON = '[]'
            T21_ACCESSIBILITY_PROJECT_PATH = 'fixture-accessibility.csproj'
            T21_VISUAL_PROJECT_PATH = 'fixture-visual.csproj'
            T21_VISUAL_BASELINE_MANIFEST_PATH = 'fixture-visual.json'
        }
        if ($stageSecretMapping -ceq $secretExpression) {
            $stageEnvironment.T21_STAGE_READONLY_PROBE_TOKEN = $fakeToken
        }
        $stageRunBody = if ($null -ne $stageReportStep -and
            -not [string]::IsNullOrWhiteSpace([string]$stageReportStep.run)) {
            [scriptblock]::Create([string]$stageReportStep.run)
        }
        else {
            $null
        }
        $stageExecution = if ($null -eq $stageRunBody) {
            [pscustomobject]@{ failure = 'Missing prepared-input report run body.'; output = @() }
        }
        else {
            Invoke-RunBody -RunBody $stageRunBody -Environment $stageEnvironment
        }
        $stageOutputRoot = Join-Path $stageRunnerTemp 'stage-operation-inputs'
        $stageOutputText = if (Test-Path -LiteralPath $stageOutputRoot) {
            (Get-ChildItem -LiteralPath $stageOutputRoot -File |
                ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
        }
        else {
            ''
        }
        Assert-EnabledPath 'enabled-prepared-input-report-body-uses-protected-token-without-persisting-it' (
            [string]::IsNullOrWhiteSpace([string]$stageExecution.failure) -and
            $global:T21R10HttpFixture.calls -eq 1 -and
            [string]$global:T21R10HttpFixture.bearerToken -ceq $fakeToken -and
            (Test-Path -LiteralPath (
                    Join-Path $stageOutputRoot 'source-backup.json') -PathType Leaf) -and
            (Test-Path -LiteralPath (
                    Join-Path $stageOutputRoot 'stage-operation-inputs.json') -PathType Leaf) -and
            -not $stageOutputText.Contains($fakeToken)
        ) "Enabled prepared inputs did not consume the step-only fake token safely: $($stageExecution.output -join ' | ')"

        Remove-Item -LiteralPath $stageOutputRoot -Recurse -Force -ErrorAction SilentlyContinue
        $global:T21R10HttpFixture.calls = 0
        $stageEnvironment.T21_STAGE_READONLY_PROBE_TOKEN = ''
        $stageMissingToken = if ($null -eq $stageRunBody) {
            [pscustomobject]@{ failure = 'Missing prepared-input report run body.'; output = @() }
        }
        else {
            Invoke-RunBody -RunBody $stageRunBody -Environment $stageEnvironment
        }
        Assert-EnabledPath 'prepared-input-missing-token-fails-before-bundle-upload-or-attestation-inputs' (
            $stageMissingToken.failure.Contains('Read-only stage probe token is missing') -and
            $global:T21R10HttpFixture.calls -eq 0 -and
            -not (Test-Path -LiteralPath (
                    Join-Path $stageOutputRoot 'stage-operation-inputs.json')) -and
            -not (Test-Path -LiteralPath (
                    Join-Path $stageOutputRoot 't21-producer-manifest.json'))
        ) "Prepared inputs accepted a missing token or materialized publishable content: $($stageMissingToken.output -join ' | ')"

        $ctoRunnerTemp = Join-Path $reportFixtureRoot 'cto-runner'
        New-Item -ItemType Directory -Path (
            Join-Path $ctoRunnerTemp 'release'), (
            Join-Path $ctoRunnerTemp 'preflight'), (
            Join-Path $ctoRunnerTemp 'prepared-inputs') -Force | Out-Null
        [IO.File]::WriteAllText(
            (Join-Path $ctoRunnerTemp 'stage-target.json'),
            '{}',
            [Text.UTF8Encoding]::new($false))
        $ctoEnvironment = @{
            HUSAYNIA_REPOSITORY_ROOT = $fakeRepositoryRoot
            RUNNER_TEMP = $ctoRunnerTemp
            T21_EXPECTED_APP_SHA256 = 'a' * 64
            T21_EXPECTED_BUNDLE_SHA256 = 'b' * 64
            T21_CTO_APPROVAL_REFERENCE = 'CHG-1001'
            T21_EXPIRES_AT_UTC = [DateTimeOffset]::UtcNow.AddHours(1).ToString('O')
            T21_DECISION = 'AUTHORIZE'
            T21_CTO_AUTHORIZED_ACTOR_IDS = '1001'
            T21_TRUSTED_WORKFLOW_REF =
                "syedmh/Dreamer/.github/workflows/trusted-protected-operations.yml@$('c' * 40)"
            T21_STAGE_INPUTS_WORKFLOW_REF =
                "syedmh/Dreamer/.github/workflows/stage-operation-inputs.yml@$('d' * 40)"
            T21_READONLY_PROBE_HOST = 'probe.example.test'
            T21_CHANGE_RECORD_ENDPOINT = 'https://probe.example.test/change/CHG-1001'
            GITHUB_ACTOR = 'cto-fixture'
            GITHUB_ACTOR_ID = '1001'
            GITHUB_REPOSITORY = 'syedmh/Dreamer'
            GITHUB_RUN_ID = '601'
            GITHUB_RUN_ATTEMPT = '1'
            GITHUB_REF = 'refs/heads/main'
            GITHUB_SHA = 'e' * 40
        }
        if ($ctoSecretMapping -ceq $secretExpression) {
            $ctoEnvironment.T21_STAGE_READONLY_PROBE_TOKEN = $fakeToken
        }
        $ctoRunBody = if ($null -ne $ctoReportStep -and
            -not [string]::IsNullOrWhiteSpace([string]$ctoReportStep.run)) {
            [scriptblock]::Create([string]$ctoReportStep.run)
        }
        else {
            $null
        }
        $global:T21R10HttpFixture.calls = 0
        $ctoExecution = if ($null -eq $ctoRunBody) {
            [pscustomobject]@{ failure = 'Missing CTO report run body.'; output = @() }
        }
        else {
            Invoke-RunBody -RunBody $ctoRunBody -Environment $ctoEnvironment
        }
        $ctoOutputRoot = Join-Path $ctoRunnerTemp "cto-authorization-$('a' * 64)"
        $ctoOutputText = if (Test-Path -LiteralPath $ctoOutputRoot) {
            (Get-ChildItem -LiteralPath $ctoOutputRoot -File |
                ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
        }
        else {
            ''
        }
        Assert-EnabledPath 'enabled-cto-report-body-uses-protected-token-without-persisting-it' (
            [string]::IsNullOrWhiteSpace([string]$ctoExecution.failure) -and
            $global:T21R10HttpFixture.calls -eq 1 -and
            [string]$global:T21R10HttpFixture.bearerToken -ceq $fakeToken -and
            (Test-Path -LiteralPath (
                    Join-Path $ctoOutputRoot 'source-change-record.json') -PathType Leaf) -and
            (Test-Path -LiteralPath (
                    Join-Path $ctoOutputRoot 'cto-authorization.json') -PathType Leaf) -and
            -not $ctoOutputText.Contains($fakeToken)
        ) "Enabled CTO authorization did not consume the step-only fake token safely: $($ctoExecution.output -join ' | ')"

        Remove-Item -LiteralPath $ctoOutputRoot -Recurse -Force -ErrorAction SilentlyContinue
        $global:T21R10HttpFixture.calls = 0
        $ctoEnvironment.T21_STAGE_READONLY_PROBE_TOKEN = ''
        $ctoMissingToken = if ($null -eq $ctoRunBody) {
            [pscustomobject]@{ failure = 'Missing CTO report run body.'; output = @() }
        }
        else {
            Invoke-RunBody -RunBody $ctoRunBody -Environment $ctoEnvironment
        }
        Assert-EnabledPath 'cto-missing-token-fails-before-authorization-upload-or-attestation-inputs' (
            $ctoMissingToken.failure.Contains('Read-only stage probe token is missing') -and
            $global:T21R10HttpFixture.calls -eq 0 -and
            -not (Test-Path -LiteralPath (
                    Join-Path $ctoOutputRoot 'source-change-record.json')) -and
            -not (Test-Path -LiteralPath (
                    Join-Path $ctoOutputRoot 'cto-authorization.json')) -and
            -not (Test-Path -LiteralPath (
                    Join-Path $ctoOutputRoot 't21-producer-manifest.json'))
        ) "CTO authorization accepted a missing token or materialized publishable content: $($ctoMissingToken.output -join ' | ')"
    }
    finally {
        if ($null -eq $priorFakeHttp) {
            Remove-Item -LiteralPath Function:\global:Invoke-T21FakeHttpClient -Force `
                -ErrorAction SilentlyContinue
        }
        else {
            Set-Item -LiteralPath Function:\global:Invoke-T21FakeHttpClient -Force `
                -Value $priorFakeHttp.ScriptBlock
        }
    }
}
finally {
    Remove-Variable -Scope Global -Name T21R10CoordinatorFixture -ErrorAction SilentlyContinue
    Remove-Variable -Scope Global -Name T21R10SqlcmdFixture -ErrorAction SilentlyContinue
    Remove-Variable -Scope Global -Name T21R10HttpFixture -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Output "SUMMARY t21-r10-enabled-path total=$($passed + $failed) passed=$passed failed=$failed auth=0 dispatches=0 deployments=0 receipts=0 database=0 resources=0 secrets=0"
if ($failed -gt 0) { exit 1 }
