using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;

namespace HusayniaSMS.WinForms.Infrastructure.Twilio;

internal sealed class TwilioTransportFactory : ITwilioTransportFactory
{
    public ITwilioTransport Create(TwilioCredentials credentials) =>
        new TwilioTransport(credentials);
}
