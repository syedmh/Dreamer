using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Dates;

public sealed record CreateServiceDateCommand(
    string Title,
    string Instructions,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset CancellationDeadlineAt,
    MembershipId ManagerMembershipId);

public sealed record AddHelpNeedCommand(
    ServiceDateId ServiceDateId,
    HelpCategory Category,
    string Instructions,
    int? Capacity);

public sealed record OpenServiceDateCommand(
    ServiceDateId ServiceDateId,
    IdempotencyKey IdempotencyKey);

public sealed record ServiceDateSummary(
    ServiceDateId Id,
    string Title,
    string Instructions,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset CancellationDeadlineAt,
    MembershipId ManagerMembershipId,
    ServiceDateStatus Status,
    long Version,
    IReadOnlyCollection<HelpNeedSummary> HelpNeeds);

public sealed record HelpNeedSummary(
    HelpNeedId Id,
    HelpCategory Category,
    string Instructions,
    int? Availability,
    HelpNeedStatus Status,
    long Version);

public sealed record OpenServiceDatesPage(
    IReadOnlyList<ServiceDateSummary> Items,
    string? NextCursor);

public sealed record LoadedServiceDate(
    Domain.Dates.ServiceDate ServiceDate,
    long LoadedVersion,
    IReadOnlyDictionary<HelpNeedId, int?> Availability,
    IReadOnlyDictionary<HelpNeedId, long> LoadedHelpNeedVersions);

public sealed record DatePersistenceEffects(
    IReadOnlyCollection<AuditEntry> AuditEntries,
    IReadOnlyCollection<OutboxMessage> OutboxMessages)
{
    public static DatePersistenceEffects Empty { get; } = new([], []);
}

public static class DateApplicationErrorCodes
{
    public const string InvalidDateRequest = "invalid_date_request";
    public const string ServiceDateNotFound = "service_date_not_found";
    public const string HelpNeedNotFound = "help_need_not_found";
    public const string IdempotencyInProgress = "idempotency_in_progress";
    public const string IdempotencyMismatch = "idempotency_mismatch";

    public static DomainError Validation(string message) =>
        DomainError.Validation(InvalidDateRequest, message);

    public static DomainError NotFound() =>
        DomainError.NotFound(ServiceDateNotFound, "The service date was not found.");

    public static DomainError NeedNotFound() =>
        DomainError.NotFound(HelpNeedNotFound, "The help need was not found.");

    public static DomainError Forbidden() =>
        DomainError.Forbidden("forbidden", "The current actor is not authorized to manage this service date.");

    public static DomainError StaleVersion() =>
        DomainError.PreconditionFailed(
            ErrorCodes.StaleVersion,
            "The service date changed. Refresh and retry.");
}
