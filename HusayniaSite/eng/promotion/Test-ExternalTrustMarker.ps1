[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Claims')]
    [string]$ClaimsPath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Claims')]
    [ValidateSet('Development', 'Staging', 'Production')]
    [string]$Stage,
    [Parameter(Mandatory = $true, ParameterSetName = 'Claims')]
    [string]$ExpectedWorkflowRef,
    [Parameter(Mandatory = $true, ParameterSetName = 'Claims')]
    [string]$ExpectedSubjectsJson,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ExpectedBundleSha256,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ActualBundleSha256,
    [Parameter(Mandatory = $true, ParameterSetName = 'BundleOnly')]
    [switch]$ValidateBundleOnly,
    [string]$PolicyPath,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

if ($ValidateBundleOnly) {
    if ($ExpectedBundleSha256.ToLowerInvariant() -cne $ActualBundleSha256.ToLowerInvariant()) {
        throw 'External trusted bundle SHA-256 does not match the validated C6 bundle.'
    }
    Write-Output "EXTERNAL-TRUST-BUNDLE status=PASS bundleSha256=$($ActualBundleSha256.ToLowerInvariant())"
    return
}

function Get-Claim {
    param($Claims, [string]$Name)
    $property = $Claims.PSObject.Properties[$Name]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "GitHub OIDC claim is missing: $Name"
    }
    return [string]$property.Value
}

function Assert-ExactPropertySet {
    param($Value, [string[]]$Expected, [string]$Label)
    if ($null -eq $Value -or @(Compare-Object ($Expected | Sort-Object) @(
            $Value.PSObject.Properties.Name | Sort-Object)).Count -ne 0) {
        throw "$Label does not exactly match the external trust contract."
    }
}

function Test-ExactOrdinalStringSet {
    param([object[]]$Actual, [string[]]$Expected)

    if ($Actual.Count -ne $Expected.Count) {
        return $false
    }
    $actualSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($value in $Actual) {
        if ($value -isnot [string] -or
            [string]::IsNullOrWhiteSpace([string]$value) -or
            -not $actualSet.Add([string]$value)) {
            return $false
        }
    }
    $expectedSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($value in $Expected) {
        if ([string]::IsNullOrWhiteSpace($value) -or -not $expectedSet.Add($value)) {
            return $false
        }
    }
    if ($actualSet.Count -ne $expectedSet.Count) {
        return $false
    }
    foreach ($value in $expectedSet) {
        if (-not $actualSet.Contains($value)) {
            return $false
        }
    }
    return $true
}

if ([string]::IsNullOrWhiteSpace($PolicyPath)) {
    $PolicyPath = Join-Path (Resolve-HusayniaRepositoryRoot) 'pipelines\config\promotion-policy.json'
}
if (-not (Test-Path -LiteralPath $ClaimsPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $PolicyPath -PathType Leaf)) {
    throw 'GitHub OIDC claims or immutable bundle policy are missing.'
}
if ($ExpectedWorkflowRef -notmatch
    '^syedmh/Dreamer/\.github/workflows/trusted-protected-operations\.yml@([a-f0-9]{40})$' -or
    $Matches[1] -eq ('0' * 40)) {
    throw 'External trusted workflow ref is absent, malformed, or uses the all-zero sentinel.'
}
$expectedWorkflowSha = $Matches[1]
if ($ExpectedBundleSha256.ToLowerInvariant() -cne $ActualBundleSha256.ToLowerInvariant()) {
    throw 'External trusted bundle SHA-256 does not match the validated C6 bundle.'
}
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json -DateKind String
$contract = $policy.trustedExecutionContract
Assert-ExactPropertySet -Value $contract -Expected @(
    'bundlePath', 'externalBundleSha256VariableName', 'externalSubjectsVariableName',
    'externalWorkflowRefVariableName', 'schemaVersion', 'workflowPath'
) -Label 'Trusted execution policy'
if ([string]$contract.schemaVersion -cne '2.0.0' -or
    [string]$contract.workflowPath -cne '.github/workflows/trusted-protected-operations.yml' -or
    [string]$contract.bundlePath -cne 'operations/protected-execution-bundle.zip') {
    throw 'Trusted execution policy contract is invalid.'
}
$provenance = $policy.t21ProvenanceContract
if ($null -eq $provenance -or [string]$provenance.schemaVersion -cne '2.1.0' -or
    [string]$provenance.repository -cne 'syedmh/Dreamer') {
    throw 'T21 caller matrix is absent or not v2.'
}
try {
    $subjects = @($ExpectedSubjectsJson | ConvertFrom-Json -DateKind String)
}
catch {
    throw 'External exact-subject marker JSON is malformed.'
}
$claims = Get-Content -LiteralPath $ClaimsPath -Raw | ConvertFrom-Json -DateKind String
$requiredClaims = @(
    'iss', 'aud', 'sub', 'repository', 'repository_id', 'repository_owner_id', 'ref', 'environment',
    'workflow_ref', 'workflow_sha', 'job_workflow_ref', 'job_workflow_sha'
)
foreach ($required in $requiredClaims) {
    $null = Get-Claim -Claims $claims -Name $required
}
$context = Get-Claim -Claims $claims -Name 'environment'
$callerMatrix = @($provenance.callerMatrix)
$callerRows = @($callerMatrix | Where-Object {
    [string]$_.stage -ceq $Stage -and
    [string]$_.context -ceq $context -and
    [string]$_.workflowRef -ceq (Get-Claim -Claims $claims -Name 'workflow_ref')
})
if ($callerRows.Count -ne 1) {
    throw 'GitHub OIDC caller workflow/context is not in the exact T21 caller matrix.'
}
$repositoryId = Get-Claim -Claims $claims -Name 'repository_id'
$ownerId = Get-Claim -Claims $claims -Name 'repository_owner_id'
$callerRef = Get-Claim -Claims $claims -Name 'workflow_ref'
$approvedSubjects = @($callerMatrix | ForEach-Object {
        $rowContext = [string]$_.context
        $rowWorkflowRef = [string]$_.workflowRef
        if ([string]::IsNullOrWhiteSpace($rowContext) -or
            [string]::IsNullOrWhiteSpace($rowWorkflowRef)) {
            throw 'T21 caller matrix contains an unrenderable approved subject row.'
        }
        "repo:syedmh@${ownerId}/Dreamer@${repositoryId}:environment:${rowContext}" +
            ":workflow_ref:${rowWorkflowRef}:job_workflow_ref:${ExpectedWorkflowRef}"
    })
