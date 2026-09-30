[CmdletBinding()]
param([string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Resolve-HusayniaRepositoryRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$passed = 0
$failed = 0
$simulatedDispatches = 0

function Assert-Lifecycle {
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
    param([hashtable]$Values, [scriptblock]$Body)

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
        & $Body
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

function Invoke-CoordinatorCase {
    param(
        [scriptblock]$RunBody,
        [hashtable]$Environment,
        [hashtable]$Conclusions,
        [object[]]$SeedRuns = @(),
        [int]$PageSize = 100,
        [hashtable]$PredecessorInputs
    )

    $fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) (
        "t21-lifecycle-$([guid]::NewGuid().ToString('N'))")
    $policyPath = Join-Path $fixtureRoot 'pipelines\config\promotion-policy.json'
    $resolverPath = Join-Path $fixtureRoot (
        'eng\promotion\Resolve-GitHubArtifactProvenance.ps1')
    $validatorPath = Join-Path $fixtureRoot (
        'eng\promotion\Test-TrustedProtectedOperationInputs.ps1')
    $completedResolverPath = Join-Path $fixtureRoot (
        'eng\promotion\Resolve-T21CompletedRun.ps1')
    New-Item -ItemType Directory -Path (Split-Path -Parent $policyPath),
        (Split-Path -Parent $resolverPath), (Split-Path -Parent $validatorPath),
        (Split-Path -Parent $completedResolverPath) -Force | Out-Null
    $policy = Get-Content -LiteralPath (
        Join-Path $RepositoryRoot 'pipelines\config\promotion-policy.json') -Raw |
        ConvertFrom-Json -DateKind String
    foreach ($stage in @($policy.stages | Where-Object {
                [string]$_.name -in @('Development', 'Staging')
            })) {
        $stage.deploymentEnabled = $true
    }
    Write-Utf8Json -Value $policy -Path $policyPath -Depth 100
    $resolverFixture = @'
[CmdletBinding()]
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
    [switch]$DiscoverReleaseIdentity,
    [switch]$DiscoverMigrationAuthorizationIdentity,
    [string]$OutputRoot,
    [string]$PolicyPath
)
$ErrorActionPreference = 'Stop'
$scenario = [string]$env:T21_LIFECYCLE_AUTH_SCENARIO
[void]$global:T21LifecycleFixture.events.Add("resolve-$ExpectedRole")
if ($DiscoverReleaseIdentity -and $ExpectedRole -ceq 'release-c6') {
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
    $value = [ordered]@{
        releaseRunId = [string]$RunId
        applicationSha256 = [string]$env:T21_LIFECYCLE_APP_SHA
    }
    [IO.File]::WriteAllText(
        (Join-Path $OutputRoot 'discovered-release-identity.json'),
        ($value | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))
    return
}
if ($DiscoverMigrationAuthorizationIdentity -and
    $ExpectedRole -ceq 'migration-authorization') {
    switch ($scenario) {
        'missing' { throw 'GitHub Actions artifact is missing.' }
        'duplicate' { throw 'GitHub Actions artifact is duplicate.' }
        'producer-failure' { throw 'GitHub Actions producer run is not completed successfully.' }
        'resolver-api-failure' { throw 'GitHub Actions API request failed.' }
        'production' { throw 'Migration authorization artifact is Production-scoped.' }
    }
    New-Item -ItemType Directory -Path (Join-Path $OutputRoot 'artifact') -Force |
        Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $OutputRoot 'artifact.zip'),
        'fixture',
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $OutputRoot 'verified-provenance.json'),
        '{}',
        [Text.UTF8Encoding]::new($false))
    $stage = [string]$env:T21_LIFECYCLE_STAGE
    $identity = [ordered]@{
        stage = $stage
        applicationSha256 = [string]$env:T21_LIFECYCLE_APP_SHA
        preflightEvidenceSha256 = 'e' * 64
        releaseManifestSha256 = 'c' * 64
        releaseCommitSha = 'd' * 40
        releaseRunId = '101'
        releaseRunAttempt = 1
        artifactName =
            "migration-apply-authorization-$stage-$env:T21_LIFECYCLE_APP_SHA-$('e' * 64)"
    }
    if ($scenario -ceq 'malformed') {
        $identity.releaseRunId = 'not-a-selector'
    }
    [IO.File]::WriteAllText(
        (Join-Path $OutputRoot 'discovered-migration-authorization-identity.json'),
        ($identity | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))
    $record = [ordered]@{
        decision = if ($scenario -ceq 'deny') { 'DENY' } else { 'AUTHORIZE' }
        mode = 'Apply'
        stage = if ($scenario -ceq 'wrong-context') { 'Staging' } else { $stage }
        applicationSha256 = [string]$env:T21_LIFECYCLE_APP_SHA
        producerBindings = [ordered]@{
            preflight = [ordered]@{ runId = '201' }
            preparedInputs = [ordered]@{ runId = '301' }
        }
    }
    [IO.File]::WriteAllText(
        (Join-Path $OutputRoot 'artifact\migration-apply-authorization.json'),
        ($record | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))
    return
}
if ($ExpectedRole -notin @('release-c6', 'trusted-preflight', 'stage-operation-inputs')) {
    throw "Unexpected lifecycle resolver role: $ExpectedRole"
}
New-Item -ItemType Directory -Path (Join-Path $OutputRoot 'artifact') -Force |
    Out-Null
