using System.Data.Common;
using System.Globalization;
using Husaynia.Application.Contracts;
using Husaynia.Domain.Identity;
using Husaynia.Infrastructure.Identity;
using Husaynia.Web.Areas.Admin.Identity;
using Husaynia.Web.Composition;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Husaynia.IntegrationTests.Identity;

public sealed class IdentityBootstrapAndConfigurationTests
{
    private const string ExpectedInstallSql =
        """
        SET XACT_ABORT ON;

        IF OBJECT_ID(N'[dbo].[IdentityAnonymousRateLimits]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[IdentityAnonymousRateLimits]
            (
                [Id] bigint IDENTITY(1,1) NOT NULL,
                [EndpointFamily] nvarchar(32) NOT NULL,
                [ClientFingerprint] binary(32) NOT NULL,
                [WindowStartedAtUtc] datetimeoffset(7) NOT NULL,
                [WindowEndsAtUtc] datetimeoffset(7) NOT NULL,
                [RequestCount] int NOT NULL,
                [RetainUntilUtc] datetimeoffset(7) NOT NULL,
                CONSTRAINT [PK_IdentityAnonymousRateLimits]
                    PRIMARY KEY CLUSTERED ([Id]),
                CONSTRAINT [UQ_IdentityAnonymousRateLimits_EndpointFamily_ClientFingerprint]
                    UNIQUE ([EndpointFamily], [ClientFingerprint]),
                CONSTRAINT [CK_IdentityAnonymousRateLimits_RequestCount]
                    CHECK ([RequestCount] > 0),
                CONSTRAINT [CK_IdentityAnonymousRateLimits_Window]
                    CHECK ([WindowEndsAtUtc] > [WindowStartedAtUtc]),
                CONSTRAINT [CK_IdentityAnonymousRateLimits_Retention]
                    CHECK ([RetainUntilUtc] >= [WindowEndsAtUtc])
            );
        END;

        IF NOT EXISTS
        (
            SELECT 1 FROM sys.indexes
            WHERE [name] = N'IX_IdentityAnonymousRateLimits_RetainUntilUtc'
              AND [object_id] = OBJECT_ID(N'[dbo].[IdentityAnonymousRateLimits]')
        )
        BEGIN
            CREATE INDEX [IX_IdentityAnonymousRateLimits_RetainUntilUtc]
                ON [dbo].[IdentityAnonymousRateLimits] ([RetainUntilUtc]);
        END;

        EXEC(N'
        CREATE OR ALTER TRIGGER [dbo].[TR_IdentityAuditEvents_AppendOnly]
        ON [dbo].[IdentityAuditEvents]
        AFTER UPDATE, DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 51004, ''Identity audit events are append-only and cannot be updated or deleted.'', 1;
        END');

        EXEC(N'
        CREATE OR ALTER TRIGGER [dbo].[TR_IdentityBootstrapState_PermanentSeal]
        ON [dbo].[IdentityBootstrapState]
        AFTER UPDATE, DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            IF EXISTS (SELECT 1 FROM deleted WHERE [Id] = 1)
            BEGIN
                THROW 51005, ''The identity bootstrap seal is permanent and cannot be updated or deleted.'', 1;
            END;
        END');
        """;

    private const string ExpectedDownSql =
        """
        SET XACT_ABORT ON;
        DROP TRIGGER IF EXISTS [dbo].[TR_IdentityBootstrapState_PermanentSeal];
        DROP TRIGGER IF EXISTS [dbo].[TR_IdentityAuditEvents_AppendOnly];
        DROP TABLE IF EXISTS [dbo].[IdentityAnonymousRateLimits];
        """;

