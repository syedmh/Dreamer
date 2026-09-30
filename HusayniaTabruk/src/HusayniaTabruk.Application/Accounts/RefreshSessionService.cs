using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Domain.Common;

namespace HusayniaTabruk.Application.Accounts;

public sealed class RefreshSessionService(ITokenService tokenService)
{
    public ValueTask<Result<TokenPair>> ExecuteAsync(
        RefreshSessionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result validation = AuthenticationValidation.ValidateRefresh(command);
        if (validation.IsFailure)
        {
            return ValueTask.FromResult(Result.Failure<TokenPair>(validation.Error));
        }

        return tokenService.RotateAsync(
            command.RefreshToken,
            command.DeviceId,
            cancellationToken);
    }
}
