using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;

namespace HusayniaSMS.Tests.TestDoubles;

internal sealed class RecordingTwilioTransportFactory : ITwilioTransportFactory
{
    private readonly Queue<TransportSendResult> _results = new();
    public List<SmsSendRequest> Requests { get; } = [];
    public int CreateCount { get; private set; }
    public Func<int, Task>? BeforeResultAsync { get; set; }

    public void Enqueue(params TransportSendResult[] results)
    {
        foreach (var result in results)
        {
            _results.Enqueue(result);
        }
    }

    public ITwilioTransport Create(TwilioCredentials credentials)
    {
        CreateCount++;
        return new RecordingTransport(this);
    }

    private sealed class RecordingTransport(RecordingTwilioTransportFactory owner)
        : ITwilioTransport
    {
        public async Task<TransportSendResult> SendAsync(
            SmsSendRequest request,
            CancellationToken cancellationToken)
        {
            owner.Requests.Add(request);
            if (owner.BeforeResultAsync is not null)
            {
                await owner.BeforeResultAsync(owner.Requests.Count);
            }

            return owner._results.Count > 0
                ? owner._results.Dequeue()
                : new(true, $"SM{owner.Requests.Count}", TransportFailureKind.None, null, null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
