using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Domain.Dates;

public sealed class ServiceDate
{
    private readonly List<HelpNeed> helpNeeds;

    private ServiceDate(
        ServiceDateId id,
        OrganizationId organizationId,
        string title,
        string instructions,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        DateTimeOffset cancellationDeadlineAt,
        MembershipId managerMembershipId,
        ServiceDateStatus status,
        long version,
        IReadOnlyCollection<HelpNeed> helpNeeds)
    {
        Id = id.EnsureValid();
        OrganizationId = organizationId.EnsureValid();
        Title = title;
        Instructions = instructions;
        StartsAt = startsAt;
        EndsAt = endsAt;
        CancellationDeadlineAt = cancellationDeadlineAt;
        ManagerMembershipId = managerMembershipId.EnsureValid();
        Status = status;
        Version = version;
        this.helpNeeds = helpNeeds.Select(need => need.DeepCopy()).ToList();
    }

    public ServiceDateId Id { get; }
    public OrganizationId OrganizationId { get; }
    public string Title { get; private set; }
    public string Instructions { get; private set; }
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
    public DateTimeOffset CancellationDeadlineAt { get; private set; }
    public MembershipId ManagerMembershipId { get; }
    public ServiceDateStatus Status { get; private set; }
    public long Version { get; private set; }
    public IReadOnlyCollection<HelpNeed> HelpNeeds =>
        Array.AsReadOnly(helpNeeds.Select(need => need.DeepCopy()).ToArray());

    public static Result<ServiceDate> Create(
        ServiceDateId id,
        OrganizationId organizationId,
        string title,
        string instructions,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        DateTimeOffset cancellationDeadlineAt,
        MembershipId managerMembershipId)
    {
        id.EnsureValid();
        organizationId.EnsureValid();
        managerMembershipId.EnsureValid();
        EnsureUtc(startsAt);
        EnsureUtc(endsAt);
        EnsureUtc(cancellationDeadlineAt);

        Result validation = ValidateState(
            title,
            instructions,
            startsAt,
            endsAt,
            cancellationDeadlineAt,
            ServiceDateStatus.Draft,
            version: 0,
            []);
        return validation.IsFailure
            ? Result.Failure<ServiceDate>(validation.Error)
            : Result.Success(
                new ServiceDate(
                    id,
                    organizationId,
                    title.Trim(),
                    instructions.Trim(),
                    startsAt,
                    endsAt,
                    cancellationDeadlineAt,
                    managerMembershipId,
                    ServiceDateStatus.Draft,
                    version: 0,
                    []));
    }

    public static Result<ServiceDate> Rehydrate(
        ServiceDateId id,
        OrganizationId organizationId,
        string title,
        string instructions,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        DateTimeOffset cancellationDeadlineAt,
        MembershipId managerMembershipId,
        ServiceDateStatus status,
        long version,
        IReadOnlyCollection<HelpNeed> helpNeeds)
    {
        id.EnsureValid();
        organizationId.EnsureValid();
        managerMembershipId.EnsureValid();
        ArgumentNullException.ThrowIfNull(helpNeeds);
        EnsureUtc(startsAt);
        EnsureUtc(endsAt);
        EnsureUtc(cancellationDeadlineAt);

        Result validation = ValidateState(
            title,
            instructions,
            startsAt,
            endsAt,
            cancellationDeadlineAt,
            status,
            version,
            helpNeeds);
        if (validation.IsFailure)
        {
            return Result.Failure<ServiceDate>(validation.Error);
        }

        if (helpNeeds.Any(need => need is null)
            || helpNeeds.Any(need => need.ServiceDateId != id)
            || helpNeeds.Select(need => need.Id).Distinct().Count() != helpNeeds.Count
            || helpNeeds.Select(need => need.Category).Distinct().Count() != helpNeeds.Count
            || (status is ServiceDateStatus.Closed or ServiceDateStatus.Cancelled or ServiceDateStatus.Completed
                && helpNeeds.Any(need => need.Status == HelpNeedStatus.Open)))
        {
            return InvalidState("The service date help needs are inconsistent.");
        }

        return Result.Success(
            new ServiceDate(
                id,
                organizationId,
                title.Trim(),
                instructions.Trim(),
                startsAt,
                endsAt,
                cancellationDeadlineAt,
                managerMembershipId,
                status,
                version,
                helpNeeds));
    }

