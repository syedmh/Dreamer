$ErrorActionPreference = "Stop"

$repository = "C:\Users\syedhu\source\repos\Dreamer"
$project = Join-Path $repository "TCFAnimation"
$godot = Join-Path $project (
    ".tools\godot-4.5.1-mono\" +
    "Godot_v4.5.1-stable_mono_win64\" +
    "Godot_v4.5.1-stable_mono_win64_console.exe"
)
$build = Join-Path $project "Build"
$exe = Join-Path $build "TCFAnimation.exe"
$resultsPath = Join-Path $PSScriptRoot "capture-context-results.json"
$results = New-Object System.Collections.Generic.List[object]

function Invoke-CapturedProcess
{
    param(
        [string] $FileName,
        [string] $WorkingDirectory,
        [string[]] $Arguments,
        [int] $TimeoutMilliseconds = 30000
    )

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FileName
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments)
    {
        [void]$start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (!$process.Start())
    {
        throw "Could not start $FileName."
    }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit($TimeoutMilliseconds))
    {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
        throw "Process timed out: $FileName"
    }
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Stdout = $stdoutTask.GetAwaiter().GetResult()
        Stderr = $stderrTask.GetAwaiter().GetResult()
    }
}

function Add-Result
{
    param(
        [string] $Name,
        [bool] $Passed,
        [string] $Detail,
        [object] $Process = $null
    )

    $results.Add([pscustomobject]@{
        name = $Name
        passed = $Passed
        detail = $Detail
        exitCode = if ($null -eq $Process) { $null } else { $Process.ExitCode }
        stdout = if ($null -eq $Process) { "" } else { $Process.Stdout.Trim() }
        stderr = if ($null -eq $Process) { "" } else { $Process.Stderr.Trim() }
    })
    Write-Output "RC3_CAPTURE_CONTEXT name=$Name passed=$Passed detail=$Detail"
    if (!$Passed)
    {
        throw "Capture-context test failed: $Name - $Detail"
    }
}

function Remove-EmptyCaptureDirectory
{
    param([string] $Path)

    if (
        (Test-Path -LiteralPath $Path -PathType Container) -and
        @(
            Get-ChildItem -LiteralPath $Path -Force
        ).Count -eq 0
    )
    {
        Remove-Item -LiteralPath $Path -Force
    }
}

if (!(Test-Path -LiteralPath $godot -PathType Leaf))
{
    throw "Missing approved Godot console: $godot"
}
if (!(Test-Path -LiteralPath $exe -PathType Leaf))
{
    throw "Missing exported executable: $exe"
}

$developmentCaptureDirectory = Join-Path $project "Captures"
$developmentTarget = Join-Path $developmentCaptureDirectory "rc3-development.png"
if (Test-Path -LiteralPath $developmentTarget)
{
    Remove-Item -LiteralPath $developmentTarget -Force
}
$development = Invoke-CapturedProcess `
    -FileName $godot `
    -WorkingDirectory "C:\Windows" `
    -Arguments @(
        "--headless",
        "--path",
        $project,
        "--",
        "--capture-frame=0",
        "--capture-path=rc3-development.png"
    )
$developmentText = $development.Stdout + "`n" + $development.Stderr
Add-Result `
    -Name "development-marker" `
    -Passed (
        $development.ExitCode -eq 1 -and
        $developmentText.Contains("CAPTURE_ROOT kind=project_resource") -and
        $developmentText.Contains("CAPTURE_FAILED") -and
        $developmentText.Contains("active rendering backend") -and
        !(Test-Path -LiteralPath $developmentTarget)
    ) `
    -Detail "kind=project_resource no_target=true no_fallback=true" `
    -Process $development
Remove-EmptyCaptureDirectory -Path $developmentCaptureDirectory

$standaloneCaptureDirectory = Join-Path $build "Captures"
$standaloneTarget = Join-Path $standaloneCaptureDirectory "rc3-standalone.png"
if (Test-Path -LiteralPath $standaloneTarget)
{
    Remove-Item -LiteralPath $standaloneTarget -Force
}
$standalone = Invoke-CapturedProcess `
    -FileName $exe `
    -WorkingDirectory "C:\Windows" `
    -Arguments @(
        "--headless",
        "--",
        "--capture-frame=0",
        "--capture-path=rc3-standalone.png"
    )
$standaloneText = $standalone.Stdout + "`n" + $standalone.Stderr
Add-Result `
    -Name "standalone-marker" `
    -Passed (
        $standalone.ExitCode -eq 1 -and
        $standaloneText.Contains("CAPTURE_ROOT kind=executable_adjacent") -and
        $standaloneText.Contains("CAPTURE_FAILED") -and
        $standaloneText.Contains("active rendering backend") -and
        !(Test-Path -LiteralPath $standaloneTarget)
    ) `
    -Detail "kind=executable_adjacent no_target=true" `
    -Process $standalone
Remove-EmptyCaptureDirectory -Path $standaloneCaptureDirectory

