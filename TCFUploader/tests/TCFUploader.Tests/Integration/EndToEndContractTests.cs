using System.Net;
using TCFUploader.Configuration;
using TCFUploader.Discovery;
using TCFUploader.Files;
using TCFUploader.Hosting;
using TCFUploader.State;
using TCFUploader.Tests.TestDoubles;
using TCFUploader.Upload;

namespace TCFUploader.Tests.Integration;

[TestClass]
public sealed class EndToEndContractTests
{
    private static RuntimeOptions Fast => RuntimeOptions.Default with
    {
        ObservationInterval = TimeSpan.FromMilliseconds(10),
        ReconciliationInterval = TimeSpan.FromMilliseconds(30),
        ShutdownGracePeriod = TimeSpan.FromSeconds(2)
    };

    [TestMethod]
    public async Task AC03_StartupBacklog_UploadsEachVersionOnceAndSkipsRestart()
    {
        using var paths = new TestPaths();
        var rootFile = Path.Combine(paths.Watch, "root.jpg");
        var nestedFile = Path.Combine(paths.Watch, "nested", "photo.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedFile)!);
        await File.WriteAllBytesAsync(rootFile, [9]);
        await File.WriteAllBytesAsync(nestedFile, [1, 2, 3]);
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpMessageHandler();
        for (var i = 0; i < 2; i++)
        {
            handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/object"}""");
            handler.Enqueue(HttpStatusCode.OK, """{"success":true}""", i == 1 ? _ => stop.Cancel() : null);
        }
        var result = await new WatcherCoordinator(Fast, handlerFactory: () => handler, localAppData: paths.Local,
            changeFeedFactory: _ => new FakeChangeFeed())
            .RunAsync(paths.Watch, "token", stop.Token, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(ExitCodes.Success, result);
        Assert.AreEqual(4, handler.Requests.Count);
        var clock = new FakeClock();
        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
        await using (reopened.Repository)
            Assert.AreEqual(0, reopened.Repository.GetResumableWork(clock.UtcNow).Count);

        var secondHandler = new ScriptedHttpMessageHandler();
        using var secondStop = new CancellationTokenSource(100);
        Assert.AreEqual(ExitCodes.Success, await new WatcherCoordinator(
            Fast, handlerFactory: () => secondHandler, localAppData: paths.Local,
            changeFeedFactory: _ => new FakeChangeFeed())
            .RunAsync(paths.Watch, "token", secondStop.Token, CancellationToken.None));
        Assert.AreEqual(0, secondHandler.Requests.Count);
    }

    [TestMethod]
    public async Task AC04_NestedLiveArrival_ProcessesAfterStability()
    {
        using var paths = new TestPaths();
        var feed = new FakeChangeFeed();
        using var stop = new CancellationTokenSource();
        var handler = SuccessfulHandler(stop);
        var run = new WatcherCoordinator(Fast, handlerFactory: () => handler, localAppData: paths.Local,
            changeFeedFactory: _ => feed)
            .RunAsync(paths.Watch, "token", stop.Token, CancellationToken.None);
        await Task.Delay(50);
        var file = Path.Combine(paths.Watch, "live", "photo.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, [4, 5, 6]);
        feed.Emit(new FileChange(FileChangeKind.Upsert, file));
        Assert.AreEqual(ExitCodes.Success, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task StartupReconciliation_UploadsOnlyMixedCaseJpgJpegAndPng()
    {
        using var paths = new TestPaths();
        var supported = new[] { "one.JpG", "two.JPEG", "three.pNg" };
        var unsupported = new[]
        {
            "image.gif",
            "image.webp",
            "video.mp4",
            "video.mov",
            "notes.txt",
            "extensionless",
            "photo.jpg.exe"
        };
        foreach (var name in supported.Concat(unsupported))
            await File.WriteAllBytesAsync(Path.Combine(paths.Watch, name), [1, 2, 3]);

        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpMessageHandler();
        for (var i = 0; i < supported.Length; i++)
        {
            handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/object"}""");
            handler.Enqueue(
                HttpStatusCode.OK,
                """{"success":true}""",
                i == supported.Length - 1 ? _ => stop.Cancel() : null);
        }

        var result = await new WatcherCoordinator(
                Fast,
                handlerFactory: () => handler,
                localAppData: paths.Local,
                changeFeedFactory: _ => new FakeChangeFeed())
            .RunAsync(paths.Watch, "token", stop.Token, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(ExitCodes.Success, result);
        Assert.AreEqual(supported.Length * 2, handler.Requests.Count);
        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
        await using (reopened.Repository)
        {
            CollectionAssert.AreEquivalent(
                supported,
                reopened.Repository.Snapshot.Items.Values.Select(item => item.RelativePath).ToArray());
            Assert.IsTrue(reopened.Repository.Snapshot.Items.Values.All(
                item => item.Status == UploadStatus.Completed));
            Assert.AreEqual(0, Directory.EnumerateFiles(reopened.Repository.SpoolDirectory).Count());
        }
    }

    [TestMethod]
    public async Task InjectedLiveEvents_IgnoreUnsupportedWithoutHttpStateOrSpoolActivity()
    {
        using var paths = new TestPaths();
        var feed = new FakeChangeFeed();
        var metrics = new CoordinatorMetrics();
        StateRepository? runningRepository = null;
        using var stop = new CancellationTokenSource();
        var handler = SuccessfulHandler(stop);
        var run = new WatcherCoordinator(
            Fast,
            handlerFactory: () => handler,
            localAppData: paths.Local,
            changeFeedFactory: _ => feed,
            metrics: metrics,
            snapshotterFactory: repository =>
            {
                runningRepository = repository;
                return new FileSnapshotter(
                    repository.SpoolDirectory,
                    repository.TrustedRoot,
                    repository.SpoolRootTrust,
                    repository.SpoolBudget);
            })
            .RunAsync(paths.Watch, "token", stop.Token, CancellationToken.None);
        await WaitUntilAsync(() => runningRepository is not null, TimeSpan.FromSeconds(2));

        var unsupported = new[]
        {
            "image.gif",
            "image.webp",
            "video.mp4",
            "video.mov",
            "notes.txt",
            "extensionless",
            "photo.jpg.exe"
        };
        foreach (var name in unsupported)
        {
            var path = Path.Combine(paths.Watch, name);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            feed.Emit(new FileChange(FileChangeKind.Upsert, path));
        }

        await Task.Delay(150);
        Assert.AreEqual(0, handler.Requests.Count);
        Assert.AreEqual(0, runningRepository!.Snapshot.Items.Count);
        Assert.AreEqual(0, Directory.EnumerateFiles(runningRepository.SpoolDirectory).Count());
        Assert.AreEqual(0, metrics.CandidateHighWater);

        var supported = Path.Combine(paths.Watch, "live.JpEg");
        await File.WriteAllBytesAsync(supported, [4, 5, 6]);
        feed.Emit(new FileChange(FileChangeKind.Upsert, supported));

        Assert.AreEqual(ExitCodes.Success, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(2, handler.Requests.Count);
        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
        await using (reopened.Repository)
        {
            var item = reopened.Repository.Snapshot.Items.Values.Single();
            Assert.AreEqual("live.JpEg", item.RelativePath);
            Assert.AreEqual("image/jpeg", item.ContentType);
            Assert.AreEqual(UploadStatus.Completed, item.Status);
            Assert.AreEqual(0, Directory.EnumerateFiles(reopened.Repository.SpoolDirectory).Count());
        }
    }

    [TestMethod]
    public async Task AC06_NotificationLossOrOverflow_ReconciliationRecoversFile()
    {
        using var paths = new TestPaths();
        using var stop = new CancellationTokenSource();
        var handler = SuccessfulHandler(stop);
        var feed = new FakeChangeFeed();
        var run = new WatcherCoordinator(Fast with { ReconciliationInterval = TimeSpan.FromMinutes(1) },
            handlerFactory: () => handler, localAppData: paths.Local,
            changeFeedFactory: _ => feed)
            .RunAsync(paths.Watch, "token", stop.Token, CancellationToken.None);
        await Task.Delay(50);
        var file = Path.Combine(paths.Watch, "missed.jpg");
        await File.WriteAllBytesAsync(file, [7]);
        feed.Emit(new FileChange(FileChangeKind.Overflow, null));
        Assert.AreEqual(ExitCodes.Success, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task AC24_MockContract_OnePutThenOnePostAndDurableCompletion()
    {
        using var paths = new TestPaths();
        var file = Path.Combine(paths.Watch, "contract.jpg");
        var bytes = new byte[] { 9, 8, 7, 6 };
        await File.WriteAllBytesAsync(file, bytes);
        var handler = await ProcessDiscoveredFile(paths, file);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Put, handler.Requests[0].Method);
        Assert.AreEqual("https://w.fotoshare.co", handler.Requests[0].Uri.GetLeftPart(UriPartial.Authority));
        CollectionAssert.AreEqual(bytes, handler.Requests[0].Body);
        Assert.AreEqual(bytes.Length, handler.Requests[0].Body.Length);
        Assert.AreEqual("image/jpeg", handler.Requests[0].ContentType);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[1].Method);
        Assert.AreEqual(UploaderConstants.PostUri, handler.Requests[1].Uri);
        var multipart = System.Text.Encoding.UTF8.GetString(handler.Requests[1].Body);
        StringAssert.Contains(multipart, "name=uploadFileField");
        StringAssert.Contains(multipart, "https://fotoshare.s3.us-east-005.backblazeb2.com/bucket/object");
        StringAssert.Contains(multipart, "name=imgSize");
        StringAssert.Contains(multipart, bytes.Length.ToString());
        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
        await using (reopened.Repository)
            Assert.IsTrue(reopened.Repository.Snapshot.Items.Values.All(item => item.Status == UploadStatus.Completed));
    }

    [TestMethod]
    public async Task AC16_Coordinator_ReplacedFileUploadsNewVersionAndUnchangedFileSkips()
    {
        using var paths = new TestPaths();
        var file = Path.Combine(paths.Watch, "replace.jpg");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);

        using var firstStop = new CancellationTokenSource();
        var firstHandler = SuccessfulHandler(firstStop);
        Assert.AreEqual(
            ExitCodes.Success,
            await new WatcherCoordinator(
                    Fast,
                    handlerFactory: () => firstHandler,
                    localAppData: paths.Local,
                    changeFeedFactory: _ => new FakeChangeFeed())
                .RunAsync(paths.Watch, "token", firstStop.Token, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(2, firstHandler.Requests.Count);

        using var unchangedStop = new CancellationTokenSource(150);
        var unchangedHandler = new ScriptedHttpMessageHandler();
        Assert.AreEqual(
            ExitCodes.Success,
            await new WatcherCoordinator(
                    Fast,
                    handlerFactory: () => unchangedHandler,
                    localAppData: paths.Local,
                    changeFeedFactory: _ => new FakeChangeFeed())
                .RunAsync(paths.Watch, "token", unchangedStop.Token, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0, unchangedHandler.Requests.Count);

        var priorWrite = File.GetLastWriteTimeUtc(file);
        await File.WriteAllBytesAsync(file, [4, 5, 6, 7]);
        File.SetLastWriteTimeUtc(file, priorWrite.AddSeconds(1));
        using var changedStop = new CancellationTokenSource();
        var changedHandler = SuccessfulHandler(changedStop);
        Assert.AreEqual(
            ExitCodes.Success,
            await new WatcherCoordinator(
                    Fast,
                    handlerFactory: () => changedHandler,
                    localAppData: paths.Local,
                    changeFeedFactory: _ => new FakeChangeFeed())
                .RunAsync(paths.Watch, "token", changedStop.Token, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(2, changedHandler.Requests.Count);

        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
        await using (reopened.Repository)
        {
            Assert.AreEqual(2, reopened.Repository.Snapshot.Items.Count);
            Assert.IsTrue(reopened.Repository.Snapshot.Items.Values.All(
                item => item.Status == UploadStatus.Completed));
        }
    }

    private static async Task<ScriptedHttpMessageHandler> ProcessDiscoveredFile(TestPaths paths, string file)
    {
        var clock = new FakeClock();
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
        await using var repository = opened.Repository;
        var relative = Path.GetRelativePath(paths.Watch, file);
        var info = new FileInfo(file);
        var ready = (SnapshotResult.Ready)await new FileSnapshotter(repository.SpoolDirectory)
            .TrySnapshotAsync(file, relative, new FileObservation(info.Length, info.LastWriteTimeUtc), default);
        if (!repository.ContainsActiveOrCompleted(ready.Snapshot.Fingerprint))
            await repository.AddPendingAsync(ready.Snapshot, FileSnapshotter.CreateRemoteKey(ready.Snapshot.Extension), default);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/object"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"success":true}""");
        using var http = new HttpClient(handler);
        await new UploadWorker(repository, new LumaBoothClient(http, RuntimeOptions.Default),
            new RetryPolicy(clock, RuntimeOptions.Default), clock, RuntimeOptions.Default, "token")
            .ProcessAsync(ready.Snapshot.Fingerprint, default);
        return handler;
    }

    private static ScriptedHttpMessageHandler SuccessfulHandler(CancellationTokenSource stop)
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/object"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"success":true}""", _ => stop.Cancel());
        return handler;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                Assert.Fail("Condition was not met before the timeout.");
            await Task.Delay(10);
        }
    }
}
