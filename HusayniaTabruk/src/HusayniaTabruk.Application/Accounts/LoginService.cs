using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Accounts;

public sealed class LoginService(
    IUnitOfWork unitOfWork,
    IIdentityService identityService,
    IMembershipRepository membershipRepository,
    ITokenService tokenService)
{
    public ValueTask<Result<TokenPair>> ExecuteAsync(
        LoginCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result validation = AuthenticationValidation.ValidateLogin(command);
        if (validation.IsFailure)
        {
            return ValueTask.FromResult(Result.Failure<TokenPair>(validation.Error));
        }

        return ExecuteCoreAsync(command, cancellationToken);
    }

    private async ValueTask<Result<TokenPair>> ExecuteCoreAsync(
        LoginCommand command,
        CancellationToken cancellationToken)
    {
        Result<UserId> verified = await identityService.VerifyCredentialsAsync(
            command.Email,
            command.Password,
            cancellationToken);
        if (verified.IsFailure)
        {
            return Result.Failure<TokenPair>(verified.Error);
        }

        Result<ActiveMembershipContext> membership =
            await membershipRepository.GetActiveMembershipAsync(
                verified.Value,
                cancellationToken);
        if (membership.IsFailure)
        {
            return Result.Failure<TokenPair>(membership.Error);
        }

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                TokenPair tokens = await tokenService.IssueAsync(
                    membership.Value.UserId,
                    membership.Value.MembershipId,
                    membership.Value.OrganizationId,
                    command.DeviceId,
                    token);
                return Result.Success(tokens);
            },
            cancellationToken);
    }
}
