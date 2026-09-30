namespace TCFUploader.State;

internal sealed record UploadItemState(
    UploadStatus Status,
    string RelativePath,
    long ObservedLength,
    DateTime ObservedLastWriteUtc,
    string ContentSha256,
    long ByteLength,
    string ContentType,
    string Extension,
    string SpoolFile,
    string RemoteKey,
    string? RemoteUrl,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? CompletedUtc,
    DateTime? NextEligibleUtc,
    string? LastCycleOutcome,
    int LastCycleAttempts);
