[CmdletBinding()]
param([string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Resolve-HusayniaRepositoryRoot }
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$passed = 0
$failed = 0
function Assert-Pipeline([string]$Name,[bool]$Condition,[string]$Failure) {
    if ($Condition) { $script:passed++; Write-Output "PASS  $Name" }
    else { $script:failed++; Write-Output "FAIL  $Name :: $Failure" }
}

$workflowRoot = Join-Path $RepositoryRoot 'pipelines\github'
$workflowFiles = @(Get-ChildItem -LiteralPath $workflowRoot -Filter *.yml | Sort-Object Name)
$expectedWorkflowNames = @(
    'automatic-nonproduction-orchestration.yml',
    'cto-authorization-record.yml',
    'migration-apply-authorization.yml',
    'operation-evidence-producer.yml',
    'pr-validation.yml',
    'production-operation-evidence.yml',
    'production-promotion.yml',
    'release-build-and-nonproduction.yml',
    'stage-evidence-intake.yml',
    'stage-operation-inputs.yml',
    'trusted-protected-operations.yml'
)
Assert-Pipeline 'exact-eleven-workflow-definition-files' (
    @(Compare-Object $expectedWorkflowNames @($workflowFiles.Name)).Count -eq 0
) 'pipelines/github is not the exact frozen eleven-file authoring set.'

$workflows = @{}
$allWorkflowText = ''
$jsonErrors = [Collections.Generic.List[string]]::new()
foreach ($file in $workflowFiles) {
    try {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        $workflows[$file.Name] = $text | ConvertFrom-Json -DateKind String
        $allWorkflowText += $text + "`n"
    }
    catch {
        $jsonErrors.Add("$($file.Name): $($_.Exception.Message)")
    }
}
Assert-Pipeline 'all-eleven-workflow-definitions-parse' ($jsonErrors.Count -eq 0) `
    "Invalid JSON authoring: $($jsonErrors -join '; ')"
if ($jsonErrors.Count -ne 0) {
    Write-Output "SUMMARY pipeline-definitions total=$($passed + $failed) passed=$passed failed=$failed"
    exit 1
}

$runParseErrors = [Collections.Generic.List[string]]::new()
$runBodies = [Collections.Generic.List[object]]::new()
foreach ($entry in $workflows.GetEnumerator()) {
    foreach ($jobProperty in $entry.Value.jobs.PSObject.Properties) {
        $job = $jobProperty.Value
        $steps = if ($null -ne $job.PSObject.Properties['steps']) { @($job.steps) } else { @() }
        foreach ($step in $steps) {
            if ($null -eq $step -or $null -eq $step.PSObject.Properties['run']) { continue }
            $run = [string]$step.run
            $runBodies.Add([pscustomobject]@{
                workflow = $entry.Key
                job = $jobProperty.Name
                step = [string]$step.name
                body = $run
            })
            $parseCandidate = [regex]::Replace($run, '\$\{\{.*?\}\}', 'x')
            try { $null = [scriptblock]::Create($parseCandidate) }
            catch { $runParseErrors.Add("$($entry.Key)/$($jobProperty.Name)/$($step.name): $($_.Exception.Message)") }
        }
    }
}
Assert-Pipeline 'all-workflow-powershell-bodies-parse' ($runParseErrors.Count -eq 0) `
    "PowerShell parse failures: $($runParseErrors -join '; ')"

$policyPath = Join-Path $RepositoryRoot 'pipelines\config\promotion-policy.json'
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json -DateKind String
Assert-Pipeline 'policy-v21-three-caller-completed-run-contract' (
    [string]$policy.t21ProvenanceContract.schemaVersion -ceq '2.1.0' -and
    @($policy.t21ProvenanceContract.callerMatrix).Count -eq 3 -and
    @($policy.t21ProvenanceContract.callerMatrix | Where-Object {
        [string]$_.workflowRef -match 'release-build-and-nonproduction'
    }).Count -eq 0 -and
    @($policy.t21ProvenanceContract.producerRoles | Where-Object {
        -not [bool]$_.forbidden -and -not [bool]$_.requiresCompletedSuccess
    }).Count -eq 0
) 'Policy retained release callers or in-progress selectable roles.'
Assert-Pipeline 'all-stages-disabled-no-rebuild-production-manual' (
    @($policy.stages | Where-Object { [bool]$_.deploymentEnabled -or [bool]$_.rebuildAllowed }).Count -eq 0 -and
    @($policy.stages | Where-Object {
        [string]$_.name -ceq 'Production' -and
        -not [bool]$_.automaticPromotionAllowed -and
        [bool]$_.environmentApprovalRequired -and
        [bool]$_.ctoAuthorizationRequired
    }).Count -eq 1
) 'A stage is enabled/rebuildable or Production lost manual CTO protection.'
Assert-Pipeline 'deployment-evidence-role-remains-forbidden' (
    @($policy.t21ProvenanceContract.producerRoles | Where-Object {
        [string]$_.role -ceq 'deployment-evidence' -and [bool]$_.forbidden
    }).Count -eq 1
) 'Deployment evidence is selectable.'
Assert-Pipeline 'prepared-input-policy-is-v21-without-change-record' (
    [string]$policy.stageOperationInputContract.schemaVersion -ceq '2.1.0' -and
    @($policy.stageOperationInputContract.reports | Where-Object {
        [string]$_.evidenceType -ceq 'change-record'
    }).Count -eq 0
) 'Prepared-input policy retained pre-CTO change-record ownership.'

$checkoutUses = @([regex]::Matches($allWorkflowText, 'actions/checkout@([a-f0-9]{40})'))
Assert-Pipeline 'all-checkouts-use-exact-approved-pin' (
    $checkoutUses.Count -ge 1 -and
    @($checkoutUses | Where-Object {
        $_.Groups[1].Value -cne '11bd71901bbe5b1630ceea73d27597364c9af683'
    }).Count -eq 0
) 'A checkout pin is absent, generic, or changed.'

$checkoutRootErrors = [Collections.Generic.List[string]]::new()
$shellErrors = [Collections.Generic.List[string]]::new()
foreach ($entry in $workflows.GetEnumerator()) {
    foreach ($jobProperty in $entry.Value.jobs.PSObject.Properties) {
        $job = $jobProperty.Value
        $steps = if ($null -ne $job.PSObject.Properties['steps']) { @($job.steps) } else { @() }
        $jobRunSteps = @($steps | Where-Object { $null -ne $_.PSObject.Properties['run'] })
        if ($jobRunSteps.Count -gt 0 -and [string]$job.defaults.run.shell -cne 'pwsh') {
            $shellErrors.Add("$($entry.Key)/$($jobProperty.Name)")
        }
        $hasCheckout = @($steps | Where-Object {
            $null -ne $_.PSObject.Properties['uses'] -and
            [string]$_.uses -match '^actions/checkout@'
        }).Count -gt 0
        if ($hasCheckout) {
            if ([string]$job.env.HUSAYNIA_REPOSITORY_ROOT -cne
                    '${{ github.workspace }}/HusayniaSite' -or
                [string]$job.defaults.run.shell -cne 'pwsh' -or
                [string]$job.defaults.run.'working-directory' -cne 'HusayniaSite' -or
                $jobRunSteps.Count -lt 1 -or
                -not ([string]$jobRunSteps[0].run).Contains('canonical') -or
                -not ([string]$jobRunSteps[0].run).Contains('HusayniaSite.sln')) {
                $checkoutRootErrors.Add("$($entry.Key)/$($jobProperty.Name)")
            }
        }
        elseif ($null -ne $job.PSObject.Properties['env'] -and
            $null -ne $job.env.PSObject.Properties['HUSAYNIA_REPOSITORY_ROOT']) {
            $checkoutRootErrors.Add("$($entry.Key)/$($jobProperty.Name): no-checkout root")
        }
    }
}
Assert-Pipeline 'all-powershell-run-bodies-use-pwsh' ($shellErrors.Count -eq 0) `
    "Jobs inherit another shell: $($shellErrors -join ', ')"
Assert-Pipeline 'checkout-jobs-use-exact-child-root-and-no-checkout-jobs-do-not' (
    $checkoutRootErrors.Count -eq 0
) "Checkout-root contract failures: $($checkoutRootErrors -join ', ')"

$rootExecutionErrors = [Collections.Generic.List[string]]::new()
$rootExecutionCount = 0
$rootFixture = Join-Path ([IO.Path]::GetTempPath()) (
    "t21-workflow-root-$([guid]::NewGuid().ToString('N'))")
$priorWorkspace = $env:GITHUB_WORKSPACE
$priorRepositoryRoot = $env:HUSAYNIA_REPOSITORY_ROOT
try {
    $fixtureWorkspace = Join-Path $rootFixture 'Dreamer'
    $fixtureRepositoryRoot = Join-Path $fixtureWorkspace 'HusayniaSite'
    New-Item -ItemType Directory -Path (
        Join-Path $fixtureRepositoryRoot 'eng'),
        (Join-Path $fixtureRepositoryRoot 'pipelines') -Force | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $fixtureRepositoryRoot 'HusayniaSite.sln'),
        'root execution fixture',
        [Text.UTF8Encoding]::new($false))

    foreach ($entry in $workflows.GetEnumerator()) {
        foreach ($jobProperty in $entry.Value.jobs.PSObject.Properties) {
            $job = $jobProperty.Value
            $steps = if ($null -ne $job.PSObject.Properties['steps']) {
                @($job.steps)
            }
            else {
                @()
            }
            $hasCheckout = @($steps | Where-Object {
                    $null -ne $_.PSObject.Properties['uses'] -and
                    [string]$_.uses -match '^actions/checkout@'
                }).Count -gt 0
            if (-not $hasCheckout) { continue }

            $runSteps = @($steps | Where-Object {
                    $null -ne $_.PSObject.Properties['run']
                })
            if ($runSteps.Count -lt 1) {
                $rootExecutionErrors.Add("$($entry.Key)/$($jobProperty.Name): missing run body")
                continue
            }
            $rootExecutionCount++
            $body = [scriptblock]::Create([string]$runSteps[0].run)
            Push-Location $fixtureRepositoryRoot
            try {
                $env:GITHUB_WORKSPACE = $fixtureWorkspace
                $env:HUSAYNIA_REPOSITORY_ROOT = $fixtureRepositoryRoot
                try {
                    & $body | Out-Null
                }
                catch {
                    $rootExecutionErrors.Add(
                        "$($entry.Key)/$($jobProperty.Name): valid child rejected: " +
                        $_.Exception.Message)
                }

                $env:HUSAYNIA_REPOSITORY_ROOT = $fixtureWorkspace
                $wrongRootRejected = $false
                try {
                    & $body | Out-Null
                }
                catch {
                    $wrongRootRejected = $_.Exception.Message.Contains(
                        'canonical Dreamer/HusayniaSite checkout root validation failed')
                }
                if (-not $wrongRootRejected) {
                    $rootExecutionErrors.Add(
                        "$($entry.Key)/$($jobProperty.Name): Dreamer parent was not rejected")
                }
            }
            finally {
                Pop-Location
            }
        }
    }
}
finally {
    $env:GITHUB_WORKSPACE = $priorWorkspace
    $env:HUSAYNIA_REPOSITORY_ROOT = $priorRepositoryRoot
    Remove-Item -LiteralPath $rootFixture -Recurse -Force -ErrorAction SilentlyContinue
}
Assert-Pipeline 'exact-checkout-root-bodies-execute-only-in-dreamer-child-layout' (
    $rootExecutionCount -eq $checkoutUses.Count -and
    $rootExecutionErrors.Count -eq 0
) "Exact root execution failures: $($rootExecutionErrors -join '; ')"

$release = $workflows['release-build-and-nonproduction.yml']
$releaseText = Get-Content -LiteralPath (Join-Path $workflowRoot 'release-build-and-nonproduction.yml') -Raw
Assert-Pipeline 'release-is-release-only-sole-c6-builder' (
    @($release.jobs.PSObject.Properties.Name).Count -eq 1 -and
    $null -ne $release.jobs.build_release -and
    $releaseText.Contains('-RepositoryRoot $env:HUSAYNIA_REPOSITORY_ROOT') -and
    @($workflowFiles | Where-Object {
        $_.Name -notin @('release-build-and-nonproduction.yml','pr-validation.yml') -and
        (Get-Content -LiteralPath $_.FullName -Raw).Contains('Invoke-PrValidation.ps1')
    }).Count -eq 0
) 'Release embeds downstream jobs or another downstream workflow builds C6.'

$prValidationText = Get-Content -LiteralPath (
    Join-Path $workflowRoot 'pr-validation.yml') -Raw
$dotnetValidationText = Get-Content -LiteralPath (
    Join-Path $RepositoryRoot 'eng\ci\Invoke-DotNetValidation.ps1') -Raw
Assert-Pipeline 'official-nuget-audit-remains-fail-closed-without-suppression' (
    $prValidationText.Contains('Run fail-closed PR validation') -and
    $prValidationText.Contains('./eng/ci/Invoke-PrValidation.ps1') -and
    -not $prValidationText.Contains('AllowKnownNuGetTlsFailure') -and
    -not $prValidationText.Contains('NuGetAudit=false') -and
    $dotnetValidationText.Contains("'-p:NuGetAudit=true'") -and
    $dotnetValidationText.Contains("'-p:NuGetAuditMode=all'") -and
    $dotnetValidationText.Contains("'--locked-mode'") -and
    $dotnetValidationText.Contains(
        'throw "Locked restore/NuGet audit failed with exit code $restoreExitCode."')
) 'The official PR path can waive, suppress, or normalize a failed locked NuGet audit.'

foreach ($name in @('operation-evidence-producer.yml','production-operation-evidence.yml')) {
    $wrapper = $workflows[$name]
    $expectedJobs = if ($name -ceq 'operation-evidence-producer.yml') {
        'preflight,prepared-inputs,record-completion,stage-operations,validate-input-shape'
    }
    else {
        'preflight,prepared-inputs,stage-operations,validate-input-shape'
    }
    Assert-Pipeline "$name-executes-one-frozen-mode" (
        @(Compare-Object @('Preflight','PreparedInputs','StageOperations') @(
            $wrapper.on.workflow_dispatch.inputs.mode.options)).Count -eq 0 -and
        (@($wrapper.jobs.PSObject.Properties.Name | Sort-Object) -join ',') -ceq
            $expectedJobs -and
        -not ((@(
                    $wrapper.jobs.preflight,
                    $wrapper.jobs.'prepared-inputs',
                    $wrapper.jobs.'stage-operations'
                ) | ConvertTo-Json -Depth 50).Contains('${{ github.run_id }}'))
    ) 'Wrapper has mixed jobs, current-run selectors, or a missing mode.'
}
$nonproductionWrapper = $workflows['operation-evidence-producer.yml']
Assert-Pipeline 'nonproduction-wrapper-attests-canonical-completed-dispatch-inputs' (
    $null -ne $nonproductionWrapper.on.workflow_dispatch.inputs.ctoAuthorizationRunId -and
    [string]$nonproductionWrapper.jobs.'record-completion'.name -ceq
        'T21 canonical completed-run attestation' -and
    [string]$nonproductionWrapper.jobs.'record-completion'.permissions.attestations -ceq
        'write' -and
    [string]$nonproductionWrapper.jobs.'record-completion'.permissions.'id-token' -ceq
        'write' -and
    (($nonproductionWrapper.jobs.'record-completion'.steps |
            ConvertTo-Json -Depth 20).Contains('New-T21CompletedRunManifest.ps1')) -and
    (($nonproductionWrapper.jobs.'record-completion'.steps |
            ConvertTo-Json -Depth 20).Contains(
            'actions/attest-build-provenance@e8998f949152b193b063cb0ec769d69d929409be'))
) 'Nonproduction completion lacks an attested canonical immutable dispatch-input record.'

$coordinator = $workflows['automatic-nonproduction-orchestration.yml']
$coordinatorText = Get-Content -LiteralPath (
    Join-Path $workflowRoot 'automatic-nonproduction-orchestration.yml') -Raw
$coordinatorScriptPath = Join-Path $RepositoryRoot (
    'eng\promotion\Invoke-T21CompletedRunCoordinator.ps1')
$coordinatorScriptText = Get-Content -LiteralPath $coordinatorScriptPath -Raw
Assert-Pipeline 'coordinator-permissions-are-exactly-nonprivileged' (
    (@($coordinator.permissions.PSObject.Properties.Name | Sort-Object) -join ',') -ceq
        'actions,attestations,contents' -and
    [string]$coordinator.permissions.actions -ceq 'write' -and
    [string]$coordinator.permissions.attestations -ceq 'read' -and
    [string]$coordinator.permissions.contents -ceq 'read' -and
    -not $coordinatorText.Contains('id-token') -and
    -not $coordinatorText.Contains('"environment"')
) 'Coordinator has OIDC, environment, secret, cloud, or artifact-write authority.'
Assert-Pipeline 'coordinator-event-allowlist-is-completed-cycle-free' (
    @(Compare-Object @(
        'T21 immutable release C6',
        'T21 protected migration Apply authorization',
        'T21 disabled nonproduction operation wrapper'
    ) @($coordinator.on.workflow_run.workflows)).Count -eq 0 -and
    (@($coordinator.on.workflow_run.types) -join ',') -ceq 'completed' -and
    -not (@($coordinator.on.workflow_run.workflows) -contains [string]$coordinator.name)
) 'Coordinator has an extra trigger or self-cycle.'
Assert-Pipeline 'coordinator-idempotency-waits-and-failure-paths-are-bounded' (
    $coordinatorScriptText.Contains('duplicate canonical child runs') -and
    $coordinatorScriptText.Contains('gh api --paginate --slurp') -and
    $coordinatorScriptText.Contains('paginated run discovery failed') -and
    $coordinatorScriptText.Contains('$attempt -lt 60') -and
    $coordinatorScriptText.Contains('Resolve-T21CompletedRun.ps1') -and
    -not $coordinatorScriptText.Contains('display_title') -and
    -not $coordinatorScriptText.Contains('T21_PREDECESSOR_TITLE') -and
    $coordinatorScriptText.Contains('T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN')
) 'Coordinator truncates discovery, duplicates children, waits without a bound, or advances failed/forbidden predecessors.'
Assert-Pipeline 'coordinator-does-not-automate-production' (
    -not $coordinatorScriptText.Contains('production-operation-evidence.yml') -and
    -not $coordinatorScriptText.Contains('Start-CorrelatedRun -Stage Production')
) 'Coordinator can dispatch Production.'
$coordinatorDispatchSteps = @($coordinator.jobs.coordinate.steps | Where-Object {
        [string]$_.name -ceq 'Dispatch only idempotent completed-run successors'
    })
$coordinatorDispatchRun = if ($coordinatorDispatchSteps.Count -eq 1) {
    [string]$coordinatorDispatchSteps[0].run
}
else {
    ''
}
Assert-Pipeline 'coordinator-dispatch-inputs-use-raw-string-fields' (
    $coordinatorDispatchSteps.Count -eq 1 -and
    $coordinatorDispatchRun -ceq
        "& (Join-Path `$env:HUSAYNIA_REPOSITORY_ROOT 'eng/promotion/Invoke-T21CompletedRunCoordinator.ps1')" -and
    ([regex]::Matches($coordinatorScriptText, '(?-i)-f (?:"|''?)inputs\[')).Count -eq 9 -and
    -not ($coordinatorScriptText -cmatch '(?:^|\s)-F(?:\s|$)') -and
    -not $coordinatorScriptText.Contains('--field')
) 'Coordinator dispatch can type-convert decimal workflow inputs instead of preserving JSON strings.'
Assert-Pipeline 'coordinator-verifies-canonical-apply-before-stageoperations-dispatch' (
    $coordinatorScriptText.Contains('-DiscoverMigrationAuthorizationIdentity') -and
    $coordinatorScriptText.Contains('-ExpectedRole migration-authorization') -and
    $coordinatorScriptText.Contains('Test-TrustedProtectedOperationInputs.ps1') -and
    $coordinatorScriptText.Contains('authorization validator returned before its immutable deployment fence') -and
    $coordinatorScriptText.IndexOf(
        'Test-TrustedProtectedOperationInputs.ps1',
        [StringComparison]::Ordinal) -lt
        $coordinatorScriptText.IndexOf(
            '-Mode StageOperations',
            [StringComparison]::Ordinal) -and
    -not $coordinatorScriptText.Contains('^T21 R10 M ')
) 'Coordinator can dispatch from run title/output without resolver-backed canonical APPLY validation.'

