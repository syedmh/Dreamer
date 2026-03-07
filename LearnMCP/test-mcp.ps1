$messages = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"TestClient","version":"1.0"}}}'
    '{"jsonrpc":"2.0","method":"notifications/initialized"}'
    '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
    '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_current_time","arguments":{"city":"Seattle"}}}'
    '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"calculate","arguments":{"operation":"multiply","a":7,"b":6}}}'
    '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"get_weather","arguments":{"city":"Tokyo"}}}'
)

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "dotnet"
$psi.Arguments = "run --project C:\Users\syedhu\source\play\LearnMCP\McpServerDemo --no-build"
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false

$proc = [System.Diagnostics.Process]::Start($psi)

# Read stdout in background to avoid deadlock
$stdoutReader = $proc.StandardOutput.ReadToEndAsync()
$null = $proc.StandardError.ReadToEndAsync()

# Send messages with delays so the server can process each
foreach ($msg in $messages) {
    $proc.StandardInput.WriteLine($msg)
    $proc.StandardInput.Flush()
    Start-Sleep -Milliseconds 300
}

# Give server time to write responses before closing input
Start-Sleep -Seconds 2
$proc.StandardInput.Close()

$null = $proc.WaitForExit(15000)
$output = $stdoutReader.GetAwaiter().GetResult()

Write-Host "========== MCP SERVER RESPONSES =========="
Write-Host ""

$lineNum = 0
foreach ($line in $output.Split("`n")) {
    $trimmed = $line.Trim()
    if ($trimmed.Length -gt 0) {
        $lineNum++
        Write-Host "--- Response $lineNum ---"
        try {
            $trimmed | ConvertFrom-Json | ConvertTo-Json -Depth 10
        } catch {
            Write-Host $trimmed
        }
        Write-Host ""
    }
}

if ($lineNum -eq 0) {
    Write-Host "(No stdout output - raw bytes: $($output.Length))"
    Write-Host "Raw output: [$output]"
}
