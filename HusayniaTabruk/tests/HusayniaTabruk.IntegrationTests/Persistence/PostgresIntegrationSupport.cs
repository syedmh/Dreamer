using System.Runtime.ExceptionServices;
using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;
using HusayniaTabruk.Domain.Notifications;
using HusayniaTabruk.Infrastructure.Identity.Entities;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Sdk;

namespace HusayniaTabruk.IntegrationTests.Persistence;

[CollectionDefinition(nameof(PostgresPersistenceCollectionDefinition), DisableParallelization = true)]
public sealed class PostgresPersistenceCollectionDefinition;

[Collection(nameof(PostgresPersistenceCollectionDefinition))]
public abstract class PostgresPersistenceTest
{
    protected static Task<PostgresTestDatabase> CreateDatabaseAsync(bool applyMigrations = true) =>
        PostgresTestDatabase.CreateAsync(applyMigrations);
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresPostgresFactAttribute : FactAttribute
{
    public RequiresPostgresFactAttribute()
    {
        string? connectionString =
            Environment.GetEnvironmentVariable("TABRUK_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Skip =
                "PostgreSQL persistence tests require TABRUK_TEST_POSTGRES_CONNECTION; no connection string was supplied.";
            return;
        }

        try
        {
            using NpgsqlConnection connection = new(connectionString);
            connection.Open();
        }
        catch (NpgsqlException exception)
        {
            Skip =
                $"PostgreSQL persistence tests could not connect using TABRUK_TEST_POSTGRES_CONNECTION: {exception.Message}";
        }
    }
}

public sealed class RequiresPostgresTheoryAttribute : TheoryAttribute
{
    public RequiresPostgresTheoryAttribute()
    {
        string? connectionString =
            Environment.GetEnvironmentVariable("TABRUK_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Skip =
                "PostgreSQL persistence tests require TABRUK_TEST_POSTGRES_CONNECTION; no connection string was supplied.";
            return;
        }

        try
        {
            using NpgsqlConnection connection = new(connectionString);
            connection.Open();
        }
        catch (NpgsqlException exception)
        {
            Skip =
                $"PostgreSQL persistence tests could not connect using TABRUK_TEST_POSTGRES_CONNECTION: {exception.Message}";
        }
    }
}

public sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly string administrativeConnectionString;
    private PostgresDefaultAclLease? defaultAclLease;

    private PostgresTestDatabase(
        string administrativeConnectionString,
        string schema,
        string connectionString,
        PostgresDefaultAclLease defaultAclLease)
    {
        this.administrativeConnectionString = administrativeConnectionString;
        this.defaultAclLease = defaultAclLease;
        Schema = schema;
        ConnectionString = connectionString;
    }

    public string Schema { get; }
    public string ConnectionString { get; }
    public string AdministrativeConnectionString => administrativeConnectionString;

    public TabrukDbContext CreateContext()
    {
        DbContextOptions<TabrukDbContext> options =
            new DbContextOptionsBuilder<TabrukDbContext>()
                .UseNpgsql(ConnectionString)
                .EnableDetailedErrors()
                .Options;
        return new TabrukDbContext(options);
    }

    public TabrukDbContext CreateMigrationContext() =>
        TabrukDbContextOptions.Create(ConnectionString);

