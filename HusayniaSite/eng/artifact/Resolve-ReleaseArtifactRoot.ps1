[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DownloadRoot,
    [Parameter(Mandatory = $true)]
    [string]$ArtifactLeaf,
    [Parameter(Mandatory = $true)]
    [string]$ReleaseArtifactName,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ExpectedAppSha256,
    [Parameter(Mandatory = $true)]
    [string]$ReleaseRunMetadataPath,
    [string]$PolicyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

Resolve-TrustedReleaseArtifactRoot `
    -DownloadRoot $DownloadRoot `
    -ArtifactLeaf $ArtifactLeaf `
    -ReleaseArtifactName $ReleaseArtifactName `
    -ExpectedAppSha256 $ExpectedAppSha256 `
    -ReleaseRunMetadataPath $ReleaseRunMetadataPath `
    -PolicyPath $PolicyPath
