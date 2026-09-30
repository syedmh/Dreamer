using System.Net;
using TCFUploader.Configuration;
using TCFUploader.Tests.State;
using TCFUploader.Tests.TestDoubles;
using TCFUploader.Upload;

namespace TCFUploader.Tests.Integration;

[TestClass]
public sealed class SourcePreservationTests
{
    [TestMethod]
    public async Task AC17_AllOutcomes_LeaveSourceMetadataAndBytesUnchanged()
    {
        foreach (var scenario in new[] { "success", "retry", "failure" })
        {
            using var paths = new TestPaths();
            var source = Path.Combine(paths.Watch, "photo.jpg");
            var bytes = new byte[] { 1, 2, 3, 4 };
            var clock = new FakeClock();
            var (repository, snapshot) = await StateRepositoryTests.CreatePendingAsync(paths, clock, bytes);
            var before = Capture(source);
            var handler = new ScriptedHttpMessageHandler();
            if (scenario == "success")
            {
                handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/object"}""");
                handler.Enqueue(HttpStatusCode.OK, """{"success":true}""");
            }
            else if (scenario == "retry")
            {
                handler.Enqueue(HttpStatusCode.InternalServerError, "{}");
                handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/object"}""");
                handler.Enqueue(HttpStatusCode.OK, """{"success":true}""");
            }
            else
            {
                handler.Enqueue(HttpStatusCode.BadRequest, "{}");
            }
            using var http = new HttpClient(handler);
            await using (repository)
                await new UploadWorker(repository, new LumaBoothClient(http, RuntimeOptions.Default),
                    new RetryPolicy(clock, RuntimeOptions.Default), clock, RuntimeOptions.Default, "token")
                    .ProcessAsync(snapshot.Fingerprint, default);
            AssertUnchanged(source, before, scenario);
        }

        using var cancellationPaths = new TestPaths();
        var cancellationSource = Path.Combine(cancellationPaths.Watch, "photo.jpg");
        var cancellationBytes = new byte[] { 5, 6, 7 };
        var cancellationClock = new FakeClock();
        var (cancellationRepository, cancellationSnapshot) = await StateRepositoryTests.CreatePendingAsync(
            cancellationPaths, cancellationClock, cancellationBytes);
        var cancellationBefore = Capture(cancellationSource);
        var blocking = new BlockingHandler();
        using var cancellationHttp = new HttpClient(blocking);
        using var cancellation = new CancellationTokenSource();
        var processing = new UploadWorker(
            cancellationRepository,
            new LumaBoothClient(cancellationHttp, RuntimeOptions.Default),
            new RetryPolicy(cancellationClock, RuntimeOptions.Default),
            cancellationClock,
            RuntimeOptions.Default,
            "token").ProcessAsync(cancellationSnapshot.Fingerprint, cancellation.Token);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        try
        {
            await processing;
            Assert.Fail("Cancellation was expected.");
        }
        catch (OperationCanceledException)
        {
        }
        await cancellationRepository.DisposeAsync();
        AssertUnchanged(cancellationSource, cancellationBefore, "cancellation");
    }

    private static SourceState Capture(string path) => new(
        Path.GetFullPath(path),
        File.ReadAllBytes(path),
        new FileInfo(path).Length,
        File.GetLastWriteTimeUtc(path));

    private static void AssertUnchanged(string path, SourceState before, string scenario)
    {
        Assert.AreEqual(before.FullPath, Path.GetFullPath(path), scenario);
        CollectionAssert.AreEqual(before.Bytes, File.ReadAllBytes(path), scenario);
        Assert.AreEqual(before.Length, new FileInfo(path).Length, scenario);
        Assert.AreEqual(before.LastWriteUtc, File.GetLastWriteTimeUtc(path), scenario);
    }

    private sealed record SourceState(string FullPath, byte[] Bytes, long Length, DateTime LastWriteUtc);

    private sealed class BlockingHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