$resolverWorkflowCalls = [regex]::Matches(
    $allWorkflowText,
    '-ExpectedRole (?:release-c6|trusted-preflight|stage-operation-inputs|cto-authorization|migration-authorization)')
$resolverConsumerCalls = [regex]::Matches($allWorkflowText, '-ConsumerRunId \$env:GITHUB_RUN_ID')
Assert-Pipeline 'every-workflow-resolver-call-passes-current-consumer' (
    $resolverWorkflowCalls.Count -eq $resolverConsumerCalls.Count -and
    $resolverWorkflowCalls.Count -ge 10
) 'A workflow resolver call omits ConsumerRunId.'

$resolverPath = Join-Path $RepositoryRoot 'eng\promotion\Resolve-GitHubArtifactProvenance.ps1'
$resolver = Get-Content -LiteralPath $resolverPath -Raw
Assert-Pipeline 'resolver-enforces-completed-success-and-temporal-boundary' (
    $resolver.Contains('[string]$ConsumerRunId') -and
    $resolver.Contains('$RunId -ceq $ConsumerRunId') -and
    $resolver.Contains('$selectedUpdatedAt -gt $consumerCreatedAt') -and
    $resolver.Contains("[string]`$run.status -cne 'completed'") -and
    $resolver.Contains("[string]`$run.conclusion -cne 'success'") -and
    -not $resolver.Contains('Reusable producer run has an invalid status')
) 'Resolver permits current, future, in-progress, failed, or cancelled runs.'
Assert-Pipeline 'resolver-binds-producer-commit-not-release-commit' (
    $resolver.Contains('ProducerCommitSha = ([string]$run.head_sha).ToLowerInvariant()') -and
    $resolver.Contains('sourceCommitSha = ([string]$run.head_sha).ToLowerInvariant()') -and
    $resolver.Contains('commitSha = ([string]$run.head_sha).ToLowerInvariant()')
) 'Producer API/manifest/attestation identity is conflated with release identity.'
Assert-Pipeline 'release-discovery-is-read-only-and-fully-validated' (
    $resolver.Contains('[switch]$DiscoverReleaseIdentity') -and
    $resolver.Contains('discovered-release-identity.json') -and
    $resolver.Contains('Test-ReleaseArtifact.ps1')
) 'Coordinator release discovery can omit internal C6 validation.'

