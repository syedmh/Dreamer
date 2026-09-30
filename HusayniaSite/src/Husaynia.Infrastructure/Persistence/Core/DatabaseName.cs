using System.Security.Cryptography;
using System.Text;

namespace Husaynia.Infrastructure.Persistence.Core;

public static class DatabaseName
{
    private const int MaximumLength = 120;

    public static string Create(string prefix, string discriminator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(discriminator);

        var normalized = string.Concat(
            $"{prefix}_{discriminator}".Select(character =>
                char.IsAsciiLetterOrDigit(character) ? character : '_'));
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{prefix}\u001f{discriminator}")))[..12];
        var stemLength = MaximumLength - hash.Length - 1;
        var stem = normalized[..Math.Min(normalized.Length, stemLength)].Trim('_');

        return $"{stem}_{hash}";
    }
}
