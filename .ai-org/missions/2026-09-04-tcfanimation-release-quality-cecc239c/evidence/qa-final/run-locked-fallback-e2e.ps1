$ErrorActionPreference = "Stop"

$Exe = "C:\Users\syedhu\source\repos\Dreamer\TCFAnimation\Build\TCFAnimation.exe"
$Out = "C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa-final\runtime-postmessage"
New-Item -ItemType Directory -Force -Path $Out | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class QaWin32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
}
'@

$script:Scenario = 0

function Start-QaApp([string]$Name) {
    $script:Scenario++
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Exe
    $psi.WorkingDirectory = "C:\Windows"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $psi
    if (-not $process.Start()) { throw "Failed to start $Exe" }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw "No window for scenario $Name; pid=$($process.Id)"
    }
    Start-Sleep -Milliseconds 700
    Write-Host ("SCENARIO_START index={0} name={1} pid={2} hwnd={3}" -f $script:Scenario, $Name, $process.Id, $process.MainWindowHandle)
    return $process
}

function Stop-QaApp([System.Diagnostics.Process]$Process) {
    if (-not $Process.HasExited) {
        [QaWin32]::PostMessage($Process.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        if (-not $Process.WaitForExit(5000)) {
            Stop-Process -Id $Process.Id -Force
            $Process.WaitForExit()
        }
    }
    $stdout = $Process.StandardOutput.ReadToEnd().Trim()
    $stderr = $Process.StandardError.ReadToEnd().Trim()
    Write-Output ("SCENARIO_EXIT pid={0} exit={1}" -f $Process.Id, $Process.ExitCode)
    if ($stdout) { Write-Output "STDOUT_BEGIN"; Write-Output $stdout; Write-Output "STDOUT_END" }
    if ($stderr) { Write-Output "STDERR_BEGIN"; Write-Output $stderr; Write-Output "STDERR_END" }
    $Process.Dispose()
}

function Get-KeyLParam([int]$Scan, [bool]$Up, [bool]$Extended = $false, [bool]$Alt = $false) {
    [uint32]$value = 1 -bor ($Scan -shl 16)
    if ($Extended) { $value = $value -bor 0x01000000 }
    if ($Alt) { $value = $value -bor 0x20000000 }
    if ($Up) { $value = $value -bor 0xC0000000 }
    return [IntPtr]([int64]$value)
}

function Key-Down([IntPtr]$Hwnd, [int]$Vk, [int]$Scan, [bool]$Extended = $false) {
    [QaWin32]::PostMessage($Hwnd, 0x0100, [IntPtr]$Vk, (Get-KeyLParam $Scan $false $Extended)) | Out-Null
}

function Key-Up([IntPtr]$Hwnd, [int]$Vk, [int]$Scan, [bool]$Extended = $false) {
    [QaWin32]::PostMessage($Hwnd, 0x0101, [IntPtr]$Vk, (Get-KeyLParam $Scan $true $Extended)) | Out-Null
}

function Key-Press([IntPtr]$Hwnd, [int]$Vk, [int]$Scan, [bool]$Extended = $false) {
    Key-Down $Hwnd $Vk $Scan $Extended
    Start-Sleep -Milliseconds 20
    Key-Up $Hwnd $Vk $Scan $Extended
}

function Alt-Enter([IntPtr]$Hwnd) {
    $down = Get-KeyLParam 0x1C $false $false $true
    $up = Get-KeyLParam 0x1C $true $false $true
    [QaWin32]::PostMessage($Hwnd, 0x0104, [IntPtr]0x0D, $down) | Out-Null
    Start-Sleep -Milliseconds 20
    [QaWin32]::PostMessage($Hwnd, 0x0105, [IntPtr]0x0D, $up) | Out-Null
}

function Type-Text([IntPtr]$Hwnd, [string]$Text) {
    foreach ($unit in $Text.ToCharArray()) {
        [QaWin32]::PostMessage($Hwnd, 0x0102, [IntPtr][int]$unit, [IntPtr]1) | Out-Null
        Start-Sleep -Milliseconds 8
    }
}

function Save-Window([System.Diagnostics.Process]$Process, [string]$Name) {
    $Process.Refresh()
    $h = $Process.MainWindowHandle
    $rect = [QaWin32+RECT]::new()
    [QaWin32]::GetWindowRect($h, [ref]$rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    $ok = [QaWin32]::PrintWindow($h, $dc, 2)
    $graphics.ReleaseHdc($dc)
    $path = Join-Path $Out ($Name + ".png")
    $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
    $client = [QaWin32+RECT]::new()
    [QaWin32]::GetClientRect($h, [ref]$client) | Out-Null
    $style = [QaWin32]::GetWindowLong($h, -16)
    Write-Output ("SNAP name={0} print={1} outer={2}x{3} client={4}x{5} style=0x{6:X8} path={7}" -f $Name, $ok, $width, $height, ($client.Right-$client.Left), ($client.Bottom-$client.Top), $style, $path)
}

function Capture-Timeline(
    [System.Diagnostics.Process]$Process,
    [string]$Prefix,
    [int]$Count,
    [int]$FirstDelayMs,
    [int]$IntervalMs
) {
    Start-Sleep -Milliseconds $FirstDelayMs
    for ($i = 0; $i -lt $Count; $i++) {
        Save-Window $Process ("{0}-{1:D2}" -f $Prefix, $i)
        if ($i + 1 -lt $Count) { Start-Sleep -Milliseconds $IntervalMs }
    }
}

# Scenario 1: directional turn, walk, reversal, both-held neutral, and edges.
$p = Start-QaApp "directional-windowed"
$h = $p.MainWindowHandle
Save-Window $p "directional-00-idle"
Key-Down $h 0x25 0x4B $true
Capture-Timeline $p "directional-left-entry" 5 45 125
Start-Sleep -Milliseconds 3600
Save-Window $p "directional-left-edge"
Key-Up $h 0x25 0x4B $true
Start-Sleep -Milliseconds 500
Save-Window $p "directional-left-return"
Key-Down $h 0x27 0x4D $true
Start-Sleep -Milliseconds 700
Save-Window $p "directional-right-walk-before-reversal"
Key-Up $h 0x27 0x4D $true
Key-Down $h 0x25 0x4B $true
Capture-Timeline $p "directional-reversal-to-left" 6 45 125
Key-Down $h 0x27 0x4D $true
Start-Sleep -Milliseconds 700
Save-Window $p "directional-both-held-neutral"
Key-Up $h 0x25 0x4B $true
Key-Up $h 0x27 0x4D $true
Start-Sleep -Milliseconds 500
Key-Down $h 0x27 0x4D $true
Start-Sleep -Milliseconds 7200
Save-Window $p "directional-right-edge"
Key-Up $h 0x27 0x4D $true
Start-Sleep -Milliseconds 500
Save-Window $p "directional-right-return"
Stop-QaApp $p

# Scenario 2: speed min/max, clamping, and W no-op.
$p = Start-QaApp "speed-and-w-noop"
$h = $p.MainWindowHandle
Save-Window $p "speed-00-idle"
for ($i = 0; $i -lt 16; $i++) { Key-Press $h 0x6D 0x4A }
Save-Window $p "speed-min-before-walk"
Key-Down $h 0x27 0x4D $true
Start-Sleep -Milliseconds 1400
Save-Window $p "speed-min-after-1400ms"
Key-Up $h 0x27 0x4D $true
Start-Sleep -Milliseconds 500
for ($i = 0; $i -lt 20; $i++) { Key-Press $h 0x6B 0x4E }
Save-Window $p "speed-max-before-walk"
Key-Down $h 0x25 0x4B $true
Start-Sleep -Milliseconds 1400
Save-Window $p "speed-max-after-1400ms"
Key-Up $h 0x25 0x4B $true
Start-Sleep -Milliseconds 500
Save-Window $p "w-noop-before"
Key-Press $h 0x57 0x11
Start-Sleep -Milliseconds 300
Save-Window $p "w-noop-after"
Stop-QaApp $p

# Scenario 3: exact clap timeline, directional interruption, and C while moving.
$p = Start-QaApp "clap"
$h = $p.MainWindowHandle
Key-Press $h 0x43 0x2E
Capture-Timeline $p "clap-step" 15 45 125
Start-Sleep -Milliseconds 250
Save-Window $p "clap-return-front"
Key-Press $h 0x43 0x2E
Start-Sleep -Milliseconds 425
Save-Window $p "clap-before-interrupt"
Key-Down $h 0x27 0x4D $true
Start-Sleep -Milliseconds 425
Save-Window $p "clap-direction-interrupted"
Key-Press $h 0x43 0x2E
Start-Sleep -Milliseconds 250
Save-Window $p "clap-c-ignored-while-moving"
Key-Up $h 0x27 0x4D $true
Stop-QaApp $p

# Scenario 4: cross, hold locks, release, held-arrow non-queue, and recovery.
$p = Start-QaApp "cross-hold-release"
$h = $p.MainWindowHandle
Key-Press $h 0x58 0x2D
Capture-Timeline $p "cross-entry" 3 45 125
Start-Sleep -Milliseconds 700
Save-Window $p "cross-held"
Key-Press $h 0x43 0x2E
Key-Down $h 0x25 0x4B $true
Start-Sleep -Milliseconds 500
Save-Window $p "cross-held-inputs-blocked"
Key-Press $h 0x58 0x2D
Capture-Timeline $p "cross-release" 6 45 125
Start-Sleep -Milliseconds 350
Save-Window $p "cross-release-complete-arrow-still-held"
Key-Up $h 0x25 0x4B $true
Start-Sleep -Milliseconds 200
Key-Down $h 0x25 0x4B $true
Start-Sleep -Milliseconds 550
Save-Window $p "cross-arrow-repressed-moves"
Key-Up $h 0x25 0x4B $true
Stop-QaApp $p

# Scenario 5: dialogue Unicode, W typing, submit/hide/cancel, input suppression.
$p = Start-QaApp "dialogue-windowed"
$h = $p.MainWindowHandle
Key-Press $h 0x0D 0x1C
Type-Text $h "Hello w/W - سلام - 😀"
Save-Window $p "dialogue-input-unicode-wW"
Key-Press $h 0x43 0x2E
Key-Press $h 0x58 0x2D
Key-Down $h 0x27 0x4D $true
Start-Sleep -Milliseconds 400
Save-Window $p "dialogue-character-input-suppressed"
Key-Up $h 0x27 0x4D $true
Key-Press $h 0x0D 0x1C
Start-Sleep -Milliseconds 300
Save-Window $p "dialogue-submitted"
Key-Press $h 0x50 0x19
Start-Sleep -Milliseconds 200
Save-Window $p "dialogue-hidden-with-p"
Key-Press $h 0x0D 0x1C
Type-Text $h "cancel me"
Key-Press $h 0x1B 0x01
Start-Sleep -Milliseconds 200
Save-Window $p "dialogue-cancelled"
Stop-QaApp $p

# Scenario 6: fullscreen arbitration while dialogue is open and Escape ownership.
$p = Start-QaApp "fullscreen-dialogue-arbitration"
$h = $p.MainWindowHandle
Save-Window $p "fullscreen-00-windowed"
Key-Press $h 0x0D 0x1C
Type-Text $h "fullscreen arbitration"
Key-Press $h 0x7A 0x57
Start-Sleep -Milliseconds 500
Save-Window $p "fullscreen-f11-dialogue-open"
Alt-Enter $h
Start-Sleep -Milliseconds 500
Save-Window $p "fullscreen-alt-enter-dialogue-open-windowed"
Key-Press $h 0x7A 0x57
Start-Sleep -Milliseconds 500
Save-Window $p "fullscreen-before-dialogue-escape"
Key-Press $h 0x1B 0x01
Start-Sleep -Milliseconds 300
Save-Window $p "fullscreen-dialogue-escape-cancel-only"
Key-Press $h 0x1B 0x01
Start-Sleep -Milliseconds 500
Save-Window $p "fullscreen-escape-exits"
Stop-QaApp $p

# Scenario 7: bubble follows and clamps at both edges, windowed and fullscreen.
$p = Start-QaApp "dialogue-edge-clamping"
$h = $p.MainWindowHandle
Key-Down $h 0x25 0x4B $true
Start-Sleep -Milliseconds 4200
Key-Up $h 0x25 0x4B $true
Start-Sleep -Milliseconds 500
Key-Press $h 0x0D 0x1C
Type-Text $h "Left edge bubble remains readable and follows."
Key-Press $h 0x0D 0x1C
Start-Sleep -Milliseconds 300
Save-Window $p "dialogue-left-edge-windowed"
Key-Press $h 0x7A 0x57
Start-Sleep -Milliseconds 500
Save-Window $p "dialogue-left-edge-fullscreen"
Key-Press $h 0x7A 0x57
Key-Press $h 0x50 0x19
Key-Down $h 0x27 0x4D $true
Start-Sleep -Milliseconds 7000
Key-Up $h 0x27 0x4D $true
Start-Sleep -Milliseconds 500
Key-Press $h 0x0D 0x1C
Type-Text $h "Right edge bubble remains readable and follows."
Key-Press $h 0x0D 0x1C
Start-Sleep -Milliseconds 300
Save-Window $p "dialogue-right-edge-windowed"
Key-Press $h 0x7A 0x57
Start-Sleep -Milliseconds 500
Save-Window $p "dialogue-right-edge-fullscreen"
Key-Press $h 0x1B 0x01
Start-Sleep -Milliseconds 500
Save-Window $p "dialogue-right-edge-back-windowed"
Stop-QaApp $p

Write-Output ("EXPORTED_E2E_CAPTURE_PASS scenarios={0}" -f $script:Scenario)