if ($callerMatrix.Count -ne 3 -or
    -not (Test-ExactOrdinalStringSet -Actual $approvedSubjects -Expected $approvedSubjects)) {
    throw 'T21 caller matrix does not render exactly three unique approved subjects.'
}
$expectedSubject = "repo:syedmh@${ownerId}/Dreamer@${repositoryId}:environment:${context}" +
    ":workflow_ref:${callerRef}:job_workflow_ref:${ExpectedWorkflowRef}"
$failures = [Collections.Generic.List[string]]::new()
if ((Get-Claim -Claims $claims -Name 'iss') -cne 'https://token.actions.githubusercontent.com') { $failures.Add('issuer') }
if ((Get-Claim -Claims $claims -Name 'aud') -cne 'api://AzureADTokenExchange') { $failures.Add('audience') }
if ((Get-Claim -Claims $claims -Name 'repository') -cne 'syedmh/Dreamer') { $failures.Add('repository') }
if ($repositoryId -notmatch '^[1-9][0-9]*$' -or $ownerId -notmatch '^[1-9][0-9]*$') { $failures.Add('immutable-ids') }
if ((Get-Claim -Claims $claims -Name 'ref') -cne 'refs/heads/main') { $failures.Add('ref') }
if ((Get-Claim -Claims $claims -Name 'workflow_sha') -notmatch '^[a-f0-9]{40}$') { $failures.Add('caller-sha') }
if ((Get-Claim -Claims $claims -Name 'job_workflow_ref') -cne $ExpectedWorkflowRef) { $failures.Add('reusable-ref') }
if ((Get-Claim -Claims $claims -Name 'job_workflow_sha') -cne $expectedWorkflowSha) { $failures.Add('reusable-sha') }
if ((Get-Claim -Claims $claims -Name 'sub') -cne $expectedSubject) { $failures.Add('rendered-subject') }
if (-not (Test-ExactOrdinalStringSet -Actual $subjects -Expected $approvedSubjects)) {
    $failures.Add('external-subject-marker')
}
if ($failures.Count -ne 0) {
    throw "GitHub OIDC identity does not match the exact caller, reusable workflow, repository, and protected-context contract: $($failures -join ',')."
}
$marker = [ordered]@{
    schemaVersion = '2.0.0'
    status = 'PASS'
    stage = $Stage
    subject = $expectedSubject
    callerWorkflowRef = $callerRef
    reusableWorkflowRef = $ExpectedWorkflowRef
    reusableWorkflowSha = $expectedWorkflowSha
    bundleSha256 = $ActualBundleSha256.ToLowerInvariant()
    verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    Write-Utf8Json -Value $marker -Path $OutputPath -Depth 12
}
Write-Output "EXTERNAL-TRUST-MARKER status=PASS stage=$Stage caller=$callerRef workflowRef=$ExpectedWorkflowRef bundleSha256=$($ActualBundleSha256.ToLowerInvariant())"
