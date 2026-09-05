using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using HusayniaSMS.Core.Contacts;
using HusayniaSMS.Tests.TestDoubles;
using HusayniaSMS.WinForms.Infrastructure.Csv;

namespace HusayniaSMS.Tests.Csv;

[TestClass]
public sealed class CsvHelperContactCsvStoreTests
{
    [TestMethod]
    public async Task LoadReturnsCanonicalPathRowsAndExactVersion()
    {
        using var temp = new TempDirectory();
        var path = temp.File("contacts.csv");
        var bytes = Encoding.UTF8.GetBytes(
            "\uFEFF name , NUMBER ,Ignored\r\n" +
            "\"Doe, Jane\",+15550100100,x\r\n" +
            "\"Ali \"\"The Tester\"\"\",+15550100101,y\r\n" +
            "\"Line one\r\nLine two\",+15550100102,z\r\n");
        await File.WriteAllBytesAsync(path, bytes);

        var result = await Store().LoadAsync(path, default);

        Assert.AreEqual(CsvImportStatus.Success, result.Status);
        Assert.AreEqual(Path.GetFullPath(path), result.FullPath);
        Assert.AreEqual(3, result.Rows.Count);
        Assert.AreEqual("Doe, Jane", result.Rows[0].Name);
        Assert.AreEqual("Ali \"The Tester\"", result.Rows[1].Name);
        Assert.AreEqual("Line one\r\nLine two", result.Rows[2].Name);
        Assert.IsNotNull(result.Version);
        Assert.AreEqual(bytes.LongLength, result.Version.Length);
        Assert.AreEqual(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
            result.Version.Sha256Hex);
        Assert.AreEqual(64, result.Version.Sha256Hex.Length);
        Assert.AreEqual(
            new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
            result.Version.LastWriteTimeUtc);
    }

    [TestMethod]
    public async Task LoadPreservesValidationAndHeaderOnlyBehavior()
    {
        using var temp = new TempDirectory();
        var empty = temp.File("empty.csv");
        await File.WriteAllTextAsync(empty, "Name,Number\r\n");
        var emptyResult = await Store().LoadAsync(empty, default);
        Assert.AreEqual(CsvImportStatus.Success, emptyResult.Status);
        Assert.AreEqual(0, emptyResult.Rows.Count);

        var invalid = temp.File("invalid.csv");
        await File.WriteAllTextAsync(
            invalid,
            "Name,Number\r\n,+15550100100\r\nSecond,+15550100100\r\n");
        var invalidResult = await Store().LoadAsync(invalid, default);
        Assert.AreEqual(CsvImportStatus.Success, invalidResult.Status);
        CollectionAssert.Contains(
            invalidResult.Rows[0].Errors.ToArray(),
            ContactErrorCode.NameRequired);
        Assert.IsTrue(invalidResult.Rows[1].IsEligible);
    }

