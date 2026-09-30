[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(
        'release-c6',
        'trusted-preflight',
        'stage-operation-inputs',
        'cto-authorization',
        'migration-authorization',
        'deployment-evidence',
        'trusted-stage-evidence'
    )]
    [string]$ExpectedRole,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[1-9][0-9]*$')]
    [string]$RunId,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[1-9][0-9]*$')]
    [string]$ConsumerRunId,
    [ValidateSet('Development', 'Staging', 'Production')]
    [string]$Stage,
    [string]$ApplicationSha256,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-f0-9]{64}$')]
    [string]$BundleSha256,
    [ValidatePattern('^[a-f0-9]{64}$')]
    [string]$ReleaseManifestSha256,
    [ValidatePattern('^[a-f0-9]{40}$')]
    [string]$ReleaseCommitSha,
    [ValidatePattern('^[a-f0-9]{64}$')]
    [string]$PreflightEvidenceSha256,
    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,
    [string]$ExpectedTopLevelCallerWorkflowRef,
    [string]$ExpectedProducerWorkflowRef,
    [string]$PolicyPath,
    [string]$FixturePath,
    [string]$TokenEnvironmentVariable = 'GITHUB_TOKEN',
    [scriptblock]$RequestInvoker,
    [string]$TestOnlyReleaseValidatorMarkerPath,
    [switch]$DiscoverReleaseIdentity,
    [switch]$DiscoverMigrationAuthorizationIdentity
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

function Get-StrictUtc {
    param([string]$Value, [string]$Label)

    $parsed = [DateTimeOffset]::MinValue
    if ([string]::IsNullOrWhiteSpace($Value) -or
        -not [DateTimeOffset]::TryParse($Value, [ref]$parsed) -or
        $parsed.Offset -ne [TimeSpan]::Zero) {
        throw "$Label is not a strict UTC timestamp."
    }
    return $parsed
}

function Get-WorkflowPathFromTopLevelRef {
    param($Contract, [string]$WorkflowRef, [string]$Label)

    $prefix = "$([string]$Contract.repository)/"
    $suffix = "@$([string]$Contract.protectedRef)"
    if ([string]::IsNullOrWhiteSpace($WorkflowRef) -or
        -not $WorkflowRef.StartsWith($prefix, [StringComparison]::Ordinal) -or
        -not $WorkflowRef.EndsWith($suffix, [StringComparison]::Ordinal)) {
        throw "$Label is not the exact protected-main workflow ref."
    }
    $workflowPath = $WorkflowRef.Substring($prefix.Length, $WorkflowRef.Length - $prefix.Length - $suffix.Length)
    if ($workflowPath -notmatch '^\.github/workflows/[A-Za-z0-9_.-]+\.yml$') {
        throw "$Label workflow path is malformed."
    }
    return $workflowPath
}

function Get-WorkflowPathFromPinnedRef {
    param($Contract, [string]$WorkflowRef, [string]$Label)

    $prefix = "$([string]$Contract.repository)/"
    if ([string]::IsNullOrWhiteSpace($WorkflowRef) -or
        -not $WorkflowRef.StartsWith($prefix, [StringComparison]::Ordinal)) {
        throw "$Label is not in the trusted repository."
    }
    $at = $WorkflowRef.LastIndexOf('@', [StringComparison]::Ordinal)
    if ($at -le $prefix.Length -or $at -eq $WorkflowRef.Length - 1) {
        throw "$Label is malformed."
    }
    $workflowPath = $WorkflowRef.Substring($prefix.Length, $at - $prefix.Length)
    $digest = $WorkflowRef.Substring($at + 1)
    if ($workflowPath -notmatch '^\.github/workflows/[A-Za-z0-9_.-]+\.yml$' -or
        $digest -notmatch '^[a-f0-9]{40}$' -or
        $digest -ceq ('0' * 40)) {
        throw "$Label is malformed or all-zero."
    }
    return $workflowPath
}

