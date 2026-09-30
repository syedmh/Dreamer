using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TCFUploader.Configuration;
using TCFUploader.Files;
using TCFUploader.Time;
using TCFUploader.Upload;

namespace TCFUploader.State;

internal abstract record StateOpenResult
{
    internal sealed record Success(StateRepository Repository) : StateOpenResult;
    internal sealed record Invalid(string Code, string Message) : StateOpenResult;
    internal sealed record Locked(string Code, string Message) : StateOpenResult;
}

internal sealed class StateRepository : IAsyncDisposable
{
    private const int FullSnapshotItemLimit = 128;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    private static readonly JsonSerializerOptions JournalJsonOptions = new(JsonOptions)
    {
        WriteIndented = false
    };
    private static readonly byte[] JournalRecordTerminator = "\n"u8.ToArray();
    private readonly IClock clock;
    private readonly RuntimeOptions options;
    private readonly FileStream instanceLock;
    private readonly TrustedRoot trustedRoot;
    private readonly TrustedRoot stateRootTrust;
    private readonly TrustedRoot spoolRootTrust;
    private readonly StateRepositoryFaultInjector? faultInjector;
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly object stateLock = new();
    private UploaderState state;
    private bool persistenceFaulted;
    private int journalRecordCount;

    private StateRepository(
        string root,
        IClock clock,
        FileStream instanceLock,
        UploaderState state,
        TrustedRoot trustedRoot,
        TrustedRoot stateRootTrust,
        TrustedRoot spoolRootTrust,
        RuntimeOptions options,
        StateRepositoryFaultInjector? faultInjector)
    {
        StateRoot = root;
        SpoolDirectory = Path.Combine(root, "spool");
        StateFile = Path.Combine(root, "state.v1.json");
        JournalFile = Path.Combine(root, "state.v1.journal");
        this.clock = clock;
        this.instanceLock = instanceLock;
        this.state = state;
        this.trustedRoot = trustedRoot;
        this.stateRootTrust = stateRootTrust;
        this.spoolRootTrust = spoolRootTrust;
        this.options = options;
        this.faultInjector = faultInjector;
        SpoolBudget = new SpoolBudget(
            SpoolDirectory,
            options.AggregateSpoolLimitBytes,
            options.MinimumFreeSpaceReserveBytes);
    }

    internal string StateRoot { get; }
    internal string SpoolDirectory { get; }
    internal string StateFile { get; }
    internal string JournalFile { get; }
    internal TrustedRoot TrustedRoot => trustedRoot;
    internal TrustedRoot SpoolRootTrust => spoolRootTrust;
    internal SpoolBudget SpoolBudget { get; }
    internal UploaderState Snapshot
    {
        get
        {
            lock (stateLock)
                return state with { Items = new(state.Items, StringComparer.OrdinalIgnoreCase) };
        }
    }

    internal static Task<StateOpenResult> OpenAsync(
        string watchedRoot, string eventId, IClock clock, CancellationToken cancellationToken) =>
        OpenAsync(watchedRoot, eventId, clock,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            RuntimeOptions.Default,
            cancellationToken);

    internal static async Task<StateOpenResult> OpenAsync(
        string watchedRoot, string eventId, IClock clock, string localAppData, CancellationToken cancellationToken) =>
        await OpenAsync(watchedRoot, eventId, clock, localAppData, RuntimeOptions.Default, cancellationToken);

    internal static async Task<StateOpenResult> OpenAsync(
        string watchedRoot,
        string eventId,
        IClock clock,
        string localAppData,
        RuntimeOptions options,
        CancellationToken cancellationToken,
        StateRepositoryFaultInjector? faultInjector = null)
    {
        watchedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(watchedRoot));
        var root = Path.Combine(localAppData, "TCFUploader", "events", Scope(eventId), Scope(watchedRoot.ToUpperInvariant()));
        if (!TrustedRoot.TryCreate(watchedRoot, out var trustedRoot) || PathsOverlapPhysically(watchedRoot, root))
        {
            trustedRoot?.Dispose();
            return new StateOpenResult.Invalid("state_overlap", "The state directory must be outside the watched tree.");
        }
        var pinnedRoot = trustedRoot!;

