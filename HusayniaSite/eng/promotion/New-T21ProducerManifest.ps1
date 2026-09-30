[CmdletBinding()]
param(
    [ValidateSet('Write', 'Validate')]
    [string]$Mode = 'Write',
    [Parameter(Mandatory = $true)]
    [string]$ArtifactRoot,
    [Parameter(Mandatory = $true)]
    [ValidateSet(
        'trusted-preflight',
        'trusted-stage-evidence',
        'stage-operation-inputs',
        'cto-authorization',
        'migration-authorization'
    )]
    [string]$ProducerRole,
    [Parameter(Mandatory = $true)]
    [ValidateSet('Development', 'Staging', 'Production')]
    [string]$Stage,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-f0-9]{64}$')]
    [string]$ApplicationSha256,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-f0-9]{64}$')]
    [string]$BundleSha256,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-f0-9]{64}$')]
    [string]$ReleaseManifestSha256,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-f0-9]{40}$')]
    [string]$ReleaseCommitSha,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[1-9][0-9]*$')]
    [string]$ReleaseRunId,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$ReleaseRunAttempt,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[1-9][0-9]*$')]
    [string]$ProducerRunId,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$ProducerRunAttempt,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-f0-9]{40}$')]
    [string]$ProducerCommitSha,
    [Parameter(Mandatory = $true)]
    [string]$TopLevelCallerWorkflowRef,
    [Parameter(Mandatory = $true)]
    [string]$ProducerWorkflowRef,
    [string]$CreatedAtUtc,
    [string]$PolicyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

function Assert-ExactProperties {
    param($Value, [string[]]$Expected, [string]$Label)

    if ($null -eq $Value -or @(Compare-Object ($Expected | Sort-Object) @(
            $Value.PSObject.Properties.Name | Sort-Object)).Count -ne 0) {
        throw "$Label properties do not exactly match the T21 v2 contract."
    }
}

function Assert-SafeManifestRelativePath {
    param([string]$Path)

    $segments = @($Path.Split('/'))
    if ([string]::IsNullOrWhiteSpace($Path) -or
        $Path -cne $Path.Normalize([Text.NormalizationForm]::FormC) -or
        $Path.Contains('\') -or
        $Path.Contains(':') -or
        $Path.StartsWith('/') -or
        $Path -match '(^|/)(\.|\.\.)($|/)' -or
        @($segments | Where-Object {
            [string]::IsNullOrWhiteSpace($_) -or $_.EndsWith('.') -or $_.EndsWith(' ') -or
            $_ -match '^(?i:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)'
        }).Count -ne 0) {
        throw "T21 producer manifest has an unsafe file path: $Path"
    }
}

function Get-ProducerFiles {
    param([string]$Root)

    $files = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $aliases = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force)) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "T21 producer artifact contains a reparse-point file: $($file.FullName)"
        }
        $relative = Get-RelativeUnixPath -BasePath $Root -Path $file.FullName
        Assert-SafeManifestRelativePath -Path $relative
        if ($relative -ceq 't21-producer-manifest.json') {
            continue
        }
        if (-not $seen.Add($relative) -or -not $aliases.Add($relative.Normalize(
                    [Text.NormalizationForm]::FormC).ToUpperInvariant())) {
            throw "T21 producer artifact contains duplicate or alias paths: $relative"
        }
        $files.Add([ordered]@{
            path = $relative
            sha256 = Get-Sha256Lower -Path $file.FullName
            length = [long]$file.Length
        })
    }
    if ($files.Count -eq 0) {
        throw 'T21 producer artifact contains no payload files.'
    }
    return @($files | Sort-Object { [string]$_.path })
}

function Get-WorkflowPathFromTopLevelRef {
    param($Contract, [string]$WorkflowRef)

    $prefix = "$([string]$Contract.repository)/"
    $suffix = "@$([string]$Contract.protectedRef)"
    if (-not $WorkflowRef.StartsWith($prefix, [StringComparison]::Ordinal) -or
        -not $WorkflowRef.EndsWith($suffix, [StringComparison]::Ordinal)) {
        throw 'T21 top-level caller workflow ref is not the exact protected-main identity.'
    }
    $workflowPath = $WorkflowRef.Substring($prefix.Length, $WorkflowRef.Length - $prefix.Length - $suffix.Length)
    if ($workflowPath -notmatch '^\.github/workflows/[A-Za-z0-9_.-]+\.yml$') {
        throw 'T21 top-level caller workflow path is malformed.'
    }
    return $workflowPath
}

