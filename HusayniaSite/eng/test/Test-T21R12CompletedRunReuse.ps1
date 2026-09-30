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
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "t21-r12-reuse-$([guid]::NewGuid().ToString('N'))")
$fixtureApiRoot = Join-Path $fixtureRoot 'api'
$appSha = 'a' * 64
$headSha = '9' * 40
$repository = 'syedmh/Dreamer'
$repositoryId = '42'
$expectedInputs = [ordered]@{
    mode = 'StageOperations'
    stage = 'Development'
    sourceReleaseRunId = '101'
    expectedAppSha256 = $appSha
    preflightRunId = '201'
    stageOperationInputsRunId = '301'
    migrationAuthorizationRunId = '401'
    ctoAuthorizationRunId = ''
    orchestrationCorrelationId = 'r10-401-development-stageoperations'
}

function Assert-R12 {
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

function Get-DispatchHash {
    param($Inputs)

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
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes(
        ($normalized | ConvertTo-Json -Depth 10 -Compress))
    return ([Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($bytes)
    )).ToLowerInvariant()
}

function New-CompletedRunApiFixture {
    param(
        [string]$RunId,
        [Collections.IDictionary]$Inputs,
        [string]$FixtureHeadSha = $script:headSha,
        [string]$FixtureRepositoryId = $script:repositoryId,
        [int]$RunAttempt = 1,
        [scriptblock]$ManifestMutation,
        [scriptblock]$RunMutation,
        [switch]$MissingAttestation
    )

    $work = Join-Path $script:fixtureApiRoot "work-$RunId"
    $content = Join-Path $work 'content'
    New-Item -ItemType Directory -Path $content -Force | Out-Null
    $manifestPath = Join-Path $content 't21-completed-run.json'
    $graph = switch ([string]$Inputs.mode) {
        'Preflight' { @('success', 'success', 'skipped', 'skipped') }
        'PreparedInputs' { @('success', 'skipped', 'success', 'skipped') }
        default { @('success', 'skipped', 'skipped', 'success') }
    }
    & (Join-Path $RepositoryRoot 'eng\promotion\New-T21CompletedRunManifest.ps1') `
        -Mode ([string]$Inputs.mode) -Stage ([string]$Inputs.stage) `
        -SourceReleaseRunId ([string]$Inputs.sourceReleaseRunId) `
        -ExpectedAppSha256 ([string]$Inputs.expectedAppSha256) `
        -PreflightRunId ([string]$Inputs.preflightRunId) `
        -StageOperationInputsRunId ([string]$Inputs.stageOperationInputsRunId) `
        -MigrationAuthorizationRunId ([string]$Inputs.migrationAuthorizationRunId) `
        -CtoAuthorizationRunId ([string]$Inputs.ctoAuthorizationRunId) `
        -OrchestrationCorrelationId ([string]$Inputs.orchestrationCorrelationId) `
        -Repository $repository -RepositoryId $FixtureRepositoryId `
        -WorkflowPath '.github/workflows/operation-evidence-producer.yml' `
        -EventName workflow_dispatch -HeadRef refs/heads/main `
        -HeadSha $FixtureHeadSha -RunId $RunId -RunAttempt $RunAttempt `
        -ValidateInputShapeResult $graph[0] -PreflightResult $graph[1] `
        -PreparedInputsResult $graph[2] -StageOperationsResult $graph[3] `
        -OutputPath $manifestPath | Out-Null
    if ($null -ne $ManifestMutation) {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw |
            ConvertFrom-Json -DateKind String
        & $ManifestMutation $manifest
        $expectedInputProperties = @(
            'ctoAuthorizationRunId', 'expectedAppSha256',
            'migrationAuthorizationRunId', 'mode',
            'orchestrationCorrelationId', 'preflightRunId',
            'sourceReleaseRunId', 'stage', 'stageOperationInputsRunId'
        )
        if ($null -ne $manifest.PSObject.Properties['dispatchInputs'] -and
            $null -ne $manifest.PSObject.Properties['dispatchInputsSha256'] -and
            @(Compare-Object ($expectedInputProperties | Sort-Object) @(
                    $manifest.dispatchInputs.PSObject.Properties.Name |
                        Sort-Object
                )).Count -eq 0) {
            $manifest.dispatchInputsSha256 = Get-DispatchHash `
                -Inputs $manifest.dispatchInputs
        }
        Write-Utf8Json -Value $manifest -Path $manifestPath -Depth 20
    }

    $archivePath = Join-Path $work 'artifact.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $content,
        $archivePath,
        [IO.Compression.CompressionLevel]::NoCompression,
        $false)
    $now = [DateTimeOffset]::UtcNow
    $run = [pscustomobject][ordered]@{
        id = $RunId
        path = '.github/workflows/operation-evidence-producer.yml'
        event = 'workflow_dispatch'
        head_branch = 'main'
        head_sha = $FixtureHeadSha
        status = 'completed'
        conclusion = 'success'
        run_attempt = $RunAttempt
        created_at = $now.AddMinutes(-5).ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'")
        updated_at = $now.AddMinutes(-3).ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'")
        repository = [pscustomobject][ordered]@{
            full_name = $repository
            id = $FixtureRepositoryId
        }
        head_repository = [pscustomobject][ordered]@{
            full_name = $repository
            id = $FixtureRepositoryId
        }
    }
    if ($null -ne $RunMutation) {
        & $RunMutation $run
    }
    $artifactId = [string]([int64]$RunId + 100000)
    $fixture = [ordered]@{
        run = $run
        jobs = @([ordered]@{
                name = 'T21 canonical completed-run attestation'
                run_id = $RunId
                head_sha = [string]$run.head_sha
                status = 'completed'
                conclusion = 'success'
            })
        artifacts = @([ordered]@{
                id = $artifactId
                name = "t21-completed-run-$RunId-$RunAttempt"
                workflow_run = [ordered]@{
                    id = $RunId
                    repository_id = [string]$run.repository.id
                    head_repository_id = [string]$run.head_repository.id
                    head_branch = [string]$run.head_branch
                    head_sha = [string]$run.head_sha
                }
                expired = $false
                size_in_bytes = (Get-Item -LiteralPath $archivePath).Length
                digest = "sha256:$(Get-Sha256Lower -Path $archivePath)"
                created_at = $now.AddMinutes(-2).ToString(
                    "yyyy-MM-dd'T'HH:mm:ss'Z'")
                expires_at = $now.AddDays(1).ToString(
                    "yyyy-MM-dd'T'HH:mm:ss'Z'")
                archivePath = $archivePath
            })
        attestation = if ($MissingAttestation) {
            $null
        }
        else {
            [ordered]@{
                predicateType = 'https://slsa.dev/provenance/v1'
                signerWorkflow =
                    "$repository/.github/workflows/operation-evidence-producer.yml"
                signerDigest = [string]$run.head_sha
                sourceRef = 'refs/heads/main'
                sourceCommitSha = [string]$run.head_sha
                status = 'verified'
            }
        }
    }
    Write-Utf8Json -Value $fixture -Path (
        Join-Path $script:fixtureApiRoot "$RunId.json") -Depth 30
}

