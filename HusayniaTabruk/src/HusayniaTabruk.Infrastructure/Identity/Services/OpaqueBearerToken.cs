using System.Security.Cryptography;

namespace HusayniaTabruk.Infrastructure.Identity.Services;

internal static class OpaqueBearerToken
{
    private const int LocatorSizeInBytes = 16;
    private const int SecretSizeInBytes = 32;
    private const int TokenSizeInBytes = LocatorSizeInBytes + SecretSizeInBytes;

    public static OpaqueBearerTokenEnvelope Create()
    {
        byte[] locatorBytes = RandomNumberGenerator.GetBytes(LocatorSizeInBytes);
        return new OpaqueBearerTokenEnvelope(
            EncodeToken(locatorBytes, RandomNumberGenerator.GetBytes(SecretSizeInBytes)),
            OpaqueTokenCrypto.Base64UrlEncode(locatorBytes));
    }

    public static string CreateToken(string locator)
    {
        if (!TryDecodeLocator(locator, out byte[] locatorBytes))
        {
            throw new InvalidOperationException("The opaque bearer-token locator is invalid.");
        }

        return EncodeToken(locatorBytes, RandomNumberGenerator.GetBytes(SecretSizeInBytes));
    }

    public static bool TryGetLocator(string token, out string locator)
    {
        locator = string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            byte[] tokenBytes = OpaqueTokenCrypto.Base64UrlDecode(token);
            if (tokenBytes.Length != TokenSizeInBytes)
            {
                return false;
            }

            locator = OpaqueTokenCrypto.Base64UrlEncode(tokenBytes[..LocatorSizeInBytes]);
            return true;
        }
        catch (Exception)
        {
            locator = string.Empty;
            return false;
        }
    }

    private static string EncodeToken(byte[] locatorBytes, byte[] secretBytes)
    {
        byte[] tokenBytes = new byte[TokenSizeInBytes];
        Buffer.BlockCopy(locatorBytes, 0, tokenBytes, 0, LocatorSizeInBytes);
        Buffer.BlockCopy(secretBytes, 0, tokenBytes, LocatorSizeInBytes, SecretSizeInBytes);
        return OpaqueTokenCrypto.Base64UrlEncode(tokenBytes);
    }

    private static bool TryDecodeLocator(string locator, out byte[] locatorBytes)
    {
        locatorBytes = [];
        if (string.IsNullOrWhiteSpace(locator))
        {
            return false;
        }

        try
        {
            locatorBytes = OpaqueTokenCrypto.Base64UrlDecode(locator);
            return locatorBytes.Length == LocatorSizeInBytes;
        }
        catch (Exception)
        {
            locatorBytes = [];
            return false;
        }
    }
}

internal sealed record OpaqueBearerTokenEnvelope(
    string Token,
    string Locator);
