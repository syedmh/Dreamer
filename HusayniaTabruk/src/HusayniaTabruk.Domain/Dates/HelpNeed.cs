using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Domain.Dates;

public sealed class HelpNeed
{
    private HelpNeed(
        HelpNeedId id,
        ServiceDateId serviceDateId,
        HelpCategory category,
        string instructions,
        int? capacity,
        HelpNeedStatus status,
        long version)
    {
        Id = id.EnsureValid();
        ServiceDateId = serviceDateId.EnsureValid();
        Category = category;
        Instructions = instructions;
        Capacity = capacity;
        Status = status;
        Version = version;
    }

    public HelpNeedId Id { get; }
    public ServiceDateId ServiceDateId { get; }
    public HelpCategory Category { get; }
    public string Instructions { get; private set; }
    public int? Capacity { get; private set; }
    public HelpNeedStatus Status { get; private set; }
    public long Version { get; private set; }

    public static Result<HelpNeed> Rehydrate(
        HelpNeedId id,
        ServiceDateId serviceDateId,
        HelpCategory category,
        string instructions,
        int? capacity,
        HelpNeedStatus status,
        long version)
    {
        id.EnsureValid();
        serviceDateId.EnsureValid();

        Result validation = ValidateState(category, instructions, capacity, status, version);
        return validation.IsFailure
            ? Result.Failure<HelpNeed>(validation.Error)
            : Result.Success(
                new HelpNeed(
                    id,
                    serviceDateId,
                    category,
                    instructions.Trim(),
                    capacity,
                    status,
                    version));
    }

    internal Result<HelpNeedChanged> Change(
        string instructions,
        int? capacity,
        HelpNeedStatus status,
        DateTimeOffset now)
    {
        EnsureUtc(now);

        if (Version == long.MaxValue)
        {
            return Result.Failure<HelpNeedChanged>(VersionExhausted());
        }

        Result validation = ValidateState(Category, instructions, capacity, status, Version + 1);
        if (validation.IsFailure)
        {
            return Result.Failure<HelpNeedChanged>(validation.Error);
        }

        string normalizedInstructions = instructions.Trim();
        if (Instructions == normalizedInstructions
            && Capacity == capacity
            && Status == status)
        {
            return Result.Failure<HelpNeedChanged>(
                DateErrorCodes.Conflict(
                    ErrorCodes.InvalidTransition,
                    "The help need change does not modify any value."));
        }

        Instructions = normalizedInstructions;
        Capacity = capacity;
        Status = status;
        Version++;

        return Result.Success(
            new HelpNeedChanged(Id, Instructions, Capacity, Status, now));
    }

    internal static Result<HelpNeed> Create(
        HelpNeedId id,
        ServiceDateId serviceDateId,
        HelpCategory category,
        string instructions,
        int? capacity) =>
        Rehydrate(
            id,
            serviceDateId,
            category,
            instructions,
            capacity,
            HelpNeedStatus.Open,
            version: 0);

    internal bool IsOpenWithExhaustedVersion =>
        Status == HelpNeedStatus.Open && Version == long.MaxValue;

    internal void Close()
    {
        if (IsOpenWithExhaustedVersion)
        {
            throw new InvalidOperationException("An exhausted help need cannot be closed.");
        }

        if (Status == HelpNeedStatus.Open)
        {
            Status = HelpNeedStatus.Closed;
            Version++;
        }
    }

    internal HelpNeed DeepCopy() =>
        new(Id, ServiceDateId, Category, Instructions, Capacity, Status, Version);

    private static Result ValidateState(
        HelpCategory category,
        string instructions,
        int? capacity,
        HelpNeedStatus status,
        long version)
    {
        if (!Enum.IsDefined(category)
            || !Enum.IsDefined(status)
            || string.IsNullOrWhiteSpace(instructions)
            || capacity is <= 0
            || version < 0)
        {
            return Result.Failure(
                DateErrorCodes.Validation(
                    DateErrorCodes.InvalidDateInput,
                    "Help need category, instructions, capacity, status, or version is invalid."));
        }

        if (status == HelpNeedStatus.Closed && version == 0)
        {
            return Result.Failure(
                DateErrorCodes.Validation(
                    DateErrorCodes.InvalidDateInput,
                    "A closed help need must include at least one transition."));
        }

        return Result.Success();
    }

    private static DomainError VersionExhausted() =>
        DateErrorCodes.Conflict(
            DateErrorCodes.VersionExhausted,
            "The help need version is exhausted.");

    private static void EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Domain timestamps must be UTC.", nameof(value));
        }
    }
}

public sealed record HelpNeedChanged(
    HelpNeedId HelpNeedId,
    string Instructions,
    int? Capacity,
    HelpNeedStatus Status,
    DateTimeOffset OccurredAt);
