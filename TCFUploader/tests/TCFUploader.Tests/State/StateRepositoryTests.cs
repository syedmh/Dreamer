using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Text.Json.Nodes;
using TCFUploader.Configuration;
using TCFUploader.Files;
using TCFUploader.State;
using TCFUploader.Tests.TestDoubles;

namespace TCFUploader.Tests.State;

[TestClass]
public sealed class StateRepositoryTests
{
    [TestMethod]
    public async Task State_FailsClosedOnCorruptionAndExclusiveLock()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var first = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
        var locked = await StateRepository.OpenAsync(paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
        Assert.IsInstanceOfType<StateOpenResult.Locked>(locked);
        var stateFile = first.Repository.StateFile;
        await first.Repository.DisposeAsync();
        await File.WriteAllTextAsync(stateFile, "{broken");
        Assert.IsInstanceOfType<StateOpenResult.Invalid>(
            await StateRepository.OpenAsync(paths.Watch, UploaderConstants.EventId, clock, paths.Local, default));
    }

    [TestMethod]
    public async Task AC22_WindowsPathEquivalence_DeduplicatesAndRejectsStateOverlap()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var insideWatch = Path.Combine(paths.Watch, "state");
        Assert.IsInstanceOfType<StateOpenResult.Invalid>(
            await StateRepository.OpenAsync(paths.Watch, UploaderConstants.EventId, clock, insideWatch, default));
        Assert.IsTrue(StateRepository.IsContained(paths.Watch.ToUpperInvariant(), Path.Combine(paths.Watch, "child")));
        var first = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
        var stateRoot = first.Repository.StateRoot;
        await first.Repository.DisposeAsync();
        var equivalent = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch.ToUpperInvariant(), UploaderConstants.EventId, clock, paths.Local, default);
        await using (equivalent.Repository)
            Assert.AreEqual(stateRoot, equivalent.Repository.StateRoot, true);
    }

    [TestMethod]
    public async Task State_CorruptionMatrix_FailsClosedBeforeReturningRepository()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var (repository, snapshot) = await CreatePendingAsync(paths, clock, [1, 2, 3]);
        var stateFile = repository.StateFile;
        var validJson = await File.ReadAllTextAsync(stateFile);
        await repository.DisposeAsync();

        var mutations = new (string Name, Action<JsonObject, JsonObject> Apply)[]
        {
            ("numeric status", (_, item) => item["status"] = 99),
            ("unknown status", (_, item) => item["status"] = "unknown"),
            ("null item", (root, _) => ((JsonObject)root["items"]!)[snapshot.Fingerprint] = null),
            ("null member", (_, item) => item["remoteKey"] = null),
            ("uppercase content hash", (_, item) => item["contentSha256"] = item["contentSha256"]!.GetValue<string>().ToUpperInvariant()),
            ("uppercase fingerprint", (root, item) =>
            {
                var items = (JsonObject)root["items"]!;
                items.Remove(snapshot.Fingerprint);
                items[snapshot.Fingerprint.ToUpperInvariant()] = item;
            }),
            ("fingerprint mismatch", (root, item) =>
            {
                var items = (JsonObject)root["items"]!;
                items.Remove(snapshot.Fingerprint);
                items[new string('a', 64)] = item;
            }),
            ("invalid remote key", (_, item) => item["remoteKey"] = "local-path.jpg"),
            ("invalid extension", (_, item) => item["extension"] = ".exe"),
            ("invalid content type", (_, item) => item["contentType"] = "text/plain"),
            ("non utc timestamp", (_, item) => item["createdUtc"] = "2026-09-29T00:00:00"),
            ("timestamp order", (_, item) => item["createdUtc"] = "2026-09-30T00:00:00Z"),
            ("spool traversal", (_, item) => item["spoolFile"] = @"spool\..\state.v1.json"),
            ("spool wrong name", (_, item) => item["spoolFile"] = @"spool\other.payload"),
            ("evil remote url", (_, item) =>
            {
                item["status"] = "putComplete";
                item["remoteUrl"] = "https://evil.example/object";
            }),
            ("scope mismatch", (root, _) => root["eventId"] = "wrong-event"),
            ("cycle invariant", (_, item) => item["lastCycleAttempts"] = 1)
        };

        foreach (var mutation in mutations)
        {
            var root = JsonNode.Parse(validJson)!.AsObject();
            var item = ((JsonObject)root["items"]!)[snapshot.Fingerprint]!.AsObject();
            mutation.Apply(root, item);
            await File.WriteAllTextAsync(stateFile, root.ToJsonString());
            var result = await StateRepository.OpenAsync(
                paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
            Assert.IsInstanceOfType<StateOpenResult.Invalid>(result, mutation.Name);
        }
    }

    [TestMethod]
    public async Task State_ActiveSpoolMissingWrongLengthOrHash_FailsClosed()
    {
        foreach (var corruption in new[] { "missing", "length", "hash" })
        {
            using var paths = new TestPaths();
            var clock = new FakeClock();
            var (repository, snapshot) = await CreatePendingAsync(paths, clock, [1, 2, 3]);
            var spool = repository.ResolveSpool(repository.GetRequired(snapshot.Fingerprint));
            await repository.DisposeAsync();
            if (corruption == "missing") File.Delete(spool);
            else if (corruption == "length")
            {
                await using var append = new FileStream(spool, FileMode.Append, FileAccess.Write, FileShare.None);
                await append.WriteAsync(new byte[] { 4 });
            }
            else await File.WriteAllBytesAsync(spool, [3, 2, 1]);
            Assert.IsInstanceOfType<StateOpenResult.Invalid>(await StateRepository.OpenAsync(
                paths.Watch, UploaderConstants.EventId, clock, paths.Local, default), corruption);
        }
    }

    [TestMethod]
    public async Task State_LargeStoreJournal_ReplaysDurablyAndRejectsCorruption()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
        var repository = opened.Repository;
        byte[]? baseState = null;
        for (var i = 0; i < 131; i++)
        {
            if (i == 128)
                baseState = await File.ReadAllBytesAsync(repository.StateFile);
            var relative = $"journal-{i}.bin";
            var bytes = BitConverter.GetBytes(i);
            var lastWrite = new DateTime(2026, 9, 29, 1, 0, 0, DateTimeKind.Utc).AddTicks(i);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var fingerprint = FileSnapshotter.ComputeFingerprint(relative, bytes.Length, lastWrite, hash);
            var spoolFile = Path.Combine("spool", $"{fingerprint}.payload");
            await File.WriteAllBytesAsync(Path.Combine(repository.StateRoot, spoolFile), bytes);
            await repository.AddPendingAsync(
                new SnapshotDescriptor(
                    fingerprint,
                    relative,
                    bytes.Length,
                    lastWrite,
                    hash,
                    bytes.Length,
                    "application/octet-stream",
                    ".bin",
                    spoolFile),
                FileSnapshotter.CreateRemoteKey(".bin"),
                default);
        }

        Assert.IsNotNull(baseState);
        var journal = await File.ReadAllBytesAsync(repository.JournalFile);
        await repository.DisposeAsync();

        await File.WriteAllBytesAsync(repository.StateFile, baseState);
        await File.WriteAllBytesAsync(repository.JournalFile, journal);
        var replayed = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
        Assert.AreEqual(131, replayed.Repository.Snapshot.Items.Count);
        await replayed.Repository.DisposeAsync();

        await File.WriteAllBytesAsync(repository.StateFile, baseState);
        var corrupted = System.Text.Encoding.UTF8.GetString(journal)
            .Replace("journal-128.bin", "journal-X28.bin", StringComparison.Ordinal);
        await File.WriteAllTextAsync(repository.JournalFile, corrupted);
        Assert.IsInstanceOfType<StateOpenResult.Invalid>(await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default));

        await File.WriteAllBytesAsync(repository.StateFile, baseState);
        await File.WriteAllBytesAsync(repository.JournalFile, journal);
        var recordLengths = System.Text.Encoding.UTF8.GetString(journal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(record => System.Text.Encoding.UTF8.GetByteCount(record))
            .ToArray();
        Assert.IsInstanceOfType<StateOpenResult.Invalid>(await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local,
            RuntimeOptions.Default with { MaxJournalRecordBytes = recordLengths.Max() - 1 },
            default));

        await File.WriteAllBytesAsync(repository.StateFile, baseState);
        await File.WriteAllBytesAsync(repository.JournalFile, journal);
        var replayRecordBound = recordLengths.Max() + 1;
        var replayJournalBound = journal.Length - replayRecordBound - 2;
        Assert.IsTrue(replayJournalBound > 0);
        Assert.IsInstanceOfType<StateOpenResult.Invalid>(await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local,
            RuntimeOptions.Default with
            {
                MaxJournalRecordBytes = replayRecordBound,
                MaxJournalBytes = replayJournalBound
            },
            default));
    }

    [TestMethod]
    public async Task State_MissingPrimaryWithJournal_FailsClosedAndPreservesRecoveryArtifacts()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
        var repository = opened.Repository;
        for (var i = 0; i < 129; i++)
        {
            var relative = $"missing-primary-{i}.bin";
            var bytes = BitConverter.GetBytes(i);
            var lastWrite = new DateTime(2026, 9, 29, 2, 0, 0, DateTimeKind.Utc).AddTicks(i);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var fingerprint = FileSnapshotter.ComputeFingerprint(relative, bytes.Length, lastWrite, hash);
            var spoolFile = Path.Combine("spool", $"{fingerprint}.payload");
            await File.WriteAllBytesAsync(Path.Combine(repository.StateRoot, spoolFile), bytes);
            await repository.AddPendingAsync(
                new SnapshotDescriptor(
                    fingerprint,
                    relative,
                    bytes.Length,
                    lastWrite,
                    hash,
                    bytes.Length,
                    "application/octet-stream",
                    ".bin",
                    spoolFile),
                FileSnapshotter.CreateRemoteKey(".bin"),
                default);
        }

        var stateFile = repository.StateFile;
        var journalFile = repository.JournalFile;
        var journalBytes = await File.ReadAllBytesAsync(journalFile);
        var spoolBytes = Directory.GetFiles(repository.SpoolDirectory)
            .ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        await repository.DisposeAsync();

        await File.WriteAllBytesAsync(journalFile, journalBytes);
        File.Delete(stateFile);

        var result = await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);

        var invalid = Assert.IsInstanceOfType<StateOpenResult.Invalid>(result);
        Assert.AreEqual("state_invalid", invalid.Code);
        Assert.IsTrue(File.Exists(journalFile));
        CollectionAssert.AreEqual(journalBytes, await File.ReadAllBytesAsync(journalFile));
        var preservedSpools = Directory.GetFiles(repository.SpoolDirectory);
        Assert.AreEqual(spoolBytes.Count, preservedSpools.Length);
        foreach (var (name, bytes) in spoolBytes)
        {
            var spoolFile = Path.Combine(repository.SpoolDirectory, name);
            Assert.IsTrue(File.Exists(spoolFile), name);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(spoolFile), name);
        }
    }

    [TestMethod]
    public async Task State_FreshStoreWithoutPrimaryOrJournal_Initializes()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();

        var opened = Assert.IsInstanceOfType<StateOpenResult.Success>(await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default));

        await using var repository = opened.Repository;
        Assert.IsTrue(File.Exists(repository.StateFile));
        Assert.IsFalse(File.Exists(repository.JournalFile));
        Assert.AreEqual(0, repository.Snapshot.Items.Count);
    }

    [TestMethod]
    public async Task State_TornJournalTail_FailsClosedAndPreservesRecoveryArtifacts()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
        var repository = opened.Repository;
        for (var i = 0; i < 129; i++)
        {
            var bytes = BitConverter.GetBytes(i);
            var relative = $"tail-{i}.bin";
            var lastWrite = clock.UtcNow.AddTicks(i + 1);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var fingerprint = FileSnapshotter.ComputeFingerprint(relative, bytes.Length, lastWrite, hash);
            var spoolFile = Path.Combine("spool", $"{fingerprint}.payload");
            await File.WriteAllBytesAsync(Path.Combine(repository.StateRoot, spoolFile), bytes);
            await repository.AddPendingAsync(new SnapshotDescriptor(
                fingerprint, relative, bytes.Length, lastWrite, hash, bytes.Length,
                "application/octet-stream", ".bin", spoolFile),
                FileSnapshotter.CreateRemoteKey(".bin"), default);
        }
        var stateBytes = await File.ReadAllBytesAsync(repository.StateFile);
        var journalBytes = await File.ReadAllBytesAsync(repository.JournalFile);
        await repository.DisposeAsync();
        await File.WriteAllBytesAsync(repository.StateFile, stateBytes);
        await File.WriteAllBytesAsync(repository.JournalFile, [.. journalBytes, .. "partial"u8.ToArray()]);

        Assert.IsInstanceOfType<StateOpenResult.Invalid>(await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default));
        Assert.IsTrue(File.Exists(repository.JournalFile));
        Assert.IsTrue((await File.ReadAllBytesAsync(repository.JournalFile)).AsSpan().EndsWith("partial"u8));
    }

    [TestMethod]
    public async Task State_LargeStoreForcedCancellationBetweenJournalRecordAndTerminator_PreservesPriorDurableState()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var recordWrites = 0;
        var injector = new StateRepositoryFaultInjector
        {
            AfterJournalRecordWrite = () =>
            {
                if (Interlocked.Increment(ref recordWrites) == 2)
                    throw new OperationCanceledException("Injected forced cancellation.");
            }
        };
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch,
            UploaderConstants.EventId,
            clock,
            paths.Local,
            RuntimeOptions.Default,
            default,
            injector);
        var repository = opened.Repository;
        string lastFingerprint = "";
        for (var i = 0; i < 129; i++)
            lastFingerprint = await AddRawPendingAsync(
                repository,
                clock.UtcNow.AddTicks(i + 1),
                $"forced-cancel-{i}.bin",
                BitConverter.GetBytes(i));

        var priorJournal = await File.ReadAllBytesAsync(repository.JournalFile);
        var priorSpools = Directory.GetFiles(repository.SpoolDirectory)
            .ToDictionary(file => Path.GetFileName(file)!, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => repository.RecordCycleFailureAsync(
            lastFingerprint,
            "must_not_apply",
            1,
            clock.UtcNow.AddMinutes(1),
            default));
        CollectionAssert.AreEqual(priorJournal, await File.ReadAllBytesAsync(repository.JournalFile));
        await repository.DisposeAsync();

        var reopened = Assert.IsInstanceOfType<StateOpenResult.Success>(await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default));
        await using (reopened.Repository)
        {
            Assert.AreEqual(129, reopened.Repository.Snapshot.Items.Count);
            var priorItem = reopened.Repository.GetRequired(lastFingerprint);
            Assert.IsNull(priorItem.LastCycleOutcome);
            Assert.AreEqual(0, priorItem.LastCycleAttempts);
            foreach (var (name, bytes) in priorSpools)
            {
                var spoolFile = Path.Combine(reopened.Repository.SpoolDirectory, name);
                Assert.IsTrue(File.Exists(spoolFile), name);
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(spoolFile), name);
            }

            await reopened.Repository.RecordCycleFailureAsync(
                lastFingerprint,
                "retry_after_restart",
                1,
                clock.UtcNow.AddMinutes(2),
                default);
        }

        var final = Assert.IsInstanceOfType<StateOpenResult.Success>(await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default));
        await using (final.Repository)
        {
            var item = final.Repository.GetRequired(lastFingerprint);
            Assert.AreEqual("retry_after_restart", item.LastCycleOutcome);
            Assert.AreEqual(1, item.LastCycleAttempts);
        }
    }

    [TestMethod]
    public async Task State_AtomicPersistenceFailure_PreservesPriorState()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
        await using var repository = opened.Repository;
        var prior = await File.ReadAllBytesAsync(repository.StateFile);
        var source = Path.Combine(paths.Watch, "locked-state.jpg");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var info = new FileInfo(source);
        var snapshot = (SnapshotResult.Ready)await new FileSnapshotter(
            repository.SpoolDirectory, repository.SpoolBudget).TrySnapshotAsync(
            source, "locked-state.jpg", new FileObservation(info.Length, info.LastWriteTimeUtc), default);
        await using var locked = new FileStream(
            repository.StateFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsExactlyAsync<IOException>(() => repository.AddPendingAsync(
            snapshot.Snapshot, FileSnapshotter.CreateRemoteKey(".jpg"), default));
        CollectionAssert.AreEqual(prior, await File.ReadAllBytesAsync(repository.StateFile));
    }

    [TestMethod]
    public async Task State_RuntimeJournalCompaction_BoundsRecordsAndReopens()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var options = RuntimeOptions.Default with { MaxJournalRecords = 2 };
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, options, default);
        var repository = opened.Repository;
        string lastFingerprint = "";
        for (var i = 0; i < 129; i++)
            lastFingerprint = await AddRawPendingAsync(repository, clock.UtcNow.AddTicks(i + 1), $"compact-{i}.bin", BitConverter.GetBytes(i));
        Assert.IsTrue(File.Exists(repository.JournalFile));
        await repository.RecordCycleFailureAsync(
            lastFingerprint, "retry", 1, clock.UtcNow.AddMinutes(1), default);
        await repository.RecordCycleFailureAsync(
            lastFingerprint, "retry", 1, clock.UtcNow.AddMinutes(1), default);
        Assert.IsFalse(File.Exists(repository.JournalFile));
        await repository.DisposeAsync();

        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, options, default);
        await using (reopened.Repository)
            Assert.AreEqual(129, reopened.Repository.Snapshot.Items.Count);
    }

    [TestMethod]
    public async Task State_CompactionDeleteFailure_RetainedOlderJournalCannotRollbackBase()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var options = RuntimeOptions.Default with { MaxJournalRecords = 1 };
        var deleteAttempts = 0;
        var injector = new StateRepositoryFaultInjector
        {
            BeforeJournalDelete = () =>
            {
                if (Interlocked.Increment(ref deleteAttempts) == 1)
                    throw new IOException("Injected journal deletion failure.");
            }
        };
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch,
            UploaderConstants.EventId,
            clock,
            paths.Local,
            options,
            default,
            injector);
        var repository = opened.Repository;
        string lastFingerprint = "";
        for (var i = 0; i < 129; i++)
            lastFingerprint = await AddRawPendingAsync(
                repository,
                clock.UtcNow.AddTicks(i + 1),
                $"delete-failure-{i}.bin",
                BitConverter.GetBytes(i));

        var retainedJournal = await File.ReadAllBytesAsync(repository.JournalFile);
        await Assert.ThrowsExactlyAsync<IOException>(() => repository.RecordCycleFailureAsync(
            lastFingerprint,
            "retry_after_compaction",
            1,
            clock.UtcNow.AddMinutes(1),
            default));
        Assert.IsTrue(File.Exists(repository.JournalFile));
        CollectionAssert.AreEqual(retainedJournal, await File.ReadAllBytesAsync(repository.JournalFile));
        var baseJson = JsonNode.Parse(await File.ReadAllTextAsync(repository.StateFile))!.AsObject();
        Assert.IsTrue(baseJson["mutationSequence"]!.GetValue<long>() > 0);
        await repository.DisposeAsync();

        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch,
            UploaderConstants.EventId,
            clock,
            paths.Local,
            options,
            default);
        await using (reopened.Repository)
        {
            Assert.AreEqual(129, reopened.Repository.Snapshot.Items.Count);
            var item = reopened.Repository.GetRequired(lastFingerprint);
            Assert.AreEqual("retry_after_compaction", item.LastCycleOutcome);
            Assert.AreEqual(1, item.LastCycleAttempts);
            Assert.IsFalse(File.Exists(reopened.Repository.JournalFile));
        }
    }

    [TestMethod]
    public async Task StateAndSpoolDirectoryIdentities_ArePinnedDuringRepositoryLifetime()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var (repository, snapshot) = await CreatePendingAsync(paths, clock, [1, 2, 3]);
        await using (repository)
        if (OperatingSystem.IsWindows())
        {
            var replaced = repository.SpoolDirectory + "-replaced";
            Directory.Move(repository.SpoolDirectory, replaced);
            try
            {
                await Assert.ThrowsExactlyAsync<IOException>(() => repository.RecordCycleFailureAsync(
                    snapshot.Fingerprint, "retry", 1, clock.UtcNow.AddMinutes(1), default));
            }
            finally
            {
                Directory.Move(replaced, repository.SpoolDirectory);
            }
        }
        Assert.IsTrue(PathSecurity.HasPrivateDirectoryAcl(repository.StateRoot));
        Assert.IsTrue(PathSecurity.HasPrivateDirectoryAcl(repository.SpoolDirectory));
    }

    [TestMethod]
    public async Task ExistingStoreWithAclDrift_FailsClosedWithoutRepairingPermissions()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var paths = new TestPaths();
        var clock = new FakeClock();
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
        var stateRoot = opened.Repository.StateRoot;
        await opened.Repository.DisposeAsync();

        var security = new DirectoryInfo(stateRoot).GetAccessControl(AccessControlSections.Access);
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        new DirectoryInfo(stateRoot).SetAccessControl(security);
        Assert.IsFalse(new DirectoryInfo(stateRoot).GetAccessControl(
            AccessControlSections.Access).AreAccessRulesProtected);

        var result = await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
        if (result is StateOpenResult.Success unexpected)
            await unexpected.Repository.DisposeAsync();

        Assert.IsInstanceOfType<StateOpenResult.Invalid>(result);
        Assert.IsFalse(new DirectoryInfo(stateRoot).GetAccessControl(
            AccessControlSections.Access).AreAccessRulesProtected);
        Assert.IsTrue(File.Exists(Path.Combine(stateRoot, "state.v1.json")));
    }

    [TestMethod]
    public async Task StateRootReplacement_IsRejectedByPinnedIdentity()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var paths = new TestPaths();
        var clock = new FakeClock();
        var (repository, snapshot) = await CreatePendingAsync(
            paths,
            clock,
            [1, 2, 3],
            "identity.bin");
        var stateRoot = repository.StateRoot;
        var originalState = await File.ReadAllBytesAsync(repository.StateFile);
        var originalSpool = await File.ReadAllBytesAsync(repository.ResolveSpool(
            repository.GetRequired(snapshot.Fingerprint)));
        var privateInstance = System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic;
        var lockField = typeof(StateRepository).GetField("instanceLock", privateInstance);
        var stateTrustField = typeof(StateRepository).GetField("stateRootTrust", privateInstance);
        var spoolTrustField = typeof(StateRepository).GetField("spoolRootTrust", privateInstance);
        Assert.IsNotNull(lockField);
        Assert.IsNotNull(stateTrustField);
        Assert.IsNotNull(spoolTrustField);
        await ((FileStream)lockField.GetValue(repository)!).DisposeAsync();
        ((TrustedRoot)spoolTrustField.GetValue(repository)!).Dispose();
        ((TrustedRoot)stateTrustField.GetValue(repository)!).Dispose();

        var replaced = stateRoot + "-original";
        Directory.Move(stateRoot, replaced);
        Directory.CreateDirectory(stateRoot);
        Directory.CreateDirectory(Path.Combine(stateRoot, "spool"));
        PathSecurity.HardenPrivateDirectory(stateRoot);
        PathSecurity.HardenPrivateDirectory(Path.Combine(stateRoot, "spool"));
        try
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => repository.RecordCycleFailureAsync(
                snapshot.Fingerprint,
                "must_not_persist",
                1,
                clock.UtcNow.AddMinutes(1),
                default));
            Assert.AreEqual(0, Directory.EnumerateFiles(stateRoot, "*", SearchOption.AllDirectories).Count());
            CollectionAssert.AreEqual(
                originalState,
                await File.ReadAllBytesAsync(Path.Combine(replaced, "state.v1.json")));
            CollectionAssert.AreEqual(
                originalSpool,
                await File.ReadAllBytesAsync(Path.Combine(
                    replaced,
                    "spool",
                    $"{snapshot.Fingerprint}.payload")));
            Assert.IsFalse(File.Exists(Path.Combine(replaced, "state.v1.journal")));
        }
        finally
        {
            await repository.DisposeAsync();
            Directory.Delete(stateRoot, recursive: true);
            Directory.Move(replaced, stateRoot);
        }
    }

    [TestMethod]
    public async Task DuplicateCleanup_NeverDeletesActiveStatePayload()
    {
        using var paths = new TestPaths();
        var (repository, snapshot) = await CreatePendingAsync(
            paths,
            new FakeClock(),
            [1, 2, 3],
            "active.bin");
        await using (repository)
        {
            var spool = repository.ResolveSpool(repository.GetRequired(snapshot.Fingerprint));
            repository.SpoolBudget.RefreshCommitted();
            repository.DeleteNewCompletedDuplicateSpool(snapshot);
            Assert.IsTrue(File.Exists(spool));
            Assert.AreEqual(3L, repository.SpoolBudget.AccountedBytes);
            Assert.AreEqual(UploadStatus.PendingPut, repository.GetRequired(snapshot.Fingerprint).Status);
        }
    }

    internal static async Task<(StateRepository Repository, SnapshotDescriptor Snapshot)> CreatePendingAsync(
        TestPaths paths, FakeClock clock, byte[] bytes, string relative = "photo.jpg")
    {
        var source = Path.Combine(paths.Watch, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source, bytes);
        var opened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
        SnapshotDescriptor descriptor;
        if (SupportedMedia.IsSupportedPath(relative))
        {
            descriptor = ((SnapshotResult.Ready)await new FileSnapshotter(opened.Repository.SpoolDirectory)
                .TrySnapshotAsync(
                    source,
                    relative,
                    new FileObservation(bytes.Length, File.GetLastWriteTimeUtc(source)),
                    default)).Snapshot;
        }
        else
        {
            var lastWrite = File.GetLastWriteTimeUtc(source);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var fingerprint = FileSnapshotter.ComputeFingerprint(relative, bytes.Length, lastWrite, hash);
            var spoolFile = Path.Combine("spool", $"{fingerprint}.payload");
            await File.WriteAllBytesAsync(Path.Combine(opened.Repository.StateRoot, spoolFile), bytes);
            descriptor = new SnapshotDescriptor(
                fingerprint,
                relative,
                bytes.Length,
                lastWrite,
                hash,
                bytes.Length,
                ContentTypeMap.Get(Path.GetExtension(relative)),
                ContentTypeMap.SanitizeExtension(Path.GetExtension(relative)),
                spoolFile);
        }
        await opened.Repository.AddPendingAsync(
            descriptor, FileSnapshotter.CreateRemoteKey(descriptor.Extension), default);
        return (opened.Repository, descriptor);
    }

    private static async Task<string> AddRawPendingAsync(
        StateRepository repository, DateTime lastWrite, string relative, byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var fingerprint = FileSnapshotter.ComputeFingerprint(relative, bytes.Length, lastWrite, hash);
        var spoolFile = Path.Combine("spool", $"{fingerprint}.payload");
        await File.WriteAllBytesAsync(Path.Combine(repository.StateRoot, spoolFile), bytes);
        await repository.AddPendingAsync(new SnapshotDescriptor(
            fingerprint, relative, bytes.Length, lastWrite, hash, bytes.Length,
            "application/octet-stream", ".bin", spoolFile),
            FileSnapshotter.CreateRemoteKey(".bin"), default);
        return fingerprint;
    }
}
