using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Husaynia.IntegrationTests.Persistence.Core;

internal sealed class SqlServerTestDatabase : IAsyncDisposable
{
    internal const string SetupCleanupFailureDataKey =
        "Husaynia.SqlServerTestDatabase.SetupCleanupFailure";

    private static int databaseSequence;
    private readonly string databaseName;
    private bool disposed;

    private SqlServerTestDatabase(string databaseName, string connectionString)
    {
        this.databaseName = databaseName;
        ConnectionString = connectionString;
    }

    internal string ConnectionString { get; }

    internal static Task<SqlServerTestDatabase> CreateAsync(string testName) =>
        CreateAsync(
            testName,
            (context, cancellationToken) =>
                context.Database.EnsureCreatedAsync(cancellationToken));

    internal static async Task<SqlServerTestDatabase> CreateAsync(
        string testName,
        Func<TestHusayniaDbContext, CancellationToken, Task> setup,
        Func<SqlServerTestDatabase, CancellationToken, Task>? cleanupOnFailure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setup);

        var sequence = Interlocked.Increment(ref databaseSequence);
        var databaseName = DatabaseName.Create(
            "HusayniaT03",
            $"{testName}_{Environment.ProcessId}_{sequence}");
        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB",
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            Encrypt = false,
            TrustServerCertificate = true,
            ConnectTimeout = 30,
        }.ConnectionString;
        var database = new SqlServerTestDatabase(databaseName, connectionString);

        try
        {
            await using var context = database.CreateContext();
            await setup(context, cancellationToken);
            return database;
        }
        catch (Exception setupException)
        {
            try
            {
                if (cleanupOnFailure is null)
                {
                    await database.CleanupDatabaseAsync().ConfigureAwait(false);
                }
                else
                {
                    await cleanupOnFailure(database, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception cleanupException)
            {
                setupException.Data[SetupCleanupFailureDataKey] = cleanupException;
            }

            ExceptionDispatchInfo.Capture(setupException).Throw();
            throw;
        }
    }

    internal TestHusayniaDbContext CreateContext(params IInterceptor[] interceptors)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var optionsBuilder = new DbContextOptionsBuilder<TestHusayniaDbContext>()
            .UseSqlServer(ConnectionString)
            .EnableDetailedErrors();
        if (interceptors.Length > 0)
        {
            optionsBuilder.AddInterceptors(interceptors);
        }

        var options = optionsBuilder.Options;
        return new TestHusayniaDbContext(options);
    }

    internal async Task<int> CountRowsAsync(string tableName)
    {
        if (!tableName.All(character =>
                char.IsAsciiLetterOrDigit(character) || character == '_'))
        {
            throw new ArgumentException("The table name contains unsafe characters.", nameof(tableName));
        }

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT_BIG(*) FROM [{tableName}]";
        return checked((int)(long)(await command.ExecuteScalarAsync() ?? 0L));
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await CleanupDatabaseAsync().ConfigureAwait(false);
    }

    private async Task CleanupDatabaseAsync()
    {
        var options = new DbContextOptionsBuilder<TestHusayniaDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        await using var context = new TestHusayniaDbContext(options);
        var connection = new SqlConnectionStringBuilder(
            context.Database.GetConnectionString());

        if (!connection.DataSource.Equals(
                @"(localdb)\MSSQLLocalDB",
                StringComparison.OrdinalIgnoreCase) ||
            !connection.InitialCatalog.Equals(databaseName, StringComparison.Ordinal) ||
            !databaseName.StartsWith("HusayniaT03_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Refusing to delete a database that is not a disposable T03 LocalDB database.");
        }

        await context.Database.EnsureDeletedAsync();
        SqlConnection.ClearAllPools();
    }
}
