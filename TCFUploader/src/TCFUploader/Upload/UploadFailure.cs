namespace TCFUploader.Upload;

internal sealed record UploadFailure(
    string OutcomeCode,
    bool IsTransient,
    bool IsAuthenticationFatal,
    TimeSpan? RetryAfter);