    public Result<HelpNeedAdded> AddNeed(
        HelpNeedId id,
        HelpCategory category,
        string instructions,
        int? capacity,
        DateTimeOffset now)
    {
        EnsureUtc(now);

        id.EnsureValid();

        if (Status is not (ServiceDateStatus.Draft or ServiceDateStatus.Open))
        {
            return Result.Failure<HelpNeedAdded>(
                DateErrorCodes.Conflict(
                    ErrorCodes.InvalidTransition,
                    $"A {Status} service date cannot accept a help need."));
        }

        if (now >= EndsAt)
        {
            return Result.Failure<HelpNeedAdded>(
                DateErrorCodes.Conflict(
                    ErrorCodes.InvalidTransition,
                    "A help need cannot be added at or after the service date end."));
        }

        if (Version == long.MaxValue)
        {
            return Result.Failure<HelpNeedAdded>(VersionExhausted());
        }

        if (helpNeeds.Any(need => need.Id == id))
        {
            return Result.Failure<HelpNeedAdded>(
                DateErrorCodes.Conflict(
                    DateErrorCodes.DuplicateHelpNeed,
                    "A service date cannot contain the same help need twice."));
        }

        if (helpNeeds.Any(need => need.Category == category))
        {
            return Result.Failure<HelpNeedAdded>(
                DateErrorCodes.Conflict(
                    DateErrorCodes.DuplicateHelpCategory,
                    "A service date cannot contain the same help category twice."));
        }

        Result<HelpNeed> created = HelpNeed.Create(id, Id, category, instructions, capacity);
        if (created.IsFailure)
        {
            return Result.Failure<HelpNeedAdded>(created.Error);
        }

        helpNeeds.Add(created.Value);
        Version++;
        return Result.Success(new HelpNeedAdded(Id, created.Value.DeepCopy(), now));
    }

    public Result<HelpNeedChanged> ChangeNeed(
        HelpNeedId id,
        string instructions,
        int? capacity,
        HelpNeedStatus status,
        DateTimeOffset now)
    {
        EnsureUtc(now);
        id.EnsureValid();

        if (Status is not (ServiceDateStatus.Draft or ServiceDateStatus.Open))
        {
            return Result.Failure<HelpNeedChanged>(
                DateErrorCodes.Conflict(
                    ErrorCodes.InvalidTransition,
                    $"A {Status} service date cannot change a help need."));
        }

        HelpNeed? need = helpNeeds.SingleOrDefault(candidate => candidate.Id == id);
        if (need is null)
        {
            return Result.Failure<HelpNeedChanged>(
                DateErrorCodes.Validation(
                    DateErrorCodes.InvalidDateInput,
                    "The help need is not owned by this service date."));
        }

        Result<HelpNeedChanged> changed = need.Change(
            instructions,
            capacity,
            status,
            now);
        return changed;
    }

    public Result<ServiceDateChanged> Edit(
        string title,
        string instructions,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        DateTimeOffset cancellationDeadlineAt)
    {
        EnsureUtc(startsAt);
        EnsureUtc(endsAt);
        EnsureUtc(cancellationDeadlineAt);

        if (Status is not (ServiceDateStatus.Draft or ServiceDateStatus.Open))
        {
            return Result.Failure<ServiceDateChanged>(
                DateErrorCodes.Conflict(
                    ErrorCodes.InvalidTransition,
                    $"A {Status} service date cannot be edited."));
        }

        if (Version == long.MaxValue)
        {
            return Result.Failure<ServiceDateChanged>(VersionExhausted());
        }

        Result validation = ValidateState(
            title,
            instructions,
            startsAt,
            endsAt,
            cancellationDeadlineAt,
            Status,
            Version + 1,
            helpNeeds);
        if (validation.IsFailure)
        {
            return Result.Failure<ServiceDateChanged>(validation.Error);
        }

        string normalizedTitle = title.Trim();
        string normalizedInstructions = instructions.Trim();
        if (Title == normalizedTitle
            && Instructions == normalizedInstructions
            && StartsAt == startsAt
            && EndsAt == endsAt
            && CancellationDeadlineAt == cancellationDeadlineAt)
        {
            return Result.Failure<ServiceDateChanged>(
                DateErrorCodes.Conflict(
                    ErrorCodes.InvalidTransition,
                    "The service date change does not modify any value."));
        }

        Title = normalizedTitle;
        Instructions = normalizedInstructions;
        StartsAt = startsAt;
        EndsAt = endsAt;
        CancellationDeadlineAt = cancellationDeadlineAt;
        Version++;

        return Result.Success(
            new ServiceDateChanged(
                Id,
                Title,
                Instructions,
                StartsAt,
                EndsAt,
                CancellationDeadlineAt));
    }

    public Result<ServiceDateOpened> Open(DateTimeOffset now)
    {
        EnsureUtc(now);

        if (Status != ServiceDateStatus.Draft || now >= StartsAt)
        {
            return Result.Failure<ServiceDateOpened>(
                DateErrorCodes.Conflict(
                    ErrorCodes.InvalidTransition,
                    "Only a draft service date may open before its start."));
        }

        if (Version == long.MaxValue)
        {
            return Result.Failure<ServiceDateOpened>(VersionExhausted());
        }

        Status = ServiceDateStatus.Open;
        Version++;
        return Result.Success(new ServiceDateOpened(Id, now));
    }

