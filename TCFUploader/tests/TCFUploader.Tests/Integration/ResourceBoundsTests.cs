using System.Net;
using System.Security.Cryptography;
using TCFUploader.Configuration;
using TCFUploader.Discovery;
using TCFUploader.Files;
using TCFUploader.Hosting;
using TCFUploader.State;
using TCFUploader.Tests.TestDoubles;
using TCFUploader.Upload;

namespace TCFUploader.Tests.Integration;

[TestClass]
public sealed class ResourceBoundsTests
{
    [TestMethod]
    [TestCategory("Resource")]
    public async Task AC20_LargeFile_StreamsWithSublinearManagedMemoryAndExactBytes()
    {
        using var paths = new TestPaths();
        var file = Path.Combine(paths.Watch, "large.jpg");
        var block = new byte[1024 * 1024];
        RandomNumberGenerator.Fill(block);
        await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, block.Length, true))
            for (var i = 0; i < 128; i++) await output.WriteAsync(block);
        var clock = new FakeClock();
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
        await using var repository = opened.Repository;
        var info = new FileInfo(file);
        var ready = (SnapshotResult.Ready)await new FileSnapshotter(repository.SpoolDirectory).TrySnapshotAsync(
            file, "large.jpg", new FileObservation(info.Length, info.LastWriteTimeUtc), default);
        await repository.AddPendingAsync(ready.Snapshot, FileSnapshotter.CreateRemoteKey(".jpg"), default);
        var handler = new CountingHandler();
        using var http = new HttpClient(handler);
        var before = GC.GetTotalMemory(true);
        var result = await new LumaBoothClient(http, RuntimeOptions.Default)
            .PutAsync(repository.GetRequired(ready.Snapshot.Fingerprint), repository.ResolveSpool(repository.GetRequired(ready.Snapshot.Fingerprint)), "token", default);
        var growth = GC.GetTotalMemory(true) - before;
        Assert.IsInstanceOfType<PutResult.Success>(result);
        Assert.AreEqual(info.Length, handler.Bytes);
        Assert.IsTrue(growth < 32L * 1024 * 1024, $"Managed memory grew by {growth} bytes.");
    }

    [TestMethod]
    public async Task AC21_NotificationBurst_RemainsBoundedAndReconciliationCompletes()
    {
        using var paths = new TestPaths();
        var files = new List<string>();
        const int notificationCount = 4096;
        for (var i = 0; i < notificationCount; i++)
        {
            var file = Path.Combine(paths.Watch, $"burst-{i}.jpg");
            File.WriteAllBytes(file, []);
            files.Add(file);
        }
        var initial = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
        await using (initial.Repository)
            Assert.AreEqual(0, initial.Repository.Snapshot.Items.Count);
        var notifications = files.Select(path => new FileChange(FileChangeKind.Upsert, path))
            .ToArray();
        Assert.AreEqual(notificationCount, notifications.Select(change => change.FullPath).Distinct(
            StringComparer.OrdinalIgnoreCase).Count());
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpMessageHandler();
        for (var i = 0; i < notificationCount; i++)
        {
            handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/object"}""");
            handler.Enqueue(HttpStatusCode.OK, """{"success":true}""",
                i == notificationCount - 1 ? _ => stop.Cancel() : null);
        }
        var options = RuntimeOptions.Default with
        {
            ObservationInterval = TimeSpan.FromMilliseconds(1),
            ReconciliationInterval = TimeSpan.FromMilliseconds(10),
            ShutdownGracePeriod = TimeSpan.FromSeconds(30),
            MaxJournalRecords = 20_000,
            MaxJournalBytes = 64L * 1024 * 1024
        };
        Assert.AreEqual(1024, options.ChangeInboxCapacity);
        Assert.AreEqual(64, options.UploadQueueCapacity);
        var metrics = new CoordinatorMetrics();
        var originalOutput = Console.Out;
        Console.SetOut(TextWriter.Null);
        int result;
        try
        {
            result = await new WatcherCoordinator(
                    options,
                    runtimeClock: new FakeClock(),
                    handlerFactory: () => handler,
                    localAppData: paths.Local,
                    changeFeedFactory: _ => new FakeChangeFeed(notifications),
                    metrics: metrics)
                .RunAsync(paths.Watch, "token", stop.Token, CancellationToken.None)
                .WaitAsync(TimeSpan.FromMinutes(5));
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
        Assert.AreEqual(ExitCodes.Success, result);
        Assert.AreEqual(notificationCount * 2, handler.Requests.Count);
        Assert.AreEqual(1, handler.MaxActiveRequests);
        Assert.IsTrue(metrics.ChangeInboxHighWater <= options.ChangeInboxCapacity,
            $"Inbox high-water was {metrics.ChangeInboxHighWater}.");
        Assert.IsTrue(metrics.UploadQueueHighWater <= options.UploadQueueCapacity,
            $"Upload queue high-water was {metrics.UploadQueueHighWater}.");
        Assert.IsTrue(
            metrics.ReconciliationRequests >= notificationCount - options.ChangeInboxCapacity,
            $"Expected at least {notificationCount - options.ChangeInboxCapacity} overflow-driven " +
            $"reconciliation requests, observed {metrics.ReconciliationRequests}.");
        Assert.IsTrue(metrics.Reconciliations > 0);
        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
        await using (reopened.Repository)
        {
            Assert.AreEqual(notificationCount, reopened.Repository.Snapshot.Items.Count);
            Assert.AreEqual(notificationCount, reopened.Repository.Snapshot.Items.Values.Count(
                item => item.Status == UploadStatus.Completed));
        }
        Console.WriteLine(
            $"AC21_PROOF notifications={notifications.Length} stableFiles={files.Count} " +
            $"newlyCompleted={notificationCount} requests={handler.Requests.Count} " +
            $"inboxHighWater={metrics.ChangeInboxHighWater} uploadQueueHighWater={metrics.UploadQueueHighWater} " +
            $"maxActiveHttp={handler.MaxActiveRequests} reconciliationRequests={metrics.ReconciliationRequests} " +
            $"reconciliations={metrics.Reconciliations}");
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        internal long Bytes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await using var sink = new CountingStream(value => Bytes += value);
            await request.Content!.CopyToAsync(sink, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"url":"/bucket/large"}""") };
        }
    }

    private sealed class CountingStream(Action<int> count) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Write(byte[] buffer, int offset, int countValue) => count(countValue);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { count(buffer.Length); return ValueTask.CompletedTask; }
        public override int Read(byte[] buffer, int offset, int countValue) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
