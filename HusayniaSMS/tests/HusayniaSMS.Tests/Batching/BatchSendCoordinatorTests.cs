using HusayniaSMS.Core.Batching;
using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;
using HusayniaSMS.Tests.TestDoubles;

namespace HusayniaSMS.Tests.Batching;

[TestClass]
public sealed class BatchSendCoordinatorTests
{
    [TestMethod]
    public async Task SendsSequentiallyInSnapshotOrderWithExactMapping()
    {
        var factory = new RecordingTwilioTransportFactory();
        var progress = new List<RecipientProgress>();
        var result = await new BatchSendCoordinator(factory).TryRunAsync(
            Request(" exact body "), new ImmediateProgress<RecipientProgress>(progress.Add), default);

        Assert.AreEqual(BatchStartStatus.Completed, result.Status);
        Assert.AreEqual(2, result.Summary!.Succeeded);
        CollectionAssert.AreEqual(
            new[] { "+15550100100", "+15550100101" },
            factory.Requests.Select(request => request.To).ToArray());
        Assert.IsTrue(factory.Requests.All(request =>
            request.SenderMode == TwilioSenderMode.FromPhoneNumber &&
            request.SenderValue == "+15550100999" &&
            request.Body == " exact body "));
        Assert.AreEqual(1, factory.CreateCount);
        Assert.AreEqual(2, progress.Count(item => item.State == RecipientSendState.Succeeded));
    }

    [TestMethod]
    public async Task MessagingServiceModeAndValuePropagateToEveryRequest()
    {
        var factory = new RecordingTwilioTransportFactory();
        var credentials = new TwilioCredentials(
            "AC0123456789abcdef0123456789ABCDEF",
            TwilioSenderMode.MessagingServiceSid,
            "MG0123456789abcdef0123456789ABCDEF",
            "token");
        var request = Request(
            "service body",
            recipientCount: 3,
            credentials: credentials);

        var result = await new BatchSendCoordinator(factory).TryRunAsync(
            request,
            new ImmediateProgress<RecipientProgress>(_ => { }),
            default);

        Assert.AreEqual(BatchStartStatus.Completed, result.Status);
        Assert.AreEqual(3, factory.Requests.Count);
        Assert.IsTrue(factory.Requests.All(item =>
            item.SenderMode == TwilioSenderMode.MessagingServiceSid &&
            item.SenderValue == "MG0123456789abcdef0123456789ABCDEF" &&
            item.Body == "service body"));
    }

    [TestMethod]
    public async Task RecipientAndNetworkFailuresContinueAndReconcile()
    {
        var factory = new RecordingTwilioTransportFactory();
        factory.Enqueue(
            new(false, null, TransportFailureKind.RecipientRejected, "REJECTED", "safe"),
            new(false, null, TransportFailureKind.NetworkUnknown, "NETWORK_UNKNOWN", "safe"));
        var result = await new BatchSendCoordinator(factory).TryRunAsync(
            Request(), new ImmediateProgress<RecipientProgress>(_ => { }), default);

        Assert.AreEqual(2, factory.Requests.Count);
        Assert.AreEqual(0, result.Summary!.Succeeded);
        Assert.AreEqual(2, result.Summary.Failed);
        Assert.AreEqual(0, result.Summary.CanceledOrNotStarted);
    }

    [TestMethod]
    public async Task MixedSuccessAndFailurePreservesOutcomesAndReconcilesTotals()
    {
        var factory = new RecordingTwilioTransportFactory();
        factory.Enqueue(
            new(true, "SM1", TransportFailureKind.None, null, null),
            new(false, null, TransportFailureKind.RecipientRejected, "TWILIO_21211",
                "Correct the recipient number before retrying."),
            new(true, "SM3", TransportFailureKind.None, null, null));
        var progress = new List<RecipientProgress>();
        var result = await new BatchSendCoordinator(factory).TryRunAsync(
            Request(recipientCount: 3),
            new ImmediateProgress<RecipientProgress>(progress.Add),
            default);

        Assert.AreEqual(3, factory.Requests.Count);
        Assert.AreEqual(3, result.Summary!.Confirmed);
        Assert.AreEqual(2, result.Summary.Succeeded);
        Assert.AreEqual(1, result.Summary.Failed);
        Assert.AreEqual(0, result.Summary.CanceledOrNotStarted);
        Assert.AreEqual(3, result.Summary.Succeeded + result.Summary.Failed +
            result.Summary.CanceledOrNotStarted);
        Assert.IsTrue(progress.Any(item =>
            item.ImportOrdinal == 2 &&
            item.State == RecipientSendState.Failed &&
            item.SafeMessage == "Correct the recipient number before retrying."));
        Assert.IsTrue(progress.Any(item =>
            item.ImportOrdinal == 3 &&
            item.State == RecipientSendState.Succeeded));
    }

