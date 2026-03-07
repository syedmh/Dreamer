using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpClientApp;

public partial class Form1 : Form
{
    private Process? _serverProcess;
    private int _requestId;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public Form1()
    {
        InitializeComponent();
        WireEvents();
        LogInfo("Welcome to the MCP Protocol Explorer!");
        LogInfo("Click '▶ Start Server' to launch your MCP server, then step through the protocol.\n");
    }

    private void WireEvents()
    {
        btnConnect.Click += async (_, _) => await StartServerAsync();
        btnDisconnect.Click += (_, _) => StopServer();
        btnInitialize.Click += async (_, _) => await SendInitializeAsync();
        btnListTools.Click += async (_, _) => await SendListToolsAsync();
        btnCallTime.Click += async (_, _) => await CallToolAsync("get_current_time", new { city = "Seattle" });
        btnCallCalc.Click += async (_, _) => await CallToolAsync("calculate", new { operation = "multiply", a = 7, b = 6 });
        btnCallWeather.Click += async (_, _) => await CallToolAsync("get_weather", new { city = "Tokyo" });
        btnSendCustom.Click += async (_, _) => await SendRawAsync(txtCustomJson.Text);
        btnClearLog.Click += (_, _) => rtbMessages.Clear();
        FormClosing += (_, _) => StopServer();
    }

    // ───── Server Lifecycle ─────

    private async Task StartServerAsync()
    {
        var projectPath = txtServerPath.Text.Trim();
        if (!Directory.Exists(projectPath))
        {
            LogError($"Project path not found: {projectPath}");
            return;
        }

        LogInfo("Building MCP server...");
        // Build first
        var buildPsi = new ProcessStartInfo("dotnet", $"build \"{projectPath}\" --nologo -v q")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var buildProc = Process.Start(buildPsi)!;
        await buildProc.WaitForExitAsync();
        if (buildProc.ExitCode != 0)
        {
            var err = await buildProc.StandardError.ReadToEndAsync();
            LogError($"Build failed:\n{err}");
            return;
        }
        LogInfo("Build succeeded ✓");

        // Launch server
        var psi = new ProcessStartInfo("dotnet", $"run --project \"{projectPath}\" --no-build")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _serverProcess = Process.Start(psi);
        _requestId = 0;

        // Read responses in background
        _ = Task.Run(ReadServerOutputAsync);

        SetConnected(true);
        LogInfo("MCP Server started! Now click '1. Initialize' to begin the handshake.\n");
    }

    private void StopServer()
    {
        if (_serverProcess is { HasExited: false })
        {
            try { _serverProcess.Kill(entireProcessTree: true); } catch { }
            LogInfo("MCP Server stopped.");
        }
        _serverProcess = null;
        SetConnected(false);
    }

    private void SetConnected(bool connected)
    {
        btnConnect.Enabled = !connected;
        txtServerPath.Enabled = !connected;
        btnDisconnect.Enabled = connected;
        btnInitialize.Enabled = connected;
        btnListTools.Enabled = connected;
        btnCallTime.Enabled = connected;
        btnCallCalc.Enabled = connected;
        btnCallWeather.Enabled = connected;
        btnSendCustom.Enabled = connected;
        lblStatus.Text = connected ? "● Connected" : "● Disconnected";
        lblStatus.ForeColor = connected ? Color.LimeGreen : Color.Gray;
    }

    // ───── MCP Protocol Messages ─────

