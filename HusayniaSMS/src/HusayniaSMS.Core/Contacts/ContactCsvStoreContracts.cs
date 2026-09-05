namespace HusayniaSMS.Core.Contacts;

public enum CsvImportStatus
{
    Success,
    Canceled,
    FileNotFound,
    AccessDenied,
    MissingHeaders,
    MalformedCsv,
    InconsistentRecord,
    IoFailure
}

public sealed record ContactCsvVersion(
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    string Sha256Hex);

public sealed record ContactCsvLoadResult(
    CsvImportStatus Status,
    string? FullPath,
    IReadOnlyList<ContactRow> Rows,
    ContactCsvVersion? Version,
    string? SafeDiagnostic);

public enum ContactCsvSaveStatus
{
    Saved = 0,
    InvalidDocument = 1,
    ConflictModified = 2,
    ConflictDeleted = 3,
    TargetExists = 4,
    AccessDenied = 5,
    IoFailure = 6,
    AtomicReplaceUnavailable = 7
}

public sealed record ContactCsvSaveRequest(
    string Path,
    IReadOnlyList<ContactRow> Rows,
    ContactCsvVersion? ExpectedVersion);

public sealed record ContactCsvSaveResult(
    ContactCsvSaveStatus Status,
    string? FullPath,
    ContactCsvVersion? SavedVersion,
    ContactCsvVersion? CurrentVersion,
    string? SafeDiagnostic);

public interface IContactCsvStore
{
    Task<ContactCsvLoadResult> LoadAsync(
        string path,
        CancellationToken cancellationToken);

    Task<ContactCsvSaveResult> SaveAsync(
        ContactCsvSaveRequest request,
        CancellationToken cancellationToken);
}
