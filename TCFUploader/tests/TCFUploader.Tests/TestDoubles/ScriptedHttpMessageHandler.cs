using System.Net;
using System.Net.Http.Headers;

namespace TCFUploader.Tests.TestDoubles;

internal sealed record CapturedRequest(
    HttpMethod Method, Uri Uri, AuthenticationHeaderValue? Authorization, string? ContentType, byte[] Body);

internal sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> responses = new();
    internal List<CapturedRequest> Requests { get; } = [];
    internal int ActiveRequests;
    internal int MaxActiveRequests;

    internal void Enqueue(HttpStatusCode status, string json, Action<HttpResponseMessage>? configure = null) =>
        responses.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(json) };
            configure?.Invoke(response);
            return response;
        });

    internal void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        responses.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref ActiveRequests);
        MaxActiveRequests = Math.Max(MaxActiveRequests, active);
        try
        {
            if (responses.Count == 0) throw new InvalidOperationException("Unexpected HTTP request.");
            var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, request.Headers.Authorization,
                request.Content?.Headers.ContentType?.MediaType, bytes));
            return responses.Dequeue()(request);
        }
        finally { Interlocked.Decrement(ref ActiveRequests); }
    }
}
