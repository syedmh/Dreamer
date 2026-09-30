using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Abstractions.Authentication;

public interface ICurrentActor
{
    UserId UserId { get; }
    MembershipId MembershipId { get; }
    OrganizationId OrganizationId { get; }
}

public interface IIdentityService
{
    ValueTask<Result<UserId>> VerifyCredentialsAsync(
        string login,
        string password,
        CancellationToken cancellationToken = default);

    ValueTask<Result> SetPasswordAsync(
        UserId userId,
        string password,
        CancellationToken cancellationToken = default);

    ValueTask<Result> VerifyPasswordAsync(
        UserId userId,
        string password,
        CancellationToken cancellationToken = default);
}

public interface ITokenService
{
    ValueTask<TokenPair> IssueAsync(
        UserId userId,
        MembershipId membershipId,
        OrganizationId organizationId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);

    ValueTask<Result<TokenPair>> RotateAsync(
        string refreshToken,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);

    ValueTask RevokeAsync(
        string refreshToken,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);
}

public interface IRefreshTokenFamilyFingerprintProvider
{
    bool TryGetFingerprint(string refreshToken, out string fingerprint);
}

public static class AuthenticationLifecycleRetention
{
    public const int CleanupBatchSize = 100;
    public static readonly TimeSpan RefreshFamily = TimeSpan.FromDays(30);
    public static readonly TimeSpan ConsumedRefreshToken = TimeSpan.FromDays(30);
    public static readonly TimeSpan StepUpGrant = TimeSpan.FromDays(30);
}

public sealed record TokenPair(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

public sealed record ActiveMembershipContext(
    UserId UserId,
    MembershipId MembershipId,
    OrganizationId OrganizationId,
    string DisplayName,
    bool EligibleAsNamedParticipant,
    IReadOnlyCollection<OrganizationRole> Roles,
    string OrganizationName,
    string OrganizationTimeZone);

public sealed record InvitationAcceptanceContext(
    UserId UserId,
    MembershipId MembershipId,
    OrganizationId OrganizationId);