    public static async Task<PostgresTestDatabase> CreateAsync(bool applyMigrations)
    {
        string? suppliedConnectionString =
            Environment.GetEnvironmentVariable("TABRUK_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(suppliedConnectionString))
        {
            throw SkipException.ForSkip(
                "PostgreSQL persistence tests require TABRUK_TEST_POSTGRES_CONNECTION; no connection string was supplied.");
        }

        await using NpgsqlConnection connection = new(suppliedConnectionString);
        try
        {
            await connection.OpenAsync();
        }
        catch (NpgsqlException exception)
        {
            throw SkipException.ForSkip(
                $"PostgreSQL persistence tests could not connect using TABRUK_TEST_POSTGRES_CONNECTION: {exception.Message}");
        }

        await EnsureApplicationRoleAsync(connection);
        PostgresDefaultAclLease defaultAclLease =
            await PostgresDefaultAclRegistry.AcquireAsync(connection);
        PostgresTestDatabase? database = null;
        try
        {
            string schema = $"tabruk_it_{Guid.NewGuid():N}";
            await using (NpgsqlCommand command = new($"CREATE SCHEMA \"{schema}\"", connection))
            {
                await command.ExecuteNonQueryAsync();
            }

            NpgsqlConnectionStringBuilder builder = new(suppliedConnectionString)
            {
                SearchPath = schema,
                Options = $"-c tabruk.target_schema={schema} -c tabruk.disposable_ef=on",
            };
            database = new(
                suppliedConnectionString,
                schema,
                builder.ConnectionString,
                defaultAclLease);

            if (applyMigrations)
            {
                await using TabrukDbContext context = database.CreateMigrationContext();
                await context.Database.MigrateAsync();
            }

            return database;
        }
        catch (Exception exception)
        {
            try
            {
                if (database is null)
                {
                    await defaultAclLease.DisposeAsync();
                }
                else
                {
                    await database.DisposeAsync();
                }
            }
            catch (Exception cleanupException)
            {
                ReportCleanupFailure(exception, cleanupException);
            }

            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
    }

    internal static void ReportCleanupFailure(
        Exception originalException,
        Exception cleanupException)
    {
        originalException.Data["PostgresTestDatabase cleanup failure"] = cleanupException.ToString();
        Console.Error.WriteLine(
            $"PostgresTestDatabase cleanup failed while preserving the original exception: {cleanupException}");
    }

    internal static async Task EnsureSafeGlobalDefaultPrivilegesAsync(
        NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new(
            """
            DO $tabruk$
            DECLARE
                inherited_role record;
                object_kind text;
            BEGIN
                FOREACH object_kind IN ARRAY ARRAY['TABLES', 'SEQUENCES', 'FUNCTIONS', 'TYPES']
                LOOP
                    EXECUTE format(
                        'ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT ALL PRIVILEGES ON %s TO %I',
                        current_user,
                        object_kind,
                        current_user);
                    EXECUTE format(
                        'ALTER DEFAULT PRIVILEGES FOR ROLE %I REVOKE ALL PRIVILEGES ON %s FROM PUBLIC, %I',
                        current_user,
                        object_kind,
                        'tabruk_app');

                    FOR inherited_role IN
                        SELECT role_definition.rolname
                        FROM pg_roles AS role_definition
                        WHERE role_definition.rolname NOT IN (current_user, 'tabruk_app')
                          AND pg_has_role('tabruk_app', role_definition.oid, 'MEMBER')
                    LOOP
                        EXECUTE format(
                            'ALTER DEFAULT PRIVILEGES FOR ROLE %I REVOKE ALL PRIVILEGES ON %s FROM %I',
                            current_user,
                            object_kind,
                            inherited_role.rolname);
                    END LOOP;
                END LOOP;
            END
            $tabruk$;
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task EnsureApplicationRoleAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand roleLookup =
            new("SELECT rolcanlogin FROM pg_roles WHERE rolname = 'tabruk_app'", connection);
        object? roleCanLogin = await roleLookup.ExecuteScalarAsync();
        if (roleCanLogin is null)
        {
            throw SkipException.ForSkip(
                "PostgreSQL persistence tests require a DBA-pre-provisioned tabruk_app NOLOGIN role; the fixture never creates or changes cluster roles.");
        }

        if (roleCanLogin is not false)
        {
            throw new InvalidOperationException(
                "The PostgreSQL persistence test fixture requires the pre-provisioned tabruk_app role to be NOLOGIN.");
        }
    }

    public string BuildMigrationConnectionString(string? searchPath, string? options)
    {
        NpgsqlConnectionStringBuilder builder = new(administrativeConnectionString);
        if (searchPath is null)
        {
            builder.Remove("Search Path");
        }
        else
        {
            builder.SearchPath = searchPath;
        }

        if (options is null)
        {
            builder.Remove("Options");
        }
        else
        {
            builder.Options = options;
        }

        return builder.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        PostgresDefaultAclLease? lease = Interlocked.Exchange(ref defaultAclLease, null);
        if (lease is null)
        {
            return;
        }

        Exception? schemaCleanupException = null;
        try
        {
            await using NpgsqlConnection connection = new(administrativeConnectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand command =
                new($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception exception)
        {
            schemaCleanupException = exception;
        }

        try
        {
            await lease.DisposeAsync();
        }
        catch (Exception defaultAclCleanupException)
        {
            if (schemaCleanupException is not null)
            {
                throw new AggregateException(
                    "PostgreSQL test fixture schema and global default ACL cleanup both failed.",
                    schemaCleanupException,
                    defaultAclCleanupException);
            }

            throw;
        }

        if (schemaCleanupException is not null)
        {
            ExceptionDispatchInfo.Capture(schemaCleanupException).Throw();
        }
    }
}

internal sealed class PostgresDefaultAclLease : IAsyncDisposable
{
    private string? registryKey;
    private readonly string connectionString;

    public PostgresDefaultAclLease(string registryKey, string connectionString)
    {
        this.registryKey = registryKey;
        this.connectionString = connectionString;
    }

    public async ValueTask DisposeAsync()
    {
        string? key = Interlocked.Exchange(ref registryKey, null);
        if (key is not null)
        {
            await PostgresDefaultAclRegistry.ReleaseAsync(key, connectionString);
        }
    }
}

internal static class PostgresDefaultAclRegistry
{
    private const long AdvisoryLock = 84150815102619;
    private static readonly SemaphoreSlim RegistryLock = new(1, 1);
    private static readonly Dictionary<string, RegistryEntry> Entries = [];

    public static async Task<PostgresDefaultAclLease> AcquireAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(connection, $"SELECT pg_advisory_lock({AdvisoryLock})");
        Exception? operationException = null;
        try
        {
            string key = await GetRegistryKeyAsync(connection);
            await RegistryLock.WaitAsync();
            try
            {
                DefaultAclSnapshot? newSnapshot = null;
                if (Entries.TryGetValue(key, out RegistryEntry? entry))
                {
                    await PostgresTestDatabase.EnsureSafeGlobalDefaultPrivilegesAsync(connection);
                    entry.ReferenceCount++;
                }
                else
                {
                    newSnapshot = await CaptureAsync(connection);
                    try
                    {
                        await PostgresTestDatabase.EnsureSafeGlobalDefaultPrivilegesAsync(connection);
                    }
                    catch (Exception exception)
                    {
                        try
                        {
                            await RestoreAsync(connection, newSnapshot);
                        }
                        catch (Exception cleanupException)
                        {
                            PostgresTestDatabase.ReportCleanupFailure(
                                exception,
                                cleanupException);
                        }

                        throw;
                    }

                    Entries.Add(key, new RegistryEntry(newSnapshot));
                }
            }
            finally
            {
                RegistryLock.Release();
            }

            return new PostgresDefaultAclLease(key, connection.ConnectionString);
        }
        catch (Exception exception)
        {
            operationException = exception;
            throw;
        }
        finally
        {
            try
            {
                await ExecuteAsync(
                    connection,
                    $"SELECT pg_advisory_unlock({AdvisoryLock})");
            }
            catch (Exception cleanupException) when (operationException is not null)
            {
                PostgresTestDatabase.ReportCleanupFailure(
                    operationException,
                    cleanupException);
            }
        }
    }

    public static async Task ReleaseAsync(string key, string connectionString)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"SELECT pg_advisory_lock({AdvisoryLock})");
        Exception? operationException = null;
        try
        {
            await RegistryLock.WaitAsync();
            try
            {
                if (!Entries.TryGetValue(key, out RegistryEntry? entry))
                {
                    throw new InvalidOperationException(
                        $"No PostgreSQL global default ACL fixture registration exists for '{key}'.");
                }

                entry.ReferenceCount--;
                if (entry.ReferenceCount == 0)
                {
                    await RestoreAsync(connection, entry.Snapshot);
                    Entries.Remove(key);
                }
            }
            finally
            {
                RegistryLock.Release();
            }
        }
        catch (Exception exception)
        {
            operationException = exception;
            throw;
        }
        finally
        {
            try
            {
                await ExecuteAsync(
                    connection,
                    $"SELECT pg_advisory_unlock({AdvisoryLock})");
            }
            catch (Exception cleanupException) when (operationException is not null)
            {
                PostgresTestDatabase.ReportCleanupFailure(
                    operationException,
                    cleanupException);
            }
        }
    }

    private static async Task<string> GetRegistryKeyAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new(
            """
            SELECT concat_ws(
                '/',
                COALESCE(inet_server_addr()::text, 'local'),
                inet_server_port()::text,
                (SELECT oid::text FROM pg_database WHERE datname = current_database()),
                (SELECT oid::text FROM pg_roles WHERE rolname = current_user))
            """,
            connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<DefaultAclSnapshot> CaptureAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new(
            """
            SELECT
                COALESCE(
                    string_agg(
                        default_acl.defaclobjtype::text || ':' || default_acl.defaclacl::text,
                        ',' ORDER BY default_acl.defaclobjtype),
                    '<none>'),
                COALESCE(
                    jsonb_agg(
                        jsonb_build_object(
                            'objectType', default_acl.defaclobjtype::text,
                            'acl', default_acl.defaclacl::text)
                        ORDER BY default_acl.defaclobjtype),
                    '[]'::jsonb)::text
            FROM pg_default_acl AS default_acl
            WHERE default_acl.defaclrole =
                      (SELECT oid FROM pg_roles WHERE rolname = current_user)
              AND default_acl.defaclnamespace = 0
              AND default_acl.defaclobjtype IN ('r', 'S', 'f', 'T')
            """,
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new DefaultAclSnapshot(reader.GetString(0), reader.GetString(1));
    }

    private static async Task RestoreAsync(
        NpgsqlConnection connection,
        DefaultAclSnapshot snapshot)
    {
        string restoreSql =
            """
            DO $tabruk$
            DECLARE
                snapshot jsonb := '@snapshot_json'::jsonb;
                object_definition record;
                snapshot_acl aclitem[];
                grantee_name text;
                privilege_definition record;
                grantee_sql text;
            BEGIN
                FOR object_definition IN
                    SELECT *
                    FROM (VALUES
                        ('r', 'TABLES', 'ALL PRIVILEGES', NULL::text),
                        ('S', 'SEQUENCES', 'ALL PRIVILEGES', NULL::text),
                        ('f', 'FUNCTIONS', 'ALL PRIVILEGES', 'EXECUTE'),
                        ('T', 'TYPES', 'ALL PRIVILEGES', 'USAGE')
                    ) AS definitions(object_type, object_kind, owner_privileges, public_privileges)
                LOOP
                    SELECT (row_definition ->> 'acl')::aclitem[]
                    INTO snapshot_acl
                    FROM jsonb_array_elements(snapshot) AS row_definition
                    WHERE row_definition ->> 'objectType' = object_definition.object_type;

                    FOR grantee_name IN
                        SELECT DISTINCT candidate.name
                        FROM (
                            SELECT CASE
                                WHEN exploded.grantee = 0 THEN 'PUBLIC'
                                ELSE pg_get_userbyid(exploded.grantee)
                            END AS name
                            FROM pg_default_acl AS default_acl
                            CROSS JOIN LATERAL aclexplode(default_acl.defaclacl) AS exploded
                            WHERE default_acl.defaclrole =
                                      (SELECT oid FROM pg_roles WHERE rolname = current_user)
                              AND default_acl.defaclnamespace = 0
                              AND default_acl.defaclobjtype =
                                      object_definition.object_type::"char"
                            UNION ALL
                            SELECT CASE
                                WHEN exploded.grantee = 0 THEN 'PUBLIC'
                                ELSE pg_get_userbyid(exploded.grantee)
                            END
                            FROM aclexplode(snapshot_acl) AS exploded
                            UNION ALL
                            SELECT current_user
                            UNION ALL
                            SELECT 'PUBLIC'
                        ) AS candidate
                    LOOP
                        grantee_sql := CASE
                            WHEN grantee_name = 'PUBLIC' THEN 'PUBLIC'
                            ELSE format('%I', grantee_name)
                        END;
                        EXECUTE format(
                            'ALTER DEFAULT PRIVILEGES FOR ROLE %I REVOKE ALL PRIVILEGES ON %s FROM %s',
                            current_user,
                            object_definition.object_kind,
                            grantee_sql);
                    END LOOP;

                    IF snapshot_acl IS NULL THEN
                        EXECUTE format(
                            'ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT %s ON %s TO %I',
                            current_user,
                            object_definition.owner_privileges,
                            object_definition.object_kind,
                            current_user);
                        IF object_definition.public_privileges IS NOT NULL THEN
                            EXECUTE format(
                                'ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT %s ON %s TO PUBLIC',
                                current_user,
                                object_definition.public_privileges,
                                object_definition.object_kind);
                        END IF;
                    ELSE
                        FOR privilege_definition IN
                            SELECT
                                CASE
                                    WHEN exploded.grantee = 0 THEN 'PUBLIC'
                                    ELSE pg_get_userbyid(exploded.grantee)
                                END AS grantee,
                                exploded.privilege_type,
                                exploded.is_grantable
                            FROM aclexplode(snapshot_acl) AS exploded
                        LOOP
                            grantee_sql := CASE
                                WHEN privilege_definition.grantee = 'PUBLIC' THEN 'PUBLIC'
                                ELSE format('%I', privilege_definition.grantee)
                            END;
                            EXECUTE format(
                                'ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT %s ON %s TO %s%s',
                                current_user,
                                privilege_definition.privilege_type,
                                object_definition.object_kind,
                                grantee_sql,
                                CASE
                                    WHEN privilege_definition.is_grantable
                                        THEN ' WITH GRANT OPTION'
                                    ELSE ''
                                END);
                        END LOOP;
                    END IF;

                    snapshot_acl := NULL;
                END LOOP;
            END
            $tabruk$;
            """;
        restoreSql = restoreSql.Replace(
            "@snapshot_json",
            snapshot.Json.Replace("'", "''", StringComparison.Ordinal),
            StringComparison.Ordinal);
        await using NpgsqlCommand command = new(restoreSql, connection);
        await command.ExecuteNonQueryAsync();

        string actualFingerprint = await GetFingerprintAsync(connection);
        if (!string.Equals(
                snapshot.Fingerprint,
                actualFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "PostgreSQL global default ACL restoration did not reproduce the captured fixture baseline. "
                + $"Expected '{snapshot.Fingerprint}', actual '{actualFingerprint}'.");
        }
    }

    private static async Task<string> GetFingerprintAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new(
            """
            SELECT COALESCE(
                string_agg(
                    default_acl.defaclobjtype::text || ':' || default_acl.defaclacl::text,
                    ',' ORDER BY default_acl.defaclobjtype),
                '<none>')
            FROM pg_default_acl AS default_acl
            WHERE default_acl.defaclrole =
                      (SELECT oid FROM pg_roles WHERE rolname = current_user)
              AND default_acl.defaclnamespace = 0
              AND default_acl.defaclobjtype IN ('r', 'S', 'f', 'T')
            """,
            connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class RegistryEntry(DefaultAclSnapshot snapshot)
    {
        public DefaultAclSnapshot Snapshot { get; } = snapshot;
        public int ReferenceCount { get; set; } = 1;
    }

    private sealed record DefaultAclSnapshot(string Fingerprint, string Json);
}

internal sealed record PersistenceSeed(
    OrganizationId OrganizationId,
    MembershipId ManagerMembershipId,
    MembershipId SecondAdministratorMembershipId,
    MembershipId MemberMembershipId,
    ServiceDateId ServiceDateId,
    HelpNeedId HelpNeedId,
    ThreadId ThreadId,
    DateTimeOffset Now)
{
    public static async Task<PersistenceSeed> CreateAsync(PostgresTestDatabase database)
    {
        OrganizationId organizationId = OrganizationId.New();
        MembershipId managerMembershipId = MembershipId.New();
        MembershipId secondAdministratorMembershipId = MembershipId.New();
        MembershipId memberMembershipId = MembershipId.New();
        ServiceDateId serviceDateId = ServiceDateId.New();
        HelpNeedId helpNeedId = HelpNeedId.New();
        ThreadId threadId = ThreadId.New();
        DateTimeOffset now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        await using TabrukDbContext context = database.CreateContext();
        Guid managerUserId = UserId.New().Value;
        Guid secondAdminUserId = UserId.New().Value;
        Guid memberUserId = UserId.New().Value;
        context.Users.AddRange(
            User(managerUserId, "manager@example.test"),
            User(secondAdminUserId, "admin@example.test"),
            User(memberUserId, "member@example.test"));
        context.Organizations.Add(
            new OrganizationEntity
            {
                Id = organizationId.Value,
                Name = "Test organization",
                TimeZone = "America/Los_Angeles",
                DefaultCancellationLeadMinutes = 60,
                Status = 0,
                BootstrapStatus = (short)AdministratorBootstrapStatus.Sealed,
                BootstrapSealedAt = now,
                Version = 0,
            });
        context.Memberships.AddRange(
            CreateMembershipEntity(managerMembershipId, organizationId, managerUserId, "Manager"),
            CreateMembershipEntity(secondAdministratorMembershipId, organizationId, secondAdminUserId, "Second administrator"),
            CreateMembershipEntity(memberMembershipId, organizationId, memberUserId, "Member"));
        context.RoleAssignments.AddRange(
            Role(organizationId, managerMembershipId, OrganizationRole.Admin, secondAdministratorMembershipId, now),
            Role(organizationId, secondAdministratorMembershipId, OrganizationRole.Admin, managerMembershipId, now),
            Role(organizationId, managerMembershipId, OrganizationRole.FoodIncharge, secondAdministratorMembershipId, now));
        context.ServiceDates.Add(
            new ServiceDateEntity
            {
                Id = serviceDateId.Value,
                OrganizationId = organizationId.Value,
                Title = "Test service date",
                Instructions = "Prepare food.",
                StartsAt = now.AddDays(1),
                EndsAt = now.AddDays(1).AddHours(4),
                CancellationDeadlineAt = now.AddHours(23),
                ManagerMembershipId = managerMembershipId.Value,
                Status = (short)ServiceDateStatus.Open,
                Version = 2,
            });
        context.HelpNeeds.Add(
            new HelpNeedEntity
            {
                Id = helpNeedId.Value,
                OrganizationId = organizationId.Value,
                ServiceDateId = serviceDateId.Value,
                Category = (short)HelpCategory.FoodPreparation,
                Instructions = "Chop vegetables.",
                Capacity = 10,
                Status = (short)HelpNeedStatus.Open,
                Version = 0,
                SignupVersion = 0,
                WaitlistOrderHighWater = 0,
            });
        context.DateThreads.Add(
            new DateThreadEntity
            {
                Id = threadId.Value,
                OrganizationId = organizationId.Value,
                ServiceDateId = serviceDateId.Value,
                Status = (short)ThreadStatus.Open,
                Version = 0,
            });
        await context.SaveChangesAsync();

        return new PersistenceSeed(
            organizationId,
            managerMembershipId,
            secondAdministratorMembershipId,
            memberMembershipId,
            serviceDateId,
            helpNeedId,
            threadId,
            now);
    }

    public (Membership Manager, Membership Member, ServiceDate ServiceDate) CreateThreadDomainView()
    {
        Membership manager = ActiveMembership(ManagerMembershipId, OrganizationId, "Manager");
        Membership secondAdmin = ActiveMembership(
            SecondAdministratorMembershipId,
            OrganizationId,
            "Second administrator");
        Membership member = ActiveMembership(MemberMembershipId, OrganizationId, "Member");
        Result<OrganizationAccountGovernance> governance =
            OrganizationAccountGovernance.Rehydrate(
                OrganizationId,
                version: 0,
                AdministratorBootstrapStatus.Unsealed,
                bootstrapSealedAt: null,
                [manager, secondAdmin, member],
                []);
        Assert.True(governance.IsSuccess, governance.IsFailure ? governance.Error.Message : null);
        Result<AdministratorBootstrapCompleted> bootstrap = governance.Value.BootstrapAdministrators(
            governance.Value.Memberships.Where(
                membership => membership.Id is var id
                    && (id == ManagerMembershipId || id == SecondAdministratorMembershipId)).ToArray(),
            Now);
        Assert.True(bootstrap.IsSuccess, bootstrap.IsFailure ? bootstrap.Error.Message : null);
        Result<FoodInchargeAssigned> assigned = governance.Value.AssignFoodIncharge(
            governance.Value.Memberships.Single(membership => membership.Id == SecondAdministratorMembershipId),
            governance.Value.Memberships.Single(membership => membership.Id == ManagerMembershipId),
            Now);
        Assert.True(assigned.IsSuccess, assigned.IsFailure ? assigned.Error.Message : null);

        Result<HelpNeed> need = HelpNeed.Rehydrate(
            HelpNeedId,
            ServiceDateId,
            HelpCategory.FoodPreparation,
            "Chop vegetables.",
            capacity: 10,
            HelpNeedStatus.Open,
            version: 0);
        Assert.True(need.IsSuccess, need.IsFailure ? need.Error.Message : null);
        Result<ServiceDate> date = ServiceDate.Rehydrate(
            ServiceDateId,
            OrganizationId,
            "Test service date",
            "Prepare food.",
            Now.AddDays(1),
            Now.AddDays(1).AddHours(4),
            Now.AddHours(23),
            ManagerMembershipId,
            ServiceDateStatus.Open,
            version: 2,
            [need.Value]);
        Assert.True(date.IsSuccess, date.IsFailure ? date.Error.Message : null);

        return (
            governance.Value.Memberships.Single(membership => membership.Id == ManagerMembershipId),
            governance.Value.Memberships.Single(membership => membership.Id == MemberMembershipId),
            date.Value);
    }

    public async Task<MessageId> AddVisibleThreadMessageAsync(PostgresTestDatabase database)
    {
        MessageId messageId = MessageId.New();
        await using TabrukDbContext context = database.CreateContext();
        context.ThreadMessages.Add(
            new ThreadMessageEntity
            {
                Id = messageId.Value,
                OrganizationId = OrganizationId.Value,
                ThreadId = ThreadId.Value,
                AuthorMembershipId = ManagerMembershipId.Value,
                ClientMessageId = IdempotencyKey.New().Value,
                Body = "A message that may be reported or hidden.",
                Visibility = (short)MessageVisibility.Visible,
                CreatedAt = Now,
            });
        DateThreadEntity thread = await context.DateThreads.SingleAsync(candidate => candidate.Id == ThreadId.Value);
        thread.Version = 1;
        await context.SaveChangesAsync();
        return messageId;
    }

    public ThreadPersistenceEffects Effects(string purpose, Guid? messageId = null)
    {
        Result<NotificationCreated> notification = Notification.Create(
            NotificationId.New(),
            OrganizationId,
            MemberMembershipId,
            NotificationType.ThreadMessagePosted,
            NotificationResourceType.Thread,
            ThreadId.Value,
            "Thread update",
            "There is a new thread update.",
            Now);
        Assert.True(notification.IsSuccess, notification.IsFailure ? notification.Error.Message : null);

        IReadOnlyCollection<ThreadModerationWrite> moderation = messageId.HasValue
            ? [
                new ThreadModerationWrite(
                    Guid.CreateVersion7(),
                    OrganizationId,
                    ThreadId,
                    MessageId.From(messageId.Value),
                    ManagerMembershipId,
                    purpose,
                    "integration test",
                    Now),
            ]
            : purpose == "lock"
                ? [
                    new ThreadModerationWrite(
                        Guid.CreateVersion7(),
                        OrganizationId,
                        ThreadId,
                        null,
                        ManagerMembershipId,
                        purpose,
                        "integration test",
                        Now),
                ]
                : [];
        return new ThreadPersistenceEffects(
            [
                notification.Value.Notification,
            ],
            [
                new AuditEntry(
                    AuditEventId.New(),
                    OrganizationId,
                    ManagerMembershipId,
                    $"thread_{purpose}",
                    "thread",
                    ThreadId.ToString(),
                    "integration test",
                    "moderation",
                    Guid.NewGuid().ToString("N"),
                    beforeState: null,
                    afterState: null,
                    Now),
            ],
            [],
            [
                new OutboxMessage(
                    OutboxMessageId.New(),
                    OrganizationId,
                    "thread.updated",
                    "{}",
                    Now),
            ],
            moderation);
    }

    public static Membership ActiveMembership(
        MembershipId id,
        OrganizationId organizationId,
        string displayName)
    {
        Result<Membership> membership =
            Membership.Invite(id, organizationId, UserId.New(), displayName, eligibleAsNamedParticipant: true);
        Assert.True(membership.IsSuccess, membership.IsFailure ? membership.Error.Message : null);
        Result<MembershipActivated> activated = membership.Value.Activate(DateTimeOffset.UnixEpoch);
        Assert.True(activated.IsSuccess, activated.IsFailure ? activated.Error.Message : null);
        return membership.Value;
    }

    private static TabrukIdentityUser User(Guid id, string email) =>
        new()
        {
            Id = id,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        };

    private static MembershipEntity CreateMembershipEntity(
        MembershipId membershipId,
        OrganizationId organizationId,
        Guid userId,
        string displayName) =>
        new()
        {
            Id = membershipId.Value,
            OrganizationId = organizationId.Value,
            UserId = userId,
            DisplayName = displayName,
            Status = (short)MembershipStatus.Active,
            EligibleAsNamedParticipant = true,
        };

    private static RoleAssignmentEntity Role(
        OrganizationId organizationId,
        MembershipId membershipId,
        OrganizationRole role,
        MembershipId assignedByMembershipId,
        DateTimeOffset assignedAt) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId.Value,
            MembershipId = membershipId.Value,
            Role = (short)role,
            AssignedByMembershipId = assignedByMembershipId.Value,
            AssignedAt = assignedAt,
        };
}
