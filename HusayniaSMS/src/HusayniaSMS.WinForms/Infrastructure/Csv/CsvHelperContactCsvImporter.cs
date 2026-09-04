using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using HusayniaSMS.Core.Contacts;

namespace HusayniaSMS.WinForms.Infrastructure.Csv;

public sealed class CsvHelperContactCsvImporter(IContactRowValidator rowValidator)
    : IContactCsvImporter
{
    public const int MaximumLogicalRecords = 100_000;
    public const int MaximumFileBytes = 10 * 1024 * 1024;
    public const int MaximumHeaderCharacters = 256;
    public const int MaximumFieldCharacters = 4_096;

    public async Task<CsvImportResult> ImportAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(path);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumFileBytes)
            {
                return Failure(CsvImportStatus.InconsistentRecord, fileName,
                    $"CSV exceeds the {MaximumFileBytes:N0} byte file size limit.");
            }

            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);

            var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                Delimiter = ",",
                HasHeaderRecord = true,
                IgnoreBlankLines = false,
                TrimOptions = TrimOptions.Trim,
                ExceptionMessagesContainRawData = false,
                BadDataFound = _ => throw new MalformedCsvRecordException(),
                MissingFieldFound = _ => throw new InconsistentCsvRecordException()
            };

            using var csv = new CsvReader(reader, configuration);
            if (!await csv.ReadAsync().ConfigureAwait(false))
            {
                return Failure(CsvImportStatus.MissingHeaders, fileName,
                    "Required Name and Number headers were not found.");
            }

            csv.ReadHeader();
            var headers = csv.HeaderRecord ?? Array.Empty<string>();
            if (headers.Any(header => header.Length > MaximumHeaderCharacters))
            {
                return Failure(CsvImportStatus.InconsistentRecord, fileName,
                    $"CSV header exceeds the {MaximumHeaderCharacters:N0} character limit.");
            }

            var normalized = headers
                .Select((header, index) => new
                {
                    Name = header.Trim().TrimStart('\uFEFF').Trim(),
                    Index = index
                })
                .ToArray();
            var nameIndexes = normalized
                .Where(item => string.Equals(item.Name, "Name", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Index)
                .ToArray();
            var numberIndexes = normalized
                .Where(item => string.Equals(item.Name, "Number", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Index)
                .ToArray();
            if (nameIndexes.Length != 1 || numberIndexes.Length != 1)
            {
                return Failure(CsvImportStatus.MissingHeaders, fileName,
                    "CSV must contain exactly one Name header and one Number header.");
            }

            var inputs = new List<ContactInput>();
            while (await csv.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (inputs.Count == MaximumLogicalRecords)
                {
                    return Failure(CsvImportStatus.InconsistentRecord, fileName,
                        "CSV exceeds the 100,000 record limit.", csv.Parser.Row);
                }

                var record = csv.Parser.Record ?? Array.Empty<string>();
                if (record.Length != headers.Length ||
                    nameIndexes[0] >= record.Length ||
                    numberIndexes[0] >= record.Length)
                {
                    return Failure(CsvImportStatus.InconsistentRecord, fileName,
                        "CSV record has an inconsistent number of fields.", csv.Parser.Row);
                }

                if (record.Any(field => field.Length > MaximumFieldCharacters))
                {
                    return Failure(CsvImportStatus.InconsistentRecord, fileName,
                        $"CSV field exceeds the {MaximumFieldCharacters:N0} character limit.",
                        csv.Parser.Row);
                }

                inputs.Add(new ContactInput(
                    inputs.Count + 1,
                    csv.GetField(nameIndexes[0]),
                    csv.GetField(numberIndexes[0])));
            }

            return new(CsvImportStatus.Success, rowValidator.Validate(inputs), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(CsvImportStatus.Canceled, fileName, "CSV import was canceled.");
        }
        catch (FileNotFoundException)
        {
            return Failure(CsvImportStatus.FileNotFound, fileName, "CSV file was not found.");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(CsvImportStatus.AccessDenied, fileName, "Access to the CSV file was denied.");
        }
        catch (MalformedCsvRecordException)
        {
            return Failure(CsvImportStatus.MalformedCsv, fileName,
                "CSV contains malformed quoting or field data.");
        }
        catch (InconsistentCsvRecordException)
        {
            return Failure(CsvImportStatus.InconsistentRecord, fileName,
                "CSV contains an inconsistent record.");
        }
        catch (DecoderFallbackException)
        {
            return Failure(CsvImportStatus.MalformedCsv, fileName,
                "CSV is not valid UTF-8 text.");
        }
        catch (CsvHelperException)
        {
            return Failure(CsvImportStatus.MalformedCsv, fileName,
                "CSV could not be parsed reliably.");
        }
        catch (IOException)
        {
            return Failure(CsvImportStatus.IoFailure, fileName, "CSV file could not be read.");
        }
    }

    private static CsvImportResult Failure(
        CsvImportStatus status,
        string fileName,
        string message,
        long? record = null)
    {
        var diagnostic = record is null
            ? $"{message} File: {fileName}."
            : $"{message} File: {fileName}. Logical record: {record}.";
        return new(status, Array.Empty<ContactRow>(), diagnostic);
    }

    private sealed class MalformedCsvRecordException : Exception;
    private sealed class InconsistentCsvRecordException : Exception;
}
