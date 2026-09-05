using HusayniaSMS.Core.Contacts;
using HusayniaSMS.WinForms.Presentation;

namespace HusayniaSMS.Tests.Presentation;

[TestClass]
public sealed class ContactDocumentStateTests
{
    private readonly ContactRowValidator _rows = new(new E164PhoneNumberValidator());

    [TestMethod]
    public void UntitledAndLoadedDocumentsAreCleanWithCorrectNextOrdinal()
    {
        var untitled = ContactDocumentState.CreateUntitled();
        Assert.AreEqual(0, untitled.Rows.Count);
        Assert.IsNull(untitled.Path);
        Assert.IsNull(untitled.Version);
        Assert.IsFalse(untitled.IsDirty);
        Assert.AreEqual(1, untitled.NextOrdinal);

        var version = Version();
        var loaded = ContactDocumentState.FromLoaded(
            "C:\\contacts.csv",
            version,
            Validated((4, "A", "+15550100100"), (9, "B", "+15550100101")));
        Assert.AreEqual(10, loaded.NextOrdinal);
        Assert.AreSame(version, loaded.Version);
        Assert.IsFalse(loaded.IsDirty);
    }

    [TestMethod]
    public void AddAppendsAndNeverMutatesPriorState()
    {
        var original = ContactDocumentState.CreateUntitled();
        var added = original.Add(ValidDraft(" A ", " +15550100100 "), _rows);

        Assert.AreEqual(0, original.Rows.Count);
        Assert.AreEqual(1, original.NextOrdinal);
        Assert.AreEqual(1, added.Rows.Count);
        Assert.AreEqual(1, added.Rows[0].ImportOrdinal);
        Assert.AreEqual("A", added.Rows[0].Name);
        Assert.AreEqual(2, added.NextOrdinal);
        Assert.IsTrue(added.IsDirty);
    }

    [TestMethod]
    public void EditKeepsOrdinalAndVisualPosition()
    {
        var loaded = ContactDocumentState.FromLoaded(
            "C:\\contacts.csv",
            Version(),
            Validated((3, "A", "+15550100100"), (8, "B", "+15550100101")));

        var edited = loaded.Edit(3, ValidDraft("Updated", "+15550100102"), _rows);

        CollectionAssert.AreEqual(
            new[] { 3, 8 },
            edited.Rows.Select(row => row.ImportOrdinal).ToArray());
        Assert.AreEqual("Updated", edited.Rows[0].Name);
        Assert.AreEqual(9, edited.NextOrdinal);
        Assert.AreEqual("A", loaded.Rows[0].Name);
    }

    [TestMethod]
    public void DeleteLeavesGapsDeleteAllAndNeverReusesOrdinals()
    {
        var state = ContactDocumentState.FromLoaded(
            "C:\\contacts.csv",
            Version(),
            Validated(
                (1, "A", "+15550100100"),
                (2, "B", "+15550100101"),
                (3, "C", "+15550100102")));

        var withGap = state.Delete(new HashSet<int> { 2 }, _rows);
        CollectionAssert.AreEqual(
            new[] { 1, 3 },
            withGap.Rows.Select(row => row.ImportOrdinal).ToArray());
        Assert.AreEqual(4, withGap.NextOrdinal);

        var appended = withGap.Add(ValidDraft("D", "+15550100103"), _rows);
        Assert.AreEqual(4, appended.Rows[^1].ImportOrdinal);

        var empty = appended.Delete(
            appended.Rows.Select(row => row.ImportOrdinal).ToHashSet(),
            _rows);
        Assert.AreEqual(0, empty.Rows.Count);
        Assert.AreEqual(5, empty.NextOrdinal);
        Assert.IsTrue(empty.IsDirty);
    }

    [TestMethod]
    public void EveryMutationFullyRevalidatesOrderedDocument()
    {
        var loaded = ContactDocumentState.FromLoaded(
            "C:\\contacts.csv",
            Version(),
            Validated((1, "A", "+15550100100"), (2, "B", "+15550100101")));

        var duplicate = loaded.Edit(
            1,
            ValidDraft("A", "+15550100101"),
            _rows);

        Assert.IsTrue(duplicate.Rows[0].IsEligible);
        CollectionAssert.AreEqual(
            new[] { ContactErrorCode.DuplicateNumber },
            duplicate.Rows[1].Errors.ToArray());

        var repaired = duplicate.Delete(new HashSet<int> { 1 }, _rows);
        Assert.IsTrue(repaired.Rows[0].IsEligible);
        Assert.AreEqual(2, repaired.Rows[0].ImportOrdinal);
    }

    [TestMethod]
    public void MarkSavedChangesPathVersionAndCleanOnly()
    {
        var dirty = ContactDocumentState.CreateUntitled()
            .Add(ValidDraft("A", "+15550100100"), _rows);
        var version = Version();

        var saved = dirty.MarkSaved("C:\\saved.csv", version);

        Assert.IsFalse(saved.IsDirty);
        Assert.AreEqual("C:\\saved.csv", saved.Path);
        Assert.AreSame(version, saved.Version);
        Assert.AreEqual(dirty.NextOrdinal, saved.NextOrdinal);
        CollectionAssert.AreEqual(dirty.Rows.ToArray(), saved.Rows.ToArray());
        Assert.IsTrue(dirty.IsDirty);
    }

    [TestMethod]
    public void InvalidDraftAndMissingMutationTargetsFailLoudly()
    {
        var state = ContactDocumentState.CreateUntitled();
        var invalid = new ContactDraftValidation(
            string.Empty,
            string.Empty,
            new[] { ContactErrorCode.NameRequired });

        Assert.ThrowsExactly<ArgumentException>(() => state.Add(invalid, _rows));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            state.Edit(99, ValidDraft("A", "+15550100100"), _rows));
        Assert.ThrowsExactly<ArgumentException>(() =>
            state.Delete(new HashSet<int> { 99 }, _rows));
    }

    private ContactDraftValidation ValidDraft(string name, string number) =>
        new ContactDraftValidator(new E164PhoneNumberValidator())
            .Validate(new(name, number), [], null);

    private IReadOnlyList<ContactRow> Validated(
        params (int Ordinal, string Name, string Number)[] values) =>
        _rows.Validate(values
            .Select(value => new ContactInput(value.Ordinal, value.Name, value.Number))
            .ToArray());

    private static ContactCsvVersion Version() =>
        new(10, DateTimeOffset.Parse("2026-09-04T00:00:00Z"), new string('A', 64));
}