    [TestMethod]
    public async Task LoadKeepsFormulaPrefixNameVisibleButIneligible()
    {
        using var temp = new TempDirectory();
        var path = temp.File("formula.csv");
        await File.WriteAllTextAsync(
            path,
            "Name,Number\r\n\"\t=HYPERLINK(\"\"https://example.invalid\"\")\",+15550100100\r\n");

        var result = await Store().LoadAsync(path, default);

        Assert.AreEqual(CsvImportStatus.Success, result.Status);
        Assert.AreEqual("=HYPERLINK(\"https://example.invalid\")", result.Rows.Single().Name);
        CollectionAssert.Contains(
            result.Rows.Single().Errors.ToArray(),
            ContactErrorCode.FormulaPrefixNotAllowed);
    }
    [TestMethod]
    public async Task LoadFailuresReturnNoRowsPathOrVersionAndNoContactData()
    {
        using var temp = new TempDirectory();
        var cases = new[]
        {
            ("missing-header.csv", "Other,Number\r\nsecret,+15550100100\r\n",
                CsvImportStatus.MissingHeaders),
            ("short.csv", "Name,Number\r\nsecret\r\n",
                CsvImportStatus.InconsistentRecord),
            ("long.csv", "Name,Number\r\nsecret,+15550100100,extra\r\n",
                CsvImportStatus.InconsistentRecord),
            ("bad.csv", "Name,Number\r\n\"secret,+15550100100\r\n",
                CsvImportStatus.MalformedCsv)
        };

        foreach (var item in cases)
        {
            var path = temp.File(item.Item1);
            await File.WriteAllTextAsync(path, item.Item2);
            var result = await Store().LoadAsync(path, default);
            Assert.AreEqual(item.Item3, result.Status, item.Item1);
            Assert.AreEqual(0, result.Rows.Count);
            Assert.IsNull(result.FullPath);
            Assert.IsNull(result.Version);
            Assert.IsFalse(result.SafeDiagnostic!.Contains("secret", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task LoadRejectsInvalidUtf8AndMissingPathsAndSupportsCancellation()
    {
        using var temp = new TempDirectory();
        var invalid = temp.File("invalid-utf8.csv");
        var prefix = Encoding.UTF8.GetBytes("Name,Number\r\nAlice,");
        await File.WriteAllBytesAsync(invalid, [.. prefix, 0xC3, 0x28]);
        Assert.AreEqual(
            CsvImportStatus.MalformedCsv,
            (await Store().LoadAsync(invalid, default)).Status);
        Assert.AreEqual(
            CsvImportStatus.FileNotFound,
            (await Store().LoadAsync(temp.File("missing.csv"), default)).Status);
        Assert.AreEqual(
            CsvImportStatus.AccessDenied,
            (await Store().LoadAsync(temp.Path, default)).Status);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.AreEqual(
            CsvImportStatus.Canceled,
            (await Store().LoadAsync(invalid, cancellation.Token)).Status);
    }

    [TestMethod]
    public async Task LoadEnforcesFileHeaderFieldAndRecordLimits()
    {
        using var temp = new TempDirectory();
        var tooLarge = temp.File("too-large.csv");
        await using (var stream = new FileStream(tooLarge, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(CsvHelperContactCsvStore.MaximumFileBytes + 1L);
        }

        Assert.AreEqual(
            CsvImportStatus.InconsistentRecord,
            (await Store().LoadAsync(tooLarge, default)).Status);

        var longHeader = temp.File("long-header.csv");
        await File.WriteAllTextAsync(
            longHeader,
            $"Name,Number,{new string('H', CsvHelperContactCsvStore.MaximumHeaderCharacters + 1)}\r\n");
        Assert.AreEqual(
            CsvImportStatus.InconsistentRecord,
            (await Store().LoadAsync(longHeader, default)).Status);

        var longField = temp.File("long-field.csv");
        await File.WriteAllTextAsync(
            longField,
            $"Name,Number\r\n{new string('N', CsvHelperContactCsvStore.MaximumFieldCharacters + 1)},+15550100100\r\n");
        Assert.AreEqual(
            CsvImportStatus.InconsistentRecord,
            (await Store().LoadAsync(longField, default)).Status);

        var records = temp.File("records.csv");
        var builder = new StringBuilder("Name,Number\r\n");
        for (var index = 0; index <= CsvHelperContactCsvStore.MaximumLogicalRecords; index++)
        {
            builder.Append("Person").Append(index).Append(",+1")
                .Append((5550100000L + index).ToString("D10"))
                .Append("\r\n");
        }

        await File.WriteAllTextAsync(records, builder.ToString());
        Assert.AreEqual(
            CsvImportStatus.InconsistentRecord,
            (await Store().LoadAsync(records, default)).Status);
    }

    [TestMethod]
    public async Task LoadAcceptsExactHeaderFieldAndRecordBoundaries()
    {
        using var temp = new TempDirectory();
        var path = temp.File("boundaries.csv");
        var extraHeader = new string(
            'H',
            CsvHelperContactCsvStore.MaximumHeaderCharacters);
        var name = new string(
            'N',
            CsvHelperContactCsvStore.MaximumFieldCharacters);
        var builder = new StringBuilder($"Name,Number,{extraHeader}\r\n");
        builder.Append(name).Append(",+15550100100,x\r\n");
        for (var index = 1; index < CsvHelperContactCsvStore.MaximumLogicalRecords; index++)
        {
            builder.Append("P").Append(index).Append(",+1")
                .Append((5550100000L + index).ToString("D10"))
                .Append(",x\r\n");
        }

        await File.WriteAllTextAsync(path, builder.ToString());

        var result = await Store().LoadAsync(path, default);

        Assert.AreEqual(CsvImportStatus.Success, result.Status);
        Assert.AreEqual(CsvHelperContactCsvStore.MaximumLogicalRecords, result.Rows.Count);
        Assert.AreEqual(name, result.Rows[0].Name);
    }

    [TestMethod]
    public async Task SaveNewWritesExactHeaderNoBomAndRoundTripsQuotedUnicode()
    {
        using var temp = new TempDirectory();
        var path = temp.File("new.csv");
        var rows = Rows(
            ("حسین, \"Ali\"\r\nLine", "+15550100100"),
            (new string('N', CsvHelperContactCsvStore.MaximumFieldCharacters), "+15550100101"));

        var result = await Store().SaveAsync(new(path, rows, null), default);

        Assert.AreEqual(ContactCsvSaveStatus.Saved, result.Status);
        Assert.AreEqual(Path.GetFullPath(path), result.FullPath);
        Assert.IsNotNull(result.SavedVersion);
        var bytes = await File.ReadAllBytesAsync(path);
        CollectionAssert.DoesNotContain(bytes.Take(3).ToArray(), (byte)0xEF);
        var text = Encoding.UTF8.GetString(bytes);
        StringAssert.StartsWith(text, "Name,Number\r\n");
        Assert.IsTrue(text.Contains("\"حسین, \"\"Ali\"\"\r\nLine\"", StringComparison.Ordinal));

        var loaded = await Store().LoadAsync(path, default);
        Assert.AreEqual(CsvImportStatus.Success, loaded.Status);
        Assert.AreEqual(rows[0].Name, loaded.Rows[0].Name);
        Assert.AreEqual(rows[1].Name, loaded.Rows[1].Name);
    }

    [TestMethod]
    public async Task SaveEmptyWritesHeaderOnly()
    {
        using var temp = new TempDirectory();
        var path = temp.File("empty.csv");

        var result = await Store().SaveAsync(
            new(path, Array.Empty<ContactRow>(), null),
            default);

        Assert.AreEqual(ContactCsvSaveStatus.Saved, result.Status);
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("Name,Number\r\n"),
            await File.ReadAllBytesAsync(path));
    }

    [TestMethod]
    public async Task SaveAcceptsExactLogicalRecordLimit()
    {
        using var temp = new TempDirectory();
        var path = temp.File("record-limit.csv");
        var rows = Enumerable.Range(0, CsvHelperContactCsvStore.MaximumLogicalRecords)
            .Select(index => new ContactRow(
                index + 1,
                $"P{index}",
                $"+1{5550100000L + index:D10}",
                Array.Empty<ContactErrorCode>()))
            .ToArray();

        var result = await Store().SaveAsync(new(path, rows, null), default);

        Assert.AreEqual(ContactCsvSaveStatus.Saved, result.Status);
        Assert.AreEqual(
            CsvHelperContactCsvStore.MaximumLogicalRecords,
            (await Store().LoadAsync(path, default)).Rows.Count);
    }

    [TestMethod]
    public async Task SaveRejectsOverLogicalRecordLimitBeforeFilesystemAccess()
    {
        var fileSystem = new RecordingFileSystem();
        var rows = Enumerable.Range(0, CsvHelperContactCsvStore.MaximumLogicalRecords + 1)
            .Select(index => new ContactRow(
                index + 1,
                $"P{index}",
                $"+1{5550100000L + index:D10}",
                Array.Empty<ContactErrorCode>()))
            .ToArray();

        var result = await Store(fileSystem).SaveAsync(
            new("too-many.csv", rows, null),
            default);

        Assert.AreEqual(ContactCsvSaveStatus.InvalidDocument, result.Status);
        Assert.AreEqual(0, fileSystem.AccessCount);
        StringAssert.Contains(result.SafeDiagnostic!, "100,000 record limit");
    }

    [TestMethod]
    public async Task SaveOverLogicalRecordLimitPreservesExistingAndAbsentTargets()
    {
        using var temp = new TempDirectory();
        var existingPath = temp.File("existing-record-limit.csv");
        var original = Encoding.UTF8.GetBytes("Name,Number\r\nOld,+15550100100\r\n");
        await File.WriteAllBytesAsync(existingPath, original);
        var version = (await Store().LoadAsync(existingPath, default)).Version;
        var rows = Enumerable.Range(0, CsvHelperContactCsvStore.MaximumLogicalRecords + 1)
            .Select(index => new ContactRow(
                index + 1,
                $"P{index}",
                $"+1{5550100000L + index:D10}",
                Array.Empty<ContactErrorCode>()))
            .ToArray();

        var existing = await Store().SaveAsync(
            new(existingPath, rows, version),
            default);
        var newPath = temp.File("new-record-limit.csv");
        var absent = await Store().SaveAsync(
            new(newPath, rows, null),
            default);

        Assert.AreEqual(ContactCsvSaveStatus.InvalidDocument, existing.Status);
        Assert.AreEqual(ContactCsvSaveStatus.InvalidDocument, absent.Status);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(existingPath));
        Assert.IsFalse(File.Exists(newPath));
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task SaveEnforcesExactSerializedUtf8ByteBoundaryBeforeCommit()
    {
        using var temp = new TempDirectory();
        var exactPath = temp.File("exact.csv");
        var exactRows = RowsForSerializedSize(CsvHelperContactCsvStore.MaximumFileBytes);

        var exact = await Store().SaveAsync(
            new(exactPath, exactRows, null),
            default);

        Assert.AreEqual(ContactCsvSaveStatus.Saved, exact.Status);
        Assert.AreEqual(
            CsvHelperContactCsvStore.MaximumFileBytes,
            new FileInfo(exactPath).Length);
        Assert.AreEqual(
            CsvImportStatus.Success,
            (await Store().LoadAsync(exactPath, default)).Status);

        var existingPath = temp.File("existing.csv");
        var original = Encoding.UTF8.GetBytes("Name,Number\r\nOld,+15550100100\r\n");
        await File.WriteAllBytesAsync(existingPath, original);
        var existingVersion = (await Store().LoadAsync(existingPath, default)).Version;
        var overRows = RowsForSerializedSize(CsvHelperContactCsvStore.MaximumFileBytes + 1);

        var overExisting = await Store().SaveAsync(
            new(existingPath, overRows, existingVersion),
            default);
        var newPath = temp.File("over-new.csv");
        var overNew = await Store().SaveAsync(
            new(newPath, overRows, null),
            default);

        Assert.AreEqual(ContactCsvSaveStatus.InvalidDocument, overExisting.Status);
        Assert.AreEqual(ContactCsvSaveStatus.InvalidDocument, overNew.Status);
        StringAssert.Contains(overExisting.SafeDiagnostic!, "byte file size limit");
        StringAssert.Contains(overNew.SafeDiagnostic!, "byte file size limit");
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(existingPath));
        Assert.IsFalse(File.Exists(newPath));
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task SaveByteLimitCountsActualMultibyteUtf8Output()
    {
        using var temp = new TempDirectory();
        var path = temp.File("multibyte-over-limit.csv");
        var name = new string('界', CsvHelperContactCsvStore.MaximumFieldCharacters);
        var rows = Enumerable.Range(0, 853)
            .Select(index => new ContactRow(
                index + 1,
                name,
                $"+1{10_000_000_000_000L + index:D14}",
                Array.Empty<ContactErrorCode>()))
            .ToArray();
        Assert.IsTrue(
            rows.Sum(row => row.Name.Length + row.Number.Length + 3) <
            CsvHelperContactCsvStore.MaximumFileBytes);

        var result = await Store().SaveAsync(new(path, rows, null), default);

        Assert.AreEqual(ContactCsvSaveStatus.InvalidDocument, result.Status);
        StringAssert.Contains(result.SafeDiagnostic!, "byte file size limit");
        Assert.IsFalse(File.Exists(path));
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task SaveExistingMatchingVersionReplacesAndChangesFingerprint()
    {
        using var temp = new TempDirectory();
        var path = temp.File("existing.csv");
        await File.WriteAllTextAsync(path, "Name,Number\r\nOld,+15550100100\r\n");
        var store = Store();
        var loaded = await store.LoadAsync(path, default);

        var result = await store.SaveAsync(
            new(path, Rows(("New", "+15550100101")), loaded.Version),
            default);

        Assert.AreEqual(ContactCsvSaveStatus.Saved, result.Status);
        Assert.AreNotEqual(loaded.Version!.Sha256Hex, result.SavedVersion!.Sha256Hex);
        StringAssert.Contains(await File.ReadAllTextAsync(path), "New");

        var reloaded = await store.LoadAsync(path, default);
        Assert.AreEqual(result.SavedVersion, reloaded.Version);
    }

    [TestMethod]
    public async Task SaveDetectsModifiedDeletedAndExistingTargetsBeforeTempCreation()
    {
        using var temp = new TempDirectory();
        var path = temp.File("target.csv");
        await File.WriteAllTextAsync(path, "Name,Number\r\nOld,+15550100100\r\n");
        var store = Store();
        var loaded = await store.LoadAsync(path, default);
        await File.WriteAllTextAsync(path, "Name,Number\r\nChanged,+15550100102\r\n");

        var modified = await store.SaveAsync(
            new(path, Rows(("New", "+15550100101")), loaded.Version),
            default);
        Assert.AreEqual(ContactCsvSaveStatus.ConflictModified, modified.Status);
        Assert.IsNotNull(modified.CurrentVersion);

        File.Delete(path);
        var deleted = await store.SaveAsync(
            new(path, Rows(("New", "+15550100101")), loaded.Version),
            default);
        Assert.AreEqual(ContactCsvSaveStatus.ConflictDeleted, deleted.Status);
        Assert.IsNull(deleted.CurrentVersion);

        await File.WriteAllTextAsync(path, "Name,Number\r\nAppeared,+15550100103\r\n");
        var exists = await store.SaveAsync(
            new(path, Rows(("New", "+15550100101")), null),
            default);
        Assert.AreEqual(ContactCsvSaveStatus.TargetExists, exists.Status);
        Assert.IsNotNull(exists.CurrentVersion);
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task FinalRecheckDetectsModifiedDeletedAndAppearedTargets()
    {
        using var temp = new TempDirectory();
        foreach (var race in new[] { Race.Modified, Race.Deleted, Race.Appeared })
        {
            var path = temp.File($"{race}.csv");
            ContactCsvVersion? expected = null;
            if (race != Race.Appeared)
            {
                await File.WriteAllTextAsync(path, "Name,Number\r\nOld,+15550100100\r\n");
                expected = (await Store().LoadAsync(path, default)).Version;
            }

            var fileSystem = new RacingFileSystem(path, race);
            var result = await Store(fileSystem).SaveAsync(
                new(path, Rows(("New", "+15550100101")), expected),
                default);

            Assert.AreEqual(
                race == Race.Deleted
                    ? ContactCsvSaveStatus.ConflictDeleted
                    : race == Race.Modified
                        ? ContactCsvSaveStatus.ConflictModified
                        : ContactCsvSaveStatus.TargetExists,
                result.Status);
            Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
        }
    }

    [TestMethod]
    public async Task AppearanceDuringNewFileCommitReturnsTargetExistsAndPreservesExternalBytes()
    {
        using var temp = new TempDirectory();
        var path = temp.File("appeared-at-commit.csv");
        var external = Encoding.UTF8.GetBytes(
            "Name,Number\r\nExternal,+15550100999\r\n");

        var result = await Store(new CommitAppearanceFileSystem(external)).SaveAsync(
            new(path, Rows(("Local", "+15550100100")), null),
            default);

        Assert.AreEqual(ContactCsvSaveStatus.TargetExists, result.Status);
        Assert.IsNotNull(result.CurrentVersion);
        CollectionAssert.AreEqual(external, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task InvalidDocumentReturnsBeforeFilesystemAccess()
    {
        var fileSystem = new RecordingFileSystem();
        var invalid = new[]
        {
            new ContactRow(
                1,
                "Bad",
                "123",
                Array.AsReadOnly(new[] { ContactErrorCode.InvalidE164 }))
        };

        var result = await Store(fileSystem).SaveAsync(
            new("invalid\0path", invalid, null),
            default);

        Assert.AreEqual(ContactCsvSaveStatus.InvalidDocument, result.Status);
        Assert.AreEqual(0, fileSystem.AccessCount);
    }

    [TestMethod]
    public async Task FormulaPrefixNameIsRejectedBeforeFilesystemAccess()
    {
        var fileSystem = new RecordingFileSystem();

        var result = await Store(fileSystem).SaveAsync(
            new(
                "formula.csv",
                Rows(("\u0001@command", "+15550100100")),
                null),
            default);

        Assert.AreEqual(ContactCsvSaveStatus.InvalidDocument, result.Status);
        Assert.AreEqual(0, fileSystem.AccessCount);
        StringAssert.Contains(result.SafeDiagnostic!, "invalid");
    }

    [TestMethod]
    [DataRow(FailurePoint.Write, ContactCsvSaveStatus.IoFailure)]
    [DataRow(FailurePoint.Flush, ContactCsvSaveStatus.IoFailure)]
    [DataRow(FailurePoint.Replace, ContactCsvSaveStatus.IoFailure)]
    [DataRow(FailurePoint.AtomicUnavailable, ContactCsvSaveStatus.AtomicReplaceUnavailable)]
    [DataRow(FailurePoint.AccessDenied, ContactCsvSaveStatus.AccessDenied)]
    [DataRow(FailurePoint.CreateAccessDenied, ContactCsvSaveStatus.AccessDenied)]
    [DataRow(FailurePoint.SecureAclUnavailable, ContactCsvSaveStatus.AccessDenied)]
    [DataRow(FailurePoint.TempFingerprintAccessDenied, ContactCsvSaveStatus.AccessDenied)]
    [DataRow(FailurePoint.TempFingerprintIoFailure, ContactCsvSaveStatus.IoFailure)]
    [DataRow(FailurePoint.TimestampIoFailure, ContactCsvSaveStatus.IoFailure)]
    public async Task ExistingSaveFailurePreservesOriginalAndCleansTemp(
        FailurePoint point,
        ContactCsvSaveStatus expectedStatus)
    {
        using var temp = new TempDirectory();
        var path = temp.File("existing.csv");
        var original = Encoding.UTF8.GetBytes("Name,Number\r\nOld,+15550100100\r\n");
        await File.WriteAllBytesAsync(path, original);
        var version = (await Store().LoadAsync(path, default)).Version;
        var fileSystem = new FaultingFileSystem(point);

        var result = await Store(fileSystem).SaveAsync(
            new(path, Rows(("New", "+15550100101")), version),
            default);

        Assert.AreEqual(expectedStatus, result.Status);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task NewSaveMoveFailureLeavesNoDestinationAndCleansTemp()
    {
        using var temp = new TempDirectory();
        var path = temp.File("new.csv");
        var result = await Store(new FaultingFileSystem(FailurePoint.Move)).SaveAsync(
            new(path, Rows(("New", "+15550100101")), null),
            default);

        Assert.AreEqual(ContactCsvSaveStatus.IoFailure, result.Status);
        Assert.IsFalse(File.Exists(path));
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task CleanupFailureDoesNotReplacePrimaryResultOrOriginal()
    {
        using var temp = new TempDirectory();
        var path = temp.File("existing.csv");
        var original = Encoding.UTF8.GetBytes("Name,Number\r\nOld,+15550100100\r\n");
        await File.WriteAllBytesAsync(path, original);
        var version = (await Store().LoadAsync(path, default)).Version;

        var result = await Store(new CleanupFailureFileSystem()).SaveAsync(
            new(path, Rows(("New", "+15550100101")), version),
            default);

        Assert.AreEqual(ContactCsvSaveStatus.IoFailure, result.Status);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
        StringAssert.Contains(result.SafeDiagnostic!, "temporary CSV file");
    }

    [TestMethod]
    public async Task SuccessfulCommitCanSurfaceSafeResidualTempWarningWithoutFailingSave()
    {
        using var temp = new TempDirectory();
        var path = temp.File("new.csv");

        var result = await Store(new CommitWarningFileSystem()).SaveAsync(
            new(path, Rows(("New", "+15550100101")), null),
            default);

        Assert.AreEqual(ContactCsvSaveStatus.Saved, result.Status);
        StringAssert.Contains(result.SafeDiagnostic!, "temporary CSV file");
        StringAssert.Contains(await File.ReadAllTextAsync(path), "New");
    }

    [TestMethod]
    public void ExistingDestinationTemporaryFileCopiesProtectedAccessAclBeforeWriting()
    {
        using var temp = new TempDirectory();
        var destination = temp.File("existing.csv");
        File.WriteAllText(destination, "Name,Number\r\n");
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User!;
        var users = new SecurityIdentifier(
            WellKnownSidType.BuiltinUsersSid,
            domainSid: null);
        var destinationSecurity = new FileSecurity();
        destinationSecurity.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        destinationSecurity.AddAccessRule(new(
            owner,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        destinationSecurity.AddAccessRule(new(
            users,
            FileSystemRights.ReadData,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(
            new FileInfo(destination),
            destinationSecurity);
        var fileSystem = new WindowsContactCsvFileSystem();

        using var stream = fileSystem.CreateSiblingTemporaryFile(
            destination,
            out var temporaryPath);
        stream.Dispose();

        var expected = FileSystemAclExtensions.GetAccessControl(
            new FileInfo(destination),
            AccessControlSections.Access);
        var actual = FileSystemAclExtensions.GetAccessControl(
            new FileInfo(temporaryPath),
            AccessControlSections.Access);
        var expectedRules = AccessRules(expected);
        var actualRules = AccessRules(actual);
        CollectionAssert.AreEqual(expectedRules, actualRules);
        Assert.AreEqual(
            expected.AreAccessRulesProtected,
            actual.AreAccessRulesProtected);
        Assert.IsTrue(actual.AreAccessRulesProtected);
        File.Delete(temporaryPath);
    }

    [TestMethod]
    public void NewDestinationTemporaryFileUsesProtectedOwnerOnlyAclBeforeWriting()
    {
        using var temp = new TempDirectory();
        var destination = temp.File("new.csv");
        var fileSystem = new WindowsContactCsvFileSystem();
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User!;

        using var stream = fileSystem.CreateSiblingTemporaryFile(
            destination,
            out var temporaryPath);
        stream.Dispose();

        var actual = FileSystemAclExtensions.GetAccessControl(
            new FileInfo(temporaryPath),
            AccessControlSections.Owner | AccessControlSections.Access);
        var rules = actual.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        Assert.AreEqual(owner, actual.GetOwner(typeof(SecurityIdentifier)));
        Assert.IsTrue(actual.AreAccessRulesProtected);
        Assert.AreEqual(1, rules.Length);
        Assert.AreEqual(owner, rules[0].IdentityReference);
        Assert.AreEqual(AccessControlType.Allow, rules[0].AccessControlType);
        Assert.AreEqual(FileSystemRights.FullControl, rules[0].FileSystemRights);
        File.Delete(temporaryPath);
    }

    [TestMethod]
    public async Task ExistingSavePreservesDestinationAccessAcl()
    {
        using var temp = new TempDirectory();
        var path = temp.File("existing-acl.csv");
        await File.WriteAllTextAsync(path, "Name,Number\r\nOld,+15550100100\r\n");
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new(
            identity.User!,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null),
            FileSystemRights.ReadData,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
        var expected = AccessRules(FileSystemAclExtensions.GetAccessControl(
            new FileInfo(path),
            AccessControlSections.Access));
        var store = Store();
        var version = (await store.LoadAsync(path, default)).Version;

        var result = await store.SaveAsync(
            new(path, Rows(("New", "+15550100101")), version),
            default);

        Assert.AreEqual(ContactCsvSaveStatus.Saved, result.Status);
        var actualSecurity = FileSystemAclExtensions.GetAccessControl(
            new FileInfo(path),
            AccessControlSections.Access);
        CollectionAssert.AreEqual(expected, AccessRules(actualSecurity));
        Assert.IsTrue(actualSecurity.AreAccessRulesProtected);
    }

    [TestMethod]
    public async Task NewSaveCommitsProtectedOwnerOnlyAcl()
    {
        using var temp = new TempDirectory();
        var path = temp.File("new-owner-only.csv");
        using var identity = WindowsIdentity.GetCurrent();

        var result = await Store().SaveAsync(
            new(path, Rows(("New", "+15550100101")), null),
            default);

        Assert.AreEqual(ContactCsvSaveStatus.Saved, result.Status);
        var actual = FileSystemAclExtensions.GetAccessControl(
            new FileInfo(path),
            AccessControlSections.Owner | AccessControlSections.Access);
        var rules = actual.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        Assert.AreEqual(identity.User, actual.GetOwner(typeof(SecurityIdentifier)));
        Assert.IsTrue(actual.AreAccessRulesProtected);
        Assert.AreEqual(1, rules.Length);
        Assert.AreEqual(identity.User, rules[0].IdentityReference);
        Assert.AreEqual(FileSystemRights.FullControl, rules[0].FileSystemRights);
    }

    [TestMethod]
    public async Task SaveCancellationPreservesOriginalAndCleansTemp()
    {
        using var temp = new TempDirectory();
        var path = temp.File("existing.csv");
        var original = Encoding.UTF8.GetBytes("Name,Number\r\nOld,+15550100100\r\n");
        await File.WriteAllBytesAsync(path, original);
        var version = (await Store().LoadAsync(path, default)).Version;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            Store().SaveAsync(
                new(path, Rows(("New", "+15550100101")), version),
                cancellation.Token));

        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
    }

    [TestMethod]
    [DataRow(FailurePoint.PostCommitAccessDenied, ContactCsvSaveStatus.AccessDenied)]
    [DataRow(FailurePoint.PostCommitIoFailure, ContactCsvSaveStatus.IoFailure)]
    public async Task PostCommitFingerprintFailureLeavesOriginalUnchanged(
        FailurePoint point,
        ContactCsvSaveStatus expectedStatus)
    {
        using var temp = new TempDirectory();
        var path = temp.File("existing.csv");
        var original = Encoding.UTF8.GetBytes("Name,Number\r\nOld,+15550100100\r\n");
        await File.WriteAllBytesAsync(path, original);
        var version = (await Store().LoadAsync(path, default)).Version;

        var fileSystem = new FaultingFileSystem(point);
        var result = await Store(fileSystem).SaveAsync(
            new(path, Rows(("New", "+15550100101")), version),
            default);

        Assert.AreEqual(ContactCsvSaveStatus.Saved, result.Status);
        Assert.AreNotEqual(expectedStatus, result.Status);
        StringAssert.Contains(await File.ReadAllTextAsync(path), "New");
        Assert.AreEqual(0, fileSystem.PostCommitOpenReadAttempts);
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
    }

    [TestMethod]
    [DataRow(FailurePoint.PostCommitAccessDenied, ContactCsvSaveStatus.AccessDenied)]
    [DataRow(FailurePoint.PostCommitIoFailure, ContactCsvSaveStatus.IoFailure)]
    public async Task PostCommitFingerprintFailureLeavesNewTargetAbsent(
        FailurePoint point,
        ContactCsvSaveStatus expectedStatus)
    {
        using var temp = new TempDirectory();
        var path = temp.File("new.csv");

        var fileSystem = new FaultingFileSystem(point);
        var result = await Store(fileSystem).SaveAsync(
            new(path, Rows(("New", "+15550100101")), null),
            default);

        Assert.AreEqual(ContactCsvSaveStatus.Saved, result.Status);
        Assert.AreNotEqual(expectedStatus, result.Status);
        Assert.IsTrue(File.Exists(path));
        StringAssert.Contains(await File.ReadAllTextAsync(path), "New");
        Assert.AreEqual(0, fileSystem.PostCommitOpenReadAttempts);
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task CancellationDuringPostCommitFingerprintLeavesOriginalUnchanged()
    {
        using var temp = new TempDirectory();
        var path = temp.File("existing.csv");
        var original = Encoding.UTF8.GetBytes("Name,Number\r\nOld,+15550100100\r\n");
        await File.WriteAllBytesAsync(path, original);
        var version = (await Store().LoadAsync(path, default)).Version;
        using var cancellation = new CancellationTokenSource();

        var result = await Store(new CancelAfterReplaceFileSystem(cancellation)).SaveAsync(
            new(path, Rows(("New", "+15550100101")), version),
            cancellation.Token);

        Assert.AreEqual(ContactCsvSaveStatus.Saved, result.Status);
        Assert.IsTrue(cancellation.IsCancellationRequested);
        StringAssert.Contains(await File.ReadAllTextAsync(path), "New");
        Assert.AreEqual(result.SavedVersion, (await Store().LoadAsync(path, default)).Version);
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task CancellationDuringNewTargetCommitStillReturnsSavedVersion()
    {
        using var temp = new TempDirectory();
        var path = temp.File("new.csv");
        using var cancellation = new CancellationTokenSource();

        var result = await Store(new CancelAfterReplaceFileSystem(cancellation)).SaveAsync(
            new(path, Rows(("New", "+15550100101")), null),
            cancellation.Token);

        Assert.AreEqual(ContactCsvSaveStatus.Saved, result.Status);
        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.AreEqual(result.SavedVersion, (await Store().LoadAsync(path, default)).Version);
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task MissingDirectoryIsTypedIoFailure()
    {
        using var temp = new TempDirectory();
        var result = await Store().SaveAsync(
            new(Path.Combine(temp.Path, "missing", "new.csv"),
                Rows(("New", "+15550100101")),
                null),
            default);

        Assert.AreEqual(ContactCsvSaveStatus.IoFailure, result.Status);
    }

    private static CsvHelperContactCsvStore Store(IContactCsvFileSystem? fileSystem = null)
    {
        var validator = new ContactRowValidator(new E164PhoneNumberValidator());
        return fileSystem is null
            ? new(validator)
            : new(validator, fileSystem);
    }

    private static IReadOnlyList<ContactRow> Rows(
        params (string Name, string Number)[] values) =>
        values.Select((value, index) => new ContactRow(
                index + 1,
                value.Name,
                value.Number,
                Array.Empty<ContactErrorCode>()))
            .ToArray();

    private static IReadOnlyList<ContactRow> RowsForSerializedSize(int totalBytes)
    {
        const int headerBytes = 13;
        const int rowNonNameBytes = 19;
        var remaining = totalBytes - headerBytes;
        var rows = new List<ContactRow>();
        var maximumName = new string('N', CsvHelperContactCsvStore.MaximumFieldCharacters);
        while (remaining > 0)
        {
            var nameLength = Math.Min(
                CsvHelperContactCsvStore.MaximumFieldCharacters,
                remaining - rowNonNameBytes);
            if (nameLength <= 0)
            {
                throw new InvalidOperationException(
                    "The requested serialized length cannot be represented by simple rows.");
            }

            var index = rows.Count;
            rows.Add(new(
                index + 1,
                nameLength == maximumName.Length
                    ? maximumName
                    : new string('N', nameLength),
                $"+1{10_000_000_000_000L + index:D14}",
                Array.Empty<ContactErrorCode>()));
            remaining -= nameLength + rowNonNameBytes;
        }

        return rows;
    }

    private static string[] AccessRules(FileSecurity security) =>
        security.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule =>
                $"{rule.IdentityReference.Value}|{rule.AccessControlType}|{rule.FileSystemRights}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    public enum FailurePoint
    {
        Write,
        Flush,
        Replace,
        AtomicUnavailable,
        Move,
        AccessDenied,
        CreateAccessDenied,
        SecureAclUnavailable,
        TempFingerprintAccessDenied,
        TempFingerprintIoFailure,
        TimestampIoFailure,
        PostCommitAccessDenied,
        PostCommitIoFailure
    }

    private enum Race
    {
        Modified,
        Deleted,
        Appeared
    }

    private sealed class RecordingFileSystem : IContactCsvFileSystem
    {
        public int AccessCount { get; private set; }
        public string GetFullPath(string path) { AccessCount++; return path; }
        public bool FileExists(string fullPath) { AccessCount++; return false; }
        public ContactCsvFileMetadata GetMetadata(string fullPath) =>
            throw new NotSupportedException();
        public Stream OpenRead(string fullPath) => throw new NotSupportedException();
        public Stream CreateSiblingTemporaryFile(
            string destinationFullPath,
            out string temporaryFullPath) =>
            throw new NotSupportedException();
        public Task FlushToDiskAsync(Stream stream, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public void SetLastWriteTimeUtc(string fullPath, DateTimeOffset lastWriteTimeUtc) =>
            throw new NotSupportedException();
        public ContactCsvCommitResult CommitNew(
            string temporaryFullPath,
            string destinationFullPath) =>
            throw new NotSupportedException();
        public ContactCsvCommitResult ReplaceExisting(
            string temporaryFullPath,
            string destinationFullPath) =>
            throw new NotSupportedException();
        public void DeleteFile(string fullPath) => throw new NotSupportedException();
    }

    private sealed class FaultingFileSystem(FailurePoint point) : IContactCsvFileSystem
    {
        private readonly WindowsContactCsvFileSystem _inner = new();
        private bool _commitCompleted;
        public int PostCommitOpenReadAttempts { get; private set; }

        public string GetFullPath(string path) => _inner.GetFullPath(path);
        public bool FileExists(string fullPath) => _inner.FileExists(fullPath);
        public ContactCsvFileMetadata GetMetadata(string fullPath) => _inner.GetMetadata(fullPath);
        public Stream OpenRead(string fullPath)
        {
            if (fullPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                point == FailurePoint.TempFingerprintAccessDenied)
            {
                throw new UnauthorizedAccessException(
                    "Injected temporary fingerprint access failure.");
            }

            if (fullPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                point == FailurePoint.TempFingerprintIoFailure)
            {
                throw new IOException("Injected temporary fingerprint I/O failure.");
            }

            if (_commitCompleted && point == FailurePoint.PostCommitAccessDenied)
            {
                PostCommitOpenReadAttempts++;
                throw new UnauthorizedAccessException("Injected post-commit access failure.");
            }

            if (_commitCompleted && point == FailurePoint.PostCommitIoFailure)
            {
                PostCommitOpenReadAttempts++;
                throw new IOException("Injected post-commit I/O failure.");
            }

            return _inner.OpenRead(fullPath);
        }
        public Stream CreateSiblingTemporaryFile(
            string destinationFullPath,
            out string temporaryFullPath)
        {
            if (point == FailurePoint.CreateAccessDenied)
            {
                temporaryFullPath = string.Empty;
                throw new UnauthorizedAccessException("Injected ACL creation failure.");
            }

            if (point == FailurePoint.SecureAclUnavailable)
            {
                temporaryFullPath = string.Empty;
                throw new SecureTemporaryFileUnavailableException(
                    "Injected secure ACL failure.");
            }

            var stream = _inner.CreateSiblingTemporaryFile(
                destinationFullPath,
                out temporaryFullPath);
            return point == FailurePoint.Write
                ? new ThrowingStream(stream, throwOnWrite: true)
                : stream;
        }

        public Task FlushToDiskAsync(Stream stream, CancellationToken cancellationToken) =>
            point == FailurePoint.Flush
                ? Task.FromException(new IOException("Injected flush failure."))
                : _inner.FlushToDiskAsync(stream, cancellationToken);

        public void SetLastWriteTimeUtc(
            string fullPath,
            DateTimeOffset lastWriteTimeUtc)
        {
            if (point == FailurePoint.TimestampIoFailure)
            {
                throw new IOException("Injected timestamp failure.");
            }

            _inner.SetLastWriteTimeUtc(fullPath, lastWriteTimeUtc);
        }

        public ContactCsvCommitResult CommitNew(
            string temporaryFullPath,
            string destinationFullPath)
        {
            if (point == FailurePoint.Move)
            {
                throw new IOException("Injected move failure.");
            }

            var result = _inner.CommitNew(temporaryFullPath, destinationFullPath);
            _commitCompleted = true;
            return result;
        }

        public ContactCsvCommitResult ReplaceExisting(
            string temporaryFullPath,
            string destinationFullPath)
        {
            if (point == FailurePoint.AtomicUnavailable)
            {
                throw new AtomicReplaceUnavailableException("Injected unavailable replacement.");
            }

            if (point == FailurePoint.Replace)
            {
                throw new IOException("Injected replacement failure.");
            }

            if (point == FailurePoint.AccessDenied)
            {
                throw new UnauthorizedAccessException("Injected access failure.");
            }

            var result = _inner.ReplaceExisting(temporaryFullPath, destinationFullPath);
            _commitCompleted = true;
            return result;
        }

        public void DeleteFile(string fullPath) => _inner.DeleteFile(fullPath);
    }

    private sealed class CancelAfterReplaceFileSystem(CancellationTokenSource cancellation)
        : IContactCsvFileSystem
    {
        private readonly WindowsContactCsvFileSystem _inner = new();

        public string GetFullPath(string path) => _inner.GetFullPath(path);
        public bool FileExists(string fullPath) => _inner.FileExists(fullPath);
        public ContactCsvFileMetadata GetMetadata(string fullPath) => _inner.GetMetadata(fullPath);
        public Stream OpenRead(string fullPath) => _inner.OpenRead(fullPath);
        public Stream CreateSiblingTemporaryFile(
            string destinationFullPath,
            out string temporaryFullPath) =>
            _inner.CreateSiblingTemporaryFile(destinationFullPath, out temporaryFullPath);
        public Task FlushToDiskAsync(Stream stream, CancellationToken cancellationToken) =>
            _inner.FlushToDiskAsync(stream, cancellationToken);
        public void SetLastWriteTimeUtc(
            string fullPath,
            DateTimeOffset lastWriteTimeUtc) =>
            _inner.SetLastWriteTimeUtc(fullPath, lastWriteTimeUtc);
        public ContactCsvCommitResult CommitNew(
            string temporaryFullPath,
            string destinationFullPath)
        {
            var result = _inner.CommitNew(temporaryFullPath, destinationFullPath);
            cancellation.Cancel();
            return result;
        }
        public ContactCsvCommitResult ReplaceExisting(
            string temporaryFullPath,
            string destinationFullPath)
        {
            var result = _inner.ReplaceExisting(temporaryFullPath, destinationFullPath);
            cancellation.Cancel();
            return result;
        }
        public void DeleteFile(string fullPath) => _inner.DeleteFile(fullPath);
    }

    private sealed class RacingFileSystem(string targetPath, Race race) : IContactCsvFileSystem
    {
        private readonly WindowsContactCsvFileSystem _inner = new();
        private int _targetChecks;

        public string GetFullPath(string path) => _inner.GetFullPath(path);

        public bool FileExists(string fullPath)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(fullPath, Path.GetFullPath(targetPath)) &&
                Interlocked.Increment(ref _targetChecks) == 2)
            {
                switch (race)
                {
                    case Race.Modified:
                        File.WriteAllText(
                            fullPath,
                            "Name,Number\r\nExternal,+15550100999\r\n");
                        break;
                    case Race.Deleted:
                        File.Delete(fullPath);
                        break;
                    case Race.Appeared:
                        File.WriteAllText(
                            fullPath,
                            "Name,Number\r\nExternal,+15550100999\r\n");
                        break;
                }
            }

            return _inner.FileExists(fullPath);
        }

        public ContactCsvFileMetadata GetMetadata(string fullPath) => _inner.GetMetadata(fullPath);
        public Stream OpenRead(string fullPath) => _inner.OpenRead(fullPath);
        public Stream CreateSiblingTemporaryFile(
            string destinationFullPath,
            out string temporaryFullPath) =>
            _inner.CreateSiblingTemporaryFile(destinationFullPath, out temporaryFullPath);
        public Task FlushToDiskAsync(Stream stream, CancellationToken cancellationToken) =>
            _inner.FlushToDiskAsync(stream, cancellationToken);
        public void SetLastWriteTimeUtc(
            string fullPath,
            DateTimeOffset lastWriteTimeUtc) =>
            _inner.SetLastWriteTimeUtc(fullPath, lastWriteTimeUtc);
        public ContactCsvCommitResult CommitNew(
            string temporaryFullPath,
            string destinationFullPath) =>
            _inner.CommitNew(temporaryFullPath, destinationFullPath);
        public ContactCsvCommitResult ReplaceExisting(
            string temporaryFullPath,
            string destinationFullPath) =>
            _inner.ReplaceExisting(temporaryFullPath, destinationFullPath);
        public void DeleteFile(string fullPath) => _inner.DeleteFile(fullPath);
    }

    private sealed class CommitAppearanceFileSystem(byte[] externalBytes)
        : IContactCsvFileSystem
    {
        private readonly WindowsContactCsvFileSystem _inner = new();

        public string GetFullPath(string path) => _inner.GetFullPath(path);
        public bool FileExists(string fullPath) => _inner.FileExists(fullPath);
        public ContactCsvFileMetadata GetMetadata(string fullPath) => _inner.GetMetadata(fullPath);
        public Stream OpenRead(string fullPath) => _inner.OpenRead(fullPath);
        public Stream CreateSiblingTemporaryFile(
            string destinationFullPath,
            out string temporaryFullPath) =>
            _inner.CreateSiblingTemporaryFile(destinationFullPath, out temporaryFullPath);
        public Task FlushToDiskAsync(Stream stream, CancellationToken cancellationToken) =>
            _inner.FlushToDiskAsync(stream, cancellationToken);
        public void SetLastWriteTimeUtc(
            string fullPath,
            DateTimeOffset lastWriteTimeUtc) =>
            _inner.SetLastWriteTimeUtc(fullPath, lastWriteTimeUtc);
        public ContactCsvCommitResult CommitNew(
            string temporaryFullPath,
            string destinationFullPath)
        {
            File.WriteAllBytes(destinationFullPath, externalBytes);
            return _inner.CommitNew(temporaryFullPath, destinationFullPath);
        }
        public ContactCsvCommitResult ReplaceExisting(
            string temporaryFullPath,
            string destinationFullPath) =>
            _inner.ReplaceExisting(temporaryFullPath, destinationFullPath);
        public void DeleteFile(string fullPath) => _inner.DeleteFile(fullPath);
    }

    private sealed class CleanupFailureFileSystem : IContactCsvFileSystem
    {
        private readonly WindowsContactCsvFileSystem _inner = new();

        public string GetFullPath(string path) => _inner.GetFullPath(path);
        public bool FileExists(string fullPath) => _inner.FileExists(fullPath);
        public ContactCsvFileMetadata GetMetadata(string fullPath) => _inner.GetMetadata(fullPath);
        public Stream OpenRead(string fullPath) => _inner.OpenRead(fullPath);
        public Stream CreateSiblingTemporaryFile(
            string destinationFullPath,
            out string temporaryFullPath) =>
            _inner.CreateSiblingTemporaryFile(destinationFullPath, out temporaryFullPath);
        public Task FlushToDiskAsync(Stream stream, CancellationToken cancellationToken) =>
            _inner.FlushToDiskAsync(stream, cancellationToken);
        public void SetLastWriteTimeUtc(
            string fullPath,
            DateTimeOffset lastWriteTimeUtc) =>
            _inner.SetLastWriteTimeUtc(fullPath, lastWriteTimeUtc);
        public ContactCsvCommitResult CommitNew(
            string temporaryFullPath,
            string destinationFullPath) =>
            _inner.CommitNew(temporaryFullPath, destinationFullPath);
        public ContactCsvCommitResult ReplaceExisting(
            string temporaryFullPath,
            string destinationFullPath) =>
            throw new IOException("Injected primary replacement failure.");
        public void DeleteFile(string fullPath) =>
            throw new UnauthorizedAccessException("Injected cleanup failure.");
    }

    private sealed class CommitWarningFileSystem : IContactCsvFileSystem
    {
        private readonly WindowsContactCsvFileSystem _inner = new();

        public string GetFullPath(string path) => _inner.GetFullPath(path);
        public bool FileExists(string fullPath) => _inner.FileExists(fullPath);
        public ContactCsvFileMetadata GetMetadata(string fullPath) =>
            _inner.GetMetadata(fullPath);
        public Stream OpenRead(string fullPath) => _inner.OpenRead(fullPath);
        public Stream CreateSiblingTemporaryFile(
            string destinationFullPath,
            out string temporaryFullPath) =>
            _inner.CreateSiblingTemporaryFile(destinationFullPath, out temporaryFullPath);
        public Task FlushToDiskAsync(Stream stream, CancellationToken cancellationToken) =>
            _inner.FlushToDiskAsync(stream, cancellationToken);
        public void SetLastWriteTimeUtc(
            string fullPath,
            DateTimeOffset lastWriteTimeUtc) =>
            _inner.SetLastWriteTimeUtc(fullPath, lastWriteTimeUtc);
        public ContactCsvCommitResult CommitNew(
            string temporaryFullPath,
            string destinationFullPath)
        {
            _inner.CommitNew(temporaryFullPath, destinationFullPath);
            return new("Contacts were saved, but a temporary CSV file may remain.");
        }
        public ContactCsvCommitResult ReplaceExisting(
            string temporaryFullPath,
            string destinationFullPath)
        {
            _inner.ReplaceExisting(temporaryFullPath, destinationFullPath);
            return new("Contacts were saved, but a temporary CSV file may remain.");
        }
        public void DeleteFile(string fullPath) => _inner.DeleteFile(fullPath);
    }
}
