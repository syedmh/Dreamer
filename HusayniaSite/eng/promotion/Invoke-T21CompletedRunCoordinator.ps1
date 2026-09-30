[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$global:LASTEXITCODE = 0

function Get-RequiredEnvironmentValue {
    param([string]$Name)

    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "T21 coordinator environment value is missing: $Name"
    }
    return $value
}

function Assert-ExactProperties {
    param($Value, [string[]]$Expected, [string]$Label)

    if ($null -eq $Value -or @(Compare-Object ($Expected | Sort-Object) @(
            $Value.PSObject.Properties.Name | Sort-Object)).Count -ne 0) {
        throw "$Label does not exactly match the T21 coordinator contract."
    }
}

$repositoryRoot = (Resolve-Path -LiteralPath (
        Get-RequiredEnvironmentValue -Name 'HUSAYNIA_REPOSITORY_ROOT')).Path
$policyPath = Join-Path $repositoryRoot 'pipelines\config\promotion-policy.json'
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json -DateKind String
$predecessorId = Get-RequiredEnvironmentValue -Name 'T21_PREDECESSOR_ID'
$predecessorName = Get-RequiredEnvironmentValue -Name 'T21_PREDECESSOR_NAME'
$predecessorPath = Get-RequiredEnvironmentValue -Name 'T21_PREDECESSOR_PATH'
$bundleSha256 = Get-RequiredEnvironmentValue -Name 'T21_TRUSTED_BUNDLE_SHA256'
$repositoryId = Get-RequiredEnvironmentValue -Name 'T21_REPOSITORY_ID'
$protectedHeadSha = Get-RequiredEnvironmentValue -Name 'T21_PROTECTED_HEAD_SHA'
$expectedPaths = @{
    'T21 immutable release C6' = '.github/workflows/release-build-and-nonproduction.yml'
    'T21 protected migration Apply authorization' = '.github/workflows/migration-apply-authorization.yml'
    'T21 disabled nonproduction operation wrapper' = '.github/workflows/operation-evidence-producer.yml'
}
if (-not $expectedPaths.ContainsKey($predecessorName) -or
    $predecessorPath -cne $expectedPaths[$predecessorName]) {
    throw 'T21 coordinator predecessor name/path is not in the exact allowlist.'
}
if ($repositoryId -notmatch '^[1-9][0-9]*$' -or
    $protectedHeadSha -notmatch '^[a-f0-9]{40}$' -or
    $protectedHeadSha -ceq ('0' * 40) -or
    [string]$policy.t21ProvenanceContract.repository -cne $env:GITHUB_REPOSITORY) {
    throw 'T21 coordinator protected repository ID, head SHA, or repository contract is malformed.'
}

function Test-StageEnabled {
    param([ValidateSet('Development', 'Staging')][string]$Stage)

    $entry = @($policy.stages | Where-Object { [string]$_.name -ceq $Stage })
    if ($entry.Count -ne 1) {
        throw 'T21 coordinator stage policy is not unique.'
    }
    $variable = if ($Stage -ceq 'Development') {
        [Environment]::GetEnvironmentVariable('T21_DEVELOPMENT_ENABLED')
    }
    else {
        [Environment]::GetEnvironmentVariable('T21_STAGING_ENABLED')
    }
    return [bool]$entry[0].deploymentEnabled -and $variable -ceq 'true'
}

function Get-CompletedProducerRuns {
    $endpoint =
        'repos/{0}/actions/workflows/operation-evidence-producer.yml/runs?event=workflow_dispatch&branch=main&status=completed&per_page=100' -f
        $env:GITHUB_REPOSITORY
    $json = @(& gh api --paginate --slurp $endpoint)
    if ($LASTEXITCODE -ne 0 -or $json.Count -eq 0) {
        throw 'T21 coordinator paginated run discovery failed.'
    }
    $pages = ($json -join [Environment]::NewLine) | ConvertFrom-Json
    return @($pages | ForEach-Object { @($_.workflow_runs) })
}