    private async Task SendInitializeAsync()
    {
        var msg = new
        {
            jsonrpc = "2.0",
            id = NextId(),
            method = "initialize",
            @params = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "McpClientApp", version = "1.0" }
            }
        };
        await SendMessageAsync(msg, "INITIALIZE — Start the MCP handshake");

        // Small delay then send the initialized notification
        await Task.Delay(300);
        var notif = new { jsonrpc = "2.0", method = "notifications/initialized" };
        await SendMessageAsync(notif, "INITIALIZED notification — Handshake complete");
    }

    private async Task SendListToolsAsync()
    {
        var msg = new { jsonrpc = "2.0", id = NextId(), method = "tools/list" };
        await SendMessageAsync(msg, "LIST TOOLS — Ask server what tools are available");
    }

    private async Task CallToolAsync(string toolName, object arguments)
    {
        var msg = new
        {
            jsonrpc = "2.0",
            id = NextId(),
            method = "tools/call",
            @params = new { name = toolName, arguments }
        };
        await SendMessageAsync(msg, $"CALL TOOL — Invoke '{toolName}'");
    }

    private async Task SendRawAsync(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        LogSent("CUSTOM MESSAGE", json);
        await WriteToServerAsync(json);
    }

    // ───── Transport ─────

    private async Task SendMessageAsync(object message, string label)
    {
        var json = JsonSerializer.Serialize(message, _jsonOptions);
        LogSent(label, json);
        await WriteToServerAsync(JsonSerializer.Serialize(message)); // compact for wire
    }

    private async Task WriteToServerAsync(string line)
    {
        if (_serverProcess is not { HasExited: false }) { LogError("Server is not running!"); return; }
        try
        {
            await _serverProcess.StandardInput.WriteLineAsync(line);
            await _serverProcess.StandardInput.FlushAsync();
        }
        catch (Exception ex) { LogError($"Send failed: {ex.Message}"); }
    }

    private async Task ReadServerOutputAsync()
    {
        if (_serverProcess is null) return;
        var reader = _serverProcess.StandardOutput;

        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Try to pretty-print the JSON
                string display;
                string label = "RESPONSE";
                try
                {
                    var node = JsonNode.Parse(line);
                    display = node!.ToJsonString(_jsonOptions);

                    // Determine response type for labeling
                    if (node?["error"] != null)
                        label = "ERROR RESPONSE";
                    else if (node?["result"]?["serverInfo"] != null)
                        label = "INITIALIZE RESULT — Server identified itself";
                    else if (node?["result"]?["tools"] != null)
                        label = "TOOLS LIST — Available tools from server";
                    else if (node?["result"]?["content"] != null)
                        label = "TOOL RESULT — Server returned data";
                    else if (node?["method"] != null)
                        label = $"SERVER NOTIFICATION — {node["method"]}";
                }
                catch
                {
                    display = line;
                }

                Invoke(() => LogReceived(label, display));
            }
        }
        catch (Exception ex)
        {
            Invoke(() => LogError($"Read error: {ex.Message}"));
        }

        Invoke(() =>
        {
            LogInfo("Server output stream ended.");
            SetConnected(false);
        });
    }

    // ───── Logging ─────

    private void LogSent(string label, string json)
    {
        AppendLog($"📤 CLIENT → SERVER: {label}", Color.FromArgb(100, 200, 255));
        AppendLog(json, Color.FromArgb(180, 220, 255));
        AppendLog("", Color.White);
    }

    private void LogReceived(string label, string json)
    {
        AppendLog($"📥 SERVER → CLIENT: {label}", Color.FromArgb(100, 255, 150));
        AppendLog(json, Color.FromArgb(200, 255, 220));
        AppendLog("", Color.White);
    }

    private void LogInfo(string text)
    {
        AppendLog($"ℹ️  {text}", Color.FromArgb(200, 200, 200));
    }

    private void LogError(string text)
    {
        AppendLog($"❌ {text}", Color.FromArgb(255, 100, 100));
    }

    private void AppendLog(string text, Color color)
    {
        rtbMessages.SelectionStart = rtbMessages.TextLength;
        rtbMessages.SelectionLength = 0;
        rtbMessages.SelectionColor = color;
        rtbMessages.AppendText(text + Environment.NewLine);
        rtbMessages.ScrollToCaret();
    }

    private int NextId() => ++_requestId;
}
