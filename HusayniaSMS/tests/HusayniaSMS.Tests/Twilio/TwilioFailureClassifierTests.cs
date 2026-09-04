using HusayniaSMS.Core.Messaging;
using HusayniaSMS.WinForms.Infrastructure.Twilio;

namespace HusayniaSMS.Tests.Twilio;

[TestClass]
public sealed class TwilioFailureClassifierTests
{
    [TestMethod]
    public void HttpAuthenticationStatusesAreBatchWide()
    {
        foreach (var status in new[] { 401, 403 })
        {
            Assert.AreEqual(TransportFailureKind.AuthenticationOrConfiguration,
                TwilioFailureClassifier.FromProviderFailure(status, null).FailureKind);
        }
    }

    [TestMethod]
    public void FrozenConfigurationCodesAreBatchWide()
    {
        foreach (var code in new[] { 20003, 20005, 21210, 21212, 21603, 21606, 21607 })
        {
            Assert.AreEqual(TransportFailureKind.AuthenticationOrConfiguration,
                TwilioFailureClassifier.FromProviderFailure(400, code).FailureKind);
        }
    }

    [TestMethod]
    public void RateRecipientAndUnknownFailuresAreClassified()
    {
        Assert.AreEqual(TransportFailureKind.RateLimited,
            TwilioFailureClassifier.FromProviderFailure(429, 20429).FailureKind);
        Assert.AreEqual(TransportFailureKind.RecipientRejected,
            TwilioFailureClassifier.FromProviderFailure(400, 21211).FailureKind);
        Assert.AreEqual(TransportFailureKind.ProviderFailure,
            TwilioFailureClassifier.FromProviderFailure(500, 99999).FailureKind);
    }

    [TestMethod]
    public void NetworkResultIsAmbiguousAndSafe()
    {
        var result = TwilioFailureClassifier.NetworkUnknown();
        Assert.AreEqual(TransportFailureKind.NetworkUnknown, result.FailureKind);
        StringAssert.Contains(result.SafeMessage!, "Verify provider state");
    }

    [TestMethod]
    public void OutputContainsOnlyAllowlistedCodeAndLocalMessage()
    {
        var result = TwilioFailureClassifier.FromProviderFailure(401, 20003);
        Assert.AreEqual("TWILIO_20003", result.SafeCode);
        Assert.IsFalse(result.SafeMessage!.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.IsNull(result.ProviderMessageId);
    }
}
