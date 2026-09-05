param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release"
)

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

$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
if ($Configuration -ieq "Release")
{
    $releaseExecutable =
        Join-Path $projectRoot "Build\TCFAnimation.exe"
    if (!(Test-Path -LiteralPath $releaseExecutable -PathType Leaf))
    {
        Write-Output (
            "RUN_ANIMATION_FAIL mode=release reason=release_not_built " +
            "path=$releaseExecutable"
        )
        exit 1
    }

    Write-Output (
        "RUN_ANIMATION_SELECTED mode=release " +
        "executable=$releaseExecutable"
    )
    & $releaseExecutable
    exit $LASTEXITCODE
}

try
{
    $godot = Get-ApprovedGodot -ProjectRoot $projectRoot -Purpose Run
}
catch
{
    Write-GodotResolutionFailure -Exception $_.Exception
    exit 1
}

Write-Output (
    "RUN_ANIMATION_SELECTED mode=debug " +
    "source=$($godot.Source) version=$($godot.Version) " +
    "executable=$($godot.Executable) sha256=$($godot.Sha256)"
)
& $godot.Executable @("--path", $projectRoot)
exit $LASTEXITCODE
