using HusayniaTabruk.Application.Admin.Members;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Notifications;
using HusayniaTabruk.Infrastructure.Identity.Entities;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HusayniaTabruk.Infrastructure.Identity.Services;

public sealed partial class PostgresAuthenticationMembershipRepository
{
    public ValueTask<Result<OrganizationAccountGovernance>> GetGovernanceAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default) =>
        new PostgresGovernanceRepository(context).GetAsync(organizationId, cancellationToken);

    public ValueTask<Result> SaveGovernanceAsync(
        OrganizationAccountGovernance aggregate,
        MembershipId actorMembershipId,
        DateTimeOffset occurredAt,
        MembershipAdministrationPersistenceEffects effects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(effects);

        return new PostgresGovernanceRepository(context).SaveAsync(
            aggregate,
            actorMembershipId,
            occurredAt,
            ToPersistenceEffects(effects),
            cancellationToken);
    }

    public ValueTask<Result> IssueInvitationAsync(
        IssueMembershipInvitationPersistenceRequest request,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(() => IssueInvitationCoreAsync(request, cancellationToken));

    private async ValueTask<Result> IssueInvitationCoreAsync(
        IssueMembershipInvitationPersistenceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string normalizedEmail = request.Email.Trim().ToUpperInvariant();
        try
        {
            return await PersistenceWriteSupport.ExecuteWriteAsync(
                context,
                async token =>
                {
                    OrganizationEntity? organization = await context.Organizations
                        .FromSqlInterpolated(
                            $"""
                            SELECT *
                            FROM organizations
                            WHERE id = {request.OrganizationId.Value}
                            FOR UPDATE
                            """)
                        .SingleOrDefaultAsync(token);
                    if (organization is null)
                    {
                        return Result.Failure(MemberAdministrationErrorCodes.StaleVersion());
                    }

                    bool organizationConflict = await HasOrganizationScopedInvitationConflictAsync(
                        request.OrganizationId,
                        normalizedEmail,
                        token);
                    if (organizationConflict)
                    {
                        return Result.Failure(MemberAdministrationErrorCodes.InvitationConflictError());
                    }

                    organization.Version = checked(organization.Version + 1);
                    string placeholderUserName = CreateInvitationPlaceholderUserName(request.MembershipId);
                    context.Users.Add(
                        new TabrukIdentityUser
                        {
                            Id = request.UserId.Value,
                            UserName = placeholderUserName,
                            NormalizedUserName = placeholderUserName.ToUpperInvariant(),
                            Email = request.Email,
                            NormalizedEmail = string.Empty,
                            SecurityStamp = Guid.NewGuid().ToString("N"),
                            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                        });
                    context.Memberships.Add(
                        new MembershipEntity
                        {
                            Id = request.MembershipId.Value,
                            OrganizationId = request.OrganizationId.Value,
                            UserId = request.UserId.Value,
                            DisplayName = request.PendingDisplayName,
                            Status = (short)MembershipStatus.Invited,
                            EligibleAsNamedParticipant = request.EligibleAsNamedParticipant,
                        });
                    context.Invitations.Add(
                        new InvitationEntity
                        {
                            Id = Guid.CreateVersion7(),
                            OrganizationId = request.OrganizationId.Value,
                            NormalizedEmail = normalizedEmail,
                            TokenHash = OpaqueTokenCrypto.ComputeSha256(request.InvitationToken),
                            ExpiresAt = request.ExpiresAt,
                            IssuedByMembershipId = request.IssuedByMembershipId.Value,
                            IssuedAt = request.IssuedAt,
                        });
                    PersistenceWriteSupport.AddEffects(context, ToPersistenceEffects(request.Effects));
                    return Result.Success();
                },
                cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return Result.Failure(MemberAdministrationErrorCodes.InvitationConflictError());
        }
    }

    private async Task<bool> HasOrganizationScopedInvitationConflictAsync(
        OrganizationId organizationId,
        string normalizedEmail,
        CancellationToken cancellationToken)
    {
        bool activeInvitationExists = await context.Invitations
            .AsNoTracking()
            .AnyAsync(
                candidate => candidate.OrganizationId == organizationId.Value
                    && candidate.NormalizedEmail == normalizedEmail
                    && candidate.AcceptedAt == null
                    && candidate.RevokedAt == null
                    && candidate.ExpiresAt > clock.UtcNow,
                cancellationToken);
        if (activeInvitationExists)
        {
            return true;
        }

        return await (
            from membership in context.Memberships.AsNoTracking()
            join user in context.Users.AsNoTracking()
                on membership.UserId equals user.Id
            where membership.OrganizationId == organizationId.Value
                && (user.NormalizedUserName == normalizedEmail
                    || user.NormalizedEmail == normalizedEmail)
            select membership.Id)
            .AnyAsync(cancellationToken);
    }

    private static string CreateInvitationPlaceholderUserName(MembershipId membershipId) =>
        $"invite-{membershipId.Value:N}";

    private static PersistenceEffects ToPersistenceEffects(
        MembershipAdministrationPersistenceEffects effects)
    {
        ArgumentNullException.ThrowIfNull(effects);

        return new PersistenceEffects(
            effects.Notifications.Select(ToEntity).ToArray(),
            effects.AuditEntries,
            [],
            effects.OutboxMessages);
    }

    private static NotificationEntity ToEntity(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return new NotificationEntity
        {
            Id = notification.Id.Value,
            OrganizationId = notification.OrganizationId.Value,
            RecipientMembershipId = notification.RecipientMembershipId.Value,
            Type = checked((short)notification.Type),
            ResourceType = checked((short)notification.ResourceType),
            ResourceId = notification.ResourceId,
            Title = notification.Title,
            Body = notification.Body,
            CreatedAt = notification.CreatedAt,
            ReadAt = notification.ReadAt,
        };
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres
                && string.Equals(postgres.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