        var spool = Path.Combine(root, "spool");
        var existingStore = Directory.Exists(root);
        try
        {
            if (existingStore)
            {
                if (!Directory.Exists(spool) ||
                    !PathSecurity.HasNoReparsePointComponents(root) ||
                    !PathSecurity.HasNoReparsePointComponents(spool) ||
                    !PathSecurity.HasPrivateDirectoryAcl(root) ||
                    !PathSecurity.HasPrivateDirectoryAcl(spool))
                {
                    pinnedRoot.Dispose();
                    return new StateOpenResult.Invalid(
                        "state_identity",
                        "The existing state directory identity or permissions are invalid.");
                }
            }
            else
            {
                Directory.CreateDirectory(spool);
                PathSecurity.HardenPrivateDirectory(root);
                PathSecurity.HardenPrivateDirectory(spool);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            pinnedRoot.Dispose();
            return new StateOpenResult.Invalid("state_unwritable", "The state directory is not writable.");
        }
        try
        {
            if (PathsOverlapPhysically(watchedRoot, root) ||
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(spool) & FileAttributes.ReparsePoint) != 0 ||
                !PathSecurity.HasPrivateDirectoryAcl(root) ||
                !PathSecurity.HasPrivateDirectoryAcl(spool))
            {
                pinnedRoot.Dispose();
                return new StateOpenResult.Invalid("state_overlap", "The state directory must be outside the watched tree.");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            pinnedRoot.Dispose();
            return new StateOpenResult.Invalid("state_unwritable", "The state directory is not writable.");
        }
        try
        {
            var probe = Path.Combine(root, $"{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(probe, [], cancellationToken);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            pinnedRoot.Dispose();
            return new StateOpenResult.Invalid("state_unwritable", "The state directory is not writable.");
        }

        if (!TrustedRoot.TryCreate(root, out var stateRootTrustCandidate, retainHandle: true))
        {
            pinnedRoot.Dispose();
            return new StateOpenResult.Invalid("state_identity", "The state directory identity or permissions are invalid.");
        }
        var stateRootTrust = stateRootTrustCandidate!;
        if (!TrustedRoot.TryCreate(spool, out var spoolRootTrustCandidate, retainHandle: true))
        {
            stateRootTrust.Dispose();
            pinnedRoot.Dispose();
            return new StateOpenResult.Invalid("state_identity", "The state directory identity or permissions are invalid.");
        }
        var spoolRootTrust = spoolRootTrustCandidate!;

        FileStream lockStream;
        try
        {
            lockStream = new FileStream(Path.Combine(root, "instance.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            stateRootTrust.Dispose();
            spoolRootTrust.Dispose();
            pinnedRoot.Dispose();
            return new StateOpenResult.Locked("state_locked", "Another uploader instance owns this state store.");
        }

        try
        {
            var file = Path.Combine(root, "state.v1.json");
            var journal = Path.Combine(root, "state.v1.journal");
            UploaderState state;
            if (!File.Exists(file))
            {
                if (File.Exists(journal))
                    throw new InvalidDataException("The primary state file is missing while a journal exists.");
                state = new UploaderState(1, eventId, watchedRoot, clock.UtcNow,
                    new Dictionary<string, UploadItemState>(StringComparer.OrdinalIgnoreCase));
                var repository = new StateRepository(
                    root,
                    clock,
                    lockStream,
                    state,
                    pinnedRoot,
                    stateRootTrust,
                    spoolRootTrust,
                    options,
                    faultInjector);
                await repository.PersistAsync(cancellationToken);
                return new StateOpenResult.Success(repository);
            }

            await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                state = (await JsonSerializer.DeserializeAsync<UploaderState>(stream, JsonOptions, cancellationToken))
                    ?? throw new JsonException();
            var loaded = new StateRepository(
                root,
                clock,
                lockStream,
                state,
                pinnedRoot,
                stateRootTrust,
                spoolRootTrust,
                options,
                faultInjector);
            var replayedJournal = File.Exists(loaded.JournalFile);
            if (replayedJournal)
                await loaded.ReplayJournalAsync(cancellationToken);
            await loaded.ValidateAsync(watchedRoot, eventId, cancellationToken);
            if (replayedJournal)
            {
                await loaded.PersistAsync(cancellationToken);
                loaded.DeleteJournal();
            }
            loaded.CleanupOrphans();
            loaded.SpoolBudget.RefreshCommitted();
            return new StateOpenResult.Success(loaded);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            await lockStream.DisposeAsync();
            stateRootTrust.Dispose();
            spoolRootTrust.Dispose();
            pinnedRoot.Dispose();
            return new StateOpenResult.Invalid("state_invalid", "State is corrupt, unreadable, incompatible, or violates spool invariants.");
        }
    }

    internal IReadOnlyList<UploadWork> GetResumableWork(DateTime nowUtc)
    {
        lock (stateLock)
            return state.Items.Where(pair => pair.Value.Status is UploadStatus.PendingPut or UploadStatus.PutComplete &&
                (pair.Value.NextEligibleUtc is null || pair.Value.NextEligibleUtc <= nowUtc))
                .Select(pair => new UploadWork(pair.Key)).ToArray();
    }

    internal bool ContainsActiveOrCompleted(string fingerprint)
    {
        lock (stateLock) return state.Items.ContainsKey(fingerprint);
    }
    internal UploadStatus? GetKnownStatus(string fingerprint)
    {
        lock (stateLock)
            return state.Items.TryGetValue(fingerprint, out var item) ? item.Status : null;
    }
    internal UploadItemState GetRequired(string fingerprint)
    {
        lock (stateLock)
            return state.Items.TryGetValue(fingerprint, out var item)
                ? item : throw new InvalidOperationException("Unknown fingerprint.");
    }
    internal string ResolveSpool(UploadItemState item)
    {
        return ResolveSafe(StateRoot, item.SpoolFile);
    }

    internal async Task AddPendingAsync(SnapshotDescriptor snapshot, string remoteKey, CancellationToken cancellationToken)
    {
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            VerifyStorage();
            lock (stateLock)
            {
                if (state.Items.ContainsKey(snapshot.Fingerprint)) return;
                var now = clock.UtcNow;
                state.Items.Add(snapshot.Fingerprint, new UploadItemState(
                    UploadStatus.PendingPut, snapshot.RelativePath, snapshot.ObservedLength, snapshot.ObservedLastWriteUtc,
                    snapshot.ContentSha256, snapshot.ByteLength, snapshot.ContentType, snapshot.Extension, snapshot.SpoolFile,
                    remoteKey, null, now, now, null, null, null, 0));
            }
            await PersistMutationAsync(snapshot.Fingerprint, cancellationToken);
        }
        finally { mutationGate.Release(); }
    }

    internal async Task MarkPutCompleteAsync(string fingerprint, Uri remoteUrl, CancellationToken cancellationToken)
    {
        if (!IsAllowedMediaUri(remoteUrl)) throw new InvalidDataException("Remote media URL is invalid.");
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            VerifyStorage();
            lock (stateLock)
            {
                var item = GetRequiredLocked(fingerprint);
                state.Items[fingerprint] = item with
                {
                    Status = UploadStatus.PutComplete, RemoteUrl = remoteUrl.AbsoluteUri, UpdatedUtc = clock.UtcNow,
                    LastCycleOutcome = null, LastCycleAttempts = 0, NextEligibleUtc = null
                };
            }
            await PersistMutationAsync(fingerprint, cancellationToken);
        }
        finally { mutationGate.Release(); }
    }

    internal async Task RecordCycleFailureAsync(
        string fingerprint, string outcome, int attempts, DateTime nextEligibleUtc, CancellationToken cancellationToken)
    {
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            VerifyStorage();
            lock (stateLock)
            {
                var item = GetRequiredLocked(fingerprint);
                state.Items[fingerprint] = item with
                {
                    UpdatedUtc = clock.UtcNow, LastCycleOutcome = LimitOutcome(outcome),
                    LastCycleAttempts = attempts, NextEligibleUtc = nextEligibleUtc
                };
            }
            await PersistMutationAsync(fingerprint, cancellationToken);
        }
        finally { mutationGate.Release(); }
    }

