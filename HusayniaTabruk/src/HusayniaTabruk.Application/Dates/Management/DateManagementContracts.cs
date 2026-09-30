using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Dates.Management;

public sealed record EditServiceDateCommand(
    ServiceDateId ServiceDateId,
    string Title,
    string Instructions,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset CancellationDeadlineAt);

public sealed record EditHelpNeedCommand(
    HelpNeedId HelpNeedId,
    string Instructions,
    int? Capacity,
    HelpNeedStatus Status);

public sealed record CloseServiceDateCommand(
    ServiceDateId ServiceDateId,
    string? Reason,
    IdempotencyKey IdempotencyKey);

public sealed record CancelServiceDateCommand(
    ServiceDateId ServiceDateId,
    string? Reason,
    IdempotencyKey IdempotencyKey);
