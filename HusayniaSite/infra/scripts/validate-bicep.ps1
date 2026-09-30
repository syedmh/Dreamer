[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BicepPath,

    [string]$InfraRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$infra = (Resolve-Path $InfraRoot).Path
$bicepCommand = if (Test-Path -LiteralPath $BicepPath) {
    (Resolve-Path -LiteralPath $BicepPath).Path
}
else {
    (Get-Command $BicepPath -ErrorAction Stop).Source
}

$outputRoot = Join-Path $infra '.validation-output'
$passed = 0
$failed = 0

function Invoke-CheckedBicep {
    param(
        [string]$Label,
        [string[]]$Arguments
    )

    $diagnostics = @(& $bicepCommand @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0 -and $diagnostics.Count -eq 0) {
        $script:passed++
        Write-Output "PASS  $Label"
        return
    }

    $script:failed++
    Write-Output "FAIL  $Label :: exit=$exitCode diagnostics=$($diagnostics.Count)"
    $diagnostics | ForEach-Object { Write-Output "      $_" }
}

try {
    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

    $bicepFiles = Get-ChildItem -Path $infra -Recurse -File -Filter '*.bicep' |
        Where-Object { $_.FullName -notlike "$outputRoot*" -and $_.FullName -notlike '*\.validation-tools\*' } |
        Sort-Object FullName

    foreach ($file in $bicepFiles) {
        $relative = $file.FullName.Substring($infra.Length + 1)
        $safeName = $relative -replace '[\\/:*?"<>|]', '_'
        $outputFile = Join-Path $outputRoot "$safeName.json"
        Invoke-CheckedBicep "build $relative" @('build', $file.FullName, '--outfile', $outputFile)
        Invoke-CheckedBicep "lint $relative" @('lint', $file.FullName)
    }

    $parameterFiles = Get-ChildItem -Path (Join-Path $infra 'parameters') -File -Filter '*.bicepparam' |
        Sort-Object FullName

    foreach ($file in $parameterFiles) {
        $relative = $file.FullName.Substring($infra.Length + 1)
        $safeName = $relative -replace '[\\/:*?"<>|]', '_'
        $outputFile = Join-Path $outputRoot "$safeName.json"
        Invoke-CheckedBicep "build-params $relative" @('build-params', $file.FullName, '--outfile', $outputFile)
    }
}
finally {
    if (Test-Path -LiteralPath $outputRoot) {
        Remove-Item -LiteralPath $outputRoot -Recurse -Force
    }
}

Write-Output "SUMMARY total=$($passed + $failed) passed=$passed failed=$failed"
if ($failed -gt 0) {
    exit 1
}
