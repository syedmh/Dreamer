using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace Husaynia.Infrastructure.Identity;

public sealed class HusayniaIdentityUser : IdentityUser<Guid>
{
    private const string DummyInvitationTokenHash =
        "0000000000000000000000000000000000000000000000000000000000000000";

    private HusayniaIdentityUser()
    {
    }

    public HusayniaIdentityUser(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Trim().Length > 256)
        {
            throw new ArgumentException("A bounded email address is required.", nameof(email));
        }

        Id = Guid.NewGuid();
        UserName = email.Trim();
        Email = email.Trim();
        EmailConfirmed = true;
        LockoutEnabled = true;
    }

    public bool IsDisabled { get; private set; }

    public DateTimeOffset? DisabledAtUtc { get; private set; }

    public DateTimeOffset? InvitationIssuedAtUtc { get; private set; }

    public DateTimeOffset? InvitationExpiresAtUtc { get; private set; }

    public string? InvitationTokenHash { get; private set; }

    public bool HasPassword => !string.IsNullOrWhiteSpace(PasswordHash);

    public void IssueInvitation(
        string tokenHash,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(tokenHash) || tokenHash.Length > 128)
        {
            throw new ArgumentException("A bounded token hash is required.", nameof(tokenHash));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            expiresAtUtc,
            issuedAtUtc,
            nameof(expiresAtUtc));

        InvitationTokenHash = tokenHash;
        InvitationIssuedAtUtc = issuedAtUtc.ToUniversalTime();
        InvitationExpiresAtUtc = expiresAtUtc.ToUniversalTime();
    }

    public bool AcceptInvitation(string tokenHash, DateTimeOffset now)
    {
        if (!HasValidInvitationToken(tokenHash, now))
        {
            return false;
        }

        InvitationTokenHash = null;
        InvitationIssuedAtUtc = null;
        InvitationExpiresAtUtc = null;
        return true;
    }

    public bool HasValidInvitationToken(string tokenHash, DateTimeOffset now)
    {
        var persistedTokenHash = string.IsNullOrWhiteSpace(InvitationTokenHash)
            ? DummyInvitationTokenHash
            : InvitationTokenHash;
        var candidateTokenHash = tokenHash ?? string.Empty;
        var hashesMatch = CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(persistedTokenHash),
            System.Text.Encoding.UTF8.GetBytes(candidateTokenHash));
        return !string.IsNullOrWhiteSpace(candidateTokenHash) &&
            !string.IsNullOrWhiteSpace(InvitationTokenHash) &&
            InvitationExpiresAtUtc is not null &&
            InvitationExpiresAtUtc > now &&
            hashesMatch;
    }

    public void Disable(DateTimeOffset now)
    {
        IsDisabled = true;
        DisabledAtUtc ??= now.ToUniversalTime();
        LockoutEnabled = true;
        LockoutEnd = DateTimeOffset.MaxValue;
    }
}

public sealed class HusayniaIdentityRole : IdentityRole<Guid>
{
    private HusayniaIdentityRole()
    {
    }

    public HusayniaIdentityRole(string roleName)
    {
        if (string.IsNullOrWhiteSpace(roleName) || roleName.Trim().Length > 256)
        {
            throw new ArgumentException("A bounded role name is required.", nameof(roleName));
        }

        Id = Guid.NewGuid();
        Name = roleName.Trim();
        NormalizedName = roleName.Trim().ToUpperInvariant();
    }
}

public static class HusayniaIdentityToken
{
    public static string CreateRawToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string rawToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);
        return Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(rawToken.Trim())));
    }
}
