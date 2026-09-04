using HusayniaSMS.Core.Contacts;
using HusayniaSMS.Core.Settings;

namespace HusayniaSMS.Tests.Core;

[TestClass]
public sealed class SetupValidatorTests
{
    private readonly SetupValidator _validator = new(new E164PhoneNumberValidator());
    private const string ValidSid = "AC0123456789abcdef0123456789ABCDEF";
    private const string ValidMessagingServiceSid = "MG0123456789abcdef0123456789ABCDEF";

    [TestMethod]
    public void ReportsEveryOffendingField()
    {
        var result = _validator.Validate(new(
            "",
            TwilioSenderMode.FromPhoneNumber,
            "bad",
            "",
            false), false);
        Assert.IsFalse(result.IsValid);
        CollectionAssert.AreEquivalent(
            new[] { "AccountSid", "SenderValue", "AuthToken" },
            result.Errors.Select(error => error.Field).ToArray());
    }

    [TestMethod]
    [DataRow("AC0123")]
    [DataRow("SK0123456789abcdef0123456789abcdef")]
    [DataRow("AC0123456789abcdef0123456789abcdeg")]
    public void RejectsMalformedAccountSid(string sid) =>
        Assert.IsFalse(_validator.Validate(
            new(
                sid,
                TwilioSenderMode.FromPhoneNumber,
                "+15550100100",
                "token",
                false), false).IsValid);

    [TestMethod]
    public void PreservedTokenRequiresUsableSavedToken()
    {
        Assert.IsFalse(_validator.Validate(
            new(
                ValidSid,
                TwilioSenderMode.FromPhoneNumber,
                "+15550100100",
                null,
                true), false).IsValid);
        Assert.IsTrue(_validator.Validate(
            new(
                ValidSid,
                TwilioSenderMode.FromPhoneNumber,
                "+15550100100",
                null,
                true), true).IsValid);
    }

    [TestMethod]
    [DataRow("+12345678")]
    [DataRow("+123456789012345")]
    [DataRow("+15550100100")]
    public void ValidFromPhoneNumberPasses(string sender) =>
        Assert.IsTrue(_validator.Validate(
            new(
                ValidSid,
                TwilioSenderMode.FromPhoneNumber,
                sender,
                " opaque-token ",
                false), false).IsValid);

    [TestMethod]
    [DataRow("+1234567")]
    [DataRow("+1234567890123456")]
    [DataRow("+02345678")]
    [DataRow("15550100100")]
    [DataRow(ValidMessagingServiceSid)]
    public void InvalidFromPhoneNumberIsRejected(string sender)
    {
        var result = _validator.Validate(new(
            ValidSid,
            TwilioSenderMode.FromPhoneNumber,
            sender,
            "token",
            false), false);

        Assert.IsFalse(result.IsValid);
        var error = result.Errors.Single();
        Assert.AreEqual("SenderValue", error.Field);
        Assert.AreEqual("InvalidFromPhoneNumber", error.Code);
    }

    [TestMethod]
    public void ValidMessagingServiceSidPasses() =>
        Assert.IsTrue(_validator.Validate(new(
            ValidSid,
            TwilioSenderMode.MessagingServiceSid,
            $" {ValidMessagingServiceSid} ",
            "token",
            false), false).IsValid);

    [TestMethod]
    [DataRow("mg0123456789abcdef0123456789ABCDEF")]
    [DataRow("MS0123456789abcdef0123456789ABCDEF")]
    [DataRow("MG0123456789abcdef0123456789ABCDE")]
    [DataRow("MG0123456789abcdef0123456789ABCDEFF")]
    [DataRow("MG0123456789abcdef0123456789ABCDEG")]
    [DataRow("+15550100100")]
    public void InvalidMessagingServiceSidIsRejected(string sender)
    {
        var result = _validator.Validate(new(
            ValidSid,
            TwilioSenderMode.MessagingServiceSid,
            sender,
            "token",
            false), false);

        Assert.IsFalse(result.IsValid);
        var error = result.Errors.Single();
        Assert.AreEqual("SenderValue", error.Field);
        Assert.AreEqual("InvalidMessagingServiceSid", error.Code);
    }

    [TestMethod]
    public void UndefinedSenderModeFailsClosedAndReportsAllOtherOffendingFields()
    {
        var result = _validator.Validate(new(
            "",
            (TwilioSenderMode)42,
            "anything",
            "",
            false), false);

        Assert.IsFalse(result.IsValid);
        CollectionAssert.AreEquivalent(
            new[] { "AccountSid", "SenderMode", "AuthToken" },
            result.Errors.Select(error => error.Field).ToArray());
        Assert.AreEqual(
            "InvalidSenderMode",
            result.Errors.Single(error => error.Field == "SenderMode").Code);
    }
}
