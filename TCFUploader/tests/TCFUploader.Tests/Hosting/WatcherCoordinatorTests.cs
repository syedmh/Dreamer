using TCFUploader.Configuration;
using TCFUploader.Discovery;
using TCFUploader.Files;
using TCFUploader.Hosting;
using TCFUploader.State;
using TCFUploader.Tests.State;
using TCFUploader.Tests.TestDoubles;
using TCFUploader.Time;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace TCFUploader.Tests.Hosting;

[TestClass]
public sealed class WatcherCoordinatorTests
{
    private static RuntimeOptions Fast => RuntimeOptions.Default with
    {
        ObservationInterval = TimeSpan.FromMilliseconds(10),
        ReconciliationInterval = TimeSpan.FromMilliseconds(30),
        ShutdownGracePeriod = TimeSpan.FromMilliseconds(200)
    };

    [TestMethod]
    public async Task AC02_EmptyFolder_RunsUntilCleanCancellationWithoutHttp()
    {
        using var paths = new TestPaths();
        var handler = new ScriptedHttpMessageHandler();
        using var first = new CancellationTokenSource(80);
        using var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            var result = await new WatcherCoordinator(Fast, handlerFactory: () => handler, localAppData: paths.Local)
                .RunAsync(paths.Watch, "token", first.Token, CancellationToken.None);
            Assert.AreEqual(ExitCodes.Success, result);
            Assert.AreEqual(0, handler.Requests.Count);
            StringAssert.Contains(output.ToString(), "operation=monitoring");
            StringAssert.Contains(output.ToString(), "outcome=active");
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [TestMethod]
    public async Task SnapshotCapacityOutcome_IsLoggedAndDoesNotIssueHttp()
    {
        using var paths = new TestPaths();
        await File.WriteAllBytesAsync(Path.Combine(paths.Watch, "too-large.jpg"), [1, 2]);
        var handler = new ScriptedHttpMessageHandler();
        using var first = new CancellationTokenSource(150);
        using var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            var result = await new WatcherCoordinator(
                Fast with
                {
                    AggregateSpoolLimitBytes = 1,
                    MinimumFreeSpaceReserveBytes = 0
                },
                handlerFactory: () => handler,
                localAppData: paths.Local,
                changeFeedFactory: _ => new FakeChangeFeed())
                .RunAsync(paths.Watch, "token", first.Token, CancellationToken.None);
            Assert.AreEqual(ExitCodes.Success, result);
            Assert.AreEqual(0, handler.Requests.Count);
            StringAssert.Contains(output.ToString(), "operation=snapshot");
            StringAssert.Contains(output.ToString(), "outcome=spool_quota_exceeded");
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [TestMethod]
    public async Task AC19_CtrlC_GracefulCompletionOrForcedResumableCancellation()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var (repository, snapshot) = await StateRepositoryTests.CreatePendingAsync(paths, clock, [1, 2, 3]);
        await repository.DisposeAsync();
        var (additionalRepository, additionalSnapshot) = await StateRepositoryTests.CreatePendingAsync(
            paths, clock, [7, 8, 9], "additional.bin");
        await additionalRepository.DisposeAsync();
        const string stagedAfterShutdown = "staged-after-shutdown.jpg";
        await File.WriteAllBytesAsync(Path.Combine(paths.Watch, stagedAfterShutdown), [13, 14, 15]);
        using var first = new CancellationTokenSource();
        var completes = new BlockingHandler(completeWhenReleased: true);
        var gracefulRun = new WatcherCoordinator(Fast with { ShutdownGracePeriod = TimeSpan.FromSeconds(1) },
            handlerFactory: () => completes, localAppData: paths.Local)
            .RunAsync(paths.Watch, "token", first.Token, CancellationToken.None);
        await completes.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        first.Cancel();
        completes.Release.TrySetResult();
        Assert.AreEqual(ExitCodes.Success, await gracefulRun.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(1, completes.RequestCount);
        var gracefulReopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
        await using (gracefulReopened.Repository)
        {
            Assert.AreEqual(
                UploadStatus.PutComplete,
                gracefulReopened.Repository.Snapshot.Items[snapshot.Fingerprint].Status);
            Assert.IsTrue(gracefulReopened.Repository.GetResumableWork(DateTime.MaxValue).Any(
                work => work.Fingerprint == additionalSnapshot.Fingerprint));
            Assert.IsFalse(gracefulReopened.Repository.Snapshot.Items.Values.Any(
                item => item.RelativePath == stagedAfterShutdown));
        }

        using var forcedPaths = new TestPaths();
        var (forcedRepository, forcedSnapshot) = await StateRepositoryTests.CreatePendingAsync(
            forcedPaths, new FakeClock(), [4, 5, 6]);
        await forcedRepository.DisposeAsync();
        var (forcedAdditionalRepository, forcedAdditionalSnapshot) = await StateRepositoryTests.CreatePendingAsync(
            forcedPaths, new FakeClock(), [10, 11, 12], "forced-additional.bin");
        await forcedAdditionalRepository.DisposeAsync();
        const string forcedStagedAfterShutdown = "forced-staged-after-shutdown.jpg";
        await File.WriteAllBytesAsync(
            Path.Combine(forcedPaths.Watch, forcedStagedAfterShutdown), [16, 17, 18]);
        using var forcedFirst = new CancellationTokenSource();
        var blocks = new BlockingHandler(completeWhenReleased: false);
        var forcedRun = new WatcherCoordinator(Fast with { ShutdownGracePeriod = TimeSpan.FromMilliseconds(100) },
            handlerFactory: () => blocks, localAppData: forcedPaths.Local)
            .RunAsync(forcedPaths.Watch, "token", forcedFirst.Token, CancellationToken.None);
        await blocks.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        forcedFirst.Cancel();
        Assert.AreEqual(ExitCodes.ForcedShutdown, await forcedRun.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(1, blocks.RequestCount);
        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            forcedPaths.Watch, UploaderConstants.EventId, new FakeClock(), forcedPaths.Local, default);
        await using (reopened.Repository)
        {
            Assert.IsTrue(reopened.Repository.GetResumableWork(DateTime.MaxValue).Any(
                work => work.Fingerprint == forcedSnapshot.Fingerprint));
            Assert.IsTrue(reopened.Repository.GetResumableWork(DateTime.MaxValue).Any(
                work => work.Fingerprint == forcedAdditionalSnapshot.Fingerprint));
            Assert.IsFalse(reopened.Repository.Snapshot.Items.Values.Any(
                item => item.RelativePath == forcedStagedAfterShutdown));
        }
    }

    [TestMethod]
    public async Task FirstSignal_DuringPutRetryBackoff_ExitsCleanlyAndLeavesResumableState()
    {
        using var paths = new TestPaths();
        var (repository, snapshot) = await StateRepositoryTests.CreatePendingAsync(
            paths, new FakeClock(), [1, 2, 3]);
        await repository.DisposeAsync();
        var clock = new RetryBackoffClock();
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.ServiceUnavailable, "{}", response =>
            response.Headers.RetryAfter =
                new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60)));
        handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/object"}""");
        using var first = new CancellationTokenSource();
        var run = new WatcherCoordinator(
            Fast with { ShutdownGracePeriod = TimeSpan.FromMilliseconds(200) },
            runtimeClock: clock,
            handlerFactory: () => handler,
            localAppData: paths.Local,
            changeFeedFactory: _ => new FakeChangeFeed())
            .RunAsync(paths.Watch, "token", first.Token, CancellationToken.None);

        await clock.BackoffStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        first.Cancel();

        Assert.AreEqual(ExitCodes.Success, await run.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(1, handler.Requests.Count);
        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
        await using (reopened.Repository)
        {
            var item = reopened.Repository.Snapshot.Items[snapshot.Fingerprint];
            Assert.AreEqual(UploadStatus.PendingPut, item.Status);
            Assert.IsNull(item.LastCycleOutcome);
        }
    }

    [TestMethod]
    public async Task FirstSignal_DuringPostRetryBackoff_ExitsCleanlyAndLeavesResumableState()
    {
        using var paths = new TestPaths();
        var (repository, snapshot) = await StateRepositoryTests.CreatePendingAsync(
            paths, new FakeClock(), [1, 2, 3]);
        await repository.MarkPutCompleteAsync(
            snapshot.Fingerprint,
            new Uri("https://fotoshare.s3.us-east-005.backblazeb2.com/bucket/object"),
            default);
        await repository.DisposeAsync();
        var clock = new RetryBackoffClock();
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.ServiceUnavailable, "{}", response =>
            response.Headers.RetryAfter =
                new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60)));
        handler.Enqueue(HttpStatusCode.OK, """{"success":true}""");
        using var first = new CancellationTokenSource();
        var run = new WatcherCoordinator(
            Fast with { ShutdownGracePeriod = TimeSpan.FromMilliseconds(200) },
            runtimeClock: clock,
            handlerFactory: () => handler,
            localAppData: paths.Local,
            changeFeedFactory: _ => new FakeChangeFeed())
            .RunAsync(paths.Watch, "token", first.Token, CancellationToken.None);

        await clock.BackoffStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        first.Cancel();

        Assert.AreEqual(ExitCodes.Success, await run.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(1, handler.Requests.Count);
        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
        await using (reopened.Repository)
        {
            var item = reopened.Repository.Snapshot.Items[snapshot.Fingerprint];
            Assert.AreEqual(UploadStatus.PutComplete, item.Status);
            Assert.IsNull(item.LastCycleOutcome);
        }
    }

    [TestMethod]
    public async Task Reconciliation_FairAdmission_ReachesEligibleFileBehindCapacityBlockedCandidates()
    {
        using var paths = new TestPaths();
        const int capacity = 4;
        for (var i = 0; i < capacity; i++)
            await File.WriteAllBytesAsync(Path.Combine(paths.Watch, $"blocked-{i}.jpg"), [1, 2]);

        var eligible = Path.Combine(paths.Watch, "eligible.jpg");
        FakeChangeFeed? feed = null;
        feed = new FakeChangeFeed(
            onStart: () => _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                await File.WriteAllBytesAsync(eligible, []);
                feed!.Emit(new FileChange(FileChangeKind.Overflow, null));
            }));
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/eligible"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"success":true}""", _ => stop.Cancel());
        var options = Fast with
        {
            AggregateSpoolLimitBytes = 1,
            MinimumFreeSpaceReserveBytes = 0,
            MaxTrackedCandidates = capacity,
            DiscoveryBatchSize = capacity,
            ReconciliationInterval = TimeSpan.FromMilliseconds(20),
            ShutdownGracePeriod = TimeSpan.FromSeconds(2)
        };

        var result = await new WatcherCoordinator(
                options,
                handlerFactory: () => handler,
                localAppData: paths.Local,
                changeFeedFactory: _ => feed)
            .RunAsync(paths.Watch, "token", stop.Token, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(ExitCodes.Success, result);
        Assert.AreEqual(2, handler.Requests.Count);
        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
        await using (reopened.Repository)
        {
            var completed = reopened.Repository.Snapshot.Items.Values.Single(
                item => item.RelativePath == "eligible.jpg");
            Assert.AreEqual(UploadStatus.Completed, completed.Status);
        }
    }

    [TestMethod]
    public async Task Restart_WithMoreThanUploadQueueCapacity_DoesNotDeadlock()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        for (var i = 0; i < 65; i++)
        {
            var (repository, _) = await StateRepositoryTests.CreatePendingAsync(
                paths, clock, BitConverter.GetBytes(i), $"file-{i}.bin");
            await repository.DisposeAsync();
        }

        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpMessageHandler();
        for (var i = 0; i < 65; i++)
        {
            handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/object"}""");
            handler.Enqueue(HttpStatusCode.OK, """{"success":true}""",
                i == 64 ? _ => stop.Cancel() : null);
        }
        var result = await new WatcherCoordinator(Fast, handlerFactory: () => handler, localAppData: paths.Local,
            changeFeedFactory: _ => new FakeChangeFeed())
            .RunAsync(paths.Watch, "token", stop.Token, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(ExitCodes.Success, result);
        Assert.AreEqual(130, handler.Requests.Count);
        Assert.AreEqual(1, handler.MaxActiveRequests);
    }

    [TestMethod]
    public async Task StartupAdmission_FirstSignalWhileQueueIsBlocked_TerminatesForcedAndRemainsResumable()
    {
        using var paths = new TestPaths();
        for (var i = 0; i < 66; i++)
        {
            var (repository, snapshot) = await StateRepositoryTests.CreatePendingAsync(
                paths, new FakeClock(), BitConverter.GetBytes(i), $"blocked-{i}.bin");
            await repository.DisposeAsync();
        }

        using var first = new CancellationTokenSource();
        var handler = new BlockingHandler(completeWhenReleased: false);
        var run = new WatcherCoordinator(Fast with { ShutdownGracePeriod = TimeSpan.FromMilliseconds(100) },
            handlerFactory: () => handler, localAppData: paths.Local,
            changeFeedFactory: _ => new FakeChangeFeed())
            .RunAsync(paths.Watch, "token", first.Token, CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        first.Cancel();

        Assert.AreEqual(ExitCodes.ForcedShutdown, await run.WaitAsync(TimeSpan.FromSeconds(3)));
        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
        await using (reopened.Repository)
            Assert.AreEqual(66, reopened.Repository.GetResumableWork(DateTime.MaxValue).Count);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    public async Task StartupAdmission_AuthenticationFatalWhileQueueIsBlocked_TerminatesWithAuthentication(
        HttpStatusCode statusCode)
    {
        using var paths = new TestPaths();
        for (var i = 0; i < 66; i++)
        {
            var (repository, _) = await StateRepositoryTests.CreatePendingAsync(
                paths, new FakeClock(), BitConverter.GetBytes(i), $"auth-{i}.bin");
            await repository.DisposeAsync();
        }

        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(statusCode, "{}");
        var result = await new WatcherCoordinator(Fast,
            handlerFactory: () => handler, localAppData: paths.Local,
            changeFeedFactory: _ => new FakeChangeFeed())
            .RunAsync(paths.Watch, "token", CancellationToken.None, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.AreEqual(ExitCodes.Authentication, result);
        Assert.AreEqual(1, handler.Requests.Count);
        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
        await using (reopened.Repository)
            Assert.AreEqual(66, reopened.Repository.GetResumableWork(DateTime.MaxValue).Count);
    }

    [TestMethod]
    public async Task StartupAdmission_WorkerFaultWhileQueueIsBlocked_TerminatesFatalAndRemainsResumable()
    {
        using var paths = new TestPaths();
        for (var i = 0; i < 66; i++)
        {
            var (repository, _) = await StateRepositoryTests.CreatePendingAsync(
                paths, new FakeClock(), BitConverter.GetBytes(i), $"fault-{i}.bin");
            await repository.DisposeAsync();
        }

        var result = await new WatcherCoordinator(Fast,
            handlerFactory: () => new FaultingHandler(), localAppData: paths.Local,
            changeFeedFactory: _ => new FakeChangeFeed())
            .RunAsync(paths.Watch, "token", CancellationToken.None, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.AreEqual(ExitCodes.Fatal, result);
        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
        await using (reopened.Repository)
            Assert.AreEqual(66, reopened.Repository.GetResumableWork(DateTime.MaxValue).Count);
    }

    [TestMethod]
    public async Task ChangeFeedStartFailure_ClosesAdmissionBeforeQueuedOrPostHttpStarts()
    {
        using var paths = new TestPaths();
        var (repository, active) = await StateRepositoryTests.CreatePendingAsync(
            paths, new FakeClock(), [1, 2, 3], "active.bin");
        await repository.DisposeAsync();
        var (queuedRepository, queued) = await StateRepositoryTests.CreatePendingAsync(
            paths, new FakeClock(), [4, 5, 6], "queued.bin");
        await queuedRepository.DisposeAsync();
        var handler = new BlockingHandler(completeWhenReleased: true);
        var fatalFinalizationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new WatcherCoordinator(Fast with { ShutdownGracePeriod = TimeSpan.FromSeconds(2) },
            handlerFactory: () => handler, localAppData: paths.Local,
            changeFeedFactory: _ => new FakeChangeFeed(onStart: () =>
            {
                handler.Started.Task.Wait(TimeSpan.FromSeconds(2));
                throw new IOException("start failed");
            }, onStop: () => fatalFinalizationStarted.TrySetResult()))
            .RunAsync(paths.Watch, "token", CancellationToken.None, CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fatalFinalizationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, handler.RequestCount);
        handler.Release.TrySetResult();

        Assert.AreEqual(ExitCodes.Fatal, await run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(1, handler.RequestCount);
        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
        await using (reopened.Repository)
        {
            Assert.AreEqual(UploadStatus.PutComplete, reopened.Repository.GetRequired(active.Fingerprint).Status);
            Assert.AreEqual(UploadStatus.PendingPut, reopened.Repository.GetRequired(queued.Fingerprint).Status);
        }
    }

    [TestMethod]
    public async Task WatchedRootLoss_ClosesAdmissionBeforeQueuedOrPostHttpStarts()
    {
        using var paths = new TestPaths();
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, Fast, default);
        var active = await AddPendingAsync(opened.Repository, paths.Watch, "active.bin", [1, 2, 3]);
        var queued = await AddPendingAsync(opened.Repository, paths.Watch, "queued.bin", [4, 5, 6]);
        var handler = new BlockingHandler(completeWhenReleased: true);
        var fatalFinalizationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new WatcherCoordinator(Fast with { ShutdownGracePeriod = TimeSpan.FromSeconds(2) },
            handlerFactory: () => handler,
            changeFeedFactory: _ => new FakeChangeFeed(
                onStart: () =>
                {
                    handler.Started.Task.Wait(TimeSpan.FromSeconds(2));
                    Directory.Delete(paths.Watch, true);
                },
                onStop: () => fatalFinalizationStarted.TrySetResult()))
            .RunAsync(opened.Repository, "token", CancellationToken.None, CancellationToken.None);

        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fatalFinalizationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, handler.RequestCount);
        handler.Release.TrySetResult();

        Assert.AreEqual(ExitCodes.Fatal, await run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(1, handler.RequestCount);
        Assert.AreEqual(UploadStatus.PutComplete, opened.Repository.GetRequired(active.Fingerprint).Status);
        Assert.AreEqual(UploadStatus.PendingPut, opened.Repository.GetRequired(queued.Fingerprint).Status);
    }

    [TestMethod]
    public async Task GeneralCoordinatorFatal_ClosesAdmissionBeforeQueuedOrPostHttpStarts()
    {
        using var paths = new TestPaths();
        var (repository, active) = await StateRepositoryTests.CreatePendingAsync(
            paths, new FakeClock(), [1, 2, 3], "active.bin");
        await repository.DisposeAsync();
        var (queuedRepository, queued) = await StateRepositoryTests.CreatePendingAsync(
            paths, new FakeClock(), [4, 5, 6], "queued.bin");
        await queuedRepository.DisposeAsync();
        var handler = new BlockingHandler(completeWhenReleased: true);
        var fatalFinalizationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new WatcherCoordinator(Fast with { ShutdownGracePeriod = TimeSpan.FromSeconds(2) },
            handlerFactory: () => handler, localAppData: paths.Local,
            changeFeedFactory: _ => new FakeChangeFeed(onStart: () =>
            {
                handler.Started.Task.Wait(TimeSpan.FromSeconds(2));
                throw new InvalidOperationException("coordinator failed");
            }, onStop: () => fatalFinalizationStarted.TrySetResult()))
            .RunAsync(paths.Watch, "token", CancellationToken.None, CancellationToken.None);

        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fatalFinalizationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, handler.RequestCount);
        handler.Release.TrySetResult();

        Assert.AreEqual(ExitCodes.Fatal, await run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(1, handler.RequestCount);
        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
        await using (reopened.Repository)
        {
            Assert.AreEqual(UploadStatus.PutComplete, reopened.Repository.GetRequired(active.Fingerprint).Status);
            Assert.AreEqual(UploadStatus.PendingPut, reopened.Repository.GetRequired(queued.Fingerprint).Status);
        }
    }

    [TestMethod]
    public async Task ChangeFeedStopFatal_ClosesAdmissionBeforeQueuedOrPostHttpStarts()
    {
        using var paths = new TestPaths();
        var (repository, active) = await StateRepositoryTests.CreatePendingAsync(
            paths, new FakeClock(), [1, 2, 3], "active.bin");
        await repository.DisposeAsync();
        var (queuedRepository, queued) = await StateRepositoryTests.CreatePendingAsync(
            paths, new FakeClock(), [4, 5, 6], "queued.bin");
        await queuedRepository.DisposeAsync();
        var stopIntake = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingHandler(completeWhenReleased: true);
        var output = new BlockingMatchTextWriter("stage=feed_stop");
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            var run = new WatcherCoordinator(Fast with { ShutdownGracePeriod = TimeSpan.FromSeconds(2) },
                handlerFactory: () => handler, localAppData: paths.Local,
                changeFeedFactory: _ => new FakeChangeFeed(
                    onStart: () =>
                    {
                        handler.Started.Task.Wait(TimeSpan.FromSeconds(2));
                        stopIntake.TrySetResult();
                    },
                    onStop: () => throw new InvalidOperationException("stop failed")),
                intakeStopRequested: () => stopIntake.Task.IsCompleted)
                .RunAsync(paths.Watch, "token", CancellationToken.None, CancellationToken.None);

            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await output.Matched.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(1, handler.RequestCount);
            handler.Release.TrySetResult();
            output.Release.TrySetResult();

            Assert.AreEqual(ExitCodes.Fatal, await run.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(1, handler.RequestCount);
            var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
                paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
            await using (reopened.Repository)
            {
                Assert.AreEqual(
                    UploadStatus.PutComplete,
                    reopened.Repository.GetRequired(active.Fingerprint).Status);
                Assert.AreEqual(
                    UploadStatus.PendingPut,
                    reopened.Repository.GetRequired(queued.Fingerprint).Status);
            }
        }
        finally
        {
            handler.Release.TrySetResult();
            output.Release.TrySetResult();
            Console.SetOut(original);
        }
    }

    [TestMethod]
    public async Task CompletedSpoolDeletionFailure_PreservesCompletionAndStopsFurtherNetworkWork()
    {
        using var paths = new TestPaths();
        var injector = new StateRepositoryFaultInjector
        {
            BeforeCompletedSpoolDelete = () => throw new IOException("sensitive injected detail")
        };
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch,
            UploaderConstants.EventId,
            new FakeClock(),
            paths.Local,
            Fast,
            default,
            injector);
        var first = await AddPendingAsync(opened.Repository, paths.Watch, "first.bin", [1, 2, 3]);
        var second = await AddPendingAsync(opened.Repository, paths.Watch, "second.bin", [4, 5, 6]);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/first"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"success":true}""");
        handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/second"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"success":true}""");
        using var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        int result;
        try
        {
            result = await new WatcherCoordinator(
                    Fast,
                    handlerFactory: () => handler,
                    changeFeedFactory: _ => new FakeChangeFeed())
                .RunAsync(opened.Repository, "token", CancellationToken.None, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.AreEqual(ExitCodes.Fatal, result);
        Assert.AreEqual(2, handler.Requests.Count);
        StringAssert.Contains(output.ToString(), "outcome=completed_spool_cleanup_failed");
        Assert.IsFalse(output.ToString().Contains("sensitive injected detail", StringComparison.Ordinal));
        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, Fast, default);
        await using (reopened.Repository)
        {
            Assert.AreEqual(UploadStatus.Completed, reopened.Repository.GetRequired(first.Fingerprint).Status);
            Assert.AreEqual(UploadStatus.PendingPut, reopened.Repository.GetRequired(second.Fingerprint).Status);
            var resumable = reopened.Repository.GetResumableWork(DateTime.MaxValue);
            Assert.AreEqual(1, resumable.Count);
            Assert.AreEqual(second.Fingerprint, resumable.Single().Fingerprint);
        }
    }

    [TestMethod]
    public async Task CompletedSpoolIdentityDrift_PreservesCompletionAndReturnsSecretFreeFatal()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var paths = new TestPaths();
        string? spoolDirectory = null;
        string? movedSpool = null;
        var injector = new StateRepositoryFaultInjector
        {
            BeforeCompletedSpoolDelete = () =>
            {
                movedSpool = spoolDirectory + "-original";
                Directory.Move(spoolDirectory!, movedSpool);
                Directory.CreateDirectory(spoolDirectory!);
                PathSecurity.HardenPrivateDirectory(spoolDirectory!);
            }
        };
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch,
            UploaderConstants.EventId,
            new FakeClock(),
            paths.Local,
            Fast,
            default,
            injector);
        spoolDirectory = opened.Repository.SpoolDirectory;
        var snapshot = await AddPendingAsync(opened.Repository, paths.Watch, "identity.bin", [7, 8, 9]);
        File.Delete(Path.Combine(paths.Watch, "identity.bin"));
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/identity"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"success":true}""");
        using var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            Assert.AreEqual(
                ExitCodes.Fatal,
                await new WatcherCoordinator(
                        Fast,
                        handlerFactory: () => handler,
                        changeFeedFactory: _ => new FakeChangeFeed())
                    .RunAsync(opened.Repository, "token", CancellationToken.None, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.AreEqual(2, handler.Requests.Count);
        StringAssert.Contains(output.ToString(), "outcome=completed_spool_cleanup_failed");
        Assert.IsFalse(output.ToString().Contains(paths.Local, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(movedSpool);
        var state = JsonNode.Parse(await File.ReadAllTextAsync(opened.Repository.StateFile))!.AsObject();
        var item = ((JsonObject)state["items"]!)[snapshot.Fingerprint]!.AsObject();
        Assert.AreEqual("completed", item["status"]!.GetValue<string>());
        CollectionAssert.AreEqual(
            new byte[] { 7, 8, 9 },
            await File.ReadAllBytesAsync(Path.Combine(movedSpool, Path.GetFileName(snapshot.SpoolFile))));
    }

    [TestMethod]
    public Task DuplicateCleanupIoFailure_ClosesAdmissionBeforeQueuedHttpStarts() =>
        VerifyDuplicateCleanupFailureClosesAdmissionAsync(
            () => throw new IOException("sensitive duplicate cleanup detail"),
            "sensitive duplicate cleanup detail");

    [TestMethod]
    public Task DuplicateCleanupAccessFailure_ClosesAdmissionBeforeQueuedHttpStarts() =>
        VerifyDuplicateCleanupFailureClosesAdmissionAsync(
            () => throw new UnauthorizedAccessException("sensitive access detail"),
            "sensitive access detail");

    [TestMethod]
    public async Task PendingStatePersistenceFailure_ClosesAdmissionBeforeQueuedHttpStarts()
    {
        using var paths = new TestPaths();
        var persistFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failNextPersist = 0;
        const string sensitiveDetail = "sensitive pending persistence detail";
        var injector = new StateRepositoryFaultInjector
        {
            AfterBaseReplace = () =>
            {
                if (Interlocked.Exchange(ref failNextPersist, 0) == 0)
                    return;
                persistFailed.TrySetResult();
                throw new IOException(sensitiveDetail);
            }
        };
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch,
            UploaderConstants.EventId,
            new FakeClock(),
            paths.Local,
            Fast,
            default,
            injector);
        var active = await AddPendingAsync(opened.Repository, paths.Watch, "active.bin", [1, 2, 3]);
        var queued = await AddPendingAsync(opened.Repository, paths.Watch, "queued.bin", [4, 5, 6]);
        File.Delete(Path.Combine(paths.Watch, "active.bin"));
        File.Delete(Path.Combine(paths.Watch, "queued.bin"));
        await File.WriteAllBytesAsync(Path.Combine(paths.Watch, "new.jpg"), [7, 8, 9]);
        Volatile.Write(ref failNextPersist, 1);

        var handler = new BlockingHandler(completeWhenReleased: true);
        using var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        int result;
        try
        {
            var run = new WatcherCoordinator(
                    Fast with { ShutdownGracePeriod = TimeSpan.FromSeconds(2) },
                    handlerFactory: () => handler,
                    changeFeedFactory: _ => new FakeChangeFeed())
                .RunAsync(opened.Repository, "token", CancellationToken.None, CancellationToken.None);

            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await persistFailed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreEqual(1, handler.RequestCount);
            handler.Release.TrySetResult();
            result = await run.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.AreEqual(ExitCodes.Fatal, result);
        Assert.AreEqual(1, handler.RequestCount);
        StringAssert.Contains(output.ToString(), "outcome=pending_state_persist_failed");
        Assert.IsFalse(output.ToString().Contains(sensitiveDetail, StringComparison.Ordinal));

        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, Fast, default);
        await using (reopened.Repository)
        {
            Assert.AreEqual(UploadStatus.PutComplete, reopened.Repository.GetRequired(active.Fingerprint).Status);
            Assert.AreEqual(UploadStatus.PendingPut, reopened.Repository.GetRequired(queued.Fingerprint).Status);
            Assert.AreEqual(
                UploadStatus.PendingPut,
                reopened.Repository.Snapshot.Items.Values.Single(
                    item => item.RelativePath == "new.jpg").Status);
            Assert.AreEqual(3, reopened.Repository.GetResumableWork(DateTime.MaxValue).Count);
        }
    }

    [TestMethod]
    public async Task RestartReconciliation_CompletedDuplicateReleasesQuotaAndAdmitsNewFileWithoutRestart()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var (repository, completedSnapshot) = await StateRepositoryTests.CreatePendingAsync(
            paths, clock, [1, 2, 3], "completed.jpg");
        await repository.MarkPutCompleteAsync(
            completedSnapshot.Fingerprint,
            new Uri("https://fotoshare.s3.us-east-005.backblazeb2.com/bucket/completed"),
            default);
        await repository.MarkCompletedAsync(completedSnapshot.Fingerprint, default);
        repository.DeleteCompletedSpool(repository.GetRequired(completedSnapshot.Fingerprint));
        await repository.DisposeAsync();

        var duplicateFinalized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDuplicate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFinalize = 0;
        StateRepository? runningRepository = null;
        var feed = new FakeChangeFeed();
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/new"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"success":true}""", _ => stop.Cancel());
        var options = Fast with
        {
            AggregateSpoolLimitBytes = 3,
            MinimumFreeSpaceReserveBytes = 0,
            ReconciliationInterval = TimeSpan.FromMilliseconds(20),
            ShutdownGracePeriod = TimeSpan.FromSeconds(2)
        };
        var run = new WatcherCoordinator(
            options,
            handlerFactory: () => handler,
            localAppData: paths.Local,
            changeFeedFactory: _ => feed,
            snapshotterFactory: state =>
            {
                runningRepository = state;
                return new FileSnapshotter(
                    state.SpoolDirectory,
                    state.TrustedRoot,
                    state.SpoolRootTrust,
                    state.SpoolBudget,
                    phase =>
                    {
                        if (phase != "after_finalize" || Interlocked.Exchange(ref firstFinalize, 1) != 0)
                            return;
                        duplicateFinalized.TrySetResult();
                        releaseDuplicate.Task.GetAwaiter().GetResult();
                    });
            })
            .RunAsync(paths.Watch, "token", stop.Token, CancellationToken.None);

        await duplicateFinalized.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsNotNull(runningRepository);
        Assert.AreEqual(3L, runningRepository.SpoolBudget.AccountedBytes);
        Assert.AreEqual(1, Directory.EnumerateFiles(runningRepository.SpoolDirectory, "*.payload").Count());
        releaseDuplicate.TrySetResult();
        await WaitUntilAsync(
            () => runningRepository.SpoolBudget.AccountedBytes == 0 &&
                !Directory.EnumerateFiles(runningRepository.SpoolDirectory, "*.payload").Any(),
            TimeSpan.FromSeconds(3));

        var newFile = Path.Combine(paths.Watch, "new.jpg");
        await File.WriteAllBytesAsync(newFile, [4, 5, 6]);
        feed.Emit(new FileChange(FileChangeKind.Upsert, newFile));

        Assert.AreEqual(ExitCodes.Success, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(2, handler.Requests.Count);
        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, options, default);
        await using (reopened.Repository)
        {
            Assert.AreEqual(2, reopened.Repository.Snapshot.Items.Count);
            Assert.IsTrue(reopened.Repository.Snapshot.Items.Values.All(
                item => item.Status == UploadStatus.Completed));
            Assert.AreEqual(0, Directory.EnumerateFiles(reopened.Repository.SpoolDirectory, "*.payload").Count());
            Assert.AreEqual(0L, reopened.Repository.SpoolBudget.AccountedBytes);
        }
    }

    private static async Task<SnapshotDescriptor> AddPendingAsync(
        StateRepository repository,
        string watchedRoot,
        string relativePath,
        byte[] bytes)
    {
        var source = Path.Combine(watchedRoot, relativePath);
        await File.WriteAllBytesAsync(source, bytes);
        SnapshotDescriptor descriptor;
        if (SupportedMedia.IsSupportedPath(relativePath))
        {
            descriptor = Assert.IsInstanceOfType<SnapshotResult.Ready>(
                await new FileSnapshotter(
                        repository.SpoolDirectory,
                        repository.TrustedRoot,
                        repository.SpoolRootTrust,
                        repository.SpoolBudget)
                    .TrySnapshotAsync(
                        source,
                        relativePath,
                        new FileObservation(bytes.Length, File.GetLastWriteTimeUtc(source)),
                        default)).Snapshot;
        }
        else
        {
            var lastWrite = File.GetLastWriteTimeUtc(source);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var fingerprint = FileSnapshotter.ComputeFingerprint(relativePath, bytes.Length, lastWrite, hash);
            var spoolFile = Path.Combine("spool", $"{fingerprint}.payload");
            await File.WriteAllBytesAsync(Path.Combine(repository.StateRoot, spoolFile), bytes);
            descriptor = new SnapshotDescriptor(
                fingerprint,
                relativePath,
                bytes.Length,
                lastWrite,
                hash,
                bytes.Length,
                ContentTypeMap.Get(Path.GetExtension(relativePath)),
                ContentTypeMap.SanitizeExtension(Path.GetExtension(relativePath)),
                spoolFile);
        }
        await repository.AddPendingAsync(
            descriptor,
            FileSnapshotter.CreateRemoteKey(descriptor.Extension),
            default);
        return descriptor;
    }

    private static async Task VerifyDuplicateCleanupFailureClosesAdmissionAsync(
        Action failCleanup,
        string sensitiveDetail)
    {
        using var paths = new TestPaths();
        var cleanupFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var injector = new StateRepositoryFaultInjector
        {
            BeforeNewCompletedDuplicateSpoolDelete = () =>
            {
                cleanupFailed.TrySetResult();
                failCleanup();
            }
        };
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch,
            UploaderConstants.EventId,
            new FakeClock(),
            paths.Local,
            Fast,
            default,
            injector);
        var completed = await AddPendingAsync(opened.Repository, paths.Watch, "completed.jpg", [1, 2, 3]);
        await opened.Repository.MarkPutCompleteAsync(
            completed.Fingerprint,
            new Uri("https://fotoshare.s3.us-east-005.backblazeb2.com/bucket/completed"),
            default);
        await opened.Repository.MarkCompletedAsync(completed.Fingerprint, default);
        opened.Repository.DeleteCompletedSpool(opened.Repository.GetRequired(completed.Fingerprint));
        var active = await AddPendingAsync(opened.Repository, paths.Watch, "active.bin", [4, 5, 6]);
        var queued = await AddPendingAsync(opened.Repository, paths.Watch, "queued.bin", [7, 8, 9]);
        File.Delete(Path.Combine(paths.Watch, "active.bin"));
        File.Delete(Path.Combine(paths.Watch, "queued.bin"));

        var handler = new BlockingHandler(completeWhenReleased: true);
        using var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        int result;
        try
        {
            var run = new WatcherCoordinator(
                    Fast with { ShutdownGracePeriod = TimeSpan.FromSeconds(2) },
                    handlerFactory: () => handler,
                    changeFeedFactory: _ => new FakeChangeFeed())
                .RunAsync(opened.Repository, "token", CancellationToken.None, CancellationToken.None);

            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await cleanupFailed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreEqual(1, handler.RequestCount);
            handler.Release.TrySetResult();
            result = await run.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.AreEqual(ExitCodes.Fatal, result);
        Assert.AreEqual(1, handler.RequestCount);
        StringAssert.Contains(output.ToString(), "outcome=duplicate_spool_cleanup_failed");
        Assert.IsFalse(output.ToString().Contains(sensitiveDetail, StringComparison.Ordinal));

        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, Fast, default);
        await using (reopened.Repository)
        {
            Assert.AreEqual(UploadStatus.PutComplete, reopened.Repository.GetRequired(active.Fingerprint).Status);
            Assert.AreEqual(UploadStatus.PendingPut, reopened.Repository.GetRequired(queued.Fingerprint).Status);
            Assert.AreEqual(2, reopened.Repository.GetResumableWork(DateTime.MaxValue).Count);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                Assert.Fail("Condition was not reached before timeout.");
            await Task.Delay(10);
        }
    }

    private sealed class BlockingHandler(bool completeWhenReleased) : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int RequestCount;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            Started.TrySetResult();
            if (completeWhenReleased)
                await Release.Task.WaitAsync(cancellationToken);
            else
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var json = request.Method == HttpMethod.Put
                ? """{"url":"/bucket/object"}"""
                : """{"success":true}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    private sealed class FaultingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Injected worker fault.");
    }

    private sealed class BlockingMatchTextWriter(string match) : TextWriter
    {
        private readonly StringWriter inner = new();
        private readonly object sync = new();

        internal TaskCompletionSource Matched { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override System.Text.Encoding Encoding => inner.Encoding;

        public override void WriteLine(string? value)
        {
            lock (sync)
                inner.WriteLine(value);
            if (value?.Contains(match, StringComparison.Ordinal) == true)
            {
                Matched.TrySetResult();
                Release.Task.GetAwaiter().GetResult();
            }
        }
    }

    private sealed class RetryBackoffClock : IClock
    {
        private DateTime utcNow = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
        internal TaskCompletionSource BackoffStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTime UtcNow => utcNow;

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (delay >= TimeSpan.FromSeconds(1))
            {
                BackoffStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            utcNow += delay;
        }

        public int NextJitterMilliseconds(int exclusiveUpperBound) => 0;
    }
}
