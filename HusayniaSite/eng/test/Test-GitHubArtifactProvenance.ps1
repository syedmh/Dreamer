[CmdletBinding()]
param([string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Resolve-HusayniaRepositoryRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$temp = Join-Path ([IO.Path]::GetTempPath()) "t21-provenance-$([guid]::NewGuid().ToString('N'))"
$passed = 0
$failed = 0
$appSha = 'a' * 64
$bundleSha = 'b' * 64
$releaseManifestSha = 'c' * 64
$releaseCommitSha = 'd' * 40
$preflightEvidenceSha = 'e' * 64

function Assert-ProvenanceTest {
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

function Get-ScriptParameterNames {
    param([string]$Path)

    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) {
        throw "PowerShell parser rejected $Path`: $($errors.Message -join ' | ')"
    }
    return @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
}

function Get-RoleIdentity {
    param(
        [ValidateSet('trusted-preflight', 'stage-operation-inputs', 'cto-authorization', 'migration-authorization')]
        [string]$Role,
        [ValidateSet('Development', 'Staging', 'Production')]
        [string]$Stage
    )

    $repository = 'syedmh/Dreamer'
    switch ($Role) {
        'trusted-preflight' {
            $topLevelPath = if ($Stage -eq 'Production') {
                '.github/workflows/production-operation-evidence.yml'
            }
            else {
                '.github/workflows/operation-evidence-producer.yml'
            }
            $producerPath = '.github/workflows/trusted-protected-operations.yml'
            $producerSha = '1' * 40
        }
        'stage-operation-inputs' {
            $topLevelPath = if ($Stage -eq 'Production') {
                '.github/workflows/production-operation-evidence.yml'
            }
            else {
                '.github/workflows/operation-evidence-producer.yml'
            }
            $producerPath = '.github/workflows/stage-operation-inputs.yml'
            $producerSha = '2' * 40
        }
        'cto-authorization' {
            if ($Stage -ne 'Production') {
                throw 'CTO authorization fixtures are Production-only.'
            }
            $topLevelPath = '.github/workflows/cto-authorization-record.yml'
            $producerPath = $topLevelPath
            $producerSha = '3' * 40
        }
        'migration-authorization' {
            $topLevelPath = '.github/workflows/migration-apply-authorization.yml'
            $producerPath = $topLevelPath
            $producerSha = '4' * 40
        }
    }
    return [pscustomobject]@{
        topLevelPath = $topLevelPath
        topLevelRef = "$repository/$topLevelPath@refs/heads/main"
        producerPath = $producerPath
        producerRef = "$repository/$producerPath@$producerSha"
        producerSha = $producerSha
    }
}

function Get-ArtifactName {
    param(
        [ValidateSet('trusted-preflight', 'stage-operation-inputs', 'cto-authorization', 'migration-authorization')]
        [string]$Role,
        [ValidateSet('Development', 'Staging', 'Production')]
        [string]$Stage
    )

    switch ($Role) {
        'trusted-preflight' { return "raw-$($Stage.ToLowerInvariant())-preflight-$appSha" }
        'stage-operation-inputs' { return "stage-operation-inputs-$($Stage.ToLowerInvariant())-$appSha" }
        'cto-authorization' { return "cto-authorization-$appSha" }
        'migration-authorization' {
            return "migration-apply-authorization-$Stage-$appSha-$preflightEvidenceSha"
        }
    }
}

function Update-FixtureArchive {
    param([string]$FixturePath, [scriptblock]$Change)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $fixture = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json -DateKind String
    $archivePath = [string]$fixture.artifacts[0].archivePath
    $rewriteRoot = Join-Path (Split-Path -Parent $FixturePath) "rewrite-$([guid]::NewGuid().ToString('N'))"
    Expand-Archive -LiteralPath $archivePath -DestinationPath $rewriteRoot
    $manifestPath = Join-Path $rewriteRoot 't21-producer-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -DateKind String
    & $Change $manifest
    Write-Utf8Json -Value $manifest -Path $manifestPath -Depth 20
    Remove-Item -LiteralPath $archivePath -Force
    [IO.Compression.ZipFile]::CreateFromDirectory($rewriteRoot, $archivePath)
    Remove-Item -LiteralPath $rewriteRoot -Recurse -Force
    $fixture.artifacts[0].digest = "sha256:$(Get-Sha256Lower -Path $archivePath)"
    Write-Utf8Json -Value $fixture -Path $FixturePath -Depth 20
}

function Update-FixturePayload {
    param([string]$FixturePath, [scriptblock]$Change)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $fixture = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json -DateKind String
    $archivePath = [string]$fixture.artifacts[0].archivePath
    $rewriteRoot = Join-Path (Split-Path -Parent $FixturePath) "rewrite-payload-$([guid]::NewGuid().ToString('N'))"
    Expand-Archive -LiteralPath $archivePath -DestinationPath $rewriteRoot
    & $Change $rewriteRoot
    Remove-Item -LiteralPath $archivePath -Force
    [IO.Compression.ZipFile]::CreateFromDirectory($rewriteRoot, $archivePath)
    Remove-Item -LiteralPath $rewriteRoot -Recurse -Force
    $fixture.artifacts[0].digest = "sha256:$(Get-Sha256Lower -Path $archivePath)"
    Write-Utf8Json -Value $fixture -Path $FixturePath -Depth 20
}

function Invoke-WorkflowManifestRun {
    param(
        [scriptblock]$RunBody,
        [hashtable]$Environment
    )

    $prior = @{}
    foreach ($name in $Environment.Keys) {
        $prior[$name] = [Environment]::GetEnvironmentVariable(
            $name,
            [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable(
            $name,
            [string]$Environment[$name],
            [EnvironmentVariableTarget]::Process)
    }
    $output = [Collections.Generic.List[string]]::new()
    $failure = ''
    $priorLastExitCodeVariable = Get-Variable -Scope Global -Name LASTEXITCODE -ErrorAction SilentlyContinue
    $hadPriorLastExitCode = $null -ne $priorLastExitCodeVariable
    $priorLastExitCode = if ($hadPriorLastExitCode) { $priorLastExitCodeVariable.Value } else { $null }
    try {
        # GitHub's pwsh host exposes this automatic variable.  Seed the local host identically so
        # the exact checked-in run body, rather than a substituted copy, can evaluate its guard.
        $global:LASTEXITCODE = 0
        try {
            & $RunBody 2>&1 | ForEach-Object {
                [void]$output.Add($_.ToString())
            }
        }
        catch {
            $failure = $_.Exception.Message
            [void]$output.Add($failure)
        }
    }
    finally {
        if ($hadPriorLastExitCode) {
            $global:LASTEXITCODE = $priorLastExitCode
        }
        else {
            Remove-Variable -Scope Global -Name LASTEXITCODE -ErrorAction SilentlyContinue
        }
        foreach ($name in $Environment.Keys) {
            [Environment]::SetEnvironmentVariable(
                $name,
                $prior[$name],
                [EnvironmentVariableTarget]::Process)
        }
    }
    return [pscustomobject]@{
        output = @($output)
        failure = $failure
    }
}

function New-WorkflowEmittedStageInputFixture {
    param(
        [string]$Root,
        [ValidateSet('Development', 'Staging', 'Production')]
        [string]$Stage = 'Development'
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Path $Root -Force | Out-Null
    $workflow = Get-Content -LiteralPath (
        Join-Path $RepositoryRoot 'pipelines\github\stage-operation-inputs.yml') -Raw |
        ConvertFrom-Json -DateKind String
    $manifestSteps = @($workflow.jobs.prepare.steps | Where-Object {
            [string]$_.name -ceq 'Create canonical stage-input producer manifest'
        })
    if ($manifestSteps.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$manifestSteps[0].run)) {
        throw 'The checked-in stage-input workflow does not have exactly one canonical manifest run body.'
    }
    $runBody = [scriptblock]::Create([string]$manifestSteps[0].run)
    $identity = Get-RoleIdentity -Role stage-operation-inputs -Stage $Stage
    $runnerTemp = Join-Path $Root 'runner-temp'
    $releaseRoot = Join-Path $Root 'release-root'
    $releaseDirectory = Join-Path $releaseRoot 'release'
    $releaseResolverRoot = Join-Path $runnerTemp 'release'
    $payload = Join-Path $runnerTemp 'stage-operation-inputs'
    New-Item -ItemType Directory -Path $releaseDirectory, $releaseResolverRoot, $payload -Force | Out-Null
    Write-Utf8Json -Value ([ordered]@{
        schemaVersion = '1.0.0'
        version = 'workflow-emitted-stage-input-fixture'
        commitSha = $releaseCommitSha
        files = @()
    }) -Path (Join-Path $releaseDirectory 'release-manifest.json') -Depth 10
    Write-Utf8Json -Value ([ordered]@{
        schemaVersion = '2.0.0'
        run = [ordered]@{
            commitSha = $releaseCommitSha
            runId = '401'
            runAttempt = 1
        }
    }) -Path (Join-Path $releaseResolverRoot 'verified-provenance.json') -Depth 10
    Write-Utf8Json -Value ([ordered]@{
        schemaVersion = '2.0.0'
        stage = $Stage
        source = 'workflow-emitted-fixture'
    }) -Path (Join-Path $payload 'stage-operation-inputs.json') -Depth 10
    [IO.File]::WriteAllText(
        (Join-Path $payload 'SHA256SUMS'),
        "$(Get-Sha256Lower -Path (Join-Path $payload 'stage-operation-inputs.json'))  stage-operation-inputs.json`n",
        [Text.UTF8Encoding]::new($false))
    $githubOutput = Join-Path $runnerTemp 'github-output.txt'
    $execution = Invoke-WorkflowManifestRun -RunBody $runBody -Environment @{
        GITHUB_WORKSPACE = $RepositoryRoot
        RUNNER_TEMP = $runnerTemp
        GITHUB_OUTPUT = $githubOutput
        T21_RELEASE_ROOT = $releaseRoot
        T21_STAGE = $Stage
        T21_EXPECTED_APP_SHA256 = $appSha
        T21_TRUSTED_BUNDLE_SHA256 = $bundleSha
        T21_STAGE_INPUTS_WORKFLOW_REF = $identity.producerRef
        T21_CALLER_WORKFLOW_REF = $identity.topLevelRef
        GITHUB_RUN_ID = '501'
        GITHUB_RUN_ATTEMPT = '1'
        GITHUB_SHA = $releaseCommitSha
    }
    $manifestPath = Join-Path $payload 't21-producer-manifest.json'
    if (-not [string]::IsNullOrWhiteSpace([string]$execution.failure) -or
        -not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "The exact checked-in stage-input manifest run body failed: $($execution.output -join ' | ')"
    }

    $archive = Join-Path $Root 'artifact.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $archive)
    $now = [DateTimeOffset]::UtcNow
    $fixture = [ordered]@{
        run = [ordered]@{
            repository = [ordered]@{ full_name = 'syedmh/Dreamer' }
            id = '501'
            path = $identity.topLevelPath
            run_attempt = 1
            head_branch = 'main'
            head_sha = $releaseCommitSha
            status = 'in_progress'
            conclusion = ''
            created_at = $now.AddMinutes(-2).ToString('O')
            updated_at = $now.AddMinutes(-1).ToString('O')
        }
        artifacts = @([ordered]@{
            id = '601'
            name = Get-ArtifactName -Role stage-operation-inputs -Stage $Stage
            digest = "sha256:$(Get-Sha256Lower -Path $archive)"
            size_in_bytes = (Get-Item -LiteralPath $archive).Length
            created_at = $now.AddMinutes(-1).ToString('O')
            expires_at = $now.AddDays(1).ToString('O')
            expired = $false
            workflow_run = [ordered]@{ id = '501' }
            archivePath = $archive
        })
        attestation = [ordered]@{
            status = 'verified'
            predicateType = 'https://slsa.dev/provenance/v1'
            signerWorkflow = $identity.producerRef.Split('@')[0]
            signerDigest = $identity.producerSha
            sourceRef = 'refs/heads/main'
            sourceCommitSha = $releaseCommitSha
        }
    }
    $fixturePath = Join-Path $Root 'fixture.json'
    Write-Utf8Json -Value $fixture -Path $fixturePath -Depth 20
    return [pscustomobject]@{
        fixturePath = $fixturePath
        workflowOutput = @($execution.output)
        githubOutput = if (Test-Path -LiteralPath $githubOutput -PathType Leaf) {
            Get-Content -LiteralPath $githubOutput -Raw
        }
        else {
            ''
        }
    }
}

function New-Fixture {
    param(
        [string]$Root,
        [ValidateSet('trusted-preflight', 'stage-operation-inputs', 'cto-authorization', 'migration-authorization')]
        [string]$Role = 'trusted-preflight',
        [ValidateSet('Development', 'Staging', 'Production')]
        [string]$Stage = 'Development'
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $identity = Get-RoleIdentity -Role $Role -Stage $Stage
    $payload = Join-Path $Root 'payload'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $payload 'producer-content.json'),
        ('{"role":"' + $Role + '"}' + "`n"),
        [Text.UTF8Encoding]::new($false))

    $writer = Join-Path $RepositoryRoot 'eng\promotion\New-T21ProducerManifest.ps1'
    $null = & $writer -ArtifactRoot $payload -ProducerRole $Role -Stage $Stage `
        -ApplicationSha256 $appSha -BundleSha256 $bundleSha -ReleaseManifestSha256 $releaseManifestSha `
        -ReleaseCommitSha $releaseCommitSha -ReleaseRunId 401 -ReleaseRunAttempt 1 `
        -ProducerRunId 501 -ProducerRunAttempt 1 -ProducerCommitSha $releaseCommitSha `
        -TopLevelCallerWorkflowRef $identity.topLevelRef -ProducerWorkflowRef $identity.producerRef
    if (-not $?) {
        throw 'Could not create v2 provenance fixture producer manifest.'
    }

    $archive = Join-Path $Root 'artifact.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $archive)
    $now = [DateTimeOffset]::UtcNow
    $fixture = [ordered]@{
        run = [ordered]@{
            repository = [ordered]@{ full_name = 'syedmh/Dreamer' }
            id = '501'
            path = $identity.topLevelPath
            run_attempt = 1
            head_branch = 'main'
            head_sha = $releaseCommitSha
            status = 'completed'
            conclusion = 'success'
            created_at = $now.AddMinutes(-2).ToString('O')
            updated_at = $now.AddMinutes(-1).ToString('O')
        }
        consumerRun = [ordered]@{
            repository = [ordered]@{ full_name = 'syedmh/Dreamer' }
            id = '901'
            path = '.github/workflows/consumer.yml'
            run_attempt = 1
            head_branch = 'main'
            head_sha = '9' * 40
            status = 'in_progress'
            conclusion = ''
            created_at = $now.ToString('O')
            updated_at = $now.ToString('O')
        }
        artifacts = @([ordered]@{
            id = '601'
            name = Get-ArtifactName -Role $Role -Stage $Stage
            digest = "sha256:$(Get-Sha256Lower -Path $archive)"
            size_in_bytes = (Get-Item -LiteralPath $archive).Length
            created_at = $now.AddMinutes(-1).ToString('O')
            expires_at = $now.AddDays(1).ToString('O')
            expired = $false
            workflow_run = [ordered]@{ id = '501' }
            archivePath = $archive
        })
        attestation = [ordered]@{
            status = 'verified'
            predicateType = 'https://slsa.dev/provenance/v1'
            signerWorkflow = $identity.producerRef.Split('@')[0]
            signerDigest = $identity.producerSha
            sourceRef = 'refs/heads/main'
            sourceCommitSha = $releaseCommitSha
        }
    }
    $fixturePath = Join-Path $Root 'fixture.json'
    Write-Utf8Json -Value $fixture -Path $fixturePath -Depth 20
    return $fixturePath
}

function Get-FixtureReleaseManifestSha256 {
    param([string]$FixturePath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $fixture = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json -DateKind String
    $archivePath = [string]$fixture.artifacts[0].archivePath
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $entry = $archive.GetEntry('t21-producer-manifest.json')
        if ($null -eq $entry) {
            throw 'Fixture producer archive has no canonical manifest.'
        }
        $stream = $entry.Open()
        try {
            $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false), $true)
            try {
                $manifest = $reader.ReadToEnd() | ConvertFrom-Json -DateKind String
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
    $releaseManifestSha256 = [string]$manifest.release.manifestSha256
    if ($releaseManifestSha256 -notmatch '^[a-f0-9]{64}$') {
        throw 'Fixture producer manifest has no exact release manifest SHA-256 binding.'
    }
    return $releaseManifestSha256
}

function Invoke-Fixture {
    param(
        [string]$FixturePath,
        [string]$Output,
        [ValidateSet('trusted-preflight', 'stage-operation-inputs', 'cto-authorization', 'migration-authorization')]
        [string]$Role = 'trusted-preflight',
        [ValidateSet('Development', 'Staging', 'Production')]
        [string]$Stage = 'Development',
        [string]$App = $appSha
    )

    $identity = Get-RoleIdentity -Role $Role -Stage $Stage
    $fixtureReleaseManifestSha256 = Get-FixtureReleaseManifestSha256 -FixturePath $FixturePath
    try {
        & (Join-Path $RepositoryRoot 'eng\promotion\Resolve-GitHubArtifactProvenance.ps1') `
            -ExpectedRole $Role -RunId 501 -ConsumerRunId 901 -Stage $Stage -ApplicationSha256 $App -BundleSha256 $bundleSha `
            -ReleaseManifestSha256 $fixtureReleaseManifestSha256 -ReleaseCommitSha $releaseCommitSha `
            -PreflightEvidenceSha256 $preflightEvidenceSha -OutputRoot $Output `
            -ExpectedTopLevelCallerWorkflowRef $identity.topLevelRef `
            -ExpectedProducerWorkflowRef $identity.producerRef -FixturePath $FixturePath
        return $true
    }
    catch {
        Write-Output "EXPECTED-REJECTION $($_.Exception.Message)"
        return $false
    }
}

try {
    New-Item -ItemType Directory -Path $temp -Force | Out-Null

    $writerParameters = Get-ScriptParameterNames -Path (
        Join-Path $RepositoryRoot 'eng\promotion\New-T21ProducerManifest.ps1')
    $resolverParameters = Get-ScriptParameterNames -Path (
        Join-Path $RepositoryRoot 'eng\promotion\Resolve-GitHubArtifactProvenance.ps1')
    Assert-ProvenanceTest 'v2-identity-parameters-replace-legacy-caller-and-signer-aliases' (
        $writerParameters -contains 'TopLevelCallerWorkflowRef' -and
        $writerParameters -contains 'ProducerWorkflowRef' -and
        $writerParameters -notcontains 'CallerWorkflowRef' -and
        $writerParameters -notcontains 'ReusableWorkflowRef' -and
        $resolverParameters -contains 'ExpectedTopLevelCallerWorkflowRef' -and
        $resolverParameters -contains 'ExpectedProducerWorkflowRef' -and
        $resolverParameters -contains 'PreflightEvidenceSha256' -and
        $resolverParameters -contains 'DiscoverMigrationAuthorizationIdentity' -and
        $resolverParameters -notcontains 'TrustedReusableWorkflowRef' -and
        $resolverParameters -notcontains 'StageInputsWorkflowRef'
    ) 'Producer or resolver still exposes a legacy caller/signer identity parameter.'

    $policy = Get-Content -LiteralPath (
        Join-Path $RepositoryRoot 'pipelines\config\promotion-policy.json') -Raw |
        ConvertFrom-Json -DateKind String
    $migrationRole = @($policy.t21ProvenanceContract.producerRoles | Where-Object {
        [string]$_.role -ceq 'migration-authorization'
    })
    $ctoRole = @($policy.t21ProvenanceContract.producerRoles | Where-Object {
        [string]$_.role -ceq 'cto-authorization'
    })
    $migrationIssuers = @($policy.provenance.migrationAuthorizationIssuers)
    Assert-ProvenanceTest 'policy-upgrades-prepared-input-and-attested-authorization-roles' (
        [string]$policy.stageOperationInputContract.schemaVersion -ceq '2.1.0' -and
        $null -eq $policy.stageOperationInputContract.PSObject.Properties['runMetadataPath'] -and
        $migrationRole.Count -eq 1 -and [bool]$migrationRole[0].requiresAttestation -and
        $ctoRole.Count -eq 1 -and [bool]$ctoRole[0].requiresAttestation -and
        @($migrationIssuers | Where-Object {
            [string]$_.workflowPath -cne '.github/workflows/migration-apply-authorization.yml'
        }).Count -eq 0
    ) 'Policy retained legacy prepared-input metadata or did not normalize both authorization roles.'

    $stageInputFixture = New-Fixture -Root (Join-Path $temp 'stage-input') -Role stage-operation-inputs
    $stageInputEmission = [pscustomobject]@{
        fixturePath = $stageInputFixture
        workflowOutput = @('T21-PRODUCER-MANIFEST status=PASS role=stage-operation-inputs stage=Development')
        githubOutput = "artifact-name=stage-operation-inputs-development-$appSha"
    }
    $stageInputOutput = @(& Invoke-Fixture -FixturePath $stageInputFixture `
            -Output (Join-Path $temp 'stage-input-out') -Role stage-operation-inputs 2>&1)
    $stageInputProvenancePath = Join-Path $temp 'stage-input-out\verified-provenance.json'
    $stageInputManifestPath = Join-Path $temp 'stage-input-out\artifact\t21-producer-manifest.json'
    $stageInputProvenance = Get-Content -LiteralPath $stageInputProvenancePath -Raw | ConvertFrom-Json -DateKind String
    $stageInputManifest = Get-Content -LiteralPath $stageInputManifestPath -Raw | ConvertFrom-Json -DateKind String
    $stageIdentity = Get-RoleIdentity -Role stage-operation-inputs -Stage Development
    Assert-ProvenanceTest 'stage-input-exact-workflow-manifest-run-emits-the-resolver-accepted-v2-producer' (
        @($stageInputEmission.workflowOutput | Where-Object {
                $_.Contains('T21-PRODUCER-MANIFEST status=PASS role=stage-operation-inputs stage=Development')
            }).Count -eq 1 -and
        $stageInputEmission.githubOutput.Contains(
            "artifact-name=stage-operation-inputs-development-$appSha") -and
        [bool]$stageInputOutput[-1] -and
        [string]$stageInputManifest.producer.workflowPath -ceq $stageIdentity.topLevelPath -and
        [string]$stageInputManifest.producer.workflowRef -ceq $stageIdentity.topLevelRef -and
        [string]$stageInputManifest.trustedExecution.topLevelCallerWorkflowRef -ceq $stageIdentity.topLevelRef -and
        [string]$stageInputManifest.trustedExecution.producerWorkflowRef -ceq $stageIdentity.producerRef -and
        [string]$stageInputProvenance.run.workflowRef -ceq $stageIdentity.topLevelRef -and
        [string]$stageInputProvenance.attestation.signerWorkflow -ceq $stageIdentity.producerRef.Split('@')[0] -and
        [string]$stageInputProvenance.attestation.signerDigest -ceq $stageIdentity.producerSha
    ) "Exact v2 top-level caller/producer identity fixture was rejected: $($stageInputOutput -join ' | ')"

    $ctoFixture = New-Fixture -Root (Join-Path $temp 'cto') -Role cto-authorization -Stage Production
    $ctoOutput = @(& Invoke-Fixture -FixturePath $ctoFixture -Output (Join-Path $temp 'cto-out') `
            -Role cto-authorization -Stage Production 2>&1)
    $ctoProvenance = Get-Content -LiteralPath (Join-Path $temp 'cto-out\verified-provenance.json') -Raw |
        ConvertFrom-Json -DateKind String
    Assert-ProvenanceTest 'cto-authorization-canonical-name-and-attested-direct-producer-accepted' (
        [bool]$ctoOutput[-1] -and
        [string]$ctoProvenance.artifact.name -ceq "cto-authorization-$appSha" -and
        [string]$ctoProvenance.run.workflowPath -ceq '.github/workflows/cto-authorization-record.yml'
    ) "Exact CTO authorization provenance was rejected: $($ctoOutput -join ' | ')"

    $migrationFixture = New-Fixture -Root (Join-Path $temp 'migration') -Role migration-authorization -Stage Staging
    $migrationOutput = @(& Invoke-Fixture -FixturePath $migrationFixture `
            -Output (Join-Path $temp 'migration-out') -Role migration-authorization -Stage Staging 2>&1)
    $migrationProvenance = Get-Content -LiteralPath (
        Join-Path $temp 'migration-out\verified-provenance.json') -Raw | ConvertFrom-Json -DateKind String
    Assert-ProvenanceTest 'migration-authorization-canonical-name-and-attested-direct-producer-accepted' (
        [bool]$migrationOutput[-1] -and
        [string]$migrationProvenance.artifact.name -ceq
            "migration-apply-authorization-Staging-$appSha-$preflightEvidenceSha" -and
        [string]$migrationProvenance.run.workflowPath -ceq '.github/workflows/migration-apply-authorization.yml'
    ) "Exact migration authorization provenance was rejected: $($migrationOutput -join ' | ')"
    $migrationIdentity = Get-RoleIdentity -Role migration-authorization -Stage Staging
    $migrationDiscoveryOutput = Join-Path $temp 'migration-discovery-out'
    $migrationDiscoverySucceeded = $true
    try {
        $null = @(& (Join-Path $RepositoryRoot (
                    'eng\promotion\Resolve-GitHubArtifactProvenance.ps1')) `
                -ExpectedRole migration-authorization -RunId 501 -ConsumerRunId 901 `
                -BundleSha256 $bundleSha -OutputRoot $migrationDiscoveryOutput `
                -ExpectedTopLevelCallerWorkflowRef $migrationIdentity.topLevelRef `
                -ExpectedProducerWorkflowRef $migrationIdentity.producerRef `
                -DiscoverMigrationAuthorizationIdentity -FixturePath $migrationFixture 2>&1)
    }
    catch {
        $migrationDiscoverySucceeded = $false
    }
    $discoveredMigrationIdentity = if ($migrationDiscoverySucceeded) {
        Get-Content -LiteralPath (
            Join-Path $migrationDiscoveryOutput (
                'discovered-migration-authorization-identity.json')) -Raw |
            ConvertFrom-Json -DateKind String
    }
    else {
        $null
    }
    Assert-ProvenanceTest 'migration-authorization-discovery-derives-only-attested-canonical-bindings' (
        $migrationDiscoverySucceeded -and
        $null -ne $discoveredMigrationIdentity -and
        [string]$discoveredMigrationIdentity.stage -ceq 'Staging' -and
        [string]$discoveredMigrationIdentity.applicationSha256 -ceq $appSha -and
        [string]$discoveredMigrationIdentity.preflightEvidenceSha256 -ceq
            $preflightEvidenceSha -and
        [string]$discoveredMigrationIdentity.releaseManifestSha256 -ceq
            $releaseManifestSha -and
        [string]$discoveredMigrationIdentity.releaseCommitSha -ceq
            $releaseCommitSha -and
        [string]$discoveredMigrationIdentity.releaseRunId -ceq '401'
    ) 'Migration discovery did not derive the exact signed stage/app/preflight/release identity.'

    $swappedRoot = Join-Path $temp 'swapped-identities'
    Copy-Item -LiteralPath (Split-Path -Parent $stageInputFixture) -Destination $swappedRoot -Recurse
    $swappedFixture = Join-Path $swappedRoot 'fixture.json'
    Update-FixtureArchive -FixturePath $swappedFixture -Change {
        param($manifest)
        $top = $manifest.trustedExecution.topLevelCallerWorkflowRef
        $manifest.trustedExecution.topLevelCallerWorkflowRef = $manifest.trustedExecution.producerWorkflowRef
        $manifest.trustedExecution.producerWorkflowRef = $top
    }
    $swappedOutput = @(& Invoke-Fixture -FixturePath $swappedFixture -Output (Join-Path $swappedRoot 'out') `
            -Role stage-operation-inputs 2>&1)
    Assert-ProvenanceTest 'swapped-top-level-caller-and-producer-identities-rejected' (
        -not [bool]$swappedOutput[-1]
    ) 'The signed producer manifest accepted exchanged top-level caller and producer workflow refs.'

    $wrongApiPathRoot = Join-Path $temp 'wrong-api-path'
    Copy-Item -LiteralPath (Split-Path -Parent $stageInputFixture) -Destination $wrongApiPathRoot -Recurse
    $wrongApiPathFixture = Join-Path $wrongApiPathRoot 'fixture.json'
    $wrongApiPath = Get-Content -LiteralPath $wrongApiPathFixture -Raw | ConvertFrom-Json -DateKind String
    $wrongApiPath.run.path = '.github/workflows/stage-operation-inputs.yml'
    Write-Utf8Json -Value $wrongApiPath -Path $wrongApiPathFixture -Depth 20
    $wrongApiPathOutput = @(& Invoke-Fixture -FixturePath $wrongApiPathFixture `
            -Output (Join-Path $wrongApiPathRoot 'out') -Role stage-operation-inputs 2>&1)
    Assert-ProvenanceTest 'wrong-api-top-level-workflow-path-rejected' (
        -not [bool]$wrongApiPathOutput[-1]
    ) 'The API reusable workflow path was accepted as the top-level caller run path.'

    $wrongSignerRoot = Join-Path $temp 'wrong-signer'
    Copy-Item -LiteralPath (Split-Path -Parent $migrationFixture) -Destination $wrongSignerRoot -Recurse
    $wrongSignerFixture = Join-Path $wrongSignerRoot 'fixture.json'
    $wrongSigner = Get-Content -LiteralPath $wrongSignerFixture -Raw | ConvertFrom-Json -DateKind String
    $wrongSigner.attestation.signerDigest = '9' * 40
    Write-Utf8Json -Value $wrongSigner -Path $wrongSignerFixture -Depth 20
    $wrongSignerOutput = @(& Invoke-Fixture -FixturePath $wrongSignerFixture `
            -Output (Join-Path $wrongSignerRoot 'out') -Role migration-authorization -Stage Staging 2>&1)
    Assert-ProvenanceTest 'wrong-role-pinned-attestation-signer-rejected' (
        -not [bool]$wrongSignerOutput[-1]
    ) 'A migration authorization attestation with the wrong signer digest was accepted.'

    $wrongDigestRoot = Join-Path $temp 'wrong-digest'
    Copy-Item -LiteralPath (Split-Path -Parent $ctoFixture) -Destination $wrongDigestRoot -Recurse
    $wrongDigestFixture = Join-Path $wrongDigestRoot 'fixture.json'
    $wrongDigest = Get-Content -LiteralPath $wrongDigestFixture -Raw | ConvertFrom-Json -DateKind String
    $wrongDigest.artifacts[0].digest = "sha256:$('f' * 64)"
    Write-Utf8Json -Value $wrongDigest -Path $wrongDigestFixture -Depth 20
    $wrongDigestOutput = @(& Invoke-Fixture -FixturePath $wrongDigestFixture `
            -Output (Join-Path $wrongDigestRoot 'out') -Role cto-authorization -Stage Production 2>&1)
    Assert-ProvenanceTest 'wrong-immutable-artifact-digest-rejected' (
        -not [bool]$wrongDigestOutput[-1]
    ) 'An artifact whose API digest did not match the archive was accepted.'

    $wrongRunRoot = Join-Path $temp 'wrong-run'
    Copy-Item -LiteralPath (Split-Path -Parent $stageInputFixture) -Destination $wrongRunRoot -Recurse
    $wrongRunFixture = Join-Path $wrongRunRoot 'fixture.json'
    $wrongRun = Get-Content -LiteralPath $wrongRunFixture -Raw | ConvertFrom-Json -DateKind String
    $wrongRun.run.id = '777'
    Write-Utf8Json -Value $wrongRun -Path $wrongRunFixture -Depth 20
    $wrongRunOutput = @(& Invoke-Fixture -FixturePath $wrongRunFixture -Output (Join-Path $wrongRunRoot 'out') `
            -Role stage-operation-inputs 2>&1)
    Assert-ProvenanceTest 'wrong-api-run-selector-rejected' (
        -not [bool]$wrongRunOutput[-1]
    ) 'The selected API run ID was not bound to the fixture run identity.'

    $legacyIdentityRoot = Join-Path $temp 'legacy-identity'
    Copy-Item -LiteralPath (Split-Path -Parent $stageInputFixture) -Destination $legacyIdentityRoot -Recurse
    $legacyIdentityFixture = Join-Path $legacyIdentityRoot 'fixture.json'
    Update-FixtureArchive -FixturePath $legacyIdentityFixture -Change {
        param($manifest)
        $manifest.trustedExecution | Add-Member -NotePropertyName callerWorkflowRef `
            -NotePropertyValue 'syedmh/Dreamer/.github/workflows/legacy.yml@refs/heads/main'
    }
    $legacyIdentityOutput = @(& Invoke-Fixture -FixturePath $legacyIdentityFixture `
            -Output (Join-Path $legacyIdentityRoot 'out') -Role stage-operation-inputs 2>&1)
    Assert-ProvenanceTest 'legacy-or-unrecognized-trusted-execution-identity-field-rejected' (
        -not [bool]$legacyIdentityOutput[-1]
    ) 'A manifest with an unrecognized legacy caller identity field was accepted.'

    $wrongMigrationNameRoot = Join-Path $temp 'wrong-migration-name'
    Copy-Item -LiteralPath (Split-Path -Parent $migrationFixture) -Destination $wrongMigrationNameRoot -Recurse
    $wrongMigrationNameFixture = Join-Path $wrongMigrationNameRoot 'fixture.json'
    $wrongMigrationName = Get-Content -LiteralPath $wrongMigrationNameFixture -Raw | ConvertFrom-Json -DateKind String
    $wrongMigrationName.artifacts[0].name = "migration-apply-authorization-staging-$appSha"
    Write-Utf8Json -Value $wrongMigrationName -Path $wrongMigrationNameFixture -Depth 20
    $wrongMigrationNameOutput = @(& Invoke-Fixture -FixturePath $wrongMigrationNameFixture `
            -Output (Join-Path $wrongMigrationNameRoot 'out') -Role migration-authorization -Stage Staging 2>&1)
    Assert-ProvenanceTest 'migration-authorization-requires-internally-derived-canonical-name' (
        -not [bool]$wrongMigrationNameOutput[-1]
    ) 'A migration authorization artifact without the exact stage/app/preflight canonical name was accepted.'

    $productionStageInputFixture = New-Fixture -Root (Join-Path $temp 'production-stage-input') `
        -Role stage-operation-inputs -Stage Production
    $productionStageInputEmission = [pscustomobject]@{
        fixturePath = $productionStageInputFixture
        workflowOutput = @('T21-PRODUCER-MANIFEST status=PASS role=stage-operation-inputs stage=Production')
        githubOutput = "artifact-name=stage-operation-inputs-production-$appSha"
    }
    $productionStageInputOutput = @(& Invoke-Fixture -FixturePath $productionStageInputFixture `
            -Output (Join-Path $temp 'production-stage-input-out') -Role stage-operation-inputs -Stage Production 2>&1)
    Assert-ProvenanceTest 'production-stage-input-approved-top-level-caller-accepted' (
        @($productionStageInputEmission.workflowOutput | Where-Object {
                $_.Contains('T21-PRODUCER-MANIFEST status=PASS role=stage-operation-inputs stage=Production')
            }).Count -eq 1 -and
        [bool]$productionStageInputOutput[-1]
    ) "The approved Production stage-input caller was rejected: $($productionStageInputOutput -join ' | ')"

    $wrongProductionCallerRoot = Join-Path $temp 'wrong-production-caller'
    Copy-Item -LiteralPath (Split-Path -Parent $productionStageInputFixture) `
        -Destination $wrongProductionCallerRoot -Recurse
    $wrongProductionCallerFixture = Join-Path $wrongProductionCallerRoot 'fixture.json'
    $wrongProductionCaller = Get-Content -LiteralPath $wrongProductionCallerFixture -Raw |
        ConvertFrom-Json -DateKind String
    $wrongProductionCaller.run.path = '.github/workflows/release-build-and-nonproduction.yml'
    Write-Utf8Json -Value $wrongProductionCaller -Path $wrongProductionCallerFixture -Depth 20
    $wrongProductionCallerOutput = @(& Invoke-Fixture -FixturePath $wrongProductionCallerFixture `
            -Output (Join-Path $wrongProductionCallerRoot 'out') -Role stage-operation-inputs -Stage Production 2>&1)
    Assert-ProvenanceTest 'production-stage-input-wrong-top-level-caller-rejected' (
        -not [bool]$wrongProductionCallerOutput[-1]
    ) 'A non-Production workflow was accepted as the Production stage-input API run caller.'

    $apiCases = @(
        @{ Name = 'wrong-repository'; Change = { param($fixture) $fixture.run.repository.full_name = 'other/repo' } },
        @{ Name = 'unapproved-workflow'; Change = { param($fixture) $fixture.run.path = '.github/workflows/unapproved.yml' } },
        @{ Name = 'wrong-ref'; Change = { param($fixture) $fixture.run.head_branch = 'release' } },
        @{ Name = 'wrong-head-sha'; Change = { param($fixture) $fixture.run.head_sha = 'f' * 40 } },
        @{ Name = 'wrong-attempt'; Change = { param($fixture) $fixture.run.run_attempt = 0 } },
        @{ Name = 'wrong-status'; Change = { param($fixture) $fixture.run.status = 'queued' } },
        @{ Name = 'no-digest'; Change = { param($fixture) $fixture.artifacts[0].digest = '' } },
        @{ Name = 'no-artifact-id'; Change = { param($fixture) $fixture.artifacts[0].id = '0' } },
        @{ Name = 'expired'; Change = { param($fixture) $fixture.artifacts[0].expired = $true } },
        @{ Name = 'foreign-run-artifact'; Change = { param($fixture) $fixture.artifacts[0].workflow_run.id = '777' } },
        @{ Name = 'missing-attestation'; Change = { param($fixture) $fixture.attestation = $null } },
        @{ Name = 'replayed-attestation'; Change = { param($fixture) $fixture.attestation.sourceCommitSha = 'f' * 40 } }
    )
    foreach ($case in $apiCases) {
        $caseRoot = Join-Path $temp "api-$($case.Name)"
        Copy-Item -LiteralPath (Split-Path -Parent $stageInputFixture) -Destination $caseRoot -Recurse
        $caseFixturePath = Join-Path $caseRoot 'fixture.json'
        $caseFixture = Get-Content -LiteralPath $caseFixturePath -Raw | ConvertFrom-Json -DateKind String
        & $case.Change $caseFixture
        Write-Utf8Json -Value $caseFixture -Path $caseFixturePath -Depth 20
        $caseOutput = @(& Invoke-Fixture -FixturePath $caseFixturePath -Output (Join-Path $caseRoot 'out') `
                -Role stage-operation-inputs 2>&1)
        Assert-ProvenanceTest "github-provenance-$($case.Name)-rejected" (
            -not [bool]$caseOutput[-1]
        ) 'A mismatched API/artifact/attestation binding reached verified provenance.'
    }

    $duplicateRoot = Join-Path $temp 'duplicate-artifact'
    Copy-Item -LiteralPath (Split-Path -Parent $stageInputFixture) -Destination $duplicateRoot -Recurse
    $duplicateFixture = Join-Path $duplicateRoot 'fixture.json'
    $duplicate = Get-Content -LiteralPath $duplicateFixture -Raw | ConvertFrom-Json -DateKind String
    $duplicate.artifacts += $duplicate.artifacts[0]
    Write-Utf8Json -Value $duplicate -Path $duplicateFixture -Depth 20
    $duplicateOutput = @(& Invoke-Fixture -FixturePath $duplicateFixture -Output (Join-Path $duplicateRoot 'out') `
            -Role stage-operation-inputs 2>&1)
    Assert-ProvenanceTest 'github-provenance-duplicate-canonical-artifact-rejected' (
        -not [bool]$duplicateOutput[-1]
    ) 'Duplicate canonical artifact names were accepted.'

    foreach ($bindingCase in @(
        @{ Name = 'mismatched-role'; Property = 'producerRole'; Value = 'trusted-preflight' },
        @{ Name = 'mismatched-stage'; Property = 'stage'; Value = 'Staging' },
        @{ Name = 'mismatched-bundle'; Property = 'bundleSha256'; Value = ('f' * 64) }
    )) {
        $caseRoot = Join-Path $temp "manifest-$($bindingCase.Name)"
        Copy-Item -LiteralPath (Split-Path -Parent $stageInputFixture) -Destination $caseRoot -Recurse
        $caseFixturePath = Join-Path $caseRoot 'fixture.json'
        $property = [string]$bindingCase.Property
        $value = [string]$bindingCase.Value
        Update-FixtureArchive -FixturePath $caseFixturePath -Change {
            param($manifest)
            $manifest.$property = $value
        }.GetNewClosure()
        $caseOutput = @(& Invoke-Fixture -FixturePath $caseFixturePath -Output (Join-Path $caseRoot 'out') `
                -Role stage-operation-inputs 2>&1)
        Assert-ProvenanceTest "github-provenance-$($bindingCase.Name)-manifest-rejected" (
            -not [bool]$caseOutput[-1]
        ) 'A content manifest with mismatched role, stage, or bundle binding reached verified provenance.'
    }

    $tamperedRoot = Join-Path $temp 'tampered-payload'
    Copy-Item -LiteralPath (Split-Path -Parent $stageInputFixture) -Destination $tamperedRoot -Recurse
    $tamperedFixture = Join-Path $tamperedRoot 'fixture.json'
    Update-FixturePayload -FixturePath $tamperedFixture -Change {
        param($payloadRoot)
        [IO.File]::WriteAllText(
            (Join-Path $payloadRoot 'producer-content.json'),
            ('{"role":"tampered"}' + "`n"),
            [Text.UTF8Encoding]::new($false))
    }
    $tamperedOutput = @(& Invoke-Fixture -FixturePath $tamperedFixture -Output (Join-Path $tamperedRoot 'out') `
            -Role stage-operation-inputs 2>&1)
    Assert-ProvenanceTest 'github-provenance-altered-content-manifest-rejected' (
        -not [bool]$tamperedOutput[-1]
    ) 'An altered archive whose API digest was updated was accepted.'

    $wrongAppOutput = @(& Invoke-Fixture -FixturePath $stageInputFixture `
            -Output (Join-Path $temp 'wrong-app') -Role stage-operation-inputs -App ('f' * 64) 2>&1)
    Assert-ProvenanceTest 'github-provenance-mismatched-application-rejected' (
        -not [bool]$wrongAppOutput[-1]
    ) 'A producer manifest with the wrong requested application binding was accepted.'

    $forbiddenOutput = @()
    $forbiddenRejected = $false
    try {
        $forbiddenOutput = @(& (Join-Path $RepositoryRoot 'eng\promotion\Resolve-GitHubArtifactProvenance.ps1') `
            -ExpectedRole deployment-evidence -RunId 501 -ConsumerRunId 901 -Stage Development -ApplicationSha256 $appSha `
            -BundleSha256 $bundleSha -OutputRoot (Join-Path $temp 'forbidden') -FixturePath $stageInputFixture 2>&1)
    }
    catch {
        $forbiddenRejected = $true
        $forbiddenOutput += $_.Exception.Message
    }
    Assert-ProvenanceTest 'github-provenance-deployment-role-forbidden' (
        $forbiddenRejected -and
        ($forbiddenOutput -join "`n").Contains('T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN')
    ) 'The deployment-evidence producer role was accepted.'

    $causalTokenEnvironmentVariable = 'T21_TEST_RESOLVER_GITHUB_TOKEN'
    $priorCausalToken = [Environment]::GetEnvironmentVariable($causalTokenEnvironmentVariable)
    $causalIdentity = Get-RoleIdentity -Role stage-operation-inputs -Stage Development
    $causalFixture = New-Fixture -Root (Join-Path $temp 'causal-token-fixture') `
        -Role stage-operation-inputs
    $causalReleaseManifestSha256 = Get-FixtureReleaseManifestSha256 -FixturePath $causalFixture
    $noTokenRequestCount = 0
    $noTokenFailure = ''
    $noTokenInvoker = {
        param([string]$Method, [string]$Uri, [hashtable]$Headers, [string]$OutFile)
        $noTokenRequestCount++
        throw 'The no-token resolver reached its request seam.'
    }.GetNewClosure()
    try {
        [Environment]::SetEnvironmentVariable($causalTokenEnvironmentVariable, $null)
        $null = @(& (Join-Path $RepositoryRoot 'eng\promotion\Resolve-GitHubArtifactProvenance.ps1') `
            -ExpectedRole stage-operation-inputs -RunId 501 -ConsumerRunId 901 -Stage Development `
            -ApplicationSha256 $appSha -BundleSha256 $bundleSha `
            -ReleaseManifestSha256 $causalReleaseManifestSha256 -ReleaseCommitSha $releaseCommitSha `
            -OutputRoot (Join-Path $temp 'causal-no-token') `
            -ExpectedTopLevelCallerWorkflowRef $causalIdentity.topLevelRef `
            -ExpectedProducerWorkflowRef $causalIdentity.producerRef `
            -TokenEnvironmentVariable $causalTokenEnvironmentVariable -RequestInvoker $noTokenInvoker 2>&1)
    }
    catch {
        $noTokenFailure = $_.Exception.Message
    }
    Assert-ProvenanceTest 'github-provenance-direct-resolver-fails-before-request-without-token' (
        $noTokenRequestCount -eq 0 -and
        $noTokenFailure -ceq 'GitHub Actions API token is absent before OIDC.'
    ) 'The direct resolver did not fail causally before its first request when the token was absent.'

    $requestFixture = Get-Content -LiteralPath $causalFixture -Raw |
        ConvertFrom-Json -DateKind String
    $mappedRequestUris = [Collections.Generic.List[string]]::new()
    $mappedAuthorizationHeaders = [Collections.Generic.List[string]]::new()
    $mappedBearer = 't21-nonsecret-mapped-token-fixture'
    $expectedAuthorization = ('{0}{1} {2}' -f 'Bear', 'er', $mappedBearer)
    $mappedArtifactUri = 'https://fixture.invalid/stage-input-artifact.zip'
    $mappedInvoker = {
        param([string]$Method, [string]$Uri, [hashtable]$Headers, [string]$OutFile)

        $mappedRequestUris.Add($Uri)
        $mappedAuthorizationHeaders.Add([string]$Headers.Authorization)
        if (-not [string]::IsNullOrWhiteSpace($OutFile)) {
            if ($Uri -cne $mappedArtifactUri) {
                throw "Unexpected mapped fixture download URI: $Uri"
            }
            [IO.File]::WriteAllBytes(
                $OutFile,
                [IO.File]::ReadAllBytes([string]$requestFixture.artifacts[0].archivePath))
            return
        }
        if ($Uri -like '*/artifacts?name=*') {
            return [pscustomobject]@{
                artifacts = @([pscustomobject]@{
                    id = [string]$requestFixture.artifacts[0].id
                    name = [string]$requestFixture.artifacts[0].name
                    digest = [string]$requestFixture.artifacts[0].digest
                    size_in_bytes = [int64]$requestFixture.artifacts[0].size_in_bytes
                    created_at = [string]$requestFixture.artifacts[0].created_at
                    expires_at = [string]$requestFixture.artifacts[0].expires_at
                    expired = [bool]$requestFixture.artifacts[0].expired
                    workflow_run = $requestFixture.artifacts[0].workflow_run
                    archive_download_url = $mappedArtifactUri
                })
            }
        }
        if ($Uri.EndsWith('/901', [StringComparison]::Ordinal)) {
            return $requestFixture.consumerRun
        }
        if ($Uri.EndsWith('/501', [StringComparison]::Ordinal)) {
            return $requestFixture.run
        }
        throw "Unexpected mapped fixture API URI: $Uri"
    }.GetNewClosure()
    $priorGhFunction = Get-Item -LiteralPath Function:\gh -ErrorAction SilentlyContinue
    $mappedFailure = ''
    $mappedOutput = @()
    $mappedProvenancePath = Join-Path $temp 'causal-mapped-token\verified-provenance.json'
    try {
        [Environment]::SetEnvironmentVariable($causalTokenEnvironmentVariable, $mappedBearer)
        Set-Item -LiteralPath Function:\gh -Value {
            $global:LASTEXITCODE = 0
        }
        $mappedOutput = @(& (Join-Path $RepositoryRoot 'eng\promotion\Resolve-GitHubArtifactProvenance.ps1') `
            -ExpectedRole stage-operation-inputs -RunId 501 -ConsumerRunId 901 -Stage Development `
            -ApplicationSha256 $appSha -BundleSha256 $bundleSha `
            -ReleaseManifestSha256 $causalReleaseManifestSha256 -ReleaseCommitSha $releaseCommitSha `
            -OutputRoot (Join-Path $temp 'causal-mapped-token') `
            -ExpectedTopLevelCallerWorkflowRef $causalIdentity.topLevelRef `
            -ExpectedProducerWorkflowRef $causalIdentity.producerRef `
            -TokenEnvironmentVariable $causalTokenEnvironmentVariable -RequestInvoker $mappedInvoker 2>&1)
    }
    catch {
        $mappedFailure = $_.Exception.Message
    }
    finally {
        [Environment]::SetEnvironmentVariable($causalTokenEnvironmentVariable, $priorCausalToken)
        if ($null -ne $priorGhFunction) {
            Set-Item -LiteralPath Function:\gh -Value $priorGhFunction.ScriptBlock
        }
        else {
            Remove-Item -LiteralPath Function:\gh -ErrorAction SilentlyContinue
        }
    }
    $mappedProvenance = if (Test-Path -LiteralPath $mappedProvenancePath -PathType Leaf) {
        Get-Content -LiteralPath $mappedProvenancePath -Raw | ConvertFrom-Json -DateKind String
    }
    else {
        $null
    }
    Assert-ProvenanceTest 'github-provenance-mapped-token-local-request-fixture-succeeds' (
        [string]::IsNullOrWhiteSpace($mappedFailure) -and
        $mappedRequestUris.Count -eq 4 -and
        @($mappedAuthorizationHeaders | Where-Object {
                $_ -cne $expectedAuthorization
            }).Count -eq 0 -and
        $null -ne $mappedProvenance -and
        [string]$mappedProvenance.expectedRole -ceq 'stage-operation-inputs' -and
        @($mappedOutput | Where-Object {
                $_.ToString().Contains('VERIFIED-PROVENANCE status=PASS role=stage-operation-inputs')
            }).Count -eq 1
    ) "The mapped-token local request fixture failed: $mappedFailure"

    $tokenEnvironmentVariable = 'T21_TEST_GITHUB_TOKEN'
    $priorToken = [Environment]::GetEnvironmentVariable($tokenEnvironmentVariable)
    $capturedAuthorizationHeaders = [Collections.Generic.List[string]]::new()
    $capturedRequestUris = [Collections.Generic.List[string]]::new()
    $fixtureBearer = 't21-nonsecret-bearer-fixture'
    $now = [DateTimeOffset]::UtcNow
    [Environment]::SetEnvironmentVariable($tokenEnvironmentVariable, $fixtureBearer)
    $requestInvoker = {
        param([string]$Method, [string]$Uri, [hashtable]$Headers, [string]$OutFile)

        $capturedRequestUris.Add($Uri)
        $capturedAuthorizationHeaders.Add([string]$Headers.Authorization)
        if (-not [string]::IsNullOrWhiteSpace($OutFile)) {
            throw 'Local authenticated-request fixture stops before artifact download.'
        }
        if ($Uri -like '*/artifacts?name=*') {
            return [pscustomobject]@{
                artifacts = @([pscustomobject]@{
                    id = '601'
                    name = "raw-development-preflight-$appSha"
                    digest = "sha256:$appSha"
                    size_in_bytes = 1
                    created_at = $now.AddMinutes(-1).ToString('O')
                    expires_at = $now.AddDays(1).ToString('O')
                    expired = $false
                    workflow_run = [pscustomobject]@{ id = '501' }
                    archive_download_url = 'https://fixture.invalid/artifact.zip'
                })
            }
        }
        return [pscustomobject]@{
            repository = [pscustomobject]@{ full_name = 'syedmh/Dreamer' }
            id = '501'
            path = '.github/workflows/release-build-and-nonproduction.yml'
            run_attempt = 1
            head_branch = 'main'
            head_sha = $releaseCommitSha
            status = 'in_progress'
            conclusion = ''
            created_at = $now.AddMinutes(-2).ToString('O')
            updated_at = $now.AddMinutes(-1).ToString('O')
        }
    }.GetNewClosure()
    try {
        $preflightIdentity = Get-RoleIdentity -Role trusted-preflight -Stage Development
        $null = @(& (Join-Path $RepositoryRoot 'eng\promotion\Resolve-GitHubArtifactProvenance.ps1') `
            -ExpectedRole trusted-preflight -RunId 501 -ConsumerRunId 901 -Stage Development -ApplicationSha256 $appSha `
            -BundleSha256 $bundleSha -ReleaseManifestSha256 $releaseManifestSha `
            -ReleaseCommitSha $releaseCommitSha -OutputRoot (Join-Path $temp 'authenticated-request') `
            -ExpectedTopLevelCallerWorkflowRef $preflightIdentity.topLevelRef `
            -ExpectedProducerWorkflowRef $preflightIdentity.producerRef `
            -TokenEnvironmentVariable $tokenEnvironmentVariable -RequestInvoker $requestInvoker 2>&1)
    }
    catch {
        # The request-invoker fixture deliberately stops before artifact download.
    }
    finally {
        [Environment]::SetEnvironmentVariable($tokenEnvironmentVariable, $priorToken)
    }
    Assert-ProvenanceTest 'github-provenance-runtime-api-bearer-header-is-not-a-mask' (
        $capturedRequestUris.Count -eq 3 -and
        @($capturedAuthorizationHeaders | Where-Object { $_ -ceq "Bearer $fixtureBearer" }).Count -eq 3
    ) 'The fixture request invoker did not receive the actual runtime bearer authorization header.'
}
finally {
    if (Test-Path -LiteralPath $temp) {
        Remove-Item -LiteralPath $temp -Recurse -Force
    }
}

Write-Output "SUMMARY github-provenance total=$($passed + $failed) passed=$passed failed=$failed auth=0 deployments=0 database=0 resources=0"
if ($failed -gt 0) {
    exit 1
}
