using HusayniaSMS.Core.Contacts;

namespace HusayniaSMS.Tests.Core;

[TestClass]
public sealed class ContactValidationTests
{
    private readonly E164PhoneNumberValidator _phone = new();

    [TestMethod]
    [DataRow("+12345678", true)]
    [DataRow("+123456789012345", true)]
    [DataRow("+1234567", false)]
    [DataRow("+1234567890123456", false)]
    [DataRow("+02345678", false)]
    [DataRow("12345678", false)]
    [DataRow("+1 5550100", false)]
    public void E164Boundaries(string value, bool expected) =>
        Assert.AreEqual(expected, _phone.IsValid(value));

    [TestMethod]
    public void ValidationTrimsAndKeepsInvalidRowsVisible()
    {
        var rows = Validator().Validate(
        [
            new(1, " Alice ", " +15550100100 "),
            new(2, " ", "+15550100101"),
            new(3, "Carol", ""),
            new(4, "Dave", "555")
        ]);

        Assert.AreEqual("Alice", rows[0].Name);
        Assert.AreEqual("+15550100100", rows[0].Number);
        Assert.IsTrue(rows[0].IsEligible);
        CollectionAssert.Contains(rows[1].Errors.ToArray(), ContactErrorCode.NameRequired);
        CollectionAssert.Contains(rows[2].Errors.ToArray(), ContactErrorCode.NumberRequired);
        CollectionAssert.Contains(rows[3].Errors.ToArray(), ContactErrorCode.InvalidE164);
        Assert.AreEqual(4, rows.Count);
    }

    [TestMethod]
    public void LaterValidDuplicateIsIneligible()
    {
        var rows = Validator().Validate(
        [
            new(1, "First", "+15550100100"),
            new(2, "Second", " +15550100100 ")
        ]);

        Assert.IsTrue(rows[0].IsEligible);
        Assert.IsFalse(rows[1].IsEligible);
        CollectionAssert.AreEqual(
            new[] { ContactErrorCode.DuplicateNumber },
            rows[1].Errors.ToArray());
    }

    [TestMethod]
    public void InvalidNumberDoesNotReserveDuplicateSlot()
    {
        var rows = Validator().Validate(
        [
            new(1, "Bad", "123"),
            new(2, "Also bad", "123")
        ]);
        Assert.IsTrue(rows.All(row => row.Errors.Contains(ContactErrorCode.InvalidE164)));
        Assert.IsTrue(rows.All(row => !row.Errors.Contains(ContactErrorCode.DuplicateNumber)));
    }

    private ContactRowValidator Validator() => new(_phone);
}