$trustedText = Get-Content -LiteralPath (
    Join-Path $workflowRoot 'trusted-protected-operations.yml') -Raw
$download = "& gh api ('repos/{0}/actions/artifacts/{1}/zip' -f `$env:GITHUB_REPOSITORY, `$artifact.id) > `$carrier"
Assert-Pipeline 'binary-download-uses-exact-native-stdout-redirection' (
    $trustedText.Contains($download) -and
    -not $trustedText.Contains('gh api --output') -and
    -not $trustedText.Contains('--output $carrier') -and
    $trustedText.Contains('$downloadExit = $LASTEXITCODE') -and
    $trustedText.Contains("PSVersion -lt [version]'7.4.0'")
) 'C6 binary retrieval uses unsupported/text output behavior.'
$downloadIndex = $trustedText.IndexOf($download,[StringComparison]::Ordinal)
$zipOpenIndex = $trustedText.IndexOf('[IO.Compression.ZipArchive]::new',[StringComparison]::Ordinal)
$envWriteIndex = $trustedText.IndexOf('T21_TRUSTED_BUNDLE_ARCHIVE=',[StringComparison]::Ordinal)
Assert-Pipeline 'binary-failures-stop-before-zip-and-side-effects' (
    $downloadIndex -gt 0 -and $zipOpenIndex -gt $downloadIndex -and $envWriteIndex -gt $zipOpenIndex -and
    $trustedText.Contains('carrier path was not empty before download') -and
    $trustedText.Contains('binary download failed or produced no carrier') -and
    $trustedText.Contains('$carrierLength -lt 1') -and
    $trustedText.Contains('$carrierLength -gt 2GB') -and
    $trustedText.Contains('carrier archive digest changed') -and
    $trustedText.Contains('[IO.File]::Delete($carrier)')
) 'Missing/empty/oversize/CLI/digest failures can reach ZIP processing or effects.'
Assert-Pipeline 'outer-carrier-and-fixed-bundle-r9-controls-remain' (
    $trustedText.Contains('t21-trusted-protected-execution-bundle.zip') -and
    $trustedText.Contains('IncrementalHash') -and
    $trustedText.Contains('$copied -ne $bundleEntry.Length') -and
    $trustedText.Contains('$actualBundleSha256 -cne $env:T21_TRUSTED_BUNDLE_SHA256') -and
    $trustedText.Contains('StringComparer]::OrdinalIgnoreCase') -and
    $trustedText.Contains('decompression limits')
) 'R9 carrier canonicalization, fixed path, limits, or streamed bundle hash weakened.'

