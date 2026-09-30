using System.Security.Claims;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Api.Auth;

public sealed record TokenSetResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt)
{
    public static TokenSetResponse From(TokenPair tokens) =>
        new(
            tokens.AccessToken,
            tokens.AccessTokenExpiresAt,
            tokens.RefreshToken,
            tokens.RefreshTokenExpiresAt);
}

public sealed record StepUpResponse(
    string StepUpToken,
    DateTimeOffset ExpiresAt);

public sealed record MeResponse(
    MeMembershipResponse Membership,
    IReadOnlyCollection<string> Roles,
    MeOrganizationResponse Organization);

public sealed record MeMembershipResponse(
    string Id,
    string DisplayName,
    bool EligibleAsNamedParticipant);

public sealed record MeOrganizationResponse(
    string Id,
    string Name,
    string TimeZone);

internal static class AuthEndpointInputs
{
    public const string InstallationIdField = "installationId";
    public const string InstallationIdError = "A valid X-Installation-Id header is required.";

    public static Result<DeviceId> ParseInstallationId(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? value = request.Headers[AuthHeaders.InstallationId].FirstOrDefault();
        if (!DeviceId.TryParse(value, out DeviceId deviceId))
        {
            return Result.Failure<DeviceId>(
                AuthenticationErrorCodes.Validation(
                    AuthenticationErrorCodes.InvalidAuthInput,
                    InstallationIdError));
        }

        return Result.Success(deviceId);
    }
}

internal static class AuthClaimsPrincipalExtensions
{
    public static MeResponse ToMeResponse(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new MeResponse(
            new MeMembershipResponse(
                user.FindFirstValue(AuthClaimTypes.MembershipId),
                user.FindFirstValue(AuthClaimTypes.DisplayName),
                bool.TryParse(
                    user.FindFirstValue(AuthClaimTypes.EligibleAsNamedParticipant),
                    out bool eligible)
                    && eligible),
            user.FindAll(ClaimTypes.Role)
                .Select(claim => claim.Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            new MeOrganizationResponse(
                user.FindFirstValue(AuthClaimTypes.OrganizationId),
                user.FindFirstValue(AuthClaimTypes.OrganizationName),
                user.FindFirstValue(AuthClaimTypes.OrganizationTimeZone)));
    }

    private static string FindFirstValue(this ClaimsPrincipal user, string claimType) =>
        user.FindFirst(claimType)?.Value
        ?? throw new InvalidOperationException(
            $"The authenticated actor is missing the '{claimType}' claim.");
}
