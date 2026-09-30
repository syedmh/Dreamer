using HusayniaTabruk.Domain.Common.Errors;

namespace HusayniaTabruk.Application.Accounts;

public static class AuthenticationErrorCodes
{
    public const string AuthenticationFailed = "authentication_failed";
    public const string InvalidAuthInput = "invalid_auth_input";
    public const string InvitationInvalid = "invitation_invalid";
    public const string RefreshTokenInvalid = "refresh_token_invalid";
    public const string RefreshTokenReused = "refresh_token_reused";
    public const string StepUpInvalid = "step_up_invalid";
    public const string StepUpPurposeMismatch = "step_up_purpose_mismatch";

    public static DomainError Validation(string code, string message) =>
        DomainError.Validation(code, message);

    public static DomainError Unauthorized(string code, string message) =>
        DomainError.Unauthorized(code, message);
}
