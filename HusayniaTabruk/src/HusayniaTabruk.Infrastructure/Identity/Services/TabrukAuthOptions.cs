using HusayniaTabruk.Domain.Common;

namespace HusayniaTabruk.Infrastructure.Identity.Services;

public sealed class TabrukAuthOptions
{
    private readonly TimeSpan accessTokenLifetime = TimeSpan.FromMinutes(10);
    private readonly TimeSpan refreshTokenLifetime = TimeSpan.FromDays(30);
    private readonly TimeSpan stepUpLifetime = TimeSpan.FromMinutes(ApplicationLimits.StepUpLifetimeMinutes);
    private int refreshFamilyConsumedTokenLimit = 8192;

    public string SigningKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = "HusayniaTabruk";
    public string Audience { get; set; } = "HusayniaTabruk.Api";
    public string InvitationBaseUrl { get; set; } = "https://tabruk.invalid";
    public int RefreshFamilyConsumedTokenLimit
    {
        get => refreshFamilyConsumedTokenLimit;
        set => refreshFamilyConsumedTokenLimit = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(value),
                "TabrukAuth:RefreshFamilyConsumedTokenLimit must be greater than zero.");
    }

    public TimeSpan AccessTokenLifetime => accessTokenLifetime;
    public TimeSpan RefreshTokenLifetime => refreshTokenLifetime;
    public TimeSpan StepUpLifetime => stepUpLifetime;

    public byte[] GetSigningKeyBytes()
    {
        byte[] keyBytes = System.Text.Encoding.UTF8.GetBytes(SigningKey ?? string.Empty);
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                "TabrukAuth:SigningKey must be configured with at least 32 UTF-8 bytes.");
        }

        return keyBytes;
    }

    public Uri GetInvitationBaseUri()
    {
        if (!Uri.TryCreate(InvitationBaseUrl, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "TabrukAuth:InvitationBaseUrl must be an absolute HTTPS URL without user information, a query, or a fragment.");
        }

        return uri;
    }
}