    [Fact]
    public void ConfigurationValidationRejectsIncompleteBootstrapSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:HusayniaDatabase"] =
                    "Server=(localdb)\\MSSQLLocalDB;Database=HusayniaValidation;Integrated Security=true;Encrypt=false",
                ["Identity:Bootstrap:Enabled"] = "true",
            })
            .Build();

        var exception = Assert.Throws<HusayniaConfigurationException>(
            () => configuration.ValidateHusayniaConfiguration(
                typeof(IHusayniaModule).Assembly,
                typeof(IdentityInfrastructureModule).Assembly,
                typeof(IdentityAdminEndpointModule).Assembly));

        Assert.Contains("Identity:Bootstrap:EnvironmentName", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Identity:Bootstrap:AdminEmail", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Identity:Bootstrap:AdminPassword", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BootstrapSeedingIsEnvironmentControlledAndPermanentlySealed()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(BootstrapSeedingIsEnvironmentControlledAndPermanentlySealed));

        var mismatchConfig = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Identity:Bootstrap:Enabled"] = "true",
            ["Identity:Bootstrap:EnvironmentName"] = "Staging",
            ["Identity:Bootstrap:AdminEmail"] = "bootstrap@example.test",
            ["Identity:Bootstrap:AdminPassword"] = "BootstrapAdmin!234",
        };
        await using (var mismatchFactory = new IdentityWebApplicationFactory(database, mismatchConfig))
        {
            using var client = mismatchFactory.CreateIdentityClient();
            _ = await client.GetAsync("/admin/identity/antiforgery");
            Assert.Null(await mismatchFactory.FindUserAsync("bootstrap@example.test"));
        }

        var matchingConfig = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Identity:Bootstrap:Enabled"] = "true",
            ["Identity:Bootstrap:EnvironmentName"] = "IntegrationTesting",
            ["Identity:Bootstrap:AdminEmail"] = "bootstrap@example.test",
            ["Identity:Bootstrap:AdminPassword"] = "BootstrapAdmin!234",
        };

        await using (var firstFactory = new IdentityWebApplicationFactory(database, matchingConfig))
        {
            using var client = firstFactory.CreateIdentityClient();
            _ = await client.GetAsync("/admin/identity/antiforgery");
        }

        await using var verificationContext = database.CreateContext();
        Assert.Equal(6, await verificationContext.Set<HusayniaIdentityRole>().CountAsync());
        Assert.Equal(1, await verificationContext.Set<HusayniaIdentityUser>()
            .CountAsync(user => user.Email == "bootstrap@example.test"));
        var firstAdmin = await verificationContext.Set<HusayniaIdentityUser>()
            .SingleAsync(user => user.Email == "bootstrap@example.test");
        Assert.True(firstAdmin.EmailConfirmed);
        Assert.Equal(
            1,
            await verificationContext.Set<IdentityBootstrapState>().CountAsync());
        Assert.Equal(
            2,
            await verificationContext.Set<AuditEvent>()
                .CountAsync(entry => entry.Action.StartsWith("identity.bootstrap.")));

        var sealBytes = await ReadPermanentSealBytesAsync(database.ConnectionString);
        var updateException = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteSqlAsync(
                database.ConnectionString,
                """
                UPDATE [dbo].[IdentityBootstrapState]
                SET [EnvironmentName] = N'Mutated',
                    [AdministratorUserId] = NEWID(),
                    [SealedAtUtc] = DATEADD(day, 1, [SealedAtUtc])
                WHERE [Id] = 1
                """));
        Assert.Equal(51005, updateException.Number);
        Assert.Equal(sealBytes, await ReadPermanentSealBytesAsync(database.ConnectionString));

        var deleteException = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteSqlAsync(
                database.ConnectionString,
                "DELETE FROM [dbo].[IdentityBootstrapState] WHERE [Id] = 1"));
        Assert.Equal(51005, deleteException.Number);
        Assert.Equal(sealBytes, await ReadPermanentSealBytesAsync(database.ConnectionString));

        var siteAdministratorRoleId = await verificationContext.Set<HusayniaIdentityRole>()
            .Where(role => role.Name == RoleNames.SiteAdministrator)
            .Select(role => role.Id)
            .SingleAsync();
        verificationContext.Remove(new IdentityUserRole<Guid>
        {
            UserId = firstAdmin.Id,
            RoleId = siteAdministratorRoleId,
        });
        await verificationContext.SaveChangesAsync();

        await using (var secondFactory = new IdentityWebApplicationFactory(database, matchingConfig))
        {
            using var client = secondFactory.CreateIdentityClient();
            _ = await client.GetAsync("/admin/identity/antiforgery");
        }

        await using var secondVerificationContext = database.CreateContext();
        Assert.Equal(6, await secondVerificationContext.Set<HusayniaIdentityRole>().CountAsync());
        Assert.Equal(1, await secondVerificationContext.Set<HusayniaIdentityUser>()
            .CountAsync(user => user.Email == "bootstrap@example.test"));
        var adminId = await secondVerificationContext.Set<HusayniaIdentityUser>()
            .Where(user => user.Email == "bootstrap@example.test")
            .Select(user => user.Id)
            .SingleAsync();
        var userRoles = await secondVerificationContext.Set<IdentityUserRole<Guid>>()
            .CountAsync(link => link.UserId == adminId);
        Assert.Equal(0, userRoles);
        Assert.Equal(
            1,
            await secondVerificationContext.Set<IdentityBootstrapState>().CountAsync());
        Assert.Equal(
            2,
            await secondVerificationContext.Set<AuditEvent>()
                .CountAsync(entry => entry.Action.StartsWith("identity.bootstrap.")));
    }

    [Fact]
    public async Task ConcurrentBootstrapInstancesCreateOneAdministratorAndOneSeal()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(ConcurrentBootstrapInstancesCreateOneAdministratorAndOneSeal));
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Identity:Bootstrap:Enabled"] = "true",
            ["Identity:Bootstrap:EnvironmentName"] = "IntegrationTesting",
            ["Identity:Bootstrap:AdminEmail"] = "concurrent-bootstrap@example.test",
            ["Identity:Bootstrap:AdminPassword"] = "BootstrapAdmin!234",
        };
        await using var firstFactory = new IdentityWebApplicationFactory(database, configuration);
        await using var secondFactory = new IdentityWebApplicationFactory(database, configuration);
        using var firstClient = firstFactory.CreateIdentityClient();
        using var secondClient = secondFactory.CreateIdentityClient();

        await Task.WhenAll(
            firstClient.GetAsync("/admin/identity/antiforgery"),
            secondClient.GetAsync("/admin/identity/antiforgery"));

        await using var context = database.CreateContext();
        Assert.Equal(
            1,
            await context.Set<HusayniaIdentityUser>()
                .CountAsync(user => user.Email == "concurrent-bootstrap@example.test"));
        Assert.Equal(1, await context.Set<IdentityBootstrapState>().CountAsync());
        Assert.Equal(
            2,
            await context.Set<AuditEvent>()
                .CountAsync(entry => entry.Action.StartsWith("identity.bootstrap.")));
    }

    [Fact]
    public async Task InvariantSqlIsExactIdempotentAndReinstallable()
    {
        Assert.Equal(ExpectedInstallSql, IdentityAuditDatabaseInvariant.InstallSql);
        Assert.Equal(ExpectedDownSql, IdentityAuditDatabaseInvariant.DownSql);

        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(InvariantSqlIsExactIdempotentAndReinstallable));

        await database.InstallInvariantsAsync();
        await database.InstallInvariantsAsync();
        Assert.Equal(9, await CountOwnedSchemaObjectsAsync(database.ConnectionString));

        await database.DownInvariantsAsync();
        await database.DownInvariantsAsync();
        Assert.Equal(0, await CountOwnedSchemaObjectsAsync(database.ConnectionString));

        await database.ReinstallInvariantsAsync();
        Assert.Equal(9, await CountOwnedSchemaObjectsAsync(database.ConnectionString));
    }

    [Fact]
    public async Task OrdinaryAndBootstrapStartupNeverExecuteOwnedDdl()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(OrdinaryAndBootstrapStartupNeverExecuteOwnedDdl));
        var interceptor = new RejectIdentityTriggerDdlInterceptor();
        await using (var factory = CreateFactory(database, interceptor))
        {
            using var client = factory.CreateIdentityClient();
            var response = await client.GetAsync("/admin/identity/antiforgery");
            response.EnsureSuccessStatusCode();
        }

        var bootstrapConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Identity:Bootstrap:Enabled"] = "true",
            ["Identity:Bootstrap:EnvironmentName"] = "IntegrationTesting",
            ["Identity:Bootstrap:AdminEmail"] = "ddl-bootstrap@example.test",
            ["Identity:Bootstrap:AdminPassword"] = "BootstrapAdmin!234",
        };
        await using (var factory = CreateFactory(database, interceptor, bootstrapConfiguration))
        {
            using var client = factory.CreateIdentityClient();
            var response = await client.GetAsync("/admin/identity/antiforgery");
            response.EnsureSuccessStatusCode();
        }

        Assert.False(interceptor.ObservedTriggerDdl);
    }

    private static IdentityWebApplicationFactory CreateFactory(
        IdentitySqlServerTestDatabase database,
        RejectIdentityTriggerDdlInterceptor interceptor,
        IReadOnlyDictionary<string, string?>? configuration = null) =>
        new(
            database,
            configuration,
            configureServices: services =>
                services.AddDbContext<HusayniaIdentityDbContext>(
                    (_, options) => options.AddInterceptors(interceptor)));

    private static async Task ExecuteSqlAsync(string connectionString, string commandText)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<byte[]> ReadPermanentSealBytesAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                CONVERT(varbinary(max), [Id]) +
                CONVERT(varbinary(max), DATALENGTH([EnvironmentName])) +
                CONVERT(varbinary(max), [EnvironmentName]) +
                CONVERT(varbinary(max), [AdministratorUserId]) +
                CONVERT(varbinary(max), [SealedAtUtc])
            FROM [dbo].[IdentityBootstrapState]
            WHERE [Id] = 1
            """;
        var value = await command.ExecuteScalarAsync();
        return Assert.IsType<byte[]>(value);
    }

    private static async Task<int> CountOwnedSchemaObjectsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                CASE WHEN OBJECT_ID(N'[dbo].[IdentityAnonymousRateLimits]', N'U') IS NULL THEN 0 ELSE 1 END +
                CASE WHEN OBJECT_ID(N'[dbo].[TR_IdentityAuditEvents_AppendOnly]', N'TR') IS NULL THEN 0 ELSE 1 END +
                CASE WHEN OBJECT_ID(N'[dbo].[TR_IdentityBootstrapState_PermanentSeal]', N'TR') IS NULL THEN 0 ELSE 1 END +
                (SELECT COUNT(*) FROM sys.key_constraints
                 WHERE [name] IN
                 (
                     N'PK_IdentityAnonymousRateLimits',
                     N'UQ_IdentityAnonymousRateLimits_EndpointFamily_ClientFingerprint'
                 )
                   AND [parent_object_id] = OBJECT_ID(N'[dbo].[IdentityAnonymousRateLimits]')) +
                (SELECT COUNT(*) FROM sys.check_constraints
                 WHERE [name] IN
                 (
                     N'CK_IdentityAnonymousRateLimits_RequestCount',
                     N'CK_IdentityAnonymousRateLimits_Window',
                     N'CK_IdentityAnonymousRateLimits_Retention'
                 )
                   AND [parent_object_id] = OBJECT_ID(N'[dbo].[IdentityAnonymousRateLimits]')) +
                (SELECT COUNT(*) FROM sys.indexes
                 WHERE [name] = N'IX_IdentityAnonymousRateLimits_RetainUntilUtc'
                   AND [object_id] = OBJECT_ID(N'[dbo].[IdentityAnonymousRateLimits]'))
            """;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private sealed class RejectIdentityTriggerDdlInterceptor : DbCommandInterceptor
    {
        internal bool ObservedTriggerDdl { get; private set; }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            RejectTriggerDdl(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            RejectTriggerDdl(command);
            return ValueTask.FromResult(result);
        }

        private void RejectTriggerDdl(DbCommand command)
        {
            var isDdl =
                command.CommandText.Contains("CREATE", StringComparison.OrdinalIgnoreCase) ||
                command.CommandText.Contains("ALTER", StringComparison.OrdinalIgnoreCase) ||
                command.CommandText.Contains("DROP", StringComparison.OrdinalIgnoreCase);
            var namesOwnedObject =
                command.CommandText.Contains(
                    IdentityAuditDatabaseInvariant.TriggerName,
                    StringComparison.OrdinalIgnoreCase) ||
                command.CommandText.Contains(
                    IdentityAuditDatabaseInvariant.BootstrapSealTriggerName,
                    StringComparison.OrdinalIgnoreCase) ||
                command.CommandText.Contains(
                    IdentityAuditDatabaseInvariant.RateLimitTableName,
                    StringComparison.OrdinalIgnoreCase);
            if (!isDdl || !namesOwnedObject)
            {
                return;
            }

            ObservedTriggerDdl = true;
            throw new InvalidOperationException("Runtime startup attempted identity trigger DDL.");
        }
    }
}
