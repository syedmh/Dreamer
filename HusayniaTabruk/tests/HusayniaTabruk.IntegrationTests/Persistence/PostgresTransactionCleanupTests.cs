using System.Data.Common;
using System.Reflection;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace HusayniaTabruk.IntegrationTests.Persistence;

[Trait("Category", "Persistence")]
public sealed class PostgresTransactionCleanupTests : PostgresPersistenceTest
{
    private const string CleanupFailureDataKey = "PostgresOwnedTransactionCleanup.Failures";
    private static readonly MethodInfo CleanupAsyncMethod = typeof(PostgresUnitOfWork).Assembly
        .GetType(
            "HusayniaTabruk.Infrastructure.Persistence.Repositories.PostgresOwnedTransactionCleanup",
            throwOnError: true)!
        .GetMethod(
            "CleanupAsync",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not locate PostgresOwnedTransactionCleanup.CleanupAsync.");

    [Fact]
    public async Task CancellationRemainsPrimaryWhenOwnedDisposeFails()
    {
        await using TabrukDbContext context = CreateUnopenedContext();
        ThrowOnDisposeContextTransaction transaction = new();
        OperationCanceledException primary = new("Synthetic cancellation.");

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
            {
                try
                {
                    throw primary;
                }
                catch (OperationCanceledException caught)
                {
                    await InvokeCleanupAsync(context, transaction, caught);
                    throw;
                }
            });

        Assert.Same(primary, exception);
        Assert.Equal(1, transaction.RollbackAsyncCallCount);
        Assert.Equal(1, transaction.DisposeAsyncCallCount);
        AssertCleanupFailure(exception, "Injected dispose cleanup failure.");
    }

    [Fact]
    public async Task DomainExceptionRemainsPrimaryWhenOwnedDisposeFails()
    {
        await using TabrukDbContext context = CreateUnopenedContext();
        ThrowOnDisposeContextTransaction transaction = new();
        InvalidOperationException primary = new("Synthetic domain failure.");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
            {
                try
                {
                    throw primary;
                }
                catch (InvalidOperationException caught)
                {
                    await InvokeCleanupAsync(context, transaction, caught);
                    throw;
                }
            });

        Assert.Same(primary, exception);
        Assert.Equal(1, transaction.RollbackAsyncCallCount);
        Assert.Equal(1, transaction.DisposeAsyncCallCount);
        AssertCleanupFailure(exception, "Injected dispose cleanup failure.");
    }

    [RequiresPostgresFact]
    public async Task CancellationRemainsPrimaryWhenOwnedRollbackFails()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        await using TabrukDbContext context = CreateContext(
            database,
            new ThrowOnRollbackTransactionInterceptor());
        PostgresUnitOfWork unitOfWork = new(context);
        using CancellationTokenSource cancellation = new();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => unitOfWork.ExecuteAsync(
                async token =>
                {
                    await context.Database.ExecuteSqlRawAsync("SELECT 1", token);
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Result.Success(true);
                },
                cancellation.Token).AsTask());

        IReadOnlyList<Exception> failures = Assert.IsAssignableFrom<IReadOnlyList<Exception>>(
            exception.Data[CleanupFailureDataKey]);
        Exception failure = Assert.Single(failures);
        Assert.Equal("Injected rollback cleanup failure.", failure.Message);
    }

    [RequiresPostgresFact]
    public async Task DomainExceptionRemainsPrimaryWhenOwnedRollbackFails()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        await using TabrukDbContext context = CreateContext(
            database,
            new ThrowOnRollbackTransactionInterceptor());
        PostgresUnitOfWork unitOfWork = new(context);

        InvalidOperationException primary = await Assert.ThrowsAsync<InvalidOperationException>(
            () => unitOfWork.ExecuteAsync<bool>(
                async token =>
                {
                    await context.Database.ExecuteSqlRawAsync("SELECT 1", token);
                    throw new InvalidOperationException("Synthetic domain failure.");
                }).AsTask());

        Assert.Equal("Synthetic domain failure.", primary.Message);
        IReadOnlyList<Exception> failures = Assert.IsAssignableFrom<IReadOnlyList<Exception>>(
            primary.Data[CleanupFailureDataKey]);
        Exception failure = Assert.Single(failures);
        Assert.Equal("Injected rollback cleanup failure.", failure.Message);
    }

    private static TabrukDbContext CreateUnopenedContext()
    {
        DbContextOptions<TabrukDbContext> options = new DbContextOptionsBuilder<TabrukDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=tabruk_cleanup_probe;Username=postgres;Pooling=false")
            .EnableDetailedErrors()
            .Options;

        return new TabrukDbContext(options);
    }

    private static async ValueTask InvokeCleanupAsync(
        TabrukDbContext context,
        IDbContextTransaction transaction,
        Exception primaryException)
    {
        object? invocationResult = CleanupAsyncMethod.Invoke(
            obj: null,
            parameters:
            [
                context,
                transaction,
                true,
                false,
                primaryException,
            ]);

        ValueTask cleanupTask = Assert.IsType<ValueTask>(invocationResult);
        await cleanupTask;
    }

    private static void AssertCleanupFailure(Exception exception, string expectedMessage)
    {
        IReadOnlyList<Exception> failures = Assert.IsAssignableFrom<IReadOnlyList<Exception>>(
            exception.Data[CleanupFailureDataKey]);
        Exception failure = Assert.Single(failures);
        Assert.Equal(expectedMessage, failure.Message);
    }

    private static TabrukDbContext CreateContext(
        PostgresTestDatabase database,
        params IInterceptor[] interceptors)
    {
        DbContextOptions<TabrukDbContext> options = new DbContextOptionsBuilder<TabrukDbContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(interceptors)
            .EnableDetailedErrors()
            .Options;

        return new TabrukDbContext(options);
    }

    private sealed class ThrowOnRollbackTransactionInterceptor : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult>(
                new InvalidOperationException("Injected rollback cleanup failure."));
    }

    private sealed class ThrowOnDisposeContextTransaction : IDbContextTransaction
    {
        public Guid TransactionId { get; } = Guid.CreateVersion7();
        public bool SupportsSavepoints => false;
        public int RollbackAsyncCallCount { get; private set; }
        public int DisposeAsyncCallCount { get; private set; }

        public void Commit() => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException());

        public void Rollback()
        {
            RollbackAsyncCallCount++;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackAsyncCallCount++;
            return Task.CompletedTask;
        }

        public void CreateSavepoint(string name) => throw new NotSupportedException();

        public Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException());

        public void RollbackToSavepoint(string name) => throw new NotSupportedException();

        public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException());

        public void ReleaseSavepoint(string name)
        {
        }

        public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync()
        {
            DisposeAsyncCallCount++;
            return ValueTask.FromException(
                new InvalidOperationException("Injected dispose cleanup failure."));
        }
    }
}
