using Husaynia.Application.Contracts;
using Husaynia.Infrastructure.Persistence.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Husaynia.IntegrationTests.Persistence.Core;

public sealed class ConcurrencyTests
{
    [Fact]
    public async Task StaleRowVersionReturnsExplicitConflict()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(nameof(StaleRowVersionReturnsExplicitConflict));
        Guid id;
        await using (var setup = database.CreateContext())
        {
            var entity = new TestAggregate("initial");
            setup.Add(entity);
            await setup.SaveChangesAsync();
            id = entity.Id;
        }

        await using var firstContext = database.CreateContext();
        await using var staleContext = database.CreateContext();
        var first = await firstContext.TestAggregates.SingleAsync(entity => entity.Id == id);
        var stale = await staleContext.TestAggregates.SingleAsync(entity => entity.Id == id);

        first.Value = "first";
        var firstResult = await new EfHusayniaUnitOfWork(firstContext).ExecuteAsync(
            _ => Task.FromResult(Result.Succeed<string, PersistenceError>("first")),
            null,
            CancellationToken.None);
        Assert.True(firstResult.IsSuccess);

        stale.Value = "stale";
        var staleResult = await new EfHusayniaUnitOfWork(staleContext).ExecuteAsync(
            _ => Task.FromResult(Result.Succeed<string, PersistenceError>("stale")),
            null,
            CancellationToken.None);

        Assert.True(staleResult.IsFailure);
        Assert.Equal(PersistenceErrorCode.ConcurrencyConflict, staleResult.Error.Code);

        await using var verification = database.CreateContext();
        Assert.Equal(
            "first",
            await verification.TestAggregates
                .Where(entity => entity.Id == id)
                .Select(entity => entity.Value)
                .SingleAsync());
    }
}
