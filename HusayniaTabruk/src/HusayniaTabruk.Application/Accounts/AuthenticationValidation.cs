using System.Text;
using HusayniaTabruk.Application.Abstractions.Security;
using HusayniaTabruk.Domain.Common;

namespace HusayniaTabruk.Application.Accounts;

internal static class AuthenticationValidation
{
    private const int MaximumDisplayNameRunes = 200;
    private const int MaximumPurposeRunes = 100;

    public static Result ValidateInvitationAcceptance(AcceptInvitationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Token)
            || string.IsNullOrWhiteSpace(command.DisplayName)
            || string.IsNullOrWhiteSpace(command.Password))
        {
            return Result.Failure(
                AuthenticationErrorCodes.Validation(
                    AuthenticationErrorCodes.InvalidAuthInput,
                    "The invitation request is invalid."));
        }

        if (command.DisplayName.Trim().EnumerateRunes().Count() > MaximumDisplayNameRunes)
        {
            return Result.Failure(
                AuthenticationErrorCodes.Validation(
                    AuthenticationErrorCodes.InvalidAuthInput,
                    "The invitation request is invalid."));
        }

        return Result.Success();
    }

    public static Result ValidateLogin(LoginCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return string.IsNullOrWhiteSpace(command.Email) || string.IsNullOrWhiteSpace(command.Password)
            ? Result.Failure(
                AuthenticationErrorCodes.Validation(
                    AuthenticationErrorCodes.InvalidAuthInput,
                    "The login request is invalid."))
            : Result.Success();
    }

    public static Result ValidateRefresh(RefreshSessionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return string.IsNullOrWhiteSpace(command.RefreshToken)
            ? Result.Failure(
                AuthenticationErrorCodes.Validation(
                    AuthenticationErrorCodes.InvalidAuthInput,
                    "The refresh request is invalid."))
            : Result.Success();
    }

    public static Result ValidateLogout(LogoutCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return string.IsNullOrWhiteSpace(command.RefreshToken)
            ? Result.Failure(
                AuthenticationErrorCodes.Validation(
                    AuthenticationErrorCodes.InvalidAuthInput,
                    "The logout request is invalid."))
            : Result.Success();
    }

    public static Result<StepUpPurpose> ValidateStepUp(StepUpCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Password) || string.IsNullOrWhiteSpace(command.Purpose))
        {
            return Result.Failure<StepUpPurpose>(
                AuthenticationErrorCodes.Validation(
                    AuthenticationErrorCodes.InvalidAuthInput,
                    "The step-up request is invalid."));
        }

        string trimmedPurpose = command.Purpose.Trim();
        if (trimmedPurpose.EnumerateRunes().Count() > MaximumPurposeRunes)
        {
            return Result.Failure<StepUpPurpose>(
                AuthenticationErrorCodes.Validation(
                    AuthenticationErrorCodes.InvalidAuthInput,
                    "The step-up request is invalid."));
        }

        return Result.Success(new StepUpPurpose(trimmedPurpose));
    }
}
