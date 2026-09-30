$ErrorActionPreference = "Stop"

$project = "C:\Users\syedhu\source\repos\Dreamer\TCFAnimation"
$exe = Join-Path $project "Build\TCFAnimation.exe"
$captureDirectory = Join-Path $project "Build\Captures"
$capturePath = Join-Path $captureDirectory "rc2-positive.png"
$evidenceCapture = Join-Path $PSScriptRoot "after\capture-positive.png"
$resultsPath = Join-Path $PSScriptRoot "capture-matrix.json"

function Invoke-CaptureCase
{
    param(
        [string] $Name,
        [string[]] $UserArguments,
        [bool] $Headless
    )

    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $exe
    $psi.WorkingDirectory = "C:\Windows"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    if ($Headless)
    {
        $null = $psi.ArgumentList.Add("--headless")
    }
    $null = $psi.ArgumentList.Add("--")
    foreach ($argument in $UserArguments)
    {
        $null = $psi.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $psi
    if (!$process.Start())
    {
        throw "Could not start capture case $Name."
    }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(30000))
    {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
        throw "Capture case $Name timed out."
    }

    return [pscustomobject]@{
        name = $Name
        exitCode = $process.ExitCode
        stdout = $stdoutTask.GetAwaiter().GetResult()
        stderr = $stderrTask.GetAwaiter().GetResult()
    }
}

if (!(Test-Path -LiteralPath $exe -PathType Leaf))
{
    throw "Missing exported executable: $exe"
}
if (Test-Path -LiteralPath $capturePath)
{
    Remove-Item -LiteralPath $capturePath -Force
}

$results = New-Object System.Collections.Generic.List[object]
$positive = Invoke-CaptureCase `
    -Name "positive" `
    -UserArguments @(
        "--capture-frame=0",
        "--capture-direction=left",
        "--capture-path=rc2-positive.png"
    ) `
    -Headless $false
$positivePresent = Test-Path -LiteralPath $capturePath -PathType Leaf
$positiveBytes = if ($positivePresent)
{
    (Get-Item -LiteralPath $capturePath).Length
}
else
{
    0
}
$positivePassed = (
    $positive.exitCode -eq 0 -and
    $positive.stdout.Contains("CAPTURE_SAVED") -and
    $positive.stdout.Contains(
        (Join-Path $project "Build\Captures\rc2-positive.png")) -and
    $positivePresent -and
    $positiveBytes -gt 0
)
$results.Add([pscustomobject]@{
    name = $positive.name
    passed = $positivePassed
    exitCode = $positive.exitCode
    marker = "CAPTURE_SAVED"
    bytes = $positiveBytes
    stdout = $positive.stdout.Trim()
    stderr = $positive.stderr.Trim()
})
if (!$positivePassed)
{
    throw "Positive exported capture failed."
}

[IO.Directory]::CreateDirectory(
    (Split-Path -Parent $evidenceCapture)) | Out-Null
Copy-Item -LiteralPath $capturePath -Destination $evidenceCapture -Force
$originalHash = (
    Get-FileHash -LiteralPath $capturePath -Algorithm SHA256
).Hash

$negativeCases = @(
    [pscustomobject]@{
        name = "no-overwrite"
        arguments = @(
            "--capture-frame=0",
            "--capture-path=rc2-positive.png"
        )
        type = "IOException"
        message = "already exists"
    },
    [pscustomobject]@{
        name = "parent-traversal"
        arguments = @(
            "--capture-frame=0",
            "--capture-path=..\escape.png"
        )
        type = "ArgumentException"
        message = "without directories"
    },
    [pscustomobject]@{
        name = "absolute"
        arguments = @(
            "--capture-frame=0",
            "--capture-path=C:\outside.png"
        )
        type = "ArgumentException"
        message = "without directories"
    },
    [pscustomobject]@{
        name = "nested"
        arguments = @(
            "--capture-frame=0",
            "--capture-path=nested\outside.png"
        )
        type = "ArgumentException"
        message = "without directories"
    },
    [pscustomobject]@{
        name = "wrong-extension"
        arguments = @(
            "--capture-frame=0",
            "--capture-path=outside.jpg"
        )
        type = "ArgumentException"
        message = ".png extension"
    },
    [pscustomobject]@{
        name = "invalid-direction"
        arguments = @(
            "--capture-frame=0",
            "--capture-direction=sideways",
            "--capture-path=unused.png"
        )
        type = "ArgumentException"
        message = "Invalid capture direction"
    }
)

foreach ($case in $negativeCases)
{
    $result = Invoke-CaptureCase `
        -Name $case.name `
        -UserArguments $case.arguments `
        -Headless $true
    $combined = $result.stdout + "`n" + $result.stderr
    $passed = (
        $result.exitCode -eq 1 -and
        $combined.Contains("CAPTURE_MODE_FAIL") -and
        $combined.Contains("error=$($case.type)") -and
        $combined.Contains($case.message)
    )
    $results.Add([pscustomobject]@{
        name = $case.name
        passed = $passed
        exitCode = $result.exitCode
        marker = "CAPTURE_MODE_FAIL"
        expectedType = $case.type
        expectedMessage = $case.message
        stdout = $result.stdout.Trim()
        stderr = $result.stderr.Trim()
    })
    if (!$passed)
    {
        throw "Negative capture case failed: $($case.name)"
    }
}

