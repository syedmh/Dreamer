using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using HusayniaSMS.Core.Contacts;

namespace HusayniaSMS.WinForms.Infrastructure.Csv;

public sealed class CsvHelperContactCsvStore : IContactCsvStore
{
    public const int MaximumLogicalRecords = 100_000;
    public const int MaximumFileBytes = 10 * 1024 * 1024;
    public const int MaximumHeaderCharacters = 256;
    public const int MaximumFieldCharacters = 4_096;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly IContactRowValidator _rowValidator;
    private readonly IContactCsvFileSystem _fileSystem;

    public CsvHelperContactCsvStore(IContactRowValidator rowValidator)
        : this(rowValidator, new WindowsContactCsvFileSystem())
    {
    }

    internal CsvHelperContactCsvStore(
        IContactRowValidator rowValidator,
        IContactCsvFileSystem fileSystem)
    {
        _rowValidator = rowValidator ?? throw new ArgumentNullException(nameof(rowValidator));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public async Task<ContactCsvLoadResult> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string? fullPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            fullPath = _fileSystem.GetFullPath(path);
            var fileName = Path.GetFileName(fullPath);
            var bytes = await ReadBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length > MaximumFileBytes)
            {
                return LoadFailure(
                    CsvImportStatus.InconsistentRecord,
                    $"CSV exceeds the {MaximumFileBytes:N0} byte file size limit.",
                    fileName);
            }

            var metadata = _fileSystem.GetMetadata(fullPath);
            var rows = await ParseAsync(bytes, fileName, cancellationToken).ConfigureAwait(false);
            if (rows.Failure is not null)
            {
                return rows.Failure;
            }

