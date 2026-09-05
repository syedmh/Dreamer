using HusayniaSMS.Core.Contacts;

namespace HusayniaSMS.Tests.Core;

[TestClass]
public sealed class ContactDraftValidatorTests
{
    private readonly E164PhoneNumberValidator _phone = new();

    [TestMethod]
    public void ValidateTrimsAndReturnsExactRequiredMessages()
    {
        var result = Validator().Validate(new(" ", " "), [], null);

        Assert.AreEqual(string.Empty, result.Name);
        Assert.AreEqual(string.Empty, result.Number);
        CollectionAssert.AreEqual(
            new[] { ContactErrorCode.NameRequired, ContactErrorCode.NumberRequired },
            result.Errors.ToArray());
        Assert.AreEqual(
            "Name is required.",
            ContactValidationMessages.GetMessage(ContactErrorCode.NameRequired));
        Assert.AreEqual(
            "Number is required.",
            ContactValidationMessages.GetMessage(ContactErrorCode.NumberRequired));
        Assert.AreEqual(
            "Number must be in E.164 format, for example +15550100100.",
            ContactValidationMessages.GetMessage(ContactErrorCode.InvalidE164));
        Assert.AreEqual(
            "Another contact already uses this number.",
            ContactValidationMessages.GetMessage(ContactErrorCode.DuplicateNumber));
        Assert.AreEqual(
            "Name cannot begin with =, +, -, or @.",
            ContactValidationMessages.GetMessage(ContactErrorCode.FormulaPrefixNotAllowed));
    }

    [TestMethod]
    public void ValidateUsesExistingE164Boundaries()
    {
        Assert.IsTrue(Validator().Validate(new("A", "+12345678"), [], null).IsValid);
        Assert.IsTrue(Validator().Validate(new("A", "+123456789012345"), [], null).IsValid);
        Assert.IsFalse(Validator().Validate(new("A", "+1234567"), [], null).IsValid);
        Assert.IsFalse(Validator().Validate(new("A", "+1234567890123456"), [], null).IsValid);
    }

    [TestMethod]
    public void AddRejectsOrdinalDuplicateAgainstOtherwiseInvalidRow()
    {
        var current = new[]
        {
            new ContactRow(
                7,
                string.Empty,
                "+15550100100",
                Array.AsReadOnly(new[] { ContactErrorCode.NameRequired }))
        };

        var result = Validator().Validate(
            new("New", " +15550100100 "),
            current,
            editingOrdinal: null);

        CollectionAssert.AreEqual(
            new[] { ContactErrorCode.DuplicateNumber },
            result.Errors.ToArray());
    }

    [TestMethod]
    public void AddRejectsNumberOwnedByDuplicateMarkedRow()
    {
        var current = new[]
        {
            new ContactRow(1, "First", "+15550100100", Array.Empty<ContactErrorCode>()),
            new ContactRow(
                2,
                "Second",
                "+15550100100",
                Array.AsReadOnly(new[] { ContactErrorCode.DuplicateNumber }))
        };

        var result = Validator().Validate(
            new("New", "+15550100100"),
            current,
            editingOrdinal: null);

        CollectionAssert.Contains(result.Errors.ToArray(), ContactErrorCode.DuplicateNumber);
    }

    [TestMethod]
    public void EditExcludesOnlyItsOwnOrdinal()
    {
        var current = new[]
        {
            new ContactRow(3, "Self", "+15550100100", Array.Empty<ContactErrorCode>()),
            new ContactRow(9, "Other", "+15550100101", Array.Empty<ContactErrorCode>())
        };

        Assert.IsTrue(Validator().Validate(
            new("Updated", "+15550100100"),
            current,
            editingOrdinal: 3).IsValid);
        Assert.IsFalse(Validator().Validate(
            new("Updated", "+15550100101"),
            current,
            editingOrdinal: 3).IsValid);
    }

    [TestMethod]
    public void DuplicateComparisonIsOrdinal()
    {
        var current = new[]
        {
            new ContactRow(1, "Existing", "+15550100100", Array.Empty<ContactErrorCode>())
        };

        var distinct = Validator().Validate(
            new("New", "+15550100101"),
            current,
            editingOrdinal: null);

        Assert.IsTrue(distinct.IsValid);
    }

    [TestMethod]
    [DataRow("=1+1")]
    [DataRow(" \t+cmd")]
    [DataRow("\r\n-value")]
    [DataRow("\u0000@value")]
    public void DraftRejectsFormulaPrefixAfterControlWhitespaceNormalization(string name)
    {
        var result = Validator().Validate(
            new(name, "+15550100100"),
            [],
            editingOrdinal: null);

        Assert.IsFalse(result.IsValid);
        CollectionAssert.Contains(
            result.Errors.ToArray(),
            ContactErrorCode.FormulaPrefixNotAllowed);
        Assert.IsTrue("=+-@".Contains(result.Name[0], StringComparison.Ordinal));
    }

    private ContactDraftValidator Validator() => new(_phone);
}
