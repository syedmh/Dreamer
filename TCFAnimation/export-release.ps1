$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "release-tooling.ps1")

function Write-GodotResolutionFailure
{
    param([System.Exception] $Exception)

    $reason = [string]$Exception.Data["Reason"]
    if ([string]::IsNullOrEmpty($reason))
    {
        $reason = "provenance_invalid"
    }
    $source = [string]$Exception.Data["Source"]
    $executable = [string]$Exception.Data["Executable"]
    Write-Output (
        "GODOT_RESOLUTION_FAIL reason=$reason source=$source " +
        "executable=$executable"
    )
}

function Write-ReleaseExportFailure
{
    param([string] $Reason)

    Write-Output "RELEASE_EXPORT_FAIL reason=$Reason"
}

function Get-GuardedBuildRoot
{
    param([string] $ProjectRoot)

    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $build = [IO.Path]::GetFullPath((Join-Path $root "Build"))
    $expected = $root + [IO.Path]::DirectorySeparatorChar + "Build"
    if (![string]::Equals(
        $build,
        $expected,
        [StringComparison]::OrdinalIgnoreCase
    ))
    {
        throw "Build root guard failed."
    }
    return $build
}

function Reset-GuardedBuildRoot
{
    param([string] $ProjectRoot, [string] $BuildRoot)

    $expected = Get-GuardedBuildRoot -ProjectRoot $ProjectRoot
    if (![string]::Equals(
        $expected,
        [IO.Path]::GetFullPath($BuildRoot),
        [StringComparison]::OrdinalIgnoreCase
    ))
    {
        throw "Build root guard failed."
    }
    if (Test-Path -LiteralPath $BuildRoot)
    {
        $item = Get-Item -LiteralPath $BuildRoot -Force -ErrorAction Stop
        if (
            $item -isnot [IO.DirectoryInfo] -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        )
        {
            throw "Build root is not a regular directory."
        }
        Remove-Item `
            -LiteralPath $BuildRoot `
            -Recurse `
            -Force `
            -ErrorAction Stop
    }
    New-Item `
        -ItemType Directory `
        -Path $BuildRoot `
        -ErrorAction Stop | Out-Null
}

$root = [IO.Path]::GetFullPath($PSScriptRoot)
$buildDirectory = Get-GuardedBuildRoot -ProjectRoot $root
try
{
    $godot = Get-ApprovedGodot -ProjectRoot $root -Purpose Export
}
catch
{
    Write-GodotResolutionFailure -Exception $_.Exception
    exit 1
}
Write-Output (
    "GODOT_SELECTED source=$($godot.Source) version=$($godot.Version) " +
    "executable=$($godot.Executable) sha256=$($godot.Sha256)"
)

$output = [IO.Path]::GetFullPath(
    (Join-Path $buildDirectory "TCFAnimation.exe")
)
$managedOutput = [IO.Path]::GetFullPath(
    (Join-Path $buildDirectory (
        "data_TCFAnimation_windows_x86_64\TCFAnimation.dll"
    ))
)
$stage = $null
$failureReason = ""
$exitCode = 0
$cleanupFailed = $false
$solution = $null
$importResult = $null
$exportResult = $null
try
{
    try
    {
        Reset-GuardedBuildRoot `
            -ProjectRoot $root `
            -BuildRoot $buildDirectory
    }
    catch
    {
        New-ReleaseToolingFailure -Reason "stage_create_failed"
    }
    $manifest = Read-ReleaseStageManifest `
        -ProjectRoot $root `
        -LiteralPath (Join-Path $root "release-stage-manifest.txt")
    $stage = New-ReleaseStage `
        -ProjectRoot $root `
        -BuildRoot $buildDirectory `
        -ManifestEntries $manifest
    $solution = New-ReleaseStageSolution `
        -BuildRoot $buildDirectory `
        -StageRoot $stage.StageRoot `
        -StageProjectRoot $stage.StageProjectRoot `
        -ManifestEntries $manifest
    Write-Output (
        "RELEASE_STAGE_READY seed_files=$($manifest.Count) " +
        "preimport_files=$($manifest.Count + 1) " +
        "solution_length=$($solution.Length) " +
        "solution_sha256=$($solution.Sha256)"
    )

    $importResult = Invoke-ApprovedGodotProcess `
        -Executable $godot.Executable `
        -Arguments @(
            "--headless",
            "--path",
            $stage.StageProjectRoot,
            "--import"
        )
    if (
        $importResult.ExitCode -ne 0 -or
        $importResult.ErrorCount -ne 0
    )
    {
        $failureReason = "stage_import_failed"
        $exitCode = if ($importResult.ExitCode -ne 0)
        {
            $importResult.ExitCode
        }
        else
        {
            1
        }
    }
    else
    {
        $exportResult = Invoke-ApprovedGodotProcess `
            -Executable $godot.Executable `
            -Arguments @(
                "--headless",
                "--path",
                $stage.StageProjectRoot,
                "--export-release",
                "Windows Desktop",
                $output
            )
        if (
            $exportResult.ExitCode -ne 0 -or
            $exportResult.ErrorCount -ne 0
        )
        {
            $failureReason = "stage_export_failed"
            $exitCode = if ($exportResult.ExitCode -ne 0)
            {
                $exportResult.ExitCode
            }
            else
            {
                1
            }
        }
        else
        {
            foreach ($requiredOutput in @($output, $managedOutput))
            {
                $outputFile = Get-Item `
                    -LiteralPath $requiredOutput `
                    -Force `
                    -ErrorAction SilentlyContinue
                if (
                    $null -eq $outputFile -or
                    $outputFile -isnot [IO.FileInfo] -or
                    ($outputFile.Attributes -band
                        [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                    $outputFile.Length -le 0
                )
                {
                    $failureReason = "missing_output"
                    $exitCode = 1
                    break
                }
            }
        }
    }
}
catch [System.InvalidOperationException]
{
    $failureReason = [string]$_.Exception.Data["Reason"]
    if ([string]::IsNullOrEmpty($failureReason))
    {
        $failureReason = "stage_create_failed"
    }
    $exitCode = 1
}
catch
{
    $failureReason = "stage_create_failed"
    $exitCode = 1
}
finally
{
    if ($null -ne $stage)
    {
        try
        {
            Remove-ReleaseStage `
                -BuildRoot $buildDirectory `
                -StageRoot $stage.StageRoot
        }
        catch
        {
            $cleanupFailed = $true
        }
    }
    try
    {
        $remainingStages = @(
            Get-ChildItem `
                -LiteralPath $buildDirectory `
                -Force `
                -Directory `
                -Filter ".release-stage-*" `
                -ErrorAction Stop
        )
        if ($remainingStages.Count -ne 0)
        {
            $cleanupFailed = $true
        }
    }
    catch
    {
        $cleanupFailed = $true
    }
    if ($cleanupFailed)
    {
        $failureReason = "stage_cleanup_failed"
        $exitCode = 1
        try
        {
            Reset-GuardedBuildRoot `
                -ProjectRoot $root `
                -BuildRoot $buildDirectory
        }
        catch
        {
        }
    }
    elseif (![string]::IsNullOrEmpty($failureReason))
    {
        try
        {
            Reset-GuardedBuildRoot `
                -ProjectRoot $root `
                -BuildRoot $buildDirectory
        }
        catch
        {
            $failureReason = "stage_cleanup_failed"
            $exitCode = 1
        }
    }
}

if (![string]::IsNullOrEmpty($failureReason))
{
    Write-ReleaseExportFailure -Reason $failureReason
    exit $exitCode
}

$finalOutput = Get-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue
$finalManagedOutput = Get-Item `
    -LiteralPath $managedOutput `
    -Force `
    -ErrorAction SilentlyContinue
if (
    $null -eq $finalOutput -or
    $finalOutput -isnot [IO.FileInfo] -or
    ($finalOutput.Attributes -band
        [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    $finalOutput.Length -le 0 -or
    $null -eq $finalManagedOutput -or
    $finalManagedOutput -isnot [IO.FileInfo] -or
    ($finalManagedOutput.Attributes -band
        [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    $finalManagedOutput.Length -le 0
)
{
    try
    {
        Reset-GuardedBuildRoot `
            -ProjectRoot $root `
            -BuildRoot $buildDirectory
    }
    catch
    {
    }
    Write-ReleaseExportFailure -Reason "missing_output"
    exit 1
}

Write-Output (
    "RELEASE_EXPORT_PASS output=$output clean_build=true " +
    "godot_version=$($godot.Version) isolated_stage=true " +
    "import_exit=$($importResult.ExitCode) " +
    "import_errors=$($importResult.ErrorCount) " +
    "export_exit=$($exportResult.ExitCode) " +
    "export_errors=$($exportResult.ErrorCount) " +
    "exe_bytes=$($finalOutput.Length) " +
    "managed_dll_bytes=$($finalManagedOutput.Length)"
)
