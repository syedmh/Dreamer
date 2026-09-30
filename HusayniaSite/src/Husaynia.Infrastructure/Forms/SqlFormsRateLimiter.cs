using System.Data;
using Husaynia.Application.Forms;
using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Husaynia.Infrastructure.Forms;

public sealed class SqlFormsRateLimiter(
    HusayniaDbContext dbContext,
    FormsOptions options,
    TimeProvider timeProvider,
    ILogger<SqlFormsRateLimiter> logger) : IFormsRateLimiter
{
    private const int CleanupBatchSize = 32;
    private const int MaximumAttempts = 4;
    private static readonly Action<ILogger, int, Exception?> LogRetry =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(1, nameof(LogRetry)),
            "Retrying a Forms rate-limit transaction after SQL error {SqlErrorNumber}.");
    private static readonly Action<ILogger, string, Exception?> LogCleanupFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2, nameof(LogCleanupFailure)),
            "Forms rate-limit cleanup failed with {ExceptionType}; stale rows remain non-counting.");
    private readonly HusayniaDbContext dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly FormsOptions options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<FormRateLimitDecision> AttemptAsync(
        string formKey,
        ReadOnlyMemory<byte> clientFingerprint,
        CancellationToken cancellationToken)
    {
        ValidatePartition(formKey, clientFingerprint);
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var connection = (SqlConnection)dbContext.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;

        try
        {
            if (closeConnection)
            {
                await dbContext.Database.OpenConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                try
                {
                    var decision = await AttemptOnceAsync(
                            formKey,
                            clientFingerprint,
                            now,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await CleanupAsync(now, cancellationToken).ConfigureAwait(false);
                    return decision;
                }
                catch (SqlException exception)
                    when (IsRetryable(exception) && attempt < MaximumAttempts)
                {
                    LogRetry(logger, exception.Number, null);
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(5 * attempt),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException exception)
                    when (IsCompletedTransaction(exception) &&
                        attempt < MaximumAttempts)
                {
                    LogRetry(logger, 0, null);
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(5 * attempt),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException("The bounded Forms rate-limit retry did not complete.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FormsRateLimitDependencyException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new FormsRateLimitDependencyException(exception);
        }
        finally
        {
            if (closeConnection && connection.State != ConnectionState.Closed)
            {
                await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<FormRateLimitDecision> AttemptOnceAsync(
        string formKey,
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
            await AcquirePartitionLockAsync(
                    connection,
                    sqlTransaction,
                    formKey,
                    clientFingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
            var row = await ReadAsync(
                    connection,
                    sqlTransaction,
                    formKey,
                    clientFingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
            FormRateLimitDecision decision;
            if (row is null || now >= row.WindowEndsAtUtc)
            {
                var windowEndsAtUtc = now.Add(options.RateLimitWindow);
                if (row is null)
                {
                    await InsertAsync(
                            connection,
                            sqlTransaction,
                            formKey,
                            clientFingerprint,
                            now,
                            windowEndsAtUtc,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await ResetAsync(
                            connection,
                            sqlTransaction,
                            row.Id,
                            now,
                            windowEndsAtUtc,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                decision = CreateDecision(true, windowEndsAtUtc, 1);
            }
            else if (row.RequestCount < options.RateLimitPermitLimit)
            {
                var count = checked(row.RequestCount + 1);
                await IncrementAsync(
                        connection,
                        sqlTransaction,
                        row.Id,
                        count,
                        cancellationToken)
                    .ConfigureAwait(false);
                decision = CreateDecision(true, row.WindowEndsAtUtc, count);
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
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }
    }

    private async Task CleanupAsync(
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
                 FROM [dbo].[FormRateLimits]
                 WHERE [RetainUntilUtc] <= @now;
                 """;
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTimeOffset)
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

    private static async Task<RateLimitRow?> ReadAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string formKey,
        ReadOnlyMemory<byte> fingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT [Id], [WindowEndsAtUtc], [RequestCount]
            FROM [dbo].[FormRateLimits] WITH (UPDLOCK, HOLDLOCK)
            WHERE [FormKey] = @formKey AND [ClientFingerprint] = @fingerprint;
            """;
        AddPartition(command, formKey, fingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new RateLimitRow(
                reader.GetInt64(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetInt32(2))
            : null;
    }

    private static async Task AcquirePartitionLockAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string formKey,
        ReadOnlyMemory<byte> fingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = 5000;
            SELECT @result;
            """;
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255)
        {
            Value = $"forms-rate:{formKey}:{Convert.ToHexString(fingerprint.Span)}",
        });
        var result = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (result < 0)
        {
            throw new TimeoutException("The Forms rate-limit partition lock could not be acquired.");
        }
    }

    private async Task InsertAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string formKey,
        ReadOnlyMemory<byte> fingerprint,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO [dbo].[FormRateLimits]
                ([FormKey], [ClientFingerprint], [WindowStartedAtUtc], [WindowEndsAtUtc],
                 [RequestCount], [RetainUntilUtc])
            VALUES
                (@formKey, @fingerprint, @startedAtUtc, @endsAtUtc, 1, @retainUntilUtc);
            """;
        AddPartition(command, formKey, fingerprint);
        AddWindow(command, startedAtUtc, endsAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ResetAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long id,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE [dbo].[FormRateLimits]
            SET [WindowStartedAtUtc] = @startedAtUtc,
                [WindowEndsAtUtc] = @endsAtUtc,
                [RequestCount] = 1,
                [RetainUntilUtc] = @retainUntilUtc
            WHERE [Id] = @id;
            """;
        AddWindow(command, startedAtUtc, endsAtUtc);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.BigInt) { Value = id });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task IncrementAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long id,
        int count,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE [dbo].[FormRateLimits]
            SET [RequestCount] = @count
            WHERE [Id] = @id;
            """;
        command.Parameters.Add(new SqlParameter("@count", SqlDbType.Int) { Value = count });
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.BigInt) { Value = id });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddPartition(
        SqlCommand command,
        string formKey,
        ReadOnlyMemory<byte> fingerprint)
    {
        command.Parameters.Add(new SqlParameter("@formKey", SqlDbType.VarChar, 100)
        {
            Value = formKey,
        });
        command.Parameters.Add(new SqlParameter("@fingerprint", SqlDbType.Binary, 32)
        {
            Value = fingerprint.ToArray(),
        });
    }

    private void AddWindow(
        SqlCommand command,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endsAtUtc)
    {
        command.Parameters.Add(new SqlParameter("@startedAtUtc", SqlDbType.DateTimeOffset)
        {
            Value = startedAtUtc,
        });
        command.Parameters.Add(new SqlParameter("@endsAtUtc", SqlDbType.DateTimeOffset)
        {
            Value = endsAtUtc,
        });
        command.Parameters.Add(new SqlParameter("@retainUntilUtc", SqlDbType.DateTimeOffset)
        {
            Value = endsAtUtc.Add(options.RateLimitRetention),
        });
    }

    private FormRateLimitDecision CreateDecision(
        bool allowed,
        DateTimeOffset endsAtUtc,
        int count) =>
        new(allowed, endsAtUtc, count, options.RateLimitPermitLimit);

    private static bool IsRetryable(SqlException exception) =>
        exception.Number is 1205 or 2601 or 2627;

    private static bool IsCompletedTransaction(InvalidOperationException exception) =>
        exception.Message.Contains(
            "transaction has completed",
            StringComparison.OrdinalIgnoreCase);

    private static void ValidatePartition(
        string formKey,
        ReadOnlyMemory<byte> fingerprint)
    {
        if (!FormSubmissionService.TryNormalizeFormKey(formKey, out _) ||
            fingerprint.Length != 32)
        {
            throw new ArgumentException("A valid Forms rate-limit partition is required.");
        }
    }

    private sealed record RateLimitRow(
        long Id,
        DateTimeOffset WindowEndsAtUtc,
        int RequestCount);
}
