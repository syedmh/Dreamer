using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Signups;

namespace HusayniaTabruk.Application.Abstractions.Persistence;

public partial interface ISignupRepository
{
    ValueTask<Result> SaveAsync(
        HelpNeedSignups aggregate,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This signup repository does not support aggregate persistence.");
}