    internal async Task MarkCompletedAsync(string fingerprint, CancellationToken cancellationToken)
    {
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            VerifyStorage();
            lock (stateLock)
            {
                var item = GetRequiredLocked(fingerprint);
                var now = clock.UtcNow;
                state.Items[fingerprint] = item with
                {
                    Status = UploadStatus.Completed, UpdatedUtc = now, CompletedUtc = now,
                    NextEligibleUtc = null, LastCycleOutcome = null, LastCycleAttempts = 0
                };
            }
            await PersistMutationAsync(fingerprint, cancellationToken);
        }
        finally { mutationGate.Release(); }
    }

    internal void DeleteCompletedSpool(UploadItemState item)
    {
        faultInjector?.BeforeCompletedSpoolDelete?.Invoke();
        VerifyStorage();
        var spool = ResolveSafe(StateRoot, item.SpoolFile);
        if (!File.Exists(spool))
            return;
        File.Delete(spool);
        SpoolBudget.ReleaseCommitted(item.ByteLength);
    }

    internal void DeleteNewCompletedDuplicateSpool(SnapshotDescriptor snapshot)
    {
        faultInjector?.BeforeNewCompletedDuplicateSpoolDelete?.Invoke();
        VerifyStorage();
        lock (stateLock)
        {
            var item = GetRequiredLocked(snapshot.Fingerprint);
            if (item.Status != UploadStatus.Completed)
                return;
            if (!string.Equals(item.SpoolFile, snapshot.SpoolFile, StringComparison.OrdinalIgnoreCase) ||
                item.ByteLength != snapshot.ByteLength)
                throw new InvalidDataException("The duplicate spool payload does not match durable state.");
        }

        var spool = ResolveSafe(StateRoot, snapshot.SpoolFile);
        if (!File.Exists(spool))
            throw new IOException("The newly created duplicate spool payload is missing.");
        File.Delete(spool);
        SpoolBudget.ReleaseCommitted(snapshot.ByteLength);
    }

    private async Task PersistMutationAsync(string fingerprint, CancellationToken cancellationToken)
    {
        try
        {
            JournalPayload payload;
            int itemCount;
            lock (stateLock)
            {
                state = state with
                {
                    UpdatedUtc = clock.UtcNow,
                    MutationSequence = checked(state.MutationSequence + 1)
                };
                payload = new JournalPayload(
                    state.MutationSequence,
                    state.UpdatedUtc,
                    fingerprint,
                    GetRequiredLocked(fingerprint));
                itemCount = state.Items.Count;
            }
            if (itemCount <= FullSnapshotItemLimit && !File.Exists(JournalFile))
            {
                await PersistAsync(cancellationToken);
                return;
            }
            if (File.Exists(JournalFile) &&
                (journalRecordCount >= options.MaxJournalRecords ||
                 new FileInfo(JournalFile).Length >= options.MaxJournalBytes))
            {
                await PersistAsync(cancellationToken);
                DeleteJournal();
                journalRecordCount = 0;
                return;
            }
            await AppendJournalAsync(payload, cancellationToken);
        }
        catch
        {
            persistenceFaulted = true;
            throw;
        }
    }

    private async Task AppendJournalAsync(JournalPayload payload, CancellationToken cancellationToken)
    {
        VerifyStorage();
        cancellationToken.ThrowIfCancellationRequested();
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JournalJsonOptions);
        var record = new JournalRecord(
            payload.Sequence,
            payload.UpdatedUtc,
            payload.Fingerprint,
            payload.Item,
            Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant());
        var recordBytes = JsonSerializer.SerializeToUtf8Bytes(record, JournalJsonOptions);
        if (recordBytes.Length > options.MaxJournalRecordBytes)
            throw new InvalidDataException("The state journal record exceeds its configured bound.");
        if (recordBytes.Length + 1L > options.MaxJournalBytes)
            throw new InvalidDataException("The state journal record exceeds the journal bound.");
        var appendBytes = new byte[recordBytes.Length + JournalRecordTerminator.Length];
        recordBytes.CopyTo(appendBytes, 0);
        JournalRecordTerminator.CopyTo(appendBytes, recordBytes.Length);
        var originalLength = File.Exists(JournalFile) ? new FileInfo(JournalFile).Length : 0L;

        FileStream? stream = new(
            JournalFile,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        try
        {
            if (faultInjector?.AfterJournalRecordWrite is not null)
            {
                await stream.WriteAsync(recordBytes, CancellationToken.None);
                faultInjector.AfterJournalRecordWrite();
                await stream.WriteAsync(JournalRecordTerminator, CancellationToken.None);
            }
            else
            {
                await stream.WriteAsync(appendBytes, CancellationToken.None);
            }
            await stream.FlushAsync(CancellationToken.None);
            stream.Flush(true);
            journalRecordCount++;
        }
        catch
        {
            await stream.DisposeAsync();
            stream = null;
            using var rollback = new FileStream(
                JournalFile,
                FileMode.Open,
                FileAccess.Write,
                FileShare.Read,
                1,
                FileOptions.WriteThrough);
            rollback.SetLength(originalLength);
            rollback.Flush(true);
            throw;
        }
        finally
        {
            if (stream is not null)
                await stream.DisposeAsync();
        }
    }

    private async Task ReplayJournalAsync(CancellationToken cancellationToken)
    {
        VerifyStorage();
        var info = new FileInfo(JournalFile);
        if (info.Length > options.MaxJournalBytes + options.MaxJournalRecordBytes + 1L)
            throw new InvalidDataException("The state journal exceeds its configured bound.");
        await using var stream = new FileStream(
            JournalFile, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        using var recordBuffer = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] == (byte)'\n')
                {
                    if (recordBuffer.Length == 0)
                        throw new InvalidDataException();
                    ReplayRecord(recordBuffer.GetBuffer().AsSpan(0, checked((int)recordBuffer.Length)));
                    recordBuffer.SetLength(0);
                    journalRecordCount++;
                    if (journalRecordCount > options.MaxJournalRecords)
                        throw new InvalidDataException("The state journal record count exceeds its configured bound.");
                }
                else
                {
                    if (recordBuffer.Length >= options.MaxJournalRecordBytes)
                        throw new InvalidDataException("The state journal record exceeds its configured bound.");
                    recordBuffer.WriteByte(buffer[index]);
                }
            }
        }
        if (recordBuffer.Length != 0)
            throw new InvalidDataException("The state journal has an unterminated final record.");

        void ReplayRecord(ReadOnlySpan<byte> bytes)
        {
            var record = JsonSerializer.Deserialize<JournalRecord>(bytes, JournalJsonOptions)
                ?? throw new InvalidDataException();
            if (record.Sequence <= 0)
                throw new InvalidDataException();
            var payload = new JournalPayload(
                record.Sequence,
                record.UpdatedUtc,
                record.Fingerprint,
                record.Item);
            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JournalJsonOptions);
            var checksum = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(checksum),
                    Encoding.ASCII.GetBytes(record.Checksum)))
                throw new InvalidDataException();
            lock (stateLock)
            {
                if (record.Sequence <= state.MutationSequence)
                    return;
                if (record.Sequence != checked(state.MutationSequence + 1))
                    throw new InvalidDataException("The state journal mutation sequence is discontinuous.");
                state.Items[record.Fingerprint] = record.Item;
                state = state with
                {
                    UpdatedUtc = record.UpdatedUtc,
                    MutationSequence = record.Sequence
                };
            }
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        VerifyStorage();
        UploaderState persisted;
        lock (stateLock)
        {
            state = state with { UpdatedUtc = clock.UtcNow };
            persisted = state;
        }
        var temporary = Path.Combine(StateRoot, $"{Guid.NewGuid():N}.tmp");
        var backup = Path.Combine(StateRoot, $"{Guid.NewGuid():N}.bak");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, persisted, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            if (File.Exists(StateFile))
                File.Replace(temporary, StateFile, backup, ignoreMetadataErrors: true);
            else
                File.Move(temporary, StateFile);
            faultInjector?.AfterBaseReplace?.Invoke();
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { if (File.Exists(backup)) File.Delete(backup); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task ValidateAsync(string watchedRoot, string eventId, CancellationToken cancellationToken)
    {
        VerifyStorage();
        if (state.SchemaVersion != 1 || string.IsNullOrEmpty(state.EventId) || state.EventId != eventId ||
            string.IsNullOrWhiteSpace(state.WatchedRoot) || state.Items is null ||
            !IsUtc(state.UpdatedUtc) || state.MutationSequence < 0 ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(state.WatchedRoot)), watchedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException();
        foreach (var pair in state.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = pair.Value ?? throw new InvalidDataException();
            if (!IsLowerHex(pair.Key) || !Enum.IsDefined(item.Status) ||
                string.IsNullOrWhiteSpace(item.RelativePath) || !IsLowerHex(item.ContentSha256) ||
                item.ContentType is null || item.Extension is null || item.SpoolFile is null || item.RemoteKey is null ||
                item.ByteLength < 0 || item.ObservedLength != item.ByteLength ||
                !IsUtc(item.ObservedLastWriteUtc) || !IsUtc(item.CreatedUtc) || !IsUtc(item.UpdatedUtc) ||
                item.UpdatedUtc < item.CreatedUtc || state.UpdatedUtc < item.UpdatedUtc ||
                item.LastCycleAttempts is < 0 or > 5)
                throw new InvalidDataException();
            var relative = FileSnapshotter.NormalizeRelativePath(item.RelativePath);
            if (!string.Equals(relative, item.RelativePath, StringComparison.Ordinal) ||
                FileSnapshotter.ComputeFingerprint(relative, item.ObservedLength, item.ObservedLastWriteUtc, item.ContentSha256) != pair.Key)
                throw new InvalidDataException();
            if (item.Extension != ContentTypeMap.SanitizeExtension(item.Extension) ||
                item.Extension != ContentTypeMap.SanitizeExtension(Path.GetExtension(item.RelativePath)) ||
                !ContentTypeMap.IsCompatibleStoredContentType(item.Extension, item.ContentType) ||
                !IsValidRemoteKey(item.RemoteKey, item.Extension))
                throw new InvalidDataException();
            var expectedSpool = Path.Combine("spool", $"{pair.Key}.payload");
            if (!string.Equals(item.SpoolFile, expectedSpool, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException();

            if (item.LastCycleOutcome is null)
            {
                if (item.LastCycleAttempts != 0 || item.NextEligibleUtc is not null) throw new InvalidDataException();
            }
            else if (!IsOutcome(item.LastCycleOutcome) || item.LastCycleAttempts is < 1 or > 5 ||
                item.NextEligibleUtc is null || !IsUtc(item.NextEligibleUtc.Value) || item.NextEligibleUtc < item.UpdatedUtc)
                throw new InvalidDataException();

            if (item.Status == UploadStatus.PendingPut)
            {
                if (item.RemoteUrl is not null || item.CompletedUtc is not null) throw new InvalidDataException();
            }
            else
            {
                if (item.RemoteUrl is null || !Uri.TryCreate(item.RemoteUrl, UriKind.Absolute, out var uri) ||
                    !IsAllowedMediaUri(uri)) throw new InvalidDataException();
                if (item.Status == UploadStatus.Completed)
                {
                    if (item.CompletedUtc is null || !IsUtc(item.CompletedUtc.Value) ||
                        item.CompletedUtc < item.CreatedUtc || item.CompletedUtc > item.UpdatedUtc ||
                        item.NextEligibleUtc is not null || item.LastCycleOutcome is not null || item.LastCycleAttempts != 0)
                        throw new InvalidDataException();
                    continue;
                }
                if (item.CompletedUtc is not null) throw new InvalidDataException();
            }

            var spool = ResolveSafe(StateRoot, item.SpoolFile);
            if (!PathSecurity.IsContainedOrEqual(SpoolDirectory, spool) ||
                !PathSecurity.IsSafeCandidate(StateRoot, spool))
                throw new InvalidDataException();
            var info = new FileInfo(spool);
            if (!info.Exists || info.Length != item.ByteLength) throw new InvalidDataException();
            await using var stream = File.OpenRead(spool);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (hash != item.ContentSha256) throw new InvalidDataException();
        }
    }

    private void CleanupOrphans()
    {
        VerifyStorage();
        var referenced = state.Items.Values.Where(i => i.Status != UploadStatus.Completed)
            .Select(i => ResolveSafe(StateRoot, i.SpoolFile)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(SpoolDirectory))
            if (!referenced.Contains(file)) File.Delete(file);
    }

    internal static bool IsAllowedMediaUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        string.Equals(uri.Host, UploaderConstants.MediaBaseUri.Host, StringComparison.OrdinalIgnoreCase);

    internal static bool IsContained(string parent, string child)
    {
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        child = Path.GetFullPath(child);
        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveSafe(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException();
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsContained(root, full)) throw new InvalidDataException();
        return full;
    }

    private static string Scope(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];
    private static bool IsLowerHex(string? value) =>
        value is not null && value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsUtc(DateTime value) => value != default && value.Kind == DateTimeKind.Utc;
    private static bool IsOutcome(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static bool IsValidRemoteKey(string? value, string extension)
    {
        const string prefix = "tcfuploader-";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) ||
            !value.EndsWith(extension, StringComparison.Ordinal) ||
            value.Length != prefix.Length + 32 + extension.Length)
            return false;
        return value.AsSpan(prefix.Length, 32).ToArray().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
    private static bool PathsOverlapPhysically(string first, string second)
    {
        var physicalFirst = PathSecurity.GetPhysicalPathForComparison(first);
        var physicalSecond = PathSecurity.GetPhysicalPathForComparison(second);
        return PathSecurity.IsContainedOrEqual(physicalFirst, physicalSecond) ||
            PathSecurity.IsContainedOrEqual(physicalSecond, physicalFirst);
    }
    private static string LimitOutcome(string outcome) =>
        outcome.Length <= 64 && outcome.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? outcome : "invalid_outcome";
    private UploadItemState GetRequiredLocked(string fingerprint) =>
        state.Items.TryGetValue(fingerprint, out var item)
            ? item : throw new InvalidOperationException("Unknown fingerprint.");
    private void VerifyStorage()
    {
        if (!stateRootTrust.VerifyCurrent() || !spoolRootTrust.VerifyCurrent() ||
            !PathSecurity.HasPrivateDirectoryAcl(StateRoot) ||
            !PathSecurity.HasPrivateDirectoryAcl(SpoolDirectory))
            throw new IOException("The state directory identity or permissions changed.");
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!persistenceFaulted && File.Exists(JournalFile))
            {
                await mutationGate.WaitAsync();
                try
                {
                    await PersistAsync(CancellationToken.None);
                    DeleteJournal();
                    journalRecordCount = 0;
                }
                finally { mutationGate.Release(); }
            }
        }
        finally
        {
            mutationGate.Dispose();
            await instanceLock.DisposeAsync();
            trustedRoot.Dispose();
            spoolRootTrust.Dispose();
            stateRootTrust.Dispose();
        }
    }

    private void DeleteJournal()
    {
        faultInjector?.BeforeJournalDelete?.Invoke();
        File.Delete(JournalFile);
    }

    private sealed record JournalPayload(
        long Sequence,
        DateTime UpdatedUtc,
        string Fingerprint,
        UploadItemState Item);

    private sealed record JournalRecord(
        long Sequence,
        DateTime UpdatedUtc,
        string Fingerprint,
        UploadItemState Item,
        string Checksum);
}

internal sealed class StateRepositoryFaultInjector
{
    internal Action? AfterBaseReplace { get; init; }
    internal Action? AfterJournalRecordWrite { get; init; }
    internal Action? BeforeJournalDelete { get; init; }
    internal Action? BeforeCompletedSpoolDelete { get; init; }
    internal Action? BeforeNewCompletedDuplicateSpoolDelete { get; init; }
}
