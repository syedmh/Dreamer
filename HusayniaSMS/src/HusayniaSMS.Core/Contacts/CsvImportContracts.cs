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

public sealed record CsvImportResult(
    CsvImportStatus Status,
    IReadOnlyList<ContactRow> Rows,
    string? SafeDiagnostic);

public interface IContactCsvImporter
{
    Task<CsvImportResult> ImportAsync(string path, CancellationToken cancellationToken);
}
