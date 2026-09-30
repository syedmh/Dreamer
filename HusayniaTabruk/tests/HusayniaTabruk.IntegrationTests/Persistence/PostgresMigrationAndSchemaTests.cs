using System.Diagnostics;
using System.Globalization;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Infrastructure.Migrations;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace HusayniaTabruk.IntegrationTests.Persistence;

[Trait("Category", "Persistence")]
public sealed class PostgresMigrationAndSchemaTests : PostgresPersistenceTest
{
    private readonly ITestOutputHelper output;

    public PostgresMigrationAndSchemaTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    private const string InitialMigration = "20260815075156_InitialPostgresSchema";
    private const string CorrectiveMigration = "20260815102612_T8CorrectivePostgresHardening";
    private const string OwnerIdempotentScriptFileName =
        "20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql";
    private const string OwnerDowngradeScriptFileName =
        "20260815102612_T8CorrectivePostgresHardening.owner-downgrade.sql";
    private const string SentinelTable = "t8_non_allowlisted_sentinel";
    private const string AlternateGrantorRole = "tabruk_alternate_grantor_probe";
    private const string HostileOwnerRole = "tabruk_hostile_owner_probe";
    private const string HostileDescendantTable = "users_hostile_descendant_probe";
    private const string HostileAncestorTable = "users_hostile_ancestor_probe";
    private const string HostilePartitionParentTable = "users_hostile_partition_parent_probe";
    private const string HostileHistoryAncestorTable = "history_hostile_ancestor_probe";
    private const string HostileHistoryPartitionParentTable = "history_hostile_partition_parent_probe";
    private const string InheritedPrivilegeProbeRole = "tabruk_app_inherited_privilege_probe";
    private const string InheritedPrivilegeBridgeRole = "tabruk_app_inherited_privilege_bridge_probe";
    private const long PostConcurrentBoundaryAdvisoryLock = 84150815102613;
    private const long CompensationFailureBoundaryAdvisoryLock = 84150815102614;
    private const long ExternalIndexOwnerRaceAdvisoryLock = 84150815102615;
    private const long PostIndexCaptureRaceAdvisoryLock = 84150815102616;
    private const long PostCompensationVerifyRaceAdvisoryLock = 84150815102617;
    private const long PostConstraintCaptureRaceAdvisoryLock = 84150815102618;
    private const long GlobalDefaultAclAdvisoryLock = 84150815102619;

    private static readonly string[] CorrectivePrivilegeSurfaces =
    [
        "factory",
        "up",
        "down",
        "owner",
    ];

    private static readonly string[] ManagedTopologyProbes =
    [
        "descendant",
        "ancestor",
        "partition",
    ];

    private static readonly string[] HistoryTopologyProbes =
    [
        "inheritance",
        "partition",
    ];

    private static readonly string[] ConcurrentCorrectiveSurfaces =
    [
        "owner",
    ];

    private static readonly string[] AlternateGrantorPublicColumnAclProbes =
    [
        "crud-select",
        "audit-insert",
    ];

    private static readonly string[] AlternateGrantorGrantOptionProbes =
    [
        "crud-table-select",
        "audit-column-insert",
        "sequence-usage",
        "audit-table-delete",
        "history-table-maintain",
    ];

    private static readonly string[] PostgreSql18TablePrivileges =
    [
        "SELECT",
        "INSERT",
        "UPDATE",
        "DELETE",
        "TRUNCATE",
        "REFERENCES",
        "TRIGGER",
        "MAINTAIN",
    ];

    private static readonly string[] CrudTablePrivileges =
    [
        "SELECT",
        "INSERT",
        "UPDATE",
        "DELETE",
    ];

    private static readonly string[] CrudDeniedTablePrivileges =
    [
        "TRUNCATE",
        "REFERENCES",
        "TRIGGER",
        "MAINTAIN",
    ];

    private static readonly string[] InsertOnlyDeniedTablePrivileges =
    [
        "SELECT",
        "UPDATE",
        "DELETE",
        "TRUNCATE",
        "REFERENCES",
        "TRIGGER",
        "MAINTAIN",
    ];

    private static readonly (string Sql, string Category)[] HostileIndexSemanticDefinitions =
    [
        (
            """
            CREATE INDEX "ux_signups_waitlisted_order_per_help_need"
                ON "signups" USING hash ("organization_id")
                WHERE "status" = 2
            """,
            "identity"
        ),
        (
            """
            CREATE UNIQUE INDEX "ux_signups_waitlisted_order_per_help_need"
                ON "signups" ("organization_id" DESC, "help_need_id", "waitlist_order")
                WHERE "status" = 2
            """,
            "key-options"
        ),
        (
            """
            CREATE UNIQUE INDEX "ux_signups_waitlisted_order_per_help_need"
                ON "signups" ("organization_id", "help_need_id", "waitlist_order")
                WITH (fillfactor = 70)
                WHERE "status" = 2
            """,
            "relation-options"
        ),
        (
            """
            CREATE UNIQUE INDEX "ux_signups_waitlisted_order_per_help_need"
                ON "signups" ("help_need_id", "organization_id", "waitlist_order")
                WHERE "status" = 2
            """,
            "key-order"
        ),
        (
            """
            CREATE UNIQUE INDEX "ux_signups_waitlisted_order_per_help_need"
                ON "signups" ("organization_id", "help_need_id", (waitlist_order + 0))
                WHERE "status" = 2
            """,
            "key-count"
        ),
        (
            """
            CREATE UNIQUE INDEX "ux_signups_waitlisted_order_per_help_need"
                ON "signups" ("organization_id", "help_need_id", "waitlist_order")
                NULLS NOT DISTINCT
                WHERE "status" = 2
            """,
            "flags"
        ),
        (
            """
            CREATE UNIQUE INDEX "ux_signups_waitlisted_order_per_help_need"
                ON "signups" ("organization_id", "help_need_id", "waitlist_order")
                WHERE "status" = 2
                  AND "waitlist_order" IS NOT NULL
            """,
            "predicate"
        ),
    ];

    private static readonly (string Sql, string Category)[] HostileConstraintSemanticDefinitions =
    [
        (
            """
            ALTER TABLE "signups"
                ADD CONSTRAINT "ck_signups_transition_chronology"
                UNIQUE ("id")
            """,
            "type"
        ),
        (
            """
            ALTER TABLE "signups"
                ADD CONSTRAINT "ck_signups_transition_chronology"
                CHECK ("last_transition_at" IS NULL OR "last_transition_at" >= "submitted_at")
                NO INHERIT
            """,
            "type"
        ),
        (
            """
            ALTER TABLE "signups"
                ADD CONSTRAINT "ck_signups_transition_chronology"
                CHECK (
                    "last_transition_at" IS NULL
                    OR "last_transition_at" > "submitted_at")
            """,
            "expression"
        ),
        (
            """
            ALTER TABLE "signups"
                ADD CONSTRAINT "ck_signups_transition_chronology"
                CHECK ("last_transition_at" IS NULL OR "last_transition_at" >= "submitted_at")
                NOT ENFORCED
            """,
            "type"
        ),
    ];

    private static readonly (string Name, string Sql)[] MalformedHistoryBootstrapDefinitions =
    [
        (
            "missing-primary-key",
            """
            CREATE TABLE "__EFMigrationsHistory"
            (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL
            )
            """
        ),
        (
            "wrong-migration-id-type",
            """
            CREATE TABLE "__EFMigrationsHistory"
            (
                "MigrationId" text NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
            )
            """
        ),
        (
            "wrong-product-version-length",
            """
            CREATE TABLE "__EFMigrationsHistory"
            (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(31) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
            )
            """
        ),
        (
            "nullable-product-version",
            """
            CREATE TABLE "__EFMigrationsHistory"
            (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32),
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
            )
            """
        ),
        (
            "extra-column",
            """
            CREATE TABLE "__EFMigrationsHistory"
            (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                "Unexpected" integer,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
            )
            """
        ),
        (
            "wrong-primary-key-column",
            """
            CREATE TABLE "__EFMigrationsHistory"
            (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("ProductVersion")
            )
            """
        ),
        (
            "wrong-primary-key-order",
            """
            CREATE TABLE "__EFMigrationsHistory"
            (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory"
                    PRIMARY KEY ("ProductVersion", "MigrationId")
            )
            """
        ),
        (
            "view",
            """
            CREATE VIEW "__EFMigrationsHistory" AS
            SELECT
                ''::character varying(150) AS "MigrationId",
                ''::character varying(32) AS "ProductVersion"
            WHERE FALSE
            """
        ),
        (
            "partitioned-table",
            """
            CREATE TABLE "__EFMigrationsHistory"
            (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
            )
            PARTITION BY HASH ("MigrationId")
            """
        ),
        (
            "trigger",
            """
            CREATE TABLE "__EFMigrationsHistory"
            (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
            );
            CREATE FUNCTION history_bootstrap_trigger() RETURNS trigger
            LANGUAGE plpgsql AS $body$
            BEGIN
                RETURN NEW;
            END
            $body$;
            CREATE TRIGGER history_bootstrap_trigger
            BEFORE INSERT ON "__EFMigrationsHistory"
            FOR EACH ROW EXECUTE FUNCTION history_bootstrap_trigger()
            """
        ),
        (
            "row-level-security",
            """
            CREATE TABLE "__EFMigrationsHistory"
            (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
            );
            ALTER TABLE "__EFMigrationsHistory" ENABLE ROW LEVEL SECURITY
            """
        ),
        (
            "rule",
            """
            CREATE TABLE "__EFMigrationsHistory"
            (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
            );
            CREATE RULE history_bootstrap_rule AS
            ON INSERT TO "__EFMigrationsHistory" DO ALSO NOTHING
            """
        ),
        (
            "primary-key-index-options",
            """
            CREATE TABLE "__EFMigrationsHistory"
            (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory"
                    PRIMARY KEY ("MigrationId") WITH (fillfactor = 70)
            )
            """
        ),
        (
            "wrong-owner",
            """
            CREATE TABLE "__EFMigrationsHistory"
            (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
            );
            ALTER TABLE "__EFMigrationsHistory" OWNER TO "tabruk_app"
            """
        ),
    ];

    public static IEnumerable<object[]> HostileIndexSemanticUpCases =>
        HostileIndexSemanticDefinitions.Select(
            definition => new object[] { definition.Sql, definition.Category });

    public static IEnumerable<object[]> HostileIndexSemanticRecoveryCases =>
        HostileIndexSemanticDefinitions.SelectMany(
            definition => new[]
            {
                new object[] { definition.Sql, definition.Category, "down" },
                new object[] { definition.Sql, definition.Category, "owner" },
            });

    public static IEnumerable<object[]> HostileConstraintSemanticUpCases =>
        HostileConstraintSemanticDefinitions.Select(
            definition => new object[] { definition.Sql, definition.Category });

    public static IEnumerable<object[]> HostileConstraintSemanticRecoveryCases =>
        HostileConstraintSemanticDefinitions.SelectMany(
            definition => new[]
            {
                new object[] { definition.Sql, definition.Category, "down" },
                new object[] { definition.Sql, definition.Category, "owner" },
            });

    public static IEnumerable<object[]> AlternateGrantorPublicColumnAclCases =>
        CorrectivePrivilegeSurfaces.SelectMany(
            surface => AlternateGrantorPublicColumnAclProbes.Select(
                probe => new object[] { surface, probe }));

    public static IEnumerable<object[]> AlternateGrantorGrantOptionCases =>
        CorrectivePrivilegeSurfaces.SelectMany(
            surface => AlternateGrantorGrantOptionProbes.Select(
                probe => new object[] { surface, probe }));

    public static IEnumerable<object[]> MalformedHistoryBootstrapCases =>
        MalformedHistoryBootstrapDefinitions.Select(
            definition => new object[] { definition.Name, definition.Sql });

    public static IEnumerable<object[]> HostileManagedDescendantCases =>
        CorrectivePrivilegeSurfaces.Select(surface => new object[] { surface });

    public static IEnumerable<object[]> ConcurrentManagedTopologyCases =>
        CorrectivePrivilegeSurfaces.SelectMany(
            surface => ManagedTopologyProbes.Select(
                probe => new object[] { surface, probe }));

    public static IEnumerable<object[]> ConcurrentHistoryTopologyCases =>
        ConcurrentCorrectiveSurfaces.SelectMany(
            surface => HistoryTopologyProbes.Select(
                probe => new object[] { surface, probe }));

    public static IEnumerable<object[]> FactoryHistoryTopologyCases =>
        HistoryTopologyProbes.Select(probe => new object[] { probe });

    [Theory]
    [InlineData("public")]
    [InlineData("tabruk_migrations")]
    [InlineData("_tabruk$1")]
    public void MigrationFactoryAcceptsOneExplicitMatchingSchema(string schema)
    {
        string connectionString = BuildFactoryConnectionString(
            schema,
            $"-c statement_timeout=30000 -c tabruk.target_schema={schema}");

        using TabrukDbContext context = TabrukDbContextOptions.Create(connectionString);

        string historyCreateSql = context.GetService<IHistoryRepository>()
            .GetCreateScript()
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(
            $"""
            CREATE TABLE {schema}."__EFMigrationsHistory" (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
            );
            """,
            historyCreateSql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationFactoryRejectsMissingOrRepeatedSchemaSettings()
    {
        InvalidOperationException missingSearchPath = Assert.Throws<InvalidOperationException>(
            () => TabrukDbContextOptions.Create(
                BuildFactoryConnectionString(
                    searchPath: null,
                    options: "-c tabruk.target_schema=public")));
        Assert.StartsWith(
            "TABRUK_MIGRATIONS_CONNECTION requires exactly one Search Path schema.",
            missingSearchPath.Message,
            StringComparison.Ordinal);

        string repeatedSearchPath =
            $"{BuildFactoryConnectionString("public", "-c tabruk.target_schema=public")};Search Path=public";
        InvalidOperationException repeatedSearchPathException = Assert.Throws<InvalidOperationException>(
            () => TabrukDbContextOptions.Create(repeatedSearchPath));
        Assert.StartsWith(
            "TABRUK_MIGRATIONS_CONNECTION requires exactly one Search Path schema.",
            repeatedSearchPathException.Message,
            StringComparison.Ordinal);

        InvalidOperationException missingTarget = Assert.Throws<InvalidOperationException>(
            () => TabrukDbContextOptions.Create(
                BuildFactoryConnectionString("public", options: null)));
        Assert.StartsWith(
            "TABRUK_MIGRATIONS_CONNECTION requires exactly one tabruk.target_schema option.",
            missingTarget.Message,
            StringComparison.Ordinal);

        InvalidOperationException repeatedTarget = Assert.Throws<InvalidOperationException>(
            () => TabrukDbContextOptions.Create(
                BuildFactoryConnectionString(
                    "public",
                    "-c tabruk.target_schema=public -c tabruk.target_schema=public")));
        Assert.StartsWith(
            "TABRUK_MIGRATIONS_CONNECTION requires exactly one tabruk.target_schema option.",
            repeatedTarget.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public,tabruk")]
    [InlineData("\"public\"")]
    [InlineData("Public")]
    [InlineData("tabruk-migrations")]
    [InlineData("$user")]
    [InlineData("pg_catalog")]
    [InlineData("information_schema")]
    [InlineData("pg_custom")]
    public void MigrationFactoryRejectsInvalidSchemaIdentifiers(string schema)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => TabrukDbContextOptions.Create(
                BuildFactoryConnectionString(
                    schema,
                    $"-c tabruk.target_schema={schema}")));

        Assert.StartsWith(
            "TABRUK_MIGRATIONS_CONNECTION schema must be one unquoted lowercase PostgreSQL identifier.",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationFactoryRejectsMismatchedSchemaSettings()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => TabrukDbContextOptions.Create(
                BuildFactoryConnectionString(
                    "public",
                    "-c tabruk.target_schema=tabruk_migrations")));

        Assert.StartsWith(
            "TABRUK_MIGRATIONS_CONNECTION Search Path and tabruk.target_schema must match.",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationFactoryRejectsMissingFalseOrRepeatedDisposableEfGuard()
    {
        string missing = BuildFactoryConnectionString(
            "public",
            "-c tabruk.target_schema=public")
            .Replace(
                " -c tabruk.disposable_ef=on",
                string.Empty,
                StringComparison.Ordinal);
        InvalidOperationException missingException = Assert.Throws<InvalidOperationException>(
            () => TabrukDbContextOptions.Create(missing));
        Assert.StartsWith(
            "TABRUK_MIGRATIONS_CONNECTION requires exactly one -c tabruk.disposable_ef=on option",
            missingException.Message,
            StringComparison.Ordinal);

        InvalidOperationException falseException = Assert.Throws<InvalidOperationException>(
            () => TabrukDbContextOptions.Create(
                BuildFactoryConnectionString(
                    "public",
                    "-c tabruk.target_schema=public -c tabruk.disposable_ef=off")));
        Assert.StartsWith(
            "TABRUK_MIGRATIONS_CONNECTION requires exactly one -c tabruk.disposable_ef=on option",
            falseException.Message,
            StringComparison.Ordinal);

        InvalidOperationException repeatedException = Assert.Throws<InvalidOperationException>(
            () => TabrukDbContextOptions.Create(
                BuildFactoryConnectionString(
                    "public",
                    "-c tabruk.target_schema=public -c tabruk.disposable_ef=on -c tabruk.disposable_ef=on")));
        Assert.StartsWith(
            "TABRUK_MIGRATIONS_CONNECTION requires exactly one -c tabruk.disposable_ef=on option",
            repeatedException.Message,
            StringComparison.Ordinal);
    }

    [RequiresPostgresFact]
    public async Task InitialMigrationAppliesAndRollsBackAnIsolatedSchema()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();

        await context.Database.MigrateAsync();
        Assert.Contains(
            await context.Database.GetAppliedMigrationsAsync(),
            migration => migration.Contains("InitialPostgresSchema", StringComparison.Ordinal));

        await context.Database.MigrateAsync("0");

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command =
            new("SELECT to_regclass('organizations') IS NULL", connection);
        Assert.True((bool)(await command.ExecuteScalarAsync())!);
    }

    [RequiresPostgresFact]
    public async Task CorrectiveMigrationUpgradeDownAndReapplyPreservesInitialDataAndPrivilegePosture()
    {
        string psqlPath = ResolvePsqlExecutablePath();
        string downgradePath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "HusayniaTabruk.Infrastructure",
            "Migrations",
            OwnerDowngradeScriptFileName);
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(InitialMigration);
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM (
                    SELECT organization_id, help_need_id, waitlist_order
                    FROM signups
                    WHERE status = 2
                    GROUP BY organization_id, help_need_id, waitlist_order
                    HAVING count(*) > 1
                ) AS duplicate_waitlist_positions
                """));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM signups
                WHERE last_transition_at IS NOT NULL
                  AND last_transition_at < submitted_at
                """));

        await SeedHostilePublicPrivilegesAsync(connection);
        await context.Database.MigrateAsync();
        Assert.Equal(
            2L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" """));
        await ExecuteAsync(
            connection,
            $"""
            CREATE TABLE {SentinelTable}
            (
                id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                value text NOT NULL
            )
            """);
        await AssertCorrectiveCatalogAsync(connection);
        await AssertHardenedPrivilegeMatrixAsync(connection, SentinelTable);
        await AssertApplicationRoleMutationDeniedAsync(
            database,
            """UPDATE "__EFMigrationsHistory" SET "ProductVersion" = "ProductVersion" WHERE FALSE""");