function Resolve-CompletedProducerRun {
    param(
        [Parameter(Mandatory = $true)][string]$RunId,
        [Collections.IDictionary]$ExpectedInputs
    )

    if ($RunId -notmatch '^[1-9][0-9]*$') {
        return $null
    }
    $outputRoot = Join-Path $env:RUNNER_TEMP (
        "completed-run-$RunId-$([guid]::NewGuid().ToString('N'))")
    $arguments = @{
        RepositoryRoot = $repositoryRoot
        RunId = $RunId
        ExpectedRepository = $env:GITHUB_REPOSITORY
        ExpectedRepositoryId = $repositoryId
        ExpectedHeadSha = $protectedHeadSha
        OutputRoot = $outputRoot
    }
    $fixtureRoot = [Environment]::GetEnvironmentVariable(
        'T21_COMPLETED_RUN_FIXTURE_ROOT')
    if (-not [string]::IsNullOrWhiteSpace($fixtureRoot)) {
        $arguments.FixturePath = Join-Path $fixtureRoot "$RunId.json"
    }
    if ($null -ne $ExpectedInputs) {
        $arguments.ExpectedMode = [string]$ExpectedInputs.mode
        $arguments.ExpectedStage = [string]$ExpectedInputs.stage
        $arguments.ExpectedSourceReleaseRunId =
            [string]$ExpectedInputs.sourceReleaseRunId
        $arguments.ExpectedAppSha256 = [string]$ExpectedInputs.expectedAppSha256
        $arguments.ExpectedPreflightRunId = [string]$ExpectedInputs.preflightRunId
        $arguments.ExpectedStageOperationInputsRunId =
            [string]$ExpectedInputs.stageOperationInputsRunId
        $arguments.ExpectedMigrationAuthorizationRunId =
            [string]$ExpectedInputs.migrationAuthorizationRunId
        $arguments.ExpectedCtoAuthorizationRunId =
            [string]$ExpectedInputs.ctoAuthorizationRunId
        $arguments.ExpectedCorrelationId =
            [string]$ExpectedInputs.orchestrationCorrelationId
    }
    try {
        & (Join-Path $repositoryRoot 'eng\promotion\Resolve-T21CompletedRun.ps1') `
            @arguments | Out-Null
        if (-not $?) {
            return $null
        }
        $verifiedPath = Join-Path $outputRoot 'verified-completed-run.json'
        if (-not (Test-Path -LiteralPath $verifiedPath -PathType Leaf)) {
            return $null
        }
        return Get-Content -LiteralPath $verifiedPath -Raw |
            ConvertFrom-Json -DateKind String
    }
    catch {
        return $null
    }
    finally {
        Remove-Item -LiteralPath $outputRoot -Recurse -Force `
            -ErrorAction SilentlyContinue
    }
}

function Get-CanonicalCompletedRuns {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.IDictionary]$ExpectedInputs
    )

    $verifiedRuns = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($candidate in @(Get-CompletedProducerRuns)) {
        $runId = [string]$candidate.id
        if ($runId -notmatch '^[1-9][0-9]*$' -or -not $seen.Add($runId)) {
            continue
        }
        $verified = Resolve-CompletedProducerRun -RunId $runId `
            -ExpectedInputs $ExpectedInputs
        if ($null -ne $verified) {
            $verifiedRuns.Add($verified)
        }
    }
    return $verifiedRuns.ToArray()
}

function Start-CorrelatedRun {
    param(
        [ValidateSet('Development', 'Staging')][string]$Stage,
        [ValidateSet('Preflight', 'PreparedInputs', 'StageOperations')][string]$Mode,
        [string]$ReleaseRunId,
        [string]$AppSha,
        [string]$PreflightRunId,
        [string]$PreparedRunId,
        [string]$MigrationRunId,
        [string]$Key
    )

    $expectedInputs = [ordered]@{
        mode = $Mode
        stage = $Stage
        sourceReleaseRunId = $ReleaseRunId
        expectedAppSha256 = $AppSha
        preflightRunId = $PreflightRunId
        stageOperationInputsRunId = $PreparedRunId
        migrationAuthorizationRunId = $MigrationRunId
        ctoAuthorizationRunId = ''
        orchestrationCorrelationId = $Key
    }
    $matches = @(Get-CanonicalCompletedRuns -ExpectedInputs $expectedInputs)
    if ($matches.Count -gt 1) {
        throw "T21 coordinator duplicate canonical child runs for $Key."
    }
    if ($matches.Count -eq 0) {
        & gh api -X POST (
            'repos/{0}/actions/workflows/operation-evidence-producer.yml/dispatches' -f
            $env:GITHUB_REPOSITORY
        ) -f ref=main -f "inputs[mode]=$Mode" -f "inputs[stage]=$Stage" `
            -f "inputs[sourceReleaseRunId]=$ReleaseRunId" `
            -f "inputs[expectedAppSha256]=$AppSha" `
            -f "inputs[preflightRunId]=$PreflightRunId" `
            -f "inputs[stageOperationInputsRunId]=$PreparedRunId" `
            -f "inputs[migrationAuthorizationRunId]=$MigrationRunId" `
            -f 'inputs[ctoAuthorizationRunId]=' `
            -f "inputs[orchestrationCorrelationId]=$Key" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "T21 coordinator dispatch failed for $Key."
        }
        for ($attempt = 0; $attempt -lt 60 -and $matches.Count -eq 0; $attempt++) {
            Start-Sleep -Seconds 10
            $matches = @(Get-CanonicalCompletedRuns -ExpectedInputs $expectedInputs)
            if ($matches.Count -gt 1) {
                throw "T21 coordinator duplicate canonical child runs for $Key."
            }
        }
    }
    if ($matches.Count -ne 1) {
        throw "T21 coordinator could not identify exactly one canonical completed child for $Key."
    }
    return [pscustomobject]@{
        id = [string]$matches[0].run.runId
        verified = $matches[0]
    }
}

