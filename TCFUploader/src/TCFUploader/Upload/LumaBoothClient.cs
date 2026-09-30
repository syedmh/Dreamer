using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TCFUploader.Configuration;
using TCFUploader.State;

namespace TCFUploader.Upload;

internal abstract record PutResult
{
    internal sealed record Success(Uri RemoteUrl) : PutResult;
    internal sealed record Failure(UploadFailure Error) : PutResult;
}
internal abstract record PostResult
{
    internal sealed record Success : PostResult;
    internal sealed record Failure(UploadFailure Error) : PostResult;
}

internal sealed class LumaBoothClient(HttpClient client, RuntimeOptions options, string? userId = null)
{
    internal async Task<PutResult> PutAsync(
        UploadItemState item, string absoluteSpoolPath, string token, CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(userId)
            ? Uri.EscapeDataString(item.RemoteKey)
            : string.Join('/',
                "lb",
                "event",
                Uri.EscapeDataString(userId),
                Uri.EscapeDataString(UploaderConstants.EventId),
                "uploads",
                Uri.EscapeDataString(item.RemoteKey));
        var uri = new Uri(UploaderConstants.PutBaseUri, key);
        if (!AllowedCredentialUri(uri, "w.fotoshare.co")) return FailurePut("put_host_rejected");
        using var request = new HttpRequestMessage(HttpMethod.Put, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Referrer = UploaderConstants.DashboardUploadUri;
        request.Headers.TryAddWithoutValidation("Origin", "https://dash.lumabooth.com");
        request.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", "*");
        var stream = new FileStream(absoluteSpoolPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(item.ContentType);
        request.Content.Headers.ContentLength = item.ByteLength;
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(options.HttpAttemptTimeout);
        try
        {
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
            if (!response.IsSuccessStatusCode)
                return await ClassifyPutFailureAsync(response, attemptCts.Token);
            using var json = await ReadJsonAsync(response, attemptCts.Token);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("url", out var property) ||
                property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
                return FailurePut("put_protocol");
            if (!Uri.TryCreate(UploaderConstants.MediaBaseUri, property.GetString(), out var remote) ||
                !StateRepository.IsAllowedMediaUri(remote))
                return FailurePut("put_url_rejected");
            return new PutResult.Success(remote);
        }
        catch (ResponseTooLargeException) { return FailurePut("put_response_too_large"); }
        catch (JsonException) { return FailurePut("put_json_invalid"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new PutResult.Failure(new UploadFailure("put_timeout", true, false, null)); }
        catch (HttpRequestException)
        { return new PutResult.Failure(new UploadFailure("put_network", true, false, null)); }
        catch (IOException)
        { return new PutResult.Failure(new UploadFailure("put_network", true, false, null)); }
    }

    internal async Task<PostResult> PostAsync(
        UploadItemState item, string token, CancellationToken cancellationToken)
    {
        if (!AllowedCredentialUri(UploaderConstants.PostUri, "fotoshare.co")) return FailurePost("post_host_rejected");
        using var request = new HttpRequestMessage(HttpMethod.Post, UploaderConstants.PostUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new MultipartFormDataContent
        {
            { new StringContent(item.RemoteUrl!), "uploadFileField" },
            { new StringContent(item.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture)), "imgSize" },
            { new StringContent("0"), "imgWidth" },
            { new StringContent("0"), "imgHeight" }
        };
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(options.HttpAttemptTimeout);
        try
        {
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
            if (!response.IsSuccessStatusCode)
                return FailurePost(response.StatusCode, response.Headers.RetryAfter);
            using var json = await ReadJsonAsync(response, attemptCts.Token);
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("success", out var success) &&
                success.ValueKind is JsonValueKind.True
                ? new PostResult.Success()
                : FailurePost("post_protocol");
        }
        catch (ResponseTooLargeException) { return FailurePost("post_response_too_large"); }
        catch (JsonException) { return FailurePost("post_json_invalid"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new PostResult.Failure(new UploadFailure("post_timeout", true, false, null)); }
        catch (HttpRequestException)
        { return new PostResult.Failure(new UploadFailure("post_network", true, false, null)); }
        catch (IOException)
        { return new PostResult.Failure(new UploadFailure("post_network", true, false, null)); }
    }

    private async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        int total = 0, read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += read;
            if (total > options.MaxJsonResponseBytes) throw new ResponseTooLargeException();
            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        bounded.Position = 0;
        return await JsonDocument.ParseAsync(bounded, cancellationToken: cancellationToken);
    }

    private static bool AllowedCredentialUri(Uri uri, string host) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment) && string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase);
    private static PutResult.Failure FailurePut(string code) => new(new UploadFailure(code, false, false, null));
    private static PostResult.Failure FailurePost(string code) => new(new UploadFailure(code, false, false, null));
    private static PutResult.Failure FailurePut(HttpStatusCode status, RetryConditionHeaderValue? retry) =>
        new(Classify("put", status, retry));
    private static PostResult.Failure FailurePost(HttpStatusCode status, RetryConditionHeaderValue? retry) =>
        new(Classify("post", status, retry));
    private static UploadFailure Classify(string stage, HttpStatusCode status, RetryConditionHeaderValue? retry)
    {
        var number = (int)status;
        var transient = number is 408 or 429 || number is >= 500 and <= 599;
        var fatal = number is 401 or 403;
        var retryAfter = retry?.Delta;
        if (retryAfter is null && retry?.Date is DateTimeOffset date)
        {
            retryAfter = date - DateTimeOffset.UtcNow;
            if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
        }
        if (retryAfter > TimeSpan.FromSeconds(60)) retryAfter = TimeSpan.FromSeconds(60);
        return new UploadFailure($"{stage}_http_{number}", transient, fatal, retryAfter);
    }

    private async Task<PutResult.Failure> ClassifyPutFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var json = await ReadJsonAsync(response, cancellationToken);
            if (json.RootElement.ValueKind == JsonValueKind.Object)
            {
                var message = GetErrorMessage(json.RootElement)?.ToLowerInvariant();
                if (message is not null)
                {
                    if (message.Contains("authorization token is empty", StringComparison.Ordinal))
                        return AuthenticationFailure("put_auth_empty");
                    if (message.Contains("token must consist of 3 parts", StringComparison.Ordinal))
                        return AuthenticationFailure("put_token_malformed");
                    if (message.Contains("expired token", StringComparison.Ordinal) ||
                        message.Contains("token expired", StringComparison.Ordinal))
                        return AuthenticationFailure("put_token_expired");
                    if (message.Contains("invalid token", StringComparison.Ordinal) ||
                        message.Contains("unauthorized", StringComparison.Ordinal) ||
                        message.Contains("authentication failed", StringComparison.Ordinal))
                        return AuthenticationFailure("put_auth_invalid");
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or ResponseTooLargeException or IOException)
        {
        }

        return FailurePut(response.StatusCode, response.Headers.RetryAfter);
    }

    private static string? GetErrorMessage(JsonElement root)
    {
        foreach (var name in new[] { "error", "message" })
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private static PutResult.Failure AuthenticationFailure(string code) =>
        new(new UploadFailure(code, false, true, null));

    private sealed class ResponseTooLargeException : Exception;
}
