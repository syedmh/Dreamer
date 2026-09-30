using Npgsql;

namespace HusayniaTabruk.IntegrationTests.Persistence;

[Collection(nameof(PostgresPersistenceCollectionDefinition))]
public sealed class PostgresTestDatabaseLifecycleTests
{
    private const long GlobalDefaultAclAdvisoryLock = 84150815102619;

    [RequiresPostgresFact]
    public async Task DisposalRestoresEmptyGlobalDefaultAclBaseline()
    {
        await WithGlobalDefaultAclBaselineAsync(
            harden: false,
            async (connection, expectedFingerprint) =>
            {
                await using (PostgresTestDatabase database =
                    await PostgresTestDatabase.CreateAsync(applyMigrations: true))
                {
                    Assert.NotEqual(expectedFingerprint, await GetFingerprintAsync(connection));
                }

                Assert.Equal(expectedFingerprint, await GetFingerprintAsync(connection));
            });
    }

    [RequiresPostgresFact]
    public async Task DisposalRestoresPreHardenedGlobalDefaultAclBaseline()
    {
        await WithGlobalDefaultAclBaselineAsync(
            harden: true,
            async (connection, expectedFingerprint) =>
            {
                await using (PostgresTestDatabase database =
                    await PostgresTestDatabase.CreateAsync(applyMigrations: true))
                {
                    Assert.Equal(expectedFingerprint, await GetFingerprintAsync(connection));
                }

                Assert.Equal(expectedFingerprint, await GetFingerprintAsync(connection));
            });
    }