function Resolve-ValidatedMigrationAuthorization {
    $resolver = Join-Path $repositoryRoot 'eng\promotion\Resolve-GitHubArtifactProvenance.ps1'
    $migrationRoot = Join-Path $env:RUNNER_TEMP 'migration-authorization'
    $migrationTopLevelRef =
        'syedmh/Dreamer/.github/workflows/migration-apply-authorization.yml@refs/heads/main'
    $migrationProducerRef =
        Get-RequiredEnvironmentValue -Name 'T21_MIGRATION_AUTHORIZATION_WORKFLOW_REF'

    & $resolver -ExpectedRole migration-authorization -RunId $predecessorId `
        -ConsumerRunId $env:GITHUB_RUN_ID -BundleSha256 $bundleSha256 `
        -ExpectedTopLevelCallerWorkflowRef $migrationTopLevelRef `
        -ExpectedProducerWorkflowRef $migrationProducerRef `
        -DiscoverMigrationAuthorizationIdentity -OutputRoot $migrationRoot `
        -PolicyPath $policyPath
    if (-not $?) {
        throw 'T21 coordinator migration authorization provenance discovery failed.'
    }

    $identityPath = Join-Path $migrationRoot 'discovered-migration-authorization-identity.json'
    $recordPath = Join-Path $migrationRoot 'artifact\migration-apply-authorization.json'
    if (-not (Test-Path -LiteralPath $identityPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $recordPath -PathType Leaf)) {
        throw 'T21 coordinator resolved migration authorization is incomplete.'
    }
    $identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json -DateKind String
    $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json -DateKind String
    Assert-ExactProperties -Value $identity -Expected @(
        'applicationSha256', 'artifactName', 'preflightEvidenceSha256',
        'releaseCommitSha', 'releaseManifestSha256', 'releaseRunAttempt',
        'releaseRunId', 'stage'
    ) -Label 'Resolved migration authorization identity'

    $stage = [string]$identity.stage
    $appSha = [string]$identity.applicationSha256
    $releaseRunId = [string]$identity.releaseRunId
    $preflightRunId = [string]$record.producerBindings.preflight.runId
    $preparedRunId = [string]$record.producerBindings.preparedInputs.runId
    if ($stage -cnotin @('Development', 'Staging') -or
        $appSha -notmatch '^[a-f0-9]{64}$' -or
        [string]$identity.preflightEvidenceSha256 -notmatch '^[a-f0-9]{64}$' -or
        [string]$identity.releaseManifestSha256 -notmatch '^[a-f0-9]{64}$' -or
        [string]$identity.releaseCommitSha -notmatch '^[a-f0-9]{40}$' -or
        $releaseRunId -notmatch '^[1-9][0-9]*$' -or
        $preflightRunId -notmatch '^[1-9][0-9]*$' -or
        $preparedRunId -notmatch '^[1-9][0-9]*$' -or
        $preflightRunId -ceq $preparedRunId) {
        throw 'T21 coordinator resolved migration authorization selectors are malformed.'
    }

    $caller = @($policy.t21ProvenanceContract.callerMatrix | Where-Object {
            [string]$_.stage -ceq $stage
        })
    if ($caller.Count -ne 1) {
        throw 'T21 coordinator stage caller binding is not unique.'
    }
    $authorizedCallerRef = [string]$caller[0].workflowRef
    $trustedProducerRef = Get-RequiredEnvironmentValue -Name 'T21_TRUSTED_WORKFLOW_REF'
    $preparedProducerRef =
        Get-RequiredEnvironmentValue -Name 'T21_STAGE_INPUTS_WORKFLOW_REF'

    $stageTargetJson = Get-RequiredEnvironmentValue -Name 'T21_STAGE_TARGET_METADATA_JSON'
    $stageTargetPath = Join-Path $env:RUNNER_TEMP 'stage-target.json'
    [IO.File]::WriteAllText(
        $stageTargetPath,
        $stageTargetJson,
        [Text.UTF8Encoding]::new($false))

    $releaseRoot = Join-Path $env:RUNNER_TEMP 'release'
    & $resolver -ExpectedRole release-c6 -RunId $releaseRunId `
        -ConsumerRunId $env:GITHUB_RUN_ID -Stage $stage -ApplicationSha256 $appSha `
        -BundleSha256 $bundleSha256 -OutputRoot $releaseRoot -PolicyPath $policyPath
    if (-not $?) {
        throw 'T21 coordinator release provenance resolution failed.'
    }

    $preflightRoot = Join-Path $env:RUNNER_TEMP 'preflight'
    & $resolver -ExpectedRole trusted-preflight -RunId $preflightRunId `
        -ConsumerRunId $env:GITHUB_RUN_ID -Stage $stage -ApplicationSha256 $appSha `
        -BundleSha256 $bundleSha256 `
        -ReleaseManifestSha256 ([string]$identity.releaseManifestSha256) `
        -ReleaseCommitSha ([string]$identity.releaseCommitSha) `
        -ExpectedTopLevelCallerWorkflowRef $authorizedCallerRef `
        -ExpectedProducerWorkflowRef $trustedProducerRef -OutputRoot $preflightRoot `
        -PolicyPath $policyPath
    if (-not $?) {
        throw 'T21 coordinator preflight provenance resolution failed.'
    }

    $preparedRoot = Join-Path $env:RUNNER_TEMP 'prepared-inputs'
    & $resolver -ExpectedRole stage-operation-inputs -RunId $preparedRunId `
        -ConsumerRunId $env:GITHUB_RUN_ID -Stage $stage -ApplicationSha256 $appSha `
        -BundleSha256 $bundleSha256 `
        -ReleaseManifestSha256 ([string]$identity.releaseManifestSha256) `
        -ReleaseCommitSha ([string]$identity.releaseCommitSha) `
        -ExpectedTopLevelCallerWorkflowRef $authorizedCallerRef `
        -ExpectedProducerWorkflowRef $preparedProducerRef -OutputRoot $preparedRoot `
        -PolicyPath $policyPath
    if (-not $?) {
        throw 'T21 coordinator prepared-input provenance resolution failed.'
    }

    $reachedImmutableFence = $false
    try {
        & (Join-Path $repositoryRoot 'eng\promotion\Test-TrustedProtectedOperationInputs.ps1') `
            -Operation StageOperations -Stage $stage -ExpectedAppSha256 $appSha `
            -ExpectedBundleSha256 $bundleSha256 `
            -ReleaseVerifiedProvenancePath (Join-Path $releaseRoot 'verified-provenance.json') `
            -PreflightVerifiedProvenancePath (Join-Path $preflightRoot 'verified-provenance.json') `
            -StageOperationInputVerifiedProvenancePath (
                Join-Path $preparedRoot 'verified-provenance.json') `
            -MigrationAuthorizationVerifiedProvenancePath (
                Join-Path $migrationRoot 'verified-provenance.json') `
            -StageTargetMetadataPath $stageTargetPath -PolicyPath $policyPath
    }
    catch {
        if ($_.Exception.Message -cne 'T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN') {
            throw
        }
        $reachedImmutableFence = $true
    }
    if (-not $reachedImmutableFence) {
        throw 'T21 coordinator authorization validator returned before its immutable deployment fence.'
    }

    return [pscustomobject]@{
        stage = $stage
        applicationSha256 = $appSha
        releaseRunId = $releaseRunId
        preflightRunId = $preflightRunId
        preparedRunId = $preparedRunId
        migrationRunId = $predecessorId
    }
}