[IO.File]::WriteAllText(
    (Join-Path $OutputRoot 'artifact.zip'),
    'fixture',
    [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText(
    (Join-Path $OutputRoot 'verified-provenance.json'),
    '{}',
    [Text.UTF8Encoding]::new($false))
'@
    [IO.File]::WriteAllText(
        $resolverPath,
        $resolverFixture,
        [Text.UTF8Encoding]::new($false))
    $validatorFixture = @'
[CmdletBinding()]
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
$ErrorActionPreference = 'Stop'
[void]$global:T21LifecycleFixture.events.Add('authorization-validation')
$recordPath = Join-Path (Split-Path -Parent $MigrationAuthorizationVerifiedProvenancePath) `
    'artifact\migration-apply-authorization.json'
$record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
if ([string]$record.decision -cne 'AUTHORIZE' -or
    [string]$record.mode -cne 'Apply') {
    throw 'Migration authorization is not canonical APPLY.'
}
if ([string]$record.stage -cne $Stage -or
    [string]$record.applicationSha256 -cne $ExpectedAppSha256) {
    throw 'Migration authorization has the wrong stage or application context.'
}
[void]$global:T21LifecycleFixture.events.Add('authorization-validated')
throw 'T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN'
'@
    [IO.File]::WriteAllText(
        $validatorPath,
        $validatorFixture,
        [Text.UTF8Encoding]::new($false))
    $completedResolverFixture = @'
[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$RunId,
    [string]$ExpectedRepository,
    [string]$ExpectedRepositoryId,
    [string]$ExpectedHeadSha,
    [string]$ExpectedMode = '',
    [string]$ExpectedStage = '',
    [string]$ExpectedSourceReleaseRunId = '',
    [string]$ExpectedAppSha256 = '',
    [string]$ExpectedPreflightRunId = '',
    [string]$ExpectedStageOperationInputsRunId = '',
    [string]$ExpectedMigrationAuthorizationRunId = '',
    [string]$ExpectedCtoAuthorizationRunId = '',
    [string]$ExpectedCorrelationId = '',
    [string]$OutputRoot,
    [string]$FixturePath
)
$ErrorActionPreference = 'Stop'
$matches = @($global:T21LifecycleFixture.runs | Where-Object {
        [string]$_.id -ceq $RunId
    })
if ($matches.Count -ne 1 -or
    [string]$matches[0].status -cne 'completed' -or
    [string]$matches[0].conclusion -cne 'success' -or
    $null -eq $matches[0].PSObject.Properties['inputs']) {
    throw 'Lifecycle completed-run fixture is not reusable.'
}
$inputs = $matches[0].inputs
if (-not [string]::IsNullOrWhiteSpace($ExpectedMode)) {
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
            throw "Lifecycle completed-run input mismatch: $name"
        }
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
    [IO.File]::WriteAllText(
        $completedResolverPath,
        $completedResolverFixture,
        [Text.UTF8Encoding]::new($false))

    $priorGh = Get-Item -LiteralPath Function:\global:gh -ErrorAction SilentlyContinue
    $priorSleep = Get-Item -LiteralPath Function:\global:Start-Sleep `
        -ErrorAction SilentlyContinue
    $fixtureRuns = [Collections.Generic.List[object]]::new()
    foreach ($seedRun in @($SeedRuns)) {
        $fixtureRuns.Add($seedRun)
    }
    if ($null -ne $PredecessorInputs -and
        [string]$Environment.T21_PREDECESSOR_ID -match '^[1-9][0-9]*$' -and
        @($fixtureRuns | Where-Object {
                [string]$_.id -ceq [string]$Environment.T21_PREDECESSOR_ID
            }).Count -eq 0) {
        $fixtureRuns.Add([pscustomobject][ordered]@{
                id = [string]$Environment.T21_PREDECESSOR_ID
                status = 'completed'
                conclusion = 'success'
                inputs = [pscustomobject]$PredecessorInputs
            })
    }
    $global:T21LifecycleFixture = [ordered]@{
        nextId = [int64]1000
        runs = $fixtureRuns
        dispatches = [Collections.Generic.List[object]]::new()
        events = [Collections.Generic.List[string]]::new()
        conclusions = $Conclusions
        pageSize = $PageSize
    }
    try {
        Set-Item -LiteralPath Function:\global:gh -Force -Value {
            param([Parameter(ValueFromRemainingArguments = $true)][object[]]$Arguments)

            $values = @($Arguments | ForEach-Object { [string]$_ })
            if ($values.Count -lt 2 -or $values[0] -cne 'api') {
                throw 'Unexpected lifecycle gh invocation.'
            }
            $endpoint = @($values | Where-Object { $_ -like 'repos/*' }) |
                Select-Object -First 1
            if ($values -contains '-X') {
                $methodIndex = [array]::IndexOf($values, '-X')
                if ($methodIndex -lt 0 -or
                    $values[$methodIndex + 1] -cne 'POST' -or
                    $endpoint -notlike 'repos/*/dispatches') {
                    throw 'Unexpected lifecycle mutation request.'
                }
                $payload = [ordered]@{ ref = ''; inputs = [ordered]@{} }
                for ($index = 0; $index -lt $values.Count; $index++) {
                    if ($values[$index] -cne '-f') { continue }
                    $index++
                    $pair = $values[$index]
                    $separator = $pair.IndexOf('=')
                    if ($separator -le 0) { throw 'Malformed lifecycle raw field.' }
                    $name = $pair.Substring(0, $separator)
                    $value = $pair.Substring($separator + 1)
                    if ($name -ceq 'ref') {
                        $payload.ref = $value
                    }
                    elseif ($name -match '^inputs\[(.+)\]$') {
                        $payload.inputs[$Matches[1]] = $value
                    }
                    else {
                        throw "Unexpected lifecycle field: $name"
                    }
                }
                $key = [string]$payload.inputs.orchestrationCorrelationId
                if ([string]::IsNullOrWhiteSpace($key)) {
                    throw 'Lifecycle dispatch omitted its correlation key.'
                }
                $global:T21LifecycleFixture.nextId++
                $mode = [string]$payload.inputs.mode
                $conclusion = if (
                    $global:T21LifecycleFixture.conclusions.ContainsKey($mode)) {
                    [string]$global:T21LifecycleFixture.conclusions[$mode]
                }
                else {
                    'success'
                }
                $run = [ordered]@{
                    id = $global:T21LifecycleFixture.nextId
                    status = 'completed'
                    conclusion = $conclusion
                    inputs = [pscustomobject]$payload.inputs
                }
                $global:T21LifecycleFixture.runs.Add([pscustomobject]$run)
                $global:T21LifecycleFixture.dispatches.Add(
                    [pscustomobject]@{
                        endpoint = $endpoint
                        ref = [string]$payload.ref
                        inputs = [pscustomobject]$payload.inputs
                        runId = [string]$run.id
                    })
                [void]$global:T21LifecycleFixture.events.Add('dispatch')
                $global:LASTEXITCODE = 0
                return
            }
            if ($endpoint -like
                'repos/*/actions/workflows/operation-evidence-producer.yml/runs*') {
                $allRuns = @($global:T21LifecycleFixture.runs)
                $pages = [Collections.Generic.List[object]]::new()
                if ($allRuns.Count -eq 0) {
                    $pages.Add([pscustomobject]@{ workflow_runs = @() })
                }
                else {
                    for ($offset = 0; $offset -lt $allRuns.Count;
                        $offset += $global:T21LifecycleFixture.pageSize) {
                        $last = [Math]::Min(
                            $offset + $global:T21LifecycleFixture.pageSize - 1,
                            $allRuns.Count - 1)
                        $pages.Add([pscustomobject]@{
                                workflow_runs = @($allRuns[$offset..$last])
                            })
                    }
                }
                $global:LASTEXITCODE = 0
                if ($values -contains '--paginate' -and $values -contains '--slurp') {
                    return (ConvertTo-Json -InputObject @($pages) -Depth 20 -Compress)
                }
                return ($pages[0] | ConvertTo-Json -Depth 20 -Compress)
            }
            if ($endpoint -match '/actions/runs/([1-9][0-9]*)$') {
                $id = $Matches[1]
                $run = @($global:T21LifecycleFixture.runs | Where-Object {
                        [string]$_.id -ceq $id
                    })
                if ($run.Count -ne 1) {
                    throw "Unknown lifecycle child run: $id"
                }
                $global:LASTEXITCODE = 0
                return ($run[0] | ConvertTo-Json -Depth 10 -Compress)
            }
            throw "Unexpected lifecycle gh endpoint: $endpoint"
        }
        Set-Item -LiteralPath Function:\global:Start-Sleep -Force -Value {
            param([int]$Seconds)
        }

        $effectiveEnvironment = @{} + $Environment
        $effectiveEnvironment.HUSAYNIA_REPOSITORY_ROOT = $fixtureRoot
        $effectiveEnvironment.RUNNER_TEMP = Join-Path $fixtureRoot 'runner'
        $effectiveEnvironment.GITHUB_STEP_SUMMARY = Join-Path $fixtureRoot 'summary.md'
        $effectiveEnvironment.T21_LIFECYCLE_APP_SHA =
            [string]$Environment.T21_LIFECYCLE_APP_SHA
        $effectiveEnvironment.T21_COMPLETED_RUN_FIXTURE_ROOT =
            Join-Path $fixtureRoot 'completed-run-fixtures'
        New-Item -ItemType Directory `
            -Path $effectiveEnvironment.T21_COMPLETED_RUN_FIXTURE_ROOT -Force |
            Out-Null
        New-Item -ItemType Directory -Path $effectiveEnvironment.RUNNER_TEMP -Force |
            Out-Null

        $output = [Collections.Generic.List[string]]::new()
        $state = [pscustomobject]@{ failure = '' }
        Invoke-WithEnvironment -Values $effectiveEnvironment -Body {
            try {
                & $RunBody 2>&1 | ForEach-Object {
                    [void]$output.Add($_.ToString())
                }
            }
            catch {
                $state.failure = $_.Exception.Message
                [void]$output.Add($state.failure)
            }
        } | Out-Null

        $dispatches = @($global:T21LifecycleFixture.dispatches)
        $script:simulatedDispatches += $dispatches.Count
        return [pscustomobject]@{
            failure = [string]$state.failure
            output = @($output)
            dispatches = $dispatches
            events = @($global:T21LifecycleFixture.events)
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
            Remove-Item -LiteralPath Function:\global:Start-Sleep -Force `
                -ErrorAction SilentlyContinue
        }
        else {
            Set-Item -LiteralPath Function:\global:Start-Sleep -Force `
                -Value $priorSleep.ScriptBlock
        }
        Remove-Variable -Scope Global -Name T21LifecycleFixture `
            -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$workflowRoot = Join-Path $RepositoryRoot 'pipelines\github'
$coordinatorPath = Join-Path $workflowRoot 'automatic-nonproduction-orchestration.yml'
$coordinatorText = Get-Content -LiteralPath $coordinatorPath -Raw
$coordinator = $coordinatorText | ConvertFrom-Json -DateKind String
$coordinatorScriptPath = Join-Path $RepositoryRoot (
    'eng\promotion\Invoke-T21CompletedRunCoordinator.ps1')
$coordinatorScriptText = Get-Content -LiteralPath $coordinatorScriptPath -Raw
$coordinatorStep = @($coordinator.jobs.coordinate.steps | Where-Object {
        [string]$_.name -ceq 'Dispatch only idempotent completed-run successors'
    })
$coordinatorRun = if ($coordinatorStep.Count -eq 1) {
    { & $coordinatorScriptPath }.GetNewClosure()
}
else {
    $null
}
$appSha = 'a' * 64
$bundleSha = 'b' * 64
$baseEnvironment = @{
    GITHUB_REPOSITORY = 'syedmh/Dreamer'
    GITHUB_RUN_ID = '900'
    T21_TRUSTED_BUNDLE_SHA256 = $bundleSha
    T21_TRUSTED_WORKFLOW_REF =
        "syedmh/Dreamer/.github/workflows/trusted-protected-operations.yml@$('1' * 40)"
    T21_STAGE_INPUTS_WORKFLOW_REF =
        "syedmh/Dreamer/.github/workflows/stage-operation-inputs.yml@$('2' * 40)"
    T21_MIGRATION_AUTHORIZATION_WORKFLOW_REF =
        "syedmh/Dreamer/.github/workflows/migration-apply-authorization.yml@$('4' * 40)"
    T21_STAGE_TARGET_METADATA_JSON = '{"schemaVersion":"1.0.0"}'
    T21_DEVELOPMENT_ENABLED = 'true'
    T21_STAGING_ENABLED = 'true'
    T21_LIFECYCLE_APP_SHA = $appSha
    T21_REPOSITORY_ID = '42'
    T21_PROTECTED_HEAD_SHA = '9' * 40
}

Assert-Lifecycle 'coordinator-runs-only-for-completed-success-protected-main-predecessors' (
    $coordinatorStep.Count -eq 1 -and
    [string]$coordinatorStep[0].run -ceq
        "& (Join-Path `$env:HUSAYNIA_REPOSITORY_ROOT 'eng/promotion/Invoke-T21CompletedRunCoordinator.ps1')" -and
    (@($coordinator.on.workflow_run.types) -join ',') -ceq 'completed' -and
    [string]$coordinator.jobs.coordinate.if -ceq (
        '${{ github.event.workflow_run.status == ''completed'' && ' +
        'github.event.workflow_run.conclusion == ''success'' && ' +
        'github.event.workflow_run.repository.full_name == github.repository && ' +
        'github.event.workflow_run.head_branch == ''main'' }}')
) 'The coordinator event/job gate can advance an incomplete, failed, foreign, or non-main run.'
Assert-Lifecycle 'coordinator-resolves-and-validates-authorization-without-predecessor-title' (
    $coordinatorScriptText.Contains('-DiscoverMigrationAuthorizationIdentity') -and
    $coordinatorScriptText.Contains('-ExpectedRole migration-authorization') -and
    $coordinatorScriptText.Contains('Test-TrustedProtectedOperationInputs.ps1') -and
    $coordinatorScriptText.IndexOf(
        'Test-TrustedProtectedOperationInputs.ps1',
        [StringComparison]::Ordinal) -lt
        $coordinatorScriptText.IndexOf(
            '-Mode StageOperations',
            [StringComparison]::Ordinal) -and
    -not $coordinatorScriptText.Contains('^T21 R10 M ')
) 'Migration dispatch still trusts the predecessor title or skips resolver/validator authorization.'

$releaseCase = if ($null -eq $coordinatorRun) {
    [pscustomobject]@{ failure = 'Missing coordinator run body.'; dispatches = @(); output = @() }
}
else {
    Invoke-CoordinatorCase -RunBody $coordinatorRun -Conclusions @{} -Environment (
        $baseEnvironment + @{
            T21_PREDECESSOR_ID = '101'
            T21_PREDECESSOR_NAME = 'T21 immutable release C6'
            T21_PREDECESSOR_PATH =
                '.github/workflows/release-build-and-nonproduction.yml'
        })
}
$releaseModes = @($releaseCase.dispatches | ForEach-Object {
        [string]$_.inputs.mode
    } | Sort-Object)
Assert-Lifecycle 'release-completion-dispatches-distinct-preflight-and-prepared-runs' (
    [string]::IsNullOrWhiteSpace([string]$releaseCase.failure) -and
    $releaseCase.dispatches.Count -eq 2 -and
    ($releaseModes -join ',') -ceq 'Preflight,PreparedInputs' -and
    @($releaseCase.dispatches | Where-Object {
            [string]$_.inputs.stage -cne 'Development' -or
            [string]$_.inputs.sourceReleaseRunId -cne '101' -or
            [string]$_.inputs.expectedAppSha256 -cne $appSha -or
            -not [string]::IsNullOrWhiteSpace([string]$_.inputs.preflightRunId) -or
            -not [string]::IsNullOrWhiteSpace(
                [string]$_.inputs.stageOperationInputsRunId) -or
            -not [string]::IsNullOrWhiteSpace(
                [string]$_.inputs.migrationAuthorizationRunId)
        }).Count -eq 0 -and
    @($releaseCase.dispatches.runId | Select-Object -Unique).Count -eq 2
) "Release successor sequence was not R -> distinct P/I: $($releaseCase.failure)"

$paginatedSeedRuns = [Collections.Generic.List[object]]::new()
foreach ($index in 1..100) {
    $paginatedSeedRuns.Add([pscustomobject]@{
            id = [string](5000 + $index)
            display_title = "unrelated historical run $index"
            status = 'completed'
            conclusion = 'success'
        })
}
$paginatedSeedRuns.Add([pscustomobject]@{
        id = '7001'
        status = 'completed'
        conclusion = 'success'
        inputs = [pscustomobject][ordered]@{
            mode = 'Preflight'
            stage = 'Development'
            sourceReleaseRunId = '777'
            expectedAppSha256 = $appSha
            preflightRunId = ''
            stageOperationInputsRunId = ''
            migrationAuthorizationRunId = ''
            ctoAuthorizationRunId = ''
            orchestrationCorrelationId = 'r10-777-development-preflight'
        }
    })
$paginatedReleaseCase = Invoke-CoordinatorCase -RunBody $coordinatorRun `
    -Conclusions @{} -SeedRuns @($paginatedSeedRuns) -PageSize 100 -Environment (
        $baseEnvironment + @{
            T21_PREDECESSOR_ID = '777'
            T21_PREDECESSOR_NAME = 'T21 immutable release C6'
            T21_PREDECESSOR_PATH =
                '.github/workflows/release-build-and-nonproduction.yml'
        })
Assert-Lifecycle 'idempotency-discovers-correlated-runs-beyond-first-hundred' (
    [string]::IsNullOrWhiteSpace([string]$paginatedReleaseCase.failure) -and
    $paginatedReleaseCase.dispatches.Count -eq 1 -and
    [string]$paginatedReleaseCase.dispatches[0].inputs.mode -ceq 'PreparedInputs' -and
    [string]$paginatedReleaseCase.dispatches[0].inputs.sourceReleaseRunId -ceq '777'
) (
    'Paginated discovery did not reuse the existing page-two Preflight run: ' +
    $paginatedReleaseCase.failure)

$producerCompletionResults = foreach ($mode in @('Preflight', 'PreparedInputs')) {
    $producerInputs = [ordered]@{
        mode = $mode
        stage = 'Development'
        sourceReleaseRunId = '101'
        expectedAppSha256 = $appSha
        preflightRunId = ''
        stageOperationInputsRunId = ''
        migrationAuthorizationRunId = ''
        ctoAuthorizationRunId = ''
        orchestrationCorrelationId = 'fixture'
    }
    Invoke-CoordinatorCase -RunBody $coordinatorRun -Conclusions @{} `
        -PredecessorInputs $producerInputs -Environment (
        $baseEnvironment + @{
            T21_PREDECESSOR_ID = if ($mode -ceq 'Preflight') { '201' } else { '301' }
            T21_PREDECESSOR_NAME = 'T21 disabled nonproduction operation wrapper'
            T21_PREDECESSOR_PATH = '.github/workflows/operation-evidence-producer.yml'
        })
}
Assert-Lifecycle 'completed-producers-wait-for-separate-protected-migration-authorization' (
    @($producerCompletionResults | Where-Object {
            -not [string]::IsNullOrWhiteSpace([string]$_.failure) -or
            $_.dispatches.Count -ne 0
        }).Count -eq 0
) 'P/I completion bypassed the separately completed protected migration authorization run.'

$failedReleaseCase = Invoke-CoordinatorCase -RunBody $coordinatorRun `
    -Conclusions @{ Preflight = 'failure' } -Environment (
        $baseEnvironment + @{
            T21_PREDECESSOR_ID = '102'
            T21_PREDECESSOR_NAME = 'T21 immutable release C6'
            T21_PREDECESSOR_PATH =
                '.github/workflows/release-build-and-nonproduction.yml'
        })
Assert-Lifecycle 'failed-producer-stops-before-any-later-phase-dispatch' (
    $failedReleaseCase.failure.Contains(
        'could not identify exactly one canonical completed child') -and
    $failedReleaseCase.dispatches.Count -eq 1 -and
    [string]$failedReleaseCase.dispatches[0].inputs.mode -ceq 'Preflight'
) 'A failed P/I run advanced to authorization or StageOperations.'

function Invoke-MigrationCompletionCase {
    param(
        [string]$Stage,
        [string]$RunId,
        [string]$Scenario = 'apply',
        [object[]]$SeedRuns = @()
    )

    Invoke-CoordinatorCase -RunBody $coordinatorRun -Conclusions @{} `
        -SeedRuns $SeedRuns -Environment (
        $baseEnvironment + @{
            T21_PREDECESSOR_ID = $RunId
            T21_PREDECESSOR_NAME = 'T21 protected migration Apply authorization'
            T21_PREDECESSOR_PATH =
                '.github/workflows/migration-apply-authorization.yml'
            T21_LIFECYCLE_STAGE = $Stage
            T21_LIFECYCLE_AUTH_SCENARIO = $Scenario
        })
}

$developmentMigration = Invoke-MigrationCompletionCase -Stage Development -RunId 401
$developmentDispatch = @($developmentMigration.dispatches)
Assert-Lifecycle 'canonical-apply-dispatches-exactly-one-stageoperations' (
    [string]::IsNullOrWhiteSpace([string]$developmentMigration.failure) -and
    $developmentDispatch.Count -eq 1 -and
    [string]$developmentDispatch[0].inputs.mode -ceq 'StageOperations' -and
    [string]$developmentDispatch[0].inputs.stage -ceq 'Development' -and
    [string]$developmentDispatch[0].inputs.sourceReleaseRunId -ceq '101' -and
    [string]$developmentDispatch[0].inputs.expectedAppSha256 -ceq $appSha -and
    [string]$developmentDispatch[0].inputs.preflightRunId -ceq '201' -and
    [string]$developmentDispatch[0].inputs.stageOperationInputsRunId -ceq '301' -and
    [string]$developmentDispatch[0].inputs.migrationAuthorizationRunId -ceq '401'
) "Canonical APPLY did not produce exactly one Development dispatch: $($developmentMigration.failure)"
$validationIndex = [array]::IndexOf(
    [object[]]$developmentMigration.events,
    'authorization-validated')
$dispatchIndex = [array]::IndexOf([object[]]$developmentMigration.events, 'dispatch')
Assert-Lifecycle 'authorization-validation-strictly-precedes-dispatch' (
    $validationIndex -ge 0 -and
    $dispatchIndex -gt $validationIndex
) "Authorization/dispatch event order was $($developmentMigration.events -join ',')."

$stagingMigration = Invoke-MigrationCompletionCase -Stage Staging -RunId 402
$stagingDispatch = @($stagingMigration.dispatches)
Assert-Lifecycle 'completed-staging-migration-dispatches-separate-stageoperations' (
    [string]::IsNullOrWhiteSpace([string]$stagingMigration.failure) -and
    $stagingDispatch.Count -eq 1 -and
    [string]$stagingDispatch[0].inputs.mode -ceq 'StageOperations' -and
    [string]$stagingDispatch[0].inputs.stage -ceq 'Staging' -and
    [string]$stagingDispatch[0].inputs.expectedAppSha256 -ceq $appSha -and
    [string]$stagingDispatch[0].inputs.migrationAuthorizationRunId -ceq '402'
) "Staging M -> S routing changed: $($stagingMigration.failure)"

$deniedMigration = Invoke-MigrationCompletionCase -Stage Development -RunId 410 `
    -Scenario deny
$malformedMigration = Invoke-MigrationCompletionCase -Stage Development -RunId 411 `
    -Scenario malformed
$missingMigration = Invoke-MigrationCompletionCase -Stage Development -RunId 412 `
    -Scenario missing
$duplicateMigration = Invoke-MigrationCompletionCase -Stage Development -RunId 413 `
    -Scenario duplicate
$wrongContextMigration = Invoke-MigrationCompletionCase -Stage Development -RunId 414 `
    -Scenario wrong-context
$producerFailureMigration = Invoke-MigrationCompletionCase -Stage Development -RunId 415 `
    -Scenario producer-failure
$resolverFailureMigration = Invoke-MigrationCompletionCase -Stage Development -RunId 416 `
    -Scenario resolver-api-failure
$replayKey = 'r10-417-development-stageoperations'
$replayedMigration = Invoke-MigrationCompletionCase -Stage Development -RunId 417 `
    -Scenario apply -SeedRuns @([pscustomobject]@{
            id = '8417'
            status = 'completed'
            conclusion = 'success'
            inputs = [pscustomobject][ordered]@{
                mode = 'StageOperations'
                stage = 'Development'
                sourceReleaseRunId = '101'
                expectedAppSha256 = $appSha
                preflightRunId = '201'
                stageOperationInputsRunId = '301'
                migrationAuthorizationRunId = '417'
                ctoAuthorizationRunId = ''
                orchestrationCorrelationId = $replayKey
            }
        })
$zeroDispatchCases = [ordered]@{
    deny = $deniedMigration
    malformed = $malformedMigration
    missing = $missingMigration
    duplicate = $duplicateMigration
    replay = $replayedMigration
    'wrong-context' = $wrongContextMigration
    'producer-failure' = $producerFailureMigration
    'resolver-api-failure' = $resolverFailureMigration
}
foreach ($entry in $zeroDispatchCases.GetEnumerator()) {
    Assert-Lifecycle "$($entry.Key)-authorization-produces-zero-stageoperations-dispatch" (
        @($entry.Value.dispatches).Count -eq 0
    ) "$($entry.Key) produced a StageOperations dispatch: $($entry.Value.failure)"
}

$productionMigration = Invoke-MigrationCompletionCase -Stage Production -RunId 403 `
    -Scenario production
Assert-Lifecycle 'production-migration-is-never-automatically-routed' (
    $productionMigration.failure.Contains('Production-scoped') -and
    $productionMigration.dispatches.Count -eq 0
) 'The nonproduction coordinator accepted a Production authorization completion.'

$stagingBoundaryInputs = [ordered]@{
    mode = 'StageOperations'
    stage = 'Development'
    sourceReleaseRunId = '101'
    expectedAppSha256 = $appSha
    preflightRunId = '201'
    stageOperationInputsRunId = '301'
    migrationAuthorizationRunId = '401'
    ctoAuthorizationRunId = ''
    orchestrationCorrelationId = 'fixture'
}
$stagingBoundary = Invoke-CoordinatorCase -RunBody $coordinatorRun -Conclusions @{} `
    -PredecessorInputs $stagingBoundaryInputs -Environment (
        $baseEnvironment + @{
            T21_PREDECESSOR_ID = '501'
            T21_PREDECESSOR_NAME = 'T21 disabled nonproduction operation wrapper'
            T21_PREDECESSOR_PATH = '.github/workflows/operation-evidence-producer.yml'
        })
Assert-Lifecycle 'development-completion-cannot-fabricate-staging-receipt-or-success' (
    $stagingBoundary.failure -ceq 'T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN' -and
    $stagingBoundary.dispatches.Count -eq 0
) 'Development completion produced a success-shaped Staging successor without a receipt.'

$resolverText = Get-Content -LiteralPath (
    Join-Path $RepositoryRoot 'eng\promotion\Resolve-GitHubArtifactProvenance.ps1') -Raw
$completedResolverText = Get-Content -LiteralPath (
    Join-Path $RepositoryRoot 'eng\promotion\Resolve-T21CompletedRun.ps1') -Raw
$wrapperText = (Get-Content -LiteralPath (
        Join-Path $workflowRoot 'operation-evidence-producer.yml') -Raw) +
    (Get-Content -LiteralPath (
        Join-Path $workflowRoot 'production-operation-evidence.yml') -Raw)
Assert-Lifecycle 'all-selectors-are-completed-successful-prior-runs' (
    $resolverText.Contains('$RunId -ceq $ConsumerRunId') -and
    $resolverText.Contains('$selectedUpdatedAt -gt $consumerCreatedAt') -and
    $resolverText.Contains("[string]`$run.status -cne 'completed'") -and
    $resolverText.Contains("[string]`$run.conclusion -cne 'success'") -and
    $completedResolverText.Contains(
        "[string]`$run.event -cne 'workflow_dispatch'") -and
    $completedResolverText.Contains("[string]`$run.status -cne 'completed'") -and
    $completedResolverText.Contains("[string]`$run.conclusion -cne 'success'") -and
    -not $wrapperText.Contains(
        '"sourceReleaseRunId": "${{ github.run_id }}"')
) 'Current, future, in-progress, failed, or cancelled selectors can reach a consumer.'

$triggerNames = @($coordinator.on.workflow_run.workflows)
$productionWrapper = Get-Content -LiteralPath (
    Join-Path $workflowRoot 'production-operation-evidence.yml') -Raw |
    ConvertFrom-Json -DateKind String
$productionPromotion = Get-Content -LiteralPath (
    Join-Path $workflowRoot 'production-promotion.yml') -Raw |
    ConvertFrom-Json -DateKind String
Assert-Lifecycle 'graph-is-cycle-free-and-production-remains-separate-manual-disabled' (
    -not ($triggerNames -contains [string]$coordinator.name) -and
    -not ($triggerNames -contains 'T21 disabled manual Production operation wrapper') -and
    -not $coordinatorText.Contains('production-operation-evidence.yml') -and
    $null -ne $productionWrapper.on.workflow_dispatch -and
    [string]$productionPromotion.jobs.production.if -ceq
        '${{ github.event_name == ''schedule'' }}'
) 'The graph contains a self-cycle or an automatic Production edge.'

$migrationText = Get-Content -LiteralPath (
    Join-Path $workflowRoot 'migration-apply-authorization.yml') -Raw
Assert-Lifecycle 'one-immutable-checksum-flows-through-r-p-i-m-s-without-rebuild' (
    @($releaseCase.dispatches | Where-Object {
            [string]$_.inputs.expectedAppSha256 -cne $appSha
        }).Count -eq 0 -and
    [string]$developmentDispatch[0].inputs.expectedAppSha256 -ceq $appSha -and
    [string]$stagingDispatch[0].inputs.expectedAppSha256 -ceq $appSha -and
    $migrationText.Contains('a${{ inputs.expectedAppSha256 }}') -and
    $migrationText.Contains(
        'T21_EXPECTED_APP_SHA256": "${{ inputs.expectedAppSha256 }}') -and
    -not $migrationText.Contains('Invoke-PrValidation.ps1')
) 'A phase can change/rebuild C6 or lose the application checksum binding.'

Write-Output (
    "SUMMARY t21-lifecycle-state-machine total=$($passed + $failed) " +
    "passed=$passed failed=$failed simulatedDispatches=$simulatedDispatches " +
    "actualDispatches=0 auth=0 deployments=0 receipts=0 database=0 resources=0 " +
    "installs=0 restores=0"
)
if ($failed -gt 0) { exit 1 }
