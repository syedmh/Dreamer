[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [Parameter(Mandatory = $true)]
    [string]$ArtifactVersion,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{40}$')]
    [string]$CommitSha,
    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,
    [switch]$Ci,
    [switch]$AuthoringValidation,
    [switch]$AllowKnownNuGetTlsFailure,
    [string]$BicepPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Resolve-HusayniaRepositoryRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
if ($AllowKnownNuGetTlsFailure -and -not $AuthoringValidation) {
    throw '-AllowKnownNuGetTlsFailure is restricted to explicit local authoring validation.'
}
$reportsRoot = if ($env:RUNNER_TEMP) {
    Join-Path $env:RUNNER_TEMP 't21-reports'
}
else {
    Join-Path ([IO.Path]::GetTempPath()) "husaynia-t21-reports-$([guid]::NewGuid().ToString('N'))"
}
New-CleanDirectory -Path $reportsRoot | Out-Null
New-Item -ItemType Directory -Path (Join-Path $reportsRoot 'accessibility') -Force | Out-Null

Invoke-CheckedScript -Path (Join-Path $PSScriptRoot 'Invoke-WorkflowSchemaValidation.ps1') `
    -Parameters @{ RepositoryRoot = $RepositoryRoot } `
    -Label 'validate workflow schemas'
Invoke-CheckedScript -Path (Join-Path $PSScriptRoot 'Test-PipelineDefinitions.ps1') `
    -Parameters @{ RepositoryRoot = $RepositoryRoot } `
    -Label 'validate pipeline definitions'

Invoke-CheckedScript -Path (Join-Path $PSScriptRoot 'Invoke-DotNetValidation.ps1') `
    -Parameters @{
        RepositoryRoot = $RepositoryRoot
        ReportsRoot = $reportsRoot
        AllowKnownNuGetTlsFailure = [bool]$AllowKnownNuGetTlsFailure
        AuthoringValidation = [bool]$AuthoringValidation
    } `
    -Label 'validate .NET solution'

$secretScanArguments = @{
    RepositoryRoot = $RepositoryRoot
    ReportPath = (Join-Path $reportsRoot 'security\secret-scan.json')
}
if ($AuthoringValidation) {
    $secretScanArguments.ScanPaths = @('pipelines', 'eng', '.config')
}
Invoke-CheckedScript -Path (Join-Path $RepositoryRoot 'eng\security\Invoke-SecretScan.ps1') `
    -Parameters $secretScanArguments `
    -Label 'scan repository for secrets'

$requiredFiles = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'pipelines\config\required-files.json') -Raw | ConvertFrom-Json
$contractGate = @($requiredFiles.gates | Where-Object { $_.name -eq 'contract' })[0]
Invoke-CheckedScript -Path (Join-Path $PSScriptRoot 'Invoke-RequiredFileGate.ps1') `
    -Parameters @{
        GateName = 'contract'
        RequiredPaths = @($contractGate.requiredPaths)
        RepositoryRoot = $RepositoryRoot
        ReportPath = (Join-Path $reportsRoot 'test-results\contract-required-files.json')
    } `
    -Label 'validate required contract files'

$accessibilityGate = @($requiredFiles.gates | Where-Object { $_.name -eq 'accessibility' })[0]
$accessibilityReport = Join-Path $reportsRoot 'accessibility\required-files.json'
if ($AuthoringValidation) {
    $pwsh = (Get-Process -Id $PID).Path
    $requiredPathsJson = @($accessibilityGate.requiredPaths) | ConvertTo-Json -Compress
    & $pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-RequiredFileGate.ps1') `
        -GateName 'accessibility' `
        -RequiredPathsJson $requiredPathsJson `
        -RepositoryRoot $RepositoryRoot `
        -ReportPath $accessibilityReport
    $accessibilityExit = $LASTEXITCODE
    if ($accessibilityExit -eq 0) {
        throw 'Authoring validation expected the unfinished accessibility required-file gate to fail.'
    }

    $failedReport = Get-Content -LiteralPath $accessibilityReport -Raw | ConvertFrom-Json
    if ($failedReport.status -ne 'FAIL') {
        throw 'Accessibility required-file gate did not produce explicit failure evidence.'
    }
    Write-Utf8Json -Value ([ordered]@{
        schemaVersion = '1.0.0'
        status = 'PASS'
        scope = 'pipeline-wiring-only'
        claim = 'The required-file gate rejected the absent future accessibility project; no accessibility compliance result was fabricated.'
        observedGateStatus = $failedReport.status
        observedExitCode = $accessibilityExit
    }) -Path (Join-Path $reportsRoot 'accessibility\t21-gate-wiring-validation.json')
    Write-Output 'PASS  accessibility future-project gate rejected absence during authoring validation.'
}
else {
    Invoke-CheckedScript -Path (Join-Path $PSScriptRoot 'Invoke-RequiredFileGate.ps1') `
        -Parameters @{
            GateName = 'accessibility'
            RequiredPaths = @($accessibilityGate.requiredPaths)
            RepositoryRoot = $RepositoryRoot
            ReportPath = $accessibilityReport
        } `
        -Label 'validate required accessibility files'

    $accessibilityProject = Join-Path $RepositoryRoot 'tests\Husaynia.AccessibilityTests\Husaynia.AccessibilityTests.csproj'
    Invoke-CheckedNative -FilePath 'dotnet' -Arguments @(
        'restore', $accessibilityProject,
        '--locked-mode',
        '--nologo',
        '-p:NuGetAudit=true',
        '-p:NuGetAuditMode=all',
        '-warnaserror'
    ) -Label 'restore and audit required accessibility project'
    Invoke-CheckedNative -FilePath 'dotnet' -Arguments @(
        'test', $accessibilityProject,
        '--configuration', 'Release',
        '--no-restore',
        '--nologo',
        '--logger', 'trx;LogFileName=accessibility.trx',
        '--results-directory', (Join-Path $reportsRoot 'accessibility')
    ) -Label 'execute required accessibility project'
}

if ([string]::IsNullOrWhiteSpace($BicepPath)) {
    Invoke-CheckedScript -Path (Join-Path $PSScriptRoot 'Invoke-BicepValidation.ps1') `
        -Parameters @{
            RepositoryRoot = $RepositoryRoot
            InstallToTemporaryDirectory = $true
        } `
        -Label 'validate Bicep baseline'
}
else {
    Invoke-CheckedScript -Path (Join-Path $PSScriptRoot 'Invoke-BicepValidation.ps1') `
        -Parameters @{
            RepositoryRoot = $RepositoryRoot
            BicepPath = $BicepPath
        } `
        -Label 'validate Bicep baseline'
}

Invoke-CheckedScript -Path (Join-Path $RepositoryRoot 'eng\artifact\New-ReleaseArtifact.ps1') `
    -Parameters @{
        RepositoryRoot = $RepositoryRoot
        Version = $ArtifactVersion
        CommitSha = $CommitSha
        OutputRoot = $OutputRoot
        ReportsRoot = $reportsRoot
        AuthoringValidation = [bool]$AuthoringValidation
    } `
    -Label 'create immutable release artifact'

Write-Output "PR-VALIDATION status=PASS authoring=$AuthoringValidation artifactVersion=$ArtifactVersion reports=$reportsRoot"
