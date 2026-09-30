using HusayniaTabruk.Domain.Common.Errors;

namespace HusayniaTabruk.Domain.Signups;

public static class SignupErrorCodes
{
    public const string InvalidSignupInput = "invalid_signup_input";
    public const string InvalidSignupAggregateState = "invalid_signup_aggregate_state";
    public const string SignupNotOwned = "signup_not_owned";
    public const string SignupChronologyInvalid = "signup_chronology_invalid";
    public const string IneligibleParticipant = "ineligible_participant";
    public const string SignupDeadlinePassed = "signup_deadline_passed";
    public const string VersionExhausted = "version_exhausted";

    internal static DomainError Conflict(string code, string message) =>
        DomainError.Conflict(code, message);

    internal static DomainError Validation(string code, string message) =>
        DomainError.Validation(code, message);
}
