using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;

namespace Husaynia.IntegrationTests.Persistence.Core;

public sealed class IsolationTests
{
    [Fact]
    public async Task DisposableDatabasesAreIsolated()
    {
        await using var first =
            await SqlServerTestDatabase.CreateAsync($"{nameof(DisposableDatabasesAreIsolated)}_first");
        await using var second =
            await SqlServerTestDatabase.CreateAsync($"{nameof(DisposableDatabasesAreIsolated)}_second");

        await using (var context = first.CreateContext())
        {
            context.Add(new TestAggregate("first-only"));
            await context.SaveChangesAsync();
        }

        await using var firstVerification = first.CreateContext();
        await using var secondVerification = second.CreateContext();
        Assert.Single(await firstVerification.TestAggregates.ToListAsync());
        Assert.Empty(await secondVerification.TestAggregates.ToListAsync());
    }

    [Fact]
    public async Task DisposalDeletesOnlyItsDeterministicallyNamedDatabase()
    {
        var database = await SqlServerTestDatabase.CreateAsync(
            nameof(DisposalDeletesOnlyItsDeterministicallyNamedDatabase));
        var databaseName = new SqlConnectionStringBuilder(database.ConnectionString).InitialCatalog;

        Assert.StartsWith("HusayniaT03_", databaseName, StringComparison.Ordinal);
        await database.DisposeAsync();

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
        command.CommandText = "SELECT COUNT_BIG(*) FROM sys.databases WHERE name = @name";
        command.Parameters.AddWithValue("@name", databaseName);

        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync() ?? -1L));
    }

    [Fact]
    public async Task RawTableCountRejectsUnsafeIdentifier()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(nameof(RawTableCountRejectsUnsafeIdentifier));

        await Assert.ThrowsAsync<ArgumentException>(
            () => database.CountRowsAsync("T03TestAggregates]; DROP DATABASE master;--"));
    }

    [Fact]
    public async Task SetupFailureDoesNotLeakPartiallyCreatedDatabase()
    {
        var expected = new InvalidOperationException("Injected setup failure.");
        string? databaseName = null;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlServerTestDatabase.CreateAsync(
                nameof(SetupFailureDoesNotLeakPartiallyCreatedDatabase),
                async (context, cancellationToken) =>
                {
                    databaseName = new SqlConnectionStringBuilder(
                        context.Database.GetConnectionString()).InitialCatalog;
                    await context.Database.EnsureCreatedAsync(cancellationToken);
                    throw expected;
                }));

        Assert.Same(expected, actual);
        Assert.NotNull(databaseName);
        Assert.DoesNotContain(databaseName, await GetOwnedDatabaseNamesAsync());
    }

    [Fact]
    public async Task SetupFailureRemainsPrimaryWhenBestEffortCleanupAlsoFails()
    {
        var expected = new InvalidOperationException("Injected setup failure.");
        var cleanupFailure = new InvalidOperationException("Injected cleanup failure.");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlServerTestDatabase.CreateAsync(
                nameof(SetupFailureRemainsPrimaryWhenBestEffortCleanupAlsoFails),
                (_, _) => throw expected,
                (_, _) => throw cleanupFailure));

        Assert.Same(expected, actual);
        Assert.Same(
            cleanupFailure,
            actual.Data[SqlServerTestDatabase.SetupCleanupFailureDataKey]);
    }

    private static async Task<string[]> GetOwnedDatabaseNamesAsync()
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
            """
            SELECT name
            FROM sys.databases
            WHERE name LIKE N'HusayniaT03[_]%'
            ORDER BY name;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return [.. names];
    }
}
