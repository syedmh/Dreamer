[CmdletBinding()]
param(
    [ValidateRange(0, [int]::MaxValue)]
    [int]$Passed,
    [ValidateRange(0, [int]::MaxValue)]
    [int]$Failed,
    [ValidateRange(0, [int]::MaxValue)]
    [int]$Blocked,
    [switch]$ContractOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Failed -gt 0) {
    Write-Output "VALIDATION-STATUS status=FAIL passed=$Passed failed=$Failed blocked=$Blocked contractOnly=$([bool]$ContractOnly)"
    exit 1
}
if ($Blocked -gt 0) {
    if ($ContractOnly) {
        Write-Output "VALIDATION-STATUS status=PASS_WITH_ENVIRONMENTAL_BLOCKS passed=$Passed failed=0 blocked=$Blocked contractOnly=true"
        exit 0
    }
    Write-Output "VALIDATION-STATUS status=BLOCKED_ENVIRONMENT passed=$Passed failed=0 blocked=$Blocked contractOnly=false"
    exit 2
}

Write-Output "VALIDATION-STATUS status=PASS passed=$Passed failed=0 blocked=0 contractOnly=$([bool]$ContractOnly)"
exit 0
