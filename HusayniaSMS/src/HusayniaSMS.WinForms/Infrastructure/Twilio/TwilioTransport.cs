using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;
using Twilio.Clients;
using Twilio.Exceptions;
using Twilio.Http;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace HusayniaSMS.WinForms.Infrastructure.Twilio;

internal sealed class TwilioTransport : ITwilioTransport
{
    private readonly System.Net.Http.HttpClient _httpClient;
    private readonly TwilioRestClient _client;

    public TwilioTransport(TwilioCredentials credentials)
    {
        _httpClient = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _client = new TwilioRestClient(
            credentials.AccountSid,
            credentials.AuthToken,
            credentials.AccountSid,
            region: null,
            new SystemNetHttpClient(_httpClient),
            edge: null);
    }

    public async Task<TransportSendResult> SendAsync(
        SmsSendRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var options = TwilioCreateMessageOptionsFactory.Create(request);
            var result = await MessageResource.CreateAsync(options, _client).ConfigureAwait(false);
            return new(true, result.Sid, TransportFailureKind.None, null, null);
        }
        catch (ApiException exception)
        {
            return TwilioFailureClassifier.FromProviderFailure(exception.Status, exception.Code);
        }
        catch (HttpRequestException)
        {
            return TwilioFailureClassifier.NetworkUnknown();
        }
        catch (TaskCanceledException)
        {
            return TwilioFailureClassifier.NetworkUnknown();
        }
    }

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