            var version = CreateVersion(bytes, metadata.LastWriteTimeUtc);
            return new(
                CsvImportStatus.Success,
                fullPath,
                rows.Rows!,
                version,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LoadFailure(
                CsvImportStatus.Canceled,
                "CSV import was canceled.",
                SafeFileName(fullPath ?? path));
        }
        catch (FileNotFoundException)
        {
            return LoadFailure(
                CsvImportStatus.FileNotFound,
                "CSV file was not found.",
                SafeFileName(fullPath ?? path));
        }
        catch (DirectoryNotFoundException)
        {
            return LoadFailure(
                CsvImportStatus.FileNotFound,
                "CSV file was not found.",
                SafeFileName(fullPath ?? path));
        }
        catch (UnauthorizedAccessException)
        {
            return LoadFailure(
                CsvImportStatus.AccessDenied,
                "Access to the CSV file was denied.",
                SafeFileName(fullPath ?? path));
        }
        catch (DecoderFallbackException)
        {
            return LoadFailure(
                CsvImportStatus.MalformedCsv,
                "CSV is not valid UTF-8 text.",
                SafeFileName(fullPath ?? path));
        }
        catch (CsvFileTooLargeException)
        {
            return LoadFailure(
                CsvImportStatus.InconsistentRecord,
                $"CSV exceeds the {MaximumFileBytes:N0} byte file size limit.",
                SafeFileName(fullPath ?? path));
        }
        catch (MalformedCsvRecordException)
        {
            return LoadFailure(
                CsvImportStatus.MalformedCsv,
                "CSV contains malformed quoting or field data.",
                SafeFileName(fullPath ?? path));
        }
        catch (InconsistentCsvRecordException)
        {
            return LoadFailure(
                CsvImportStatus.InconsistentRecord,
                "CSV contains an inconsistent record.",
                SafeFileName(fullPath ?? path));
        }
        catch (CsvHelperException)
        {
            return LoadFailure(
                CsvImportStatus.MalformedCsv,
                "CSV could not be parsed reliably.",
                SafeFileName(fullPath ?? path));
        }
        catch (IOException)
        {
            return LoadFailure(
                CsvImportStatus.IoFailure,
                "CSV file could not be read.",
                SafeFileName(fullPath ?? path));
        }
        catch (ArgumentException)
        {
            return LoadFailure(
                CsvImportStatus.IoFailure,
                "CSV path is invalid.",
                SafeFileName(path));
        }
        catch (NotSupportedException)
        {
            return LoadFailure(
                CsvImportStatus.IoFailure,
                "CSV path is invalid.",
                SafeFileName(path));
        }
    }

    public async Task<ContactCsvSaveResult> SaveAsync(
        ContactCsvSaveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Rows);

        if (request.Rows.Count > MaximumLogicalRecords)
        {
            return SaveFailure(
                ContactCsvSaveStatus.InvalidDocument,
                null,
                "CSV exceeds the 100,000 record limit.");
        }

        var validated = _rowValidator.Validate(request.Rows
            .Select(row => new ContactInput(row.ImportOrdinal, row.Name, row.Number))
            .ToArray());
        if (validated.Any(row => !row.IsEligible))
        {
            return SaveFailure(
                ContactCsvSaveStatus.InvalidDocument,
                null,
                "CSV contains invalid or duplicate contacts.");
        }

        if (validated.Any(row =>
            row.Name.Length > MaximumFieldCharacters ||
            row.Number.Length > MaximumFieldCharacters))
        {
            return SaveFailure(
                ContactCsvSaveStatus.InvalidDocument,
                null,
                $"CSV field exceeds the {MaximumFieldCharacters:N0} character limit.");
        }

        byte[] serializedBytes;
        try
        {
            serializedBytes = await SerializeRowsAsync(validated, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CsvOutputTooLargeException)
        {
            return SaveFailure(
                ContactCsvSaveStatus.InvalidDocument,
                null,
                $"CSV exceeds the {MaximumFileBytes:N0} byte file size limit.");
        }
        catch (EncoderFallbackException)
        {
            return SaveFailure(
                ContactCsvSaveStatus.InvalidDocument,
                null,
                "CSV contains text that cannot be encoded as valid UTF-8.");
        }

        string? fullPath = null;
        string? temporaryFullPath = null;
        ContactCsvSaveResult result;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            fullPath = _fileSystem.GetFullPath(request.Path);
            var initial = await InspectTargetAsync(
                fullPath,
                request.ExpectedVersion,
                cancellationToken).ConfigureAwait(false);
            if (initial is not null)
            {
                return initial;
            }

            await using (var stream = _fileSystem.CreateSiblingTemporaryFile(
                fullPath,
                out temporaryFullPath))
            {
                await stream.WriteAsync(serializedBytes, cancellationToken).ConfigureAwait(false);
                await _fileSystem.FlushToDiskAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            _fileSystem.SetLastWriteTimeUtc(temporaryFullPath, DateTimeOffset.UtcNow);
            var temporaryBytes = await ReadBytesAsync(
                temporaryFullPath,
                cancellationToken).ConfigureAwait(false);
            if (!temporaryBytes.AsSpan().SequenceEqual(serializedBytes))
            {
                throw new IOException("The temporary CSV bytes changed before commit.");
            }

            var temporaryMetadata = _fileSystem.GetMetadata(temporaryFullPath);
            if (temporaryMetadata.Length != temporaryBytes.LongLength)
            {
                throw new IOException("The temporary CSV length changed before commit.");
            }

            var savedVersion = CreateVersion(
                temporaryBytes,
                temporaryMetadata.LastWriteTimeUtc);
            var final = await InspectTargetAsync(
                fullPath,
                request.ExpectedVersion,
                cancellationToken).ConfigureAwait(false);
            if (final is not null)
            {
                result = final;
            }
            else
            {
                try
                {
                    var commit = request.ExpectedVersion is null
                        ? _fileSystem.CommitNew(temporaryFullPath, fullPath)
                        : _fileSystem.ReplaceExisting(temporaryFullPath, fullPath);
                    temporaryFullPath = null;
                    return new(
                        ContactCsvSaveStatus.Saved,
                        fullPath,
                        savedVersion,
                        null,
                        commit.SafeDiagnostic);
                }
                catch (IOException) when (request.ExpectedVersion is null &&
                    _fileSystem.FileExists(fullPath))
                {
                    var current = await ReadVersionAsync(fullPath, cancellationToken)
                        .ConfigureAwait(false);
                    result = SaveFailure(
                        ContactCsvSaveStatus.TargetExists,
                        fullPath,
                        "The selected CSV file now exists.",
                        current);
                }
                catch (IOException) when (request.ExpectedVersion is not null)
                {
                    var conflict = await InspectTargetAsync(
                        fullPath,
                        request.ExpectedVersion,
                        cancellationToken).ConfigureAwait(false);
                    if (conflict is null)
                    {
                        throw;
                    }

                    result = conflict;
                }
            }
        }
        catch (OperationCanceledException exception) when (
            cancellationToken.IsCancellationRequested)
        {
            if (TryDeleteTemporaryFile(temporaryFullPath))
            {
                throw new OperationCanceledException(
                    "CSV save was canceled. A temporary CSV file could not be removed.",
                    exception,
                    cancellationToken);
            }

            throw;
        }
        catch (AtomicReplaceUnavailableException)
        {
            result = SaveFailure(
                ContactCsvSaveStatus.AtomicReplaceUnavailable,
                fullPath,
                "The destination does not support safe atomic CSV replacement.");
        }
        catch (SecureTemporaryFileUnavailableException)
        {
            result = SaveFailure(
                ContactCsvSaveStatus.AccessDenied,
                fullPath,
                "Secure temporary CSV permissions are unavailable for this destination.");
        }
        catch (UnauthorizedAccessException)
        {
            result = SaveFailure(
                ContactCsvSaveStatus.AccessDenied,
                fullPath,
                "Access to the CSV destination was denied.");
        }
        catch (ArgumentException)
        {
            result = SaveFailure(
                ContactCsvSaveStatus.IoFailure,
                null,
                "CSV destination path is invalid.");
        }
        catch (NotSupportedException)
        {
            result = SaveFailure(
                ContactCsvSaveStatus.IoFailure,
                null,
                "CSV destination path is invalid.");
        }
        catch (IOException)
        {
            result = SaveFailure(
                ContactCsvSaveStatus.IoFailure,
                fullPath,
                "CSV file could not be saved.");
        }

        if (TryDeleteTemporaryFile(temporaryFullPath))
        {
            result = result with
            {
                SafeDiagnostic = AppendDiagnostic(
                    result.SafeDiagnostic,
                    "A temporary CSV file could not be removed.")
            };
        }

        return result;
    }

    private async Task<(IReadOnlyList<ContactRow>? Rows, ContactCsvLoadResult? Failure)> ParseAsync(
        byte[] bytes,
        string fileName,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(
            stream,
            StrictUtf8,
            detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(reader, CreateConfiguration());

        if (!await csv.ReadAsync().ConfigureAwait(false))
        {
            return (null, LoadFailure(
                CsvImportStatus.MissingHeaders,
                "Required Name and Number headers were not found.",
                fileName));
        }

        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? Array.Empty<string>();
        if (headers.Any(header => header.Length > MaximumHeaderCharacters))
        {
            return (null, LoadFailure(
                CsvImportStatus.InconsistentRecord,
                $"CSV header exceeds the {MaximumHeaderCharacters:N0} character limit.",
                fileName));
        }

        var normalized = headers
            .Select((header, index) => new
            {
                Name = header.Trim().TrimStart('\uFEFF').Trim(),
                Index = index
            })
            .ToArray();
        var nameIndexes = normalized
            .Where(item => string.Equals(
                item.Name,
                "Name",
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Index)
            .ToArray();
        var numberIndexes = normalized
            .Where(item => string.Equals(
                item.Name,
                "Number",
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Index)
            .ToArray();
        if (nameIndexes.Length != 1 || numberIndexes.Length != 1)
        {
            return (null, LoadFailure(
                CsvImportStatus.MissingHeaders,
                "CSV must contain exactly one Name header and one Number header.",
                fileName));
        }

        var inputs = new List<ContactInput>();
        while (await csv.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inputs.Count == MaximumLogicalRecords)
            {
                return (null, LoadFailure(
                    CsvImportStatus.InconsistentRecord,
                    "CSV exceeds the 100,000 record limit.",
                    fileName,
                    csv.Parser.Row));
            }

            var record = csv.Parser.Record ?? Array.Empty<string>();
            if (record.Length != headers.Length ||
                nameIndexes[0] >= record.Length ||
                numberIndexes[0] >= record.Length)
            {
                return (null, LoadFailure(
                    CsvImportStatus.InconsistentRecord,
                    "CSV record has an inconsistent number of fields.",
                    fileName,
                    csv.Parser.Row));
            }

            if (record.Any(field => field.Length > MaximumFieldCharacters))
            {
                return (null, LoadFailure(
                    CsvImportStatus.InconsistentRecord,
                    $"CSV field exceeds the {MaximumFieldCharacters:N0} character limit.",
                    fileName,
                    csv.Parser.Row));
            }

            inputs.Add(new(
                inputs.Count + 1,
                csv.GetField(nameIndexes[0]),
                csv.GetField(numberIndexes[0])));
        }

        return (_rowValidator.Validate(inputs), null);
    }

    private async Task<ContactCsvSaveResult?> InspectTargetAsync(
        string fullPath,
        ContactCsvVersion? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (!_fileSystem.FileExists(fullPath))
        {
            return expectedVersion is null
                ? null
                : SaveFailure(
                    ContactCsvSaveStatus.ConflictDeleted,
                    fullPath,
                    "The CSV file was deleted outside the application.");
        }

        var current = await ReadVersionAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (expectedVersion is null)
        {
            return SaveFailure(
                ContactCsvSaveStatus.TargetExists,
                fullPath,
                "The selected CSV file already exists.",
                current);
        }

        return current == expectedVersion
            ? null
            : SaveFailure(
                ContactCsvSaveStatus.ConflictModified,
                fullPath,
                "The CSV file changed outside the application.",
                current);
    }

    private async Task<ContactCsvVersion> ReadVersionAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var metadata = _fileSystem.GetMetadata(fullPath);
        return CreateVersion(bytes, metadata.LastWriteTimeUtc);
    }

    private async Task<byte[]> ReadBytesAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        await using var stream = _fileSystem.OpenRead(fullPath);
        if (stream.CanSeek && stream.Length > MaximumFileBytes)
        {
            throw new CsvFileTooLargeException();
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async Task WriteRowsAsync(
        Stream stream,
        IReadOnlyList<ContactRow> rows,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(
            stream,
            StrictUtf8,
            bufferSize: 4096,
            leaveOpen: true);
        await using var csv = new CsvWriter(writer, CreateConfiguration(), leaveOpen: true);
        csv.WriteField("Name");
        csv.WriteField("Number");
        await csv.NextRecordAsync().ConfigureAwait(false);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            csv.WriteField(row.Name);
            csv.WriteField(row.Number);
            await csv.NextRecordAsync().ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> SerializeRowsAsync(
        IReadOnlyList<ContactRow> rows,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await using (var bounded = new MaximumLengthWriteStream(
            buffer,
            MaximumFileBytes))
        {
            await WriteRowsAsync(bounded, rows, cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private static CsvConfiguration CreateConfiguration() => new(CultureInfo.InvariantCulture)
    {
        Delimiter = ",",
        HasHeaderRecord = true,
        IgnoreBlankLines = false,
        TrimOptions = TrimOptions.Trim,
        ExceptionMessagesContainRawData = false,
        BadDataFound = _ => throw new MalformedCsvRecordException(),
        MissingFieldFound = _ => throw new InconsistentCsvRecordException(),
        NewLine = "\r\n"
    };

    private static ContactCsvVersion CreateVersion(
        byte[] bytes,
        DateTimeOffset lastWriteTimeUtc) =>
        new(bytes.LongLength, lastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(bytes)));

    private static ContactCsvLoadResult LoadFailure(
        CsvImportStatus status,
        string message,
        string fileName,
        long? record = null)
    {
        var diagnostic = record is null
            ? $"{message} File: {fileName}."
            : $"{message} File: {fileName}. Logical record: {record}.";
        return new(status, null, Array.Empty<ContactRow>(), null, diagnostic);
    }

    private static ContactCsvSaveResult SaveFailure(
        ContactCsvSaveStatus status,
        string? fullPath,
        string message,
        ContactCsvVersion? currentVersion = null) =>
        new(status, fullPath, null, currentVersion, message);

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            return "selected CSV";
        }
        catch (NotSupportedException)
        {
            return "selected CSV";
        }
    }

    private bool TryDeleteTemporaryFile(string? temporaryFullPath)
    {
        if (string.IsNullOrWhiteSpace(temporaryFullPath))
        {
            return false;
        }

        try
        {
            _fileSystem.DeleteFile(temporaryFullPath);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string AppendDiagnostic(string? primary, string cleanup) =>
        string.IsNullOrWhiteSpace(primary) ? cleanup : $"{primary} {cleanup}";

    private sealed class MalformedCsvRecordException : Exception;
    private sealed class InconsistentCsvRecordException : Exception;
    private sealed class CsvFileTooLargeException : IOException;
    private sealed class CsvOutputTooLargeException : IOException;

    private sealed class MaximumLengthWriteStream(Stream inner, long maximumLength)
        : Stream
    {
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            inner.Write(buffer, offset, count);
            _length += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            inner.Write(buffer);
            _length += buffer.Length;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureCapacity(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _length += buffer.Length;
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            EnsureCapacity(count);
            var write = inner.WriteAsync(buffer, offset, count, cancellationToken);
            if (write.IsCompletedSuccessfully)
            {
                _length += count;
                return Task.CompletedTask;
            }

            return CompleteWriteAsync(write, count);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The caller owns the underlying memory stream.
            base.Dispose(disposing);
        }

        private void EnsureCapacity(int count)
        {
            if (count < 0 || _length > maximumLength - count)
            {
                throw new CsvOutputTooLargeException();
            }
        }

        private async Task CompleteWriteAsync(Task write, int count)
        {
            await write.ConfigureAwait(false);
            _length += count;
        }
    }
}
