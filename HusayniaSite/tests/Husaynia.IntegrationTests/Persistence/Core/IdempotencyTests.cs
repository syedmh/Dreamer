using Husaynia.Application.Contracts;
using Husaynia.Infrastructure.Persistence.Core;
using Husaynia.Infrastructure.Persistence.Transactions;
using Microsoft.Data.SqlClient;

namespace Husaynia.IntegrationTests.Persistence.Core;

public sealed class IdempotencyTests
{
    [Fact]
    public async Task ConcurrentDuplicateCreatesOneReservationAndReturnsPriorReceipt()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(nameof(ConcurrentDuplicateCreatesOneReservationAndReturnsPriorReceipt));
        using var start = new Barrier(2);

        var first = Task.Run(() => RunAttemptAsync(database, start, "receipt-a"));
        var second = Task.Run(() => RunAttemptAsync(database, start, "receipt-b"));
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Single(results, result => result.Success.Outcome == IdempotencyOutcome.Acquired);
        Assert.Single(
            results,
            result => result.Success.Outcome == IdempotencyOutcome.DuplicateCompleted);
        Assert.Equal(1, await database.CountRowsAsync("PersistenceIdempotency"));

        await using (var verification = database.CreateContext())
        {
            var stored = await new EfIdempotencyStore(verification).FindAsync(
                "donation-webhook",
                "evt-123",
                CancellationToken.None);
            Assert.True(stored.IsSuccess);
            Assert.True(stored.Success.Receipt is "receipt-a" or "receipt-b");
        }

        var databaseName =
            new SqlConnectionStringBuilder(database.ConnectionString).InitialCatalog;
        await database.DisposeAsync();
        Assert.False(await DatabaseExistsAsync(databaseName));
    }

    [Fact]
    public async Task ReusingKeyForDifferentPayloadReturnsConflict()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(nameof(ReusingKeyForDifferentPayloadReturnsConflict));
        await using (var firstContext = database.CreateContext())
        {
            var store = new EfIdempotencyStore(firstContext);
            var first = await store.TryReserveAsync(
                "import",
                "source:version",
                "hash-one",
                CancellationToken.None);
            Assert.True(first.IsSuccess);
        }

        await using var secondContext = database.CreateContext();
        var duplicate = await new EfIdempotencyStore(secondContext).TryReserveAsync(
            "import",
            "source:version",
            "hash-two",
            CancellationToken.None);

        Assert.True(duplicate.IsFailure);
        Assert.Equal(IdempotencyErrorCode.PayloadConflict, duplicate.Error.Code);
        Assert.Equal(1, await database.CountRowsAsync("PersistenceIdempotency"));
    }

    private static async Task<Result<IdempotencyReservation, PersistenceError>> RunAttemptAsync(
        SqlServerTestDatabase database,
        Barrier start,
        string receipt)
    {
        await using var context = database.CreateContext();
        var store = new EfIdempotencyStore(context);
        var unitOfWork = new EfHusayniaUnitOfWork(context);

        return await unitOfWork.ExecuteAsync(
            async cancellationToken =>
            {
                start.SignalAndWait(cancellationToken);
                var reservation = await store.TryReserveAsync(
                    "donation-webhook",
                    "evt-123",
                    "request-hash",
                    cancellationToken);
                if (reservation.IsFailure)
                {
                    return Result.Fail<IdempotencyReservation, PersistenceError>(
                        new PersistenceError(
                            PersistenceErrorCode.UnexpectedFailure,
                            reservation.Error.Message));
                }

                if (reservation.Success.Outcome == IdempotencyOutcome.Acquired)
                {
                    var completion = await store.CompleteAsync(
                        reservation.Success.ReservationId,
                        receipt,
                        cancellationToken);
                    if (completion.IsFailure)
                    {
                        return Result.Fail<IdempotencyReservation, PersistenceError>(
                            new PersistenceError(
                                PersistenceErrorCode.UnexpectedFailure,
                                completion.Error.Message));
                    }
                }

                return Result.Succeed<IdempotencyReservation, PersistenceError>(
                    reservation.Success);
            },
            UnitOfWorkOptions.Default,
            CancellationToken.None);
    }

    private static async Task<bool> DatabaseExistsAsync(string databaseName)
    {
        var masterConnectionString = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB",
            InitialCatalog = "master",
            IntegratedSecurity = true,
            Encrypt = false,
            TrustServerCertificate = true,
            ConnectTimeout = 30,
        }.ConnectionString;
        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT_BIG(*) FROM sys.databases WHERE name = @databaseName";
        command.Parameters.AddWithValue("@databaseName", databaseName);
        return (long)(await command.ExecuteScalarAsync() ?? 0L) != 0;
    }
}
