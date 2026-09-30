using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Security;
using HusayniaTabruk.Domain.Common;

namespace HusayniaTabruk.Application.Accounts;

public sealed class IssueStepUpService(
    ICurrentActor currentActor,
    IStepUpVerifier stepUpVerifier)
{
    public ValueTask<Result<StepUpGrant>> ExecuteAsync(
        StepUpCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<StepUpPurpose> validation = AuthenticationValidation.ValidateStepUp(command);
        if (validation.IsFailure)
        {
            return ValueTask.FromResult(Result.Failure<StepUpGrant>(validation.Error));
        }

        return stepUpVerifier.IssueAsync(
            currentActor.UserId,
            command.Password,
            validation.Value,
            cancellationToken);
    }
}