    [TestMethod]
    public async Task UnexpectedTransportExceptionIsAmbiguousAndStopsLaterRecipients()
    {
        var factory = new RecordingTwilioTransportFactory
        {
            BeforeResultAsync = _ => throw new InvalidOperationException(
                "secret-token provider response")
        };
        var progress = new List<RecipientProgress>();
        var result = await new BatchSendCoordinator(factory).TryRunAsync(
            Request(), new ImmediateProgress<RecipientProgress>(progress.Add), default);

        Assert.AreEqual(1, factory.Requests.Count);
        Assert.AreEqual(0, result.Summary!.Succeeded);
        Assert.AreEqual(1, result.Summary.Failed);
        Assert.AreEqual(1, result.Summary.CanceledOrNotStarted);
        var failed = progress.Single(item =>
            item.ImportOrdinal == 1 && item.State == RecipientSendState.Failed);
        Assert.AreEqual(TransportFailureKind.NetworkUnknown.ToString(), failed.SafeCode);
        StringAssert.Contains(failed.SafeMessage!, "Verify");
        Assert.IsFalse(failed.SafeMessage.Contains("secret-token", StringComparison.Ordinal));
        var notStarted = progress.Single(item =>
            item.ImportOrdinal == 2 &&
            item.State == RecipientSendState.CanceledOrNotStarted);
        StringAssert.Contains(notStarted.SafeMessage!, "provider");
    }

    [TestMethod]
    public async Task AuthenticationFailureStopsLaterRecipients()
    {
        var factory = new RecordingTwilioTransportFactory();
        factory.Enqueue(new TransportSendResult(false, null,
            TransportFailureKind.AuthenticationOrConfiguration, "HTTP_401", "safe"));
        var progress = new List<RecipientProgress>();
        var result = await new BatchSendCoordinator(factory).TryRunAsync(
            Request(), new ImmediateProgress<RecipientProgress>(progress.Add), default);

        Assert.AreEqual(1, factory.Requests.Count);
        Assert.AreEqual(1, result.Summary!.Failed);
        Assert.AreEqual(1, result.Summary.CanceledOrNotStarted);
        Assert.IsTrue(progress.Any(item =>
            item.ImportOrdinal == 2 &&
            item.State == RecipientSendState.CanceledOrNotStarted));
    }

    [TestMethod]
    public async Task CancellationLetsInflightSettleAndStartsNoLaterRecipient()
    {
        var factory = new ControllableTwilioTransportFactory();
        var coordinator = new BatchSendCoordinator(factory);
        using var cancellation = new CancellationTokenSource();
        var run = coordinator.TryRunAsync(
            Request(), new ImmediateProgress<RecipientProgress>(_ => { }), cancellation.Token);

        await WaitUntilAsync(() => factory.Started == 1);
        cancellation.Cancel();
        factory.ReleaseOne();
        var result = await run;

        Assert.AreEqual(BatchStartStatus.Canceled, result.Status);
        Assert.AreEqual(1, factory.Started);
        Assert.AreEqual(1, factory.MaximumInFlight);
        Assert.AreEqual(1, result.Summary!.Succeeded);
        Assert.AreEqual(1, result.Summary.CanceledOrNotStarted);
    }

    [TestMethod]
    public async Task SecondActiveBatchIsRejectedWithoutQueuing()
    {
        var factory = new ControllableTwilioTransportFactory();
        var coordinator = new BatchSendCoordinator(factory);
        var first = coordinator.TryRunAsync(
            Request(), new ImmediateProgress<RecipientProgress>(_ => { }), default);
        await WaitUntilAsync(() => factory.Started == 1);

        var second = await coordinator.TryRunAsync(
            Request(), new ImmediateProgress<RecipientProgress>(_ => { }), default);
        Assert.AreEqual(BatchStartStatus.RejectedAlreadyActive, second.Status);
        Assert.IsNull(second.Summary);
        Assert.AreEqual(1, factory.Started);

        factory.ReleaseOne();
        await WaitUntilAsync(() => factory.Started == 2);
        factory.ReleaseOne();
        await first;
    }

    [TestMethod]
    public async Task SnapshotIsUnaffectedBySourceListMutation()
    {
        var source = new List<RecipientSnapshot>
        {
            new(1, "One", "+15550100100")
        };
        var request = new SmsBatchRequest(
            Guid.NewGuid(), SendScope.Selected, source.ToArray(), "body", Credentials());
        source.Add(new(2, "Two", "+15550100101"));
        var factory = new RecordingTwilioTransportFactory();
        var result = await new BatchSendCoordinator(factory).TryRunAsync(
            request, new ImmediateProgress<RecipientProgress>(_ => { }), default);

        Assert.AreEqual(1, result.Summary!.Confirmed);
        Assert.AreEqual(1, factory.Requests.Count);
    }

    private static SmsBatchRequest Request(
        string body = "body",
        int recipientCount = 2,
        TwilioCredentials? credentials = null) => new(
        Guid.NewGuid(),
        SendScope.Selected,
        Enumerable.Range(1, recipientCount)
            .Select(index => new RecipientSnapshot(
                index, $"Person {index}", $"+155501001{index - 1:D2}"))
            .ToArray(),
        body,
        credentials ?? Credentials());

    private static TwilioCredentials Credentials() =>
        new(
            "AC0123456789abcdef0123456789ABCDEF",
            TwilioSenderMode.FromPhoneNumber,
            "+15550100999",
            "token");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