$headlessName = "rc2-headless-unavailable.png"
$headlessPath = Join-Path $captureDirectory $headlessName
$headless = Invoke-CaptureCase `
    -Name "headless-render-unavailable" `
    -UserArguments @(
        "--capture-frame=0",
        "--capture-path=$headlessName"
    ) `
    -Headless $true
$headlessCombined = $headless.stdout + "`n" + $headless.stderr
$headlessPassed = (
    $headless.exitCode -eq 1 -and
    $headlessCombined.Contains("CAPTURE_FAILED") -and
    $headlessCombined.Contains("error=InvalidOperationException") -and
    $headlessCombined.Contains("active rendering backend") -and
    !(Test-Path -LiteralPath $headlessPath)
)
$results.Add([pscustomobject]@{
    name = $headless.name
    passed = $headlessPassed
    exitCode = $headless.exitCode
    marker = "CAPTURE_FAILED"
    expectedType = "InvalidOperationException"
    expectedMessage = "active rendering backend"
    stdout = $headless.stdout.Trim()
    stderr = $headless.stderr.Trim()
})
if (!$headlessPassed)
{
    throw "Headless capture failure was not specific or bounded."
}

$finalHash = (
    Get-FileHash -LiteralPath $capturePath -Algorithm SHA256
).Hash
if ($finalHash -cne $originalHash)
{
    throw "No-overwrite case changed the successful capture."
}
$stagingCount = @(
    Get-ChildItem `
        -LiteralPath $captureDirectory `
        -Force `
        -File `
        -Filter "*.staging" `
        -ErrorAction SilentlyContinue
).Count
if ($stagingCount -ne 0)
{
    throw "Capture matrix left $stagingCount staging files."
}

$summary = [ordered]@{
    passed = $true
    caseCount = $results.Count
    positiveRoot = $captureDirectory
    positiveBytes = $positiveBytes
    positiveSha256 = $originalHash
    noOverwritePreserved = $true
    stagingCount = $stagingCount
    cases = $results
}
[IO.File]::WriteAllText(
    $resultsPath,
    ($summary | ConvertTo-Json -Depth 8),
    (New-Object Text.UTF8Encoding($false)))

Remove-Item -LiteralPath $capturePath -Force
if (
    (Test-Path -LiteralPath $captureDirectory -PathType Container) -and
    @(
        Get-ChildItem -LiteralPath $captureDirectory -Force
    ).Count -eq 0
)
{
    Remove-Item -LiteralPath $captureDirectory -Force
}

Write-Output (
    "CAPTURE_MATRIX_PASS cases=$($results.Count) " +
    "positive_bytes=$positiveBytes " +
    "root=$captureDirectory no_overwrite=true staging=0 cleanup=true"
)
