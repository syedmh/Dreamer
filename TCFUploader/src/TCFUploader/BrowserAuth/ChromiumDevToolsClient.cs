using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TCFUploader.Configuration;

namespace TCFUploader.BrowserAuth;

internal sealed class ChromiumDevToolsClient : IBrowserTokenCapture
{
    private const int MaximumDevToolsMessageBytes = 64 * 1024;
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(500);
    private const string AuthenticationExpression =
        "(() => { const raw = localStorage.getItem('user-settings'); if (raw === null) return null; " +
        "try { const parsed = JSON.parse(raw); const state = parsed?.state ?? parsed; " +
        "const token = state?.settings?.fotoshare_token; const id = state?.user?.profile?.id; " +
        "return typeof token === 'string' && (typeof id === 'string' || typeof id === 'number') " +
        "? JSON.stringify({token, userId: String(id)}) : null; } " +
        "catch { return null; } })()";

    public async Task<BrowserTokenCaptureResult> CaptureAsync(
        string profilePath,
        IBrowserProcess process,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(2)
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        var portFile = Path.Combine(profilePath, "DevToolsActivePort");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                return new BrowserTokenCaptureResult.BrowserClosed();

            var port = await TryReadPortAsync(portFile, cancellationToken);
            if (port is not null)
            {
                foreach (var target in await GetDashboardTargetsAsync(http, port.Value, cancellationToken))
                {
                    var authentication = await TryEvaluateAuthenticationAsync(target, cancellationToken);
                    if (authentication is not null)
                        return new BrowserTokenCaptureResult.Authentication(
                            authentication.Value.Token,
                            authentication.Value.UserId);
                }
            }

            await Task.Delay(PollDelay, cancellationToken);
        }
    }

    internal static bool IsDashboardTarget(Uri pageUri, Uri webSocketUri, int expectedPort) =>
        pageUri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(pageUri.Host, UploaderConstants.DashboardUploadUri.Host, StringComparison.OrdinalIgnoreCase) &&
        pageUri.IsDefaultPort &&
        webSocketUri.Scheme == "ws" &&
        string.Equals(webSocketUri.Host, "127.0.0.1", StringComparison.Ordinal) &&
        webSocketUri.Port == expectedPort;

    internal static string? ExtractTokenFromPersistedRecord(string record)
        => ExtractAuthenticationFromPersistedRecord(record)?.Token;

    internal static (string Token, string UserId)? ExtractAuthenticationFromPersistedRecord(string record)
    {
        try
        {
            using var json = JsonDocument.Parse(record);
            var root = json.RootElement;
            var state = root.TryGetProperty("state", out var persistedState) ? persistedState : root;
            if (!state.TryGetProperty("settings", out var settings) ||
                !TryGetToken(settings, out var token) ||
                !state.TryGetProperty("user", out var user) ||
                !user.TryGetProperty("profile", out var profile) ||
                !profile.TryGetProperty("id", out var id))
                return null;
            var userId = id.ValueKind switch
            {
                JsonValueKind.String => id.GetString(),
                JsonValueKind.Number => id.GetRawText(),
                _ => null
            };
            return string.IsNullOrWhiteSpace(userId) ? null : (token!, userId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static (string Token, string UserId)? ParseEvaluationResponse(string response, int expectedId)
    {
        try
        {
            using var json = JsonDocument.Parse(response);
            var root = json.RootElement;
            if (!root.TryGetProperty("id", out var id) ||
                id.GetInt32() != expectedId ||
                !root.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("result", out var remoteResult) ||
                !remoteResult.TryGetProperty("value", out var value) ||
                value.ValueKind != JsonValueKind.String)
                return null;
            return value.GetString() is { } authentication
                ? ExtractAuthenticationPayload(authentication)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static bool TryGetToken(JsonElement settings, out string? token)
    {
        token = null;
        if (!settings.TryGetProperty("fotoshare_token", out var value) ||
            value.ValueKind != JsonValueKind.String)
            return false;
        token = value.GetString();
        return token is not null;
    }

    private static async Task<int?> TryReadPortAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(path, cancellationToken);
            return lines.Length >= 1 &&
                int.TryParse(lines[0], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var port) &&
                port is > 0 and <= 65535
                    ? port
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<Uri>> GetDashboardTargetsAsync(
        HttpClient http,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(
                new Uri($"http://127.0.0.1:{port}/json/list"),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return [];

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var content = await ReadBoundedAsync(stream, cancellationToken);
            if (content is null)
                return [];
            using var json = JsonDocument.Parse(content);
            var targets = new List<Uri>();
            foreach (var target in json.RootElement.EnumerateArray())
            {
                if (!target.TryGetProperty("type", out var type) ||
                    type.GetString() != "page" ||
                    !target.TryGetProperty("url", out var url) ||
                    !target.TryGetProperty("webSocketDebuggerUrl", out var webSocket) ||
                    !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var pageUri) ||
                    !Uri.TryCreate(webSocket.GetString(), UriKind.Absolute, out var webSocketUri) ||
                    !IsDashboardTarget(pageUri, webSocketUri, port))
                    continue;
                targets.Add(webSocketUri);
            }
            return targets;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
            JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    private static (string Token, string UserId)? ExtractAuthenticationPayload(string payload)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            if (!root.TryGetProperty("token", out var token) ||
                token.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("userId", out var userId) ||
                userId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(token.GetString()) ||
                string.IsNullOrWhiteSpace(userId.GetString()))
                return null;
            return (token.GetString()!, userId.GetString()!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<(string Token, string UserId)?> TryEvaluateAuthenticationAsync(
        Uri webSocketUri,
        CancellationToken cancellationToken)
    {
        try
        {
            using var socket = new ClientWebSocket();
            socket.Options.Proxy = null;
            await socket.ConnectAsync(webSocketUri, cancellationToken);
            const int requestId = 1;
            var request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = requestId,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = AuthenticationExpression,
                    returnByValue = true,
                    awaitPromise = false
                }
            });
            await socket.SendAsync(
                request,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);

            var response = await ReceiveTextAsync(socket, cancellationToken);
            return response is null ? null : ParseEvaluationResponse(response, requestId);
        }
        catch (Exception ex) when (ex is WebSocketException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var content = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return content.ToArray();
            if (content.Length + read > MaximumDevToolsMessageBytes)
                return null;
            content.Write(buffer, 0, read);
        }
    }

    private static async Task<string?> ReceiveTextAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var response = new MemoryStream();
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
                return null;
            if (received.MessageType != WebSocketMessageType.Text ||
                response.Length + received.Count > MaximumDevToolsMessageBytes)
                return null;
            response.Write(buffer, 0, received.Count);
            if (received.EndOfMessage)
                return Encoding.UTF8.GetString(response.GetBuffer(), 0, checked((int)response.Length));
        }
    }
}
