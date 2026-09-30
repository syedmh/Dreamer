using System.Data;
using System.Text;
using System.Text.Json;
using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Roster;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Application.Signups.Submit;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;
using HusayniaTabruk.Domain.Notifications;
using HusayniaTabruk.Domain.Signups;
using HusayniaTabruk.Domain.Threads;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HusayniaTabruk.Infrastructure.Persistence.Repositories;

public sealed record NewSignupLabel(SignupId SignupId, string? Label);

public sealed class PostgresSignupRepository(TabrukDbContext context) : ISignupRepository
{
    private const int AmbientReadAttempts = 3;

    public ValueTask<Result<HelpNeedSignups>> GetAsync(
        OrganizationId organizationId,
        HelpNeedId helpNeedId,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => GetCoreAsync(organizationId, helpNeedId, cancellationToken));

    public ValueTask<Result<SignupSubmissionContext>> GetSubmissionContextAsync(
        OrganizationId organizationId,
        HelpNeedId helpNeedId,
        MembershipId primaryMembershipId,
        IReadOnlyCollection<MembershipId> memberParticipantIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memberParticipantIds);
        return PostgresDependencyFailure.ExecuteAsync(
            () => GetSubmissionContextCoreAsync(
                organizationId,
                helpNeedId,
                primaryMembershipId,
                memberParticipantIds,
                cancellationToken));
    }

    public ValueTask<Result<SignupDecisionContext>> GetDecisionContextAsync(
        OrganizationId organizationId,
        MembershipId actorMembershipId,
        SignupId signupId,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => GetDecisionContextCoreAsync(
                organizationId,
                actorMembershipId,
                signupId,
                cancellationToken));

    public ValueTask<Result<SignupCancellationContext>> GetOwnedCancellationContextAsync(
        OrganizationId organizationId,
        MembershipId actorMembershipId,
        SignupId signupId,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => GetOwnedCancellationContextCoreAsync(
                organizationId,
                actorMembershipId,
                signupId,
                cancellationToken));

    public ValueTask<Result<SignupCancellationContext>> GetManagedCancellationContextAsync(
        OrganizationId organizationId,
        MembershipId actorMembershipId,
        SignupId signupId,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => GetManagedCancellationContextCoreAsync(
                organizationId,
                actorMembershipId,
                signupId,
                cancellationToken));

    public ValueTask<Result<SignupReassignmentContext>> GetReassignmentContextAsync(
        OrganizationId organizationId,
        MembershipId actorMembershipId,
        HelpNeedId helpNeedId,
        SignupId signupId,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => GetReassignmentContextCoreAsync(
                organizationId,
                actorMembershipId,
                helpNeedId,
                signupId,
                cancellationToken));

    private async ValueTask<Result<HelpNeedSignups>> GetCoreAsync(
        OrganizationId organizationId,
        HelpNeedId helpNeedId,
        CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is not null)
        {
            return await ReadWithinAmbientTransactionAsync(
                organizationId,
                helpNeedId,
                cancellationToken);
        }

        IDbContextTransaction? transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        Exception? primaryException = null;
        bool rollbackOwnedTransaction = false;
        try
        {
            await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
            Result<HelpNeedSignups> result = await ReadSignupsAsync(
                organizationId,
                helpNeedId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception exception)
        {
            primaryException = exception;
            rollbackOwnedTransaction = true;
            throw;
        }
        finally
        {
            await PostgresOwnedTransactionCleanup.CleanupAsync(
                context,
                transaction,
                rollbackOwnedTransaction,
                clearChangeTracker: false,
                primaryException);
        }
    }

    private async ValueTask<Result<HelpNeedSignups>> ReadWithinAmbientTransactionAsync(
        OrganizationId organizationId,
        HelpNeedId helpNeedId,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < AmbientReadAttempts; attempt++)
        {
            SignupHydrationRows? rows = await ReadSignupRowsAsync(
                organizationId,
                helpNeedId,
                cancellationToken);
            if (rows is null)
            {
                return HelpNeedNotFound();
            }

            HelpNeedEntity? current = await context.HelpNeeds
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == helpNeedId.Value
                        && candidate.OrganizationId == organizationId.Value,
                    cancellationToken);
            if (current is null)
            {
                return HelpNeedNotFound();
            }

            if (current.SignupVersion == rows.Need.SignupVersion)
            {
                return HydrateSignups(rows);
            }
        }

        return Result.Failure<HelpNeedSignups>(
            DomainError.Conflict(
                "signup_read_conflict",
                "The signup aggregate changed while it was being read. Please retry."));
    }

    private async ValueTask<Result<HelpNeedSignups>> ReadSignupsAsync(
        OrganizationId organizationId,
        HelpNeedId helpNeedId,
        CancellationToken cancellationToken)
    {
        SignupHydrationRows? rows = await ReadSignupRowsAsync(
            organizationId,
            helpNeedId,
            cancellationToken);
        return rows is null ? HelpNeedNotFound() : HydrateSignups(rows);
    }

    private async ValueTask<SignupHydrationRows?> ReadSignupRowsAsync(
        OrganizationId organizationId,
        HelpNeedId helpNeedId,
        CancellationToken cancellationToken)
    {
        HelpNeedEntity? need = await context.HelpNeeds
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == helpNeedId.Value
                    && candidate.OrganizationId == organizationId.Value,
                cancellationToken);
        if (need is null)
        {
            return null;
        }

        ServiceDateEntity? date = await context.ServiceDates
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == need.ServiceDateId
                    && candidate.OrganizationId == need.OrganizationId,
                cancellationToken);
        if (date is null)
        {
            return new SignupHydrationRows(need, null, [], [], []);
        }

        List<HelpNeedEntity> dateNeeds = await context.HelpNeeds
            .AsNoTracking()
            .Where(candidate => candidate.OrganizationId == date.OrganizationId && candidate.ServiceDateId == date.Id)
            .OrderBy(candidate => candidate.Id)
            .ToListAsync(cancellationToken);
        List<SignupEntity> signupRows = await context.Signups
            .AsNoTracking()
            .Where(candidate => candidate.OrganizationId == need.OrganizationId && candidate.HelpNeedId == need.Id)
            .OrderBy(candidate => candidate.Id)
            .ToListAsync(cancellationToken);
        Guid[] signupIds = signupRows.Select(candidate => candidate.Id).ToArray();
        List<SignupMemberParticipantEntity> participantRows = await context.SignupMemberParticipants
            .AsNoTracking()
            .Where(candidate => signupIds.Contains(candidate.SignupId))
            .ToListAsync(cancellationToken);

        return new SignupHydrationRows(need, date, dateNeeds, signupRows, participantRows);
    }

    private static Result<HelpNeedSignups> HydrateSignups(SignupHydrationRows rows)
    {
        if (rows.Date is null)
        {
            return InvalidSignupState("The help need has no canonical service date.");
        }

        List<HelpNeed> hydratedDateNeeds = [];
        foreach (HelpNeedEntity dateNeed in rows.DateNeeds)
        {
            Result<HelpNeed> hydratedNeed = HydrateHelpNeed(dateNeed);
            if (hydratedNeed.IsFailure)
            {
                return Result.Failure<HelpNeedSignups>(hydratedNeed.Error);
            }

            hydratedDateNeeds.Add(hydratedNeed.Value);
        }

        Result<ServiceDate> serviceDate = HydrateServiceDate(rows.Date, hydratedDateNeeds);
        if (serviceDate.IsFailure)
        {
            return Result.Failure<HelpNeedSignups>(serviceDate.Error);
        }

        HelpNeed? canonicalNeed = serviceDate.Value.HelpNeeds.SingleOrDefault(
            candidate => candidate.Id.Value == rows.Need.Id);
        if (canonicalNeed is null)
        {
            return InvalidSignupState("The canonical help need is missing from its service date.");
        }

        List<Signup> signups = [];
        foreach (SignupEntity signupRow in rows.Signups)
        {
            Result<Signup> signup = HydrateSignup(
                signupRow,
                rows.Participants.Where(candidate => candidate.SignupId == signupRow.Id));
            if (signup.IsFailure)
            {
                return Result.Failure<HelpNeedSignups>(signup.Error);
            }

            signups.Add(signup.Value);
        }

        return HelpNeedSignups.PersistenceFactory.Rehydrate(
            serviceDate.Value,
            canonicalNeed,
            rows.Need.SignupVersion,
            rows.Need.WaitlistOrderHighWater,
            signups);
    }

    private async ValueTask<Result<SignupSubmissionContext>> GetSubmissionContextCoreAsync(
        OrganizationId organizationId,
        HelpNeedId helpNeedId,
        MembershipId primaryMembershipId,
        IReadOnlyCollection<MembershipId> memberParticipantIds,
        CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Signup submission context must be loaded inside the submission unit of work.");
        }

        Result<HelpNeedSignups> aggregate = await GetCoreAsync(
            organizationId,
            helpNeedId,
            cancellationToken);
        if (aggregate.IsFailure)
        {
            return Result.Failure<SignupSubmissionContext>(aggregate.Error);
        }

        var canonical = await (
            from need in context.HelpNeeds.AsNoTracking()
            join date in context.ServiceDates.AsNoTracking()
                on new { need.OrganizationId, Id = need.ServiceDateId }
                equals new { date.OrganizationId, date.Id }
            where need.OrganizationId == organizationId.Value
                && need.Id == helpNeedId.Value
                && date.Id == aggregate.Value.ServiceDateId.Value
            select new
            {
                need.Category,
                date.ManagerMembershipId,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (canonical is null)
        {
            return Result.Failure<SignupSubmissionContext>(
                InvalidSignupState<HelpNeedSignups>(
                    "The canonical help need and service date could not be resolved.").Error);
        }

        Guid[] lockedMembershipIds = memberParticipantIds
            .Select(id => id.Value)
            .Append(primaryMembershipId.Value)
            .Append(canonical.ManagerMembershipId)
            .Distinct()
            .ToArray();
        List<MembershipEntity> memberships = await context.Memberships
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM memberships
                WHERE organization_id = {organizationId.Value}
                  AND id = ANY ({lockedMembershipIds})
                FOR SHARE
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        MembershipEntity? primaryRow = memberships.SingleOrDefault(
            candidate => candidate.Id == primaryMembershipId.Value);
        MembershipEntity? managerRow = memberships.SingleOrDefault(
            candidate => candidate.Id == canonical.ManagerMembershipId);
        Dictionary<Guid, MembershipEntity> participantsById = memberships
            .Where(candidate => memberParticipantIds.Any(id => id.Value == candidate.Id))
            .ToDictionary(candidate => candidate.Id);
        if (primaryRow is null
            || primaryRow.Status != (short)MembershipStatus.Active
            || managerRow is null
            || managerRow.Status != (short)MembershipStatus.Active
            || memberParticipantIds.Any(id => !participantsById.ContainsKey(id.Value))
            || participantsById.Values.Any(
                candidate => candidate.Status != (short)MembershipStatus.Active
                    || !candidate.EligibleAsNamedParticipant))
        {
            return Result.Failure<SignupSubmissionContext>(
                DomainError.Conflict(
                    SignupErrorCodes.IneligibleParticipant,
                    "The primary contact, manager, and referenced participants must be active and eligible."));
        }

        List<RoleAssignmentEntity> managerRoles = await context.RoleAssignments
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM role_assignments
                WHERE organization_id = {organizationId.Value}
                  AND membership_id = {canonical.ManagerMembershipId}
                  AND role = {(short)OrganizationRole.FoodIncharge}
                  AND revoked_at IS NULL
                FOR SHARE
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        if (managerRoles.Count != 1)
        {
            return Result.Failure<SignupSubmissionContext>(CategoryClosed().Error);
        }

        Result<Membership> primary = HydrateActiveMembership(primaryRow);
        if (primary.IsFailure)
        {
            return Result.Failure<SignupSubmissionContext>(primary.Error);
        }

        List<Membership> participants = [];
        foreach (MembershipId participantId in memberParticipantIds)
        {
            Result<Membership> participant = HydrateActiveMembership(
                participantsById[participantId.Value]);
            if (participant.IsFailure)
            {
                return Result.Failure<SignupSubmissionContext>(participant.Error);
            }

            participants.Add(participant.Value);
        }

        return Result.Success(
            new SignupSubmissionContext(
                aggregate.Value,
                primary.Value,
                participants,
                MembershipId.From(canonical.ManagerMembershipId),
                (HelpCategory)canonical.Category));
    }

    private async ValueTask<Result<SignupDecisionContext>> GetDecisionContextCoreAsync(
        OrganizationId organizationId,
        MembershipId actorMembershipId,
        SignupId signupId,
        CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Signup decision context must be loaded inside the decision unit of work.");
        }

        SignupEntity? targetRow = await context.Signups
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.OrganizationId == organizationId.Value
                    && candidate.Id == signupId.Value,
                cancellationToken);
        if (targetRow is null)
        {
            return SignupNotFound<SignupDecisionContext>();
        }

        Result<HelpNeedSignups> aggregate = await GetCoreAsync(
            organizationId,
            HelpNeedId.From(targetRow.HelpNeedId),
            cancellationToken);
        if (aggregate.IsFailure)
        {
            return aggregate.Error.Code == SignupApplicationErrorCodes.HelpNeedNotFound
                ? SignupNotFound<SignupDecisionContext>()
                : Result.Failure<SignupDecisionContext>(aggregate.Error);
        }

        Signup? target = aggregate.Value.Signups.SingleOrDefault(
            candidate => candidate.Id == signupId);
        if (target is null)
        {
            return SignupNotFound<SignupDecisionContext>();
        }

        int authorityCount = await (
            from date in context.ServiceDates.AsNoTracking()
            join membership in context.Memberships.AsNoTracking()
                on new { date.OrganizationId, Id = date.ManagerMembershipId }
                equals new { membership.OrganizationId, membership.Id }
            join role in context.RoleAssignments.AsNoTracking()
                on new
                {
                    membership.OrganizationId,
                    MembershipId = membership.Id,
                    Role = (short)OrganizationRole.FoodIncharge,
                }
                equals new
                {
                    role.OrganizationId,
                    role.MembershipId,
                    role.Role,
                }
            where date.OrganizationId == organizationId.Value
                && date.Id == targetRow.ServiceDateId
                && date.ManagerMembershipId == actorMembershipId.Value
                && membership.Id == actorMembershipId.Value
                && membership.Status == (short)MembershipStatus.Active
                && role.RevokedAt == null
            select role.Id)
            .CountAsync(cancellationToken);
        if (authorityCount != 1)
        {
            return SignupNotFound<SignupDecisionContext>();
        }

        Result<SignupSummary> summary = await BuildDecisionSummaryAsync(
            aggregate.Value,
            target,
            targetRow,
            cancellationToken);
        return summary.IsSuccess
            ? Result.Success(new SignupDecisionContext(aggregate.Value, summary.Value))
            : Result.Failure<SignupDecisionContext>(summary.Error);
    }

    private async ValueTask<Result<SignupCancellationContext>>
        GetOwnedCancellationContextCoreAsync(
            OrganizationId organizationId,
            MembershipId actorMembershipId,
            SignupId signupId,
            CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Signup cancellation context must be loaded inside the cancellation unit of work.");
        }

        SignupEntity? targetRow = await context.Signups
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.OrganizationId == organizationId.Value
                    && candidate.Id == signupId.Value
                    && candidate.PrimaryMembershipId == actorMembershipId.Value,
                cancellationToken);
        if (targetRow is null)
        {
            return SignupNotFound<SignupCancellationContext>();
        }

        Result<HelpNeedSignups> aggregate = await GetCoreAsync(
            organizationId,
            HelpNeedId.From(targetRow.HelpNeedId),
            cancellationToken);
        if (aggregate.IsFailure)
        {
            return aggregate.Error.Code == SignupApplicationErrorCodes.HelpNeedNotFound
                ? SignupNotFound<SignupCancellationContext>()
                : Result.Failure<SignupCancellationContext>(aggregate.Error);
        }

        Signup? target = aggregate.Value.Signups.SingleOrDefault(
            candidate => candidate.Id == signupId
                && candidate.PrimaryMembershipId == actorMembershipId);
        if (target is null)
        {
            return SignupNotFound<SignupCancellationContext>();
        }

        Result<SignupSummary> summary = await BuildDecisionSummaryAsync(
            aggregate.Value,
            target,
            targetRow,
            cancellationToken);
        return summary.IsSuccess
            ? Result.Success(
                new SignupCancellationContext(aggregate.Value, summary.Value))
            : Result.Failure<SignupCancellationContext>(summary.Error);
    }

    private async ValueTask<Result<SignupCancellationContext>>
        GetManagedCancellationContextCoreAsync(
            OrganizationId organizationId,
            MembershipId actorMembershipId,
            SignupId signupId,
            CancellationToken cancellationToken)
    {
        Result<SignupDecisionContext> contextResult =
            await GetDecisionContextCoreAsync(
                organizationId,
                actorMembershipId,
                signupId,
                cancellationToken);
        return contextResult.IsSuccess
            ? Result.Success(
                new SignupCancellationContext(
                    contextResult.Value.Aggregate,
                    contextResult.Value.Signup))
            : Result.Failure<SignupCancellationContext>(contextResult.Error);
    }

    private async ValueTask<Result<SignupReassignmentContext>>
        GetReassignmentContextCoreAsync(
            OrganizationId organizationId,
            MembershipId actorMembershipId,
            HelpNeedId helpNeedId,
            SignupId signupId,
            CancellationToken cancellationToken)
    {
        Result<SignupDecisionContext> contextResult =
            await GetDecisionContextCoreAsync(
                organizationId,
                actorMembershipId,
                signupId,
                cancellationToken);
        if (contextResult.IsFailure
            || contextResult.Value.Aggregate.HelpNeedId != helpNeedId)
        {
            return contextResult.IsFailure
                ? Result.Failure<SignupReassignmentContext>(contextResult.Error)
                : SignupNotFound<SignupReassignmentContext>();
        }

        return Result.Success(
            new SignupReassignmentContext(
                contextResult.Value.Aggregate,
                contextResult.Value.Signup));
    }

    private async ValueTask<Result<SignupSummary>> BuildDecisionSummaryAsync(
        HelpNeedSignups aggregate,
        Signup signup,
        SignupEntity row,
        CancellationToken cancellationToken)
    {
        List<SignupMemberParticipantEntity> participantRows =
            await context.SignupMemberParticipants
                .AsNoTracking()
                .Where(candidate => candidate.SignupId == row.Id)
                .ToListAsync(cancellationToken);
        Guid[] membershipIds = participantRows
            .Select(candidate => candidate.MembershipId)
            .Append(row.PrimaryMembershipId)
            .Distinct()
            .ToArray();
        Dictionary<Guid, MembershipEntity> memberships = await context.Memberships
            .AsNoTracking()
            .Where(candidate => membershipIds.Contains(candidate.Id))
            .ToDictionaryAsync(candidate => candidate.Id, cancellationToken);
        HelpNeedEntity? need = await context.HelpNeeds
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.OrganizationId == aggregate.OrganizationId.Value
                    && candidate.Id == aggregate.HelpNeedId.Value,
                cancellationToken);
        if (need is null
            || !memberships.TryGetValue(row.PrimaryMembershipId, out MembershipEntity? primary))
        {
            return Result.Failure<SignupSummary>(
                InvalidSignupState<HelpNeedSignups>(
                    "The signup decision projection references missing canonical records.").Error);
        }

        List<SignupParticipantSummary> participants = [];
        foreach (SignupMemberParticipantEntity participantRow in participantRows)
        {
            if (!memberships.TryGetValue(
                    participantRow.MembershipId,
                    out MembershipEntity? participant))
            {
                return Result.Failure<SignupSummary>(
                    InvalidSignupState<HelpNeedSignups>(
                        "The signup decision projection references a missing participant.").Error);
            }

            participants.Add(
                new SignupParticipantSummary(
                    MembershipId.From(participant.Id),
                    participant.DisplayName));
        }

        participants = participants
            .OrderBy(participant => participant.DisplayName, StringComparer.Ordinal)
            .ThenBy(participant => participant.MembershipId.Value)
            .ToList();
        return Result.Success(
            new SignupSummary(
                signup.Id,
                signup.ServiceDateId,
                signup.HelpNeedId,
                (HelpCategory)need.Category,
                new SignupParticipantSummary(
                    MembershipId.From(primary.Id),
                    primary.DisplayName),
                signup.Kind,
                row.Label,
                participants,
                signup.UnnamedParticipantCount,
                signup.TotalParticipantCount,
                signup.Status,
                signup.SubmittedAt,
                signup.LastTransitionAt,
                signup.WaitlistOrder,
                signup.Version,
                aggregate.Version));
    }

    public ValueTask<Result<SignupSummary>> GetOwnedAsync(
        OrganizationId organizationId,
        MembershipId primaryMembershipId,
        SignupId signupId,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => GetOwnedCoreAsync(
                organizationId,
                primaryMembershipId,
                signupId,
                cancellationToken));

    public ValueTask<Result<SignupPage>> ListMineAsync(
        OrganizationId organizationId,
        MembershipId primaryMembershipId,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => ListMineCoreAsync(
                organizationId,
                primaryMembershipId,
                cursor,
                pageSize,
                cancellationToken));

    public ValueTask<Result<RosterPage>> GetManagedRosterAsync(
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        MembershipId managerMembershipId,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => GetManagedRosterCoreAsync(
                organizationId,
                serviceDateId,
                managerMembershipId,
                cursor,
                pageSize,
                cancellationToken));

    private async ValueTask<Result<SignupSummary>> GetOwnedCoreAsync(
        OrganizationId organizationId,
        MembershipId primaryMembershipId,
        SignupId signupId,
        CancellationToken cancellationToken)
    {
        SignupEntity? row = await context.Signups
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.OrganizationId == organizationId.Value
                    && candidate.PrimaryMembershipId == primaryMembershipId.Value
                    && candidate.Id == signupId.Value,
                cancellationToken);
        if (row is null)
        {
            return Result.Failure<SignupSummary>(
                DomainError.NotFound(
                    SignupApplicationErrorCodes.SignupNotFound,
                    "The signup was not found."));
        }

        Result<IReadOnlyList<SignupSummary>> summaries =
            await BuildSummariesAsync([row], cancellationToken);
        return summaries.IsSuccess
            ? Result.Success(summaries.Value[0])
            : Result.Failure<SignupSummary>(summaries.Error);
    }

    private async ValueTask<Result<SignupPage>> ListMineCoreAsync(
        OrganizationId organizationId,
        MembershipId primaryMembershipId,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        Result<int> start = DecodeCursor(cursor);
        if (start.IsFailure)
        {
            return Result.Failure<SignupPage>(start.Error);
        }

        Result<int> takeResult = NormalizePageSize(pageSize);
        if (takeResult.IsFailure)
        {
            return Result.Failure<SignupPage>(takeResult.Error);
        }

        int take = takeResult.Value;
        List<SignupEntity> rows = await context.Signups
            .AsNoTracking()
            .Where(
                candidate => candidate.OrganizationId == organizationId.Value
                    && candidate.PrimaryMembershipId == primaryMembershipId.Value)
            .OrderByDescending(candidate => candidate.SubmittedAt)
            .ThenByDescending(candidate => candidate.Id)
            .Skip(start.Value)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        bool hasMore = rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        Result<IReadOnlyList<SignupSummary>> summaries =
            await BuildSummariesAsync(rows, cancellationToken);
        return summaries.IsSuccess
            ? Result.Success(
                new SignupPage(
                    summaries.Value,
                    hasMore ? EncodeCursor(start.Value + rows.Count) : null))
            : Result.Failure<SignupPage>(summaries.Error);
    }

    private async ValueTask<Result<RosterPage>> GetManagedRosterCoreAsync(
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        MembershipId managerMembershipId,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is not null)
        {
            return await GetManagedRosterWithinTransactionAsync(
                organizationId,
                serviceDateId,
                managerMembershipId,
                cursor,
                pageSize,
                cancellationToken);
        }

        IDbContextTransaction? transaction =
            await context.Database.BeginTransactionAsync(cancellationToken);
        Exception? primaryException = null;
        bool rollbackOwnedTransaction = false;
        try
        {
            Result<RosterPage> result =
                await GetManagedRosterWithinTransactionAsync(
                    organizationId,
                    serviceDateId,
                    managerMembershipId,
                    cursor,
                    pageSize,
                    cancellationToken);
            if (result.IsFailure)
            {
                rollbackOwnedTransaction = true;
                return result;
            }

            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception exception)
        {
            primaryException = exception;
            rollbackOwnedTransaction = true;
            throw;
        }
        finally
        {
            await PostgresOwnedTransactionCleanup.CleanupAsync(
                context,
                transaction,
                rollbackOwnedTransaction,
                clearChangeTracker: false,
                primaryException);
        }
    }

    private async ValueTask<Result<RosterPage>> GetManagedRosterWithinTransactionAsync(
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        MembershipId managerMembershipId,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        List<RoleAssignmentEntity> authority = await context.RoleAssignments
            .FromSqlInterpolated(
                $"""
                SELECT role_assignment.*
                FROM role_assignments AS role_assignment
                INNER JOIN memberships AS membership
                    ON membership.organization_id = role_assignment.organization_id
                   AND membership.id = role_assignment.membership_id
                INNER JOIN service_dates AS service_date
                    ON service_date.organization_id = role_assignment.organization_id
                   AND service_date.manager_membership_id = role_assignment.membership_id
                WHERE role_assignment.organization_id = {organizationId.Value}
                  AND role_assignment.membership_id = {managerMembershipId.Value}
                  AND role_assignment.role = {(short)OrganizationRole.FoodIncharge}
                  AND role_assignment.revoked_at IS NULL
                  AND membership.status = {(short)MembershipStatus.Active}
                  AND service_date.id = {serviceDateId.Value}
                FOR SHARE OF role_assignment, membership, service_date
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        if (authority.Count != 1)
        {
            return Result.Failure<RosterPage>(
                RosterApplicationErrorCodes.Concealed());
        }

        Result<int> start = DecodeCursor(cursor);
        if (start.IsFailure)
        {
            return Result.Failure<RosterPage>(start.Error);
        }

        Result<int> takeResult = NormalizePageSize(pageSize);
        if (takeResult.IsFailure)
        {
            return Result.Failure<RosterPage>(takeResult.Error);
        }

        int take = takeResult.Value;
        List<SignupEntity> rows = await context.Signups
            .AsNoTracking()
            .Where(
                candidate => candidate.OrganizationId == organizationId.Value
                    && candidate.ServiceDateId == serviceDateId.Value)
            .OrderBy(candidate => candidate.SubmittedAt)
            .ThenBy(candidate => candidate.Id)
            .Skip(start.Value)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        bool hasMore = rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        Result<IReadOnlyList<SignupSummary>> summaries =
            await BuildSummariesAsync(rows, cancellationToken);
        return summaries.IsSuccess
            ? Result.Success(
                new RosterPage(
                    serviceDateId,
                    summaries.Value,
                    hasMore ? EncodeCursor(start.Value + rows.Count) : null))
            : Result.Failure<RosterPage>(summaries.Error);
    }

    private async ValueTask<Result<IReadOnlyList<SignupSummary>>> BuildSummariesAsync(
        List<SignupEntity> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return Result.Success<IReadOnlyList<SignupSummary>>([]);
        }

        Guid[] signupIds = rows.Select(candidate => candidate.Id).ToArray();
        Guid[] helpNeedIds = rows.Select(candidate => candidate.HelpNeedId).Distinct().ToArray();
        List<SignupMemberParticipantEntity> participantRows =
            await context.SignupMemberParticipants
                .AsNoTracking()
                .Where(candidate => signupIds.Contains(candidate.SignupId))
                .ToListAsync(cancellationToken);
        Guid[] membershipIds = rows
            .Select(candidate => candidate.PrimaryMembershipId)
            .Concat(participantRows.Select(candidate => candidate.MembershipId))
            .Distinct()
            .ToArray();
        Dictionary<Guid, MembershipEntity> memberships = await context.Memberships
            .AsNoTracking()
            .Where(candidate => membershipIds.Contains(candidate.Id))
            .ToDictionaryAsync(candidate => candidate.Id, cancellationToken);
        Dictionary<Guid, HelpNeedEntity> needs = await context.HelpNeeds
            .AsNoTracking()
            .Where(candidate => helpNeedIds.Contains(candidate.Id))
            .ToDictionaryAsync(candidate => candidate.Id, cancellationToken);

        List<SignupSummary> summaries = [];
        foreach (SignupEntity row in rows)
        {
            if (!memberships.TryGetValue(row.PrimaryMembershipId, out MembershipEntity? primary)
                || !needs.TryGetValue(row.HelpNeedId, out HelpNeedEntity? need))
            {
                return Result.Failure<IReadOnlyList<SignupSummary>>(
                    InvalidSignupState<HelpNeedSignups>(
                        "The signup projection references missing canonical records.").Error);
            }

            List<SignupParticipantSummary> participants = [];
            foreach (SignupMemberParticipantEntity participantRow in participantRows
                         .Where(candidate => candidate.SignupId == row.Id))
            {
                if (!memberships.TryGetValue(participantRow.MembershipId, out MembershipEntity? participant))
                {
                    return Result.Failure<IReadOnlyList<SignupSummary>>(
                        InvalidSignupState<HelpNeedSignups>(
                            "The signup projection references a missing participant.").Error);
                }

                participants.Add(
                    new SignupParticipantSummary(
                        MembershipId.From(participant.Id),
                        participant.DisplayName));
            }

            participants = participants
                .OrderBy(participant => participant.DisplayName, StringComparer.Ordinal)
                .ThenBy(participant => participant.MembershipId.Value)
                .ToList();
            summaries.Add(
                new SignupSummary(
                    SignupId.From(row.Id),
                    ServiceDateId.From(row.ServiceDateId),
                    HelpNeedId.From(row.HelpNeedId),
                    (HelpCategory)need.Category,
                    new SignupParticipantSummary(
                        MembershipId.From(primary.Id),
                        primary.DisplayName),
                    (SignupKind)row.Kind,
                    row.Label,
                    participants,
                    row.UnnamedParticipantCount,
                    1 + participants.Count + row.UnnamedParticipantCount,
                    (SignupStatus)row.Status,
                    row.SubmittedAt,
                    row.LastTransitionAt,
                    row.WaitlistOrder,
                    row.Version,
                    need.SignupVersion));
        }

        return Result.Success<IReadOnlyList<SignupSummary>>(summaries);
    }

    private static Result<Membership> HydrateActiveMembership(MembershipEntity row)
    {
        Result<Membership> invited = Membership.Invite(
            MembershipId.From(row.Id),
            OrganizationId.From(row.OrganizationId),
            UserId.From(row.UserId),
            row.DisplayName,
            row.EligibleAsNamedParticipant);
        if (invited.IsFailure)
        {
            return invited;
        }

        Result<MembershipActivated> activated =
            invited.Value.Activate(DateTimeOffset.UnixEpoch);
        return activated.IsSuccess
            ? invited
            : Result.Failure<Membership>(activated.Error);
    }

    private static Result<int> NormalizePageSize(int? pageSize)
    {
        int take = pageSize ?? ApplicationLimits.DefaultPageSize;
        return take < 1
            ? Result.Failure<int>(
                DomainError.Validation(
                    SignupApplicationErrorCodes.InvalidSignupRequest,
                    "Page size must be positive."))
            : Result.Success(Math.Min(take, ApplicationLimits.MaximumPageSize));
    }

    private static Result<int> DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return Result.Success(0);
        }

        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return int.TryParse(
                    decoded,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int value)
                && value >= 0
                ? Result.Success(value)
                : Result.Failure<int>(
                    DomainError.Validation(
                        SignupApplicationErrorCodes.InvalidSignupRequest,
                        "The cursor is invalid."));
        }
        catch (FormatException)
        {
            return Result.Failure<int>(
                DomainError.Validation(
                    SignupApplicationErrorCodes.InvalidSignupRequest,
                    "The cursor is invalid."));
        }
    }

    private static string EncodeCursor(int value) =>
        Convert.ToBase64String(
            Encoding.UTF8.GetBytes(
                value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public ValueTask<Result> SaveAsync(
        HelpNeedSignups aggregate,
        CancellationToken cancellationToken = default)
        => SaveAsync(aggregate, [], cancellationToken);

    public ValueTask<Result> SaveAsync(
        HelpNeedSignups aggregate,
        IReadOnlyCollection<NewSignupLabel> newSignupLabels,
        CancellationToken cancellationToken = default)
        => SaveCoreAsync(
            aggregate,
            newSignupLabels,
            PersistenceEffects.Empty,
            requiredNewSignupId: null,
            decisionWrite: null,
            cancellationWrite: null,
            reassignmentWrite: null,
            dateCancellationActorMembershipId: null,
            cancellationToken: cancellationToken);

    public ValueTask<Result> SaveDateCancellationAsync(
        HelpNeedSignups aggregate,
        MembershipId actorMembershipId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        actorMembershipId.EnsureValid();

        return SaveCoreAsync(
            aggregate,
            [],
            PersistenceEffects.Empty,
            requiredNewSignupId: null,
            decisionWrite: null,
            cancellationWrite: null,
            reassignmentWrite: null,
            dateCancellationActorMembershipId: actorMembershipId,
            cancellationToken: cancellationToken);
    }

    public ValueTask<Result> SaveSubmissionAsync(
        HelpNeedSignups aggregate,
        SignupSubmissionWrite write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(write.Effects);

        if (write.Effects.Notifications.Count != 1
            || write.Effects.AuditEntries.Count != 1
            || write.Effects.OutboxMessages.Count != 1)
        {
            return ValueTask.FromResult(
                InvalidSignupInput(
                    "A signup submission must persist exactly one notification, audit event, and outbox message."));
        }

        PersistenceEffects effects = new(
            write.Effects.Notifications
                .Select(PersistenceWriteSupport.ToEntity)
                .ToArray(),
            write.Effects.AuditEntries,
            [],
            write.Effects.OutboxMessages);
        return SaveCoreAsync(
            aggregate,
            [new NewSignupLabel(write.SignupId, write.Label)],
            effects,
            write.SignupId,
            decisionWrite: null,
            cancellationWrite: null,
            reassignmentWrite: null,
            dateCancellationActorMembershipId: null,
            cancellationToken: cancellationToken);
    }

    public ValueTask<Result> SaveDecisionAsync(
        HelpNeedSignups aggregate,
        SignupDecisionWrite write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(write.Effects);

        Result validated = ValidateDecisionWrite(aggregate, write);
        if (validated.IsFailure)
        {
            return ValueTask.FromResult(validated);
        }

        PersistenceEffects effects = new(
            write.Effects.Notifications
                .Select(PersistenceWriteSupport.ToEntity)
                .ToArray(),
            write.Effects.AuditEntries,
            [],
            write.Effects.OutboxMessages);
        return SaveCoreAsync(
            aggregate,
            [],
            effects,
            requiredNewSignupId: null,
            write,
            cancellationWrite: null,
            reassignmentWrite: null,
            dateCancellationActorMembershipId: null,
            cancellationToken: cancellationToken);
    }

    public ValueTask<Result> SaveCancellationAsync(
        HelpNeedSignups aggregate,
        SignupCancellationWrite write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(write.Effects);

        Result validated = ValidateCancellationWrite(aggregate, write);
        if (validated.IsFailure)
        {
            return ValueTask.FromResult(validated);
        }

        PersistenceEffects effects = new(
            write.Effects.Notifications
                .Select(PersistenceWriteSupport.ToEntity)
                .ToArray(),
            write.Effects.AuditEntries,
            [],
            write.Effects.OutboxMessages);
        return SaveCoreAsync(
            aggregate,
            [],
            effects,
            requiredNewSignupId: null,
            decisionWrite: null,
            write,
            reassignmentWrite: null,
            dateCancellationActorMembershipId: null,
            cancellationToken: cancellationToken);
    }

    public ValueTask<Result> SaveReassignmentAsync(
        HelpNeedSignups aggregate,
        SignupReassignmentWrite write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(write.Effects);

        Result validated = ValidateReassignmentWrite(aggregate, write);
        if (validated.IsFailure)
        {
            return ValueTask.FromResult(validated);
        }

        PersistenceEffects effects = new(
            write.Effects.Notifications
                .Select(PersistenceWriteSupport.ToEntity)
                .ToArray(),
            write.Effects.AuditEntries,
            [],
            write.Effects.OutboxMessages);
        return SaveCoreAsync(
            aggregate,
            [],
            effects,
            requiredNewSignupId: null,
            decisionWrite: null,
            cancellationWrite: null,
            write,
            dateCancellationActorMembershipId: null,
            cancellationToken: cancellationToken);
    }

    private ValueTask<Result> SaveCoreAsync(
        HelpNeedSignups aggregate,
        IReadOnlyCollection<NewSignupLabel> newSignupLabels,
        PersistenceEffects effects,
        SignupId? requiredNewSignupId,
        SignupDecisionWrite? decisionWrite,
        SignupCancellationWrite? cancellationWrite,
        SignupReassignmentWrite? reassignmentWrite,
        MembershipId? dateCancellationActorMembershipId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(newSignupLabels);
        ArgumentNullException.ThrowIfNull(effects);

        Result<Dictionary<Guid, string?>> validatedLabels = ValidateNewSignupLabels(
            aggregate,
            newSignupLabels);
        if (validatedLabels.IsFailure)
        {
            return ValueTask.FromResult(Result.Failure(validatedLabels.Error));
        }

        if (aggregate.Version < aggregate.OriginalVersion)
        {
            throw new InvalidOperationException("A signup aggregate version cannot move backwards.");
        }

        if (aggregate.Version == aggregate.OriginalVersion)
        {
            if (validatedLabels.Value.Count != 0)
            {
                return ValueTask.FromResult(
                    InvalidSignupInput(
                        "New signup labels may only be supplied when the aggregate creates a signup."));
            }

            return ValueTask.FromResult(Result.Success());
        }

        MembershipId? managerActorMembershipId =
            dateCancellationActorMembershipId
            ?? decisionWrite?.ActorMembershipId
            ?? reassignmentWrite?.ActorMembershipId
            ?? (cancellationWrite?.Authority
                    == SignupCancellationAuthority.ManagingFoodIncharge
                ? cancellationWrite.ActorMembershipId
                : null);
        SignupId? mutationSignupId =
            decisionWrite?.SignupId
            ?? cancellationWrite?.SignupId
            ?? reassignmentWrite?.SignupId;
        bool requiresOpenCategory = !dateCancellationActorMembershipId.HasValue
            && cancellationWrite?.Authority
                != SignupCancellationAuthority.PrimaryContact;
        bool requireCompleteExistingAggregate = dateCancellationActorMembershipId.HasValue;

        return PersistenceWriteSupport.ExecuteWriteAsync(
            context,
            async token =>
            {
                List<ServiceDateEntity> lockedDates = await context.ServiceDates
                    .FromSqlInterpolated(
                        $"""
                        SELECT *
                        FROM service_dates
                        WHERE id = {aggregate.ServiceDateId.Value}
                          AND organization_id = {aggregate.OrganizationId.Value}
                        FOR UPDATE
                        """)
                    .AsNoTracking()
                    .ToListAsync(token);
                ServiceDateEntity? lockedDate = lockedDates.SingleOrDefault();
                if (lockedDate is null)
                {
                    return StaleVersion();
                }

                List<HelpNeedEntity> lockedNeeds = await context.HelpNeeds
                    .FromSqlInterpolated(
                        $"""
                        SELECT *
                        FROM help_needs
                        WHERE id = {aggregate.HelpNeedId.Value}
                          AND organization_id = {aggregate.OrganizationId.Value}
                          AND service_date_id = {aggregate.ServiceDateId.Value}
                        FOR UPDATE
                        """)
                    .AsNoTracking()
                    .ToListAsync(token);
                HelpNeedEntity? lockedNeed = lockedNeeds.SingleOrDefault();
                if (lockedNeed is null)
                {
                    return StaleVersion();
                }

                if (managerActorMembershipId.HasValue)
                {
                    List<RoleAssignmentEntity> authority = await context.RoleAssignments
                        .FromSqlInterpolated(
                            $"""
                            SELECT role_assignment.*
                            FROM role_assignments AS role_assignment
                            INNER JOIN memberships AS membership
                                ON membership.organization_id = role_assignment.organization_id
                               AND membership.id = role_assignment.membership_id
                            WHERE role_assignment.organization_id = {aggregate.OrganizationId.Value}
                              AND role_assignment.membership_id = {managerActorMembershipId.Value.Value}
                              AND role_assignment.role = {(short)OrganizationRole.FoodIncharge}
                              AND role_assignment.revoked_at IS NULL
                              AND membership.status = {(short)MembershipStatus.Active}
                            FOR SHARE OF role_assignment, membership
                            """)
                        .AsNoTracking()
                        .ToListAsync(token);
                    if (authority.Count != 1
                        || lockedDate.ManagerMembershipId
                            != managerActorMembershipId.Value.Value)
                    {
                        return SignupNotFound();
                    }
                }

                if (cancellationWrite?.Authority
                        == SignupCancellationAuthority.PrimaryContact)
                {
                    List<MembershipEntity> activePrimary = await context.Memberships
                        .FromSqlInterpolated(
                            $"""
                            SELECT *
                            FROM memberships
                            WHERE organization_id = {aggregate.OrganizationId.Value}
                              AND id = {cancellationWrite.ActorMembershipId.Value}
                              AND status = {(short)MembershipStatus.Active}
                            FOR SHARE
                            """)
                        .AsNoTracking()
                        .ToListAsync(token);
                    SignupEntity? ownedTarget = await context.Signups
                        .AsNoTracking()
                        .SingleOrDefaultAsync(
                            candidate =>
                                candidate.OrganizationId
                                    == aggregate.OrganizationId.Value
                                && candidate.Id == cancellationWrite.SignupId.Value
                                && candidate.PrimaryMembershipId
                                    == cancellationWrite.ActorMembershipId.Value,
                            token);
                    if (activePrimary.Count != 1 || ownedTarget is null)
                    {
                        return SignupNotFound();
                    }
                }

                if (requiresOpenCategory
                    && (lockedDate.Status != (short)ServiceDateStatus.Open
                        || lockedNeed.Status != (short)HelpNeedStatus.Open))
                {
                    return CategoryClosed();
                }

                int updated = await context.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE help_needs
                    SET signup_version = {aggregate.Version},
                        waitlist_order_high_water = {aggregate.WaitlistOrderHighWater}
                    WHERE id = {aggregate.HelpNeedId.Value}
                      AND organization_id = {aggregate.OrganizationId.Value}
                      AND signup_version = {aggregate.OriginalVersion}
                      AND ({!requiresOpenCategory}
                           OR status = {(short)HelpNeedStatus.Open})
                      AND EXISTS (
                          SELECT 1
                          FROM service_dates
                          WHERE id = {aggregate.ServiceDateId.Value}
                            AND organization_id = {aggregate.OrganizationId.Value}
                            AND ({!requiresOpenCategory}
                                 OR status = {(short)ServiceDateStatus.Open}))
                    """,
                    token);
                if (updated == 0)
                {
                    return StaleVersion();
                }

                List<SignupEntity> existing = await context.Signups
                    .Where(
                        candidate => candidate.OrganizationId == aggregate.OrganizationId.Value
                            && candidate.HelpNeedId == aggregate.HelpNeedId.Value)
                    .ToListAsync(token);
                Dictionary<Guid, SignupEntity> existingById =
                    existing.ToDictionary(candidate => candidate.Id);
                HashSet<Guid> aggregateSignupIds = aggregate.Signups
                    .Select(candidate => candidate.Id.Value)
                    .ToHashSet();
                if (existingById.Keys.Any(id => !aggregateSignupIds.Contains(id)))
                {
                    return InvalidSignupStateResult(
                        "The signup aggregate omitted a persisted child and is not a complete hydration.");
                }

                if (requiredNewSignupId.HasValue)
                {
                    Guid[] newIds = aggregateSignupIds
                        .Where(id => !existingById.ContainsKey(id))
                        .ToArray();
                    if (newIds.Length != 1 || newIds[0] != requiredNewSignupId.Value.Value)
                    {
                        return InvalidSignupInput(
                            "A signup submission save must persist exactly the submitted signup.");
                    }
                }
                else if (requireCompleteExistingAggregate
                         && aggregateSignupIds.Any(id => !existingById.ContainsKey(id)))
                {
                    return InvalidSignupStateResult(
                        "A date-cancellation save must contain the complete existing aggregate.");
                }

                Guid? changedMutationSignupId = null;
                if (mutationSignupId.HasValue)
                {
                    if (aggregateSignupIds.Any(id => !existingById.ContainsKey(id))
                        || existingById.Keys.Any(id => !aggregateSignupIds.Contains(id)))
                    {
                        return InvalidSignupStateResult(
                            "A signup transition save must contain the complete existing aggregate.");
                    }

                    Guid[] changedIds = aggregate.Signups
                        .Where(signup =>
                            !HasSameMutableState(existingById[signup.Id.Value], signup))
                        .Select(signup => signup.Id.Value)
                        .ToArray();
                    if (changedIds.Length != 1
                        || changedIds[0] != mutationSignupId.Value.Value)
                    {
                        return InvalidSignupStateResult(
                            "A signup transition save must change exactly the target child.");
                    }

                    changedMutationSignupId = changedIds[0];
                }

                foreach (Signup signup in aggregate.Signups)
                {
                    if (!existingById.TryGetValue(signup.Id.Value, out SignupEntity? row))
                    {
                        context.Signups.Add(
                            ToEntity(
                                signup,
                                validatedLabels.Value.GetValueOrDefault(signup.Id.Value)));
                        context.SignupMemberParticipants.AddRange(
                            signup.MemberParticipantIds.Select(
                                participantId => new SignupMemberParticipantEntity
                                {
                                    SignupId = signup.Id.Value,
                                    OrganizationId = signup.OrganizationId.Value,
                                    MembershipId = participantId.Value,
                                }));
                        continue;
                    }

                    if (validatedLabels.Value.ContainsKey(signup.Id.Value))
                    {
                        return InvalidSignupInput(
                            "A signup label may only be supplied for a newly persisted signup.");
                    }

                    EnsureImmutableSignupColumns(row, signup);
                    if (mutationSignupId.HasValue
                        && row.Id != changedMutationSignupId)
                    {
                        continue;
                    }

                    row.Status = checked((short)signup.Status);
                    row.LastTransitionAt = signup.LastTransitionAt;
                    row.WaitlistOrder = signup.WaitlistOrder;
                    row.Version = signup.Version;
                }

                PersistenceWriteSupport.AddEffects(context, effects);
                return Result.Success();
            },
            cancellationToken);
    }

    private static Result ValidateDecisionWrite(
        HelpNeedSignups aggregate,
        SignupDecisionWrite write)
    {
        if (aggregate.Version != aggregate.OriginalVersion + 1
            || write.Effects.Notifications.Count != 1
            || write.Effects.AuditEntries.Count != 1
            || write.Effects.OutboxMessages.Count != 1)
        {
            return InvalidSignupInput(
                "A signup decision must persist one transition and exactly one effect of each type.");
        }

        Signup? target = aggregate.Signups.SingleOrDefault(
            signup => signup.Id == write.SignupId);
        if (target is null)
        {
            return InvalidSignupStateResult(
                "The signup decision target is absent from the aggregate.");
        }

        Notification notification = write.Effects.Notifications.Single();
        AuditEntry audit = write.Effects.AuditEntries.Single();
        OutboxMessage outbox = write.Effects.OutboxMessages.Single();
        (string title, string body, string action) = target.Status switch
        {
            SignupStatus.Approved => (
                "Signup approved",
                "Your signup request was approved.",
                "signup.approved"),
            SignupStatus.Declined => (
                "Signup declined",
                "Your signup request was declined.",
                "signup.declined"),
            SignupStatus.Waitlisted => (
                "Signup waitlisted",
                "Your signup request was added to the waitlist.",
                "signup.waitlisted"),
            _ => (string.Empty, string.Empty, string.Empty),
        };
        if (action.Length == 0
            || notification.OrganizationId != aggregate.OrganizationId
            || notification.RecipientMembershipId != target.PrimaryMembershipId
            || notification.Type != NotificationType.SignupStatusChanged
            || notification.ResourceType != NotificationResourceType.Signup
            || notification.ResourceId != write.SignupId.Value
            || notification.Title != title
            || notification.Body != body
            || audit.OrganizationId != aggregate.OrganizationId
            || audit.ActorMembershipId != write.ActorMembershipId
            || audit.Action != action
            || audit.ResourceType != "signup"
            || audit.ResourceId != write.SignupId.ToString()
            || audit.Purpose != "signup_decision"
            || outbox.OrganizationId != aggregate.OrganizationId
            || outbox.Type != "notification.push_requested"
            || !IsValidDecisionOutbox(outbox, notification, target))
        {
            return InvalidSignupInput(
                "The signup decision effects are not scoped to the target transition.");
        }

        return HasOnlyDecisionStateProperties(audit.BeforeState)
            && HasOnlyDecisionStateProperties(audit.AfterState)
            ? Result.Success()
            : InvalidSignupInput(
                "Signup decision audit state contains unsupported fields.");
    }

    private static Result ValidateCancellationWrite(
        HelpNeedSignups aggregate,
        SignupCancellationWrite write)
    {
        if (aggregate.Version != aggregate.OriginalVersion + 1
            || write.Effects.Notifications.Count != 1
            || write.Effects.AuditEntries.Count != 1
            || write.Effects.OutboxMessages.Count is < 1 or > 2)
        {
            return InvalidSignupInput(
                "A signup cancellation must persist one transition and its complete effect set.");
        }

        Signup? target = aggregate.Signups.SingleOrDefault(
            signup => signup.Id == write.SignupId);
        if (target is null)
        {
            return InvalidSignupStateResult(
                "The signup cancellation target is absent from the aggregate.");
        }

        bool overrideCancellation =
            write.Authority == SignupCancellationAuthority.ManagingFoodIncharge;
        if ((overrideCancellation && target.Status != SignupStatus.Cancelled)
            || (!overrideCancellation
                && target.Status is not (
                    SignupStatus.Withdrawn or SignupStatus.Cancelled)))
        {
            return InvalidSignupInput(
                "The signup cancellation authority does not match the target state.");
        }

        Notification notification = write.Effects.Notifications.Single();
        AuditEntry audit = write.Effects.AuditEntries.Single();
        bool cancelled = target.Status == SignupStatus.Cancelled;
        string expectedTitle = cancelled ? "Signup cancelled" : "Signup withdrawn";
        string expectedBody = overrideCancellation
            ? "Your signup was cancelled by the managing Food Incharge."
            : cancelled
                ? "Your signup was cancelled."
                : "Your signup was withdrawn.";
        string expectedAction = cancelled ? "signup.cancelled" : "signup.withdrawn";
        string expectedPurpose = overrideCancellation
            ? "signup_cancellation_override"
            : "signup_cancellation";
        if (notification.OrganizationId != aggregate.OrganizationId
            || notification.RecipientMembershipId != target.PrimaryMembershipId
            || notification.Type != NotificationType.SignupStatusChanged
            || notification.ResourceType != NotificationResourceType.Signup
            || notification.ResourceId != write.SignupId.Value
            || notification.Title != expectedTitle
            || notification.Body != expectedBody
            || audit.OrganizationId != aggregate.OrganizationId
            || audit.ActorMembershipId != write.ActorMembershipId
            || audit.Action != expectedAction
            || audit.ResourceType != "signup"
            || audit.ResourceId != write.SignupId.ToString()
            || audit.Purpose != expectedPurpose
            || !HasOnlyDecisionStateProperties(audit.BeforeState)
            || !HasOnlyDecisionStateProperties(audit.AfterState))
        {
            return InvalidSignupInput(
                "The signup cancellation effects are not scoped to the target transition.");
        }

        OutboxMessage? push = write.Effects.OutboxMessages.SingleOrDefault(
            message => message.Type == "notification.push_requested");
        OutboxMessage? access = write.Effects.OutboxMessages.SingleOrDefault(
            message => message.Type == "thread.access_changed");
        bool wasApproved = AuditStateHasStatus(audit.BeforeState, "approved");
        if (push is null
            || !IsValidDecisionOutbox(push, notification, target)
            || (wasApproved
                ? access is null
                    || write.Effects.OutboxMessages.Count != 2
                    || !IsValidThreadAccessOutbox(
                        access,
                        aggregate,
                        target,
                        eligible: false)
                : access is not null
                    || write.Effects.OutboxMessages.Count != 1))
        {
            return InvalidSignupInput(
                "The signup cancellation outbox effects are incomplete or invalid.");
        }

        return Result.Success();
    }

    private static Result ValidateReassignmentWrite(
        HelpNeedSignups aggregate,
        SignupReassignmentWrite write)
    {
        if (aggregate.Version != aggregate.OriginalVersion + 1
            || aggregate.HelpNeedId != write.HelpNeedId
            || write.Effects.Notifications.Count != 1
            || write.Effects.AuditEntries.Count != 1
            || write.Effects.OutboxMessages.Count != 2)
        {
            return InvalidSignupInput(
                "A signup reassignment must persist one transition and its complete effect set.");
        }

        Signup? target = aggregate.Signups.SingleOrDefault(
            signup => signup.Id == write.SignupId);
        if (target is null || target.Status != SignupStatus.Approved)
        {
            return InvalidSignupStateResult(
                "The reassignment target must be an approved aggregate-owned signup.");
        }

        Notification notification = write.Effects.Notifications.Single();
        AuditEntry audit = write.Effects.AuditEntries.Single();
        OutboxMessage? push = write.Effects.OutboxMessages.SingleOrDefault(
            message => message.Type == "notification.push_requested");
        OutboxMessage? access = write.Effects.OutboxMessages.SingleOrDefault(
            message => message.Type == "thread.access_changed");
        if (notification.OrganizationId != aggregate.OrganizationId
            || notification.RecipientMembershipId != target.PrimaryMembershipId
            || notification.Type != NotificationType.SignupStatusChanged
            || notification.ResourceType != NotificationResourceType.Signup
            || notification.ResourceId != write.SignupId.Value
            || notification.Title != "Waitlisted signup approved"
            || notification.Body != "Your selected waitlisted signup was approved."
            || audit.OrganizationId != aggregate.OrganizationId
            || audit.ActorMembershipId != write.ActorMembershipId
            || audit.Action != "signup.reassigned"
            || audit.ResourceType != "signup"
            || audit.ResourceId != write.SignupId.ToString()
            || audit.Purpose != "waitlist_reassignment"
            || !AuditStateHasStatus(audit.BeforeState, "waitlisted")
            || !AuditStateHasStatus(audit.AfterState, "approved")
            || !HasOnlyDecisionStateProperties(audit.BeforeState)
            || !HasOnlyDecisionStateProperties(audit.AfterState)
            || push is null
            || !IsValidDecisionOutbox(push, notification, target)
            || access is null
            || !IsValidThreadAccessOutbox(
                access,
                aggregate,
                target,
                eligible: true))
        {
            return InvalidSignupInput(
                "The signup reassignment effects are not scoped to the selected transition.");
        }

        return Result.Success();
    }

    private static bool AuditStateHasStatus(string? json, string status)
    {
        if (json is null)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty("status").GetString() == status;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidThreadAccessOutbox(
        OutboxMessage outbox,
        HelpNeedSignups aggregate,
        Signup target,
        bool eligible)
    {
        if (outbox.OrganizationId != aggregate.OrganizationId)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(outbox.Payload);
            JsonElement root = document.RootElement;
            return root.EnumerateObject()
                    .Select(property => property.Name)
                    .ToHashSet(StringComparer.Ordinal)
                    .SetEquals(
                        ["serviceDateId", "membershipId", "eligible", "signupId"])
                && root.GetProperty("serviceDateId").GetString()
                    == aggregate.ServiceDateId.ToString()
                && root.GetProperty("membershipId").GetString()
                    == target.PrimaryMembershipId.ToString()
                && root.GetProperty("eligible").GetBoolean() == eligible
                && root.GetProperty("signupId").GetString()
                    == target.Id.ToString();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidDecisionOutbox(
        OutboxMessage outbox,
        Notification notification,
        Signup target)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(outbox.Payload);
            JsonElement root = document.RootElement;
            HashSet<string> properties = root.EnumerateObject()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            return properties.SetEquals(
                    ["notificationId", "recipientMembershipId", "resourceType", "resourceId"])
                && root.GetProperty("notificationId").GetString()
                    == notification.Id.ToString()
                && root.GetProperty("recipientMembershipId").GetString()
                    == target.PrimaryMembershipId.ToString()
                && root.GetProperty("resourceType").GetString() == "signup"
                && root.GetProperty("resourceId").GetString() == target.Id.ToString();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasOnlyDecisionStateProperties(string? json)
    {
        if (json is null)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(
                    ["status", "childVersion", "rootSignupVersion", "waitlistOrder"]);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasSameMutableState(SignupEntity row, Signup signup) =>
        row.Status == checked((short)signup.Status)
        && row.LastTransitionAt == signup.LastTransitionAt
        && row.WaitlistOrder == signup.WaitlistOrder
        && row.Version == signup.Version;

    private static Result<Dictionary<Guid, string?>> ValidateNewSignupLabels(
        HelpNeedSignups aggregate,
        IReadOnlyCollection<NewSignupLabel> newSignupLabels)
    {
        HashSet<Guid> aggregateSignupIds = aggregate.Signups
            .Select(signup => signup.Id.Value)
            .ToHashSet();
        Dictionary<Guid, string?> labelsBySignupId = [];
        foreach (NewSignupLabel newSignupLabel in newSignupLabels)
        {
            ArgumentNullException.ThrowIfNull(newSignupLabel);

            if (!aggregateSignupIds.Contains(newSignupLabel.SignupId.Value))
            {
                return InvalidSignupInput<Dictionary<Guid, string?>>(
                    "A signup label must belong to the aggregate being persisted.");
            }

            if (!labelsBySignupId.TryAdd(newSignupLabel.SignupId.Value, newSignupLabel.Label))
            {
                return InvalidSignupInput<Dictionary<Guid, string?>>(
                    "A signup label may be supplied only once for each new signup.");
            }

            SignupLabelValidationResult validation =
                SignupLabelPolicy.Normalize(newSignupLabel.Label);
            if (validation.Error == SignupLabelValidationError.TooLarge)
            {
                return Result.Failure<Dictionary<Guid, string?>>(
                    DomainError.PayloadTooLarge(
                        "The signup label exceeds the permitted content size."));
            }

            if (!validation.IsSuccess
                || !string.Equals(
                    validation.Value,
                    newSignupLabel.Label,
                    StringComparison.Ordinal))
            {
                return InvalidSignupInput<Dictionary<Guid, string?>>(
                    "A signup label must use the documented non-identifying format.");
            }
        }

        return Result.Success(labelsBySignupId);
    }

    private sealed record SignupHydrationRows(
        HelpNeedEntity Need,
        ServiceDateEntity? Date,
        IReadOnlyCollection<HelpNeedEntity> DateNeeds,
        IReadOnlyCollection<SignupEntity> Signups,
        IReadOnlyCollection<SignupMemberParticipantEntity> Participants);

    private static Result<HelpNeed> HydrateHelpNeed(HelpNeedEntity entity) =>
        PersistenceHydration.Try(
            () => HelpNeed.Rehydrate(
                HelpNeedId.From(entity.Id),
                ServiceDateId.From(entity.ServiceDateId),
                (HelpCategory)entity.Category,
                entity.Instructions,
                entity.Capacity,
                (HelpNeedStatus)entity.Status,
                entity.Version),
            InvalidSignupState<HelpNeed>(
                "The persisted help need has an invalid canonical identifier."));

    private static Result<ServiceDate> HydrateServiceDate(
        ServiceDateEntity entity,
        IReadOnlyCollection<HelpNeed> helpNeeds) =>
        PersistenceHydration.Try(
            () => ServiceDate.Rehydrate(
                ServiceDateId.From(entity.Id),
                OrganizationId.From(entity.OrganizationId),
                entity.Title,
                entity.Instructions,
                entity.StartsAt,
                entity.EndsAt,
                entity.CancellationDeadlineAt,
                MembershipId.From(entity.ManagerMembershipId),
                (ServiceDateStatus)entity.Status,
                entity.Version,
                helpNeeds),
            InvalidSignupState<ServiceDate>(
                "The persisted canonical service date has an invalid identifier."));

    private static Result<Signup> HydrateSignup(
        SignupEntity entity,
        IEnumerable<SignupMemberParticipantEntity> participants) =>
        PersistenceHydration.Try(
            () => Signup.Rehydrate(
                SignupId.From(entity.Id),
                OrganizationId.From(entity.OrganizationId),
                ServiceDateId.From(entity.ServiceDateId),
                HelpNeedId.From(entity.HelpNeedId),
                MembershipId.From(entity.PrimaryMembershipId),
                (SignupKind)entity.Kind,
                participants.Select(candidate => MembershipId.From(candidate.MembershipId)).ToArray(),
                entity.UnnamedParticipantCount,
                (SignupStatus)entity.Status,
                entity.SubmittedAt,
                entity.LastTransitionAt,
                entity.WaitlistOrder,
                entity.Version),
            InvalidSignupState<Signup>("The persisted signup has an invalid identifier."));

    private static SignupEntity ToEntity(Signup signup, string? label) =>
        new()
        {
            Id = signup.Id.Value,
            OrganizationId = signup.OrganizationId.Value,
            ServiceDateId = signup.ServiceDateId.Value,
            HelpNeedId = signup.HelpNeedId.Value,
            PrimaryMembershipId = signup.PrimaryMembershipId.Value,
            Kind = checked((short)signup.Kind),
            Label = label,
            UnnamedParticipantCount = signup.UnnamedParticipantCount,
            Status = checked((short)signup.Status),
            SubmittedAt = signup.SubmittedAt,
            LastTransitionAt = signup.LastTransitionAt,
            WaitlistOrder = signup.WaitlistOrder,
            Version = signup.Version,
        };

    private static void EnsureImmutableSignupColumns(SignupEntity row, Signup signup)
    {
        if (row.OrganizationId != signup.OrganizationId.Value
            || row.ServiceDateId != signup.ServiceDateId.Value
            || row.HelpNeedId != signup.HelpNeedId.Value
            || row.PrimaryMembershipId != signup.PrimaryMembershipId.Value
            || row.Kind != checked((short)signup.Kind)
            || row.UnnamedParticipantCount != signup.UnnamedParticipantCount
            || row.SubmittedAt != signup.SubmittedAt)
        {
            throw new InvalidOperationException(
                "A persisted signup changed an immutable aggregate-owned field.");
        }
    }

    private static Result<T> InvalidSignupState<T>(string message) =>
        Result.Failure<T>(
            DomainError.Validation(SignupErrorCodes.InvalidSignupAggregateState, message));

    private static Result<HelpNeedSignups> InvalidSignupState(string message) =>
        InvalidSignupState<HelpNeedSignups>(message);

    private static Result<T> InvalidSignupInput<T>(string message) =>
        Result.Failure<T>(
            DomainError.Validation(SignupErrorCodes.InvalidSignupInput, message));

    private static Result InvalidSignupInput(string message) =>
        Result.Failure(
            DomainError.Validation(SignupErrorCodes.InvalidSignupInput, message));

    private static Result InvalidSignupStateResult(string message) =>
        Result.Failure(
            DomainError.Validation(SignupErrorCodes.InvalidSignupAggregateState, message));

    private static Result<HelpNeedSignups> HelpNeedNotFound() =>
        Result.Failure<HelpNeedSignups>(
            DomainError.NotFound("help_need_not_found", "The help need was not found."));

    private static Result StaleVersion() =>
        Result.Failure(
            DomainError.PreconditionFailed(
                ErrorCodes.StaleVersion,
                "The signup aggregate was changed by another writer."));

    private static Result SignupNotFound() =>
        Result.Failure(
            DomainError.NotFound(
                SignupApplicationErrorCodes.SignupNotFound,
                "The signup was not found."));

    private static Result<T> SignupNotFound<T>() =>
        Result.Failure<T>(
            DomainError.NotFound(
                SignupApplicationErrorCodes.SignupNotFound,
                "The signup was not found."));

    private static Result CategoryClosed() =>
        Result.Failure(
            DomainError.Conflict(
                ErrorCodes.CategoryClosed,
                "The service date and help need must be open before signup changes are saved."));
}

public sealed partial class PostgresThreadRepository(TabrukDbContext context) : IThreadRepository
{
    private const int AmbientReadAttempts = 3;

    public ValueTask<Result<LoadedDateThread>> GetAsync(
        OrganizationId organizationId,
        ThreadId threadId,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => GetCoreAsync(organizationId, threadId, cancellationToken));

    public ValueTask<Result<LoadedDateThread>> GetByServiceDateAsync(
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => GetByServiceDateCoreAsync(
                organizationId,
                serviceDateId,
                cancellationToken));

    private async ValueTask<Result<LoadedDateThread>> GetCoreAsync(
        OrganizationId organizationId,
        ThreadId threadId,
        CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is not null)
        {
            return await ReadWithinAmbientTransactionAsync(
                organizationId,
                threadId,
                cancellationToken);
        }

        IDbContextTransaction? transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        Exception? primaryException = null;
        bool rollbackOwnedTransaction = false;
        try
        {
            await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
            Result<LoadedDateThread> result = await ReadThreadAsync(
                organizationId,
                threadId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception exception)
        {
            primaryException = exception;
            rollbackOwnedTransaction = true;
            throw;
        }
        finally
        {
            await PostgresOwnedTransactionCleanup.CleanupAsync(
                context,
                transaction,
                rollbackOwnedTransaction,
                clearChangeTracker: false,
                primaryException);
        }
    }

    private async ValueTask<Result<LoadedDateThread>> GetByServiceDateCoreAsync(
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        CancellationToken cancellationToken)
    {
        DateThreadEntity? row = await context.DateThreads
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.OrganizationId == organizationId.Value
                    && candidate.ServiceDateId == serviceDateId.Value,
                cancellationToken);
        return row is null
            ? ThreadNotFound()
            : await GetCoreAsync(
                organizationId,
                ThreadId.From(row.Id),
                cancellationToken);
    }

    private async ValueTask<Result<LoadedDateThread>> ReadWithinAmbientTransactionAsync(
        OrganizationId organizationId,
        ThreadId threadId,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < AmbientReadAttempts; attempt++)
        {
            ThreadHydrationRows? rows = await ReadThreadRowsAsync(
                organizationId,
                threadId,
                cancellationToken);
            if (rows is null)
            {
                return ThreadNotFound();
            }

            DateThreadEntity? current = await context.DateThreads
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == threadId.Value
                        && candidate.OrganizationId == organizationId.Value,
                    cancellationToken);
            if (current is null)
            {
                return ThreadNotFound();
            }

            if (current.Version == rows.Thread.Version)
            {
                return HydrateThread(rows);
            }
        }

        return Result.Failure<LoadedDateThread>(
            DomainError.Conflict(
                "thread_read_conflict",
                "The date thread changed while it was being read. Please retry."));
    }

    private async ValueTask<Result<LoadedDateThread>> ReadThreadAsync(
        OrganizationId organizationId,
        ThreadId threadId,
        CancellationToken cancellationToken)
    {
        ThreadHydrationRows? rows = await ReadThreadRowsAsync(
            organizationId,
            threadId,
            cancellationToken);
        return rows is null ? ThreadNotFound() : HydrateThread(rows);
    }

    private async ValueTask<ThreadHydrationRows?> ReadThreadRowsAsync(
        OrganizationId organizationId,
        ThreadId threadId,
        CancellationToken cancellationToken)
    {
        DateThreadEntity? row = await context.DateThreads
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == threadId.Value
                    && candidate.OrganizationId == organizationId.Value,
                cancellationToken);
        if (row is null)
        {
            return null;
        }

        List<ThreadMessageEntity> messageRows = await context.ThreadMessages
            .AsNoTracking()
            .Where(candidate => candidate.OrganizationId == row.OrganizationId && candidate.ThreadId == row.Id)
            .OrderBy(candidate => candidate.CreatedAt)
            .ToListAsync(cancellationToken);
        Guid[] messageIds = messageRows.Select(candidate => candidate.Id).ToArray();
        List<MessageReportEntity> reportRows = await context.MessageReports
            .AsNoTracking()
            .Where(candidate => candidate.OrganizationId == row.OrganizationId && messageIds.Contains(candidate.MessageId))
            .OrderBy(candidate => candidate.ReportedAt)
            .ToListAsync(cancellationToken);

        return new ThreadHydrationRows(row, messageRows, reportRows);
    }

    private static Result<LoadedDateThread> HydrateThread(ThreadHydrationRows rows)
    {
        List<ThreadMessage> messages = [];
        foreach (ThreadMessageEntity messageRow in rows.Messages)
        {
            Result<ThreadMessage> message = HydrateThreadMessage(messageRow);
            if (message.IsFailure)
            {
                return Result.Failure<LoadedDateThread>(message.Error);
            }

            messages.Add(message.Value);
        }

        List<MessageReport> reports = [];
        foreach (MessageReportEntity reportRow in rows.Reports)
        {
            Result<MessageReport> report = HydrateMessageReport(reportRow);
            if (report.IsFailure)
            {
                return Result.Failure<LoadedDateThread>(report.Error);
            }

            reports.Add(report.Value);
        }

        Result<DateThread> thread = HydrateDateThread(rows.Thread, messages, reports);
        return thread.IsSuccess
            ? Result.Success(new LoadedDateThread(thread.Value, rows.Thread.Version))
            : Result.Failure<LoadedDateThread>(thread.Error);
    }

    public ValueTask<Result> SaveAsync(
        LoadedDateThread loaded,
        ThreadPersistenceEffects effects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(effects);

        PersistenceEffects persistenceEffects = new(
            effects.Notifications.Select(PersistenceWriteSupport.ToEntity).ToArray(),
            effects.AuditEntries,
            effects.PrivilegedAccessEntries,
            effects.OutboxMessages);

        if (loaded.Thread.Version == loaded.LoadedVersion)
        {
            if (!PersistenceWriteSupport.IsEmpty(effects))
            {
                return ValueTask.FromResult(
                    Result.Failure(
                        DomainError.Conflict(
                            ErrorCodes.InvalidTransition,
                            "An unchanged thread cannot create persistence side effects.")));
            }

            return ValueTask.FromResult(Result.Success());
        }

        if (loaded.Thread.Version != loaded.LoadedVersion + 1)
        {
            throw new InvalidOperationException(
                "A thread save must represent exactly one Domain transition from the loaded version.");
        }

        return PersistenceWriteSupport.ExecuteWriteAsync(
            context,
            async token =>
            {
                int updated = await context.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE date_threads
                    SET status = {(short)loaded.Thread.Status},
                        locked_at = {loaded.Thread.LockedAt},
                        version = {loaded.Thread.Version}
                    WHERE id = {loaded.Thread.Id.Value}
                      AND organization_id = {loaded.Thread.OrganizationId.Value}
                      AND version = {loaded.LoadedVersion}
                    """,
                    token);
                if (updated == 0)
                {
                    return StaleVersion();
                }

                List<ThreadMessageEntity> existingMessages = await context.ThreadMessages
                    .Where(
                        candidate => candidate.OrganizationId == loaded.Thread.OrganizationId.Value
                            && candidate.ThreadId == loaded.Thread.Id.Value)
                    .ToListAsync(token);
                Dictionary<Guid, ThreadMessageEntity> messagesById =
                    existingMessages.ToDictionary(candidate => candidate.Id);
                foreach (ThreadMessage message in loaded.Thread.Messages)
                {
                    if (!messagesById.TryGetValue(message.Id.Value, out ThreadMessageEntity? row))
                    {
                        context.ThreadMessages.Add(ToEntity(message, loaded.Thread.OrganizationId.Value));
                        continue;
                    }

                    EnsureImmutableMessageColumns(row, message);
                    row.Visibility = checked((short)message.Visibility);
                    row.HiddenAt = message.HiddenAt;
                }

                List<MessageReportEntity> existingReports = await context.MessageReports
                    .Where(
                        candidate => candidate.OrganizationId == loaded.Thread.OrganizationId.Value
                            && messagesById.Keys.Contains(candidate.MessageId))
                    .ToListAsync(token);
                HashSet<(Guid MessageId, Guid ReporterMembershipId)> reportKeys = existingReports
                    .Select(report => (report.MessageId, report.ReporterMembershipId))
                    .ToHashSet();
                foreach (MessageReport report in loaded.Thread.Reports)
                {
                    if (reportKeys.Add((report.MessageId.Value, report.ReporterMembershipId.Value)))
                    {
                        context.MessageReports.Add(
                            new MessageReportEntity
                            {
                                OrganizationId = loaded.Thread.OrganizationId.Value,
                                MessageId = report.MessageId.Value,
                                ReporterMembershipId = report.ReporterMembershipId.Value,
                                Reason = checked((short)report.Reason),
                                Comment = report.Comment,
                                State = 0,
                                ReportedAt = report.ReportedAt,
                            });
                    }
                }

                context.ThreadModerationEvents.AddRange(
                    effects.ModerationEvents.Select(
                        moderation => new ThreadModerationEventEntity
                        {
                            Id = moderation.Id,
                            OrganizationId = moderation.OrganizationId.Value,
                            ThreadId = moderation.ThreadId.Value,
                            MessageId = moderation.MessageId?.Value,
                            ActorMembershipId = moderation.ActorMembershipId.Value,
                            Action = moderation.Action,
                            Reason = moderation.Reason,
                            OccurredAt = moderation.OccurredAt,
                        }));
                PersistenceWriteSupport.AddEffects(context, persistenceEffects);
                return Result.Success();
            },
            cancellationToken);
    }

    private static ThreadMessageEntity ToEntity(ThreadMessage message, Guid organizationId) =>
        new()
        {
            Id = message.Id.Value,
            OrganizationId = organizationId,
            ThreadId = message.ThreadId.Value,
            AuthorMembershipId = message.AuthorMembershipId.Value,
            ClientMessageId = message.ClientMessageId.Value,
            Body = message.Body,
            Visibility = checked((short)message.Visibility),
            CreatedAt = message.CreatedAt,
            HiddenAt = message.HiddenAt,
        };

    private static Result<ThreadMessage> HydrateThreadMessage(ThreadMessageEntity entity) =>
        PersistenceHydration.Try(
            () => ThreadMessage.Rehydrate(
                MessageId.From(entity.Id),
                ThreadId.From(entity.ThreadId),
                MembershipId.From(entity.AuthorMembershipId),
                IdempotencyKey.From(entity.ClientMessageId),
                entity.Body,
                (MessageVisibility)entity.Visibility,
                entity.CreatedAt,
                entity.HiddenAt),
            InvalidThreadState<ThreadMessage>("The persisted thread message has an invalid identifier."));

    private static Result<MessageReport> HydrateMessageReport(MessageReportEntity entity) =>
        PersistenceHydration.Try(
            () => MessageReport.Rehydrate(
                MessageId.From(entity.MessageId),
                MembershipId.From(entity.ReporterMembershipId),
                (MessageReportReason)entity.Reason,
                entity.Comment,
                entity.ReportedAt),
            InvalidThreadState<MessageReport>("The persisted message report has an invalid identifier."));

    private static Result<DateThread> HydrateDateThread(
        DateThreadEntity entity,
        IReadOnlyCollection<ThreadMessage> messages,
        IReadOnlyCollection<MessageReport> reports) =>
        PersistenceHydration.Try(
            () => DateThread.Rehydrate(
                ThreadId.From(entity.Id),
                OrganizationId.From(entity.OrganizationId),
                ServiceDateId.From(entity.ServiceDateId),
                (ThreadStatus)entity.Status,
                entity.LockedAt,
                entity.Version,
                messages,
                reports),
            InvalidThreadState<DateThread>("The persisted date thread has an invalid identifier."));

    private static void EnsureImmutableMessageColumns(ThreadMessageEntity row, ThreadMessage message)
    {
        if (row.ThreadId != message.ThreadId.Value
            || row.AuthorMembershipId != message.AuthorMembershipId.Value
            || row.ClientMessageId != message.ClientMessageId.Value
            || row.Body != message.Body
            || row.CreatedAt != message.CreatedAt)
        {
            throw new InvalidOperationException(
                "A persisted thread message changed an immutable field.");
        }
    }

    private static Result StaleVersion() =>
        Result.Failure(
            DomainError.PreconditionFailed(
                ErrorCodes.StaleVersion,
                "The date thread was changed by another writer."));

    private static Result<LoadedDateThread> ThreadNotFound() =>
        Result.Failure<LoadedDateThread>(
            DomainError.NotFound("thread_not_found", "The date thread was not found."));

    private static Result<T> InvalidThreadState<T>(string message) =>
        Result.Failure<T>(DomainError.Validation("invalid_thread_hydration", message));

    private sealed record ThreadHydrationRows(
        DateThreadEntity Thread,
        IReadOnlyCollection<ThreadMessageEntity> Messages,
        IReadOnlyCollection<MessageReportEntity> Reports);
}

public sealed class PostgresNotificationRepository(TabrukDbContext context)
    : INotificationRepository, INotificationWriter
{
    public ValueTask AddAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();
        context.Notifications.Add(
            new NotificationEntity
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
            });
        return ValueTask.CompletedTask;
    }

    public ValueTask<Result<Notification>> GetAsync(
        OrganizationId organizationId,
        MembershipId recipientMembershipId,
        NotificationId notificationId,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => GetCoreAsync(
                organizationId,
                recipientMembershipId,
                notificationId,
                cancellationToken));

    private async ValueTask<Result<Notification>> GetCoreAsync(
        OrganizationId organizationId,
        MembershipId recipientMembershipId,
        NotificationId notificationId,
        CancellationToken cancellationToken)
    {
        NotificationEntity? row = await context.Notifications
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == notificationId.Value
                    && candidate.OrganizationId == organizationId.Value
                    && candidate.RecipientMembershipId == recipientMembershipId.Value,
                cancellationToken);
        if (row is null)
        {
            return Result.Failure<Notification>(
                DomainError.NotFound("notification_not_found", "The notification was not found."));
        }

        return PersistenceHydration.Try(
            () => Notification.Rehydrate(
                NotificationId.From(row.Id),
                OrganizationId.From(row.OrganizationId),
                MembershipId.From(row.RecipientMembershipId),
                (NotificationType)row.Type,
                (NotificationResourceType)row.ResourceType,
                row.ResourceId,
                row.Title,
                row.Body,
                row.CreatedAt,
                row.ReadAt),
            Result.Failure<Notification>(
                DomainError.Validation(
                    "invalid_notification_hydration",
                    "The persisted notification has an invalid identifier.")));
    }
}

public sealed class PostgresGovernanceRepository(TabrukDbContext context)
{
    private const int AmbientReadAttempts = 3;

    public ValueTask<Result<OrganizationAccountGovernance>> GetAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => GetCoreAsync(organizationId, cancellationToken));

    private async ValueTask<Result<OrganizationAccountGovernance>> GetCoreAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is not null)
        {
            return await ReadWithinAmbientTransactionAsync(organizationId, cancellationToken);
        }

        IDbContextTransaction? transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        Exception? primaryException = null;
        bool rollbackOwnedTransaction = false;
        try
        {
            await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
            GovernanceHydrationRows? rows = await ReadGovernanceRowsAsync(
                organizationId,
                cancellationToken);
            Result<OrganizationAccountGovernance> result =
                rows is null ? OrganizationNotFound() : HydrateGovernanceRows(rows);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception exception)
        {
            primaryException = exception;
            rollbackOwnedTransaction = true;
            throw;
        }
        finally
        {
            await PostgresOwnedTransactionCleanup.CleanupAsync(
                context,
                transaction,
                rollbackOwnedTransaction,
                clearChangeTracker: false,
                primaryException);
        }
    }

    private async ValueTask<Result<OrganizationAccountGovernance>> ReadWithinAmbientTransactionAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < AmbientReadAttempts; attempt++)
        {
            GovernanceHydrationRows? rows = await ReadGovernanceRowsAsync(
                organizationId,
                cancellationToken);
            if (rows is null)
            {
                return OrganizationNotFound();
            }

            OrganizationEntity? current = await context.Organizations
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == organizationId.Value,
                    cancellationToken);
            if (current is null)
            {
                return OrganizationNotFound();
            }

            if (current.Version == rows.Organization.Version)
            {
                return HydrateGovernanceRows(rows);
            }
        }

        return Result.Failure<OrganizationAccountGovernance>(
            DomainError.Conflict(
                "governance_read_conflict",
                "The organization governance changed while it was being read. Please retry."));
    }

    private async ValueTask<GovernanceHydrationRows?> ReadGovernanceRowsAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        OrganizationEntity? organization = await context.Organizations
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == organizationId.Value, cancellationToken);
        if (organization is null)
        {
            return null;
        }

        List<MembershipEntity> membershipRows = await context.Memberships
            .AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organization.Id)
            .OrderBy(candidate => candidate.Id)
            .ToListAsync(cancellationToken);
        List<RoleAssignmentEntity> activeRoleRows = await context.RoleAssignments
            .AsNoTracking()
            .Where(
                candidate => candidate.OrganizationId == organization.Id
                    && candidate.RevokedAt == null)
            .ToListAsync(cancellationToken);
        List<RoleChangeRequestEntity> requestRows = await context.RoleChangeRequests
            .AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organization.Id)
            .OrderBy(candidate => candidate.Id)
            .ToListAsync(cancellationToken);

        return new GovernanceHydrationRows(
            organization,
            membershipRows,
            activeRoleRows,
            requestRows);
    }

    private static Result<OrganizationAccountGovernance> HydrateGovernanceRows(
        GovernanceHydrationRows rows)
    {
        Result<IReadOnlyCollection<Membership>> memberships = RehydrateMemberships(
            rows.Organization,
            rows.Memberships,
            rows.ActiveRoleAssignments);
        if (memberships.IsFailure)
        {
            return Result.Failure<OrganizationAccountGovernance>(memberships.Error);
        }

        List<RoleChangeRequest> requests = [];
        foreach (RoleChangeRequestEntity requestRow in rows.RoleChangeRequests)
        {
            Result<RoleChangeRequest> request = HydrateRoleChangeRequest(requestRow);
            if (request.IsFailure)
            {
                return Result.Failure<OrganizationAccountGovernance>(request.Error);
            }

            requests.Add(request.Value);
        }

        return HydrateGovernance(rows.Organization, memberships.Value, requests);
    }

    public ValueTask<Result> SaveAsync(
        OrganizationAccountGovernance aggregate,
        MembershipId actorMembershipId,
        DateTimeOffset occurredAt,
        PersistenceEffects effects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(effects);
        actorMembershipId.EnsureValid();
        if (occurredAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Persistence timestamps must be UTC.", nameof(occurredAt));
        }

        if (aggregate.Version < aggregate.OriginalVersion)
        {
            throw new InvalidOperationException("A governance aggregate version cannot move backwards.");
        }

        if (aggregate.Version == aggregate.OriginalVersion)
        {
            if (!PersistenceWriteSupport.IsEmpty(effects))
            {
                return ValueTask.FromResult(
                    Result.Failure(
                        DomainError.Conflict(
                            ErrorCodes.InvalidTransition,
                            "An unchanged governance aggregate cannot create side effects.")));
            }

            return ValueTask.FromResult(Result.Success());
        }

        return PersistenceWriteSupport.ExecuteWriteAsync(
            context,
            async token =>
            {
                int updated = await context.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE organizations
                    SET bootstrap_status = {(short)aggregate.BootstrapStatus},
                        bootstrap_sealed_at = {aggregate.BootstrapSealedAt},
                        version = {aggregate.Version}
                    WHERE id = {aggregate.OrganizationId.Value}
                      AND version = {aggregate.OriginalVersion}
                    """,
                    token);
                if (updated == 0)
                {
                    return StaleVersion();
                }

                List<MembershipEntity> membershipRows = await context.Memberships
                    .Where(candidate => candidate.OrganizationId == aggregate.OrganizationId.Value)
                    .ToListAsync(token);
                Dictionary<Guid, MembershipEntity> membershipsById =
                    membershipRows.ToDictionary(candidate => candidate.Id);
                if (membershipsById.Count != aggregate.Memberships.Count)
                {
                    throw new InvalidOperationException(
                        "Governance persistence requires the complete organization membership set.");
                }

                foreach (Membership membership in aggregate.Memberships)
                {
                    if (!membershipsById.TryGetValue(membership.Id.Value, out MembershipEntity? row))
                    {
                        throw new InvalidOperationException(
                            "Governance persistence cannot create or omit a membership outside the aggregate.");
                    }

                    row.Status = checked((short)membership.Status);
                }

                List<RoleAssignmentEntity> activeAssignments = await context.RoleAssignments
                    .Where(
                        candidate => candidate.OrganizationId == aggregate.OrganizationId.Value
                            && candidate.RevokedAt == null)
                    .ToListAsync(token);
                HashSet<(Guid MembershipId, short Role)> desiredRoles = aggregate.Memberships
                    .SelectMany(
                        membership => membership.ActiveRoles.Select(
                            role => (membership.Id.Value, checked((short)role))))
                    .ToHashSet();
                foreach (RoleAssignmentEntity assignment in activeAssignments)
                {
                    if (desiredRoles.Remove((assignment.MembershipId, assignment.Role)))
                    {
                        continue;
                    }

                    assignment.RevokedByMembershipId = actorMembershipId.Value;
                    assignment.RevokedAt = occurredAt;
                }

                foreach ((Guid membershipId, short role) in desiredRoles)
                {
                    context.RoleAssignments.Add(
                        new RoleAssignmentEntity
                        {
                            Id = Guid.CreateVersion7(),
                            OrganizationId = aggregate.OrganizationId.Value,
                            MembershipId = membershipId,
                            Role = role,
                            AssignedByMembershipId = actorMembershipId.Value,
                            AssignedAt = occurredAt,
                        });
                }

                List<RoleChangeRequestEntity> existingRequests = await context.RoleChangeRequests
                    .Where(candidate => candidate.OrganizationId == aggregate.OrganizationId.Value)
                    .ToListAsync(token);
                Dictionary<Guid, RoleChangeRequestEntity> requestsById =
                    existingRequests.ToDictionary(candidate => candidate.Id);
                foreach (RoleChangeRequest request in aggregate.RoleChangeRequests)
                {
                    if (!requestsById.TryGetValue(request.Id.Value, out RoleChangeRequestEntity? row))
                    {
                        context.RoleChangeRequests.Add(ToEntity(request));
                        continue;
                    }

                    EnsureImmutableRoleRequestColumns(row, request);
                    row.ApproverMembershipId = request.ApproverMembershipId?.Value;
                    row.ApprovedAt = request.ApprovedAt;
                    row.Status = checked((short)request.Status);
                }

                PersistenceWriteSupport.AddEffects(context, effects);
                return Result.Success();
            },
            cancellationToken);
    }

    private static Result<IReadOnlyCollection<Membership>> RehydrateMemberships(
        OrganizationEntity organization,
        IReadOnlyCollection<MembershipEntity> membershipRows,
        IReadOnlyCollection<RoleAssignmentEntity> activeRoleRows)
    {
        List<Membership> initialMemberships = [];
        foreach (MembershipEntity row in membershipRows)
        {
            Result<Membership> invited = HydrateMembership(row);
            if (invited.IsFailure)
            {
                return Result.Failure<IReadOnlyCollection<Membership>>(invited.Error);
            }

            Membership membership = invited.Value;
            if ((MembershipStatus)row.Status is MembershipStatus.Active or MembershipStatus.Disabled)
            {
                Result<MembershipActivated> activated = membership.Activate(DateTimeOffset.UnixEpoch);
                if (activated.IsFailure)
                {
                    return Result.Failure<IReadOnlyCollection<Membership>>(activated.Error);
                }
            }

            initialMemberships.Add(membership);
        }

        if (activeRoleRows.Count == 0
            && (AdministratorBootstrapStatus)organization.BootstrapStatus == AdministratorBootstrapStatus.Unsealed)
        {
            if (membershipRows.Any(row => (MembershipStatus)row.Status == MembershipStatus.Disabled))
            {
                return InvalidGovernanceState<IReadOnlyCollection<Membership>>(
                    "An unsealed organization cannot contain a disabled membership.");
            }

            return Result.Success<IReadOnlyCollection<Membership>>(initialMemberships);
        }

        Result<OrganizationAccountGovernance> builderResult = PersistenceHydration.Try(
            () => OrganizationAccountGovernance.Rehydrate(
                OrganizationId.From(organization.Id),
                version: 0,
                AdministratorBootstrapStatus.Unsealed,
                bootstrapSealedAt: null,
                initialMemberships,
                []),
            InvalidGovernanceState<OrganizationAccountGovernance>(
                "The persisted organization has an invalid identifier."));
        if (builderResult.IsFailure)
        {
            return Result.Failure<IReadOnlyCollection<Membership>>(builderResult.Error);
        }

        OrganizationAccountGovernance builder = builderResult.Value;
        HashSet<Guid> administratorIds = activeRoleRows
            .Where(assignment => assignment.Role == (short)OrganizationRole.Admin)
            .Select(assignment => assignment.MembershipId)
            .ToHashSet();
        Membership[] administrators = builder.Memberships
            .Where(membership => administratorIds.Contains(membership.Id.Value))
            .ToArray();
        Result<AdministratorBootstrapCompleted> bootstrap =
            builder.BootstrapAdministrators(
                administrators,
                organization.BootstrapSealedAt ?? DateTimeOffset.UnixEpoch);
        if (bootstrap.IsFailure)
        {
            return Result.Failure<IReadOnlyCollection<Membership>>(bootstrap.Error);
        }

        foreach (Guid foodInchargeId in activeRoleRows
                     .Where(assignment => assignment.Role == (short)OrganizationRole.FoodIncharge)
                     .Select(assignment => assignment.MembershipId))
        {
            Membership target = builder.Memberships.Single(membership => membership.Id.Value == foodInchargeId);
            Membership actor = builder.Memberships.First(
                membership => membership.Status == MembershipStatus.Active
                    && membership.HasRole(OrganizationRole.Admin)
                    && membership.Id != target.Id);
            Result<FoodInchargeAssigned> assigned =
                builder.AssignFoodIncharge(actor, target, DateTimeOffset.UnixEpoch);
            if (assigned.IsFailure)
            {
                return Result.Failure<IReadOnlyCollection<Membership>>(assigned.Error);
            }
        }

        foreach (Guid disabledMembershipId in membershipRows
                     .Where(row => (MembershipStatus)row.Status == MembershipStatus.Disabled)
                     .Select(row => row.Id))
        {
            Membership target = builder.Memberships.Single(membership => membership.Id.Value == disabledMembershipId);
            Membership actor = builder.Memberships.First(
                membership => membership.Status == MembershipStatus.Active
                    && membership.HasRole(OrganizationRole.Admin)
                    && membership.Id != target.Id);
            Result<MembershipDisabled> disabled =
                builder.DisableMembership(actor, target, DateTimeOffset.UnixEpoch);
            if (disabled.IsFailure)
            {
                return Result.Failure<IReadOnlyCollection<Membership>>(disabled.Error);
            }
        }

        return Result.Success<IReadOnlyCollection<Membership>>(builder.Memberships);
    }

    private static Result<RoleChangeRequest> HydrateRoleChangeRequest(RoleChangeRequestEntity entity) =>
        PersistenceHydration.Try(
            () => RoleChangeRequest.Rehydrate(
                RoleChangeRequestId.From(entity.Id),
                OrganizationId.From(entity.OrganizationId),
                MembershipId.From(entity.TargetMembershipId),
                (AdministratorRoleChangeAction)entity.Action,
                MembershipId.From(entity.ProposerMembershipId),
                entity.ApproverMembershipId.HasValue
                    ? MembershipId.From(entity.ApproverMembershipId.Value)
                    : null,
                entity.Reason,
                entity.ProposedAt,
                entity.ExpiresAt,
                entity.ApprovedAt,
                (RoleChangeRequestStatus)entity.Status),
            InvalidGovernanceState<RoleChangeRequest>(
                "The persisted role change request has an invalid identifier."));

    private static Result<Membership> HydrateMembership(MembershipEntity entity) =>
        PersistenceHydration.Try(
            () => Membership.Invite(
                MembershipId.From(entity.Id),
                OrganizationId.From(entity.OrganizationId),
                UserId.From(entity.UserId),
                entity.DisplayName,
                entity.EligibleAsNamedParticipant),
            InvalidGovernanceState<Membership>("The persisted membership has an invalid identifier."));

    private static Result<OrganizationAccountGovernance> HydrateGovernance(
        OrganizationEntity entity,
        IReadOnlyCollection<Membership> memberships,
        IReadOnlyCollection<RoleChangeRequest> requests) =>
        PersistenceHydration.Try(
            () => OrganizationAccountGovernance.Rehydrate(
                OrganizationId.From(entity.Id),
                entity.Version,
                (AdministratorBootstrapStatus)entity.BootstrapStatus,
                entity.BootstrapSealedAt,
                memberships,
                requests),
            InvalidGovernanceState<OrganizationAccountGovernance>(
                "The persisted organization has an invalid identifier."));

    private static RoleChangeRequestEntity ToEntity(RoleChangeRequest request) =>
        new()
        {
            Id = request.Id.Value,
            OrganizationId = request.OrganizationId.Value,
            TargetMembershipId = request.TargetMembershipId.Value,
            Action = checked((short)request.Action),
            ProposerMembershipId = request.ProposerMembershipId.Value,
            ApproverMembershipId = request.ApproverMembershipId?.Value,
            Reason = request.Reason,
            ProposedAt = request.ProposedAt,
            ExpiresAt = request.ExpiresAt,
            ApprovedAt = request.ApprovedAt,
            Status = checked((short)request.Status),
        };

    private static void EnsureImmutableRoleRequestColumns(
        RoleChangeRequestEntity row,
        RoleChangeRequest request)
    {
        if (row.OrganizationId != request.OrganizationId.Value
            || row.TargetMembershipId != request.TargetMembershipId.Value
            || row.Action != checked((short)request.Action)
            || row.ProposerMembershipId != request.ProposerMembershipId.Value
            || row.Reason != request.Reason
            || row.ProposedAt != request.ProposedAt
            || row.ExpiresAt != request.ExpiresAt)
        {
            throw new InvalidOperationException(
                "A persisted role change request changed an immutable aggregate-owned field.");
        }
    }

    private static Result StaleVersion() =>
        Result.Failure(
            DomainError.PreconditionFailed(
                ErrorCodes.StaleVersion,
                "The organization governance aggregate was changed by another writer."));

    private static Result<T> InvalidGovernanceState<T>(string message) =>
        Result.Failure<T>(
            DomainError.Validation(AccountErrorCodes.InvalidAccountGovernanceState, message));

    private static Result<OrganizationAccountGovernance> OrganizationNotFound() =>
        Result.Failure<OrganizationAccountGovernance>(
            DomainError.NotFound("organization_not_found", "The organization was not found."));

    private sealed record GovernanceHydrationRows(
        OrganizationEntity Organization,
        IReadOnlyCollection<MembershipEntity> Memberships,
        IReadOnlyCollection<RoleAssignmentEntity> ActiveRoleAssignments,
        IReadOnlyCollection<RoleChangeRequestEntity> RoleChangeRequests);
}

internal static class PersistenceHydration
{
    public static Result<T> Try<T>(Func<Result<T>> rehydrate, Result<T> invalidState)
    {
        try
        {
            return rehydrate();
        }
        catch (ArgumentException)
        {
            return invalidState;
        }
    }
}
