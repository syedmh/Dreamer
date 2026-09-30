using System.Security.Cryptography;
using System.Text;

namespace HusayniaTabruk.Infrastructure.Identity.Services;

internal static class OpaqueTokenCrypto
{
    public static string GenerateSecret(int sizeInBytes = 32) =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(sizeInBytes));

    public static string ComputeSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    public static bool FixedTimeEquals(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)
            || string.IsNullOrWhiteSpace(right)
            || left.Length != right.Length)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(left),
                Encoding.UTF8.GetBytes(right));
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string Base64UrlEncode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static byte[] Base64UrlDecode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string normalized = value.Replace('-', '+').Replace('_', '/');
        int remainder = normalized.Length % 4;
        if (remainder > 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - remainder), '=');
        }

        return Convert.FromBase64String(normalized);
    }
}