$loginMatches = [regex]::Matches($allWorkflowText, 'azure/login@([a-f0-9]{40})')
$attestationMatches = [regex]::Matches(
    $allWorkflowText,
    'actions/attest-build-provenance@([a-f0-9]{40})')
Assert-Pipeline 'azure-login-pin-is-one-consistent-existing-commit' (
    $loginMatches.Count -eq 1 -and
    $loginMatches[0].Groups[1].Value -ceq '7184910d9eb2b1c5e48f7073824a90609bb9b6d6'
) 'Azure login uses a tag, branch, zero, nonexistent, or inconsistent pin.'
Assert-Pipeline 'exactly-five-attestation-producers-use-one-existing-commit' (
    $attestationMatches.Count -eq 5 -and
    @($attestationMatches | Where-Object {
        $_.Groups[1].Value -cne 'e8998f949152b193b063cb0ec769d69d929409be'
    }).Count -eq 0 -and
    [string]$policy.t21ProvenanceContract.attestation.action -ceq
        'actions/attest-build-provenance@e8998f949152b193b063cb0ec769d69d929409be'
) 'Attestation pins are inconsistent or producer count broadened.'
Assert-Pipeline 'old-invalid-action-pins-are-absent' (
    -not $allWorkflowText.Contains('858f4093d287a904987dfd22abd163280f939550') -and
    -not $allWorkflowText.Contains('96b4a1ef7235a096b17240c259729fdd70c83d45') -and
    -not (Get-Content -LiteralPath $policyPath -Raw).Contains(
        '96b4a1ef7235a096b17240c259729fdd70c83d45')
) 'An invalid R9 action pin remains.'