$junctionTarget = Join-Path $env:TEMP (
    "TCFAnimation-rc3-junction-" + [guid]::NewGuid().ToString("N")
)
try
{
    if (Test-Path -LiteralPath $standaloneCaptureDirectory)
    {
        $existing = Get-Item -LiteralPath $standaloneCaptureDirectory -Force
        if (
            ($existing.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            @(
                Get-ChildItem -LiteralPath $standaloneCaptureDirectory -Force
            ).Count -ne 0
        )
        {
            throw "Build Captures is not safe for the junction fixture."
        }
        Remove-Item -LiteralPath $standaloneCaptureDirectory -Force
    }
    [IO.Directory]::CreateDirectory($junctionTarget) | Out-Null
    New-Item `
        -ItemType Junction `
        -Path $standaloneCaptureDirectory `
        -Target $junctionTarget | Out-Null
    $reparse = Invoke-CapturedProcess `
        -FileName $exe `
        -WorkingDirectory "C:\Windows" `
        -Arguments @(
            "--",
            "--capture-frame=0",
            "--capture-path=rc3-reparse.png"
        )
    $reparseText = $reparse.Stdout + "`n" + $reparse.Stderr
    Add-Result `
        -Name "standalone-reparse" `
        -Passed (
            $reparse.ExitCode -eq 1 -and
            $reparseText.Contains("CAPTURE_ROOT kind=executable_adjacent") -and
            $reparseText.Contains("CAPTURE_FAILED") -and
            $reparseText.Contains("error=IOException") -and
            $reparseText.Contains("reparse point") -and
            @(
                Get-ChildItem -LiteralPath $junctionTarget -Force
            ).Count -eq 0
        ) `
        -Detail "specific=IOException/reparse target_files=0" `
        -Process $reparse
}
finally
{
    if (Test-Path -LiteralPath $standaloneCaptureDirectory)
    {
        Remove-Item -LiteralPath $standaloneCaptureDirectory -Force
    }
    if (Test-Path -LiteralPath $junctionTarget)
    {
        Remove-Item -LiteralPath $junctionTarget -Recurse -Force
    }
}

$readOnlyRoot = Join-Path $env:TEMP (
    "TCFAnimation-rc3-readonly-" + [guid]::NewGuid().ToString("N")
)
$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
try
{
    Copy-Item -LiteralPath $build -Destination $readOnlyRoot -Recurse
    $readOnlyExe = Join-Path $readOnlyRoot "TCFAnimation.exe"
    $aclOutput = @(
        & icacls.exe $readOnlyRoot `
            /inheritance:r `
            /grant:r "*${currentSid}:(RX)" `
            /T `
            /C
    )
    if ($LASTEXITCODE -ne 0)
    {
        throw "Could not create read-only portable fixture: $aclOutput"
    }
    $readOnly = Invoke-CapturedProcess `
        -FileName $readOnlyExe `
        -WorkingDirectory "C:\Windows" `
        -Arguments @(
            "--",
            "--capture-frame=0",
            "--capture-path=rc3-readonly.png"
        )
    $readOnlyText = $readOnly.Stdout + "`n" + $readOnly.Stderr
    Add-Result `
        -Name "standalone-read-only" `
        -Passed (
            $readOnly.ExitCode -eq 1 -and
            $readOnlyText.Contains("CAPTURE_ROOT kind=executable_adjacent") -and
            $readOnlyText.Contains("CAPTURE_FAILED") -and
            (
                $readOnlyText.Contains("UnauthorizedAccessException") -or
                $readOnlyText.Contains("Access to the path")
            ) -and
            !(Test-Path -LiteralPath (Join-Path $readOnlyRoot "Captures"))
        ) `
        -Detail "fail_closed=true no_fallback=true no_capture_directory=true" `
        -Process $readOnly
}
finally
{
    if (Test-Path -LiteralPath $readOnlyRoot)
    {
        & icacls.exe $readOnlyRoot `
            /grant:r "*${currentSid}:(F)" `
            /T `
            /C | Out-Null
        $removed = $false
        for ($attempt = 0; $attempt -lt 20 -and !$removed; $attempt++)
        {
            try
            {
                Remove-Item `
                    -LiteralPath $readOnlyRoot `
                    -Recurse `
                    -Force `
                    -ErrorAction Stop
                $removed = $true
            }
            catch
            {
                Start-Sleep -Milliseconds 500
            }
        }
        if (!$removed)
        {
            throw "Read-only portable fixture cleanup failed."
        }
    }
}

$payload = [ordered]@{
    status = "PASS"
    cases = $results.Count
    results = $results
}
[IO.Directory]::CreateDirectory($PSScriptRoot) | Out-Null
[IO.File]::WriteAllText(
    $resultsPath,
    ($payload | ConvertTo-Json -Depth 8),
    (New-Object Text.UTF8Encoding($false))
)
Write-Output (
    "RC3_CAPTURE_CONTEXT_PASS cases=$($results.Count) " +
    "development=project_resource standalone=executable_adjacent " +
    "reparse=fail_closed readonly=fail_closed"
)
