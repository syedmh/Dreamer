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
    "GODOT_SELECTED source=$($godot.Source) version=$($godot.Version) " +
    "executable=$($godot.Executable) sha256=$($godot.Sha256)"
)
& $godot.Executable @("--path", $projectRoot)
exit $LASTEXITCODE