$trusted = $workflows['trusted-protected-operations.yml']
$trustedSteps = @($trusted.jobs.'protected-operation'.steps)
$loginIndex = [array]::FindIndex([object[]]$trustedSteps,[Predicate[object]]{
    param($step) [string]$step.name -ceq 'Login with externally bound stage identity'
})
$semanticIndex = [array]::FindIndex([object[]]$trustedSteps,[Predicate[object]]{
    param($step) [string]$step.name -ceq 'Validate bundle and resolve all completed pre-OIDC provenance'
})
$tokenIndex = [array]::FindIndex([object[]]$trustedSteps,[Predicate[object]]{
    param($step) [string]$step.name -ceq 'Mint OIDC only after verified provenance'
})
$recheckIndex = [array]::FindIndex([object[]]$trustedSteps,[Predicate[object]]{
    param($step) [string]$step.name -ceq 'Recheck immutable extraction'
})
$enablementIndex = [array]::FindIndex([object[]]$trustedSteps,[Predicate[object]]{
    param($step) [string]$step.name -ceq 'Enforce immutable stage enablement before token request'
})
$preflightProducerIndex = [array]::FindIndex([object[]]$trustedSteps,[Predicate[object]]{
    param($step)
    [string]$step.name -ceq 'Produce canonical attested raw preflight after read-only login'
})
$githubTokenSteps = @($trustedSteps | Where-Object {
        $null -ne $_.PSObject.Properties['env'] -and
        $null -ne $_.env.PSObject.Properties['GITHUB_TOKEN']
    })
