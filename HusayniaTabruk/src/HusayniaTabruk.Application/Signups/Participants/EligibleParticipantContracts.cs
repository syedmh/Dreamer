using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Signups.Participants;

public sealed record EligibleSignupParticipantSummary(
    MembershipId MembershipId,
    string DisplayName);

public sealed record EligibleSignupParticipantPage(
    IReadOnlyList<EligibleSignupParticipantSummary> Items,
    string? NextCursor);

public static class EligibleParticipantErrorCodes
{
    public const string InvalidParticipantQuery = "invalid_participant_query";

    public static DomainError Validation(string message) =>
        DomainError.Validation(InvalidParticipantQuery, message);
}