    public Result<ServiceDateClosed> Close(DateTimeOffset now)
    {
        EnsureUtc(now);

        if (Status != ServiceDateStatus.Open)
        {
            return Result.Failure<ServiceDateClosed>(
                DateErrorCodes.Conflict(
                    ErrorCodes.InvalidTransition,
                    "Only an open service date may close."));
        }

        if (Version == long.MaxValue || helpNeeds.Any(need => need.IsOpenWithExhaustedVersion))
        {
            return Result.Failure<ServiceDateClosed>(VersionExhausted());
        }

        CloseOpenNeeds();
        Status = ServiceDateStatus.Closed;
        Version++;
        return Result.Success(
            new ServiceDateClosed(
                Id,
                Array.AsReadOnly(helpNeeds.Select(need => need.Id).ToArray()),
                now));
    }

    public Result<ServiceDateCancelled> Cancel(DateTimeOffset now)
    {
        EnsureUtc(now);

        if (Status is ServiceDateStatus.Cancelled or ServiceDateStatus.Completed)
        {
            return Result.Failure<ServiceDateCancelled>(
                DateErrorCodes.Conflict(
                    ErrorCodes.InvalidTransition,
                    $"A {Status} service date cannot be cancelled."));
        }

        if (Version == long.MaxValue || helpNeeds.Any(need => need.IsOpenWithExhaustedVersion))
        {
            return Result.Failure<ServiceDateCancelled>(VersionExhausted());
        }

        CloseOpenNeeds();
        Status = ServiceDateStatus.Cancelled;
        Version++;
        return Result.Success(
            new ServiceDateCancelled(
                Id,
                Array.AsReadOnly(helpNeeds.Select(need => need.Id).ToArray()),
                now));
    }

    private static Result ValidateState(
        string title,
        string instructions,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        DateTimeOffset cancellationDeadlineAt,
        ServiceDateStatus status,
        long version,
        IReadOnlyCollection<HelpNeed> helpNeeds)
    {
        if (string.IsNullOrWhiteSpace(title)
            || string.IsNullOrWhiteSpace(instructions)
            || startsAt >= endsAt
            || cancellationDeadlineAt > startsAt
            || !Enum.IsDefined(status)
            || version < 0
            || helpNeeds.Any(need => need is null))
        {
            return Result.Failure(
                DateErrorCodes.Validation(
                    DateErrorCodes.InvalidDateInput,
                    "Service date text, chronology, status, version, or help needs are invalid."));
        }

        long needCount = helpNeeds.Count;
        bool hasReachableVersion = status switch
        {
            ServiceDateStatus.Draft => version >= needCount,
            ServiceDateStatus.Open => version >= needCount + 1,
            ServiceDateStatus.Closed => version >= needCount + 2,
            ServiceDateStatus.Cancelled => version >= needCount + 1,
            _ => false,
        };
        if (!hasReachableVersion)
        {
            return Result.Failure(
                DateErrorCodes.Validation(
                    DateErrorCodes.InvalidDateInput,
                    "The service date status and version do not represent reachable transition history."));
        }

        return Result.Success();
    }

    private static Result<ServiceDate> InvalidState(string message) =>
        Result.Failure<ServiceDate>(
            DateErrorCodes.Validation(DateErrorCodes.InvalidDateInput, message));

    private static DomainError VersionExhausted() =>
        DateErrorCodes.Conflict(
            DateErrorCodes.VersionExhausted,
            "The service date or one of its help need versions is exhausted.");

    private void CloseOpenNeeds()
    {
        foreach (HelpNeed need in helpNeeds)
        {
            need.Close();
        }
    }

    private static void EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Domain timestamps must be UTC.", nameof(value));
        }
    }
}

public sealed record HelpNeedAdded(
    ServiceDateId ServiceDateId,
    HelpNeed HelpNeed,
    DateTimeOffset OccurredAt);

public sealed record ServiceDateOpened(
    ServiceDateId ServiceDateId,
    DateTimeOffset OccurredAt);

public sealed record ServiceDateChanged(
    ServiceDateId ServiceDateId,
    string Title,
    string Instructions,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset CancellationDeadlineAt);

public sealed record ServiceDateClosed(
    ServiceDateId ServiceDateId,
    IReadOnlyCollection<HelpNeedId> RetainedHelpNeedIds,
    DateTimeOffset OccurredAt);

public sealed record ServiceDateCancelled(
    ServiceDateId ServiceDateId,
    IReadOnlyCollection<HelpNeedId> RetainedHelpNeedIds,
    DateTimeOffset OccurredAt);