$expectedGithubTokenStepNames = @(
    'Resolve canonical C6 and compare protected archive before extraction',
    'Validate bundle and resolve all completed pre-OIDC provenance'
)
Assert-Pipeline 'trusted-github-token-is-exactly-step-scoped-to-api-consumers' (
    $semanticIndex -ge 0 -and
    @(Compare-Object $expectedGithubTokenStepNames @(
            $githubTokenSteps.name | Sort-Object)).Count -eq 0 -and
    @($githubTokenSteps | Where-Object {
            [string]$_.env.GITHUB_TOKEN -cne '${{ github.token }}'
        }).Count -eq 0 -and
    [string]$trustedSteps[$semanticIndex].env.GITHUB_TOKEN -ceq '${{ github.token }}' -and
    @($trustedSteps | Where-Object {
            $null -ne $_.PSObject.Properties['run'] -and
            ([string]$_.run).Contains('GITHUB_TOKEN')
        }).Count -eq 0 -and
    ($null -eq $trusted.jobs.'protected-operation'.PSObject.Properties['env'] -or
        $null -eq $trusted.jobs.'protected-operation'.env.PSObject.Properties['GITHUB_TOKEN'])
) 'The resolver lacks its token, an unrelated/later step received it, or a run body can log it.'
Assert-Pipeline 'trusted-provenance-and-forbidden-boundary-precede-login' (
    $semanticIndex -ge 0 -and $loginIndex -gt $semanticIndex -and
    ([string]$trustedSteps[$semanticIndex].run).Contains('Test-TrustedProtectedOperationInputs.ps1') -and
    ([string]$trustedSteps[$semanticIndex].run).Contains('-ExpectedRole migration-authorization')
) 'StageOperations can reach login before R/P/I/C/M provenance and the forbidden boundary.'
Assert-Pipeline 'trusted-enabled-preflight-authenticates-before-readonly-bundle-producer' (
    $enablementIndex -gt $semanticIndex -and
    $tokenIndex -gt $enablementIndex -and
    $loginIndex -gt $tokenIndex -and
    $recheckIndex -gt $loginIndex -and
    $preflightProducerIndex -gt $recheckIndex -and
    [string]$trustedSteps[$preflightProducerIndex].env.T21_MIGRATION_IDENTITY_MODE -ceq 'readonly' -and
    @($trustedSteps | Where-Object {
            $null -ne $_.PSObject.Properties['env'] -and
            $null -ne $_.env.PSObject.Properties['T21_MIGRATION_IDENTITY_MODE']
        }).Count -eq 1
) 'Enabled Preflight does not preserve pre-token disablement and post-login bundle-only readonly execution.'
$protectedValidationSegments = @(
    $trustedSteps |
        Where-Object { $null -ne $_.PSObject.Properties['run'] } |
        ForEach-Object { ([string]$_.run).Split(';') } |
        Where-Object { $_.Contains('Test-ProtectedExecutionBundle.ps1') }
)
Assert-Pipeline 'trusted-validates-app-bound-sql-runtime-before-and-after-login' (
    $protectedValidationSegments.Count -eq 3 -and
    @($protectedValidationSegments | Where-Object {
        -not $_.Contains('-ExpectedApplicationSha256')
    }).Count -eq 0 -and
    ([string]$trustedSteps[$enablementIndex].env.T21_EXPECTED_APP_SHA256 -ceq
        '${{ inputs.expectedAppSha256 }}') -and
    ([string]$trustedSteps[$enablementIndex].run).Contains(
        'Assert-MigrationSqlRuntimeCompatibility -BundleRoot $env:T21_TRUSTED_BUNDLE_ROOT') -and
    ([string]$trustedSteps[$enablementIndex].run).Contains(
        '-ExpectedApplicationSha256 $env:T21_EXPECTED_APP_SHA256') -and
    ([string]$trustedSteps[$enablementIndex - 1].run).Contains(
        'Get-ChildItem -LiteralPath $bundleRoot -Recurse -File -Force') -and
    ([string]$trustedSteps[$enablementIndex - 1].run).Contains(
        'chmod -R a-w -- $bundleRoot')
) 'Trusted Preflight does not validate the application-bound SqlClient closure before OIDC and after login.'
Assert-Pipeline 'trusted-has-no-checkout-or-post-login-repository-code' (
    @($trustedSteps | Where-Object {
        $null -ne $_.PSObject.Properties['uses'] -and
        [string]$_.uses -match '^actions/checkout@'
    }).Count -eq 0 -and
    -not $trustedText.Contains('$env:GITHUB_WORKSPACE') -and
    ([string]$trustedSteps[$loginIndex + 1].run).Contains('T21_TRUSTED_BUNDLE_ROOT')
) 'Trusted execution reads repository content after login.'

