using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Roster;

public sealed record RosterPage(
    ServiceDateId ServiceDateId,
    IReadOnlyList<SignupSummary> Items,
    string? NextCursor);

public static class RosterApplicationErrorCodes
{
    public const string RosterNotFound = "roster_not_found";

    public static DomainError Concealed() =>
        DomainError.NotFound(
            RosterNotFound,
            "The roster was not found.");
}