function Assert-ExpectedIdentityPair {
    param($Contract, $RoleEntry, [string]$Role, [string]$StageName,
        [string]$TopLevelRef, [string]$PinnedProducerRef)

    $topLevelPath = Get-WorkflowPathFromTopLevelRef -Contract $Contract -WorkflowRef $TopLevelRef `
        -Label 'Expected top-level caller workflow ref'
    $producerPath = Get-WorkflowPathFromPinnedRef -Contract $Contract -WorkflowRef $PinnedProducerRef `
        -Label 'Expected producer workflow ref'
    if ($producerPath -cne [string]$RoleEntry.workflowPath) {
        throw 'Expected producer workflow ref does not match the role-pinned signer workflow.'
    }
    if ($Role -in @('trusted-preflight', 'trusted-stage-evidence', 'stage-operation-inputs')) {
        $matchingCallers = @($Contract.callerMatrix | Where-Object {
            [string]$_.stage -ceq $StageName -and [string]$_.workflowRef -ceq $TopLevelRef
        })
        if ($matchingCallers.Count -ne 1) {
            throw 'Expected top-level caller workflow ref is not in the exact caller matrix.'
        }
    }
    elseif ($topLevelPath -cne [string]$RoleEntry.workflowPath) {
        throw 'Expected top-level caller workflow ref does not match the direct producer role.'
    }
    if ($Role -eq 'cto-authorization' -and $StageName -cne 'Production') {
        throw 'CTO authorization provenance is restricted to Production.'
    }
    return [pscustomobject]@{
        topLevelPath = $topLevelPath
        producerPath = $producerPath
    }
}

function Get-RequiredArtifactName {
    param([string]$Role, [string]$StageName, [string]$AppSha, [string]$PreflightSha)

    $lower = $StageName.ToLowerInvariant()
    switch ($Role) {
        'trusted-preflight' { return "raw-$lower-preflight-$AppSha" }
        'trusted-stage-evidence' { return "raw-$lower-evidence-$AppSha" }
        'stage-operation-inputs' { return "stage-operation-inputs-$lower-$AppSha" }
        'cto-authorization' { return "cto-authorization-$AppSha" }
        'migration-authorization' {
            if ($PreflightSha -notmatch '^[a-f0-9]{64}$') {
                throw 'Migration authorization provenance requires the exact preflight evidence SHA-256.'
            }
            return "migration-apply-authorization-$StageName-$AppSha-$PreflightSha"
        }
        'deployment-evidence' { return "deployment-evidence-$lower-$AppSha" }
        default { throw "T21 role has no canonical artifact name: $Role" }
    }
}