$stageInputWorkflow = $workflows['stage-operation-inputs.yml']
$stageInputSteps = @($stageInputWorkflow.jobs.prepare.steps)
$stageInputReportSteps = @($stageInputSteps | Where-Object {
        [string]$_.name -ceq 'Generate inert role-derived prepared input reports and bundle'
    })
$ctoWorkflow = $workflows['cto-authorization-record.yml']
$ctoSteps = @($ctoWorkflow.jobs.'record-authorization'.steps)
$ctoReportSteps = @($ctoSteps | Where-Object {
        [string]$_.name -ceq 'Create checked Production change record and CTO authorization'
    })
Assert-Pipeline 'report-producers-use-protected-environments-and-step-only-readonly-secret' (
    [string]$stageInputWorkflow.jobs.prepare.environment.name -ceq
        '${{ inputs.stage == ''Production'' && ''Husaynia-Production'' || format(''Husaynia-{0}-Operations'', inputs.stage) }}' -and
    [string]$ctoWorkflow.jobs.'record-authorization'.environment -ceq
        'Husaynia-CTO-Authorization' -and
    $stageInputReportSteps.Count -eq 1 -and
    $ctoReportSteps.Count -eq 1 -and
    [string]$stageInputReportSteps[0].env.T21_STAGE_READONLY_PROBE_TOKEN -ceq
        '${{ secrets.T21_STAGE_READONLY_PROBE_TOKEN }}' -and
    [string]$ctoReportSteps[0].env.T21_STAGE_READONLY_PROBE_TOKEN -ceq
        '${{ secrets.T21_STAGE_READONLY_PROBE_TOKEN }}' -and
    @($stageInputSteps | Where-Object {
            $null -ne $_.PSObject.Properties['env'] -and
            $null -ne $_.env.PSObject.Properties['T21_STAGE_READONLY_PROBE_TOKEN']
        }).Count -eq 1 -and
    @($ctoSteps | Where-Object {
            $null -ne $_.PSObject.Properties['env'] -and
            $null -ne $_.env.PSObject.Properties['T21_STAGE_READONLY_PROBE_TOKEN']
        }).Count -eq 1
) 'Read-only service tokens are missing, unprotected, or exposed beyond report-producing steps.'

$inputGate = Get-Content -LiteralPath (
    Join-Path $RepositoryRoot 'eng\promotion\Test-TrustedProtectedOperationInputs.ps1') -Raw
Assert-Pipeline 'single-stable-deployment-forbidden-boundary-remains' (
    ([regex]::Matches($inputGate,
        "throw 'T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN'")).Count -eq 1 -and
    (Get-Content -LiteralPath (
        Join-Path $RepositoryRoot 'eng\common\Release.Common.ps1') -Raw).
        Contains("throw 'T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN'")
) 'The stable deployment forbidden boundary was removed or duplicated.'
Assert-Pipeline 'v21-stageoperations-cross-bindings-are-required' (
    $inputGate.Contains('producerBindings.preflight.runId') -and
    $inputGate.Contains('producerBindings.preparedInputs.runId') -and
    $inputGate.Contains('productionCtoAuthorization.sourceChangeRecordSha256') -and
    $inputGate.Contains("schemaVersion -cne '2.1.0'")
) 'StageOperations does not require exact R/P/I/C/M v2.1 bindings.'

$internalReusableUses = @([regex]::Matches(
    $allWorkflowText,
    'uses": "syedmh/Dreamer/\.github/workflows/[^"]+@([a-f0-9]{40})"'))
Assert-Pipeline 'external-reusable-zero-sha-sentinels-remain-installation-gated' (
    $internalReusableUses.Count -eq 6 -and
    @($internalReusableUses | Where-Object { $_.Groups[1].Value -cne ('0' * 40) }).Count -eq 0
) 'An internal reusable workflow was installed or received a non-frozen pin.'

$migrationCommon = Get-Content -LiteralPath (
    Join-Path $RepositoryRoot 'eng\artifact\migrations\bundle\Migration.Common.ps1') -Raw
$migrationBundle = Get-Content -LiteralPath (
    Join-Path $RepositoryRoot 'eng\artifact\migrations\bundle\Invoke-MigrationBundle.ps1') -Raw
