using System.Net;
using TCFUploader.Configuration;
using TCFUploader.State;
using TCFUploader.Tests.State;
using TCFUploader.Tests.TestDoubles;
using TCFUploader.Upload;

namespace TCFUploader.Tests.Integration;

[TestClass]
public sealed class RestartRecoveryTests
{
    [TestMethod]
    public async Task AC15_PutCompleteRestart_ResumesPostWithoutPut()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var (repository, snapshot) = await StateRepositoryTests.CreatePendingAsync(paths, clock, [1, 2]);
        await repository.MarkPutCompleteAsync(snapshot.Fingerprint,
            new Uri("https://fotoshare.s3.us-east-005.backblazeb2.com/bucket/object"), default);
        await repository.DisposeAsync();

        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
        await using (reopened.Repository)
        {
            var handler = new ScriptedHttpMessageHandler();
            handler.Enqueue(HttpStatusCode.OK, """{"success":true}""");
            using var http = new HttpClient(handler);
            var worker = new UploadWorker(reopened.Repository, new LumaBoothClient(http, RuntimeOptions.Default),
                new RetryPolicy(clock, RuntimeOptions.Default), clock, RuntimeOptions.Default, "token");
            await worker.ProcessAsync(snapshot.Fingerprint, default);
            Assert.AreEqual(1, handler.Requests.Count);
            Assert.AreEqual(HttpMethod.Post, handler.Requests[0].Method);
        }
    }

    [TestMethod]
    public async Task AC16_FingerprintChange_ReprocessesWhileUnchangedSkips()
    {
        using var paths = new TestPaths();
        var source = Path.Combine(paths.Watch, "a.jpg");
        await File.WriteAllBytesAsync(source, [1]);
        var clock = new FakeClock();
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
        await using var repository = opened.Repository;
        var snapshotter = new TCFUploader.Files.FileSnapshotter(
            repository.SpoolDirectory, repository.SpoolBudget);
        var firstInfo = new FileInfo(source);
        var first = (TCFUploader.Files.SnapshotResult.Ready)await snapshotter.TrySnapshotAsync(
            source, "a.jpg",
            new TCFUploader.Files.FileObservation(firstInfo.Length, firstInfo.LastWriteTimeUtc), default);
        await repository.AddPendingAsync(
            first.Snapshot, TCFUploader.Files.FileSnapshotter.CreateRemoteKey(".jpg"), default);
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/one"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"success":true}""");
        handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/two"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"success":true}""");
        using var http = new HttpClient(handler);
        var worker = new UploadWorker(repository, new LumaBoothClient(http, RuntimeOptions.Default),
            new RetryPolicy(clock, RuntimeOptions.Default), clock, RuntimeOptions.Default, "token");
        await worker.ProcessAsync(first.Snapshot.Fingerprint, default);

        var unchanged = (TCFUploader.Files.SnapshotResult.Ready)await snapshotter.TrySnapshotAsync(
            source, "a.jpg",
            new TCFUploader.Files.FileObservation(firstInfo.Length, firstInfo.LastWriteTimeUtc), default);
        Assert.AreEqual(first.Snapshot.Fingerprint, unchanged.Snapshot.Fingerprint);
        Assert.IsTrue(repository.ContainsActiveOrCompleted(unchanged.Snapshot.Fingerprint));
        Assert.AreEqual(2, handler.Requests.Count);

        await File.WriteAllBytesAsync(source, [2, 3]);
        File.SetLastWriteTimeUtc(source, firstInfo.LastWriteTimeUtc.AddSeconds(1));
        var changedInfo = new FileInfo(source);
        var changed = (TCFUploader.Files.SnapshotResult.Ready)await snapshotter.TrySnapshotAsync(
            source, "a.jpg",
            new TCFUploader.Files.FileObservation(changedInfo.Length, changedInfo.LastWriteTimeUtc), default);
        Assert.AreNotEqual(first.Snapshot.Fingerprint, changed.Snapshot.Fingerprint);
        await repository.AddPendingAsync(
            changed.Snapshot, TCFUploader.Files.FileSnapshotter.CreateRemoteKey(".jpg"), default);
        await worker.ProcessAsync(changed.Snapshot.Fingerprint, default);
        Assert.AreEqual(4, handler.Requests.Count);
        Assert.AreEqual(2, repository.Snapshot.Items.Values.Count(item => item.Status == UploadStatus.Completed));
    }
}
