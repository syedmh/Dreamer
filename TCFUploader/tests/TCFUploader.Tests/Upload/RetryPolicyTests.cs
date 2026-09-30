using TCFUploader.Configuration;
using TCFUploader.Tests.TestDoubles;
using TCFUploader.Upload;

namespace TCFUploader.Tests.Upload;

[TestClass]
public sealed class RetryPolicyTests
{
    [TestMethod]
    public async Task AC14_TransientAndPermanentFailures_RespectAttemptAndDelayBounds()
    {
        var clock = new FakeClock();
        var policy = new RetryPolicy(clock, RuntimeOptions.Default);
        var transient = await policy.ExecuteAsync(
            (attempt, _) => Task.FromResult(attempt),
            value => value < 5 ? new UploadFailure("retry", true, false, null) : null, default);
        Assert.AreEqual(5, transient.Attempts);
        CollectionAssert.AreEqual(new[]
        {
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)
        }, clock.Delays);

        var jitterClock = new FakeClock();
        jitterClock.EnqueueJitter(250, 250, 250, 250);
        await new RetryPolicy(jitterClock, RuntimeOptions.Default).ExecuteAsync(
            (attempt, _) => Task.FromResult(attempt),
            value => value < 5 ? new UploadFailure("retry", true, false, null) : null, default);
        CollectionAssert.AreEqual(new[]
        {
            TimeSpan.FromMilliseconds(1_250), TimeSpan.FromMilliseconds(2_250),
            TimeSpan.FromMilliseconds(4_250), TimeSpan.FromMilliseconds(8_250)
        }, jitterClock.Delays);

        var permanent = await policy.ExecuteAsync(
            (_, _) => Task.FromResult(1),
            _ => new UploadFailure("permanent", false, false, null), default);
        Assert.AreEqual(1, permanent.Attempts);

        var cappedClock = new FakeClock();
        cappedClock.EnqueueJitter(250);
        var capped = await new RetryPolicy(cappedClock, RuntimeOptions.Default).ExecuteAsync(
            (attempt, _) => Task.FromResult(attempt),
            _ => new UploadFailure("retry", true, false, TimeSpan.FromMinutes(5)),
            default);
        Assert.AreEqual(5, capped.Attempts);
        Assert.IsTrue(cappedClock.Delays.All(delay => delay == TimeSpan.FromSeconds(60)));
    }

    [TestMethod]
    public async Task AC14_HttpClassification_CoversNetwork4084295xxAndOther4xx()
    {
        using var paths = new TestPaths();
        var spool = Path.Combine(paths.Root, "payload");
        await File.WriteAllBytesAsync(spool, [1]);
        var item = new TCFUploader.State.UploadItemState(
            TCFUploader.State.UploadStatus.PendingPut, "a.bin", 1, DateTime.UtcNow,
            new string('a', 64), 1, "application/octet-stream", ".bin", "spool/a.payload",
            "tcfuploader-0123456789abcdef0123456789abcdef.bin", null,
            DateTime.UtcNow, DateTime.UtcNow, null, null, null, 0);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(_ => throw new HttpRequestException("offline"));
        handler.Enqueue(System.Net.HttpStatusCode.RequestTimeout, "{}");
        handler.Enqueue(System.Net.HttpStatusCode.TooManyRequests, "{}", response =>
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7)));
        handler.Enqueue(System.Net.HttpStatusCode.InternalServerError, "{}");
        handler.Enqueue(System.Net.HttpStatusCode.BadRequest, "{}");
        using var http = new HttpClient(handler);
        var client = new LumaBoothClient(http, RuntimeOptions.Default);
        foreach (var expected in new[] { "put_network", "put_http_408", "put_http_429", "put_http_500" })
        {
            var failure = Assert.IsInstanceOfType<PutResult.Failure>(
                await client.PutAsync(item, spool, "token", default)).Error;
            Assert.AreEqual(expected, failure.OutcomeCode);
            Assert.IsTrue(failure.IsTransient);
            if (expected == "put_http_429")
                Assert.AreEqual(TimeSpan.FromSeconds(7), failure.RetryAfter);
        }
        var badRequest = Assert.IsInstanceOfType<PutResult.Failure>(
            await client.PutAsync(item, spool, "token", default)).Error;
        Assert.AreEqual("put_http_400", badRequest.OutcomeCode);
        Assert.IsFalse(badRequest.IsTransient);
    }
}
