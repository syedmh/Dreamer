using HusayniaSMS.Core.Settings;

namespace HusayniaSMS.Core.Messaging;

public sealed record SmsSendRequest(
    string To,
    TwilioSenderMode SenderMode,
    string SenderValue,
    string Body);

public enum TransportFailureKind
{
    None,
    RecipientRejected,
    AuthenticationOrConfiguration,
    RateLimited,
    NetworkUnknown,
    ProviderFailure
}

public sealed record TransportSendResult(
    bool Succeeded,
    string? ProviderMessageId,
    TransportFailureKind FailureKind,
    string? SafeCode,
    string? SafeMessage);

public interface ITwilioTransport : IAsyncDisposable
{
    Task<TransportSendResult> SendAsync(
        SmsSendRequest request,
        CancellationToken cancellationToken);
}

public interface ITwilioTransportFactory
{
    ITwilioTransport Create(TwilioCredentials credentials);
}
