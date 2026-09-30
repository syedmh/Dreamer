using System.Text;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Dates;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;
using HusayniaTabruk.Infrastructure.Identity.Entities;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace HusayniaTabruk.Infrastructure.Persistence.Repositories;

public sealed class PostgresServiceDateRepository(TabrukDbContext context) : IServiceDateRepository
{
    public ValueTask<Result> CreateAsync(
        ServiceDate serviceDate,
        DatePersistenceEffects effects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceDate);
        ArgumentNullException.ThrowIfNull(effects);
        return PersistenceWriteSupport.ExecuteWriteAsync(
            context,
            token =>
            {
                context.ServiceDates.Add(ToEntity(serviceDate));
                context.HelpNeeds.AddRange(
                    serviceDate.HelpNeeds.Select(
                        need => ToEntity(need, serviceDate.OrganizationId.Value)));
                AddEffects(effects);
                return ValueTask.FromResult(Result.Success());
            },
            cancellationToken);
    }

    public ValueTask<Result<LoadedServiceDate>> GetAsync(
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => GetCoreAsync(organizationId, serviceDateId, cancellationToken));

    public ValueTask<Result<LoadedServiceDate>> GetByHelpNeedAsync(
        OrganizationId organizationId,
        HelpNeedId helpNeedId,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => GetByHelpNeedCoreAsync(organizationId, helpNeedId, cancellationToken));

    public ValueTask<Result> RevalidateManagedAuthorityAsync(
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        MembershipId actorMembershipId,
        CancellationToken cancellationToken = default)
    {
        organizationId.EnsureValid();
        serviceDateId.EnsureValid();
        actorMembershipId.EnsureValid();

        return PersistenceWriteSupport.ExecuteWriteAsync(
            context,
            async token =>
            {
                List<ServiceDateEntity> lockedDates = await context.ServiceDates
                    .FromSqlInterpolated(
                        $"""
                        SELECT *
                        FROM service_dates
                        WHERE id = {serviceDateId.Value}
                          AND organization_id = {organizationId.Value}
                        FOR UPDATE
                        """)
                    .AsNoTracking()
                    .ToListAsync(token);
                ServiceDateEntity? lockedDate = lockedDates.SingleOrDefault();
                if (lockedDate is null)
                {
                    return Result.Failure(DateApplicationErrorCodes.NotFound());
                }

                List<RoleAssignmentEntity> authority = await context.RoleAssignments
                    .FromSqlInterpolated(
                        $"""
                        SELECT role_assignment.*
                        FROM role_assignments AS role_assignment
                        INNER JOIN memberships AS membership
                            ON membership.organization_id = role_assignment.organization_id
                           AND membership.id = role_assignment.membership_id
                        WHERE role_assignment.organization_id = {organizationId.Value}
                          AND role_assignment.membership_id = {actorMembershipId.Value}
                          AND role_assignment.role = {(short)OrganizationRole.FoodIncharge}
                          AND role_assignment.revoked_at IS NULL
                          AND membership.status = {(short)MembershipStatus.Active}
                        FOR SHARE OF role_assignment, membership
                        """)
                    .AsNoTracking()
                    .ToListAsync(token);
                return authority.Count == 1
                       && lockedDate.ManagerMembershipId == actorMembershipId.Value
                    ? Result.Success()
                    : Result.Failure(DateApplicationErrorCodes.Forbidden());
            },
            cancellationToken);
    }

    public ValueTask<Result> SaveAsync(
        LoadedServiceDate loaded,
        DatePersistenceEffects effects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(effects);

        if (loaded.ServiceDate.Version != loaded.LoadedVersion + 1)
        {
            throw new InvalidOperationException(
                "A service-date save must represent exactly one Domain transition.");
        }

        return PersistenceWriteSupport.ExecuteWriteAsync(
            context,
            async token =>
            {
                ServiceDate date = loaded.ServiceDate;
                IReadOnlyDictionary<HelpNeedId, long> loadedVersions = loaded.LoadedHelpNeedVersions;
                List<HelpNeed> changedExistingNeeds = [];
                List<HelpNeed> newNeeds = [];
                HashSet<HelpNeedId> currentNeedIds = [];

                foreach (HelpNeed need in date.HelpNeeds.OrderBy(need => need.Id.ToString(), StringComparer.Ordinal))
                {
                    if (!currentNeedIds.Add(need.Id))
                    {
                        throw new InvalidOperationException(
                            "A service-date save duplicated a help need.");
                    }

                    if (loadedVersions.TryGetValue(need.Id, out long loadedNeedVersion))
                    {
                        if (need.Version == loadedNeedVersion)
                        {
                            continue;
                        }

                        if (need.Version != loadedNeedVersion + 1)
                        {
                            throw new InvalidOperationException(
                                "A service-date save must apply at most one transition to an existing help need.");
                        }

                        changedExistingNeeds.Add(need);
                        continue;
                    }

                    if (need.Version != 0)
                    {
                        throw new InvalidOperationException(
                            "A newly added help need must begin at version zero.");
                    }

                    newNeeds.Add(need);
                }

                if (loadedVersions.Keys.Any(loadedNeedId => !currentNeedIds.Contains(loadedNeedId)))
                {
                    throw new InvalidOperationException(
                        "A service-date save omitted a persisted help need.");
                }

                int updated = await context.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE service_dates
                    SET title = {date.Title},
                        instructions = {date.Instructions},
                        starts_at = {date.StartsAt},
                        ends_at = {date.EndsAt},
                        cancellation_deadline_at = {date.CancellationDeadlineAt},
                        status = {(short)date.Status},
                        version = {date.Version}
                    WHERE id = {date.Id.Value}
                      AND organization_id = {date.OrganizationId.Value}
                      AND version = {loaded.LoadedVersion}
                    """,
                    token);
                if (updated == 0)
                {
                    return Result.Failure(DateApplicationErrorCodes.StaleVersion());
                }

                foreach (HelpNeed need in changedExistingNeeds)
                {
                    int needUpdated = await context.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        UPDATE help_needs
                        SET instructions = {need.Instructions},
                            capacity = {need.Capacity},
                            status = {(short)need.Status},
                            version = {need.Version}
                        WHERE id = {need.Id.Value}
                          AND organization_id = {date.OrganizationId.Value}
                          AND service_date_id = {date.Id.Value}
                          AND version = {loadedVersions[need.Id]}
                        """,
                        token);
                    if (needUpdated == 0)
                    {
                        return Result.Failure(DateApplicationErrorCodes.StaleVersion());
                    }
                }

                foreach (HelpNeed need in newNeeds)
                {
                    context.HelpNeeds.Add(ToEntity(need, date.OrganizationId.Value));
                }

                AddEffects(effects);
                return Result.Success();
            },
            cancellationToken);
    }

    public ValueTask<Result> SaveNeedAsync(
        LoadedServiceDate loaded,
        HelpNeedId helpNeedId,
        long expectedHelpNeedVersion,
        long? expectedSignupVersion,
        DatePersistenceEffects effects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(effects);
        helpNeedId.EnsureValid();

        HelpNeed changedNeed = loaded.ServiceDate.HelpNeeds.SingleOrDefault(
                                  need => need.Id == helpNeedId)
                              ?? throw new InvalidOperationException(
                                  "The loaded service date does not contain the edited help need.");
        if (changedNeed.Version != expectedHelpNeedVersion + 1)
        {
            throw new InvalidOperationException(
                "A help-need save must represent exactly one Domain transition from the loaded version.");
        }

        return PersistenceWriteSupport.ExecuteWriteAsync(
            context,
            async token =>
            {
                int updated = await context.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE help_needs
                    SET instructions = {changedNeed.Instructions},
                        capacity = {changedNeed.Capacity},
                        status = {(short)changedNeed.Status},
                        version = {changedNeed.Version}
                    WHERE id = {helpNeedId.Value}
                      AND organization_id = {loaded.ServiceDate.OrganizationId.Value}
                      AND service_date_id = {loaded.ServiceDate.Id.Value}
                      AND version = {expectedHelpNeedVersion}
                      AND ({!expectedSignupVersion.HasValue}
                           OR signup_version = {expectedSignupVersion.GetValueOrDefault()})
                    """,
                    token);
                if (updated == 0)
                {
                    return Result.Failure(DateApplicationErrorCodes.StaleVersion());
                }

                AddEffects(effects);
                return Result.Success();
            },
            cancellationToken);
    }

    public ValueTask<Result<OpenServiceDatesPage>> ListOpenAsync(
        OrganizationId organizationId,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => ListOpenCoreAsync(organizationId, cursor, pageSize, cancellationToken));

    private async ValueTask<Result<LoadedServiceDate>> GetCoreAsync(
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        CancellationToken cancellationToken)
    {
        ServiceDateEntity? row = await context.ServiceDates
            .AsNoTracking()
            .SingleOrDefaultAsync(
                date => date.OrganizationId == organizationId.Value
                    && date.Id == serviceDateId.Value,
                cancellationToken);
        if (row is null)
        {
            return Result.Failure<LoadedServiceDate>(DateApplicationErrorCodes.NotFound());
        }

        List<HelpNeedEntity> needs = await context.HelpNeeds
            .AsNoTracking()
            .Where(
                need => need.OrganizationId == organizationId.Value
                    && need.ServiceDateId == serviceDateId.Value)
            .OrderBy(need => need.Category)
            .ToListAsync(cancellationToken);
        Result<ServiceDate> hydrated = Hydrate(row, needs);
        IReadOnlyDictionary<HelpNeedId, int?> availability = await CalculateAvailabilityAsync(
            organizationId.Value,
            serviceDateId.Value,
            needs,
            cancellationToken);
        IReadOnlyDictionary<HelpNeedId, long> loadedHelpNeedVersions = needs.ToDictionary(
            need => HelpNeedId.From(need.Id),
            need => need.Version);
        return hydrated.IsSuccess
            ? Result.Success(
                new LoadedServiceDate(
                    hydrated.Value,
                    row.Version,
                    availability,
                    loadedHelpNeedVersions))
            : Result.Failure<LoadedServiceDate>(hydrated.Error);
    }

    private async ValueTask<Result<LoadedServiceDate>> GetByHelpNeedCoreAsync(
        OrganizationId organizationId,
        HelpNeedId helpNeedId,
        CancellationToken cancellationToken)
    {
        HelpNeedEntity? targetNeed = await context.HelpNeeds
            .AsNoTracking()
            .SingleOrDefaultAsync(
                need => need.OrganizationId == organizationId.Value
                    && need.Id == helpNeedId.Value,
                cancellationToken);
        if (targetNeed is null)
        {
            return Result.Failure<LoadedServiceDate>(DateApplicationErrorCodes.NeedNotFound());
        }

        return await GetCoreAsync(
            organizationId,
            ServiceDateId.From(targetNeed.ServiceDateId),
            cancellationToken);
    }

    private async ValueTask<Result<OpenServiceDatesPage>> ListOpenCoreAsync(
        OrganizationId organizationId,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        Result<int> start = DecodeCursor(cursor);
        if (start.IsFailure)
        {
            return Result.Failure<OpenServiceDatesPage>(start.Error);
        }

        int take = pageSize ?? ApplicationLimits.DefaultPageSize;
        if (take < 1)
        {
            return Result.Failure<OpenServiceDatesPage>(
                DateApplicationErrorCodes.Validation("Page size must be positive."));
        }

        take = Math.Min(take, ApplicationLimits.MaximumPageSize);
        List<ServiceDateEntity> rows = await context.ServiceDates
            .AsNoTracking()
            .Where(
                date => date.OrganizationId == organizationId.Value
                    && date.Status == (short)ServiceDateStatus.Open)
            .OrderBy(date => date.StartsAt)
            .ThenBy(date => date.Id)
            .Skip(start.Value)
            .Take(take + 1)
            .ToListAsync(cancellationToken);

        bool hasMore = rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        Guid[] ids = rows.Select(row => row.Id).ToArray();
        List<HelpNeedEntity> needs = await context.HelpNeeds
            .AsNoTracking()
            .Where(
                need => need.OrganizationId == organizationId.Value
                    && ids.Contains(need.ServiceDateId))
            .OrderBy(need => need.Category)
            .ToListAsync(cancellationToken);

        List<ServiceDateSummary> summaries = [];
        foreach (ServiceDateEntity row in rows)
        {
            Result<ServiceDate> hydrated = Hydrate(
                row,
                needs.Where(need => need.ServiceDateId == row.Id).ToArray());
            if (hydrated.IsFailure)
            {
                return Result.Failure<OpenServiceDatesPage>(hydrated.Error);
            }

            IReadOnlyDictionary<HelpNeedId, int?> availability = await CalculateAvailabilityAsync(
                organizationId.Value,
                row.Id,
                needs.Where(need => need.ServiceDateId == row.Id).ToArray(),
                cancellationToken);
            summaries.Add(ToSummary(hydrated.Value, availability));
        }

        return Result.Success(
            new OpenServiceDatesPage(
                summaries,
                hasMore ? EncodeCursor(start.Value + rows.Count) : null));
    }

    private void AddEffects(DatePersistenceEffects effects)
    {
        PersistenceWriteSupport.AddEffects(
            context,
            new PersistenceEffects([], effects.AuditEntries, [], effects.OutboxMessages));
    }

    private static Result<ServiceDate> Hydrate(
        ServiceDateEntity row,
        IReadOnlyCollection<HelpNeedEntity> needRows)
    {
        List<HelpNeed> needs = [];
        foreach (HelpNeedEntity needRow in needRows)
        {
            Result<HelpNeed> need = PersistenceHydration.Try(
                () => HelpNeed.Rehydrate(
                    HelpNeedId.From(needRow.Id),
                    ServiceDateId.From(needRow.ServiceDateId),
                    (HelpCategory)needRow.Category,
                    needRow.Instructions,
                    needRow.Capacity,
                    (HelpNeedStatus)needRow.Status,
                    needRow.Version),
                InvalidHydration<HelpNeed>());
            if (need.IsFailure)
            {
                return Result.Failure<ServiceDate>(need.Error);
            }

            needs.Add(need.Value);
        }

        return PersistenceHydration.Try(
            () => ServiceDate.Rehydrate(
                ServiceDateId.From(row.Id),
                OrganizationId.From(row.OrganizationId),
                row.Title,
                row.Instructions,
                row.StartsAt,
                row.EndsAt,
                row.CancellationDeadlineAt,
                MembershipId.From(row.ManagerMembershipId),
                (ServiceDateStatus)row.Status,
                row.Version,
                needs),
            InvalidHydration<ServiceDate>());
    }

    private static ServiceDateEntity ToEntity(ServiceDate date) =>
        new()
        {
            Id = date.Id.Value,
            OrganizationId = date.OrganizationId.Value,
            Title = date.Title,
            Instructions = date.Instructions,
            StartsAt = date.StartsAt,
            EndsAt = date.EndsAt,
            CancellationDeadlineAt = date.CancellationDeadlineAt,
            ManagerMembershipId = date.ManagerMembershipId.Value,
            Status = checked((short)date.Status),
            Version = date.Version,
        };

    private static HelpNeedEntity ToEntity(HelpNeed need, Guid organizationId) =>
        new()
        {
            Id = need.Id.Value,
            OrganizationId = organizationId,
            ServiceDateId = need.ServiceDateId.Value,
            Category = checked((short)need.Category),
            Instructions = need.Instructions,
            Capacity = need.Capacity,
            Status = checked((short)need.Status),
            Version = need.Version,
            SignupVersion = 0,
            WaitlistOrderHighWater = 0,
        };

    private static void EnsureImmutable(HelpNeedEntity row, HelpNeed need)
    {
        if (row.ServiceDateId != need.ServiceDateId.Value
            || row.Category != (short)need.Category)
        {
            throw new InvalidOperationException("A persisted help need changed an immutable field.");
        }
    }

    private static ServiceDateSummary ToSummary(
        ServiceDate date,
        IReadOnlyDictionary<HelpNeedId, int?> availability) =>
        new(
            date.Id,
            date.Title,
            date.Instructions,
            date.StartsAt,
            date.EndsAt,
            date.CancellationDeadlineAt,
            date.ManagerMembershipId,
            date.Status,
            date.Version,
            date.HelpNeeds
                .Where(need => need.Status == HelpNeedStatus.Open)
                .Select(
                    need => new HelpNeedSummary(
                        need.Id,
                        need.Category,
                        need.Instructions,
                        availability.GetValueOrDefault(need.Id, need.Capacity),
                        need.Status,
                        need.Version))
                .ToArray());

    private async ValueTask<IReadOnlyDictionary<HelpNeedId, int?>> CalculateAvailabilityAsync(
        Guid organizationId,
        Guid serviceDateId,
        IReadOnlyCollection<HelpNeedEntity> needs,
        CancellationToken cancellationToken)
    {
        Guid[] needIds = needs.Select(need => need.Id).ToArray();
        List<SignupEntity> approved = await context.Signups
            .AsNoTracking()
            .Where(
                signup => signup.OrganizationId == organizationId
                    && signup.ServiceDateId == serviceDateId
                    && needIds.Contains(signup.HelpNeedId)
                    && signup.Status == (short)SignupStatus.Approved)
            .ToListAsync(cancellationToken);
        Guid[] signupIds = approved.Select(signup => signup.Id).ToArray();
        Dictionary<Guid, int> referencedMembers = await context.SignupMemberParticipants
            .AsNoTracking()
            .Where(participant => signupIds.Contains(participant.SignupId))
            .GroupBy(participant => participant.SignupId)
            .ToDictionaryAsync(group => group.Key, group => group.Count(), cancellationToken);

        Dictionary<HelpNeedId, int?> result = [];
        foreach (HelpNeedEntity need in needs)
        {
            int approvedParticipants = approved
                .Where(signup => signup.HelpNeedId == need.Id)
                .Sum(
                    signup => 1
                        + signup.UnnamedParticipantCount
                        + referencedMembers.GetValueOrDefault(signup.Id));
            result[HelpNeedId.From(need.Id)] = need.Capacity is null
                ? null
                : Math.Max(0, need.Capacity.Value - approvedParticipants);
        }

        return result;
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
            return int.TryParse(decoded, out int value) && value >= 0
                ? Result.Success(value)
                : Result.Failure<int>(DateApplicationErrorCodes.Validation("The cursor is invalid."));
        }
        catch (FormatException)
        {
            return Result.Failure<int>(DateApplicationErrorCodes.Validation("The cursor is invalid."));
        }
    }

    private static string EncodeCursor(int value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static Result<T> InvalidHydration<T>() =>
        Result.Failure<T>(
            DomainError.Validation(
                DateApplicationErrorCodes.InvalidDateRequest,
                $"The persisted {typeof(T).Name} is invalid."));
}
