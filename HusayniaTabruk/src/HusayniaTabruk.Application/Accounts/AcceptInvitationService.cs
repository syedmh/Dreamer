using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Domain.Common;

namespace HusayniaTabruk.Application.Accounts;

public sealed class AcceptInvitationService(
    IUnitOfWork unitOfWork,
    IMembershipRepository membershipRepository,
    IIdentityService identityService,
    ITokenService tokenService,
    IClock clock)
{
    public ValueTask<Result<TokenPair>> ExecuteAsync(
        AcceptInvitationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result validation = AuthenticationValidation.ValidateInvitationAcceptance(command);
        if (validation.IsFailure)
        {
            return ValueTask.FromResult(Result.Failure<TokenPair>(validation.Error));
        }

        return unitOfWork.ExecuteAsync(
            token => ExecuteCoreAsync(command, token),
            cancellationToken);
    }

    private async ValueTask<Result<TokenPair>> ExecuteCoreAsync(
        AcceptInvitationCommand command,
        CancellationToken cancellationToken)
    {
        Result<InvitationAcceptanceContext> invitation =
            await membershipRepository.GetInvitationAcceptanceContextAsync(
                command.Token,
                cancellationToken);
        if (invitation.IsFailure)
        {
            return Result.Failure<TokenPair>(invitation.Error);
        }

        Result passwordSet = await identityService.SetPasswordAsync(
            invitation.Value.UserId,
            command.Password,
            cancellationToken);
        if (passwordSet.IsFailure)
        {
            return Result.Failure<TokenPair>(passwordSet.Error);
        }

        Result accepted = await membershipRepository.AcceptInvitationAsync(
            command.Token,
            invitation.Value.MembershipId,
            command.DisplayName.Trim(),
            clock.UtcNow,
            cancellationToken);
        if (accepted.IsFailure)
        {
            return Result.Failure<TokenPair>(accepted.Error);
        }

        TokenPair tokens = await tokenService.IssueAsync(
            invitation.Value.UserId,
            invitation.Value.MembershipId,
            invitation.Value.OrganizationId,
            command.DeviceId,
            cancellationToken);

        return Result.Success(tokens);
    }
}
