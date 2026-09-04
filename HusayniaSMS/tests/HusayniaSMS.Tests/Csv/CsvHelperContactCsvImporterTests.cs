using System.Text;
using HusayniaSMS.Core.Contacts;
using HusayniaSMS.Tests.TestDoubles;
using HusayniaSMS.WinForms.Infrastructure.Csv;

namespace HusayniaSMS.Tests.Csv;

[TestClass]
public sealed class CsvHelperContactCsvImporterTests
{
    [TestMethod]
    public async Task ParsesBomTrimmedHeadersQuotedCommasQuotesAndLineBreaks()
    {
        using var temp = new TempDirectory();
        var path = temp.File("quoted.csv");
        var content =
            "\uFEFF name , NUMBER ,Ignored\r\n" +
            "\"Doe, Jane\",+15550100100,x\r\n" +
            "\"Ali \"\"The Tester\"\"\",+15550100101,y\r\n" +
            "\"Line one\r\nLine two\",+15550100102,z\r\n";
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(true));

        var result = await Importer().ImportAsync(path, default);

        Assert.AreEqual(CsvImportStatus.Success, result.Status);
        Assert.AreEqual(3, result.Rows.Count);
        Assert.AreEqual("Doe, Jane", result.Rows[0].Name);
        Assert.AreEqual("Ali \"The Tester\"", result.Rows[1].Name);
        Assert.AreEqual("Line one\r\nLine two", result.Rows[2].Name);
    }

    [TestMethod]
    public async Task ExplicitEmptyFieldBecomesRowValidationError()
    {
        using var temp = new TempDirectory();
        var path = temp.File("empty.csv");
        await File.WriteAllTextAsync(path, "Name,Number\r\nAlice,\r\n");
        var result = await Importer().ImportAsync(path, default);
        Assert.AreEqual(CsvImportStatus.Success, result.Status);
        CollectionAssert.Contains(
            result.Rows[0].Errors.ToArray(),
            ContactErrorCode.NumberRequired);
    }

    [TestMethod]
    public async Task MissingOrDuplicateRequiredHeadersFailWithoutRows()
    {
        using var temp = new TempDirectory();
        foreach (var content in new[]
        {
            "Name,Other\r\nAlice,x\r\n",
            "Name,Number,number\r\nAlice,+15550100100,+15550100101\r\n"
        })
        {
            var path = temp.File(Guid.NewGuid().ToString("N") + ".csv");
            await File.WriteAllTextAsync(path, content);
            var result = await Importer().ImportAsync(path, default);
            Assert.AreEqual(CsvImportStatus.MissingHeaders, result.Status);
            Assert.AreEqual(0, result.Rows.Count);
        }
    }

    [TestMethod]
    public async Task HeaderOnlyFileSuccessfullyReturnsEmptyRows()
    {
        using var temp = new TempDirectory();
        var path = temp.File("headers.csv");
        await File.WriteAllTextAsync(path, "Name,Number\r\n");
        var result = await Importer().ImportAsync(path, default);
        Assert.AreEqual(CsvImportStatus.Success, result.Status);
        Assert.AreEqual(0, result.Rows.Count);
    }

    [TestMethod]
    public async Task MissingFieldPositionIsInconsistentRecord()
    {
        using var temp = new TempDirectory();
        var path = temp.File("short.csv");
        await File.WriteAllTextAsync(path, "Name,Number\r\nAlice\r\n");
        var result = await Importer().ImportAsync(path, default);
        Assert.AreEqual(CsvImportStatus.InconsistentRecord, result.Status);
        Assert.AreEqual(0, result.Rows.Count);
        StringAssert.Contains(result.SafeDiagnostic!, "Logical record");
        Assert.IsFalse(result.SafeDiagnostic.Contains("Alice"));
    }

    [TestMethod]
    public async Task ExtraFieldIsInconsistentRecord()
    {
        using var temp = new TempDirectory();
        var path = temp.File("long.csv");
        await File.WriteAllTextAsync(path,
            "Name,Number\r\nAlice,+15550100100,unexpected\r\n");
        var result = await Importer().ImportAsync(path, default);
        Assert.AreEqual(CsvImportStatus.InconsistentRecord, result.Status);
        Assert.AreEqual(0, result.Rows.Count);
        Assert.IsFalse(result.SafeDiagnostic!.Contains("unexpected"));
    }

    [TestMethod]
    public async Task MalformedQuotingFailsWithoutRawData()
    {
        using var temp = new TempDirectory();
        var path = temp.File("bad.csv");
        await File.WriteAllTextAsync(path, "Name,Number\r\n\"secret,+15550100100\r\n");
        var result = await Importer().ImportAsync(path, default);
        Assert.AreNotEqual(CsvImportStatus.Success, result.Status);
        Assert.AreEqual(0, result.Rows.Count);
        Assert.IsFalse(result.SafeDiagnostic!.Contains("secret"));
    }

    [TestMethod]
    public async Task InvalidUtf8FailsTransactionallyWithActionableDiagnostic()
    {
        using var temp = new TempDirectory();
        var path = temp.File("invalid-utf8.csv");
        var prefix = Encoding.UTF8.GetBytes("Name,Number\r\nAlice,");
        await File.WriteAllBytesAsync(path, [.. prefix, 0xC3, 0x28]);

        var result = await Importer().ImportAsync(path, default);

        Assert.AreEqual(CsvImportStatus.MalformedCsv, result.Status);
        Assert.AreEqual(0, result.Rows.Count);
        StringAssert.Contains(result.SafeDiagnostic!, "valid UTF-8");
        StringAssert.Contains(result.SafeDiagnostic!, "invalid-utf8.csv");
    }

    [TestMethod]
    public async Task MissingFileDirectoryAndCancellationAreTyped()
    {
        using var temp = new TempDirectory();
        Assert.AreEqual(CsvImportStatus.FileNotFound,
            (await Importer().ImportAsync(temp.File("missing.csv"), default)).Status);
        Assert.AreEqual(CsvImportStatus.AccessDenied,
            (await Importer().ImportAsync(temp.Path, default)).Status);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.AreEqual(CsvImportStatus.Canceled,
            (await Importer().ImportAsync(temp.File("anything.csv"), cancellation.Token)).Status);
    }

    [TestMethod]
    public async Task RecordLimitRejects100001AndReturnsNoPartialRows()
    {
        using var temp = new TempDirectory();
        var path = temp.File("large.csv");
        var builder = new StringBuilder("Name,Number\r\n");
        for (var index = 0; index <= CsvHelperContactCsvImporter.MaximumLogicalRecords; index++)
        {
            builder.Append("Person").Append(index).Append(",+1")
                .Append((5550100000L + index).ToString("D10"))
                .Append("\r\n");
        }

        await File.WriteAllTextAsync(path, builder.ToString());
        var result = await Importer().ImportAsync(path, default);
        Assert.AreEqual(CsvImportStatus.InconsistentRecord, result.Status);
        Assert.AreEqual(0, result.Rows.Count);
        StringAssert.Contains(result.SafeDiagnostic!, "100,000");
    }

    [TestMethod]
    public async Task Exactly100000RecordsSucceeds()
    {
        using var temp = new TempDirectory();
        var path = temp.File("boundary.csv");
        var builder = new StringBuilder("Name,Number\r\n");
        for (var index = 0; index < CsvHelperContactCsvImporter.MaximumLogicalRecords; index++)
        {
            builder.Append("Person").Append(index).Append(",+1")
                .Append((5550100000L + index).ToString("D10"))
                .Append("\r\n");
        }

        await File.WriteAllTextAsync(path, builder.ToString());
        var result = await Importer().ImportAsync(path, default);
        Assert.AreEqual(CsvImportStatus.Success, result.Status);
        Assert.AreEqual(CsvHelperContactCsvImporter.MaximumLogicalRecords, result.Rows.Count);
    }

    [TestMethod]
    public async Task FileLargerThanMaximumBytesIsRejectedTransactionally()
    {
        using var temp = new TempDirectory();
        var path = temp.File("too-large.csv");
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(CsvHelperContactCsvImporter.MaximumFileBytes + 1L);
        }

        var result = await Importer().ImportAsync(path, default);

        Assert.AreEqual(CsvImportStatus.InconsistentRecord, result.Status);
        Assert.AreEqual(0, result.Rows.Count);
        StringAssert.Contains(result.SafeDiagnostic!, "file size");
    }

    [TestMethod]
    public async Task HeaderLengthBoundaryIsAcceptedAndExceededHeaderIsRejected()
    {
        using var temp = new TempDirectory();
        var accepted = temp.File("header-boundary.csv");
        var extraHeader = new string('H',
            CsvHelperContactCsvImporter.MaximumHeaderCharacters);
        await File.WriteAllTextAsync(accepted,
            $"Name,Number,{extraHeader}\r\nAlice,+15550100100,x\r\n");

        var acceptedResult = await Importer().ImportAsync(accepted, default);
        Assert.AreEqual(CsvImportStatus.Success, acceptedResult.Status);

        var rejected = temp.File("header-too-long.csv");
        await File.WriteAllTextAsync(rejected,
            $"Name,Number,{extraHeader}X\r\nAlice,+15550100100,x\r\n");
        var rejectedResult = await Importer().ImportAsync(rejected, default);
        Assert.AreEqual(CsvImportStatus.InconsistentRecord, rejectedResult.Status);
        Assert.AreEqual(0, rejectedResult.Rows.Count);
        StringAssert.Contains(rejectedResult.SafeDiagnostic!, "header");
    }

    [TestMethod]
    public async Task FieldLengthBoundaryIsAcceptedAndExceededFieldIsRejected()
    {
        using var temp = new TempDirectory();
        var accepted = temp.File("field-boundary.csv");
        var name = new string('N', CsvHelperContactCsvImporter.MaximumFieldCharacters);
        await File.WriteAllTextAsync(accepted,
            $"Name,Number\r\n{name},+15550100100\r\n");

        var acceptedResult = await Importer().ImportAsync(accepted, default);
        Assert.AreEqual(CsvImportStatus.Success, acceptedResult.Status);
        Assert.AreEqual(name, acceptedResult.Rows[0].Name);

        var rejected = temp.File("field-too-long.csv");
        await File.WriteAllTextAsync(rejected,
            $"Name,Number\r\n{name}X,+15550100100\r\n");
        var rejectedResult = await Importer().ImportAsync(rejected, default);
        Assert.AreEqual(CsvImportStatus.InconsistentRecord, rejectedResult.Status);
        Assert.AreEqual(0, rejectedResult.Rows.Count);
        StringAssert.Contains(rejectedResult.SafeDiagnostic!, "field");
        Assert.IsFalse(rejectedResult.SafeDiagnostic.Contains(name, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DuplicateNumbersUseCanonicalRowValidator()
    {
        using var temp = new TempDirectory();
        var path = temp.File("duplicates.csv");
        await File.WriteAllTextAsync(path,
            "Name,Number\r\nFirst,+15550100100\r\nSecond,+15550100100\r\n");
        var result = await Importer().ImportAsync(path, default);
        Assert.IsTrue(result.Rows[0].IsEligible);
        CollectionAssert.Contains(result.Rows[1].Errors.ToArray(),
            ContactErrorCode.DuplicateNumber);
    }

    private static CsvHelperContactCsvImporter Importer()
    {
        var phone = new E164PhoneNumberValidator();
        return new(new ContactRowValidator(phone));
    }
}
