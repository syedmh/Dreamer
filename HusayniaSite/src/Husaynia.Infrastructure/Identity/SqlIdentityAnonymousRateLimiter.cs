using System.Data;
using Husaynia.Application.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Husaynia.Infrastructure.Identity;

public sealed class SqlIdentityAnonymousRateLimiter(
    HusayniaIdentityDbContext dbContext,
    IdentityModuleOptions options,
    TimeProvider timeProvider,
    ILogger<SqlIdentityAnonymousRateLimiter> logger)
    : IIdentityAnonymousRateLimiter
{
    private const int CleanupBatchSize = 32;
    private const int MaximumAttempts = 2;
    private static readonly Action<ILogger, int, Exception?> LogRetry =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(1, nameof(LogRetry)),
            "Retrying an anonymous identity rate-limit transaction after SQL error {SqlErrorNumber}.");
    private static readonly Action<ILogger, string, Exception?> LogCleanupFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2, nameof(LogCleanupFailure)),
            "Anonymous identity rate-limit cleanup failed with {ExceptionType}; stale rows remain non-counting.");
    private readonly IdentityAnonymousRateLimitOptions rateLimitOptions =
        options?.AnonymousRateLimit ?? throw new ArgumentNullException(nameof(options));

    public async Task<IdentityRateLimitDecision> AttemptAsync(
        string endpointFamily,
        ReadOnlyMemory<byte> clientFingerprint,
        CancellationToken cancellationToken)
    {
        ValidatePartition(endpointFamily, clientFingerprint);
        var now = timeProvider.GetUtcNow();
        var connection = (SqlConnection)dbContext.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;

        try
        {
            if (closeConnection)
            {
                await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            }

            for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                try
                {
                    var decision = await AttemptOnceAsync(
                            endpointFamily,
                            clientFingerprint,
                            now,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await CleanupExpiredRowsAsync(now, cancellationToken).ConfigureAwait(false);
                    return decision;
                }
                catch (SqlException exception)
                    when (IsRetryable(exception) && attempt < MaximumAttempts)
                {
                    LogRetry(logger, exception.Number, null);
                }
            }

            throw new InvalidOperationException("The bounded anonymous rate-limit retry loop did not complete.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IdentityAnonymousRateLimitDependencyException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new IdentityAnonymousRateLimitDependencyException(exception);
        }
        finally
        {
            if (closeConnection && connection.State != ConnectionState.Closed)
            {
                await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<IdentityRateLimitDecision> AttemptOnceAsync(
        string endpointFamily,
        ReadOnlyMemory<byte> clientFingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        var connection = (SqlConnection)dbContext.Database.GetDbConnection();
        var sqlTransaction = (SqlTransaction)transaction.GetDbTransaction();

        try
        {
            var row = await ReadPartitionAsync(
                    connection,
                    sqlTransaction,
                    endpointFamily,
                    clientFingerprint,
                    cancellationToken)
                .ConfigureAwait(false);

            IdentityRateLimitDecision decision;
            if (row is null)
            {
                var windowEndsAtUtc = now.Add(rateLimitOptions.Window);
                await InsertPartitionAsync(
                        connection,
                        sqlTransaction,
                        endpointFamily,
                        clientFingerprint,
                        now,
                        windowEndsAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
                decision = CreateDecision(true, windowEndsAtUtc, 1);
            }
            else if (now >= row.WindowEndsAtUtc)
            {
                var windowEndsAtUtc = now.Add(rateLimitOptions.Window);
                await ResetPartitionAsync(
                        connection,
                        sqlTransaction,
                        row.Id,
                        now,
                        windowEndsAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
                decision = CreateDecision(true, windowEndsAtUtc, 1);
            }
            else if (row.RequestCount < rateLimitOptions.PermitLimit)
            {
                var requestCount = checked(row.RequestCount + 1);
                await IncrementPartitionAsync(
                        connection,
                        sqlTransaction,
                        row.Id,
                        requestCount,
                        cancellationToken)
                    .ConfigureAwait(false);
                decision = CreateDecision(true, row.WindowEndsAtUtc, requestCount);
            }
            else
            {
                decision = CreateDecision(false, row.WindowEndsAtUtc, row.RequestCount);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return decision;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task CleanupExpiredRowsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            var connection = (SqlConnection)dbContext.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                DELETE TOP ({CleanupBatchSize})
                FROM [dbo].[IdentityAnonymousRateLimits]
                WHERE [RetainUntilUtc] <= @now;
                """;
            command.Parameters.Add(
                new SqlParameter("@now", SqlDbType.DateTimeOffset)
                {
                    Value = now,
                });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogCleanupFailure(logger, exception.GetType().Name, null);
        }
    }

    private static async Task<RateLimitRow?> ReadPartitionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string endpointFamily,
        ReadOnlyMemory<byte> clientFingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT [Id], [WindowEndsAtUtc], [RequestCount]
            FROM [dbo].[IdentityAnonymousRateLimits] WITH (UPDLOCK, HOLDLOCK)
            WHERE [EndpointFamily] = @endpointFamily
              AND [ClientFingerprint] = @clientFingerprint;
            """;
        AddPartitionParameters(command, endpointFamily, clientFingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new RateLimitRow(
            reader.GetInt64(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetInt32(2));
    }

    private async Task InsertPartitionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string endpointFamily,
        ReadOnlyMemory<byte> clientFingerprint,
        DateTimeOffset windowStartedAtUtc,
        DateTimeOffset windowEndsAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO [dbo].[IdentityAnonymousRateLimits]
                ([EndpointFamily], [ClientFingerprint], [WindowStartedAtUtc], [WindowEndsAtUtc],
                 [RequestCount], [RetainUntilUtc])
            VALUES
                (@endpointFamily, @clientFingerprint, @windowStartedAtUtc, @windowEndsAtUtc,
                 1, @retainUntilUtc);
            """;
        AddPartitionParameters(command, endpointFamily, clientFingerprint);
        command.Parameters.Add(new SqlParameter("@windowStartedAtUtc", SqlDbType.DateTimeOffset)
        {
            Value = windowStartedAtUtc,
        });
        command.Parameters.Add(new SqlParameter("@windowEndsAtUtc", SqlDbType.DateTimeOffset)
        {
            Value = windowEndsAtUtc,
        });
        command.Parameters.Add(new SqlParameter("@retainUntilUtc", SqlDbType.DateTimeOffset)
        {
            Value = windowEndsAtUtc.Add(rateLimitOptions.Retention),
        });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ResetPartitionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long id,
        DateTimeOffset windowStartedAtUtc,
        DateTimeOffset windowEndsAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE [dbo].[IdentityAnonymousRateLimits]
            SET [WindowStartedAtUtc] = @windowStartedAtUtc,
                [WindowEndsAtUtc] = @windowEndsAtUtc,
                [RequestCount] = 1,
                [RetainUntilUtc] = @retainUntilUtc
            WHERE [Id] = @id;
            """;
        command.Parameters.Add(new SqlParameter("@windowStartedAtUtc", SqlDbType.DateTimeOffset)
        {
            Value = windowStartedAtUtc,
        });
        command.Parameters.Add(new SqlParameter("@windowEndsAtUtc", SqlDbType.DateTimeOffset)
        {
            Value = windowEndsAtUtc,
        });
        command.Parameters.Add(new SqlParameter("@retainUntilUtc", SqlDbType.DateTimeOffset)
        {
            Value = windowEndsAtUtc.Add(rateLimitOptions.Retention),
        });
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.BigInt) { Value = id });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task IncrementPartitionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long id,
        int requestCount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE [dbo].[IdentityAnonymousRateLimits]
            SET [RequestCount] = @requestCount
            WHERE [Id] = @id;
            """;
        command.Parameters.Add(new SqlParameter("@requestCount", SqlDbType.Int) { Value = requestCount });
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.BigInt) { Value = id });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddPartitionParameters(
        SqlCommand command,
        string endpointFamily,
        ReadOnlyMemory<byte> clientFingerprint)
    {
        command.Parameters.Add(
            new SqlParameter("@endpointFamily", SqlDbType.NVarChar, 32)
            {
                Value = endpointFamily,
            });
        command.Parameters.Add(
            new SqlParameter("@clientFingerprint", SqlDbType.Binary, 32)
            {
                Value = clientFingerprint.ToArray(),
            });
    }

    private IdentityRateLimitDecision CreateDecision(
        bool isAllowed,
        DateTimeOffset windowEndsAtUtc,
        int requestCount) =>
        new(isAllowed, windowEndsAtUtc, requestCount, rateLimitOptions.PermitLimit);

    private static bool IsRetryable(SqlException exception) =>
        exception.Number is 2601 or 2627 or 1205;

    private static void ValidatePartition(
        string endpointFamily,
        ReadOnlyMemory<byte> clientFingerprint)
    {
        if (string.IsNullOrWhiteSpace(endpointFamily) || endpointFamily.Length > 32)
        {
            throw new ArgumentException(
                "A non-empty endpoint family of at most 32 characters is required.",
                nameof(endpointFamily));
        }

        if (clientFingerprint.Length != 32)
        {
            throw new ArgumentException(
                "The client fingerprint must contain exactly 32 bytes.",
                nameof(clientFingerprint));
        }
    }

    private sealed record RateLimitRow(
        long Id,
        DateTimeOffset WindowEndsAtUtc,
        int RequestCount);
}