function Get-WorkflowPathFromPinnedRef {
    param($Contract, [string]$WorkflowRef, [string]$Label)

    $prefix = "$([string]$Contract.repository)/"
    if (-not $WorkflowRef.StartsWith($prefix, [StringComparison]::Ordinal)) {
        throw "$Label is not in the trusted repository."
    }
    $suffixStart = $WorkflowRef.LastIndexOf('@', [StringComparison]::Ordinal)
    if ($suffixStart -le $prefix.Length -or $suffixStart -eq $WorkflowRef.Length - 1) {
        throw "$Label is malformed."
    }
    $workflowPath = $WorkflowRef.Substring($prefix.Length, $suffixStart - $prefix.Length)
    $digest = $WorkflowRef.Substring($suffixStart + 1)
    if ($workflowPath -notmatch '^\.github/workflows/[A-Za-z0-9_.-]+\.yml$' -or
        $digest -notmatch '^[a-f0-9]{40}$' -or
        $digest -ceq ('0' * 40)) {
        throw "$Label is malformed or all-zero."
    }
    return $workflowPath
}

function Assert-RoleAndIdentityContract {
    param(
        $Policy,
        [string]$Role,
        [string]$StageName,
        [string]$TopLevelRef,
        [string]$PinnedProducerRef
    )

    $contract = $Policy.t21ProvenanceContract
    Assert-ExactProperties -Value $contract -Expected @(
        'attestation', 'callerMatrix', 'producerRoles', 'protectedRef', 'repository', 'schemaVersion'
    ) -Label 'T21 provenance policy'
    if ([string]$contract.schemaVersion -cne '2.1.0' -or
        [string]$contract.repository -cne 'syedmh/Dreamer' -or
        [string]$contract.protectedRef -cne 'refs/heads/main') {
        throw 'T21 provenance policy identity is invalid.'
    }

    $roleEntry = @($contract.producerRoles | Where-Object { [string]$_.role -ceq $Role })
    if ($roleEntry.Count -ne 1 -or [bool]$roleEntry[0].forbidden -or
        -not [bool]$roleEntry[0].requiresAttestation) {
        throw "T21 producer role is absent, forbidden, or not attestable: $Role"
    }

    $topLevelPath = Get-WorkflowPathFromTopLevelRef -Contract $contract -WorkflowRef $TopLevelRef
    $producerPath = Get-WorkflowPathFromPinnedRef -Contract $contract -WorkflowRef $PinnedProducerRef `
        -Label 'T21 producer workflow ref'
    if ($producerPath -cne [string]$roleEntry[0].workflowPath) {
        throw 'T21 producer workflow ref is not the role-pinned signer identity.'
    }

    if ($Role -in @('trusted-preflight', 'trusted-stage-evidence', 'stage-operation-inputs')) {
        $callers = @($contract.callerMatrix | Where-Object {
            [string]$_.stage -ceq $StageName -and [string]$_.workflowRef -ceq $TopLevelRef
        })
        if ($callers.Count -ne 1) {
            throw 'T21 producer top-level caller is not in the exact caller matrix.'
        }
    }
    elseif ($topLevelPath -cne [string]$roleEntry[0].workflowPath) {
        throw 'T21 direct producer top-level caller does not match the exact role workflow path.'
    }
    if ($Role -eq 'cto-authorization' -and $StageName -cne 'Production') {
        throw 'T21 CTO authorization producer is restricted to Production.'
    }

    return $topLevelPath
}

if ([string]::IsNullOrWhiteSpace($PolicyPath)) {
    $PolicyPath = Join-Path (Resolve-HusayniaRepositoryRoot) 'pipelines\config\promotion-policy.json'
}
if (-not (Test-Path -LiteralPath $PolicyPath -PathType Leaf)) {
    throw 'T21 producer policy is missing.'
}
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json -DateKind String
$root = (Resolve-Path -LiteralPath $ArtifactRoot).Path
if ((Get-Item -LiteralPath $root).PSIsContainer -ne $true -or
    ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'T21 producer artifact root must be a real directory.'
}
$topLevelWorkflowPath = Assert-RoleAndIdentityContract -Policy $policy -Role $ProducerRole `
    -StageName $Stage -TopLevelRef $TopLevelCallerWorkflowRef -PinnedProducerRef $ProducerWorkflowRef

$manifestPath = Join-Path $root 't21-producer-manifest.json'
if ($Mode -eq 'Write' -and (Test-Path -LiteralPath $manifestPath)) {
    throw 'T21 producer manifest already exists; the archive must contain exactly one generated manifest.'
}
if ($Mode -eq 'Write') {
    if ([string]::IsNullOrWhiteSpace($CreatedAtUtc)) {
        $CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $created = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse($CreatedAtUtc, [ref]$created) -or
        $created.Offset -ne [TimeSpan]::Zero) {
        throw 'T21 producer manifest timestamp must be a UTC RFC3339 value.'
    }
    $manifest = [ordered]@{
        schemaVersion = '2.0.0'
        kind = 't21-producer-manifest'
        producerRole = $ProducerRole
        stage = $Stage
        applicationSha256 = $ApplicationSha256
        bundleSha256 = $BundleSha256
        release = [ordered]@{
            manifestSha256 = $ReleaseManifestSha256
            commitSha = $ReleaseCommitSha
            runId = $ReleaseRunId
            runAttempt = $ReleaseRunAttempt
        }
        producer = [ordered]@{
            repository = 'syedmh/Dreamer'
            workflowPath = $topLevelWorkflowPath
            workflowRef = $TopLevelCallerWorkflowRef
            runId = $ProducerRunId
            runAttempt = $ProducerRunAttempt
            commitSha = $ProducerCommitSha
            ref = 'refs/heads/main'
        }
        trustedExecution = [ordered]@{
            topLevelCallerWorkflowRef = $TopLevelCallerWorkflowRef
            producerWorkflowRef = $ProducerWorkflowRef
            bundlePath = 'operations/protected-execution-bundle.zip'
            bundleSha256 = $BundleSha256
        }
        files = @(Get-ProducerFiles -Root $root)
        createdAtUtc = $created.ToString('O')
    }
    Write-Utf8Json -Value $manifest -Path $manifestPath -Depth 20
}

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'T21 producer manifest is missing.'
}
$actual = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -DateKind String
Assert-ExactProperties -Value $actual -Expected @(
    'applicationSha256', 'bundleSha256', 'createdAtUtc', 'files', 'kind', 'producer', 'producerRole',
    'release', 'schemaVersion', 'stage', 'trustedExecution'
) -Label 'T21 producer manifest'
Assert-ExactProperties -Value $actual.release -Expected @(
    'commitSha', 'manifestSha256', 'runAttempt', 'runId'
) -Label 'T21 producer manifest release'
Assert-ExactProperties -Value $actual.producer -Expected @(
    'commitSha', 'ref', 'repository', 'runAttempt', 'runId', 'workflowPath', 'workflowRef'
) -Label 'T21 producer manifest producer'
Assert-ExactProperties -Value $actual.trustedExecution -Expected @(
    'bundlePath', 'bundleSha256', 'producerWorkflowRef', 'topLevelCallerWorkflowRef'
) -Label 'T21 producer manifest trusted execution'
if ([string]$actual.schemaVersion -cne '2.0.0' -or
    [string]$actual.kind -cne 't21-producer-manifest' -or
    [string]$actual.producerRole -cne $ProducerRole -or
    [string]$actual.stage -cne $Stage -or
    [string]$actual.applicationSha256 -cne $ApplicationSha256 -or
    [string]$actual.bundleSha256 -cne $BundleSha256 -or
    [string]$actual.release.manifestSha256 -cne $ReleaseManifestSha256 -or
    [string]$actual.release.commitSha -cne $ReleaseCommitSha -or
    [string]$actual.release.runId -cne $ReleaseRunId -or
    [int]$actual.release.runAttempt -ne $ReleaseRunAttempt -or
    [string]$actual.producer.workflowPath -cne $topLevelWorkflowPath -or
    [string]$actual.producer.workflowRef -cne $TopLevelCallerWorkflowRef -or
    [string]$actual.producer.runId -cne $ProducerRunId -or
    [int]$actual.producer.runAttempt -ne $ProducerRunAttempt -or
    [string]$actual.producer.commitSha -cne $ProducerCommitSha -or
    [string]$actual.producer.repository -cne 'syedmh/Dreamer' -or
    [string]$actual.producer.ref -cne 'refs/heads/main' -or
    [string]$actual.trustedExecution.topLevelCallerWorkflowRef -cne $TopLevelCallerWorkflowRef -or
    [string]$actual.trustedExecution.producerWorkflowRef -cne $ProducerWorkflowRef -or
    [string]$actual.trustedExecution.bundlePath -cne 'operations/protected-execution-bundle.zip' -or
    [string]$actual.trustedExecution.bundleSha256 -cne $BundleSha256) {
    throw 'T21 producer manifest binding is invalid.'
}
$expectedFiles = @(Get-ProducerFiles -Root $root)
$actualFiles = @($actual.files)
if ($actualFiles.Count -ne $expectedFiles.Count) {
    throw 'T21 producer manifest does not exactly cover its payload.'
}
for ($index = 0; $index -lt $expectedFiles.Count; $index++) {
    $expected = $expectedFiles[$index]
    $actualFile = $actualFiles[$index]
    Assert-ExactProperties -Value $actualFile -Expected @('length', 'path', 'sha256') `
        -Label 'T21 producer manifest file'
    if ([string]$actualFile.path -cne [string]$expected.path -or
        [string]$actualFile.sha256 -cne [string]$expected.sha256 -or
        [long]$actualFile.length -ne [long]$expected.length) {
        throw 'T21 producer manifest file order, length, or checksum is invalid.'
    }
}
Write-Output "T21-PRODUCER-MANIFEST status=PASS role=$ProducerRole stage=$Stage files=$($expectedFiles.Count) mode=$Mode"
