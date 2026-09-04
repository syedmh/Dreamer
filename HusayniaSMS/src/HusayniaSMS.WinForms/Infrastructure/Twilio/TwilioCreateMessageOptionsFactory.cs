using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace HusayniaSMS.WinForms.Infrastructure.Twilio;

internal static class TwilioCreateMessageOptionsFactory
{
    internal static CreateMessageOptions Create(SmsSendRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = new CreateMessageOptions(new PhoneNumber(request.To))
        {
            Body = request.Body
        };

        switch (request.SenderMode)
        {
            case TwilioSenderMode.FromPhoneNumber:
                options.From = new PhoneNumber(request.SenderValue);
                break;
            case TwilioSenderMode.MessagingServiceSid:
                options.MessagingServiceSid = request.SenderValue;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    request.SenderMode,
                    "Unsupported Twilio sender mode.");
        }

        return options;
    }
}
