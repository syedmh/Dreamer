$ErrorActionPreference = "Stop"

$project = "C:\Users\syedhu\source\repos\Dreamer\TCFAnimation"
$exe = Join-Path $project "Build\TCFAnimation.exe"
$captureDirectory = Join-Path $project "Build\Captures"
$outputDirectory = Join-Path $PSScriptRoot "viewport-containment"

[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$sizes = @(
    [pscustomobject]@{ Name = "default"; Arguments = @(); Width = 1280; Height = 720 },
    [pscustomobject]@{ Name = "1920x1080"; Arguments = @("--resolution", "1920x1080"); Width = 1920; Height = 1080 },
    [pscustomobject]@{ Name = "1536x960"; Arguments = @("--resolution", "1536x960"); Width = 1536; Height = 864 },
    [pscustomobject]@{ Name = "1536x864"; Arguments = @("--resolution", "1536x864"); Width = 1536; Height = 864 },
    [pscustomobject]@{ Name = "1280x720"; Arguments = @("--resolution", "1280x720"); Width = 1280; Height = 720 },
    [pscustomobject]@{ Name = "1000x800"; Arguments = @("--resolution", "1000x800"); Width = 1000; Height = 562 }
)
$scenes = @(
    [pscustomobject]@{ Name = "center"; Preview = $null },
    [pscustomobject]@{ Name = "left-dialogue"; Preview = "left" },
    [pscustomobject]@{ Name = "right-dialogue"; Preview = "right" },
    [pscustomobject]@{ Name = "input-dialogue"; Preview = "input" }
)

$records = New-Object System.Collections.Generic.List[object]
foreach ($size in $sizes)
{
    foreach ($scene in $scenes)
    {
        $name = "rc2-test-$($size.Name)-$($scene.Name)"
        $fileName = "$name.png"
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
        foreach ($argument in $size.Arguments)
        {
            $null = $psi.ArgumentList.Add($argument)
        }
        $null = $psi.ArgumentList.Add("--")
        foreach ($argument in @(
            "--capture-frame=0",
            "--capture-direction=right",
            "--capture-path=$fileName"
        ))
        {
            $null = $psi.ArgumentList.Add($argument)
        }
        if ($null -ne $scene.Preview)
        {
            $null = $psi.ArgumentList.Add(
                "--dialogue-preview=$($scene.Preview)")
        }

        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $psi
        if (!$process.Start())
        {
            throw "Could not start viewport case $name."
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(30000))
        {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
            throw "Viewport case $name timed out."
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if (
            ($process.ExitCode -ne 0) -or
            (-not $stdout.Contains("CAPTURE_SAVED")) -or
            (-not (Test-Path -LiteralPath $capturePath -PathType Leaf))
        )
        {
            throw (
                "Viewport case $name failed exit=$($process.ExitCode) " +
                "stdout=$stdout stderr=$stderr")
        }

        $destination = Join-Path $outputDirectory $fileName
        Move-Item -LiteralPath $capturePath -Destination $destination -Force
        $records.Add([ordered]@{
            name = $name
            requested = $size.Name
            scenario = $scene.Name
            expectedWidth = $size.Width
            expectedHeight = $size.Height
            path = $destination
        })
    }
}

if (
    (Test-Path -LiteralPath $captureDirectory -PathType Container) -and
    (@(Get-ChildItem -LiteralPath $captureDirectory -Force).Count -eq 0)
)
{
    Remove-Item -LiteralPath $captureDirectory -Force
}

$records | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $outputDirectory "matrix-input.json") `
        -Encoding utf8NoBOM

Write-Output "VIEWPORT_CONTAINMENT_CAPTURE_PASS cases=$($records.Count)"
