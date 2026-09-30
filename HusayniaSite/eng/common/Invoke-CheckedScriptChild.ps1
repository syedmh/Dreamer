[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TargetPath,
    [Parameter(Mandatory = $true)]
    [string]$ParametersPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

try {
    if (-not (Test-Path -LiteralPath $TargetPath -PathType Leaf)) {
        throw "Script is missing: $TargetPath"
    }
    if (-not (Test-Path -LiteralPath $ParametersPath -PathType Leaf)) {
        throw 'Serialized script parameters are missing.'
    }

    $parameters = Import-Clixml -LiteralPath $ParametersPath
    if ($parameters -isnot [Collections.IDictionary]) {
        throw 'Serialized script parameters are not a dictionary.'
    }

    & $TargetPath @parameters
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
