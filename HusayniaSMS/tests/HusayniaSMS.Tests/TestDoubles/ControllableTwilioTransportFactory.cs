using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;

namespace HusayniaSMS.Tests.TestDoubles;

internal sealed class ControllableTwilioTransportFactory : ITwilioTransportFactory
{
    private readonly SemaphoreSlim _release = new(0);
    public int Started { get; private set; }
    public int InFlight { get; private set; }
    public int MaximumInFlight { get; private set; }

    public void ReleaseOne() => _release.Release();

    public ITwilioTransport Create(TwilioCredentials credentials) => new Transport(this);

    private sealed class Transport(ControllableTwilioTransportFactory owner) : ITwilioTransport
    {
        public async Task<TransportSendResult> SendAsync(
            SmsSendRequest request,
            CancellationToken cancellationToken)
        {
            owner.Started++;
            owner.InFlight++;
            owner.MaximumInFlight = Math.Max(owner.MaximumInFlight, owner.InFlight);
            await owner._release.WaitAsync(cancellationToken);
            owner.InFlight--;
            return new(true, $"SM{owner.Started}", TransportFailureKind.None, null, null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
