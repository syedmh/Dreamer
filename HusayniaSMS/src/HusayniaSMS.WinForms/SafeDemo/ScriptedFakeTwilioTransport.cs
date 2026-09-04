using HusayniaSMS.Core.Messaging;

namespace HusayniaSMS.WinForms.SafeDemo;

public sealed class ScriptedFakeTwilioTransport(SafeDemoScenario scenario) : ITwilioTransport
{
    private int _callCount;

    public async Task<TransportSendResult> SendAsync(
        SmsSendRequest request,
        CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _callCount);
        if (scenario == SafeDemoScenario.Delayed)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        return scenario switch
        {
            SafeDemoScenario.AuthFailure =>
                new(false, null, TransportFailureKind.AuthenticationOrConfiguration,
                    "DEMO_AUTH_FAILURE",
                    "Safe demo simulated an account or sender configuration failure."),
            SafeDemoScenario.Mixed when call % 2 == 0 =>
                new(false, null, TransportFailureKind.RecipientRejected,
                    "DEMO_RECIPIENT_REJECTED",
                    "Safe demo simulated a recipient rejection."),
            _ => new(true, $"SMDEMO{call:D8}", TransportFailureKind.None, null, null)
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
