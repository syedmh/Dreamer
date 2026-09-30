$ErrorActionPreference = "Stop"

$project = "C:\Users\syedhu\source\repos\Dreamer\TCFAnimation"
$exe = Join-Path $project "Build\TCFAnimation.exe"
$captureDirectory = Join-Path $project "Build\Captures"
$output = Join-Path $PSScriptRoot "viewport-captures"
[IO.Directory]::CreateDirectory($output) | Out-Null
Add-Type -AssemblyName System.Drawing

function Invoke-ViewportCapture
{
    param(
        [string] $Name,
        [string[]] $EngineArguments
    )

    $fileName = "rc2-viewport-$Name.png"
    $capturePath = Join-Path $captureDirectory $fileName
    if (Test-Path -LiteralPath $capturePath)
    {
        Remove-Item -LiteralPath $capturePath -Force
    }

    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $exe
    $psi.WorkingDirectory = "C:\Windows"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    foreach ($argument in $EngineArguments)
    {
        $null = $psi.ArgumentList.Add($argument)
    }
    $null = $psi.ArgumentList.Add("--")
    foreach (
        $argument in @(
            "--capture-frame=0",
            "--capture-direction=right",
            "--capture-path=$fileName",
            "--dialogue-preview=right"
        )
    )
    {
        $null = $psi.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $psi
    if (!$process.Start())
    {
        throw "Could not start viewport capture $Name."
    }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(30000))
    {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
        throw "Viewport capture $Name timed out."
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if (
        $process.ExitCode -ne 0 -or
        !$stdout.Contains("CAPTURE_SAVED") -or
        !(Test-Path -LiteralPath $capturePath -PathType Leaf)
    )
    {
        throw (
            "Viewport capture $Name failed exit=$($process.ExitCode) " +
            "stdout=$stdout stderr=$stderr"
        )
    }

    $image = [Drawing.Bitmap]::new($capturePath)
    try
    {
        $width = $image.Width
        $height = $image.Height
    }
    finally
    {
        $image.Dispose()
    }
    $aspect = $width / [double] $height
    if ([Math]::Abs($aspect - (16.0 / 9.0)) -gt 0.003)
    {
        throw "Viewport capture $Name is not fitted to 16:9: $width x $height."
    }

    $evidencePath = Join-Path $output "$Name.png"
    Copy-Item -LiteralPath $capturePath -Destination $evidencePath -Force
    Remove-Item -LiteralPath $capturePath -Force
    return [pscustomobject]@{
        name = $Name
        engineArguments = $EngineArguments
        width = $width
        height = $height
        aspect = $aspect
        bytes = (Get-Item -LiteralPath $evidencePath).Length
        sha256 = (
            Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256
        ).Hash
        path = $evidencePath
    }
}

$cases = @(
    [pscustomobject]@{ name = "default-1280x720"; arguments = @() },
    [pscustomobject]@{
        name = "requested-1920x1080"
        arguments = @("--resolution", "1920x1080")
    },
    [pscustomobject]@{
        name = "requested-1536x960"
        arguments = @("--resolution", "1536x960")
    },
    [pscustomobject]@{
        name = "requested-1536x864"
        arguments = @("--resolution", "1536x864")
    },
    [pscustomobject]@{
        name = "requested-1280x720"
        arguments = @("--resolution", "1280x720")
    },
    [pscustomobject]@{
        name = "requested-1000x800"
        arguments = @("--resolution", "1000x800")
    }
)

$records = New-Object System.Collections.Generic.List[object]
foreach ($case in $cases)
{
    $records.Add(
        (Invoke-ViewportCapture `
            -Name $case.name `
            -EngineArguments $case.arguments))
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
    throw "Viewport capture matrix left $stagingCount staging files."
}
$remainingRc2 = @(
    Get-ChildItem `
        -LiteralPath $captureDirectory `
        -Force `
        -File `
        -Filter "rc2-viewport-*.png" `
        -ErrorAction SilentlyContinue
).Count
if ($remainingRc2 -ne 0)
{
    throw "Viewport capture matrix left $remainingRc2 capture files."
}
if (
    (Test-Path -LiteralPath $captureDirectory -PathType Container) -and
    @(Get-ChildItem -LiteralPath $captureDirectory -Force).Count -eq 0
)
{
    Remove-Item -LiteralPath $captureDirectory -Force
}

$summary = [ordered]@{
    passed = $true
    logicalViewport = "1920x1080"
    configuredOverride = "1280x720"
    stretchMode = "canvas_items"
    stretchAspect = "keep"
    caseCount = $records.Count
    records = $records
    stagingCount = $stagingCount
    cleanup = $true
}
[IO.File]::WriteAllText(
    (Join-Path $output "viewport-capture-matrix.json"),
    ($summary | ConvertTo-Json -Depth 7),
    (New-Object Text.UTF8Encoding($false)))

$sizes = $records |
    ForEach-Object { "$($_.name)=$($_.width)x$($_.height)" }
Write-Output (
    "VIEWPORT_CAPTURE_MATRIX_PASS cases=$($records.Count) " +
    ($sizes -join ";") +
    " staging=0 cleanup=true"
)
