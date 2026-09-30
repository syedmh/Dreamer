using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Signups.Queries;

public sealed record SignupParticipantSummary(
    MembershipId MembershipId,
    string DisplayName);

public sealed record SignupSummary(
    SignupId Id,
    ServiceDateId ServiceDateId,
    HelpNeedId HelpNeedId,
    HelpCategory Category,
    SignupParticipantSummary PrimaryContact,
    SignupKind Kind,
    string? Label,
    IReadOnlyCollection<SignupParticipantSummary> MemberParticipants,
    int UnnamedParticipantCount,
    int TotalParticipantCount,
    SignupStatus Status,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? LastTransitionAt,
    long? WaitlistOrder,
    long Version,
    long SignupVersion);

public sealed record SignupPage(
    IReadOnlyList<SignupSummary> Items,
    string? NextCursor);

public static class SignupApplicationErrorCodes
{
    public const string InvalidSignupRequest = "invalid_signup_input";
    public const string HelpNeedNotFound = "help_need_not_found";
    public const string SignupNotFound = "signup_not_found";
    public const string IdempotencyInProgress = "idempotency_in_progress";
    public const string IdempotencyMismatch = "idempotency_mismatch";
}
