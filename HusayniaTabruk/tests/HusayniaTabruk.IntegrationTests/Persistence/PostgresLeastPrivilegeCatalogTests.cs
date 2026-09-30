using HusayniaTabruk.Infrastructure.Migrations;
using HusayniaTabruk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace HusayniaTabruk.IntegrationTests.Persistence;

[Trait("Category", "Persistence")]
public sealed class PostgresLeastPrivilegeCatalogTests
{
    private const string OwnerIdempotentScriptFileName =
        "20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql";

    private static readonly string[] ExpectedHistoryRevokeStatements =
    [
        """REVOKE ALL PRIVILEGES ON TABLE "__EFMigrationsHistory" FROM "tabruk_app";""",
        """REVOKE ALL PRIVILEGES ON TABLE "__EFMigrationsHistory" FROM PUBLIC;""",
    ];

    private static readonly string[] ExpectedPostgreSql18TablePrivileges =
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

    private static readonly string[] ExpectedPostgreSql18ColumnPrivileges =
    [
        "SELECT",
        "INSERT",
        "UPDATE",
        "REFERENCES",
    ];

    [Fact]
    public void CatalogClassifiesEveryEfMappedTableExactlyOnce()
    {
        using TabrukDbContext context = CreateContext();

        string[] efMappedTables = context.Model.GetEntityTypes()
            .Select(entityType => entityType.GetTableName())
            .Where(tableName => !string.IsNullOrWhiteSpace(tableName))
            .Select(tableName => tableName!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tableName => tableName, StringComparer.Ordinal)
            .ToArray();
        string[] classifiedTables = PostgresLeastPrivilegeCatalog.ClassifiedTables
            .OrderBy(tableName => tableName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(classifiedTables.Length, PostgresLeastPrivilegeCatalog.ClassifiedTables.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(PostgresLeastPrivilegeCatalog.CrudTables.Intersect(PostgresLeastPrivilegeCatalog.InsertOnlyTables, StringComparer.Ordinal));
        Assert.Equal(efMappedTables, classifiedTables);
        Assert.Equal(PostgresLeastPrivilegeCatalog.MigrationHistoryTable, Assert.Single(PostgresLeastPrivilegeCatalog.DeniedTables));
        Assert.DoesNotContain(PostgresLeastPrivilegeCatalog.MigrationHistoryTable, efMappedTables, StringComparer.Ordinal);
    }

    [Fact]
    public void LeastPrivilegeSqlUsesOnlyExactIdentifiers()
    {
        string sql = PostgresLeastPrivilegeCatalog.ApplyRuntimeLeastPrivilegeSql;
        Assert.DoesNotContain("ALL TABLES", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALL SEQUENCES", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pg_get_serial_sequence", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("current_setting('tabruk.target_schema', true)", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE ALL PRIVILEGES ON TABLE", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT INSERT ON TABLE", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT USAGE, SELECT ON SEQUENCE", sql, StringComparison.Ordinal);
        Assert.Contains(PostgresLeastPrivilegeCatalog.MigrationHistoryTable, sql, StringComparison.Ordinal);
        Assert.Contains("format('%I.%I'", sql, StringComparison.Ordinal);
        Assert.Equal(
            """LOCK TABLE "tabruk_probe"."__EFMigrationsHistory" IN ACCESS EXCLUSIVE MODE;""",
            PostgresLeastPrivilegeCatalog.MigrationHistoryWriteLockSql("tabruk_probe"));
        Assert.Equal(
            $"LOCK TABLE {string.Join(", ", PostgresLeastPrivilegeCatalog.ManagedTables.Select(table => $"\"tabruk_probe\".\"{table}\""))} IN ACCESS EXCLUSIVE MODE;",
            PostgresLeastPrivilegeCatalog.ManagedTableTopologyLockSqlForSchema("tabruk_probe"));

        foreach (string table in PostgresLeastPrivilegeCatalog.ManagedTables)
        {
            Assert.Contains($"'{table}'", sql, StringComparison.Ordinal);
        }

        foreach (string sequence in PostgresLeastPrivilegeCatalog.UsageSelectSequences)
        {
            Assert.Contains($"'{sequence}'", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CatalogFreezesHistoryInventoryAndCompleteObjectSemantics()
    {
        Assert.Equal(
            "20260815075156_InitialPostgresSchema",
            PostgresLeastPrivilegeCatalog.InitialMigrationId);
        Assert.Equal(
            "20260815102612_T8CorrectivePostgresHardening",
            PostgresLeastPrivilegeCatalog.CorrectiveMigrationId);
        Assert.Equal(
            "__EFMigrationsHistory",
            PostgresLeastPrivilegeCatalog.MigrationHistoryTable);
        Assert.Equal(
            ExpectedPostgreSql18TablePrivileges,
            PostgresLeastPrivilegeCatalog.PostgreSql18TablePrivileges);
        Assert.Equal(
            ExpectedPostgreSql18ColumnPrivileges,
            PostgresLeastPrivilegeCatalog.PostgreSql18ColumnPrivileges);

        string attestationSql = PostgresLeastPrivilegeCatalog.PreHistoryAttestationSql;
        string historyStructureSql =
            PostgresLeastPrivilegeCatalog.MigrationHistoryStructureAttestationSql;
        string historyPrivilegeSql =
            PostgresLeastPrivilegeCatalog.MigrationHistoryPrivilegeAttestationSql;
        string topologySql =
            PostgresLeastPrivilegeCatalog.ManagedTableTopologyAttestationSql;
        string runtimePrivilegeSql =
            PostgresLeastPrivilegeCatalog.RuntimePrivilegeAttestationSql;
        string objectSql = PostgresLeastPrivilegeCatalog.CorrectiveObjectPreflightSql;
        foreach (string category in new[]
                 {
                     "identity",
                     "owner",
                     "state",
                     "flags",
                     "key-count",
                     "key-order",
                     "key-types",
                     "operator-classes",
                     "key-options",
                     "relation-options",
                     "constraint-attachment",
                     "predicate",
                     "type",
                     "inheritance",
                     "expression",
                 })
        {
            Assert.Contains($"category={category}.", objectSql, StringComparison.Ordinal);
        }

        foreach (string fixedPredicate in new[]
                 {
                     "index_relation.relowner = table_relation.relowner",
                     "ARRAY['uuid', 'uuid', 'bigint']",
                     "ARRAY['uuid_ops', 'uuid_ops', 'int8_ops']",
                     "index_definition.indcollation::text = '0 0 0'",
                     "index_definition.indoption::text = '0 0 0'",
                     "COALESCE(cardinality(index_relation.reloptions), 0) = 0",
                     "NOT index_definition.indnullsnotdistinct",
                     "NOT index_definition.indisprimary",
                     "NOT index_definition.indisexclusion",
                     "index_definition.indimmediate",
                     "NOT index_definition.indisclustered",
                     "NOT index_definition.indisreplident",
                     "WHERE conindid = index_oid",
                     "constraint_definition.conislocal",
                     "constraint_definition.coninhcount = 0",
                     "constraint_definition.conparentid = 0",
                     "constraint_definition.conenforced",
                 })
        {
            Assert.Contains(fixedPredicate, objectSql, StringComparison.Ordinal);
        }

        Assert.Contains(
            "T8 pre-history attestation failed: corrective-history-object-mismatch",
            attestationSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "T8 pre-history attestation failed: history-structure-mismatch",
            historyStructureSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "T8 pre-history attestation failed: managed-table-topology-mismatch",
            topologySql,
            StringComparison.Ordinal);
        Assert.Contains(
            $"'{PostgresLeastPrivilegeCatalog.MigrationHistoryTable}'",
            topologySql,
            StringComparison.Ordinal);
        foreach (string topologyPredicate in new[]
                 {
                     "relation.relkind <> 'r'",
                     "relation.relispartition",
                     "relation.relpartbound IS NOT NULL",
                     "FROM pg_catalog.pg_inherits AS inheritance",
                     "inheritance.inhparent = relation.oid",
                     "inheritance.inhrelid = relation.oid",
                 })
        {
            Assert.Contains(topologyPredicate, topologySql, StringComparison.Ordinal);
        }
        foreach (string exactHistoryShape in new[]
                 {
                     "history_relation.relkind = 'r'",
                     "history_relation.relpersistence = 'p'",
                     "table_access_method.amname = 'heap'",
                     "history_relation.relowner =",
                     "history_relation.relname = '__EFMigrationsHistory'",
                     "attribute.attname = 'MigrationId'",
                     "attribute.atttypmod = 154",
                     "attribute.attname = 'ProductVersion'",
                     "attribute.atttypmod = 36",
                     "constraint_definition.conname = 'PK___EFMigrationsHistory'",
                     "constraint_definition.conkey = ARRAY[1]::smallint[]",
                     "index_definition.indclass::text =",
                     "operator_class.opcname = 'text_ops'",
                     "NOT history_relation.relrowsecurity",
                     "NOT history_relation.relhasrules",
                     "FROM pg_catalog.pg_policy AS policy_definition",
                     "FROM pg_catalog.pg_trigger AS trigger_definition",
                     "NOT trigger_definition.tgisinternal",
                     "FROM pg_catalog.pg_inherits AS inheritance",
                 })
        {
            Assert.Contains(exactHistoryShape, historyStructureSql, StringComparison.Ordinal);
        }
        Assert.Contains(
            "THEN 'malformed-ef-bootstrap'",
            PostgresLeastPrivilegeCatalog.ClassifyInitialInventorySql,
            StringComparison.Ordinal);
        Assert.Contains(
            "T8 pre-history attestation failed: privilege-mismatch",
            attestationSql,
            StringComparison.Ordinal);
        foreach (string privilege in PostgresLeastPrivilegeCatalog.PostgreSql18TablePrivileges)
        {
            Assert.Contains($"'{privilege}'", historyPrivilegeSql, StringComparison.Ordinal);
            Assert.Contains($"'{privilege}'", runtimePrivilegeSql, StringComparison.Ordinal);
        }
        foreach (string privilege in PostgresLeastPrivilegeCatalog.PostgreSql18ColumnPrivileges)
        {
            Assert.Contains($"'{privilege}'", historyPrivilegeSql, StringComparison.Ordinal);
            Assert.Contains($"'{privilege}'", runtimePrivilegeSql, StringComparison.Ordinal);
        }

        Assert.Contains(
            "NOT relation.relname = ANY",
            runtimePrivilegeSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "relation.relkind IN ('r', 'p', 'v', 'm', 'f')",
            runtimePrivilegeSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "has_table_privilege(",
            runtimePrivilegeSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "has_any_column_privilege(",
            historyPrivilegeSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "has_any_column_privilege(",
            runtimePrivilegeSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "JOIN pg_catalog.pg_attribute AS attribute",
            runtimePrivilegeSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "aclexplode(attribute.attacl)",
            runtimePrivilegeSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "privilege.is_grantable",
            runtimePrivilegeSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "pg_has_role(",
            runtimePrivilegeSql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "'SELECT,INSERT,UPDATE,DELETE'",
            runtimePrivilegeSql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("@", attestationSql, StringComparison.Ordinal);
        Assert.DoesNotContain("@", historyStructureSql, StringComparison.Ordinal);
        Assert.DoesNotContain("@", historyPrivilegeSql, StringComparison.Ordinal);
        Assert.DoesNotContain("@", topologySql, StringComparison.Ordinal);
        Assert.DoesNotContain("@", runtimePrivilegeSql, StringComparison.Ordinal);
        Assert.Contains("T8_ATTESTATION_FAILED:acl", PostgresLeastPrivilegeCatalog.SchemaOwnerAndAclAttestationSql, StringComparison.Ordinal);
        Assert.Contains("T8_ATTESTATION_FAILED:temp", topologySql, StringComparison.Ordinal);
        Assert.Contains("pg_default_acl", PostgresLeastPrivilegeCatalog.SchemaOwnerAndAclAttestationSql, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN pg_catalog.pg_default_acl", PostgresLeastPrivilegeCatalog.SchemaOwnerAndAclAttestationSql, StringComparison.Ordinal);
        Assert.Contains("COALESCE(", PostgresLeastPrivilegeCatalog.SchemaOwnerAndAclAttestationSql, StringComparison.Ordinal);
        Assert.Contains("pg_catalog.acldefault(default_scope.object_type, owner_oid)", PostgresLeastPrivilegeCatalog.SchemaOwnerAndAclAttestationSql, StringComparison.Ordinal);
        Assert.Contains("ALTER DEFAULT PRIVILEGES", PostgresLeastPrivilegeCatalog.ApplySafeDefaultPrivilegesSql, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "'ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT",
            PostgresLeastPrivilegeCatalog.ApplySafeDefaultPrivilegesSql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "'ALTER DEFAULT PRIVILEGES FOR ROLE %I REVOKE",
            PostgresLeastPrivilegeCatalog.ApplySafeDefaultPrivilegesSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "'ALTER DEFAULT PRIVILEGES FOR ROLE %I REVOKE",
            PostgresLeastPrivilegeCatalog.ApplySafeDisposableDefaultPrivilegesSql,
            StringComparison.Ordinal);
        Assert.Contains("pg_has_role", PostgresLeastPrivilegeCatalog.SchemaOwnerAndAclAttestationSql, StringComparison.Ordinal);
        Assert.Contains("privilege.privilege_type = 'USAGE'", PostgresLeastPrivilegeCatalog.SchemaOwnerAndAclAttestationSql, StringComparison.Ordinal);
        Assert.Contains("privilege.grantee = application_oid", PostgresLeastPrivilegeCatalog.SchemaOwnerAndAclAttestationSql, StringComparison.Ordinal);
        Assert.Contains("NOT privilege.is_grantable", PostgresLeastPrivilegeCatalog.SchemaOwnerAndAclAttestationSql, StringComparison.Ordinal);
        Assert.Contains("operator_class.opcmethod = index_relation.relam", objectSql, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovedOwnerScriptsUseSessionLockQualificationAndHistoryLast()
    {
        string scriptPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "HusayniaTabruk.Infrastructure",
            "Migrations",
            OwnerIdempotentScriptFileName);
        string downgradePath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "HusayniaTabruk.Infrastructure",
            "Migrations",
            "20260815102612_T8CorrectivePostgresHardening.owner-downgrade.sql");
        string script = NormalizeLineEndings(File.ReadAllText(scriptPath));
        string downgrade = NormalizeLineEndings(File.ReadAllText(downgradePath));
        Assert.Contains("pg_advisory_lock", script, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_lock", downgrade, StringComparison.Ordinal);
        Assert.Contains("hashtextextended", script, StringComparison.Ordinal);
        Assert.Contains("hashtextextended", downgrade, StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE INDEX CONCURRENTLY", script, StringComparison.Ordinal);
        Assert.Contains("T8_STAGE=preflight", script, StringComparison.Ordinal);
        Assert.Contains("T8_STAGE=index", script, StringComparison.Ordinal);
        Assert.Contains("T8_STAGE=compensation", script, StringComparison.Ordinal);
        Assert.Contains("T8_RESULT=compensation_failed", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP INDEX CONCURRENTLY", script, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE FUNCTION pg_temp.t8_drop_created_index()", script, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(script, "t8_drop_created_index()"));
        Assert.Equal(4, CountOccurrences(script, "LOCK TABLE :\"target_schema\".\"signups\" IN ACCESS EXCLUSIVE MODE;"));
        Assert.Contains("pg_catalog.acldefault(default_scope.object_type, owner_oid)", script, StringComparison.Ordinal);
        Assert.Contains("pg_catalog.acldefault(default_scope.object_type, owner_oid)", downgrade, StringComparison.Ordinal);
        Assert.Contains("ALTER DEFAULT PRIVILEGES", script, StringComparison.Ordinal);
        Assert.Contains("ALTER DEFAULT PRIVILEGES", downgrade, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "'ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "'ALTER DEFAULT PRIVILEGES FOR ROLE %I REVOKE",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "'ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT",
            downgrade,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "'ALTER DEFAULT PRIVILEGES FOR ROLE %I REVOKE",
            downgrade,
            StringComparison.Ordinal);
        Assert.Contains("created_constraint_oid oid", script, StringComparison.Ordinal);
        Assert.Contains("constraint_oid IS DISTINCT FROM", script, StringComparison.Ordinal);
        Assert.Equal(5, CountOccurrences(script, "t8_drop_created_constraint()"));
        Assert.Contains("T8_STAGE=down", downgrade, StringComparison.Ordinal);
        Assert.Contains("T8_RESULT=success", script, StringComparison.Ordinal);
        Assert.Contains("T8_RESULT=success", downgrade, StringComparison.Ordinal);
        Assert.Contains(
            "PERFORM pg_temp.t8_attest(false, false);",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "SELECT pg_temp.t8_down_attest(false);",
            downgrade,
            StringComparison.Ordinal);
        Assert.DoesNotContain("pg_auth_members", script, StringComparison.Ordinal);
        Assert.DoesNotContain("pg_auth_members", downgrade, StringComparison.Ordinal);
        Assert.Contains(
            "'INSERT INTO %I.%I (\"MigrationId\", \"ProductVersion\")",
            script,
            StringComparison.Ordinal);
        Assert.Contains("DELETE FROM :\"target_schema\".\"__EFMigrationsHistory\"", downgrade, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP INDEX", downgrade, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP CONSTRAINT", downgrade, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_default_acl", script, StringComparison.Ordinal);
        Assert.Contains("pg_my_temp_schema", script, StringComparison.Ordinal);
        Assert.Contains("T8_ATTESTATION_FAILED:acl", script, StringComparison.Ordinal);
        Assert.Contains("T8_ATTESTATION_FAILED:object", downgrade, StringComparison.Ordinal);
        Assert.Contains("privilege.privilege_type = 'USAGE'", script, StringComparison.Ordinal);
        Assert.Contains("privilege.privilege_type = 'USAGE'", downgrade, StringComparison.Ordinal);
        Assert.Contains("privilege.grantee = app_oid", script, StringComparison.Ordinal);
        Assert.Contains("privilege.grantee = app_oid", downgrade, StringComparison.Ordinal);
        Assert.Contains("NOT privilege.is_grantable", script, StringComparison.Ordinal);
        Assert.Contains("NOT privilege.is_grantable", downgrade, StringComparison.Ordinal);
        Assert.Contains("operator_namespace.nspname = 'pg_catalog'", script, StringComparison.Ordinal);
        Assert.Contains("operator_namespace.nspname = 'pg_catalog'", downgrade, StringComparison.Ordinal);
        Assert.Contains("operator_class.opcmethod = index_relation.relam", script, StringComparison.Ordinal);
        Assert.Contains("operator_class.opcmethod = index_relation.relam", downgrade, StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(script, "ADD CONSTRAINT %I CHECK"));
        Assert.Equal(
            1,
            CountOccurrences(script, "VALIDATE CONSTRAINT \"ck_signups_transition_chronology\""));
        Assert.Equal(
            1,
            CountOccurrences(script, "CREATE UNIQUE INDEX CONCURRENTLY"));
        Assert.DoesNotContain(
            "CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS",
            script,
            StringComparison.Ordinal);
        Assert.True(
            script.IndexOf(
                "ADD CONSTRAINT %I CHECK",
                StringComparison.Ordinal)
            < script.IndexOf(
                "VALIDATE CONSTRAINT \"ck_signups_transition_chronology\"",
                StringComparison.Ordinal));
        Assert.True(
            script.IndexOf(
                "VALIDATE CONSTRAINT \"ck_signups_transition_chronology\"",
                StringComparison.Ordinal)
            < script.IndexOf(
                "CREATE UNIQUE INDEX CONCURRENTLY",
                StringComparison.Ordinal));
        Assert.True(
            script.IndexOf(
                "CREATE UNIQUE INDEX CONCURRENTLY",
                StringComparison.Ordinal)
            < script.IndexOf("T8_STAGE=finalize", StringComparison.Ordinal));
        Assert.True(
            script.IndexOf(
                "'INSERT INTO %I.%I (\"MigrationId\", \"ProductVersion\")",
                StringComparison.Ordinal)
            > script.IndexOf("T8_STAGE=finalize", StringComparison.Ordinal));
    }

    private static TabrukDbContext CreateContext()
    {
        DbContextOptions<TabrukDbContext> options = new DbContextOptionsBuilder<TabrukDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=tabruk_catalog_probe;Username=postgres;Pooling=false")
            .EnableDetailedErrors()
            .Options;

        return new TabrukDbContext(options);
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", Environment.NewLine, StringComparison.Ordinal);

    private static string CollapseWhitespace(string text) =>
        string.Join(
            " ",
            text.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string JoinIdentifiers(IEnumerable<string> identifiers) =>
        string.Join(", ", identifiers.Select(identifier => $"\"{identifier}\""));

    private static string[] SplitStatements(string sql) =>
        NormalizeLineEndings(sql)
            .Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
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
}
