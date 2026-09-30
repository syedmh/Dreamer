using System.Net;
using TCFUploader.Configuration;
using TCFUploader.Cli;
using TCFUploader.Discovery;
using TCFUploader.Files;
using TCFUploader.Hosting;
using TCFUploader.State;
using TCFUploader.Tests.State;
using TCFUploader.Tests.TestDoubles;
using TCFUploader.Upload;

namespace TCFUploader.Tests.Integration;

[TestClass]
public sealed class SecurityBoundaryTests
{
    [TestMethod]
    public async Task AC18_LogsStateErrorsAndRequests_NeverExposeSecret()
    {
        const string secret = "unique-test-secret";
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var (repository, snapshot) = await StateRepositoryTests.CreatePendingAsync(paths, clock, [1]);
        await using (repository)
        {
            var handler = new ScriptedHttpMessageHandler();
            handler.Enqueue(HttpStatusCode.BadRequest, "{}");
            using var http = new HttpClient(handler);
            using var output = new StringWriter();
            var original = Console.Out;
            Console.SetOut(output);
            try
            {
                WatcherCoordinator.Log("ERROR", "upload", snapshot.RelativePath, "put", 1,
                    snapshot.Fingerprint[..12], "put_http_400");
                await new UploadWorker(repository, new LumaBoothClient(http, RuntimeOptions.Default),
                    new RetryPolicy(clock, RuntimeOptions.Default), clock, RuntimeOptions.Default, secret)
                    .ProcessAsync(snapshot.Fingerprint, default);

                using var retryPaths = new TestPaths();
                var (retryRepository, retrySnapshot) = await StateRepositoryTests.CreatePendingAsync(
                    retryPaths, new FakeClock(), [2]);
                await using (retryRepository)
                {
                    var retryHandler = new ScriptedHttpMessageHandler();
                    retryHandler.Enqueue(HttpStatusCode.InternalServerError, "{}");
                    retryHandler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/object"}""");
                    retryHandler.Enqueue(HttpStatusCode.OK, """{"success":true}""");
                    using var retryHttp = new HttpClient(retryHandler);
                    await new UploadWorker(retryRepository,
                        new LumaBoothClient(retryHttp, RuntimeOptions.Default),
                        new RetryPolicy(new FakeClock(), RuntimeOptions.Default), new FakeClock(),
                        RuntimeOptions.Default, secret).ProcessAsync(retrySnapshot.Fingerprint, default);
                }

                using var startupPaths = new TestPaths();
                var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
                    startupPaths.Watch, UploaderConstants.EventId, new FakeClock(), startupPaths.Local, default);
                var corrupt = opened.Repository.StateFile;
                await opened.Repository.DisposeAsync();
                await File.WriteAllTextAsync(corrupt, "{bad");
                Assert.AreEqual(ExitCodes.State, await new WatcherCoordinator(localAppData: startupPaths.Local)
                    .RunAsync(startupPaths.Watch, secret, CancellationToken.None, CancellationToken.None));

                using var shutdownPaths = new TestPaths();
                using var stop = new CancellationTokenSource(50);
                Assert.AreEqual(ExitCodes.Success, await new WatcherCoordinator(
                    RuntimeOptions.Default with
                    {
                        ObservationInterval = TimeSpan.FromMilliseconds(5),
                        ReconciliationInterval = TimeSpan.FromMilliseconds(10)
                    },
                    handlerFactory: () => new ScriptedHttpMessageHandler(),
                    localAppData: shutdownPaths.Local,
                    changeFeedFactory: _ => new FakeChangeFeed())
                    .RunAsync(shutdownPaths.Watch, secret, stop.Token, CancellationToken.None));
            }
            finally { Console.SetOut(original); }
            Assert.IsFalse(output.ToString().Contains(secret, StringComparison.Ordinal));
            Assert.IsFalse(output.ToString().Contains("Authorization:" + " Bearer", StringComparison.Ordinal));
            Assert.IsFalse((await File.ReadAllTextAsync(repository.StateFile)).Contains(secret, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task WindowsJunctions_RootAncestorAndStateOverlap_AreRejected()
        {
            Assert.IsTrue(OperatingSystem.IsWindows());
            using var paths = new TestPaths();
            var outside = Path.Combine(paths.Root, "outside");
            Directory.CreateDirectory(outside);
            await File.WriteAllTextAsync(Path.Combine(outside, "secret.jpg"), "outside");

            var rootTarget = Path.Combine(paths.Root, "root-target");
            Directory.CreateDirectory(rootTarget);
            var rootLink = Path.Combine(paths.Root, "root-link");
            CreateJunction(rootLink, rootTarget);
            Assert.IsInstanceOfType<CliParseResult.Error>(CliOptionsParser.Parse(["--folder", rootLink]));
            Assert.IsInstanceOfType<StateOpenResult.Invalid>(await StateRepository.OpenAsync(
                rootLink, UploaderConstants.EventId, new FakeClock(), paths.Local, default));

            var ancestorLink = Path.Combine(paths.Watch, "linked");
            CreateJunction(ancestorLink, outside);
            var scan = await new Reconciler().ScanBatchAsync(paths.Watch, 100, default);
            Assert.IsFalse(scan.Files.Any(path => path.Contains("secret.jpg", StringComparison.OrdinalIgnoreCase)));
            var linkedFile = Path.Combine(ancestorLink, "secret.jpg");
            var info = new FileInfo(linkedFile);
            var snapshot = await new FileSnapshotter(Path.Combine(paths.Local, "safe-spool"), paths.Watch)
                .TrySnapshotAsync(linkedFile, @"linked\secret.jpg",
                    new FileObservation(info.Length, info.LastWriteTimeUtc), default);
            Assert.IsInstanceOfType<SnapshotResult.RetryLater>(snapshot);

            Directory.Delete(paths.Local, true);
            var stateInsideWatch = Path.Combine(paths.Watch, "state-target");
            Directory.CreateDirectory(stateInsideWatch);
            CreateJunction(paths.Local, stateInsideWatch);
            Assert.IsInstanceOfType<StateOpenResult.Invalid>(await StateRepository.OpenAsync(
                paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default));
            RemoveJunction(paths.Local);
            Directory.CreateDirectory(paths.Local);
            RemoveJunction(ancestorLink);
            RemoveJunction(rootLink);
        }

    [TestMethod]
    public async Task StartupBeneathAncestorJunction_IsRejectedBeforeWatcherSpoolOrHttp()
    {
        Assert.IsTrue(OperatingSystem.IsWindows());
        using var paths = new TestPaths();
        var physicalParent = Path.Combine(paths.Root, "physical-parent");
        var physicalRoot = Path.Combine(physicalParent, "watch");
        Directory.CreateDirectory(physicalRoot);
        var linkedParent = Path.Combine(paths.Root, "linked-parent");
        CreateJunction(linkedParent, physicalParent);
        var linkedRoot = Path.Combine(linkedParent, "watch");

        Assert.IsInstanceOfType<CliParseResult.Error>(
            CliOptionsParser.Parse(["--folder", linkedRoot]));
        Assert.IsInstanceOfType<StateOpenResult.Invalid>(await StateRepository.OpenAsync(
            linkedRoot, UploaderConstants.EventId, new FakeClock(), paths.Local, default));

        var handlers = 0;
        var feeds = 0;
        var exit = await new WatcherCoordinator(
            handlerFactory: () =>
            {
                handlers++;
                return new ScriptedHttpMessageHandler();
            },
            localAppData: paths.Local,
            changeFeedFactory: _ =>
            {
                feeds++;
                return new FakeChangeFeed();
            })
            .RunAsync(linkedRoot, "token", CancellationToken.None, CancellationToken.None);

        Assert.AreEqual(ExitCodes.State, exit);
        Assert.AreEqual(0, handlers);
        Assert.AreEqual(0, feeds);
        Assert.IsFalse(Directory.Exists(Path.Combine(paths.Local, "TCFUploader")));
        RemoveJunction(linkedParent);
    }

    [TestMethod]
    public async Task AncestorSwapAfterStartupBeforeReconciliation_FailsClosedWithoutSpoolOrHttp()
    {
        Assert.IsTrue(OperatingSystem.IsWindows());
        using var paths = new TestPaths();
        var ancestor = Path.Combine(paths.Root, "ancestor");
        var watch = Path.Combine(ancestor, "watch");
        Directory.CreateDirectory(watch);
        var outsideParent = Path.Combine(paths.Root, "outside-parent");
        var outsideWatch = Path.Combine(outsideParent, "watch");
        Directory.CreateDirectory(outsideWatch);
        await File.WriteAllTextAsync(Path.Combine(outsideWatch, "evil.jpg"), "evil");
        var originalAncestor = Path.Combine(paths.Root, "ancestor-original");
        var handler = new ScriptedHttpMessageHandler();

        var exit = await new WatcherCoordinator(
            RuntimeOptions.Default with
            {
                ObservationInterval = TimeSpan.FromMilliseconds(5),
                ReconciliationInterval = TimeSpan.FromMilliseconds(10),
                ShutdownGracePeriod = TimeSpan.FromMilliseconds(100)
            },
            handlerFactory: () => handler,
            localAppData: paths.Local,
            changeFeedFactory: _ => new FakeChangeFeed(onStart: () =>
            {
                Directory.Move(ancestor, originalAncestor);
                CreateJunction(ancestor, outsideParent);
            }))
            .RunAsync(watch, "token", CancellationToken.None, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.AreEqual(ExitCodes.Fatal, exit);
        Assert.AreEqual(0, handler.Requests.Count);
        var stateRoot = Directory.EnumerateDirectories(
            Path.Combine(paths.Local, "TCFUploader", "events"), "*", SearchOption.AllDirectories)
            .First(directory => Path.GetFileName(directory) == "spool");
        Assert.AreEqual(0, Directory.EnumerateFiles(stateRoot).Count());
        RemoveJunction(ancestor);
    }

    [TestMethod]
    public async Task AncestorSwapBetweenObservationAndSnapshot_FailsClosedWithoutSpool()
    {
        Assert.IsTrue(OperatingSystem.IsWindows());
        using var paths = new TestPaths();
        var ancestor = Path.Combine(paths.Root, "snapshot-ancestor");
        var watch = Path.Combine(ancestor, "watch");
        Directory.CreateDirectory(watch);
        var source = Path.Combine(watch, "photo.jpg");
        await File.WriteAllTextAsync(source, "safe");
        var observation = new FileObservation(new FileInfo(source).Length, File.GetLastWriteTimeUtc(source));
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
        await using var repository = opened.Repository;

        var outsideParent = Path.Combine(paths.Root, "snapshot-outside");
        var outsideWatch = Path.Combine(outsideParent, "watch");
        Directory.CreateDirectory(outsideWatch);
        var outsideFile = Path.Combine(outsideWatch, "photo.jpg");
        await File.WriteAllTextAsync(outsideFile, "evil");
        File.SetLastWriteTimeUtc(outsideFile, observation.LastWriteUtc);
        var originalAncestor = Path.Combine(paths.Root, "snapshot-original");
        Directory.Move(ancestor, originalAncestor);
        CreateJunction(ancestor, outsideParent);

        var result = await new FileSnapshotter(repository.SpoolDirectory, repository.TrustedRoot)
            .TrySnapshotAsync(source, "photo.jpg", observation, default);
        Assert.IsInstanceOfType<SnapshotResult.RetryLater>(result);
        Assert.AreEqual(0, Directory.EnumerateFiles(repository.SpoolDirectory).Count());
        RemoveJunction(ancestor);
    }

    [TestMethod]
    public async Task JunctionSwapBetweenObservationAndSnapshot_FailsClosed()
        {
            Assert.IsTrue(OperatingSystem.IsWindows());
            using var paths = new TestPaths();
            var safeDirectory = Path.Combine(paths.Watch, "album");
            Directory.CreateDirectory(safeDirectory);
            var source = Path.Combine(safeDirectory, "photo.jpg");
            await File.WriteAllTextAsync(source, "safe");
            var observation = new FileObservation(new FileInfo(source).Length, File.GetLastWriteTimeUtc(source));

            var outside = Path.Combine(paths.Root, "outside-swap");
            Directory.CreateDirectory(outside);
            await File.WriteAllTextAsync(Path.Combine(outside, "photo.jpg"), "evil");
            File.SetLastWriteTimeUtc(Path.Combine(outside, "photo.jpg"), observation.LastWriteUtc);
            File.Delete(source);
            RemoveJunction(safeDirectory);
            CreateJunction(safeDirectory, outside);

            var result = await new FileSnapshotter(Path.Combine(paths.Local, "spool"), paths.Watch)
                .TrySnapshotAsync(source, @"album\photo.jpg", observation, default);
            Assert.IsInstanceOfType<SnapshotResult.RetryLater>(result);
            Assert.IsFalse(Directory.Exists(Path.Combine(paths.Local, "spool")) &&
                Directory.EnumerateFiles(Path.Combine(paths.Local, "spool")).Any());
            Directory.Delete(safeDirectory);
        }

    private static void CreateJunction(string link, string target)
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode,
                $"mklink failed: {process.StandardOutput.ReadToEnd()} {process.StandardError.ReadToEnd()}");
    }

    private static void RemoveJunction(string link)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/d /c rmdir \"{link}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode);
    }
}