function Assert-SafeEntry {
    param(
        [string]$Name,
        [Collections.Generic.HashSet[string]]$Names,
        [Collections.Generic.HashSet[string]]$Aliases
    )

    $segments = @($Name.Split('/'))
    if ([string]::IsNullOrWhiteSpace($Name) -or
        $Name -cne $Name.Normalize([Text.NormalizationForm]::FormC) -or
        $Name.Contains('\') -or
        $Name.Contains(':') -or
        $Name.StartsWith('/') -or
        $Name.EndsWith('/') -or
        @($segments | Where-Object {
            [string]::IsNullOrWhiteSpace($_) -or $_ -in @('.', '..') -or
            $_.EndsWith('.') -or $_.EndsWith(' ')
        }).Count -ne 0 -or
        -not $Names.Add($Name) -or
        -not $Aliases.Add($Name.Normalize([Text.NormalizationForm]::FormC).ToUpperInvariant())) {
        throw "GitHub artifact archive contains an unsafe, duplicate, or alias path: $Name"
    }
}

function Expand-VerifiedArchive {
    param([string]$ArchivePath, [string]$Destination)

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $Destination) {
        throw 'Verified artifact extraction target already exists.'
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $root = (Resolve-Path -LiteralPath $Destination).Path
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $aliases = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $total = [long]0
        foreach ($entry in $archive.Entries) {
            Assert-SafeEntry -Name $entry.FullName -Names $names -Aliases $aliases
            $mode = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if ($mode -eq 0xA000 -or $entry.Length -gt 16MB) {
                throw "GitHub artifact archive has a link or oversized entry: $($entry.FullName)"
            }
            $total += $entry.Length
            if ($total -gt 64MB) {
                throw 'GitHub artifact archive exceeds the T21 size limit.'
            }
            $destinationPath = Join-Path $root $entry.FullName
            New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
            $input = $entry.Open()
            try {
                $output = [IO.File]::Open(
                    $destinationPath,
                    [IO.FileMode]::CreateNew,
                    [IO.FileAccess]::Write,
                    [IO.FileShare]::None)
                try {
                    $input.CopyTo($output)
                }
                finally {
                    $output.Dispose()
                }
            }
            finally {
                $input.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
    return $root
}

if ([string]::IsNullOrWhiteSpace($PolicyPath)) {
    $PolicyPath = Join-Path (Resolve-HusayniaRepositoryRoot) 'pipelines\config\promotion-policy.json'
}
if (-not (Test-Path -LiteralPath $PolicyPath -PathType Leaf)) {
    throw 'T21 provenance policy is missing.'
}
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json -DateKind String
$contract = $policy.t21ProvenanceContract
Assert-ExactProperties -Value $contract -Expected @(
    'attestation', 'callerMatrix', 'producerRoles', 'protectedRef', 'repository', 'schemaVersion'
) -Label 'T21 provenance policy'
if ([string]$contract.schemaVersion -cne '2.1.0' -or
    [string]$contract.repository -cne 'syedmh/Dreamer' -or
    [string]$contract.protectedRef -cne 'refs/heads/main') {
    throw 'T21 provenance policy identity is invalid.'
}
$role = @($contract.producerRoles | Where-Object { [string]$_.role -ceq $ExpectedRole })
if ($role.Count -ne 1) {
    throw "T21 producer role is not uniquely defined: $ExpectedRole"
}
if ([bool]$role[0].forbidden) {
    throw 'T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN'
}

$isReleaseC6 = $ExpectedRole -eq 'release-c6'
$isMigrationAuthorizationDiscovery = [bool]$DiscoverMigrationAuthorizationIdentity
if ($DiscoverReleaseIdentity -and $isMigrationAuthorizationDiscovery) {
    throw 'T21 provenance identity discovery modes are mutually exclusive.'
}
if ($DiscoverReleaseIdentity -and -not $isReleaseC6) {
    throw 'Release identity discovery is available only for release-c6 provenance.'
}
if ($isMigrationAuthorizationDiscovery -and $ExpectedRole -cne 'migration-authorization') {
    throw 'Migration authorization identity discovery is available only for migration-authorization provenance.'
}
if (-not $isMigrationAuthorizationDiscovery -and [string]::IsNullOrWhiteSpace($Stage)) {
    throw 'T21 provenance requires an exact expected stage.'
}
if (-not $DiscoverReleaseIdentity -and -not $isMigrationAuthorizationDiscovery -and
    $ApplicationSha256 -notmatch '^[a-f0-9]{64}$') {
    throw 'T21 provenance requires the expected application SHA-256.'
}
if ($DiscoverReleaseIdentity -and -not [string]::IsNullOrWhiteSpace($ApplicationSha256) -and
    $ApplicationSha256 -notmatch '^[a-f0-9]{64}$') {
    throw 'Release identity discovery received a malformed optional application SHA-256.'
}
if ($isMigrationAuthorizationDiscovery -and
    (-not [string]::IsNullOrWhiteSpace($Stage) -or
     -not [string]::IsNullOrWhiteSpace($ApplicationSha256) -or
     -not [string]::IsNullOrWhiteSpace($ReleaseManifestSha256) -or
     -not [string]::IsNullOrWhiteSpace($ReleaseCommitSha) -or
     -not [string]::IsNullOrWhiteSpace($PreflightEvidenceSha256))) {
    throw 'Migration authorization identity discovery does not accept caller-declared artifact bindings.'
}
if ($isReleaseC6) {
    if (-not [string]::IsNullOrWhiteSpace($ExpectedTopLevelCallerWorkflowRef) -or
        -not [string]::IsNullOrWhiteSpace($ExpectedProducerWorkflowRef)) {
        throw 'Release C6 provenance does not accept producer identity parameters.'
    }
}
else {
    if ([string]::IsNullOrWhiteSpace($ExpectedTopLevelCallerWorkflowRef) -or
        [string]::IsNullOrWhiteSpace($ExpectedProducerWorkflowRef)) {
        throw 'T21 producer provenance requires an exact top-level caller and pinned producer workflow pair.'
    }
    $expectedIdentity = Assert-ExpectedIdentityPair -Contract $contract -RoleEntry $role[0] `
        -Role $ExpectedRole -StageName $Stage -TopLevelRef $ExpectedTopLevelCallerWorkflowRef `
        -PinnedProducerRef $ExpectedProducerWorkflowRef
    if (-not $isMigrationAuthorizationDiscovery -and
        ($ReleaseManifestSha256 -notmatch '^[a-f0-9]{64}$' -or
         $ReleaseCommitSha -notmatch '^[a-f0-9]{40}$')) {
        throw 'T21 non-C6 provenance requires immutable release manifest and commit bindings.'
    }
}
if (-not [string]::IsNullOrWhiteSpace($TestOnlyReleaseValidatorMarkerPath) -and
    (-not $isReleaseC6 -or [string]::IsNullOrWhiteSpace($FixturePath))) {
    throw 'The release validator marker is available only to local release-c6 fixtures.'
}
$expectedName = if ($isReleaseC6 -or $isMigrationAuthorizationDiscovery) {
    ''
}
else {
    Get-RequiredArtifactName -Role $ExpectedRole -StageName $Stage -AppSha $ApplicationSha256 `
        -PreflightSha $PreflightEvidenceSha256
}

if (-not [string]::IsNullOrWhiteSpace($FixturePath)) {
    $fixture = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json -DateKind String
    Assert-ExactProperties -Value $fixture -Expected @('artifacts', 'attestation', 'consumerRun', 'run') `
        -Label 'T21 provenance fixture'
    $run = $fixture.run
    $consumerRun = $fixture.consumerRun
    $artifacts = @($fixture.artifacts)
}
else {
    $token = [Environment]::GetEnvironmentVariable($TokenEnvironmentVariable)
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw 'GitHub Actions API token is absent before OIDC.'
    }
    $headers = @{
        Accept = 'application/vnd.github+json'
        Authorization = "Bearer $token"
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    if ($null -eq $RequestInvoker) {
        $run = Invoke-RestMethod -Method Get `
            -Uri "https://api.github.com/repos/$($contract.repository)/actions/runs/$RunId" -Headers $headers
        $consumerRun = Invoke-RestMethod -Method Get `
            -Uri "https://api.github.com/repos/$($contract.repository)/actions/runs/$ConsumerRunId" -Headers $headers
        $artifactQuery = if ($isReleaseC6 -or $isMigrationAuthorizationDiscovery) {
            ''
        }
        else {
            "?name=$expectedName"
        }
        $artifactResponse = Invoke-RestMethod -Method Get `
            -Uri "https://api.github.com/repos/$($contract.repository)/actions/runs/$RunId/artifacts$artifactQuery" `
            -Headers $headers
    }
    else {
        $run = & $RequestInvoker -Method Get `
            -Uri "https://api.github.com/repos/$($contract.repository)/actions/runs/$RunId" -Headers $headers
        $consumerRun = & $RequestInvoker -Method Get `
            -Uri "https://api.github.com/repos/$($contract.repository)/actions/runs/$ConsumerRunId" -Headers $headers
        $artifactQuery = if ($isReleaseC6 -or $isMigrationAuthorizationDiscovery) {
            ''
        }
        else {
            "?name=$expectedName"
        }
        $artifactResponse = & $RequestInvoker -Method Get `
            -Uri "https://api.github.com/repos/$($contract.repository)/actions/runs/$RunId/artifacts$artifactQuery" `
            -Headers $headers
    }
    $artifacts = @($artifactResponse.artifacts)
    $fixture = $null
}

$runRef = if ([string]$run.head_branch -match '^refs/') {
    [string]$run.head_branch
}
else {
    "refs/heads/$([string]$run.head_branch)"
}
$expectedRunPath = if ($isReleaseC6) {
    [string]$role[0].workflowPath
}
else {
    [string]$expectedIdentity.topLevelPath
}
$selectedCreatedAt = Get-StrictUtc -Value ([string]$run.created_at) -Label 'GitHub run creation'
$selectedUpdatedAt = Get-StrictUtc -Value ([string]$run.updated_at) -Label 'GitHub run update'
$consumerCreatedAt = Get-StrictUtc -Value ([string]$consumerRun.created_at) -Label 'GitHub consumer run creation'
if ([string]$consumerRun.repository.full_name -cne [string]$contract.repository -or
    [string]$consumerRun.id -cne $ConsumerRunId -or
    [int]$consumerRun.run_attempt -lt 1 -or
    $RunId -ceq $ConsumerRunId -or
    $selectedUpdatedAt -gt $consumerCreatedAt) {
    throw 'GitHub Actions producer run is current, future, or not completed before the consumer run started.'
}
if ([string]$run.repository.full_name -cne [string]$contract.repository -or
    [string]$run.id -cne $RunId -or
    [string]$run.path -cne $expectedRunPath -or
    $runRef -cne [string]$contract.protectedRef -or
    [string]$run.head_sha -notmatch '^[a-f0-9]{40}$' -or
    [int]$run.run_attempt -lt 1 -or
    $selectedUpdatedAt -lt $selectedCreatedAt) {
    throw 'GitHub Actions run metadata does not satisfy the exact T21 API run binding.'
}
if (-not [bool]$role[0].requiresCompletedSuccess -or
    [string]$run.status -cne 'completed' -or [string]$run.conclusion -cne 'success') {
    throw 'GitHub Actions producer run is not completed successfully.'
}
$artifactMatches = @($artifacts | Where-Object {
    $isExpectedName = if ($isReleaseC6) {
        [string]$_.name -match '^husaynia-site-[0-9A-Za-z][0-9A-Za-z._-]{0,127}$'
    }
    elseif ($isMigrationAuthorizationDiscovery) {
        [string]$_.name -match
            '^migration-apply-authorization-(Development|Staging)-[a-f0-9]{64}-[a-f0-9]{64}$'
    }
    else {
        [string]$_.name -ceq $expectedName
    }
    $isExpectedName -and [string]$_.workflow_run.id -ceq $RunId
})
if ($artifactMatches.Count -ne 1 -or [bool]$artifactMatches[0].expired -or
    [string]$artifactMatches[0].id -notmatch '^[1-9][0-9]*$' -or
    [string]$artifactMatches[0].digest -notmatch '^sha256:[a-f0-9]{64}$' -or
    [int64]$artifactMatches[0].size_in_bytes -lt 1 -or
    (Get-StrictUtc -Value ([string]$artifactMatches[0].created_at) -Label 'GitHub artifact creation') `
        -gt [DateTimeOffset]::UtcNow.AddMinutes(5) -or
    (-not [string]::IsNullOrWhiteSpace([string]$artifactMatches[0].expires_at) -and
        (Get-StrictUtc -Value ([string]$artifactMatches[0].expires_at) -Label 'GitHub artifact expiry') `
            -le [DateTimeOffset]::UtcNow)) {
    throw 'GitHub Actions artifact is missing, duplicate, expired, or lacks an immutable SHA-256 digest.'
}

$discoveredMigrationStage = ''
$discoveredMigrationApplicationSha256 = ''
$discoveredMigrationPreflightEvidenceSha256 = ''
if ($isMigrationAuthorizationDiscovery) {
    if ([string]$artifactMatches[0].name -cnotmatch
        '^migration-apply-authorization-(Development|Staging)-([a-f0-9]{64})-([a-f0-9]{64})$') {
        throw 'Migration authorization artifact name is not canonical for nonproduction discovery.'
    }
    $discoveredMigrationStage = $Matches[1]
    $discoveredMigrationApplicationSha256 = $Matches[2]
    $discoveredMigrationPreflightEvidenceSha256 = $Matches[3]
}

$output = New-CleanDirectory -Path $OutputRoot
$archivePath = Join-Path $output 'artifact.zip'
if ($null -ne $fixture) {
    if ([string]::IsNullOrWhiteSpace([string]$artifactMatches[0].archivePath) -or
        -not (Test-Path -LiteralPath ([string]$artifactMatches[0].archivePath) -PathType Leaf)) {
        throw 'T21 provenance fixture archive is missing.'
    }
    Copy-Item -LiteralPath ([string]$artifactMatches[0].archivePath) -Destination $archivePath
}
else {
    if ($null -eq $RequestInvoker) {
        Invoke-WebRequest -Uri ([string]$artifactMatches[0].archive_download_url) `
            -Headers $headers -OutFile $archivePath
    }
    else {
        & $RequestInvoker -Method Get -Uri ([string]$artifactMatches[0].archive_download_url) `
            -Headers $headers -OutFile $archivePath
    }
}
$archiveSha = Get-Sha256Lower -Path $archivePath
if ($archiveSha -cne ([string]$artifactMatches[0].digest).Substring(7)) {
    throw 'GitHub Actions artifact archive digest changed before safe extraction.'
}
$artifactContainer = Join-Path $output 'artifact'
$extractionDestination = if ($isReleaseC6) {
    New-Item -ItemType Directory -Path $artifactContainer -Force | Out-Null
    Join-Path $artifactContainer ([string]$artifactMatches[0].name)
}
else {
    $artifactContainer
}
$artifactRoot = Expand-VerifiedArchive -ArchivePath $archivePath -Destination $extractionDestination
$archiveCreatedAtUtc = (Get-StrictUtc -Value ([string]$artifactMatches[0].created_at) `
    -Label 'GitHub artifact creation').ToString('O')
$archiveExpiresAtUtc = if ([string]::IsNullOrWhiteSpace([string]$artifactMatches[0].expires_at)) {
    ''
}
else {
    (Get-StrictUtc -Value ([string]$artifactMatches[0].expires_at) `
        -Label 'GitHub artifact expiry').ToString('O')
}

if ($isReleaseC6) {
    $releaseRoot = (Resolve-Path -LiteralPath $artifactRoot).Path
    if ((Split-Path -Leaf $releaseRoot) -cne [string]$artifactMatches[0].name -or
        @((Get-ChildItem -LiteralPath $artifactContainer -Force)).Count -ne 1 -or
        -not (Test-PathWithinDirectory -BasePath $artifactContainer -Path $releaseRoot)) {
        throw 'Release C6 archive was not materialized under exactly one API-selected canonical artifact root.'
    }
    $bundlePath = Join-Path $releaseRoot 'operations\protected-execution-bundle.zip'
    if (-not (Test-Path -LiteralPath $bundlePath -PathType Leaf) -or
        (Get-Sha256Lower -Path $bundlePath) -cne $BundleSha256) {
        throw 'Release C6 protected bundle does not match the caller-provided protected SHA-256 before release validation.'
    }
    if (-not [string]::IsNullOrWhiteSpace($TestOnlyReleaseValidatorMarkerPath)) {
        $markerParent = Split-Path -Parent $TestOnlyReleaseValidatorMarkerPath
        if (-not [string]::IsNullOrWhiteSpace($markerParent)) {
            New-Item -ItemType Directory -Path $markerParent -Force | Out-Null
        }
        [IO.File]::WriteAllText(
            $TestOnlyReleaseValidatorMarkerPath,
            "release-validator-invoked`n",
            [Text.UTF8Encoding]::new($false))
    }
    $effectiveApplicationSha256 = $ApplicationSha256
    if ($DiscoverReleaseIdentity -and [string]::IsNullOrWhiteSpace($effectiveApplicationSha256)) {
        $applicationPath = Join-Path $releaseRoot 'app\Husaynia.Web.zip'
        if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
            throw 'Release identity discovery could not locate the immutable application archive.'
        }
        $effectiveApplicationSha256 = Get-Sha256Lower -Path $applicationPath
    }
    & (Join-Path $PSScriptRoot '..\artifact\Test-ReleaseArtifact.ps1') `
        -ArtifactRoot $releaseRoot -ExpectedAppSha256 $effectiveApplicationSha256 -PolicyPath $PolicyPath
    if (-not $?) {
        throw 'Release C6 artifact did not pass the immutable release-manifest validator.'
    }
    $releaseManifestPath = Join-Path $releaseRoot 'release\release-manifest.json'
    $releaseManifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json -DateKind String
    $releaseManifestSha = Get-Sha256Lower -Path $releaseManifestPath
    $releaseCommit = ([string]$releaseManifest.commitSha).ToLowerInvariant()
    $bundleEntry = @($releaseManifest.files | Where-Object {
        [string]$_.path -ceq 'operations/protected-execution-bundle.zip'
    })
    if ($releaseCommit -cne [string]$run.head_sha -or
        $bundleEntry.Count -ne 1 -or
        [string]$bundleEntry[0].sha256 -cne $BundleSha256 -or
        (Get-Sha256Lower -Path $bundlePath) -cne $BundleSha256) {
        throw 'Release C6 manifest, commit, or protected-bundle binding does not match API-verified provenance.'
    }
    $releaseProvenance = [ordered]@{
        schemaVersion = '2.0.0'
        authority = 'github-actions-api-and-sigstore-v1'
        expectedRole = 'release-c6'
        run = [ordered]@{
            repository = [string]$run.repository.full_name
            workflowPath = [string]$run.path
            workflowRef = "$([string]$contract.repository)/$([string]$run.path)@$([string]$contract.protectedRef)"
            runId = $RunId
            runAttempt = [int]$run.run_attempt
            ref = $runRef
            commitSha = $releaseCommit
            status = [string]$run.status
            conclusion = [string]$run.conclusion
        }
        artifact = [ordered]@{
            id = [string]$artifactMatches[0].id
            name = [string]$artifactMatches[0].name
            archiveSha256 = $archiveSha
            sizeBytes = [int64]$artifactMatches[0].size_in_bytes
            createdAtUtc = $archiveCreatedAtUtc
            expiresAtUtc = $archiveExpiresAtUtc
        }
        contentManifestSha256 = $releaseManifestSha
        attestation = $null
    }
    Write-Utf8Json -Value $releaseProvenance -Path (Join-Path $output 'verified-provenance.json') -Depth 20
    if ($DiscoverReleaseIdentity) {
        Write-Utf8Json -Value ([ordered]@{
            schemaVersion = '1.0.0'
            releaseRunId = $RunId
            applicationSha256 = $effectiveApplicationSha256
            bundleSha256 = $BundleSha256
            releaseManifestSha256 = $releaseManifestSha
            releaseCommitSha = $releaseCommit
            artifactName = [string]$artifactMatches[0].name
        }) -Path (Join-Path $output 'discovered-release-identity.json') -Depth 10
    }
    Write-Output "VERIFIED-PROVENANCE status=PASS role=release-c6 runId=$RunId consumerRunId=$ConsumerRunId artifactId=$($artifactMatches[0].id) applicationSha256=$effectiveApplicationSha256 root=$releaseRoot"
    return
}

$manifestPath = Join-Path $artifactRoot 't21-producer-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'Verified producer artifact does not contain its canonical manifest.'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -DateKind String
$effectiveStage = $Stage
$effectiveApplicationSha256 = $ApplicationSha256
$effectiveReleaseManifestSha256 = $ReleaseManifestSha256
$effectiveReleaseCommitSha = $ReleaseCommitSha
if ($isMigrationAuthorizationDiscovery) {
    $effectiveStage = $discoveredMigrationStage
    $effectiveApplicationSha256 = $discoveredMigrationApplicationSha256
    $effectiveReleaseManifestSha256 = [string]$manifest.release.manifestSha256
    $effectiveReleaseCommitSha = [string]$manifest.release.commitSha
    if ([string]$manifest.stage -cne $effectiveStage -or
        [string]$manifest.applicationSha256 -cne $effectiveApplicationSha256 -or
        $effectiveReleaseManifestSha256 -notmatch '^[a-f0-9]{64}$' -or
        $effectiveReleaseManifestSha256 -ceq ('0' * 64) -or
        $effectiveReleaseCommitSha -notmatch '^[a-f0-9]{40}$' -or
        $effectiveReleaseCommitSha -ceq ('0' * 40) -or
        [string]$manifest.release.runId -notmatch '^[1-9][0-9]*$' -or
        [int]$manifest.release.runAttempt -lt 1) {
        throw 'Migration authorization discovery manifest has invalid immutable release or stage bindings.'
    }
}
$manifestArguments = @{
    Mode = 'Validate'
    ArtifactRoot = $artifactRoot
    ProducerRole = $ExpectedRole
    Stage = $effectiveStage
    ApplicationSha256 = $effectiveApplicationSha256
    BundleSha256 = $BundleSha256
    ReleaseManifestSha256 = $effectiveReleaseManifestSha256
    ReleaseCommitSha = $effectiveReleaseCommitSha
    ReleaseRunId = [string]$manifest.release.runId
    ReleaseRunAttempt = [int]$manifest.release.runAttempt
    ProducerRunId = $RunId
    ProducerRunAttempt = [int]$run.run_attempt
    ProducerCommitSha = ([string]$run.head_sha).ToLowerInvariant()
    TopLevelCallerWorkflowRef = $ExpectedTopLevelCallerWorkflowRef
    ProducerWorkflowRef = $ExpectedProducerWorkflowRef
    PolicyPath = $PolicyPath
}
& (Join-Path $PSScriptRoot 'New-T21ProducerManifest.ps1') @manifestArguments
if (-not $? -or
    [string]$manifest.producer.workflowPath -cne [string]$expectedIdentity.topLevelPath -or
    [string]$manifest.producer.workflowRef -cne $ExpectedTopLevelCallerWorkflowRef -or
    [string]$manifest.producer.runId -cne $RunId -or
    [int]$manifest.producer.runAttempt -ne [int]$run.run_attempt -or
    [string]$manifest.producer.commitSha -cne ([string]$run.head_sha).ToLowerInvariant() -or
    [string]$manifest.trustedExecution.topLevelCallerWorkflowRef -cne $ExpectedTopLevelCallerWorkflowRef -or
    [string]$manifest.trustedExecution.producerWorkflowRef -cne $ExpectedProducerWorkflowRef) {
    throw 'Signed producer content manifest does not bind the exact API run and expected identity pair.'
}

$attestation = $null
if ([bool]$role[0].requiresAttestation) {
    $signerWorkflow = $ExpectedProducerWorkflowRef.Split('@')[0]
    $signerDigest = $ExpectedProducerWorkflowRef.Split('@')[1]
    if ($null -ne $fixture) {
        $attestation = $fixture.attestation
        Assert-ExactProperties -Value $attestation -Expected @(
            'predicateType', 'signerDigest', 'signerWorkflow', 'sourceCommitSha', 'sourceRef', 'status'
        ) -Label 'T21 attestation fixture'
        if ([string]$attestation.status -cne 'verified' -or
            [string]$attestation.predicateType -cne [string]$contract.attestation.predicateType -or
            [string]$attestation.signerWorkflow -cne $signerWorkflow -or
            [string]$attestation.signerDigest -cne $signerDigest -or
            [string]$attestation.sourceRef -cne [string]$contract.protectedRef -or
            [string]$attestation.sourceCommitSha -cne ([string]$run.head_sha).ToLowerInvariant()) {
            throw 'Sigstore attestation fixture does not match the role-pinned signer and source identity.'
        }
    }
    else {
        $null = & gh attestation verify $manifestPath --repo $contract.repository `
            --signer-workflow $signerWorkflow --signer-digest $signerDigest `
            --source-ref $contract.protectedRef --source-digest ([string]$run.head_sha).ToLowerInvariant() `
            --predicate-type $contract.attestation.predicateType --deny-self-hosted-runners 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw 'GitHub/Sigstore attestation verification failed.'
        }
        $attestation = [ordered]@{
            predicateType = [string]$contract.attestation.predicateType
            signerWorkflow = $signerWorkflow
            signerDigest = $signerDigest
            sourceRef = [string]$contract.protectedRef
            sourceCommitSha = ([string]$run.head_sha).ToLowerInvariant()
            status = 'verified'
        }
    }
}
$provenance = [ordered]@{
    schemaVersion = '2.0.0'
    authority = 'github-actions-api-and-sigstore-v1'
    expectedRole = $ExpectedRole
    run = [ordered]@{
        repository = [string]$run.repository.full_name
        workflowPath = [string]$run.path
        workflowRef = "$([string]$contract.repository)/$([string]$run.path)@$([string]$contract.protectedRef)"
        runId = $RunId
        runAttempt = [int]$run.run_attempt
        ref = $runRef
        commitSha = ([string]$run.head_sha).ToLowerInvariant()
        status = [string]$run.status
        conclusion = [string]$run.conclusion
    }
    artifact = [ordered]@{
        id = [string]$artifactMatches[0].id
        name = [string]$artifactMatches[0].name
        archiveSha256 = $archiveSha
        sizeBytes = [int64]$artifactMatches[0].size_in_bytes
        createdAtUtc = $archiveCreatedAtUtc
        expiresAtUtc = $archiveExpiresAtUtc
    }
    contentManifestSha256 = Get-Sha256Lower -Path $manifestPath
    attestation = $attestation
}
Write-Utf8Json -Value $provenance -Path (Join-Path $output 'verified-provenance.json') -Depth 20
if ($isMigrationAuthorizationDiscovery) {
    Write-Utf8Json -Value ([ordered]@{
        stage = $effectiveStage
        applicationSha256 = $effectiveApplicationSha256
        preflightEvidenceSha256 = $discoveredMigrationPreflightEvidenceSha256
        releaseManifestSha256 = $effectiveReleaseManifestSha256
        releaseCommitSha = $effectiveReleaseCommitSha
        releaseRunId = [string]$manifest.release.runId
        releaseRunAttempt = [int]$manifest.release.runAttempt
        artifactName = [string]$artifactMatches[0].name
    }) -Path (Join-Path $output 'discovered-migration-authorization-identity.json') -Depth 10
}
Write-Output "VERIFIED-PROVENANCE status=PASS role=$ExpectedRole runId=$RunId consumerRunId=$ConsumerRunId artifactId=$($artifactMatches[0].id) root=$artifactRoot"
