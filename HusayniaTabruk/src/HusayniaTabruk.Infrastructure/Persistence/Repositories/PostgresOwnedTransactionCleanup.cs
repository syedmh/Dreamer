using System.Collections.ObjectModel;
using Microsoft.EntityFrameworkCore.Storage;

namespace HusayniaTabruk.Infrastructure.Persistence.Repositories;

internal static class PostgresOwnedTransactionCleanup
{
    internal const string CleanupFailuresDataKey = "PostgresOwnedTransactionCleanup.Failures";

    public static async ValueTask CleanupAsync(
        TabrukDbContext context,
        IDbContextTransaction? transaction,
        bool rollbackOwnedTransaction,
        bool clearChangeTracker,
        Exception? primaryException)
    {
        ArgumentNullException.ThrowIfNull(context);

        List<Exception>? failures = null;

        if (rollbackOwnedTransaction && transaction is not null)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                AddFailure(ref failures, exception);
            }
        }

        if (clearChangeTracker)
        {
            try
            {
                context.ChangeTracker.Clear();
            }
            catch (Exception exception)
            {
                AddFailure(ref failures, exception);
            }
        }

        if (transaction is not null)
        {
            try
            {
                await transaction.DisposeAsync();
            }
            catch (Exception exception)
            {
                AddFailure(ref failures, exception);
            }
        }

        if (failures is null)
        {
            return;
        }

        if (primaryException is null)
        {
            throw CreateCleanupException(failures);
        }

        primaryException.Data[CleanupFailuresDataKey] = failures.AsReadOnly();
    }

    private static void AddFailure(ref List<Exception>? failures, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        failures ??= [];
        failures.Add(exception);
    }

    private static Exception CreateCleanupException(List<Exception> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);

        return failures.Count == 1
            ? failures[0]
            : new AggregateException(
                "Owned transaction cleanup failed in multiple steps.",
                new ReadOnlyCollection<Exception>(failures));
    }
}
