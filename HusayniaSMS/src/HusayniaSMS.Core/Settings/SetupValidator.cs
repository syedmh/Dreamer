using System.Text.RegularExpressions;
using HusayniaSMS.Core.Contacts;

namespace HusayniaSMS.Core.Settings;

public sealed partial class SetupValidator(IPhoneNumberValidator phoneNumberValidator)
    : ISetupValidator
{
    public SetupValidationResult Validate(SetupInput input, bool savedTokenExists)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new List<FieldError>();
        var accountSid = input.AccountSid?.Trim() ?? string.Empty;
        var sender = input.SenderValue?.Trim() ?? string.Empty;
        var newToken = input.NewAuthToken?.Trim() ?? string.Empty;

        if (!AccountSidRegex().IsMatch(accountSid))
        {
            errors.Add(new("AccountSid", "InvalidAccountSid",
                "Account SID must be AC followed by 32 hexadecimal characters."));
        }

        switch (input.SenderMode)
        {
            case TwilioSenderMode.FromPhoneNumber when !phoneNumberValidator.IsValid(sender):
                errors.Add(new("SenderValue", "InvalidFromPhoneNumber",
                    "From phone number must be in E.164 format, for example +15550100100."));
                break;
            case TwilioSenderMode.MessagingServiceSid when
                !MessagingServiceSidRegex().IsMatch(sender):
                errors.Add(new("SenderValue", "InvalidMessagingServiceSid",
                    "Messaging Service SID must be MG followed by 32 hexadecimal characters."));
                break;
            case TwilioSenderMode.FromPhoneNumber:
            case TwilioSenderMode.MessagingServiceSid:
                break;
            default:
                errors.Add(new("SenderMode", "InvalidSenderMode",
                    "Select either From phone number or Messaging Service SID."));
                break;
        }

        if (input.PreserveSavedToken)
        {
            if (!savedTokenExists)
            {
                errors.Add(new("AuthToken", "AuthTokenRequired",
                    "Enter an auth token because no usable saved token is available."));
            }
        }
        else if (newToken.Length == 0)
        {
            errors.Add(new("AuthToken", "AuthTokenRequired", "Auth token is required."));
        }

        return new(errors.Count == 0, errors.AsReadOnly());
    }

    [GeneratedRegex(@"^AC[0-9A-Fa-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex AccountSidRegex();

    [GeneratedRegex(@"^MG[0-9A-Fa-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex MessagingServiceSidRegex();
}
