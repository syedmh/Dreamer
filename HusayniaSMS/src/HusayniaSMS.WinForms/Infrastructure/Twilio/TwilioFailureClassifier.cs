using HusayniaSMS.Core.Messaging;

namespace HusayniaSMS.WinForms.Infrastructure.Twilio;

internal static class TwilioFailureClassifier
{
    private static readonly HashSet<int> AuthenticationOrConfigurationCodes =
    [
        20003, 20005, 21210, 21212, 21603, 21606, 21607
    ];

    public static TransportSendResult FromProviderFailure(int? httpStatus, int? providerCode)
    {
        var safeCode = providerCode is not null
            ? $"TWILIO_{providerCode.Value}"
            : httpStatus is not null ? $"HTTP_{httpStatus.Value}" : "TWILIO_PROVIDER_FAILURE";

        if (httpStatus is 401 or 403 ||
            providerCode is not null && AuthenticationOrConfigurationCodes.Contains(providerCode.Value))
        {
            return new(false, null, TransportFailureKind.AuthenticationOrConfiguration,
                safeCode,
                "Twilio rejected the account or sender configuration. Review setup before retrying.");
        }

        if (httpStatus == 429 || providerCode == 20429)
        {
            return new(false, null, TransportFailureKind.RateLimited,
                safeCode,
                "Twilio rate-limited this recipient. Wait before starting a new confirmed batch.");
        }

        if (providerCode is >= 21211 and <= 21617)
        {
            return new(false, null, TransportFailureKind.RecipientRejected,
                safeCode,
                "Twilio rejected this recipient. Verify the number and recipient status.");
        }

        return new(false, null, TransportFailureKind.ProviderFailure,
            safeCode,
            "Twilio could not accept this message. Review the safe code before retrying.");
    }

    public static TransportSendResult NetworkUnknown() =>
        new(false, null, TransportFailureKind.NetworkUnknown,
            "NETWORK_UNKNOWN",
            "The network result is unknown. Verify provider state before manually retrying.");
}
