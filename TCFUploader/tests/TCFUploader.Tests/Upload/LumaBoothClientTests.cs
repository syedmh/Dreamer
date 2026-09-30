using System.Net;
using System.Text;
using TCFUploader.Configuration;
using TCFUploader.State;
using TCFUploader.Tests.TestDoubles;
using TCFUploader.Upload;

namespace TCFUploader.Tests.Upload;

[TestClass]
public sealed class LumaBoothClientTests
{
    [TestMethod]
    public async Task AC07_Put_UsesPersistedKeyExactBytesTypeAndBearerHeaderOnly()
    {
        using var paths = new TestPaths();
        var spool = Path.Combine(paths.Root, "payload");
        var bytes = Encoding.UTF8.GetBytes("exact bytes");
        await File.WriteAllBytesAsync(spool, bytes);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/object"}""");
        using var http = new HttpClient(handler);
        var result = await new LumaBoothClient(http, RuntimeOptions.Default)
            .PutAsync(Item(bytes.Length), spool, "test-token", default);
        Assert.IsInstanceOfType<PutResult.Success>(result);
        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Put, request.Method);
        Assert.IsTrue(request.Uri.AbsoluteUri.EndsWith("/tcfuploader-0123456789abcdef0123456789abcdef.jpg", StringComparison.Ordinal));
        Assert.AreEqual("image/jpeg", request.ContentType);
        Assert.AreEqual("Bearer", request.Authorization?.Scheme);
        Assert.AreEqual("test-token", request.Authorization?.Parameter);
        CollectionAssert.AreEqual(bytes, request.Body);
        Assert.IsFalse(request.Uri.AbsoluteUri.Contains("test-token", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AC09_RelativePutUrl_ResolvesAgainstFixedMediaBase()
    {
        using var paths = new TestPaths();
        var spool = Path.Combine(paths.Root, "payload");
        await File.WriteAllBytesAsync(spool, [1]);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/object"}""");
        using var http = new HttpClient(handler);
        var result = (PutResult.Success)await new LumaBoothClient(http, RuntimeOptions.Default)
            .PutAsync(Item(1), spool, "token", default);
        Assert.AreEqual("https://fotoshare.s3.us-east-005.backblazeb2.com/bucket/object", result.RemoteUrl.AbsoluteUri);
    }

    [TestMethod]
    public async Task BrowserIdentity_PrefixesPutKeyWithCurrentEventAndUserPath()
    {
        using var paths = new TestPaths();
        var spool = Path.Combine(paths.Root, "payload");
        await File.WriteAllBytesAsync(spool, [1]);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/object"}""");
        using var http = new HttpClient(handler);

        await new LumaBoothClient(http, RuntimeOptions.Default, "user/123")
            .PutAsync(Item(1), spool, "token", default);

        Assert.AreEqual(
            "https://w.fotoshare.co/upload_file/lb/event/user%2F123/-P1Y143qUTagjT1hDguA/uploads/tcfuploader-0123456789abcdef0123456789abcdef.jpg",
            handler.Requests.Single().Uri.AbsoluteUri);
    }

    [TestMethod]
    public async Task AC10_AbsolutePutUrl_AcceptsOnlyAllowlistedHttpsMediaHost()
    {
        using var paths = new TestPaths();
        var spool = Path.Combine(paths.Root, "payload");
        await File.WriteAllBytesAsync(spool, [1]);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"url":"https://fotoshare.s3.us-east-005.backblazeb2.com/object"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"url":"http://fotoshare.s3.us-east-005.backblazeb2.com/object"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"url":"https://evil.example/object"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"url":"https://fotoshare.s3.us-east-005.backblazeb2.com.evil.example/object"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"url":"https://user@fotoshare.s3.us-east-005.backblazeb2.com/object"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"url":"https://fotoshare.s3.us-east-005.backblazeb2.com:444/object"}""");
        using var http = new HttpClient(handler);
        var client = new LumaBoothClient(http, RuntimeOptions.Default);
        Assert.IsInstanceOfType<PutResult.Success>(await client.PutAsync(Item(1), spool, "token", default));
        Assert.AreEqual("put_url_rejected",
            Assert.IsInstanceOfType<PutResult.Failure>(
                await client.PutAsync(Item(1), spool, "token", default)).Error.OutcomeCode);
        for (var i = 0; i < 3; i++)
            Assert.AreEqual("put_url_rejected",
                Assert.IsInstanceOfType<PutResult.Failure>(
                    await client.PutAsync(Item(1), spool, "token", default)).Error.OutcomeCode);
        Assert.AreEqual("put_url_rejected",
            Assert.IsInstanceOfType<PutResult.Failure>(
                await client.PutAsync(Item(1), spool, "token", default)).Error.OutcomeCode);
    }

    [TestMethod]
    public async Task AC11_Post_UsesExactEndpointMultipartFieldsTokenAndByteLength()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"success":true}""");
        using var http = new HttpClient(handler);
        var item = Item(123) with { Status = UploadStatus.PutComplete, RemoteUrl = UploaderConstants.MediaBaseUri + "bucket/object" };
        Assert.IsInstanceOfType<PostResult.Success>(
            await new LumaBoothClient(http, RuntimeOptions.Default).PostAsync(item, "token", default));
        var request = handler.Requests.Single();
        Assert.AreEqual(UploaderConstants.PostUri, request.Uri);
        Assert.AreEqual("Bearer", request.Authorization?.Scheme);
        Assert.AreEqual("token", request.Authorization?.Parameter);
        var body = Encoding.UTF8.GetString(request.Body);
        StringAssert.Contains(body, "uploadFileField");
        StringAssert.Contains(body, "imgSize");
        StringAssert.Contains(body, "123");
        StringAssert.Contains(body, "imgWidth");
        StringAssert.Contains(body, "imgHeight");
    }

    [TestMethod]
    public async Task AC14_WholeAttemptTimeoutAndBodyDisconnect_AreTransient()
    {
        using var paths = new TestPaths();
        var spool = Path.Combine(paths.Root, "payload");
        await File.WriteAllBytesAsync(spool, [1]);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream())
        });
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new DisconnectingStream())
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new LumaBoothClient(http, RuntimeOptions.Default with
        {
            HttpAttemptTimeout = TimeSpan.FromMilliseconds(50)
        });
        var timeout = Assert.IsInstanceOfType<PutResult.Failure>(
            await client.PutAsync(Item(1), spool, "token", default));
        Assert.AreEqual("put_timeout", timeout.Error.OutcomeCode);
        Assert.IsTrue(timeout.Error.IsTransient);
        var disconnect = Assert.IsInstanceOfType<PutResult.Failure>(
            await client.PutAsync(Item(1), spool, "token", default));
        Assert.AreEqual("put_network", disconnect.Error.OutcomeCode);
        Assert.IsTrue(disconnect.Error.IsTransient);
    }

    [TestMethod]
    public async Task AC13_ResponseShapeAndStatusMatrix_ReturnsPreciseStageOutcomes()
    {
        using var paths = new TestPaths();
        var spool = Path.Combine(paths.Root, "payload");
        await File.WriteAllBytesAsync(spool, [1]);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{bad");
        handler.Enqueue(HttpStatusCode.BadRequest, "{}");
        handler.Enqueue(HttpStatusCode.OK, """{"url":""}""");
        handler.Enqueue(HttpStatusCode.OK, "{bad");
        handler.Enqueue(HttpStatusCode.BadRequest, "{}");
        handler.Enqueue(HttpStatusCode.OK, """{"success":false}""");
        handler.Enqueue(HttpStatusCode.OK, """{}""");
        using var http = new HttpClient(handler);
        var client = new LumaBoothClient(http, RuntimeOptions.Default);
        Assert.AreEqual("put_json_invalid",
            Assert.IsInstanceOfType<PutResult.Failure>(await client.PutAsync(Item(1), spool, "token", default)).Error.OutcomeCode);
        Assert.AreEqual("put_http_400",
            Assert.IsInstanceOfType<PutResult.Failure>(await client.PutAsync(Item(1), spool, "token", default)).Error.OutcomeCode);
        Assert.AreEqual("put_protocol",
            Assert.IsInstanceOfType<PutResult.Failure>(await client.PutAsync(Item(1), spool, "token", default)).Error.OutcomeCode);
        var postItem = Item(1) with
        {
            Status = UploadStatus.PutComplete,
            RemoteUrl = UploaderConstants.MediaBaseUri + "bucket/object"
        };
        Assert.AreEqual("post_json_invalid",
            Assert.IsInstanceOfType<PostResult.Failure>(await client.PostAsync(postItem, "token", default)).Error.OutcomeCode);
        Assert.AreEqual("post_http_400",
            Assert.IsInstanceOfType<PostResult.Failure>(await client.PostAsync(postItem, "token", default)).Error.OutcomeCode);
        Assert.AreEqual("post_protocol",
            Assert.IsInstanceOfType<PostResult.Failure>(await client.PostAsync(postItem, "token", default)).Error.OutcomeCode);
        Assert.AreEqual("post_protocol",
            Assert.IsInstanceOfType<PostResult.Failure>(await client.PostAsync(postItem, "token", default)).Error.OutcomeCode);
    }

    [TestMethod]
    public async Task Put_ServerCredentialErrorsEncodedAs500_AreFatalAndSpecific()
    {
        using var paths = new TestPaths();
        var spool = Path.Combine(paths.Root, "payload");
        await File.WriteAllBytesAsync(spool, [1]);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError,
            """{"error":"Token Error: Error: token must consist of 3 parts"}""");
        using var http = new HttpClient(handler);

        var failure = Assert.IsInstanceOfType<PutResult.Failure>(
            await new LumaBoothClient(http, RuntimeOptions.Default, "user-123")
                .PutAsync(Item(1), spool, "token", default));

        Assert.AreEqual("put_token_malformed", failure.Error.OutcomeCode);
        Assert.IsTrue(failure.Error.IsAuthenticationFatal);
        Assert.IsFalse(failure.Error.IsTransient);
    }

    private class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DisconnectingStream : StalledStream
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("connection reset"));
    }

    private static UploadItemState Item(long length) => new(
        UploadStatus.PendingPut, "photo.jpg", length, DateTime.UtcNow, new string('a', 64), length,
        "image/jpeg", ".jpg", "spool/x.payload",
        "tcfuploader-0123456789abcdef0123456789abcdef.jpg", null,
        DateTime.UtcNow, DateTime.UtcNow, null, null, null, 0);
}