$operationEvidenceProducer = Get-Content -LiteralPath (
    Join-Path $RepositoryRoot 'eng\promotion\Invoke-OperationEvidenceProducer.ps1') -Raw
Assert-Pipeline 'durable-sql-fence-remains-equal-or-stricter' (
    $migrationCommon.Contains('ActiveMutationId') -and
    $migrationCommon.Contains('ActiveMutationStartedAtUtc') -and
    $migrationCommon.Contains('UPDLOCK, HOLDLOCK') -and
    $migrationCommon.Contains('BeginMutation') -and
    $migrationCommon.Contains('CompleteMutation')
) 'Durable SQL-backed apply fencing was weakened.'
Assert-Pipeline 'migration-runtime-uses-vendored-in-process-sqlclient-without-install' (
    $migrationCommon.Contains('Microsoft.Data.SqlClient.SqlConnection') -and
    $migrationCommon.Contains('RestrictedAssemblyLoadContext') -and
    $migrationCommon.Contains('managedIdentities') -and
    $migrationCommon.Contains('dependencyAssetPath') -and
    $migrationCommon.Contains('runtimeIdentifier') -and
    $migrationCommon.Contains(': base(name, true)') -and
    $migrationCommon.Contains('Pooling=False') -and
    $migrationCommon.Contains('T21_AZURE_SQL_TOKEN_UNAVAILABLE') -and
    $migrationCommon.Contains('T21_SQL_EXECUTION_FAILED') -and
    -not "$migrationCommon`n$migrationBundle`n$trustedText".Contains('sqlcmd') -and
    -not "$migrationCommon`n$migrationBundle`n$trustedText".Contains('apt-get') -and
    -not "$migrationCommon`n$migrationBundle`n$trustedText".Contains('dotnet tool') -and
    -not "$migrationCommon`n$migrationBundle`n$trustedText".Contains('dotnet restore')
) 'Migration execution still depends on an unavailable/installable SQL client or lacks the fixed adapter contract.'
Assert-Pipeline 'migration-v21-provenance-c6-bundle-and-local-seam-boundaries-are-exact' (
    $migrationCommon.Contains(
        "[string]`$contract.schemaVersion -cne '2.1.0'") -and
    $migrationBundle.Contains(
        '$bundlePath = Join-Path $artifact $bundleRelativePath') -and
    $migrationBundle.Contains(
        '$protectedBundlePath = Join-Path $artifact $protectedBundleRelativePath') -and
    $migrationBundle.Contains(
        '[string]$authorization.bundleSha256 -ne $protectedBundleSha256') -and
    $operationEvidenceProducer.Contains(
        'ExpectedProtectedBundleSha256 = $TrustedBundleSha256') -and
    -not $migrationBundle.Contains(
        'Join-Path $resolvedTrustedBundleRoot "eng\artifact\$bundleRelativePath"') -and
    $migrationCommon.Contains(
        "GetEnvironmentVariable('T21_MIGRATION_STAGE_LEASE_STATE_PATH')") -and
    $migrationCommon.Contains(
        'Assert-MigrationLocalTestSeams -EnableLocalTestSeams:$EnableLocalTestSeams')
) 'Migration accepts stale v2.0 provenance, requires the Apply DLL in the protected closure, or activates a local lease seam implicitly.'
Assert-Pipeline 'migration-producer-and-release-commits-are-independent' (
    $migrationBundle.Contains(
        '[string]$releaseVerified.provenance.run.commitSha -cne $releaseCommitSha') -and
    -not $migrationBundle.Contains(
        '[string]$operationRun.commitSha -cne $releaseCommitSha')
) 'Migration producer commit is conflated with the immutable release commit.'
$releaseBuilder = Get-Content -LiteralPath (
    Join-Path $RepositoryRoot 'eng\artifact\New-ReleaseArtifact.ps1') -Raw
$releaseValidator = Get-Content -LiteralPath (
    Join-Path $RepositoryRoot 'eng\artifact\Test-ReleaseArtifact.ps1') -Raw
Assert-Pipeline 'release-cross-binds-protected-sql-runtime-to-application-archive' (
    $releaseBuilder.Contains(
        'ApplicationArchivePath = (Join-Path $appDirectory ''Husaynia.Web.zip'')') -and
    $releaseValidator.Contains(
        'ExpectedApplicationSha256 = [string]$applicationEntry[0].sha256')
) 'C6 builder or release validator omits the application archive/runtime cross-binding.'

$forbiddenStubs = @(
    $workflows['production-promotion.yml'].jobs.production.steps[0],
    $workflows['stage-evidence-intake.yml'].jobs.intake.steps[0]
)
Assert-Pipeline 'production-and-evidence-stubs-remain-disabled-pwsh-forbidden' (
    @($forbiddenStubs | Where-Object {
        -not ([string]$_.run).Contains('T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN')
    }).Count -eq 0 -and
    [string]$workflows['production-promotion.yml'].jobs.production.defaults.run.shell -ceq 'pwsh' -and
    [string]$workflows['stage-evidence-intake.yml'].jobs.intake.defaults.run.shell -ceq 'pwsh'
) 'A forbidden stub was enabled, removed, or left on a non-PowerShell shell.'

Write-Output "SUMMARY pipeline-definitions total=$($passed + $failed) passed=$passed failed=$failed workflows=$($workflowFiles.Count) runBodies=$($runBodies.Count) auth=0 dispatches=0 deployments=0 receipts=0 database=0 resources=0"
if ($failed -gt 0) { exit 1 }
