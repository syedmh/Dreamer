using System.Transactions;
using System.Data.Common;
using Husaynia.Application.Contracts;
using Husaynia.Infrastructure.Persistence.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Husaynia.IntegrationTests.Persistence.Core;

public sealed class UnitOfWorkTests
{
    [Fact]
    public async Task CommitsSuccessfulOperation()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(nameof(CommitsSuccessfulOperation));
        await using (var context = database.CreateContext())
        {
            var unitOfWork = new EfHusayniaUnitOfWork(context);
            var result = await unitOfWork.ExecuteAsync(
                _ =>
                {
                    context.Add(new TestAggregate("committed"));
                    return Task.FromResult(Success("committed"));
                },
                null,
                CancellationToken.None);

            Assert.True(result.IsSuccess);
        }

        await using var verification = database.CreateContext();
        Assert.Equal(1, await verification.TestAggregates.CountAsync());
    }

    [Fact]
    public async Task RollsBackExpectedFailureAndAllowsRecovery()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(nameof(RollsBackExpectedFailureAndAllowsRecovery));
        await using (var context = database.CreateContext())
        {
            var unitOfWork = new EfHusayniaUnitOfWork(context);
            var failed = await unitOfWork.ExecuteAsync(
                _ =>
                {
                    context.Add(new TestAggregate("rolled-back"));
                    return Task.FromResult(Failure<string>("expected"));
                },
                null,
                CancellationToken.None);

            Assert.True(failed.IsFailure);

            var recovered = await unitOfWork.ExecuteAsync(
                _ =>
                {
                    context.Add(new TestAggregate("recovered"));
                    return Task.FromResult(Success("recovered"));
                },
                null,
                CancellationToken.None);

            Assert.True(recovered.IsSuccess);
        }

        await using var verification = database.CreateContext();
        var value = await verification.TestAggregates.Select(entity => entity.Value).SingleAsync();
        Assert.Equal("recovered", value);
    }

    [Fact]
    public async Task RollsBackUnexpectedFailureAndAllowsRecovery()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(nameof(RollsBackUnexpectedFailureAndAllowsRecovery));
        await using (var context = database.CreateContext())
        {
            var unitOfWork = new EfHusayniaUnitOfWork(context);
            var failed = await unitOfWork.ExecuteAsync<string>(
                _ =>
                {
                    context.Add(new TestAggregate("exception"));
                    throw new InvalidOperationException("test failure");
                },
                null,
                CancellationToken.None);

            Assert.True(failed.IsFailure);
            Assert.Equal(PersistenceErrorCode.UnexpectedFailure, failed.Error.Code);

            var recovered = await unitOfWork.ExecuteAsync(
                _ =>
                {
                    context.Add(new TestAggregate("recovered"));
                    return Task.FromResult(Success("recovered"));
                },
                null,
                CancellationToken.None);
            Assert.True(recovered.IsSuccess);
        }

        await using var verification = database.CreateContext();
        Assert.Equal(
            "recovered",
            await verification.TestAggregates.Select(entity => entity.Value).SingleAsync());
    }

    [Fact]
    public async Task NestedFailureDoomsOuterTransactionEvenWhenIgnored()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(nameof(NestedFailureDoomsOuterTransactionEvenWhenIgnored));
        await using var context = database.CreateContext();
        var unitOfWork = new EfHusayniaUnitOfWork(context);

        var result = await unitOfWork.ExecuteAsync(
            async cancellationToken =>
            {
                context.Add(new TestAggregate("outer"));
                _ = await unitOfWork.ExecuteAsync(
                    _ =>
                    {
                        context.Add(new TestAggregate("nested"));
                        return Task.FromResult(Failure<string>("nested failure"));
                    },
                    null,
                    cancellationToken);
                return Success("outer ignored nested failure");
            },
            null,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(PersistenceErrorCode.NestedOperationFailed, result.Error.Code);

        await using var verification = database.CreateContext();
        Assert.Empty(await verification.TestAggregates.ToListAsync());
    }

    [Fact]
    public async Task NestedSuccessJoinsOuterTransaction()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(nameof(NestedSuccessJoinsOuterTransaction));
        await using var context = database.CreateContext();
        var unitOfWork = new EfHusayniaUnitOfWork(context);

        var result = await unitOfWork.ExecuteAsync(
            async cancellationToken =>
            {
                context.Add(new TestAggregate("outer"));
                var nested = await unitOfWork.ExecuteAsync(
                    _ =>
                    {
                        context.Add(new TestAggregate("nested"));
                        return Task.FromResult(Success("nested"));
                    },
                    null,
                    cancellationToken);
                return nested.IsSuccess ? Success("outer") : Failure<string>("nested failed");
            },
            null,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        await using var verification = database.CreateContext();
        Assert.Equal(2, await verification.TestAggregates.CountAsync());
    }

    [Fact]
    public async Task CaughtNestedCancellationBeforeNestedWriteDoomsOuterTransaction()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(
                nameof(CaughtNestedCancellationBeforeNestedWriteDoomsOuterTransaction));
        await using var context = database.CreateContext();
        var unitOfWork = new EfHusayniaUnitOfWork(context);
        using var nestedCancellation = new CancellationTokenSource();
        await nestedCancellation.CancelAsync();

        var result = await unitOfWork.ExecuteAsync(
            async outerCancellationToken =>
            {
                _ = outerCancellationToken;
                context.Add(new TestAggregate("outer"));
                try
                {
                    _ = await unitOfWork.ExecuteAsync(
                        cancellationToken =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            return Task.FromResult(Success("nested"));
                        },
                        null,
                        nestedCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    context.Add(new TestAggregate("outer-continued"));
                }

                return Success("outer");
            },
            null,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(PersistenceErrorCode.NestedOperationFailed, result.Error.Code);
        await using var verification = database.CreateContext();
        Assert.Empty(await verification.TestAggregates.ToListAsync());
    }

    [Fact]
    public async Task CaughtNestedCancellationAfterNestedWriteDoomsOuterTransaction()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(
                nameof(CaughtNestedCancellationAfterNestedWriteDoomsOuterTransaction));
        await using var context = database.CreateContext();
        var unitOfWork = new EfHusayniaUnitOfWork(context);
        using var nestedCancellation = new CancellationTokenSource();
        await nestedCancellation.CancelAsync();

        var result = await unitOfWork.ExecuteAsync(
            async outerCancellationToken =>
            {
                _ = outerCancellationToken;
                context.Add(new TestAggregate("outer"));
                try
                {
                    _ = await unitOfWork.ExecuteAsync(
                        cancellationToken =>
                        {
                            context.Add(new TestAggregate("nested"));
                            cancellationToken.ThrowIfCancellationRequested();
                            return Task.FromResult(Success("nested"));
                        },
                        null,
                        nestedCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    context.Add(new TestAggregate("outer-continued"));
                }

                return Success("outer");
            },
            null,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(PersistenceErrorCode.NestedOperationFailed, result.Error.Code);
        await using var verification = database.CreateContext();
        Assert.Empty(await verification.TestAggregates.ToListAsync());
    }

    [Fact]
    public async Task RejectsAmbientAndExternallyManagedTransactions()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(nameof(RejectsAmbientAndExternallyManagedTransactions));
        await using var context = database.CreateContext();
        var unitOfWork = new EfHusayniaUnitOfWork(context);

        using (var scope = new TransactionScope(
                   TransactionScopeOption.Required,
                   TransactionScopeAsyncFlowOption.Enabled))
        {
            var ambient = await unitOfWork.ExecuteAsync(
                _ => Task.FromResult(Success("not run")),
                null,
                CancellationToken.None);
            Assert.Equal(PersistenceErrorCode.AmbientTransactionNotSupported, ambient.Error.Code);
        }

        await using var transaction = await context.Database.BeginTransactionAsync();
        var existing = await unitOfWork.ExecuteAsync(
            _ => Task.FromResult(Success("not run")),
            null,
            CancellationToken.None);
        Assert.Equal(PersistenceErrorCode.ExistingTransactionNotSupported, existing.Error.Code);
    }

    [Fact]
    public async Task CancellationRollsBackAndPropagates()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(nameof(CancellationRollsBackAndPropagates));
        await using var context = database.CreateContext();
        var unitOfWork = new EfHusayniaUnitOfWork(context);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            unitOfWork.ExecuteAsync(
                async cancellationToken =>
                {
                    context.Add(new TestAggregate("cancelled"));
                    await cancellation.CancelAsync();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return Success("unreachable");
                },
                null,
                cancellation.Token));

        await using var verification = database.CreateContext();
        Assert.Empty(await verification.TestAggregates.ToListAsync());
    }

    [Fact]
    public async Task PreCancelledAcquisitionLeavesNextExecutionAsFreshOuterTransaction()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(
                nameof(PreCancelledAcquisitionLeavesNextExecutionAsFreshOuterTransaction));
        await using var context = database.CreateContext();
        var unitOfWork = new EfHusayniaUnitOfWork(context);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var operationCalls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            unitOfWork.ExecuteAsync(
                _ =>
                {
                    operationCalls++;
                    context.Add(new TestAggregate("must-not-save"));
                    return Task.FromResult(Success("cancelled"));
                },
                null,
                cancellation.Token));

        Assert.Equal(0, operationCalls);
        var recovered = await unitOfWork.ExecuteAsync(
            _ =>
            {
                context.Add(new TestAggregate("recovered"));
                return Task.FromResult(Success("recovered"));
            },
            null,
            CancellationToken.None);

        Assert.True(recovered.IsSuccess);
        await using var verification = database.CreateContext();
        Assert.Equal(
            ["recovered"],
            await verification.TestAggregates.Select(entity => entity.Value).ToArrayAsync());
    }

    [Fact]
    public async Task AcquisitionFailureLeavesNextExecutionAsFreshOuterTransaction()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(
                nameof(AcquisitionFailureLeavesNextExecutionAsFreshOuterTransaction));
        var interceptor = new FailFirstConnectionOpeningInterceptor();
        await using var context = database.CreateContext(interceptor);
        var unitOfWork = new EfHusayniaUnitOfWork(context);
        var operationCalls = 0;

        var failed = await unitOfWork.ExecuteAsync(
            _ =>
            {
                operationCalls++;
                context.Add(new TestAggregate("must-not-save"));
                return Task.FromResult(Success("unexpected"));
            },
            null,
            CancellationToken.None);

        Assert.True(failed.IsFailure);
        Assert.Equal(PersistenceErrorCode.UnexpectedFailure, failed.Error.Code);
        Assert.Equal(0, operationCalls);

        var recovered = await unitOfWork.ExecuteAsync(
            _ =>
            {
                context.Add(new TestAggregate("recovered"));
                return Task.FromResult(Success("recovered"));
            },
            null,
            CancellationToken.None);

        Assert.True(recovered.IsSuccess);
        await using var verification = database.CreateContext();
        Assert.Equal(
            ["recovered"],
            await verification.TestAggregates.Select(entity => entity.Value).ToArrayAsync());
    }

    [Fact]
    public void UnsafeOperationsDoNotRetryByDefault()
    {
        Assert.Equal(ExecutionRetryMode.None, UnitOfWorkOptions.Default.RetryMode);
        Assert.Equal(
            ExecutionRetryMode.ExplicitlyIdempotent,
            UnitOfWorkOptions.IdempotentWithRetry(System.Data.IsolationLevel.Serializable).RetryMode);
    }

    [Fact]
    public async Task UnsafeUnexpectedFailureExecutesOnlyOnce()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(nameof(UnsafeUnexpectedFailureExecutesOnlyOnce));
        await using var context = database.CreateContext();
        var attempts = 0;

        var result = await new EfHusayniaUnitOfWork(context).ExecuteAsync<string>(
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("unsafe failure");
            },
            UnitOfWorkOptions.Default,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(PersistenceErrorCode.UnexpectedFailure, result.Error.Code);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExplicitlyIdempotentOperationRetriesTransientSqlFailure()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(
                nameof(ExplicitlyIdempotentOperationRetriesTransientSqlFailure));
        await using var context = database.CreateContext();
        var attempts = 0;

        var result = await new EfHusayniaUnitOfWork(context).ExecuteAsync(
            async cancellationToken =>
            {
                attempts++;
                if (attempts < 3)
                {
                    await context.Database.ExecuteSqlRawAsync(
                        "RAISERROR (41302, 16, 1);",
                        cancellationToken);
                }

                context.Add(new TestAggregate("retried"));
                return Success("retried");
            },
            UnitOfWorkOptions.IdempotentWithRetry(System.Data.IsolationLevel.Serializable),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, attempts);

        await using var verification = database.CreateContext();
        Assert.Single(await verification.TestAggregates.ToListAsync());
    }

    private static Result<T, PersistenceError> Success<T>(T value) =>
        Result.Succeed<T, PersistenceError>(value);

    private static Result<T, PersistenceError> Failure<T>(string message) =>
        Result.Fail<T, PersistenceError>(
            new PersistenceError(PersistenceErrorCode.UnexpectedFailure, message));

    private sealed class FailFirstConnectionOpeningInterceptor : DbConnectionInterceptor
    {
        private int attempts;

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("Injected transaction startup failure.");
            }

            return ValueTask.FromResult(result);
        }
    }
}