        PsqlInvocationResult downgrade = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            downgradePath,
            database.Schema);
        Assert.Equal(0, downgrade.ExitCode);
        Assert.Single(await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connection,
                "SELECT COUNT(*) FROM organizations WHERE id = @organizationId",
                ("organizationId", seed.OrganizationId.Value)));
        await AssertCorrectiveCatalogAsync(connection);
        await AssertHardenedPrivilegeMatrixAsync(connection, SentinelTable);
        await AssertApplicationRoleMutationDeniedAsync(
            database,
            """UPDATE "__EFMigrationsHistory" SET "ProductVersion" = "ProductVersion" WHERE FALSE""");

        await SeedHostilePublicPrivilegesAsync(connection);
        await context.Database.MigrateAsync();
        Assert.Equal(2, (await context.Database.GetAppliedMigrationsAsync()).Count());
        await AssertCorrectiveCatalogAsync(connection);
        await AssertHardenedPrivilegeMatrixAsync(connection, SentinelTable);
    }

    [RequiresPostgresFact]
    public async Task CorrectiveMigrationRejectsDuplicatePreexistingWaitlistPositionsAtomically()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(InitialMigration);
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();

        await InsertWaitlistedSignupAsync(
            connection,
            seed,
            seed.MemberMembershipId.Value,
            waitlistOrder: 1);
        await InsertWaitlistedSignupAsync(
            connection,
            seed,
            seed.ManagerMembershipId.Value,
            waitlistOrder: 1);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => context.Database.MigrateAsync());

        Assert.True(exception.SqlState == "23505", exception.ToString());
        Assert.Equal("ux_signups_waitlisted_order_per_help_need", exception.ConstraintName);
        Assert.Single(await context.Database.GetAppliedMigrationsAsync());
        await AssertCorrectiveObjectsAbsentAsync(connection);
        await AssertHardenedPrivilegeMatrixAsync(connection);

        await ExecuteAsync(
            connection,
            """
            UPDATE signups
            SET waitlist_order = 2
            WHERE organization_id = @organizationId
              AND help_need_id = @helpNeedId
              AND primary_membership_id = @membershipId
            """,
            ("organizationId", seed.OrganizationId.Value),
            ("helpNeedId", seed.HelpNeedId.Value),
            ("membershipId", seed.ManagerMembershipId.Value));

        await using TabrukDbContext retryContext = database.CreateMigrationContext();
        await retryContext.Database.MigrateAsync();
        Assert.Equal(2, (await retryContext.Database.GetAppliedMigrationsAsync()).Count());
        await AssertCorrectiveCatalogAsync(connection);
        await AssertHardenedPrivilegeMatrixAsync(connection);
    }

    [RequiresPostgresFact]
    public async Task CorrectiveMigrationRejectsPreexistingInvalidChronologyAtomically()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(InitialMigration);
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        Guid invalidSignupId = Guid.CreateVersion7();
        await ExecuteAsync(
            connection,
            """
            INSERT INTO signups
                (id, organization_id, service_date_id, help_need_id, primary_membership_id, kind,
                 unnamed_participant_count, status, submitted_at, last_transition_at, version)
            VALUES
                (@id, @organizationId, @serviceDateId, @helpNeedId, @membershipId, 0, 0, 3,
                 @submittedAt, @lastTransitionAt, 1)
            """,
            ("id", invalidSignupId),
            ("organizationId", seed.OrganizationId.Value),
            ("serviceDateId", seed.ServiceDateId.Value),
            ("helpNeedId", seed.HelpNeedId.Value),
            ("membershipId", seed.MemberMembershipId.Value),
            ("submittedAt", seed.Now),
            ("lastTransitionAt", seed.Now.AddTicks(-1)));

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => context.Database.MigrateAsync());

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal("ck_signups_transition_chronology", exception.ConstraintName);
        Assert.Single(await context.Database.GetAppliedMigrationsAsync());
        await AssertHardenedPrivilegeMatrixAsync(connection);
        await AssertWaitlistIndexStateAsync(connection, expectedPresent: true, expectedValid: true);
        await AssertChronologyConstraintStateAsync(
            connection,
            expectedPresent: true,
            expectedValidated: false);

        await ExecuteAsync(
            connection,
            """
            UPDATE signups
            SET last_transition_at = submitted_at
            WHERE id = @id
            """,
            ("id", invalidSignupId));

        await using TabrukDbContext retryContext = database.CreateMigrationContext();
        await retryContext.Database.MigrateAsync();
        Assert.Equal(2, (await retryContext.Database.GetAppliedMigrationsAsync()).Count());
        await AssertCorrectiveCatalogAsync(connection);
        await AssertHardenedPrivilegeMatrixAsync(connection);
    }

    [RequiresPostgresFact]
    public async Task CorrectiveMigrationRejectsHostileSameNameIndexBeforeHistoryAndPreservesIt()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(InitialMigration);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            CREATE INDEX "ux_signups_waitlisted_order_per_help_need"
                ON "signups" ("organization_id", "help_need_id", "waitlist_order")
                WHERE "status" = 2
            """);
        await SeedHostilePublicPrivilegesAsync(connection);
        string hostileDefinition = await GetWaitlistIndexDefinitionAsync(connection);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => context.Database.MigrateAsync());

        Assert.Equal("P0001", exception.SqlState);
        Assert.StartsWith(
            "T8 object definition mismatch: object=ux_signups_waitlisted_order_per_help_need; category=state.",
            exception.MessageText,
            StringComparison.Ordinal);
        Assert.Single(await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(hostileDefinition, await GetWaitlistIndexDefinitionAsync(connection));
        await AssertChronologyConstraintStateAsync(
            connection,
            expectedPresent: false,
            expectedValidated: false);
        await AssertHardenedPrivilegeMatrixAsync(connection);
    }

    [RequiresPostgresFact]
    public async Task CorrectiveMigrationRejectsHostileSameNameConstraintBeforeHistoryAndPreservesIt()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(InitialMigration);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            ALTER TABLE "signups"
                ADD CONSTRAINT "ck_signups_transition_chronology"
                CHECK ("last_transition_at" IS NULL OR TRUE)
            """);
        await SeedHostilePublicPrivilegesAsync(connection);
        string hostileDefinition = await GetChronologyConstraintDefinitionAsync(connection);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => context.Database.MigrateAsync());

        Assert.Equal("P0001", exception.SqlState);
        Assert.StartsWith(
            "T8 object definition mismatch: object=ck_signups_transition_chronology; category=expression.",
            exception.MessageText,
            StringComparison.Ordinal);
        Assert.Single(await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(hostileDefinition, await GetChronologyConstraintDefinitionAsync(connection));
        await AssertWaitlistIndexStateAsync(
            connection,
            expectedPresent: false,
            expectedValid: false);
        await AssertHardenedPrivilegeMatrixAsync(connection);
    }

    [RequiresPostgresFact]
    public async Task CorrectiveMigrationRejectsInterruptedInvalidIndexAndPreservesIt()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(InitialMigration);
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await InsertWaitlistedSignupAsync(
            connection,
            seed,
            seed.MemberMembershipId.Value,
            waitlistOrder: 1);
        await InsertWaitlistedSignupAsync(
            connection,
            seed,
            seed.ManagerMembershipId.Value,
            waitlistOrder: 1);

        PostgresException createFailure = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(
                connection,
                """
                CREATE UNIQUE INDEX CONCURRENTLY "ux_signups_waitlisted_order_per_help_need"
                    ON "signups" ("organization_id", "help_need_id", "waitlist_order")
                    WHERE "status" = 2
                """));
        Assert.Equal("23505", createFailure.SqlState);
        await ExecuteAsync(
            connection,
            """
            UPDATE "signups"
            SET "waitlist_order" = 2
            WHERE "organization_id" = @organizationId
              AND "help_need_id" = @helpNeedId
              AND "primary_membership_id" = @membershipId
            """,
            ("organizationId", seed.OrganizationId.Value),
            ("helpNeedId", seed.HelpNeedId.Value),
            ("membershipId", seed.ManagerMembershipId.Value));
        string invalidDefinition = await GetWaitlistIndexDefinitionAsync(connection);
        await AssertWaitlistIndexStateAsync(connection, expectedPresent: true, expectedValid: false);

        PostgresException migrationFailure = await Assert.ThrowsAsync<PostgresException>(
            () => context.Database.MigrateAsync());

        Assert.Equal("P0001", migrationFailure.SqlState);
        Assert.StartsWith(
            "T8 object definition mismatch: object=ux_signups_waitlisted_order_per_help_need; category=state.",
            migrationFailure.MessageText,
            StringComparison.Ordinal);
        Assert.Single(await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(invalidDefinition, await GetWaitlistIndexDefinitionAsync(connection));
        await AssertWaitlistIndexStateAsync(connection, expectedPresent: true, expectedValid: false);
    }

    [RequiresPostgresFact]
    public async Task CorrectiveMigrationReusesCorrectExistingObjectsAndValidatesRetryableConstraint()
    {
        await using PostgresTestDatabase validatedDatabase = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext validatedContext = validatedDatabase.CreateContext();
        await validatedContext.Database.MigrateAsync(InitialMigration);
        await using NpgsqlConnection validatedConnection = new(validatedDatabase.ConnectionString);
        await validatedConnection.OpenAsync();
        await CreateCorrectiveObjectsAsync(validatedConnection, validateConstraint: true);
        uint validatedIndexOid = await GetWaitlistIndexOidAsync(validatedConnection);
        uint validatedConstraintOid = await GetChronologyConstraintOidAsync(validatedConnection);

        await validatedContext.Database.MigrateAsync();

        Assert.Equal(2, (await validatedContext.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(validatedIndexOid, await GetWaitlistIndexOidAsync(validatedConnection));
        Assert.Equal(validatedConstraintOid, await GetChronologyConstraintOidAsync(validatedConnection));
        await AssertCorrectiveCatalogAsync(validatedConnection);

        await using PostgresTestDatabase retryDatabase = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext retryContext = retryDatabase.CreateContext();
        await retryContext.Database.MigrateAsync(InitialMigration);
        await using NpgsqlConnection retryConnection = new(retryDatabase.ConnectionString);
        await retryConnection.OpenAsync();
        await CreateCorrectiveObjectsAsync(retryConnection, validateConstraint: false);
        uint retryConstraintOid = await GetChronologyConstraintOidAsync(retryConnection);
        await AssertChronologyConstraintStateAsync(
            retryConnection,
            expectedPresent: true,
            expectedValidated: false);

        await retryContext.Database.MigrateAsync();

        Assert.Equal(2, (await retryContext.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(retryConstraintOid, await GetChronologyConstraintOidAsync(retryConnection));
        await AssertCorrectiveCatalogAsync(retryConnection);
    }

    [RequiresPostgresFact]
    public async Task ForgedCorrectiveHistoryFailsBeforeTrustAndCommittedHardeningSurvives()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using (TabrukDbContext initialContext = database.CreateMigrationContext())
        {
            await initialContext.Database.MigrateAsync(InitialMigration);
        }

        await ExecuteCommittedAsApplicationRoleAsync(
            database,
            """
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('20260815102612_T8CorrectivePostgresHardening', '10.0.11')
            """);

        await using TabrukDbContext forgedContext = database.CreateMigrationContext();
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => forgedContext.Database.MigrateAsync());

        Assert.Equal("P0001", exception.SqlState);
        Assert.StartsWith(
            "T8 pre-history attestation failed: corrective-history-object-mismatch.",
            exception.MessageText,
            StringComparison.Ordinal);

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
        await AssertCorrectiveObjectsAbsentAsync(connection);
        await AssertHardenedPrivilegeMatrixAsync(connection);
        await AssertApplicationRoleMutationDeniedAsync(
            database,
            """UPDATE "__EFMigrationsHistory" SET "ProductVersion" = "ProductVersion" WHERE FALSE""");
    }

    [RequiresPostgresFact]
    public async Task PreHistoryGateAllowsExactEmptyEfBootstrapState()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using (NpgsqlConnection connection = new(database.ConnectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(
                connection,
                """
                CREATE TABLE "__EFMigrationsHistory"
                (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
                )
                """);
        }

        await using TabrukDbContext context = database.CreateMigrationContext();
        Assert.Empty(await context.Database.GetAppliedMigrationsAsync());
        await context.Database.MigrateAsync();
        Assert.Equal(2, (await context.Database.GetAppliedMigrationsAsync()).Count());
    }

    [RequiresPostgresTheory]
    [MemberData(nameof(MalformedHistoryBootstrapCases))]
    public async Task PreHistoryGateRejectsMalformedEmptyEfBootstrapAndPreservesIt(
        string variant,
        string historyDefinitionSql)
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            $"""
            {historyDefinitionSql};
            GRANT ALL PRIVILEGES ON TABLE "__EFMigrationsHistory" TO "tabruk_app";
            GRANT ALL PRIVILEGES ON TABLE "__EFMigrationsHistory" TO PUBLIC
            """);
        uint originalOid = await ScalarAsync<uint>(
            connection,
            """SELECT '"__EFMigrationsHistory"'::regclass::oid""");
        string originalStructure = await GetHistoryStructureFingerprintAsync(connection);

        await using TabrukDbContext context = database.CreateMigrationContext();
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => context.Database.MigrateAsync());

        Assert.Equal("P0001", exception.SqlState);
        Assert.StartsWith(
            "T8 pre-history attestation failed: history-structure-mismatch.",
            exception.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(
            originalOid,
            await ScalarAsync<uint>(
                connection,
                """SELECT '"__EFMigrationsHistory"'::regclass::oid"""));
        Assert.Equal(originalStructure, await GetHistoryStructureFingerprintAsync(connection));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" """));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """
                SELECT count(*)
                FROM pg_class AS relation
                JOIN pg_namespace AS schema_definition
                  ON schema_definition.oid = relation.relnamespace
                CROSS JOIN LATERAL aclexplode(
                    COALESCE(relation.relacl, acldefault('r', relation.relowner)))
                    AS privilege
                WHERE schema_definition.nspname = current_schema()
                  AND relation.relname = '__EFMigrationsHistory'
                  AND (
                      privilege.grantee = 0
                      OR (
                          privilege.grantee =
                              (SELECT oid FROM pg_roles WHERE rolname = 'tabruk_app')
                          AND privilege.grantee <> relation.relowner))
                """));
        Assert.False(string.IsNullOrWhiteSpace(variant));
    }

    [RequiresPostgresFact]
    public async Task EmptyEfBootstrapCommitsHistoryRevocationAgainstPreOpenedAttacker()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using (NpgsqlConnection setupConnection = new(database.ConnectionString))
        {
            await setupConnection.OpenAsync();
            await ExecuteAsync(
                setupConnection,
                """
                CREATE TABLE "__EFMigrationsHistory"
                (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
                );

                DO $tabruk$
                BEGIN
                    EXECUTE format(
                        'GRANT USAGE ON SCHEMA %I TO "tabruk_app"',
                        current_schema());
                END
                $tabruk$;

                GRANT ALL PRIVILEGES ON TABLE "__EFMigrationsHistory" TO "tabruk_app";
                GRANT ALL PRIVILEGES ON TABLE "__EFMigrationsHistory" TO PUBLIC;
                """);
        }

        await using NpgsqlConnection attackerConnection = new(database.ConnectionString);
        await attackerConnection.OpenAsync();
        await using NpgsqlTransaction attackerTransaction =
            await attackerConnection.BeginTransactionAsync();
        await using (NpgsqlCommand setRole = new(
                         """SET LOCAL ROLE "tabruk_app" """,
                         attackerConnection,
                         attackerTransaction))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.OpenConnectionAsync();

        await using NpgsqlCommand insertHistory = new(
            """
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('20260815102612_T8CorrectivePostgresHardening', '10.0.11')
            """,
            attackerConnection,
            attackerTransaction);
        PostgresException insertDenied = await Assert.ThrowsAsync<PostgresException>(
            () => insertHistory.ExecuteNonQueryAsync());
        Assert.Equal("42501", insertDenied.SqlState);
        await attackerTransaction.RollbackAsync();

        await using (NpgsqlConnection verificationConnection = new(database.ConnectionString))
        {
            await verificationConnection.OpenAsync();
            Assert.Equal(
                0L,
                await ScalarAsync<long>(
                    verificationConnection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" """));
        }

        await context.Database.MigrateAsync();
        Assert.Equal(2, (await context.Database.GetAppliedMigrationsAsync()).Count());

        await using NpgsqlTransaction postMigrationTransaction =
            await attackerConnection.BeginTransactionAsync();
        await using (NpgsqlCommand setRole = new(
                         """SET LOCAL ROLE "tabruk_app" """,
                         attackerConnection,
                         postMigrationTransaction))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        await using NpgsqlCommand updateHistory = new(
            """
            UPDATE "__EFMigrationsHistory"
            SET "ProductVersion" = "ProductVersion"
            WHERE FALSE
            """,
            attackerConnection,
            postMigrationTransaction);
        PostgresException updateDenied = await Assert.ThrowsAsync<PostgresException>(
            () => updateHistory.ExecuteNonQueryAsync());
        Assert.Equal("42501", updateDenied.SqlState);
        await postMigrationTransaction.RollbackAsync();
    }

    [RequiresPostgresFact]
    public async Task EmptyEfBootstrapWaitsForPreRevocationWriterAndRejectsCommittedForgedHistory()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using NpgsqlConnection setupConnection = new(database.ConnectionString);
        await setupConnection.OpenAsync();
        await ExecuteAsync(
            setupConnection,
            """
            CREATE TABLE "__EFMigrationsHistory"
            (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
            );

            DO $tabruk$
            BEGIN
                EXECUTE format(
                    'GRANT USAGE ON SCHEMA %I TO "tabruk_app"',
                    current_schema());
            END
            $tabruk$;

            GRANT ALL PRIVILEGES ON TABLE "__EFMigrationsHistory" TO "tabruk_app";
            GRANT ALL PRIVILEGES ON TABLE "__EFMigrationsHistory" TO PUBLIC;
            """);

        await using NpgsqlConnection attackerConnection = new(database.ConnectionString);
        await attackerConnection.OpenAsync();
        await using NpgsqlTransaction attackerTransaction =
            await attackerConnection.BeginTransactionAsync();
        await using (NpgsqlCommand setRole = new(
                         """SET LOCAL ROLE "tabruk_app" """,
                         attackerConnection,
                         attackerTransaction))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand insertHistory = new(
                         """
                         INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                         VALUES ('20260815102612_T8CorrectivePostgresHardening', '10.0.11')
                         """,
                         attackerConnection,
                         attackerTransaction))
        {
            Assert.Equal(1, await insertHistory.ExecuteNonQueryAsync());
        }

        string gateApplicationName = $"t8-history-hardening-{Guid.NewGuid():N}";
        NpgsqlConnectionStringBuilder gateConnectionString = new(database.ConnectionString)
        {
            ApplicationName = gateApplicationName,
            CommandTimeout = 10,
            Options = $"-c tabruk.target_schema={database.Schema} -c tabruk.disposable_ef=on -c lock_timeout=8s",
        };
        await using TabrukDbContext context =
            TabrukDbContextOptions.Create(gateConnectionString.ConnectionString);
        Task<IEnumerable<string>> gateTask = context.Database.GetAppliedMigrationsAsync();

        await WaitForHistoryTableLockAsync(
            setupConnection,
            gateApplicationName,
            gateTask,
            TimeSpan.FromSeconds(5));
        Assert.False(gateTask.IsCompleted);

        await attackerTransaction.CommitAsync();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            async () => await gateTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("P0001", exception.SqlState);
        Assert.StartsWith(
            "T8 pre-history attestation failed: partial-initial-inventory.",
            exception.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                setupConnection,
                """
                SELECT COUNT(*)
                FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = '20260815102612_T8CorrectivePostgresHardening'
                  AND "ProductVersion" = '10.0.11'
                """));
        Assert.False(
            await ScalarAsync<bool>(
                setupConnection,
                """
                SELECT has_table_privilege(
                    'tabruk_app',
                    '"__EFMigrationsHistory"',
                    'SELECT,INSERT,UPDATE,DELETE')
                """));
        Assert.False(
            await ScalarAsync<bool>(
                setupConnection,
                """
                SELECT has_table_privilege(
                    'public',
                    '"__EFMigrationsHistory"',
                    'SELECT,INSERT,UPDATE,DELETE')
                """));
    }

    [RequiresPostgresFact]
    public async Task EmptyEfBootstrapRejectsInheritedHistoryColumnPrivilegeAfterCommittedRevokes()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ResetInheritedPrivilegeProbeRoleAsync(connection);
        try
        {
            await ExecuteAsync(
                connection,
                $"""
                CREATE TABLE "__EFMigrationsHistory"
                (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
                );

                DO $tabruk$
                BEGIN
                    EXECUTE format(
                        'GRANT USAGE ON SCHEMA %I TO "tabruk_app"',
                        current_schema());
                END
                $tabruk$;

                GRANT ALL PRIVILEGES ON TABLE "__EFMigrationsHistory" TO "tabruk_app";
                GRANT ALL PRIVILEGES ON TABLE "__EFMigrationsHistory" TO PUBLIC;
                CREATE ROLE "{InheritedPrivilegeProbeRole}" NOLOGIN;
                GRANT "{InheritedPrivilegeProbeRole}" TO "tabruk_app";
                GRANT INSERT ("MigrationId", "ProductVersion")
                    ON TABLE "__EFMigrationsHistory"
                    TO "{InheritedPrivilegeProbeRole}";
                """);

            await using TabrukDbContext context = database.CreateMigrationContext();
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                () => context.Database.OpenConnectionAsync());

            Assert.Equal("P0001", exception.SqlState);
            Assert.StartsWith(
                "T8 pre-history attestation failed: privilege-mismatch.",
                exception.MessageText,
                StringComparison.Ordinal);
            Assert.Equal(
                0L,
                await ScalarAsync<long>(
                    connection,
                    """
                    SELECT COUNT(*)
                    FROM pg_class AS relation
                    JOIN pg_namespace AS schema_definition
                      ON schema_definition.oid = relation.relnamespace
                    CROSS JOIN LATERAL aclexplode(
                        COALESCE(relation.relacl, acldefault('r', relation.relowner)))
                        AS privilege
                    WHERE schema_definition.nspname = current_schema()
                      AND relation.relname = '__EFMigrationsHistory'
                      AND privilege.grantee IN (
                          0,
                          (SELECT oid FROM pg_roles WHERE rolname = 'tabruk_app'))
                    """));
            Assert.False(
                await HasTablePrivilegeAsync(
                    connection,
                    PostgresLeastPrivilegeCatalog.MigrationHistoryTable,
                    "INSERT"));
            Assert.True(
                await HasColumnPrivilegeAsync(
                    connection,
                    PostgresLeastPrivilegeCatalog.MigrationHistoryTable,
                    "MigrationId",
                    "INSERT"));
            Assert.Equal(
                0L,
                await ScalarAsync<long>(
                    connection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" """));
        }
        finally
        {
            await ResetInheritedPrivilegeProbeRoleAsync(connection);
        }
    }

    [RequiresPostgresFact]
    public async Task PreOpenedInheritedHistoryColumnGrantCannotMakePostGateForgeryTrusted()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using (TabrukDbContext initialContext = database.CreateMigrationContext())
        {
            await initialContext.Database.MigrateAsync(InitialMigration);
        }

        await using NpgsqlConnection ownerConnection = new(database.ConnectionString);
        await ownerConnection.OpenAsync();
        await ResetInheritedPrivilegeProbeRoleAsync(ownerConnection);
        try
        {
            await ExecuteAsync(
                ownerConnection,
                $"""
                DO $tabruk$
                BEGIN
                    EXECUTE format(
                        'GRANT USAGE ON SCHEMA %I TO "tabruk_app"',
                        current_schema());
                END
                $tabruk$;

                CREATE ROLE "{InheritedPrivilegeProbeRole}" NOLOGIN;
                GRANT "{InheritedPrivilegeProbeRole}" TO "tabruk_app";
                GRANT INSERT ("MigrationId", "ProductVersion")
                    ON TABLE "__EFMigrationsHistory"
                    TO "{InheritedPrivilegeProbeRole}";
                """);

            await using NpgsqlConnection attackerConnection = new(database.ConnectionString);
            await attackerConnection.OpenAsync();
            await using NpgsqlTransaction attackerTransaction =
                await attackerConnection.BeginTransactionAsync();
            await using (NpgsqlCommand setRole = new(
                             """SET LOCAL ROLE "tabruk_app" """,
                             attackerConnection,
                             attackerTransaction))
            {
                await setRole.ExecuteNonQueryAsync();
            }

            await using (TabrukDbContext gatedContext = database.CreateMigrationContext())
            {
                PostgresException privilegeException = await Assert.ThrowsAsync<PostgresException>(
                    () => gatedContext.Database.GetAppliedMigrationsAsync());
                Assert.Equal("P0001", privilegeException.SqlState);
                Assert.StartsWith(
                    "T8 pre-history attestation failed: privilege-mismatch.",
                    privilegeException.MessageText,
                    StringComparison.Ordinal);
            }

            Assert.False(
                await ScalarAsync<bool>(
                    ownerConnection,
                    """
                    SELECT has_table_privilege(
                        'tabruk_app',
                        format('%I.%I', current_schema(), @table),
                        'INSERT WITH GRANT OPTION')
                    """,
                    ("table", PostgresLeastPrivilegeCatalog.MigrationHistoryTable)));
            Assert.True(
                await HasColumnPrivilegeAsync(
                    ownerConnection,
                    PostgresLeastPrivilegeCatalog.MigrationHistoryTable,
                    "MigrationId",
                    "INSERT"));

            await using (NpgsqlCommand forgeHistory = new(
                             """
                             INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                             VALUES ('20260815102612_T8CorrectivePostgresHardening', '10.0.11')
                             """,
                             attackerConnection,
                             attackerTransaction))
            {
                Assert.Equal(1, await forgeHistory.ExecuteNonQueryAsync());
            }

            await attackerTransaction.CommitAsync();
            await ResetInheritedPrivilegeProbeRoleAsync(ownerConnection);

            await using TabrukDbContext retryContext = database.CreateMigrationContext();
            PostgresException historyException = await Assert.ThrowsAsync<PostgresException>(
                () => retryContext.Database.GetAppliedMigrationsAsync());
            Assert.Equal("P0001", historyException.SqlState);
            Assert.StartsWith(
                "T8 pre-history attestation failed: corrective-history-object-mismatch.",
                historyException.MessageText,
                StringComparison.Ordinal);
            Assert.Equal(
                1L,
                await ScalarAsync<long>(
                    ownerConnection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));
            await AssertCorrectiveObjectsAbsentAsync(ownerConnection);
        }
        finally
        {
            await ResetInheritedPrivilegeProbeRoleAsync(ownerConnection);
        }
    }

    [RequiresPostgresTheory]
    [InlineData("crud-references")]
    [InlineData("audit-select")]
    [InlineData("non-allowlisted-update")]
    public async Task EffectiveColumnPrivilegeAttestationRejectsInheritedDeniedPrivilege(
        string probe)
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using (TabrukDbContext initialContext = database.CreateMigrationContext())
        {
            await initialContext.Database.MigrateAsync(InitialMigration);
        }

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ResetInheritedPrivilegeProbeRoleAsync(connection);
        try
        {
            await GrantInheritedColumnPrivilegeAsync(connection, probe);

            await using TabrukDbContext context = database.CreateMigrationContext();
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                () => context.Database.GetAppliedMigrationsAsync());

            Assert.Equal("P0001", exception.SqlState);
            Assert.StartsWith(
                "T8 pre-history attestation failed: privilege-mismatch.",
                exception.MessageText,
                StringComparison.Ordinal);
            Assert.Equal(
                1L,
                await ScalarAsync<long>(
                    connection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" """));
            await AssertCorrectiveObjectsAbsentAsync(connection);
        }
        finally
        {
            await ResetInheritedPrivilegeProbeRoleAsync(connection);
        }
    }

    [RequiresPostgresTheory]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("owner")]
    public async Task InheritedHistoryColumnPrivilegeFailsClosedAcrossCorrectiveSurfaces(
        string surface)
    {
        string psqlPath = ResolvePsqlExecutablePath();
        string scriptPath = ResolveOwnerIdempotentScriptPath();
        bool isDown = surface == "down";
        bool isOwner = surface == "owner";
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(
            isDown ? CorrectiveMigration : InitialMigration);

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ResetInheritedPrivilegeProbeRoleAsync(connection);
        try
        {
            await GrantInheritedColumnPrivilegeAsync(connection, "history-insert");

            if (isOwner)
            {
                PsqlInvocationResult result = await ExecutePsqlScriptAsync(
                    psqlPath,
                    database.ConnectionString,
                    scriptPath,
                    database.Schema);
                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains(
                    "T8 pre-history attestation failed: privilege-mismatch.",
                    result.CombinedOutput,
                    StringComparison.Ordinal);
            }
            else
            {
                PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                    () => context.Database.MigrateAsync(
                        isDown ? InitialMigration : CorrectiveMigration));
                Assert.Equal("P0001", exception.SqlState);
                Assert.StartsWith(
                    "T8 pre-history attestation failed: privilege-mismatch.",
                    exception.MessageText,
                    StringComparison.Ordinal);
            }

            Assert.Equal(
                isDown ? 1L : 0L,
                await ScalarAsync<long>(
                    connection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));
            if (isDown)
            {
                await AssertCorrectiveCatalogAsync(connection);
            }
            else
            {
                await AssertCorrectiveObjectsAbsentAsync(connection);
            }
        }
        finally
        {
            await ResetInheritedPrivilegeProbeRoleAsync(connection);
        }
    }

    [RequiresPostgresTheory]
    [InlineData("TRUNCATE")]
    [InlineData("REFERENCES")]
    [InlineData("TRIGGER")]
    [InlineData("MAINTAIN")]
    public async Task EffectivePrivilegeAttestationRejectsInheritedPostgres18TablePrivilege(
        string privilege)
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using (TabrukDbContext initialContext = database.CreateMigrationContext())
        {
            await initialContext.Database.MigrateAsync(InitialMigration);
        }

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ResetInheritedPrivilegeProbeRoleAsync(connection);
        try
        {
            await GrantInheritedTablePrivilegeAsync(connection, privilege);

            await using TabrukDbContext context = database.CreateMigrationContext();
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                () => context.Database.GetAppliedMigrationsAsync());

            Assert.Equal("P0001", exception.SqlState);
            Assert.StartsWith(
                "T8 pre-history attestation failed: privilege-mismatch.",
                exception.MessageText,
                StringComparison.Ordinal);
            Assert.Equal(
                1L,
                await ScalarAsync<long>(
                    connection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" """));
            await AssertCorrectiveObjectsAbsentAsync(connection);
        }
        finally
        {
            await ResetInheritedPrivilegeProbeRoleAsync(connection);
        }
    }

    [RequiresPostgresTheory]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("owner")]
    public async Task InheritedMaintainPrivilegeFailsClosedAcrossCorrectiveSurfaces(
        string surface)
    {
        string psqlPath = ResolvePsqlExecutablePath();
        string scriptPath = ResolveOwnerIdempotentScriptPath();
        bool isDown = surface == "down";
        bool isOwner = surface == "owner";
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(
            isDown ? CorrectiveMigration : InitialMigration);

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ResetInheritedPrivilegeProbeRoleAsync(connection);
        try
        {
            await GrantInheritedTablePrivilegeAsync(connection, "MAINTAIN");

            if (isOwner)
            {
                PsqlInvocationResult result = await ExecutePsqlScriptAsync(
                    psqlPath,
                    database.ConnectionString,
                    scriptPath,
                    database.Schema);
                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains(
                    "T8 pre-history attestation failed: privilege-mismatch.",
                    result.CombinedOutput,
                    StringComparison.Ordinal);
            }
            else
            {
                PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                    () => context.Database.MigrateAsync(
                        isDown ? InitialMigration : CorrectiveMigration));
                Assert.Equal("P0001", exception.SqlState);
                Assert.StartsWith(
                    "T8 pre-history attestation failed: privilege-mismatch.",
                    exception.MessageText,
                    StringComparison.Ordinal);
            }

            Assert.Equal(
                isDown ? 1L : 0L,
                await ScalarAsync<long>(
                    connection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));
            if (isDown)
            {
                await AssertCorrectiveCatalogAsync(connection);
            }
            else
            {
                await AssertCorrectiveObjectsAbsentAsync(connection);
            }
        }
        finally
        {
            await ResetInheritedPrivilegeProbeRoleAsync(connection);
        }
    }

    [RequiresPostgresTheory]
    [MemberData(nameof(AlternateGrantorPublicColumnAclCases))]
    public Task AlternateGrantorPublicColumnAclFailsClosedAcrossCorrectiveSurfaces(
        string surface,
        string probe) =>
        AssertAlternateGrantorPrivilegeFailsClosedAcrossCorrectiveSurfacesAsync(
            surface,
            probe,
            publicColumnAcl: true);

    [RequiresPostgresTheory]
    [MemberData(nameof(AlternateGrantorGrantOptionCases))]
    public Task AlternateGrantorGrantOptionFailsClosedAcrossCorrectiveSurfaces(
        string surface,
        string probe) =>
        AssertAlternateGrantorPrivilegeFailsClosedAcrossCorrectiveSurfacesAsync(
            surface,
            probe,
            publicColumnAcl: false);

    [RequiresPostgresFact]
    public async Task PreHistoryGateRejectsUnknownHistoryAfterCommittedHardening()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using (TabrukDbContext initialContext = database.CreateMigrationContext())
        {
            await initialContext.Database.MigrateAsync(InitialMigration);
        }

        await using (NpgsqlConnection setupConnection = new(database.ConnectionString))
        {
            await setupConnection.OpenAsync();
            await ExecuteAsync(
                setupConnection,
                """
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('20990101000000_UnknownMigration', '10.0.11')
                """);
            await SeedHostilePublicPrivilegesAsync(setupConnection);
        }

        await using TabrukDbContext context = database.CreateMigrationContext();
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => context.Database.GetAppliedMigrationsAsync());

        Assert.Equal("P0001", exception.SqlState);
        Assert.StartsWith(
            "T8 pre-history attestation failed: unknown-history.",
            exception.MessageText,
            StringComparison.Ordinal);

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = '20990101000000_UnknownMigration'
                """));
        await AssertHardenedPrivilegeMatrixAsync(connection);
    }

    [RequiresPostgresFact]
    public async Task OwnerScriptReconstructsForgedHistoryAndEfThenAcceptsAttestedNoOp()
    {
        string psqlPath = ResolvePsqlExecutablePath();
        string scriptPath = ResolveOwnerIdempotentScriptPath();
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using (TabrukDbContext initialContext = database.CreateMigrationContext())
        {
            await initialContext.Database.MigrateAsync(InitialMigration);
        }

        await ExecuteCommittedAsApplicationRoleAsync(
            database,
            """
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('20260815102612_T8CorrectivePostgresHardening', '10.0.11')
            """);

        await using (TabrukDbContext forgedContext = database.CreateMigrationContext())
        {
            await Assert.ThrowsAsync<PostgresException>(() => forgedContext.Database.MigrateAsync());
        }

        PsqlInvocationResult recovery = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            scriptPath,
            database.Schema);
        Assert.True(
            recovery.ExitCode == 0,
            $"Owner reconstruction failed.{Environment.NewLine}{recovery.CombinedOutput}");

        await using TabrukDbContext attestedContext = database.CreateMigrationContext();
        await attestedContext.Database.MigrateAsync();
        Assert.Equal(2, (await attestedContext.Database.GetAppliedMigrationsAsync()).Count());

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await AssertCorrectiveCatalogAsync(connection);
        await AssertHardenedPrivilegeMatrixAsync(connection);
    }

    [RequiresPostgresFact]
    public async Task FullyCorrectForgedHistoryIsAcceptedAsAlreadyApplied()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using (TabrukDbContext initialContext = database.CreateMigrationContext())
        {
            await initialContext.Database.MigrateAsync(InitialMigration);
        }

        await using (NpgsqlConnection setupConnection = new(database.ConnectionString))
        {
            await setupConnection.OpenAsync();
            await CreateCorrectiveObjectsAsync(setupConnection, validateConstraint: true);
        }

        await ExecuteCommittedAsApplicationRoleAsync(
            database,
            """
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('20260815102612_T8CorrectivePostgresHardening', '10.0.11')
            """);

        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync();
        Assert.Equal(2, (await context.Database.GetAppliedMigrationsAsync()).Count());

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await AssertCorrectiveCatalogAsync(connection);
        await AssertHardenedPrivilegeMatrixAsync(connection);
    }

    [RequiresPostgresFact]
    public async Task InterruptedDowngradeHistoryObjectMismatchFailsClosed()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """ALTER TABLE "signups" DROP CONSTRAINT "ck_signups_transition_chronology" """);
        await SeedHostilePublicPrivilegesAsync(connection);

        await using TabrukDbContext context = database.CreateMigrationContext();
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => context.Database.GetAppliedMigrationsAsync());

        Assert.Equal("P0001", exception.SqlState);
        Assert.StartsWith(
            "T8 pre-history attestation failed: corrective-history-object-mismatch.",
            exception.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
        await AssertWaitlistIndexStateAsync(connection, expectedPresent: true, expectedValid: true);
        await AssertChronologyConstraintStateAsync(
            connection,
            expectedPresent: false,
            expectedValidated: false);
        await AssertHardenedPrivilegeMatrixAsync(connection);
    }

    [RequiresPostgresTheory]
    [MemberData(nameof(HostileIndexSemanticUpCases))]
    public async Task CorrectiveMigrationRejectsHostileIndexSemanticVariantAndPreservesIt(
        string hostileSql,
        string category)
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using (TabrukDbContext initialContext = database.CreateMigrationContext())
        {
            await initialContext.Database.MigrateAsync(InitialMigration);
        }

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, hostileSql);
        string hostileDefinition = await GetWaitlistIndexDefinitionAsync(connection);

        await using TabrukDbContext context = database.CreateMigrationContext();
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => context.Database.MigrateAsync());

        Assert.Equal("P0001", exception.SqlState);
        Assert.StartsWith(
            $"T8 object definition mismatch: object=ux_signups_waitlisted_order_per_help_need; category={category}.",
            exception.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(hostileDefinition, await GetWaitlistIndexDefinitionAsync(connection));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
        await AssertHardenedPrivilegeMatrixAsync(connection);
    }

    [RequiresPostgresTheory]
    [MemberData(nameof(HostileIndexSemanticRecoveryCases))]
    public async Task DownAndOwnerRejectHostileIndexSemanticVariantAndPreserveIt(
        string hostileSql,
        string category,
        string surface)
    {
        string psqlPath = ResolvePsqlExecutablePath();
        string scriptPath = ResolveOwnerIdempotentScriptPath();
        bool isDown = surface == "down";
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using (TabrukDbContext setupContext = database.CreateMigrationContext())
        {
            if (isDown)
            {
                await setupContext.Database.MigrateAsync();
            }
            else
            {
                await setupContext.Database.MigrateAsync(InitialMigration);
            }
        }

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        if (isDown)
        {
            await ExecuteAsync(
                connection,
                """DROP INDEX "ux_signups_waitlisted_order_per_help_need" """);
        }

        await ExecuteAsync(connection, hostileSql);
        string hostileDefinition = await GetWaitlistIndexDefinitionAsync(connection);

        if (isDown)
        {
            await using TabrukDbContext context = database.CreateMigrationContext();
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                () => context.Database.MigrateAsync(InitialMigration));
            Assert.Equal("P0001", exception.SqlState);
            Assert.StartsWith(
                $"T8 object definition mismatch: object=ux_signups_waitlisted_order_per_help_need; category={category}.",
                exception.MessageText,
                StringComparison.Ordinal);
        }
        else
        {
            PsqlInvocationResult result = await ExecutePsqlScriptAsync(
                psqlPath,
                database.ConnectionString,
                scriptPath,
                database.Schema);
            Assert.NotEqual(0, result.ExitCode);
            Assert.True(
                result.CombinedOutput.Contains(
                    $"category={category}.",
                    StringComparison.Ordinal),
                result.CombinedOutput);
        }

        Assert.Equal(hostileDefinition, await GetWaitlistIndexDefinitionAsync(connection));
        Assert.Equal(
            isDown ? 1L : 0L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
        await AssertHardenedPrivilegeMatrixAsync(connection);
    }

    [RequiresPostgresTheory]
    [MemberData(nameof(HostileConstraintSemanticUpCases))]
    public async Task CorrectiveMigrationRejectsHostileConstraintSemanticVariantAndPreservesIt(
        string hostileSql,
        string category)
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using (TabrukDbContext initialContext = database.CreateMigrationContext())
        {
            await initialContext.Database.MigrateAsync(InitialMigration);
        }

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, hostileSql);
        string hostileDefinition = await GetChronologyConstraintDefinitionAsync(connection);

        await using TabrukDbContext context = database.CreateMigrationContext();
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => context.Database.MigrateAsync());

        Assert.Equal("P0001", exception.SqlState);
        Assert.StartsWith(
            $"T8 object definition mismatch: object=ck_signups_transition_chronology; category={category}.",
            exception.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(hostileDefinition, await GetChronologyConstraintDefinitionAsync(connection));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
        await AssertHardenedPrivilegeMatrixAsync(connection);
    }

    [RequiresPostgresTheory]
    [MemberData(nameof(HostileConstraintSemanticRecoveryCases))]
    public async Task DownAndOwnerRejectHostileConstraintSemanticVariantAndPreserveIt(
        string hostileSql,
        string category,
        string surface)
    {
        string psqlPath = ResolvePsqlExecutablePath();
        string scriptPath = ResolveOwnerIdempotentScriptPath();
        bool isDown = surface == "down";
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using (TabrukDbContext setupContext = database.CreateMigrationContext())
        {
            if (isDown)
            {
                await setupContext.Database.MigrateAsync();
            }
            else
            {
                await setupContext.Database.MigrateAsync(InitialMigration);
            }
        }

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        if (isDown)
        {
            await ExecuteAsync(
                connection,
                """ALTER TABLE "signups" DROP CONSTRAINT "ck_signups_transition_chronology" """);
        }

        await ExecuteAsync(connection, hostileSql);
        string hostileDefinition = await GetChronologyConstraintDefinitionAsync(connection);

        if (isDown)
        {
            await using TabrukDbContext context = database.CreateMigrationContext();
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                () => context.Database.MigrateAsync(InitialMigration));
            Assert.Equal("P0001", exception.SqlState);
            Assert.StartsWith(
                $"T8 object definition mismatch: object=ck_signups_transition_chronology; category={category}.",
                exception.MessageText,
                StringComparison.Ordinal);
        }
        else
        {
            PsqlInvocationResult result = await ExecutePsqlScriptAsync(
                psqlPath,
                database.ConnectionString,
                scriptPath,
                database.Schema);
            Assert.NotEqual(0, result.ExitCode);
            Assert.True(
                result.CombinedOutput.Contains(
                    $"category={category}.",
                    StringComparison.Ordinal),
                result.CombinedOutput);
        }

        Assert.Equal(
            hostileDefinition,
            await GetChronologyConstraintDefinitionAsync(connection));
        Assert.Equal(
            isDown ? 1L : 0L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
        await AssertHardenedPrivilegeMatrixAsync(connection);
    }

    [RequiresPostgresFact]
    public async Task CorrectiveDownRejectsHostileObjectPreservesHistoryAndFailsClosedPrivileges()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        await using TabrukDbContext context = database.CreateMigrationContext();
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            DROP INDEX "ux_signups_waitlisted_order_per_help_need";
            CREATE INDEX "ux_signups_waitlisted_order_per_help_need"
                ON "signups" ("organization_id")
            """);
        await SeedHostilePublicPrivilegesAsync(connection);
        string hostileDefinition = await GetWaitlistIndexDefinitionAsync(connection);
        uint constraintOid = await GetChronologyConstraintOidAsync(connection);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => context.Database.MigrateAsync(InitialMigration));

        Assert.Equal("P0001", exception.SqlState);
        Assert.StartsWith(
            "T8 object definition mismatch: object=ux_signups_waitlisted_order_per_help_need; category=state.",
            exception.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(
            2L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" """));
        Assert.Equal(hostileDefinition, await GetWaitlistIndexDefinitionAsync(connection));
        Assert.Equal(constraintOid, await GetChronologyConstraintOidAsync(connection));
        await AssertHardenedPrivilegeMatrixAsync(connection);
    }

    [RequiresPostgresTheory]
    [MemberData(nameof(HostileManagedDescendantCases))]
    public async Task CorrectiveSurfacesRejectHostileOwnedManagedDescendantBeforeMutation(
        string surface)
    {
        bool isFactory = surface == "factory";
        bool isDown = surface == "down";
        bool isOwner = surface == "owner";
        string? psqlPath = isOwner ? ResolvePsqlExecutablePath() : null;
        string? scriptPath = isOwner ? ResolveOwnerIdempotentScriptPath() : null;
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(isDown ? CorrectiveMigration : InitialMigration);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            $"""
            DROP ROLE IF EXISTS "{HostileOwnerRole}";
            CREATE ROLE "{HostileOwnerRole}" NOLOGIN;
            CREATE TABLE "{HostileDescendantTable}"
            (
                hostile_marker text
            )
            INHERITS ("users");
            GRANT SELECT ON TABLE "{HostileDescendantTable}" TO PUBLIC;
            ALTER TABLE "{HostileDescendantTable}" OWNER TO "{HostileOwnerRole}";
            GRANT UPDATE ON TABLE "organizations" TO PUBLIC;
            """);

        try
        {
            uint descendantOid = await ScalarAsync<uint>(
                connection,
                $"""SELECT '"{HostileDescendantTable}"'::regclass::oid""");

            if (isOwner)
            {
                PsqlInvocationResult result = await ExecutePsqlScriptAsync(
                    psqlPath!,
                    database.ConnectionString,
                    scriptPath!,
                    database.Schema);
                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains(
                    "T8 pre-history attestation failed: managed-table-topology-mismatch.",
                    result.CombinedOutput,
                    StringComparison.Ordinal);
            }
            else
            {
                await using TabrukDbContext? gatedContext =
                    isFactory ? database.CreateMigrationContext() : null;
                PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                    () => isFactory
                        ? gatedContext!.Database.GetAppliedMigrationsAsync()
                        : context.Database.MigrateAsync(
                            isDown ? InitialMigration : CorrectiveMigration));
                Assert.Equal("P0001", exception.SqlState);
                Assert.StartsWith(
                    "T8 pre-history attestation failed: managed-table-topology-mismatch.",
                    exception.MessageText,
                    StringComparison.Ordinal);
            }

            Assert.Equal(
                descendantOid,
                await ScalarAsync<uint>(
                    connection,
                    $"""SELECT '"{HostileDescendantTable}"'::regclass::oid"""));
            Assert.Equal(
                HostileOwnerRole,
                await ScalarAsync<string>(
                    connection,
                    $"""
                    SELECT owner_role.rolname
                    FROM pg_class AS relation
                    JOIN pg_roles AS owner_role ON owner_role.oid = relation.relowner
                    WHERE relation.oid = '"{HostileDescendantTable}"'::regclass
                    """));
            Assert.True(
                await ScalarAsync<bool>(
                    connection,
                    $"""SELECT has_table_privilege('public', '"{HostileDescendantTable}"', 'SELECT')"""));
            Assert.True(await HasTablePrivilegeAsync(connection, "organizations", "UPDATE"));
            Assert.Equal(
                isDown ? 1L : 0L,
                await ScalarAsync<long>(
                    connection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));

            if (isDown)
            {
                await AssertCorrectiveCatalogAsync(connection);
            }
            else
            {
                await AssertCorrectiveObjectsAbsentAsync(connection);
            }
        }
        finally
        {
            await ExecuteAsync(
                connection,
                $"""
                DROP TABLE IF EXISTS "{HostileDescendantTable}";
                DROP ROLE IF EXISTS "{HostileOwnerRole}";
                """);
        }
    }

    [RequiresPostgresTheory]
    [MemberData(nameof(ConcurrentManagedTopologyCases))]
    public async Task CorrectiveSurfacesSerializeConcurrentManagedTopologyBeforeTrust(
        string surface,
        string topologyProbe)
    {
        bool isFactory = surface == "factory";
        bool isDown = surface == "down";
        bool isOwner = surface == "owner";
        string surfaceApplicationName = $"tabruk_t8_surface_{surface}_{topologyProbe}";
        string attackerApplicationName = $"tabruk_t8_attacker_{surface}_{topologyProbe}";
        string? psqlPath = isOwner ? ResolvePsqlExecutablePath() : null;
        string? scriptPath = isOwner ? ResolveOwnerIdempotentScriptPath() : null;
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext setupContext = database.CreateMigrationContext();
        await setupContext.Database.MigrateAsync(isDown ? CorrectiveMigration : InitialMigration);
        await using NpgsqlConnection setupConnection = new(database.ConnectionString);
        await setupConnection.OpenAsync();
        await PrepareTopologyProbeAsync(setupConnection, topologyProbe);

        NpgsqlConnectionStringBuilder surfaceBuilder = new(database.ConnectionString)
        {
            ApplicationName = surfaceApplicationName,
        };
        NpgsqlConnectionStringBuilder attackerBuilder = new(database.ConnectionString)
        {
            ApplicationName = attackerApplicationName,
        };
        await using NpgsqlConnection blockerConnection = new(database.ConnectionString);
        await using NpgsqlConnection observerConnection = new(database.ConnectionString);
        await using NpgsqlConnection attackerConnection = new(attackerBuilder.ConnectionString);
        await blockerConnection.OpenAsync();
        await observerConnection.OpenAsync();
        await attackerConnection.OpenAsync();
        await using NpgsqlTransaction blockerTransaction =
            await blockerConnection.BeginTransactionAsync();
        await using (NpgsqlCommand blockerCommand = new(
            """LOCK TABLE "__EFMigrationsHistory" IN ROW EXCLUSIVE MODE""",
            blockerConnection,
            blockerTransaction))
        {
            await blockerCommand.ExecuteNonQueryAsync();
        }

        Task<Exception?>? efSurfaceTask = null;
        Task<PsqlInvocationResult>? ownerSurfaceTask = null;
        if (isOwner)
        {
            ownerSurfaceTask = ExecutePsqlScriptAsync(
                psqlPath!,
                surfaceBuilder.ConnectionString,
                scriptPath!,
                database.Schema,
                applicationName: surfaceApplicationName);
        }
        else
        {
            efSurfaceTask = CaptureExceptionAsync(
                async () =>
                {
                    await using TabrukDbContext context =
                        TabrukDbContextOptions.Create(surfaceBuilder.ConnectionString);
                    if (isFactory)
                    {
                        await context.Database.GetAppliedMigrationsAsync();
                    }
                    else
                    {
                        await context.Database.MigrateAsync(
                            isDown ? InitialMigration : CorrectiveMigration);
                    }
                });
        }

        Task surfaceTask = (Task?)ownerSurfaceTask ?? efSurfaceTask!;
        await WaitForManagedTopologyLockAsync(
            observerConnection,
            surfaceApplicationName,
            surfaceTask,
            TimeSpan.FromSeconds(10));

        Task<Exception?> attackerTask = CaptureExceptionAsync(
            () => ExecuteAsync(
                attackerConnection,
                TopologyProbeSql(topologyProbe)));
        await WaitForRelationLockAsync(
            observerConnection,
            attackerApplicationName,
            "users",
            attackerTask,
            TimeSpan.FromSeconds(10));
        Assert.False(attackerTask.IsCompleted);

        await blockerTransaction.CommitAsync();

        Exception? attackerException = await attackerTask;
        Assert.Null(attackerException);

        if (isOwner)
        {
            PsqlInvocationResult result = await ownerSurfaceTask!;
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "T8 pre-history attestation failed: managed-table-topology-mismatch.",
                result.CombinedOutput,
                StringComparison.Ordinal);
        }
        else
        {
            Exception? surfaceException = await efSurfaceTask!;
            if (isFactory)
            {
                if (surfaceException is not null)
                {
                    PostgresException exception =
                        Assert.IsType<PostgresException>(surfaceException);
                    Assert.Equal("P0001", exception.SqlState);
                    Assert.StartsWith(
                        "T8 pre-history attestation failed: managed-table-topology-mismatch.",
                        exception.MessageText,
                        StringComparison.Ordinal);
                }

                await using TabrukDbContext retryContext = database.CreateMigrationContext();
                PostgresException retryException = await Assert.ThrowsAsync<PostgresException>(
                    () => retryContext.Database.GetAppliedMigrationsAsync());
                Assert.Equal("P0001", retryException.SqlState);
                Assert.StartsWith(
                    "T8 pre-history attestation failed: managed-table-topology-mismatch.",
                    retryException.MessageText,
                    StringComparison.Ordinal);
            }
            else
            {
                PostgresException exception = Assert.IsType<PostgresException>(surfaceException);
                Assert.Equal("P0001", exception.SqlState);
                Assert.StartsWith(
                    "T8 pre-history attestation failed: managed-table-topology-mismatch.",
                    exception.MessageText,
                    StringComparison.Ordinal);
            }
        }

        Assert.Equal(
            isDown ? 1L : 0L,
            await ScalarAsync<long>(
                setupConnection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));

        await RemoveTopologyProbeAsync(setupConnection, topologyProbe);

        if (isOwner)
        {
            PsqlInvocationResult firstRetry = await ExecutePsqlScriptAsync(
                psqlPath!,
                database.ConnectionString,
                scriptPath!,
                database.Schema);
            PsqlInvocationResult secondRetry = await ExecutePsqlScriptAsync(
                psqlPath!,
                database.ConnectionString,
                scriptPath!,
                database.Schema);
            Assert.Equal(0, firstRetry.ExitCode);
            Assert.Equal(0, secondRetry.ExitCode);
        }
        else
        {
            await using TabrukDbContext retryContext = database.CreateMigrationContext();
            if (!isFactory)
            {
                await retryContext.Database.MigrateAsync(
                    isDown ? InitialMigration : CorrectiveMigration);
            }

            await retryContext.Database.GetAppliedMigrationsAsync();
            await retryContext.Database.GetAppliedMigrationsAsync();
        }
    }

    [RequiresPostgresTheory]
    [MemberData(nameof(ConcurrentHistoryTopologyCases))]
    public async Task CorrectiveSurfacesRejectQueuedHistoryTopologyAfterConcurrentPhase(
        string surface,
        string topologyProbe)
    {
        bool isDown = surface == "down";
        bool isOwner = surface == "owner";
        string surfaceApplicationName = $"tabruk_t8_post_concurrent_{surface}_{topologyProbe}";
        string attackerApplicationName = $"tabruk_t8_history_attacker_{surface}_{topologyProbe}";
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext setupContext = database.CreateMigrationContext();
        await setupContext.Database.MigrateAsync(isDown ? CorrectiveMigration : InitialMigration);
        await using NpgsqlConnection setupConnection = new(database.ConnectionString);
        await using NpgsqlConnection observerConnection = new(database.ConnectionString);
        await setupConnection.OpenAsync();
        await observerConnection.OpenAsync();
        await PrepareHistoryTopologyProbeAsync(setupConnection, topologyProbe);
        uint historyOid = await ScalarAsync<uint>(
            setupConnection,
            """SELECT '"__EFMigrationsHistory"'::regclass::oid""");

        NpgsqlConnectionStringBuilder surfaceBuilder = new(database.ConnectionString)
        {
            ApplicationName = surfaceApplicationName,
        };
        NpgsqlConnectionStringBuilder attackerBuilder = new(database.ConnectionString)
        {
            ApplicationName = attackerApplicationName,
        };
        await using NpgsqlConnection blockerConnection = new(database.ConnectionString);
        await using NpgsqlConnection attackerConnection = new(attackerBuilder.ConnectionString);
        await blockerConnection.OpenAsync();
        await attackerConnection.OpenAsync();
        await InstallConcurrentBoundaryPauseAsync(setupConnection, surfaceApplicationName);
        await ExecuteAsync(
            blockerConnection,
            $"SELECT pg_advisory_lock({PostConcurrentBoundaryAdvisoryLock})");

        Task<Exception?>? efSurfaceTask = null;
        Task<PsqlInvocationResult>? ownerSurfaceTask = null;
        if (isOwner)
        {
            ownerSurfaceTask = ExecutePsqlScriptAsync(
                ResolvePsqlExecutablePath(),
                surfaceBuilder.ConnectionString,
                ResolveOwnerIdempotentScriptPath(),
                database.Schema,
                applicationName: surfaceApplicationName);
        }
        else
        {
            efSurfaceTask = CaptureExceptionAsync(
                async () =>
                {
                    await using TabrukDbContext context =
                        TabrukDbContextOptions.Create(surfaceBuilder.ConnectionString);
                    await context.Database.MigrateAsync(
                        isDown ? InitialMigration : CorrectiveMigration);
                });
        }

        Task surfaceTask = (Task?)ownerSurfaceTask ?? efSurfaceTask!;
        await WaitForConcurrentIndexBoundaryAsync(
            observerConnection,
            surfaceApplicationName,
            isDown,
            surfaceTask,
            TimeSpan.FromSeconds(15));
        await using NpgsqlTransaction blockerTransaction =
            await blockerConnection.BeginTransactionAsync();
        await ExecuteAsync(
            blockerConnection,
            """LOCK TABLE "__EFMigrationsHistory" IN ACCESS SHARE MODE""");

        Task<Exception?> attackerTask = CaptureExceptionAsync(
            () => ExecuteAsync(
                attackerConnection,
                HistoryTopologyProbeSql(topologyProbe)));
        await WaitForRelationLockAsync(
            observerConnection,
            attackerApplicationName,
            "__EFMigrationsHistory",
            attackerTask,
            TimeSpan.FromSeconds(10));

        await ExecuteAsync(
            blockerConnection,
            $"SELECT pg_advisory_unlock({PostConcurrentBoundaryAdvisoryLock})");
        await blockerTransaction.CommitAsync();
        Assert.Null(await attackerTask);

        if (isOwner)
        {
            PsqlInvocationResult result = await ownerSurfaceTask!;
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "T8 pre-history attestation failed: history-structure-mismatch.",
                result.CombinedOutput,
                StringComparison.Ordinal);
        }
        else
        {
            Exception surfaceException = await efSurfaceTask!
                ?? new InvalidOperationException("The corrective surface completed without an exception.");
            PostgresException exception =
                Assert.IsType<PostgresException>(
                    surfaceException is InvalidOperationException
                    {
                        InnerException: PostgresException innerException,
                    }
                        ? innerException
                        : surfaceException);
            Assert.True(
                exception.SqlState is "P0001" or "40P01",
                exception.ToString());
            if (exception.SqlState == "P0001")
            {
                Assert.StartsWith(
                    "T8 pre-history attestation failed: history-structure-mismatch.",
                    exception.MessageText,
                    StringComparison.Ordinal);
            }
        }

        Assert.Equal(
            historyOid,
            await ScalarAsync<uint>(
                setupConnection,
                """SELECT '"__EFMigrationsHistory"'::regclass::oid"""));
        Assert.Equal(
            isDown ? 1L : 0L,
            await ScalarAsync<long>(
                setupConnection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
        await RemoveHistoryTopologyProbeAsync(setupConnection, topologyProbe);
        await RemoveConcurrentBoundaryPauseAsync(setupConnection);
    }

    [RequiresPostgresTheory]
    [MemberData(nameof(FactoryHistoryTopologyCases))]
    public async Task MigrationFactoryRejectsHostileHistoryTopologyAndPreservesIt(
        string topologyProbe)
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext setupContext = database.CreateMigrationContext();
        await setupContext.Database.MigrateAsync(InitialMigration);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await PrepareHistoryTopologyProbeAsync(connection, topologyProbe);
        await ExecuteAsync(connection, HistoryTopologyProbeSql(topologyProbe));
        uint historyOid = await ScalarAsync<uint>(
            connection,
            """SELECT '"__EFMigrationsHistory"'::regclass::oid""");

        await using TabrukDbContext context = database.CreateMigrationContext();
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => context.Database.GetAppliedMigrationsAsync());

        Assert.Equal("P0001", exception.SqlState);
        Assert.StartsWith(
            "T8 pre-history attestation failed: history-structure-mismatch.",
            exception.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(
            historyOid,
            await ScalarAsync<uint>(
                connection,
                """SELECT '"__EFMigrationsHistory"'::regclass::oid"""));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
        await RemoveHistoryTopologyProbeAsync(connection, topologyProbe);
    }

    [RequiresPostgresFact]
    public async Task ApprovedOwnerRejectsQueuedCanonicalConstraintReplacementAfterConcurrentPhase()
    {
        const string surface = "owner";
        string surfaceApplicationName = $"tabruk_t8_post_concurrent_object_{surface}";
        string attackerApplicationName = $"tabruk_t8_object_attacker_{surface}";
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext setupContext = database.CreateMigrationContext();
        await setupContext.Database.MigrateAsync(InitialMigration);
        await using NpgsqlConnection setupConnection = new(database.ConnectionString);
        await using NpgsqlConnection observerConnection = new(database.ConnectionString);
        await setupConnection.OpenAsync();
        await observerConnection.OpenAsync();

        NpgsqlConnectionStringBuilder surfaceBuilder = new(database.ConnectionString)
        {
            ApplicationName = surfaceApplicationName,
        };
        NpgsqlConnectionStringBuilder attackerBuilder = new(database.ConnectionString)
        {
            ApplicationName = attackerApplicationName,
        };
        await using NpgsqlConnection blockerConnection = new(database.ConnectionString);
        await using NpgsqlConnection attackerConnection = new(attackerBuilder.ConnectionString);
        await blockerConnection.OpenAsync();
        await attackerConnection.OpenAsync();
        await ExecuteAsync(
            blockerConnection,
            $"SELECT pg_advisory_lock({PostIndexCaptureRaceAdvisoryLock})");

        Task<PsqlInvocationResult> ownerSurfaceTask = ExecutePsqlScriptAsync(
            ResolvePsqlExecutablePath(),
            surfaceBuilder.ConnectionString,
            ResolveOwnerIdempotentScriptPath(),
            database.Schema,
            optionsOverride:
                $"-c tabruk.target_schema={database.Schema} -c search_path={database.Schema} -c tabruk.t8_test_pause_after_index_capture=on",
            applicationName: surfaceApplicationName);
        Task surfaceTask = ownerSurfaceTask;
        await WaitForAdvisoryLockAsync(
            observerConnection,
            surfaceApplicationName,
            surfaceTask,
            TimeSpan.FromSeconds(15));
        await using NpgsqlTransaction blockerTransaction =
            await blockerConnection.BeginTransactionAsync();
        await ExecuteAsync(
            blockerConnection,
            """LOCK TABLE "signups" IN ACCESS SHARE MODE""");

        Task<Exception?> attackerTask = CaptureExceptionAsync(
            () => ExecuteAsync(
                attackerConnection,
                """
                ALTER TABLE "signups"
                    ADD CONSTRAINT "ck_signups_transition_chronology"
                    CHECK ("last_transition_at" IS NULL OR TRUE)
                """));
        await WaitForRelationLockAsync(
            observerConnection,
            attackerApplicationName,
            "signups",
            attackerTask,
            TimeSpan.FromSeconds(10));
        await ExecuteAsync(
            blockerConnection,
            $"SELECT pg_advisory_unlock({PostIndexCaptureRaceAdvisoryLock})");
        await blockerTransaction.CommitAsync();
        PostgresException attackerException =
            Assert.IsType<PostgresException>(await attackerTask);
        Assert.Equal("42710", attackerException.SqlState);

        PsqlInvocationResult result = await ownerSurfaceTask;
        Assert.True(
            result.ExitCode == 0,
            $"Owner script failed.{Environment.NewLine}{result.CombinedOutput}");
        Assert.Contains("T8_RESULT=success", result.CombinedOutput, StringComparison.Ordinal);

        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                setupConnection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
        string constraintDefinition = await GetChronologyConstraintDefinitionAsync(setupConnection);
        Assert.Contains(
            "last_transition_at >= submitted_at",
            constraintDefinition,
            StringComparison.Ordinal);
    }

    [RequiresPostgresFact]
    public async Task EfUpgradeSchemaMatrixRejectsAmbiguousOrUnresolvedTargetsWithoutHistoryMutation()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext validContext = database.CreateMigrationContext();
        await validContext.Database.MigrateAsync(InitialMigration);

        await AssertFactoryRoutingFailuresAsync(database, expectedHistoryCount: 1);
        string unresolvedSchema = $"tabruk_missing_{Guid.NewGuid():N}";
        string unresolvedConnection = database.BuildMigrationConnectionString(
            unresolvedSchema,
            $"-c tabruk.target_schema={unresolvedSchema} -c tabruk.disposable_ef=on");
        await using TabrukDbContext unresolvedContext =
            TabrukDbContextOptions.Create(unresolvedConnection);
        await Assert.ThrowsAsync<PostgresException>(
            () => unresolvedContext.Database.MigrateAsync());

        Assert.Single(await validContext.Database.GetAppliedMigrationsAsync());
    }

    [RequiresPostgresFact]
    public async Task EfDowngradeSchemaMatrixRejectsAmbiguousOrUnresolvedTargetsWithoutHistoryMutation()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        await using TabrukDbContext validContext = database.CreateMigrationContext();

        await AssertFactoryRoutingFailuresAsync(database, expectedHistoryCount: 2);
        string unresolvedSchema = $"tabruk_missing_{Guid.NewGuid():N}";
        string unresolvedConnection = database.BuildMigrationConnectionString(
            unresolvedSchema,
            $"-c tabruk.target_schema={unresolvedSchema} -c tabruk.disposable_ef=on");
        await using TabrukDbContext unresolvedContext =
            TabrukDbContextOptions.Create(unresolvedConnection);
        await Assert.ThrowsAsync<PostgresException>(
            () => unresolvedContext.Database.MigrateAsync(InitialMigration));

        Assert.Equal(2, (await validContext.Database.GetAppliedMigrationsAsync()).Count());
    }

    [RequiresPostgresFact]
    public async Task ApprovedOwnerIdempotentScriptAppliesAndReappliesOnInitialOnlySchema()
    {
        string psqlPath = ResolvePsqlExecutablePath();
        string scriptPath = ResolveOwnerIdempotentScriptPath();
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(InitialMigration);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await SeedHostilePublicPrivilegesAsync(connection);

        PsqlInvocationResult firstRun = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            scriptPath,
            database.Schema);
        Assert.True(
            firstRun.ExitCode == 0,
            $"First owner idempotent corrective script run failed.{Environment.NewLine}{firstRun.CombinedOutput}");
        await AssertHardenedPrivilegeMatrixAsync(connection);
        uint indexOid = await GetWaitlistIndexOidAsync(connection);
        uint constraintOid = await GetChronologyConstraintOidAsync(connection);
        await SeedHostilePublicPrivilegesAsync(connection);

        PsqlInvocationResult secondRun = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            scriptPath,
            database.Schema);
        Assert.True(
            secondRun.ExitCode == 0,
            $"Second owner idempotent corrective script run failed.{Environment.NewLine}{secondRun.CombinedOutput}");
        Assert.Equal(indexOid, await GetWaitlistIndexOidAsync(connection));
        Assert.Equal(constraintOid, await GetChronologyConstraintOidAsync(connection));

        await using TabrukDbContext verificationContext = database.CreateMigrationContext();
        Assert.Equal(2, (await verificationContext.Database.GetAppliedMigrationsAsync()).Count());

        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
        await AssertCorrectiveCatalogAsync(connection);
        await AssertHardenedPrivilegeMatrixAsync(connection);
        await AssertApplicationRoleMutationDeniedAsync(
            database,
            """UPDATE "__EFMigrationsHistory" SET "ProductVersion" = "ProductVersion" WHERE FALSE""");
    }

    [RequiresPostgresTheory]
    [InlineData("up", "trigger")]
    [InlineData("up", "rule")]
    [InlineData("up", "policy")]
    [InlineData("down", "trigger")]
    [InlineData("down", "rule")]
    [InlineData("down", "policy")]
    public async Task ApprovedOwnerScriptsRejectExecutableHistoryMetadataBeforeMutation(
        string surface,
        string hostileMetadata)
    {
        bool isDown = surface == "down";
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(isDown ? CorrectiveMigration : InitialMigration);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await InstallHostileHistoryMetadataAsync(connection, hostileMetadata);
        long expectedCorrectiveCount = isDown ? 1L : 0L;

        PsqlInvocationResult result = await ExecutePsqlScriptAsync(
            ResolvePsqlExecutablePath(),
            database.ConnectionString,
            isDown ? ResolveOwnerDowngradeScriptPath() : ResolveOwnerIdempotentScriptPath(),
            database.Schema);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "T8_ATTESTATION_FAILED:history",
            result.CombinedOutput,
            StringComparison.Ordinal);
        Assert.Equal(
            expectedCorrectiveCount,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
    }

    [RequiresPostgresTheory]
    [InlineData("up")]
    [InlineData("down")]
    public async Task ApprovedOwnerScriptsRejectDroppedAndRecreatedTargetSchema(string surface)
    {
        bool isDown = surface == "down";
        string applicationName = $"tabruk_t8_schema_replacement_{surface}";
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(isDown ? CorrectiveMigration : InitialMigration);
        await using NpgsqlConnection blockerConnection = new(database.ConnectionString);
        await using NpgsqlConnection observerConnection = new(database.AdministrativeConnectionString);
        await using NpgsqlConnection attackerConnection = new(database.AdministrativeConnectionString);
        await blockerConnection.OpenAsync();
        await observerConnection.OpenAsync();
        await attackerConnection.OpenAsync();
        uint originalNamespace = await ScalarAsync<uint>(
            blockerConnection,
            "SELECT oid FROM pg_namespace WHERE nspname = @schema",
            ("schema", database.Schema));
        long advisoryKey = await ScalarAsync<long>(
            blockerConnection,
            """
            SELECT hashtextextended(
                current_database() || ':' || @namespace::text || ':T8',
                0)
            """,
            ("namespace", (int)originalNamespace));
        await ExecuteAsync(blockerConnection, "SELECT pg_advisory_lock(@key)", ("key", advisoryKey));

        NpgsqlConnectionStringBuilder surfaceBuilder = new(database.ConnectionString)
        {
            ApplicationName = applicationName,
        };
        PsqlProcessInvocation? invocation = null;
        try
        {
            invocation = StartPsqlScript(
                ResolvePsqlExecutablePath(),
                surfaceBuilder.ConnectionString,
                isDown ? ResolveOwnerDowngradeScriptPath() : ResolveOwnerIdempotentScriptPath(),
                database.Schema,
                applicationName: applicationName);
            await WaitForAdvisoryLockAsync(
                observerConnection,
                applicationName,
                invocation.Completion,
                TimeSpan.FromSeconds(10));

            await ExecuteAsync(
                attackerConnection,
                $"""
                DROP SCHEMA "{database.Schema}" CASCADE;
                CREATE SCHEMA "{database.Schema}";
                """);
            uint replacementNamespace = await ScalarAsync<uint>(
                attackerConnection,
                "SELECT oid FROM pg_namespace WHERE nspname = @schema",
                ("schema", database.Schema));
            Assert.NotEqual(originalNamespace, replacementNamespace);

            await ExecuteAsync(
                blockerConnection,
                "SELECT pg_advisory_unlock(@key)",
                ("key", advisoryKey));
            PsqlInvocationResult result = await invocation.Completion;

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "T8_ATTESTATION_FAILED:schema",
                result.CombinedOutput,
                StringComparison.Ordinal);
            Assert.Equal(
                0L,
                await ScalarAsync<long>(
                    attackerConnection,
                    """
                    SELECT COUNT(*)
                    FROM pg_class
                    WHERE relnamespace = @namespace::oid
                    """,
                    ("namespace", (int)replacementNamespace)));
        }
        finally
        {
            await ExecuteAsync(
                blockerConnection,
                "SELECT pg_advisory_unlock(@key)",
                ("key", advisoryKey));
            if (invocation is not null)
            {
                await invocation.DisposeAsync();
            }
        }
    }

    [RequiresPostgresTheory]
    [InlineData("up")]
    [InlineData("down")]
    public async Task ApprovedOwnerScriptsRejectInheritedSchemaUsageWithoutDirectGrant(string surface)
    {
        bool isDown = surface == "down";
        string scriptPath = isDown
            ? ResolveOwnerDowngradeScriptPath()
            : ResolveOwnerIdempotentScriptPath();
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(isDown ? CorrectiveMigration : InitialMigration);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ResetInheritedPrivilegeProbeRoleAsync(connection);
        try
        {
            await ExecuteAsync(
                connection,
                $"""
                DO $tabruk$
                BEGIN
                    EXECUTE format(
                        'REVOKE USAGE ON SCHEMA %I FROM "tabruk_app"',
                        current_schema());
                END
                $tabruk$;
                CREATE ROLE "{InheritedPrivilegeProbeRole}" NOLOGIN;
                CREATE ROLE "{InheritedPrivilegeBridgeRole}" NOLOGIN;
                GRANT "{InheritedPrivilegeProbeRole}" TO "{InheritedPrivilegeBridgeRole}";
                GRANT "{InheritedPrivilegeBridgeRole}" TO "tabruk_app";
                DO $tabruk$
                BEGIN
                    EXECUTE format(
                        'GRANT USAGE ON SCHEMA %I TO "{InheritedPrivilegeProbeRole}"',
                        current_schema());
                END
                $tabruk$;
                """);

            Assert.True(
                await ScalarAsync<bool>(
                    connection,
                    "SELECT has_schema_privilege('tabruk_app', current_schema(), 'USAGE')"));
            Assert.False(
                await ScalarAsync<bool>(
                    connection,
                    """
                    SELECT EXISTS (
                        SELECT 1
                        FROM pg_namespace AS namespace_definition
                        CROSS JOIN LATERAL aclexplode(namespace_definition.nspacl) AS privilege
                        WHERE namespace_definition.nspname = current_schema()
                          AND privilege.grantee =
                              (SELECT oid FROM pg_roles WHERE rolname = 'tabruk_app')
                          AND privilege.privilege_type = 'USAGE'
                          AND NOT privilege.is_grantable)
                    """));

            PsqlInvocationResult result = await ExecutePsqlScriptAsync(
                ResolvePsqlExecutablePath(),
                database.ConnectionString,
                scriptPath,
                database.Schema);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("T8_ATTESTATION_FAILED:acl", result.CombinedOutput, StringComparison.Ordinal);
            Assert.Equal(
                isDown ? 1L : 0L,
                await ScalarAsync<long>(
                    connection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));
        }
        finally
        {
            await ResetInheritedPrivilegeProbeRoleAsync(connection);
        }
    }

    [RequiresPostgresTheory]
    [InlineData("up")]
    [InlineData("down")]
    public async Task ApprovedOwnerScriptsRejectCatalogNamedOperatorClassOutsidePgCatalog(
        string surface)
    {
        bool isDown = surface == "down";
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(isDown ? CorrectiveMigration : InitialMigration);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        if (isDown)
        {
            await ExecuteAsync(
                connection,
                """DROP INDEX "ux_signups_waitlisted_order_per_help_need" """);
        }

        await ExecuteAsync(
            connection,
            """
            DO $tabruk$
            DECLARE
                target_schema text := current_schema();
            BEGIN
                EXECUTE format(
                    'CREATE OPERATOR CLASS %I.uuid_ops
                     FOR TYPE uuid USING btree AS
                         OPERATOR 1 pg_catalog.< (uuid, uuid),
                         OPERATOR 2 pg_catalog.<= (uuid, uuid),
                         OPERATOR 3 pg_catalog.= (uuid, uuid),
                         OPERATOR 4 pg_catalog.>= (uuid, uuid),
                         OPERATOR 5 pg_catalog.> (uuid, uuid),
                         FUNCTION 1 pg_catalog.uuid_cmp(uuid, uuid)',
                    target_schema);
                EXECUTE format(
                    'CREATE UNIQUE INDEX %I ON %I.signups
                         (organization_id %I.uuid_ops, help_need_id, waitlist_order)
                     WHERE status = 2',
                    'ux_signups_waitlisted_order_per_help_need',
                    target_schema,
                    target_schema);
            END
            $tabruk$;
            """);
        uint hostileIndexOid = await GetWaitlistIndexOidAsync(connection);

        PsqlInvocationResult result = await ExecutePsqlScriptAsync(
            ResolvePsqlExecutablePath(),
            database.ConnectionString,
            isDown
                ? ResolveOwnerDowngradeScriptPath()
                : ResolveOwnerIdempotentScriptPath(),
            database.Schema);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("T8_ATTESTATION_FAILED:object", result.CombinedOutput, StringComparison.Ordinal);
        Assert.Equal(hostileIndexOid, await GetWaitlistIndexOidAsync(connection));
        Assert.Equal(
            isDown ? 1L : 0L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
    }

    [RequiresPostgresFact]
    public async Task ApprovedOwnerCompensationNeverDropsExternalCanonicalIndexCreatedAfterEntryCapture()
    {
        const string ownerApplicationName = "tabruk_t8_external_index_owner_race";
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using NpgsqlConnection setupConnection = new(database.ConnectionString);
        await using NpgsqlConnection attackerConnection = new(database.ConnectionString);
        await using NpgsqlConnection observerConnection = new(database.ConnectionString);
        await setupConnection.OpenAsync();
        await attackerConnection.OpenAsync();
        await observerConnection.OpenAsync();
        await AcquireGlobalDefaultAclLockAsync(setupConnection);
        string globalDefaultsBefore = await GetGlobalDefaultAclFingerprintAsync(setupConnection);
        long forbiddenGlobalDefaultsBefore =
            await GetForbiddenEffectiveDefaultAclCountAsync(setupConnection, "global");
        try
        {
            await using TabrukDbContext context = database.CreateMigrationContext();
            await context.Database.MigrateAsync(InitialMigration);
            await HardenGlobalDefaultPrivilegesAsync(setupConnection);
            await InstallExternalIndexOwnerRacePauseAsync(setupConnection, ownerApplicationName);

            PsqlProcessInvocation? ownerInvocation = null;
            try
            {
                await ExecuteAsync(
                    setupConnection,
                    $"SELECT pg_advisory_lock({ExternalIndexOwnerRaceAdvisoryLock})");
                ownerInvocation = StartPsqlScript(
                    ResolvePsqlExecutablePath(),
                    database.ConnectionString,
                    ResolveOwnerIdempotentScriptPath(),
                    database.Schema,
                    optionsOverride:
                        $"-c tabruk.target_schema={database.Schema} -c search_path={database.Schema} -c tabruk.t8_force_final_attestation_failure=on",
                    applicationName: ownerApplicationName);
                await WaitForAdvisoryLockAsync(
                    observerConnection,
                    ownerApplicationName,
                    ownerInvocation.Completion,
                    TimeSpan.FromSeconds(15));

                await ExecuteAsync(
                    attackerConnection,
                    """
                    CREATE UNIQUE INDEX "ux_signups_waitlisted_order_per_help_need"
                        ON "signups" ("organization_id", "help_need_id", "waitlist_order")
                        WHERE "status" = 2
                    """);
                uint foreignIndexOid = await GetWaitlistIndexOidAsync(attackerConnection);

                await ExecuteAsync(
                    setupConnection,
                    $"SELECT pg_advisory_unlock({ExternalIndexOwnerRaceAdvisoryLock})");
                PsqlInvocationResult result = await ownerInvocation.Completion;

                Assert.NotEqual(0, result.ExitCode);
                Assert.Equal(foreignIndexOid, await GetWaitlistIndexOidAsync(attackerConnection));
                Assert.Equal(
                    0L,
                    await ScalarAsync<long>(
                        attackerConnection,
                        """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                        ("migrationId", CorrectiveMigration)));
            }
            finally
            {
                await ExecuteAsync(
                    setupConnection,
                    $"SELECT pg_advisory_unlock({ExternalIndexOwnerRaceAdvisoryLock})");
                await RemoveExternalIndexOwnerRacePauseAsync(setupConnection);
                if (ownerInvocation is not null)
                {
                    await ownerInvocation.DisposeAsync();
                }
            }
        }
        finally
        {
            await RestoreGlobalDefaultPrivilegesAsync(
                setupConnection,
                globalDefaultsBefore,
                forbiddenGlobalDefaultsBefore);
        }
    }

    [RequiresPostgresFact]
    public async Task ApprovedOwnerCompensationPreservesReplacementAfterCapturedOidAndRecovers()
    {
        const string ownerApplicationName = "tabruk_t8_post_capture_replacement";
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(InitialMigration);
        await using NpgsqlConnection setupConnection = new(database.ConnectionString);
        await using NpgsqlConnection attackerConnection = new(database.ConnectionString);
        await using NpgsqlConnection observerConnection = new(database.ConnectionString);
        await setupConnection.OpenAsync();
        await attackerConnection.OpenAsync();
        await observerConnection.OpenAsync();

        PsqlProcessInvocation? ownerInvocation = null;
        try
        {
            await ExecuteAsync(
                setupConnection,
                $"SELECT pg_advisory_lock({PostIndexCaptureRaceAdvisoryLock})");
            ownerInvocation = StartPsqlScript(
                ResolvePsqlExecutablePath(),
                database.ConnectionString,
                ResolveOwnerIdempotentScriptPath(),
                database.Schema,
                optionsOverride:
                    $"-c tabruk.target_schema={database.Schema} -c search_path={database.Schema} -c tabruk.t8_force_final_attestation_failure=on -c tabruk.t8_test_pause_after_index_capture=on",
                applicationName: ownerApplicationName);
            await WaitForAdvisoryLockAsync(
                observerConnection,
                ownerApplicationName,
                ownerInvocation.Completion,
                TimeSpan.FromSeconds(15));

            uint capturedIndexOid = await GetWaitlistIndexOidAsync(attackerConnection);
            await ExecuteAsync(
                attackerConnection,
                """
                DROP INDEX "ux_signups_waitlisted_order_per_help_need";
                CREATE UNIQUE INDEX "ux_signups_waitlisted_order_per_help_need"
                    ON "signups" ("organization_id", "help_need_id", "waitlist_order")
                    WHERE "status" = 2
                """);
            uint foreignIndexOid = await GetWaitlistIndexOidAsync(attackerConnection);
            Assert.NotEqual(capturedIndexOid, foreignIndexOid);

            await ExecuteAsync(
                setupConnection,
                $"SELECT pg_advisory_unlock({PostIndexCaptureRaceAdvisoryLock})");
            PsqlInvocationResult failedResult = await ownerInvocation.Completion;

            Assert.NotEqual(0, failedResult.ExitCode);
            Assert.Contains("T8_COMPENSATION_FAILED", failedResult.CombinedOutput, StringComparison.Ordinal);
            Assert.Equal(foreignIndexOid, await GetWaitlistIndexOidAsync(attackerConnection));
            Assert.Equal(
                0L,
                await ScalarAsync<long>(
                    attackerConnection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));

            PsqlInvocationResult recovery = await ExecutePsqlScriptAsync(
                ResolvePsqlExecutablePath(),
                database.ConnectionString,
                ResolveOwnerIdempotentScriptPath(),
                database.Schema);
            Assert.Equal(0, recovery.ExitCode);
            Assert.Equal(foreignIndexOid, await GetWaitlistIndexOidAsync(attackerConnection));
            Assert.Equal(
                1L,
                await ScalarAsync<long>(
                    attackerConnection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));
        }
        finally
        {
            await ExecuteAsync(
                setupConnection,
                $"SELECT pg_advisory_unlock({PostIndexCaptureRaceAdvisoryLock})");
            if (ownerInvocation is not null)
            {
                await ownerInvocation.DisposeAsync();
            }
        }
    }

    [RequiresPostgresFact]
    public async Task ApprovedOwnerCompensationLocksOutReplacementBetweenVerificationAndDrop()
    {
        const string ownerApplicationName = "tabruk_t8_post_verify_replacement";
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(InitialMigration);
        await using NpgsqlConnection setupConnection = new(database.ConnectionString);
        await using NpgsqlConnection attackerConnection = new(database.ConnectionString);
        await using NpgsqlConnection observerConnection = new(database.ConnectionString);
        await setupConnection.OpenAsync();
        await attackerConnection.OpenAsync();
        await observerConnection.OpenAsync();

        PsqlProcessInvocation? ownerInvocation = null;
        try
        {
            await ExecuteAsync(
                setupConnection,
                $"SELECT pg_advisory_lock({PostCompensationVerifyRaceAdvisoryLock})");
            ownerInvocation = StartPsqlScript(
                ResolvePsqlExecutablePath(),
                database.ConnectionString,
                ResolveOwnerIdempotentScriptPath(),
                database.Schema,
                optionsOverride:
                    $"-c tabruk.target_schema={database.Schema} -c search_path={database.Schema} -c tabruk.t8_force_final_attestation_failure=on -c tabruk.t8_test_pause_after_compensation_verify=on",
                applicationName: ownerApplicationName);
            await WaitForAdvisoryLockAsync(
                observerConnection,
                ownerApplicationName,
                ownerInvocation.Completion,
                TimeSpan.FromSeconds(15));

            Task replacementTask = ExecuteAsync(
                attackerConnection,
                """
                DROP INDEX IF EXISTS "ux_signups_waitlisted_order_per_help_need";
                CREATE UNIQUE INDEX "ux_signups_waitlisted_order_per_help_need"
                    ON "signups" ("organization_id", "help_need_id", "waitlist_order")
                    WHERE "status" = 2
                """);
            await Task.Delay(250);
            Assert.False(replacementTask.IsCompleted);

            await ExecuteAsync(
                setupConnection,
                $"SELECT pg_advisory_unlock({PostCompensationVerifyRaceAdvisoryLock})");
            PsqlInvocationResult failedResult = await ownerInvocation.Completion;
            await replacementTask;
            uint foreignIndexOid = await GetWaitlistIndexOidAsync(attackerConnection);

            Assert.NotEqual(0, failedResult.ExitCode);
            Assert.Equal(
                0L,
                await ScalarAsync<long>(
                    attackerConnection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));

            PsqlInvocationResult recovery = await ExecutePsqlScriptAsync(
                ResolvePsqlExecutablePath(),
                database.ConnectionString,
                ResolveOwnerIdempotentScriptPath(),
                database.Schema);
            Assert.Equal(0, recovery.ExitCode);
            Assert.Equal(foreignIndexOid, await GetWaitlistIndexOidAsync(attackerConnection));
            Assert.Equal(
                1L,
                await ScalarAsync<long>(
                    attackerConnection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));
        }
        finally
        {
            await ExecuteAsync(
                setupConnection,
                $"SELECT pg_advisory_unlock({PostCompensationVerifyRaceAdvisoryLock})");
            if (ownerInvocation is not null)
            {
                await ownerInvocation.DisposeAsync();
            }
        }
    }

    [RequiresPostgresTheory]
    [InlineData("chronology")]
    [InlineData("index")]
    [InlineData("final-attestation")]
    [InlineData("history-insert")]
    public async Task ApprovedOwnerConstraintCompensationPreservesReplacementAcrossCleanupBranches(
        string failureMode)
    {
        const string ownerApplicationName = "tabruk_t8_constraint_replacement";
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using NpgsqlConnection setupConnection = new(database.ConnectionString);
        await using NpgsqlConnection attackerConnection = new(database.ConnectionString);
        await using NpgsqlConnection observerConnection = new(database.ConnectionString);
        await setupConnection.OpenAsync();
        await attackerConnection.OpenAsync();
        await observerConnection.OpenAsync();
        await AcquireGlobalDefaultAclLockAsync(setupConnection);
        string globalDefaultsBefore = await GetGlobalDefaultAclFingerprintAsync(setupConnection);
        long forbiddenGlobalDefaultsBefore =
            await GetForbiddenEffectiveDefaultAclCountAsync(setupConnection, "global");
        try
        {
            await using TabrukDbContext context = database.CreateMigrationContext();
            await context.Database.MigrateAsync(InitialMigration);
            await HardenGlobalDefaultPrivilegesAsync(setupConnection);

            PersistenceSeed? seed = null;
            Guid? invalidSignupId = null;
            if (failureMode is "chronology" or "index")
            {
                seed = await PersistenceSeed.CreateAsync(database);
            }
            if (failureMode == "chronology")
            {
                invalidSignupId = Guid.CreateVersion7();
                await ExecuteAsync(
                    attackerConnection,
                    """
                    INSERT INTO signups
                        (id, organization_id, service_date_id, help_need_id, primary_membership_id, kind,
                         unnamed_participant_count, status, submitted_at, last_transition_at, version)
                    VALUES
                        (@id, @organizationId, @serviceDateId, @helpNeedId, @membershipId, 0, 0, 3,
                         @submittedAt, @lastTransitionAt, 1)
                    """,
                    ("id", invalidSignupId.Value),
                    ("organizationId", seed!.OrganizationId.Value),
                    ("serviceDateId", seed.ServiceDateId.Value),
                    ("helpNeedId", seed.HelpNeedId.Value),
                    ("membershipId", seed.MemberMembershipId.Value),
                    ("submittedAt", seed.Now),
                    ("lastTransitionAt", seed.Now.AddTicks(-1)));
            }
            else if (failureMode == "index")
            {
                await InsertWaitlistedSignupAsync(
                    attackerConnection,
                    seed!,
                    seed!.MemberMembershipId.Value,
                    waitlistOrder: 1);
                await InsertWaitlistedSignupAsync(
                    attackerConnection,
                    seed,
                    seed.ManagerMembershipId.Value,
                    waitlistOrder: 1);
            }

            string forcedFailureOption = failureMode switch
            {
                "final-attestation" => " -c tabruk.t8_force_final_attestation_failure=on",
                "history-insert" => " -c tabruk.t8_force_history_insert_failure=on",
                _ => string.Empty,
            };
            PsqlProcessInvocation? ownerInvocation = null;
            try
            {
                await ExecuteAsync(
                    setupConnection,
                    $"SELECT pg_advisory_lock({PostConstraintCaptureRaceAdvisoryLock})");
                ownerInvocation = StartPsqlScript(
                    ResolvePsqlExecutablePath(),
                    database.ConnectionString,
                    ResolveOwnerIdempotentScriptPath(),
                    database.Schema,
                    optionsOverride:
                        $"-c tabruk.target_schema={database.Schema} -c search_path={database.Schema} -c tabruk.t8_test_pause_after_constraint_capture=on{forcedFailureOption}",
                    applicationName: ownerApplicationName);
                await WaitForAdvisoryLockAsync(
                    observerConnection,
                    ownerApplicationName,
                    ownerInvocation.Completion,
                    TimeSpan.FromSeconds(15));

                uint capturedConstraintOid = await GetChronologyConstraintOidAsync(attackerConnection);
                await ExecuteAsync(
                    attackerConnection,
                    """
                ALTER TABLE "signups"
                    DROP CONSTRAINT "ck_signups_transition_chronology";
                ALTER TABLE "signups"
                    ADD CONSTRAINT "ck_signups_transition_chronology"
                    CHECK ("last_transition_at" IS NULL OR "last_transition_at" >= "submitted_at")
                    NOT VALID
                """);
                uint foreignConstraintOid = await GetChronologyConstraintOidAsync(attackerConnection);
                Assert.NotEqual(capturedConstraintOid, foreignConstraintOid);

                await ExecuteAsync(
                    setupConnection,
                    $"SELECT pg_advisory_unlock({PostConstraintCaptureRaceAdvisoryLock})");
                PsqlInvocationResult failedResult = await ownerInvocation.Completion;

                Assert.NotEqual(0, failedResult.ExitCode);
                Assert.Contains(
                    "T8_COMPENSATION_FAILED",
                    failedResult.CombinedOutput,
                    StringComparison.Ordinal);
                Assert.Equal(foreignConstraintOid, await GetChronologyConstraintOidAsync(attackerConnection));
                Assert.Equal(
                    0L,
                    await ScalarAsync<long>(
                        attackerConnection,
                        """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                        ("migrationId", CorrectiveMigration)));

                if (failureMode == "chronology")
                {
                    await ExecuteAsync(
                        attackerConnection,
                        """
                    UPDATE signups
                    SET last_transition_at = submitted_at
                    WHERE id = @id
                    """,
                        ("id", invalidSignupId!.Value));
                }
                else if (failureMode == "index")
                {
                    await ExecuteAsync(
                        attackerConnection,
                        """
                    UPDATE signups
                    SET waitlist_order = 2
                    WHERE organization_id = @organizationId
                      AND help_need_id = @helpNeedId
                      AND primary_membership_id = @membershipId
                    """,
                        ("organizationId", seed!.OrganizationId.Value),
                        ("helpNeedId", seed.HelpNeedId.Value),
                        ("membershipId", seed.ManagerMembershipId.Value));
                }

                PsqlInvocationResult recovery = await ExecutePsqlScriptAsync(
                    ResolvePsqlExecutablePath(),
                    database.ConnectionString,
                    ResolveOwnerIdempotentScriptPath(),
                    database.Schema);
                Assert.Equal(0, recovery.ExitCode);
                Assert.Equal(foreignConstraintOid, await GetChronologyConstraintOidAsync(attackerConnection));
                Assert.Equal(
                    1L,
                    await ScalarAsync<long>(
                        attackerConnection,
                        """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                        ("migrationId", CorrectiveMigration)));
            }
            finally
            {
                await ExecuteAsync(
                    setupConnection,
                    $"SELECT pg_advisory_unlock({PostConstraintCaptureRaceAdvisoryLock})");
                if (ownerInvocation is not null)
                {
                    await ownerInvocation.DisposeAsync();
                }
            }
        }
        finally
        {
            await RestoreGlobalDefaultPrivilegesAsync(
                setupConnection,
                globalDefaultsBefore,
                forbiddenGlobalDefaultsBefore);
        }
    }

    [RequiresPostgresTheory]
    [InlineData("up")]
    [InlineData("down")]
    public async Task ApprovedOwnerScriptsRejectUnsafePostgresGlobalDefaultsWithoutRewriting(
        string surface)
    {
        bool isDown = surface == "down";
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await AcquireGlobalDefaultAclLockAsync(connection);
        string globalDefaultsBefore = await GetGlobalDefaultAclFingerprintAsync(connection);
        long forbiddenGlobalDefaultsBefore =
            await GetForbiddenEffectiveDefaultAclCountAsync(connection, "global");
        try
        {
            await using TabrukDbContext context = database.CreateMigrationContext();
            await context.Database.MigrateAsync(isDown ? CorrectiveMigration : InitialMigration);
            await HardenGlobalDefaultPrivilegesAsync(connection);
            await ResetDefaultPrivilegesToPostgresDefaultsAsync(connection);

            string hostileGlobalDefaults = await GetGlobalDefaultAclFingerprintAsync(connection);
            Assert.True(await GetForbiddenEffectiveDefaultAclCountAsync(connection, "global") > 0);

            PsqlInvocationResult result = await ExecutePsqlScriptAsync(
                ResolvePsqlExecutablePath(),
                database.ConnectionString,
                isDown
                    ? ResolveOwnerDowngradeScriptPath()
                    : ResolveOwnerIdempotentScriptPath(),
                database.Schema);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("T8_ATTESTATION_FAILED:acl", result.CombinedOutput, StringComparison.Ordinal);
            Assert.Equal(hostileGlobalDefaults, await GetGlobalDefaultAclFingerprintAsync(connection));
            Assert.Equal(
                isDown ? 1L : 0L,
                await ScalarAsync<long>(
                    connection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));
        }
        finally
        {
            await RestoreGlobalDefaultPrivilegesAsync(
                connection,
                globalDefaultsBefore,
                forbiddenGlobalDefaultsBefore);
        }
    }

    [RequiresPostgresTheory]
    [InlineData("up")]
    [InlineData("down")]
    public async Task ApprovedOwnerScriptsHardenHostileTargetDefaultAclRows(string surface)
    {
        bool isDown = surface == "down";
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await AcquireGlobalDefaultAclLockAsync(connection);
        string globalDefaultsBefore = await GetGlobalDefaultAclFingerprintAsync(connection);
        long forbiddenGlobalDefaultsBefore =
            await GetForbiddenEffectiveDefaultAclCountAsync(connection, "global");
        try
        {
            await using TabrukDbContext context = database.CreateMigrationContext();
            await context.Database.MigrateAsync(isDown ? CorrectiveMigration : InitialMigration);
            await HardenGlobalDefaultPrivilegesAsync(connection);
            await ExecuteAsync(
                connection,
                $"""
                ALTER DEFAULT PRIVILEGES IN SCHEMA "{database.Schema}"
                    GRANT EXECUTE ON FUNCTIONS TO tabruk_app;
                """);

            Assert.True(await GetForbiddenEffectiveDefaultAclCountAsync(connection, "target") > 0);

            PsqlInvocationResult result = await ExecutePsqlScriptAsync(
                ResolvePsqlExecutablePath(),
                database.ConnectionString,
                isDown
                    ? ResolveOwnerDowngradeScriptPath()
                    : ResolveOwnerIdempotentScriptPath(),
                database.Schema);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(0L, await GetForbiddenEffectiveDefaultAclCountAsync(connection, "target"));
        }
        finally
        {
            await RestoreGlobalDefaultPrivilegesAsync(
                connection,
                globalDefaultsBefore,
                forbiddenGlobalDefaultsBefore);
        }
    }

    [RequiresPostgresTheory]
    [InlineData("up")]
    [InlineData("down")]
    public async Task ApprovedOwnerScriptsRejectHostileGlobalDefaultAclRowsWithoutRewriting(
        string surface)
    {
        bool isDown = surface == "down";
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await AcquireGlobalDefaultAclLockAsync(connection);
        string globalDefaultsBefore = await GetGlobalDefaultAclFingerprintAsync(connection);
        long forbiddenGlobalDefaultsBefore =
            await GetForbiddenEffectiveDefaultAclCountAsync(connection, "global");
        try
        {
            await using TabrukDbContext context = database.CreateMigrationContext();
            await context.Database.MigrateAsync(isDown ? CorrectiveMigration : InitialMigration);
            await HardenGlobalDefaultPrivilegesAsync(connection);
            await ExecuteAsync(
                connection,
                """
                ALTER DEFAULT PRIVILEGES
                    GRANT SELECT ON TABLES TO tabruk_app
                """);
            string hostileGlobalDefaults = await GetGlobalDefaultAclFingerprintAsync(connection);

            PsqlInvocationResult result = await ExecutePsqlScriptAsync(
                ResolvePsqlExecutablePath(),
                database.ConnectionString,
                isDown
                    ? ResolveOwnerDowngradeScriptPath()
                    : ResolveOwnerIdempotentScriptPath(),
                database.Schema);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("T8_ATTESTATION_FAILED:acl", result.CombinedOutput, StringComparison.Ordinal);
            Assert.Equal(hostileGlobalDefaults, await GetGlobalDefaultAclFingerprintAsync(connection));
            Assert.Equal(
                isDown ? 1L : 0L,
                await ScalarAsync<long>(
                    connection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));
        }
        finally
        {
            await RestoreGlobalDefaultPrivilegesAsync(
                connection,
                globalDefaultsBefore,
                forbiddenGlobalDefaultsBefore);
        }
    }

    [RequiresPostgresFact]
    public async Task ApprovedOwnerDowngradeIsLogicalIdempotentAndReUpgrades()
    {
        string psqlPath = ResolvePsqlExecutablePath();
        string upgradePath = ResolveOwnerIdempotentScriptPath();
        string downgradePath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "HusayniaTabruk.Infrastructure",
            "Migrations",
            OwnerDowngradeScriptFileName);
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext setupContext = database.CreateMigrationContext();
        await setupContext.Database.MigrateAsync(InitialMigration);

        PsqlInvocationResult upgrade = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            upgradePath,
            database.Schema);
        Assert.Equal(0, upgrade.ExitCode);

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        uint indexOid = await GetWaitlistIndexOidAsync(connection);
        uint constraintOid = await GetChronologyConstraintOidAsync(connection);
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));

        PsqlInvocationResult downgrade = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            downgradePath,
            database.Schema);
        Assert.Equal(0, downgrade.ExitCode);
        Assert.Equal(indexOid, await GetWaitlistIndexOidAsync(connection));
        Assert.Equal(constraintOid, await GetChronologyConstraintOidAsync(connection));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
        await AssertCorrectiveCatalogAsync(connection);

        PsqlInvocationResult secondDowngrade = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            downgradePath,
            database.Schema);
        Assert.Equal(0, secondDowngrade.ExitCode);

        PsqlInvocationResult reupgrade = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            upgradePath,
            database.Schema);
        Assert.Equal(0, reupgrade.ExitCode);
        Assert.Equal(indexOid, await GetWaitlistIndexOidAsync(connection));
        Assert.Equal(constraintOid, await GetChronologyConstraintOidAsync(connection));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
    }

    [RequiresPostgresFact]
    public async Task ApprovedOwnerUpSerializesBehindConcurrentOwnerUp()
    {
        const string firstApplicationName = "tabruk_t8_owner_up_up_first";
        const string secondApplicationName = "tabruk_t8_owner_up_up_second";
        string psqlPath = ResolvePsqlExecutablePath();
        string upgradePath = ResolveOwnerIdempotentScriptPath();
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext migrationContext = database.CreateMigrationContext();
        await migrationContext.Database.MigrateAsync(InitialMigration);
        PsqlInvocationResult baselineResult = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            upgradePath,
            database.Schema);
        Assert.True(
            baselineResult.ExitCode == 0,
            $"Owner Up baseline failed.{Environment.NewLine}{baselineResult.CombinedOutput}");
        await using NpgsqlConnection setupConnection = new(database.ConnectionString);
        await using NpgsqlConnection observerConnection = new(database.ConnectionString);
        await using NpgsqlConnection blockerConnection = new(database.ConnectionString);
        await setupConnection.OpenAsync();
        await observerConnection.OpenAsync();
        await blockerConnection.OpenAsync();
        await using NpgsqlTransaction blockerTransaction =
            await blockerConnection.BeginTransactionAsync();
        await using (NpgsqlCommand blockerCommand = new(
                         """LOCK TABLE "__EFMigrationsHistory" IN ACCESS SHARE MODE""",
                         blockerConnection,
                         blockerTransaction))
        {
            await blockerCommand.ExecuteNonQueryAsync();
        }

        PsqlProcessInvocation? firstInvocation = null;
        PsqlProcessInvocation? secondInvocation = null;
        bool blockerCommitted = false;
        try
        {
            firstInvocation = StartPsqlScript(
                psqlPath,
                database.ConnectionString,
                upgradePath,
                database.Schema,
                applicationName: firstApplicationName);
            await WaitForHistoryTableLockAsync(
                observerConnection,
                firstApplicationName,
                firstInvocation.Completion,
                TimeSpan.FromSeconds(10));
            int firstBackendPid = await GetBackendPidAsync(
                observerConnection,
                firstApplicationName);

            secondInvocation = StartPsqlScript(
                psqlPath,
                database.ConnectionString,
                upgradePath,
                database.Schema,
                applicationName: secondApplicationName);
            int secondBackendPid = await WaitForAdvisoryLockAsync(
                observerConnection,
                secondApplicationName,
                secondInvocation.Completion,
                TimeSpan.FromSeconds(10));
            Assert.NotEqual(firstInvocation.ProcessId, secondInvocation.ProcessId);
            Assert.True(secondBackendPid > 0);
            output.WriteLine(
                $"owner-up-vs-up first_os_pid={firstInvocation.ProcessId} first_backend_pid={firstBackendPid} second_os_pid={secondInvocation.ProcessId} second_backend_pid={secondBackendPid}");

            await blockerTransaction.CommitAsync();
            blockerCommitted = true;

            PsqlInvocationResult firstResult = await firstInvocation.Completion;
            PsqlInvocationResult secondResult = await secondInvocation.Completion;
            Assert.True(
                firstResult.ExitCode == 0,
                $"First owner Up failed (OS PID {firstInvocation.ProcessId}).{Environment.NewLine}{firstResult.CombinedOutput}");
            Assert.True(
                secondResult.ExitCode == 0,
                $"Second owner Up failed (OS PID {secondInvocation.ProcessId}, backend PID {secondBackendPid}).{Environment.NewLine}{secondResult.CombinedOutput}");
            Assert.Equal(
                1L,
                await ScalarAsync<long>(
                    observerConnection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));
            await AssertCorrectiveCatalogAsync(observerConnection);
            await WaitForApplicationsToDisconnectAsync(
                observerConnection,
                [firstApplicationName, secondApplicationName],
                TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!blockerCommitted)
            {
                await blockerTransaction.RollbackAsync();
            }
            if (firstInvocation is not null)
            {
                await firstInvocation.DisposeAsync();
            }
            if (secondInvocation is not null)
            {
                await secondInvocation.DisposeAsync();
            }
        }
    }

    [RequiresPostgresFact]
    public async Task ApprovedOwnerDownSerializesBehindConcurrentOwnerUp()
    {
        const string upApplicationName = "tabruk_t8_owner_up_down_up";
        const string downApplicationName = "tabruk_t8_owner_up_down_down";
        string psqlPath = ResolvePsqlExecutablePath();
        string upgradePath = ResolveOwnerIdempotentScriptPath();
        string downgradePath = ResolveOwnerDowngradeScriptPath();
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext migrationContext = database.CreateMigrationContext();
        await migrationContext.Database.MigrateAsync(InitialMigration);
        PsqlInvocationResult baselineResult = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            upgradePath,
            database.Schema);
        Assert.True(
            baselineResult.ExitCode == 0,
            $"Owner Up baseline failed.{Environment.NewLine}{baselineResult.CombinedOutput}");
        await using NpgsqlConnection setupConnection = new(database.ConnectionString);
        await using NpgsqlConnection observerConnection = new(database.ConnectionString);
        await using NpgsqlConnection blockerConnection = new(database.ConnectionString);
        await setupConnection.OpenAsync();
        await observerConnection.OpenAsync();
        await blockerConnection.OpenAsync();
        await using NpgsqlTransaction blockerTransaction =
            await blockerConnection.BeginTransactionAsync();
        await using (NpgsqlCommand blockerCommand = new(
                         """LOCK TABLE "__EFMigrationsHistory" IN ACCESS SHARE MODE""",
                         blockerConnection,
                         blockerTransaction))
        {
            await blockerCommand.ExecuteNonQueryAsync();
        }

        PsqlProcessInvocation? upInvocation = null;
        PsqlProcessInvocation? downInvocation = null;
        bool blockerCommitted = false;
        try
        {
            upInvocation = StartPsqlScript(
                psqlPath,
                database.ConnectionString,
                upgradePath,
                database.Schema,
                applicationName: upApplicationName);
            await WaitForHistoryTableLockAsync(
                observerConnection,
                upApplicationName,
                upInvocation.Completion,
                TimeSpan.FromSeconds(10));
            int upBackendPid = await GetBackendPidAsync(
                observerConnection,
                upApplicationName);

            downInvocation = StartPsqlScript(
                psqlPath,
                database.ConnectionString,
                downgradePath,
                database.Schema,
                applicationName: downApplicationName);
            int downBackendPid = await WaitForAdvisoryLockAsync(
                observerConnection,
                downApplicationName,
                downInvocation.Completion,
                TimeSpan.FromSeconds(10));
            Assert.NotEqual(upInvocation.ProcessId, downInvocation.ProcessId);
            Assert.True(downBackendPid > 0);
            output.WriteLine(
                $"owner-up-vs-down up_os_pid={upInvocation.ProcessId} up_backend_pid={upBackendPid} down_os_pid={downInvocation.ProcessId} down_backend_pid={downBackendPid}");

            await blockerTransaction.CommitAsync();
            blockerCommitted = true;

            PsqlInvocationResult upResult = await upInvocation.Completion;
            PsqlInvocationResult downResult = await downInvocation.Completion;
            Assert.True(
                upResult.ExitCode == 0,
                $"Owner Up failed (OS PID {upInvocation.ProcessId}).{Environment.NewLine}{upResult.CombinedOutput}");
            Assert.True(
                downResult.ExitCode == 0,
                $"Owner Down failed (OS PID {downInvocation.ProcessId}, backend PID {downBackendPid}).{Environment.NewLine}{downResult.CombinedOutput}");
            Assert.Equal(
                0L,
                await ScalarAsync<long>(
                    observerConnection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));
            await AssertCorrectiveCatalogAsync(observerConnection);
            await WaitForApplicationsToDisconnectAsync(
                observerConnection,
                [upApplicationName, downApplicationName],
                TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!blockerCommitted)
            {
                await blockerTransaction.RollbackAsync();
            }
            if (upInvocation is not null)
            {
                await upInvocation.DisposeAsync();
            }
            if (downInvocation is not null)
            {
                await downInvocation.DisposeAsync();
            }
        }
    }

    [RequiresPostgresFact]
    public async Task ApprovedOwnerCompensationFailureRetainsLockUntilDisconnectAndRecovery()
    {
        const string failedApplicationName = "tabruk_t8_owner_compensation_failed";
        const string recoveryApplicationName = "tabruk_t8_owner_compensation_recovery";
        string psqlPath = ResolvePsqlExecutablePath();
        string upgradePath = ResolveOwnerIdempotentScriptPath();
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext setupContext = database.CreateMigrationContext();
        await setupContext.Database.MigrateAsync(InitialMigration);
        await using NpgsqlConnection setupConnection = new(database.ConnectionString);
        await using NpgsqlConnection observerConnection = new(database.ConnectionString);
        await setupConnection.OpenAsync();
        await observerConnection.OpenAsync();
        await InstallCompensationFailureTriggerAsync(setupConnection, failedApplicationName);

        PsqlProcessInvocation? failedInvocation = null;
        PsqlProcessInvocation? recoveryInvocation = null;
        try
        {
            await ExecuteAsync(
                setupConnection,
                $"SELECT pg_advisory_lock({CompensationFailureBoundaryAdvisoryLock})");
            failedInvocation = StartPsqlScript(
                psqlPath,
                database.ConnectionString,
                upgradePath,
                database.Schema,
                optionsOverride:
                    $"-c tabruk.target_schema={database.Schema} -c search_path={database.Schema} -c tabruk.t8_force_final_attestation_failure=on",
                applicationName: failedApplicationName);
            int failedBackendPid = await WaitForCompensationFailureBoundaryAsync(
                observerConnection,
                failedApplicationName,
                failedInvocation.Completion,
                TimeSpan.FromSeconds(15));
            Assert.Equal(
                0L,
                await ScalarAsync<long>(
                    observerConnection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));

            recoveryInvocation = StartPsqlScript(
                psqlPath,
                database.ConnectionString,
                upgradePath,
                database.Schema,
                applicationName: recoveryApplicationName);
            int recoveryBackendPid = await WaitForAdvisoryLockAsync(
                observerConnection,
                recoveryApplicationName,
                recoveryInvocation.Completion,
                TimeSpan.FromSeconds(10));
            Assert.False(recoveryInvocation.Completion.IsCompleted);
            output.WriteLine(
                $"owner-compensation failed_os_pid={failedInvocation.ProcessId} failed_backend_pid={failedBackendPid} recovery_os_pid={recoveryInvocation.ProcessId} recovery_backend_pid={recoveryBackendPid}");

            await ExecuteAsync(
                setupConnection,
                $"SELECT pg_advisory_unlock({CompensationFailureBoundaryAdvisoryLock})");
            PsqlInvocationResult failedResult = await failedInvocation.Completion;
            Assert.NotEqual(0, failedResult.ExitCode);
            Assert.Contains(
                "T8_RESULT=compensation_failed",
                failedResult.CombinedOutput,
                StringComparison.Ordinal);
            Assert.Contains(
                "T8_COMPENSATION_FAILED",
                failedResult.CombinedOutput,
                StringComparison.Ordinal);

            PsqlInvocationResult recoveryResult = await recoveryInvocation.Completion;
            Assert.True(
                recoveryResult.ExitCode == 0,
                $"Recovery owner Up failed (OS PID {recoveryInvocation.ProcessId}, backend PID {recoveryBackendPid}).{Environment.NewLine}{recoveryResult.CombinedOutput}");
            Assert.Equal(
                1L,
                await ScalarAsync<long>(
                    observerConnection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));
            await AssertCorrectiveCatalogAsync(observerConnection);
            await AssertHardenedPrivilegeMatrixAsync(observerConnection);
            await WaitForApplicationsToDisconnectAsync(
                observerConnection,
                [failedApplicationName, recoveryApplicationName],
                TimeSpan.FromSeconds(10));
        }
        finally
        {
            await ExecuteAsync(
                setupConnection,
                $"SELECT pg_advisory_unlock({CompensationFailureBoundaryAdvisoryLock})");
            await RemoveCompensationFailureTriggerAsync(setupConnection);
            if (failedInvocation is not null)
            {
                await failedInvocation.DisposeAsync();
            }
            if (recoveryInvocation is not null)
            {
                await recoveryInvocation.DisposeAsync();
            }
        }
    }

    [RequiresPostgresFact]
    public async Task ApprovedOwnerIdempotentScriptFailsWithoutInitialMigrationHistoryAndWritesNoCorrectiveHistory()
    {
        string psqlPath = ResolvePsqlExecutablePath();
        string scriptPath = ResolveOwnerIdempotentScriptPath();
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE "__EFMigrationsHistory"
            (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
            )
            """);

        PsqlInvocationResult failedRun = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            scriptPath,
            database.Schema);
        Assert.NotEqual(0, failedRun.ExitCode);
        Assert.True(
            failedRun.CombinedOutput.Contains(
                "T8 pre-history attestation failed: partial-initial-inventory.",
                StringComparison.Ordinal),
            failedRun.CombinedOutput);
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
    }

    [RequiresPostgresFact]
    public async Task ApprovedOwnerScriptRejectsMalformedHistoryAndPreservesItWithoutCorrectiveHistory()
    {
        string psqlPath = ResolvePsqlExecutablePath();
        string scriptPath = ResolveOwnerIdempotentScriptPath();
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(InitialMigration);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """ALTER TABLE "__EFMigrationsHistory" ADD COLUMN "hostile_payload" text""");
        uint originalOid = await ScalarAsync<uint>(
            connection,
            """SELECT '"__EFMigrationsHistory"'::regclass::oid""");
        string originalStructure = await GetHistoryStructureFingerprintAsync(connection);

        PsqlInvocationResult failedRun = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            scriptPath,
            database.Schema);

        Assert.NotEqual(0, failedRun.ExitCode);
        Assert.Contains(
            "T8 pre-history attestation failed: history-structure-mismatch.",
            failedRun.CombinedOutput,
            StringComparison.Ordinal);
        Assert.Equal(
            originalOid,
            await ScalarAsync<uint>(
                connection,
                """SELECT '"__EFMigrationsHistory"'::regclass::oid"""));
        Assert.Equal(originalStructure, await GetHistoryStructureFingerprintAsync(connection));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
        await AssertCorrectiveObjectsAbsentAsync(connection);
    }

    [RequiresPostgresTheory]
    [InlineData("table")]
    [InlineData("sequence")]
    public async Task ApprovedOwnerScriptRejectsHostileManagedObjectOwnerAndPreservesIt(
        string objectKind)
    {
        string psqlPath = ResolvePsqlExecutablePath();
        string scriptPath = ResolveOwnerIdempotentScriptPath();
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(InitialMigration);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            $"""
            DROP ROLE IF EXISTS "{HostileOwnerRole}";
            CREATE ROLE "{HostileOwnerRole}" NOLOGIN;
            """);
        string relationName = objectKind switch
        {
            "table" => "users",
            "sequence" => "identity_user_claims_Id_seq",
            _ => throw new ArgumentOutOfRangeException(
                nameof(objectKind),
                objectKind,
                "Unsupported hostile ownership probe."),
        };
        string alterOwnerSql = objectKind switch
        {
            "table" => $"""ALTER TABLE "users" OWNER TO "{HostileOwnerRole}" """,
            "sequence" =>
                $"""ALTER TABLE "identity_user_claims" OWNER TO "{HostileOwnerRole}" """,
            _ => throw new ArgumentOutOfRangeException(
                nameof(objectKind),
                objectKind,
                "Unsupported hostile ownership probe."),
        };

        try
        {
            await ExecuteAsync(connection, alterOwnerSql);

            PsqlInvocationResult failedRun = await ExecutePsqlScriptAsync(
                psqlPath,
                database.ConnectionString,
                scriptPath,
                database.Schema);

            Assert.NotEqual(0, failedRun.ExitCode);
            Assert.Contains(
                "T8 pre-history attestation failed: initial-owner-mismatch.",
                failedRun.CombinedOutput,
                StringComparison.Ordinal);
            Assert.Equal(
                HostileOwnerRole,
                await ScalarAsync<string>(
                    connection,
                    """
                    SELECT owner_role.rolname
                    FROM pg_class AS relation
                    JOIN pg_namespace AS schema_definition
                      ON schema_definition.oid = relation.relnamespace
                    JOIN pg_roles AS owner_role
                      ON owner_role.oid = relation.relowner
                    WHERE schema_definition.nspname = current_schema()
                      AND relation.relname = @relationName
                    """,
                    ("relationName", relationName)));
            Assert.Equal(
                0L,
                await ScalarAsync<long>(
                    connection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));
            await AssertCorrectiveObjectsAbsentAsync(connection);
        }
        finally
        {
            await ExecuteAsync(
                connection,
                objectKind == "table"
                    ? $"""
                      ALTER TABLE "users" OWNER TO CURRENT_USER;
                      DROP ROLE "{HostileOwnerRole}";
                      """
                    : $"""
                      ALTER TABLE "identity_user_claims" OWNER TO CURRENT_USER;
                      DROP ROLE "{HostileOwnerRole}";
                      """);
        }
    }

    [RequiresPostgresFact]
    public async Task ApprovedOwnerIdempotentScriptRequiresMatchingExplicitSchemaAndDoesNotHardenAlternateSchema()
    {
        string psqlPath = ResolvePsqlExecutablePath();
        string scriptPath = ResolveOwnerIdempotentScriptPath();
        await using PostgresTestDatabase targetDatabase = await CreateDatabaseAsync(applyMigrations: false);
        string alternateSchema = $"tabruk_it_{Guid.NewGuid():N}";
        try
        {
            await using (NpgsqlConnection setupConnection = new(targetDatabase.ConnectionString))
            {
                await setupConnection.OpenAsync();
                await ExecuteAsync(setupConnection, $"""CREATE SCHEMA "{alternateSchema}" """);
            }

            await using (TabrukDbContext targetContext = targetDatabase.CreateContext())
            {
                await targetContext.Database.MigrateAsync(InitialMigration);
            }

            NpgsqlConnectionStringBuilder alternateBuilder = new(targetDatabase.ConnectionString)
            {
                SearchPath = alternateSchema,
                Options = $"-c tabruk.target_schema={alternateSchema} -c tabruk.disposable_ef=on",
            };

            await using (TabrukDbContext alternateContext = TabrukDbContextOptions.Create(alternateBuilder.ConnectionString))
            {
                await alternateContext.Database.MigrateAsync(InitialMigration);
            }

            PsqlInvocationResult mismatchedRun = await ExecutePsqlScriptAsync(
                psqlPath,
                targetDatabase.ConnectionString,
                scriptPath,
                targetDatabase.Schema,
                searchPath: alternateSchema);
            Assert.Equal(0, mismatchedRun.ExitCode);
            Assert.Contains("T8_RESULT=success", mismatchedRun.CombinedOutput, StringComparison.Ordinal);

            await using NpgsqlConnection targetConnection = new(targetDatabase.ConnectionString);
            await targetConnection.OpenAsync();
            await using NpgsqlConnection alternateConnection = new(alternateBuilder.ConnectionString);
            await alternateConnection.OpenAsync();

            Assert.Equal(
                1L,
                await ScalarAsync<long>(
                    targetConnection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));
            Assert.Equal(
                0L,
                await ScalarAsync<long>(
                    alternateConnection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));

            PsqlInvocationResult successfulRun = await ExecutePsqlScriptAsync(
                psqlPath,
                targetDatabase.ConnectionString,
                scriptPath,
                targetDatabase.Schema);
            Assert.True(
                successfulRun.ExitCode == 0,
                $"Explicit schema-pinned owner idempotent corrective script run failed.{Environment.NewLine}{successfulRun.CombinedOutput}");

            Assert.Equal(
                1L,
                await ScalarAsync<long>(
                    targetConnection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));
            Assert.Equal(
                0L,
                await ScalarAsync<long>(
                    alternateConnection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));
            Assert.False(
                await HasTablePrivilegeAsync(
                    targetConnection,
                    PostgresLeastPrivilegeCatalog.MigrationHistoryTable,
                    "UPDATE"));
            Assert.True(
                await HasTablePrivilegeAsync(
                    alternateConnection,
                    PostgresLeastPrivilegeCatalog.MigrationHistoryTable,
                    "UPDATE"));
            await AssertCorrectiveCatalogAsync(targetConnection);
        }
        finally
        {
            await using NpgsqlConnection cleanupConnection = new(targetDatabase.ConnectionString);
            await cleanupConnection.OpenAsync();
            await ExecuteAsync(cleanupConnection, $"""DROP SCHEMA IF EXISTS "{alternateSchema}" CASCADE""");
        }
    }

    [RequiresPostgresFact]
    public async Task ApprovedOwnerScriptRejectsHostileSameNameIndexAndPreservesIt()
    {
        string psqlPath = ResolvePsqlExecutablePath();
        string scriptPath = ResolveOwnerIdempotentScriptPath();
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(InitialMigration);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            CREATE INDEX "ux_signups_waitlisted_order_per_help_need"
                ON "signups" ("organization_id")
            """);
        await SeedHostilePublicPrivilegesAsync(connection);
        string hostileDefinition = await GetWaitlistIndexDefinitionAsync(connection);

        PsqlInvocationResult failedRun = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            scriptPath,
            database.Schema);

        Assert.NotEqual(0, failedRun.ExitCode);
        Assert.Contains(
            "T8 object definition mismatch: object=ux_signups_waitlisted_order_per_help_need; category=state.",
            failedRun.CombinedOutput,
            StringComparison.Ordinal);
        Assert.Equal(hostileDefinition, await GetWaitlistIndexDefinitionAsync(connection));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
        await AssertHardenedPrivilegeMatrixAsync(connection);
    }

    [RequiresPostgresFact]
    public async Task ApprovedOwnerScriptRejectsHostileSameNameConstraintAndPreservesIt()
    {
        string psqlPath = ResolvePsqlExecutablePath();
        string scriptPath = ResolveOwnerIdempotentScriptPath();
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(InitialMigration);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            ALTER TABLE "signups"
                ADD CONSTRAINT "ck_signups_transition_chronology"
                CHECK ("last_transition_at" IS NULL OR TRUE)
            """);
        string hostileDefinition = await GetChronologyConstraintDefinitionAsync(connection);

        PsqlInvocationResult failedRun = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            scriptPath,
            database.Schema);

        Assert.NotEqual(0, failedRun.ExitCode);
        Assert.Contains(
            "T8 object definition mismatch: object=ck_signups_transition_chronology; category=expression.",
            failedRun.CombinedOutput,
            StringComparison.Ordinal);
        Assert.Equal(hostileDefinition, await GetChronologyConstraintDefinitionAsync(connection));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
        await AssertWaitlistIndexStateAsync(
            connection,
            expectedPresent: false,
            expectedValid: false);
    }

    [RequiresPostgresFact]
    public async Task ApprovedOwnerScriptReusesCorrectUnvalidatedObjectsAndValidatesConstraint()
    {
        string psqlPath = ResolvePsqlExecutablePath();
        string scriptPath = ResolveOwnerIdempotentScriptPath();
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(InitialMigration);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await AcquireGlobalDefaultAclLockAsync(connection);
        string globalDefaultsBefore = await GetGlobalDefaultAclFingerprintAsync(connection);
        long forbiddenGlobalDefaultsBefore =
            await GetForbiddenEffectiveDefaultAclCountAsync(connection, "global");
        await HardenGlobalDefaultPrivilegesAsync(connection);
        Assert.Equal(0L, await GetForbiddenEffectiveDefaultAclCountAsync(connection, "global"));
        try
        {
            await CreateCorrectiveObjectsAsync(connection, validateConstraint: false);
            uint indexOid = await GetWaitlistIndexOidAsync(connection);
            uint constraintOid = await GetChronologyConstraintOidAsync(connection);

            PsqlInvocationResult run = await ExecutePsqlScriptAsync(
                psqlPath,
                database.ConnectionString,
                scriptPath,
                database.Schema);

            Assert.True(
                run.ExitCode == 0,
                $"Owner script failed for correct existing objects.{Environment.NewLine}{run.CombinedOutput}");
            Assert.Equal(indexOid, await GetWaitlistIndexOidAsync(connection));
            Assert.Equal(constraintOid, await GetChronologyConstraintOidAsync(connection));
            await AssertCorrectiveCatalogAsync(connection);
            Assert.Equal(
                1L,
                await ScalarAsync<long>(
                    connection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));
        }
        finally
        {
            await RestoreGlobalDefaultPrivilegesAsync(
                connection,
                globalDefaultsBefore,
                forbiddenGlobalDefaultsBefore);
        }
    }

    [RequiresPostgresFact]
    public async Task ApprovedOwnerScriptSchemaMatrixRejectsInvalidTargetsBeforeHistoryMutation()
    {
        string psqlPath = ResolvePsqlExecutablePath();
        string scriptPath = ResolveOwnerIdempotentScriptPath();
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(InitialMigration);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();

        string[] invalidOptions =
        [
            $"-c search_path={database.Schema}",
            $"-c tabruk.target_schema={database.Schema}",
            $"-c tabruk.target_schema={database.Schema} -c search_path={database.Schema},public",
            $"-c tabruk.target_schema={database.Schema} -c search_path=public",
            "-c tabruk.target_schema=tabruk_unresolved -c search_path=tabruk_unresolved",
        ];
        foreach (string options in invalidOptions)
        {
            PsqlInvocationResult run = await ExecutePsqlScriptAsync(
                psqlPath,
                database.ConnectionString,
                scriptPath,
                database.Schema,
                optionsOverride: options);
            Assert.Equal(0, run.ExitCode);
            Assert.Contains("T8_RESULT=success", run.CombinedOutput, StringComparison.Ordinal);
            Assert.Equal(
                1L,
                await ScalarAsync<long>(
                    connection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));
        }
    }

    [RequiresPostgresFact]
    public async Task ApprovedOwnerIdempotentScriptRetriesAfterChronologyRepairWithoutWritingHistoryEarly()
    {
        string psqlPath = ResolvePsqlExecutablePath();
        string scriptPath = ResolveOwnerIdempotentScriptPath();
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(InitialMigration);
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        Guid invalidSignupId = Guid.CreateVersion7();
        await ExecuteAsync(
            connection,
            """
            INSERT INTO signups
                (id, organization_id, service_date_id, help_need_id, primary_membership_id, kind,
                 unnamed_participant_count, status, submitted_at, last_transition_at, version)
            VALUES
                (@id, @organizationId, @serviceDateId, @helpNeedId, @membershipId, 0, 0, 3,
                 @submittedAt, @lastTransitionAt, 1)
            """,
            ("id", invalidSignupId),
            ("organizationId", seed.OrganizationId.Value),
            ("serviceDateId", seed.ServiceDateId.Value),
            ("helpNeedId", seed.HelpNeedId.Value),
            ("membershipId", seed.MemberMembershipId.Value),
            ("submittedAt", seed.Now),
            ("lastTransitionAt", seed.Now.AddTicks(-1)));

        PsqlInvocationResult failedRun = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            scriptPath,
            database.Schema);
        Assert.NotEqual(0, failedRun.ExitCode);
        Assert.Contains(
            "ck_signups_transition_chronology",
            failedRun.CombinedOutput,
            StringComparison.Ordinal);

        await using TabrukDbContext failedStateContext = database.CreateMigrationContext();
        Assert.Single(await failedStateContext.Database.GetAppliedMigrationsAsync());
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
        await AssertHardenedPrivilegeMatrixAsync(connection);
        await AssertWaitlistIndexStateAsync(connection, expectedPresent: false, expectedValid: false);
        await AssertChronologyConstraintStateAsync(
            connection,
            expectedPresent: false,
            expectedValidated: false);

        await ExecuteAsync(
            connection,
            """
            UPDATE signups
            SET last_transition_at = submitted_at
            WHERE id = @id
            """,
            ("id", invalidSignupId));

        PsqlInvocationResult retryRun = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            scriptPath,
            database.Schema);
        Assert.True(
            retryRun.ExitCode == 0,
            $"Retry owner idempotent corrective script run failed.{Environment.NewLine}{retryRun.CombinedOutput}");

        PsqlInvocationResult reexecutionRun = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            scriptPath,
            database.Schema);
        Assert.True(
            reexecutionRun.ExitCode == 0,
            $"Owner idempotent corrective script re-execution failed after completion.{Environment.NewLine}{reexecutionRun.CombinedOutput}");

        await using TabrukDbContext verificationContext = database.CreateMigrationContext();
        Assert.Equal(2, (await verificationContext.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
        await AssertCorrectiveCatalogAsync(connection);
        await AssertHardenedPrivilegeMatrixAsync(connection);
        await AssertApplicationRoleMutationDeniedAsync(
            database,
            """UPDATE "__EFMigrationsHistory" SET "ProductVersion" = "ProductVersion" WHERE FALSE""");
    }

    [RequiresPostgresTheory]
    [InlineData("tabruk.t8_force_final_attestation_failure")]
    [InlineData("tabruk.t8_force_history_insert_failure")]
    public async Task ApprovedOwnerScriptCompensatesForcedFinalizeFailures(string failureSetting)
    {
        string psqlPath = ResolvePsqlExecutablePath();
        string scriptPath = ResolveOwnerIdempotentScriptPath();
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(InitialMigration);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();

        PsqlInvocationResult failedRun = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            scriptPath,
            database.Schema,
            optionsOverride:
                $"-c tabruk.target_schema={database.Schema} -c search_path={database.Schema} -c {failureSetting}=on");

        Assert.NotEqual(0, failedRun.ExitCode);
        Assert.Contains("T8_RESULT=failed", failedRun.CombinedOutput, StringComparison.Ordinal);
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
        await AssertCorrectiveObjectsAbsentAsync(connection);
        await AssertHardenedPrivilegeMatrixAsync(connection);

        PsqlInvocationResult retryRun = await ExecutePsqlScriptAsync(
            psqlPath,
            database.ConnectionString,
            scriptPath,
            database.Schema);
        Assert.Equal(0, retryRun.ExitCode);
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connection,
                """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                ("migrationId", CorrectiveMigration)));
    }

    [RequiresPostgresFact]
    public async Task SchemaEnforcesOperationalConstraintsIndexesAndAuditPermissions()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using TabrukDbContext context = database.CreateMigrationContext();

        context.Signups.Add(
            new SignupEntity
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = seed.OrganizationId.Value,
                ServiceDateId = seed.ServiceDateId.Value,
                HelpNeedId = seed.HelpNeedId.Value,
                PrimaryMembershipId = seed.MemberMembershipId.Value,
                Kind = (short)SignupKind.Individual,
                UnnamedParticipantCount = 0,
                Status = (short)SignupStatus.Pending,
                SubmittedAt = seed.Now,
                Version = 0,
            });
        await context.SaveChangesAsync();

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await AssertHardenedPrivilegeMatrixAsync(connection);
        Assert.Contains(
            "PRIMARY KEY (organization_id, membership_id, key)",
            await ScalarAsync<string>(
                connection,
                """
                SELECT pg_get_constraintdef(constraint_definition.oid)
                FROM pg_constraint AS constraint_definition
                JOIN pg_class AS relation ON relation.oid = constraint_definition.conrelid
                WHERE relation.relname = 'idempotency_records'
                  AND constraint_definition.contype = 'p'
                """));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                  AND table_name = 'signup_member_participants'
                  AND column_name ILIKE '%name%'
                """));
        Assert.True(
            await ScalarAsync<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM pg_indexes
                WHERE schemaname = current_schema()
                  AND indexname = 'ux_signups_active_primary_per_help_need'
                """) == 1L);
        Assert.True(
            await ScalarAsync<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM information_schema.table_constraints
                WHERE table_schema = current_schema()
                  AND table_name = 'date_threads'
                  AND constraint_name = 'ck_date_threads_lock_state'
                """) == 1L);

        PostgresException duplicateDateThread = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(
                connection,
                """
                INSERT INTO signups
                    (id, organization_id, service_date_id, help_need_id, primary_membership_id, kind,
                     unnamed_participant_count, status, submitted_at, version)
                VALUES
                    (@id, @organizationId, @serviceDateId, @helpNeedId, @memberId, 0, 0, 0, @submittedAt, 0)
                """,
                ("id", Guid.CreateVersion7()),
                ("organizationId", seed.OrganizationId.Value),
                ("serviceDateId", seed.ServiceDateId.Value),
                ("helpNeedId", seed.HelpNeedId.Value),
                ("memberId", seed.MemberMembershipId.Value),
                ("submittedAt", seed.Now)));
        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(
                connection,
                "UPDATE signups SET service_date_id = @serviceDateId",
                ("serviceDateId", Guid.CreateVersion7())));
        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(
                connection,
                "UPDATE help_needs SET waitlist_order_high_water = -1 WHERE id = @id",
                ("id", seed.HelpNeedId.Value)));
        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(
                connection,
                "UPDATE date_threads SET status = 1, locked_at = NULL WHERE id = @id",
                ("id", seed.ThreadId.Value)));
        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(
                connection,
                """
                INSERT INTO role_assignments
                    (id, organization_id, membership_id, role, assigned_by_membership_id, assigned_at)
                VALUES
                    (@id, @organizationId, @membershipId, 0, @assignedByMembershipId, @assignedAt)
                """,
                ("id", Guid.CreateVersion7()),
                ("organizationId", seed.OrganizationId.Value),
                ("membershipId", seed.ManagerMembershipId.Value),
                ("assignedByMembershipId", seed.SecondAdministratorMembershipId.Value),
                ("assignedAt", seed.Now)));
        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(
                connection,
                """
                INSERT INTO role_change_requests
                    (id, organization_id, target_membership_id, action, proposer_membership_id, reason,
                     proposed_at, expires_at, status)
                VALUES
                    (@id, @organizationId, @targetMembershipId, 0, @proposerMembershipId, 'invalid state',
                     @proposedAt, @expiresAt, 1)
                """,
                ("id", Guid.CreateVersion7()),
                ("organizationId", seed.OrganizationId.Value),
                ("targetMembershipId", seed.MemberMembershipId.Value),
                ("proposerMembershipId", seed.ManagerMembershipId.Value),
                ("proposedAt", seed.Now),
                ("expiresAt", seed.Now.AddHours(24))));
        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(
                connection,
                """
                INSERT INTO date_threads (id, organization_id, service_date_id, status, version)
                VALUES (@id, @organizationId, @serviceDateId, 0, 0)
                """,
                ("id", Guid.CreateVersion7()),
                ("organizationId", seed.OrganizationId.Value),
                ("serviceDateId", seed.ServiceDateId.Value)));
        Assert.Equal("23505", duplicateDateThread.SqlState);
        PostgresException invalidDateThreadForeignKey = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(
                connection,
                """
                INSERT INTO date_threads (id, organization_id, service_date_id, status, version)
                VALUES (@id, @organizationId, @serviceDateId, 0, 0)
                """,
                ("id", Guid.CreateVersion7()),
                ("organizationId", seed.OrganizationId.Value),
                ("serviceDateId", Guid.CreateVersion7())));
        Assert.Equal("23503", invalidDateThreadForeignKey.SqlState);

        Guid userId = await ScalarAsync<Guid>(connection, "SELECT id FROM users ORDER BY id LIMIT 1");
        await ExecuteAsApplicationRoleAsync(
            database,
            """
            INSERT INTO identity_user_claims ("UserId", "ClaimType", "ClaimValue")
            VALUES (@userId, 'permission', 'identity-sequence')
            """,
            ("userId", userId));
        await ExecuteAsApplicationRoleAsync(
            database,
            """
            INSERT INTO audit_events
                (id, organization_id, actor_membership_id, action, resource_type, resource_id, reason, purpose,
                 correlation_id, occurred_at)
            VALUES
                (@id, @organizationId, @actorMembershipId, 'permission_test', 'test', 'audit', 'test insert',
                 'integration', 'audit-permission', @occurredAt)
            """,
            ("id", Guid.CreateVersion7()),
            ("organizationId", seed.OrganizationId.Value),
            ("actorMembershipId", seed.ManagerMembershipId.Value),
            ("occurredAt", seed.Now));
        await ExecuteAsApplicationRoleAsync(
            database,
            """
            INSERT INTO privileged_access_events
                (id, organization_id, actor_membership_id, resource_type, resource_id, reason, purpose, case_id,
                 page_hash, occurred_at)
            VALUES
                (@id, @organizationId, @actorMembershipId, 'thread', 'test', 'test insert', 0, 'case-1',
                 @pageHash, @occurredAt)
            """,
            ("id", Guid.CreateVersion7()),
            ("organizationId", seed.OrganizationId.Value),
            ("actorMembershipId", seed.ManagerMembershipId.Value),
            ("pageHash", new string('a', 64)),
            ("occurredAt", seed.Now));
        await AssertApplicationRoleMutationDeniedAsync(
            database,
            "UPDATE audit_events SET action = 'denied' WHERE FALSE");
        await AssertApplicationRoleMutationDeniedAsync(
            database,
            "DELETE FROM audit_events WHERE FALSE");
        await AssertApplicationRoleMutationDeniedAsync(
            database,
            "UPDATE privileged_access_events SET reason = 'denied' WHERE FALSE");
        await AssertApplicationRoleMutationDeniedAsync(
            database,
            "DELETE FROM privileged_access_events WHERE FALSE");
    }

    [RequiresPostgresFact]
    public async Task CorrectiveSignupConstraintsEnforceWaitlistUniquenessAndTransitionChronology()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        HelpNeedId otherHelpNeedId = HelpNeedId.New();
        await using (TabrukDbContext setup = database.CreateMigrationContext())
        {
            setup.HelpNeeds.Add(
                new HelpNeedEntity
                {
                    Id = otherHelpNeedId.Value,
                    OrganizationId = seed.OrganizationId.Value,
                    ServiceDateId = seed.ServiceDateId.Value,
                    Category = (short)HelpCategory.Cleanup,
                    Instructions = "Clean the hall.",
                    Capacity = 5,
                    Status = (short)HelpNeedStatus.Open,
                    Version = 0,
                    SignupVersion = 0,
                    WaitlistOrderHighWater = 0,
                });
            await setup.SaveChangesAsync();
        }

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await AssertCorrectiveCatalogAsync(connection);

        await InsertWaitlistedSignupAsync(
            connection,
            seed,
            seed.MemberMembershipId.Value,
            waitlistOrder: 1);
        PostgresException duplicateWaitlistPosition = await Assert.ThrowsAsync<PostgresException>(
            () => InsertWaitlistedSignupAsync(
                connection,
                seed,
                seed.ManagerMembershipId.Value,
                waitlistOrder: 1));
        Assert.Equal("23505", duplicateWaitlistPosition.SqlState);
        Assert.Equal(
            "ux_signups_waitlisted_order_per_help_need",
            duplicateWaitlistPosition.ConstraintName);

        await ExecuteAsync(
            connection,
            """
            INSERT INTO signups
                (id, organization_id, service_date_id, help_need_id, primary_membership_id, kind,
                 unnamed_participant_count, status, submitted_at, last_transition_at, waitlist_order, version)
            VALUES
                (@id, @organizationId, @serviceDateId, @helpNeedId, @membershipId, 0, 0, 2,
                 @submittedAt, @lastTransitionAt, 1, 1)
            """,
            ("id", Guid.CreateVersion7()),
            ("organizationId", seed.OrganizationId.Value),
            ("serviceDateId", seed.ServiceDateId.Value),
            ("helpNeedId", otherHelpNeedId.Value),
            ("membershipId", seed.ManagerMembershipId.Value),
            ("submittedAt", seed.Now),
            ("lastTransitionAt", seed.Now));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO signups
                (id, organization_id, service_date_id, help_need_id, primary_membership_id, kind,
                 unnamed_participant_count, status, submitted_at, version)
            VALUES
                (@id, @organizationId, @serviceDateId, @helpNeedId, @membershipId, 0, 0, 0,
                 @submittedAt, 0)
            """,
            ("id", Guid.CreateVersion7()),
            ("organizationId", seed.OrganizationId.Value),
            ("serviceDateId", seed.ServiceDateId.Value),
            ("helpNeedId", seed.HelpNeedId.Value),
            ("membershipId", seed.SecondAdministratorMembershipId.Value),
            ("submittedAt", seed.Now));
        await InsertTerminalSignupAsync(connection, seed, status: 3, seed.Now, seed.Now);
        await InsertTerminalSignupAsync(connection, seed, status: 4, seed.Now, seed.Now.AddTicks(1));

        PostgresException invalidChronology = await Assert.ThrowsAsync<PostgresException>(
            () => InsertTerminalSignupAsync(
                connection,
                seed,
                status: 5,
                seed.Now,
                seed.Now.AddTicks(-1)));
        Assert.Equal("23514", invalidChronology.SqlState);
        Assert.Equal("ck_signups_transition_chronology", invalidChronology.ConstraintName);
    }

    [RequiresPostgresFact]
    public async Task SignupLabelSchemaIsNullableBoundedAndSeparateFromParticipantNameColumns()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        Guid signupId = Guid.CreateVersion7();
        await using (TabrukDbContext setup = database.CreateMigrationContext())
        {
            setup.Signups.Add(
                new SignupEntity
                {
                    Id = signupId,
                    OrganizationId = seed.OrganizationId.Value,
                    ServiceDateId = seed.ServiceDateId.Value,
                    HelpNeedId = seed.HelpNeedId.Value,
                    PrimaryMembershipId = seed.MemberMembershipId.Value,
                    Kind = (short)SignupKind.Household,
                    UnnamedParticipantCount = 1,
                    Status = (short)SignupStatus.Pending,
                    SubmittedAt = seed.Now,
                    Version = 0,
                });
            await setup.SaveChangesAsync();
        }

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();

        Assert.Equal(
            "YES",
            await ScalarAsync<string>(
                connection,
                """
                SELECT is_nullable
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                  AND table_name = 'signups'
                  AND column_name = 'label'
                """));
        Assert.Equal(
            ApplicationLimits.MaximumSignupLabelUnicodeScalars,
            await ScalarAsync<int>(
                connection,
                """
                SELECT character_maximum_length
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                  AND table_name = 'signups'
                  AND column_name = 'label'
                """));

        string unicodeConstraint = await ScalarAsync<string>(
            connection,
            """
            SELECT pg_get_constraintdef(constraint_definition.oid)
            FROM pg_constraint AS constraint_definition
            JOIN pg_class AS relation ON relation.oid = constraint_definition.conrelid
            WHERE relation.relname = 'signups'
              AND constraint_definition.conname = 'ck_signups_label_unicode_scalars'
            """);
        Assert.Contains("char_length", unicodeConstraint);
        Assert.Contains(
            ApplicationLimits.MaximumSignupLabelUnicodeScalars.ToString(CultureInfo.InvariantCulture),
            unicodeConstraint);

        string utf8Constraint = await ScalarAsync<string>(
            connection,
            """
            SELECT pg_get_constraintdef(constraint_definition.oid)
            FROM pg_constraint AS constraint_definition
            JOIN pg_class AS relation ON relation.oid = constraint_definition.conrelid
            WHERE relation.relname = 'signups'
              AND constraint_definition.conname = 'ck_signups_label_utf8_bytes'
            """);
        Assert.Contains("convert_to", utf8Constraint);
        Assert.Contains("UTF8", utf8Constraint);
        Assert.Contains(
            ApplicationLimits.MaximumSignupLabelUtf8Bytes.ToString(CultureInfo.InvariantCulture),
            utf8Constraint);

        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                  AND table_name = 'signups'
                  AND column_name ~* '(^|_)name($|_)'
                """));

        string maximumLabel = string.Concat(
            Enumerable.Repeat("😀", ApplicationLimits.MaximumSignupLabelUnicodeScalars));
        await ExecuteAsync(
            connection,
            "UPDATE signups SET label = @label WHERE id = @id",
            ("label", maximumLabel),
            ("id", signupId));

        await using (TabrukDbContext verification = database.CreateMigrationContext())
        {
            SignupEntity signup = await verification.Signups.SingleAsync(
                candidate => candidate.Id == signupId);
            Assert.Equal(maximumLabel, signup.Label);
        }

        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(
                connection,
                "UPDATE signups SET label = @label WHERE id = @id",
                ("label", new string('x', ApplicationLimits.MaximumSignupLabelUnicodeScalars + 1)),
                ("id", signupId)));
    }

    private static async Task AssertFactoryRoutingFailuresAsync(
        PostgresTestDatabase database,
        int expectedHistoryCount)
    {
        InvalidOperationException missingSearchPath = Assert.Throws<InvalidOperationException>(
            () => TabrukDbContextOptions.Create(
                database.BuildMigrationConnectionString(
                    searchPath: null,
                    options: $"-c tabruk.target_schema={database.Schema}")));
        Assert.StartsWith(
            "TABRUK_MIGRATIONS_CONNECTION requires exactly one Search Path schema.",
            missingSearchPath.Message,
            StringComparison.Ordinal);

        InvalidOperationException multiSchema = Assert.Throws<InvalidOperationException>(
            () => TabrukDbContextOptions.Create(
                database.BuildMigrationConnectionString(
                    $"{database.Schema},public",
                    $"-c tabruk.target_schema={database.Schema} -c tabruk.disposable_ef=on")));
        Assert.StartsWith(
            "TABRUK_MIGRATIONS_CONNECTION schema must be one unquoted lowercase PostgreSQL identifier.",
            multiSchema.Message,
            StringComparison.Ordinal);

        InvalidOperationException mismatch = Assert.Throws<InvalidOperationException>(
            () => TabrukDbContextOptions.Create(
                database.BuildMigrationConnectionString(
                    database.Schema,
                    "-c tabruk.target_schema=public -c tabruk.disposable_ef=on")));
        Assert.StartsWith(
            "TABRUK_MIGRATIONS_CONNECTION Search Path and tabruk.target_schema must match.",
            mismatch.Message,
            StringComparison.Ordinal);

        await using TabrukDbContext verificationContext = database.CreateMigrationContext();
        Assert.Equal(
            expectedHistoryCount,
            (await verificationContext.Database.GetAppliedMigrationsAsync()).Count());
    }

    private static async Task CreateCorrectiveObjectsAsync(
        NpgsqlConnection connection,
        bool validateConstraint)
    {
        await ExecuteAsync(
            connection,
            """
            CREATE UNIQUE INDEX "ux_signups_waitlisted_order_per_help_need"
                ON "signups" ("organization_id", "help_need_id", "waitlist_order")
                WHERE "status" = 2;

            ALTER TABLE "signups"
                ADD CONSTRAINT "ck_signups_transition_chronology"
                CHECK ("last_transition_at" IS NULL OR "last_transition_at" >= "submitted_at")
                NOT VALID;
            """);

        if (validateConstraint)
        {
            await ExecuteAsync(
                connection,
                """
                ALTER TABLE "signups"
                    VALIDATE CONSTRAINT "ck_signups_transition_chronology"
                """);
        }
    }

    private static async Task SeedHostilePublicPrivilegesAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(
            connection,
            """
            GRANT SELECT, UPDATE ON TABLE "organizations" TO PUBLIC;
            GRANT SELECT, UPDATE, DELETE ON TABLE "audit_events" TO PUBLIC;
            GRANT SELECT, UPDATE, DELETE ON TABLE "privileged_access_events" TO PUBLIC;
            GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE "__EFMigrationsHistory" TO PUBLIC;
            GRANT USAGE, SELECT, UPDATE ON SEQUENCE "identity_role_claims_Id_seq" TO PUBLIC;
            GRANT USAGE, SELECT, UPDATE ON SEQUENCE "identity_user_claims_Id_seq" TO PUBLIC;
            """);
    }

    private static Task InstallHostileHistoryMetadataAsync(
        NpgsqlConnection connection,
        string hostileMetadata) =>
        hostileMetadata switch
        {
            "trigger" => ExecuteAsync(
                connection,
                """
                CREATE FUNCTION t8_hostile_history_trigger()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $body$
                BEGIN
                    RETURN NULL;
                END
                $body$;
                CREATE TRIGGER t8_hostile_history_trigger
                    BEFORE INSERT OR DELETE ON "__EFMigrationsHistory"
                    FOR EACH ROW
                    EXECUTE FUNCTION t8_hostile_history_trigger();
                """),
            "rule" => ExecuteAsync(
                connection,
                """
                CREATE RULE t8_hostile_history_rule
                    AS ON INSERT TO "__EFMigrationsHistory"
                    DO INSTEAD NOTHING;
                """),
            "policy" => ExecuteAsync(
                connection,
                """
                ALTER TABLE "__EFMigrationsHistory" ENABLE ROW LEVEL SECURITY;
                CREATE POLICY t8_hostile_history_policy
                    ON "__EFMigrationsHistory"
                    USING (true)
                    WITH CHECK (true);
                """),
            _ => throw new ArgumentOutOfRangeException(
                nameof(hostileMetadata),
                hostileMetadata,
                "Unknown hostile history metadata probe."),
        };

    private static async Task AssertAlternateGrantorPrivilegeFailsClosedAcrossCorrectiveSurfacesAsync(
        string surface,
        string probe,
        bool publicColumnAcl)
    {
        bool isFactory = surface == "factory";
        bool isDown = surface == "down";
        bool isOwner = surface == "owner";
        string? psqlPath = isOwner ? ResolvePsqlExecutablePath() : null;
        string? scriptPath = isOwner ? ResolveOwnerIdempotentScriptPath() : null;
        await using PostgresTestDatabase database = await CreateDatabaseAsync(applyMigrations: false);
        await using TabrukDbContext context = database.CreateMigrationContext();
        await context.Database.MigrateAsync(
            isDown ? CorrectiveMigration : InitialMigration);

        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await ResetAlternateGrantorRoleAsync(connection);
        try
        {
            if (publicColumnAcl)
            {
                await GrantAlternateGrantorPublicColumnAclAsync(connection, probe);
            }
            else
            {
                await GrantAlternateGrantorGrantOptionAsync(connection, probe);
            }

            Assert.Equal(
                1L,
                await CountAlternateGrantorPrivilegeAsync(
                    connection,
                    probe,
                    publicColumnAcl));

            if (isOwner)
            {
                PsqlInvocationResult result = await ExecutePsqlScriptAsync(
                    psqlPath!,
                    database.ConnectionString,
                    scriptPath!,
                    database.Schema);
                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains(
                    "T8 pre-history attestation failed: privilege-mismatch.",
                    result.CombinedOutput,
                    StringComparison.Ordinal);
            }
            else if (isFactory)
            {
                await using TabrukDbContext gatedContext = database.CreateMigrationContext();
                PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                    () => gatedContext.Database.GetAppliedMigrationsAsync());
                Assert.Equal("P0001", exception.SqlState);
                Assert.StartsWith(
                    "T8 pre-history attestation failed: privilege-mismatch.",
                    exception.MessageText,
                    StringComparison.Ordinal);
            }
            else
            {
                PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                    () => context.Database.MigrateAsync(
                        isDown ? InitialMigration : CorrectiveMigration));
                Assert.Equal("P0001", exception.SqlState);
                Assert.StartsWith(
                    "T8 pre-history attestation failed: privilege-mismatch.",
                    exception.MessageText,
                    StringComparison.Ordinal);
            }

            Assert.Equal(
                1L,
                await CountAlternateGrantorPrivilegeAsync(
                    connection,
                    probe,
                    publicColumnAcl));
            Assert.Equal(
                isDown ? 1L : 0L,
                await ScalarAsync<long>(
                    connection,
                    """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migrationId""",
                    ("migrationId", CorrectiveMigration)));
            if (isDown)
            {
                await AssertCorrectiveCatalogAsync(connection);
            }
            else
            {
                await AssertCorrectiveObjectsAbsentAsync(connection);
            }
        }
        finally
        {
            await ResetAlternateGrantorRoleAsync(connection);
        }
    }

    private static Task GrantAlternateGrantorPublicColumnAclAsync(
        NpgsqlConnection connection,
        string probe)
    {
        string grants = probe switch
        {
            "crud-select" =>
                $"""
                GRANT SELECT ("password_hash")
                    ON TABLE "users"
                    TO "{AlternateGrantorRole}"
                    WITH GRANT OPTION;
                SET ROLE "{AlternateGrantorRole}";
                GRANT SELECT ("password_hash") ON TABLE "users" TO PUBLIC;
                RESET ROLE;
                """,
            "audit-insert" =>
                $"""
                GRANT INSERT ("id")
                    ON TABLE "audit_events"
                    TO "{AlternateGrantorRole}"
                    WITH GRANT OPTION;
                SET ROLE "{AlternateGrantorRole}";
                GRANT INSERT ("id") ON TABLE "audit_events" TO PUBLIC;
                RESET ROLE;
                """,
            _ => throw new ArgumentOutOfRangeException(
                nameof(probe),
                probe,
                "Unsupported alternate-grantor PUBLIC column ACL probe."),
        };

        return ExecuteAsync(
            connection,
            $"""
            CREATE ROLE "{AlternateGrantorRole}" NOLOGIN;
            DO $tabruk$
            BEGIN
                EXECUTE format(
                    'GRANT USAGE ON SCHEMA %I TO "{AlternateGrantorRole}"',
                    current_schema());
            END
            $tabruk$;
            {grants}
            """);
    }

    private static Task GrantAlternateGrantorGrantOptionAsync(
        NpgsqlConnection connection,
        string probe)
    {
        string grants = probe switch
        {
            "crud-table-select" =>
                $"""
                GRANT SELECT
                    ON TABLE "users"
                    TO "{AlternateGrantorRole}"
                    WITH GRANT OPTION;
                SET ROLE "{AlternateGrantorRole}";
                GRANT SELECT
                    ON TABLE "users"
                    TO "tabruk_app"
                    WITH GRANT OPTION;
                RESET ROLE;
                """,
            "audit-column-insert" =>
                $"""
                GRANT INSERT ("id")
                    ON TABLE "audit_events"
                    TO "{AlternateGrantorRole}"
                    WITH GRANT OPTION;
                SET ROLE "{AlternateGrantorRole}";
                GRANT INSERT ("id")
                    ON TABLE "audit_events"
                    TO "tabruk_app"
                    WITH GRANT OPTION;
                RESET ROLE;
                """,
            "sequence-usage" =>
                $"""
                GRANT USAGE
                    ON SEQUENCE "identity_user_claims_Id_seq"
                    TO "{AlternateGrantorRole}"
                    WITH GRANT OPTION;
                SET ROLE "{AlternateGrantorRole}";
                GRANT USAGE
                    ON SEQUENCE "identity_user_claims_Id_seq"
                    TO "tabruk_app"
                    WITH GRANT OPTION;
                RESET ROLE;
                """,
            "audit-table-delete" =>
                $"""
                GRANT DELETE
                    ON TABLE "audit_events"
                    TO "{AlternateGrantorRole}"
                    WITH GRANT OPTION;
                SET ROLE "{AlternateGrantorRole}";
                GRANT DELETE
                    ON TABLE "audit_events"
                    TO "tabruk_app"
                    WITH GRANT OPTION;
                RESET ROLE;
                """,
            "history-table-maintain" =>
                $"""
                GRANT MAINTAIN
                    ON TABLE "__EFMigrationsHistory"
                    TO "{AlternateGrantorRole}"
                    WITH GRANT OPTION;
                SET ROLE "{AlternateGrantorRole}";
                GRANT MAINTAIN
                    ON TABLE "__EFMigrationsHistory"
                    TO "tabruk_app"
                    WITH GRANT OPTION;
                RESET ROLE;
                """,
            _ => throw new ArgumentOutOfRangeException(
                nameof(probe),
                probe,
                "Unsupported alternate-grantor grant-option probe."),
        };

        return ExecuteAsync(
            connection,
            $"""
            CREATE ROLE "{AlternateGrantorRole}" NOLOGIN;
            DO $tabruk$
            BEGIN
                EXECUTE format(
                    'GRANT USAGE ON SCHEMA %I TO "{AlternateGrantorRole}"',
                    current_schema());
            END
            $tabruk$;
            {grants}
            """);
    }

    private static Task<long> CountAlternateGrantorPrivilegeAsync(
        NpgsqlConnection connection,
        string probe,
        bool publicColumnAcl)
    {
        if (publicColumnAcl)
        {
            (string table, string column, string privilege) = probe switch
            {
                "crud-select" => ("users", "password_hash", "SELECT"),
                "audit-insert" => ("audit_events", "id", "INSERT"),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(probe),
                    probe,
                    "Unsupported alternate-grantor PUBLIC column ACL probe."),
            };

            return ScalarAsync<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM pg_class AS relation
                JOIN pg_namespace AS schema_definition
                  ON schema_definition.oid = relation.relnamespace
                JOIN pg_attribute AS attribute
                  ON attribute.attrelid = relation.oid
                CROSS JOIN LATERAL aclexplode(attribute.attacl) AS privilege
                JOIN pg_roles AS grantor_role
                  ON grantor_role.oid = privilege.grantor
                WHERE schema_definition.nspname = current_schema()
                  AND relation.relname = @table
                  AND attribute.attname = @column
                  AND privilege.privilege_type = @privilege
                  AND privilege.grantee = 0
                  AND grantor_role.rolname = @grantor
                """,
                ("table", table),
                ("column", column),
                ("privilege", privilege),
                ("grantor", AlternateGrantorRole));
        }

        if (probe == "audit-column-insert")
        {
            return ScalarAsync<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM pg_class AS relation
                JOIN pg_namespace AS schema_definition
                  ON schema_definition.oid = relation.relnamespace
                JOIN pg_attribute AS attribute
                  ON attribute.attrelid = relation.oid
                CROSS JOIN LATERAL aclexplode(attribute.attacl) AS privilege
                JOIN pg_roles AS grantor_role
                  ON grantor_role.oid = privilege.grantor
                WHERE schema_definition.nspname = current_schema()
                  AND relation.relname = 'audit_events'
                  AND attribute.attname = 'id'
                  AND privilege.privilege_type = 'INSERT'
                  AND privilege.grantee =
                      (SELECT oid FROM pg_roles WHERE rolname = 'tabruk_app')
                  AND privilege.is_grantable
                  AND grantor_role.rolname = @grantor
                """,
                ("grantor", AlternateGrantorRole));
        }

        (string relationName, string privilegeName) = probe switch
        {
            "crud-table-select" => ("users", "SELECT"),
            "sequence-usage" => ("identity_user_claims_Id_seq", "USAGE"),
            "audit-table-delete" => ("audit_events", "DELETE"),
            "history-table-maintain" => ("__EFMigrationsHistory", "MAINTAIN"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(probe),
                probe,
                "Unsupported alternate-grantor grant-option probe."),
        };

        return ScalarAsync<long>(
            connection,
            """
            SELECT COUNT(*)
            FROM pg_class AS relation
            JOIN pg_namespace AS schema_definition
              ON schema_definition.oid = relation.relnamespace
            CROSS JOIN LATERAL aclexplode(relation.relacl) AS privilege
            JOIN pg_roles AS grantor_role
              ON grantor_role.oid = privilege.grantor
            WHERE schema_definition.nspname = current_schema()
              AND relation.relname = @relation
              AND privilege.privilege_type = @privilege
              AND privilege.grantee =
                  (SELECT oid FROM pg_roles WHERE rolname = 'tabruk_app')
              AND privilege.is_grantable
              AND grantor_role.rolname = @grantor
            """,
            ("relation", relationName),
            ("privilege", privilegeName),
            ("grantor", AlternateGrantorRole));
    }

    private static async Task ResetAlternateGrantorRoleAsync(NpgsqlConnection connection)
    {
        bool roleExists = await ScalarAsync<bool>(
            connection,
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_roles
                WHERE rolname = @role)
            """,
            ("role", AlternateGrantorRole));
        if (!roleExists)
        {
            return;
        }

        string[] currentSchemaGrants = await ScalarAsync<string[]>(
            connection,
            """
            SELECT COALESCE(
                array_agg(grant_kind ORDER BY grant_kind),
                ARRAY[]::text[])
            FROM (
                SELECT 'public-crud-column' AS grant_kind
                FROM pg_class AS relation
                JOIN pg_namespace AS schema_definition
                  ON schema_definition.oid = relation.relnamespace
                JOIN pg_attribute AS attribute
                  ON attribute.attrelid = relation.oid
                CROSS JOIN LATERAL aclexplode(attribute.attacl) AS privilege
                WHERE schema_definition.nspname = current_schema()
                  AND relation.relname = 'users'
                  AND attribute.attname = 'password_hash'
                  AND privilege.privilege_type = 'SELECT'
                  AND privilege.grantee = 0
                  AND privilege.grantor =
                      (SELECT oid FROM pg_roles WHERE rolname = @role)

                UNION ALL

                SELECT 'public-audit-column'
                FROM pg_class AS relation
                JOIN pg_namespace AS schema_definition
                  ON schema_definition.oid = relation.relnamespace
                JOIN pg_attribute AS attribute
                  ON attribute.attrelid = relation.oid
                CROSS JOIN LATERAL aclexplode(attribute.attacl) AS privilege
                WHERE schema_definition.nspname = current_schema()
                  AND relation.relname = 'audit_events'
                  AND attribute.attname = 'id'
                  AND privilege.privilege_type = 'INSERT'
                  AND privilege.grantee = 0
                  AND privilege.grantor =
                      (SELECT oid FROM pg_roles WHERE rolname = @role)

                UNION ALL

                SELECT 'app-crud-table'
                FROM pg_class AS relation
                JOIN pg_namespace AS schema_definition
                  ON schema_definition.oid = relation.relnamespace
                CROSS JOIN LATERAL aclexplode(relation.relacl) AS privilege
                WHERE schema_definition.nspname = current_schema()
                  AND relation.relname = 'users'
                  AND privilege.privilege_type = 'SELECT'
                  AND privilege.grantee =
                      (SELECT oid FROM pg_roles WHERE rolname = 'tabruk_app')
                  AND privilege.grantor =
                      (SELECT oid FROM pg_roles WHERE rolname = @role)

                UNION ALL

                SELECT 'app-audit-column'
                FROM pg_class AS relation
                JOIN pg_namespace AS schema_definition
                  ON schema_definition.oid = relation.relnamespace
                JOIN pg_attribute AS attribute
                  ON attribute.attrelid = relation.oid
                CROSS JOIN LATERAL aclexplode(attribute.attacl) AS privilege
                WHERE schema_definition.nspname = current_schema()
                  AND relation.relname = 'audit_events'
                  AND attribute.attname = 'id'
                  AND privilege.privilege_type = 'INSERT'
                  AND privilege.grantee =
                      (SELECT oid FROM pg_roles WHERE rolname = 'tabruk_app')
                  AND privilege.grantor =
                      (SELECT oid FROM pg_roles WHERE rolname = @role)

                UNION ALL

                SELECT 'app-sequence'
                FROM pg_class AS relation
                JOIN pg_namespace AS schema_definition
                  ON schema_definition.oid = relation.relnamespace
                CROSS JOIN LATERAL aclexplode(relation.relacl) AS privilege
                WHERE schema_definition.nspname = current_schema()
                  AND relation.relname = 'identity_user_claims_Id_seq'
                  AND privilege.privilege_type = 'USAGE'
                  AND privilege.grantee =
                      (SELECT oid FROM pg_roles WHERE rolname = 'tabruk_app')
                  AND privilege.grantor =
                      (SELECT oid FROM pg_roles WHERE rolname = @role)

                UNION ALL

                SELECT 'app-audit-delete'
                FROM pg_class AS relation
                JOIN pg_namespace AS schema_definition
                  ON schema_definition.oid = relation.relnamespace
                CROSS JOIN LATERAL aclexplode(relation.relacl) AS privilege
                WHERE schema_definition.nspname = current_schema()
                  AND relation.relname = 'audit_events'
                  AND privilege.privilege_type = 'DELETE'
                  AND privilege.grantee =
                      (SELECT oid FROM pg_roles WHERE rolname = 'tabruk_app')
                  AND privilege.grantor =
                      (SELECT oid FROM pg_roles WHERE rolname = @role)

                UNION ALL

                SELECT 'app-history-maintain'
                FROM pg_class AS relation
                JOIN pg_namespace AS schema_definition
                  ON schema_definition.oid = relation.relnamespace
                CROSS JOIN LATERAL aclexplode(relation.relacl) AS privilege
                WHERE schema_definition.nspname = current_schema()
                  AND relation.relname = '__EFMigrationsHistory'
                  AND privilege.privilege_type = 'MAINTAIN'
                  AND privilege.grantee =
                      (SELECT oid FROM pg_roles WHERE rolname = 'tabruk_app')
                  AND privilege.grantor =
                      (SELECT oid FROM pg_roles WHERE rolname = @role)
            ) AS known_grants
            """,
            ("role", AlternateGrantorRole));
        if (currentSchemaGrants.Length > 0)
        {
            string revokeSql = string.Join(
                Environment.NewLine,
                currentSchemaGrants.Select(
                    grant => grant switch
                    {
                        "public-crud-column" =>
                            """REVOKE SELECT ("password_hash") ON TABLE "users" FROM PUBLIC;""",
                        "public-audit-column" =>
                            """REVOKE INSERT ("id") ON TABLE "audit_events" FROM PUBLIC;""",
                        "app-crud-table" =>
                            """REVOKE SELECT ON TABLE "users" FROM "tabruk_app";""",
                        "app-audit-column" =>
                            """REVOKE INSERT ("id") ON TABLE "audit_events" FROM "tabruk_app";""",
                        "app-sequence" =>
                            """REVOKE USAGE ON SEQUENCE "identity_user_claims_Id_seq" FROM "tabruk_app";""",
                        "app-audit-delete" =>
                            """REVOKE DELETE ON TABLE "audit_events" FROM "tabruk_app";""",
                        "app-history-maintain" =>
                            """REVOKE MAINTAIN ON TABLE "__EFMigrationsHistory" FROM "tabruk_app";""",
                        _ => throw new InvalidOperationException(
                            $"Unknown alternate-grantor cleanup entry: {grant}."),
                    }));

            await ExecuteAsync(
                connection,
                $"""
                DO $tabruk$
                BEGIN
                    EXECUTE format(
                        'GRANT USAGE ON SCHEMA %I TO "{AlternateGrantorRole}"',
                        current_schema());
                END
                $tabruk$;
                SET ROLE "{AlternateGrantorRole}";
                {revokeSql}
                RESET ROLE;
                """);
        }

        await ExecuteAsync(
            connection,
            $"""
            DROP OWNED BY "{AlternateGrantorRole}";
            DROP ROLE "{AlternateGrantorRole}";
            """);
    }

    private static async Task GrantInheritedTablePrivilegeAsync(
        NpgsqlConnection connection,
        string privilege)
    {
        string grant = privilege switch
        {
            "TRUNCATE" => """GRANT TRUNCATE ON TABLE "organizations" TO "tabruk_app_inherited_privilege_probe" """,
            "REFERENCES" => """GRANT REFERENCES ON TABLE "organizations" TO "tabruk_app_inherited_privilege_probe" """,
            "TRIGGER" => """GRANT TRIGGER ON TABLE "organizations" TO "tabruk_app_inherited_privilege_probe" """,
            "MAINTAIN" => """GRANT MAINTAIN ON TABLE "organizations" TO "tabruk_app_inherited_privilege_probe" """,
            _ => throw new ArgumentOutOfRangeException(
                nameof(privilege),
                privilege,
                "Unsupported PostgreSQL table privilege probe."),
        };

        await ExecuteAsync(
            connection,
            $"""
            CREATE ROLE "{InheritedPrivilegeProbeRole}" NOLOGIN;
            CREATE ROLE "{InheritedPrivilegeBridgeRole}" NOLOGIN;
            GRANT "{InheritedPrivilegeProbeRole}" TO "{InheritedPrivilegeBridgeRole}";
            GRANT "{InheritedPrivilegeBridgeRole}" TO "tabruk_app";
            {grant};
            """);
    }

    private static async Task GrantInheritedColumnPrivilegeAsync(
        NpgsqlConnection connection,
        string probe)
    {
        string setupSql = probe switch
        {
            "history-insert" =>
                """
                GRANT INSERT ("MigrationId", "ProductVersion")
                    ON TABLE "__EFMigrationsHistory"
                    TO "tabruk_app_inherited_privilege_probe"
                """,
            "crud-references" =>
                """
                GRANT REFERENCES ("id")
                    ON TABLE "organizations"
                    TO "tabruk_app_inherited_privilege_probe"
                """,
            "audit-select" =>
                """
                GRANT SELECT ("id")
                    ON TABLE "audit_events"
                    TO "tabruk_app_inherited_privilege_probe"
                """,
            "non-allowlisted-update" =>
                """
                CREATE TABLE "t8_non_allowlisted_sentinel" ("id" integer NOT NULL);
                GRANT UPDATE ("id")
                    ON TABLE "t8_non_allowlisted_sentinel"
                    TO "tabruk_app_inherited_privilege_probe"
                """,
            _ => throw new ArgumentOutOfRangeException(
                nameof(probe),
                probe,
                "Unsupported PostgreSQL column privilege probe."),
        };

        await ExecuteAsync(
            connection,
            $"""
            CREATE ROLE "{InheritedPrivilegeProbeRole}" NOLOGIN;
            CREATE ROLE "{InheritedPrivilegeBridgeRole}" NOLOGIN;
            GRANT "{InheritedPrivilegeProbeRole}" TO "{InheritedPrivilegeBridgeRole}";
            GRANT "{InheritedPrivilegeBridgeRole}" TO "tabruk_app";
            {setupSql};
            """);
    }

    private static Task ResetInheritedPrivilegeProbeRoleAsync(NpgsqlConnection connection) =>
        ExecuteAsync(
            connection,
            $"""
            DO $tabruk$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM pg_roles
                    WHERE rolname = '{InheritedPrivilegeBridgeRole}'
                ) THEN
                    REVOKE "{InheritedPrivilegeBridgeRole}" FROM "tabruk_app";
                    DROP OWNED BY "{InheritedPrivilegeBridgeRole}";
                END IF;
                IF EXISTS (
                    SELECT 1
                    FROM pg_roles
                    WHERE rolname = '{InheritedPrivilegeProbeRole}'
                ) THEN
                    IF EXISTS (
                        SELECT 1
                        FROM pg_roles
                        WHERE rolname = '{InheritedPrivilegeBridgeRole}'
                    ) THEN
                        REVOKE "{InheritedPrivilegeProbeRole}" FROM "{InheritedPrivilegeBridgeRole}";
                    END IF;
                    DROP OWNED BY "{InheritedPrivilegeProbeRole}";
                    DROP ROLE "{InheritedPrivilegeProbeRole}";
                END IF;
                IF EXISTS (
                    SELECT 1
                    FROM pg_roles
                    WHERE rolname = '{InheritedPrivilegeBridgeRole}'
                ) THEN
                    DROP ROLE "{InheritedPrivilegeBridgeRole}";
                END IF;
            END
            $tabruk$;
            """);

    private static Task HardenGlobalDefaultPrivilegesAsync(NpgsqlConnection connection) =>
        ExecuteAsync(
            connection,
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
            """);

    private static Task ResetDefaultPrivilegesToPostgresDefaultsAsync(
        NpgsqlConnection connection) =>
        ExecuteAsync(
            connection,
            """
            DO $tabruk$
            DECLARE
                target_schema text := current_schema();
                object_kind text;
            BEGIN
                FOREACH object_kind IN ARRAY ARRAY['TABLES', 'SEQUENCES', 'FUNCTIONS', 'TYPES']
                LOOP
                    EXECUTE format(
                        'ALTER DEFAULT PRIVILEGES FOR ROLE %I REVOKE ALL PRIVILEGES ON %s FROM %I',
                        current_user,
                        object_kind,
                        'tabruk_app');
                    EXECUTE format(
                        'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA %I REVOKE ALL PRIVILEGES ON %s FROM PUBLIC, %I, %I',
                        current_user,
                        target_schema,
                        object_kind,
                        current_user,
                        'tabruk_app');
                END LOOP;

                EXECUTE format(
                    'ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT EXECUTE ON FUNCTIONS TO PUBLIC',
                    current_user);
                EXECUTE format(
                    'ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT USAGE ON TYPES TO PUBLIC',
                    current_user);
            END
            $tabruk$;
            """);

    private static async Task RestoreGlobalDefaultPrivilegesAsync(
        NpgsqlConnection connection,
        string expectedFingerprint,
        long forbiddenDefaultsBefore)
    {
        try
        {
            if (forbiddenDefaultsBefore == 0)
            {
                await HardenGlobalDefaultPrivilegesAsync(connection);
            }
            else
            {
                await ResetDefaultPrivilegesToPostgresDefaultsAsync(connection);
            }

            Assert.Equal(
                expectedFingerprint,
                await GetGlobalDefaultAclFingerprintAsync(connection));
        }
        finally
        {
            await ExecuteAsync(
                connection,
                $"SELECT pg_advisory_unlock({GlobalDefaultAclAdvisoryLock})");
        }
    }

    private static Task AcquireGlobalDefaultAclLockAsync(NpgsqlConnection connection) =>
        ExecuteAsync(
            connection,
            $"SELECT pg_advisory_lock({GlobalDefaultAclAdvisoryLock})");

    private static Task<long> GetRelevantDefaultAclRowCountAsync(NpgsqlConnection connection) =>
        ScalarAsync<long>(
            connection,
            """
            SELECT COUNT(*)
            FROM pg_default_acl AS default_acl
            WHERE default_acl.defaclrole =
                      (SELECT oid FROM pg_roles WHERE rolname = current_user)
              AND default_acl.defaclnamespace IN (
                  0,
                  (SELECT oid FROM pg_namespace WHERE nspname = current_schema()))
              AND default_acl.defaclobjtype IN ('r', 'S', 'f', 'T')
            """);

    private static Task<string> GetGlobalDefaultAclFingerprintAsync(NpgsqlConnection connection) =>
        ScalarAsync<string>(
            connection,
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
            """);

    private static Task<long> GetForbiddenEffectiveDefaultAclCountAsync(
        NpgsqlConnection connection,
        string scope = "all") =>
        ScalarAsync<long>(
            connection,
            """
            WITH owner_definition AS (
                SELECT
                    (SELECT oid FROM pg_roles WHERE rolname = current_user) AS owner_oid,
                    (SELECT oid FROM pg_roles WHERE rolname = 'tabruk_app') AS app_oid,
                    (SELECT oid FROM pg_namespace WHERE nspname = current_schema()) AS namespace_oid
            )
            SELECT COUNT(*)
            FROM owner_definition
            CROSS JOIN LATERAL (
                VALUES
                    (0::oid, 'r'::"char"),
                    (0::oid, 'S'::"char"),
                    (0::oid, 'f'::"char"),
                    (0::oid, 'T'::"char"),
                    (owner_definition.namespace_oid, 'r'::"char"),
                    (owner_definition.namespace_oid, 'S'::"char"),
                    (owner_definition.namespace_oid, 'f'::"char"),
                    (owner_definition.namespace_oid, 'T'::"char")
            ) AS default_scope(namespace_oid, object_type)
            LEFT JOIN pg_default_acl AS default_acl
              ON default_acl.defaclrole = owner_definition.owner_oid
             AND default_acl.defaclnamespace = default_scope.namespace_oid
             AND default_acl.defaclobjtype = default_scope.object_type
            CROSS JOIN LATERAL aclexplode(
                COALESCE(
                    default_acl.defaclacl,
                    acldefault(default_scope.object_type, owner_definition.owner_oid)))
                AS privilege
            WHERE (
                    privilege.grantee = 0
                    OR privilege.grantee = owner_definition.app_oid
                    OR pg_has_role('tabruk_app', privilege.grantee, 'MEMBER')
                  )
              AND (
                    @scope = 'all'
                    OR (@scope = 'global' AND default_scope.namespace_oid = 0)
                    OR (
                        @scope = 'target'
                        AND default_scope.namespace_oid = owner_definition.namespace_oid)
                  )
            """,
            ("scope", scope));

    private static Task<string> GetWaitlistIndexDefinitionAsync(NpgsqlConnection connection) =>
        ScalarAsync<string>(
            connection,
            """
            SELECT pg_get_indexdef(index_relation.oid)
            FROM pg_class AS index_relation
            JOIN pg_namespace AS index_namespace
              ON index_namespace.oid = index_relation.relnamespace
            WHERE index_namespace.nspname = current_schema()
              AND index_relation.relname = 'ux_signups_waitlisted_order_per_help_need'
            """);

    private static Task<uint> GetWaitlistIndexOidAsync(NpgsqlConnection connection) =>
        ScalarAsync<uint>(
            connection,
            """
            SELECT index_relation.oid
            FROM pg_class AS index_relation
            JOIN pg_namespace AS index_namespace
              ON index_namespace.oid = index_relation.relnamespace
            WHERE index_namespace.nspname = current_schema()
              AND index_relation.relname = 'ux_signups_waitlisted_order_per_help_need'
            """);

    private static Task<string> GetChronologyConstraintDefinitionAsync(NpgsqlConnection connection) =>
        ScalarAsync<string>(
            connection,
            """
            SELECT pg_get_constraintdef(constraint_definition.oid, true)
            FROM pg_constraint AS constraint_definition
            JOIN pg_class AS table_relation
              ON table_relation.oid = constraint_definition.conrelid
            JOIN pg_namespace AS table_namespace
              ON table_namespace.oid = table_relation.relnamespace
            WHERE table_namespace.nspname = current_schema()
              AND table_relation.relname = 'signups'
              AND constraint_definition.conname = 'ck_signups_transition_chronology'
            """);

    private static Task<uint> GetChronologyConstraintOidAsync(NpgsqlConnection connection) =>
        ScalarAsync<uint>(
            connection,
            """
            SELECT constraint_definition.oid
            FROM pg_constraint AS constraint_definition
            JOIN pg_class AS table_relation
              ON table_relation.oid = constraint_definition.conrelid
            JOIN pg_namespace AS table_namespace
              ON table_namespace.oid = table_relation.relnamespace
            WHERE table_namespace.nspname = current_schema()
              AND table_relation.relname = 'signups'
              AND constraint_definition.conname = 'ck_signups_transition_chronology'
            """);

    private static async Task ExecuteAsApplicationRoleAsync(
        PostgresTestDatabase database,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (NpgsqlCommand setRole = new("SET LOCAL ROLE tabruk_app", connection, transaction))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        await using NpgsqlCommand command = new(sql, connection, transaction);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
        await transaction.RollbackAsync();
    }

    private static async Task ExecuteCommittedAsApplicationRoleAsync(
        PostgresTestDatabase database,
        string sql)
    {
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (NpgsqlCommand setRole = new("SET LOCAL ROLE tabruk_app", connection, transaction))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        await using NpgsqlCommand command = new(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task AssertApplicationRoleMutationDeniedAsync(
        PostgresTestDatabase database,
        string sql)
    {
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsApplicationRoleAsync(database, sql));
        Assert.Equal("42501", exception.SqlState);
    }

    private static async Task InsertWaitlistedSignupAsync(
        NpgsqlConnection connection,
        PersistenceSeed seed,
        Guid primaryMembershipId,
        long waitlistOrder)
    {
        await ExecuteAsync(
            connection,
            """
            INSERT INTO signups
                (id, organization_id, service_date_id, help_need_id, primary_membership_id, kind,
                 unnamed_participant_count, status, submitted_at, last_transition_at, waitlist_order, version)
            VALUES
                (@id, @organizationId, @serviceDateId, @helpNeedId, @membershipId, 0, 0, 2,
                 @submittedAt, @lastTransitionAt, @waitlistOrder, 1)
            """,
            ("id", Guid.CreateVersion7()),
            ("organizationId", seed.OrganizationId.Value),
            ("serviceDateId", seed.ServiceDateId.Value),
            ("helpNeedId", seed.HelpNeedId.Value),
            ("membershipId", primaryMembershipId),
            ("submittedAt", seed.Now),
            ("lastTransitionAt", seed.Now),
            ("waitlistOrder", waitlistOrder));
    }

    private static async Task InsertTerminalSignupAsync(
        NpgsqlConnection connection,
        PersistenceSeed seed,
        short status,
        DateTimeOffset submittedAt,
        DateTimeOffset lastTransitionAt)
    {
        await ExecuteAsync(
            connection,
            """
            INSERT INTO signups
                (id, organization_id, service_date_id, help_need_id, primary_membership_id, kind,
                 unnamed_participant_count, status, submitted_at, last_transition_at, version)
            VALUES
                (@id, @organizationId, @serviceDateId, @helpNeedId, @membershipId, 0, 0, @status,
                 @submittedAt, @lastTransitionAt, 1)
            """,
            ("id", Guid.CreateVersion7()),
            ("organizationId", seed.OrganizationId.Value),
            ("serviceDateId", seed.ServiceDateId.Value),
            ("helpNeedId", seed.HelpNeedId.Value),
            ("membershipId", seed.ManagerMembershipId.Value),
            ("status", status),
            ("submittedAt", submittedAt),
            ("lastTransitionAt", lastTransitionAt));
    }

    private static async Task AssertCorrectiveCatalogAsync(NpgsqlConnection connection)
    {
        Assert.True(
            await ScalarAsync<bool>(
                connection,
                """
                SELECT
                    index_namespace.nspname = current_schema()
                    AND index_relation.relkind = 'i'
                    AND table_namespace.nspname = current_schema()
                    AND table_relation.relname = 'signups'
                    AND access_method.amname = 'btree'
                    AND index_definition.indisunique
                    AND index_definition.indisvalid
                    AND index_definition.indisready
                    AND index_definition.indislive
                    AND index_definition.indnkeyatts = 3
                    AND index_definition.indnatts = 3
                    AND index_definition.indexprs IS NULL
                    AND NOT EXISTS (
                        SELECT 1
                        FROM unnest(index_definition.indkey) AS indexed_attribute(attribute_number)
                        WHERE indexed_attribute.attribute_number = 0
                    )
                FROM pg_index AS index_definition
                JOIN pg_class AS index_relation
                  ON index_relation.oid = index_definition.indexrelid
                JOIN pg_namespace AS index_namespace
                  ON index_namespace.oid = index_relation.relnamespace
                JOIN pg_class AS table_relation
                  ON table_relation.oid = index_definition.indrelid
                JOIN pg_namespace AS table_namespace
                  ON table_namespace.oid = table_relation.relnamespace
                JOIN pg_am AS access_method
                  ON access_method.oid = index_relation.relam
                WHERE index_namespace.nspname = current_schema()
                  AND index_relation.relname = 'ux_signups_waitlisted_order_per_help_need'
                """));
        await AssertWaitlistIndexStateAsync(connection, expectedPresent: true, expectedValid: true);
        Assert.Equal(
            "organization_id,help_need_id,waitlist_order",
            await ScalarAsync<string>(
                connection,
                """
                SELECT string_agg(attribute.attname, ',' ORDER BY indexed_column.ordinality)
                FROM pg_index AS index_definition
                JOIN pg_class AS index_relation ON index_relation.oid = index_definition.indexrelid
                JOIN pg_namespace AS index_namespace
                  ON index_namespace.oid = index_relation.relnamespace
                CROSS JOIN LATERAL unnest(index_definition.indkey)
                    WITH ORDINALITY AS indexed_column(attribute_number, ordinality)
                JOIN pg_attribute AS attribute
                  ON attribute.attrelid = index_definition.indrelid
                 AND attribute.attnum = indexed_column.attribute_number
                WHERE index_namespace.nspname = current_schema()
                  AND index_relation.relname = 'ux_signups_waitlisted_order_per_help_need'
                """));
        Assert.Equal(
            "(status = 2)",
            await ScalarAsync<string>(
                connection,
                """
                SELECT pg_get_expr(index_definition.indpred, index_definition.indrelid)
                FROM pg_index AS index_definition
                JOIN pg_class AS index_relation ON index_relation.oid = index_definition.indexrelid
                JOIN pg_namespace AS index_namespace
                  ON index_namespace.oid = index_relation.relnamespace
                WHERE index_namespace.nspname = current_schema()
                  AND index_relation.relname = 'ux_signups_waitlisted_order_per_help_need'
                """));
        await AssertChronologyConstraintStateAsync(
            connection,
            expectedPresent: true,
            expectedValidated: true);
        Assert.True(
            await ScalarAsync<bool>(
                connection,
                """
                SELECT
                    table_namespace.nspname = current_schema()
                    AND table_relation.relname = 'signups'
                    AND constraint_definition.contype = 'c'
                    AND NOT constraint_definition.connoinherit
                FROM pg_constraint AS constraint_definition
                JOIN pg_class AS table_relation
                  ON table_relation.oid = constraint_definition.conrelid
                JOIN pg_namespace AS table_namespace
                  ON table_namespace.oid = table_relation.relnamespace
                WHERE table_namespace.nspname = current_schema()
                  AND table_relation.relname = 'signups'
                  AND constraint_definition.conname = 'ck_signups_transition_chronology'
                """));
        Assert.Equal(
            "((last_transition_at IS NULL) OR (last_transition_at >= submitted_at))",
            await ScalarAsync<string>(
                connection,
                """
                SELECT pg_get_expr(constraint_definition.conbin, constraint_definition.conrelid)
                FROM pg_constraint AS constraint_definition
                JOIN pg_class AS relation ON relation.oid = constraint_definition.conrelid
                JOIN pg_namespace AS schema_definition
                  ON schema_definition.oid = relation.relnamespace
                WHERE schema_definition.nspname = current_schema()
                  AND relation.relname = 'signups'
                  AND constraint_definition.conname = 'ck_signups_transition_chronology'
                """));
    }

    private static async Task AssertCorrectiveObjectsAbsentAsync(NpgsqlConnection connection)
    {
        await AssertWaitlistIndexStateAsync(connection, expectedPresent: false, expectedValid: false);
        await AssertChronologyConstraintStateAsync(
            connection,
            expectedPresent: false,
            expectedValidated: false);
    }

    private static async Task AssertHardenedPrivilegeMatrixAsync(
        NpgsqlConnection connection,
        params string[] additionalDeniedTables)
    {
        foreach (string table in PostgresLeastPrivilegeCatalog.CrudTables)
        {
            foreach (string privilege in CrudTablePrivileges)
            {
                Assert.True(
                    await HasTablePrivilegeAsync(connection, table, privilege),
                    $"tabruk_app must have {privilege} on {table}.");
            }

            foreach (string privilege in CrudDeniedTablePrivileges)
            {
                Assert.False(
                    await HasTablePrivilegeAsync(connection, table, privilege),
                    $"tabruk_app must not have {privilege} on {table}.");
            }
        }

        foreach (string insertOnlyTable in PostgresLeastPrivilegeCatalog.InsertOnlyTables)
        {
            Assert.True(await HasTablePrivilegeAsync(connection, insertOnlyTable, "INSERT"));
            foreach (string privilege in InsertOnlyDeniedTablePrivileges)
            {
                Assert.False(
                    await HasTablePrivilegeAsync(connection, insertOnlyTable, privilege),
                    $"tabruk_app must not have {privilege} on {insertOnlyTable}.");
            }
        }

        foreach (string deniedTable in PostgresLeastPrivilegeCatalog.DeniedTables
                     .Concat(additionalDeniedTables)
                     .Distinct(StringComparer.Ordinal))
        {
            foreach (string privilege in PostgreSql18TablePrivileges)
            {
                Assert.False(
                    await HasTablePrivilegeAsync(connection, deniedTable, privilege),
                    $"tabruk_app must not have {privilege} on {deniedTable}.");
            }
        }

        string[] expectedPrivilegedTables = PostgresLeastPrivilegeCatalog.ClassifiedTables
            .OrderBy(table => table, StringComparer.Ordinal)
            .ToArray();
        string[] privilegedTables = await ScalarAsync<string[]>(
            connection,
            """
            SELECT COALESCE(
                array_agg(relation.relname ORDER BY relation.relname),
                ARRAY[]::text[])
            FROM pg_class AS relation
            JOIN pg_namespace AS schema_definition
              ON schema_definition.oid = relation.relnamespace
            WHERE schema_definition.nspname = current_schema()
              AND relation.relkind IN ('r', 'p', 'v', 'm', 'f')
              AND EXISTS (
                  SELECT 1
                  FROM unnest(@privileges) AS table_privilege(privilege_name)
                  WHERE has_table_privilege(
                      'tabruk_app',
                      relation.oid,
                      table_privilege.privilege_name)
              )
            """,
            ("privileges", PostgreSql18TablePrivileges));
        Assert.Equal(expectedPrivilegedTables, privilegedTables);

        string[] expectedSequences = PostgresLeastPrivilegeCatalog.UsageSelectSequences
            .OrderBy(sequence => sequence, StringComparer.Ordinal)
            .ToArray();
        Array.Sort(expectedSequences, StringComparer.Ordinal);
        string[] privilegedSequences = await ScalarAsync<string[]>(
            connection,
            """
            SELECT COALESCE(
                array_agg(relation.relname ORDER BY relation.relname),
                ARRAY[]::text[])
            FROM pg_class AS relation
            JOIN pg_namespace AS schema_definition
              ON schema_definition.oid = relation.relnamespace
            WHERE schema_definition.nspname = current_schema()
              AND relation.relkind = 'S'
              AND (
                  has_sequence_privilege('tabruk_app', relation.oid, 'USAGE')
                  OR has_sequence_privilege('tabruk_app', relation.oid, 'SELECT')
                  OR has_sequence_privilege('tabruk_app', relation.oid, 'UPDATE')
              )
            """);

        Assert.Equal(expectedSequences, privilegedSequences);
        foreach (string sequence in expectedSequences)
        {
            Assert.True(await HasSequencePrivilegeAsync(connection, sequence, "USAGE"));
            Assert.True(await HasSequencePrivilegeAsync(connection, sequence, "SELECT"));
            Assert.False(await HasSequencePrivilegeAsync(connection, sequence, "UPDATE"));
        }

        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM pg_class AS relation
                JOIN pg_namespace AS schema_definition
                  ON schema_definition.oid = relation.relnamespace
                CROSS JOIN LATERAL aclexplode(
                    COALESCE(relation.relacl, acldefault('r', relation.relowner))) AS privilege
                WHERE schema_definition.nspname = current_schema()
                  AND relation.relname = ANY(@tables)
                  AND privilege.grantee = 0
                """,
                ("tables", PostgresLeastPrivilegeCatalog.ManagedTables.ToArray())));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM pg_class AS relation
                JOIN pg_namespace AS schema_definition
                  ON schema_definition.oid = relation.relnamespace
                CROSS JOIN LATERAL aclexplode(
                    COALESCE(relation.relacl, acldefault('S', relation.relowner))) AS privilege
                WHERE schema_definition.nspname = current_schema()
                  AND relation.relname = ANY(@sequences)
                  AND privilege.grantee = 0
                """,
                ("sequences", expectedSequences)));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM pg_class AS relation
                JOIN pg_namespace AS schema_definition
                  ON schema_definition.oid = relation.relnamespace
                JOIN pg_attribute AS attribute
                  ON attribute.attrelid = relation.oid
                CROSS JOIN LATERAL aclexplode(attribute.attacl) AS privilege
                WHERE schema_definition.nspname = current_schema()
                  AND relation.relkind IN ('r', 'p', 'v', 'm', 'f')
                  AND attribute.attnum > 0
                  AND NOT attribute.attisdropped
                  AND privilege.grantee = 0
                """));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM pg_class AS relation
                JOIN pg_namespace AS schema_definition
                  ON schema_definition.oid = relation.relnamespace
                CROSS JOIN LATERAL aclexplode(
                    COALESCE(
                        relation.relacl,
                        acldefault(
                            CASE
                                WHEN relation.relkind = 'S' THEN 'S'::"char"
                                ELSE 'r'::"char"
                            END,
                            relation.relowner))) AS privilege
                WHERE schema_definition.nspname = current_schema()
                  AND relation.relkind IN ('r', 'p', 'v', 'm', 'f', 'S')
                  AND privilege.is_grantable
                  AND privilege.grantee <> 0
                  AND (
                      privilege.grantee =
                          (SELECT oid FROM pg_roles WHERE rolname = 'tabruk_app')
                      OR pg_has_role(
                          'tabruk_app',
                          privilege.grantee,
                          'MEMBER')
                  )
                """));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM pg_class AS relation
                JOIN pg_namespace AS schema_definition
                  ON schema_definition.oid = relation.relnamespace
                JOIN pg_attribute AS attribute
                  ON attribute.attrelid = relation.oid
                CROSS JOIN LATERAL aclexplode(attribute.attacl) AS privilege
                WHERE schema_definition.nspname = current_schema()
                  AND relation.relkind IN ('r', 'p', 'v', 'm', 'f')
                  AND attribute.attnum > 0
                  AND NOT attribute.attisdropped
                  AND privilege.is_grantable
                  AND privilege.grantee <> 0
                  AND (
                      privilege.grantee =
                          (SELECT oid FROM pg_roles WHERE rolname = 'tabruk_app')
                      OR pg_has_role(
                          'tabruk_app',
                          privilege.grantee,
                          'MEMBER')
                  )
                """));
    }

    private static async Task AssertWaitlistIndexStateAsync(
        NpgsqlConnection connection,
        bool expectedPresent,
        bool expectedValid)
    {
        long count = await ScalarAsync<long>(
            connection,
            """
            SELECT COUNT(*)
            FROM pg_indexes
            WHERE schemaname = current_schema()
              AND indexname = 'ux_signups_waitlisted_order_per_help_need'
            """);

        Assert.Equal(expectedPresent ? 1L : 0L, count);
        if (!expectedPresent)
        {
            return;
        }

        Assert.Equal(
            expectedValid,
            await ScalarAsync<bool>(
                connection,
                """
                SELECT index_definition.indisvalid
                FROM pg_index AS index_definition
                WHERE index_definition.indexrelid =
                    to_regclass(format('%I.%I', current_schema(), 'ux_signups_waitlisted_order_per_help_need'))
                """));
    }

    private static async Task AssertChronologyConstraintStateAsync(
        NpgsqlConnection connection,
        bool expectedPresent,
        bool expectedValidated)
    {
        long count = await ScalarAsync<long>(
            connection,
            """
            SELECT COUNT(*)
            FROM information_schema.table_constraints
            WHERE table_schema = current_schema()
              AND table_name = 'signups'
              AND constraint_name = 'ck_signups_transition_chronology'
            """);

        Assert.Equal(expectedPresent ? 1L : 0L, count);
        if (!expectedPresent)
        {
            return;
        }

        Assert.Equal(
            expectedValidated,
            await ScalarAsync<bool>(
                connection,
                """
                SELECT constraint_definition.convalidated
                FROM pg_constraint AS constraint_definition
                JOIN pg_class AS relation ON relation.oid = constraint_definition.conrelid
                JOIN pg_namespace AS schema_definition
                  ON schema_definition.oid = relation.relnamespace
                WHERE schema_definition.nspname = current_schema()
                  AND relation.relname = 'signups'
                  AND constraint_definition.conname = 'ck_signups_transition_chronology'
                """));
        Assert.True(
            await ScalarAsync<bool>(
                connection,
                """
                SELECT constraint_definition.conenforced
                FROM pg_constraint AS constraint_definition
                JOIN pg_class AS relation ON relation.oid = constraint_definition.conrelid
                JOIN pg_namespace AS schema_definition
                  ON schema_definition.oid = relation.relnamespace
                WHERE schema_definition.nspname = current_schema()
                  AND relation.relname = 'signups'
                  AND constraint_definition.conname = 'ck_signups_transition_chronology'
                """));
    }

    private static async Task<bool> HasTablePrivilegeAsync(
        NpgsqlConnection connection,
        string table,
        string privilege)
    {
        await using NpgsqlCommand command = new(
            """
            SELECT has_table_privilege(
                'tabruk_app',
                format('%I.%I', current_schema(), @table),
                @privilege)
            """,
            connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("privilege", privilege);
        return Assert.IsType<bool>(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> HasColumnPrivilegeAsync(
        NpgsqlConnection connection,
        string table,
        string column,
        string privilege)
    {
        await using NpgsqlCommand command = new(
            """
            SELECT has_column_privilege(
                'tabruk_app',
                format('%I.%I', current_schema(), @table),
                @column,
                @privilege)
            """,
            connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        command.Parameters.AddWithValue("privilege", privilege);
        return Assert.IsType<bool>(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> HasSequencePrivilegeAsync(
        NpgsqlConnection connection,
        string sequence,
        string privilege)
    {
        await using NpgsqlCommand command = new(
            """
            SELECT has_sequence_privilege(
                'tabruk_app',
                format('%I.%I', current_schema(), @sequence),
                @privilege)
            """,
            connection);
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("privilege", privilege);
        return Assert.IsType<bool>(await command.ExecuteScalarAsync());
    }

    private static Task<string> GetHistoryStructureFingerprintAsync(
        NpgsqlConnection connection) =>
        ScalarAsync<string>(
            connection,
            """
            SELECT jsonb_build_object(
                'relation',
                jsonb_build_array(
                    relation.relkind,
                    relation.relpersistence,
                    relation.relispartition,
                    relation.relrowsecurity,
                    relation.relforcerowsecurity,
                    relation.relhasrules,
                    relation.relhastriggers,
                    relation.relhassubclass,
                    relation.relreplident,
                    relation.reloptions,
                    relation.relowner),
                'columns',
                (
                    SELECT jsonb_agg(
                        jsonb_build_array(
                            attribute.attnum,
                            attribute.attname,
                            attribute.atttypid,
                            attribute.atttypmod,
                            attribute.attnotnull,
                            attribute.atthasdef,
                            attribute.attidentity,
                            attribute.attgenerated,
                            attribute.attisdropped,
                            attribute.attcollation)
                        ORDER BY attribute.attnum)
                    FROM pg_attribute AS attribute
                    WHERE attribute.attrelid = relation.oid
                      AND attribute.attnum > 0
                ),
                'constraints',
                (
                    SELECT jsonb_agg(
                        jsonb_build_array(
                            constraint_definition.conname,
                            constraint_definition.contype,
                            constraint_definition.conkey,
                            constraint_definition.convalidated,
                            constraint_definition.conenforced,
                            constraint_definition.connoinherit,
                            pg_get_constraintdef(constraint_definition.oid))
                        ORDER BY constraint_definition.conname)
                    FROM pg_constraint AS constraint_definition
                    WHERE constraint_definition.conrelid = relation.oid
                ),
                'indexes',
                (
                    SELECT jsonb_agg(
                        jsonb_build_array(
                            index_relation.relname,
                            index_relation.relowner,
                            index_relation.reloptions,
                            pg_get_indexdef(index_definition.indexrelid))
                        ORDER BY index_relation.relname)
                    FROM pg_index AS index_definition
                    JOIN pg_class AS index_relation
                      ON index_relation.oid = index_definition.indexrelid
                    WHERE index_definition.indrelid = relation.oid
                ),
                'triggers',
                (
                    SELECT jsonb_agg(
                        pg_get_triggerdef(trigger_definition.oid)
                        ORDER BY trigger_definition.tgname)
                    FROM pg_trigger AS trigger_definition
                    WHERE trigger_definition.tgrelid = relation.oid
                ),
                'rules',
                (
                    SELECT jsonb_agg(
                        pg_get_ruledef(rule_definition.oid)
                        ORDER BY rule_definition.rulename)
                    FROM pg_rewrite AS rule_definition
                    WHERE rule_definition.ev_class = relation.oid
                ),
                'policies',
                (
                    SELECT jsonb_agg(policy_definition.polname ORDER BY policy_definition.polname)
                    FROM pg_policy AS policy_definition
                    WHERE policy_definition.polrelid = relation.oid
                ),
                'inherits',
                (
                    SELECT jsonb_agg(
                        jsonb_build_array(inheritance.inhparent, inheritance.inhrelid)
                        ORDER BY inheritance.inhseqno)
                    FROM pg_inherits AS inheritance
                    WHERE inheritance.inhparent = relation.oid
                       OR inheritance.inhrelid = relation.oid
                ),
                'view',
                CASE
                    WHEN relation.relkind IN ('v', 'm')
                    THEN pg_get_viewdef(relation.oid)
                    ELSE NULL
                END)::text
            FROM pg_class AS relation
            JOIN pg_namespace AS schema_definition
              ON schema_definition.oid = relation.relnamespace
            WHERE schema_definition.nspname = current_schema()
              AND relation.relname = '__EFMigrationsHistory'
            """);

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        return await ScalarAsync<T>(connection, sql, []);
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        object? result = await command.ExecuteScalarAsync();
        return Assert.IsType<T>(result);
    }

    private static async Task WaitForHistoryTableLockAsync(
        NpgsqlConnection observerConnection,
        string applicationName,
        Task gateTask,
        TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (gateTask.IsCompleted)
            {
                Assert.Fail(
                    "The owner gate completed before waiting for the outstanding history writer.");
            }

            bool waiting = await ScalarAsync<bool>(
                observerConnection,
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity AS activity
                    JOIN pg_locks AS relation_lock
                      ON relation_lock.pid = activity.pid
                    JOIN pg_class AS relation
                      ON relation.oid = relation_lock.relation
                    JOIN pg_namespace AS schema_definition
                      ON schema_definition.oid = relation.relnamespace
                    WHERE activity.application_name = @applicationName
                      AND schema_definition.nspname = current_schema()
                      AND relation.relname = '__EFMigrationsHistory'
                      AND NOT relation_lock.granted)
                """,
                ("applicationName", applicationName));
            if (waiting)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Timed out waiting for the owner gate to block on migration history.");
    }

    private static async Task WaitForManagedTopologyLockAsync(
        NpgsqlConnection observerConnection,
        string applicationName,
        Task surfaceTask,
        TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (surfaceTask.IsCompleted)
            {
                Assert.Fail(
                    "The corrective surface completed before its managed topology locks were observed.");
            }

            bool lockStateObserved = await ScalarAsync<bool>(
                observerConnection,
                """
                SELECT
                    EXISTS (
                        SELECT 1
                        FROM pg_stat_activity AS activity
                        JOIN pg_locks AS relation_lock ON relation_lock.pid = activity.pid
                        JOIN pg_class AS relation ON relation.oid = relation_lock.relation
                        JOIN pg_namespace AS schema_definition
                          ON schema_definition.oid = relation.relnamespace
                        WHERE activity.application_name = @applicationName
                          AND schema_definition.nspname = current_schema()
                          AND relation.relname = 'users'
                          AND relation_lock.mode = 'AccessExclusiveLock'
                          AND relation_lock.granted)
                    AND EXISTS (
                        SELECT 1
                        FROM pg_stat_activity AS activity
                        JOIN pg_locks AS relation_lock ON relation_lock.pid = activity.pid
                        JOIN pg_class AS relation ON relation.oid = relation_lock.relation
                        JOIN pg_namespace AS schema_definition
                          ON schema_definition.oid = relation.relnamespace
                        WHERE activity.application_name = @applicationName
                          AND schema_definition.nspname = current_schema()
                          AND relation.relname = '__EFMigrationsHistory'
                          AND relation_lock.mode = 'AccessExclusiveLock'
                          AND NOT relation_lock.granted)
                """,
                ("applicationName", applicationName));
            if (lockStateObserved)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Timed out waiting for managed topology locks.");
    }

    private static async Task WaitForConcurrentIndexBoundaryAsync(
        NpgsqlConnection observerConnection,
        string applicationName,
        bool isDown,
        Task surfaceTask,
        TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (surfaceTask.IsCompleted)
            {
                Assert.Fail(
                    "The corrective surface completed before its concurrent index boundary was observed.");
            }

            bool waitingAtBoundary = await ScalarAsync<bool>(
                observerConnection,
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity AS activity
                    WHERE activity.application_name = @applicationName
                      AND activity.query ILIKE @concurrentCommand
                      AND activity.wait_event IS NOT NULL)
                """,
                ("applicationName", applicationName),
                ("concurrentCommand", isDown
                    ? "%DROP INDEX CONCURRENTLY%"
                    : "%CREATE UNIQUE INDEX CONCURRENTLY%"));
            if (waitingAtBoundary)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Timed out waiting for the concurrent index boundary.");
    }

    private static async Task<int> WaitForAdvisoryLockAsync(
        NpgsqlConnection observerConnection,
        string applicationName,
        Task processTask,
        TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (processTask.IsCompleted)
            {
                Assert.Fail(
                    $"The psql process '{applicationName}' completed before waiting on the T8 advisory lock.");
            }

            int backendPid = await ScalarAsync<int>(
                observerConnection,
                """
                SELECT COALESCE(max(activity.pid), 0)::integer
                FROM pg_stat_activity AS activity
                JOIN pg_locks AS advisory_lock ON advisory_lock.pid = activity.pid
                WHERE activity.application_name = @applicationName
                  AND advisory_lock.locktype = 'advisory'
                  AND NOT advisory_lock.granted
                """,
                ("applicationName", applicationName));
            if (backendPid > 0)
            {
                return backendPid;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for '{applicationName}' to block on the T8 advisory lock.");
        return 0;
    }

    private static async Task<int> WaitForCompensationFailureBoundaryAsync(
        NpgsqlConnection observerConnection,
        string applicationName,
        Task processTask,
        TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (processTask.IsCompleted)
            {
                Assert.Fail(
                    $"The failed psql process '{applicationName}' completed before reaching compensation cleanup.");
            }

            int backendPid = await ScalarAsync<int>(
                observerConnection,
                """
                SELECT COALESCE(max(activity.pid), 0)::integer
                FROM pg_stat_activity AS activity
                JOIN pg_locks AS advisory_lock ON advisory_lock.pid = activity.pid
                WHERE activity.application_name = @applicationName
                  AND (
                      activity.query ILIKE '%DROP INDEX%'
                      OR activity.query ILIKE '%t8_drop_created_index%')
                  AND advisory_lock.locktype = 'advisory'
                  AND NOT advisory_lock.granted
                """,
                ("applicationName", applicationName));
            if (backendPid > 0)
            {
                return backendPid;
            }

            await Task.Delay(25);
        }

        Assert.Fail(
            $"Timed out waiting for '{applicationName}' to reach forced compensation cleanup failure.");
        return 0;
    }

    private static async Task<int> GetBackendPidAsync(
        NpgsqlConnection observerConnection,
        string applicationName)
    {
        return await ScalarAsync<int>(
            observerConnection,
            """
            SELECT pid
            FROM pg_stat_activity
            WHERE application_name = @applicationName
            """,
            ("applicationName", applicationName));
    }

    private static async Task WaitForApplicationsToDisconnectAsync(
        NpgsqlConnection observerConnection,
        string[] applicationNames,
        TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            long activeCount = await ScalarAsync<long>(
                observerConnection,
                """
                SELECT COUNT(*)
                FROM pg_stat_activity
                WHERE application_name = ANY(@applicationNames)
                """,
                ("applicationNames", applicationNames));
            if (activeCount == 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail(
            $"Timed out waiting for psql applications to disconnect: {string.Join(", ", applicationNames)}.");
    }

    private static Task InstallConcurrentBoundaryPauseAsync(
        NpgsqlConnection connection,
        string applicationName)
    {
        string escapedApplicationName =
            applicationName.Replace("'", "''", StringComparison.Ordinal);
        return ExecuteAsync(
            connection,
            $"""
            DROP EVENT TRIGGER IF EXISTS tabruk_t8_concurrent_boundary_pause;
            DROP FUNCTION IF EXISTS tabruk_t8_concurrent_boundary_pause();

            CREATE FUNCTION tabruk_t8_concurrent_boundary_pause()
            RETURNS event_trigger
            LANGUAGE plpgsql
            AS $body$
            BEGIN
                IF current_setting('application_name') = '{escapedApplicationName}'
                    AND tg_tag IN ('CREATE INDEX', 'DROP INDEX')
                THEN
                    PERFORM pg_advisory_lock({PostConcurrentBoundaryAdvisoryLock});
                    PERFORM pg_advisory_unlock({PostConcurrentBoundaryAdvisoryLock});
                END IF;
            END
            $body$;

            CREATE EVENT TRIGGER tabruk_t8_concurrent_boundary_pause
            ON ddl_command_end
            EXECUTE FUNCTION tabruk_t8_concurrent_boundary_pause();
            """);
    }

    private static Task RemoveConcurrentBoundaryPauseAsync(NpgsqlConnection connection) =>
        ExecuteAsync(
            connection,
            """
            DROP EVENT TRIGGER IF EXISTS tabruk_t8_concurrent_boundary_pause;
            DROP FUNCTION IF EXISTS tabruk_t8_concurrent_boundary_pause();
            """);

    private static Task InstallExternalIndexOwnerRacePauseAsync(
        NpgsqlConnection connection,
        string applicationName)
    {
        string escapedApplicationName =
            applicationName.Replace("'", "''", StringComparison.Ordinal);
        return ExecuteAsync(
            connection,
            $"""
            DROP EVENT TRIGGER IF EXISTS tabruk_t8_external_index_owner_race;
            DROP FUNCTION IF EXISTS tabruk_t8_external_index_owner_race();

            CREATE FUNCTION tabruk_t8_external_index_owner_race()
            RETURNS event_trigger
            LANGUAGE plpgsql
            AS $body$
            BEGIN
                IF current_setting('application_name') = '{escapedApplicationName}'
                    AND tg_tag = 'CREATE INDEX'
                THEN
                    PERFORM pg_advisory_lock({ExternalIndexOwnerRaceAdvisoryLock});
                    PERFORM pg_advisory_unlock({ExternalIndexOwnerRaceAdvisoryLock});
                END IF;
            END
            $body$;

            CREATE EVENT TRIGGER tabruk_t8_external_index_owner_race
            ON ddl_command_start
            EXECUTE FUNCTION tabruk_t8_external_index_owner_race();
            """);
    }

    private static Task RemoveExternalIndexOwnerRacePauseAsync(NpgsqlConnection connection) =>
        ExecuteAsync(
            connection,
            """
            DROP EVENT TRIGGER IF EXISTS tabruk_t8_external_index_owner_race;
            DROP FUNCTION IF EXISTS tabruk_t8_external_index_owner_race();
            """);

    private static Task InstallCompensationFailureTriggerAsync(
        NpgsqlConnection connection,
        string applicationName)
    {
        string escapedApplicationName =
            applicationName.Replace("'", "''", StringComparison.Ordinal);
        return ExecuteAsync(
            connection,
            $"""
            DROP EVENT TRIGGER IF EXISTS tabruk_t8_compensation_failure;
            DROP FUNCTION IF EXISTS tabruk_t8_compensation_failure();

            CREATE FUNCTION tabruk_t8_compensation_failure()
            RETURNS event_trigger
            LANGUAGE plpgsql
            AS $body$
            BEGIN
                IF current_setting('application_name') = '{escapedApplicationName}'
                    AND tg_tag = 'DROP INDEX'
                THEN
                    PERFORM pg_advisory_lock({CompensationFailureBoundaryAdvisoryLock});
                    PERFORM pg_advisory_unlock({CompensationFailureBoundaryAdvisoryLock});
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'T8 forced compensation cleanup failure';
                END IF;
            END
            $body$;

            CREATE EVENT TRIGGER tabruk_t8_compensation_failure
            ON ddl_command_start
            EXECUTE FUNCTION tabruk_t8_compensation_failure();
            """);
    }

    private static Task RemoveCompensationFailureTriggerAsync(NpgsqlConnection connection) =>
        ExecuteAsync(
            connection,
            """
            DROP EVENT TRIGGER IF EXISTS tabruk_t8_compensation_failure;
            DROP FUNCTION IF EXISTS tabruk_t8_compensation_failure();
            """);

    private static async Task WaitForRelationLockAsync(
        NpgsqlConnection observerConnection,
        string applicationName,
        string relationName,
        Task commandTask,
        TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (commandTask.IsCompleted)
            {
                Assert.Fail(
                    $"The concurrent topology command completed before blocking on {relationName}.");
            }

            bool waiting = await ScalarAsync<bool>(
                observerConnection,
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity AS activity
                    JOIN pg_locks AS relation_lock ON relation_lock.pid = activity.pid
                    JOIN pg_class AS relation ON relation.oid = relation_lock.relation
                    JOIN pg_namespace AS schema_definition
                      ON schema_definition.oid = relation.relnamespace
                    WHERE activity.application_name = @applicationName
                      AND schema_definition.nspname = current_schema()
                      AND relation.relname = @relationName
                      AND NOT relation_lock.granted)
                """,
                ("applicationName", applicationName),
                ("relationName", relationName));
            if (waiting)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for the topology command to block on {relationName}.");
    }

    private static async Task<Exception?> CaptureExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Task PrepareTopologyProbeAsync(
        NpgsqlConnection connection,
        string topologyProbe) =>
        topologyProbe switch
        {
            "descendant" => Task.CompletedTask,
            "ancestor" => ExecuteAsync(
                connection,
                $"""CREATE TABLE "{HostileAncestorTable}" ("id" uuid)"""),
            "partition" => ExecuteAsync(
                connection,
                $"""
                CREATE TABLE "{HostilePartitionParentTable}"
                    (LIKE "users" INCLUDING DEFAULTS INCLUDING GENERATED INCLUDING IDENTITY)
                    PARTITION BY LIST ("email_confirmed")
                """),
            _ => throw new ArgumentOutOfRangeException(
                nameof(topologyProbe),
                topologyProbe,
                "Unknown managed topology probe."),
        };

    private static Task PrepareHistoryTopologyProbeAsync(
        NpgsqlConnection connection,
        string topologyProbe) =>
        topologyProbe switch
        {
            "inheritance" => ExecuteAsync(
                connection,
                $"""CREATE TABLE "{HostileHistoryAncestorTable}" (LIKE "__EFMigrationsHistory")"""),
            "partition" => ExecuteAsync(
                connection,
                $"""
                CREATE TABLE "{HostileHistoryPartitionParentTable}"
                    (LIKE "__EFMigrationsHistory")
                    PARTITION BY RANGE ("MigrationId")
                """),
            _ => throw new ArgumentOutOfRangeException(
                nameof(topologyProbe),
                topologyProbe,
                "Unknown history topology probe."),
        };

    private static string HistoryTopologyProbeSql(string topologyProbe) =>
        topologyProbe switch
        {
            "inheritance" =>
                $"""ALTER TABLE "__EFMigrationsHistory" INHERIT "{HostileHistoryAncestorTable}" """,
            "partition" =>
                $"""ALTER TABLE "{HostileHistoryPartitionParentTable}" ATTACH PARTITION "__EFMigrationsHistory" DEFAULT""",
            _ => throw new ArgumentOutOfRangeException(
                nameof(topologyProbe),
                topologyProbe,
                "Unknown history topology probe."),
        };

    private static Task RemoveHistoryTopologyProbeAsync(
        NpgsqlConnection connection,
        string topologyProbe) =>
        topologyProbe switch
        {
            "inheritance" => ExecuteAsync(
                connection,
                $"""
                ALTER TABLE "__EFMigrationsHistory" NO INHERIT "{HostileHistoryAncestorTable}";
                DROP TABLE "{HostileHistoryAncestorTable}";
                """),
            "partition" => ExecuteAsync(
                connection,
                $"""
                ALTER TABLE "{HostileHistoryPartitionParentTable}"
                    DETACH PARTITION "__EFMigrationsHistory";
                DROP TABLE "{HostileHistoryPartitionParentTable}";
                """),
            _ => throw new ArgumentOutOfRangeException(
                nameof(topologyProbe),
                topologyProbe,
                "Unknown history topology probe."),
        };

    private static string TopologyProbeSql(string topologyProbe) =>
        topologyProbe switch
        {
            "descendant" =>
                $"""CREATE TABLE "{HostileDescendantTable}" () INHERITS ("users")""",
            "ancestor" =>
                $"""ALTER TABLE "users" INHERIT "{HostileAncestorTable}" """,
            "partition" =>
                $"""ALTER TABLE "{HostilePartitionParentTable}" ATTACH PARTITION "users" FOR VALUES IN (false)""",
            _ => throw new ArgumentOutOfRangeException(
                nameof(topologyProbe),
                topologyProbe,
                "Unknown managed topology probe."),
        };

    private static Task RemoveTopologyProbeAsync(
        NpgsqlConnection connection,
        string topologyProbe) =>
        topologyProbe switch
        {
            "descendant" => ExecuteAsync(
                connection,
                $"""DROP TABLE "{HostileDescendantTable}" """),
            "ancestor" => ExecuteAsync(
                connection,
                $"""
                ALTER TABLE "users" NO INHERIT "{HostileAncestorTable}";
                DROP TABLE "{HostileAncestorTable}";
                """),
            "partition" => ExecuteAsync(
                connection,
                $"""
                ALTER TABLE "{HostilePartitionParentTable}" DETACH PARTITION "users";
                DROP TABLE "{HostilePartitionParentTable}";
                """),
            _ => throw new ArgumentOutOfRangeException(
                nameof(topologyProbe),
                topologyProbe,
                "Unknown managed topology probe."),
        };

    private static async Task<PsqlInvocationResult> ExecutePsqlScriptAsync(
        string psqlPath,
        string connectionString,
        string scriptPath,
        string targetSchema,
        string? searchPath = null,
        string? optionsOverride = null,
        string? applicationName = null)
    {
        await using PsqlProcessInvocation invocation = StartPsqlScript(
            psqlPath,
            connectionString,
            scriptPath,
            targetSchema,
            searchPath,
            optionsOverride,
            applicationName);
        return await invocation.Completion;
    }

    private static PsqlProcessInvocation StartPsqlScript(
        string psqlPath,
        string connectionString,
        string scriptPath,
        string targetSchema,
        string? searchPath = null,
        string? optionsOverride = null,
        string? applicationName = null)
    {
        string psqlConnectionInfo = BuildPsqlConnectionInfo(
            connectionString,
            targetSchema,
            searchPath ?? targetSchema,
            optionsOverride,
            applicationName);
        ProcessStartInfo startInfo = new(psqlPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        NpgsqlConnectionStringBuilder connectionStringBuilder = new(connectionString);
        if (!string.IsNullOrEmpty(connectionStringBuilder.Password))
        {
            startInfo.Environment["PGPASSWORD"] = connectionStringBuilder.Password;
        }

        startInfo.ArgumentList.Add("--dbname");
        startInfo.ArgumentList.Add(psqlConnectionInfo);
        startInfo.ArgumentList.Add("-X");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add($"target_schema={targetSchema}");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("ON_ERROR_STOP=1");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(scriptPath);

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start psql using '{psqlPath}'.");

        int processId = process.Id;
        Task<PsqlInvocationResult> completion = CompletePsqlProcessAsync(process);
        return new PsqlProcessInvocation(processId, completion);
    }

    private static async Task<PsqlInvocationResult> CompletePsqlProcessAsync(Process process)
    {
        using (process)
        {
            string standardOutput = await process.StandardOutput.ReadToEndAsync();
            string standardError = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new PsqlInvocationResult(process.ExitCode, standardOutput, standardError);
        }
    }

    private static string ResolveOwnerIdempotentScriptPath()
    {
        string scriptPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "HusayniaTabruk.Infrastructure",
            "Migrations",
            OwnerIdempotentScriptFileName);
        Assert.True(File.Exists(scriptPath), $"Missing owner idempotent corrective script: {scriptPath}");
        return scriptPath;
    }

    private static string ResolveOwnerDowngradeScriptPath()
    {
        string scriptPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "HusayniaTabruk.Infrastructure",
            "Migrations",
            OwnerDowngradeScriptFileName);
        Assert.True(File.Exists(scriptPath), $"Missing owner downgrade script: {scriptPath}");
        return scriptPath;
    }

    private static string ResolvePsqlExecutablePath()
    {
        string? configuredPath = Environment.GetEnvironmentVariable("TABRUK_TEST_PSQL_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            Assert.True(File.Exists(configuredPath), $"TABRUK_TEST_PSQL_PATH was not found: {configuredPath}");
            return configuredPath;
        }

        string executableName = OperatingSystem.IsWindows() ? "psql.exe" : "psql";
        string[] pathSegments = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string pathSegment in pathSegments)
        {
            string candidate = Path.Combine(pathSegment, executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw SkipException.ForSkip(
            "Approved owner idempotent migration script tests require TABRUK_TEST_PSQL_PATH or psql on PATH.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "HusayniaTabruk.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate HusayniaTabruk.sln from the test output directory.");
    }

    private static string BuildPsqlConnectionInfo(
        string connectionString,
        string targetSchema,
        string searchPath,
        string? optionsOverride,
        string? applicationName = null)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString);
        string host = builder.Host
            ?? throw new InvalidOperationException("A PostgreSQL host is required for psql script execution.");
        string database = builder.Database
            ?? throw new InvalidOperationException("A PostgreSQL database name is required for psql script execution.");
        string username = builder.Username
            ?? throw new InvalidOperationException("A PostgreSQL username is required for psql script execution.");
        string explicitTargetSchema = string.IsNullOrWhiteSpace(targetSchema)
            ? throw new InvalidOperationException("A PostgreSQL target schema is required for psql script execution.")
            : targetSchema;
        string explicitSearchPath = string.IsNullOrWhiteSpace(searchPath)
            ? throw new InvalidOperationException("A PostgreSQL search_path is required for psql script execution.")
            : searchPath;
        List<string> parts =
        [
            $"host={EscapePsqlConnectionValue(host)}",
            $"port={builder.Port}",
            $"dbname={EscapePsqlConnectionValue(database)}",
            $"user={EscapePsqlConnectionValue(username)}",
        ];
        string options = optionsOverride
            ?? $"-c tabruk.target_schema={explicitTargetSchema} -c search_path={explicitSearchPath}";
        if (!string.IsNullOrWhiteSpace(options))
        {
            parts.Add($"options={EscapePsqlConnectionValue(options)}");
        }
        if (!string.IsNullOrWhiteSpace(applicationName))
        {
            parts.Add($"application_name={EscapePsqlConnectionValue(applicationName)}");
        }

        return string.Join(" ", parts);
    }

    private static string BuildFactoryConnectionString(
        string? searchPath,
        string? options)
    {
        NpgsqlConnectionStringBuilder builder = new(
            "Host=127.0.0.1;Database=tabruk_factory_probe;Username=tabruk;Pooling=false");
        if (searchPath is not null)
        {
            builder.SearchPath = searchPath;
        }

        if (options is not null)
        {
            builder.Options = options.Contains(
                "tabruk.disposable_ef=",
                StringComparison.Ordinal)
                ? options
                : $"{options} -c tabruk.disposable_ef=on";
        }

        return builder.ConnectionString;
    }

    private static string EscapePsqlConnectionValue(string value) =>
        $"'{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}'";

    private sealed record PsqlInvocationResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => $"{StandardOutput}{Environment.NewLine}{StandardError}";
    }

    private sealed class PsqlProcessInvocation(
        int processId,
        Task<PsqlInvocationResult> completion) : IAsyncDisposable
    {
        public int ProcessId { get; } = processId;
        public Task<PsqlInvocationResult> Completion { get; } = completion;

        public async ValueTask DisposeAsync()
        {
            await Completion;
        }
    }

}