function Write-FixtureScript {
    param([string]$Path, [string]$Content)

    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force |
        Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Invoke-CoordinatorFixtureCase {
    param(
        [object[]]$SeedRuns = @(),
        [string]$PostDispatchRunId = '',
        [string]$AuthorizationScenario = 'apply'
    )

    $priorGh = Get-Item -LiteralPath Function:\global:gh `
        -ErrorAction SilentlyContinue
    $priorSleep = Get-Item -LiteralPath Function:\global:Start-Sleep `
        -ErrorAction SilentlyContinue
    $global:T21R12Fixture = [ordered]@{
        seedRuns = @($SeedRuns)
        postDispatchRunId = $PostDispatchRunId
        dispatched = $false
        dispatches = [Collections.Generic.List[object]]::new()
    }
    try {
        Set-Item -LiteralPath Function:\global:gh -Force -Value {
            param([Parameter(ValueFromRemainingArguments = $true)]
                [object[]]$Arguments)

            $values = @($Arguments | ForEach-Object { [string]$_ })
            if ($values.Count -lt 2 -or $values[0] -cne 'api') {
                throw 'Unexpected T21-R12 gh invocation.'
            }
            $endpoint = @($values | Where-Object { $_ -like 'repos/*' }) |
                Select-Object -First 1
            if ($values -contains '-X') {
                $payload = [ordered]@{ ref = ''; inputs = [ordered]@{} }
                for ($index = 0; $index -lt $values.Count; $index++) {
                    if ($values[$index] -cne '-f') { continue }
                    $index++
                    $pair = $values[$index]
                    $separator = $pair.IndexOf('=')
                    if ($separator -le 0) {
                        throw 'Malformed T21-R12 dispatch field.'
                    }
                    $name = $pair.Substring(0, $separator)
                    $value = $pair.Substring($separator + 1)
                    if ($name -ceq 'ref') {
                        $payload.ref = $value
                    }
                    elseif ($name -match '^inputs\[(.+)\]$') {
                        $payload.inputs[$Matches[1]] = $value
                    }
                    else {
                        throw "Unexpected T21-R12 dispatch field: $name"
                    }
                }
                $global:T21R12Fixture.dispatches.Add(
                    [pscustomobject]$payload)
                $global:T21R12Fixture.dispatched = $true
                $global:LASTEXITCODE = 0
                return
            }
            if ($endpoint -like
                'repos/*/actions/workflows/operation-evidence-producer.yml/runs*') {
                $runs = [Collections.Generic.List[object]]::new()
                foreach ($seed in @($global:T21R12Fixture.seedRuns)) {
                    $runs.Add($seed)
                }
                if ($global:T21R12Fixture.dispatched -and
                    -not [string]::IsNullOrWhiteSpace(
                        [string]$global:T21R12Fixture.postDispatchRunId)) {
                    $runs.Add([pscustomobject]@{
                            id = [string]$global:T21R12Fixture.postDispatchRunId
                        })
                }
                $page = [pscustomobject]@{ workflow_runs = @($runs) }
                $global:LASTEXITCODE = 0
                return (ConvertTo-Json -InputObject @($page) -Depth 10 -Compress)
            }
            throw "Unexpected T21-R12 gh endpoint: $endpoint"
        }
        Set-Item -LiteralPath Function:\global:Start-Sleep -Force -Value {
            param([int]$Seconds)
        }
        $environment = @{
            HUSAYNIA_REPOSITORY_ROOT = $fixtureRoot
            GITHUB_REPOSITORY = $repository
            GITHUB_RUN_ID = '900'
            RUNNER_TEMP = (Join-Path $fixtureRoot 'runner')
            GITHUB_STEP_SUMMARY = (Join-Path $fixtureRoot 'summary.md')
            T21_PREDECESSOR_ID = '401'
            T21_PREDECESSOR_NAME =
                'T21 protected migration Apply authorization'
            T21_PREDECESSOR_PATH =
                '.github/workflows/migration-apply-authorization.yml'
            T21_TRUSTED_BUNDLE_SHA256 = 'b' * 64
            T21_TRUSTED_WORKFLOW_REF =
                "syedmh/Dreamer/.github/workflows/trusted-protected-operations.yml@$('1' * 40)"
            T21_STAGE_INPUTS_WORKFLOW_REF =
                "syedmh/Dreamer/.github/workflows/stage-operation-inputs.yml@$('2' * 40)"
            T21_MIGRATION_AUTHORIZATION_WORKFLOW_REF =
                "syedmh/Dreamer/.github/workflows/migration-apply-authorization.yml@$('4' * 40)"
            T21_STAGE_TARGET_METADATA_JSON = '{"schemaVersion":"1.0.0"}'
            T21_DEVELOPMENT_ENABLED = 'true'
            T21_STAGING_ENABLED = 'true'
            T21_REPOSITORY_ID = $repositoryId
            T21_PROTECTED_HEAD_SHA = $headSha
            T21_COMPLETED_RUN_FIXTURE_ROOT = $fixtureApiRoot
            T21_R12_AUTH_SCENARIO = $AuthorizationScenario
        }
        New-Item -ItemType Directory -Path $environment.RUNNER_TEMP -Force |
            Out-Null
        $prior = @{}
        foreach ($name in $environment.Keys) {
            $prior[$name] = [Environment]::GetEnvironmentVariable($name)
            [Environment]::SetEnvironmentVariable($name, [string]$environment[$name])
        }
        $failure = ''
        try {
            & (Join-Path $RepositoryRoot (
                    'eng\promotion\Invoke-T21CompletedRunCoordinator.ps1')) |
                Out-Null
        }
        catch {
            $failure = $_.Exception.Message
        }
        finally {
            foreach ($name in $environment.Keys) {
                [Environment]::SetEnvironmentVariable($name, $prior[$name])
            }
        }
        $dispatches = @($global:T21R12Fixture.dispatches)
        $script:simulatedDispatches += $dispatches.Count
        return [pscustomobject]@{
            failure = $failure
            dispatches = $dispatches
        }
    }
    finally {
        if ($null -eq $priorGh) {
            Remove-Item -LiteralPath Function:\global:gh -Force `
                -ErrorAction SilentlyContinue
        }
        else {
            Set-Item -LiteralPath Function:\global:gh -Force `
                -Value $priorGh.ScriptBlock
        }
        if ($null -eq $priorSleep) {
            Remove-Item -LiteralPath Function:\global:Start-Sleep -Force `
                -ErrorAction SilentlyContinue
        }
        else {
            Set-Item -LiteralPath Function:\global:Start-Sleep -Force `
                -Value $priorSleep.ScriptBlock
        }
        Remove-Variable -Scope Global -Name T21R12Fixture `
            -ErrorAction SilentlyContinue
    }
}

try {
    New-Item -ItemType Directory -Path $fixtureApiRoot -Force | Out-Null
    $policyPath = Join-Path $fixtureRoot 'pipelines\config\promotion-policy.json'
    New-Item -ItemType Directory -Path (Split-Path -Parent $policyPath) -Force |
        Out-Null
    $policy = Get-Content -LiteralPath (
        Join-Path $RepositoryRoot 'pipelines\config\promotion-policy.json') -Raw |
        ConvertFrom-Json -DateKind String
    @($policy.stages | Where-Object {
            [string]$_.name -cin @('Development', 'Staging')
        }) | ForEach-Object { $_.deploymentEnabled = $true }
    Write-Utf8Json -Value $policy -Path $policyPath -Depth 100
    New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'eng\common'),
        (Join-Path $fixtureRoot 'eng\promotion') -Force | Out-Null
    Copy-Item -LiteralPath (
        Join-Path $RepositoryRoot 'eng\common\Release.Common.ps1') -Destination (
        Join-Path $fixtureRoot 'eng\common\Release.Common.ps1')
    Copy-Item -LiteralPath (
        Join-Path $RepositoryRoot 'eng\promotion\Resolve-T21CompletedRun.ps1') `
        -Destination (Join-Path $fixtureRoot (
                'eng\promotion\Resolve-T21CompletedRun.ps1')) -Force

    Write-FixtureScript -Path (Join-Path $fixtureRoot (
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
if ($env:T21_R12_AUTH_SCENARIO -ceq 'resolver-failure') {
    throw 'T21-R12 resolver failure fixture.'
}
New-Item -ItemType Directory -Path (Join-Path $OutputRoot 'artifact') -Force |
    Out-Null
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
        artifactName =
            "migration-apply-authorization-Development-$('a' * 64)-$('e' * 64)"
    }
    $record = [ordered]@{
        decision = if ($env:T21_R12_AUTH_SCENARIO -ceq 'deny') {
            'DENY'
        }
        else {
            'AUTHORIZE'
        }
        mode = 'Apply'
        stage = 'Development'
        applicationSha256 = 'a' * 64
        producerBindings = [ordered]@{
            preflight = [ordered]@{ runId = '201' }
            preparedInputs = [ordered]@{ runId = '301' }
        }
    }
    [IO.File]::WriteAllText(
        (Join-Path $OutputRoot 'discovered-migration-authorization-identity.json'),
        ($identity | ConvertTo-Json -Depth 20))
    [IO.File]::WriteAllText(
        (Join-Path $OutputRoot 'artifact\migration-apply-authorization.json'),
        ($record | ConvertTo-Json -Depth 20))
}
'@
    Write-FixtureScript -Path (Join-Path $fixtureRoot (
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
$record = Get-Content -LiteralPath (
    Join-Path (Split-Path -Parent $MigrationAuthorizationVerifiedProvenancePath) `
        'artifact\migration-apply-authorization.json') -Raw | ConvertFrom-Json
if ([string]$record.decision -cne 'AUTHORIZE' -or
    [string]$record.mode -cne 'Apply') {
    throw 'Migration authorization is not canonical APPLY.'
}
throw 'T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN'
'@

    New-CompletedRunApiFixture -RunId '9001' -Inputs $expectedInputs
    New-CompletedRunApiFixture -RunId '8001' -Inputs $expectedInputs
    New-CompletedRunApiFixture -RunId '8002' -Inputs $expectedInputs
    $directResolverFailure = ''
    try {
        & (Join-Path $fixtureRoot 'eng\promotion\Resolve-T21CompletedRun.ps1') `
            -RepositoryRoot $fixtureRoot -RunId '8001' `
            -ExpectedRepository $repository -ExpectedRepositoryId $repositoryId `
            -ExpectedHeadSha $headSha -ExpectedMode StageOperations `
            -ExpectedStage Development -ExpectedSourceReleaseRunId '101' `
            -ExpectedAppSha256 $appSha -ExpectedPreflightRunId '201' `
            -ExpectedStageOperationInputsRunId '301' `
            -ExpectedMigrationAuthorizationRunId '401' `
            -ExpectedCtoAuthorizationRunId '' `
            -ExpectedCorrelationId 'r10-401-development-stageoperations' `
            -OutputRoot (Join-Path $fixtureRoot 'direct-resolver') `
            -FixturePath (Join-Path $fixtureApiRoot '8001.json') | Out-Null
    }
    catch {
        $directResolverFailure = $_.Exception.Message
    }
    Assert-R12 'canonical-api-digest-attestation-fixture-resolves' (
        [string]::IsNullOrWhiteSpace($directResolverFailure)
    ) $directResolverFailure

    $titleOnly = Invoke-CoordinatorFixtureCase -SeedRuns @(
        [pscustomobject]@{
            id = '7001'
            display_title =
                'crafted correlation=r10-401-development-stageoperations'
            status = 'completed'
            conclusion = 'success'
        }) -PostDispatchRunId '9001'
    Assert-R12 'title-only-success-never-suppresses-canonical-dispatch' (
        [string]::IsNullOrWhiteSpace($titleOnly.failure) -and
        $titleOnly.dispatches.Count -eq 1
    ) "Title-only run suppressed dispatch: $($titleOnly.failure)"

    $exactReuse = Invoke-CoordinatorFixtureCase -SeedRuns @(
        [pscustomobject]@{ id = '8001' })
    Assert-R12 'exact-attested-canonical-stageoperations-reuses-zero-dispatch' (
        [string]::IsNullOrWhiteSpace($exactReuse.failure) -and
        $exactReuse.dispatches.Count -eq 0
    ) "Canonical completed run was not reused: $($exactReuse.failure)"
    Assert-R12 'valid-reuse-requires-no-title' (
        $exactReuse.dispatches.Count -eq 0
    ) 'A valid canonical run required display_title/title authority.'

    $invalidCases = [ordered]@{}
    $caseId = 8100
    $alternateInputs = {
        param([string]$Property, [string]$Value)
        $copy = [ordered]@{}
        foreach ($name in $expectedInputs.Keys) {
            $copy[$name] = [string]$expectedInputs[$name]
        }
        $copy[$Property] = $Value
        return $copy
    }
    $wrongModeInputs = & $alternateInputs mode Preflight
    $wrongModeInputs.preflightRunId = ''
    $wrongModeInputs.stageOperationInputsRunId = ''
    $wrongModeInputs.migrationAuthorizationRunId = ''
    New-CompletedRunApiFixture -RunId ([string](++$caseId)) `
        -Inputs $wrongModeInputs
    $invalidCases.mode = [string]$caseId
    New-CompletedRunApiFixture -RunId ([string](++$caseId)) `
        -Inputs (& $alternateInputs stage Staging)
    $invalidCases.stage = [string]$caseId
    foreach ($selector in @(
            @{ Name = 'release'; Property = 'sourceReleaseRunId'; Value = '102' },
            @{ Name = 'preflight'; Property = 'preflightRunId'; Value = '202' },
            @{ Name = 'prepared'; Property = 'stageOperationInputsRunId'; Value = '302' },
            @{ Name = 'migration'; Property = 'migrationAuthorizationRunId'; Value = '402' },
            @{ Name = 'correlation'; Property = 'orchestrationCorrelationId'; Value = 'wrong' }
        )) {
        New-CompletedRunApiFixture -RunId ([string](++$caseId)) `
            -Inputs (& $alternateInputs $selector.Property $selector.Value)
        $invalidCases[$selector.Name] = [string]$caseId
    }
    New-CompletedRunApiFixture -RunId ([string](++$caseId)) `
        -Inputs $expectedInputs -ManifestMutation {
            param($manifest)
            $manifest.dispatchInputs.ctoAuthorizationRunId = '501'
        }
    $invalidCases.cto = [string]$caseId
    New-CompletedRunApiFixture -RunId ([string](++$caseId)) `
        -Inputs $expectedInputs -FixtureHeadSha ('8' * 40)
    $invalidCases.'head-sha' = [string]$caseId
    New-CompletedRunApiFixture -RunId ([string](++$caseId)) `
        -Inputs $expectedInputs -ManifestMutation {
            param($manifest)
            $manifest.workflow.path = '.github/workflows/other.yml'
        } -RunMutation {
            param($run)
            $run.path = '.github/workflows/other.yml'
        }
    $invalidCases.'workflow-path' = [string]$caseId
    New-CompletedRunApiFixture -RunId ([string](++$caseId)) `
        -Inputs $expectedInputs -ManifestMutation {
            param($manifest)
            $manifest.workflow.event = 'push'
        } -RunMutation {
            param($run)
            $run.event = 'push'
        }
    $invalidCases.event = [string]$caseId
    New-CompletedRunApiFixture -RunId ([string](++$caseId)) `
        -Inputs $expectedInputs -ManifestMutation {
            param($manifest)
            $manifest.workflow.headRef = 'refs/heads/other'
        } -RunMutation {
            param($run)
            $run.head_branch = 'other'
        }
    $invalidCases.ref = [string]$caseId
    New-CompletedRunApiFixture -RunId ([string](++$caseId)) `
        -Inputs $expectedInputs -FixtureRepositoryId '43'
    $invalidCases.'repository-id' = [string]$caseId
    New-CompletedRunApiFixture -RunId ([string](++$caseId)) `
        -Inputs $expectedInputs -RunAttempt 2 -ManifestMutation {
            param($manifest)
            $manifest.run.attempt = 1
        }
    $invalidCases.'run-attempt' = [string]$caseId

    foreach ($entry in $invalidCases.GetEnumerator()) {
        $case = Invoke-CoordinatorFixtureCase -SeedRuns @(
            [pscustomobject]@{ id = [string]$entry.Value }
        ) -PostDispatchRunId '9001'
        Assert-R12 "wrong-$($entry.Key)-is-rejected-and-canonical-dispatched" (
            [string]::IsNullOrWhiteSpace($case.failure) -and
            $case.dispatches.Count -eq 1
        ) "Wrong $($entry.Key) candidate was reused or blocked safe recovery: $($case.failure)"
    }

    $duplicates = Invoke-CoordinatorFixtureCase -SeedRuns @(
        [pscustomobject]@{ id = '8001' },
        [pscustomobject]@{ id = '8002' })
    Assert-R12 'duplicate-canonical-matches-fail-closed-zero-dispatch' (
        $duplicates.failure.Contains('duplicate canonical child runs') -and
        $duplicates.dispatches.Count -eq 0
    ) "Duplicate canonical runs did not fail closed: $($duplicates.failure)"

    New-CompletedRunApiFixture -RunId '8301' -Inputs $expectedInputs `
        -ManifestMutation {
            param($manifest)
            $manifest.dispatchInputs.PSObject.Properties.Remove(
                'migrationAuthorizationRunId')
        }
    New-CompletedRunApiFixture -RunId '8302' -Inputs $expectedInputs `
        -MissingAttestation
    foreach ($invalid in @(
            @{ Name = 'missing-api-fixture'; Id = '8399' },
            @{ Name = 'malformed-input-manifest'; Id = '8301' },
            @{ Name = 'missing-attestation-provenance'; Id = '8302' }
        )) {
        $case = Invoke-CoordinatorFixtureCase -SeedRuns @(
            [pscustomobject]@{ id = $invalid.Id }
        ) -PostDispatchRunId '9001'
        Assert-R12 "$($invalid.Name)-ignored-for-safe-canonical-dispatch" (
            [string]::IsNullOrWhiteSpace($case.failure) -and
            $case.dispatches.Count -eq 1
        ) "$($invalid.Name) was treated as reusable: $($case.failure)"
    }

    $apply = Invoke-CoordinatorFixtureCase -PostDispatchRunId '9001'
    Assert-R12 'canonical-apply-normal-path-dispatches-exactly-once' (
        [string]::IsNullOrWhiteSpace($apply.failure) -and
        $apply.dispatches.Count -eq 1 -and
        [string]$apply.dispatches[0].inputs.mode -ceq 'StageOperations'
    ) "Canonical APPLY count changed: $($apply.failure)"
    $deny = Invoke-CoordinatorFixtureCase -AuthorizationScenario deny
    Assert-R12 'deny-remains-zero-dispatch' (
        -not [string]::IsNullOrWhiteSpace($deny.failure) -and
        $deny.dispatches.Count -eq 0
    ) 'DENY produced a dispatch.'
    $resolverFailure = Invoke-CoordinatorFixtureCase `
        -AuthorizationScenario resolver-failure
    Assert-R12 'authorization-failure-remains-zero-dispatch' (
        -not [string]::IsNullOrWhiteSpace($resolverFailure.failure) -and
        $resolverFailure.dispatches.Count -eq 0
    ) 'Authorization failure produced a dispatch.'
}
finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force `
        -ErrorAction SilentlyContinue
}

Write-Output (
    "SUMMARY t21-r12-completed-run-reuse total=$($passed + $failed) " +
    "passed=$passed failed=$failed simulatedDispatches=$simulatedDispatches " +
    "actualDispatches=0 auth=0 deployments=0 database=0 resources=0 " +
    "installs=0 restores=0 mutations=0"
)
if ($failed -gt 0) { exit 1 }
