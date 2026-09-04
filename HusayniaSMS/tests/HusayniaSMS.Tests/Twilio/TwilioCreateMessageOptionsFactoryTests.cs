using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;
using HusayniaSMS.WinForms.Infrastructure.Twilio;

namespace HusayniaSMS.Tests.Twilio;

[TestClass]
public sealed class TwilioCreateMessageOptionsFactoryTests
{
    [TestMethod]
    public void FromPhoneModeSetsFromOnlyAndPreservesToAndBody()
    {
        var options = TwilioCreateMessageOptionsFactory.Create(new(
            "+15550100100",
            TwilioSenderMode.FromPhoneNumber,
            "+15550100999",
            " exact body "));

        Assert.AreEqual("+15550100100", options.To.ToString());
        Assert.AreEqual(" exact body ", options.Body);
        Assert.AreEqual("+15550100999", options.From!.ToString());
        Assert.IsNull(options.MessagingServiceSid);
    }

    [TestMethod]
    public void MessagingServiceModeSetsSidOnlyAndPreservesToAndBody()
    {
        const string serviceSid = "MG0123456789abcdef0123456789ABCDEF";
        var options = TwilioCreateMessageOptionsFactory.Create(new(
            "+15550100100",
            TwilioSenderMode.MessagingServiceSid,
            serviceSid,
            " exact body "));

        Assert.AreEqual("+15550100100", options.To.ToString());
        Assert.AreEqual(" exact body ", options.Body);
        Assert.IsNull(options.From);
        Assert.AreEqual(serviceSid, options.MessagingServiceSid);
    }

    [TestMethod]
    public void UndefinedModeThrowsBeforeAnyProviderCall()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TwilioCreateMessageOptionsFactory.Create(new(
                "+15550100100",
                (TwilioSenderMode)42,
                "invalid",
                "body")));
    }
}