if ($predecessorName -ceq 'T21 immutable release C6') {
    if (-not (Test-StageEnabled -Stage Development)) {
        'T21 coordinator Development disabled by protected variable/policy; dispatches=0' |
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
        return
    }
    $discovery = Join-Path $env:RUNNER_TEMP 'release-discovery'
    & (Join-Path $repositoryRoot 'eng\promotion\Resolve-GitHubArtifactProvenance.ps1') `
        -ExpectedRole release-c6 -RunId $predecessorId -ConsumerRunId $env:GITHUB_RUN_ID `
        -Stage Development -BundleSha256 $bundleSha256 -DiscoverReleaseIdentity `
        -OutputRoot $discovery -PolicyPath $policyPath
    $identity = Get-Content -LiteralPath (
        Join-Path $discovery 'discovered-release-identity.json') -Raw |
        ConvertFrom-Json
    $preflightKey = "r10-$predecessorId-development-preflight"
    $preparedKey = "r10-$predecessorId-development-preparedinputs"
    $preflight = Start-CorrelatedRun -Stage Development -Mode Preflight `
        -ReleaseRunId $identity.releaseRunId -AppSha $identity.applicationSha256 `
        -PreflightRunId '' -PreparedRunId '' -MigrationRunId '' -Key $preflightKey
    $prepared = Start-CorrelatedRun -Stage Development -Mode PreparedInputs `
        -ReleaseRunId $identity.releaseRunId -AppSha $identity.applicationSha256 `
        -PreflightRunId '' -PreparedRunId '' -MigrationRunId '' -Key $preparedKey
    "predecessor=$predecessorId correlation=$preflightKey preflightRunId=$($preflight.id) " +
        "correlation2=$preparedKey preparedInputsRunId=$($prepared.id) conclusions=success,success" |
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
    return
}

if ($predecessorName -ceq 'T21 protected migration Apply authorization') {
    $validated = Resolve-ValidatedMigrationAuthorization
    if (-not (Test-StageEnabled -Stage $validated.stage)) {
        "T21 coordinator $($validated.stage) disabled by protected variable/policy; dispatches=0" |
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
        return
    }
    $key =
        "r10-$predecessorId-$($validated.stage.ToLowerInvariant())-stageoperations"
    $child = Start-CorrelatedRun -Stage $validated.stage -Mode StageOperations `
        -ReleaseRunId $validated.releaseRunId -AppSha $validated.applicationSha256 `
        -PreflightRunId $validated.preflightRunId -PreparedRunId $validated.preparedRunId `
        -MigrationRunId $validated.migrationRunId -Key $key
    "predecessor=$predecessorId authorization=validated correlation=$key " +
        "stageOperationsRunId=$($child.id) conclusion=success" |
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
    return
}

if ($predecessorName -ceq 'T21 disabled nonproduction operation wrapper') {
    $verifiedPredecessor = Resolve-CompletedProducerRun -RunId $predecessorId
    if ($null -eq $verifiedPredecessor) {
        throw 'T21 coordinator producer predecessor lacks canonical API/digest/attestation provenance.'
    }
    $predecessorInputs = $verifiedPredecessor.dispatchInputs
    if ([string]$predecessorInputs.mode -ceq 'StageOperations' -and
        [string]$predecessorInputs.stage -ceq 'Development') {
        if (-not (Test-StageEnabled -Stage Staging)) {
            'T21 coordinator Staging disabled by protected variable/policy; dispatches=0' |
                Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
            return
        }
        throw 'T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN'
    }
}
'T21 coordinator ignored non-successor producer completion; dispatches=0' |
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
