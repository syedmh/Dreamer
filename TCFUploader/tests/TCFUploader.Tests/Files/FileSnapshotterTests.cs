using System.Security.Cryptography;
using TCFUploader.Configuration;
using TCFUploader.Files;
using TCFUploader.State;
using TCFUploader.Tests.TestDoubles;

namespace TCFUploader.Tests.Files;

[TestClass]
public sealed class FileSnapshotterTests
{
    [TestMethod]
    public async Task Snapshot_StreamsHashesAndPreservesSource()
    {
        using var paths = new TestPaths();
        var file = Path.Combine(paths.Watch, "image.jpg");
        var bytes = RandomNumberGenerator.GetBytes(1024 * 1024);
        await File.WriteAllBytesAsync(file, bytes);
        var time = File.GetLastWriteTimeUtc(file);
        var spool = Path.Combine(paths.Local, "spool");
        var result = await new FileSnapshotter(spool).TrySnapshotAsync(
            file, "image.jpg", new FileObservation(bytes.Length, time), default);
        var ready = (SnapshotResult.Ready)result;
        Assert.AreEqual(bytes.Length, ready.Snapshot.ByteLength);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), ready.Snapshot.ContentSha256);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(paths.Local, ready.Snapshot.SpoolFile)));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(file));
    }

    [TestMethod]
    public async Task Snapshot_AggregateQuotaAndFreeSpace_AreEnforcedBeforeAndDuringCopy()
    {
        using var paths = new TestPaths();
        var file = Path.Combine(paths.Watch, "large.jpg");
        await File.WriteAllBytesAsync(file, new byte[256 * 1024]);
        var observation = new FileObservation(new FileInfo(file).Length, File.GetLastWriteTimeUtc(file));
        var spool = Path.Combine(paths.Local, "spool");

        var quota = new SpoolBudget(spool, observation.Length - 1, 0, () => long.MaxValue);
        var quotaResult = await new FileSnapshotter(spool, quota).TrySnapshotAsync(
            file, "large.jpg", observation, default);
        Assert.AreEqual("spool_quota_exceeded",
            Assert.IsInstanceOfType<SnapshotResult.CapacityUnavailable>(quotaResult).OutcomeCode);
        Assert.AreEqual(0, Directory.EnumerateFiles(spool).Count());

        var checks = 0;
        var reserve = new SpoolBudget(
            spool, observation.Length * 2, 100,
            () => Interlocked.Increment(ref checks) == 1 ? observation.Length + 100 : 99);
        var reserveResult = await new FileSnapshotter(spool, reserve).TrySnapshotAsync(
            file, "large.jpg", observation, default);
        Assert.AreEqual("spool_free_space_reserved",
            Assert.IsInstanceOfType<SnapshotResult.CapacityUnavailable>(reserveResult).OutcomeCode);
        Assert.AreEqual(0, Directory.EnumerateFiles(spool).Count());
        Assert.AreEqual(0L, reserve.AccountedBytes);
    }

    [TestMethod]
    public async Task AC05_LockedFile_IsUnavailableUntilProducerReleasesIt()
    {
        using var paths = new TestPaths();
        var file = Path.Combine(paths.Watch, "locked.jpg");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        var observation = new FileObservation(new FileInfo(file).Length, File.GetLastWriteTimeUtc(file));
        var spool = Path.Combine(paths.Local, "spool");
        await using (var producer = new FileStream(
            file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var unavailable = await new FileSnapshotter(spool).TrySnapshotAsync(
                file, "locked.jpg", observation, default);
            Assert.IsInstanceOfType<SnapshotResult.RetryLater>(unavailable);
            Assert.AreEqual(0, Directory.EnumerateFiles(spool).Count());
        }

        Assert.IsInstanceOfType<SnapshotResult.Ready>(await new FileSnapshotter(spool).TrySnapshotAsync(
            file, "locked.jpg", observation, default));
    }

    [TestMethod]
    public void Snapshot_Reservations_AreConcurrencySafeAndReleased()
    {
        using var paths = new TestPaths();
        var budget = new SpoolBudget(Path.Combine(paths.Local, "spool"), 10, 0, () => long.MaxValue);
        var first = Assert.IsInstanceOfType<SpoolBudget.ReservationResult.Granted>(budget.TryReserve(6));
        Assert.AreEqual("spool_quota_exceeded",
            Assert.IsInstanceOfType<SpoolBudget.ReservationResult.Unavailable>(budget.TryReserve(5)).OutcomeCode);
        first.Value.Dispose();
        Assert.IsInstanceOfType<SpoolBudget.ReservationResult.Granted>(budget.TryReserve(10));
    }

    [TestMethod]
    public async Task Snapshot_SpoolReplacementDuringCopy_FailsClosedBeforeCreatingPayload()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var paths = new TestPaths();
        var source = Path.Combine(paths.Watch, "identity.jpg");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch,
            UploaderConstants.EventId,
            new FakeClock(),
            paths.Local,
            default);
        await using var repository = opened.Repository;
        var replaced = repository.SpoolDirectory + "-original";
        var hookCalled = false;
        var snapshotter = new FileSnapshotter(
            repository.SpoolDirectory,
            repository.TrustedRoot,
            repository.SpoolRootTrust,
            repository.SpoolBudget,
            phase =>
            {
                if (phase != "before_create" || hookCalled)
                    return;
                hookCalled = true;
                Directory.Move(repository.SpoolDirectory, replaced);
                Directory.CreateDirectory(repository.SpoolDirectory);
                PathSecurity.HardenPrivateDirectory(repository.SpoolDirectory);
            });
        var observation = new FileObservation(
            new FileInfo(source).Length,
            File.GetLastWriteTimeUtc(source));

        try
        {
            await Assert.ThrowsExactlyAsync<SpoolIdentityException>(() => snapshotter.TrySnapshotAsync(
                source,
                "identity.jpg",
                observation,
                default));
            Assert.AreEqual(0, Directory.EnumerateFiles(repository.SpoolDirectory).Count());
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(source));
        }
        finally
        {
            Directory.Delete(repository.SpoolDirectory, recursive: true);
            Directory.Move(replaced, repository.SpoolDirectory);
        }
    }

    [TestMethod]
    [DataRow("photo.gif")]
    [DataRow("photo.webp")]
    [DataRow("video.mp4")]
    [DataRow("video.mov")]
    [DataRow("notes.txt")]
    [DataRow("extensionless")]
    [DataRow("photo.jpg.exe")]
    public async Task Snapshot_UnsupportedMedia_IsRejectedBeforeSpoolCreationOrReservation(string name)
    {
        using var paths = new TestPaths();
        var source = Path.Combine(paths.Watch, name);
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var spool = Path.Combine(paths.Local, "spool");
        var budget = new SpoolBudget(spool, 1, 0, () => long.MaxValue);

        var result = await new FileSnapshotter(spool, budget).TrySnapshotAsync(
            source,
            name,
            new FileObservation(3, File.GetLastWriteTimeUtc(source)),
            default);

        Assert.AreEqual(
            SupportedMedia.UnsupportedOutcomeCode,
            Assert.IsInstanceOfType<SnapshotResult.RetryLater>(result).OutcomeCode);
        Assert.AreEqual(0, Directory.EnumerateFiles(spool).Count());
        Assert.AreEqual(0L, budget.AccountedBytes);
    }

    [TestMethod]
    [DataRow("photo.JPG", "image/jpeg", ".jpg")]
    [DataRow("photo.JpEg", "image/jpeg", ".jpeg")]
    [DataRow("photo.pNg", "image/png", ".png")]
    public async Task Snapshot_SupportedMixedCaseMedia_IsAccepted(
        string name,
        string expectedContentType,
        string expectedExtension)
    {
        using var paths = new TestPaths();
        var source = Path.Combine(paths.Watch, name);
        await File.WriteAllBytesAsync(source, [1, 2, 3]);

        var result = Assert.IsInstanceOfType<SnapshotResult.Ready>(
            await new FileSnapshotter(Path.Combine(paths.Local, "spool")).TrySnapshotAsync(
                source,
                name,
                new FileObservation(3, File.GetLastWriteTimeUtc(source)),
                default));

        Assert.AreEqual(expectedContentType, result.Snapshot.ContentType);
        Assert.AreEqual(expectedExtension, result.Snapshot.Extension);
    }
}
