using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;
using HusayniaSMS.WinForms.SafeDemo;

namespace HusayniaSMS.Tests.Twilio;

[TestClass]
public sealed class SafeDemoTransportTests
{
    private static readonly SmsSendRequest FromPhoneRequest =
        new(
            "+15550100100",
            TwilioSenderMode.FromPhoneNumber,
            "+15550100999",
            "body");
    private static readonly SmsSendRequest MessagingServiceRequest =
        new(
            "+15550100100",
            TwilioSenderMode.MessagingServiceSid,
            "MG0123456789abcdef0123456789ABCDEF",
            "body");

    [TestMethod]
    [DataRow(TwilioSenderMode.FromPhoneNumber)]
    [DataRow(TwilioSenderMode.MessagingServiceSid)]
    public async Task AllSuccessSupportsBothSenderModesWithoutLiveClient(TwilioSenderMode mode)
    {
        await using var transport = Create(SafeDemoScenario.AllSuccess);
        var request = mode == TwilioSenderMode.FromPhoneNumber
            ? FromPhoneRequest
            : MessagingServiceRequest;
        var result = await transport.SendAsync(request, default);
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("SMDEMO00000001", result.ProviderMessageId);
        Assert.AreEqual(typeof(ScriptedFakeTwilioTransport), transport.GetType());
    }

    [TestMethod]
    public async Task MixedAlternatesSuccessAndRecipientFailure()
    {
        await using var transport = Create(SafeDemoScenario.Mixed);
        Assert.IsTrue((await transport.SendAsync(FromPhoneRequest, default)).Succeeded);
        var second = await transport.SendAsync(MessagingServiceRequest, default);
        Assert.IsFalse(second.Succeeded);
        Assert.AreEqual(TransportFailureKind.RecipientRejected, second.FailureKind);
    }

    [TestMethod]
    public async Task AuthFailureIsBatchWideCategory()
    {
        await using var transport = Create(SafeDemoScenario.AuthFailure);
        var result = await transport.SendAsync(MessagingServiceRequest, default);
        Assert.AreEqual(TransportFailureKind.AuthenticationOrConfiguration, result.FailureKind);
    }

    [TestMethod]
    public async Task DelayedScenarioIsCancelableAndUsesNoLiveClient()
    {
        await using var transport = Create(SafeDemoScenario.Delayed);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => transport.SendAsync(MessagingServiceRequest, cancellation.Token));
        Assert.AreEqual(typeof(ScriptedFakeTwilioTransport), transport.GetType());
    }

    private static ITwilioTransport Create(SafeDemoScenario scenario) =>
        new ScriptedFakeTwilioTransportFactory(scenario).Create(
            new TwilioCredentials(
                "fake",
                TwilioSenderMode.FromPhoneNumber,
                "fake",
                "fake"));
}
