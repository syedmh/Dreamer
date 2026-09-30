using HusayniaTabruk.Application.Abstractions;
using Npgsql;

namespace HusayniaTabruk.Infrastructure.Persistence.Repositories;

internal static class PostgresDependencyFailure
{
    internal static async ValueTask<T> ExecuteAsync<T>(Func<ValueTask<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            return await operation();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DependencyUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (Find<DependencyUnavailableException>(exception) is not null)
            {
                throw;
            }

            NpgsqlException? providerException = Find<NpgsqlException>(exception);
            if (providerException?.IsTransient == true)
            {
                throw new DependencyUnavailableException(providerException);
            }

            throw;
        }
    }

    private static TException? Find<TException>(Exception exception)
        where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException match)
            {
                return match;
            }
        }

        return null;
    }
}
