using System.Security.Cryptography;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Admin.Members;

public static class InvitationTokenCodec
{
    private const int MembershipIdBytes = 16;
    private const int TokenBytes = 48;

    public static string Create(MembershipId membershipId)
    {
        membershipId.EnsureValid();

        byte[] bytes = new byte[TokenBytes];
        membershipId.Value.TryWriteBytes(bytes.AsSpan(0, MembershipIdBytes));
        RandomNumberGenerator.Fill(bytes.AsSpan(MembershipIdBytes));

        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryParseMembershipId(string token, out MembershipId membershipId)
    {
        membershipId = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        string normalized = token.Trim().Replace('-', '+').Replace('_', '/');
        int remainder = normalized.Length % 4;
        if (remainder > 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - remainder), '=');
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(normalized);
            if (bytes.Length != TokenBytes)
            {
                return false;
            }

            Guid value = new(bytes.AsSpan(0, MembershipIdBytes));
            if (value == Guid.Empty)
            {
                return false;
            }

            membershipId = MembershipId.From(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
