using System.Security.Cryptography;
using System.Text;
using HusayniaTabruk.Application.Abstractions.Authentication;
using Microsoft.Extensions.Options;

namespace HusayniaTabruk.Infrastructure.Identity.Services;

public sealed class RefreshTokenFamilyFingerprintProvider(
    IOptions<TabrukAuthOptions> options) : IRefreshTokenFamilyFingerprintProvider
{
    private const int FingerprintLength = 24;

    public bool TryGetFingerprint(string refreshToken, out string fingerprint)
    {
        fingerprint = string.Empty;
        if (!OpaqueBearerToken.TryGetLocator(refreshToken, out string locator))
        {
            return false;
        }

        byte[] hash = HMACSHA256.HashData(
            options.Value.GetSigningKeyBytes(),
            Encoding.UTF8.GetBytes(locator));
        fingerprint = Convert.ToHexString(hash)[..FingerprintLength];
        return true;
    }
}
