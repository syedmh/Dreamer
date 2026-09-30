using System.Linq.Expressions;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Accounts;
using HusayniaTabruk.Application.Admin.Members;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Infrastructure.Identity.Entities;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace HusayniaTabruk.Infrastructure.Identity.Services;

public sealed partial class PostgresAuthenticationMembershipRepository(
    TabrukDbContext context,
    IClock clock)
    : IMembershipRepository
{
    public ValueTask<Result<InvitationAcceptanceContext>> GetInvitationAcceptanceContextAsync(
        string invitationToken,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => GetInvitationAcceptanceContextCoreAsync(invitationToken, cancellationToken));

    public ValueTask<Result> AcceptInvitationAsync(
        string invitationToken,
        MembershipId membershipId,
        string displayName,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => AcceptInvitationCoreAsync(
                invitationToken,
                membershipId,
                displayName,
                acceptedAt,
                cancellationToken));

    public ValueTask<Result<ActiveMembershipContext>> GetActiveMembershipAsync(
        UserId userId,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => GetActiveMembershipCoreAsync(userId, cancellationToken));

    public ValueTask<Result<ActiveMembershipContext>> ResolveActiveActorAsync(
        UserId userId,
        MembershipId membershipId,
        OrganizationId organizationId,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => ResolveActiveActorCoreAsync(userId, membershipId, organizationId, cancellationToken));

    private async ValueTask<Result<InvitationAcceptanceContext>> GetInvitationAcceptanceContextCoreAsync(
        string invitationToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(invitationToken))
        {
            return Result.Failure<InvitationAcceptanceContext>(InvitationInvalid());
        }

        string tokenHash = OpaqueTokenCrypto.ComputeSha256(invitationToken.Trim());
        DateTimeOffset now = clock.UtcNow;

        InvitationEntity? invitation = await context.Invitations
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM invitations
                WHERE token_hash = {tokenHash}
                FOR UPDATE
                """)
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (invitation is null
            || invitation.AcceptedAt is not null
            || invitation.RevokedAt is not null
            || invitation.ExpiresAt <= now)
        {
            return Result.Failure<InvitationAcceptanceContext>(InvitationInvalid());
        }

        return InvitationTokenCodec.TryParseMembershipId(invitationToken, out MembershipId parsedMembershipId)
            ? await GetInvitationAcceptanceContextByMembershipAsync(
                invitation,
                parsedMembershipId,
                cancellationToken)
            : await GetLegacyInvitationAcceptanceContextAsync(invitation, cancellationToken);
    }

    private ValueTask<Result> AcceptInvitationCoreAsync(
        string invitationToken,
        MembershipId membershipId,
        string displayName,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(invitationToken)
            || string.IsNullOrWhiteSpace(displayName)
            || acceptedAt.Offset != TimeSpan.Zero)
        {
            return ValueTask.FromResult(Result.Failure(
                AuthenticationErrorCodes.Validation(
                    AuthenticationErrorCodes.InvalidAuthInput,
                    "The invitation request is invalid.")));
        }

        string trimmedDisplayName = displayName.Trim();
        if (trimmedDisplayName.EnumerateRunes().Count() > 200)
        {
            return ValueTask.FromResult(Result.Failure(
                AuthenticationErrorCodes.Validation(
                    AuthenticationErrorCodes.InvalidAuthInput,
                    "The invitation request is invalid.")));
        }

        string tokenHash = OpaqueTokenCrypto.ComputeSha256(invitationToken.Trim());
        return PersistenceWriteSupport.ExecuteWriteAsync(
            context,
            async token =>
            {
                InvitationEntity? invitation = await context.Invitations
                    .FromSqlInterpolated(
                        $"""
                        SELECT *
                        FROM invitations
                        WHERE token_hash = {tokenHash}
                        FOR UPDATE
                        """)
                    .SingleOrDefaultAsync(token);
                if (invitation is null
                    || invitation.AcceptedAt is not null
                    || invitation.RevokedAt is not null
                    || invitation.ExpiresAt <= acceptedAt)
                {
                    return Result.Failure(InvitationInvalid());
                }

                if (InvitationTokenCodec.TryParseMembershipId(invitationToken, out MembershipId tokenMembershipId)
                    && tokenMembershipId != membershipId)
                {
                    return Result.Failure(InvitationInvalid());
                }

                MembershipEntity? membership = await context.Memberships
                    .SingleOrDefaultAsync(
                        candidate => candidate.Id == membershipId.Value
                            && candidate.OrganizationId == invitation.OrganizationId
                            && candidate.Status == (short)MembershipStatus.Invited,
                        token);
                if (membership is null)
                {
                    return Result.Failure(InvitationInvalid());
                }

                TabrukIdentityUser? user = await context.Users
                    .SingleOrDefaultAsync(candidate => candidate.Id == membership.UserId, token);
                if (user is null || string.IsNullOrWhiteSpace(user.PasswordHash))
                {
                    return Result.Failure(InvitationInvalid());
                }

                bool loginConflict = await context.Users
                    .AsNoTracking()
                    .AnyAsync(
                        candidate => candidate.Id != user.Id
                            && (candidate.NormalizedUserName == invitation.NormalizedEmail
                                || candidate.NormalizedEmail == invitation.NormalizedEmail),
                        token);
                if (loginConflict)
                {
                    return Result.Failure(InvitationInvalid());
                }

                invitation.AcceptedAt = acceptedAt;
                membership.Status = (short)MembershipStatus.Active;
                membership.DisplayName = trimmedDisplayName;
                FinalizeInvitedUser(user, invitation.NormalizedEmail);
                return Result.Success();
            },
            cancellationToken);
    }

    private async Task<Result<InvitationAcceptanceContext>> GetInvitationAcceptanceContextByMembershipAsync(
        InvitationEntity invitation,
        MembershipId membershipId,
        CancellationToken cancellationToken)
    {
        InvitationAcceptanceContext? match = await (
            from membership in context.Memberships.AsNoTracking()
            join user in context.Users.AsNoTracking()
                on membership.UserId equals user.Id
            where membership.OrganizationId == invitation.OrganizationId
                && membership.Id == membershipId.Value
                && membership.Status == (short)MembershipStatus.Invited
            select new InvitationAcceptanceContext(
                UserId.From(user.Id),
                MembershipId.From(membership.Id),
                OrganizationId.From(membership.OrganizationId)))
            .SingleOrDefaultAsync(cancellationToken);

        return match is not null
            ? Result.Success(match)
            : Result.Failure<InvitationAcceptanceContext>(InvitationInvalid());
    }

    private async Task<Result<InvitationAcceptanceContext>> GetLegacyInvitationAcceptanceContextAsync(
        InvitationEntity invitation,
        CancellationToken cancellationToken)
    {
        InvitationAcceptanceContext[] matches = await
            (from user in context.Users.AsNoTracking()
             join membership in context.Memberships.AsNoTracking()
                 on new { invitation.OrganizationId, UserId = user.Id }
                 equals new { membership.OrganizationId, membership.UserId }
             where user.NormalizedEmail == invitation.NormalizedEmail
                   && membership.Status == (short)MembershipStatus.Invited
             select new InvitationAcceptanceContext(
                 UserId.From(user.Id),
                 MembershipId.From(membership.Id),
                 OrganizationId.From(membership.OrganizationId)))
            .Take(2)
            .ToArrayAsync(cancellationToken);

        return matches.Length == 1
            ? Result.Success(matches[0])
            : Result.Failure<InvitationAcceptanceContext>(InvitationInvalid());
    }

    private static void FinalizeInvitedUser(TabrukIdentityUser user, string normalizedEmail)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedEmail);

        string login = string.IsNullOrWhiteSpace(user.Email)
            ? normalizedEmail
            : user.Email.Trim();
        user.UserName = login;
        user.NormalizedUserName = normalizedEmail;
        user.NormalizedEmail = normalizedEmail;
    }

    private async ValueTask<Result<ActiveMembershipContext>> GetActiveMembershipCoreAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        ActiveMembershipContext[] memberships = await QueryActiveMembershipsAsync(
            candidate => candidate.UserId == userId.Value,
            cancellationToken);

        return memberships.Length == 1
            ? Result.Success(memberships[0])
            : Result.Failure<ActiveMembershipContext>(AuthenticationFailed());
    }

    private async ValueTask<Result<ActiveMembershipContext>> ResolveActiveActorCoreAsync(
        UserId userId,
        MembershipId membershipId,
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        ActiveMembershipContext[] memberships = await QueryActiveMembershipsAsync(
            candidate => candidate.Id == membershipId.Value
                && candidate.UserId == userId.Value
                && candidate.OrganizationId == organizationId.Value,
            cancellationToken);

        return memberships.Length == 1
            ? Result.Success(memberships[0])
            : Result.Failure<ActiveMembershipContext>(AuthenticationFailed());
    }

    private async Task<ActiveMembershipContext[]> QueryActiveMembershipsAsync(
        Expression<Func<MembershipEntity, bool>> predicate,
        CancellationToken cancellationToken)
    {
        var memberships = await (
            from membership in context.Memberships.AsNoTracking().Where(predicate)
            join organization in context.Organizations.AsNoTracking()
                on membership.OrganizationId equals organization.Id
            where membership.Status == (short)MembershipStatus.Active
            select new
            {
                membership.Id,
                membership.OrganizationId,
                membership.UserId,
                membership.DisplayName,
                membership.EligibleAsNamedParticipant,
                organization.Name,
                organization.TimeZone,
            })
            .Take(2)
            .ToArrayAsync(cancellationToken);
        if (memberships.Length == 0)
        {
            return [];
        }

        Guid[] membershipIds = memberships.Select(candidate => candidate.Id).ToArray();
        var roles = await context.RoleAssignments
            .AsNoTracking()
            .Where(candidate => membershipIds.Contains(candidate.MembershipId)
                && candidate.RevokedAt == null)
            .OrderBy(candidate => candidate.Role)
            .ToArrayAsync(cancellationToken);

        return memberships.Select(
                membership => new ActiveMembershipContext(
                    UserId.From(membership.UserId),
                    MembershipId.From(membership.Id),
                    OrganizationId.From(membership.OrganizationId),
                    membership.DisplayName,
                    membership.EligibleAsNamedParticipant,
                    roles.Where(candidate => candidate.MembershipId == membership.Id)
                        .Select(candidate => (OrganizationRole)candidate.Role)
                        .Distinct()
                        .Order()
                        .ToArray(),
                    membership.Name,
                    membership.TimeZone))
            .ToArray();
    }

    private static HusayniaTabruk.Domain.Common.Errors.DomainError InvitationInvalid() =>
        AuthenticationErrorCodes.Unauthorized(
            AuthenticationErrorCodes.InvitationInvalid,
            "The invitation is invalid or has expired.");

    private static HusayniaTabruk.Domain.Common.Errors.DomainError AuthenticationFailed() =>
        AuthenticationErrorCodes.Unauthorized(
            AuthenticationErrorCodes.AuthenticationFailed,
            "Authentication failed.");
}
