using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Domain.Common;

namespace HusayniaTabruk.Application.Accounts;

public sealed class LogoutService(ITokenService tokenService)
{
    public async ValueTask<Result> ExecuteAsync(
        LogoutCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result validation = AuthenticationValidation.ValidateLogout(command);
        if (validation.IsFailure)
        {
            return validation;
        }

        await tokenService.RevokeAsync(
            command.RefreshToken,
            command.DeviceId,
            cancellationToken);
        return Result.Success();
    }
}