    [RequiresPostgresFact]
    public async Task DisposalRestoresBaselineWhenTestPathFails()
    {
        await WithGlobalDefaultAclBaselineAsync(
            harden: false,
            async (connection, expectedFingerprint) =>
            {
                InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                    async () =>
                    {
                        await using PostgresTestDatabase database =
                            await PostgresTestDatabase.CreateAsync(applyMigrations: true);
                        throw new InvalidOperationException("intentional test-path failure");
                    });

                Assert.Equal("intentional test-path failure", exception.Message);
                Assert.Equal(expectedFingerprint, await GetFingerprintAsync(connection));
            });
    }

    [RequiresPostgresFact]
    public async Task FirstConcurrentDisposalDoesNotRestoreWhileSecondFixtureIsActive()
    {
        await WithGlobalDefaultAclBaselineAsync(
            harden: false,
            async (connection, expectedFingerprint) =>
            {
                PostgresTestDatabase? first = null;
                PostgresTestDatabase? second = null;
                try
                {
                    first = await PostgresTestDatabase.CreateAsync(applyMigrations: false);
                    await HardenAsync(connection);
                    string activeFingerprint = await GetFingerprintAsync(connection);
                    Assert.NotEqual(expectedFingerprint, activeFingerprint);

                    second = await PostgresTestDatabase.CreateAsync(applyMigrations: false);
                    await first.DisposeAsync();
                    first = null;

                    Assert.Equal(activeFingerprint, await GetFingerprintAsync(connection));

                    await second.DisposeAsync();
                    second = null;

                    Assert.Equal(expectedFingerprint, await GetFingerprintAsync(connection));
                }
                finally
                {
                    if (first is not null)
                    {
                        await first.DisposeAsync();
                    }

                    if (second is not null)
                    {
                        await second.DisposeAsync();
                    }
                }
            });
    }

    [RequiresPostgresFact]
    public async Task SafeGlobalDefaultAclMutationHoldsSharedAdvisoryLock()
    {
        await WithGlobalDefaultAclBaselineAsync(
            harden: false,
            async (connection, _) =>
            {
                await ExecuteAsync(
                    connection,
                    $"SELECT pg_advisory_lock({GlobalDefaultAclAdvisoryLock})");
                try
                {
                    await InstallDefaultAclLockAssertionAsync(connection);
                }
                finally
                {
                    await ExecuteAsync(
                        connection,
                        $"SELECT pg_advisory_unlock({GlobalDefaultAclAdvisoryLock})");
                }

                try
                {
                    await using PostgresTestDatabase database =
                        await PostgresTestDatabase.CreateAsync(applyMigrations: false);
                }
                finally
                {
                    await ExecuteAsync(
                        connection,
                        $"SELECT pg_advisory_lock({GlobalDefaultAclAdvisoryLock})");
                    try
                    {
                        await RemoveDefaultAclLockAssertionAsync(connection);
                    }
                    finally
                    {
                        await ExecuteAsync(
                            connection,
                            $"SELECT pg_advisory_unlock({GlobalDefaultAclAdvisoryLock})");
                    }
                }
            });
    }

    private static async Task WithGlobalDefaultAclBaselineAsync(
        bool harden,
        Func<NpgsqlConnection, string, Task> test)
    {
        string connectionString =
            Environment.GetEnvironmentVariable("TABRUK_TEST_POSTGRES_CONNECTION")!;
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            $"SELECT pg_advisory_lock({GlobalDefaultAclAdvisoryLock})");
        try
        {
            await ResetAsync(connection);
            if (harden)
            {
                await HardenAsync(connection);
            }

            string expectedFingerprint = await GetFingerprintAsync(connection);
            await ExecuteAsync(
                connection,
                $"SELECT pg_advisory_unlock({GlobalDefaultAclAdvisoryLock})");
            try
            {
                await test(connection, expectedFingerprint);
            }
            finally
            {
                await ExecuteAsync(
                    connection,
                    $"SELECT pg_advisory_lock({GlobalDefaultAclAdvisoryLock})");
            }
        }
        finally
        {
            await ResetAsync(connection);
            await ExecuteAsync(
                connection,
                $"SELECT pg_advisory_unlock({GlobalDefaultAclAdvisoryLock})");
        }
    }

    private static Task HardenAsync(NpgsqlConnection connection) =>
        ExecuteAsync(
            connection,
            """
            ALTER DEFAULT PRIVILEGES GRANT ALL PRIVILEGES ON TABLES TO CURRENT_USER;
            ALTER DEFAULT PRIVILEGES REVOKE ALL PRIVILEGES ON TABLES FROM PUBLIC, tabruk_app;
            ALTER DEFAULT PRIVILEGES GRANT ALL PRIVILEGES ON SEQUENCES TO CURRENT_USER;
            ALTER DEFAULT PRIVILEGES REVOKE ALL PRIVILEGES ON SEQUENCES FROM PUBLIC, tabruk_app;
            ALTER DEFAULT PRIVILEGES GRANT ALL PRIVILEGES ON FUNCTIONS TO CURRENT_USER;
            ALTER DEFAULT PRIVILEGES REVOKE ALL PRIVILEGES ON FUNCTIONS FROM PUBLIC, tabruk_app;
            ALTER DEFAULT PRIVILEGES GRANT ALL PRIVILEGES ON TYPES TO CURRENT_USER;
            ALTER DEFAULT PRIVILEGES REVOKE ALL PRIVILEGES ON TYPES FROM PUBLIC, tabruk_app;
            """);

    private static Task ResetAsync(NpgsqlConnection connection) =>
        ExecuteAsync(
            connection,
            """
            ALTER DEFAULT PRIVILEGES REVOKE ALL PRIVILEGES ON TABLES FROM PUBLIC, tabruk_app;
            ALTER DEFAULT PRIVILEGES GRANT ALL PRIVILEGES ON TABLES TO CURRENT_USER;
            ALTER DEFAULT PRIVILEGES REVOKE ALL PRIVILEGES ON SEQUENCES FROM PUBLIC, tabruk_app;
            ALTER DEFAULT PRIVILEGES GRANT ALL PRIVILEGES ON SEQUENCES TO CURRENT_USER;
            ALTER DEFAULT PRIVILEGES REVOKE ALL PRIVILEGES ON FUNCTIONS FROM tabruk_app;
            ALTER DEFAULT PRIVILEGES GRANT ALL PRIVILEGES ON FUNCTIONS TO PUBLIC, CURRENT_USER;
            ALTER DEFAULT PRIVILEGES REVOKE ALL PRIVILEGES ON TYPES FROM tabruk_app;
            ALTER DEFAULT PRIVILEGES GRANT ALL PRIVILEGES ON TYPES TO PUBLIC, CURRENT_USER;
            """);

    private static Task InstallDefaultAclLockAssertionAsync(NpgsqlConnection connection) =>
        ExecuteAsync(
            connection,
            $"""
            DROP EVENT TRIGGER IF EXISTS tabruk_fixture_default_acl_lock_assertion;
            DROP FUNCTION IF EXISTS tabruk_fixture_default_acl_lock_assertion();

            CREATE FUNCTION tabruk_fixture_default_acl_lock_assertion()
            RETURNS event_trigger
            LANGUAGE plpgsql
            AS $body$
            BEGIN
                IF tg_tag = 'ALTER DEFAULT PRIVILEGES'
                    AND NOT EXISTS (
                        SELECT 1
                        FROM pg_locks
                        WHERE locktype = 'advisory'
                          AND pid = pg_backend_pid()
                          AND granted
                          AND classid::bigint =
                              ({GlobalDefaultAclAdvisoryLock}::bigint >> 32)
                          AND objid::bigint =
                              ({GlobalDefaultAclAdvisoryLock}::bigint & 4294967295)
                          AND objsubid = 1)
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE =
                            'PostgreSQL fixture changed global default ACLs without the shared advisory lock';
                END IF;
            END
            $body$;

            CREATE EVENT TRIGGER tabruk_fixture_default_acl_lock_assertion
            ON ddl_command_start
            EXECUTE FUNCTION tabruk_fixture_default_acl_lock_assertion();
            """);

    private static Task RemoveDefaultAclLockAssertionAsync(NpgsqlConnection connection) =>
        ExecuteAsync(
            connection,
            """
            DROP EVENT TRIGGER IF EXISTS tabruk_fixture_default_acl_lock_assertion;
            DROP FUNCTION IF EXISTS tabruk_fixture_default_acl_lock_assertion();
            """);

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
}
