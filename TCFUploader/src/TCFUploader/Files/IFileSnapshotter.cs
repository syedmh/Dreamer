namespace TCFUploader.Files;

internal interface IFileSnapshotter
{
    Task<SnapshotResult> TrySnapshotAsync(
        string canonicalFullPath,
        string normalizedRelativePath,
        FileObservation expected,
        CancellationToken cancellationToken);
}

internal abstract record SnapshotResult
{
    internal sealed record Ready(SnapshotDescriptor Snapshot, bool PayloadCreated) : SnapshotResult;
    internal sealed record RetryLater(string OutcomeCode) : SnapshotResult;
    internal sealed record CapacityUnavailable(string OutcomeCode) : SnapshotResult;
}

internal sealed record SnapshotDescriptor(
    string Fingerprint,
    string RelativePath,
    long ObservedLength,
    DateTime ObservedLastWriteUtc,
    string ContentSha256,
    long ByteLength,
    string ContentType,
    string Extension,
    string SpoolFile);
