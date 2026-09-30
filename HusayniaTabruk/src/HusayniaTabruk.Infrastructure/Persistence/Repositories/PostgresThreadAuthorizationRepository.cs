using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Signups;
using HusayniaTabruk.Domain.Threads;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace HusayniaTabruk.Infrastructure.Persistence.Repositories;

public sealed partial class PostgresThreadRepository
{
    public ValueTask<Result<ThreadOrdinaryAuthorizationContext>> LockOrdinaryAuthorizationAsync(
        UserId userId,
        MembershipId membershipId,
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        ThreadAuthorizationRequirement requirement,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => LockOrdinaryAuthorizationCoreAsync(
                userId,
                membershipId,
                organizationId,
                serviceDateId,
                requirement,
                cancellationToken));

    public ValueTask<Result<ThreadPrivilegedAuthorizationContext>> LockPrivilegedAuthorizationAsync(
        UserId userId,
        MembershipId membershipId,
        OrganizationId organizationId,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => LockPrivilegedAuthorizationCoreAsync(
                userId,
                membershipId,
                organizationId,
                cancellationToken));

    private async ValueTask<Result<ThreadOrdinaryAuthorizationContext>>
        LockOrdinaryAuthorizationCoreAsync(
            UserId userId,
            MembershipId membershipId,
            OrganizationId organizationId,
            ServiceDateId serviceDateId,
            ThreadAuthorizationRequirement requirement,
            CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(requirement))
        {
            throw new ArgumentOutOfRangeException(nameof(requirement));
        }

        List<ServiceDateEntity> lockedDates = await context.ServiceDates
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM service_dates
                WHERE id = {serviceDateId.Value}
                  AND organization_id = {organizationId.Value}
                  AND status <> {(short)ServiceDateStatus.Cancelled}
                FOR SHARE
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        ServiceDateEntity? lockedDate = lockedDates.SingleOrDefault();
        if (lockedDate is null)
        {
            return ConcealedOrdinary();
        }

        Result<LockedMembership> actor = await LockActiveMembershipAsync(
            userId,
            membershipId,
            organizationId,
            cancellationToken);
        if (actor.IsFailure)
        {
            return ConcealedOrdinary();
        }

        bool isManager = actor.Value.Membership.HasRole(OrganizationRole.FoodIncharge)
            && lockedDate.ManagerMembershipId == membershipId.Value;
        if (requirement == ThreadAuthorizationRequirement.ManagingFoodIncharge)
        {
            if (!isManager)
            {
                return ConcealedOrdinary();
            }
        }

        Result<HusayniaTabruk.Application.Dates.LoadedServiceDate> loadedDate =
            await new PostgresServiceDateRepository(context).GetAsync(
                organizationId,
                serviceDateId,
                cancellationToken);
        if (loadedDate.IsFailure)
        {
            return Result.Failure<ThreadOrdinaryAuthorizationContext>(loadedDate.Error);
        }

        if (isManager)
        {
            return Result.Success(
                new ThreadOrdinaryAuthorizationContext(
                    actor.Value.Membership,
                    loadedDate.Value.ServiceDate,
                    ApprovedPrimarySignup: null));
        }

        List<SignupEntity> approvedRows = await context.Signups
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM signups
                WHERE organization_id = {organizationId.Value}
                  AND service_date_id = {serviceDateId.Value}
                  AND primary_membership_id = {membershipId.Value}
                  AND status = {(short)SignupStatus.Approved}
                ORDER BY help_need_id
                FOR SHARE
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        SignupEntity? approved = approvedRows.FirstOrDefault();
        if (approved is null)
        {
            return ConcealedOrdinary();
        }

        Result<HelpNeedSignups> aggregate = await new PostgresSignupRepository(context).GetAsync(
            organizationId,
            HelpNeedId.From(approved.HelpNeedId),
            cancellationToken);
        if (aggregate.IsFailure)
        {
            return Result.Failure<ThreadOrdinaryAuthorizationContext>(aggregate.Error);
        }

        Signup? signup = aggregate.Value.Signups.SingleOrDefault(
            candidate => candidate.Id.Value == approved.Id
                && candidate.PrimaryMembershipId == membershipId
                && candidate.Status == SignupStatus.Approved);
        return signup is null
            ? ConcealedOrdinary()
            : Result.Success(
                new ThreadOrdinaryAuthorizationContext(
                    actor.Value.Membership,
                    loadedDate.Value.ServiceDate,
                    signup));
    }

    private async ValueTask<Result<ThreadPrivilegedAuthorizationContext>>
        LockPrivilegedAuthorizationCoreAsync(
            UserId userId,
            MembershipId membershipId,
            OrganizationId organizationId,
            CancellationToken cancellationToken)
    {
        Result<LockedMembership> actor = await LockActiveMembershipAsync(
            userId,
            membershipId,
            organizationId,
            cancellationToken);
        if (actor.IsFailure)
        {
            return Result.Failure<ThreadPrivilegedAuthorizationContext>(
                DomainError.Unauthorized(
                    "authentication_failed",
                    "Authentication failed."));
        }

        return actor.Value.Membership.HasRole(OrganizationRole.Admin)
            ? Result.Success(
                new ThreadPrivilegedAuthorizationContext(
                    userId,
                    membershipId,
                    organizationId))
            : Result.Failure<ThreadPrivilegedAuthorizationContext>(
                DomainError.Forbidden(
                    "forbidden",
                    "The current actor is not authorized for privileged thread access."));
    }

    private async ValueTask<Result<LockedMembership>> LockActiveMembershipAsync(
        UserId userId,
        MembershipId membershipId,
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        List<MembershipEntity> memberships = await context.Memberships
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM memberships
                WHERE id = {membershipId.Value}
                  AND organization_id = {organizationId.Value}
                  AND user_id = {userId.Value}
                  AND status = {(short)MembershipStatus.Active}
                FOR SHARE
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        MembershipEntity? row = memberships.SingleOrDefault();
        if (row is null)
        {
            return Result.Failure<LockedMembership>(ThreadErrorCodes.Concealed());
        }

        List<RoleAssignmentEntity> roleRows = await context.RoleAssignments
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM role_assignments
                WHERE organization_id = {organizationId.Value}
                  AND membership_id = {membershipId.Value}
                  AND revoked_at IS NULL
                ORDER BY role
                FOR SHARE
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        Result<Membership> membership = Membership.Rehydrate(
            membershipId,
            organizationId,
            userId,
            row.DisplayName,
            MembershipStatus.Active,
            row.EligibleAsNamedParticipant,
            roleRows
                .Select(role => (OrganizationRole)role.Role)
                .Distinct()
                .Order()
                .ToArray());
        return membership.IsFailure
            ? Result.Failure<LockedMembership>(membership.Error)
            : Result.Success(new LockedMembership(membership.Value));
    }

    private static Result<ThreadOrdinaryAuthorizationContext> ConcealedOrdinary() =>
        Result.Failure<ThreadOrdinaryAuthorizationContext>(
            ThreadErrorCodes.Concealed());

    private sealed record LockedMembership(Membership Membership);
}
