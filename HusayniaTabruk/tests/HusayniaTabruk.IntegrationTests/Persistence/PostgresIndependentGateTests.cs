using System.Data.Common;
using System.Text.Json;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Application.Abstractions;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace HusayniaTabruk.IntegrationTests.Persistence;

[Trait("Category", "Persistence")]
public sealed class PostgresIndependentGateTests : PostgresPersistenceTest
{
    [RequiresPostgresFact]
    public async Task InitialMigrationCanApplyDownAndApplyAgainOnTheSameFreshSchema()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();

        await context.Database.MigrateAsync();
        Assert.Equal(2, (await context.Database.GetAppliedMigrationsAsync()).Count());

        await context.Database.MigrateAsync("0");
        Assert.Empty(await context.Database.GetAppliedMigrationsAsync());

        await context.Database.MigrateAsync();
        Assert.Equal(2, (await context.Database.GetAppliedMigrationsAsync()).Count());

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command =
            new("SELECT to_regclass('organizations') IS NOT NULL", connection);
        Assert.True(Assert.IsType<bool>(await command.ExecuteScalarAsync()));
    }

    [RequiresPostgresFact]
    public async Task ConcurrentCreateProducesExactlyOneCreatedReceiptAndOneStoredRow()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        IdempotencyCreateRequest request = CreateRequest(seed);
        await using TabrukDbContext firstContext = database.CreateContext();
        await using TabrukDbContext secondContext = database.CreateContext();

        IdempotencyCreateResult[] results = await Task.WhenAll(
            Task.Run(async () => await new PostgresIdempotencyStore(firstContext).TryCreateProcessingAsync(request)),
            Task.Run(async () => await new PostgresIdempotencyStore(secondContext).TryCreateProcessingAsync(request)));

        Assert.Single(results, result => result.Outcome == IdempotencyCreateOutcome.Created);
        Assert.Single(results, result => result.Outcome == IdempotencyCreateOutcome.ExistingProcessing);
        Assert.Equal(results[0].Receipt, results[1].Receipt);

        await using TabrukDbContext verification = database.CreateContext();
        Assert.Equal(
            1,
            await verification.IdempotencyRecords.CountAsync(
                record => record.OrganizationId == seed.OrganizationId.Value
                    && record.MembershipId == seed.MemberMembershipId.Value
                    && record.Key == request.Key.Value));
    }

    [RequiresPostgresFact]
    public async Task ConcurrentCompleteProducesExactlyOneWinnerAndStableCompletedReceipt()
    {
        await AssertConcurrentTerminalTransitionAsync(complete: true);
    }

    [RequiresPostgresFact]
    public async Task ConcurrentFailProducesExactlyOneWinnerAndStableFailedReceipt()
    {
        await AssertConcurrentTerminalTransitionAsync(complete: false);
    }

    [RequiresPostgresFact]
    public async Task CancellationAfterDatabaseWritesRollsBackNotificationAndOutboxAtomically()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        Guid notificationId = Guid.CreateVersion7();
        Guid outboxId = Guid.CreateVersion7();
        await using TabrukDbContext context = database.CreateContext();
        PostgresUnitOfWork unitOfWork = new(context);
        using CancellationTokenSource cancellation = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await unitOfWork.ExecuteAsync(
                async token =>
                {
                    await context.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        INSERT INTO notifications
                            (id, organization_id, recipient_membership_id, type, resource_type, resource_id,
                             title, body, created_at)
                        VALUES
                            ({notificationId}, {seed.OrganizationId.Value}, {seed.MemberMembershipId.Value},
                             {1}, {1}, {seed.ThreadId.Value}, {"cancelled notification"},
                             {"must roll back"}, {seed.Now})
                        """,
                        token);
                    await context.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        INSERT INTO outbox_messages
                            (id, organization_id, type, payload, attempts, next_attempt_at, occurred_at)
                        VALUES
                            ({outboxId}, {seed.OrganizationId.Value}, {"cancelled.test"}, {"{}"},
                             {0}, {seed.Now}, {seed.Now})
                        """,
                        token);
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return HusayniaTabruk.Domain.Common.Result.Success(true);
                },
                cancellation.Token).AsTask());

        await using TabrukDbContext verification = database.CreateContext();
        Assert.False(await verification.Notifications.AnyAsync(row => row.Id == notificationId));
        Assert.False(await verification.OutboxMessages.AnyAsync(row => row.Id == outboxId));
    }

    [RequiresPostgresFact]
    public async Task UnavailablePostgresEndpointFailsClosedInsteadOfUsingAnotherProvider()
    {
        const string unavailable =
            "Host=127.0.0.1;Port=1;Database=postgres;Username=postgres;Search Path=public;Options=-c tabruk.target_schema=public -c tabruk.disposable_ef=on;Pooling=false;Timeout=1;Command Timeout=1";
        await using TabrukDbContext context = TabrukDbContextOptions.Create(unavailable);

        DependencyUnavailableException exception = await Assert.ThrowsAsync<DependencyUnavailableException>(
            () => new PostgresGovernanceRepository(context).GetAsync(OrganizationId.New()).AsTask());
        NpgsqlException providerException = Assert.IsType<NpgsqlException>(exception.InnerException);

        Assert.True(providerException.IsTransient);
        Assert.Equal("A required dependency is temporarily unavailable.", exception.Message);
        Assert.Same(providerException, exception.InnerException);
        Assert.IsNotType<DependencyUnavailableException>(providerException.InnerException);
    }

    [RequiresPostgresFact]
    public async Task RealPostgresCommandTimeoutReturnsSanitizedServiceUnavailableContract()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        ForceFirstReaderTimeoutInterceptor interceptor = new();
        DbContextOptions<TabrukDbContext> options = new DbContextOptionsBuilder<TabrukDbContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        await using TabrukDbContext context = new(options);
        PostgresGovernanceRepository repository = new(context);
        using ServiceProvider services = new ServiceCollection()
            .AddOptions()
            .Configure<JsonOptions>(_ => { })
            .BuildServiceProvider();
        DefaultHttpContext httpContext = new()
        {
            RequestServices = services,
        };
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Path = "/api/v1/_independent/postgres-timeout";
        httpContext.Response.Body = new MemoryStream();
        ApiProblemDetailsMiddleware middleware = new(
            async _ =>
            {
                await repository.GetAsync(seed.OrganizationId);
            },
            NullLogger<ApiProblemDetailsMiddleware>.Instance);

        await middleware.InvokeAsync(httpContext);

        Assert.True(interceptor.TimeoutInjected);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, httpContext.Response.StatusCode);
        Assert.Equal("application/problem+json", httpContext.Response.ContentType);
        httpContext.Response.Body.Position = 0;
        using JsonDocument problem = await JsonDocument.ParseAsync(httpContext.Response.Body);
        Assert.Equal(
            "dependency_unavailable",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "A required dependency is temporarily unavailable.",
            problem.RootElement.GetProperty("detail").GetString());
        string responseBody = problem.RootElement.GetRawText();
        Assert.DoesNotContain("pg_sleep", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Timeout", responseBody, StringComparison.OrdinalIgnoreCase);
    }

    [RequiresPostgresFact]
    public async Task ApplicationRoleCanCrudRuntimeAndOutboxButCannotReadHistoryOrMutateAudit()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        Guid notificationId = Guid.CreateVersion7();
        Guid outboxId = Guid.CreateVersion7();
        Guid auditId = Guid.CreateVersion7();

        await using (NpgsqlConnection connection = new(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await SetApplicationRoleAsync(connection, transaction);
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO notifications
                    (id, organization_id, recipient_membership_id, type, resource_type, resource_id,
                     title, body, created_at)
                VALUES
                    (@id, @organizationId, @recipientId, 1, 1, @resourceId, 'created', 'runtime CRUD', @createdAt)
                """,
                ("id", notificationId),
                ("organizationId", seed.OrganizationId.Value),
                ("recipientId", seed.MemberMembershipId.Value),
                ("resourceId", seed.ThreadId.Value),
                ("createdAt", seed.Now));
            await ExecuteAsync(
                connection,
                transaction,
                "UPDATE notifications SET title = 'updated' WHERE id = @id",
                ("id", notificationId));
            Assert.Equal(
                "updated",
                await ScalarAsync<string>(
                    connection,
                    transaction,
                    "SELECT title FROM notifications WHERE id = @id",
                    ("id", notificationId)));
            await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM notifications WHERE id = @id",
                ("id", notificationId));

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO outbox_messages
                    (id, organization_id, type, payload, attempts, next_attempt_at, occurred_at)
                VALUES
                    (@id, @organizationId, 'permission.test', '{}', 0, @occurredAt, @occurredAt)
                """,
                ("id", outboxId),
                ("organizationId", seed.OrganizationId.Value),
                ("occurredAt", seed.Now));
            await ExecuteAsync(
                connection,
                transaction,
                "UPDATE outbox_messages SET attempts = 1 WHERE id = @id",
                ("id", outboxId));
            Assert.Equal(
                1,
                await ScalarAsync<int>(
                    connection,
                    transaction,
                    "SELECT attempts FROM outbox_messages WHERE id = @id",
                    ("id", outboxId)));
            await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM outbox_messages WHERE id = @id",
                ("id", outboxId));

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO audit_events
                    (id, organization_id, actor_membership_id, action, resource_type, resource_id,
                     reason, purpose, correlation_id, occurred_at)
                VALUES
                    (@id, @organizationId, @actorId, 'permission_test', 'test', 'audit',
                     'verify insert-only', 'integration', 't8-app-role', @occurredAt)
                """,
                ("id", auditId),
                ("organizationId", seed.OrganizationId.Value),
                ("actorId", seed.ManagerMembershipId.Value),
                ("occurredAt", seed.Now));
            await transaction.CommitAsync();
        }

        PostgresException historyDenied = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsApplicationRoleAsync(
                database,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" """));
        Assert.Equal("42501", historyDenied.SqlState);
        PostgresException auditUpdateDenied = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsApplicationRoleAsync(
                database,
                "UPDATE audit_events SET action = 'forbidden' WHERE id = @id",
                ("id", auditId)));
        Assert.Equal("42501", auditUpdateDenied.SqlState);

        await using TabrukDbContext verification = database.CreateContext();
        Assert.False(await verification.Notifications.AnyAsync(row => row.Id == notificationId));
        Assert.False(await verification.OutboxMessages.AnyAsync(row => row.Id == outboxId));
        Assert.True(await verification.AuditEvents.AnyAsync(row => row.Id == auditId));
    }

    [RequiresPostgresFact]
    public async Task NonTransientPostgresConstraintFailureIsNotTranslated()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        Guid notificationId = Guid.CreateVersion7();
        await using TabrukDbContext context = database.CreateContext();
        context.Notifications.Add(
            new()
            {
                Id = notificationId,
                OrganizationId = seed.OrganizationId.Value,
                RecipientMembershipId = seed.MemberMembershipId.Value,
                Type = 1,
                ResourceType = 1,
                ResourceId = seed.ThreadId.Value,
                Title = "Existing notification",
                Body = "Constraint translation coverage.",
                CreatedAt = seed.Now,
            });
        await context.SaveChangesAsync();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => new PostgresUnitOfWork(context).ExecuteAsync(
                async cancellationToken =>
                {
                    await context.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        INSERT INTO notifications
                            (id, organization_id, recipient_membership_id, type, resource_type, resource_id,
                             title, body, created_at)
                        VALUES
                            ({notificationId}, {seed.OrganizationId.Value}, {seed.MemberMembershipId.Value},
                             {1}, {1}, {seed.ThreadId.Value}, {"duplicate notification"},
                             {"must remain a provider constraint error"}, {seed.Now})
                        """,
                        cancellationToken);
                    return HusayniaTabruk.Domain.Common.Result.Success(true);
                }).AsTask());

        Assert.Equal("23505", exception.SqlState);
    }

    [RequiresPostgresFact]
    public async Task PersistenceEnvironmentIsPostgres186WithNoLoginApplicationRole()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand versionCommand = new("SHOW server_version", connection);
        await using NpgsqlCommand roleCommand =
            new("SELECT rolcanlogin FROM pg_roles WHERE rolname = 'tabruk_app'", connection);

        string version = Assert.IsType<string>(await versionCommand.ExecuteScalarAsync());
        bool canLogin = Assert.IsType<bool>(await roleCommand.ExecuteScalarAsync());

        Assert.StartsWith("18.6", version, StringComparison.Ordinal);
        Assert.False(canLogin);
    }

    private static async Task AssertConcurrentTerminalTransitionAsync(bool complete)
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        IdempotencyCreateRequest create = CreateRequest(seed);
        await using (TabrukDbContext setup = database.CreateContext())
        {
            IdempotencyCreateResult created =
                await new PostgresIdempotencyStore(setup).TryCreateProcessingAsync(create);
            Assert.Equal(IdempotencyCreateOutcome.Created, created.Outcome);
        }

        IdempotencyRequest transition = new(
            create.OrganizationId,
            create.MembershipId,
            create.Key,
            create.Operation,
            create.RequestFingerprint);
        await using TabrukDbContext firstContext = database.CreateContext();
        await using TabrukDbContext secondContext = database.CreateContext();
        PostgresIdempotencyStore first = new(firstContext);
        PostgresIdempotencyStore second = new(secondContext);

        Task<IdempotencyTransitionResult> FirstTransition() =>
            complete
                ? first.TryCompleteAsync(transition, "signup-123").AsTask()
                : first.TryFailAsync(transition).AsTask();
        Task<IdempotencyTransitionResult> SecondTransition() =>
            complete
                ? second.TryCompleteAsync(transition, "signup-123").AsTask()
                : second.TryFailAsync(transition).AsTask();

        IdempotencyTransitionResult[] results =
            await Task.WhenAll(Task.Run(FirstTransition), Task.Run(SecondTransition));
        IdempotencyTransitionOutcome winner = complete
            ? IdempotencyTransitionOutcome.Completed
            : IdempotencyTransitionOutcome.Failed;
        Assert.Single(results, result => result.Outcome == winner);
        Assert.Single(
            results,
            result => result.Outcome == IdempotencyTransitionOutcome.ExpectedStatusMismatch);
        Assert.Equal(results[0].Receipt, results[1].Receipt);
        Assert.Equal(
            complete ? IdempotencyStatus.Completed : IdempotencyStatus.Failed,
            results[0].Receipt!.Status);
    }

    private static IdempotencyCreateRequest CreateRequest(PersistenceSeed seed)
    {
        DateTimeOffset createdAt = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        return new IdempotencyCreateRequest(
            seed.OrganizationId,
            seed.MemberMembershipId,
            IdempotencyKey.New(),
            "signup.submit",
            RequestFingerprint.FromSha256(new string('c', 64)),
            createdAt,
            createdAt.AddMinutes(10));
    }

    private static async Task ExecuteAsApplicationRoleAsync(
        PostgresTestDatabase database,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await SetApplicationRoleAsync(connection, transaction);
        await ExecuteAsync(connection, transaction, sql, parameters);
        await transaction.RollbackAsync();
    }

    private static async Task SetApplicationRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using NpgsqlCommand command = new("SET LOCAL ROLE tabruk_app", connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Assert.IsType<T>(await command.ExecuteScalarAsync());
    }

    private sealed class ForceFirstReaderTimeoutInterceptor : DbCommandInterceptor
    {
        private int injected;

        public bool TimeoutInjected => Volatile.Read(ref injected) == 1;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref injected, 1, 0) == 0)
            {
                command.CommandTimeout = 1;
                command.CommandText = "SELECT pg_sleep(2);" + Environment.NewLine + command.CommandText;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
